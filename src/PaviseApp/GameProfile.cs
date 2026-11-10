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

        public bool ForceTrigger;

        public readonly HashSet<string> Entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly string[] RetiredOverrideKeys = new string[38]
        {
            "GmDisplaySolo", "GmMemShield", "GmGpuClockLockV1", "GmCacheWarm", "GmManualCorePlanV1", "GmCoreMask", "GmStrictCores", "GmCoreDomainAlt", "GmHeavySqueezeV2", "GmHeavyCoreMaskV1",
            "AutoRecording", "AutoInput", "AutoGraphics", "GmEnglishInputV1", "GmStandbyCleanerV2", "GmPauseUpdate", "GmPauseServices", "NvMaxPerf", "NvLowLat", "NvSmoothMotion",
            "NvShaderCache", "NvRebar", "NvDlss", "AmdAntiLag", "AmdAfmf", "GmIntelEnduranceV1", "GmGpuPowerMax", "GmWsTrimV1", "GmIdlePolicyV1", "AutoBackgroundCoreLayoutV1",
            "AutoLaptopPerformanceV1", "AutoLaptopPerformanceV2", "GmLaptopPerfV1", "GmAggressive", "GmAdaptiveEscalateV1", "PowerPlanOn", "GmRenderLane", "FrameCoordination"
        };

        public bool SuppressFamilyBackground
        {
            get
            {
                string Override;
                if (Overrides.TryGetValue("GameFamilyBackground", out Override))
                {
                    return Override == "1";
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
                string Override;
                if (Overrides.TryGetValue("GameDisableFullscreenOptimizationsV1", out Override))
                {
                    return Override == "1";
                }
                return false;
            }
        }

        public GameProfile Clone()
        {
            GameProfile currentGameProfile = new GameProfile();
            currentGameProfile.Id = Id;
            currentGameProfile.Name = Name;
            currentGameProfile.Root = Root;
            currentGameProfile.ExecutablePath = ExecutablePath;
            currentGameProfile.LearnedExecutablePath = LearnedExecutablePath;
            currentGameProfile.ForceTrigger = ForceTrigger;
            foreach (string entry in Entries)
            {
                currentGameProfile.Entries.Add(entry);
            }
            foreach (KeyValuePair<string, string> @override in Overrides)
            {
                currentGameProfile.Overrides[@override.Key] = @override.Value;
            }
            return currentGameProfile;
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
                foreach (string a in RetiredOverrideKeys)
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
