using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

namespace PaviseApp
{
    internal static class SelfTests { internal static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Product startup forbidden in isolated recovery checks"); } }
    // Runs production methods against memory delegates and transient Settings only.
    internal static class ConsumerRecoveryChecks
    {
        private static int checks;
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        private static PerformanceActionStore.Receipt Receipt(string target, string expected)
        { return new PerformanceActionStore.Receipt { Id = "fixture", Target = target, Original = "0", Expected = expected, Committed = true }; }
        private static void QuantumCycle()
        {
            Settings.Save(PerformanceActionCatalog.Quantum, true);
            Settings.SaveStr(QuantumProfiles.SettingKey, "38");
            string a = PerformanceTuning.EnvironmentAutomaticCycle(PerformanceActionCatalog.Quantum);
            Settings.SaveStr(QuantumProfiles.SettingKey, "37");
            string b = PerformanceTuning.EnvironmentAutomaticCycle(PerformanceActionCatalog.Quantum);
            Settings.SaveStr(QuantumProfiles.SettingKey, "38");
            Check(a != b && a == PerformanceTuning.EnvironmentAutomaticCycle(PerformanceActionCatalog.Quantum), "Quantum phase did not follow the actual stable profile value");
            string reason;
            ActionFailureStore.BeginAutomaticCycle("fixture-quantum", "target", a);
            Check(ActionFailureStore.TryBeginApply("fixture-quantum", "target", out reason), "Quantum initial admission failed");
            ActionFailureStore.Trip("fixture-quantum", "target", "fixture");
            ActionFailureStore.CancelApply("fixture-quantum", "target");
            ActionFailureStore.BeginAutomaticCycle("fixture-quantum", "target", b);
            Check(ActionFailureStore.TryBeginApply("fixture-quantum", "target", out reason), "Material quantum profile change was blocked");
            ActionFailureStore.CancelApply("fixture-quantum", "target");
            ActionFailureStore.BeginAutomaticCycle("fixture-quantum", "target", a);
            Check(!ActionFailureStore.TryBeginApply("fixture-quantum", "target", out reason), "Quantum A-B-A replenished failed A");
        }
        private static void SharedBudget()
        {
            PerformanceTuning.BeginRecoveryCycle(100);
            var receipt = Receipt("target", "A"); int calls = 0;
            Func<bool> fail = () => { calls++; return false; };
            for (int i = 0; i < 1003; i++) Check(!PerformanceTuning.RestoreWithBudget(receipt, 3, fail), "Failure reported restored");
            Check(calls == 3, "Repeated recovery poll bypassed total budget");
            receipt.Committed = false;
            Check(!PerformanceTuning.RestoreWithBudget(receipt, 3, fail) && calls == 3, "Mutable receipt flag replenished budget");
            receipt.Expected = "B"; for (int i = 0; i < 3; i++) PerformanceTuning.RestoreWithBudget(receipt, 3, fail);
            receipt.Expected = "A"; Check(!PerformanceTuning.RestoreWithBudget(receipt, 3, fail) && calls == 6, "A B A refilled A");
            PerformanceTuning.BeginRecoveryCycle(101); PerformanceTuning.BeginRecoveryCycle(100);
            Check(PerformanceTuning.RecoveryCycleSerial == 101, "Old worker rolled back session identity");
            for (int i = 0; i < 4; i++) PerformanceTuning.RestoreWithBudget(receipt, 3, fail);
            Check(calls == 9, "New session did not select exactly one bounded budget");
            PerformanceTuning.BeginRecoveryCycle(102); calls = 0;
            PerformanceTuning.RestoreWithBudget(receipt, 3, fail); PerformanceTuning.RestoreWithBudget(receipt, 3, fail);
            Check(PerformanceTuning.RestoreWithBudget(receipt, 3, () => true), "Complete restoration failed");
            for (int i = 0; i < 4; i++) PerformanceTuning.RestoreWithBudget(receipt, 3, fail);
            Check(calls == 5, "Complete neutral settlement did not end its failure episode");
            PerformanceTuning.BeginRecoveryCycle(103); calls = 0;
            var workers = new Thread[12];
            for (int i = 0; i < workers.Length; i++) { workers[i] = new Thread(() => PerformanceTuning.RestoreWithBudget(receipt, 3, fail)); workers[i].Start(); }
            foreach (Thread worker in workers) Check(worker.Join(1000), "Bounded recovery worker did not drain");
            Check(calls == 3, "Concurrent callers exceeded shared receipt budget");
        }
        private sealed class PrivacyBackend : IPrivacyBackend
        {
            internal string Value = "wanted"; internal bool Fail = true; internal int Writes, Reads;
            public PrivacyPlan Inspect(string action, string selectedTarget) { throw new Exception("No forward inspect allowed"); }
            public string Read(string action, string target) { Reads++; return Value; }
            public bool Write(string action, string target, string value) { Writes++; if (Fail) return false; Value = value; return true; }
        }
        private static void PrivacyReceipt()
        {
            PerformanceTuning.BeginRecoveryCycle(104);
            var backend = new PrivacyBackend(); PrivacyControl.Bind(backend);
            var receipt = new PerformanceActionStore.Receipt { Id = PrivacyActionCatalog.BackgroundGlobal, Target = "fixture", Original = "original", Expected = "wanted", Committed = true };
            string slot = PerformanceActionStore.Slot(receipt.Id, receipt.Target);
            Check(Settings.SaveStrDurable(slot, PerformanceActionStore.Pack(receipt)), "Memory receipt seed failed");
            for (int i = 0; i < 1000; i++) Check(!PrivacyControl.RestoreReceipt(receipt), "Failed restore reported success");
            Check(backend.Writes == 3 && backend.Reads == 3, "Privacy duplicate entry points bypassed recovery cap");
            Check(Settings.LoadStr(slot, "").Length != 0, "Exhausted recovery deleted its receipt");
            backend.Fail = false; PerformanceTuning.BeginRecoveryCycle(105);
            Check(PrivacyControl.RestoreReceipt(receipt), "New session did not restore saved original");
            Check(backend.Value == "original" && Settings.LoadStr(slot, "").Length == 0, "Restore did not verify and durably clear receipt");
        }
        private static void GuardianPhase()
        {
            int calls = 0; var waits = new List<int>();
            Check(!CriticalThreadGuardian.RetainRecoveryUntilSettled(() => { calls++; if (calls == 2) throw new Exception("fixture"); return false; }, delay => waits.Add(delay)), "Exhausted guardian phase reported settled");
            Check(calls == 3 && waits.Count == 2 && waits[0] == 250 && waits[1] == 500, "Critical guardian phase budget/backoff incorrect");
            calls = 0; Check(CriticalThreadGuardian.RetainRecoveryUntilSettled(() => { calls++; return true; }, delay => { throw new Exception("Unexpected wait"); }) && calls == 1, "Healthy guardian restoration was capped or delayed");
            calls = 0; int exitWaits = 0;
            Check(!CriticalThreadGuardian.RetainRecoveryAcrossTargetExit(() => { calls++; return false; }, delay => { }, () => true,
                () => { exitWaits++; return true; }, 250) && calls == 3 && exitWaits == 0, "Critical initially gone target received two phases");
            bool gone = false; int live = 0, exited = 0;
            Check(!CriticalThreadGuardian.RetainRecoveryAcrossTargetExit(() => { if (gone) exited++; else live++; return false; }, delay => gone = true,
                () => gone, () => { exitWaits++; return true; }, 250) && live == 1 && exited == 3 && exitWaits == 0,
                "Critical exit during backoff refilled gone phase twice");
        }
        private static object Invoke(Type type, string name, params object[] args)
        { return type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args); }
        private static void SuppressionPhases()
        {
            Type type = typeof(SuppressionCore), entryType = type.GetNestedType("Entry", BindingFlags.NonPublic);
            object entry = Activator.CreateInstance(entryType, true);
            entryType.GetField("Name").SetValue(entry, "fixture"); entryType.GetField("Creation").SetValue(entry, 17L);
            for (int i = 0; i < 8; i++) Invoke(type, "RecordFailureBudget", entry, "restore", false, 42L);
            object phase = Invoke(type, "FailureBudgetFor", entry, "restore");
            FieldInfo failures = phase.GetType().GetField("Failures", BindingFlags.NonPublic | BindingFlags.Instance);
            Check((int)failures.GetValue(phase) == 8, "Suppression failure count missing");
            FieldInfo level = entryType.GetField("Level"); object first = level.GetValue(entry); level.SetValue(entry, (SuppressionLevel)1);
            object other = Invoke(type, "FailureBudgetFor", entry, "restore"); Check((int)failures.GetValue(other) == 0, "New policy failed to select its budget");
            level.SetValue(entry, first); Check(object.ReferenceEquals(phase, Invoke(type, "FailureBudgetFor", entry, "restore")), "Policy A B A replaced its exhausted budget");
            PerformanceTuning.BeginRecoveryCycle(106); Check((int)failures.GetValue(Invoke(type, "FailureBudgetFor", entry, "restore")) == 0, "Real session did not select suppression recovery phase");
        }
        private static void HistoricalBudgets()
        {
            Type type = typeof(CriticalThreadGuardian); const string key = "fixture-history-A";
            for (int i = 0; i < 3; i++) { Check((bool)Invoke(type, "HistoricalAttemptAllowed", key), "Historical budget stopped prematurely"); Invoke(type, "HistoricalAttemptEnded", key, false); }
            for (int i = 0; i < 1000; i++) Check(!(bool)Invoke(type, "HistoricalAttemptAllowed", key), "Cross-batch history budget refilled");
            Check((bool)Invoke(type, "HistoricalAttemptAllowed", "fixture-history-B"), "Exhausted journal blocked a different historical journal");
            Check((bool)Invoke(type, "BeginHistoricalAttempt", "fixture-owned-history"), "Historical operation did not acquire its own attempt");
            Check(!(bool)Invoke(type, "BeginHistoricalAttempt", "fixture-owned-history"), "Concurrent historical operation acquired the same attempt");
            Invoke(type, "HistoricalAttemptEnded", "fixture-owned-history", false);
            Check((bool)Invoke(type, "BeginHistoricalAttempt", "fixture-owned-history"), "Completed historical failure leaked in-flight ownership");
            Invoke(type, "HistoricalAttemptEnded", "fixture-owned-history", true);
        }
        private sealed class FailedLayoutPlatform : SuppressionCore.IAutomaticLayoutPlatform
        {
            internal int Opens;
            public IntPtr Open(int pid, long creation, string name) { Opens++; return IntPtr.Zero; }
            public uint[] ReadSets(IntPtr handle) { throw new Exception("Unexpected layout read"); }
            public bool ReadAffinity(IntPtr handle, out ulong mask) { throw new Exception("Unexpected layout affinity read"); }
            public bool WriteSets(IntPtr handle, uint[] sets) { throw new Exception("Unexpected layout write"); }
            public void Close(IntPtr handle) { throw new Exception("Unexpected layout close"); }
        }
        private static void SuppressionActualRecovery()
        {
            Type type = typeof(SuppressionCore), entryType = type.GetNestedType("Entry", BindingFlags.NonPublic);
            object core = FormatterServices.GetUninitializedObject(type), entry = Activator.CreateInstance(entryType, true);
            type.GetField("sync", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(core, new object());
            entryType.GetField("AutomaticLayoutSets").SetValue(entry, new uint[] { 1 });
            entryType.GetField("Name").SetValue(entry, "fixture"); entryType.GetField("Creation").SetValue(entry, 17L);
            FieldInfo platformField = type.GetField("automaticLayoutPlatform", BindingFlags.NonPublic | BindingFlags.Static);
            object original = platformField.GetValue(null); var platform = new FailedLayoutPlatform();
            platformField.SetValue(null, platform);
            try
            {
                MethodInfo restore = type.GetMethod("RestoreAutomaticLayoutLocked", BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < 1000; i++) Check(!(bool)restore.Invoke(core, new object[] { 101, entry }), "Failed layout reported restored");
                Check(platform.Opens == 8 && entryType.GetField("AutomaticLayoutSets").GetValue(entry) != null, "Actual retired layout path exceeded eight attempts or dropped debt");
                for (int i = 0; i < 8; i++) Invoke(type, "RecordFailureBudget", entry, "restore", false, 0L);
                MethodInfo force = null;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                    if (method.Name == "TryRestore" && method.GetParameters().Length == 4) force = method;
                Check(force != null && !(bool)force.Invoke(core, new object[] { 101, entry, false, null }), "Forced restoration bypassed its exhausted budget");
                PerformanceTuning.BeginRecoveryCycle(107);
                Check(!(bool)restore.Invoke(core, new object[] { 101, entry }) && platform.Opens == 9, "Real session did not resume bounded layout recovery");
            }
            finally { platformField.SetValue(null, original); }
        }
        private static void BackgroundJournalDebt()
        {
            Type type = typeof(SuppressionCore), entryType = type.GetNestedType("Entry", BindingFlags.NonPublic);
            const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (bool stop in new[] { false, true })
            {
                long serial = stop ? 112L : 110L;
                PerformanceTuning.BeginRecoveryCycle(serial);
                var core = (SuppressionCore)FormatterServices.GetUninitializedObject(type);
                var owner = new GameAffinityJobLease();
                type.GetField("sync", fields).SetValue(core, new object());
                type.GetField("backgroundAffinityJobs", fields).SetValue(core, owner);
                FieldInfo mapField = type.GetField("map", fields), budgetsField = type.GetField("backgroundJobJournalFailures", fields);
                var map = (IDictionary)Activator.CreateInstance(mapField.FieldType);
                var budgets = (IDictionary)Activator.CreateInstance(budgetsField.FieldType);
                mapField.SetValue(core, map); budgetsField.SetValue(core, budgets);
                object entry = Activator.CreateInstance(entryType, true);
                entryType.GetField("Name").SetValue(entry, "fixture");
                entryType.GetField("Creation").SetValue(entry, 19L);
                entryType.GetField("OrigPri").SetValue(entry, 32U);
                entryType.GetField("BackgroundJobPlacement").SetValue(entry, true);
                entryType.GetField("BackgroundJobActive").SetValue(entry, true);
                entryType.GetField("BackgroundJobs").SetValue(entry, owner);
                entryType.GetField("BackgroundPid").SetValue(entry, 701);
                entryType.GetField("SqueezeAff").SetValue(entry, 1UL);
                map.Add(701, entry);
                string directory = Path.Combine(Environment.CurrentDirectory, "journal-debt-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "state");
                type.GetField("journalPath", fields).SetValue(core, path);
                Func<bool> release = stop ? (Func<bool>)core.StopBackgroundAffinityJobs : core.ReleaseAllBackgroundAffinityJobs;
                try
                {
                    using (var blocker = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        Check(!release() && core.BackgroundAffinityPending && core.BackgroundReleasePending,
                            "Journal failure reported clean after native ownership was neutral");
                        Check(!(bool)entryType.GetField("BackgroundJobActive").GetValue(entry)
                            && !(bool)entryType.GetField("BackgroundRestorePending").GetValue(entry), "Journal-only debt fabricated native restore work");
                        object phase = budgets[serial + "/False"];
                        FieldInfo failures = phase.GetType().GetField("Failures", fields), next = phase.GetType().GetField("NotBefore", fields);
                        for (int i = 0; i < 1000; i++) Check(!release(), "Next poll forgot failed journal completion");
                        Check((int)failures.GetValue(phase) == 1, "Journal cooldown retried file writes");
                        for (int i = 1; i < 8; i++) { next.SetValue(phase, 0L); Check(!release(), "Blocked journal write reported success"); }
                        Check((int)failures.GetValue(phase) == 8, "Journal write budget was not finite");
                        core.EnforceBackgroundAffinity = true; Check(!release(), "Blocked policy B journal reported success");
                        core.EnforceBackgroundAffinity = false;
                        Check(!release() && (int)failures.GetValue(phase) == 8, "Journal A-B-A replenished exhausted A");
                    }
                    for (int i = 0; i < 1000; i++) Check(!release(), "Exhausted journal debt resumed on ordinary poll");
                    Check(!File.Exists(path) && core.BackgroundAffinityPending, "Exhausted journal debt was forgotten");
                    PerformanceTuning.BeginRecoveryCycle(serial + 1);
                    Check(release() && File.Exists(path) && !core.BackgroundAffinityPending && !core.BackgroundReleasePending,
                        "New genuine session did not durably settle journal-only debt");
                }
                finally { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); }
            }
        }
        private sealed class StandbyControl : IStandbyMemoryControl
        {
            internal bool Fail = true; internal int Queries;
            public bool TryQuery(out StandbyMemorySnapshot snapshot)
            {
                Interlocked.Increment(ref Queries);
                snapshot = new StandbyMemorySnapshot { TotalBytes = 16UL << 30, AvailableBytes = 8UL << 30, FreeBytes = 8UL << 30 };
                return !Fail;
            }
            public bool TryPurge(Func<bool> mayContinue, out int nativeStatus) { throw new Exception("No purge permitted in fixture"); }
        }
        private static void StandbyStableCycle()
        {
            PerformanceTuning.BeginRecoveryCycle(108);
            var control = new StandbyControl(); var faulted = new AutoResetEvent(false);
            var first = new StandbyCleanerOptions(100, 100, 250); var other = new StandbyCleanerOptions(200, 100, 250);
            var runner = new StandbyCleanerRunner(new StandbyCleanerEngine(control), (generation, result, status) => faulted.Set());
            try
            {
                Check(runner.Update(1, first, () => true) && faulted.WaitOne(2000), "Standby failure did not latch");
                for (int i = 2; i < 1002; i++) Check(!runner.Update(i, first, () => true), "Worker generation refilled standby budget");
                Check(control.Queries == 1, "Exhausted standby phase queried again");
                Check(runner.Update(1002, other, () => true) && faulted.WaitOne(2000), "Material standby options did not select another phase");
                Check(!runner.Update(1003, first, () => true) && control.Queries == 2, "Standby A B A refilled old phase");
            }
            finally { Check(runner.Close(1000), "Standby worker did not stop"); }
            runner = new StandbyCleanerRunner(new StandbyCleanerEngine(control), (generation, result, status) => faulted.Set());
            try
            {
                Check(!runner.Update(1004, first, () => true), "New standby worker forgot failed cycle");
                PerformanceTuning.BeginRecoveryCycle(109);
                Check(runner.Update(1004, first, () => true) && faulted.WaitOne(2000) && control.Queries == 3, "True new session failed to admit one standby attempt");
                control.Fail = false;
                Check(runner.Update(1005, new StandbyCleanerOptions(300, 0, 250), () => true), "Healthy standby observations did not start");
                int initial = control.Queries;
                for (int i = 0; i < 300 && control.Queries < initial + 4; i++) Thread.Sleep(10);
                Check(control.Queries >= initial + 4, "Successful below-threshold samples consumed a failure budget");
            }
            finally { Check(runner.Close(1000), "Second standby worker did not stop"); faulted.Dispose(); }
        }
        private sealed class FailedIdentityBackend : IConfigurationPresetBackend
        {
            internal int Loads;
            public string LoadIdentity() { Interlocked.Increment(ref Loads); throw new IOException("fixture identity failure"); }
            public string Read(ConfigurationPresetEntry entry) { throw new Exception("Identity failure must precede fingerprint reads"); }
            public ConfigurationPresetAvailability Inspect(ConfigurationPresetEntry entry) { throw new Exception("No native inspection permitted"); }
            public bool Write(ConfigurationPresetEntry entry, string value, Func<bool> cancelled, out string detail) { throw new Exception("No write permitted"); }
            public bool SaveIdentity(string identity) { throw new Exception("No identity write permitted"); }
        }
        private static void PollIdentity(ConfigurationPresetRuntime runtime)
        {
            var ignored = runtime.CurrentPreset;
            FieldInfo refreshing = typeof(ConfigurationPresetRuntime).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while ((int)refreshing.GetValue(runtime) != 0 && timer.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            Check((int)refreshing.GetValue(runtime) == 0, "Preset identity refresh did not complete");
        }
        private static void SavedPresetConfigurationPhase()
        {
            Settings.UseTransientStoreForCurrentProcess();
            var saved = new ConfigurationPresetBackend(null);
            string initial = ConfigurationPresetBackend.SavedIdentityPhase();
            foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries)
            {
                string value = saved.Read(entry);
                Check(value != null, "Default saved preset value was unreadable: " + entry.Key);
                string key = PrivacyControl.IsAction(entry.Key) ? PrivacyControl.PreferencePrefix + entry.Key : entry.Key;
                Settings.SaveStr(key, value);
                Check(ConfigurationPresetBackend.SavedIdentityPhase() == initial, "Explicit default invented a new configuration phase: " + entry.Key);
            }
            Settings.SaveStr("GmBoost", "true");
            Check(ConfigurationPresetBackend.SavedIdentityPhase() == initial, "Equivalent true/1 preference changed normalized phase");
            Settings.SaveStr(ConfigurationPresetRuntime.IdentityKey, "new-last-applied-metadata");
            Settings.SaveStr("fixture-report-timestamp", "later");
            Check(ConfigurationPresetBackend.SavedIdentityPhase() == initial, "Identity/report metadata changed configuration phase");
            PerformanceTuning.BeginRecoveryCycle(114);
            var backend = new FailedIdentityBackend(); int snapshots = 0;
            var runtime = new ConfigurationPresetRuntime(backend, () => Settings.MutationGeneration, null, null, null,
                () => { snapshots++; return ConfigurationPresetBackend.SavedIdentityPhase(); });
            for (int i = 0; i < 3; i++) PollIdentity(runtime);
            Check(backend.Loads == 3 && snapshots == 1, "Initial production configuration failure phase was not bounded/cached");
            for (int i = 0; i < 1000; i++) { Settings.SaveStr("fixture-unrelated", i.ToString()); PollIdentity(runtime); }
            Check(backend.Loads == 3, "Unrelated actual Settings mutations requeued failed identity work");
            Settings.Save("GmBoost", false);
            for (int i = 0; i < 3; i++) PollIdentity(runtime);
            Check(backend.Loads == 6, "Actual GmBoost change did not open a bounded configuration phase");
            Settings.Save("GmBoost", true); PollIdentity(runtime);
            Check(backend.Loads == 6 && ConfigurationPresetBackend.SavedIdentityPhase() == initial, "Actual GmBoost A-B-A refilled exhausted A");
            Settings.SaveStr("GmBoost", "True"); PollIdentity(runtime);
            Check(backend.Loads == 6, "Equivalent boolean representation replenished failed A");
            PerformanceTuning.BeginRecoveryCycle(115); PollIdentity(runtime);
            Check(backend.Loads == 7, "Real session did not reopen saved configuration identity phase");
            Settings.Save("EnvQuantumProfileV1", true); Settings.SaveStr("EnvQuantumSelectedProfileV1", "38");
            string quantum = ConfigurationPresetBackend.SavedIdentityPhase();
            Settings.SaveStr("EnvQuantumSelectedProfileV1", "038");
            Check(ConfigurationPresetBackend.SavedIdentityPhase() == quantum, "Equivalent quantum numeric representation changed phase");
            Settings.SaveStr("EnvQuantumSelectedProfileV1", "37");
            Check(ConfigurationPresetBackend.SavedIdentityPhase() != quantum, "Actual quantum sub-choice was absent from phase");
            Settings.Save("EnvStorageIdleV1", true); string storage = ConfigurationPresetBackend.SavedIdentityPhase();
            Settings.Save("EnvStorageRuntimeV1", true);
            Check(ConfigurationPresetBackend.SavedIdentityPhase() != storage, "Storage sub-member change was hidden by an already-enabled group");
            backend = new FailedIdentityBackend();
            runtime = new ConfigurationPresetRuntime(backend, () => Settings.MutationGeneration, null);
            for (int i = 0; i < 3; i++) PollIdentity(runtime);
            Settings.Save("GmBoost", false); PollIdentity(runtime);
            Check(backend.Loads == 4, "Default runtime constructor did not use actual saved configuration phases");
        }
        private sealed class SavedSameValueIdentityBackend : IConfigurationPresetBackend, IConfigurationPresetSnapshot
        {
            internal readonly ConfigurationPresetBackend Saved = new ConfigurationPresetBackend(null);
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
            internal int Loads;
            internal bool FailIdentity = true, BlockThird = true;
            public string LoadIdentity() { int call = Interlocked.Increment(ref Loads); if (call == 3 && BlockThird) { Entered.Set(); Release.WaitOne(); } if (FailIdentity) throw new IOException("old saved-identity observer"); return Saved.LoadIdentity(); }
            public string Read(ConfigurationPresetEntry entry) { return Saved.Read(entry); }
            public string ExtraSnapshot() { return Saved.ExtraSnapshot(); }
            public ConfigurationPresetAvailability Inspect(ConfigurationPresetEntry entry) { return ConfigurationPresetAvailability.Yes(); }
            public bool Write(ConfigurationPresetEntry entry, string value, Func<bool> cancelled, out string detail)
            { detail = "already configured; memory transaction boundary"; return Read(entry) == value; }
            public bool SaveIdentity(string identity) { return Saved.SaveIdentity(identity); }
        }
        private static void SeedSavedPreset(ConfigurationPresetKind preset)
        {
            foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries)
            {
                string key = PrivacyControl.IsAction(entry.Key) ? PrivacyControl.PreferencePrefix + entry.Key : entry.Key;
                if (entry.Key == PerformanceActionCatalog.Quantum)
                {
                    bool on = ConfigurationPresetEntry.IsEnabled(entry.Value(preset));
                    Settings.Save(entry.Key, on); Settings.SaveStr(QuantumProfiles.SettingKey, on ? entry.Value(preset) : "38");
                }
                else if (entry.Key == PerformanceActionCatalog.CpuMitigations) Settings.Save(key, true);
                else Settings.SaveStr(key, entry.Value(preset));
            }
            Settings.Save(CpuMitigationPolicy.ExpandedPreferenceKey, preset == ConfigurationPresetKind.Extreme);
            foreach (string member in PerformanceTuning.StorageMembers()) Settings.Save(member, preset == ConfigurationPresetKind.Extreme);
        }
        private static void SavedSameValueApplyRace()
        {
            Settings.UseTransientStoreForCurrentProcess(); SeedSavedPreset(ConfigurationPresetKind.Extreme);
            var backend = new SavedSameValueIdentityBackend(); var completed = new ManualResetEvent(false);
            var runtime = new ConfigurationPresetRuntime(backend, () => Settings.MutationGeneration, null);
            string phase = ConfigurationPresetBackend.SavedIdentityPhase();
            try
            {
                PollIdentity(runtime); PollIdentity(runtime); var ignored = runtime.CurrentPreset;
                Check(backend.Entered.WaitOne(3000), "Real saved-identity third failure did not block");
                int before = Settings.MutationGeneration; ConfigurationPresetResult result = null;
                Check(runtime.TryApply(ConfigurationPresetKind.Extreme, () => false, value => { result = value; completed.Set(); }), "Real saved same-value apply was not admitted");
                Check(completed.WaitOne(3000) && result != null && result.Failed == 0 && result.Identity != null, "Real saved settings did not verify Extreme");
                Check(Settings.MutationGeneration > before && ConfigurationPresetBackend.SavedIdentityPhase() == phase,
                    "Same-value apply must bump real Settings generation without changing its material phase");
                Check(runtime.CurrentPreset == ConfigurationPresetKind.Extreme, "Verified saved apply did not publish Extreme");
                backend.Release.Set(); PollIdentity(runtime);
                for (int i = 0; i < 1000; i++) Check(runtime.CurrentPreset == ConfigurationPresetKind.Extreme, "Old exception erased a newer verified saved identity");
                Check(backend.Loads == 3, "Same-value real apply reopened exhausted material phase A");
            }
            finally { backend.Release.Set(); PollIdentity(runtime); backend.Entered.Dispose(); backend.Release.Dispose(); completed.Dispose(); }
        }
        private static void SavedMaterialDisplay()
        {
            Settings.UseTransientStoreForCurrentProcess(); SeedSavedPreset(ConfigurationPresetKind.Default);
            var backend = new SavedSameValueIdentityBackend { BlockThird = false }; var model = new ConfigurationPresetCoordinator(backend);
            backend.SaveIdentity("4|default|" + model.Fingerprint());
            var runtime = new ConfigurationPresetRuntime(backend, () => Settings.MutationGeneration, null);
            var done = new ManualResetEvent(false);
            try
            {
                for (int i = 0; i < 3; i++) PollIdentity(runtime);
                SeedSavedPreset(ConfigurationPresetKind.Extreme); backend.SaveIdentity("2|extreme|" + model.Fingerprint()); backend.FailIdentity = false;
                PollIdentity(runtime); Check(runtime.CurrentPreset == ConfigurationPresetKind.Extreme, "Actual material B did not display verified Extreme");
                SeedSavedPreset(ConfigurationPresetKind.Default); backend.SaveIdentity("4|default|" + model.Fingerprint());
                for (int i = 0; i < 1000; i++) Check(!runtime.CurrentPreset.HasValue, "Actual exhausted material A retained B's preset label");
                Check(backend.Loads == 4, "Actual A-B-A invalidation reopened the exhausted identity reader");
                ConfigurationPresetResult result = null;
                Check(runtime.TryApply(ConfigurationPresetKind.Default, () => false, value => { result = value; done.Set(); }), "Explicit verified apply to material A was not admitted");
                Check(done.WaitOne(3000) && result != null && result.Failed == 0 && result.Identity != null, "Actual saved material A failed verification");
                for (int i = 0; i < 1000; i++) Check(runtime.CurrentPreset == ConfigurationPresetKind.Default, "Actual material A erased its own newer verified apply");
                Check(backend.Loads == 4, "Verified material A apply refilled its exhausted observer phase");
            }
            finally { backend.Release.Set(); PollIdentity(runtime); backend.Entered.Dispose(); backend.Release.Dispose(); done.Dispose(); }
        }
        private static void RecoveryOwnerReporters()
        {
            PerformanceTuning.BeginRecoveryCycle(116); Settings.UseTransientStoreForCurrentProcess();
            Type type = typeof(SuppressionCore), entryType = type.GetNestedType("Entry", BindingFlags.NonPublic);
            const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
            var core = (SuppressionCore)FormatterServices.GetUninitializedObject(type);
            type.GetField("sync", fields).SetValue(core, new object());
            FieldInfo mapField = type.GetField("map", fields);
            var map = (IDictionary)Activator.CreateInstance(mapField.FieldType); mapField.SetValue(core, map);
            object entry = Activator.CreateInstance(entryType, true);
            entryType.GetField("AutomaticLayoutSets").SetValue(entry, new uint[] { 1 });
            entryType.GetField("Name").SetValue(entry, "fixture"); entryType.GetField("Creation").SetValue(entry, 19L); map.Add(701, entry);
            FieldInfo platformField = type.GetField("automaticLayoutPlatform", BindingFlags.NonPublic | BindingFlags.Static);
            object original = platformField.GetValue(null); var platform = new FailedLayoutPlatform(); platformField.SetValue(null, platform);
            try
            {
                long now = DateTime.UtcNow.Ticks; var routes = new RecoveryRouteSet(116, () => now);
                Check(!routes.Capture("layout", core.RestoreAutomaticLayouts) && platform.Opens == 1, "Actual layout restore did not run once");
                for (int i = 0; i < 1000; i++) routes.RetryDue();
                Check(platform.Opens == 1, "Outer cooldown polling consumed native layout retries");
                for (int i = 1; i < 8; i++) { now = routes.Next; routes.RetryDue(); }
                Check(platform.Opens == 8, "Outer generic three-failure cap truncated the existing eight-attempt owner");
                for (int i = 0; i < 1000; i++) { now = routes.Next; routes.RetryDue(); }
                Check(platform.Opens == 8, "Exhausted owner reporter restarted native layout work");
                core.ObserveTargetExit(701, 19); now = routes.Next; routes.RetryDue();
                Check(platform.Opens == 9, "Read-only phase observer did not admit one verified exit phase");
                for (int i = 0; i < 7; i++) { now = routes.Next; routes.RetryDue(); }
                core.ObserveTargetExit(701, 19);
                for (int i = 0; i < 1000; i++) { now = routes.Next; routes.RetryDue(); }
                Check(platform.Opens == 16, "Repeated target exit replenished an already-exhausted exit phase");
                map.Clear(); int neutralCalls = 0; var neutral = new RecoveryRouteSet(116, () => now);
                Check(neutral.Capture("neutral-layout", () => { neutralCalls++; return core.RestoreAutomaticLayouts(); }), "Empty actual layout owner was not neutral");
                for (int i = 0; i < 1000; i++) neutral.RetryDue();
                Check(neutralCalls == 1 && platform.Opens == 16, "Neutral route repeated owner I/O");
            }
            finally { platformField.SetValue(null, original); }

            var receipt = new PerformanceActionStore.Receipt { Id = "MatchVisualEffectsV1", Target = "fixture", Original = "0", Expected = "1", Committed = true };
            string slot = PerformanceActionStore.Slot(receipt.Id, receipt.Target);
            Settings.SaveStrDurable(slot, PerformanceActionStore.Pack(receipt));
            var coordinator = new PerformanceActionCoordinator(); Type coordinatorType = typeof(PerformanceActionCoordinator);
            var attempts = (IDictionary)coordinatorType.GetField("restoreAttempts", fields).GetValue(coordinator);
            Type attemptType = coordinatorType.GetNestedType("Attempt", BindingFlags.NonPublic); object attempt = Activator.CreateInstance(attemptType, true);
            long due = DateTime.UtcNow.AddMinutes(2).Ticks;
            attemptType.GetField("Failures", fields).SetValue(attempt, 2); attemptType.GetField("Next", fields).SetValue(attempt, due);
            attempts[PerformanceTuning.RecoveryCycleSerial + "\n" + slot + "\n0\n1"] = attempt;
            try
            {
                lock (SystemConfigurationGate.Sync)
                {
                    coordinator.Queue(false, false, new string[0]);
                    using (var trace = new RecoveryAttemptTrace())
                    {
                        Check(!coordinator.WaitForSessionRestore(0) && trace.Reported && trace.Next > DateTime.UtcNow.Ticks
                            && trace.Next <= DateTime.UtcNow.AddSeconds(2).Ticks, "Async wait did not report its one-second deferred state");
                    }
                }
                Check(coordinator.DrainForNvHandoff(3000), "Coordinator cooldown worker did not drain");
                using (var trace = new RecoveryAttemptTrace())
                    Check(!coordinator.WaitForSessionRestore(0) && trace.Reported && trace.Next == due, "Worker's actual restore deadline was not published to the waiting route");
                int actual = 0;
                for (int i = 0; i < 5; i++) PerformanceTuning.RestoreWithBudget(receipt, 5, () => { actual++; return false; });
                attemptType.GetField("Next", fields).SetValue(attempt, 0L);
                coordinatorType.GetField("nextCheck", fields).SetValue(coordinator, 0L);
                coordinator.Queue(false, false, new string[0]); Check(coordinator.DrainForNvHandoff(3000), "Coordinator exhausted worker did not drain");
                using (var trace = new RecoveryAttemptTrace())
                {
                    Check(!coordinator.WaitForSessionRestore(0) && trace.Reported && trace.Next == long.MaxValue && trace.PhaseObservers.Count > 0,
                        "True shared exhaustion was reported as a new failure or endless wait");
                    Check(actual == 5 && Settings.LoadStr(slot, "").Length > 0, "Reporter changed actual attempt count or discarded ownership");
                    PerformanceTuning.BeginRecoveryCycle(117); bool reopened = false;
                    foreach (Func<bool> observe in trace.PhaseObservers) reopened |= observe();
                    Check(reopened, "Published exhausted state lost its real-session observer");
                }
            }
            finally { Settings.SaveStrDurable(slot, ""); Check(coordinator.Close(1000), "Memory coordinator did not close after fixture receipt cleanup"); }
        }
        private static int Main()
        {
            try { Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                SharedBudget(); QuantumCycle(); PrivacyReceipt(); GuardianPhase(); SuppressionPhases(); HistoricalBudgets(); SuppressionActualRecovery(); StandbyStableCycle(); BackgroundJournalDebt(); SavedPresetConfigurationPhase(); SavedSameValueApplyRace(); SavedMaterialDisplay(); RecoveryOwnerReporters();
                Console.WriteLine("PASS ConsumerRecoveryChecks checks=" + checks + " production_methods=true transient_settings=true native_writes=0 product_started=false"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
