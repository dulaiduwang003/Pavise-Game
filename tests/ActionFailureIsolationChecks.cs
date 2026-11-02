#if PAVISE_SELFTEST && PAVISE_ACTION_FAILURE_RUNNER
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int isolationAssertions;
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                StoreKeyCache(); StoreIsolation(); StoreUnreadable(); IntentCrashBoundaries(); LegacyIsolation(); EnvironmentIsolation(); CpuMitigationGroupIsolation(); CoordinatorIsolation(); TimerIsolation(); StandbyIsolation(); RunEnvFailureIsolationChecks(); RunBoostFailureIsolationChecks();
                Console.WriteLine("PASS action failure isolation assertions=" + isolationAssertions + " settings=transient backend=mocked application_started=false native_writes=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { Logger.WritesEnabled = false; Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest(); }
        }
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application runtime unavailable in isolation checks"); }
        private static void Check(bool ok, string description)
        { isolationAssertions++; if (!ok) throw new InvalidOperationException(description); }
        private static void Fresh()
        { Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest(); PerformanceTuning.ResetForTest(); }
        private static void StoreKeyCache()
        {
            Fresh();
            string key = ActionFailureStore.RecordKey(" fixture ", @" C:\Game.exe ");
            Check(object.ReferenceEquals(key, ActionFailureStore.RecordKey("FIXTURE", @"c:\game.EXE")), "normalized identity did not reuse its cached key");
            Check(ActionFailureStore.RecordKeyCacheCountForTest == 1, "normalization created duplicate cache entries");
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                string expected = ActionFailureStore.Prefix + BitConverter.ToString(hash.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes("FIXTURE\nC:\\GAME.EXE"))).Replace("-", "").ToLowerInvariant();
                Check(key == expected, "cached key changed the persistent identity format");
            }
            for (int i = 0; i < ActionFailureStore.RecordKeyCacheLimit + 20; i++)
                ActionFailureStore.RecordKey("fixture", "target-" + i);
            Check(ActionFailureStore.RecordKeyCacheCountForTest == ActionFailureStore.RecordKeyCacheLimit, "record-key cache exceeded its entry limit");
            Check(ActionFailureStore.RecordKey("fixture", @"C:\Game.exe") == key, "cache eviction changed an existing persistent identity");
            ActionFailureStore.ReloadForTest();
            string boundary = new string('x', ActionFailureStore.RecordKeyCacheMaxChars - 2);
            ActionFailureStore.RecordKey("f", boundary);
            Check(ActionFailureStore.RecordKeyCacheCountForTest == 1, "maximum-length identity was not cached");
            string oversized = new string('x', ActionFailureStore.RecordKeyCacheMaxChars + 1);
            string longKey = ActionFailureStore.RecordKey("fixture", oversized);
            Check(longKey == ActionFailureStore.RecordKey(" FIXTURE ", oversized.ToUpperInvariant()), "uncached long identity was not normalized");
            Check(ActionFailureStore.RecordKeyCacheCountForTest == 1, "oversized identity changed the bounded cache");
            ActionFailureStore.Trip("fixture", @"C:\Game.exe", "fixture failure");
            Check(ActionFailureStore.IsBlocked("fixture", @"C:\Game.exe"), "cached key concealed a newly recorded failure");
            string reason;
            Check(ActionFailureStore.RequestRetry(key) && ActionFailureStore.TryBeginApply("fixture", @"C:\Game.exe", out reason)
                && ActionFailureStore.CompleteApply("fixture", @"C:\Game.exe") && !ActionFailureStore.IsBlocked("fixture", @"C:\Game.exe"),
                "cached key retained stale isolation after verified retry");
            Settings.SaveStrDurable(key, "invalid externally replaced record");
            Check(ActionFailureStore.IsBlocked("fixture", @"C:\Game.exe"), "key cache concealed an external persistence change");
        }
        private static void StoreIsolation()
        {
            Fresh(); string reason;
            Check(!ActionFailureStore.CompleteApply("unadmitted", "system"), "completion without a durable admission was accepted");
            Settings.Save("fixture-choice", true);
            Check(ActionFailureStore.TryBeginApply("fixture", @"C:\Game.exe", out reason), "healthy action denied");
            ActionFailureStore.Trip("fixture", @"C:\Game.exe", "readback failed", "GPU fixture / driver 1");
            Check(Settings.Load("fixture-choice", false), "trip changed user preference");
            IList<ActionFailureRecord> records = ActionFailureStore.Records();
            Check(records.Count == 1 && records[0].Environment.Contains("driver 1") && records[0].OccurredUtc.Kind == DateTimeKind.Utc, "failure metadata missing");
            ActionFailureStore.ReloadForTest();
            Check(ActionFailureStore.IsBlocked("fixture", @"c:\game.EXE"), "restart or case changed isolation identity");
            Check(!ActionFailureStore.TryBeginApply("fixture", @"C:\Game.exe", out reason), "failed action retried automatically");
            Check(ActionFailureStore.TryBeginApply("other", @"C:\Game.exe", out reason), "unrelated action isolated");
            Check(ActionFailureStore.CompleteApply("other", @"C:\Game.exe"), "healthy completion failed");
            Check(ActionFailureStore.RequestRetry(records[0].Key), "explicit retry denied");
            Check(!ActionFailureStore.RequestRetry(records[0].Key), "duplicate permission admitted");
            Check(ActionFailureStore.IsIsolated("fixture", @"C:\Game.exe") && ActionFailureStore.IsRetryPending("fixture", @"C:\Game.exe"), "retry deleted failure prematurely");
            Check(ActionFailureStore.TryBeginApply("fixture", @"C:\Game.exe", out reason), "retry did not admit once");
            Check(!ActionFailureStore.TryBeginApply("fixture", @"C:\Game.exe", out reason), "retry admitted concurrently twice");
            ActionFailureStore.CancelApply("fixture", @"C:\Game.exe");
            Check(ActionFailureStore.IsRetryPending("fixture", @"C:\Game.exe"), "normal cancellation counted as execution failure");
            Check(ActionFailureStore.TryBeginApply("fixture", @"C:\Game.exe", out reason), "cancelled retry permission lost");
            ActionFailureStore.Trip("fixture", @"C:\Game.exe", "retry failed");
            Check(!ActionFailureStore.IsRetryPending("fixture", @"C:\Game.exe") && ActionFailureStore.IsBlocked("fixture", @"C:\Game.exe"), "failed retry stayed permitted");
            Check(ActionFailureStore.RequestRetry(records[0].Key), "second explicit retry denied");
            Check(ActionFailureStore.TryBeginApply("fixture", @"C:\Game.exe", out reason), "second retry denied at execution");
            ActionFailureStore.SaveForTest = delegate(string key, string value) { return value.Length != 0 && Settings.SaveStrDurable(key, value); };
            Check(!ActionFailureStore.CompleteApply("fixture", @"C:\Game.exe") && ActionFailureStore.IsBlocked("fixture", @"C:\Game.exe"), "failed durable clear reopened action");
            ActionFailureStore.CancelApply("fixture", @"C:\Game.exe"); // No backend write occurred in this store-only fixture.
            ActionFailureStore.SaveForTest = null;
            Check(ActionFailureStore.RequestRetry(records[0].Key), "repaired persistence could not accept an explicit retry");
            Check(ActionFailureStore.TryBeginApply("fixture", @"C:\Game.exe", out reason) && ActionFailureStore.CompleteApply("fixture", @"C:\Game.exe"), "verified retry did not clear isolation");
            ActionFailureStore.ReloadForTest(); Check(ActionFailureStore.Records().Count == 0 && !ActionFailureStore.IsIsolated("fixture", @"C:\Game.exe"), "verified clearance did not survive restart");
        }
        private static void StoreUnreadable()
        {
            Fresh(); string key = ActionFailureStore.RecordKey("bad", "system"), reason;
            Settings.SaveStrDurable(key, "broken");
            Check(ActionFailureStore.IsBlocked("bad", "system") && !ActionFailureStore.RequestRetry(key), "corrupt record admitted retry");
            Check(ActionFailureStore.TryBeginApply("good", "system", out reason), "corrupt record blocked unrelated action"); ActionFailureStore.CompleteApply("good", "system");
            Settings.BeforeStoredNamesReadForTest = delegate(string prefix) { if (prefix == ActionFailureStore.Prefix) throw new IOException("fixture unavailable"); };
            bool threw = false; try { ActionFailureStore.Records(); } catch (InvalidOperationException) { threw = true; }
            finally { Settings.BeforeStoredNamesReadForTest = null; }
            Check(threw, "unreadable enumeration reported an empty healthy list");
            Fresh(); ActionFailureStore.SaveForTest = delegate { return false; }; ActionFailureStore.Trip("volatile", "system", "disk unavailable");
            Check(ActionFailureStore.IsBlocked("volatile", "system") && ActionFailureStore.Records()[0].PersistenceFailed, "failed persistence forgot in-process isolation");
            Check(!ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey("volatile", "system")), "retry admitted without durable failure record");
            ActionFailureStore.SaveForTest = null;
        }
        private static void LegacyIsolation()
        {
            Fresh(); Settings.Save("EnvFuse_standby", true); Settings.SaveStr("NvFailStreak_prerender", "1"); Settings.SaveStr("PowerPlanFailStreak", "1");
            Check(ActionFailureStore.IsBlocked(PolicyCatalog.KeyStandbyCleaner, "system"), "legacy environment fuse not imported");
            Check(ActionFailureStore.IsBlocked(PolicyCatalog.KeyNvLowLat, "Game.exe") && ActionFailureStore.IsBlocked(PolicyCatalog.KeyPowerPlan, "system"), "legacy NV/power first failure lost");
            string key = ActionFailureStore.RecordKey(PolicyCatalog.KeyNvLowLat, "*"), reason;
            Check(ActionFailureStore.RequestRetry(key) && ActionFailureStore.TryBeginApply(PolicyCatalog.KeyNvLowLat, "Game.exe", out reason), "legacy retry not admitted");
            ActionFailureStore.Trip(PolicyCatalog.KeyNvLowLat, "Game.exe", "failed again");
            Check(ActionFailureStore.Records().Count == 3, "legacy retry produced duplicate overlapping records");
            Check(ActionFailureStore.RequestRetry(key) && ActionFailureStore.TryBeginApply(PolicyCatalog.KeyNvLowLat, "Game.exe", out reason) && ActionFailureStore.CompleteApply(PolicyCatalog.KeyNvLowLat, "Game.exe"), "legacy verified retry failed");
            ActionFailureStore.ReloadForTest(); Check(!ActionFailureStore.IsIsolated(PolicyCatalog.KeyNvLowLat, "Game.exe"), "old migrated counter silently re-isolated successful retry");
            Fresh(); Settings.Save("EnvFuse_awake", true); Check(ActionFailureStore.IsIsolated(PolicyCatalog.KeyAwake, "system"), "legacy completion fixture migration failed");
            Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(PolicyCatalog.KeyAwake, "*")) && ActionFailureStore.TryBeginApply(PolicyCatalog.KeyAwake, "system", out reason), "legacy completion fixture admission failed");
            ActionFailureStore.SaveForTest = delegate(string name, string value) { return value.Length != 0 && Settings.SaveStrDurable(name, value); };
            Check(!ActionFailureStore.CompleteApply(PolicyCatalog.KeyAwake, "system"), "legacy failed completion reported success");
            ActionFailureStore.ReloadForTest(); Check(ActionFailureStore.Records().Count == 1 && ActionFailureStore.IsBlocked(PolicyCatalog.KeyAwake, "system"), "legacy unfinished retry created duplicate overlapping isolation");
        }
        private static void IntentCrashBoundaries()
        {
            Fresh(); string reason;
            Check(ActionFailureStore.TryBeginApply("interrupted", "system", out reason), "could not persist first execution intent");
            ActionFailureStore.ReloadForTest();
            Check(ActionFailureStore.IsBlocked("interrupted", "system") && ActionFailureStore.Records().Count == 1, "crash between admission and verified completion reopened action");
            Check(ActionFailureStore.TryBeginApply("unrelated", "system", out reason) && ActionFailureStore.CompleteApply("unrelated", "system"), "interrupted action isolated unrelated actions");
            Fresh(); ActionFailureStore.SaveForTest = delegate(string key, string value) { return !key.StartsWith(ActionFailureStore.AttemptPrefix, StringComparison.Ordinal) && Settings.SaveStrDurable(key, value); };
            Check(!ActionFailureStore.TryBeginApply("unsaved", "system", out reason), "failed execution intent persistence admitted an apply");
            ActionFailureStore.SaveForTest = null; ActionFailureStore.ReloadForTest();
            Check(ActionFailureStore.IsBlocked("unsaved", "system"), "intent preflight failure was forgotten on restart");
            Fresh(); ActionFailureStore.SaveForTest = delegate { return true; };
            Check(!ActionFailureStore.TryBeginApply("lying-save", "system", out reason), "unverified save acknowledgement admitted execution");
            Fresh(); Check(ActionFailureStore.TryBeginApply("failed-trip", "system", out reason), "trip fixture intent failed");
            ActionFailureStore.SaveForTest = delegate(string key, string value) { return !key.StartsWith(ActionFailureStore.Prefix, StringComparison.Ordinal) && Settings.SaveStrDurable(key, value); };
            ActionFailureStore.Trip("failed-trip", "system", "write failed and storage unavailable");
            Check(ActionFailureStore.IsBlocked("failed-trip", "system"), "failed Trip persistence reopened in-process action");
            ActionFailureStore.SaveForTest = null; ActionFailureStore.ReloadForTest();
            Check(ActionFailureStore.IsBlocked("failed-trip", "system"), "durable intent did not protect failed Trip across restart");
            Fresh(); Check(ActionFailureStore.TryBeginApply("normal-cancel", "system", out reason), "cancel intent admission failed");
            ActionFailureStore.CancelApply("normal-cancel", "system"); ActionFailureStore.ReloadForTest();
            Check(ActionFailureStore.Records().Count == 0 && !ActionFailureStore.IsIsolated("normal-cancel", "system"), "cancelled-before-write intent isolated on restart");
            foreach (bool retry in new[] { false, true })
            {
                Fresh(); const string action = "empty-then-failed";
                if (retry) { ActionFailureStore.Trip(action, "system", "original failure"); Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(action, "system")), "empty-clear retry setup failed"); }
                Check(ActionFailureStore.TryBeginApply(action, "system", out reason), "empty-clear fixture admission failed");
                ActionFailureStore.SaveForTest = delegate(string name, string value) { Settings.SaveStrDurable(name, value); return value.Length != 0; };
                Check(!ActionFailureStore.CompleteApply(action, "system"), "unconfirmed empty write reported successful completion");
                ActionFailureStore.ReloadForTest();
                Check(ActionFailureStore.IsBlocked(action, "system"), "empty write followed by failure and restart lost durable isolation");
            }
            Fresh(); ActionFailureStore.Trip("last-clear", "system", "original failure");
            Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey("last-clear", "system")) && ActionFailureStore.TryBeginApply("last-clear", "system", out reason), "last intent-clear setup failed");
            ActionFailureStore.SaveForTest = delegate(string name, string value) { Settings.SaveStrDurable(name, value); return value.Length != 0 || !name.StartsWith(ActionFailureStore.AttemptPrefix, StringComparison.Ordinal); };
            Check(!ActionFailureStore.CompleteApply("last-clear", "system"), "failed final intent clearance reported success");
            ActionFailureStore.ReloadForTest(); Check(ActionFailureStore.IsBlocked("last-clear", "system"), "both tombstones written before failure lost isolation across restart");
        }
        private static void EnvironmentIsolation()
        {
            Fresh(); const string id = PerformanceActionCatalog.LastAccess, target = "fixture-registry";
            string value = "d:0"; bool denyRestore = true, fail = true; int applies = 0;
            PerformanceTuning.BootForTest = delegate { return BootSessionIdentity.Create(new Guid("12345678-1234-1234-1234-1234567890ab"), 1); };
            PerformanceActionBackend.TargetsForTest = delegate { return new[] { target }; };
            PerformanceActionBackend.ReadForTest = delegate { return value; };
            PerformanceActionBackend.WriteForTest = delegate(string action, string name, string next) {
                if (next == "d:0") { if (denyRestore) return false; value = next; return true; }
                applies++; value = next; return !fail;
            };
            Settings.Save(id, true);
            Check(!PerformanceTuning.ApplyEnvironment(id, true) && applies == 1, "first failed environment apply not observed");
            Check(ActionFailureStore.IsBlocked(id, target) && PerformanceActionStore.All().Count == 1, "restore failure lost receipt or isolation");
            Check(!PerformanceTuning.ApplyEnvironment(id, true) && applies == 1 && Settings.Load(id, false), "isolated environment action reapplied or choice changed");
            string key = ActionFailureStore.RecordKey(id, target); Check(ActionFailureStore.RequestRetry(key), "environment retry denied");
            Check(!PerformanceTuning.ApplyEnvironment(id, true) && applies == 1 && !ActionFailureStore.IsRetryPending(id, target), "retry applied before recovery or remained authorized after recovery failure");
            denyRestore = false; Check(PerformanceTuning.ApplyEnvironment(id, false) && value == "d:0", "isolation prevented restoration");
            fail = false; Check(ActionFailureStore.RequestRetry(key), "environment retry after recovery denied");
            Check(PerformanceTuning.ApplyEnvironment(id, true) && applies == 2 && PerformanceActionStore.All()[0].Original == "d:0", "retry snapshotted half-applied state");
            Check(!ActionFailureStore.IsIsolated(id, target), "verified environment retry left isolation");
            Check(PerformanceTuning.ApplyEnvironment(id, false) && value == "d:0", "healthy environment restore changed original");
            Fresh(); PerformanceActionBackend.TargetsForTest = delegate { return new string[0]; };
            Check(!PerformanceTuning.ApplyEnvironment(id, true) && ActionFailureStore.Records().Count == 0, "unsupported target classified as failure");
            PerformanceActionBackend.TargetsForTest = delegate { return new[] { target }; };
            PerformanceActionBackend.ReadForTest = delegate { throw new OperationCanceledException(); };
            Check(!PerformanceTuning.ApplyEnvironment(id, true) && ActionFailureStore.Records().Count == 0, "normal cancellation classified as failure");
        }
        private static void CoordinatorIsolation()
        {
            Fresh(); const string target = @"C:\Fixture\Game.exe"; long now = DateTime.UtcNow.Ticks;
            var values = new Dictionary<string, string>(); int badApplies = 0;
            PerformanceActionBackend.DefenderServiceForTest = delegate { return 1; };
            PerformanceActionBackend.ShortNameVolumeForTest = delegate { return null; };
            PerformanceActionBackend.ReadForTest = delegate(string id, string name) {
                string value; return values.TryGetValue(id + name, out value) ? value : id == PerformanceActionCatalog.Mitigations ? "NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False" : "absent";
            };
            PerformanceActionBackend.WriteForTest = delegate(string id, string name, string value) {
                if (id == PerformanceActionCatalog.OpenGl) { badApplies++; return false; }
                values[id + name] = value == "off64" ? "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False" : value; return true;
            };
            // 这些动作只按用户显式选择执行：未选择时守护开启也不得写入。
            var unselected = new PerformanceActionCoordinator { ClockForTest = delegate { return now; } };
            try
            {
                unselected.Queue(true, false, new[] { target }); Check(unselected.DrainForTest(3000), "unselected coordinator did not drain");
                Check(badApplies == 0 && values.Count == 0, "guard applied actions the user never selected");
            }
            finally { Check(unselected.Close(3000), "unselected coordinator did not close"); }
            foreach (string id in new[] { PerformanceActionCatalog.Mitigations, PerformanceActionCatalog.OpenGl, PerformanceActionCatalog.ShortNames })
                Settings.Save(id, true);
            for (int session = 0; session < 2; session++)
            {
                if (session == 1) ActionFailureStore.ReloadForTest();
                var coordinator = new PerformanceActionCoordinator { ClockForTest = delegate { return now; } };
                try
                {
                    for (int mode = 0; mode < 3; mode++)
                    {
                        coordinator.Queue(true, false, new[] { target }); Check(coordinator.DrainForTest(3000), "coordinator did not drain");
                        Check(badApplies == 1 && coordinator.State(PerformanceActionCatalog.OpenGl) == "stopped", "mode/session/restart bypassed persistent isolation");
                        coordinator.Queue(false, false, new string[0]); Check(coordinator.DrainForTest(3000), "guard off did not drain"); now += TimeSpan.FromMinutes(31).Ticks;
                    }
                }
                finally { Check(coordinator.Close(3000), "coordinator restoration did not close"); }
            }
        }
        private static void CpuMitigationGroupIsolation()
        {
            foreach (string bad in new[] { "override", "mask" })
            {
                Fresh(); const string id = PerformanceActionCatalog.CpuMitigations;
                var values = new Dictionary<string, string> { { "override", "d:128" }, { "mask", "d:8" } };
                var applied = new List<string>(); bool fail = true;
                PerformanceTuning.BootForTest = delegate { return BootSessionIdentity.Create(new Guid("12345678-1234-1234-1234-1234567890ab"), 1); };
                PerformanceActionBackend.TargetsForTest = delegate { return new[] { "override", "mask" }; };
                PerformanceActionBackend.ReadForTest = delegate(string action, string target) { return values[target]; };
                PerformanceActionBackend.WriteForTest = delegate(string action, string target, string value) {
                    bool restoring = value == (target == "override" ? "d:128" : "d:8");
                    values[target] = value; if (!restoring) applied.Add(target);
                    return restoring || !fail || target != bad;
                };
                Check(!PerformanceTuning.ApplyEnvironment(id, true), "CPU coupled failure was reported healthy");
                Check(values["override"] == "d:128" && values["mask"] == "d:8" && PerformanceActionStore.All().Count == 0, "CPU coupled rollback did not restore both original fields");
                Check(applied.Count == (bad == "override" ? 1 : 2), "CPU applied the remaining field after the first failure");
                Check(ActionFailureStore.Records().Count == 1 && ActionFailureStore.Records()[0].Target == "system", "CPU failure did not isolate one system transaction");
                int before = applied.Count; Check(!PerformanceTuning.ApplyEnvironment(id, true) && applied.Count == before, "CPU group automatically retried");
                fail = false; Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(id, "system")), "CPU group explicit retry denied");
                Check(PerformanceTuning.ApplyEnvironment(id, true) && applied.Count == before + 2 && !ActionFailureStore.IsIsolated(id, "system"), "CPU group retry was not a single verified transaction");
                Check(PerformanceTuning.ApplyEnvironment(id, false) && values["override"] == "d:128" && values["mask"] == "d:8", "CPU successful retry overwrote original state");
            }
        }
        private static void TimerIsolation()
        {
            Fresh(); long now = DateTime.UtcNow.Ticks; int acquire = 0, release = 0, references = 0;
            bool honor = false, canRelease = true; uint actual = 10000;
            var lease = new TimerResolutionLease();
            lease.Set = delegate(uint desired, bool on, out uint value) {
                if (on) { acquire++; references++; if (honor) actual = 5000; }
                else { release++; if (!canRelease) { value = actual; return -1; } references--; }
                value = actual; return 0;
            };
            lease.Query = delegate(out uint coarse, out uint fine, out uint value) { coarse = 156250; fine = 5000; value = actual; return 0; };
            Check(!lease.Update(true, now) && acquire == 1 && release == 1 && references == 0, "accepted-but-unverified timer not isolated and released");
            lease.ResetAcquireAttempts(); ActionFailureStore.ReloadForTest(); honor = true;
            Check(!lease.Update(true, now + TimeSpan.FromHours(1).Ticks) && acquire == 1, "timer reset or restart silently retried");
            Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(TimerResolutionLease.FailureAction, "system")), "timer explicit retry denied");
            Check(lease.Update(true, ++now) && acquire == 2 && references == 1 && lease.TargetReached, "timer retry did not verify");
            actual = 10000; canRelease = false; now += TimeSpan.FromSeconds(6).Ticks;
            Check(!lease.Update(true, now) && acquire == 2 && references == 1, "failed paired maintenance acquired another reference");
            int before = release; Check(!lease.Update(true, ++now) && release == before, "timer failed restoration retried without backoff");
            canRelease = true; now += TimeSpan.FromSeconds(31).Ticks;
            Check(!lease.Update(true, now) && references == 0 && acquire == 2, "timer recovery re-applied without a new user permit");
            Fresh(); var limited = new TimerResolutionLease(); int calls = 0;
            limited.Set = delegate(uint desired, bool on, out uint value) { calls++; value = 10000; return 0; };
            limited.Query = delegate(out uint coarse, out uint fine, out uint value) { coarse = 156250; fine = value = 10000; return 0; };
            Check(limited.Update(true, now) && limited.State == "timer-limited" && ActionFailureStore.Records().Count == 0, "system timer limit classified as execution failure");
            Check(limited.Update(true, now + TimeSpan.FromHours(1).Ticks) && calls == 1, "system-limited request re-applied"); Check(limited.Update(false, ++now) && calls == 2, "limited timer reference not balanced");
            Fresh(); bool owned = false, configured = false; int writes = 0; string boot = BootSessionIdentity.Create(new Guid("12345678-1234-1234-1234-1234567890ab"), 1);
            Func<bool> enable = delegate { writes++; owned = true; return false; };
            Func<bool> restore = delegate { owned = configured = false; return true; };
            Check(TimerModeController.Step("AutoTimerTickBoot", true, boot, delegate { return configured; }, delegate { return owned; }, enable, restore) == "stopped" && !owned, "timer registry first failure not rolled back");
            Check(TimerModeController.Step("AutoTimerTickBoot", true, boot, delegate { return configured; }, delegate { return owned; }, enable, restore) == "stopped" && writes == 1, "timer registry automatically retried");
            Fresh(); owned = configured = false;
            ActionFailureStore.SaveForTest = delegate(string name, string value) { return !(value.Length == 0 && name.StartsWith(ActionFailureStore.AttemptPrefix, StringComparison.Ordinal)) && Settings.SaveStrDurable(name, value); };
            enable = delegate { owned = configured = true; return true; };
            Check(TimerModeController.Step("AutoTimerTickBoot", true, boot, delegate { return configured; }, delegate { return owned; }, enable, restore) == "stopped" && !owned && !configured,
                "timer setting completion failure did not immediately restore original ownership");
            Fresh(); owned = configured = false;
            enable = delegate { owned = configured = true; throw new OperationCanceledException(); };
            Check(TimerModeController.Step("AutoTimerTickBoot", true, boot, delegate { return configured; }, delegate { return owned; }, enable, restore) == "" && !owned && !configured && ActionFailureStore.Records().Count == 0,
                "timer normal cancellation after a write was not restored before clearing intent");
        }
        private sealed class IsolationMemory : IStandbyMemoryControl
        {
            internal int Queries, Purges; internal bool Fail = true;
            public bool TryQuery(out StandbyMemorySnapshot snapshot) { Queries++; snapshot = new StandbyMemorySnapshot { TotalBytes = 8UL << 30, AvailableBytes = 1UL << 30, FreeBytes = 512UL << 20, ListBytes = 2UL << 30, StandbyBytes = 2UL << 30 }; return true; }
            public bool TryPurge(Func<bool> admission, out int status) { Purges++; status = Fail ? -1 : 0; return !Fail; }
        }
        private static void StandbyIsolation()
        {
            Fresh(); int admits = 0;
            var noNeed = new IsolationMemory(); StandbyMemorySnapshot sample; int status;
            Check(new StandbyCleanerEngine(noNeed).Poll(new StandbyCleanerOptions(1024, 0, 250), delegate { return true; }, out sample, out status,
                delegate { admits++; return true; }) == StandbyCleanerResult.BelowThreshold && admits == 0 && noNeed.Purges == 0,
                "below-threshold observation created a write intent");
            Fresh(); var memory = new IsolationMemory();
            using (var failed = new ManualResetEvent(false))
            {
                var runner = new StandbyCleanerRunner(new StandbyCleanerEngine(memory), delegate { failed.Set(); });
                try
                {
                    var options = new StandbyCleanerOptions(1024, 1024, 250);
                    Check(runner.Update(1, options, delegate { return true; }) && failed.WaitOne(3000), "standby failure not reported");
                    Check(memory.Purges == 1 && ActionFailureStore.IsBlocked(PolicyCatalog.KeyStandbyCleaner, "system"), "standby first failure not isolated");
                    Check(!runner.Update(2, options, delegate { return true; }), "standby generation bypassed isolation");
                    memory.Fail = false; Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(PolicyCatalog.KeyStandbyCleaner, "system")), "standby explicit retry denied");
                    Check(runner.Update(2, options, delegate { return true; }), "standby authorized retry could not queue");
                    Check(SpinWait.SpinUntil(delegate { return !ActionFailureStore.IsIsolated(PolicyCatalog.KeyStandbyCleaner, "system"); }, 3000) && memory.Purges == 2, "standby verified retry did not clear isolation");
                }
                finally { runner.Pause(); Check(runner.Close(3000), "standby runner did not close"); }
            }
        }
    }
}
#endif
