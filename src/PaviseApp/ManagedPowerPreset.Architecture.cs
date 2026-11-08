using System;
using System.Text.RegularExpressions;

namespace PaviseApp
{
    internal sealed class ManagedPowerHardwareFacts
    {
        internal string CpuName = "", Vendor = "";
        internal int Family, Model;
        internal bool Hybrid, AsymmetricCache;
        internal int ProcessorGroupCount = 1, PackageCount = 1, EfficiencyClassCount = 1;
        internal bool Arm, Mobile, Workstation, VirtualMachine;

        internal string Identity
        {
            get
            {
                return (Vendor ?? "") + ":" + Family + ":" + Model + ":" + (CpuName ?? "")
                    + ":groups=" + ProcessorGroupCount + ":packages=" + PackageCount
                    + ":classes=" + EfficiencyClassCount + ":hybrid=" + Hybrid
                    + ":asym=" + AsymmetricCache + ":arm=" + Arm + ":mobile=" + Mobile
                    + ":workstation=" + Workstation + ":vm=" + VirtualMachine;
            }
        }
    }

    internal sealed class ManagedPowerPresetSelection
    {
        internal readonly ManagedPowerPreset Preset;
        internal readonly bool PreserveParking, PreferOem, IsExplicit;
        internal readonly string Reason;

        internal ManagedPowerPresetSelection(ManagedPowerPreset preset, bool preserveParking,
            bool preferOem, bool isExplicit, string reason)
        {
            Preset = preset; PreserveParking = preserveParking; PreferOem = preferOem;
            IsExplicit = isExplicit; Reason = reason;
        }
    }

