using System;

namespace PaviseApp
{
    internal static class PerformancePolicy
    {
        internal const string VersionKey = "AutomaticPolicyV1";

        internal const string GameLowLatencyKey = "GameLowLatencyOn";

        internal static bool GameLowLatency
        {
            get
            {
                return PolicyResolver.RawGlobalValue("NvLowLat") == "on";
            }
        }

        internal static bool IsLowLatency(string key)
        {
            return key == "NvLowLat";
        }

        internal static bool IsRetired(string key)
        {
            switch (key)
            {
            default:
                if (!ThreadBoostSelection.IsLegacy(key))
                {
                    return IsRetiredSceneRule(key);
                }
                break;
            case "AmdAntiLag":
            case "GmGpuPowerMax":
            case "GmWsTrimV1":
            case "GmLaptopPerfV1":
            case "GmIdlePolicyV1":
            case "GmPauseServices":
            case "AutoLaptopPerformanceV1":
            case "AutoBackgroundCoreLayoutV1":
            case "MatchVisualEffectsV1":
            case "GmBoostMemoryV1":
                break;
            }
            return true;
        }

        internal static bool IsRetiredSceneRule(string key)
        {
            if (key != "GmAggressive")
            {
                return key == "GmAdaptiveEscalateV1";
            }
            return true;
        }

        internal static bool IsEnvironment(string key)
        {
            switch (key)
            {
            default:
                return key == "GmStandbyCleanerV2";
            case "AutoTimerTick":
            case "AutoGlobalTimer":
            case "GmCacheWarmV2":
                return true;
            }
        }

        internal static bool IsIndependentManual(string key)
        {
            switch (key)
            {
            case "EnvCpuMitigationsV1":
            case "EnvNtfsLastAccessV1":
            case "EnvQuantumProfileV1":
            case "EnvStorageIdleV1":
            case "EnvStorageRuntimeV1":
            case "EnvStorageD3ColdV1":
            case "EnvAudioIdleV1":
            case "EnvPrivacyNtfsMemoryV1":
            case "EnvVbsDisabledV1":
            case "AutoTimerTick":
            case "AutoGlobalTimer":
            case "GmMmcss":
            case "Hags":
            case "AmdSam":
                return true;
            default:
                return false;
            }
        }

        internal static bool EnvironmentPreference(string key)
        {
            bool value;
            if (Settings.TryLoadStoredBool(key, out value))
            {
                return value;
            }
            if (key != "AutoTimerTick")
            {
                if (key != "AutoGlobalTimer")
                {
                    return false;
                }
                return GlobalTimerResTweak.OwnsState;
            }
            return TimerTickTweak.OwnsState;
        }

        internal static bool IsIntent(string key)
        {
            switch (key)
            {
            default:
                return key == "GameDisableFullscreenOptimizationsV1";
            case "PerformancePreset":
            case "GmAutoCoresV2":
            case "GameFamilyBackground":
            case "GameNvRebarV1":
                return true;
            }
        }

        internal static bool IsGamePriorityOption(string key)
        {
            switch (key)
            {
            default:
                return key == "GmBoostGpuV1";
            case "GmBoostCpuV1":
            case "GmBoostIoV1":
                return true;
            }
        }

        internal static bool UsesFrameCoordination(PerformancePreset mode)
        {
            return PresetValue.IsValid((int)mode);
        }

        internal static bool SceneUsesWideBackgroundScope(PerformancePreset mode)
        {
            return PresetValue.IsCompetitive(mode);
        }

        internal static bool SceneUsesAdaptiveEscalation(PerformancePreset mode)
        {
            return mode == PerformancePreset.Standard;
        }

        internal static bool SceneTunesProcessor(PerformancePreset mode)
        {
            return mode == PerformancePreset.Standard || mode == PerformancePreset.Competitive;
        }

        internal static string Text(string zh, string en)
        {
            if (Lang.Cur != 0)
            {
                return en;
            }
            return zh;
        }

        internal static string Resolve(string key, Func<string, string> raw)
        {
            if (key == null)
            {
                return null;
            }
            if (IsRetired(key))
            {
                if (!IsLowLatency(key) && key != "GmThreadBoostStrategyV1")
                {
                    return "0";
                }
                return "off";
            }
            if (IsGamePriorityOption(key))
            {
                return Resolve("GmBoost", raw);
            }
            if (key == "GmAwake") return "1";
            if (key == "GmPauseDl" || key == "GmGpuDemote") return PolicyResolver.GlobalValue("GmSuppress") == "1" ? Resolve("GmSuppress", raw) : "0";
            if (key == "PowerPlanOn")
            {
                if (!PowerPlan.NoSwitchSelected)
                {
                    return "1";
                }
                return "0";
            }
            if (IsEnvironment(key))
            {
                if (!EnvironmentPreference(key))
                {
                    return "0";
                }
                return "1";
            }
            return raw(key);
        }

        internal static string Reason(string key)
        {
            return Text("按您的手动选择和适用条件执行，恢复规则保持不变", "Follows your manual choice and eligibility conditions; restoration rules are unchanged");
        }
    }
}
