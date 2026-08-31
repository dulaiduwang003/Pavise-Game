// @author bdth 2074055628@qq.com
// 文件用途 逐游戏独立配置的键目录与取值规范化
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal enum PolicyValueKind
    {
        Bool = 0,
        Enum = 1,
        Choice = 2,
        MaskHex = 3
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
        public const string KeySuppress = "GmSuppress";
        public const string KeySuppressFamily = "GameFamilyBackground";
        public const string KeyBoost = "GmBoost";
        public const string KeyAggressive = "GmAggressive";
        public const string KeyGpuDemote = "GmGpuDemote";
        public const string KeyRenderLane = "GmRenderLane";
        public const string KeyStrictCores = "GmStrictCores";
        public const string KeyCoreDomainAlt = "GmCoreDomainAlt";
        public const string KeyCoreMask = "GmCoreMask";
        public const string KeyPowerPlan = "PowerPlanOn";
        public const string KeyPowerYield = "GmPowerYield";
        // 独立的开关 绝不继承已退役选项留下的 true 值
        public const string KeyDisableCpuIdle = "GmDisableCpuIdleV2";
        public const string KeyStandbyCleaner = "GmStandbyCleanerV2";
        public const string KeyPauseDl = "GmPauseDl";
        public const string KeyPauseUpdate = "GmPauseUpdate";
        public const string KeyPauseServices = "GmPauseServices";
        public const string KeyWlanGuard = "GmWlanGuard";
        public const string KeyAwake = "GmAwake";
        public const string KeyEnglishInput = "GmEnglishInputV1";
        public const string KeyNvMaxPerf = "NvMaxPerf";
        public const string KeyNvLowLat = "NvLowLat";
        public const string KeyNvSmoothMotion = "NvSmoothMotion";
        public const string KeyNvShaderCache = "NvShaderCache";
        public const string KeyNvRebar = "NvRebar";
        public const string KeyNvDlss = "NvDlss";
        public const string KeyAmdAntiLag = "AmdAntiLag";
        public const string KeyAmdAfmf = "AmdAfmf";
        public const string KeyIntelLowLatency = "GmIntelLowLatencyV1";
        public const string KeyVramShield = "GmVramShield";
        public const string KeyMemShield = "GmMemShield";
        public const string KeyCacheWarm = "GmCacheWarm";
        public const string KeyDisplaySolo = "GmDisplaySolo";

        public const string GroupMode = "cfg.group.mode";
        public const string GroupBackground = "cfg.group.bg";
        public const string GroupCores = "cfg.group.core";
        public const string GroupMemPower = "cfg.group.mempower";
        public const string GroupEnvironment = "cfg.group.env";
        public const string GroupGraphics = "cfg.group.gpu";

        private static readonly PolicyItem[] Items =
        {
            // 顺序就是界面顺序 CfgOptionLabels 和 ModeStrip.Order 都按下标对齐 别改成数值序
            new PolicyItem(KeyPreset, PolicyValueKind.Enum, "0", "cfg.mode", GroupMode,
                new[] { "0", "1", "4", "2" }),
            new PolicyItem(KeySuppress, PolicyValueKind.Bool, "1", "v14.bg.master", GroupBackground, null),
            new PolicyItem(KeySuppressFamily, PolicyValueKind.Bool, "0", "lib.family.suppress", GroupBackground, null),
            new PolicyItem(KeyBoost, PolicyValueKind.Bool, "1", "gm.boost", GroupBackground, null),
            new PolicyItem(KeyAggressive, PolicyValueKind.Bool, "0", "gm.aggressive", GroupBackground, null),
            new PolicyItem(KeyGpuDemote, PolicyValueKind.Bool, "0", "gm.gpudemote", GroupBackground, null),
            new PolicyItem(KeyRenderLane, PolicyValueKind.Bool, "1", "gm.lane", GroupBackground, null),
            new PolicyItem(KeyStrictCores, PolicyValueKind.Bool, "0", "cfg.strictcores", GroupCores, null),
            new PolicyItem(KeyCoreDomainAlt, PolicyValueKind.Bool, "0", "cfg.domainalt", GroupCores, null),
            new PolicyItem(KeyCoreMask, PolicyValueKind.MaskHex, "", "cfg.coremask", GroupCores, null),
            new PolicyItem(KeyPowerPlan, PolicyValueKind.Bool, "1", "plan.pick.title", GroupMemPower, null),
            new PolicyItem(KeyPowerYield, PolicyValueKind.Bool, "0", "gm.poweryield", GroupMemPower, null),
            new PolicyItem(KeyDisableCpuIdle, PolicyValueKind.Bool, "0", "gm.disablecpuidle", GroupMemPower, null),
            new PolicyItem(KeyStandbyCleaner, PolicyValueKind.Bool, "0", "gm.standbycleaner", GroupMemPower, null),
            new PolicyItem(KeyPauseDl, PolicyValueKind.Bool, "1", "gm.pausedl", GroupEnvironment, null),
            new PolicyItem(KeyPauseUpdate, PolicyValueKind.Bool, "0", "gm.pausewu", GroupEnvironment, null),
            new PolicyItem(KeyPauseServices, PolicyValueKind.Bool, "0", "gm.pausesvc", GroupEnvironment, null),
            new PolicyItem(KeyWlanGuard, PolicyValueKind.Bool, "0", "gm.wlanguard", GroupEnvironment, null),
            new PolicyItem(KeyAwake, PolicyValueKind.Bool, "1", "set.awake", GroupEnvironment, null),
            new PolicyItem(KeyDisplaySolo, PolicyValueKind.Bool, "0", "gm.solo", GroupEnvironment, null),
            new PolicyItem(KeyEnglishInput, PolicyValueKind.Bool, "0", "gm.englishinput", GroupEnvironment, null),
            new PolicyItem(KeyNvMaxPerf, PolicyValueKind.Bool, "0", "set.nvmax", GroupGraphics, null),
            new PolicyItem(KeyNvLowLat, PolicyValueKind.Choice, "off", "set.nvll", GroupGraphics,
                new[] { "off", "on", "ultra" }),
            new PolicyItem(KeyNvSmoothMotion, PolicyValueKind.Bool, "0", "set.nvsmooth", GroupGraphics, null),
            new PolicyItem(KeyNvShaderCache, PolicyValueKind.Bool, "0", "set.nvshader", GroupGraphics, null),
            new PolicyItem(KeyNvRebar, PolicyValueKind.Bool, "0", "set.nvrebar", GroupGraphics, null),
            new PolicyItem(KeyNvDlss, PolicyValueKind.Choice, "off", "set.nvdlss", GroupGraphics,
                new[] { "off", "latest", "j", "k" }),
            new PolicyItem(KeyAmdAntiLag, PolicyValueKind.Bool, "0", "set.amdalag", GroupGraphics, null),
            new PolicyItem(KeyAmdAfmf, PolicyValueKind.Bool, "0", "set.amdafmf", GroupGraphics, null),
            new PolicyItem(KeyIntelLowLatency, PolicyValueKind.Bool, "0", "set.intel.lowlatency", GroupGraphics, null),
            new PolicyItem(KeyVramShield, PolicyValueKind.Bool, "0", "gm.vramshield", GroupGraphics, null),
            new PolicyItem(KeyMemShield, PolicyValueKind.Bool, "0", "gm.memshield", GroupMemPower, null),
            new PolicyItem(KeyCacheWarm, PolicyValueKind.Bool, "0", "gm.cachewarm", GroupMemPower, null),
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
                    return item.Fallback;
                case PolicyValueKind.Choice:
                    foreach (string choice in item.Choices)
                        if (string.Equals(choice, v, StringComparison.OrdinalIgnoreCase)) return choice;
                    return item.Fallback;
                default:
                    if (v.Length == 0) return "";
                    ulong mask;
                    return ulong.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask)
                        && mask != 0 ? mask.ToString("X") : "";
            }
        }
    }
}
