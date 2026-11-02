// 保存七份原始电源计划的完整数据；架构选择规则见 ManagedPowerPreset.Architecture.cs。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;

namespace PaviseApp
{
    internal sealed class ManagedPowerPresetValue
    {
        internal readonly Guid Sub, Setting;
        internal readonly uint? Ac, Dc;
        // Non-DWORD source values remain raw bytes represented as hex; null is absence, never zero.
        internal readonly string AcRaw, DcRaw;
        internal readonly int AcRegistryType, DcRegistryType;

        internal ManagedPowerPresetValue(string sub, string setting, uint? ac, uint? dc,
            string acRaw, string dcRaw, int acRegistryType, int dcRegistryType)
        {
            Sub = new Guid(sub); Setting = new Guid(setting); Ac = ac; Dc = dc;
            AcRaw = acRaw; DcRaw = dcRaw;
            AcRegistryType = acRegistryType; DcRegistryType = dcRegistryType;
        }
    }

    internal sealed partial class ManagedPowerPreset
    {
        internal static readonly Guid SubNone = new Guid("fea3413e-7e05-4911-9a71-700331f1c294");
        internal const string ResourcePrefix = "Pavise.PowerPlans.";
        internal readonly string Id, DisplayName, OriginalFile, Hash;
        internal string SourceName { get { return DisplayName; } }
        internal string SourceSha256 { get { return Hash; } }
        private readonly ReadOnlyCollection<ManagedPowerPresetValue> values;
        internal IEnumerable<ManagedPowerPresetValue> Entries { get { return values; } }

        private ManagedPowerPreset(string id, string displayName, string file, string hash)
        {
            Id = id; DisplayName = displayName; OriginalFile = file; Hash = hash;
            values = Array.AsReadOnly(DataFor(id));
        }

        private static readonly ReadOnlyCollection<ManagedPowerPreset> sources = Array.AsReadOnly(new[]
        {
            new ManagedPowerPreset("excellent", "卓越性能", "excellent_performance.pow", "ecfedf513f42eb25d0ca4494e5b0dadd608b96d438c43b47ee4abbf4370a93b4"),
            new ManagedPowerPreset("amd-generic", "AMD 通用", "amd_generic.pow", "fe8e1f41e4b854eb0397af49ad07f1494537e9088d8b47da241a2a454066811b"),
            new ManagedPowerPreset("powerx-v3", "Intel Non-K", "intel_non_k.pow", "2275d6b076d18bf0d8e2f3883d03b86f08c784c8cdf76a716524d7fdc6c8938e"),
            new ManagedPowerPreset("intel-kf", "Intel K 系列性能", "intel_k_series.pow", "7b5e5331a846d5b017a72e5063dd730447859af8cb25d92a49ad953ecfda9678"),
            new ManagedPowerPreset("amd-x3d", "AMD X3D", "amd_x3d.pow", "04d5b67d6deef9d9b4927977a58bafb3009bb81d99aa64315db2452600bc685d"),
            new ManagedPowerPreset("amd-lowlat", "AMD 低延迟", "amd_low_latency.pow", "659b06ae8ee6486a03063607a7c67e7139ce4a84d4126c9474c3c5f55da5e98e"),
            new ManagedPowerPreset("universal", "笔记本/台式通用", "laptop_desktop_universal.pow", "5b629fdd70ac5590b143b9accae65d1cd4e90b92cc37875b83ca1eedf8d1449d"),
        });
        internal static IEnumerable<ManagedPowerPreset> AllSources { get { return sources; } }
        private static readonly ReadOnlyCollection<ManagedPowerPresetValue> allSettings = UnionSettings();
        // Key union for inspection/restoration. The representative values must not be applied as a composite plan.
        internal static IEnumerable<ManagedPowerPresetValue> AllSettings { get { return allSettings; } }

        private static ReadOnlyCollection<ManagedPowerPresetValue> UnionSettings()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ManagedPowerPresetValue>();
            foreach (ManagedPowerPreset source in sources)
                foreach (ManagedPowerPresetValue value in source.values)
                    if (seen.Add(value.Sub.ToString("D") + "/" + value.Setting.ToString("D"))) result.Add(value);
            return result.AsReadOnly();
        }

        internal static ManagedPowerPreset Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || string.Equals(id, "auto", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (ManagedPowerPreset source in sources)
                if (string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(source.OriginalFile, id, StringComparison.OrdinalIgnoreCase)) return source;
            return null;
        }

        internal uint? AcFor(Guid sub, Guid setting)
        {
            ManagedPowerPresetValue value = FindValue(sub, setting);
            return value == null ? null : value.Ac;
        }

        internal uint? DcFor(Guid sub, Guid setting)
        {
            ManagedPowerPresetValue value = FindValue(sub, setting);
            return value == null ? null : value.Dc;
        }

        internal ManagedPowerPresetValue FindValue(Guid sub, Guid setting)
        {
            foreach (ManagedPowerPresetValue value in values)
                if (value.Sub == sub && value.Setting == setting) return value;
            return null;
        }

        internal byte[] ReadBytes()
        {
            using (Stream input = typeof(ManagedPowerPreset).Assembly.GetManifestResourceStream(ResourcePrefix + OriginalFile))
            {
                if (input == null) throw new InvalidDataException("Missing power-plan resource: " + OriginalFile);
                using (var output = new MemoryStream())
                {
                    input.CopyTo(output);
                    byte[] bytes = output.ToArray();
                    using (SHA256 sha = SHA256.Create())
                    {
                        string actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                        if (!string.Equals(actual, Hash, StringComparison.Ordinal))
                            throw new InvalidDataException("Power-plan resource hash mismatch: " + OriginalFile);
                    }
                    return bytes;
                }
            }
        }
    }
}
