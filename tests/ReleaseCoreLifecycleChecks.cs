using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

internal static partial class ReleaseCorePlacementChecks
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Invoke(object obj, string name, params object[] args)
    { return obj.GetType().GetMethod(name, AnyInstance).Invoke(obj, args); }
    private static void Set(object obj, string name, object value)
    { obj.GetType().GetField(name, AnyInstance).SetValue(obj, value); }
    private static object Get(object obj, string name)
    { return obj.GetType().GetField(name, AnyInstance).GetValue(obj); }
    private static object Make(Type type, params object[] args)
    { return Activator.CreateInstance(type, AnyInstance, null, args, null); }
    private static object Prop(object obj, string name)
    { return obj.GetType().GetProperty(name, AnyInstance).GetValue(obj, null); }

    [DllImport("advapi32.dll")]
    private static extern int RegOverridePredefKey(IntPtr predefined, IntPtr replacement);
    private sealed class IsolatedRegistry : IDisposable
    {
        internal readonly string Path;
        private readonly bool owns;
        private RegistryKey key;
        private static readonly IntPtr CurrentUser = new IntPtr(unchecked((int)0x80000001));
        internal IsolatedRegistry(string existing = null)
        {
            owns = existing == null;
            Path = existing ?? @"Software\PaviseReleaseLab\" + Guid.NewGuid().ToString("N");
            Guid id;
            Require(Path.StartsWith(@"Software\PaviseReleaseLab\", StringComparison.Ordinal) && Guid.TryParseExact(Path.Substring(@"Software\PaviseReleaseLab\".Length), "N", out id), "Invalid lab registry path.");
            key = Registry.CurrentUser.CreateSubKey(Path);
            Require(key != null, "Cannot create isolated test registry.");
            Require(RegOverridePredefKey(CurrentUser, key.Handle.DangerousGetHandle()) == 0, "Cannot isolate test registry.");
            Registry.CurrentUser.SetValue("PaviseLabSentinel", Path);
            Require((string)key.GetValue("PaviseLabSentinel") == Path, "Registry isolation verification failed.");
        }
        public void Dispose()
        {
            Require(RegOverridePredefKey(CurrentUser, IntPtr.Zero) == 0, "Cannot restore test registry mapping.");
            key.Dispose(); if (owns) Registry.CurrentUser.DeleteSubKeyTree(Path, false);
        }
    }

    private static int HandleCount()
    { using (var self = Process.GetCurrentProcess()) return self.HandleCount; }
    private static double CpuMs()
    { using (var self = Process.GetCurrentProcess()) return self.TotalProcessorTime.TotalMilliseconds; }
    private static void RunLifecycleStress(Assembly product, int seed, int cycles = 100, int dwellMs = 0)
    {
        string root = Path.Combine(Path.GetTempPath(), "PaviseReleaseStress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (var registry = new IsolatedRegistry())
        {
            Type native = product.GetType("PaviseApp.Native"), settings = product.GetType("PaviseApp.Settings"), guard = product.GetType("PaviseApp.CrashGuard");
            Type modeType = product.GetType("PaviseApp.GameMode"), snapType = modeType.GetNestedType("Snap", BindingFlags.NonPublic), passType = modeType.GetNestedType("BoostPass", BindingFlags.NonPublic);
            product.GetType("PaviseApp.Logger").GetField("LogPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, Path.Combine(root, "runtime.log"));
            Call(product.GetType("PaviseApp.Lang"), "Init");
            object core = Make(product.GetType("PaviseApp.SuppressionCore"), Path.Combine(root, "suppression.state"));
            object mode = Make(modeType, root, core);
            var rng = new Random(seed);
            using (var child = Process.Start(new ProcessStartInfo {
                FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--load-child", UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardInput = true
            }))
            {
                IntPtr h = (IntPtr)Call(native, "OpenAffinityProcess", child.Id);
                ulong original = (ulong)Call(native, "QueryAffinity", h);
                int inheritedIo = (int)Call(native, "QueryIoPriority", h);
                if (inheritedIo != 2)
                {
                    MethodInfo setIo = native.GetMethod("TrySetIoPriority", BindingFlags.Static | BindingFlags.Public,
                        null, new[] { typeof(IntPtr), typeof(int) }, null);
                    Require((bool)setIo.Invoke(null, new object[] { h, 2 })
                        && (int)Call(native, "QueryIoPriority", h) == 2, "Owned child Normal I/O baseline failed.");
                    Console.WriteLine("FIXTURE owned child I/O priority inherited=" + inheritedIo + " baseline=2");
                }
                object[] sample = { h, 0L, 0L, 0UL };
                Require((bool)Call(native, "QueryProcessSample", sample), "Cannot read child identity.");
                long creation = (long)sample[1]; string name = (string)Call(native, "ImageName", h);
                var bits = new System.Collections.Generic.List<ulong>();
                for (int b = 0; b < 64; b++) if ((original & (1UL << b)) != 0) bits.Add(1UL << b);
                Require(bits.Count >= 4, "Lifecycle test needs four logical CPUs.");
                object snap = Make(snapType); Set(snap, "Aff", original); Set(snap, "Creation", creation); Set(snap, "Name", name); Set(snap, "CpuSets", new uint[0]);
                object pass = Make(passType); Set(pass, "RendererPid", child.Id); Set(pass, "RendererCreation", creation); Set(pass, "RendererName", name); Set(pass, "Exclusive", true);
                object plan = Make(product.GetType("PaviseApp.AutoCorePlan")); Set(plan, "Exclusive", bits[1] | bits[2]); Set(mode, "corePlan", plan);
                object governor = Make(product.GetType("PaviseApp.AutoCoreGovernor"), 4, 8);
                Invoke(governor, "EngageManual"); Set(mode, "coreGovernor", governor);
                Set(mode, "coreWatchPid", child.Id); Set(mode, "coreWatchCreation", creation);
                ((IDictionary)Get(mode, "gameBoost"))[child.Id] = snap;
                MethodInfo mark = guard.GetMethod("MarkBoostProcess", BindingFlags.Static | BindingFlags.Public, null,
                    new[] { typeof(int), typeof(long), typeof(string), typeof(uint), typeof(ulong), typeof(int), typeof(int), typeof(int), typeof(uint[]) }, null);
                var timer = Stopwatch.StartNew();
                double cpuStart = CpuMs(); int warmHandles = 0, peakHandles = 0;
                try
                {
                    for (int cycle = 0; cycle < cycles; cycle++)
                    {
                        ulong target = bits[1 + rng.Next(bits.Count - 1)];
                        Set(pass, "DesiredMask", target);
                        Set(mode, "coreManual", Make(product.GetType("PaviseApp.ManualCorePlan"), target, false, "test-owned-child", false));
                        Require((bool)mark.Invoke(null, new object[] { child.Id, creation, name, (uint)0x20, original, -1, -1, -1, new uint[0] }), "Mark failed.");
                        Require((bool)Invoke(mode, "PinExclusive", h, child.Id, pass, snap), "Production pin failed cycle=" + cycle);
                        Require((ulong)Call(native, "QueryAffinity", h) == target, "Pin readback mismatch.");
                        if (dwellMs > 0) System.Threading.Thread.Sleep(dwellMs);
                        if (cycle % 3 == 0)
                        {
                            // 生产路径在钉住并接通后登记 gamePlacement；已验证的手动硬绑定在回读审计中既不扩大也不释放。
                            var placements = (IDictionary)Get(mode, "gamePlacement");
                            placements[child.Id] = target;
                            Set(pass, "Exclusive", true);
                            Set(mode, "coreEngaged", true); Set(mode, "exclusiveCheckTicks", 0L);
                            Invoke(mode, "RunIndependentCorePlacement", pass);
                            Require((bool)Get(mode, "coreAffinityRead") && (bool)Get(mode, "coreAffinityVerified"), "Readback audit did not run against the verified pin.");
                            Require((ulong)Call(native, "QueryAffinity", h) == target && ((IDictionary)Get(mode, "hardPins")).Count == 1,
                                "Verified manual hard affinity was widened or released by its readback audit.");
                            // Pavise 主导：其他程序改写已钉住的游戏亲和性，下一次巡检把钉住的掩码原样写回，所有权不变。
                            Require((bool)Call(native, "SetProcessAffinityMask", h, (UIntPtr)original), "External rewrite failed.");
                            Set(mode, "exclusiveCheckTicks", 0L);
                            Invoke(mode, "RunIndependentCorePlacement", pass);
                            Require((ulong)Call(native, "QueryAffinity", h) == target && ((IDictionary)Get(mode, "hardPins")).Count == 1
                                && (bool)Get(mode, "coreAffinityVerified") && !((System.Collections.Generic.HashSet<int>)Get(mode, "placementGaveUp")).Contains(child.Id),
                                "An external rewrite of the pinned game affinity was not written back.");
                            if (cycle % 2 == 0)
                            {
                                // 手动方案按入局快照固定；即使期望范围与实际不一致，也只能停止或恢复，绝不扩大。
                                ulong expanded = target | bits[0]; Set(pass, "DesiredMask", expanded); Set(pass, "Exclusive", true); Set(mode, "exclusiveCheckTicks", 0L);
                                Invoke(mode, "RunIndependentCorePlacement", pass);
                                ulong after = (ulong)Call(native, "QueryAffinity", h);
                                Require(after != expanded && (after == target || after == original), "Retired automatic expansion changed affinity.");
                                // 让出所有权后 Pavise 不再恢复该值，夹具自行复位并换回独占 governor。
                                if (((IDictionary)Get(mode, "hardPins")).Count == 0 && after != original)
                                    Require((bool)Call(native, "SetProcessAffinityMask", h, (UIntPtr)original), "Fixture reset after yield failed.");
                                ((System.Collections.Generic.HashSet<int>)Get(mode, "placementGaveUp")).Remove(child.Id);
                                object fresh = Make(product.GetType("PaviseApp.AutoCoreGovernor"), 4, 8);
                                Invoke(fresh, "EngageManual"); Set(mode, "coreGovernor", fresh);
                                Set(mode, "corePlacementStopReason", null);
                                Set(pass, "DesiredMask", target); Set(pass, "Exclusive", true);
                            }
                            placements.Remove(child.Id);
                        }
                        if (cycle % 3 == 1)
                        {
                            Require((bool)Call(native, "SetProcessAffinityMask", h, (UIntPtr)bits[0]), "External write failed.");
                            Require((bool)Invoke(mode, "RestoreHardPin", child.Id), "External ownership release failed.");
                            Require((ulong)Call(native, "QueryAffinity", h) == bits[0], "External affinity was overwritten.");
                            Require((bool)Call(native, "SetProcessAffinityMask", h, (UIntPtr)original), "Fixture reset failed.");
                        }
                        else
                        {
                            if (cycle % 5 == 0)
                            {
                                ((IDictionary)Get(mode, "gameBoost")).Remove(child.Id);
                                Require(((IDictionary)Get(mode, "gameBoost")).Count == 0, "Fixture must have an empty broad ledger.");
                                bool clean = (bool)modeType.GetMethod("UnboostGames", AnyInstance, null, Type.EmptyTypes, null).Invoke(mode, null);
                                Require((ulong)Call(native, "QueryAffinity", h) == original, "BUG: empty-ledger teardown left native affinity pinned.");
                                Require(clean, "Empty-ledger teardown did not finish.");
                                ((IDictionary)Get(mode, "gameBoost"))[child.Id] = snap;
                            }
                            else Require((bool)Invoke(mode, "RestoreHardPin", child.Id), "Restore failed.");
                            Require((ulong)Call(native, "QueryAffinity", h) == original, "Restore readback mismatch.");
                        }
                        Require(((IDictionary)Get(mode, "hardPins")).Count == 0, "Retained handle left behind.");
                        Call(guard, "ReleaseBoostProcess", child.Id, creation);
                        Set(mode, "coreEngaged", false);
                        if (cycle == 9) warmHandles = HandleCount();
                        if (cycle >= 9) peakHandles = Math.Max(peakHandles, HandleCount());
                    }
                    GC.Collect(); GC.WaitForPendingFinalizers(); int endHandles = HandleCount();
                    Require(endHandles <= warmHandles + 8, "Sustained handle growth: warm=" + warmHandles + " end=" + endHandles);
                    Console.WriteLine("PASS release lifecycle seed=" + seed + " cycles=" + cycles + ": pin/expand/external ownership/exact restore wall_ms=" + timer.ElapsedMilliseconds
                        + " harness_cpu_ms=" + (CpuMs() - cpuStart).ToString("F1") + " handles_warm=" + warmHandles + " handles_peak=" + peakHandles + " handles_end=" + endHandles);

                    Require((bool)mark.Invoke(null, new object[] { child.Id, creation, name, (uint)0x20, original, -1, -1, -1, new uint[0] }), "Mark failed.");
                    Set(pass, "DesiredMask", bits[1]);
                    Require((bool)Invoke(mode, "PinExclusive", h, child.Id, pass, snap), "Pin before journal-failure test failed.");
                    Call(settings, "SuspendWritesForReset");
                    bool restored;
                    try { restored = (bool)Invoke(mode, "RestoreHardPin", child.Id); }
                    finally { settings.GetField("writesSuspendedForReset", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, false); }
                    Require(!restored, "BUG: restore reported complete while durable receipt clearing failed.");
                    Require((ulong)Call(native, "QueryAffinity", h) == original, "OS affinity should already be restored.");
                    Require(((IDictionary)Get(mode, "hardPins")).Count == 1, "Must retain recovery owner until receipt clear succeeds.");
                    Require((bool)Invoke(mode, "RestoreHardPin", child.Id), "Receipt retry failed.");
                    Require(((IDictionary)Get(mode, "hardPins")).Count == 0, "Retained owner not cleared after retry.");
                    Call(guard, "ReleaseBoostProcess", child.Id, creation);
                    Console.WriteLine("PASS release restore journal failure: OS restored, ownership retained, retry clears receipt");

                    bool reportsReadFailure = modeType.GetMethod("PlacementChangedOutside", AnyInstance).GetParameters().Length == 4;
                    object[] unreadable = reportsReadFailure ? new object[] { IntPtr.Zero, snap, 0UL, false } : new object[] { IntPtr.Zero, snap, 0UL };
                    Require(!(bool)Invoke(mode, "PlacementChangedOutside", unreadable), "Unreadable placement was misclassified as an external writer.");
                    Require(reportsReadFailure && (bool)unreadable[3], "Read failure must be reported explicitly.");
                    Console.WriteLine("PASS unreadable placement is not classified as an outside affinity change");

                    Require((bool)mark.Invoke(null, new object[] { child.Id, creation, name, (uint)0x20, original, -1, -1, -1, new uint[0] }), "Mark failed.");
                    IntPtr readOnly = (IntPtr)Call(native, "OpenProcess", 0x1000, false, child.Id);
                    try
                    {
                        Require(!(bool)Invoke(mode, "PinExclusive", readOnly, child.Id, pass, snap), "Read-only handle unexpectedly wrote affinity.");
                        Require((string)Get(pass, "PlacementStage") == "set-affinity" && (int)Get(pass, "PlacementError") == 5, "Wrong write rejection diagnosis.");
                    }
                    finally { Call(native, "CloseHandle", readOnly); }
                    Require((ulong)Call(native, "QueryAffinity", h) == original, "Rejected pin modified the child.");
                    Set(pass, "RendererCreation", creation + 1); object[] identity = { h, child.Id, pass, 0L };
                    Require(!(bool)Invoke(mode, "VerifyRendererIdentity", identity), "Mismatched creation was accepted.");
                    Set(pass, "RendererCreation", creation); Call(guard, "ReleaseBoostProcess", child.Id, creation);
                    Console.WriteLine("PASS release native write rejection and process identity mismatch");
                    RunThreadLifecycle(product, child, h, creation, original, settings);
                    RunKilledOwnerRecovery(product, registry.Path, child, h, original, bits[1], bits[0]);

                    Call(settings, "SaveStr", "AutoCoreThreadPlacementV1", "bad-first;bad-second");
                    for (int retry = 0; retry < 3; retry++)
                    {
                        Require(!(bool)Call(product.GetType("PaviseApp.CoreThreadPlacement"), "Recover"), "Corrupt journal must not succeed.");
                        Require((string)Call(settings, "LoadStr", "AutoCoreThreadPlacementV1", "") == "bad-first;bad-second", "Recovery changed the remaining receipt order.");
                    }
                    Call(settings, "SaveStr", "AutoCoreThreadPlacementV1", "");
                    Console.WriteLine("PASS corrupt receipt retained in original order across three recovery attempts");

                    // 旧版本留下的有效进程默认 CPU Sets 收据：仍是收据写入的值就还原，已被外部改动就保留外部值；两种情况都清空收据。
                    Type topology = product.GetType("PaviseApp.CpuTopology"), placementType = product.GetType("PaviseApp.CoreThreadPlacement");
                    uint[] applied = (uint[])Call(topology, "CpuSetIdsFor", bits[1] | bits[2]), outside = (uint[])Call(topology, "CpuSetIdsFor", bits[3]);
                    Require(applied != null && applied.Length == 2 && outside != null && outside.Length == 1, "Historical receipt fixture has no CPU Set ids.");
                    string receipt = child.Id + "|" + creation + "|0|0||" + string.Join(",", Array.ConvertAll(applied, x => x.ToString()));
                    Require((bool)Call(native, "RestoreCpuSets", h, applied), "Historical receipt fixture write failed.");
                    Call(settings, "SaveStr", "AutoCoreThreadPlacementV1", receipt);
                    Require((bool)Call(placementType, "Recover"), "Valid historical receipt was not recovered.");
                    Require((bool)Call(native, "CpuSetsMatch", h, new uint[0]), "Historical receipt did not restore the original default CPU Sets.");
                    Require((string)Call(settings, "LoadStr", "AutoCoreThreadPlacementV1", "x") == "", "Recovered receipt stayed in the journal.");
                    Require((bool)Call(native, "RestoreCpuSets", h, outside), "External CPU Sets fixture write failed.");
                    Call(settings, "SaveStr", "AutoCoreThreadPlacementV1", receipt);
                    Require((bool)Call(placementType, "Recover"), "Externally changed historical receipt was not settled.");
                    Require((bool)Call(native, "CpuSetsMatch", h, outside), "Historical receipt recovery overwrote an external CPU Sets change.");
                    Require((string)Call(settings, "LoadStr", "AutoCoreThreadPlacementV1", "x") == "", "Settled receipt stayed in the journal.");
                    Require((bool)Call(native, "RestoreCpuSets", h, new uint[0]), "Fixture CPU Sets reset failed.");
                    Console.WriteLine("PASS valid historical soft placement receipt restored; external CPU Sets change preserved");

                    Require((bool)mark.Invoke(null, new object[] { child.Id, creation, name, (uint)0x20, original, -1, -1, -1, new uint[0] }), "Mark failed.");
                    Set(pass, "DesiredMask", original);
                    Require((bool)Invoke(mode, "PinExclusive", h, child.Id, pass, snap), "Exit fixture pin failed.");
                    RequireRetiredThreadPlacement(product, h);
                    child.StandardInput.WriteLine("exit"); Require(child.WaitForExit(3000), "Target did not exit.");
                    Require((bool)Call(product.GetType("PaviseApp.CoreThreadPlacement"), "Recover"), "Exited target thread receipts were not cleared.");
                    Require((bool)Invoke(mode, "RestoreHardPin", child.Id), "Exited target handle was not released.");
                    Call(guard, "ReleaseBoostProcess", child.Id, creation);
                    Require(!(bool)Call(guard, "HasPending"), "Exited target left recovery debt.");
                    Console.WriteLine("PASS target exit while routed: retained handles and recovery receipts cleared");
                }
                finally
                {
                    settings.GetField("writesSuspendedForReset", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, false);
                    Invoke(mode, "RestoreAllHardPins");
                    Call(native, "SetProcessAffinityMask", h, (UIntPtr)original); Call(native, "CloseHandle", h);
                    if (!child.HasExited) { child.StandardInput.WriteLine("exit"); if (!child.WaitForExit(3000)) { child.Kill(); child.WaitForExit(); } }
                }
            }
        }
        Console.WriteLine("STRESS_LOG=" + Path.Combine(root, "runtime.log"));
    }

    // 核心调度只用硬亲和性：发布版只保留历史软 CPU Sets 收据的静态恢复，没有任何施加入口。
    private static void RequireRetiredThreadPlacement(Assembly product, IntPtr process)
    {
        Type native = product.GetType("PaviseApp.Native"), placement = product.GetType("PaviseApp.CoreThreadPlacement");
        Require(placement.IsAbstract && placement.IsSealed, "Retired thread placement can still be instantiated.");
        foreach (string retired in new[] { "Apply", "Add", "Audit", "Restore", "Save" })
            Require(placement.GetMethod(retired, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null,
                "Release build still carries retired soft thread placement: " + retired);
        Require((bool)Call(native, "CpuSetsMatch", process, new uint[0]), "Retired thread placement changed default CPU Sets.");
    }

    private static void RunThreadLifecycle(Assembly product, Process child, IntPtr h, long creation, ulong original, Type settings)
    {
        Type native = product.GetType("PaviseApp.Native"), placementType = product.GetType("PaviseApp.CoreThreadPlacement");
        for (int cycle = 0; cycle < 50; cycle++)
        {
            RequireRetiredThreadPlacement(product, h);
            uint[] expected = new uint[0];
            try
            {
                if (cycle % 3 == 0)
                    Require((bool)Call(placementType, "Recover"), "Empty historical thread receipt replay failed.");
                if (cycle % 3 == 1)
                {
                    // 外部（用户）设置的 CPU Sets 不归 Pavise 所有，拒绝应用与恢复都不能改动。
                    expected = (uint[])Call(product.GetType("PaviseApp.CpuTopology"), "CpuSetIdsFor", original);
                    Require((bool)Call(native, "RestoreCpuSets", h, expected), "External CPU Sets write failed.");
                }
                Require((bool)Call(placementType, "Recover"), "Historical thread receipt recovery failed.");
                Require((bool)Call(native, "CpuSetsMatch", h, expected), "Historical thread receipt recovery changed external CPU Sets.");
                Require(!(bool)placementType.GetProperty("UnownedPending", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null, null), "Thread owner leaked.");
                Require((string)Call(settings, "LoadStr", "AutoCoreThreadPlacementV1", "") == "", "Thread receipt leaked.");
            }
            finally { Call(native, "RestoreCpuSets", h, new uint[0]); }
        }
        Console.WriteLine("PASS release thread lifecycle cycles=50: soft thread/default CPU Sets refused, no receipt, external CPU Sets preserved");
    }

    private static void RunKilledOwnerRecovery(Assembly product, string registryPath, Process target, IntPtr h, ulong original, ulong pin, ulong outside)
    {
        Type native = product.GetType("PaviseApp.Native"), guard = product.GetType("PaviseApp.CrashGuard");
        string file = product.Location;
        for (int scenario = 0; scenario < 3; scenario++)
        {
            using (var owner = Process.Start(new ProcessStartInfo {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = "--crash-owner \"" + file + "\" \"" + registryPath + "\" " + target.Id + " " + pin + " " + (scenario == 2 ? "thread" : "pin"),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }))
            {
                try
                {
                    var ready = owner.StandardOutput.ReadLineAsync();
                    Require(ready.Wait(10000) && ready.Result == "READY", "Crash owner did not reach the committed placement.");
                    owner.Kill(); Require(owner.WaitForExit(3000), "Owned process did not terminate.");
                    if (scenario == 1) Require((bool)Call(native, "SetProcessAffinityMask", h, (UIntPtr)outside), "External crash-recovery write failed.");
                    Call(guard, "HealFromCrash");
                    Require((ulong)Call(native, "QueryAffinity", h) == (scenario == 1 ? outside : original), "Killed-owner recovery affinity mismatch.");
                    Require((bool)Call(native, "CpuSetsMatch", h, new uint[0]), "Killed-owner recovery left worker sets.");
                    Require(!(bool)Call(guard, "HasPending"), "Killed-owner recovery left receipts.");
                    Call(native, "SetProcessAffinityMask", h, (UIntPtr)original);
                }
                finally { if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(); } }
            }
        }
        Console.WriteLine("PASS three actual owner-process kills: hard pin restored, external affinity preserved, refused soft thread placement left nothing to recover");
    }

    private static int RunCrashOwner(string[] args)
    {
        try
        {
            using (var registry = new IsolatedRegistry(args[2]))
            {
                Assembly product = Assembly.LoadFile(Path.GetFullPath(args[1])); Type native = product.GetType("PaviseApp.Native");
                int pid = int.Parse(args[3]); ulong targetMask = ulong.Parse(args[4]);
                string root = Path.Combine(Path.GetTempPath(), "PaviseCrashOwner-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                product.GetType("PaviseApp.Logger").GetField("LogPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, Path.Combine(root, "runtime.log"));
                Call(product.GetType("PaviseApp.Lang"), "Init");
                IntPtr h = (IntPtr)Call(native, "OpenAffinityProcess", pid); Require(h != IntPtr.Zero, "Crash owner open failed.");
                object[] sample = { h, 0L, 0L, 0UL }; Require((bool)Call(native, "QueryProcessSample", sample), "Crash owner identity failed.");
                long creation = (long)sample[1]; ulong original = (ulong)Call(native, "QueryAffinity", h);
                if (args[5] == "thread")
                {
                    RequireRetiredThreadPlacement(product, h);
                }
                else
                {
                    Type modeType = product.GetType("PaviseApp.GameMode");
                    object mode = Make(modeType, root, Make(product.GetType("PaviseApp.SuppressionCore"), Path.Combine(root, "state")));
                    string name = (string)Call(native, "ImageName", h);
                    object pass = Make(modeType.GetNestedType("BoostPass", BindingFlags.NonPublic));
                    Set(pass, "RendererPid", pid); Set(pass, "RendererCreation", creation); Set(pass, "RendererName", name); Set(pass, "DesiredMask", targetMask);
                    object[] capture = { h, pid, creation, pass, false, false, false, false };
                    Require((bool)Invoke(mode, "CaptureAndTrack", capture), "Crash owner snapshot failed.");
                    object snap = ((IDictionary)Get(mode, "gameBoost"))[pid];
                    Require((bool)Invoke(mode, "PinExclusive", h, pid, pass, snap), "Crash owner pin failed.");
                }
                Console.WriteLine("READY"); Console.Out.Flush(); Console.ReadLine();
                return 0;
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
