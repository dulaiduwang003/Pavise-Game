using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class PolicySnapshot
    {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly HashSet<string> overridden = new HashSet<string>(StringComparer.Ordinal);
        private readonly bool gameSchedulingRuleOff;
        // 逐游戏快照 全局快照不套用逐游戏的默认值
        private readonly bool gameScoped;

        public readonly string ProfileId;

        public readonly string ProfileName;

        internal readonly ManualCorePlan ManualCores;

        public int OverrideCount
        {
            get
            {
                return overridden.Count;
            }
        }

        public bool IsGlobal
        {
            get
            {
                return overridden.Count == 0;
            }
        }

        public PerformancePreset Preset
        {
            get
            {
                int result;
                if (!int.TryParse(ValueOf("PerformancePreset"), out result))
                {
                    return PerformancePreset.Standard;
                }
                return PresetValue.From(result);
            }
        }

        public bool SuppressBackground
        {
            get
            {
                if (PolicyResolver.GlobalValue("GmSuppress") == "1")
                {
                    return On("GmSuppress");
                }
                return false;
            }
        }

        // 性能优先级（GmBoost）只有逐游戏开关 默认开 没有在深度调优里单独关掉的游戏都开着 不读全局保存值
        public bool BoostGame
        {
            get
            {
                return GameSwitchOn(PolicyCatalog.KeyBoost);
            }
        }

        // 只看这款游戏单独保存的值 没有保存过 或不是某款游戏的快照时 按开处理
        private bool GameSwitchOn(string key)
        {
            string value;
            if (!gameScoped || !values.TryGetValue(key, out value)) return true;
            return key == ThreadBoostSelection.Key ? ThreadBoostSelection.IsOn(value) : value == "1";
        }

        public bool GameSchedulingOn
        {
            get
            {
                // 总开关关闭时 导入的逐游戏开启值也不能重新放行运行期控制
                return !gameSchedulingRuleOff && PolicyResolver.GlobalValue(PolicyCatalog.KeyGameScheduling) == "1" && On(PolicyCatalog.KeyGameScheduling);
            }
        }

        internal int BackgroundGovernanceMode
        {
            get { return BackgroundGovernancePolicy.Parse(ValueOf(PolicyCatalog.KeyBackgroundGovernance)); }
        }

        internal bool BackgroundBoostLimitRequested
        {
            get { return BackgroundGovernancePolicy.WantsBackgroundBoostLimit(SuppressBackground, BackgroundGovernanceMode); }
        }

        public bool WideBackgroundScope
        {
            get
            {
                if (SuppressBackground)
                {
                    return PerformancePolicy.SceneUsesWideBackgroundScope(Preset);
                }
                return false;
            }
        }

        public bool AdaptiveEscalation
        {
            get
            {
                if (SuppressBackground)
                {
                    return PerformancePolicy.SceneUsesAdaptiveEscalation(Preset);
                }
                return false;
            }
        }

        public bool ProcessorTuning
        {
            get
            {
                if (PowerPlanOn)
                {
                    return PerformancePolicy.SceneTunesProcessor(Preset);
                }
                return false;
            }
        }

        public bool ThreadBoostOn
        {
            get
            {
                // 游戏线程提优和性能优先级一样 只有逐游戏开关 默认开
                return GameSwitchOn(ThreadBoostSelection.Key);
            }
        }

        public bool RenderLane
        {
            get
            {
                return ThreadBoostOn;
            }
        }

        public bool FrameCoordinationOn
        {
            get
            {
                return ThreadBoostOn;
            }
        }

        public bool PowerPlanOn
        {
            get
            {
                return !PowerPlan.NoSwitchSelected;
            }
        }

        public bool StandbyCleaner
        {
            get
            {
                return On("GmStandbyCleanerV2");
            }
        }

        public bool PauseDownloads
        {
            get
            {
                return SuppressBackground;
            }
        }

        public bool PauseUpdate
        {
            get
            {
                return On("GmPauseUpdate");
            }
        }

        public bool PauseMaintenance
        {
            get
            {
                return SuppressBackground;
            }
        }

        public bool PauseServices
        {
            get
            {
                return false;
            }
        }

        public bool Awake
        {
            get
            {
                return true;
            }
        }

        public bool AutoCoresAllowed
        {
            get
            {
                if (ManualCores != null)
                {
                    return On("GmAutoCoresV2");
                }
                return false;
            }
        }

        public bool GpuPowerMax
        {
            get
            {
                return false;
            }
        }

        public bool NvMaxPerf
        {
            get
            {
                return On("NvMaxPerf");
            }
        }

        public bool NvShaderCacheMax
        {
            get
            {
                return On("NvShaderCache");
            }
        }

        public bool NvRebar
        {
            get
            {
                return On("GameNvRebarV1");
            }
        }

        public string NvDlssMode
        {
            get
            {
                return "off";
            }
        }

        public bool AmdAntiLag
        {
            get
            {
                return false;
            }
        }

        public bool AmdAfmf
        {
            get
            {
                return false;
            }
        }

        public bool GlobalTimerLease
        {
            get
            {
                return On("AutoGlobalTimer");
            }
        }

        public bool EffSuppress
        {
            get
            {
                return SuppressBackground;
            }
        }

        public bool EffBoost
        {
            get
            {
                return BoostGame;
            }
        }

        public bool EffLane
        {
            get
            {
                return RenderLane;
            }
        }

        internal PolicySnapshot(GameProfile profile)
        {
            if (profile == null)
            {
                return;
            }
            gameScoped = true;
            CriticalThreadRule savedSchedulingRule = CriticalThreadRule.ParseForRuntime(CriticalThreadRule.Raw(profile));
            gameSchedulingRuleOff = savedSchedulingRule != null && savedSchedulingRule.IsOff;
            ManualCores = ManualCorePlan.Parse(ManualCorePlan.Raw(profile));
            if (ManualCores != null)
            {
                overridden.Add("GmManualCorePlanV2");
            }
            Dictionary<string, string> dictionary = new Dictionary<string, string>(profile.Overrides, StringComparer.Ordinal);
            if (!ThreadBoostSelection.MigrateOverrides(dictionary))
            {
                dictionary["GmThreadBoostStrategyV1"] = "off";
            }
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                string value;
                if (item.Key != "PerformancePreset" && item.Key != "PowerPlanOn" && dictionary.TryGetValue(item.Key, out value))
                {
                    string canonicalResult = PolicyCatalog.Canonical(item.Key, value);
                    values[item.Key] = canonicalResult ?? item.Fallback;
                    overridden.Add(item.Key);
                }
            }
            ProfileId = profile.Id;
            ProfileName = profile.Name;
        }

        public bool HasOverride(string key)
        {
            if (key != null)
            {
                return overridden.Contains(key);
            }
            return false;
        }

        public string ValueOf(string key)
        {
            if (key == null)
            {
                return null;
            }
            if (PerformancePolicy.IsRetired(key))
            {
                return "0";
            }
            string resolveResult = PerformancePolicy.Resolve(key, RawValueOf);
            if (resolveResult != null)
            {
                return resolveResult;
            }
            PolicyItem policyItem = PolicyCatalog.ItemOf(key);
            if (policyItem != null)
            {
                return policyItem.Fallback;
            }
            return null;
        }

        internal string RawValueOf(string key)
        {
            if (PerformancePolicy.IsRetired(key))
            {
                return "0";
            }
            if (key == "PowerPlanOn")
            {
                return PolicyResolver.RawGlobalValue(key);
            }
            string value;
            if (!values.TryGetValue(key, out value))
            {
                // 渲染线程亲和性逐游戏默认关闭 没有显式开启值的游戏不继承全局值 全局值只是总开关 由 GameSchedulingOn 另行核对
                if (gameScoped && key == PolicyCatalog.KeyGameScheduling) return "0";
                // 性能优先级和游戏线程提优没有全局开关 没单独保存过就是默认开
                if (key == PolicyCatalog.KeyBoost || key == ThreadBoostSelection.Key) return PolicyCatalog.ItemOf(key).Fallback;
                return PolicyResolver.RawGlobalValue(key);
            }
            return value;
        }

        private bool On(string key)
        {
            return ValueOf(key) == "1";
        }
    }
}
