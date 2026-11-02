using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

internal static partial class ReleaseCorePlacementChecks
{
    private static readonly Dictionary<short, OpCode> Codes = BuildCodes();
    private static Dictionary<short, OpCode> BuildCodes()
    {
        var result = new Dictionary<short, OpCode>();
        foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.FieldType == typeof(OpCode)) { var op = (OpCode)f.GetValue(null); result[op.Value] = op; }
        return result;
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static object Call(Type type, string name, params object[] args)
    { return type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, args); }

    private static void CheckReleaseIL(Assembly assembly)
    {
        int calls = 0, placementCalls = 0;
        foreach (Type type in assembly.GetTypes())
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            MethodBody body = method.GetMethodBody();
            if (body == null) continue;
            byte[] il = body.GetILAsByteArray();
            MethodBase pending = null;
            for (int pos = 0; pos < il.Length;)
            {
                byte first = il[pos++];
                short value = first == 0xFE ? (short)(0xFE00 | il[pos++]) : (short)first;
                OpCode op = Codes[value];
                if (op == OpCodes.Nop) continue;
                if (pending != null)
                {
                    Require(op != OpCodes.Pop, "DISCARDED HANDLE: " + type.FullName + "." + method.Name + " drops " + pending.Name + " return value in RELEASE");
                    pending = null;
                }
                if (op == OpCodes.Call || op == OpCodes.Callvirt)
                {
                    // 泛型类型或泛型方法里的成员引用需要各自的类型参数才能解析。
                    MethodBase target = method.Module.ResolveMethod(BitConverter.ToInt32(il, pos),
                        type.IsGenericType ? type.GetGenericArguments() : null, method.IsGenericMethod ? method.GetGenericArguments() : null);
                    if (target.DeclaringType.FullName == "PaviseApp.Native"
                        && (target.Name == "OpenProcess" || target.Name == "OpenThread" || target.Name == "OpenAffinityProcess"))
                    {
                        calls++; pending = target;
                        if (type.FullName == "PaviseApp.GameMode" && method.Name == "RunIndependentCorePlacementImpl") placementCalls++;
                    }
                }
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: pos++; break;
                    case OperandType.InlineVar: pos += 2; break;
                    case OperandType.InlineI8: case OperandType.InlineR: pos += 8; break;
                    case OperandType.InlineSwitch: pos += 4 + 4 * BitConverter.ToInt32(il, pos); break;
                    default: pos += 4; break;
                }
            }
        }
        Require(placementCalls == 1, "Release placement must consume exactly one native process opener result.");
        Console.WriteLine("PASS release IL: " + calls + " process/thread opener call sites; no discarded handle; placement call present");
    }

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--child") { Console.ReadLine(); return 0; }
        if (args.Length == 1 && args[0] == "--load-child")
        {
            using (var done = new System.Threading.ManualResetEvent(false))
            {
                var workers = new System.Threading.Thread[4];
                for (int i = 0; i < workers.Length; i++)
                {
                    workers[i] = new System.Threading.Thread(delegate() { while (!done.WaitOne(5)) System.Threading.Thread.SpinWait(20000); });
                    workers[i].IsBackground = true; workers[i].Start();
                }
                Console.ReadLine(); done.Set(); foreach (var worker in workers) worker.Join();
            }
            return 0;
        }
        if (args.Length == 6 && args[0] == "--crash-owner") return RunCrashOwner(args);
        if (args.Length != 1 && (args.Length != 3 || args[1] != "--stress" && args[1] != "--soak")) return 2;
        try
        {
            string file = Path.GetFullPath(args[0]);
            Assembly product = Assembly.LoadFile(file);
            Require(product.GetType("PaviseApp.GameMode").GetField("CorePlacementOpenForTest", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                "This check requires the real release binary, not a self-test build.");
            CheckReleaseIL(product);
            if (args.Length == 3)
                RunLifecycleStress(product, int.Parse(args[2]), args[1] == "--soak" ? 400 : 100, args[1] == "--soak" ? 300 : 0);
            Type native = product.GetType("PaviseApp.Native", true);
            using (var child = Process.Start(new ProcessStartInfo {
                FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--child",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
            }))
            {
                IntPtr h = IntPtr.Zero; ulong original = 0;
                try
                {
                    h = (IntPtr)Call(native, "OpenAffinityProcess", child.Id);
                    Require(h != IntPtr.Zero, "Release affinity opener returned a null handle.");
                    original = (ulong)Call(native, "QueryAffinity", h);
                    Require(original != 0, "Release affinity read failed.");
                    ulong target = original & (~original + 1UL);
                    Require(target != original, "At least two logical processors are required.");
                    Require((bool)Call(native, "SetProcessAffinityMask", h, (UIntPtr)target), "Release affinity write failed.");
                    Require((ulong)Call(native, "QueryAffinity", h) == target, "Release affinity readback mismatch.");
                    Require((bool)Call(native, "SetProcessAffinityMask", h, (UIntPtr)original), "Release affinity restore failed.");
                    Require((ulong)Call(native, "QueryAffinity", h) == original, "Release affinity restore readback mismatch.");
                    Console.WriteLine("PASS exact release binary: owned hidden child open/write/readback/restore original=0x" + original.ToString("X") + " target=0x" + target.ToString("X"));
                    for (int i = 0; i < 64; i++)
                    {
                        IntPtr repeated = (IntPtr)Call(native, "OpenAffinityProcess", child.Id);
                        Require(repeated != IntPtr.Zero, "Repeated release open failed.");
                        Require((bool)Call(native, "CloseHandle", repeated), "Repeated release close failed.");
                    }
                    Console.WriteLine("PASS 64 release opener handles explicitly closed; no user settings or system reservation modified");
                }
                finally
                {
                    if (h != IntPtr.Zero)
                    {
                        if (original != 0) Call(native, "SetProcessAffinityMask", h, (UIntPtr)original);
                        Call(native, "CloseHandle", h);
                    }
                    child.StandardInput.WriteLine("exit");
                    if (!child.WaitForExit(3000)) { child.Kill(); child.WaitForExit(); }
                }
            }
            Console.WriteLine("RELEASE=" + file);
            using (var hash = System.Security.Cryptography.SHA256.Create())
            using (var input = File.OpenRead(file))
                Console.WriteLine("SHA256=" + BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
