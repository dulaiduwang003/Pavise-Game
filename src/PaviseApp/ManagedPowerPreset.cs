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
        internal readonly string Id, DisplayName, ResourceFile, Hash;
        internal string SourceName { get { return DisplayName; } }
        internal string SourceSha256 { get { return Hash; } }
        private readonly ReadOnlyCollection<ManagedPowerPresetValue> values;
        internal IEnumerable<ManagedPowerPresetValue> Entries { get { return values; } }

        private ManagedPowerPreset(string id, string displayName, string file, string hash)
        {
            Id = id; DisplayName = displayName; ResourceFile = file; Hash = hash;
            values = Array.AsReadOnly(DataFor(id));
        }

        private static readonly ReadOnlyCollection<ManagedPowerPreset> sources = Array.AsReadOnly(new[]
        {
            new ManagedPowerPreset("excellent", "卓越性能", "excellent_performance.pow", "c02d0ddbba34b3afcf01ec8bfdf70e34b701d25d5232770c2d76ede807a5ae0f"),
            new ManagedPowerPreset("amd-generic", "AMD 通用", "amd_generic.pow", "8ff95d1fa4f8d0024d210276be6b8ee3c72480cd23e723bf2fe73ba50b7ac7ec"),
            new ManagedPowerPreset("intel-non-k", "Intel Non-K", "intel_non_k.pow", "c768f9114f5b584becedd960b4d2c28a6e604fe39cec327f05c9f8e3834bbf5c"),
            new ManagedPowerPreset("intel-k-series", "Intel K 系列性能", "intel_k_series.pow", "182c0aca7058baff1d10f7b6cf87e73f42168e613a7db738c9e9c3ca0433f1bc"),
            new ManagedPowerPreset("amd-x3d", "AMD X3D", "amd_x3d.pow", "a677d7afa8b70250dbe765a5777af8109721a6ed03760f4b94783c786efb0bbc"),
            new ManagedPowerPreset("amd-lowlat", "AMD 低延迟", "amd_low_latency.pow", "a3118f0442cdb220e3870f546d6bfffcf4346c85bdaf0f492fe21ef3b300e844"),
            new ManagedPowerPreset("universal", "笔记本/台式通用", "laptop_desktop_universal.pow", "9e54c556126aab2623f397efcf1eac8bf7445e439b63f95da7b5c46c5057526d"),
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
                    || string.Equals(source.ResourceFile, id, StringComparison.OrdinalIgnoreCase)) return source;
            return null;
        }

        internal static ManagedPowerPreset FromPolicyRecord(string id, string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrEmpty(fingerprint)) return null;
            ManagedPowerPreset current = Find(id);
            if (current != null) return current;
            string[] fields = fingerprint.Split(':');
            if (fields.Length != 3 || fields[0] != "full2" || (fields[2] != "normal" && fields[2] != "cache")) return null;
            // 旧标识仅按已知资源指纹识别，用于恢复记录兼容，不参与架构匹配。
            if (string.Equals(fields[1], "2275d6b076d18bf0d8e2f3883d03b86f08c784c8cdf76a716524d7fdc6c8938e", StringComparison.OrdinalIgnoreCase))
                return Find("intel-non-k");
            if (string.Equals(fields[1], "7b5e5331a846d5b017a72e5063dd730447859af8cb25d92a49ad953ecfda9678", StringComparison.OrdinalIgnoreCase))
                return Find("intel-k-series");
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
            using (Stream input = typeof(ManagedPowerPreset).Assembly.GetManifestResourceStream(ResourcePrefix + ResourceFile))
            {
                if (input == null) throw new InvalidDataException("Missing power-plan resource: " + ResourceFile);
                using (var output = new MemoryStream())
                {
                    input.CopyTo(output);
                    byte[] bytes = output.ToArray();
                    using (SHA256 sha = SHA256.Create())
                    {
                        string actual = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                        if (!string.Equals(actual, Hash, StringComparison.Ordinal))
                            throw new InvalidDataException("Power-plan resource hash mismatch: " + ResourceFile);
                    }
                    return bytes;
                }
            }
        }
    }
}
