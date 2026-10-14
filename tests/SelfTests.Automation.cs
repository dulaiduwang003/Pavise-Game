#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void RetiredPreferencesRejected(string root)
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save(AutomaticPolicy.VersionKey, true);
            var profile = new GameProfile { Id = "retired-preferences", Name = "Retired preference fixture" };
            var oldRecords = new List<string>();
            Func<string, string> encode = delegate(string value) { return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)); };
            foreach (string key in new[] { "AutoInput", "AutoRecording", "AutoGraphics",
                PolicyCatalog.KeyEnglishInput, PolicyCatalog.KeyStandbyCleaner, PolicyCatalog.KeyPauseServices,
                PolicyCatalog.KeyPauseUpdate, PolicyCatalog.KeyNvMaxPerf, PolicyCatalog.KeyNvLowLat,
                PolicyCatalog.KeyNvSmoothMotion, PolicyCatalog.KeyNvShaderCache, PolicyCatalog.KeyNvRebar,
                PolicyCatalog.KeyNvDlss, PolicyCatalog.KeyAmdAntiLag, PolicyCatalog.KeyAmdAfmf,
                PolicyCatalog.KeyIntelEndurance, PolicyCatalog.KeyGpuPowerMax })
            {
                Settings.SaveStr(key, "1"); profile.Overrides[key] = "1";
                bool automaticAgain = key == PolicyCatalog.KeyPauseServices || key == PolicyCatalog.KeyNvMaxPerf
                    || key == PolicyCatalog.KeyNvShaderCache || key == PolicyCatalog.KeyPauseUpdate || key == PolicyCatalog.KeyIntelEndurance
                    || key == PolicyCatalog.KeyNvLowLat || key == PolicyCatalog.KeyAmdAntiLag || key == PolicyCatalog.KeyStandbyCleaner;
                Eq(!automaticAgain, PolicyCatalog.ItemOf(key) == null);
                Eq(false, PolicyResolver.SetOverride(profile, key, "1"));
                Eq(true, GameProfile.IsRetiredOverrideKey(key));
                oldRecords.Add("O|" + encode(profile.Id) + "|" + encode(key) + "|" + encode("1"));
            }
            Settings.SaveStr("AutoInput", "english"); Settings.SaveStr("AutoRecording", "unused");
            Settings.SaveStr("AutoGraphics", "smooth"); Settings.Save("GameDvrOff", true);
            PolicyResolver.Sanitize(profile); Eq(0, profile.Overrides.Count);
            string libraryDir = Path.Combine(root, "retired-profile-load-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(libraryDir);
            var store = new GameProfileStore(libraryDir);
            Eq(true, store.Save(new[] { profile }));
            File.AppendAllLines(Path.Combine(libraryDir, GameProfileStore.FileName), oldRecords, new System.Text.UTF8Encoding(false));
            var reload = new GameProfileStore(libraryDir);
            var loaded = reload.LoadProfiles();
            Eq(false, reload.LoadFailed); Eq(false, reload.SaveFailed);
            Eq(1, loaded.Count); Eq(profile.Name, loaded[0].Name); Eq(0, loaded[0].Overrides.Count);
            Eq(true, reload.Save(loaded));
            Eq(true, PolicyResolver.SetOverride(profile, PolicyCatalog.KeyGameRebar, "1"));
            Settings.Save(PolicyCatalog.KeyGameRebar, true);
            Eq(false, PolicyResolver.Global().NvRebar);
            Eq(false, PolicyResolver.For(new GameProfile()).NvRebar);
            foreach (string preset in new[] { "0", "1", "6", "4" })
            {
                Settings.SaveStr(PolicyCatalog.KeyPreset, preset);
                Eq(true, PolicyResolver.For(profile).NvRebar);
            }
            Eq(true, store.Save(new[] { profile }));
            var rebarLoaded = new GameProfileStore(libraryDir).LoadProfiles();
            Eq(true, PolicyResolver.For(rebarLoaded[0]).NvRebar);
            var rebarMode = new GameMode(libraryDir, new SuppressionCore());
            rebarMode.ProbeSessionPolicyApply(rebarLoaded[0]);
            Eq(true, rebarMode.SetProfileOverride(profile.Id, PolicyCatalog.KeyGameRebar, "0"));
            Eq(false, rebarMode.AutomationPolicySnapshot.NvRebar);
            Eq(false, PolicyResolver.For(new GameProfileStore(libraryDir).LoadProfiles()[0]).NvRebar);
            Eq(true, rebarMode.SetProfileOverride(profile.Id, PolicyCatalog.KeyGameRebar, "1"));
            Eq(true, rebarMode.AutomationPolicySnapshot.NvRebar);
            Eq(true, rebarMode.ClearProfileOverrides(profile.Id) > 0);
            Eq(false, rebarMode.AutomationPolicySnapshot.NvRebar);
            var rebarStore = (GameProfileStore)typeof(GameMode).GetField("profileStore", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rebarMode);
            rebarStore.MarkSaveFailed(new IOException("ReBAR fixture save denied"));
            Eq(false, rebarMode.SetProfileOverride(profile.Id, PolicyCatalog.KeyGameRebar, "1"));
            Eq(false, PolicyResolver.For(rebarMode.GetProfiles()[0]).NvRebar);
            Eq(false, rebarMode.AutomationPolicySnapshot.NvRebar);
            var mode = new GameMode(root, new SuppressionCore());
            mode.InitializeAutomation();
            foreach (string preset in new[] { "0", "1", "4" })
            {
                Settings.SaveStr(PolicyCatalog.KeyPreset, preset);
                typeof(GameMode).GetMethod("SyncAutomationPolicy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode, null);
                foreach (string field in new[] { "englishInputOn",
                    "nvSmoothMotion", "nvRebarOn", "amdAntiLag",
                    "amdAfmf", "intelEnduranceOn" })
                    Eq(false, (bool)typeof(GameMode).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode));
                Eq(true, mode.KillGameDvr);
                Eq(true, (bool)typeof(GameMode).GetField("mmcssOn", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode));
                Eq("off", mode.AutomationPolicySnapshot.NvDlssMode);
                Eq(true, mode.AutomationPolicySnapshot.PauseUpdate);
                Eq(true, (bool)typeof(GameMode).GetField("pauseUpdateOn", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode));
                Eq("off", mode.AutomationPolicySnapshot.NvLowLatMode);
                Eq(preset == "1", mode.PauseServices);
                Eq(preset != "4", mode.NvMaxPerf);
                Eq(true, mode.NvShaderCacheMax);
            }
            Settings.UseTransientStoreForCurrentProcess();
        }
        private static void RunAutomaticPolicyTests()
        {
            Eq("lib.rebar.driver", RebarProbe.EnableBlockReason(false, true, true, true));
            Eq("lib.rebar.unknown", RebarProbe.EnableBlockReason(true, false, true, false));
            Eq("lib.rebar.unknown", RebarProbe.EnableBlockReason(true, true, false, true));
            Eq("lib.rebar.bios", RebarProbe.EnableBlockReason(true, true, true, false));
            Eq(true, RebarProbe.EnableBlockReason(true, true, true, true) == null);
            Eq(false, RebarProbe.EnabledFromWindow(256UL * 1024 * 1024));
            Eq(true, RebarProbe.EnabledFromWindow(1024UL * 1024 * 1024));
            RunExtremeModeTests();
            RunGameLowLatencyPreferenceTests();
            RunGameDvrPreferenceTests();
            Settings.Save(AutomaticPolicy.VersionKey, true);
            foreach (PolicyItem item in PolicyCatalog.All)
                if (!AutomaticPolicy.IsIntent(item.Key)) Settings.SaveStr(item.Key, item.Kind == PolicyValueKind.Bool ? "0" : "off");
            var profile = new GameProfile { Id = "automatic-test", Name = "Automatic test" };
            profile.Overrides[PolicyCatalog.KeyPreset] = "1";
            profile.Overrides[PolicyCatalog.KeySuppress] = "0";
            profile.Overrides[PolicyCatalog.KeyBoost] = "0";
            foreach (string mode in new[] { "0", "1", "6", "4" })
            {
                Settings.SaveStr(PolicyCatalog.KeyPreset, mode);
                PolicySnapshot policy = PolicyResolver.For(profile);
                Eq(PresetValue.From(int.Parse(mode)), policy.Preset);
                Eq(true, policy.SuppressBackground); Eq(true, policy.BoostGame);
                Eq(true, policy.Awake); Eq(true, policy.PauseMaintenance);
                Eq(false, policy.AutoCoresAllowed); Eq(false, policy.EnglishInput);
                Eq(false, policy.StandbyCleaner); Eq(mode == "1" || mode == "6", policy.PauseServices); Eq(true, policy.PauseUpdate);
                Eq(mode == "6", policy.DwmBoost); Eq(mode == "6", policy.IntelEnduranceOff);
                Eq(mode == "6", policy.IdlePolicy);
                Eq(mode != "4", policy.NvMaxPerf); Eq(true, policy.NvShaderCacheMax);
                Eq(false, policy.NvRebar); Eq(false, policy.NvSmoothMotion);
                Eq("off", policy.NvDlssMode);
                Eq(false, policy.CacheWarm); Eq(mode != "4", policy.WsTrim);
            }
            Eq(false, PolicyResolver.SetOverride(profile, PolicyCatalog.KeySuppress, "0"));
            Eq(false, PolicyResolver.SetOverride(profile, PolicyCatalog.KeyPreset, "1"));
            PolicyResolver.Sanitize(profile);
            Eq(false, profile.Overrides.ContainsKey(PolicyCatalog.KeyPreset));
            Eq(true, PolicyResolver.SetOverride(profile, PolicyCatalog.KeyAutoCores, "1"));
            Eq(false, PolicyResolver.For(profile).AutoCoresAllowed);
            Settings.SaveStr(PolicyCatalog.KeyPreset, "1");
            Eq(true, PowerPlan.SelectPlan(PowerPlan.ManagedChoice)); Eq(true, PolicyResolver.Global().PowerPlanOn);
            Eq(null, PolicyCatalog.ItemOf("GmPowerYield"));
            Eq(null, PolicyCatalog.ItemOf("GmVramShield"));
            Eq(true, GameProfile.IsRetiredOverrideKey("GmVramShield"));
            Eq(null, typeof(GameMode).Assembly.GetType("PaviseApp.VramShield"));
            Eq(null, typeof(Native).GetMethod("D3DKMTChangeVideoMemoryReservation"));
            Eq(true, GameProfile.IsRetiredOverrideKey("GmPowerYield"));
            Eq(null, typeof(GameMode).Assembly.GetType("PaviseApp.PowerBudgetYieldRunner"));
            Eq(null, typeof(PowerPlan).GetMethod("TryYieldEpp", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
            string localPlan = PowerPlan.BalancedGuid.ToString();
            Eq(true, PowerPlan.SelectPlan(localPlan)); Eq(localPlan, PowerPlan.EffectivePlanId);
            Eq(true, PolicyResolver.Global().PowerPlanOn);
            Eq(false, PowerPlan.SelectPlan("not-a-plan")); Eq(localPlan, PowerPlan.EffectivePlanId);
            Settings.SaveStr(PolicyCatalog.KeyPreset, "4"); Eq(true, PolicyResolver.Global().PowerPlanOn);
            Eq(true, PowerPlan.SelectPlan(PowerPlan.ManagedChoice)); Eq(false, PolicyResolver.Global().PowerPlanOn);
            Settings.SaveStr("AutoInput", "english"); Eq(false, PolicyResolver.Global().EnglishInput);
            foreach (string key in new[] { "AutoInput", "AutoRecording", "AutoGraphics",
                PolicyCatalog.KeyEnglishInput, PolicyCatalog.KeyStandbyCleaner,
                PolicyCatalog.KeyPauseUpdate, PolicyCatalog.KeyNvLowLat,
                PolicyCatalog.KeyNvSmoothMotion, PolicyCatalog.KeyNvRebar,
                PolicyCatalog.KeyNvDlss, PolicyCatalog.KeyAmdAntiLag, PolicyCatalog.KeyAmdAfmf,
                PolicyCatalog.KeyIntelEndurance, PolicyCatalog.KeyGpuPowerMax })
            {
                if (key != PolicyCatalog.KeyPauseUpdate && key != PolicyCatalog.KeyIntelEndurance
                    && key != PolicyCatalog.KeyNvLowLat && key != PolicyCatalog.KeyAmdAntiLag && key != PolicyCatalog.KeyStandbyCleaner) Eq(null, PolicyCatalog.ItemOf(key));
                Eq(false, PolicyResolver.SetOverride(profile, key, "1"));
            }
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                if (AutomaticPolicy.IsIntent(item.Key) || AutomaticPolicy.IsEnvironment(item.Key) || AutomaticPolicy.IsLowLatency(item.Key)
                    || AutomaticPolicy.IsRetiredInAuto(item.Key)) continue;
                bool participates = false;
                foreach (string mode in new[] { "0", "1", "6", "4" })
                {
                    Settings.SaveStr(PolicyCatalog.KeyPreset, mode);
                    string value = PolicyResolver.GlobalValue(item.Key);
                    participates |= value != null && value != "0" && value != "off";
                }
                Eq(true, participates);
            }
            Settings.SaveStr(PolicyCatalog.KeyPreset, "2"); Eq(PerformancePreset.Standard, PolicyResolver.Global().Preset);
            long start = DateTime.UtcNow.Ticks;
            Eq(false, WsTrim.ObserveIdle(77123, 10, 10, 2, start));
            Eq(false, WsTrim.ObserveIdle(77123, 10, 10, 2, start + TimeSpan.TicksPerSecond * 59));
            Eq(true, WsTrim.ObserveIdle(77123, 10, 10, 2, start + TimeSpan.TicksPerMinute));
            Eq(false, WsTrim.ObserveIdle(77123, 10, 11, 2, start + TimeSpan.TicksPerMinute));
            Eq(false, WsTrim.ObserveIdle(77123, 11, 11, 2, start + TimeSpan.TicksPerMinute * 2));
            RunNoSwitchPowerPlanTests();
        }
        private static void RunNoSwitchPowerPlanTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            PowerPlan.ResetPlanRestoreForTest();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                var profile = new GameProfile { Id = "power-off-fixture", Name = "Power off fixture" };
                profile.Overrides[PolicyCatalog.KeyPowerPlan] = "1";
                Settings.Save(PolicyCatalog.KeyPowerPlan, true);
                Settings.Save(LaptopPerfMode.PreferenceKey, true);
                PolicySnapshot snapshot = PolicyResolver.For(profile);
                var mode = new GameMode(Path.Combine(Path.GetTempPath(), "PaviseNoSwitch-" + Guid.NewGuid().ToString("N")), new SuppressionCore());
                foreach (bool automatic in new[] { false, true })
                foreach (string preset in new[] { "0", "1", "6", "4" })
                {
                    Settings.Save(AutomaticPolicy.VersionKey, automatic);
                    Settings.SaveStr(PolicyCatalog.KeyPreset, preset);
                    Eq(true, PowerPlan.SelectPlan(PowerPlan.NoSwitchChoice));
                    Eq(PowerPlan.NoSwitchChoice, PowerPlan.EffectivePlanId);
                    Eq(false, PowerPlan.AutomaticSelection);
                    Eq(Lang.T("plan.pick.off"), PowerPlan.SelectionLabel);
                    Eq(false, snapshot.PowerPlanOn);
                    Eq(false, PolicyResolver.Global().PowerPlanOn);
                    Eq(false, GameMode.ResolvePowerPlanEnabled(PresetValue.From(int.Parse(preset)), true));
                    typeof(GameMode).GetField("active", flags).SetValue(mode, true);
                    typeof(GameMode).GetField("enabled", flags).SetValue(mode, true);
                    typeof(GameMode).GetField("planActive", flags).SetValue(mode, true);
                    typeof(GameMode).GetField("lastPowerPolicyKey", flags).SetValue(mode, 3);
                    Eq(false, (bool)typeof(GameMode).GetMethod("LaptopPerformanceAdmitted", flags).Invoke(mode, null));
                    Eq(false, (bool)typeof(GameMode).GetMethod("PowerPlanNotificationStillCurrent", flags).Invoke(mode, new object[] { 0, 3 }));
                    mode.NotifyPowerSchemeChanged();
                    Eq(0, (int)typeof(GameMode).GetField("powerPlanNotificationPending", flags).GetValue(mode));
                    Eq(0, (int)typeof(GameMode).GetField("powerApplyInFlight", flags).GetValue(mode));
                    typeof(GameMode).GetField("powerPlanNotificationPending", flags).SetValue(mode, 1);
                    typeof(GameMode).GetMethod("QueuePowerPlanNotificationAudit", flags).Invoke(mode, null);
                    Eq(0, (int)typeof(GameMode).GetField("powerPlanNotificationPending", flags).GetValue(mode));
                    Eq(0, (int)typeof(GameMode).GetField("powerApplyInFlight", flags).GetValue(mode));
                    int calls = 0;
                    Eq(false, (bool)typeof(GameMode).GetMethod("RunPowerPlanApply", flags).Invoke(mode,
                        new object[] { 0, new Func<bool>(delegate { calls++; return true; }) }));
                    Eq(0, calls);
                    typeof(GameMode).GetMethod("MaybeActivatePowerOverlay", flags).Invoke(mode, new object[] { true });
                    Eq(0, (int)typeof(GameMode).GetField("overlayAttempts", flags).GetValue(mode));
                    Eq(0L, (long)typeof(GameMode).GetField("nextOverlayAuditTicks", flags).GetValue(mode));
                    Eq(true, PowerPlan.SelectPlan(PowerPlan.BalancedGuid.ToString()));
                    Eq(true, snapshot.PowerPlanOn);
                    Eq(true, PowerPlan.SelectPlan(PowerPlan.ManagedChoice));
                    Eq(!automatic || preset != "4", snapshot.PowerPlanOn);
                }
                Eq(true, PowerPlan.SelectPlan(PowerPlan.NoSwitchChoice));
                int nativeCalls = 0;
                Guid original = PowerPlan.BalancedGuid;
                Guid managed = new Guid("27018216-8761-442c-a0dc-5a678ae322f4");
                PowerPlan.ActivateTargetPlanForTest = delegate { nativeCalls++; return managed; };
                PowerPlan.RestoreCurrentPlanForTest = delegate { nativeCalls++; return managed; };
                PowerPlan.RestoreSetPlanForTest = delegate(Guid plan) { nativeCalls++; return true; };
                PowerPlan.RestoreOrphanManagedPlanForTest = delegate { nativeCalls++; return false; };
                Eq(false, PowerPlan.ActivatePlanForTest());
                Eq(false, PowerPlan.Enforce(true, false, true));
                typeof(PowerPlan).GetField("active", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, true);
                Eq(false, PowerPlan.Enforce(true, false, true));
                Eq(0, nativeCalls);
                Eq(true, PowerPlan.RestorePlanForTest(false));
                Eq(0, nativeCalls);
                Eq("", Settings.LoadStr(PowerPlan.PlanJournalKey, ""));
                Guid external = new Guid("11111111-2222-4333-8444-555555555555");
                Eq(false, PowerPlan.ClassifyRestorePlanForTest(external));
                Settings.SaveStr("PgPlanGuid", managed.ToString());
                Settings.SaveStr(PowerPlan.PlanJournalKey, original.ToString());
                Eq(false, PowerPlan.Enforce(true, false));
                Eq(original.ToString(), Settings.LoadStr(PowerPlan.PlanJournalKey, ""));
                Guid current = managed;
                int writes = 0;
                PowerPlan.RestoreCurrentPlanForTest = delegate { return current; };
                PowerPlan.RestorePlanIsOwnedForTest = PowerPlan.ClassifyRestorePlanForTest;
                PowerPlan.RestoreSetPlanForTest = delegate(Guid plan) { writes++; current = plan; return true; };
                Eq(true, PowerPlan.RestorePlanForTest(true));
                Eq(original, current); Eq(1, writes);
                Eq("", Settings.LoadStr(PowerPlan.PlanJournalKey, ""));
                Settings.SaveStr(PowerPlan.PlanJournalKey, original.ToString());
                current = external;
                Eq(true, PowerPlan.RestorePlanForTest(true));
                Eq(external, current); Eq(1, writes);
                Eq(true, PowerPlan.SelectPlan(PowerPlan.NoSwitchChoice));
                Eq(false, PowerPlan.SelectPlan("invalid-plan"));
                Eq(PowerPlan.NoSwitchChoice, PowerPlan.EffectivePlanId);
                Eq(true, PowerPlan.SelectPlan(PowerPlan.ManagedChoice));
                Settings.SuspendWritesForReset();
                Eq(false, PowerPlan.SelectPlan(PowerPlan.NoSwitchChoice));
                Eq(PowerPlan.ManagedChoice, PowerPlan.EffectivePlanId);
            }
            finally
            {
                PowerPlan.ResetPlanRestoreForTest();
                Settings.UseTransientStoreForCurrentProcess();
            }
        }
        private static void RunGameDvrPreferenceTests()
        {
            foreach (bool requested in new[] { false, true })
            {
                Settings.UseTransientStoreForCurrentProcess();
                string dir = Path.Combine(Path.GetTempPath(), "PaviseDvrPreference-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var mode = new GameMode(dir, new SuppressionCore());
                mode.InitializeAutomation();
                Eq(true, mode.KillGameDvr);
                Eq(true, mode.SetGameDvrSuppression(requested));
                Eq(requested, Settings.Load("GameDvrOff", !requested));
                foreach (string preset in new[] { "0", "1", "6", "4", "2" })
                {
                    Settings.SaveStr(PolicyCatalog.KeyPreset, preset);
                    mode.RefreshAutomaticPreferences();
                    Eq(requested, mode.KillGameDvr);
                    Eq(true, (bool)typeof(GameMode).GetField("mmcssOn", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode));
                }
                var reloaded = new GameMode(dir, new SuppressionCore());
                reloaded.InitializeAutomation();
                Eq(requested, reloaded.KillGameDvr);
                Settings.SuspendWritesForReset();
                Eq(false, mode.SetGameDvrSuppression(!requested));
                Eq(requested, mode.KillGameDvr);
                mode.KillGameDvr = !requested;
                Eq(requested, mode.KillGameDvr);
                Eq(requested, Settings.Load("GameDvrOff", !requested));
                Eq(false, GameDvr.HasResidue);
            }
            Settings.UseTransientStoreForCurrentProcess();
        }

        private static void RunGameLowLatencyPreferenceTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            var mode = new GameMode(Path.GetTempPath(), new SuppressionCore());
            mode.InitializeAutomation();
            Eq(false, AutomaticPolicy.GameLowLatency);
            var profile = new GameProfile { Id = "latency-test", Name = "Latency fixture" };
            foreach (string key in new[] { PolicyCatalog.KeyNvLowLat, PolicyCatalog.KeyAmdAntiLag })
            {
                Settings.SaveStr(key, key == PolicyCatalog.KeyNvLowLat ? "on" : "1");
                profile.Overrides[key] = "1";
            }
            var snapshot = PolicyResolver.For(profile);
            var nvPlanKey = typeof(GameMode).GetField("automaticNvPlanKey", BindingFlags.Instance | BindingFlags.NonPublic);
            Eq(true, AutomaticPolicy.IsLowLatency(PolicyCatalog.KeyNvLowLat));
            Eq(false, AutomaticPolicy.IsLowLatency(PolicyCatalog.KeyAmdAntiLag));
            foreach (string preset in new[] { "0", "1", "6", "4" })
            {
                Settings.SaveStr(PolicyCatalog.KeyPreset, preset);
                Eq("off", snapshot.NvLowLatMode); Eq(false, snapshot.AmdAntiLag);
                nvPlanKey.SetValue(mode, 42);
                Eq(true, mode.SetGameLowLatency(true));
                Eq(-1, (int)nvPlanKey.GetValue(mode));
                Eq("on", snapshot.NvLowLatMode); Eq(false, snapshot.AmdAntiLag);
                Eq(false, mode.AmdAntiLag);
                Settings.SaveStr(PolicyCatalog.KeyPreset, preset == "4" ? "6" : "4");
                Eq(true, AutomaticPolicy.GameLowLatency); Eq("on", snapshot.NvLowLatMode);
                Eq(true, mode.SetGameLowLatency(false));
                Eq("off", snapshot.NvLowLatMode); Eq(false, snapshot.AmdAntiLag);
            }
            Settings.Save(AutomaticPolicy.GameLowLatencyKey, false);
            Settings.Save(AutomaticPolicy.VersionKey, false);
            Settings.Save(PolicyCatalog.KeyAmdAntiLag, true);
            Eq(false, PolicyResolver.For(profile).AmdAntiLag);
            mode.AmdAntiLag = true;
            Eq(false, mode.AmdAntiLag); Eq(false, Settings.Load(PolicyCatalog.KeyAmdAntiLag, true));
            Settings.Save(AutomaticPolicy.VersionKey, true);
            var receipt = new Dictionary<string, string>();
            receipt["g0.alag"] = "0"; receipt["g0.chill"] = "1|40|60";
            string encoded = NvDrsTweaks.SerializeSnapshot(receipt);
            Settings.SaveStr(AdlxTweaks.SnapKey, encoded);
            Eq(true, AdlxTweaks.HasAntiLagResidue());
            Eq(false, AdlxTweaks.Available);
            Eq(false, AdlxTweaks.RestoreAntiLag()); Eq(false, AdlxTweaks.RestoreChill());
            Eq(encoded, Settings.LoadStr(AdlxTweaks.SnapKey, ""));
            Settings.SaveStr(AdlxTweaks.SnapKey, "");
            Eq(false, AdlxTweaks.HasAntiLagResidue());
            nvPlanKey.SetValue(mode, 42);
            Settings.SuspendWritesForReset();
            Eq(false, mode.SetGameLowLatency(true)); Eq(false, AutomaticPolicy.GameLowLatency);
            Eq(42, (int)nvPlanKey.GetValue(mode));
            Settings.UseTransientStoreForCurrentProcess();
        }

        private static void RunPreStageNvidiaRegression()
        {
            INvDrsDriver original = NvDrsTweaks.Driver;
            string previousLog = Logger.LogPath;
            string dir = Path.Combine(Path.GetTempPath(), "PavisePreStage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Settings.Save(AutomaticPolicy.VersionKey, true);
                var mode = new GameMode(dir, new SuppressionCore(Path.Combine(dir, "state")));
                var profile = new GameProfile { Id = "prestage", Name = "Prestage test" };
                var keys = new HashSet<int>();
                foreach (bool max in new[] { false, true })
                foreach (bool cache in new[] { false, true })
                foreach (bool low in new[] { false, true })
                foreach (bool vrr in new[] { false, true })
                foreach (bool rebar in new[] { false, true })
                    Eq(true, keys.Add(NvGamePlan.ForAutomatic(max, cache, true, low ? "on" : "off", vrr, rebar).AutomaticKey));
                foreach (string preset in new[] { "0", "1", "6", "4" })
                foreach (bool low in new[] { false, true })
                foreach (bool onAc in new[] { false, true })
                foreach (bool vrr in new[] { false, true })
                {
                    Settings.SaveStr(PolicyCatalog.KeyPreset, preset);
                    Settings.Save(AutomaticPolicy.GameLowLatencyKey, low);
                    PolicySnapshot policy = PolicyResolver.For(profile);
                    NvGamePlan before = GameMode.AutomaticPreStagePlan(policy, onAc, vrr);
                    NvGamePlan live = mode.RefreshAutomaticNvPlan(policy.NvMaxPerf, policy.NvShaderCacheMax,
                        onAc, policy.NvLowLatMode, vrr, policy.NvRebar);
                    Eq(low ? "on" : "off", before.LowLatMode); Eq(vrr, before.WindowedVrr);
                    Eq(live.AutomaticKey, before.AutomaticKey);
                    var wanted = NvDrsTweaks.BuildDesired(before);
                    var actual = NvDrsTweaks.BuildDesired(live);
                    Eq(wanted.Count, actual.Count);
                    for (int i = 0; i < wanted.Count; i++) Eq(wanted[i], actual[i]);
                    bool unlimitedCache = false;
                    foreach (var setting in wanted)
                        if (setting.Key == NvDrsTweaks.KeyShaderCache)
                        {
                            Eq(NvApi.ShaderCacheUnlimited, setting.Value);
                            unlimitedCache = true;
                        }
                    Eq(true, unlimitedCache);
                }

                var fake = new AutomaticNvDriverFake(); NvDrsTweaks.Driver = fake;
                fake.Values[NvApi.SettingPreRenderLimit] = 3;
                fake.Values[NvApi.SettingVrrAppOverride] = 0;
                Settings.SaveStr(PolicyCatalog.KeyPreset, "6");
                Settings.Save(AutomaticPolicy.GameLowLatencyKey, true);
                NvGamePlan plan = GameMode.AutomaticPreStagePlan(PolicyResolver.For(profile), true, true);
                bool retry;
                Eq(0, mode.ApplyNvGamePlan(@"C:\fixture\prestage-game.exe", plan, out retry, preStage: true).Count);
                Eq(false, retry); Eq(1u, fake.Values[NvApi.SettingPreRenderLimit]);
                Eq(NvApi.VrrAppAllow, fake.Values[NvApi.SettingVrrAppOverride]);
                Eq(NvApi.ShaderCacheUnlimited, fake.Values[NvApi.SettingShaderCacheSize]);
                Eq(true, NvDrsTweaks.RestoreAllGames());
                Eq(3u, fake.Values[NvApi.SettingPreRenderLimit]); Eq(0u, fake.Values[NvApi.SettingVrrAppOverride]);

                string log = Path.Combine(dir, "prestage.log"); Logger.LogPath = log;
                var evidence = (Dictionary<string, string>)typeof(GameMode).GetField("automationEvidence", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(mode);
                evidence[PolicyCatalog.KeyNvMaxPerf] = "restored";
                typeof(GameMode).GetField("automationSession", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(mode, "finished-fixture");
                var record = typeof(GameMode).GetMethod("RecordNvAutomation", BindingFlags.NonPublic | BindingFlags.Instance);
                record.Invoke(mode, new object[] { PolicyCatalog.KeyNvMaxPerf, "configured", true });
                record.Invoke(mode, new object[] { PolicyCatalog.KeyNvMaxPerf, "failed", true });
                Eq("restored", evidence[PolicyCatalog.KeyNvMaxPerf]);
                string[] lines = File.ReadAllLines(log);
                Eq(2, lines.Length); Eq(true, lines[0].Contains("PRESTAGE "));
                Eq(false, File.ReadAllText(log).Contains("finished-fixture"));
                Eq(LogEventSeverity.Info, LogStreamView.Parse(lines[0]).Severity);
                Eq(LogEventSeverity.Warning, LogStreamView.Parse(lines[1]).Severity);
                fake.DenyOpen = true;
                mode.ApplyNvGamePlan(@"C:\fixture\prestage-failed.exe", plan, out retry, preStage: true);
                Eq(true, retry); Eq("restored", evidence[PolicyCatalog.KeyNvMaxPerf]);
                Eq(false, File.ReadAllText(log).Contains("finished-fixture"));
                record.Invoke(mode, new object[] { PolicyCatalog.KeyNvMaxPerf, "configured", false });
                Eq("configured", evidence[PolicyCatalog.KeyNvMaxPerf]);
                Eq(true, File.ReadAllText(log).Contains("AUTO [finished-fixture]"));
                int logged = File.ReadAllLines(log).Length;
                record.Invoke(mode, new object[] { PolicyCatalog.KeyNvMaxPerf, "unchanged", false });
                Eq("unchanged", evidence[PolicyCatalog.KeyNvMaxPerf]);
                Eq(logged, File.ReadAllLines(log).Length);
                Console.WriteLine("PASS pre-stage/live NVIDIA plans agree, restore originals and keep pre-stage records outside the completed session");
            }
            finally
            {
                NvDrsTweaks.Driver = original; Logger.LogPath = previousLog;
                Settings.Save(AutomaticPolicy.GameLowLatencyKey, false);
                Settings.SaveStr(PolicyCatalog.KeyPreset, "0");
            }
        }

        private sealed class AutomaticNvDriverFake : INvDrsDriver
        {
            internal Dictionary<uint, uint> Values = new Dictionary<uint, uint>();
            private Dictionary<uint, uint> working;
            internal uint RejectId, UnsupportedId, UnreadableId;
            internal bool DenySave, DenyOpen;
            internal int Opens, Writes;
            public bool Available { get { return true; } }
            public bool TryOpenSession(out IntPtr session)
            { Opens++; working = new Dictionary<uint, uint>(Values); session = new IntPtr(1); return !DenyOpen; }
            public void CloseSession(IntPtr session) { }
            public bool SaveSession(IntPtr session)
            { if (DenySave) return false; Values = new Dictionary<uint, uint>(working); return true; }
            public bool FindOrCreateAppProfile(IntPtr session, string exe, out IntPtr profile)
            { profile = new IntPtr(2); return true; }
            public int TryGetDword(IntPtr session, IntPtr profile, uint id, out uint value)
            { value = 0; if (id == UnreadableId) return -1; return working.TryGetValue(id, out value) ? 1 : 0; }
            public bool SetDword(IntPtr session, IntPtr profile, uint id, uint value, out int status)
            {
                status = id == UnsupportedId ? NvApi.StatusSettingNotFound : id == RejectId ? -999 : 0;
                if (status != 0) return false;
                Eq(true, NvDrsTweaks.HasGameResidue);
                Writes++; working[id] = value; return true;
            }
            public bool DeleteSetting(IntPtr session, IntPtr profile, uint id)
            { working.Remove(id); return true; }
        }

        private static void PermitAutomaticNvFailures()
        {
            foreach (ActionFailureRecord record in ActionFailureStore.Records())
                if (!record.RetryPending) Eq(true, ActionFailureStore.RequestRetry(record.Key));
        }

        private static void RunAutomaticNvidiaTests()
        {
            RunPreStageNvidiaRegression();
            INvDrsDriver original = NvDrsTweaks.Driver;
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Settings.Save(AutomaticPolicy.VersionKey, true);
                var fake = new AutomaticNvDriverFake(); NvDrsTweaks.Driver = fake;
                NvGamePlan plan = NvGamePlan.ForAutomatic(true, true, true);
                var desired = new Dictionary<string, uint>();
                foreach (var item in NvDrsTweaks.BuildDesired(plan)) desired[item.Key] = item.Value;
                Eq(3, desired.Count); Eq(NvApi.PStatePreferMax, desired[NvDrsTweaks.KeyPState]);
                Eq(NvApi.ShaderCacheOn, desired[NvDrsTweaks.KeyShaderOn]); Eq(NvApi.ShaderCacheUnlimited, desired[NvDrsTweaks.KeyShaderCache]);
                Eq(false, desired.ContainsKey(NvDrsTweaks.KeyForceP2));
                Eq(false, NvGamePlan.ForAutomatic(true, true, false).MaxPerf); Eq(false, NvGamePlan.ForAutomatic(false, true, true).MaxPerf);
                fake.Values[NvApi.SettingPreferredPState] = 5; fake.Values[NvApi.SettingShaderCacheEnable] = 0;
                bool retry;
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", plan, out retry).Count);
                Eq(false, retry); Eq(3, fake.Values.Count); Eq(true, NvDrsTweaks.HasGameResidue);
                int opens = fake.Opens;
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", plan, out retry).Count); Eq(opens, fake.Opens);
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", NvGamePlan.ForAutomatic(false, true, false), out retry).Count);
                Eq(5u, fake.Values[NvApi.SettingPreferredPState]);
                fake.DenySave = true; Eq(false, NvDrsTweaks.RestoreAllGames()); Eq(true, NvDrsTweaks.HasGameResidue);
                fake.DenySave = false; PermitAutomaticNvFailures(); Eq(true, NvDrsTweaks.RestoreAllGames());
                Eq(false, NvDrsTweaks.HasGameResidue); Eq(2, fake.Values.Count); Eq(0u, fake.Values[NvApi.SettingShaderCacheEnable]);
                Eq(false, fake.Values.ContainsKey(NvApi.SettingShaderCacheSize));
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", plan, out retry).Count);
                Eq(false, ActionFailureStore.IsIsolated(PolicyCatalog.KeyNvShaderCache, "game.exe")); Eq(true, NvDrsTweaks.RestoreAllGames());
                fake.Values[NvApi.SettingShaderCacheSize] = NvApi.ShaderCacheDefault;
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", plan, out retry).Count);
                Eq(NvApi.ShaderCacheUnlimited, fake.Values[NvApi.SettingShaderCacheSize]); Eq(true, NvDrsTweaks.RestoreAllGames());
                Eq(NvApi.ShaderCacheDefault, fake.Values[NvApi.SettingShaderCacheSize]); fake.Values.Remove(NvApi.SettingShaderCacheSize);

                fake.UnreadableId = NvApi.SettingShaderCacheEnable;
                Eq(true, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", plan, out retry).Contains(NvDrsTweaks.KeyShaderOn));
                Eq(true, ActionFailureStore.IsBlocked(PolicyCatalog.KeyNvShaderCache, "game.exe"));
                fake.UnreadableId = 0; PermitAutomaticNvFailures(); Eq(true, NvDrsTweaks.RestoreAllGames());
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", plan, out retry).Count); Eq(true, NvDrsTweaks.RestoreAllGames());
                fake.RejectId = NvApi.SettingPreferredPState;
                Eq(true, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", plan, out retry).Contains(NvDrsTweaks.KeyPState));
                Eq(true, ActionFailureStore.IsBlocked(PolicyCatalog.KeyNvMaxPerf, "game.exe"));
                fake.RejectId = 0; PermitAutomaticNvFailures();
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", plan, out retry).Count); Eq(true, NvDrsTweaks.RestoreAllGames());
                fake.Values[NvApi.SettingPreRenderLimit] = 3;
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", NvGamePlan.ForAutomatic(false, true, true, "on"), out retry).Count);
                Eq(1u, fake.Values[NvApi.SettingPreRenderLimit]);
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", NvGamePlan.ForAutomatic(false, true, true), out retry).Count);
                Eq(3u, fake.Values[NvApi.SettingPreRenderLimit]); Eq(true, NvDrsTweaks.RestoreAllGames());
                fake.Values[NvApi.SettingVrrAppOverride] = 3;
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", NvGamePlan.ForAutomatic(false, true, true, "off", true), out retry).Count);
                Eq(NvApi.VrrAppAllow, fake.Values[NvApi.SettingVrrAppOverride]);
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", NvGamePlan.ForAutomatic(false, true, true), out retry).Count);
                Eq(3u, fake.Values[NvApi.SettingVrrAppOverride]); Eq(true, NvDrsTweaks.RestoreAllGames());
                fake.Values[NvApi.SettingRebarFeature] = 0;
                NvGamePlan rebarPlan = NvGamePlan.ForAutomatic(false, true, true, rebar: true);
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", rebarPlan, out retry).Count);
                Eq(1u, fake.Values[NvApi.SettingRebarFeature]); Eq(1u, fake.Values[NvApi.SettingRebarOptions]);
                Eq(NvApi.RebarSizeDefault, fake.Values[NvApi.SettingRebarSizeLimit]);
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", NvGamePlan.ForAutomatic(false, true, true), out retry).Count);
                Eq(0u, fake.Values[NvApi.SettingRebarFeature]); Eq(false, fake.Values.ContainsKey(NvApi.SettingRebarOptions));
                Eq(false, fake.Values.ContainsKey(NvApi.SettingRebarSizeLimit)); Eq(true, NvDrsTweaks.RestoreAllGames());
                fake.RejectId = NvApi.SettingRebarFeature;
                Eq(true, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", rebarPlan, out retry).Contains(NvDrsTweaks.KeyRebarFeat));
                fake.RejectId = 0; PermitAutomaticNvFailures();
                Eq(0, NvDrsTweaks.ApplyForGame(@"C:\fixture\game.exe", rebarPlan, out retry).Count); Eq(true, NvDrsTweaks.RestoreAllGames());

                Settings.UseTransientStoreForCurrentProcess(); Settings.Save(AutomaticPolicy.VersionKey, true);
                fake = new AutomaticNvDriverFake { DenyOpen = true }; NvDrsTweaks.Driver = fake;
                var mode = new GameMode(Path.GetTempPath(), new SuppressionCore());
                var applied = (HashSet<int>)typeof(GameMode).GetField("tweakApplied", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode);
                mode.RefreshAutomaticNvPlan(true, true, true, rebar: true); applied.Add(9987);
                mode.RefreshAutomaticNvPlan(true, true, true, rebar: true); Eq(true, applied.Contains(9987));
                mode.RefreshAutomaticNvPlan(true, true, true); Eq(0, applied.Count);
                mode.RefreshAutomaticNvPlan(true, true, true, "on"); applied.Add(9987);
                mode.RefreshAutomaticNvPlan(true, true, true); Eq(0, applied.Count); applied.Add(9987);
                mode.RefreshAutomaticNvPlan(true, true, true); Eq(true, applied.Contains(9987));
                mode.RefreshAutomaticNvPlan(true, true, false); Eq(0, applied.Count); applied.Add(9987);
                mode.RefreshAutomaticNvPlan(false, true, true); Eq(true, applied.Contains(9987));
                mode.RefreshAutomaticNvPlan(true, true, true); Eq(0, applied.Count);
                Eq(null, mode.ApplyNvGamePlan(@"C:\fixture\game.exe", plan, out retry)); Eq(true, retry); opens = fake.Opens;
                for (int round = 0; round < 20; round++)
                {
                    mode.ResetNvApplyAttempts(); mode.RefreshAutomaticNvPlan(false, true, true); mode.RefreshAutomaticNvPlan(true, true, true);
                    Eq(null, mode.ApplyNvGamePlan(@"C:\FIXTURE\GAME.EXE", plan, out retry));
                }
                Eq(opens, fake.Opens); Eq(true, ActionFailureStore.IsBlocked(NvDrsTweaks.DriverFailureAction, "game.exe"));
                Eq(false, ActionFailureStore.IsIsolated(PolicyCatalog.KeyNvShaderCache, "game.exe"));
                fake.DenyOpen = false;
                Eq(0, mode.ApplyNvGamePlan(@"C:\fixture\other.exe", plan, out retry).Count); Eq(true, NvDrsTweaks.RestoreAllGames());
                Eq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(NvDrsTweaks.DriverFailureAction, "game.exe")));
                Eq(0, mode.ApplyNvGamePlan(@"C:\fixture\game.exe", plan, out retry).Count); Eq(false, retry);
                Eq(false, ActionFailureStore.IsIsolated(NvDrsTweaks.DriverFailureAction, "game.exe")); Eq(true, NvDrsTweaks.RestoreAllGames());
                Settings.UseTransientStoreForCurrentProcess(); Settings.Save(AutomaticPolicy.VersionKey, true);
                fake = new AutomaticNvDriverFake { DenySave = true }; NvDrsTweaks.Driver = fake;
                fake.Values[NvApi.SettingPreferredPState] = 5;
                long now = TimeSpan.FromHours(1).Ticks; NvDrsTweaks.RestoreClockForTest = delegate { return now; };
                Eq(true, mode.ApplyNvGamePlan(@"C:\fixture\save-failed.exe", plan, out retry).Count > 0);
                Eq(true, ActionFailureStore.IsBlocked(NvDrsTweaks.DriverFailureAction, "save-failed.exe"));
                Eq(false, ActionFailureStore.IsIsolated(PolicyCatalog.KeyNvShaderCache, "save-failed.exe"));
                fake.DenySave = false; int writes = fake.Writes;
                mode.ResetNvApplyAttempts(); mode.ApplyNvGamePlan(@"C:\fixture\save-failed.exe", plan, out retry); Eq(writes, fake.Writes);
                PermitAutomaticNvFailures(); Eq(0, mode.ApplyNvGamePlan(@"C:\fixture\save-failed.exe", plan, out retry).Count);
                Eq(false, retry); Eq(false, ActionFailureStore.IsIsolated(NvDrsTweaks.DriverFailureAction, "save-failed.exe"));
                now += TimeSpan.FromMinutes(1).Ticks; Eq(true, NvDrsTweaks.RestoreAllGames());
            }
            finally { NvDrsTweaks.Driver = original; NvDrsTweaks.RestoreClockForTest = null; NvDrsTweaks.DriverVersionForTest = null; }
        }

        private static void RunAutomaticFreshStartTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PaviseAutomaticFresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Settings.Save("GameDvrOff", true);
            Settings.Save(PolicyCatalog.KeyEnglishInput, true);
            Settings.Save(PolicyCatalog.KeySuppress, false);
            var mode = new GameMode(dir, new SuppressionCore());
            mode.InitializeAutomation();
            Eq(true, AutomaticPolicy.Enabled);
            Eq(true, mode.AutomationPolicySnapshot.SuppressBackground);
            Eq(true, mode.KillGameDvr);
            Eq(false, mode.AutomationPolicySnapshot.EnglishInput);
            Eq(0, Directory.GetDirectories(dir).Length);
            Eq(@"Software\PaviseAuto", Settings.Key);
            Eq("PaviseAuto", TaskHelper.TaskName);
            mode.InitializeAutomation();
            Eq(0, Directory.GetDirectories(dir).Length);
            int attempts = mode.EnvAttemptCountForTest("awake", true, false, delegate { return false; }, delegate { return true; }, 100);
            Eq(2, attempts); Eq(true, PolicyResolver.Global().Awake);
            Eq("stopped", mode.AutomaticExecutionState(PolicyCatalog.KeyAwake));
        }

        private static void RunBoundedRetryTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save(AutomaticPolicy.VersionKey, true);
            var mode = new GameMode(Path.GetTempPath(), new SuppressionCore());
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var envStep = typeof(GameMode).GetMethod("EnvStep", flags);
            var next = (Dictionary<string,long>)typeof(GameMode).GetField("envNextAttempt", flags).GetValue(mode);
            int applies = 0, restores = 0;
            bool restoreOk = false;
            Func<bool> apply = delegate { applies++; return false; };
            Func<bool> restore = delegate { restores++; return restoreOk; };
            Action run = delegate {
                envStep.Invoke(mode, new object[] { "awake", true, false, apply, restore });
            };
            run(); next.Clear(); run(); Eq(1, applies); Eq(2, restores);
            for (int i = 0; i < 20; i++) { next.Clear(); run(); }
            Eq(1, applies); Eq(22, restores);
            double delay = TimeSpan.FromTicks(next["awake"] - DateTime.UtcNow.Ticks).TotalSeconds;
            Eq(true, delay > 55 && delay <= 60);
            restoreOk = true; next.Clear(); run();
            int restored = restores;
            for (int i = 0; i < 20; i++) { next.Clear(); run(); }
            Eq(1, applies); Eq(restored, restores);
            typeof(GameMode).GetMethod("BeginRetrySession", flags).Invoke(mode, null);
            run(); Eq(1, applies);
            Eq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(PolicyCatalog.KeyAwake, "system")));
            run(); Eq(2, applies); run(); Eq(2, applies);

            typeof(GameMode).GetField("lastPowerPolicyKey", flags).SetValue(mode, 3);
            var stopped = typeof(GameMode).GetMethod("PowerPlanStopped", flags);
            int powerCalls = 0;
            var powerApply = typeof(GameMode).GetMethod("RunPowerPlanApply", flags);
            int generation = (int)typeof(GameMode).GetField("powerSessionGen", flags).GetValue(mode);
            GameMode.PowerRestoreForTest = delegate { return true; };
            try
            {
                powerApply.Invoke(mode, new object[] { generation, new Func<bool>(delegate { powerCalls++; return false; }) });
                Eq(1, powerCalls); Eq(true, (bool)stopped.Invoke(mode, new object[] { 3 })); Eq(true, (bool)stopped.Invoke(mode, new object[] { 11 }));
                powerApply.Invoke(mode, new object[] { generation, new Func<bool>(delegate { powerCalls++; return true; }) }); Eq(1, powerCalls);
                typeof(GameMode).GetMethod("BeginRetrySession", flags).Invoke(mode, null); Eq(true, (bool)stopped.Invoke(mode, new object[] { 3 }));
                Eq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(PolicyCatalog.KeyPowerPlan, "system")));
                typeof(GameMode).GetField("lastPowerPolicyKey", flags).SetValue(mode, 3);
                powerApply.Invoke(mode, new object[] { generation, new Func<bool>(delegate { powerCalls++; return true; }) });
                Eq(2, powerCalls); Eq(false, (bool)stopped.Invoke(mode, new object[] { 3 }));
            }
            finally { GameMode.PowerRestoreForTest = null; }

            int tickWrites = 0, globalWrites = 0;
            var timers = new TimerModeController {
                Boot = delegate { return TimerFixtureBoot(1); }, TickOn = delegate { return false; },
                GlobalOn = delegate { return false; }, TickOwned = delegate { return false; }, GlobalOwned = delegate { return false; },
                TickEnable = delegate { tickWrites++; return false; }, GlobalEnable = delegate { globalWrites++; return false; },
                TickRestore = delegate { return true; }, GlobalRestore = delegate { return true; }
            };
            long now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < 100; i++) timers.Sync(true, true, 26100, now + TimeSpan.FromMinutes(i * 6).Ticks);
            Eq(1, tickWrites); Eq(1, globalWrites);
            timers.Sync(false, true, 26100, now + TimeSpan.FromHours(11).Ticks);
            timers.Sync(true, true, 26100, now + TimeSpan.FromHours(12).Ticks);
            Eq(1, tickWrites); Eq(1, globalWrites);
            Eq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(PolicyCatalog.KeyTimerTick, "system")));
            timers.Sync(true, true, 26100, now + TimeSpan.FromHours(13).Ticks);
            Eq(2, tickWrites); Eq(1, globalWrites);
            var lease = new TimerResolutionLease(); int leaseWrites = 0;
            lease.Set = delegate(uint desired, bool acquire, out uint actual) { leaseWrites++; actual = 0; return -1; };
            long leaseNow = now;
            Eq(false, lease.Update(true, leaseNow)); Eq(1, leaseWrites);
            foreach (int seconds in new[] { 30, 60, 120, 240, 300, 300 }) {
                int beforeLease = leaseWrites;
                Eq(false, lease.Update(true, leaseNow + TimeSpan.FromSeconds(seconds).Ticks - 1)); Eq(beforeLease, leaseWrites);
                leaseNow += TimeSpan.FromSeconds(seconds).Ticks;
                Eq(false, lease.Update(true, leaseNow)); Eq(beforeLease, leaseWrites);
            }
            Eq(1, leaseWrites); lease.ResetAcquireAttempts(); lease.Update(true, ++leaseNow); Eq(1, leaseWrites);
            Eq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(TimerResolutionLease.FailureAction, "system")));
            lease.Update(true, ++leaseNow); Eq(2, leaseWrites);

            var core = new SuppressionCore(delegate(int p, long c, string n) { return SuppressionCore.RestoreResult.Restored; }, true);
            var entryType = typeof(SuppressionCore).GetNestedType("Entry", BindingFlags.NonPublic);
            var entries = (System.Collections.IDictionary)typeof(SuppressionCore).GetField("map", flags).GetValue(core);
            object entry = Activator.CreateInstance(entryType, true); entries[777] = entry;
            var note = typeof(SuppressionCore).GetMethod("NoteSqueezeAttempt", flags);
            note.Invoke(core, new object[] { 777, entry, 3UL, false });
            note.Invoke(core, new object[] { 777, entry, 0UL, false });
            Eq(1, (int)entryType.GetField("SqueezeFailures").GetValue(entry));
            note.Invoke(core, new object[] { 777, entry, 3UL, false });
            Eq(true, (bool)entryType.GetField("SqueezeRefused").GetValue(entry));
            object replacement = Activator.CreateInstance(entryType, true); entries[777] = replacement;
            note.Invoke(core, new object[] { 777, entry, 3UL, false });
            Eq(false, (bool)entryType.GetField("SqueezeRefused").GetValue(replacement));

            string log = Logger.LogPath;
            string fixture = Path.Combine(Path.GetTempPath(), "pavise-retry-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                Logger.LogPath = fixture; Logger.ResetWriteBarrierForTest();
                for (int i = 0; i < 100; i++) Logger.RepeatedError("fixture", "same failure", now + i);
                Eq(1, File.ReadAllLines(fixture).Length);
                Logger.RepeatedRecovered("fixture");
                string text = File.ReadAllText(fixture);
                Eq(true, text.Contains("repeated=99")); Eq(true, text.Contains("recovered"));
                Logger.RepeatedError("fixture", "same failure", now + 200);
                Logger.RepeatedError("fixture", "different failure", now + 201);
                Eq(5, File.ReadAllLines(fixture).Length);
                Logger.RepeatedRecovered("fixture");
                string beforeMute = File.ReadAllText(fixture);
                Logger.WritesEnabled = false;
                for (int i = 0; i < 100; i++) Logger.RepeatedError("fixture", "muted error", now + i);
                Logger.WritesEnabled = true;
                Logger.RepeatedRecovered("fixture");
                Eq(beforeMute, File.ReadAllText(fixture));
                Logger.RepeatedError("fixture", "before mute", now);
                Logger.RepeatedError("fixture", "before mute", now + 1);
                Logger.WritesEnabled = false; Logger.WritesEnabled = true;
                beforeMute = File.ReadAllText(fixture);
                Logger.RepeatedRecovered("fixture");
                Eq(beforeMute, File.ReadAllText(fixture));
            }
            finally { Logger.LogPath = log; Logger.ResetWriteBarrierForTest(); File.Delete(fixture); }
            Settings.UseTransientStoreForCurrentProcess();
        }
    }
}
#endif
