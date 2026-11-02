using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace PaviseApp
{
    // Independent source identities and routing expectations; no native hardware or power access.
    internal static class ManagedPowerPresetChecks
    {
        private static int checks;
        private static readonly Guid Cpu = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static void Check(bool ok, string message)
        { checks++; if (!ok) throw new InvalidOperationException(message); }
        private static ManagedPowerHardwareFacts Facts(string name, string vendor)
        { return new ManagedPowerHardwareFacts { CpuName = name, Vendor = vendor }; }
        private static void Route(ManagedPowerHardwareFacts facts, bool aggressive, string expected)
        {
            ManagedPowerPresetSelection value = ManagedPowerPreset.Resolve(facts, aggressive, "auto");
            Check((value.Preset == null ? null : value.Preset.Id) == expected,
                "Unexpected automatic route for " + (facts == null ? "missing facts" : facts.CpuName) + "/" + aggressive);
            Check(value.PreferOem == (expected == null) && !value.IsExplicit && !string.IsNullOrEmpty(value.Reason),
                "Automatic route lost fallback authority or its explanation");
        }
        private static void Routing()
        {
            Route(null, false, null);
            Route(Facts("unrecognized processor", "other"), true, null);
            Route(Facts("Intel(R) Core(TM) i7-9750H CPU", "GenuineIntel"), false, "universal");
            Route(Facts("Intel(R) Core(TM) i7-9750H CPU", "GenuineIntel"), true, "powerx-v3");
            Route(Facts("Intel(R) Core(TM) i7-9700KF CPU", "GenuineIntel"), true, "intel-kf");
            foreach (string suffix in new[] { "K", "KF", "KS" })
            {
                var intel = Facts("Intel(R) Core(TM) i9-13900" + suffix, "GenuineIntel"); intel.Hybrid = true; intel.EfficiencyClassCount = 2;
                Route(intel, false, "intel-kf"); Route(intel, true, "intel-kf");
            }
            Route(Facts("Intel(R) Core(TM) i5-12400F", "GenuineIntel"), true, "powerx-v3");
            Route(Facts("Intel(R) Core(TM) i9-14900HX", "GenuineIntel"), true, "universal");
            var ultra = Facts("Intel(R) Core(TM) Ultra 7 155H", "GenuineIntel"); ultra.Hybrid = true; ultra.EfficiencyClassCount = 3;
            Route(ultra, false, "universal"); Route(ultra, true, "universal");
            Route(Facts("Intel(R) Core(TM) i9-10980XE", "GenuineIntel"), true, null);
            foreach (string name in new[] { "AMD Ryzen 5 5600X", "AMD Ryzen 9 7950X" })
            { Route(Facts(name, "AuthenticAMD"), false, "amd-generic"); Route(Facts(name, "AuthenticAMD"), true, "amd-lowlat"); }
            var single = Facts("AMD Ryzen 7 9800X3D", "AuthenticAMD");
            Route(single, false, "amd-x3d"); Route(single, true, "amd-x3d");
            Check(!ManagedPowerPreset.Resolve(single, true, "auto").PreserveParking,
                "Single-cache-domain X3D was mistaken for asymmetric dual CCD");
            var dual = Facts("AMD Ryzen 9 7950X3D", "AuthenticAMD"); dual.AsymmetricCache = true;
            Route(dual, true, "amd-x3d");
            Check(ManagedPowerPreset.Resolve(dual, true, "auto").PreserveParking,
                "Asymmetric X3D lost its baseline parking requirement");
            Route(Facts("AMD Ryzen 9 7945HX3D", "AuthenticAMD"), true, "universal");
            var mobile = Facts("AMD Ryzen AI 9 HX 370", "AuthenticAMD"); mobile.Mobile = true; mobile.Hybrid = true; mobile.EfficiencyClassCount = 2;
            Route(mobile, true, "universal");
            // CPU-based mobile routing works without model lists or a user device override.
            foreach (string name in new[] { "AMD Ryzen Z1 Extreme", "AMD Ryzen Z2 Extreme", "AMD Ryzen 7 7840U" })
            {
                var mobileCpu = Facts(name, "AuthenticAMD");
                Route(mobileCpu, false, "universal"); Route(mobileCpu, true, "amd-lowlat");
                mobileCpu.Hybrid = true; mobileCpu.EfficiencyClassCount = 2;
                Route(mobileCpu, false, "universal"); Route(mobileCpu, true, "universal");
            }
            var arm = Facts("Snapdragon X Elite", "Qualcomm"); arm.Arm = true;
            var groups = Facts("AMD Ryzen Threadripper", "AuthenticAMD"); groups.ProcessorGroupCount = 2;
            var packages = Facts("Intel Core i9-13900K", "GenuineIntel"); packages.PackageCount = 2;
            var vm = Facts("Intel Core i9-13900K", "GenuineIntel"); vm.VirtualMachine = true;
            foreach (var platform in new[] {
                Facts("AMD Ryzen Threadripper PRO 7995WX", "AuthenticAMD"),
                Facts("Intel Xeon W-2495X", "GenuineIntel"),
                Facts("AMD EPYC 9654", "AuthenticAMD"), Facts("AMD Opteron 6386 SE", "AuthenticAMD"), groups, packages,
                new ManagedPowerHardwareFacts { CpuName="Workstation", Workstation=true } })
                foreach (bool aggressive in new[] { false, true })
                    foreach (string legacySource in new[] { "auto", "amd-lowlat", "powerx-v3" })
                    {
                        ManagedPowerPresetSelection selected = ManagedPowerPreset.Resolve(platform, aggressive, legacySource);
                        Check(selected.Preset != null && selected.Preset.Id == "universal" && !selected.PreferOem && !selected.IsExplicit,
                            "Workstation or multi-processor route did not select the universal plan: " + platform.CpuName);
                    }
            foreach (var retained in new[] { arm, vm, Facts("unrecognized processor", "other"),
                new ManagedPowerHardwareFacts { Vendor="Qualcomm", CpuName="Snapdragon", ProcessorGroupCount=2 } })
                Route(retained, true, null);
            foreach (ManagedPowerPreset source in ManagedPowerPreset.AllSources)
            {
                ManagedPowerPresetSelection selected = ManagedPowerPreset.Resolve(dual, false, source.Id);
                Check(selected.Preset == source && selected.IsExplicit && !selected.PreferOem && selected.PreserveParking,
                    "Explicit source selection was rerouted or lost topology adaptation: " + source.Id);
            }
            var unknown = ManagedPowerPreset.Resolve(single, true, "not-a-source");
            Check(unknown.Preset == null && unknown.IsExplicit && unknown.PreferOem,
                "Missing explicit source silently fell back to another preset");
        }
        private static void ResourcesAndRawData()
        {
            string[] ids = { "excellent", "amd-generic", "powerx-v3", "intel-kf", "amd-x3d", "amd-lowlat", "universal" };
            string[] labels = { "卓越性能", "AMD 通用", "Intel Non-K", "Intel K 系列性能", "AMD X3D", "AMD 低延迟", "笔记本/台式通用" };
            int[] sizes = { 8192, 40960, 28672, 16384, 40960, 16384, 8192 };
            int[] counts = { 2, 141, 100, 56, 141, 56, 12 };
            string[] hashes = {
                "ecfedf513f42eb25d0ca4494e5b0dadd608b96d438c43b47ee4abbf4370a93b4",
                "fe8e1f41e4b854eb0397af49ad07f1494537e9088d8b47da241a2a454066811b",
                "2275d6b076d18bf0d8e2f3883d03b86f08c784c8cdf76a716524d7fdc6c8938e",
                "7b5e5331a846d5b017a72e5063dd730447859af8cb25d92a49ad953ecfda9678",
                "04d5b67d6deef9d9b4927977a58bafb3009bb81d99aa64315db2452600bc685d",
                "659b06ae8ee6486a03063607a7c67e7139ce4a84d4126c9474c3c5f55da5e98e",
                "5b629fdd70ac5590b143b9accae65d1cd4e90b92cc37875b83ca1eedf8d1449d" };
            int total = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                ManagedPowerPreset source = ManagedPowerPreset.Find(ids[i]);
                Check(source != null && ManagedPowerPreset.Find(source.OriginalFile) == source, "Source lookup failed: " + ids[i]);
                Check(source.DisplayName == labels[i], "Power-plan label does not describe its hardware or purpose: " + ids[i]);
                byte[] bytes = source.ReadBytes();
                using (SHA256 sha = SHA256.Create())
                    Check(bytes.Length == sizes[i] && BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() == hashes[i]
                        && source.Hash == hashes[i], "Original power-plan bytes or pinned identity changed: " + ids[i]);
                var seen = new HashSet<string>(); int count = 0;
                foreach (ManagedPowerPresetValue value in source.Entries)
                { Check(seen.Add(value.Sub.ToString("N") + "/" + value.Setting.ToString("N")), "Duplicate source setting: " + ids[i]); count++; }
                Check(count == counts[i], "Source settings were filtered or synthesized: " + ids[i]); total += count;
            }
            Check(total == 508, "Complete source setting record count changed");
            Guid epp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
            ManagedPowerPreset powerx = ManagedPowerPreset.Find("powerx-v3");
            Check(!powerx.AcFor(Cpu, epp).HasValue && powerx.DcFor(Cpu, epp) == 0,
                "Absent AC was incorrectly synthesized from the explicit DC zero");
            ManagedPowerPreset amd = ManagedPowerPreset.Find("amd-generic");
            Check(amd.AcFor(Cpu, new Guid("4d2b0152-7d5c-498b-88e2-34345392a2c5")) == 5000
                && amd.AcFor(Cpu, new Guid("7d24baa7-0b84-480f-840c-1b0743c00f5f")) == 128
                && amd.AcFor(Cpu, new Guid("8baa4a8a-14c6-4451-8e8b-14bdbd197537")) == 0,
                "Original performance interval, history or autonomous mode was silently replaced");
            Check(amd.AcFor(Cpu, new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c")) == 100
                && ManagedPowerPreset.Find("amd-x3d").AcFor(Cpu, new Guid("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5")) == 4,
                "Original idle or heterogeneous policy was filtered out");
            ManagedPowerPresetValue wireless = amd.FindValue(new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1"), new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a"));
            Check(wireless != null && !wireless.Ac.HasValue && !wireless.Dc.HasValue
                && wireless.AcRaw == "00000000" && wireless.DcRaw == "00000000"
                && wireless.AcRegistryType == 3 && wireless.DcRegistryType == 3,
                "REG_BINARY source values were retyped or lost");
            Check(amd.FindValue(ManagedPowerPreset.SubNone, new Guid("245d8541-3943-4422-b025-13a784f679b7")) != null,
                "Root personality setting was not normalized to the no-subgroup identifier");
        }
        internal static int Run()
        {
            checks = 0; Routing(); ResourcesAndRawData();
            Console.WriteLine("PASS managed preset sources and routing checks=" + checks + "; seven original resources; no native calls");
            return checks;
        }
    }
}
