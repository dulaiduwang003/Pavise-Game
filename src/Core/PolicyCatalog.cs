// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal enum PolicyValueKind
    {
        Bool = 0,
        Enum = 1,
        Choice = 2
    }

    internal sealed class PolicyItem
    {
        public readonly string Key;
        public readonly PolicyValueKind Kind;
        public readonly string Fallback;
        public readonly string LangKey;
        public readonly string GroupKey;
        public readonly string[] Choices;

        public PolicyItem(string key, PolicyValueKind kind, string fallback,
            string langKey, string groupKey, string[] choices)
        {
            Key = key;
            Kind = kind;
            Fallback = fallback;
            LangKey = langKey;
            GroupKey = groupKey;
            Choices = choices ?? new string[0];
        }
    }

    internal static class PolicyCatalog
    {
        public const string KeyPreset = "PerformancePreset";
        public const string KeyTimerTick = "AutoTimerTick";
        public const string KeyGlobalTimer = "AutoGlobalTimer";
        public const string KeySuppress = "GmSuppress";
        public const string KeySuppressFamily = "GameFamilyBackground";
        public const string KeyGameFullscreenOptimizations = "GameDisableFullscreenOptimizationsV1";
        public const string KeyBoost = "GmBoost";
        public const string KeyAutoCores = "GmAutoCoresV2";
        public const string KeyAggressive = "GmAggressive";
        public const string KeyGpuDemote = "GmGpuDemote";
        public const string KeyAdaptiveEscalate = "GmAdaptiveEscalateV1";
        public const string KeyRenderLane = "GmRenderLane";
        public const string KeyPowerPlan = "PowerPlanOn";
        public const string KeyStandbyCleaner = "GmStandbyCleanerV2";
        public const string KeyCacheWarm = "GmCacheWarmV2";
        public const string KeyPauseDl = "GmPauseDl";
        public const string KeyPauseUpdate = "GmPauseUpdate";
        public const string KeyPauseMaintenance = "GmPauseMaintenanceV1";
        public const string KeyPauseServices = "GmPauseServices";
        public const string KeyAwake = "GmAwake";
        public const string KeyEnglishInput = "GmEnglishInputV1";
        public const string KeyNvMaxPerf = "NvMaxPerf";
        public const string KeyNvLowLat = "NvLowLat";
        public const string KeyNvSmoothMotion = "NvSmoothMotion";
        public const string KeyNvShaderCache = "NvShaderCache";
        public const string KeyNvRebar = "NvRebar";
        public const string KeyGameRebar = "GameNvRebarV1";
        public const string KeyNvDlss = "NvDlss";
        public const string KeyAmdAntiLag = "AmdAntiLag";
        public const string KeyAmdAfmf = "AmdAfmf";
        public const string KeyIntelEndurance = "GmIntelEnduranceV1";
        public const string KeyLaptopPerf = "GmLaptopPerfV1";
        public const string KeyIdlePolicy = "GmIdlePolicyV1";
        public const string KeyGpuPowerMax = "GmGpuPowerMax";
        public const string KeyDwmBoost = "GmDwmBoost";
        public const string KeyWsTrim = "GmWsTrimV1";

        // 掌机档不提供的项，在快照中也视为关闭。
        public static readonly string[] HandheldBlocked =
            { KeyCacheWarm };

        public static bool IsHandheldBlocked(string key)
        {
            if (key == null) return false;
            foreach (string blocked in HandheldBlocked)
                if (string.Equals(blocked, key, StringComparison.Ordinal)) return true;
            return false;
        }

        public const string GroupMode = "cfg.group.mode";
        public const string GroupBackground = "cfg.group.bg";
        public const string GroupMemPower = "cfg.group.mempower";
        public const string GroupEnvironment = "cfg.group.env";
        public const string GroupGraphics = "cfg.group.gpu";

        private static readonly PolicyItem[] Items =
        {
            new PolicyItem(KeyPreset, PolicyValueKind.Enum, "0", "cfg.mode", GroupMode,
                new[] { "0", "1", "6", "4", "2" }),
            new PolicyItem(KeySuppress, PolicyValueKind.Bool, "1", "v14.bg.master", GroupBackground, null),
            new PolicyItem(KeySuppressFamily, PolicyValueKind.Bool, "0", "lib.family.suppress", GroupBackground, null),
            new PolicyItem(KeyGameFullscreenOptimizations, PolicyValueKind.Bool, "0", "auto.fso", GroupGraphics, null),
            new PolicyItem(KeyAutoCores, PolicyValueKind.Bool, "0", "schedule.auto.allow", GroupBackground, null),
            new PolicyItem(KeyGameRebar, PolicyValueKind.Bool, "0", "set.nvrebar", GroupGraphics, null),
            new PolicyItem(KeyBoost, PolicyValueKind.Bool, "1", "gm.boost", GroupBackground, null),
            new PolicyItem(KeyAggressive, PolicyValueKind.Bool, "0", "gm.aggressive", GroupBackground, null),
            new PolicyItem(KeyGpuDemote, PolicyValueKind.Bool, "0", "gm.gpudemote", GroupBackground, null),
            new PolicyItem(KeyAdaptiveEscalate, PolicyValueKind.Bool, "0", "gm.adaptive", GroupBackground, null),
            new PolicyItem(KeyRenderLane, PolicyValueKind.Bool, "1", "gm.lane", GroupBackground, null),
            new PolicyItem(KeyPowerPlan, PolicyValueKind.Bool, "1", "plan.pick.title", GroupMemPower, null),
            new PolicyItem(KeyCacheWarm, PolicyValueKind.Bool, "0", "gm.cachewarm", GroupMemPower, null),
            new PolicyItem(KeyStandbyCleaner, PolicyValueKind.Bool, "0", "gm.standbycleaner", GroupEnvironment, null),
            new PolicyItem(KeyIdlePolicy, PolicyValueKind.Bool, "0", "extreme.idle", GroupMemPower, null),
            new PolicyItem(KeyDwmBoost, PolicyValueKind.Bool, "0", "extreme.dwm", GroupEnvironment, null),
            new PolicyItem(KeyTimerTick, PolicyValueKind.Bool, "0", "set.timertick", GroupEnvironment, null),
            new PolicyItem(KeyGlobalTimer, PolicyValueKind.Bool, "0", "set.gtimer", GroupEnvironment, null),
            new PolicyItem(KeyPauseUpdate, PolicyValueKind.Bool, "0", "gm.pausewu", GroupEnvironment, null),
            new PolicyItem(KeyIntelEndurance, PolicyValueKind.Bool, "0", "set.intel.endurance", GroupGraphics, null),
            new PolicyItem(KeyNvLowLat, PolicyValueKind.Choice, "off", "set.nvll", GroupGraphics, new[] { "off", "on" }),
            new PolicyItem(KeyAmdAntiLag, PolicyValueKind.Bool, "0", "set.amdalag", GroupGraphics, null),
            new PolicyItem(KeyWsTrim, PolicyValueKind.Bool, "0", "gm.wstrim", GroupMemPower, null),
            new PolicyItem(KeyPauseDl, PolicyValueKind.Bool, "1", "gm.pausedl", GroupEnvironment, null),
            new PolicyItem(KeyPauseMaintenance, PolicyValueKind.Bool, "1", "gm.pausemaint", GroupEnvironment, null),
            new PolicyItem(KeyPauseServices, PolicyValueKind.Bool, "0", "gm.pausesvc", GroupEnvironment, null),
            new PolicyItem(KeyAwake, PolicyValueKind.Bool, "1", "set.awake", GroupEnvironment, null),
            new PolicyItem(KeyNvMaxPerf, PolicyValueKind.Bool, "0", "set.nvmax", GroupGraphics, null),
            new PolicyItem(KeyNvShaderCache, PolicyValueKind.Bool, "0", "auto.nvshader", GroupGraphics, null),
        };

        private static readonly Dictionary<string, PolicyItem> ByKey = BuildIndex();

        private static Dictionary<string, PolicyItem> BuildIndex()
        {
            var map = new Dictionary<string, PolicyItem>(StringComparer.Ordinal);
            foreach (PolicyItem item in Items) map[item.Key] = item;
            return map;
        }

        public static int Count
        {
            get { return Items.Length; }
        }

        public static IList<PolicyItem> All
        {
            get { return Items; }
        }

        public static PolicyItem ItemOf(string key)
        {
            PolicyItem item;
            return key != null && ByKey.TryGetValue(key, out item) ? item : null;
        }

        public static string Canonical(string key, string value)
        {
            if (key == ManualCorePlan.Key)
            {
                ManualCorePlan manual = ManualCorePlan.Parse(value);
                return manual == null ? null : manual.Encode();
            }
            PolicyItem item = ItemOf(key);
            if (item == null) return null;
            string v = (value ?? "").Trim();
            switch (item.Kind)
            {
                case PolicyValueKind.Bool:
                    return v.Length == 0 || v == "0"
                        || v.Equals("false", StringComparison.OrdinalIgnoreCase) ? "0" : "1";
                case PolicyValueKind.Enum:
                    int parsed;
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        return item.Fallback;
                    string normalized = parsed.ToString(CultureInfo.InvariantCulture);
                    foreach (string choice in item.Choices)
                        if (choice == normalized) return normalized;
                    return key == KeyPreset
                        ? ((int)PresetValue.From(parsed)).ToString(CultureInfo.InvariantCulture)
                        : item.Fallback;
                case PolicyValueKind.Choice:
                    foreach (string choice in item.Choices)
                        if (string.Equals(choice, v, StringComparison.OrdinalIgnoreCase)) return choice;
                    return item.Fallback;
                default:
                    return null;
            }
        }
    }
}
