// 作者：bdth 2074055628@qq.com
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
            if (profile == null || key == PolicyCatalog.KeyStandbyCleaner || AutomaticPolicy.IsRetired(key)
                || (AutomaticPolicy.Enabled && (!AutomaticPolicy.IsIntent(key) || key == PolicyCatalog.KeyPreset))) return false;
            string canonical = PolicyCatalog.Canonical(key, value);
            if (canonical == null) return false;
            if (key == PolicyCatalog.KeyPreset && canonical == "2") canonical = "0";
            profile.Overrides[key] = canonical;
            return true;
        }

        public static string Read(GameProfile profile, string key)
        {
            if (AutomaticPolicy.IsRetired(key)) return "0";
            if (AutomaticPolicy.Enabled) return For(profile).ValueOf(key);
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
                string canonical = GameProfile.IsRetiredOverrideKey(key) ? null : PolicyCatalog.Canonical(key, profile.Overrides[key]);
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
            if (AutomaticPolicy.IsRetired(key)) return "0";
            return AutomaticPolicy.Enabled ? AutomaticPolicy.Resolve(key, RawGlobalValue) : RawGlobalValue(key);
        }

        internal static string RawGlobalValue(string key)
        {
            if (AutomaticPolicy.IsRetired(key)) return "0";
            if (key == PolicyCatalog.KeySuppressFamily || key == PolicyCatalog.KeyAutoCores
                || key == PolicyCatalog.KeyGameRebar || key == PolicyCatalog.KeyGameFullscreenOptimizations) return "0";
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
        internal readonly ManualCorePlan ManualCores;

        internal PolicySnapshot(GameProfile profile)
        {
            if (profile != null)
            {
                ManualCores = ManualCorePlan.Parse(ManualCorePlan.Raw(profile));
                if (ManualCores != null) overridden.Add(ManualCorePlan.Key);
                foreach (PolicyItem item in PolicyCatalog.All)
                {
                    if (AutomaticPolicy.Enabled && item.Key == PolicyCatalog.KeyPreset) continue;
                    string value;
                    if (!profile.Overrides.TryGetValue(item.Key, out value)) continue;
                    string canonical = PolicyCatalog.Canonical(item.Key, value);
                    values[item.Key] = canonical ?? item.Fallback;
                    overridden.Add(item.Key);
                }
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
            if (key == null) return null;
            if (AutomaticPolicy.Enabled && AutomaticPolicy.IsEnvironment(key)) return AutomaticPolicy.EnvironmentPreference(key) ? "1" : "0";
            if (AutomaticPolicy.IsRetired(key)) return "0";
            if (AutomaticPolicy.Enabled) return AutomaticPolicy.Resolve(key, RawValueOf);
            if (PolicyCatalog.IsHandheldBlocked(key) && Preset == PerformancePreset.Handheld) return "0";
            string value;
            if (values.TryGetValue(key, out value)) return value;
            PolicyItem item = PolicyCatalog.ItemOf(key);
            if (item == null) return null;
            return PolicyResolver.GlobalValue(item.Key) ?? item.Fallback;
        }

        internal string RawValueOf(string key)
        {
            if (AutomaticPolicy.IsRetired(key)) return "0";
            string value;
            return values.TryGetValue(key, out value) ? value : PolicyResolver.RawGlobalValue(key);
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
        public bool FrameCoordinationOn { get { return AutomaticPolicy.Enabled && On(FrameCoordination.ActionKey); } }
        public bool PowerPlanOn { get { return !PowerPlan.NoSwitchSelected && On(PolicyCatalog.KeyPowerPlan); } }
        public bool StandbyCleaner { get { return On(PolicyCatalog.KeyStandbyCleaner); } }
        public bool PauseDownloads { get { return On(PolicyCatalog.KeyPauseDl); } }
        public bool PauseUpdate { get { return On(PolicyCatalog.KeyPauseUpdate); } }
        public bool PauseMaintenance { get { return On(PolicyCatalog.KeyPauseMaintenance); } }
        public bool PauseServices { get { return On(PolicyCatalog.KeyPauseServices); } }
        public bool Awake { get { return On(PolicyCatalog.KeyAwake); } }
        public bool EnglishInput { get { return On(PolicyCatalog.KeyEnglishInput); } }
        public bool AutoCoresAllowed { get { return ManualCores != null && On(PolicyCatalog.KeyAutoCores); } }
        public bool IdlePolicy { get { return On(PolicyCatalog.KeyIdlePolicy); } }
        public bool GpuPowerMax { get { return On(PolicyCatalog.KeyGpuPowerMax); } }
        public bool DwmBoost { get { return On(PolicyCatalog.KeyDwmBoost); } }
        public bool WsTrim { get { return On(PolicyCatalog.KeyWsTrim); } }
        public bool NvMaxPerf { get { return On(PolicyCatalog.KeyNvMaxPerf); } }
        public string NvLowLatMode { get { return ValueOf(PolicyCatalog.KeyNvLowLat); } }
        public bool NvSmoothMotion { get { return On(PolicyCatalog.KeyNvSmoothMotion); } }
        public bool NvShaderCacheMax { get { return On(PolicyCatalog.KeyNvShaderCache); } }
        public bool NvRebar { get { return On(PolicyCatalog.KeyGameRebar); } }
        public string NvDlssMode { get { return ValueOf(PolicyCatalog.KeyNvDlss); } }
        public bool AmdAntiLag { get { return On(PolicyCatalog.KeyAmdAntiLag); } }
        public bool AmdAfmf { get { return On(PolicyCatalog.KeyAmdAfmf); } }
        public bool IntelEnduranceOff { get { return On(PolicyCatalog.KeyIntelEndurance); } }
        public bool CacheWarm { get { return On(PolicyCatalog.KeyCacheWarm); } }
        public bool AdaptiveEscalate { get { return On(PolicyCatalog.KeyAdaptiveEscalate); } }

        public bool EffSuppress { get { return SuppressBackground; } }
        public bool EffBoost { get { return BoostGame; } }
        public bool EffLane { get { return RenderLane; } }
        public bool EffAggressive { get { return GameMode.IsAggressive(Preset, Aggressive); } }
    }
}
