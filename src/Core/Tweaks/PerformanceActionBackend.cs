using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class PerformanceActionBackend
    {
#if PAVISE_SELFTEST
        internal static Func<string, string, string> ReadForTest;
        internal static Func<string, string, string, bool> WriteForTest;
        internal static Func<string, string[]> TargetsForTest;
        internal static Func<int> DefenderServiceForTest;
        internal static Func<string> ResourcePlatformForTest;
        internal static Func<string, string> ShortNameVolumeForTest;
        internal static Func<bool> NvAvailableForTest;
#endif
        internal sealed class DefenderInactiveException : NotSupportedException
        {
            internal DefenderInactiveException() : base(AutomaticPolicy.Text("Defender 未运行，无需执行扫描优化", "Defender is not running; scan optimization is unnecessary")) { }
        }
        internal static bool DefenderRunning()
        {
#if PAVISE_SELFTEST
            if (DefenderServiceForTest == null) throw new InvalidOperationException("Unmocked Defender service query");
            return DefenderServiceForTest() == 4;
#else
            return SvcState.Query("WinDefend") == 4;
#endif
        }
        private static bool DefenderAction(string id)
        { return id == PerformanceActionCatalog.LegacyDefenderSession || id == PerformanceActionCatalog.DefenderResources; }
        // 旧版对局 Defender 调整已移除，残留记录只做恢复：只重新开启防护，从不发出关闭命令。
        // 返回 null 表示值无法识别；空串表示两项都保持开启状态的原值，无需写入。
        internal static string LegacyDefenderRestoreScript(string value)
        {
            string[] p = (value ?? "").Split(','); bool realtime, behavior;
            if (p.Length != 2 || !bool.TryParse(p[0], out realtime) || !bool.TryParse(p[1], out behavior)) return null;
            if (realtime && behavior) return "";
            return "Set-MpPreference" + (realtime ? "" : " -DisableRealtimeMonitoring $false")
                + (behavior ? "" : " -DisableBehaviorMonitoring $false") + " -ErrorAction Stop";
        }
        internal static string Quote(string value) { return "'" + (value ?? "").Replace("'", "''") + "'"; }
        private static string Script(string script)
        {
            string result, failure;
            if (!PsRunner.RunWithFailure("$ErrorActionPreference='Stop'; " + script, "Performance", 10000, out result, out failure))
                throw new InvalidOperationException(failure);
            return result.Trim();
        }
        internal static string[] MitigationNames(string target)
        { return MitigationNames(Is32Bit(target)); }
        private static string[] MitigationNames(bool includeSimExec)
        {
            var names = new List<string> { "CFG", "EnableExportAddressFilter", "EnableExportAddressFilterPlus",
                "EnableImportAddressFilter", "EnableRopCallerCheck" };
            if (includeSimExec) names.Add("EnableRopSimExec");
            return names.ToArray();
        }
        internal static bool Is32Bit(string target)
        {
            try
            {
                using (var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return false;
                    stream.Position = 0x3c; uint offset = reader.ReadUInt32();
                    if (offset > stream.Length - 26) return false;
                    stream.Position = offset;
                    if (reader.ReadUInt32() != 0x4550) return false;
                    ushort machine = reader.ReadUInt16();
                    stream.Position = offset + 24;
                    return machine == 0x14c && reader.ReadUInt16() == 0x10b;
                }
            }
            catch { return false; }
        }
        internal static bool MitigationsOff(string value)
        { return MitigationsOff(value, true); }
        internal static bool MitigationsOff(string value, bool includeSimExec)
        {
            string[] states = (value ?? "").Split(';');
            if (states.Length != 5 && states.Length != 6) return false;
            for (int i = 0; i < (includeSimExec ? states.Length : 5); i++)
                if (!states[i].StartsWith("OFF:", StringComparison.Ordinal)) return false;
            return true;
        }
        internal static string MitigationQuery(string target)
        {
            var script = new StringBuilder("Import-Module ProcessMitigations; $q=Get-ProcessMitigation -Name " + Quote(target) + "; $r=@(); ");
            foreach (string name in MitigationNames(true))
            {
                string section = name == "CFG" ? "CFG" : "Payload";
                string property = name == "CFG" ? "Enable" : name;
                string force = name == "CFG" ? "OverrideCFG" : "Override" + name;
                script.Append("if($null-eq$q){$r+='NOTSET:False'}else{$r+=($q." + section + "." + property
                    + ".ToString()+':'+$q." + section + "." + force + ".ToString())}; ");
            }
            return script.Append("Write-Output ($r -join ';')").ToString();
        }
        internal static string MitigationWrite(string target, string value)
        {
            return MitigationNativeScript(target, value);
        }
        internal static string Read(string id, string target)
        {
            if (DefenderAction(id) && !DefenderRunning()) throw new DefenderInactiveException();
#if PAVISE_SELFTEST
            if (ReadForTest == null) throw new InvalidOperationException("Unmocked performance read");
            return ReadForTest(id, target);
#else
            if (id == PerformanceActionCatalog.VisualEffects) return MatchVisualEffects.Read(target);
            if (id == PerformanceActionCatalog.Mitigations) return Script(MitigationQuery(target));
            if (id == PerformanceActionCatalog.LegacyDefenderSession)
                return Script("$p=Get-MpPreference; Write-Output ($p.DisableRealtimeMonitoring.ToString()+','+$p.DisableBehaviorMonitoring.ToString())");
            if (id == PerformanceActionCatalog.DefenderResources)
                return Script("$p=Get-MpPreference; if(@($p.ExclusionPath)-contains " + Quote(target) + "){Write-Output '1'}else{Write-Output '0'}");
            if (id == PerformanceActionCatalog.ShortNames)
                return Script("$v=& $env:SystemRoot\\System32\\fsutil.exe 8dot3name query " + Quote(target)
                    + "; if($LASTEXITCODE-ne0){throw 'Volume query failed'}; $m=[regex]::Match(($v -join \"`n\"),'[：:]\\s*([01])\\s*(?:\\(|$)'); if(!$m.Success){throw 'Unknown volume state'}; Write-Output $m.Groups[1].Value");
            if (id == PerformanceActionCatalog.BackgroundFps || id == PerformanceActionCatalog.OpenGl) return ReadNv(id, target);
            return ReadRegistry(target);
#endif
        }
        internal static bool Write(string id, string target, string value)
        {
            if (DefenderAction(id) && !DefenderRunning()) throw new DefenderInactiveException();
#if PAVISE_SELFTEST
            if (WriteForTest == null) throw new InvalidOperationException("Unmocked performance write");
            return WriteForTest(id, target, value);
#else
            if (id == PerformanceActionCatalog.VisualEffects) return MatchVisualEffects.Write(target, value);
            if (id == PerformanceActionCatalog.Mitigations) { Script(MitigationWrite(target, value)); return true; }
            if (id == PerformanceActionCatalog.LegacyDefenderSession)
            {
                string script = LegacyDefenderRestoreScript(value);
                if (script == null) return false;
                if (script.Length > 0) Script(script);
                return true;
            }
            if (id == PerformanceActionCatalog.DefenderResources)
            { Script((value == "1" ? "Add" : "Remove") + "-MpPreference -ExclusionPath " + Quote(target) + " -ErrorAction Stop"); return true; }
            if (id == PerformanceActionCatalog.ShortNames)
            {
                if (value != "0" && value != "1") return false;
                Script("& $env:SystemRoot\\System32\\fsutil.exe 8dot3name set " + Quote(target) + " " + value
                    + "; if($LASTEXITCODE-ne0){throw 'Volume configuration failed'}"); return true;
            }
            if (id == PerformanceActionCatalog.BackgroundFps || id == PerformanceActionCatalog.OpenGl) return WriteNv(id, target, value);
            return WriteRegistry(target, value);
#endif
        }
        internal static bool Apply(string id, string target, string wanted)
        {
            if (id == PerformanceActionCatalog.LegacyDefenderSession)
                throw new InvalidOperationException("The legacy Defender change is restore-only");
            Func<PerformanceActionStore.Receipt, string, PerformanceActionStore.Receipt> reconcile = null;
            if (id == PerformanceActionCatalog.Mitigations && (wanted == "off32" || wanted == "off64"))
                reconcile = delegate(PerformanceActionStore.Receipt receipt, string current) { return ReconcileMitigations(receipt, current, wanted); };
            bool ok = PerformanceActionStore.Change(id, target, wanted, delegate { return Read(id, target); },
                delegate(string value) { return Write(id, target, value); },
                delegate(string value) {
                    uint cap;
                    if (id == PerformanceActionCatalog.BackgroundFps && uint.TryParse(value, out cap) && cap > 0 && cap <= 30) return true;
                    return id == PerformanceActionCatalog.Mitigations ? MitigationsOff(value, wanted == "off32") : value == wanted;
                }, reconcile);
            return ok;
        }
        internal static string ApplyFailureHint(string id, string target)
        {
            string reason = PerformanceActionStore.LastChangeFailure;
            if (reason == "Recovery record ownership does not match the target")
                return AutomaticPolicy.Text("恢复记录与目标不一致，未写入", reason);
            if (reason == "Recovery snapshot cannot be reconciled")
                return AutomaticPolicy.Text("恢复记录格式无法核对，已保留记录", reason);
            if (reason == "Configuration kept changing during reconciliation")
                return AutomaticPolicy.Text("配置持续被外部修改，本次未写入", reason);
            if (reason == "Could not save the reconciled recovery record" || reason == "Could not save the recovery record")
                return AutomaticPolicy.Text("原配置记录保存失败，本次未写入", reason);
            if (reason == "Could not commit the recovery record")
                return AutomaticPolicy.Text("写入后的恢复记录保存失败，已保留记录", reason);
            if (reason == "Configuration write did not complete")
                return AutomaticPolicy.Text("配置写入未完成，已保留恢复记录", reason);
            if (reason == "Configuration did not match the requested state after writing")
                return AutomaticPolicy.Text("配置写入后复核不一致，已保留恢复记录", reason);
            if (reason == "Legacy recovery record must be reconciled before enabling SimExec")
                return AutomaticPolicy.Text("旧恢复记录需先核对第六项，未写入", reason);
            if (reason == "Configuration changed externally; recovery record retained")
                return AutomaticPolicy.Text("配置已被外部修改，已保留恢复记录", reason);
            if (reason == "Configuration changed externally; previous ownership released")
                return AutomaticPolicy.Text("配置已被外部修改，已解除旧配置托管", reason);
            if (reason == "Configuration changed externally; recovery record could not be cleared")
                return AutomaticPolicy.Text("配置已被外部修改，旧恢复记录清理未完成", reason);
            return reason ?? AutomaticPolicy.Text("写入或复核未完成，已保留恢复记录",
                "Write or verification incomplete; recovery record retained");
        }
        private static bool MitigationSnapshot(string value, out string[] parts)
        {
            parts = (value ?? "").Split(';');
            if (parts.Length != 5 && parts.Length != 6) return false;
            foreach (string part in parts)
            {
                string[] p = part.Split(':'); bool force;
                if (p.Length != 2 || !bool.TryParse(p[1], out force) || (p[0] != "ON" && p[0] != "OFF" && p[0] != "NOTSET")) return false;
            }
            return true;
        }
        private static PerformanceActionStore.Receipt ReconcileMitigations(PerformanceActionStore.Receipt receipt, string current, string wanted)
        {
            string[] original, actual, expected;
            if (!MitigationSnapshot(receipt.Original, out original) || !MitigationSnapshot(current, out actual) || actual.Length != 6) return null;
            bool snapshotExpected = MitigationSnapshot(receipt.Expected, out expected);
            if (receipt.Committed && (!snapshotExpected || expected.Length < original.Length)) return null;
            if (!receipt.Committed && !snapshotExpected && receipt.Expected != "off32" && receipt.Expected != "off64") return null;
            if (snapshotExpected && expected.Length < original.Length) return null;
            var rebased = (string[])actual.Clone();
            for (int i = 0; i < original.Length; i++)
            {
                bool owned = snapshotExpected ? actual[i] == expected[i]
                    : (i < 5 || receipt.Expected == "off32") && actual[i].StartsWith("OFF:", StringComparison.Ordinal);
                if (owned) rebased[i] = original[i];
            }
            var planned = (string[])actual.Clone();
            for (int i = 0; i < (wanted == "off32" ? 6 : 5); i++)
                planned[i] = "OFF:" + actual[i].Split(':')[1];
            return new PerformanceActionStore.Receipt { Id = receipt.Id, Target = receipt.Target,
                Original = string.Join(";", rebased), Expected = string.Join(";", planned), Committed = false };
        }
        internal static bool Restore(PerformanceActionStore.Receipt r)
        {
            if (DefenderAction(r.Id) && !DefenderRunning()) return false;
            if (r.Id == PerformanceActionCatalog.Mitigations || r.Id == PerformanceActionCatalog.LegacyDefenderSession)
            {
                char separator = r.Id == PerformanceActionCatalog.Mitigations ? ';' : ',';
                string[] original = r.Original.Split(separator);
                string[] expected = r.Committed ? r.Expected.Split(separator) : null;
                if (!r.Committed && r.Id == PerformanceActionCatalog.Mitigations)
                {
                    string[] planned;
                    if (MitigationSnapshot(r.Expected, out planned)) expected = planned;
                }
                return PerformanceActionStore.RestoreComponents(r, delegate { return Read(r.Id, r.Target); },
                    delegate(string value) { return Write(r.Id, r.Target, value); }, delegate(string current)
                    {
                        string[] actual = current.Split(separator);
                        bool legacyMitigations = r.Id == PerformanceActionCatalog.Mitigations && original.Length == 5 && actual.Length == 6;
                        if (actual.Length != original.Length && !legacyMitigations) return null;
                        if (expected != null && expected.Length != actual.Length && !(legacyMitigations && expected.Length == 5)) return null;
                        var merged = (string[])actual.Clone();
                        // 旧收据未记录 SimExec，恢复时保持原样。
                        for (int i = 0; i < original.Length; i++)
                        {
                            bool ours = expected != null ? actual[i] == expected[i]
                                : r.Id == PerformanceActionCatalog.LegacyDefenderSession ? actual[i] == "True"
                                : (i < 5 || r.Expected == "off32") && actual[i].StartsWith("OFF:", StringComparison.Ordinal);
                            if (ours) merged[i] = original[i];
                        }
                        return string.Join(separator.ToString(), merged);
                    });
            }
            return PerformanceActionStore.Restore(r, delegate { return Read(r.Id, r.Target); },
                delegate(string value) { return Write(r.Id, r.Target, value); },
                delegate(string value) { return value == r.Expected; });
        }
        internal static string RestoreFailureHint(PerformanceActionStore.Receipt r)
        {
            if (DefenderAction(r.Id) && !DefenderRunning())
                return AutomaticPolicy.Text("Defender 未运行，已保留恢复记录", "Defender is not running; recovery record retained");
            if (r.Id == PerformanceActionCatalog.BackgroundFps || r.Id == PerformanceActionCatalog.OpenGl)
            {
#if PAVISE_SELFTEST
                bool available = NvAvailableForTest == null || NvAvailableForTest();
#else
                bool available = NvApi.Available;
#endif
                if (!available)
                    return AutomaticPolicy.Text("显卡驱动接口不可用，已保留恢复记录", "Graphics driver interface unavailable; recovery record retained");
            }
            return AutomaticPolicy.Text("写入或复核未完成，已保留恢复记录", "Write or verification incomplete; recovery record retained");
        }
        internal static string RegistryTarget(string path, string name) { return path + "\n" + name; }
        internal static string ReadRegistry(string target)
        {
            string[] p = target.Split('\n');
            if (p.Length != 2 || !p[0].StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Invalid registry target");
            using (var key = Registry.LocalMachine.OpenSubKey(p[0]))
            {
                object value = key == null ? null : key.GetValue(p[1]);
                if (value == null) return "absent";
                RegistryValueKind kind = key.GetValueKind(p[1]);
                if (kind == RegistryValueKind.DWord) return "d:" + ((int)value).ToString(CultureInfo.InvariantCulture);
                if (kind == RegistryValueKind.Binary) return "b:" + Convert.ToBase64String((byte[])value);
                throw new InvalidOperationException("Unexpected registry value type");
            }
        }
        private static bool WriteRegistry(string target, string value)
        {
            string[] p = target.Split('\n');
            if (p.Length != 2 || !p[0].StartsWith("SYSTEM\\", StringComparison.OrdinalIgnoreCase)) return false;
            using (var key = Registry.LocalMachine.CreateSubKey(p[0]))
            {
                if (key == null) return false;
                if (value == "absent") key.DeleteValue(p[1], false);
                else if (value.StartsWith("d:", StringComparison.Ordinal)) key.SetValue(p[1], int.Parse(value.Substring(2), CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                else if (value.StartsWith("b:", StringComparison.Ordinal)) key.SetValue(p[1], Convert.FromBase64String(value.Substring(2)), RegistryValueKind.Binary);
                else return false;
            }
            return true;
        }
        internal static uint Dword(string value)
        {
            if (value == "absent") return 0u;
            if (value == null || !value.StartsWith("d:", StringComparison.Ordinal)) throw new FormatException("Expected a DWORD setting");
            return unchecked((uint)int.Parse(value.Substring(2), CultureInfo.InvariantCulture));
        }
        internal static string WantedRegistry(string id, string original)
        {
            if (id == PerformanceActionCatalog.CpuMitigations) return "d:" + unchecked((int)(Dword(original) | 3u)).ToString(CultureInfo.InvariantCulture);
            if (id == PerformanceActionCatalog.Quantum) return "d:" + unchecked((int)((Dword(original) & ~63u) | (uint)QuantumProfiles.Target)).ToString(CultureInfo.InvariantCulture);
            if (id == PerformanceActionCatalog.AudioIdle)
            {
                if (original == null || !original.StartsWith("b:", StringComparison.Ordinal) || Convert.FromBase64String(original.Substring(2)).Length != 4)
                    throw new FormatException("Expected a four-byte audio idle setting");
                return "b:" + Convert.ToBase64String(new byte[4]);
            }
            Dword(original);
            return "d:1";
        }
        internal static string[] EnvironmentTargets(string id)
        {
#if PAVISE_SELFTEST
            if (TargetsForTest == null) throw new InvalidOperationException("Unmocked performance target discovery");
            return TargetsForTest(id);
#else
            if (id == PerformanceActionCatalog.CpuMitigations) return new[] {
                RegistryTarget(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride"),
                RegistryTarget(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverrideMask") };
            if (id == PerformanceActionCatalog.LastAccess) return new[] { RegistryTarget(@"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate") };
            if (id == PerformanceActionCatalog.Quantum) return new[] { RegistryTarget(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation") };
            var targets = new List<string>();
            if (id == PerformanceActionCatalog.AudioIdle)
            {
                const string root = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}";
                using (var key = Registry.LocalMachine.OpenSubKey(root))
                    if (key != null) foreach (string name in key.GetSubKeyNames())
                    {
                        string path = root + "\\" + name + "\\PowerSettings";
                        using (var power = Registry.LocalMachine.OpenSubKey(path))
                            if (power != null && power.GetValue("PerformanceIdleTime") is byte[]
                                && ((byte[])power.GetValue("PerformanceIdleTime")).Length == 4)
                                targets.Add(RegistryTarget(path, "PerformanceIdleTime"));
                    }
            }
            else
            {
                bool disk = id == PerformanceActionCatalog.StorageIdle;
                string root = @"SYSTEM\CurrentControlSet\Enum\" + (disk ? "SCSI" : "PCI");
                string classId = disk ? "{4d36e967-e325-11ce-bfc1-08002be10318}" : "{4d36e97b-e325-11ce-bfc1-08002be10318}";
                string setting = disk ? "DisableIdlePowerManagement" : id == PerformanceActionCatalog.StorageRuntime ? "DisableRuntimePowerManagement" : "DisableD3Cold";
                using (var key = Registry.LocalMachine.OpenSubKey(root))
                    if (key != null) foreach (string hardware in key.GetSubKeyNames())
                    using (var h = key.OpenSubKey(hardware))
                        if (h != null) foreach (string instance in h.GetSubKeyNames())
                        using (var device = h.OpenSubKey(instance))
                            if (device != null && string.Equals(device.GetValue("ClassGUID") as string, classId, StringComparison.OrdinalIgnoreCase))
                            using (var port = device.OpenSubKey(@"Device Parameters\StorPort"))
                                if (port != null) targets.Add(RegistryTarget(root + "\\" + hardware + "\\" + instance + @"\Device Parameters\StorPort", setting));
            }
            return targets.ToArray();
#endif
        }
        internal static string ShortNameVolume(string exe)
        {
#if PAVISE_SELFTEST
            if (ShortNameVolumeForTest != null) return ShortNameVolumeForTest(exe);
            return Path.GetPathRoot(exe).TrimEnd('\\');
#else
            string root = Path.GetPathRoot(exe);
            var drive = new DriveInfo(root);
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem"))
            {
                object setting = key == null ? null : key.GetValue("NtfsDisable8dot3NameCreation");
                if (!(setting is int) || (int)setting != 2 || drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS") return null;
            }
            return root.TrimEnd('\\');
#endif
        }
        internal static string ResourcePlatform()
        {
            if (!DefenderRunning()) throw new DefenderInactiveException();
#if PAVISE_SELFTEST
            if (ResourcePlatformForTest == null) throw new InvalidOperationException("Unmocked Defender platform query");
            string platform = ResourcePlatformForTest();
            if (platform != "contextual" && platform != "directory") throw new InvalidOperationException("Defender platform version unavailable");
            return platform;
#else
            string version = Script("$s=Get-MpComputerStatus -ErrorAction Stop; if([string]::IsNullOrEmpty($s.AMProductVersion)){throw 'Defender platform version unavailable'}; Write-Output $s.AMProductVersion");
            Version parsed;
            if (!Version.TryParse(version, out parsed)) throw new InvalidOperationException("Defender platform version unavailable");
            return parsed >= new Version(4, 18, 2205, 7) ? "contextual" : "directory";
#endif
        }
        internal static string ResourceTarget(string exe, string root = null, bool contextual = true)
        {
            string folder = !string.IsNullOrEmpty(root) && FamilyBoundary.SafeFamilyDir(root) && FamilyBoundary.UnderRoot(exe, root)
                ? root.TrimEnd('\\') : Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(folder) || folder.Length <= Path.GetPathRoot(folder).Length
                || folder.IndexOf('{') >= 0 || folder.IndexOf('}') >= 0 || exe.IndexOf('{') >= 0 || exe.IndexOf('}') >= 0) return null;
            if (!DefenderRunning()) throw new DefenderInactiveException();
            return contextual ? folder + "\\:{Process:" + exe + "}" : folder;
        }
        private static uint NvId(string id)
        { return id == PerformanceActionCatalog.OpenGl ? NvApi.SettingOpenGlThreadControl : NvApi.SettingFrlFpsBackground; }
        private static string ReadNv(string id, string target)
        { lock (NvDrsTweaks.SessionGate) return ReadNvCore(id, target); }
        private static string ReadNvCore(string id, string target)
        {
            IntPtr session, profile;
            if (!NvApi.Available) throw new NotSupportedException("NVIDIA unavailable");
            if (!NvApi.TryOpenSession(out session)) throw new InvalidOperationException("NVIDIA session unavailable");
            try
            {
                if (!NvApi.FindOrCreateAppProfile(session, Path.GetFileName(target), out profile)) throw new InvalidOperationException("NVIDIA profile unavailable");
                uint value; int status = NvApi.TryGetDword(session, profile, NvId(id), out value);
                if (status < 0) throw new InvalidOperationException("NVIDIA setting unavailable");
                return status == 0 ? "absent" : value.ToString(CultureInfo.InvariantCulture);
            }
            finally { NvApi.CloseSession(session); }
        }
        private static bool WriteNv(string id, string target, string value)
        { lock (NvDrsTweaks.SessionGate) return WriteNvCore(id, target, value); }
        private static bool WriteNvCore(string id, string target, string value)
        {
            IntPtr session, profile;
            if (!NvApi.TryOpenSession(out session)) return false;
            try
            {
                if (!NvApi.FindOrCreateAppProfile(session, Path.GetFileName(target), out profile)) return false;
                int status;
                bool ok = value == "absent" ? NvApi.DeleteSetting(session, profile, NvId(id))
                    : NvApi.SetDword(session, profile, NvId(id), uint.Parse(value, CultureInfo.InvariantCulture), out status);
                return ok && NvApi.SaveSession(session);
            }
            finally { NvApi.CloseSession(session); }
        }
    }
}
