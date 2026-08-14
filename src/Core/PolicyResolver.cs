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
                    string raw = item.Key == PolicyCatalog.KeyNvLowLat
                        ? Settings.LoadStr(item.Key, "off")
                        : Settings.LoadStr(item.Key, item.Fallback);
                    return PolicyCatalog.Canonical(item.Key, raw);
                default:
                    return PolicyCatalog.Canonical(item.Key, Settings.LoadStr(item.Key, item.Fallback));
            }
        }
    }

    internal sealed class PolicySnapshot
    {
        private readonly Dictionary<string, string> values =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> overridden = new HashSet<string>(StringComparer.Ordinal);
        public readonly string ProfileId;
        public readonly string ProfileName;

        internal PolicySnapshot(GameProfile profile)
        {
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                string value;
                if (profile != null && profile.Overrides.TryGetValue(item.Key, out value))
                {
                    string canonical = PolicyCatalog.Canonical(item.Key, value);
                    values[item.Key] = canonical ?? item.Fallback;
                    overridden.Add(item.Key);
                }
                else values[item.Key] = PolicyResolver.GlobalValue(item.Key) ?? item.Fallback;
            }
            if (profile != null)
            {
                ProfileId = profile.Id;
                ProfileName = profile.Name;
            }
        }

        public int OverrideCount { get { return overridden.Count; } }

        public bool IsGlobal { get { return overridden.Count == 0; } }

        public bool HasOverride(string key)
        {
            return key != null && overridden.Contains(key);
        }

        public string ValueOf(string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : null;
        }

        private bool On(string key)
        {
            return values[key] == "1";
        }

        public PerformancePreset Preset
        {
            get
            {
                int parsed;
                return int.TryParse(values[PolicyCatalog.KeyPreset], out parsed)
                    && parsed >= 0 && parsed <= 3 ? (PerformancePreset)parsed : PerformancePreset.Standard;
            }
        }

        public bool SuppressBackground { get { return On(PolicyCatalog.KeySuppress); } }
        public bool BoostGame { get { return On(PolicyCatalog.KeyBoost); } }
        public bool Aggressive { get { return On(PolicyCatalog.KeyAggressive); } }
        public bool SqueezeBackground { get { return On(PolicyCatalog.KeySqueezeBg); } }
        public bool GpuDemote { get { return On(PolicyCatalog.KeyGpuDemote); } }
        public bool IfeoBoost { get { return On(PolicyCatalog.KeyIfeoBoost); } }
        public bool RenderLane { get { return On(PolicyCatalog.KeyRenderLane); } }
        public bool UploadYield { get { return On(PolicyCatalog.KeyUploadYield); } }
        public bool StrictCores { get { return On(PolicyCatalog.KeyStrictCores); } }
        public bool CoreDomainAlt { get { return On(PolicyCatalog.KeyCoreDomainAlt); } }
        public bool StandbySweep { get { return On(PolicyCatalog.KeyStandbySweep); } }
        public bool PowerPlanOn { get { return On(PolicyCatalog.KeyPowerPlan); } }
        public bool PauseDownloads { get { return On(PolicyCatalog.KeyPauseDl); } }
        public bool PauseUpdate { get { return On(PolicyCatalog.KeyPauseUpdate); } }
        public bool SvcPause { get { return On(PolicyCatalog.KeySvcPause); } }
        public bool SvcYield { get { return On(PolicyCatalog.KeySvcYield); } }
        public bool WlanGuard { get { return On(PolicyCatalog.KeyWlanGuard); } }
        public bool Awake { get { return On(PolicyCatalog.KeyAwake); } }
        public bool GameDvrOff { get { return On(PolicyCatalog.KeyGameDvrOff); } }
        public bool NvMaxPerf { get { return On(PolicyCatalog.KeyNvMaxPerf); } }
        public string NvLowLatMode { get { return values[PolicyCatalog.KeyNvLowLat]; } }
        public bool NvSmoothMotion { get { return On(PolicyCatalog.KeyNvSmoothMotion); } }
        public bool NvShaderCacheMax { get { return On(PolicyCatalog.KeyNvShaderCache); } }
        public bool NvAnselOff { get { return On(PolicyCatalog.KeyNvAnselOff); } }
        public bool NvRebar { get { return On(PolicyCatalog.KeyNvRebar); } }
        public bool NvBattFull { get { return On(PolicyCatalog.KeyNvBattFull); } }
        public string NvFrlMode { get { return values[PolicyCatalog.KeyNvFrl]; } }
        public string NvDlssMode { get { return values[PolicyCatalog.KeyNvDlss]; } }
        public bool AmdAntiLag { get { return On(PolicyCatalog.KeyAmdAntiLag); } }
        public bool AmdAfmf { get { return On(PolicyCatalog.KeyAmdAfmf); } }
        public string AmdFrlMode { get { return values[PolicyCatalog.KeyAmdFrl]; } }

        public ulong CoreMask
        {
            get
            {
                string raw = values[PolicyCatalog.KeyCoreMask];
                ulong mask;
                return raw.Length > 0 && ulong.TryParse(raw, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out mask) ? mask : 0;
            }
        }

        public bool Extreme { get { return Preset == PerformancePreset.Extreme; } }
        public bool EffSuppress { get { return SuppressBackground || Extreme; } }
        public bool EffBoost { get { return BoostGame || Extreme; } }
        public bool EffIfeo { get { return IfeoBoost || Extreme; } }
        public bool EffLane { get { return RenderLane || Extreme; } }
        public bool EffAggressive { get { return GameMode.IsAggressive(Preset, Aggressive); } }
    }
}