    internal sealed partial class ManagedPowerPreset
    {
        internal static ManagedPowerPresetSelection Resolve(ManagedPowerHardwareFacts facts,
            bool aggressive, string explicitSourceChoice)
        {
            bool preserveParking = facts != null && facts.AsymmetricCache;
            string name = facts == null ? "" : (facts.CpuName ?? "").ToUpperInvariant();
            string vendor = facts == null ? "" : (facts.Vendor ?? "").ToUpperInvariant();
            bool arm = facts != null && (facts.Arm || vendor.IndexOf("ARM", StringComparison.Ordinal) >= 0
                || vendor.IndexOf("QUALCOMM", StringComparison.Ordinal) >= 0
                || name.IndexOf("SNAPDRAGON", StringComparison.Ordinal) >= 0);
            // 工作站及多处理器平台统一使用通用计划，旧来源偏好不能绕过此规则。
            if (facts != null && !arm && !facts.VirtualMachine)
            {
                if (facts.ProcessorGroupCount > 1 || facts.PackageCount > 1)
                    return Chosen("universal", preserveParking, "multiple-processor-groups-or-packages; universal source");
                if (facts.Workstation || Regex.IsMatch(name, @"\b(?:XEON|EPYC|THREADRIPPER|OPTERON)\b"))
                    return Chosen("universal", preserveParking, "workstation-or-server; universal source");
            }
            if (!string.IsNullOrWhiteSpace(explicitSourceChoice)
                && !string.Equals(explicitSourceChoice, "auto", StringComparison.OrdinalIgnoreCase))
            {
                ManagedPowerPreset explicitSource = Find(explicitSourceChoice);
                if (explicitSource == null)
                    return new ManagedPowerPresetSelection(null, preserveParking, true, true,
                        "explicit-source-unavailable; keep OEM/base");
                // 显式来源仅覆盖自动选择，不代表已验证硬件兼容性。
                return new ManagedPowerPresetSelection(explicitSource, preserveParking, false, true,
                    "explicit-plan; preserve baseline preferences and topology adaptation");
            }

            if (facts == null) return Oem(false, "hardware-facts-unavailable");
            if (arm)
                return Oem(preserveParking, "ARM; keep OEM/base");
            if (facts.VirtualMachine) return Oem(preserveParking, "virtual-machine; keep OEM/base");

            bool amd = vendor == "AUTHENTICAMD" || vendor == "AMD";
            bool intel = vendor == "GENUINEINTEL" || vendor == "INTEL";
            if (vendor.Length == 0)
            {
                amd = name.IndexOf("AMD", StringComparison.Ordinal) >= 0;
                intel = name.IndexOf("INTEL", StringComparison.Ordinal) >= 0;
            }
            if (amd == intel) return Oem(preserveParking, "unknown-cpu-vendor; keep OEM/base");

            bool mobile = facts.Mobile || IsMobileName(name, intel);
            if (mobile && !aggressive)
                return Chosen("universal", preserveParking, "mobile-smart; universal source");
            if (mobile && (Regex.IsMatch(name, @"\b\d{3,5}(?:HK|HX(?:3D)?)\b")
                || (name.IndexOf("ULTRA", StringComparison.Ordinal) >= 0 && facts.EfficiencyClassCount > 2)))
                return Chosen("universal", preserveParking,
                    "mobile-unlocked-or-Ultra-multiple-efficiency-classes; universal source");

            if (amd)
            {
                if (mobile && (facts.Hybrid || facts.EfficiencyClassCount > 1))
                    return Chosen("universal", preserveParking, "AMD-mobile-multiple-core-classes; universal source");
                if (name.IndexOf("X3D", StringComparison.Ordinal) >= 0 || facts.AsymmetricCache)
                    return Chosen("amd-x3d", preserveParking,
                        preserveParking ? "AMD-asymmetric-cache-X3D; preserve baseline parking and topology policies"
                            : "AMD-single-cache-class-X3D; X3D source");
                return Chosen(aggressive ? "amd-lowlat" : "amd-generic", preserveParking,
                    aggressive ? "AMD-competitive; low-latency source" : "AMD-smart; generic source");
            }

            if (!mobile && Regex.IsMatch(name, @"\b\d{3,5}(?:KF|KS|K)\b"))
                return Chosen("intel-k-series", preserveParking, "Intel-desktop-K-KF-KS; K-series plan");
            if (Regex.IsMatch(name, @"\b\d{3,5}(?:HK|HX|XE|X|KF|KS|K)\b"))
                return mobile ? Chosen("universal", preserveParking, "Intel-mobile-unlocked; universal source")
                    : Oem(preserveParking, "Intel-HEDT-or-unmatched-unlocked-model; keep OEM/base");

            bool consumerIntel = name.IndexOf("CORE", StringComparison.Ordinal) >= 0
                || name.IndexOf("PENTIUM", StringComparison.Ordinal) >= 0
                || name.IndexOf("CELERON", StringComparison.Ordinal) >= 0
                || Regex.IsMatch(name, @"\bINTEL\s+(?:\(R\)\s*)?PROCESSOR\b");
            if (consumerIntel && Regex.IsMatch(name, @"\b(?:\d{3,5}(?:F|TE|T|S|H|U|P|V|Y|G[147])?|N\d{2,3})\b"))
                return Chosen("intel-non-k", preserveParking, "Intel-Non-K; standard processor plan");
            return Oem(preserveParking, "Intel-model-unclassified; keep OEM/base");
        }

        private static bool IsMobileName(string name, bool intel)
        {
            if (intel)
                return Regex.IsMatch(name, @"\b\d{3,5}(?:HK|HX|H|U|P|V|Y|G[147])\b");
            return Regex.IsMatch(name, @"\b\d{3,5}(?:HS|HX(?:3D)?|H|U|C)\b")
                || name.IndexOf("RYZEN AI", StringComparison.Ordinal) >= 0
                || name.IndexOf("RYZEN Z", StringComparison.Ordinal) >= 0;
        }

        private static ManagedPowerPresetSelection Chosen(string id, bool preserveParking, string reason)
        {
            return new ManagedPowerPresetSelection(Find(id), preserveParking, false, false, reason);
        }

        private static ManagedPowerPresetSelection Oem(bool preserveParking, string reason)
        {
            return new ManagedPowerPresetSelection(null, preserveParking, true, false, reason);
        }

    }
}
