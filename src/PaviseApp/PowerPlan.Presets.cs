using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    internal interface IManagedPowerPresetPlans : IManagedPowerPlans
    {
        bool CreatePreset(ManagedPowerPreset preset, Guid destination);
        Guid[] Subgroups(Guid scheme);
        bool CopyBaseSetting(Guid source, Guid target, Guid sub, Guid setting, bool ac);
    }

    internal interface IManagedPowerDeviceKind
    {
        LaboratoryEppDeviceKind DeviceKind { get; }
    }

    internal static partial class PowerPlan
    {
        private const string PresetCreationKey = "ManagedPresetCreationV1";
        private const string PresetRevision = "copy1";
        private static bool requestedAggressive;
        private static ManagedPowerHardwareFacts cachedManagedHardware;
        [ThreadStatic] private static string applyFailureReason;
        [ThreadStatic] private static bool applyFailurePermanent;
        internal static string LastApplyFailureReason { get { return applyFailureReason ?? ""; } }
        internal static bool LastApplyFailureIsPermanent { get { return applyFailurePermanent; } }
        internal static void ClearApplyFailureReason() { applyFailureReason = null; applyFailurePermanent = false; }

        // Only verified desktops use architecture presets; uncertain chassis data retains the legacy policy.
        // A battery may belong to a desktop UPS.
        internal static bool UsesLegacyManagedPolicy()
        {
            var device = ManagedPlans as IManagedPowerDeviceKind;
            return device == null || device.DeviceKind != LaboratoryEppDeviceKind.Desktop;
        }

        // Keep the first apply failure; cleanup errors must not replace its diagnostic.
        private static bool PowerApplyFailed(string stage, string detail)
        {
            if (string.IsNullOrEmpty(applyFailureReason))
                applyFailureReason = "stage=" + stage + " detail=" + PowerFailureDetail(detail);
            return false;
        }

        private static Guid ManagedPlanFailed(string stage, string detail)
        {
            PowerApplyFailed(stage, detail);
            return Guid.Empty;
        }

        private static string PowerFailureDetail(string detail)
        {
            detail = (detail ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (detail.Length > 1200) detail = detail.Substring(0, 1200);
            return "\"" + detail.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static ManagedPowerPresetSelection SelectManagedPreset(bool aggressive)
        {
            ManagedPowerHardwareFacts facts = ReadManagedHardwareFacts();
            // Retired source preferences cannot override device-policy routing.
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

        private static bool CreatePresetNative(ManagedPowerPreset preset, Guid destination)
        {
            if (HandheldBypass) return false;
            if (preset == null) return PresetCreationFailed(null, "Missing parameter set");
            try
            {
                Guid baseline;
                if (!TryReadPresetCreationIntent(destination, out baseline)) return PresetCreationFailed(preset, "Missing durable creation record");
                string failure;
                return PowerPresetWriter.Create(preset, destination, Ultimate, HighPerf, baseline, new NativePowerPresetWriter(), out failure)
                    || PresetCreationFailed(preset, failure, PowerPresetWriter.LastFailureNativeCode);
            }
            catch (Exception error) { return PresetCreationFailed(preset, error.GetType().Name + ": " + error.Message); }
        }

        private static bool PresetCreationFailed(ManagedPowerPreset preset, string detail, uint nativeCode = 0)
        {
            applyFailurePermanent |= PowerPresetWriter.PermanentNativeFailure(nativeCode);
            if (string.IsNullOrEmpty(applyFailureReason))
                applyFailureReason = "stage=preset-creation source=" + (preset == null ? "unknown" : preset.Id)
                    + " detail=" + PowerFailureDetail(detail);
            return false;
        }

        private static bool HasPresetCreationIntent(Guid destination)
        {
            Guid baseline;
            return TryReadPresetCreationIntent(destination, out baseline);
        }

        private static bool TryReadPresetCreationIntent(Guid destination, out Guid baseline)
        {
            baseline = Guid.Empty;
            if (destination == Guid.Empty || IsBuiltInPlan(destination)) return false;
            string raw;
            if (!Settings.TryLoadStr(PresetCreationKey, out raw) || string.IsNullOrEmpty(raw)) return false;
            string[] parts = raw.Split('|'); Guid candidate;
            return parts.Length == 3 && parts[0] == "1" && Guid.TryParse(parts[1], out candidate) && candidate == destination
                && Guid.TryParse(parts[2], out baseline) && baseline != Guid.Empty && baseline != destination;
        }

        private sealed class NativePowerPresetWriter : IPowerPresetWriterPlatform, IVerifiedPowerPresetBaseline, IManagedPowerSchemeEvidence
        {
            private Guid createdHere;
            public Guid? Current() { return PowerPlan.Current(); }
            public bool Exists(Guid scheme) { return SchemeUsable(scheme); }
            public ManagedPowerSchemeEvidence SchemeEvidence(Guid scheme) { return ReadManagedSchemeEvidenceNative(scheme); }
            public bool IsRegistered(ManagedPowerPresetValue value)
            { return PowerPresetWriter.IsRegistered(PowerPresetWriter.SettingPath(value)); }

            public bool VerifyBaseline(Guid baseline, Guid destination)
            {
                Guid recorded;
                ManagedPolicyRecord policy;
                Guid? active = Current();
                return TryReadPresetCreationIntent(destination, out recorded) && recorded == baseline
                    && ReadManagedPolicy(out policy) && active.HasValue && active != Guid.Empty && active != destination
                    && SchemeEvidence(baseline).State == ManagedPowerSchemeState.Present
                    && SchemeEvidence(destination).State != ManagedPowerSchemeState.Unavailable
                    && !ManagedPlans.IsOwned(baseline) && Current() == active;
            }

            public uint Duplicate(Guid source, Guid destination)
            {
                if (HandheldBypass) return 5;
                Guid baseline;
                if (!TryReadPresetCreationIntent(destination, out baseline)) return 87;
                ManagedPowerSchemeEvidence candidate = SchemeEvidence(destination);
                if (candidate.State == ManagedPowerSchemeState.Unavailable) return candidate.NativeCode;
                if (candidate.State != ManagedPowerSchemeState.ConfirmedMissing) return 87;
                if (!VerifyBaseline(baseline, destination)) return 5;
                if (source != Ultimate && source != HighPerf && source != baseline) return 87;
                if (source != Ultimate && source != HighPerf && Current() != baseline) return 5;
                IntPtr allocated = Marshal.AllocHGlobal(16), result = allocated;
                try
                {
                    Marshal.StructureToPtr(destination, allocated, false);
                    if (HandheldBypass) return 5;
                    uint code = PowerDuplicateScheme(IntPtr.Zero, ref source, ref result);
                    if (code != 0) return code;
                    if (result == IntPtr.Zero || (Guid)Marshal.PtrToStructure(result, typeof(Guid)) != destination) return 13;
                    createdHere = destination;
                    return 0;
                }
                finally
                {
                    if (result != IntPtr.Zero && result != allocated) LocalFree(result);
                    Marshal.FreeHGlobal(allocated);
                }
            }

            private bool CanWrite(Guid scheme)
            {
                if (HandheldBypass) return false;
                Guid? active = Current();
                return createdHere == scheme && HasPresetCreationIntent(scheme)
                    && active.HasValue && active != Guid.Empty && active != scheme
                    && SchemeEvidence(scheme).State == ManagedPowerSchemeState.Present;
            }

            public ManagedPowerPresetValue[] StoredSettings(Guid scheme)
            {
                if (!CanWrite(scheme)) return null;
                try
                {
                    var values = new List<ManagedPowerPresetValue>();
                    Guid[] groups = ReadPresetSubgroups(scheme);
                    if (groups == null) return null;
                    foreach (Guid sub in groups)
                    {
                        Guid[] settings = ReadPresetStoredSettings(scheme, sub);
                        if (settings == null) return null;
                        foreach (Guid setting in settings)
                            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(PresetSettingPath(scheme, sub, setting)))
                            {
                                if (key == null) return null;
                                values.Add(new ManagedPowerPresetValue(sub.ToString(), setting.ToString(),
                                    key.GetValue("ACSettingIndex") == null ? (uint?)null : 0u,
                                    key.GetValue("DCSettingIndex") == null ? (uint?)null : 0u, null, null, 4, 4));
                            }
                    }
                    return values.ToArray();
                }
                catch { return null; }
            }

            public uint ReadDefault(ManagedPowerPresetValue value, Guid personality, bool ac, out uint index)
            {
                Guid sub = value.Sub, setting = value.Setting;
                return ac ? ReadPresetAcDefault(IntPtr.Zero, ref personality, ref sub, ref setting, out index)
                    : ReadPresetDcDefault(IntPtr.Zero, ref personality, ref sub, ref setting, out index);
            }

            public uint Write(Guid scheme, ManagedPowerPresetValue value, bool ac, uint index)
            {
                if (!CanWrite(scheme)) return 5;
                Guid sub = value.Sub, setting = value.Setting;
                return ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, index)
                    : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, index);
            }

            public uint Read(Guid scheme, ManagedPowerPresetValue value, bool ac, out uint index)
            {
                Guid sub = value.Sub, setting = value.Setting;
                return ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out index)
                    : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out index);
            }
        }

        [DllImport("powrprof.dll", EntryPoint = "PowerReadACDefaultIndex")]
        private static extern uint ReadPresetAcDefault(IntPtr root, ref Guid personality, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll", EntryPoint = "PowerReadDCDefaultIndex")]
        private static extern uint ReadPresetDcDefault(IntPtr root, ref Guid personality, ref Guid sub, ref Guid setting, out uint value);

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

        // Preserve non-performance AC preferences and all readable DC values, including OEM settings absent from the source.
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
            if (HandheldBypass) return false;
            ManagedPolicyRecord record;
            var plans = ManagedPlans as IManagedPowerPresetPlans;
            if (plans == null || !ReadManagedPolicy(out record) || record == null || !record.CurrentTemplate
                || record.Plan != scheme || record.Base == scheme || plans.Current() == scheme) return false;
            ManagedPowerPresetSelection selection = SelectManagedPreset(aggressive);
            if (selection.Preset == null || record.Source != selection.Preset.Id || record.Fingerprint != PresetFingerprint(selection)) return false;
            try
            {
                // 在本机 AC/DC 适配前核对写入值；未注册项已在创建阶段跳过。
                var readEvidence = plans as IManagedPowerReadEvidence;
                foreach (ManagedPowerPresetValue value in selection.Preset.Entries)
                {
                    if (PreserveBaseAc(value.Sub, value.Setting, selection.PreserveParking)) continue;
                    if (value.Ac.HasValue && readEvidence != null)
                    {
                        ManagedPowerReadEvidence read = readEvidence.ReadEvidence(scheme, value.Sub, value.Setting, true);
                        if (read.State == ManagedPowerReadState.Unavailable)
                            return PresetPreparationFailed("AC value unavailable: " + value.Sub + "/" + value.Setting
                                + " code=" + read.NativeCode + " resolution=" + read.ResolutionCode, read.NativeCode, read.ResolutionCode);
                        if (read.State == ManagedPowerReadState.Value && read.Value != value.Ac.Value)
                            return PresetPreparationFailed("AC readback mismatch: " + value.Sub + "/" + value.Setting);
                        continue;
                    }
                    uint? actual = plans.Read(scheme, value.Sub, value.Setting, true);
                    if (value.Ac.HasValue && actual.HasValue && actual.Value != value.Ac.Value)
                        return PresetPreparationFailed("AC readback mismatch: " + value.Sub + "/" + value.Setting);
                }
                Guid[] baseGroups = plans.Subgroups(record.Base), targetGroups = plans.Subgroups(scheme);
                if (baseGroups == null || targetGroups == null) return PresetPreparationFailed("Subgroup enumeration failed");
                var groups = new HashSet<Guid>(baseGroups); groups.UnionWith(targetGroups); groups.Add(SubNone);
                if (!CopyPresetBaseValue(plans, record.Base, scheme, SubNone, Personality, false)) return false;
                int copied = 0;
                foreach (Guid sub in groups)
                {
                    Guid[] baseSettings = plans.ProcessorSettings(record.Base, sub), targetSettings = plans.ProcessorSettings(scheme, sub);
                    if (baseSettings == null || targetSettings == null) return PresetPreparationFailed("Setting enumeration failed: " + sub);
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
            catch (Exception error) { return PresetPreparationFailed(error.GetType().Name + ": " + error.Message); }
        }

        private static bool ManagedPresetIntact(Guid scheme, bool aggressive)
        {
            intactMismatch = "";
            ManagedPolicyRecord record;
            IManagedPowerPlans plans = ManagedPlans;
            if (!ReadManagedPolicy(out record) || record == null || !record.CurrentTemplate || record.Plan != scheme
                || !plans.Exists(scheme) || !plans.IsOwned(scheme)) return NoteIntactMismatch("policy-record-or-plan-ownership");
            bool legacy = UsesLegacyManagedPolicy();
            if ((record.Source == Legacy3110Source) != legacy) return NoteIntactMismatch("device-policy-route");
            if (legacy)
            {
                if (record.Fingerprint.Length != 0 && record.Fingerprint != Legacy3110Fingerprint(aggressive))
                    return NoteIntactMismatch("legacy-fingerprint");
                return Legacy3110ManagedIntact(scheme, aggressive);
            }
            ManagedPowerPresetSelection selection = SelectManagedPreset(aggressive);
            if (selection.Preset == null || record.Source != selection.Preset.Id || record.Fingerprint != PresetFingerprint(selection))
                return NoteIntactMismatch("preset-fingerprint");
            HashSet<Guid> laboratory = LaboratoryPower.OwnedSettings(scheme);
            if (laboratory == null) return NoteIntactMismatch("laboratory-ownership-unreadable");
            foreach (ManagedPowerPresetValue value in selection.Preset.Entries)
            {
                // Daily-use AC and all battery settings remain local preferences.
                // Laboratory mutations have their own audit / recovery owner.
                if (!value.Ac.HasValue || PreserveBaseAc(value.Sub, value.Setting, selection.PreserveParking)
                    || value.Sub == SubProcessor && laboratory.Contains(value.Setting)) continue;
                var evidence = plans as IManagedPowerReadEvidence;
                if (evidence != null)
                {
                    ManagedPowerReadEvidence actualRead = evidence.ReadEvidence(scheme, value.Sub, value.Setting, true);
                    if (actualRead.State == ManagedPowerReadState.Unavailable)
                        return NoteIntactMismatch("setting=" + value.Setting.ToString("D") + " ac=unreadable code=" + actualRead.NativeCode);
                    if (actualRead.State == ManagedPowerReadState.Value && actualRead.Value != value.Ac.Value)
                        return NoteIntactMismatch("setting=" + value.Setting.ToString("D") + " ac expected=" + value.Ac.Value + " actual=" + actualRead.Value);
                    if (actualRead.State == ManagedPowerReadState.ConfirmedUnregistered
                        && evidence.ReadEvidence(record.Base, value.Sub, value.Setting, true).State != ManagedPowerReadState.ConfirmedUnregistered)
                        return NoteIntactMismatch("setting=" + value.Setting.ToString("D") + " ac=unregistered-in-plan-only");
                    continue;
                }
                uint? actual = plans.Read(scheme, value.Sub, value.Setting, true);
                if (!actual.HasValue && plans.Read(record.Base, value.Sub, value.Setting, true).HasValue)
                    return NoteIntactMismatch("setting=" + value.Setting.ToString("D") + " ac=unreadable");
                if (actual.HasValue && actual.Value != value.Ac.Value)
                    return NoteIntactMismatch("setting=" + value.Setting.ToString("D") + " ac expected=" + value.Ac.Value + " actual=" + actual.Value);
            }
            return true;
        }

        private static bool PresetPreparationFailed(string detail, uint nativeCode = 0, uint resolutionCode = 0)
        {
            applyFailurePermanent |= PowerPresetWriter.PermanentNativeFailure(nativeCode) || PowerPresetWriter.PermanentNativeFailure(resolutionCode);
            if (string.IsNullOrEmpty(applyFailureReason))
                applyFailureReason = "stage=managed-parameters detail=" + PowerFailureDetail(detail);
            return false;
        }

        private static bool CopyPresetBaseValue(IManagedPowerPlans plans, Guid source, Guid targetPlan, Guid sub, Guid setting, bool ac)
        {
            if (HandheldBypass) return false;
            var imported = plans as IManagedPowerPresetPlans;
            return imported != null && imported.CopyBaseSetting(source, targetPlan, sub, setting, ac)
                || PresetPreparationFailed("Baseline adaptation failed: " + sub + "/" + setting + "/" + (ac ? "AC" : "DC"));
        }

        private static string PresetSettingPath(Guid scheme, Guid sub, Guid setting)
        {
            return @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + scheme.ToString("D")
                + "\\" + (sub == SubNone ? "" : sub.ToString("D") + "\\") + setting.ToString("D");
        }

        private static bool CanEditPreset(Guid scheme)
        {
            Guid? active = ManagedPlans.Current();
            return scheme != Guid.Empty && !IsBuiltInPlan(scheme) && active.HasValue && active != Guid.Empty
                && active != scheme && ManagedPlans.IsOwned(scheme);
        }

        private static bool CopyPresetBaseNative(Guid source, Guid destination, Guid sub, Guid setting, bool ac)
        {
            Guid? activeBefore = ManagedPlans.Current();
            if (!CanCopyPresetBase(source, destination, activeBefore))
                return PresetPreparationFailed("Baseline/candidate ownership or active-plan check failed");
            string name = ac ? "ACSettingIndex" : "DCSettingIndex";
            try
            {
                ManagedPowerReadEvidence baseline = ReadPresetPowerEvidence(source, sub, setting, ac);
                if (baseline.State == ManagedPowerReadState.Unavailable)
                    return PresetPreparationFailed("Local value unavailable: " + name + " " + sub + "/" + setting
                        + " code=" + baseline.NativeCode + " resolution=" + baseline.ResolutionCode, baseline.NativeCode, baseline.ResolutionCode);
                ManagedPowerReadEvidence target = ReadPresetPowerEvidence(destination, sub, setting, ac);
                if (baseline.State == ManagedPowerReadState.ConfirmedUnregistered)
                    return target.State == ManagedPowerReadState.ConfirmedUnregistered
                        && CanCopyPresetBase(source, destination, activeBefore)
                        || PresetPreparationFailed("Unregistered local setting could not be verified: " + name + " " + sub + "/" + setting);
                if (target.State == ManagedPowerReadState.Unavailable && target.NativeCode != 2 && target.NativeCode != 3 && target.NativeCode != 87)
                    return PresetPreparationFailed("Candidate value unavailable: " + name + " " + sub + "/" + setting
                        + " code=" + target.NativeCode + " resolution=" + target.ResolutionCode, target.NativeCode, target.ResolutionCode);
                uint stored;
                uint storedCode = ReadPresetStoredIndex(source, sub, setting, ac, out stored);
                if (storedCode != 2 && (storedCode != 0 || stored != baseline.Value))
                    return PresetPreparationFailed("Local stored index could not be verified: " + name + " " + sub + "/" + setting + " code=" + storedCode, storedCode);
                // Missing overrides may resolve differently by personality; copy a verified baseline value, not equal error codes.
                ManagedPowerReadEvidence verifyBase = ReadPresetPowerEvidence(source, sub, setting, ac);
                if (!CanCopyPresetBase(source, destination, activeBefore) || verifyBase.State != ManagedPowerReadState.Value
                    || verifyBase.Value != baseline.Value)
                    return PresetPreparationFailed("Baseline changed or candidate became active/ownership changed: " + name + " " + sub + "/" + setting, verifyBase.NativeCode, verifyBase.ResolutionCode);
                if (target.State == ManagedPowerReadState.Value && target.Value == baseline.Value) return true;
                uint code = ac ? PowerWriteACValueIndex(IntPtr.Zero, ref destination, ref sub, ref setting, baseline.Value)
                    : PowerWriteDCValueIndex(IntPtr.Zero, ref destination, ref sub, ref setting, baseline.Value);
                if (code != 0) return PresetPreparationFailed("Write " + name + " " + sub + "/" + setting + " code=" + code, code);
                uint actual;
                code = ReadPresetIndex(destination, sub, setting, ac, out actual);
                if (code != 0 || actual != baseline.Value)
                    return PresetPreparationFailed("Baseline readback mismatch: " + name + " " + sub + "/" + setting
                        + " code=" + code + " expected=" + baseline.Value + " actual=" + actual, code);
                return CanCopyPresetBase(source, destination, activeBefore)
                    || PresetPreparationFailed("Candidate became active or ownership changed after baseline readback");
            }
            catch (Exception error) { return PresetPreparationFailed(name + " " + sub + "/" + setting + ": " + error.GetType().Name + ": " + error.Message); }
        }

        private static bool CanCopyPresetBase(Guid source, Guid destination, Guid? expectedActive)
        {
            if (HandheldBypass) return false;
            ManagedPolicyRecord record; IManagedPowerPlans plans = ManagedPlans;
            return source != Guid.Empty && source != destination && expectedActive.HasValue && expectedActive != Guid.Empty
                && ReadManagedPolicy(out record) && record != null && record.CurrentTemplate
                && record.Plan == destination && record.Base == source && plans.Exists(source) && plans.Exists(destination)
                && !plans.IsOwned(source) && CanEditPreset(destination) && plans.Current() == expectedActive;
        }

        private static uint ReadPresetIndex(Guid scheme, Guid sub, Guid setting, bool ac, out uint value)
        {
            // *Value returns a payload; only *ValueIndex is an AC/DCSettingIndex.
            return ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value)
                : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value);
        }

        private static uint ReadPresetStoredIndex(Guid scheme, Guid sub, Guid setting, bool ac, out uint value)
        {
            value = 0; string name = ac ? "ACSettingIndex" : "DCSettingIndex";
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(PresetSettingPath(scheme, sub, setting)))
                {
                    if (key == null || Array.IndexOf(key.GetValueNames(), name) < 0) return 2;
                    object stored = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (key.GetValueKind(name) != RegistryValueKind.DWord || !(stored is int)) return 13;
                    value = unchecked((uint)(int)stored); return 0;
                }
            }
            catch (UnauthorizedAccessException) { return 5; }
            catch (System.Security.SecurityException) { return 5; }
            catch { return 31; }
        }

        private static ManagedPowerReadEvidence ReadPresetPowerEvidence(Guid scheme, Guid sub, Guid setting, bool ac)
        {
            uint value, code = ReadPresetIndex(scheme, sub, setting, ac, out value);
            // Access denial, invalid handles and unknown errors never establish absence.
            if (code != 0 && code != 2 && code != 3 && code != 87)
                return new ManagedPowerReadEvidence(ManagedPowerReadState.Unavailable, 0, code, code, false);
            if (!SchemeUsable(scheme)) return new ManagedPowerReadEvidence(ManagedPowerReadState.Unavailable, 0, code, 2, false);
            uint definition = ReadPresetDefinitionStatus(sub, setting);
            // Do not reintroduce unsupported settings from a historical per-plan index.
            if ((code == 0 || code == 2 || code == 3) && (definition == 2 || definition == 3))
                return new ManagedPowerReadEvidence(ManagedPowerReadState.ConfirmedUnregistered, 0, code, definition, false);
            if (definition != 0) return new ManagedPowerReadEvidence(ManagedPowerReadState.Unavailable, 0, code, definition, false);
            if (code == 0) return new ManagedPowerReadEvidence(ManagedPowerReadState.Value, value, 0, 0, false);
            uint storedCode = ReadPresetStoredIndex(scheme, sub, setting, ac, out value);
            // Resolve defaults only after proving an override absent; ERROR_INVALID_PARAMETER is not absence evidence.
            if (storedCode != 2) return new ManagedPowerReadEvidence(ManagedPowerReadState.Unavailable, 0, code, storedCode == 0 ? code : storedCode, false);
            uint resolution = ReadPresetEffectiveDefault(scheme, sub, setting, ac, out value);
            return new ManagedPowerReadEvidence(resolution == 0 ? ManagedPowerReadState.Value : ManagedPowerReadState.Unavailable,
                value, code, resolution, resolution == 0);
        }

        private static uint ReadPresetEffectiveDefault(Guid scheme, Guid sub, Guid setting, bool ac, out uint value)
        {
            value = 0; uint index;
            uint code = ReadPresetIndex(scheme, SubNone, Personality, ac, out index);
            if (code != 0) return code;
            Guid personality = index == 0 ? PowerSaver : index == 1 ? HighPerf : index == 2 ? Balanced : Guid.Empty;
            if (personality == Guid.Empty) return 13;
            code = ac ? ReadPresetAcDefault(IntPtr.Zero, ref personality, ref sub, ref setting, out value)
                : ReadPresetDcDefault(IntPtr.Zero, ref personality, ref sub, ref setting, out value);
            if (code != 0) return code;
            uint verify;
            code = ReadPresetIndex(scheme, SubNone, Personality, ac, out verify);
            if (code != 0) return code;
            return verify == index && SchemeUsable(scheme) ? 0u : 1237u;
        }

        private static uint ReadPresetDefinitionStatus(Guid sub, Guid setting)
        {
            string path = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\"
                + (sub == SubNone ? "" : sub.ToString("D") + "\\") + setting.ToString("D");
            IntPtr key;
            uint code = OpenPresetDefinition(new IntPtr(unchecked((int)0x80000002)), path, 0, 0x20019, out key);
            if (code == 0) ClosePresetDefinition(key);
            return code;
        }

        [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode)]
        private static extern uint OpenPresetDefinition(IntPtr root, string path, uint options, int access, out IntPtr key);
        [DllImport("advapi32.dll", EntryPoint = "RegCloseKey")]
        private static extern uint ClosePresetDefinition(IntPtr key);

        private static bool SamePresetRegistryValue(object expected, object actual)
        {
            Array a = expected as Array, b = actual as Array;
            if (a == null || b == null) return object.Equals(expected, actual);
            if (a.Length != b.Length || a.GetType() != b.GetType()) return false;
            for (int i = 0; i < a.Length; i++) if (!object.Equals(a.GetValue(i), b.GetValue(i))) return false;
            return true;
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
            ManagedPowerSchemeEvidence candidateState = ReadManagedSchemeEvidence(plans, candidate);
            if (!ManagedSchemeEvidenceAvailable(candidateState, candidate)) return false;
            if (record == null || record.Plan != candidate)
            {
                Guid? current = plans.Current();
                if (!current.HasValue || current == Guid.Empty) return false;
                if (current == candidate)
                {
                    ManagedPowerSchemeEvidence baselineState = ReadManagedSchemeEvidence(plans, baseline);
                    if (!ManagedSchemeEvidenceAvailable(baselineState, baseline)
                        || baselineState.State != ManagedPowerSchemeState.Present
                        || !plans.Activate(baseline) || plans.Current() != baseline) return false;
                }
                if (!DeleteInactiveManaged(plans, candidate, true)) return false;
            }
            return Settings.SaveStrDurable(PresetCreationKey, "") && Settings.LoadStr(PresetCreationKey, "!") == "";
        }

        private static void CleanupPreviousPresetPlan()
        {
            ManagedPolicyRecord record;
            if (!ReadManagedPolicy(out record) || record == null || !record.CurrentTemplate || record.Previous == Guid.Empty) return;
            IManagedPowerPlans plans = ManagedPlans;
            if (plans.Current() != record.Plan) return;
            if (!DeleteInactiveManaged(plans, record.Previous)) return;
            record.Previous = Guid.Empty;
            string encoded = record.Encode();
            // A failed cleanup save retains a harmless reference to an absent plan for the next retry.
            Settings.SaveStrDurable(ManagedPolicyKey, encoded);
        }
    }
}
