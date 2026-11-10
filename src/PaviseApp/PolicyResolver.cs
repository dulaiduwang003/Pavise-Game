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
                string cachedValue;
                if (values.TryGetValue(key, out cachedValue))
                {
                    return cachedValue;
                }
                cachedValue = ReadRawGlobalValue(key);
                if (generation == Settings.MutationGeneration)
                {
                    values[key] = cachedValue;
                }
                return cachedValue;
            }

            public void Dispose()
            {
                preferenceReads = previous;
            }
        }

        [ThreadStatic]
        private static PreferenceReadScope preferenceReads;

        internal static IDisposable BeginReadScope()
        {
            return new PreferenceReadScope();
        }

        public static bool SetOverride(GameProfile profile, string key, string value)
        {
            if (profile == null || key == "GmStandbyCleanerV2" || PerformancePolicy.IsRetired(key) || !PerformancePolicy.IsIntent(key) || key == "PerformancePreset")
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

        public static bool HasOverride(GameProfile profile, string key)
        {
            if (profile != null && key != null)
            {
                return profile.Overrides.ContainsKey(key);
            }
            return false;
        }

        public static int CountOverrides(GameProfile profile)
        {
            if (profile != null)
            {
                return profile.Overrides.Count;
            }
            return 0;
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

        public static void Sanitize(GameProfile profile)
        {
            if (profile == null || profile.Overrides.Count == 0 || !ThreadBoostSelection.MigrateOverrides(profile.Overrides))
            {
                return;
            }
            foreach (string currentKeysSnapshot in new List<string>(profile.Overrides.Keys))
            {
                string canonicalResult = (GameProfile.IsRetiredOverrideKey(currentKeysSnapshot) ? null : PolicyCatalog.Canonical(currentKeysSnapshot, profile.Overrides[currentKeysSnapshot]));
                if (canonicalResult == null)
                {
                    profile.Overrides.Remove(currentKeysSnapshot);
                }
                else
                {
                    profile.Overrides[currentKeysSnapshot] = canonicalResult;
                }
            }
        }

        public static List<PolicyDiff> Diff(GameProfile profile)
        {
            List<PolicyDiff> policyDiffs = new List<PolicyDiff>();
            if (profile == null)
            {
                return policyDiffs;
            }
            foreach (PolicyItem all in PolicyCatalog.All)
            {
                string overrideValue;
                if (profile.Overrides.TryGetValue(all.Key, out overrideValue))
                {
                    string globalValueResult = GlobalValue(all.Key);
                    policyDiffs.Add(new PolicyDiff
                    {
                        Key = all.Key,
                        Value = overrideValue,
                        GlobalValue = globalValueResult,
                        SameAsGlobal = string.Equals(overrideValue, globalValueResult, StringComparison.Ordinal)
                    });
                }
            }
            return policyDiffs;
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
            case "GmAutoCoresV2":
            case "GameNvRebarV1":
            case "GameDisableFullscreenOptimizationsV1":
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
            if (key == "GmThreadBoostStrategyV1")
            {
                return ThreadBoostSelection.RawGlobal;
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

        internal static bool HasExplicitGlobal(string key)
        {
            PolicyItem policyItem = PolicyCatalog.ItemOf(key);
            if (policyItem == null)
            {
                return false;
            }
            bool storedEnabled;
            if (policyItem.Kind == PolicyValueKind.Bool)
            {
                return Settings.TryLoadStoredBool(key, out storedEnabled);
            }
            string text;
            if (Settings.TryLoadStr(key, out text))
            {
                return !string.IsNullOrEmpty(text);
            }
            return false;
        }
    }
}
