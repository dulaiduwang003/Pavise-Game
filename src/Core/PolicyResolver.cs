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
            if (CoreScheduling.IsRetiredKey(key)) return key == CoreScheduling.HeavyMaskKey ? "" : "0";
            if (CoreScheduling.IsPlacementKey(key) && profile != null
                && profile.Overrides.ContainsKey(CoreScheduling.Key))
                return CoreScheduling.Value(CoreScheduling.ForProfile(profile, CoreScheduling.LoadGlobal()), key);
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
            if (CoreScheduling.IsRetiredKey(key)) return key == CoreScheduling.HeavyMaskKey ? "" : "0";
            if (CoreScheduling.IsPlacementKey(key) && CoreScheduling.HasGlobalRecord())
                return CoreScheduling.Value(CoreScheduling.LoadGlobal(), key);
            // 这个选项没有全局设置 老的 GmFamilyExempt 不会迁移成
            // 对现有库里每个游戏都不安全的开启状态
            if (key == PolicyCatalog.KeySuppressFamily) return "0";
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
        private readonly Dictionary<string, string> values =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> overridden = new HashSet<string>(StringComparer.Ordinal);
        public readonly string ProfileId;
        public readonly string ProfileName;
        public readonly bool ManualPlacement;
        public readonly CoreSchedulingPlan CorePlan;

        internal PolicySnapshot(GameProfile profile)
        {
            // 核心方案按对局冻结，避免一次扫后台读到两份正在切换的配置。
            bool hasGlobalRecord;
            CoreSchedulingPlan global = CoreScheduling.LoadGlobal(out hasGlobalRecord);
            CorePlan = CoreScheduling.ForProfile(profile, global);
            ManualPlacement = hasGlobalRecord
                || profile != null && profile.Overrides.ContainsKey(CoreScheduling.Key);
            foreach (string key in CoreScheduling.PlacementKeys)
                values[key] = hasGlobalRecord ? CoreScheduling.Value(global, key) : CoreScheduling.LegacyPolicyValue(key);
            if (profile == null) return;
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                string value;
                if (!profile.Overrides.TryGetValue(item.Key, out value)) continue;
                string canonical = PolicyCatalog.Canonical(item.Key, value);
                values[item.Key] = canonical ?? item.Fallback;
                overridden.Add(item.Key);
            }
            if (ManualPlacement)
            {
                CoreSchedulingPlan plan = CoreScheduling.ForProfile(profile, global);
                foreach (string key in CoreScheduling.PlacementKeys)
                {
                    values[key] = CoreScheduling.Value(plan, key);
                }
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
            if (CoreScheduling.IsRetiredKey(key)) return key == CoreScheduling.HeavyMaskKey ? "" : "0";
            // 极限档覆盖 只对清单内且未被用户停用的键 用户配置原样保留 切走即恢复
            //   先查清单再查档位 档位判定会回到 ValueOf(KeyPreset) 清单不含它 不会递归
            if (key != PolicyCatalog.KeyPreset)
            {
                string forced = ExtremeMode.ForcedPolicyValue(key);
                if (forced != null && Preset == PerformancePreset.Extreme) return forced;
            }
            string value;
            if (values.TryGetValue(key, out value)) return value;
            PolicyItem item = PolicyCatalog.ItemOf(key);
            if (item == null) return null;
            return PolicyResolver.GlobalValue(item.Key) ?? item.Fallback;
        }

        // 不经极限覆盖的本值 覆盖有就是覆盖 没有就是全局 给"是用户自己开的还是档位强制的"这种判断用
        public string OwnValueOf(string key)
        {
            if (key == null) return null;
            if (CoreScheduling.IsRetiredKey(key)) return key == CoreScheduling.HeavyMaskKey ? "" : "0";
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
                    ? PresetValue.From(parsed) : PerformancePreset.Standard;
            }
        }

        public bool SuppressBackground { get { return On(PolicyCatalog.KeySuppress); } }
        public bool BoostGame { get { return On(PolicyCatalog.KeyBoost); } }
        public bool Aggressive { get { return On(PolicyCatalog.KeyAggressive); } }
        public bool GpuDemote { get { return On(PolicyCatalog.KeyGpuDemote); } }
        public bool RenderLane { get { return On(PolicyCatalog.KeyRenderLane); } }
        public bool StrictCores { get { return On(PolicyCatalog.KeyStrictCores); } }
        public bool CoreDomainAlt { get { return On(PolicyCatalog.KeyCoreDomainAlt); } }
        public bool PowerPlanOn { get { return On(PolicyCatalog.KeyPowerPlan); } }
        public bool PowerYield { get { return On(PolicyCatalog.KeyPowerYield); } }
        public bool DisableCpuIdle { get { return On(PolicyCatalog.KeyDisableCpuIdle); } }
        public bool StandbyCleaner { get { return On(PolicyCatalog.KeyStandbyCleaner); } }
        public bool PauseDownloads { get { return On(PolicyCatalog.KeyPauseDl); } }
        public bool PauseUpdate { get { return On(PolicyCatalog.KeyPauseUpdate); } }
        public bool PauseMaintenance { get { return On(PolicyCatalog.KeyPauseMaintenance); } }
        public bool PauseServices { get { return On(PolicyCatalog.KeyPauseServices); } }
        public bool Awake { get { return On(PolicyCatalog.KeyAwake); } }
        public bool EnglishInput { get { return On(PolicyCatalog.KeyEnglishInput); } }
        public bool NvMaxPerf { get { return On(PolicyCatalog.KeyNvMaxPerf); } }
        public string NvLowLatMode { get { return ValueOf(PolicyCatalog.KeyNvLowLat); } }
        public bool NvSmoothMotion { get { return On(PolicyCatalog.KeyNvSmoothMotion); } }
        public bool NvShaderCacheMax { get { return On(PolicyCatalog.KeyNvShaderCache); } }
        public bool NvRebar { get { return On(PolicyCatalog.KeyNvRebar); } }
        public string NvDlssMode { get { return ValueOf(PolicyCatalog.KeyNvDlss); } }
        public bool AmdAntiLag { get { return On(PolicyCatalog.KeyAmdAntiLag); } }
        public bool AmdAfmf { get { return On(PolicyCatalog.KeyAmdAfmf); } }
        public bool IntelLowLatency { get { return On(PolicyCatalog.KeyIntelLowLatency); } }
        public bool IntelEnduranceOff { get { return On(PolicyCatalog.KeyIntelEndurance); } }
        public bool VramShield { get { return On(PolicyCatalog.KeyVramShield); } }
        public bool CacheWarm { get { return On(PolicyCatalog.KeyCacheWarm); } }
        public bool AdaptiveEscalate { get { return On(PolicyCatalog.KeyAdaptiveEscalate); } }

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
        public bool EffLane { get { return RenderLane; } }
        public bool EffAggressive { get { return GameMode.IsAggressive(Preset, Aggressive); } }
    }
}
