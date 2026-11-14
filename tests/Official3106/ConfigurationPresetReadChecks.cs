using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static class ConfigurationPresetReadChecks
    {
        private static int checks;
        private static readonly string[] gameTuningKeys = {
            "GmBoost", "GmBoostCpuV1", "GmBoostIoV1", "GmBoostGpuV1", "GmBoostMemoryV1",
            "GmThreadBoostStrategyV1", "GmRenderLane", "FrameCoordination", "GmGameSchedulingV1",
            "GameCriticalThreadHardRuleV1", "GmAutoCoresV2", "GmManualCorePlanV2", "GameFamilyBackground",
            "GameNvRebarV1", "GameDisableFullscreenOptimizationsV1"
        };
        private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        internal static void Main()
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                ConfigurationPresetBackend backend = new ConfigurationPresetBackend(null);
                ConfigurationPresetCoordinator model = new ConfigurationPresetCoordinator(backend);
                foreach (ConfigurationPresetKind preset in new[] { ConfigurationPresetKind.Default, ConfigurationPresetKind.Extreme })
                {
                    foreach (ConfigurationPresetEntry e in ConfigurationPresetCatalog.Entries) Store(e.Key, e.Value(preset));
                    foreach (ConfigurationPresetEntry e in ConfigurationPresetCatalog.Entries) Check(e.Value(preset) == null ? backend.Read(e) != null : backend.Read(e) == e.Value(preset), "production saved-setting mapping " + e.Key);
                    string raw = ConfigurationPresetCoordinator.IdentityPrefix(preset) + model.Fingerprint();
                    Check(backend.SaveIdentity(raw), "identity saved");
                    Check(model.CurrentPreset() == preset && ConfigurationPresetRuntime.CurrentFromSettings() == preset, "current identity matches saved settings");
                    Settings.Save("GmSuppress", false); Check(!ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "covered boolean changes actual identity"); Settings.Save("GmSuppress", true);
                    Settings.Save("EnvAudioIdleV1", false); Check(!ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "partial environment member change changes identity"); Settings.Save("EnvAudioIdleV1", true);
                    Settings.SaveStr("LabCpuIdleStrategyV1", "disabled"); Check(!ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "manual idle disabled not mislabeled extreme"); Store("LabCpuIdleStrategyV1", "default");
                    Check(ConfigurationPresetRuntime.CurrentFromSettings() == preset, "restoring exact saved values restores identity");
                    Settings.Save(CpuMitigationPolicy.ExpandedPreferenceKey, preset != ConfigurationPresetKind.Extreme);
                    Check(!ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "CPU scope change invalidates preset identity even while the bundle remains ON");
                    Settings.Save(CpuMitigationPolicy.ExpandedPreferenceKey, preset == ConfigurationPresetKind.Extreme);
                    Check(ConfigurationPresetRuntime.CurrentFromSettings() == preset, "restoring CPU scope restores exact preset identity");
                }
                Settings.SaveStr("GmSuppress", "invalid"); Check(backend.Read(Entry("GmSuppress")) == null, "malformed boolean is unknown, not assumed default");
                Check(model.Fingerprint() == null && !ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "unreadable mapping invalidates fingerprint");
                Check(Settings.LoadStr("PerformancePreset", "not-set") == "not-set" && Settings.LoadStr("PowerPlanOn", "not-set") == "not-set", "reads do not create strategy or power settings");
                Check(NvDrsTweaks.PresetKindsRestored(new[] { "pstate" }), "no NVIDIA receipts means restored");
                Settings.SaveStr("NvDrsList", "game.exe"); Settings.SaveStr("NvDrs_game.exe", "pstate=0~1;shadercache=1~2");
                Check(!NvDrsTweaks.PresetKindsRestored(new[] { "pstate", "forcep2" }), "pstate receipt blocks false restore success");
                Check(!NvDrsTweaks.PresetKindsRestored(new[] { "shadercache", "shaderon" }), "shader receipt blocks false restore success");
                Check(NvDrsTweaks.PresetKindsRestored(new[] { "prerender" }), "unrelated kinds do not block restore");
                Settings.SaveStr("NvDrs_game.exe", "corrupt"); Check(!NvDrsTweaks.PresetKindsRestored(new[] { "pstate" }), "corrupt receipts cannot impersonate restored");
                Settings.SaveStr("NvDrsList", ""); Settings.SaveStr("NvDrs_game.exe", "shadercache=1~2");
                Check(!NvDrsTweaks.PresetKindsRestored(new[] { "shadercache" }), "orphaned receipt is still detected");
                GameTuningIsolation();
                Console.WriteLine("PASS " + checks + " production preset read checks; transient settings, real PolicyResolver, no GameMode, UI or native eligibility/writes");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        }
        private static ConfigurationPresetEntry Entry(string key)
        {
            foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries) if (entry.Key == key) return entry;
            throw new Exception("Missing preset entry: " + key);
        }

        // 只替换应用端的系统写入，读回和预设协调器仍使用产品实现。
        private sealed class MemoryApplyBackend : IConfigurationPresetBackend, IConfigurationPresetSnapshot
        {
            private readonly ConfigurationPresetBackend reads = new ConfigurationPresetBackend(null);
            internal int Writes;
            public string Read(ConfigurationPresetEntry entry) { return reads.Read(entry); }
            public string ExtraSnapshot() { return reads.ExtraSnapshot(); }
            public bool SaveIdentity(string value) { return reads.SaveIdentity(value); }
            public string LoadIdentity() { return reads.LoadIdentity(); }
            public ConfigurationPresetAvailability Inspect(ConfigurationPresetEntry entry)
            {
                Check(!ConfigurationPresetCatalog.IsGameTuningKey(entry.Key), "preset probed a game-card setting: " + entry.Key);
                return ConfigurationPresetAvailability.Yes();
            }
            public bool Write(ConfigurationPresetEntry entry, string value, Func<bool> cancelled, out string detail)
            {
                Check(!ConfigurationPresetCatalog.IsGameTuningKey(entry.Key), "preset wrote a game-card setting: " + entry.Key);
                if (cancelled != null && cancelled()) { detail = "cancelled"; return false; }
                Writes++;
                Store(entry.Key, value);
                detail = "transient settings only";
                return true;
            }
        }

        private static string GlobalsSnapshot()
        {
            StringBuilder text = new StringBuilder();
            foreach (string key in gameTuningKeys) text.Append(key).Append('=').Append(Settings.LoadStr(key, "<absent>")).Append('\n');
            return text.ToString();
        }

        private static string ProfileSnapshot(GameProfile profile)
        {
            StringBuilder text = new StringBuilder();
            List<string> keys = new List<string>(profile.Overrides.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys) text.Append(key).Append('=').Append(profile.Overrides[key]).Append('\n');
            PolicySnapshot policy = PolicyResolver.For(profile);
            foreach (string key in gameTuningKeys)
                text.Append(key).Append(':').Append(policy.HasOverride(key)).Append(':').Append(policy.RawValueOf(key))
                    .Append(':').Append(policy.ValueOf(key)).Append('\n');
            CriticalThreadRule rule = PolicyResolver.GameSchedulingRule(profile);
            text.Append("effective=").Append(policy.BoostGame).Append('|').Append(policy.GameSchedulingOn)
                .Append('|').Append(rule == null ? "<null>" : rule.Encode()).Append('|').Append(policy.AutoCoresAllowed)
                .Append('|').Append(policy.ManualCores == null ? "<none>" : policy.ManualCores.Encode())
                .Append('|').Append(profile.SuppressFamilyBackground).Append('|').Append(policy.NvRebar)
                .Append('|').Append(profile.DisableFullscreenOptimizations);
            return text.ToString();
        }

        private static GameProfile[] GameProfiles(int globalBoost, int globalScheduling)
        {
            List<GameProfile> profiles = new List<GameProfile>();
            for (int overridden = -1; overridden <= 1; overridden++)
                for (int ruleKind = 0; ruleKind < 3; ruleKind++)
                {
                    GameProfile profile = new GameProfile { Id = "preset-isolation-" + overridden + "-" + ruleKind, Name = "Memory game" };
                    if (overridden >= 0)
                    {
                        foreach (string key in new[] { "GmBoost", "GmGameSchedulingV1", "GmAutoCoresV2", "GameFamilyBackground", "GameNvRebarV1", "GameDisableFullscreenOptimizationsV1" })
                            profile.Overrides[key] = overridden.ToString();
                        profile.Overrides[ManualCorePlan.Key] = new ManualCorePlan(overridden == 0 ? 3UL : 15UL, overridden == 1, "FF,3,C,30,C0").Encode();
                        profile.Overrides[ThreadBoostSelection.Key] = overridden == 0 ? "off" : "frame";
                    }
                    if (ruleKind == 1) profile.Overrides[CriticalThreadRule.Key] = CriticalThreadRule.UnifiedOff().Encode();
                    if (ruleKind == 2) profile.Overrides[CriticalThreadRule.Key] = CriticalThreadRule.UnifiedStrategy(SchedulingStrategy.RenderCore, overridden == 1).Encode();
                    PolicySnapshot policy = PolicyResolver.For(profile);
                    Check(policy.BoostGame == (overridden < 0 ? globalBoost != 0 : overridden == 1), "resource priority fallback fixture is incorrect");
                    Check(policy.GameSchedulingOn == (overridden == 1 && globalScheduling != 0 && ruleKind != 1), "game scheduling opt-in fixture is incorrect");
                    Check(policy.AutoCoresAllowed == (overridden == 1) && profile.SuppressFamilyBackground == (overridden == 1)
                        && policy.NvRebar == (overridden == 1) && profile.DisableFullscreenOptimizations == (overridden == 1), "game-card fixture is incorrect");
                    profiles.Add(profile);
                }
            GameProfile legacy = new GameProfile { Id = "legacy-thread-overrides", Name = "Legacy memory game" };
            legacy.Overrides["GmRenderLane"] = "0";
            legacy.Overrides["FrameCoordination"] = "1";
            profiles.Add(legacy);
            return profiles.ToArray();
        }

        private static void SetGlobalChoice(string key, int value)
        {
            if (value < 0) Settings.Remove(key);
            else Settings.Save(key, value == 1);
        }

        private static void SeedLegacyThreadChoice(int choice)
        {
            Settings.Remove(ThreadBoostSelection.Key);
            Settings.Remove("GmRenderLane");
            Settings.Remove("FrameCoordination");
            if (choice > 0 && choice < 5)
                Settings.SaveStr(ThreadBoostSelection.Key, new[] { "", "off", "on", "candidate", "frame" }[choice]);
            if (choice >= 5)
            {
                Settings.Save("GmRenderLane", ((choice - 5) & 1) != 0);
                Settings.Save("FrameCoordination", ((choice - 5) & 2) != 0);
            }
        }

        private static void GameTuningIsolation()
        {
            foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries)
                Check(!ConfigurationPresetCatalog.IsGameTuningKey(entry.Key), "game-card setting remains in preset catalog: " + entry.Key);
            foreach (string key in gameTuningKeys) Check(ConfigurationPresetCatalog.IsGameTuningKey(key), "game-card key is not protected: " + key);
            int fixture = 0;
            for (int globalBoost = -1; globalBoost <= 1; globalBoost++)
                for (int globalScheduling = -1; globalScheduling <= 1; globalScheduling++)
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries) Store(entry.Key, entry.DefaultValue);
                    SetGlobalChoice("GmBoost", globalBoost);
                    SetGlobalChoice(PolicyCatalog.KeyGameScheduling, globalScheduling);
                    SeedLegacyThreadChoice(fixture++);
                    foreach (string key in new[] { "GmBoostCpuV1", "GmBoostIoV1", "GmBoostGpuV1", "GmBoostMemoryV1", "GmAutoCoresV2", "GameFamilyBackground", "GameNvRebarV1", "GameDisableFullscreenOptimizationsV1" })
                        Settings.Save(key, fixture % 2 == 0);
                    Settings.SaveStr(ManualCorePlan.Key, new ManualCorePlan(3, true, "FF,3,C,30,C0").Encode());
                    Settings.SaveStr(CriticalThreadRule.Key, CriticalThreadRule.UnifiedStrategy(SchedulingStrategy.DependencyBoost, false).Encode());
                    GameProfile[] profiles = GameProfiles(globalBoost, globalScheduling);
                    string globals = GlobalsSnapshot();
                    string[] before = new string[profiles.Length];
                    for (int i = 0; i < profiles.Length; i++) before[i] = ProfileSnapshot(profiles[i]);
                    MemoryApplyBackend backend = new MemoryApplyBackend();
                    ConfigurationPresetCoordinator model = new ConfigurationPresetCoordinator(backend);
                    foreach (ConfigurationPresetKind preset in new[] { ConfigurationPresetKind.Default, ConfigurationPresetKind.Default,
                        ConfigurationPresetKind.Extreme, ConfigurationPresetKind.Extreme, ConfigurationPresetKind.Default })
                    {
                        ConfigurationPresetResult result = model.Apply(preset, () => false);
                        Check(result.Failed == 0 && result.Pending == 0 && result.Skipped == 0 && !result.Cancelled
                            && result.Items.Count == ConfigurationPresetCatalog.Entries.Length, "memory preset application did not complete");
                        Check(result.Identity == ConfigurationPresetCoordinator.IdentityPrefix(preset) + model.Fingerprint()
                            && model.CurrentPreset() == preset && ConfigurationPresetRuntime.CurrentFromSettings() == preset, "applied preset identity disagrees with production saved reads");
                        Check(GlobalsSnapshot() == globals, "preset changed or created a game-card global fallback");
                        for (int i = 0; i < profiles.Length; i++)
                            Check(ProfileSnapshot(profiles[i]) == before[i], "preset changed game-card raw values, inheritance or effective policy: " + profiles[i].Id);
                    }
                    Check(backend.Writes == ConfigurationPresetCatalog.Entries.Length * 5, "same-value apply or A-B-A did not exercise the complete catalog");
                    GameCardEditsPreserveIdentity(model, profiles);
                }
        }

        private static void GameCardEditsPreserveIdentity(ConfigurationPresetCoordinator model, GameProfile[] profiles)
        {
            string fingerprint = model.Fingerprint(), phase = ConfigurationPresetBackend.SavedIdentityPhase();
            foreach (string key in gameTuningKeys)
            {
                Check(phase.IndexOf(key + "=", StringComparison.Ordinal) < 0, "game-card value remains in preset identity phase: " + key);
                string value = key == ThreadBoostSelection.Key ? "off" : key == CriticalThreadRule.Key ? CriticalThreadRule.UnifiedOff().Encode()
                    : key == ManualCorePlan.Key ? new ManualCorePlan(15, false, "FF,3,C,30,C0").Encode() : Settings.LoadStr(key, "0") == "1" ? "0" : "1";
                Settings.SaveStr(key, value);
                foreach (GameProfile profile in profiles) profile.Overrides[key] = value;
                Check(model.Fingerprint() == fingerprint && ConfigurationPresetBackend.SavedIdentityPhase() == phase
                    && model.CurrentPreset() == ConfigurationPresetKind.Default && ConfigurationPresetRuntime.CurrentFromSettings() == ConfigurationPresetKind.Default,
                    "editing a game-card setting invalidated the independent preset: " + key);
            }
            Settings.Save("GmSuppress", false);
            Check(model.Fingerprint() != fingerprint && ConfigurationPresetBackend.SavedIdentityPhase() != phase
                && !model.CurrentPreset().HasValue && !ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "covered background setting failed to invalidate identity");
            Settings.Save("GmSuppress", true);
            Check(model.Fingerprint() == fingerprint && ConfigurationPresetRuntime.CurrentFromSettings() == ConfigurationPresetKind.Default, "restoring covered settings did not restore identity");
            string identity = ConfigurationPresetCoordinator.IdentityPrefix(ConfigurationPresetKind.Default) + fingerprint;
            foreach (string oldPrefix in new[] { "4|default|", "2|extreme|" })
            {
                Settings.SaveStr(ConfigurationPresetRuntime.IdentityKey, oldPrefix + fingerprint);
                Check(!model.CurrentPreset().HasValue && !ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "old preset definition accepted a new fingerprint");
            }
            Settings.SaveStr(ConfigurationPresetRuntime.IdentityKey, identity);
        }

        private static void Store(string key, string value)
        {
            if (value == null) return;
            if (key == PerformanceTuning.EnvironmentBundleKey)
            { foreach (string member in new[] { "EnvCpuMitigationsV1", "EnvNtfsLastAccessV1", "EnvAudioIdleV1", "EnvVbsDisabledV1" }) Settings.Save(member, value == "1"); }
            else if (key == PerformanceActionCatalog.CpuMitigations)
            { Settings.Save(key, value != "0"); Settings.Save(CpuMitigationPolicy.ExpandedPreferenceKey, value == "1"); }
            else if (key == PerformanceTuning.StorageGroupKey)
            { foreach (string member in PerformanceTuning.StorageMembers()) Settings.Save(member, value == "1"); }
            else if (key == PerformanceActionCatalog.Quantum)
            { Settings.Save(key, value != "0"); Settings.SaveStr("EnvQuantumSelectedProfileV1", value); }
            else if (PrivacyControl.IsAction(key)) Settings.SaveStr(PrivacyControl.PreferencePrefix + key, value);
            else Settings.SaveStr(key, value);
        }
    }
}
