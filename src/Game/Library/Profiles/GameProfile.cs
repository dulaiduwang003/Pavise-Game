using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class GameProfile
    {
        public string Id;

        public string Name;

        public string Root;

        public string ExecutablePath;

        public string LearnedExecutablePath;

        // 来源只用于诊断 绑定登记时的精确路径 不决定调度或已开启功能的执行资格
        public string DiscoverySource;

        public string DiscoverySourcePath;

        internal string SourceFor(string executablePath)
        {
            return !string.IsNullOrEmpty(executablePath)
                && string.Equals(executablePath, DiscoverySourcePath, StringComparison.OrdinalIgnoreCase)
                && (DiscoverySource == "manual" || DiscoverySource == "automatic") ? DiscoverySource : "unknown";
        }

        internal bool RecordSource(string executablePath, bool manuallySelected)
        {
            string source = manuallySelected ? "manual" : "automatic";
            if (string.IsNullOrEmpty(executablePath)) return false;
            if (SourceFor(executablePath) == source || (!manuallySelected && SourceFor(executablePath) == "manual")) return false;
            DiscoverySource = source;
            DiscoverySourcePath = executablePath;
            return true;
        }

        internal string SourceDescription(string executablePath)
        {
            string source = SourceFor(executablePath);
            return source == "manual" ? Logger.Text("手工选择", "manual selection") : source == "automatic" ? Logger.Text("自动识别", "auto-detected") : Logger.Text("来源未记录", "source not recorded");
        }

        public bool ForceTrigger;

        public readonly HashSet<string> Entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        // GameAceSuppressionV1 和 GameAceGovernanceV1 是旧版反作弊开关 读取时丢弃 不换算成别的设置 逐游戏开关用 GameAntiCheatSuppressionV2
        private static readonly string[] RetiredOverrideKeys = new string[41]
        {
            "GameAceSuppressionV1", "GameAceGovernanceV1",
            "GmDisplaySolo", "GmMemShield", "GmGpuClockLockV1", "GmCacheWarm", "GmCacheWarmV2", "GmManualCorePlanV1", "GmCoreMask", "GmStrictCores", "GmCoreDomainAlt", "GmHeavySqueezeV2", "GmHeavyCoreMaskV1",
            "AutoRecording", "AutoInput", "AutoGraphics", "GmEnglishInputV1", "GmStandbyCleanerV2", "GmPauseUpdate", "GmPauseServices", "NvMaxPerf", "NvLowLat", "NvSmoothMotion",
            "NvShaderCache", "NvRebar", "NvDlss", "AmdAntiLag", "AmdAfmf", "GmIntelEnduranceV1", "GmGpuPowerMax", "GmWsTrimV1", "GmIdlePolicyV1", "AutoBackgroundCoreLayoutV1",
            "AutoLaptopPerformanceV1", "AutoLaptopPerformanceV2", "GmLaptopPerfV1", "GmAggressive", "GmAdaptiveEscalateV1", "PowerPlanOn", "GmRenderLane", "FrameCoordination"
        };

        public bool SuppressFamilyBackground
        {
            get
            {
                string value;
                if (Overrides.TryGetValue("GameFamilyBackground", out value))
                {
                    return value == "1";
                }
                return false;
            }
        }

        public string PreferredExecutablePath
        {
            get
            {
                if (!string.IsNullOrEmpty(LearnedExecutablePath))
                {
                    return LearnedExecutablePath;
                }
                return ExecutablePath;
            }
        }

        public bool DisableFullscreenOptimizations
        {
            get
            {
                string value;
                if (Overrides.TryGetValue("GameDisableFullscreenOptimizationsV1", out value))
                {
                    return value == "1";
                }
                return false;
            }
        }

        public GameProfile Clone()
        {
            GameProfile gameProfile = new GameProfile();
            gameProfile.Id = Id;
            gameProfile.Name = Name;
            gameProfile.Root = Root;
            gameProfile.ExecutablePath = ExecutablePath;
            gameProfile.LearnedExecutablePath = LearnedExecutablePath;
            gameProfile.DiscoverySource = DiscoverySource;
            gameProfile.DiscoverySourcePath = DiscoverySourcePath;
            gameProfile.ForceTrigger = ForceTrigger;
            GameProfile gameProfile2 = gameProfile;
            foreach (string entry in Entries)
            {
                gameProfile2.Entries.Add(entry);
            }
            foreach (KeyValuePair<string, string> @override in Overrides)
            {
                gameProfile2.Overrides[@override.Key] = @override.Value;
            }
            return gameProfile2;
        }

        internal static bool IsRetiredOverrideKey(string key)
        {
            switch (key)
            {
            case "GmPowerYield":
            case "GmVramShield":
                return true;
            case "PerformancePreset":
                return true;
            default:
            {
                string[] retiredOverrideKeys = RetiredOverrideKeys;
                foreach (string a in retiredOverrideKeys)
                {
                    if (string.Equals(a, key, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }
            }
        }

        public bool ContainsPath(string path)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(Root))
            {
                return false;
            }
            string value = Root.TrimEnd('\\') + "\\";
            return path.StartsWith(value, StringComparison.OrdinalIgnoreCase);
        }
    }
}
