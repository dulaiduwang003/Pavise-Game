// Reflection-only metadata and IL inspection. Never invokes inspected assemblies.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

internal static class InspectExporter
{
    private static readonly Dictionary<ushort, OpCode> codes = new Dictionary<ushort, OpCode>();
    private static int Main(string[] args)
    {
        try
        {
            string file = Path.GetFullPath(args[0]), directory = Path.GetDirectoryName(file);
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += delegate(object sender, ResolveEventArgs request) {
                string candidate = Path.Combine(directory, new AssemblyName(request.Name).Name + ".dll");
                return File.Exists(candidate) ? Assembly.ReflectionOnlyLoadFrom(candidate) : Assembly.ReflectionOnlyLoad(request.Name);
            };
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.FieldType == typeof(OpCode)) { var code = (OpCode)field.GetValue(null); codes[unchecked((ushort)code.Value)] = code; }
            Assembly assembly = Assembly.ReflectionOnlyLoadFrom(file);
            Console.WriteLine("REFLECTION ONLY " + assembly.FullName);
            foreach (Type type in assembly.GetTypes())
            {
                Console.WriteLine("TYPE " + type.FullName);
                foreach (MethodBase method in Methods(type))
                {
                    MethodBody body = method.GetMethodBody();
                    if (body == null) continue;
                    Console.WriteLine("METHOD " + method);
                    byte[] bytes = body.GetILAsByteArray();
                    for (int at = 0; at < bytes.Length;)
                    {
                        int offset = at; ushort key = bytes[at++];
                        if (key == 0xfe) key = (ushort)(0xfe00 | bytes[at++]);
                        OpCode code = codes[key]; string operand = ""; int size;
                        switch (code.OperandType)
                        {
                            case OperandType.InlineNone: size = 0; break;
                            case OperandType.ShortInlineBrTarget:
                            case OperandType.ShortInlineI:
                            case OperandType.ShortInlineVar: size = 1; operand = bytes[at].ToString(); break;
                            case OperandType.InlineVar: size = 2; operand = BitConverter.ToUInt16(bytes, at).ToString(); break;
                            case OperandType.InlineI8:
                            case OperandType.InlineR: size = 8; break;
                            case OperandType.InlineSwitch: size = 4 + 4 * BitConverter.ToInt32(bytes, at); break;
                            default:
                                size = 4; int token = BitConverter.ToInt32(bytes, at);
                                try {
                                    if (code.OperandType == OperandType.InlineString) operand = "\"" + method.Module.ResolveString(token) + "\"";
                                    else if (code.OperandType == OperandType.InlineField || code.OperandType == OperandType.InlineMethod
                                        || code.OperandType == OperandType.InlineType || code.OperandType == OperandType.InlineTok)
                                        operand = method.Module.ResolveMember(token).ToString();
                                    else operand = token.ToString();
                                } catch { operand = "token:0x" + token.ToString("X8"); }
                                break;
                        }
                        at += size;
                        Console.WriteLine("  " + offset.ToString("X4") + " " + code.Name + " " + operand);
                    }
                }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static IEnumerable<MethodBase> Methods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (MethodBase method in type.GetConstructors(flags)) yield return method;
        foreach (MethodBase method in type.GetMethods(flags)) yield return method;
    }
}
