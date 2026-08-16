// @author bdth 2074055628@qq.com
// 文件用途 解析逐游戏覆盖与全局默认 生成对局冻结快照

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal sealed class PolicyDiff
    {
        public string Key;
        public string Value;
        public string GlobalValue;
        public bool SameAsGlobal;
    }

    internal static class PolicyResolver
    {
        public static bool SetOverride(GameProfile profile, string key, string value)
        {
            if (profile == null) return false;
            string canonical = PolicyCatalog.Canonical(key, value);
            if (canonical == null) return false;
            profile.Overrides[key] = canonical;
            return true;
        }

        public static string Read(GameProfile profile, string key)
        {
            string value;
            if (profile != null && profile.Overrides.TryGetValue(key, out value)) return value;
            return GlobalValue(key);
        }

        public static bool HasOverride(GameProfile profile, string key)
        {
            return profile != null && key != null && profile.Overrides.ContainsKey(key);
        }

        public static int CountOverrides(GameProfile profile)
        {
            return profile == null ? 0 : profile.Overrides.Count;
        }

        public static int ClearAllOverrides(GameProfile profile)
        {
            if (profile == null) return 0;
            int n = profile.Overrides.Count;
            profile.Overrides.Clear();
            return n;
        }

        public static int CopyOverrides(GameProfile source, GameProfile target)
        {
            if (source == null || target == null) return 0;
            target.Overrides.Clear();
            foreach (KeyValuePair<string, string> kv in source.Overrides)
                target.Overrides[kv.Key] = kv.Value;
            return target.Overrides.Count;
        }

        public static void Sanitize(GameProfile profile)
        {
            if (profile == null || profile.Overrides.Count == 0) return;
            var keys = new List<string>(profile.Overrides.Keys);
            foreach (string key in keys)
            {
                string canonical = PolicyCatalog.Canonical(key, profile.Overrides[key]);
                if (canonical == null) profile.Overrides.Remove(key);
                else profile.Overrides[key] = canonical;
            }
        }

        public static List<PolicyDiff> Diff(GameProfile profile)
        {
            var result = new List<PolicyDiff>();
            if (profile == null) return result;
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                string value;
                if (!profile.Overrides.TryGetValue(item.Key, out value)) continue;
                string global = GlobalValue(item.Key);
                result.Add(new PolicyDiff
                {
                    Key = item.Key,
                    Value = value,
                    GlobalValue = global,
                    SameAsGlobal = string.Equals(value, global, StringComparison.Ordinal)
                });
            }
            return result;
        }

        public static PolicySnapshot For(GameProfile profile)
        {
            return new PolicySnapshot(profile);
        }

        public static PolicySnapshot Global()
        {
            return new PolicySnapshot(null);
        }

        internal static string GlobalValue(string key)
        {
            PolicyItem item = PolicyCatalog.ItemOf(key);
            if (item == null) return null;
            switch (item.Kind)
            {
                case PolicyValueKind.Bool:
                    return Settings.Load(item.Key, item.Fallback == "1") ? "1" : "0";
                case PolicyValueKind.Enum:
                case PolicyValueKind.Choice:
                    return PolicyCatalog.Canonical(item.Key, Settings.LoadStr(item.Key, item.Fallback));
                default:
                    return PolicyCatalog.Canonical(item.Key, Settings.LoadStr(item.Key, item.Fallback));
            }
        }
    }

    internal sealed class PolicySnapshot
    {
        // 只钉住每游戏覆盖项 未覆盖项每次访问活读全局
        // 否则对局中修改全局模式或开关会被冻结值吞掉 必须退游戏才生效
        private readonly Dictionary<string, string> values =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> overridden = new HashSet<string>(StringComparer.Ordinal);
        public readonly string ProfileId;
        public readonly string ProfileName;

        internal PolicySnapshot(GameProfile profile)
        {
            if (profile == null) return;
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                string value;
                if (!profile.Overrides.TryGetValue(item.Key, out value)) continue;
                string canonical = PolicyCatalog.Canonical(item.Key, value);
                values[item.Key] = canonical ?? item.Fallback;
                overridden.Add(item.Key);
            }
            ProfileId = profile.Id;
            ProfileName = profile.Name;
        }

        public int OverrideCount { get { return overridden.Count; } }

        public bool IsGlobal { get { return overridden.Count == 0; } }

        public bool HasOverride(string key)
        {
            return key != null && overridden.Contains(key);
        }

        public string ValueOf(string key)
        {
            if (key == null) return null;
            string value;
            if (values.TryGetValue(key, out value)) return value;
            PolicyItem item = PolicyCatalog.ItemOf(key);
            if (item == null) return null;
            return PolicyResolver.GlobalValue(item.Key) ?? item.Fallback;
        }

        private bool On(string key)
        {
            return ValueOf(key) == "1";
        }

        public PerformancePreset Preset
        {
            get
            {
                int parsed;
                return int.TryParse(ValueOf(PolicyCatalog.KeyPreset), out parsed)
                    && parsed >= 0 && parsed <= 2 ? (PerformancePreset)parsed : PerformancePreset.Standard;
            }
        }

        public bool SuppressBackground { get { return On(PolicyCatalog.KeySuppress); } }
        public bool BoostGame { get { return On(PolicyCatalog.KeyBoost); } }
        public bool Aggressive { get { return On(PolicyCatalog.KeyAggressive); } }
        public bool SqueezeBackground { get { return On(PolicyCatalog.KeySqueezeBg); } }
        public bool GpuDemote { get { return On(PolicyCatalog.KeyGpuDemote); } }
        public bool IfeoBoost { get { return On(PolicyCatalog.KeyIfeoBoost); } }
        public bool RenderLane { get { return On(PolicyCatalog.KeyRenderLane); } }
        public bool StrictCores { get { return On(PolicyCatalog.KeyStrictCores); } }
        public bool CoreDomainAlt { get { return On(PolicyCatalog.KeyCoreDomainAlt); } }
        public bool StandbySweep { get { return On(PolicyCatalog.KeyStandbySweep); } }
        public bool PowerPlanOn { get { return On(PolicyCatalog.KeyPowerPlan); } }
        public bool PauseDownloads { get { return On(PolicyCatalog.KeyPauseDl); } }
        public bool PauseUpdate { get { return On(PolicyCatalog.KeyPauseUpdate); } }
        public bool WlanGuard { get { return On(PolicyCatalog.KeyWlanGuard); } }
        public bool Awake { get { return On(PolicyCatalog.KeyAwake); } }
        public bool NvMaxPerf { get { return On(PolicyCatalog.KeyNvMaxPerf); } }
        public string NvLowLatMode { get { return ValueOf(PolicyCatalog.KeyNvLowLat); } }
        public bool NvSmoothMotion { get { return On(PolicyCatalog.KeyNvSmoothMotion); } }
        public bool NvShaderCacheMax { get { return On(PolicyCatalog.KeyNvShaderCache); } }
        public bool NvAnselOff { get { return On(PolicyCatalog.KeyNvAnselOff); } }
        public bool NvRebar { get { return On(PolicyCatalog.KeyNvRebar); } }
        public string NvDlssMode { get { return ValueOf(PolicyCatalog.KeyNvDlss); } }
        public bool AmdAntiLag { get { return On(PolicyCatalog.KeyAmdAntiLag); } }
        public bool AmdAfmf { get { return On(PolicyCatalog.KeyAmdAfmf); } }

        public ulong CoreMask
        {
            get
            {
                string raw = ValueOf(PolicyCatalog.KeyCoreMask) ?? "";
                ulong mask;
                return raw.Length > 0 && ulong.TryParse(raw, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out mask) ? mask : 0;
            }
        }

        public bool EffSuppress { get { return SuppressBackground; } }
        public bool EffBoost { get { return BoostGame; } }
        public bool EffIfeo { get { return IfeoBoost; } }
        public bool EffLane { get { return RenderLane; } }
        public bool EffAggressive { get { return GameMode.IsAggressive(Preset, Aggressive); } }
    }
}
