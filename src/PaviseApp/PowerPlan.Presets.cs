using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    internal interface IManagedPowerPresetPlans : IManagedPowerPlans
    {
        bool ImportPreset(ManagedPowerPreset preset, Guid destination);
        Guid[] Subgroups(Guid scheme);
        bool RemoveOverride(Guid scheme, Guid sub, Guid setting, bool ac);
        bool CopyBaseSetting(Guid source, Guid target, Guid sub, Guid setting, bool ac);
    }

    internal static partial class PowerPlan
    {
        private const string PresetCreationKey = "ManagedPresetCreationV1";
        private const string PresetRevision = "full2";
        private static bool requestedAggressive;
        private static ManagedPowerHardwareFacts cachedManagedHardware;

        private static ManagedPowerPresetSelection SelectManagedPreset(bool aggressive)
        {
            ManagedPowerHardwareFacts facts = ReadManagedHardwareFacts();
            // Source selection is internal. Retired PowerPlanSourceV1 values
            // cannot override architecture or session routing, even if cleanup fails.
            return ManagedPowerPreset.Resolve(facts, aggressive, "auto");
        }

        private static string PresetFingerprint(ManagedPowerPresetSelection selection)
        { return PresetRevision + ":" + selection.Preset.Hash + ":" + (selection.PreserveParking ? "cache" : "normal"); }

        private static ManagedPowerHardwareFacts ReadManagedHardwareFacts()
        {
            if (cachedManagedHardware != null) return cachedManagedHardware;
            var facts = new ManagedPowerHardwareFacts();
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (key != null)
                    {
                        facts.CpuName = key.GetValue("ProcessorNameString", "") as string ?? "";
                        facts.Vendor = key.GetValue("VendorIdentifier", "") as string ?? "";
                        string identity = key.GetValue("Identifier", "") as string ?? "";
                        Match match = Regex.Match(identity, @"Family\s+(\d+)\s+Model\s+(\d+)", RegexOptions.IgnoreCase);
                        if (match.Success)
                        { int.TryParse(match.Groups[1].Value, out facts.Family); int.TryParse(match.Groups[2].Value, out facts.Model); }
                    }
                }
                facts.Hybrid = CpuTopology.Hybrid;
                facts.AsymmetricCache = CpuTopology.AsymCache;
                facts.ProcessorGroupCount = CpuTopology.MultiGroup ? 2 : 1;
                facts.Mobile = LaboratoryEppEligibility.VerifiedLaptop;
                ReadManagedTopology(facts);
                using (var query = new ManagementObjectSearcher("SELECT Manufacturer,Model,NumberOfProcessors FROM Win32_ComputerSystem"))
                using (ManagementObjectCollection results = query.Get())
                foreach (ManagementObject computer in results)
                {
                    using (computer)
                    {
                        string machine = Convert.ToString(computer["Manufacturer"], CultureInfo.InvariantCulture) + " "
                            + Convert.ToString(computer["Model"], CultureInfo.InvariantCulture);
                        facts.VirtualMachine = Regex.IsMatch(machine, @"VMware|VirtualBox|Virtual Machine|QEMU|KVM|Parallels|Xen", RegexOptions.IgnoreCase);
                        int packages; if (int.TryParse(Convert.ToString(computer["NumberOfProcessors"], CultureInfo.InvariantCulture), out packages) && packages > 0)
                            facts.PackageCount = packages;
                    }
                }
            }
            catch { /* Incomplete facts must not be replaced with a guessed model. */ }
            facts.Workstation = Regex.IsMatch(facts.CpuName ?? "", @"\b(Xeon|EPYC|Threadripper|Opteron)\b", RegexOptions.IgnoreCase);
            cachedManagedHardware = facts;
            return facts;
        }

        private static void ReadManagedTopology(ManagedPowerHardwareFacts facts)
        {
            IntPtr native = Marshal.AllocHGlobal(64);
            try { GetNativeSystemInfoForPreset(native); ushort arch = unchecked((ushort)Marshal.ReadInt16(native)); facts.Arm = arch == 5 || arch == 12; }
            finally { Marshal.FreeHGlobal(native); }
            uint size = 0;
            GetLogicalProcessorInformationForPreset(0xffff, IntPtr.Zero, ref size);
            if (size == 0 || size > 1024 * 1024) return;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetLogicalProcessorInformationForPreset(0xffff, buffer, ref size)) return;
                var classes = new HashSet<byte>(); int packages = 0;
                for (int offset = 0; offset + 8 <= size; )
                {
                    IntPtr item = IntPtr.Add(buffer, offset);
                    int relation = Marshal.ReadInt32(item), bytes = Marshal.ReadInt32(item, 4);
                    if (bytes < 8 || bytes > size - offset) return;
                    if (relation == 0 && bytes >= 10) classes.Add(Marshal.ReadByte(item, 9));
                    if (relation == 3) packages++;
                    offset += bytes;
                }
                if (classes.Count > 0) { facts.EfficiencyClassCount = classes.Count; facts.Hybrid = classes.Count > 1; }
                if (packages > 0) facts.PackageCount = packages;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [DllImport("kernel32.dll", EntryPoint = "GetNativeSystemInfo")]
        private static extern void GetNativeSystemInfoForPreset(IntPtr info);
        [DllImport("kernel32.dll", EntryPoint = "GetLogicalProcessorInformationEx", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationForPreset(int relation, IntPtr buffer, ref uint length);
        [DllImport("powrprof.dll", EntryPoint = "PowerEnumerate")]
        private static extern uint EnumeratePresetSubgroup(IntPtr root, ref Guid scheme, IntPtr sub, uint access, uint index, byte[] buffer, ref uint size);

        private static bool ImportPresetNative(ManagedPowerPreset preset, Guid destination)
        {
            if (preset == null || destination == Guid.Empty || IsBuiltInPlan(destination) || ManagedPlans.Exists(destination)) return false;
            string file = Path.Combine(Path.GetTempPath(), "Pavise-PowerPlan-" + destination.ToString("N") + ".pow");
            bool createdHere = false;
            try
            {
                byte[] bytes = preset.ReadBytes();
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { createdHere = true; output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe"),
                    "/import \"" + file + "\" " + destination.ToString("D"));
                start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
                start.RedirectStandardOutput = true; start.RedirectStandardError = true;
                using (Process process = Process.Start(start))
                {
                    if (process == null) return false;
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(15000)) { try { process.Kill(); process.WaitForExit(); } catch { } return false; }
                    process.WaitForExit();
                    if (process.ExitCode != 0) { Logger.Warn("POWER preset import failed: " + preset.Id + " code=" + process.ExitCode); return false; }
                    return ManagedPlans.Exists(destination);
                }
            }
            catch (Exception error) { Logger.Warn("POWER preset import failed: " + preset.Id + " " + error.GetType().Name); return false; }
            finally { if (createdHere) try { File.Delete(file); } catch { } }
        }

        private static Guid[] ReadPresetSubgroups(Guid scheme)
        {
            var result = new List<Guid> { SubNone };
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + scheme.ToString("D")))
                {
                    if (key == null) return null;
                    foreach (string name in key.GetSubKeyNames())
                    {
                        Guid sub;
                        if (!Guid.TryParse(name, out sub)) continue;
                        using (RegistryKey child = key.OpenSubKey(name))
                            if (child != null && child.SubKeyCount > 0 && !result.Contains(sub)) result.Add(sub);
                    }
                }
            }
            catch { return null; }
            for (uint index = 0; index < 512; index++)
            {
                uint size = 16; var bytes = new byte[16];
                uint code = EnumeratePresetSubgroup(IntPtr.Zero, ref scheme, IntPtr.Zero, 17, index, bytes, ref size);
                if (code == 259) return result.ToArray();
                if (code != 0 || size != 16) return null;
                Guid sub = new Guid(bytes); if (!result.Contains(sub)) result.Add(sub);
            }
            return null;
        }

        private static Guid[] ReadPresetStoredSettings(Guid scheme, Guid sub)
        {
            var result = new List<Guid>();
            string path = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + scheme.ToString("D")
                + (sub == SubNone ? "" : "\\" + sub.ToString("D"));
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (key == null) return result.ToArray();
                    foreach (string name in key.GetSubKeyNames())
                    {
                        Guid setting; if (!Guid.TryParse(name, out setting)) continue;
                        using (RegistryKey child = key.OpenSubKey(name))
                        {
                            if (child == null) continue;
                            string[] names = child.GetValueNames();
                            if (Array.IndexOf(names, "ACSettingIndex") >= 0 || Array.IndexOf(names, "DCSettingIndex") >= 0) result.Add(setting);
                        }
                    }
                }
                return result.ToArray();
            }
            catch { return null; }
        }

        // Entire non-performance AC subgroups retain the user's daily preferences.
        // DC retains every readable base setting, including OEM settings absent from the source file.
        private static bool PreserveBaseAc(Guid sub, Guid setting, bool preserveParking)
        {
            if (sub == SubProcessor) return preserveParking && IsPresetParkingOrScheduling(setting);
            if (preserveParking && sub == new Guid("48672f38-7a9a-4bb2-8bf8-3d85be19de4e")) return true;
            if (sub == SubNone) return false;
            return sub != SubDisk && sub != SubUsb && sub != SubPcie && sub != SubWireless
                && sub != SubIntelGfx && sub != SubSwitchGfx && sub != new Guid("48672f38-7a9a-4bb2-8bf8-3d85be19de4e");
        }

        private static bool IsPresetParkingOrScheduling(Guid setting)
        {
            // Includes all parking gates/times, latency-unpark, class transitions and heterogeneous scheduling.
            string id = setting.ToString("D");
            return Array.IndexOf(PresetParkingSettings, id) >= 0;
        }

        private static readonly string[] PresetParkingSettings = {
            "0cc5b647-c1df-4637-891a-dec35c318583", "0cc5b647-c1df-4637-891a-dec35c318584",
            "ea062031-0e34-4ff1-9b6d-eb1059334028", "ea062031-0e34-4ff1-9b6d-eb1059334029",
            "2ddd5a84-5a71-437e-912a-db0b8c788732", "dfd10d17-d5eb-45dd-877a-9a34ddd15c82",
            "c7be0679-2817-4d69-9d02-519a537ed0c6", "71021b41-c749-4d21-be74-a00f335d582b",
            "2430ab6f-a520-44a2-9601-f7f23b5134b1", "4bdaf4e9-d103-46d7-a5f0-6280121616ef",
            "943c8cb6-6f93-4227-ad87-e9a3feec08d1", "f735a673-2066-4f80-a0c5-ddee0cf1bf5d",
            "97cfac41-2217-47eb-992d-618b1977c907", "e0007330-f589-42ed-a401-5ddb10e785d3",
            "447235c7-6a8d-4cc0-8e24-9eaf70b96e2b", "447235c7-6a8d-4cc0-8e24-9eaf70b96e2c",
            "616cdaa5-695e-4545-97ad-97dc2d1bdd88", "616cdaa5-695e-4545-97ad-97dc2d1bdd89",
            "1facfc65-a930-4bc5-9f38-504ec097bbc0", "b000397d-9b0b-483d-98c9-692a6060cfbf",
            "f8861c27-95e7-475c-865b-13c0cb3f9d6b", "fddc842b-8364-4edc-94cf-c17f60de1c80",
            "7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5", "93b8b6dc-0698-4d1c-9ee4-0644e900c85d",
            "bae08b81-2d5e-4688-ad6a-13243356654b", "df142941-20f3-4edf-9a4a-9c83d3d717d1",
            "68dd2f27-a4ce-4e11-8487-3794e4135dfa"
        };

        private static bool TunePresetTarget(Guid scheme, bool aggressive)
        {
            ManagedPolicyRecord record;
            var plans = ManagedPlans as IManagedPowerPresetPlans;
            if (plans == null || !ReadManagedPolicy(out record) || record == null || !record.CurrentTemplate
                || record.Plan != scheme || record.Base == scheme || plans.Current() == scheme) return false;
            ManagedPowerPresetSelection selection = SelectManagedPreset(aggressive);
            if (selection.Preset == null || record.Source != selection.Preset.Id || record.Fingerprint != PresetFingerprint(selection)) return false;
            try
            {
                // Validate the imported source before deliberate AC/DC adaptation. Unsupported items are skipped.
                foreach (ManagedPowerPresetValue value in selection.Preset.Entries)
                {
                    if (PreserveBaseAc(value.Sub, value.Setting, selection.PreserveParking)) continue;
                    uint? actual = plans.Read(scheme, value.Sub, value.Setting, true);
                    if (value.Ac.HasValue && actual.HasValue && actual.Value != value.Ac.Value) return false;
                }
                Guid[] baseGroups = plans.Subgroups(record.Base), targetGroups = plans.Subgroups(scheme);
                if (baseGroups == null || targetGroups == null) return false;
                var groups = new HashSet<Guid>(baseGroups); groups.UnionWith(targetGroups); groups.Add(SubNone);
                if (!CopyPresetBaseValue(plans, record.Base, scheme, SubNone, Personality, false)) return false;
                int copied = 0;
                foreach (Guid sub in groups)
                {
                    Guid[] baseSettings = plans.ProcessorSettings(record.Base, sub), targetSettings = plans.ProcessorSettings(scheme, sub);
                    if (baseSettings == null || targetSettings == null) return false;
                    var settings = new HashSet<Guid>(baseSettings); settings.UnionWith(targetSettings);
                    foreach (Guid setting in settings)
                    {
                        bool ac = PreserveBaseAc(sub, setting, selection.PreserveParking);
                        if (ac && !CopyPresetBaseValue(plans, record.Base, scheme, sub, setting, true)) return false;
                        if (!CopyPresetBaseValue(plans, record.Base, scheme, sub, setting, false)) return false;
                        if (ac) copied++;
                    }
                }
                // Retired idle receipts are recovered before this point. The source's idle policy stays intact.
                Logger.Info("POWER source=" + selection.Preset.Id + " sha256=" + selection.Preset.Hash
                    + " adaptation=" + (selection.PreserveParking ? "local-DC/daily/cache-topology" : "local-DC/daily")
                    + " preservedAC=" + copied + " reason=" + selection.Reason);
                return true;
            }
            catch (Exception error) { Logger.Warn("POWER preset preparation failed: " + error.GetType().Name); return false; }
        }

        private static bool CopyPresetBaseValue(IManagedPowerPlans plans, Guid source, Guid targetPlan, Guid sub, Guid setting, bool ac)
        {
            var imported = plans as IManagedPowerPresetPlans;
            return imported != null && imported.CopyBaseSetting(source, targetPlan, sub, setting, ac);
        }

        [DllImport("powrprof.dll", EntryPoint = "PowerReadACValue")]
        private static extern uint ReadPresetAcValue(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint type, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll", EntryPoint = "PowerReadDCValue")]
        private static extern uint ReadPresetDcValue(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint type, byte[] buffer, ref uint size);

        private static string PresetSettingPath(Guid scheme, Guid sub, Guid setting)
        {
            return @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + scheme.ToString("D")
                + "\\" + (sub == SubNone ? "" : sub.ToString("D") + "\\") + setting.ToString("D");
        }

        private static bool CanEditPreset(Guid scheme)
        { return scheme != Guid.Empty && !IsBuiltInPlan(scheme) && ManagedPlans.Current() != scheme && ManagedPlans.IsOwned(scheme); }

        private static bool CopyPresetBaseNative(Guid source, Guid destination, Guid sub, Guid setting, bool ac)
        {
            if (source == destination || !CanEditPreset(destination)) return false;
            string name = ac ? "ACSettingIndex" : "DCSettingIndex";
            try
            {
                object value = null; RegistryValueKind kind = RegistryValueKind.Unknown;
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(PresetSettingPath(source, sub, setting)))
                {
                    if (key != null && Array.IndexOf(key.GetValueNames(), name) >= 0)
                    { value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); kind = key.GetValueKind(name); }
                }
                if (value == null)
                {
                    // Read the base's effective default as raw data too; a missing DWORD is not a zero.
                    uint type, size = 0;
                    uint code = ac ? ReadPresetAcValue(IntPtr.Zero, ref source, ref sub, ref setting, out type, null, ref size)
                        : ReadPresetDcValue(IntPtr.Zero, ref source, ref sub, ref setting, out type, null, ref size);
                    if (code == 2 || code == 3 || code == 87) return RemovePresetOverrideNative(destination, sub, setting, ac);
                    if (code != 0 && code != 234 || size > 1024 * 1024) return false;
                    var bytes = new byte[size];
                    code = ac ? ReadPresetAcValue(IntPtr.Zero, ref source, ref sub, ref setting, out type, bytes, ref size)
                        : ReadPresetDcValue(IntPtr.Zero, ref source, ref sub, ref setting, out type, bytes, ref size);
                    if (code != 0) return false;
                    kind = (RegistryValueKind)type;
                    switch (kind)
                    {
                        case RegistryValueKind.DWord: if (size != 4) return false; value = BitConverter.ToInt32(bytes, 0); break;
                        case RegistryValueKind.QWord: if (size != 8) return false; value = BitConverter.ToInt64(bytes, 0); break;
                        case RegistryValueKind.Binary: case RegistryValueKind.None: value = bytes; break;
                        case RegistryValueKind.String: case RegistryValueKind.ExpandString: value = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0'); break;
                        case RegistryValueKind.MultiString: value = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0').Split('\0'); break;
                        default: return false;
                    }
                }
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(PresetSettingPath(destination, sub, setting)))
                {
                    if (key == null) return false;
                    key.SetValue(name, value, kind);
                    object read = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return key.GetValueKind(name) == kind && SamePresetRegistryValue(value, read);
                }
            }
            catch { return false; }
        }

        private static bool SamePresetRegistryValue(object expected, object actual)
        {
            Array a = expected as Array, b = actual as Array;
            if (a == null || b == null) return object.Equals(expected, actual);
            if (a.Length != b.Length || a.GetType() != b.GetType()) return false;
            for (int i = 0; i < a.Length; i++) if (!object.Equals(a.GetValue(i), b.GetValue(i))) return false;
            return true;
        }

        private static bool RemovePresetOverrideNative(Guid scheme, Guid sub, Guid setting, bool ac)
        {
            // Only an inactive, durably owned imported plan can have an index removed.
            // This preserves an absent local/OEM value without inventing zero or deleting a setting definition.
            if (!CanEditPreset(scheme)) return false;
            string path = PresetSettingPath(scheme, sub, setting);
            string value = ac ? "ACSettingIndex" : "DCSettingIndex";
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path, true))
                {
                    if (key == null) return true;
                    key.DeleteValue(value, false);
                    return key.GetValue(value, null) == null;
                }
            }
            catch { return false; }
        }

        private static bool RecoverPresetCreation(IManagedPowerPlans plans)
        {
            string raw; if (!Settings.TryLoadStr(PresetCreationKey, out raw) || raw == null) return false;
            if (raw.Length == 0) return true;
            string[] parts = raw.Split('|'); Guid candidate, baseline;
            if (parts.Length != 3 || parts[0] != "1" || !Guid.TryParse(parts[1], out candidate)
                || !Guid.TryParse(parts[2], out baseline) || candidate == Guid.Empty || baseline == Guid.Empty
                || candidate == baseline || IsBuiltInPlan(candidate)) return false;
            ManagedPolicyRecord record;
            if (!ReadManagedPolicy(out record)) return false;
            if (record == null || record.Plan != candidate)
            {
                if (plans.Current() == candidate && (!plans.Exists(baseline) || !plans.Activate(baseline) || plans.Current() != baseline)) return false;
                if (plans.Exists(candidate) && !DeleteInactiveManaged(plans, candidate, true)) return false;
            }
            return Settings.SaveStrDurable(PresetCreationKey, "") && Settings.LoadStr(PresetCreationKey, "!") == "";
        }

        private static void CleanupPreviousPresetPlan()
        {
            ManagedPolicyRecord record;
            if (!ReadManagedPolicy(out record) || record == null || !record.CurrentTemplate || record.Previous == Guid.Empty) return;
            IManagedPowerPlans plans = ManagedPlans;
            if (plans.Current() != record.Plan) return;
            if (plans.Exists(record.Previous) && !DeleteInactiveManaged(plans, record.Previous)) return;
            record.Previous = Guid.Empty;
            string encoded = record.Encode();
            // A failed cleanup save retains a harmless reference to an absent plan for the next retry.
            Settings.SaveStrDurable(ManagedPolicyKey, encoded);
        }
    }
}
