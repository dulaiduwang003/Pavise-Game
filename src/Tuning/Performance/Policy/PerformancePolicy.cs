using System;

namespace PaviseApp
{
    internal static class PerformancePolicy
    {
        internal const string VersionKey = "AutomaticPolicyV1";

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
            case "GmDwmBoost":
            // 计时器节拍实测和公开测试都没有可测收益 已下线 只按记录恢复启动配置
            case "AutoTimerTick":
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
            case "AutoGlobalTimer":
                return true;
            }
        }

        internal static bool IsIndependentManual(string key)
        {
            switch (key)
            {
            case "EnvCpuMitigationsV1":
            case "EnvAudioIdleV1":
            case "EnvVbsDisabledV1":
            case "AutoGlobalTimer":
                return true;
            default:
                return false;
            }
        }

        internal static bool EnvironmentPreference(string key)
        {
            // 已下线的永远不再想要 现有的恢复流程会把留下的改动还原
            if (IsRetired(key))
            {
                return false;
            }
            bool value;
            if (Settings.TryLoadStoredBool(key, out value))
            {
                return value;
            }
            if (key != "AutoGlobalTimer")
            {
                return false;
            }
            return GlobalTimerResTweak.OwnsState;
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
            case AntiCheatSuppressionPolicy.Key:
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
            return Lang.Text(zh, en);
        }

        internal static string Resolve(string key, Func<string, string> raw)
        {
            if (key == null)
            {
                return null;
            }
            if (IsRetired(key))
            {
                if (key != "GmThreadBoostStrategyV1")
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
            // 下载暂停与自动维护限制都随对局写入 退局恢复 由后台治理统一控制 不单独保存开关
            if (key == "GmPauseDl" || key == "GmPauseMaintenanceV1") return PolicyResolver.GlobalValue("GmSuppress") == "1" ? Resolve("GmSuppress", raw) : "0";
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
