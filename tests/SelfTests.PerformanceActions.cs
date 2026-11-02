#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int PerformanceActionAssertions;
        private static void PerformanceEq<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            PerformanceActionAssertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Line " + line + ": Expected " + expected + ", actual " + actual);
        }
        internal static void RunPerformanceActionTests()
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
            PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; };
            try
            {
                ActionReceiptRoundTrip(); ActionReceiptFailureBoundaries(); ActionComponentRestore();
                ActionRoutingAndCommands(); ActionImageCollisions(); ActionEnvironmentRebootState();
                ActionRetiredFailureUi();
                ActionEnvironmentPartialFailure(); ActionEnvironmentRetryStatus(); ActionEnvironmentVbs(); ActionEnvironmentBundle(); ActionEnvironmentQuantumIndependent(); ActionEnvironmentTimerConcurrency(); ActionCoordinatorLifecycle(); ActionCoordinatorMixedResults(); ActionCoordinatorBatchedRecords();
                ActionEnvironmentStorageGroup(); ActionQuantumLegacyRecovery(); ActionQuantumAuditReadOnly();
                ActionDefenderInactive(); ActionDefenderFailureRecord(); ActionDefenderRetirementMatrix(); ActionDefenderRetirementClose(); ActionPowerShellFailures();
                ActionCoordinatorRetryBudget(); ActionDiscoveryBudget(); ActionRestoreIsolation(); ActionBackgroundTargetMoves(); ActionLoggerRecords();
                RunEnvironmentBundleLayoutRegressionTests();
                RunMatchVisualEffectsTests();
            }
            finally
            {
                PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null;
                PerformanceActionBackend.TargetsForTest = null; PerformanceTuning.BootForTest = null;
                PerformanceActionBackend.DefenderServiceForTest = null;
                PerformanceActionBackend.ShortNameVolumeForTest = null;
                PerformanceTuning.VbsConfiguredForTest = null; PerformanceTuning.VbsChangeForTest = null;
                PerformanceTuning.ResetForTest(); Settings.UseTransientStoreForCurrentProcess();
            }
        }
        internal static void RunEnvironmentQuantumTests()
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
            try
            {
                ActionEnvironmentBundle(); ActionEnvironmentQuantumIndependent(); ActionQuantumProfiles();
                ActionEnvironmentStorageGroup(); ActionQuantumLegacyRecovery(); ActionQuantumAuditReadOnly();
                RunEnvironmentBundleLayoutRegressionTests();
            }
            finally
            {
                PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null;
                PerformanceActionBackend.TargetsForTest = null; PerformanceTuning.BootForTest = null;
                PerformanceTuning.VbsConfiguredForTest = null; PerformanceTuning.VbsChangeForTest = null;
                PerformanceTuning.ResetForTest(); Settings.UseTransientStoreForCurrentProcess();
            }
        }
        private static void ActionReceiptRoundTrip()
        {
            Settings.UseTransientStoreForCurrentProcess();
            string value = "absent"; int writes = 0;
            Func<string> read = delegate { return value; };
            Func<string, bool> write = delegate(string next) {
                PerformanceEq(true, Settings.LoadStr(PerformanceActionStore.ListKey, "").Contains(PerformanceActionStore.Slot("fixture", "C:\\游戏;'test.exe")));
                PerformanceEq(true, Settings.LoadStr(PerformanceActionStore.Slot("fixture", "C:\\游戏;'test.exe"), "").Length > 0);
                writes++; value = next; return true;
            };
            Func<string, bool> match = delegate(string state) { return state == "d:1"; };
            PerformanceEq(true, PerformanceActionStore.Change("fixture", "C:\\游戏;'test.exe", "d:1", read, write, match));
            PerformanceEq(true, PerformanceActionStore.Change("fixture", "C:\\游戏;'test.exe", "d:1", read, write, match));
            PerformanceEq(1, writes);
            var receipt = PerformanceActionStore.All()[0];
            PerformanceEq("absent", receipt.Original); PerformanceEq(true, receipt.Committed);
            PerformanceEq(true, PerformanceActionStore.Restore(receipt, read, write, match));
            PerformanceEq("absent", value); PerformanceEq(0, PerformanceActionStore.All().Count);
            PerformanceEq("", Settings.LoadStr(PerformanceActionStore.ListKey, ""));
            value = "d:1";
            PerformanceEq(true, PerformanceActionStore.Change("fixture", "C:\\游戏;'test.exe", "d:1", read, write, match));
            PerformanceEq(0, PerformanceActionStore.All().Count);
            value = "d:0";
            PerformanceEq(true, PerformanceActionStore.Change("fixture", "C:\\游戏;'test.exe", "d:1", read, write, match));
            value = "d:7"; writes = 0;
            PerformanceEq(true, PerformanceActionStore.Restore(PerformanceActionStore.All()[0], read, write, match));
            PerformanceEq(0, writes); PerformanceEq("d:7", value);
        }
        private static void ActionReceiptFailureBoundaries()
        {
            Settings.UseTransientStoreForCurrentProcess(); string value = "d:0"; int writes = 0;
            Func<string> read = delegate { return value; };
            Func<string, bool> match = delegate(string state) { return state == "d:1"; };
            Func<string, bool> partial = delegate(string next) { writes++; value = next; return false; };
            PerformanceEq(false, PerformanceActionStore.Change("partial", "x", "d:1", read, partial, match));
            PerformanceEq(false, PerformanceActionStore.All()[0].Committed);
            PerformanceEq(true, PerformanceActionStore.Restore(PerformanceActionStore.All()[0], read,
                delegate(string next) { value = next; return true; }, match));
            PerformanceEq("d:0", value);
            PerformanceEq(true, PerformanceActionStore.Change("partial", "x", "d:1", read,
                delegate(string next) { value = next; return true; }, match));
            bool threw = false;
            try { PerformanceActionStore.Restore(PerformanceActionStore.All()[0], delegate { throw new IOException("read failed"); }, partial, match); }
            catch (IOException) { threw = true; }
            PerformanceEq(true, threw); PerformanceEq(1, PerformanceActionStore.All().Count);
            Settings.UseTransientStoreForCurrentProcess(); Settings.SuspendWritesForReset(); writes = 0; value = "d:0";
            PerformanceEq(false, PerformanceActionStore.Change("blocked", "x", "d:1", read, partial, match));
            PerformanceEq(0, writes);
            Settings.UseTransientStoreForCurrentProcess();
            string slot = PerformanceActionStore.Slot("corrupt", "x");
            Settings.SaveStr(PerformanceActionStore.ListKey, slot); Settings.SaveStr(slot, "corrupt");
            threw = false;
            try { PerformanceActionStore.All(); } catch (FormatException) { threw = true; }
            PerformanceEq(true, threw); PerformanceEq("corrupt", Settings.LoadStr(slot, ""));
        }
        private static void ActionComponentRestore()
        {
            Settings.UseTransientStoreForCurrentProcess();
            var values = new Dictionary<string, string>();
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) { return values[id]; };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) { values[id] = value; return true; };
            string idDef = PerformanceActionCatalog.LegacyDefenderSession;
            values[idDef] = "False,False";
            bool refused = false;
            try { PerformanceActionBackend.Apply(idDef, "Defender", "True,True"); }
            catch (InvalidOperationException) { refused = true; }
            PerformanceEq(true, refused); PerformanceEq("False,False", values[idDef]); PerformanceEq(0, PerformanceActionStore.All().Count);
            SeedLegacyDefenderReceipt();
            values[idDef] = "False,True";
            PerformanceEq(true, PerformanceActionBackend.Restore(PerformanceActionStore.All()[0]));
            PerformanceEq("False,False", values[idDef]);
            string idMit = PerformanceActionCatalog.Mitigations;
            string original = "ON:True;NOTSET:False;OFF:False;ON:False;NOTSET:False;ON:False";
            string applied = "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;ON:False";
            values[idMit] = original;
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                values[id] = value == "off64" ? applied : value; return true;
            };
            PerformanceEq(true, PerformanceActionBackend.Apply(idMit, "C:\\Game.exe", "off64"));
            values[idMit] = "OFF:False;ON:True;OFF:False;OFF:False;OFF:False;ON:False";
            PerformanceEq(true, PerformanceActionBackend.Restore(PerformanceActionStore.All()[0]));
            PerformanceEq("ON:True;ON:True;OFF:False;ON:False;NOTSET:False;ON:False", values[idMit]);
            PerformanceEq(0, PerformanceActionStore.All().Count);
        }
        private static void ActionRoutingAndCommands()
        {
            int automatic = 0, environment = 0;
            foreach (PerformanceAction action in PerformanceActionCatalog.All)
            {
                if (action.Environment) environment++; else automatic++;
                PerformanceEq(false, action.Key.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0);
                PerformanceEq(false, action.Key.IndexOf("IdleDisable", StringComparison.OrdinalIgnoreCase) >= 0);
                PerformanceEq(false, action.Key == PerformanceActionCatalog.LegacyDefenderSession);
                PerformanceEq(false, action.Key == PerformanceActionCatalog.DefenderResources);
            }
            PerformanceEq(5, automatic); PerformanceEq(7, environment);
            PerformanceEq(true, PerformanceActionCatalog.Find(PerformanceActionCatalog.LegacyDefenderSession) != null);
            PerformanceEq(true, PerformanceActionCatalog.Find(PerformanceActionCatalog.DefenderResources) != null);
            foreach (string legacy in new[] { "False,False", "False,True", "True,False", "True,True" })
                PerformanceEq(false, PerformanceActionBackend.LegacyDefenderRestoreScript(legacy).Contains("$true"));
            PerformanceEq("Set-MpPreference -DisableRealtimeMonitoring $false -DisableBehaviorMonitoring $false -ErrorAction Stop",
                PerformanceActionBackend.LegacyDefenderRestoreScript("False,False"));
            PerformanceEq("Set-MpPreference -DisableBehaviorMonitoring $false -ErrorAction Stop", PerformanceActionBackend.LegacyDefenderRestoreScript("True,False"));
            PerformanceEq("", PerformanceActionBackend.LegacyDefenderRestoreScript("True,True"));
            PerformanceEq(null, PerformanceActionBackend.LegacyDefenderRestoreScript("True"));
            PerformanceEq(false, PerformanceActionCatalog.Find(PerformanceActionCatalog.Mitigations).Environment);
            PerformanceEq(true, PerformanceActionCatalog.Find(PerformanceActionCatalog.CpuMitigations).Environment);
            PerformanceEq(0x10835016u, NvApi.SettingFrlFpsBackground);
            PerformanceEq(0x20C1221Eu, NvApi.SettingOpenGlThreadControl);
            string command = PerformanceActionBackend.MitigationWrite("C:\\it's a game.exe", "off64");
            PerformanceEq(true, command.Contains("'C:\\it''s a game.exe'"));
            PerformanceEq(true, command.Contains("::Apply($a, 'C:\\it''s a game.exe', $false)"));
            PerformanceEq(false, command.Contains("-System"));
            PerformanceEq(true, PerformanceActionBackend.MitigationWrite("C:\\Game.exe", "off32").Contains("::Apply($a, 'C:\\Game.exe', $true)"));
            string restore = PerformanceActionBackend.MitigationWrite("C:\\Game.exe", "ON:True;OFF:False;NOTSET:False;OFF:True;ON:False;NOTSET:False");
            PerformanceEq(true, restore.Contains("::Restore($a, 'C:\\Game.exe', [string[]]@('ON:True','OFF:False','NOTSET:False','OFF:True','ON:False','NOTSET:False'))"));
            PerformanceEq(false, restore.Contains(" -Reset "));
            PerformanceEq(false, restore.Contains(" -Remove "));
            string longCommand = PerformanceActionBackend.MitigationWrite("C:\\" + new string('x', 2000) + ".exe", "off64");
            PerformanceEq(true, Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(longCommand)).Length + 1024 < 32767);
            bool tooLong = false;
            try { PerformanceActionBackend.MitigationWrite("C:\\" + new string('x', 8000) + ".exe", "off64"); }
            catch (NotSupportedException) { tooLong = true; }
            PerformanceEq(true, tooLong);
            bool badSnapshot = false;
            try { PerformanceActionBackend.MitigationWrite("C:\\Game.exe", "NOTSET:False;OFF:False"); }
            catch (FormatException) { badSnapshot = true; }
            PerformanceEq(true, badSnapshot);
            PerformanceEq("d:259", PerformanceActionBackend.WantedRegistry(PerformanceActionCatalog.CpuMitigations, "d:256"));
            PerformanceEq("d:294", PerformanceActionBackend.WantedRegistry(PerformanceActionCatalog.Quantum, "d:256"));
            PerformanceEq("b:AAAAAA==", PerformanceActionBackend.WantedRegistry(PerformanceActionCatalog.AudioIdle, "b:AQAAAA=="));
            PerformanceEq(false, PerformanceActionBackend.Apply(PerformanceActionCatalog.DefenderResources, @"C:\Fixture\Game", "1"));
            PerformanceEq(false, PerformanceActionBackend.Write(PerformanceActionCatalog.DefenderResources, @"C:\Fixture\Game", "1"));
            bool rejected = false;
            try { PerformanceActionBackend.WantedRegistry(PerformanceActionCatalog.CpuMitigations, "b:AAAAAA=="); } catch (FormatException) { rejected = true; }
            PerformanceEq(true, rejected);
            rejected = false;
            try { PerformanceActionBackend.WantedRegistry(PerformanceActionCatalog.AudioIdle, "b:AA=="); } catch (FormatException) { rejected = true; }
            PerformanceEq(true, rejected);
        }
        private static void ActionEnvironmentRebootState()
        {
            Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
            string boot = BootSessionIdentity.Create(Guid.NewGuid(), 1);
            PerformanceTuning.BootForTest = delegate { return boot; };
            string value = "d:256";
            PerformanceActionBackend.TargetsForTest = delegate { return new[] { "fake registry" }; };
            PerformanceActionBackend.ReadForTest = delegate { return value; };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string next) { value = next; return true; };
            string actionId = PerformanceActionCatalog.CpuMitigations;
            PerformanceEq(true, PerformanceTuning.ApplyEnvironment(actionId, true));
            PerformanceEq("d:259", value); PerformanceEq("pending-reboot", PerformanceTuning.State(actionId));
            PerformanceEq(true, PerformanceTuning.ApplyEnvironment(actionId, true));
            PerformanceEq("pending-reboot", PerformanceTuning.State(actionId));
            string[] fields = boot.Split(':'); boot = fields[0] + ":" + fields[1] + ":2";
            PerformanceEq("ready", PerformanceTuning.State(actionId));
            PerformanceEq(true, PerformanceTuning.ApplyEnvironment(actionId, false));
            PerformanceEq("d:256", value); PerformanceEq("restore-pending-reboot", PerformanceTuning.State(actionId));
            boot = null; PerformanceEq("unverified", PerformanceTuning.State(actionId));
            PerformanceActionBackend.TargetsForTest = delegate { return new string[0]; };
            PerformanceEq(false, PerformanceTuning.ApplyEnvironment(PerformanceActionCatalog.AudioIdle, true));
            PerformanceEq("unsupported", PerformanceTuning.State(PerformanceActionCatalog.AudioIdle));
            PerformanceTuning.ResetForTest();
        }
        private static void ActionImageCollisions()
        {
            using (var fixture = new AutomaticFsoFixture())
            {
                var method = typeof(GameMode).GetMethod("BackgroundFpsProtectedLocked", FsoHidden);
                PerformanceEq(true, (bool)method.Invoke(fixture.Mode, new object[] { "GameOne", @"C:\Different\GameOne.exe" }));
                PerformanceEq(false, (bool)method.Invoke(fixture.Mode, new object[] { "BackgroundBrowser", @"C:\Different\BackgroundBrowser.exe" }));
                WhitelistRule rule; PerformanceEq(true, WhitelistRule.TryCreate(WhitelistRuleKind.ExactPath, @"C:\Work\Browser.exe", out rule));
                var rules = (List<WhitelistRule>)typeof(GameMode).GetField("whiteRules", FsoHidden).GetValue(fixture.Mode);
                rules.Add(rule);
                PerformanceEq(true, (bool)method.Invoke(fixture.Mode, new object[] { "Browser", @"C:\Different\Browser.exe" }));
            }
        }
        private static void ActionRetiredFailureUi()
        {
            Settings.UseTransientStoreForCurrentProcess();
            foreach (string id in new[] { PerformanceActionCatalog.DefenderResources, PerformanceActionCatalog.LegacyDefenderSession })
                ActionFailureStore.Trip(id, "legacy", "old failure");
            ActionFailureStore.Trip(PerformanceActionCatalog.OpenGl, "current", "current failure");
            List<MaintenanceListItem> items = PanelForm.ReadFailureItems();
            PerformanceEq(3, items.Count);
            foreach (MaintenanceListItem item in items)
            {
                bool retired = item.Key == ActionFailureStore.RecordKey(PerformanceActionCatalog.DefenderResources, "legacy")
                    || item.Key == ActionFailureStore.RecordKey(PerformanceActionCatalog.LegacyDefenderSession, "legacy");
                PerformanceEq(!retired, item.Retryable);
                if (retired) PerformanceEq(true, item.Status.Contains("仅恢复") || item.Status.Contains("restore legacy"));
            }
            Settings.UseTransientStoreForCurrentProcess();
        }
        private static void ActionEnvironmentPartialFailure()
        {
            Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
            string boot = BootSessionIdentity.Create(Guid.NewGuid(), 1);
            PerformanceTuning.BootForTest = delegate { return boot; };
            string good = "d:256";
            PerformanceActionBackend.TargetsForTest = delegate { return new[] { "bad", "good" }; };
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) { return target == "bad" ? "b:AAAAAA==" : good; };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) { good = value; return true; };
            PerformanceEq(false, PerformanceTuning.ApplyEnvironment(PerformanceActionCatalog.CpuMitigations, true));
            PerformanceEq("d:256", good); PerformanceEq("stopped", PerformanceTuning.State(PerformanceActionCatalog.CpuMitigations));
            PerformanceEq(true, PerformanceTuning.RestoreAll()); PerformanceEq("d:256", good);
            PerformanceTuning.ResetForTest();
        }
        private static void ActionEnvironmentRetryStatus()
        {
            Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
            string boot = BootSessionIdentity.Create(Guid.NewGuid(), 1), value = "d:256";
            PerformanceTuning.BootForTest = delegate { return boot; };
            PerformanceActionBackend.TargetsForTest = delegate { return new[] { "device" }; };
            PerformanceActionBackend.ReadForTest = delegate { return value; };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string next) { value = next; return false; };
            string key = PerformanceActionCatalog.CpuMitigations;
            PerformanceEq(false, PerformanceTuning.ApplyEnvironment(key, true));
            PerformanceEq("d:256", value); PerformanceEq("restore-failed", PerformanceTuning.State(key));
            PerformanceEq(false, PerformanceTuning.ApplyEnvironment(key, true));
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string next) { value = next; return true; };
            PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, false));
            PerformanceEq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(key, "system")));
            PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, true));
            PerformanceEq("pending-reboot", PerformanceTuning.State(key));
            PerformanceEq(1, PerformanceActionStore.All().Count);
            string[] fields = boot.Split(':'); boot = fields[0] + ":" + fields[1] + ":2";
            PerformanceEq("ready", PerformanceTuning.State(key));
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string next) { value = next; return true; };
            PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, false));
            value = "d:259"; PerformanceTuning.SetState(key, "failed");
            PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, true));
            PerformanceEq("external", PerformanceTuning.State(key));
            PerformanceEq(0, PerformanceActionStore.All().Count);
            PerformanceTuning.ResetForTest();
        }
        private static void ActionEnvironmentVbs()
        {
            Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
            string boot = BootSessionIdentity.Create(Guid.NewGuid(), 1);
            PerformanceTuning.BootForTest = delegate { return boot; };
            bool configured = false, fail = false; int writes = 0;
            PerformanceTuning.VbsConfiguredForTest = delegate { return configured; };
            PerformanceTuning.VbsChangeForTest = delegate(bool on) {
                PerformanceEq(boot, Settings.LoadStr(PerformanceTuning.EnvironmentVbsKey + "Boot", ""));
                writes++;
                if (on)
                {
                    Settings.SaveStr("PrevHvLaunch", "auto"); configured = true;
                    if (fail) return false;
                    Settings.Save("VbsDisabledByPavise", true);
                }
                else
                {
                    if (fail) return false;
                    configured = false; Settings.Save("VbsDisabledByPavise", false); Settings.SaveStr("PrevHvLaunch", "");
                }
                return true;
            };
            string key = PerformanceTuning.EnvironmentVbsKey;
            PerformanceEq(true, PerformanceTuning.ApplyEnvironmentVbs(false)); PerformanceEq(0, writes);
            configured = true;
            PerformanceEq(true, PerformanceTuning.ApplyEnvironmentVbs(true));
            PerformanceEq("external", PerformanceTuning.State(key)); PerformanceEq(false, VbsTweak.OwnsState);
            PerformanceEq(true, PerformanceTuning.ApplyEnvironmentVbs(false)); PerformanceEq(true, configured); PerformanceEq(0, writes);
            configured = false;
            PerformanceEq(true, PerformanceTuning.ApplyEnvironmentVbs(true)); PerformanceEq(1, writes);
            PerformanceEq("pending-reboot", PerformanceTuning.State(key)); PerformanceEq(true, VbsTweak.OwnsState);
            PerformanceEq(true, PerformanceTuning.ApplyEnvironmentVbs(true)); PerformanceEq(1, writes);
            string[] fields = boot.Split(':'); boot = fields[0] + ":" + fields[1] + ":2";
            PerformanceEq("ready", PerformanceTuning.State(key));
            string savedBoot = boot; boot = null;
            PerformanceEq(false, PerformanceTuning.ApplyEnvironmentVbs(false)); PerformanceEq(1, writes);
            PerformanceEq(true, VbsTweak.OwnsState); PerformanceEq("restore-failed", PerformanceTuning.State(key));
            boot = savedBoot;
            PerformanceEq(true, PerformanceTuning.ApplyEnvironmentVbs(false)); PerformanceEq(2, writes);
            PerformanceEq(false, configured); PerformanceEq(false, VbsTweak.OwnsState);
            PerformanceEq("restore-pending-reboot", PerformanceTuning.State(key));
            boot = fields[0] + ":" + fields[1] + ":3"; PerformanceEq("restored", PerformanceTuning.State(key));
            fail = true;
            PerformanceEq(false, PerformanceTuning.ApplyEnvironmentVbs(true)); PerformanceEq("restore-failed", PerformanceTuning.State(key));
            PerformanceEq(true, VbsTweak.OwnsState);
            int partialWrites = writes;
            PerformanceEq(false, PerformanceTuning.ApplyEnvironmentVbs(true));
            PerformanceEq("restore-failed", PerformanceTuning.State(key)); PerformanceEq(partialWrites, writes);
            PerformanceEq(false, PerformanceTuning.ApplyEnvironmentVbs(false)); PerformanceEq("restore-failed", PerformanceTuning.State(key));
            fail = false;
            PerformanceEq(true, PerformanceTuning.ApplyEnvironmentVbs(false)); PerformanceEq(false, VbsTweak.OwnsState);
            PerformanceEq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(key, "system")));
            int before = writes; Settings.SuspendWritesForReset();
            PerformanceEq(false, PerformanceTuning.ApplyEnvironmentVbs(true)); PerformanceEq(before, writes);
            PerformanceEq(true, VbsTweak.ShouldRestoreHvLaunch("off", "auto"));
            PerformanceEq(true, VbsTweak.ShouldRestoreHvLaunch("off", ReversibleReg.Absent));
            PerformanceEq(false, VbsTweak.ShouldRestoreHvLaunch("off", "off"));
            PerformanceEq(false, VbsTweak.ShouldRestoreHvLaunch("on", "auto"));
            PerformanceEq(false, VbsTweak.ShouldRestoreHvLaunch("auto", "on"));
            PerformanceTuning.ResetForTest(); Settings.UseTransientStoreForCurrentProcess();
        }
        private static void ActionEnvironmentBundle()
        {
            Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
            string boot = BootSessionIdentity.Create(Guid.NewGuid(), 1);
            PerformanceTuning.BootForTest = delegate { return boot; };
            var values = new Dictionary<string, string>(); var originals = new Dictionary<string, string>();
            var valueGate = new object(); int writes = 0; bool failRead = false, failRestore = false;
            bool tickOn = false, globalOn = false, tickOwned = false, globalOwned = false;
            int timerWrites = 0;
            bool vbsOff = false; int vbsWrites = 0;
            PerformanceTuning.VbsConfiguredForTest = delegate { return vbsOff; };
            PerformanceTuning.VbsChangeForTest = delegate(bool on) {
                Interlocked.Increment(ref vbsWrites); vbsOff = on; Settings.Save("VbsDisabledByPavise", on); return true;
            };
            var timers = new TimerModeController {
                Boot = delegate { return boot; }, OsBuild = delegate { return 26000; },
                TickOn = delegate { return tickOn; }, TickOwned = delegate { return tickOwned; },
                GlobalOn = delegate { return globalOn; }, GlobalOwned = delegate { return globalOwned; },
                TickEnable = delegate { Interlocked.Increment(ref timerWrites); tickOwned = tickOn = true; return true; },
                GlobalEnable = delegate { Interlocked.Increment(ref timerWrites); globalOwned = globalOn = true; return true; },
                TickRestore = delegate { Interlocked.Increment(ref timerWrites); tickOwned = tickOn = false; return true; },
                GlobalRestore = delegate { Interlocked.Increment(ref timerWrites); globalOwned = globalOn = false; return true; }
            };
            PerformanceActionBackend.TargetsForTest = delegate(string id) {
                if (id == PerformanceActionCatalog.StorageCold) return new string[0];
                return id == PerformanceActionCatalog.CpuMitigations ? new[] { "override", "mask" } : new[] { "device" };
            };
            foreach (PerformanceAction action in PerformanceActionCatalog.All)
                if (action.Environment) foreach (string target in PerformanceActionBackend.TargetsForTest(action.Key))
                {
                    string original = action.Key == PerformanceActionCatalog.AudioIdle ? "b:AQAAAA=="
                        : action.Key == PerformanceActionCatalog.CpuMitigations ? (target == "mask" ? "d:8" : "d:128")
                        : action.Key == PerformanceActionCatalog.Quantum ? "d:256" : action.Key == PerformanceActionCatalog.StorageRuntime ? "d:1" : "d:0";
                    values[action.Key + "|" + target] = original; originals[action.Key + "|" + target] = original;
                }
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                if (failRead && id == PerformanceActionCatalog.CpuMitigations && target == "override") throw new IOException("Injected bundle read failure");
                lock (valueGate) return values[id + "|" + target];
            };
            var blocked = new ManualResetEvent(false); var release = new ManualResetEvent(false); var done = new ManualResetEvent(false);
            bool holdWrite = true;
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                if (holdWrite) { blocked.Set(); release.WaitOne(3000); }
                Interlocked.Increment(ref writes);
                if (failRestore && id == PerformanceActionCatalog.CpuMitigations && target == "override" && value == "d:128") return false;
                lock (valueGate) values[id + "|" + target] = value; return true;
            };
            try
            {
                PerformanceEq(false, PerformanceTuning.EnvironmentBundleEnabled);
                PerformanceEq("off", PerformanceTuning.EnvironmentBundleState());
                Settings.Save("TimerTickByPavise", true); Settings.Save("GlobalTimerResByPavise", true);
                PerformanceEq(0, PerformanceTuning.EnvironmentEnabledCount());
                PerformanceEq("off", PerformanceTuning.EnvironmentBundleState());
                PerformanceEq(true, AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyTimerTick));
                PerformanceEq(true, AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyGlobalTimer));
                Settings.Save("VbsDisabledByPavise", true);
                PerformanceEq(1, PerformanceTuning.EnvironmentEnabledCount());
                PerformanceEq("partial", PerformanceTuning.EnvironmentBundleState()); PerformanceEq(0, timerWrites);
                Settings.Remove("TimerTickByPavise"); Settings.Remove("GlobalTimerResByPavise");
                Settings.Remove("VbsDisabledByPavise");
                Settings.Save(PolicyCatalog.KeyTimerTick, true); Settings.Save(PolicyCatalog.KeyGlobalTimer, false);
                timers.SyncEnvironmentPreferences();
                PerformanceEq(true, tickOn); PerformanceEq(false, globalOn); PerformanceEq(1, timerWrites);
                string tickMarker = Settings.LoadStr("AutoTimerTickBoot", "");
                Settings.Save(PerformanceActionCatalog.CpuMitigations, true);
                PerformanceEq(true, PerformanceTuning.EnvironmentBundleEnabled);
                PerformanceEq("partial", PerformanceTuning.EnvironmentBundleState());
                PerformanceEq(1, PerformanceTuning.EnvironmentEnabledCount()); PerformanceEq(0, writes);
                PerformanceEq(true, PerformanceTuning.SetEnvironmentBundle(true, delegate { done.Set(); }));
                PerformanceEq(true, blocked.WaitOne(3000));
                PerformanceEq(4, PerformanceTuning.EnvironmentEnabledCount()); PerformanceEq(true, PerformanceTuning.EnvironmentBundleBusy());
                PerformanceEq(false, Settings.LoadCached(PerformanceActionCatalog.Quantum, false));
                foreach (string storage in PerformanceTuning.StorageMembers())
                    PerformanceEq(false, Settings.LoadCached(storage, false));
                PerformanceEq(true, Settings.LoadCached(PerformanceTuning.EnvironmentVbsKey, false)); PerformanceEq(0, vbsWrites);
                PerformanceEq(true, AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyTimerTick));
                PerformanceEq(false, AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyGlobalTimer));
                PerformanceEq("working", PerformanceTuning.EnvironmentBundleState());
                PerformanceEq(false, PerformanceTuning.SetEnvironmentBundle(false, null));
                PerformanceEq(false, PerformanceTuning.SetEnvironment(PerformanceActionCatalog.Quantum, false, null));
                PerformanceEq(false, PerformanceTuning.RestoreAll());
                timers.SyncEnvironmentPreferences();
                PerformanceEq(true, tickOn); PerformanceEq(false, globalOn); PerformanceEq(1, timerWrites);
                PerformanceEq(false, vbsOff); PerformanceEq(0, vbsWrites);
                holdWrite = false; release.Set(); PerformanceEq(true, done.WaitOne(3000));
                PerformanceEq("pending-reboot", PerformanceTuning.EnvironmentBundleState());
                PerformanceEq(true, tickOn); PerformanceEq(false, globalOn); PerformanceEq(1, timerWrites);
                PerformanceEq(tickMarker, Settings.LoadStr("AutoTimerTickBoot", ""));
                PerformanceEq(true, vbsOff); PerformanceEq(1, vbsWrites);
                timers.SyncEnvironmentPreferences(); PerformanceEq(1, timerWrites);
                foreach (string timerState in new[] { "failed", "restore-failed", "pending-reboot", "restore-pending-reboot", "unverified", "unsupported" })
                {
                    timers.TickState = timers.GlobalState = timerState;
                    PerformanceEq("pending-reboot", PerformanceTuning.EnvironmentBundleState());
                }
                // 存储三项已移出整包：整包开启不读写、不记录它们。
                foreach (string storage in PerformanceTuning.StorageMembers())
                {
                    PerformanceEq("", PerformanceTuning.State(storage)); PerformanceEq(false, Settings.LoadCached(storage, false));
                }
                PerformanceEq(originals[PerformanceActionCatalog.StorageIdle + "|device"], values[PerformanceActionCatalog.StorageIdle + "|device"]);
                PerformanceEq("off", PerformanceTuning.StorageGroupState());
                PerformanceEq("d:131", values[PerformanceActionCatalog.CpuMitigations + "|override"]);
                PerformanceEq("d:11", values[PerformanceActionCatalog.CpuMitigations + "|mask"]);
                PerformanceEq("b:AAAAAA==", values[PerformanceActionCatalog.AudioIdle + "|device"]);
                PerformanceEq(4, PerformanceActionStore.All().Count);
                PerformanceEq("d:256", values[PerformanceActionCatalog.Quantum + "|device"]);
                string[] bootFields = boot.Split(':'); boot = bootFields[0] + ":" + bootFields[1] + ":2";
                timers.Sync(true, false, 26000, DateTime.UtcNow.AddMinutes(6).Ticks);
                PerformanceEq("ready", PerformanceTuning.EnvironmentBundleState());
                done.Reset(); PerformanceEq(true, PerformanceTuning.SetEnvironmentBundle(false, delegate { done.Set(); }));
                PerformanceEq(true, done.WaitOne(3000)); PerformanceEq(false, PerformanceTuning.EnvironmentBundleEnabled);
                PerformanceEq(true, tickOn); PerformanceEq(false, globalOn); PerformanceEq(1, timerWrites);
                PerformanceEq(true, AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyTimerTick));
                PerformanceEq(false, AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyGlobalTimer));
                PerformanceEq(false, vbsOff); PerformanceEq(2, vbsWrites);
                foreach (var original in originals) PerformanceEq(original.Value, values[original.Key]);
                PerformanceEq(0, PerformanceActionStore.All().Count);
                PerformanceEq("restore-pending-reboot", PerformanceTuning.EnvironmentBundleState());
                boot = bootFields[0] + ":" + bootFields[1] + ":3";
                timers.Sync(true, false, 26000, DateTime.UtcNow.AddMinutes(6).Ticks);
                PerformanceEq("restored", PerformanceTuning.EnvironmentBundleState());
                failRead = true; done.Reset();
                PerformanceEq(true, PerformanceTuning.SetEnvironmentBundle(true, delegate { done.Set(); }));
                PerformanceEq(true, done.WaitOne(3000)); PerformanceEq("stopped", PerformanceTuning.EnvironmentBundleState());
                PerformanceEq(true, PerformanceTuning.EnvironmentBundleLabel().Contains("1"));
                PerformanceEq(true, PerformanceTuning.EnvironmentBundleDetail.Contains("CPU"));
                PerformanceEq(false, PerformanceTuning.EnvironmentBundleDetail.Contains("恢复未完成项"));
                PerformanceEq("d:1", values[PerformanceActionCatalog.LastAccess + "|device"]);
                PerformanceEq("d:256", values[PerformanceActionCatalog.Quantum + "|device"]);
                failRead = false; done.Reset();
                PerformanceEq(true, PerformanceTuning.SetEnvironmentBundle(false, delegate { done.Set(); }));
                PerformanceEq(true, done.WaitOne(3000)); PerformanceEq(0, PerformanceActionStore.All().Count);
                PerformanceEq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(PerformanceActionCatalog.CpuMitigations, "system")));
                done.Reset();
                PerformanceEq(true, PerformanceTuning.SetEnvironmentBundle(true, delegate { done.Set(); }));
                PerformanceEq(true, done.WaitOne(3000));
                failRestore = true; done.Reset();
                PerformanceEq(true, PerformanceTuning.SetEnvironmentBundle(false, delegate { done.Set(); }));
                PerformanceEq(true, done.WaitOne(3000)); PerformanceEq("restore-failed", PerformanceTuning.EnvironmentBundleState());
                PerformanceEq(true, PerformanceTuning.EnvironmentBundleDetail.Contains("CPU"));
                PerformanceEq(1, PerformanceActionStore.All().Count);
                PerformanceEq(true, tickOwned); PerformanceEq(false, globalOwned); PerformanceEq(1, timerWrites);
                failRestore = false;
                done.Reset(); PerformanceEq(true, PerformanceTuning.SetEnvironmentBundle(false, delegate { done.Set(); }));
                PerformanceEq(true, done.WaitOne(3000));
                PerformanceEq(0, PerformanceActionStore.All().Count);
                PerformanceEq("restore-pending-reboot", PerformanceTuning.EnvironmentBundleState());
                PerformanceEq(true, tickOn); PerformanceEq(false, globalOn); PerformanceEq(1, timerWrites);
                int before = writes, timersBefore = timerWrites, vbsBefore = vbsWrites; Settings.SuspendWritesForReset();
                PerformanceEq(false, PerformanceTuning.SetEnvironmentBundle(true, null)); PerformanceEq(before, writes);
                PerformanceEq(timersBefore, timerWrites);
                PerformanceEq(vbsBefore, vbsWrites);
            }
            finally
            {
                release.Set(); if (PerformanceTuning.EnvironmentBundleBusy()) done.WaitOne(3000);
                blocked.Dispose(); release.Dispose(); done.Dispose();
                PerformanceTuning.ResetForTest(); Settings.UseTransientStoreForCurrentProcess();
            }
        }
        private static void ActionQuantumProfiles()
        {
            Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
            const string key = PerformanceActionCatalog.Quantum;
            string value = "d:257";
            PerformanceTuning.BootForTest = delegate { return BootSessionIdentity.Create(new Guid("8746e9d4-5e94-42dd-80e7-2673d998076c"), 1); };
            PerformanceActionBackend.TargetsForTest = delegate { return new[] { "quantum" }; };
            PerformanceActionBackend.ReadForTest = delegate { return value; };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string next) { value = next; return true; };
            PerformanceEq(0, QuantumProfiles.Selected);
            Settings.Save(key, true); PerformanceEq(1, QuantumProfiles.Selected);
            Settings.Save(key, false);
            using (var done = new ManualResetEvent(false)) {
                for (int index = 1; index < QuantumProfiles.Values.Length; index++) {
                    done.Reset();
                    PerformanceEq(true, PerformanceTuning.SetQuantumProfile(index, delegate { done.Set(); }));
                    PerformanceEq(true, done.WaitOne(3000));
                    PerformanceEq(index, QuantumProfiles.Selected);
                    PerformanceEq("d:" + (256 | QuantumProfiles.Values[index]), value);
                    PerformanceEq("d:257", PerformanceActionStore.All()[0].Original);
                    PerformanceEq("pending-reboot", PerformanceTuning.State(key));
                }
                PerformanceEq(false, PerformanceTuning.SetQuantumProfile(-1, null));
                PerformanceEq(false, PerformanceTuning.SetQuantumProfile(QuantumProfiles.Values.Length, null));
                done.Reset(); PerformanceEq(true, PerformanceTuning.SetQuantumProfile(0, delegate { done.Set(); }));
                PerformanceEq(true, done.WaitOne(3000));
                PerformanceEq("d:257", value); PerformanceEq(0, QuantumProfiles.Selected);
                PerformanceEq(0, PerformanceActionStore.All().Count);
                PerformanceEq("restore-pending-reboot", PerformanceTuning.State(key));
            }
            Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
        }
        private static void ActionEnvironmentQuantumIndependent()
        {
            const string key = PerformanceActionCatalog.Quantum, target = "legacy quantum target";
            foreach (string original in new[] { "absent", "d:256", "d:319" })
                foreach (bool committed in new[] { false, true })
                {
                    Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
                    string boot = BootSessionIdentity.Create(Guid.NewGuid(), 1);
                    PerformanceTuning.BootForTest = delegate { return boot; };
                    string expected = PerformanceActionBackend.WantedRegistry(key, original), value = expected;
                    int writes = 0; bool failRestore = false;
                    PerformanceActionBackend.TargetsForTest = delegate(string id) { return id == key ? new[] { target } : new string[0]; };
                    PerformanceActionBackend.ReadForTest = delegate(string id, string name) {
                        PerformanceEq(key, id); PerformanceEq(target, name); return value;
                    };
                    PerformanceActionBackend.WriteForTest = delegate(string id, string name, string next) {
                        PerformanceEq(key, id); PerformanceEq(target, name); writes++;
                        if (failRestore) return false;
                        value = next; return true;
                    };
                    PerformanceTuning.VbsConfiguredForTest = delegate { return true; };
                    PerformanceTuning.VbsChangeForTest = delegate { throw new InvalidOperationException("Quantum fixture must not write VBS"); };
                    var receipt = new PerformanceActionStore.Receipt { Id = key, Target = target, Original = original,
                        Expected = expected, Committed = committed };
                    string slot = PerformanceActionStore.Slot(key, target), snapshot = PerformanceActionStore.Pack(receipt);
                    Settings.SaveStr(slot, snapshot); Settings.SaveStr(PerformanceActionStore.ListKey, slot);
                    Settings.Save(key, true); Settings.SaveStr(key + "Boot", boot); Settings.SaveStr(key + "State", "configured");
                    using (var done = new ManualResetEvent(false))
                    {
                        PerformanceEq(0, PerformanceTuning.EnvironmentEnabledCount());
                        PerformanceEq(false, PerformanceTuning.EnvironmentBundleEnabled);
                        PerformanceEq("off", PerformanceTuning.EnvironmentBundleState());
                        PerformanceEq("pending-reboot", PerformanceTuning.State(key));
                        PerformanceEq(0, writes); PerformanceEq(snapshot, Settings.LoadStr(slot, ""));
                        foreach (bool on in new[] { true, false })
                        {
                            done.Reset(); PerformanceEq(true, PerformanceTuning.SetEnvironmentBundle(on, delegate { done.Set(); }));
                            PerformanceEq(true, done.WaitOne(3000));
                            PerformanceEq(true, Settings.LoadCached(key, false)); PerformanceEq(expected, value);
                            PerformanceEq(snapshot, Settings.LoadStr(slot, "")); PerformanceEq(0, writes);
                        }
                        done.Reset(); PerformanceEq(true, PerformanceTuning.SetEnvironment(key, true, delegate { done.Set(); }));
                        PerformanceEq(true, done.WaitOne(3000)); PerformanceEq(0, writes);
                        PerformanceEq(original, PerformanceActionStore.All()[0].Original);
                        PerformanceEq("pending-reboot", PerformanceTuning.State(key));
                        failRestore = true; done.Reset();
                        PerformanceEq(true, PerformanceTuning.SetEnvironment(key, false, delegate { done.Set(); }));
                        PerformanceEq(true, done.WaitOne(3000)); PerformanceEq("restore-failed", PerformanceTuning.State(key));
                        PerformanceEq(1, PerformanceActionStore.All().Count); PerformanceEq(expected, value);
                        PerformanceEq("off", PerformanceTuning.EnvironmentBundleState());
                        failRestore = false; done.Reset();
                        PerformanceEq(true, PerformanceTuning.SetEnvironment(key, false, delegate { done.Set(); }));
                        PerformanceEq(true, done.WaitOne(3000)); PerformanceEq(original, value);
                        PerformanceEq(0, PerformanceActionStore.All().Count);
                        PerformanceEq("restore-pending-reboot", PerformanceTuning.State(key));
                        PerformanceEq(false, Settings.LoadCached(key, true));
                        int restoredWrites = writes; done.Reset();
                        PerformanceEq(true, PerformanceTuning.SetEnvironment(key, false, delegate { done.Set(); }));
                        PerformanceEq(true, done.WaitOne(3000)); PerformanceEq(restoredWrites, writes);
                        PerformanceEq(true, PerformanceTuning.RestoreAll()); PerformanceEq(restoredWrites, writes);
                        string[] fields = boot.Split(':'); boot = fields[0] + ":" + fields[1] + ":2";
                        PerformanceEq("restored", PerformanceTuning.State(key));
                        PerformanceEq(true, PerformanceTuning.CloseEnvironmentWork(0));
                        PerformanceEq(false, PerformanceTuning.SetEnvironment(key, true, null));
                        PerformanceEq(false, Settings.LoadCached(key, true)); PerformanceEq(restoredWrites, writes);
                    }
                    PerformanceTuning.ResetForTest();
                }
        }
        // 持久存储三项：单独的高级开关。整包不再触及它们；旧版整包留下的选择与收据保持可见、不被静默恢复，可单独关闭恢复。
        private static void ActionEnvironmentStorageGroup()
        {
            Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
            string boot = BootSessionIdentity.Create(Guid.NewGuid(), 1);
            PerformanceTuning.BootForTest = delegate { return boot; };
            var values = new Dictionary<string, string>(); var originals = new Dictionary<string, string>(); var valueGate = new object();
            int writes = 0; bool holdWrite = false, vbsOff = false; string failRestore = null;
            PerformanceTuning.VbsConfiguredForTest = delegate { return vbsOff; };
            PerformanceTuning.VbsChangeForTest = delegate(bool on) { vbsOff = on; Settings.Save("VbsDisabledByPavise", on); return true; };
            PerformanceActionBackend.TargetsForTest = delegate(string id) {
                if (id == PerformanceActionCatalog.StorageCold) return new string[0];
                return id == PerformanceActionCatalog.CpuMitigations ? new[] { "override", "mask" } : new[] { "device" };
            };
            foreach (PerformanceAction action in PerformanceActionCatalog.All)
                if (action.Environment) foreach (string target in PerformanceActionBackend.TargetsForTest(action.Key))
                {
                    string original = action.Key == PerformanceActionCatalog.AudioIdle ? "b:AQAAAA=="
                        : action.Key == PerformanceActionCatalog.CpuMitigations ? (target == "mask" ? "d:8" : "d:128")
                        : action.Key == PerformanceActionCatalog.Quantum ? "d:256" : action.Key == PerformanceActionCatalog.StorageRuntime ? "d:1" : "d:0";
                    values[action.Key + "|" + target] = original; originals[action.Key + "|" + target] = original;
                }
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) { lock (valueGate) return values[id + "|" + target]; };
            var blocked = new ManualResetEvent(false); var release = new ManualResetEvent(false); var done = new ManualResetEvent(false);
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                if (holdWrite) { blocked.Set(); release.WaitOne(3000); }
                if (failRestore == id && value == originals[id + "|" + target]) return false;
                Interlocked.Increment(ref writes);
                lock (valueGate) values[id + "|" + target] = value; return true;
            };
            const string idle = PerformanceActionCatalog.StorageIdle + "|device";
            Func<int> storageReceipts = delegate {
                int count = 0;
                foreach (PerformanceActionStore.Receipt receipt in PerformanceActionStore.All())
                    if (PerformanceTuning.StorageMember(receipt.Id)) count++;
                return count;
            };
            Action<bool> setStorage = delegate(bool on) {
                done.Reset(); PerformanceEq(true, PerformanceTuning.SetStorageGroup(on, delegate { done.Set(); })); PerformanceEq(true, done.WaitOne(3000));
            };
            Action<bool> setBundle = delegate(bool on) {
                done.Reset(); PerformanceEq(true, PerformanceTuning.SetEnvironmentBundle(on, delegate { done.Set(); })); PerformanceEq(true, done.WaitOne(3000));
            };
            try
            {
                PerformanceEq(3, PerformanceTuning.StorageMembers().Count);
                PerformanceEq(false, PerformanceTuning.StorageGroupEnabled);
                PerformanceEq("off", PerformanceTuning.StorageGroupState());
                PerformanceEq(PerformanceTuning.Label("off"), PerformanceTuning.StorageGroupLabel());
                foreach (string name in new[] { AutomaticPolicy.Text("存储空闲电源", "Storage idle power"), AutomaticPolicy.Text("存储运行时电源", "Storage runtime power"), AutomaticPolicy.Text("存储 D3Cold", "Storage D3Cold") })
                    PerformanceEq(true, PerformanceTuning.StorageGroupDetail.Contains(name));
                PerformanceEq(true, PerformanceTuning.EnvironmentBundleDetail.Contains(AutomaticPolicy.Text("共四项", "four settings")));
                PerformanceEq(false, PerformanceTuning.EnvironmentBundleDetail.Contains(AutomaticPolicy.Text("七项", "seven")));
                Settings.Save(PerformanceActionCatalog.CpuMitigations, true);
                PerformanceEq(true, PerformanceTuning.EnvironmentBundleLabel().EndsWith("1/4"));
                Settings.Save(PerformanceActionCatalog.CpuMitigations, false);

                // 一键整包不再读写或记录存储三项。
                setBundle(true);
                PerformanceEq(0, storageReceipts()); PerformanceEq("off", PerformanceTuning.StorageGroupState());
                foreach (string key in PerformanceTuning.StorageMembers()) PerformanceEq(false, Settings.LoadCached(key, false));
                PerformanceEq(originals[idle], values[idle]);
                setBundle(false);

                // 旧版整包写下的存储偏好与收据：显示为已启用；整包关闭或开启都不静默恢复，也不丢失。
                foreach (string key in PerformanceTuning.StorageMembers()) { Settings.Save(key, true); PerformanceTuning.ApplyEnvironment(key, true); }
                PerformanceEq(1, storageReceipts()); PerformanceEq("d:1", values[idle]);
                PerformanceEq(true, PerformanceTuning.StorageGroupEnabled);
                PerformanceEq("pending-reboot", PerformanceTuning.StorageGroupState());
                setBundle(true); setBundle(false);
                PerformanceEq(1, storageReceipts()); PerformanceEq("d:1", values[idle]);
                foreach (string key in PerformanceTuning.StorageMembers()) PerformanceEq(true, Settings.LoadCached(key, false));
                PerformanceEq(true, PerformanceTuning.StorageGroupEnabled);
                PerformanceEq(true, PerformanceTuning.StorageGroupLabel().Contains(AutomaticPolicy.Text("跳过不适用项：", "Unsupported settings skipped: ") + "1"));
                PerformanceEq(true, PerformanceTuning.StorageGroupDetail.Contains(PerformanceTuning.Label("unsupported")));

                // 单独关闭：只恢复存储收据。
                int bundleWrites = writes;
                setStorage(false);
                PerformanceEq(0, storageReceipts()); PerformanceEq(originals[idle], values[idle]); PerformanceEq(bundleWrites + 1, writes);
                PerformanceEq(false, PerformanceTuning.StorageGroupEnabled);
                PerformanceEq("restore-pending-reboot", PerformanceTuning.StorageGroupState());

                // 单独开启：写入可用项，不适用项跳过。
                setStorage(true);
                PerformanceEq(1, storageReceipts()); PerformanceEq("d:1", values[idle]);
                PerformanceEq("pending-reboot", PerformanceTuning.StorageGroupState());
                PerformanceEq("unsupported", PerformanceTuning.State(PerformanceActionCatalog.StorageCold));
                PerformanceEq(false, PerformanceTuning.EnvironmentBundleEnabled);

                // 恢复失败：收据保留、开关仍显示开启，之后关闭可重试。
                failRestore = PerformanceActionCatalog.StorageIdle;
                setStorage(false);
                PerformanceEq("restore-failed", PerformanceTuning.StorageGroupState());
                PerformanceEq(true, PerformanceTuning.StorageGroupEnabled);
                PerformanceEq(1, storageReceipts()); PerformanceEq("d:1", values[idle]);
                failRestore = null;
                setStorage(false);
                PerformanceEq(0, storageReceipts()); PerformanceEq(originals[idle], values[idle]);
                PerformanceEq(false, PerformanceTuning.StorageGroupEnabled);

                // 偏好已关闭但收据仍在、尚无失败标记（例如关闭后、恢复执行前中断）：显示为原配置待恢复，不显示已关闭，
                // 只读恢复记录、不自动恢复；关闭此项才恢复。
                setStorage(true);
                PerformanceEq(1, storageReceipts());
                foreach (string key in PerformanceTuning.StorageMembers()) Settings.Save(key, false);
                int pendingWrites = writes;
                string pendingText = AutomaticPolicy.Text("原配置待恢复 · 关闭此项即可恢复", "Original configuration not yet restored · switch off to restore");
                PerformanceEq(true, PerformanceTuning.StorageGroupEnabled);
                PerformanceEq("recovery-pending", PerformanceTuning.StorageGroupState());
                PerformanceEq(pendingText, PerformanceTuning.StorageGroupLabel());
                PerformanceEq(true, PerformanceTuning.StorageGroupDetail.Contains(pendingText));
                PerformanceTuning.SetState(PerformanceActionCatalog.StorageIdle, "");
                PerformanceEq("recovery-pending", PerformanceTuning.StorageGroupState());
                PerformanceEq(pendingWrites, writes); PerformanceEq("d:1", values[idle]);
                // 记录锁被写入占用时界面查询不等待，沿用上次读到的结果。
                object storeGate = typeof(PerformanceActionStore).GetField("gate",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
                using (var held = new ManualResetEvent(false))
                using (var free = new ManualResetEvent(false))
                {
                    var holder = new Thread(delegate() { lock (storeGate) { held.Set(); free.WaitOne(3000); } });
                    holder.IsBackground = true; holder.Start(); PerformanceEq(true, held.WaitOne(1000));
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    bool shown = PerformanceTuning.StorageGroupEnabled; string shownState = PerformanceTuning.StorageGroupState();
                    clock.Stop(); free.Set(); PerformanceEq(true, holder.Join(3000));
                    PerformanceEq(true, clock.ElapsedMilliseconds < 1000); PerformanceEq(true, shown); PerformanceEq("recovery-pending", shownState);
                }
                setStorage(false);
                PerformanceEq(0, storageReceipts()); PerformanceEq(originals[idle], values[idle]);
                PerformanceEq(false, PerformanceTuning.StorageGroupEnabled);
                PerformanceEq(false, PerformanceTuning.StorageGroupState() == "recovery-pending");
                // 恢复记录不可读：按三项都待恢复处理，不当作已关闭。
                string corruptSlot = PerformanceActionStore.Slot("corrupt", "storage fixture");
                Settings.SaveStr(PerformanceActionStore.ListKey, corruptSlot); Settings.SaveStr(corruptSlot, "corrupt");
                PerformanceEq(true, PerformanceTuning.StorageGroupEnabled); PerformanceEq("recovery-pending", PerformanceTuning.StorageGroupState());
                Settings.SaveStr(corruptSlot, ""); Settings.SaveStr(PerformanceActionStore.ListKey, "");
                PerformanceEq(false, PerformanceTuning.StorageGroupEnabled); PerformanceEq(pendingWrites + 1, writes);
                // 整包语义不变：只看偏好，成员收据不改变整包开关。
                PerformanceEq(false, PerformanceTuning.EnvironmentBundleEnabled);

                // 存储写入进行中：整包、时间片和存储都不准入，全部恢复也不执行。
                holdWrite = true; done.Reset();
                PerformanceEq(true, PerformanceTuning.SetStorageGroup(true, delegate { done.Set(); }));
                PerformanceEq(true, blocked.WaitOne(3000));
                PerformanceEq("working", PerformanceTuning.StorageGroupState()); PerformanceEq("working", PerformanceTuning.EnvironmentBundleState());
                PerformanceEq(false, PerformanceTuning.SetEnvironmentBundle(true, null));
                PerformanceEq(false, PerformanceTuning.SetStorageGroup(false, null));
                PerformanceEq(false, PerformanceTuning.SetQuantumProfile(1, null));
                PerformanceEq(false, PerformanceTuning.SetEnvironment(PerformanceActionCatalog.Quantum, true, null));
                PerformanceEq(false, Settings.LoadCached(PerformanceActionCatalog.Quantum, false));
                PerformanceEq(false, PerformanceTuning.RestoreAll());
                holdWrite = false; release.Set(); PerformanceEq(true, done.WaitOne(3000));
                PerformanceEq(false, PerformanceTuning.EnvironmentBundleBusy());
                setStorage(false);

                // 排队失败或抛出、部分偏好保存失败：偏好全部回滚，不写入，不残留忙碌状态。
                int before = writes;
                PerformanceTuning.EnvironmentQueueForTest = delegate(WaitCallback work) { return false; };
                PerformanceEq(false, PerformanceTuning.SetStorageGroup(true, null));
                PerformanceTuning.EnvironmentQueueForTest = delegate(WaitCallback work) { throw new InvalidOperationException("Injected storage queue failure"); };
                PerformanceEq(false, PerformanceTuning.SetStorageGroup(true, null));
                PerformanceTuning.EnvironmentQueueForTest = null;
                Settings.BeforeValueWriteForTest = delegate(string name) {
                    if (name == PerformanceActionCatalog.StorageRuntime) throw new IOException("Injected preference write failure");
                };
                PerformanceEq(false, PerformanceTuning.SetStorageGroup(true, null));
                Settings.BeforeValueWriteForTest = null;
                foreach (string key in PerformanceTuning.StorageMembers()) PerformanceEq(false, Settings.LoadCached(key, false));
                PerformanceEq(false, PerformanceTuning.EnvironmentBundleBusy()); PerformanceEq(before, writes);
                Settings.SuspendWritesForReset();
                PerformanceEq(false, PerformanceTuning.SetStorageGroup(true, null)); PerformanceEq(before, writes);
                Settings.UseTransientStoreForCurrentProcess();
                PerformanceEq(true, PerformanceTuning.CloseEnvironmentWork(0));
                PerformanceEq(false, PerformanceTuning.SetStorageGroup(true, null));
                PerformanceEq(false, Settings.LoadCached(PerformanceActionCatalog.StorageIdle, false)); PerformanceEq(before, writes);
            }
            finally
            {
                release.Set(); if (PerformanceTuning.EnvironmentBundleBusy()) done.WaitOne(3000);
                PerformanceTuning.EnvironmentQueueForTest = null; Settings.BeforeValueWriteForTest = null;
                blocked.Dispose(); release.Dispose(); done.Dispose();
                PerformanceTuning.ResetForTest(); Settings.UseTransientStoreForCurrentProcess();
            }
        }
        // 前台时间片的三类记录（档位收据、旧版校正 PrevPrioritySep、更早的 PrevWin32PriSep）按任意先后叠放时的统一恢复。
        // 所有读写都经过替身，真实 HKLM 不参与。
        private sealed class QuantumLegacyFixture
        {
            internal string Value, ChangedValue; internal int Writes, Reads, ChangeOnRead; internal bool FailWrite, FailRead, ChangeAfterReturn;
            internal Action<int> OnRead;
            internal QuantumLegacyFixture(string value)
            {
                Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest();
                Value = value;
                string boot = BootSessionIdentity.Create(Guid.NewGuid(), 1);
                PerformanceTuning.BootForTest = delegate { return boot; };
                PerformanceActionBackend.TargetsForTest = delegate(string id) {
                    return id == PerformanceActionCatalog.Quantum ? new[] { QuantumRecovery.Target } : new string[0];
                };
                PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                    PerformanceEq(PerformanceActionCatalog.Quantum, id); PerformanceEq(QuantumRecovery.Target, target);
                    Reads++; if (FailRead) throw new IOException("Injected time-slice read failure");
                    if (OnRead != null) OnRead(Reads);
                    string observed = Value;
                    if (ChangeOnRead > 0 && Reads == ChangeOnRead) { Value = ChangedValue; if (!ChangeAfterReturn) observed = Value; }
                    return observed;
                };
                PerformanceActionBackend.WriteForTest = delegate(string id, string target, string next) {
                    PerformanceEq(PerformanceActionCatalog.Quantum, id); PerformanceEq(QuantumRecovery.Target, target);
                    Writes++; if (FailWrite) return false; Value = next; return true;
                };
            }
            // ReversibleReg 编码："=原值" 加可选的 "\u001F=写入值"（旧格式没有写入值）。
            internal void Legacy(string slot, int original, int applied = -1)
            { Settings.SaveStr(slot, "=" + original + (applied < 0 ? "" : "\u001F=" + applied)); }
            internal void Profile(string original, string expected, bool committed)
            {
                var receipt = new PerformanceActionStore.Receipt { Id = PerformanceActionCatalog.Quantum, Target = QuantumRecovery.Target,
                    Original = original, Expected = expected, Committed = committed };
                string slot = PerformanceActionStore.Slot(receipt.Id, receipt.Target);
                Settings.SaveStr(slot, PerformanceActionStore.Pack(receipt)); Settings.SaveStr(PerformanceActionStore.ListKey, slot);
            }
            internal string Stored(string slot) { return Settings.LoadStr(slot, ""); }
            internal string ReceiptText { get { return Settings.LoadStr(PerformanceActionStore.Slot(PerformanceActionCatalog.Quantum, QuantumRecovery.Target), ""); } }
            internal int Receipts
            {
                get
                {
                    int count = 0;
                    foreach (PerformanceActionStore.Receipt receipt in PerformanceActionStore.All()) if (receipt.Id == PerformanceActionCatalog.Quantum) count++;
                    return count;
                }
            }
        }
        private static void ActionQuantumLegacyRecovery()
        {
            const string key = PerformanceActionCatalog.Quantum, repair = QuantumRecovery.RepairSlot, boost = QuantumRecovery.BoostSlot;
            try
            {
                // 只有旧版校正：「恢复原配置」写回原值并清除记录、标记和退休的模式键；再次恢复不再写入。
                var fx = new QuantumLegacyFixture("d:38");
                fx.Legacy(repair, 37, 38); Settings.Save(QuantumRecovery.RepairedFlag, true); Settings.SaveStr(QuantumRecovery.RetiredModeKey, "1");
                PerformanceEq(true, QuantumRecovery.HasLegacyRecords); PerformanceEq(true, QuantumTweak.RepairedByPavise); PerformanceEq(0, fx.Writes);
                PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, false));
                PerformanceEq("d:37", fx.Value); PerformanceEq(1, fx.Writes); PerformanceEq("", fx.Stored(repair));
                PerformanceEq(false, QuantumTweak.RepairedByPavise); PerformanceEq("", Settings.LoadStr(QuantumRecovery.RetiredModeKey, ""));
                PerformanceEq("restore-pending-reboot", PerformanceTuning.State(key));
                PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, false)); PerformanceEq(1, fx.Writes);
                PerformanceEq(true, QuantumTweak.Restore()); PerformanceEq(1, fx.Writes);

                // 重置/完整恢复入口（QuantumTweak.Restore）走同一顺序。
                fx = new QuantumLegacyFixture("d:38"); fx.Legacy(repair, 37, 38);
                PerformanceEq(true, QuantumTweak.Restore()); PerformanceEq("d:37", fx.Value); PerformanceEq("", fx.Stored(repair));

                // 外部已改成别的有效值：尊重外部改动，只释放记录。
                fx = new QuantumLegacyFixture("d:24"); fx.Legacy(repair, 37, 38);
                PerformanceEq(true, QuantumTweak.Restore()); PerformanceEq("d:24", fx.Value); PerformanceEq(0, fx.Writes); PerformanceEq("", fx.Stored(repair));

                // 旧格式没有写入值：无法证明当前值仍属于旧版校正，不写入、保留记录；当前值已等于原值时才清除。
                fx = new QuantumLegacyFixture("d:38"); fx.Legacy(repair, 37);
                PerformanceEq(false, QuantumTweak.Restore()); PerformanceEq("d:38", fx.Value); PerformanceEq(0, fx.Writes); PerformanceEq("=37", fx.Stored(repair));
                PerformanceEq(true, QuantumRecovery.HasRecords());
                fx = new QuantumLegacyFixture("d:37"); fx.Legacy(repair, 37);
                PerformanceEq(true, QuantumTweak.Restore()); PerformanceEq(0, fx.Writes); PerformanceEq("", fx.Stored(repair));

                // 写入值等于原值的档位收据从未改变这个值：先释放，再还原其下的旧校正。
                fx = new QuantumLegacyFixture("d:38"); fx.Legacy(repair, 24, 38); fx.Profile("d:38", "d:38", true);
                PerformanceEq(true, QuantumRecovery.RestoreAll()); PerformanceEq("d:24", fx.Value); PerformanceEq(1, fx.Writes);
                PerformanceEq(false, QuantumRecovery.HasRecords());

                // 判断之后旧记录或当前值被改动：写入前复核失败，不写入，记录与外部值保留。
                fx = new QuantumLegacyFixture("d:38"); fx.Legacy(repair, 24, 38);
                fx.OnRead = delegate(int read) { if (read == 1) Settings.SaveStr(repair, "=16\u001F=38"); };
                PerformanceEq(false, QuantumRecovery.RestoreAll()); PerformanceEq(0, fx.Writes); PerformanceEq("d:38", fx.Value);
                PerformanceEq("=16\u001F=38", fx.Stored(repair));
                fx = new QuantumLegacyFixture("d:38"); fx.Legacy(repair, 24, 38);
                fx.ChangeOnRead = 1; fx.ChangedValue = "d:37"; fx.ChangeAfterReturn = true;
                PerformanceEq(false, QuantumRecovery.RestoreAll()); PerformanceEq(0, fx.Writes); PerformanceEq("d:37", fx.Value);
                PerformanceEq("=24\u001F=38", fx.Stored(repair));

                // 记录无法解析：不写入、不删除，状态为恢复未完成。
                fx = new QuantumLegacyFixture("d:38"); Settings.SaveStr(repair, "=not-a-number");
                PerformanceEq(false, PerformanceTuning.ApplyEnvironment(key, false));
                PerformanceEq("restore-failed", PerformanceTuning.State(key));
                PerformanceEq("=not-a-number", fx.Stored(repair)); PerformanceEq(0, fx.Writes);
                PerformanceEq(false, QuantumTweak.Restore()); PerformanceEq("=not-a-number", fx.Stored(repair)); PerformanceEq(0, fx.Writes);

                // 先校正后档位：通用收据恢复只回到校正值并保留旧记录；随后的重置入口回到最早原值。
                fx = new QuantumLegacyFixture("d:24"); fx.Legacy(repair, 37, 38); fx.Profile("d:38", "d:24", true);
                PerformanceEq(true, PerformanceTuning.RestoreAll());
                PerformanceEq("d:38", fx.Value); PerformanceEq(0, fx.Receipts); PerformanceEq("=37\u001F=38", fx.Stored(repair));
                PerformanceEq(true, QuantumTweak.Restore()); PerformanceEq("d:37", fx.Value); PerformanceEq("", fx.Stored(repair)); PerformanceEq(2, fx.Writes);
                // 同一叠放下「恢复原配置」一次回到最早原值，不停在中间值。
                fx = new QuantumLegacyFixture("d:24"); fx.Legacy(repair, 37, 38); fx.Profile("d:38", "d:24", true); Settings.Save(key, true);
                PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, false));
                PerformanceEq("d:37", fx.Value); PerformanceEq(2, fx.Writes); PerformanceEq(0, fx.Receipts); PerformanceEq("", fx.Stored(repair));

                // 先档位后校正（旧版健康检查改了用户选的 0x25）：先撤销校正再撤销档位，回到最早原值。
                fx = new QuantumLegacyFixture("d:38"); fx.Profile("d:2", "d:37", true); fx.Legacy(repair, 37, 38);
                PerformanceEq(true, PerformanceTuning.RestoreAll());
                PerformanceEq("d:2", fx.Value); PerformanceEq(2, fx.Writes); PerformanceEq(0, fx.Receipts); PerformanceEq("", fx.Stored(repair));
                fx = new QuantumLegacyFixture("d:38"); fx.Profile("d:2", "d:37", true); fx.Legacy(repair, 37, 38);
                PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, false)); PerformanceEq("d:2", fx.Value); PerformanceEq(2, fx.Writes);

                // 两层都声称当前值：不猜测，不写入，所有记录保留。
                fx = new QuantumLegacyFixture("d:24"); fx.Profile("d:2", "d:24", true); fx.Legacy(repair, 37, 24);
                string receipt = fx.ReceiptText;
                PerformanceEq(false, QuantumRecovery.RestoreAll()); PerformanceEq(true, QuantumRecovery.LastFailure.Contains("Several"));
                PerformanceEq(false, PerformanceTuning.RestoreAll());
                PerformanceEq(0, fx.Writes); PerformanceEq(receipt, fx.ReceiptText); PerformanceEq("=37\u001F=24", fx.Stored(repair));

                // 旧格式记录与不在顶层的档位收据：无法排序，失败关闭。
                fx = new QuantumLegacyFixture("d:38"); fx.Profile("d:2", "d:37", true); fx.Legacy(repair, 37);
                PerformanceEq(false, QuantumRecovery.RestoreAll()); PerformanceEq(0, fx.Writes); PerformanceEq(1, fx.Receipts); PerformanceEq("=37", fx.Stored(repair));
                // 旧格式记录在下、档位收据在顶：只还原自己能证明的档位层，旧格式记录不猜测、保留为待恢复。
                fx = new QuantumLegacyFixture("d:24"); fx.Legacy(repair, 37); fx.Profile("d:38", "d:24", true);
                PerformanceEq(false, QuantumRecovery.RestoreAll()); PerformanceEq("d:38", fx.Value); PerformanceEq(1, fx.Writes);
                PerformanceEq(0, fx.Receipts); PerformanceEq("=37", fx.Stored(repair));
                // 档位已回到原值后只剩旧格式记录：不能证明它仍在顶层，保留。
                fx = new QuantumLegacyFixture("d:2"); fx.Profile("d:2", "d:37", true); fx.Legacy(repair, 37);
                PerformanceEq(false, QuantumRecovery.RestoreAll()); PerformanceEq(0, fx.Writes); PerformanceEq("=37", fx.Stored(repair));
                // 还原档位时值被外部改走：档位记录按原语义释放，剩下的旧格式记录不再写回，保留外部值与记录。
                fx = new QuantumLegacyFixture("d:24"); fx.Legacy(repair, 37); fx.Profile("d:38", "d:24", true);
                fx.ChangeOnRead = 2; fx.ChangedValue = "d:30";
                PerformanceEq(false, QuantumRecovery.RestoreAll()); PerformanceEq(0, fx.Writes); PerformanceEq("d:30", fx.Value);
                PerformanceEq(0, fx.Receipts); PerformanceEq("=37", fx.Stored(repair));

                // 更早的 PrevWin32PriSep 在最下：逐层还原到最早原值。
                fx = new QuantumLegacyFixture("d:38"); fx.Legacy(boost, 2, 37); fx.Legacy(repair, 37, 38);
                PerformanceEq(true, FgBoost.HasResidue());
                PerformanceEq(true, FgBoost.Restore()); PerformanceEq("d:2", fx.Value); PerformanceEq(2, fx.Writes);
                PerformanceEq(false, FgBoost.HasResidue()); PerformanceEq("", fx.Stored(repair));

                // 档位收据只做了准备（写入未发生）：先还原其下的旧校正，准备记录随后释放，不写入。
                fx = new QuantumLegacyFixture("d:38"); fx.Legacy(repair, 36, 38); fx.Profile("d:38", "d:24", false);
                PerformanceEq(true, QuantumRecovery.RestoreAll()); PerformanceEq("d:36", fx.Value); PerformanceEq(1, fx.Writes); PerformanceEq(0, fx.Receipts);

                // 只有档位收据（标准目标）：原有语义不变，外部改动只释放记录。
                fx = new QuantumLegacyFixture("d:24"); fx.Profile("d:37", "d:24", true); Settings.Save(key, true);
                PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, false)); PerformanceEq("d:37", fx.Value); PerformanceEq(0, fx.Receipts);
                fx = new QuantumLegacyFixture("d:30"); fx.Profile("d:37", "d:24", true);
                PerformanceEq(true, PerformanceTuning.RestoreAll()); PerformanceEq("d:30", fx.Value); PerformanceEq(0, fx.Writes); PerformanceEq(0, fx.Receipts);

                // 过期的收据快照不能恢复更新后的记录。
                fx = new QuantumLegacyFixture("d:24"); fx.Legacy(repair, 37, 38); fx.Profile("d:38", "d:24", true);
                PerformanceActionStore.Receipt stale = PerformanceActionStore.All()[0]; stale.Expected = "d:25";
                PerformanceEq(false, QuantumRecovery.RestoreThrough(stale)); PerformanceEq(0, fx.Writes); PerformanceEq(1, fx.Receipts);

                // 有旧版校正时选择档位：先回到最早原值，新收据记下真正的原值，再写入档位。
                fx = new QuantumLegacyFixture("d:38"); fx.Legacy(repair, 37, 38);
                Settings.Save(key, true); Settings.SaveStr(QuantumProfiles.SettingKey, "24");
                PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, true));
                PerformanceEq("d:24", fx.Value); PerformanceEq(2, fx.Writes); PerformanceEq("", fx.Stored(repair));
                PerformanceEq("d:37", PerformanceActionStore.All()[0].Original);
                PerformanceEq("pending-reboot", PerformanceTuning.State(key));
                PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, false)); PerformanceEq("d:37", fx.Value);
                // 旧记录无法排序时不写入新档位，记录保留，状态为恢复未完成且不隔离。
                fx = new QuantumLegacyFixture("d:24"); fx.Profile("d:2", "d:24", true); fx.Legacy(repair, 37, 24);
                receipt = fx.ReceiptText; Settings.Save(key, true); Settings.SaveStr(QuantumProfiles.SettingKey, "38");
                PerformanceEq(false, PerformanceTuning.ApplyEnvironment(key, true));
                PerformanceEq(0, fx.Writes); PerformanceEq("d:24", fx.Value); PerformanceEq("restore-failed", PerformanceTuning.State(key));
                PerformanceEq(receipt, fx.ReceiptText); PerformanceEq("=37\u001F=24", fx.Stored(repair));
                PerformanceEq(false, ActionFailureStore.IsBlocked(key, QuantumRecovery.Target));

                // 写入失败或读取失败：记录保留，稍后可重试。
                fx = new QuantumLegacyFixture("d:38"); fx.Legacy(repair, 37, 38); fx.FailWrite = true;
                PerformanceEq(false, PerformanceTuning.ApplyEnvironment(key, false));
                PerformanceEq("restore-failed", PerformanceTuning.State(key)); PerformanceEq("=37\u001F=38", fx.Stored(repair)); PerformanceEq("d:38", fx.Value);
                fx.FailWrite = false;
                PerformanceEq(true, PerformanceTuning.ApplyEnvironment(key, false)); PerformanceEq("d:37", fx.Value); PerformanceEq("", fx.Stored(repair));
                fx = new QuantumLegacyFixture("d:38"); fx.Legacy(repair, 37, 38); fx.FailRead = true;
                PerformanceEq(false, QuantumTweak.Restore()); PerformanceEq(0, fx.Writes); PerformanceEq("=37\u001F=38", fx.Stored(repair));

                // 健康检查的旧修复入口已退休：拒绝且不写入；只读检查也不写入。
                fx = new QuantumLegacyFixture("d:37");
                PerformanceEq(false, QuantumTweak.NeedsRepair()); PerformanceEq(false, QuantumTweak.Repair());
                PerformanceEq(0, fx.Writes); PerformanceEq("", fx.Stored(repair)); PerformanceEq(false, QuantumTweak.RepairedByPavise);
                fx.Legacy(repair, 37, 38);
                PerformanceEq(true, QuantumAudit.Inspect().OfferSetting); PerformanceEq(true, QuantumTweak.Describe().Length > 0); PerformanceEq(0, fx.Writes);
                PerformanceEq(true, QuantumTweak.HasRetiredModeResidue());
                PerformanceEq(true, QuantumTweak.PurgeRetiredMode()); PerformanceEq(false, QuantumTweak.HasRetiredModeResidue()); PerformanceEq(0, fx.Writes);
            }
            finally
            {
                PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null; PerformanceActionBackend.TargetsForTest = null;
                PerformanceTuning.ResetForTest(); Settings.UseTransientStoreForCurrentProcess();
            }
        }
        // 健康检查的时间片行只读：文档化取值（含 0x25、0x18 等档位）都是有效配置；只有旧版记录或未文档化取值才提示前往设置。
        private static void ActionQuantumAuditReadOnly()
        {
            int previousLanguage = Lang.Cur;
            try
            {
                foreach (int language in new[] { 0, 1 })
                {
                    Lang.Cur = language;
                    foreach (int value in new[] { 0x25, 0x24, 0x28, 0x15, 0x14, 0x18, 0x26, 0x16, 0x2 })
                    {
                        QuantumAudit.Finding valid = QuantumAudit.Classify("d:" + value, null, false);
                        PerformanceEq(false, valid.Warn); PerformanceEq(false, valid.OfferSetting);
                        PerformanceEq(AutomaticPolicy.Text("有效配置", "Valid setting"), valid.Value);
                        PerformanceEq(true, valid.Note.Contains("0x" + value.ToString("X")));
                    }
                    QuantumAudit.Finding owned = QuantumAudit.Classify("d:294", "d:294", false);
                    PerformanceEq(false, owned.Warn || owned.OfferSetting); PerformanceEq(true, owned.Note.Contains(QuantumProfiles.Title(1)));
                    QuantumAudit.Finding external = QuantumAudit.Classify("d:37", "d:38", false);
                    PerformanceEq(AutomaticPolicy.Text("有效配置", "Valid setting"), external.Value);
                    QuantumAudit.Finding undocumented = QuantumAudit.Classify("d:39", null, false);
                    PerformanceEq(true, undocumented.Warn); PerformanceEq(true, undocumented.OfferSetting);
                    QuantumAudit.Finding legacy = QuantumAudit.Classify("d:38", null, true);
                    PerformanceEq(false, legacy.Warn); PerformanceEq(true, legacy.OfferSetting);
                    QuantumAudit.Finding absent = QuantumAudit.Classify("absent", null, false);
                    PerformanceEq(false, absent.Warn || absent.OfferSetting);
                    QuantumAudit.Finding unreadable = QuantumAudit.Classify(null, null, false);
                    PerformanceEq(false, unreadable.Warn || unreadable.OfferSetting);
                }
            }
            finally { Lang.Cur = previousLanguage; }
            // 健康检查修复表中的时间片项没有修复或还原写入，只能跳转，不计入一键修复。
            var form = (PanelForm)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PanelForm));
            var fixes = (System.Collections.IDictionary)typeof(PanelForm).GetMethod("BuildAuditFixes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, null);
            object quantum = fixes["quantum"]; Type fixType = quantum.GetType();
            PerformanceEq(false, ((Func<bool>)fixType.GetField("CanFix").GetValue(quantum))());
            PerformanceEq(false, ((Func<bool>)fixType.GetField("CanRevert").GetValue(quantum))());
            PerformanceEq(null, fixType.GetField("Fix").GetValue(quantum));
            PerformanceEq(null, fixType.GetField("Revert").GetValue(quantum));
            PerformanceEq(true, fixType.GetField("Open").GetValue(quantum) != null);
            PerformanceEq(true, ((Func<string>)fixType.GetField("OpenText").GetValue(quantum))().Length > 0);
        }
        private static void ActionEnvironmentTimerConcurrency()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save(PolicyCatalog.KeyTimerTick, true); Settings.Save(PolicyCatalog.KeyGlobalTimer, false);
            bool on = false; int writes = 0;
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var started = new ManualResetEvent(false))
            using (var firstDone = new ManualResetEvent(false))
            using (var secondDone = new ManualResetEvent(false))
            {
                var timers = new TimerModeController {
                    Boot = delegate { return BootSessionIdentity.Create(new Guid("12345678-1234-1234-1234-1234567890ab"), 1); },
                    OsBuild = delegate { return 26000; }, TickOn = delegate { return on; }, TickOwned = delegate { return on; },
                    TickEnable = delegate { Interlocked.Increment(ref writes); entered.Set(); release.WaitOne(3000); on = true; return true; },
                    TickRestore = delegate { throw new InvalidOperationException("Unexpected timer restore"); },
                    GlobalOn = delegate { return false; }, GlobalOwned = delegate { return false; },
                    GlobalEnable = delegate { throw new InvalidOperationException("Unexpected global timer enable"); },
                    GlobalRestore = delegate { throw new InvalidOperationException("Unexpected global timer restore"); }
                };
                var first = new Thread(delegate() { try { timers.SyncEnvironmentPreferences(); } finally { firstDone.Set(); } });
                var second = new Thread(delegate() { started.Set(); try { timers.SyncEnvironmentPreferences(); } finally { secondDone.Set(); } });
                first.IsBackground = second.IsBackground = true;
                try
                {
                    first.Start(); PerformanceEq(true, entered.WaitOne(3000));
                    second.Start(); PerformanceEq(true, started.WaitOne(3000));
                    PerformanceEq(false, secondDone.WaitOne(100)); PerformanceEq(1, writes);
                    release.Set(); PerformanceEq(true, firstDone.WaitOne(3000)); PerformanceEq(true, secondDone.WaitOne(3000));
                    PerformanceEq(1, writes); PerformanceEq("pending-reboot", timers.TickState);
                }
                finally { release.Set(); if (first.IsAlive) first.Join(3000); if (second.IsAlive) second.Join(3000); }
            }
        }
        private static void ActionCoordinatorMixedResults()
        {
            Settings.UseTransientStoreForCurrentProcess();
            SelectPerformanceFixtureActions(PerformanceActionCatalog.Mitigations, PerformanceActionCatalog.OpenGl);
            var values = new Dictionary<string, string>(); var valueGate = new object();
            const string first = @"C:\A\GameA.exe", second = @"C:\B\GameB.exe";
            int retiredReads = 0;
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                if (id == PerformanceActionCatalog.DefenderResources) retiredReads++;
                lock (valueGate)
                {
                    string value;
                    return values.TryGetValue(id + "|" + target, out value) ? value : id == PerformanceActionCatalog.Mitigations
                        ? "NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False" : "absent";
                }
            };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                if (id == PerformanceActionCatalog.Mitigations && target == first) return false;
                if (value == "off64") value = "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False";
                lock (valueGate) values[id + "|" + target] = value; return true;
            };
            var coordinator = new PerformanceActionCoordinator();
            try
            {
                coordinator.Queue(true, false, new[] { first, second }, new string[0]);
                PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(0, retiredReads);
                PerformanceEq("configured", coordinator.State(PerformanceActionCatalog.OpenGl));
                PerformanceEq("stopped", coordinator.State(PerformanceActionCatalog.Mitigations));
                coordinator.Queue(false, false, new string[0], new string[0]);
                PerformanceEq(true, coordinator.WaitForSessionRestore(3000));
                PerformanceEq(true, coordinator.Close(3000));
                PerformanceEq(true, PerformanceTuning.RestoreAll()); PerformanceEq(0, PerformanceActionStore.All().Count);
            }
            finally { coordinator.Close(3000); }
        }
        private static string[] ActionRecords(string path, string marker)
        {
            var records = new List<string>();
            if (File.Exists(path)) foreach (string line in File.ReadAllLines(path))
                if (line.Contains(marker)) records.Add(line);
            return records.ToArray();
        }
        private static void ActionCoordinatorBatchedRecords()
        {
            foreach (bool failureFirst in new[] { true, false })
            {
                Settings.UseTransientStoreForCurrentProcess();
                long now = DateTime.UtcNow.Ticks; int rejectedWrites = 0;
                bool blockBadRead = true, throwBadRead = false, rejectBadWrite = true;
                const string first = @"C:\A\GameA.exe", second = @"C:\B\GameB.exe";
                string bad = failureFirst ? first : second;
                string id = PerformanceActionCatalog.Mitigations;
                SelectPerformanceFixtureActions(id);
                string marker = "AUTO " + PerformanceActionCatalog.Find(id).Title + " | ";
                string oldPath = Logger.LogPath; bool oldWrites = Logger.WritesEnabled;
                string path = Path.Combine(Path.GetTempPath(), "PaviseBatchFixture-" + Guid.NewGuid().ToString("N") + ".log");
                var values = new Dictionary<string, string>(); var valueGate = new object();
                var reached = new ManualResetEvent(false); var release = new ManualResetEvent(false);
                PerformanceActionBackend.DefenderServiceForTest = delegate { return 1; };
                PerformanceActionBackend.ShortNameVolumeForTest = delegate { return null; };
                PerformanceActionBackend.ReadForTest = delegate(string action, string target) {
                    if (action == id && target == bad)
                    {
                        if (blockBadRead) { reached.Set(); if (!release.WaitOne(3000)) throw new TimeoutException("Fixture release timed out"); }
                        if (throwBadRead) throw new IOException("fixture changed provider failure 0x1234");
                    }
                    lock (valueGate) { string value; return values.TryGetValue(action + "|" + target, out value) ? value : ActionMockDefault(action); }
                };
                PerformanceActionBackend.WriteForTest = delegate(string action, string target, string value) {
                    if (action == id && target == bad && rejectBadWrite) { rejectedWrites++; return false; }
                    if (value == "off64") value = "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False";
                    lock (valueGate) values[action + "|" + target] = value;
                    return true;
                };
                var coordinator = new PerformanceActionCoordinator { ClockForTest = delegate { return now; } };
                try
                {
                    Logger.LogPath = path; Logger.WritesEnabled = true;
                    coordinator.Queue(true, false, new[] { first, second }, new string[0]);
                    PerformanceEq(true, reached.WaitOne(3000));
                    PerformanceEq("", coordinator.State(id));
                    PerformanceEq(0, ActionRecords(path, marker).Length);
                    blockBadRead = false; release.Set();
                    PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq("stopped", coordinator.State(id));
                    string[] records = ActionRecords(path, marker);
                    PerformanceEq(1, records.Length);
                    PerformanceEq(true, records[0].Contains("WARN AUTO ") && records[0].Contains(" | " + bad + " | "));
                    PerformanceEq(false, records[0].EndsWith(" | " + bad + " | "));
                    for (int minute = 1; minute <= 4; minute++)
                    {
                        now += TimeSpan.FromMinutes(1).Ticks;
                        coordinator.Queue(true, false, new[] { first, second }, new string[0]);
                        PerformanceEq(true, coordinator.DrainForTest(3000));
                    }
                    PerformanceEq(1, rejectedWrites); PerformanceEq(1, ActionRecords(path, marker).Length);
                    now += TimeSpan.FromMinutes(1).Ticks;
                    coordinator.Queue(true, false, new[] { first, second }, new string[0]);
                    PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq("stopped", coordinator.State(id));
                    PerformanceEq(1, rejectedWrites); PerformanceEq(1, ActionRecords(path, marker).Length);
                    for (int minute = 0; minute < 60; minute++)
                    {
                        now += TimeSpan.FromMinutes(1).Ticks;
                        coordinator.Queue(true, false, new[] { first, second }, new string[0]);
                        PerformanceEq(true, coordinator.DrainForTest(3000));
                    }
                    PerformanceEq("stopped", coordinator.State(id));
                    PerformanceEq(1, rejectedWrites); PerformanceEq(1, ActionRecords(path, marker).Length);
                    throwBadRead = true;
                    coordinator.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                    coordinator.Queue(true, false, new[] { first, second }, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                    records = ActionRecords(path, marker);
                    PerformanceEq(1, records.Length); PerformanceEq("stopped", coordinator.State(id));
                    PerformanceEq(1, rejectedWrites);
                    throwBadRead = false; rejectBadWrite = false;
                    PerformanceEq(true, ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(id, bad)));
                    coordinator.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                    coordinator.Queue(true, false, new[] { first, second }, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq("configured", coordinator.State(id)); PerformanceEq(2, ActionRecords(path, marker).Length);
                    PerformanceEq(true, coordinator.Close(3000)); PerformanceEq(true, PerformanceTuning.RestoreAll());
                    PerformanceEq(0, PerformanceActionStore.All().Count);
                }
                finally
                {
                    blockBadRead = false; throwBadRead = false; rejectBadWrite = false; release.Set();
                    coordinator.Close(3000); reached.Dispose(); release.Dispose();
                    Logger.WritesEnabled = oldWrites; Logger.LogPath = oldPath;
                    PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; };
                    PerformanceActionBackend.ShortNameVolumeForTest = null;
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }
        private static void ActionCoordinatorLifecycle()
        {
            Settings.UseTransientStoreForCurrentProcess();
            var values = new Dictionary<string, string>(); var valueGate = new object();
            Func<string, string, string> key = delegate(string id, string target) { return id + "|" + target; };
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                lock (valueGate) { string value; return values.TryGetValue(key(id, target), out value) ? value : id == PerformanceActionCatalog.LegacyDefenderSession ? "False,False" : "absent"; }
            };
            int defenderWrites = 0;
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                lock (valueGate) { if (id == PerformanceActionCatalog.LegacyDefenderSession) defenderWrites++; values[key(id, target)] = value; }
                return true;
            };
            var coordinator = new PerformanceActionCoordinator();
            try
            {
                // Manual control does not opt into driver/session actions merely by enabling the guard.
                coordinator.Queue(true, true, new string[0], new[] { "C:\\Browser.exe" });
                PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq("off", coordinator.State(PerformanceActionCatalog.BackgroundFps));
                PerformanceEq("absent", PerformanceActionBackend.ReadForTest(PerformanceActionCatalog.BackgroundFps, "C:\\Browser.exe"));
                PerformanceEq(0, PerformanceActionStore.All().Count);
                SelectPerformanceFixtureActions(PerformanceActionCatalog.BackgroundFps);
                for (int round = 0; round < 2; round++)
                {
                    coordinator.Queue(true, true, new string[0], new[] { "C:\\Browser.exe" });
                    WaitPerformance(delegate { return coordinator.State(PerformanceActionCatalog.BackgroundFps) == "configured"; });
                    PerformanceEq("30", PerformanceActionBackend.ReadForTest(PerformanceActionCatalog.BackgroundFps, "C:\\Browser.exe"));
                    PerformanceEq(true, Settings.Save(PerformanceActionCatalog.BackgroundFps, false));
                    coordinator.Queue(true, true, new string[0], new[] { "C:\\Browser.exe" });
                    PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq("off", coordinator.State(PerformanceActionCatalog.BackgroundFps));
                    PerformanceEq("absent", PerformanceActionBackend.ReadForTest(PerformanceActionCatalog.BackgroundFps, "C:\\Browser.exe"));
                    PerformanceEq(0, PerformanceActionStore.All().Count);
                    SelectPerformanceFixtureActions(PerformanceActionCatalog.BackgroundFps);
                    coordinator.Queue(true, true, new string[0], new[] { "C:\\Browser.exe" });
                    WaitPerformance(delegate { return coordinator.State(PerformanceActionCatalog.BackgroundFps) == "configured"; });
                    coordinator.Queue(false, false, new string[0], new string[0]);
                    PerformanceEq(true, coordinator.WaitForSessionRestore(3000));
                    PerformanceEq("absent", PerformanceActionBackend.ReadForTest(PerformanceActionCatalog.BackgroundFps, "C:\\Browser.exe"));
                    PerformanceEq(0, PerformanceActionStore.All().Count);
                }
                PerformanceEq(0, defenderWrites); PerformanceEq("", coordinator.State(PerformanceActionCatalog.LegacyDefenderSession));
                PerformanceEq(true, coordinator.Close(3000));
            }
            finally { coordinator.Close(3000); }
            // 旧版对局中断留下的记录：对局中不再执行，直接恢复，不等退局。
            Settings.UseTransientStoreForCurrentProcess();
            lock (valueGate) values[key(PerformanceActionCatalog.LegacyDefenderSession, "Defender")] = "True,True";
            SeedLegacyDefenderReceipt();
            coordinator = new PerformanceActionCoordinator();
            try
            {
                coordinator.Queue(true, true, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq("False,False", PerformanceActionBackend.ReadForTest(PerformanceActionCatalog.LegacyDefenderSession, "Defender"));
                PerformanceEq(1, defenderWrites); PerformanceEq("restored", coordinator.State(PerformanceActionCatalog.LegacyDefenderSession));
                PerformanceEq(0, PerformanceActionStore.All().Count);
                for (int round = 0; round < 2; round++)
                {
                    coordinator.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, coordinator.WaitForSessionRestore(3000));
                    coordinator.Queue(true, true, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                }
                PerformanceEq(1, defenderWrites);
                PerformanceEq("False,False", PerformanceActionBackend.ReadForTest(PerformanceActionCatalog.LegacyDefenderSession, "Defender"));
                PerformanceEq(true, coordinator.Close(3000));
            }
            finally { coordinator.Close(3000); }
        }
        // 旧版（3.1.0.0 至 3.1.0.4）对局 Defender 调整留下的已提交记录；新版本只恢复，不再写入关闭。
        private static void SeedLegacyDefenderReceipt()
        {
            PerformanceEq(true, Settings.SaveStrDurable(PerformanceActionStore.Slot(PerformanceActionCatalog.LegacyDefenderSession, "Defender"),
                PerformanceActionStore.Pack(new PerformanceActionStore.Receipt { Id = PerformanceActionCatalog.LegacyDefenderSession,
                    Target = "Defender", Original = "False,False", Expected = "True,True", Committed = true })));
        }
        private static void ActionDefenderInactive()
        {
            Settings.UseTransientStoreForCurrentProcess();
            int service = 1, serviceQueries = 0, reads = 0, writes = 0; long now = DateTime.UtcNow.Ticks;
            const string id = PerformanceActionCatalog.DefenderResources, target = @"C:\Fixture\Game";
            string value = "1";
            PerformanceActionBackend.DefenderServiceForTest = delegate { serviceQueries++; return service; };
            PerformanceActionBackend.ReadForTest = delegate(string action, string path) {
                PerformanceEq(id, action); PerformanceEq(target, path); reads++; return value;
            };
            PerformanceActionBackend.WriteForTest = delegate(string action, string path, string next) {
                PerformanceEq(id, action); PerformanceEq(target, path); PerformanceEq("0", next); writes++; value = next; return true;
            };
            var coordinator = new PerformanceActionCoordinator { ClockForTest = delegate { return now; } };
            try
            {
                foreach (bool guard in new[] { true, false })
                {
                    coordinator.Queue(guard, true, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq(0, serviceQueries); PerformanceEq(0, reads); PerformanceEq(0, writes);
                }
                string receipt = SeedRetiredResourceReceipt(target, "0", true);
                foreach (int inactive in new[] { 1, 0, 3 })
                {
                    service = inactive; now += TimeSpan.FromMinutes(1).Ticks;
                    coordinator.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq(false, coordinator.WaitForSessionRestore(100));
                    PerformanceEq("restore-failed", coordinator.State(id));
                    PerformanceEq(0, reads); PerformanceEq(0, writes);
                    PerformanceEq(receipt, Settings.LoadStr(PerformanceActionStore.Slot(id, target), ""));
                }
                service = 4; now += TimeSpan.FromMinutes(1).Ticks;
                coordinator.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(true, coordinator.WaitForSessionRestore(100)); PerformanceEq("0", value);
                PerformanceEq(1, writes); PerformanceEq(0, PerformanceActionStore.All().Count);
                int queried = serviceQueries;
                now += TimeSpan.FromMinutes(1).Ticks; coordinator.Queue(false, false, new string[0], new string[0]);
                PerformanceEq(true, coordinator.DrainForTest(3000)); PerformanceEq(queried, serviceQueries);
                PerformanceEq(true, coordinator.Close(3000)); PerformanceEq(queried, serviceQueries);
            }
            finally { service = 4; coordinator.Close(3000); PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; }; }
        }
        private static string SeedRetiredResourceReceipt(string target, string original, bool committed)
        {
            var receipt = new PerformanceActionStore.Receipt { Id = PerformanceActionCatalog.DefenderResources,
                Target = target, Original = original, Expected = "1", Committed = committed };
            string text = PerformanceActionStore.Pack(receipt);
            PerformanceEq(true, Settings.SaveStrDurable(PerformanceActionStore.Slot(receipt.Id, target), text));
            return text;
        }
        private static void ActionDefenderFailureRecord()
        {
            Settings.UseTransientStoreForCurrentProcess();
            int reads = 0, writes = 0; bool broken = true; long now = DateTime.UtcNow.Ticks;
            const string id = PerformanceActionCatalog.DefenderResources, target = @"C:\Fixture\ReadFailedGame";
            string value = "1", receipt = SeedRetiredResourceReceipt(target, "0", true);
            PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; };
            PerformanceActionBackend.ReadForTest = delegate(string action, string path) {
                PerformanceEq(id, action); PerformanceEq(target, path); reads++;
                if (broken) throw new IOException("provider failure 0x800106ba"); return value;
            };
            PerformanceActionBackend.WriteForTest = delegate(string action, string path, string next) {
                PerformanceEq(id, action); PerformanceEq(target, path); PerformanceEq("0", next); writes++; value = next; return true;
            };
            var coordinator = new PerformanceActionCoordinator { ClockForTest = delegate { return now; } };
            try
            {
                coordinator.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq("restore-failed", coordinator.State(id)); PerformanceEq(false, coordinator.WaitForSessionRestore(100));
                PerformanceEq(1, reads); PerformanceEq(0, writes);
                PerformanceEq(receipt, Settings.LoadStr(PerformanceActionStore.Slot(id, target), ""));
                for (int i = 0; i < 100; i++)
                {
                    now += TimeSpan.FromMinutes(1).Ticks;
                    coordinator.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                }
                PerformanceEq(true, reads < 30); PerformanceEq(0, writes);
                PerformanceEq(receipt, Settings.LoadStr(PerformanceActionStore.Slot(id, target), ""));
                PerformanceEq(0, ActionFailureStore.Records().Count);
                broken = false; now += TimeSpan.FromMinutes(6).Ticks;
                coordinator.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(true, coordinator.WaitForSessionRestore(100)); PerformanceEq("restored", coordinator.State(id));
                PerformanceEq(1, writes); PerformanceEq("0", value); PerformanceEq(0, PerformanceActionStore.All().Count);
                PerformanceEq(true, coordinator.Close(3000));
            }
            finally { broken = false; coordinator.Close(3000); }
        }
        private static void ActionDefenderRetirementMatrix()
        {
            const string id = PerformanceActionCatalog.DefenderResources, target = @"C:\Fixture\Legacy\:{Process:C:\Fixture\Game.exe}";
            foreach (bool committed in new[] { false, true })
                foreach (string original in new[] { "0", "1" })
                    foreach (string current in new[] { "0", "1", "unknown" })
                    {
                        Settings.UseTransientStoreForCurrentProcess();
                        string value = current; int writes = 0;
                        string receipt = SeedRetiredResourceReceipt(target, original, committed);
                        PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; };
                        PerformanceActionBackend.ReadForTest = delegate(string action, string path) {
                            PerformanceEq(id, action); PerformanceEq(target, path); return value;
                        };
                        PerformanceActionBackend.WriteForTest = delegate(string action, string path, string next) {
                            PerformanceEq(id, action); PerformanceEq(target, path); PerformanceEq("0", next); writes++; value = next; return true;
                        };
                        bool resolves = current != "unknown" && !(original == "1" && current == "0");
                        var coordinator = new PerformanceActionCoordinator();
                        try
                        {
                            coordinator.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                            PerformanceEq(resolves, coordinator.WaitForSessionRestore(100));
                            PerformanceEq(resolves ? "restored" : "restore-failed", coordinator.State(id));
                            PerformanceEq(original == "0" && current == "1" ? 1 : 0, writes);
                            PerformanceEq(original == "0" && current == "1" ? "0" : current, value);
                            PerformanceEq(resolves ? "" : receipt, Settings.LoadStr(PerformanceActionStore.Slot(id, target), ""));
                            PerformanceEq(resolves, coordinator.Close(3000));
                            PerformanceEq(resolves ? 0 : 1, PerformanceTuning.RestoreAllFailures().Count);
                        }
                        finally { coordinator.Close(3000); }
                    }
            Settings.UseTransientStoreForCurrentProcess();
            int nativeReads = 0;
            PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; };
            PerformanceActionBackend.ReadForTest = delegate { nativeReads++; throw new InvalidOperationException("Corrupt receipt must not authorize a native query"); };
            string slot = PerformanceActionStore.Slot(id, target);
            PerformanceEq(true, Settings.SaveStr(slot, "damaged"));
            var damaged = new PerformanceActionCoordinator();
            try
            {
                damaged.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, damaged.DrainForTest(3000));
                PerformanceEq(false, damaged.WaitForSessionRestore(100)); PerformanceEq("damaged", Settings.LoadStr(slot, ""));
                PerformanceEq(0, nativeReads); PerformanceEq(false, damaged.Close(3000));
                PerformanceEq(true, PerformanceTuning.RestoreAllFailures().Count > 0);
            }
            finally { Settings.UseTransientStoreForCurrentProcess(); damaged.Close(3000); PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; }; }
        }
        private static void ActionDefenderRetirementClose()
        {
            Settings.UseTransientStoreForCurrentProcess();
            const string id = PerformanceActionCatalog.DefenderResources, target = @"C:\Fixture\ClosingGame";
            string value = "1"; int writes = 0;
            string receipt = SeedRetiredResourceReceipt(target, "0", true);
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; };
                PerformanceActionBackend.ReadForTest = delegate(string action, string path) {
                    PerformanceEq(id, action); PerformanceEq(target, path); return value;
                };
                PerformanceActionBackend.WriteForTest = delegate(string action, string path, string next) {
                    PerformanceEq(id, action); PerformanceEq(target, path); PerformanceEq("0", next);
                    entered.Set(); if (!release.WaitOne(3000)) throw new TimeoutException("Remove fixture release timed out");
                    writes++; value = next; return true;
                };
                var coordinator = new PerformanceActionCoordinator();
                try
                {
                    coordinator.Queue(false, false, new string[0], new string[0]); PerformanceEq(true, entered.WaitOne(3000));
                    PerformanceEq(false, coordinator.Close(25)); PerformanceEq("1", value);
                    PerformanceEq(receipt, Settings.LoadStr(PerformanceActionStore.Slot(id, target), ""));
                    release.Set(); PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq(true, coordinator.Close(3000)); PerformanceEq(1, writes);
                    PerformanceEq("0", value); PerformanceEq(0, PerformanceActionStore.All().Count);
                }
                finally { release.Set(); coordinator.Close(3000); }
            }
        }
        private static string ActionMockDefault(string id)
        {
            return id == PerformanceActionCatalog.LegacyDefenderSession ? "False,False"
                : id == PerformanceActionCatalog.DefenderResources ? "0"
                : id == PerformanceActionCatalog.Mitigations ? "NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False" : "absent";
        }
        private static void SelectPerformanceFixtureActions(params string[] ids)
        {
            // Fault/recovery fixtures exercise explicit manual consent; no saved choice means off.
            foreach (string id in ids) PerformanceEq(true, Settings.Save(id, true));
        }
        private static void ActionCoordinatorRetryBudget()
        {
            foreach (string failingId in new[] { PerformanceActionCatalog.Mitigations, PerformanceActionCatalog.OpenGl,
                PerformanceActionCatalog.ShortNames,
                PerformanceActionCatalog.BackgroundFps })
            {
                Settings.UseTransientStoreForCurrentProcess();
                SelectPerformanceFixtureActions(failingId);
                int reads = 0; long now = DateTime.UtcNow.Ticks;
                bool match = failingId == PerformanceActionCatalog.BackgroundFps;
                var values = new Dictionary<string, string>();
                PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                    if (id == failingId) { reads++; throw new IOException("fixture blocked"); }
                    string value; return values.TryGetValue(id + "|" + target, out value) ? value : ActionMockDefault(id);
                };
                PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                    values[id + "|" + target] = value == "off64" ? "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False" : value; return true;
                };
                var coordinator = new PerformanceActionCoordinator { ClockForTest = delegate { return now; } };
                string[] games = { @"C:\Games\Game.exe" }, background = { @"C:\Browser.exe" };
                try
                {
                    coordinator.Queue(true, match, games, background); PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq(1, reads); PerformanceEq("stopped", coordinator.State(failingId));
                    now += TimeSpan.FromMinutes(4).Ticks;
                    coordinator.Queue(true, match, games, background); PerformanceEq(true, coordinator.DrainForTest(3000)); PerformanceEq(1, reads);
                    now += TimeSpan.FromMinutes(1).Ticks;
                    coordinator.Queue(true, match, games, background); PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq(1, reads); PerformanceEq("stopped", coordinator.State(failingId));
                    for (int i = 0; i < 100; i++)
                    {
                        now += TimeSpan.FromMinutes(10).Ticks;
                        coordinator.Queue(true, match, games, background); PerformanceEq(true, coordinator.DrainForTest(3000));
                    }
                    PerformanceEq(1, reads); PerformanceEq("stopped", coordinator.State(failingId));
                    if (failingId == PerformanceActionCatalog.BackgroundFps)
                    {
                        coordinator.Queue(true, true, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                        coordinator.Queue(true, true, games, background); PerformanceEq(true, coordinator.DrainForTest(3000));
                        PerformanceEq(1, reads); PerformanceEq("stopped", coordinator.State(failingId));
                    }
                    coordinator.Queue(false, false, games, background); PerformanceEq(true, coordinator.DrainForTest(3000));
                    coordinator.Queue(true, match, games, background); PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq(1, reads); PerformanceEq("stopped", coordinator.State(failingId));
                    PerformanceEq(true, coordinator.Close(3000)); PerformanceEq(true, PerformanceTuning.RestoreAll());
                }
                finally { coordinator.Close(3000); }
            }
        }
        private static void ActionDiscoveryBudget()
        {
            Settings.UseTransientStoreForCurrentProcess();
            SelectPerformanceFixtureActions(PerformanceActionCatalog.Mitigations, PerformanceActionCatalog.OpenGl, PerformanceActionCatalog.ShortNames);
            int serviceQueries = 0, volumes = 0, resourceReads = 0; long now = DateTime.UtcNow.Ticks;
            var values = new Dictionary<string, string>();
            PerformanceActionBackend.DefenderServiceForTest = delegate { serviceQueries++; throw new InvalidOperationException("No receipt should query Defender"); };
            PerformanceActionBackend.ShortNameVolumeForTest = delegate { volumes++; throw new IOException("volume query denied"); };
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                if (id == PerformanceActionCatalog.DefenderResources) resourceReads++;
                string value; return values.TryGetValue(id + "|" + target, out value) ? value : ActionMockDefault(id);
            };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                values[id + "|" + target] = value == "off64" ? "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False" : value; return true;
            };
            var coordinator = new PerformanceActionCoordinator { ClockForTest = delegate { return now; } };
            string[] games = { @"C:\A\GameA.exe", @"C:\B\GameB.exe", @"C:\C\GameC.exe" };
            try
            {
                coordinator.Queue(true, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(0, serviceQueries); PerformanceEq(1, volumes); PerformanceEq(0, resourceReads);
                now += TimeSpan.FromMinutes(1).Ticks;
                coordinator.Queue(true, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(0, serviceQueries); PerformanceEq(1, volumes);
                now += TimeSpan.FromMinutes(4).Ticks;
                coordinator.Queue(true, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(0, serviceQueries); PerformanceEq(2, volumes);
                for (int i = 0; i < 100; i++)
                {
                    now += TimeSpan.FromMinutes(10).Ticks;
                    coordinator.Queue(true, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                }
                PerformanceEq(0, serviceQueries); PerformanceEq(102, volumes); PerformanceEq(0, resourceReads);
                PerformanceEq("", coordinator.State(PerformanceActionCatalog.DefenderResources));
                PerformanceEq("failed", coordinator.State(PerformanceActionCatalog.ShortNames));
                PerformanceEq(0, ActionFailureStore.Records().Count);
                PerformanceEq("configured", coordinator.State(PerformanceActionCatalog.Mitigations));
                PerformanceEq("configured", coordinator.State(PerformanceActionCatalog.OpenGl));
                ActionFailureStore.Trip(PerformanceActionCatalog.DefenderResources, @"C:\A", "old resource failure");
                ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(PerformanceActionCatalog.DefenderResources, @"C:\A"));
                coordinator.Queue(false, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                coordinator.Queue(true, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq("", coordinator.State(PerformanceActionCatalog.DefenderResources)); PerformanceEq(0, resourceReads);
                PerformanceEq(0, serviceQueries);
                PerformanceEq(false, PerformanceActionStore.All().Exists(delegate(PerformanceActionStore.Receipt r) { return r.Id == PerformanceActionCatalog.DefenderResources; }));
                PerformanceEq(true, coordinator.Close(3000)); PerformanceEq(true, PerformanceTuning.RestoreAll());
            }
            finally
            {
                coordinator.Close(3000); PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; };
                PerformanceActionBackend.ShortNameVolumeForTest = null;
            }
        }
        private static void ActionRestoreIsolation()
        {
            foreach (string failedId in new[] { PerformanceActionCatalog.LegacyDefenderSession, PerformanceActionCatalog.BackgroundFps })
            {
                bool legacyFails = failedId == PerformanceActionCatalog.LegacyDefenderSession;
                Settings.UseTransientStoreForCurrentProcess();
                SelectPerformanceFixtureActions(PerformanceActionCatalog.BackgroundFps);
                var values = new Dictionary<string, string>(); long now = DateTime.UtcNow.Ticks;
                bool failRestore = false, differentError = false; int failedWrites = 0;
                string oldPath = Logger.LogPath; bool oldLogging = Logger.WritesEnabled;
                string path = Path.Combine(Path.GetTempPath(), "PaviseRestoreFixture-" + Guid.NewGuid().ToString("N") + ".log");
                PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                    if (differentError && id == failedId) throw new IOException("changed restore cause");
                    string value; return values.TryGetValue(id + "|" + target, out value) ? value : ActionMockDefault(id);
                };
                PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                    if (failRestore && id == failedId && value == ActionMockDefault(id)) { failedWrites++; return false; }
                    values[id + "|" + target] = value; return true;
                };
                var coordinator = new PerformanceActionCoordinator { ClockForTest = delegate { return now; } };
                try
                {
                    Logger.LogPath = path; Logger.WritesEnabled = true;
                    // 旧 Defender 记录每轮都会尝试恢复：要测它恢复失败，就得从对局第一轮起让它失败。
                    values[PerformanceActionCatalog.LegacyDefenderSession + "|Defender"] = "True,True";
                    SeedLegacyDefenderReceipt(); failRestore = legacyFails;
                    coordinator.Queue(true, true, new string[0], new[] { @"C:\Browser.exe" }); PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq(legacyFails ? 2 : 1, PerformanceActionStore.All().Count);
                    failRestore = true;
                    coordinator.Queue(true, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq(false, coordinator.WaitForSessionRestore(3000));
                    PerformanceEq("restore-failed", coordinator.State(failedId));
                    string goodId = legacyFails ? PerformanceActionCatalog.BackgroundFps : PerformanceActionCatalog.LegacyDefenderSession;
                    PerformanceEq("restored", coordinator.State(goodId)); PerformanceEq(1, PerformanceActionStore.All().Count);
                    PerformanceEq(1, failedWrites);
                    for (int i = 0; i < 100; i++)
                    {
                        now += TimeSpan.FromMinutes(1).Ticks;
                        coordinator.Queue(true, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                    }
                    PerformanceEq(true, failedWrites < 30); PerformanceEq(1, PerformanceActionStore.All().Count);
                    var entries = LogStreamView.ParseEvents(File.ReadAllText(path));
                    PerformanceEq(1, entries.FindAll(delegate(LogEventEntry e) { return e.Severity == LogEventSeverity.Warning; }).Count);
                    differentError = true; now += TimeSpan.FromMinutes(6).Ticks;
                    coordinator.Queue(true, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                    entries = LogStreamView.ParseEvents(File.ReadAllText(path));
                    PerformanceEq(2, entries.FindAll(delegate(LogEventEntry e) { return e.Severity == LogEventSeverity.Warning; }).Count);
                    PerformanceEq(true, File.ReadAllText(path).Contains("changed restore cause"));
                    differentError = failRestore = false; now += TimeSpan.FromMinutes(6).Ticks;
                    coordinator.Queue(true, false, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq(true, coordinator.WaitForSessionRestore(3000)); PerformanceEq(0, PerformanceActionStore.All().Count);
                    PerformanceEq("restored", coordinator.State(failedId)); PerformanceEq(true, coordinator.Close(3000));
                }
                finally
                {
                    differentError = failRestore = false; coordinator.Close(3000);
                    Logger.WritesEnabled = oldLogging; Logger.LogPath = oldPath;
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }
        private static void ActionBackgroundTargetMoves()
        {
            Settings.UseTransientStoreForCurrentProcess();
            SelectPerformanceFixtureActions(PerformanceActionCatalog.BackgroundFps);
            var values = new Dictionary<string, string>(); int writes = 0;
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                string value; return values.TryGetValue(id + "|" + target, out value) ? value : ActionMockDefault(id);
            };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                if (id == PerformanceActionCatalog.BackgroundFps) writes++;
                values[id + "|" + target] = value; return true;
            };
            var coordinator = new PerformanceActionCoordinator(); string[] background = { @"C:\Browser.exe" };
            try
            {
                coordinator.Queue(true, true, new string[0], background); PerformanceEq(true, coordinator.DrainForTest(3000)); PerformanceEq(1, writes);
                coordinator.Queue(true, true, new string[0], new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(2, writes); PerformanceEq("restored", coordinator.State(PerformanceActionCatalog.BackgroundFps));
                coordinator.Queue(true, true, new string[0], background); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(3, writes); PerformanceEq("configured", coordinator.State(PerformanceActionCatalog.BackgroundFps));
                PerformanceEq(true, coordinator.Close(3000)); PerformanceEq(0, PerformanceActionStore.All().Count);
            }
            finally { coordinator.Close(3000); }
        }
        private static void ActionLoggerRecords()
        {
            string oldPath = Logger.LogPath; bool oldLogging = Logger.WritesEnabled;
            string path = Path.Combine(Path.GetTempPath(), "PaviseLoggerFixture-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                Logger.LogPath = path; Logger.WritesEnabled = true;
                Logger.Warn("one warning\r\nError continuation"); Logger.Info("status error=0\nnext line"); Logger.Error("real failure\rstack");
                PerformanceEq(3, File.ReadAllLines(path).Length);
                var entries = LogStreamView.ParseEvents(File.ReadAllText(path));
                PerformanceEq(3, entries.Count); PerformanceEq(LogEventSeverity.Warning, entries[0].Severity);
                PerformanceEq(LogEventSeverity.Info, entries[1].Severity); PerformanceEq(LogEventSeverity.Error, entries[2].Severity);
                PerformanceEq(true, entries[0].Raw.Contains("Error continuation"));
                int warnings, errors; DiagnosticSummary.CountIssues(File.ReadAllText(path), out warnings, out errors);
                PerformanceEq(1, warnings); PerformanceEq(1, errors);
                string parent = "2026-10-04 11:27:35 WARN Performance #< CLIXML";
                string xml = "<Objs xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><S S=\"Error\">"
                    + new string('游', 5000) + " error 0x800106ba_x000D__x000A_</S></Objs>";
                File.WriteAllText(path, parent + "\r\n" + xml + "\r\n", System.Text.Encoding.UTF8);
                entries = LogStreamView.ParseEvents(Logger.Tail(1));
                PerformanceEq(1, entries.Count); PerformanceEq(LogEventSeverity.Warning, entries[0].Severity);
                PerformanceEq("11:27:35", entries[0].Time); PerformanceEq(true, entries[0].Raw.Contains(xml));
                File.AppendAllText(path, "2026-10-04 11:27:36 INFO latest\r\n");
                entries = LogStreamView.ParseEvents(Logger.Tail(2));
                PerformanceEq(2, entries.Count); PerformanceEq(LogEventSeverity.Warning, entries[0].Severity);
                PerformanceEq("11:27:36", LogStreamView.ParseEvents(Logger.Tail(1))[0].Time);
                PerformanceEq("", Logger.Tail(0));
                PerformanceEq(true, DiagnosticSummary.RecentContext(Logger.Tail(2)).Contains(parent));
            }
            finally { Logger.WritesEnabled = oldLogging; Logger.LogPath = oldPath; if (File.Exists(path)) File.Delete(path); }
        }
        private static void ActionPowerShellFailures()
        {
            string xml = "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">"
                + "<Obj S=\"progress\"><S>Preparing module</S></Obj><S S=\"Error\">测试错误 0x800106ba_x000D__x000A_</S>"
                + "<S S=\"Error\">_x005F_x000A_ literal</S></Objs>";
            PerformanceEq("测试错误 0x800106ba _x000A_ literal", PsRunner.NormalizeError("#< CLIXML\r\n" + xml));
            PerformanceEq("测试错误 0x800106ba", PsRunner.NormalizeError("  测试错误\r\n0x800106ba  "));
            PerformanceEq("", PsRunner.NormalizeError("#< CLIXML\n<Objs><Obj S=\"progress\"><S>Preparing module</S></Obj></Objs>"));
            PerformanceEq("Unrecognized PowerShell error output", PsRunner.NormalizeError("#< CLIXML\n<Objs><S S=\"Error\">malformed"));
            PerformanceEq("Unrecognized PowerShell error output", PsRunner.NormalizeError("<Objs><S S=\"Error\">&external;</S></Objs>"));
            PerformanceEq(true, PsRunner.NormalizeError(new string('x', 3000)).Length <= 2001);
            string original = "2026-10-04 11:27:35 WARN Performance PowerShell 执行失败 exit 1 #< CLIXML\n" + xml;
            var entries = LogStreamView.ParseEvents(original + "\n2026-10-04 11:27:36 FAIL REAL actual error");
            PerformanceEq(2, entries.Count); PerformanceEq(LogEventSeverity.Warning, entries[0].Severity);
            PerformanceEq("11:27:35", entries[0].Time); PerformanceEq(original, entries[0].Raw);
            PerformanceEq(true, entries[0].Message.Contains("测试错误 0x800106ba"));
            PerformanceEq(false, entries[0].Message.Contains("<Objs"));
            PerformanceEq(LogEventSeverity.Error, entries[1].Severity);
            int warnings, errors;
            DiagnosticSummary.CountIssues(original + "\n2026-10-04 11:27:36 FAIL REAL actual error", out warnings, out errors);
            PerformanceEq(1, warnings); PerformanceEq(1, errors);
            entries = LogStreamView.ParseEvents("2026-10-04 11:27:35 WARN Performance #< CLIXML\n<Objs>\nFAIL REAL actual error");
            PerformanceEq(2, entries.Count); PerformanceEq(LogEventSeverity.Warning, entries[0].Severity);
            PerformanceEq(LogEventSeverity.Error, entries[1].Severity);
            entries = LogStreamView.ParseEvents(xml); PerformanceEq(1, entries.Count);
            PerformanceEq(LogEventSeverity.Error, entries[0].Severity);
            string stdout, failure;
            PerformanceEq(true, PsRunner.RunWithFailure("Write-Output '成功; 游戏 $x ` 字符'", "Fixture", 10000, out stdout, out failure));
            PerformanceEq("成功; 游戏 $x ` 字符", stdout.Trim()); PerformanceEq("", failure);
            PerformanceEq(false, PsRunner.RunWithFailure("$ErrorActionPreference='Stop'; throw '测试错误 0x800106ba'", "Fixture", 10000, out stdout, out failure));
            PerformanceEq(true, failure.Contains("测试错误 0x800106ba"));
            PerformanceEq(true, failure.Contains("exit 1"));
            PerformanceEq(false, failure.Contains("<Objs") || failure.Contains("CLIXML") || failure.Contains("\n") || failure.Contains("�"));
            PerformanceEq(false, PsRunner.RunWithFailure("Write-Error 'nonterminating fixture'", "Fixture", 10000, out stdout, out failure));
            PerformanceEq(true, failure.Contains("nonterminating fixture") && failure.Contains("exit 1"));
            PerformanceEq(true, PsRunner.RunWithFailure("Write-Error 'handled by continued script'; Write-Output 'continued'", "Fixture", 10000, out stdout, out failure));
            PerformanceEq("continued", stdout.Trim()); PerformanceEq("", failure);
            PerformanceEq(true, PsRunner.RunWithFailure("try { throw 'handled' } catch { Write-Output 'recovered' }", "Fixture", 10000, out stdout, out failure));
            PerformanceEq("recovered", stdout.Trim());
            PerformanceEq(true, PsRunner.Run("Write-Output $env:PaviseFixtureValue", "Fixture", 10000,
                new Dictionary<string, string> { { "PaviseFixtureValue", "游戏 ' $x ; 字符" } }, out stdout));
            PerformanceEq("游戏 ' $x ; 字符", stdout.Trim());
            PerformanceEq(false, PsRunner.RunWithFailure("[Console]::Error.WriteLine('native error'); exit 7", "Fixture", 10000, out stdout, out failure));
            PerformanceEq(true, failure.Contains("exit 7") && failure.Contains("native error"));
            PerformanceEq(false, PsRunner.RunWithFailure("Start-Sleep -Seconds 10", "Fixture", 100, out stdout, out failure));
            PerformanceEq(true, failure.Contains("100ms"));
        }
        private static void WaitPerformance(Func<bool> ready)
        {
            var time = System.Diagnostics.Stopwatch.StartNew();
            while (!ready() && time.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            PerformanceEq(true, ready());
        }
    }
}
#endif
