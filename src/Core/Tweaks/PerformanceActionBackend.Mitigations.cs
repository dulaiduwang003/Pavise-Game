using System;
using System.Text;

namespace PaviseApp
{
    internal static partial class PerformanceActionBackend
    {
        // 按位恢复安全策略，保留其他原生及持久字段。
        private const string MitigationNativeSource = @"
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32;
public static class PaviseNativeMitigationRestore
{
private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
private const string Prefix = ""Microsoft.Samples.PowerShell.Commands.Ntdll"";
private static readonly string[] Fields = { ""ControlFlowGuard"", ""EnableExportAddressFilter"", ""EnableExportAddressFilterPlus"", ""EnableImportAddressFilter"", ""EnableRopCallerCheck"", ""EnableRopSimExec"" };
private sealed class Group
{
internal Type Type;
internal object Kind, Buffer;
internal MethodInfo Query, Set;
internal string Name;
}
private static Group ReadGroup(Assembly assembly, string target, string typeName, string kindName, uint flags)
{
Type native = assembly.GetType(Prefix, true);
Type type = assembly.GetType(Prefix + ""+"" + typeName, true);
Type kindType = assembly.GetType(Prefix + ""+IMAGE_MITIGATION_POLICY"", true);
var group = new Group { Type = type, Kind = Enum.Parse(kindType, kindName), Buffer = Activator.CreateInstance(type), Name = kindName };
foreach (MethodInfo method in native.GetMethods(Flags))
{
if (!method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1 || method.GetParameters().Length != 4) continue;
if (method.Name == ""QueryImageMitigationPolicy"") group.Query = method.MakeGenericMethod(type);
if (method.Name == ""SetImageMitigationPolicy"") group.Set = method.MakeGenericMethod(type);
}
if (group.Query == null || group.Set == null) throw new NotSupportedException(""Unsupported image mitigation API"");
FieldInfo modules = type.GetField(""ModuleList"");
if (modules != null)
{
var marshal = (MarshalAsAttribute)Attribute.GetCustomAttribute(modules, typeof(MarshalAsAttribute));
if (marshal == null || marshal.SizeConst != 512) throw new NotSupportedException(""Unsupported mitigation module list"");
modules.SetValue(group.Buffer, new char[marshal.SizeConst]);
}
object[] args = { target, group.Kind, flags, group.Buffer };
int status = (int)group.Query.Invoke(null, args);
// An absent application has no local overrides. Every other error must retain its receipt.
if (status != 0 && status != unchecked((int)0xc0000034)) ThrowStatus(""Query "" + kindName, status);
if (status == 0) group.Buffer = args[3];
return group;
}
private static Group ReadCfg(Assembly assembly, string target)
{ return ReadGroup(assembly, target, ""RTL_IMAGE_MITIGATION_CONTROL_FLOW_GUARD_POLICY"", ""ImageControlFlowGuardPolicy"", 0); }
private static Group ReadPayload(Assembly assembly, string target, uint flags)
{ return ReadGroup(assembly, target, ""RTL_IMAGE_MITIGATION_PAYLOAD_RESTRICTION_POLICY"", ""ImagePayloadRestrictionPolicy"", flags); }
private static void ThrowStatus(string operation, int status)
{ throw new InvalidOperationException(operation + "" failed: 0x"" + unchecked((uint)status).ToString(""X8"")); }
private static ulong ReadField(Group group, string name)
{
FieldInfo field = group.Type.GetField(name);
if (field == null) throw new NotSupportedException(""Missing mitigation field "" + name);
object policy = field.GetValue(group.Buffer);
FieldInfo state = policy.GetType().GetField(""PolicyState"");
if (state == null || state.FieldType != typeof(ulong)) throw new NotSupportedException(""Unsupported mitigation state"");
return (ulong)state.GetValue(policy);
}
private static bool ChangeField(Group group, string name, ulong bits, ulong mask)
{
ulong oldValue = ReadField(group, name), wanted = (oldValue & ~mask) | bits;
if (wanted == oldValue) return false;
FieldInfo field = group.Type.GetField(name);
object policy = field.GetValue(group.Buffer);
policy.GetType().GetField(""PolicyState"").SetValue(policy, wanted);
field.SetValue(group.Buffer, policy);
return true;
}
private static void Commit(Group group, string target, bool changed)
{
if (!changed) return;
int status = (int)group.Set.Invoke(null, new object[] { target, group.Kind, (uint)0, group.Buffer });
if (status != 0) ThrowStatus(""Set "" + group.Name, status);
}
private static bool Same(byte[] a, byte[] b)
{
if (a == null || b == null || a.Length != b.Length) return false;
for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
return true;
}
private static void ValidateTarget(string target)
{
if (string.IsNullOrEmpty(target) || !Path.IsPathRooted(target) || !string.Equals(Path.GetFullPath(target), target, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(target).Length == 0)
throw new NotSupportedException(""An absolute canonical image path is required"");
}
private static void WriteCfg(Assembly assembly, string target, ulong bits, byte mask)
{
if (!Path.IsPathRooted(target) || Path.GetFileName(target).Length == 0)
throw new NotSupportedException(""An absolute image path is required"");
Group cfg = ReadCfg(assembly, target);
if ((ReadField(cfg, ""ControlFlowGuard"") & mask) == bits) return;
string path = Path.GetFullPath(target);
const string root = ""SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\"";
using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
using (RegistryKey image = machine.OpenSubKey(root + Path.GetFileName(path), false))
{
if (image == null || !(image.GetValue(""UseFilter"") is int) || (int)image.GetValue(""UseFilter"") != 1)
throw new NotSupportedException(""No image path mitigation filter"");
string match = null;
foreach (string child in image.GetSubKeyNames())
{
using (RegistryKey filter = image.OpenSubKey(child, false))
{
string fullPath = filter == null ? null : filter.GetValue(""FilterFullPath"") as string;
if (fullPath == null || !string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase)) continue;
if (match != null) throw new NotSupportedException(""Multiple image mitigation filters"");
match = child;
}
}
if (match == null) throw new NotSupportedException(""No exact image path mitigation filter"");
using (RegistryKey filter = machine.OpenSubKey(root + Path.GetFileName(path) + ""\\"" + match, true))
{
if (filter == null || !string.Equals(filter.GetValue(""FilterFullPath"") as string, path, StringComparison.OrdinalIgnoreCase))
throw new NotSupportedException(""Image mitigation filter changed"");
for (int attempt = 0; attempt < 3; attempt++)
{
byte[] current = filter.GetValue(""MitigationOptions"") as byte[];
if (current == null || (current.Length != 16 && current.Length != 24) || filter.GetValueKind(""MitigationOptions"") != RegistryValueKind.Binary)
throw new NotSupportedException(""Unsupported image mitigation options"");
ulong actual = ReadField(ReadCfg(assembly, target), ""ControlFlowGuard"");
if ((ulong)(current[5] & 7) != (actual & 7)) throw new NotSupportedException(""Image mitigation layout does not match the native query"");
if (!Same(current, filter.GetValue(""MitigationOptions"") as byte[])) continue;
byte[] wanted = (byte[])current.Clone();
// CFG uses bits 40..42. Changing this field through RtlSet also forces StrictCFG off.
// Restrict this write to the exact path filter and preserve every other persisted bit.
wanted[5] = (byte)((wanted[5] & ~mask) | (int)bits);
if (Same(current, wanted)) return;
filter.SetValue(""MitigationOptions"", wanted, RegistryValueKind.Binary);
if (!Same(wanted, filter.GetValue(""MitigationOptions"") as byte[]))
throw new InvalidOperationException(""Image mitigation write could not be verified"");
return;
}
throw new InvalidOperationException(""Image mitigation options kept changing"");
}
}
}
private static ulong[] Validate(string[] states)
{
if (states == null || states.Length != 5 && states.Length != Fields.Length) throw new FormatException(""Invalid mitigation snapshot"");
var parsed = new ulong[states.Length];
for (int i = 0; i < states.Length; i++)
{
string[] pieces = (states[i] ?? """").Split(':'); bool force;
if (pieces.Length != 2 || !bool.TryParse(pieces[1], out force) || (pieces[0] != ""ON"" && pieces[0] != ""OFF"" && pieces[0] != ""NOTSET""))
throw new FormatException(""Invalid mitigation state"");
parsed[i] = (pieces[0] == ""ON"" ? 1UL : pieces[0] == ""OFF"" ? 2UL : 0UL) | (force ? 4UL : 0UL);
}
return parsed;
}
public static void Restore(Assembly assembly, string target, string[] states)
{
ValidateTarget(target);
ulong[] wanted = Validate(states);
Group payload = ReadPayload(assembly, target, 0);
bool payloadChanged = false;
for (int i = 1; i < wanted.Length; i++) payloadChanged |= ChangeField(payload, Fields[i], wanted[i], 7);
Commit(payload, target, payloadChanged);
WriteCfg(assembly, target, wanted[0], 7);
}
public static void Apply(Assembly assembly, string target, bool includeSimExec)
{
ValidateTarget(target);
Group payload = ReadPayload(assembly, target, 0);
bool payloadChanged = false;
for (int i = 1; i < (includeSimExec ? Fields.Length : Fields.Length - 1); i++) payloadChanged |= ChangeField(payload, Fields[i], 2, 3);
Commit(payload, target, payloadChanged);
WriteCfg(assembly, target, 2, 3);
}
}
";
        private static string MitigationNativeScript(string target, string value)
        {
            bool apply = value == "off32" || value == "off64";
            string[] states = apply ? null : (value ?? "").Split(';');
            if (!apply)
            {
                if (states.Length != 5 && states.Length != 6) throw new FormatException("Invalid mitigation snapshot");
                foreach (string state in states)
                {
                    string[] pieces = state.Split(':'); bool force;
                    if (pieces.Length != 2 || !bool.TryParse(pieces[1], out force) || (pieces[0] != "ON" && pieces[0] != "OFF" && pieces[0] != "NOTSET"))
                        throw new FormatException("Invalid mitigation state");
                }
            }
            var script = new StringBuilder("Import-Module ProcessMitigations -ErrorAction Stop -WarningAction Stop; if($null-eq('PaviseNativeMitigationRestore'-as[type])){Add-Type -TypeDefinition @'\n");
            script.Append(MitigationNativeSource).Append("\n'@ -ErrorAction Stop\n}; $a=(Get-Command Set-ProcessMitigation -ErrorAction Stop).ImplementingType.Assembly; [PaviseNativeMitigationRestore]::");
            if (apply) script.Append("Apply($a, ").Append(Quote(target)).Append(value == "off32" ? ", $true)" : ", $false)");
            else
            {
                script.Append("Restore($a, ").Append(Quote(target)).Append(", [string[]]@(");
                for (int i = 0; i < states.Length; i++)
                {
                    if (i != 0) script.Append(',');
                    script.Append(Quote(states[i]));
                }
                script.Append("))");
            }
            if (script.Length > 11500) throw new NotSupportedException("Image mitigation command is too long");
            return script.ToString();
        }
    }
}