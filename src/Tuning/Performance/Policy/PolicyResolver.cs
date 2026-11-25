using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class PolicyResolver
    {
        private sealed class PreferenceReadScope : IDisposable
        {
            private readonly PreferenceReadScope previous;

            private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

            private int generation = -1;

            internal PreferenceReadScope()
            {
                previous = preferenceReads;
                preferenceReads = this;
            }

            internal string Read(string key)
            {
                int mutationGeneration = Settings.MutationGeneration;
                if (generation != mutationGeneration)
                {
                    values.Clear();
                    generation = mutationGeneration;
                }
                string value;
                if (values.TryGetValue(key, out value))
                {
                    return value;
                }
                value = ReadRawGlobalValue(key);
                if (generation == Settings.MutationGeneration)
                {
                    values[key] = value;
                }
                return value;
            }

            public void Dispose()
            {
                preferenceReads = previous;
            }
        }

        [ThreadStatic]
        private static PreferenceReadScope preferenceReads;
#if PAVISE_SELFTEST
        internal static Action<string> RawReadForTest;
#endif

        internal static IDisposable BeginReadScope()
        {
            return new PreferenceReadScope();
        }

        public static bool SetOverride(GameProfile profile, string key, string value)
        {
            // 性能优先级和游戏线程提优只有逐游戏开关 在深度调优里设置 没有单独保存过的游戏默认开
            bool perGame = PerformancePolicy.IsIntent(key) || key == PolicyCatalog.KeyBoost || key == ThreadBoostSelection.Key;
            if (profile == null || key == "GmStandbyCleanerV2" || PerformancePolicy.IsRetired(key) || !perGame || key == "PerformancePreset")
            {
                return false;
            }
            string canonicalResult = PolicyCatalog.Canonical(key, value);
            if (canonicalResult == null)
            {
                return false;
            }
            profile.Overrides[key] = canonicalResult;
            return true;
        }

        public static string Read(GameProfile profile, string key)
        {
            if (PerformancePolicy.IsRetired(key))
            {
                return "0";
            }
            return For(profile).ValueOf(key);
        }

        public static int ClearAllOverrides(GameProfile profile)
        {
            if (profile == null)
            {
                return 0;
            }
            int count = profile.Overrides.Count;
            profile.Overrides.Clear();
            return count;
        }

        public static PolicySnapshot For(GameProfile profile)
        {
            return new PolicySnapshot(profile);
        }

        public static PolicySnapshot Global()
        {
            return new PolicySnapshot(null);
        }

        internal static CriticalThreadRule GameSchedulingRule(GameProfile profile)
        {
            // 没有游戏档案时不按全局值放行 逐游戏是否开启见 PolicySnapshot.GameSchedulingOn
            if (profile == null) return CriticalThreadRule.UnifiedOff();
            if (!For(profile).GameSchedulingOn) return CriticalThreadRule.UnifiedOff();
            return StoredSchedulingRule(profile);
        }

        // 读取运行规则 不看开关 未保存模式和旧布局按关闭处理
        internal static CriticalThreadRule StoredSchedulingRule(GameProfile profile)
        {
            string raw = CriticalThreadRule.Raw(profile);
            return raw == null || !raw.StartsWith("7|", StringComparison.Ordinal)
                ? CriticalThreadRule.UnifiedOff() : CriticalThreadRule.ParseForRuntime(raw);
        }

        internal static string GlobalValue(string key)
        {
            if (PerformancePolicy.IsRetired(key))
            {
                return "0";
            }
            return PerformancePolicy.Resolve(key, RawGlobalValue);
        }

        internal static string RawGlobalValue(string key)
        {
            if (PerformancePolicy.IsRetired(key))
            {
                return "0";
            }
            if (key == "PowerPlanOn")
            {
                if (!PowerPlan.NoSwitchSelected)
                {
                    return "1";
                }
                return "0";
            }
            if (PerformancePolicy.IsGamePriorityOption(key))
            {
                return RawGlobalValue("GmBoost");
            }
            switch (key)
            {
            case "GameFamilyBackground":
            case AntiCheatSuppressionPolicy.Key:
            case "GmAutoCoresV2":
            case "GameNvRebarV1":
            case PolicyCatalog.KeyGameLowLatency:
            case "GameDisableFullscreenOptimizationsV1":
            case PolicyCatalog.KeyGameControlFlowGuard:
                return "0";
            default:
                if (preferenceReads != null)
                {
                    return preferenceReads.Read(key);
                }
                return ReadRawGlobalValue(key);
            }
        }

        private static string ReadRawGlobalValue(string key)
        {
#if PAVISE_SELFTEST
            if (RawReadForTest != null) RawReadForTest(key);
#endif
            if (key == "GmThreadBoostStrategyV1")
            {
                return ThreadBoostSelection.RawGlobal;
            }
            if (key == PolicyCatalog.KeyBackgroundGovernance)
            {
                return BackgroundGovernancePolicy.Text(BackgroundGovernancePolicy.Load());
            }
            PolicyItem policyItem = PolicyCatalog.ItemOf(key);
            if (policyItem == null)
            {
                return null;
            }
            switch (policyItem.Kind)
            {
            case PolicyValueKind.Bool:
                if (!Settings.Load(policyItem.Key, policyItem.Fallback == "1"))
                {
                    return "0";
                }
                return "1";
            case PolicyValueKind.Enum:
            case PolicyValueKind.Choice:
                return PolicyCatalog.Canonical(policyItem.Key, Settings.LoadStr(policyItem.Key, policyItem.Fallback));
            default:
                return PolicyCatalog.Canonical(policyItem.Key, Settings.LoadStr(policyItem.Key, policyItem.Fallback));
            }
        }
    }
}
