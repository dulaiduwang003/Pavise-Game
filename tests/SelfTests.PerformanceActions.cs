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
            PerformanceActionBackend.ResourcePlatformForTest = delegate { return "contextual"; };
            try
            {
                ActionReceiptRoundTrip(); ActionReceiptFailureBoundaries(); ActionComponentRestore();
                ActionRoutingAndCommands(); ActionImageCollisions(); ActionEnvironmentRebootState();
                ActionEnvironmentPartialFailure(); ActionEnvironmentRetryStatus(); ActionEnvironmentVbs(); ActionEnvironmentBundle(); ActionEnvironmentQuantumIndependent(); ActionEnvironmentTimerConcurrency(); ActionCoordinatorLifecycle(); ActionCoordinatorMixedResults(); ActionCoordinatorBatchedRecords();
                ActionDefenderInactive(); ActionDefenderFailureRecord(); ActionPowerShellFailures();
                ActionCoordinatorRetryBudget(); ActionDiscoveryBudget(); ActionRestoreIsolation(); ActionBackgroundTargetMoves(); ActionLoggerRecords();
                RunEnvironmentBundleLayoutRegressionTests();
                RunMatchVisualEffectsTests();
            }
            finally
            {
                PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null;
                PerformanceActionBackend.TargetsForTest = null; PerformanceTuning.BootForTest = null;
                PerformanceActionBackend.DefenderServiceForTest = null;
                PerformanceActionBackend.ResourcePlatformForTest = null; PerformanceActionBackend.ShortNameVolumeForTest = null;
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
            }
            PerformanceEq(6, automatic); PerformanceEq(7, environment);
            PerformanceEq(true, PerformanceActionCatalog.Find(PerformanceActionCatalog.LegacyDefenderSession) != null);
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
            PerformanceEq(true, PerformanceActionBackend.ResourceTarget("C:\\Games\\Title\\Bin\\Game.exe", "C:\\Games\\Title").StartsWith("C:\\Games\\Title\\:{Process:"));
            PerformanceEq(null, PerformanceActionBackend.ResourceTarget("C:\\Games\\Title\\Bad{Game}.exe"));
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
                PerformanceEq(7, PerformanceTuning.EnvironmentEnabledCount()); PerformanceEq(true, PerformanceTuning.EnvironmentBundleBusy());
                PerformanceEq(false, Settings.LoadCached(PerformanceActionCatalog.Quantum, false));
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
                PerformanceEq("unsupported", PerformanceTuning.State(PerformanceActionCatalog.StorageCold));
                PerformanceEq("d:131", values[PerformanceActionCatalog.CpuMitigations + "|override"]);
                PerformanceEq("d:11", values[PerformanceActionCatalog.CpuMitigations + "|mask"]);
                PerformanceEq("b:AAAAAA==", values[PerformanceActionCatalog.AudioIdle + "|device"]);
                PerformanceEq(5, PerformanceActionStore.All().Count);
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
            var values = new Dictionary<string, string>(); var valueGate = new object();
            const string first = @"C:\A\GameA.exe", second = @"C:\B\GameB.exe";
            string resourceSecond = PerformanceActionBackend.ResourceTarget(second);
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
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
                PerformanceEq("1", PerformanceActionBackend.ReadForTest(PerformanceActionCatalog.DefenderResources, resourceSecond));
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
                for (int round = 0; round < 2; round++)
                {
                    coordinator.Queue(true, true, new string[0], new[] { "C:\\Browser.exe" });
                    WaitPerformance(delegate { return coordinator.State(PerformanceActionCatalog.BackgroundFps) == "configured"; });
                    PerformanceEq("30", PerformanceActionBackend.ReadForTest(PerformanceActionCatalog.BackgroundFps, "C:\\Browser.exe"));
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
            int service = 1, defenderReads = 0, defenderWrites = 0;
            long now = DateTime.UtcNow.Ticks;
            var values = new Dictionary<string, string>();
            PerformanceActionBackend.DefenderServiceForTest = delegate { return service; };
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                if (id == PerformanceActionCatalog.LegacyDefenderSession || id == PerformanceActionCatalog.DefenderResources) defenderReads++;
                string value;
                return values.TryGetValue(id + "|" + target, out value) ? value : ActionMockDefault(id);
            };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                if (id == PerformanceActionCatalog.LegacyDefenderSession || id == PerformanceActionCatalog.DefenderResources) defenderWrites++;
                values[id + "|" + target] = value == "off64" ? "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False" : value;
                return true;
            };
            var coordinator = new PerformanceActionCoordinator { ClockForTest = delegate { return now; } };
            string[] games = { @"C:\A\GameA.exe", @"C:\B\GameB.exe" };
            try
            {
                coordinator.Queue(true, true, games, new[] { @"C:\Browser.exe" });
                PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq("", coordinator.State(PerformanceActionCatalog.LegacyDefenderSession));
                PerformanceEq("inactive", coordinator.State(PerformanceActionCatalog.DefenderResources));
                PerformanceEq("configured", coordinator.State(PerformanceActionCatalog.BackgroundFps));
                PerformanceEq(0, defenderReads); PerformanceEq(0, defenderWrites);
                coordinator.Queue(true, false, games, new string[0]);
                PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq("configured", coordinator.State(PerformanceActionCatalog.Mitigations));
                PerformanceEq("configured", coordinator.State(PerformanceActionCatalog.OpenGl));
                PerformanceEq("applied", coordinator.State(PerformanceActionCatalog.ShortNames));
                PerformanceEq(0, defenderReads); PerformanceEq(0, defenderWrites);
                service = 4; now += TimeSpan.FromMinutes(1).Ticks;
                coordinator.Queue(true, false, games, new string[0]);
                PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq("applied", coordinator.State(PerformanceActionCatalog.DefenderResources));
                PerformanceEq(2, defenderWrites);
                values[PerformanceActionCatalog.LegacyDefenderSession + "|Defender"] = "True,True";
                SeedLegacyDefenderReceipt();
                int reads = defenderReads, writes = defenderWrites;
                foreach (int stopped in new[] { 1, 0, 3 })
                {
                    service = stopped; now += TimeSpan.FromMinutes(1).Ticks;
                    coordinator.Queue(true, false, games, new string[0]);
                    PerformanceEq(true, coordinator.DrainForTest(3000));
                    PerformanceEq(false, coordinator.WaitForSessionRestore(3000));
                    PerformanceEq("restore-failed", coordinator.State(PerformanceActionCatalog.LegacyDefenderSession));
                    PerformanceEq(reads, defenderReads); PerformanceEq(writes, defenderWrites);
                    PerformanceEq(true, PerformanceActionStore.All().Exists(delegate(PerformanceActionStore.Receipt r) { return r.Id == PerformanceActionCatalog.LegacyDefenderSession; }));
                }
                service = 4; now += TimeSpan.FromMinutes(1).Ticks;
                coordinator.Queue(true, false, games, new string[0]);
                PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(true, coordinator.WaitForSessionRestore(3000));
                PerformanceEq("False,False", values[PerformanceActionCatalog.LegacyDefenderSession + "|Defender"]);
                PerformanceEq("restored", coordinator.State(PerformanceActionCatalog.LegacyDefenderSession));
                PerformanceEq(false, PerformanceActionStore.All().Exists(delegate(PerformanceActionStore.Receipt r) { return r.Id == PerformanceActionCatalog.LegacyDefenderSession; }));
                PerformanceEq(true, coordinator.Close(3000));
                PerformanceEq(true, PerformanceTuning.RestoreAll()); PerformanceEq(0, PerformanceActionStore.All().Count);
            }
            finally { service = 4; coordinator.Close(3000); PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; }; }
        }
        private static void ActionDefenderFailureRecord()
        {
            Settings.UseTransientStoreForCurrentProcess();
            int service = 4, reads = 0;
            long now = DateTime.UtcNow.Ticks;
            string oldPath = Logger.LogPath;
            bool oldWrites = Logger.WritesEnabled;
            string path = Path.Combine(Path.GetTempPath(), "PaviseFailureFixture-" + Guid.NewGuid().ToString("N") + ".log");
            var values = new Dictionary<string, string>();
            PerformanceActionBackend.DefenderServiceForTest = delegate { return service; };
            PerformanceActionBackend.ResourcePlatformForTest = delegate { return "contextual"; };
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                if (id == PerformanceActionCatalog.DefenderResources) { reads++; throw new IOException("provider failure 0x800106ba"); }
                string value; return values.TryGetValue(id + "|" + target, out value) ? value : ActionMockDefault(id);
            };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                if (id == PerformanceActionCatalog.DefenderResources) throw new InvalidOperationException("A rejected query must never write");
                values[id + "|" + target] = value == "off64" ? "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False" : value; return true;
            };
            var coordinator = new PerformanceActionCoordinator { ClockForTest = delegate { return now; } };
            string[] games = { @"C:\A\GameA.exe" };
            Func<int> warnings = delegate {
                return LogStreamView.ParseEvents(File.ReadAllText(path)).FindAll(delegate(LogEventEntry e) { return e.Severity == LogEventSeverity.Warning; }).Count;
            };
            try
            {
                Logger.LogPath = path; Logger.WritesEnabled = true;
                coordinator.Queue(true, false, games, new string[0]);
                PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq("stopped", coordinator.State(PerformanceActionCatalog.DefenderResources));
                PerformanceEq(1, reads); PerformanceEq(1, warnings());
                PerformanceEq(true, File.ReadAllText(path).Contains("provider failure 0x800106ba"));
                int records = File.ReadAllLines(path).Length;
                now += TimeSpan.FromMinutes(1).Ticks;
                coordinator.Queue(true, false, games, new string[0]);
                PerformanceEq(true, coordinator.DrainForTest(3000)); PerformanceEq(1, reads);
                PerformanceEq(records, File.ReadAllLines(path).Length);
                now += TimeSpan.FromMinutes(4).Ticks;
                coordinator.Queue(true, false, games, new string[0]);
                PerformanceEq(true, coordinator.DrainForTest(3000)); PerformanceEq(1, reads);
                PerformanceEq(records, File.ReadAllLines(path).Length); PerformanceEq(1, warnings());
                PerformanceEq("stopped", coordinator.State(PerformanceActionCatalog.DefenderResources));
                service = 1; now += TimeSpan.FromMinutes(1).Ticks;
                coordinator.Queue(true, false, games, new string[0]);
                PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq("inactive", coordinator.State(PerformanceActionCatalog.DefenderResources)); PerformanceEq(1, reads);
                PerformanceEq(true, coordinator.Close(3000));
            }
            finally
            {
                coordinator.Close(3000); Logger.WritesEnabled = oldWrites; Logger.LogPath = oldPath;
                PerformanceActionBackend.DefenderServiceForTest = delegate { return 4; };
                if (File.Exists(path)) File.Delete(path);
            }
        }
        private static string ActionMockDefault(string id)
        {
            return id == PerformanceActionCatalog.LegacyDefenderSession ? "False,False"
                : id == PerformanceActionCatalog.DefenderResources ? "0"
                : id == PerformanceActionCatalog.Mitigations ? "NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False" : "absent";
        }
        private static void ActionCoordinatorRetryBudget()
        {
            foreach (string failingId in new[] { PerformanceActionCatalog.Mitigations, PerformanceActionCatalog.OpenGl,
                PerformanceActionCatalog.DefenderResources, PerformanceActionCatalog.ShortNames,
                PerformanceActionCatalog.BackgroundFps })
            {
                Settings.UseTransientStoreForCurrentProcess();
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
            int platforms = 0, volumes = 0, resourceReads = 0; long now = DateTime.UtcNow.Ticks;
            var values = new Dictionary<string, string>();
            PerformanceActionBackend.ResourcePlatformForTest = delegate { platforms++; throw new IOException("platform query denied"); };
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
                PerformanceEq(1, platforms); PerformanceEq(1, volumes); PerformanceEq(0, resourceReads);
                now += TimeSpan.FromMinutes(1).Ticks;
                coordinator.Queue(true, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(1, platforms); PerformanceEq(1, volumes);
                now += TimeSpan.FromMinutes(4).Ticks;
                coordinator.Queue(true, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq(2, platforms); PerformanceEq(2, volumes);
                for (int i = 0; i < 100; i++)
                {
                    now += TimeSpan.FromMinutes(10).Ticks;
                    coordinator.Queue(true, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                }
                PerformanceEq(102, platforms); PerformanceEq(102, volumes); PerformanceEq(0, resourceReads);
                PerformanceEq("failed", coordinator.State(PerformanceActionCatalog.DefenderResources));
                PerformanceEq("failed", coordinator.State(PerformanceActionCatalog.ShortNames));
                PerformanceEq(0, ActionFailureStore.Records().Count);
                PerformanceEq("configured", coordinator.State(PerformanceActionCatalog.Mitigations));
                PerformanceEq("configured", coordinator.State(PerformanceActionCatalog.OpenGl));
                PerformanceActionBackend.ResourcePlatformForTest = delegate { platforms++; return ""; };
                coordinator.Queue(false, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                coordinator.Queue(true, false, games, new string[0]); PerformanceEq(true, coordinator.DrainForTest(3000));
                PerformanceEq("failed", coordinator.State(PerformanceActionCatalog.DefenderResources)); PerformanceEq(0, resourceReads);
                PerformanceEq(true, coordinator.Close(3000)); PerformanceEq(true, PerformanceTuning.RestoreAll());
            }
            finally
            {
                coordinator.Close(3000); PerformanceActionBackend.ResourcePlatformForTest = delegate { return "contextual"; };
                PerformanceActionBackend.ShortNameVolumeForTest = null;
            }
        }
        private static void ActionRestoreIsolation()
        {
            foreach (string failedId in new[] { PerformanceActionCatalog.LegacyDefenderSession, PerformanceActionCatalog.BackgroundFps })
            {
                bool legacyFails = failedId == PerformanceActionCatalog.LegacyDefenderSession;
                Settings.UseTransientStoreForCurrentProcess();
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
