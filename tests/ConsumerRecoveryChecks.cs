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
    // 生产方法仅使用内存委托及临时 Settings 执行
    internal static class ConsumerRecoveryChecks
    {
        private static int checks;
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        private static PerformanceActionStore.Receipt Receipt(string target, string expected)
        { return new PerformanceActionStore.Receipt { Id = "fixture", Target = target, Original = "0", Expected = expected, Committed = true }; }
        private static void QuantumCycle()
        {
            // 前台时间片已下线 不做前向应用 自动阶段不跟保存的档位走 和其他系统设置一样只按会话和动作区分
            Settings.Save(PerformanceActionCatalog.Quantum, true);
            Settings.SaveStr(QuantumProfiles.SettingKey, "38");
            string quantum = PerformanceTuning.EnvironmentAutomaticCycle(PerformanceActionCatalog.Quantum);
            Settings.SaveStr(QuantumProfiles.SettingKey, "37");
            Check(quantum == PerformanceTuning.EnvironmentAutomaticCycle(PerformanceActionCatalog.Quantum)
                && quantum == PerformanceTuning.RecoveryCycleSerial + "\n" + PerformanceActionCatalog.Quantum, "Retired quantum phase still follows the saved profile");
            Settings.SaveStr(QuantumProfiles.SettingKey, "38");
            // 阶段账本按 A B A 切回 A 时不补回 A 的失败次数
            string a = PerformanceTuning.EnvironmentAutomaticCycle(PerformanceActionCatalog.CpuMitigations), b = a + "\nfixture-material-change";
            Check(a != b, "Fixture phases are not distinct");
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
            public string Read(string action, string target) { Reads++; return Value; }
            public bool Write(string action, string target, string value) { Writes++; if (Fail) return false; Value = value; return true; }
        }
        private static void PrivacyReceipt()
        {
            PerformanceTuning.BeginRecoveryCycle(104);
            var backend = new PrivacyBackend(); PrivacyControl.Bind(backend);
            var receipt = new PerformanceActionStore.Receipt { Id = PrivacyActionCatalog.Diagnostics, Target = "fixture", Original = "original", Expected = "wanted", Committed = true };
            string slot = PerformanceActionStore.Slot(receipt.Id, receipt.Target);
            Check(Settings.SaveStrDurable(slot, PerformanceActionStore.Pack(receipt)), "Memory receipt seed failed");
            for (int i = 0; i < 1000; i++) Check(!PrivacyControl.RestoreReceipt(receipt), "Failed restore reported success");
            Check(backend.Writes == 3 && backend.Reads == 3, "Privacy duplicate entry points bypassed recovery cap");
            Check(Settings.LoadStr(slot, "").Length != 0, "Exhausted recovery deleted its receipt");
            backend.Fail = false; PerformanceTuning.BeginRecoveryCycle(105);
            Check(PrivacyControl.RestoreReceipt(receipt), "New session did not restore saved original");
            Check(backend.Value == "original" && Settings.LoadStr(slot, "").Length == 0, "Restore did not verify and durably clear receipt");
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
            // 历史回执每个阶段最多三次 跨批次不补次数 换阶段或换回执各有自己的三次
            Type type = typeof(HardThreadStorageDomain); const string key = "fixture-history-A/live";
            for (int i = 0; i < 3; i++) { Check((bool)Invoke(type, "AttemptAllowed", key), "Historical budget stopped prematurely"); Invoke(type, "AttemptEnded", key, false); }
            for (int i = 0; i < 1000; i++) Check(!(bool)Invoke(type, "AttemptAllowed", key), "Cross-batch history budget refilled");
            Check((bool)Invoke(type, "AttemptAllowed", "fixture-history-A/gone"), "Exhausted live phase blocked the exited phase of the same journal");
            Check((bool)Invoke(type, "AttemptAllowed", "fixture-history-B/live"), "Exhausted journal blocked a different historical journal");
            const string settled = "fixture-history-C/live";
            Invoke(type, "AttemptEnded", settled, false); Invoke(type, "AttemptEnded", settled, false); Invoke(type, "AttemptEnded", settled, true);
            for (int i = 0; i < 3; i++) { Check((bool)Invoke(type, "AttemptAllowed", settled), "A settled journal kept its old failures"); Invoke(type, "AttemptEnded", settled, false); }
            Check(!(bool)Invoke(type, "AttemptAllowed", settled), "A reused key was not bounded again after it settled");
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
                Check(runner.Update(1005, new StandbyCleanerOptions(300, 1, 250), () => true), "Healthy standby observations did not start");
                int initial = control.Queries;
                for (int i = 0; i < 300 && control.Queries < initial + 4; i++) Thread.Sleep(10);
                Check(control.Queries >= initial + 4, "Successful below-threshold samples consumed a failure budget");
            }
            finally { Check(runner.Close(1000), "Second standby worker did not stop"); faulted.Dispose(); }
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
                    coordinator.Queue(false, false);
                    using (var trace = new RecoveryAttemptTrace())
                    {
                        Check(!coordinator.WaitForSessionRestore(0) && trace.Reported && trace.Next > DateTime.UtcNow.Ticks
                            && trace.Next <= DateTime.UtcNow.AddSeconds(2).Ticks, "Async wait did not report its one-second deferred state");
                    }
                }
                Check(coordinator.DrainForTest(3000), "Coordinator cooldown worker did not drain");
                using (var trace = new RecoveryAttemptTrace())
                    Check(!coordinator.WaitForSessionRestore(0) && trace.Reported && trace.Next == due, "Worker's actual restore deadline was not published to the waiting route");
                int actual = 0;
                for (int i = 0; i < 5; i++) PerformanceTuning.RestoreWithBudget(receipt, 5, () => { actual++; return false; });
                attemptType.GetField("Next", fields).SetValue(attempt, 0L);
                coordinatorType.GetField("nextCheck", fields).SetValue(coordinator, 0L);
                coordinator.Queue(false, false); Check(coordinator.DrainForTest(3000), "Coordinator exhausted worker did not drain");
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
        // 写入途中进程退出留下的执行意图 没有恢复记录时按恢复完成结清 之后照常重新应用 有记录时不越过记录
        private static void DanglingIntentWithoutReceipt()
        {
            Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.BeginRecoveryCycle(118);
            const string action = PerformanceActionCatalog.CpuMitigations;
            string target = @"C:\SyntheticOnly\dangling.exe", other = @"C:\SyntheticOnly\owned.exe";
            var admissions = (IDictionary)typeof(ActionFailureStore).GetField("admissions", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            string reason;
            foreach (string item in new[] { target, other })
            {
                ActionFailureStore.BeginAutomaticCycle(action, item, "118\noff64");
                Check(ActionFailureStore.TryBeginApply(action, item, out reason), "Fixture intent was not admitted: " + reason);
                // 模拟重启 内存准入已不在 持久意图仍在 新进程开始新一轮自动阶段
                admissions.Remove(ActionFailureStore.RecordKey(action, item));
                ActionFailureStore.BeginAutomaticCycle(action, item, "119\noff64");
            }
            Check(ActionFailureStore.IsIsolated(action, target) && ActionFailureStore.IsBlocked(action, target), "Dangling intent did not isolate the target");
            var receipt = new PerformanceActionStore.Receipt { Id = action, Target = other, Original = "a", Expected = "b", Committed = true };
            string slot = PerformanceActionStore.Slot(receipt.Id, receipt.Target);
            Settings.SaveStrDurable(slot, PerformanceActionStore.Pack(receipt));
            Settings.SaveStrDurable(PerformanceActionStore.ListKey, slot);
            try
            {
                Check(!PerformanceTuning.SettleIntentWithoutReceipt(action, other) && ActionFailureStore.IsBlocked(action, other),
                    "A target with its own recovery record was settled without restoring it");
                Check(PerformanceTuning.SettleIntentWithoutReceipt(action, target) && !ActionFailureStore.IsBlocked(action, target),
                    "A dangling intent without any recovery record stayed blocked");
                Check(ActionFailureStore.IsIsolated(action, target), "Settling the intent erased the failure record before a verified apply");
                Check(ActionFailureStore.TryBeginApply(action, target, out reason) && ActionFailureStore.CompleteApply(action, target)
                    && !ActionFailureStore.IsIsolated(action, target), "Verified apply after settlement did not clear isolation: " + reason);
            }
            finally
            {
                Settings.SaveStrDurable(slot, ""); Settings.SaveStrDurable(PerformanceActionStore.ListKey, "");
                ActionFailureStore.CancelApply(action, other);
            }
        }

        private static int AutomaticFailures(string cyclePrefix)
        {
            var failures = (Dictionary<string, int>)typeof(PerformanceTuning).GetField("recoveryFailures", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            int total = 0;
            lock (SystemConfigurationGate.Sync)
                foreach (KeyValuePair<string, int> entry in failures) if (entry.Key.StartsWith(cyclePrefix, StringComparison.Ordinal)) total += entry.Value;
            return total;
        }

        // 用户亲手关开关算用户操作 每次操作每条回执只写一次 失败不占自动预算 成功时清零这条回执的自动失败计数
        private static void UserRestoreBudget()
        {
            PerformanceTuning.BeginRecoveryCycle(119);
            var receipt = Receipt("user", "A"); int calls = 0;
            Func<bool> fail = () => { calls++; return false; };
            using (PerformanceTuning.BeginUserRestore())
            {
                Check(!PerformanceTuning.RestoreWithBudget(receipt, 3, fail) && calls == 1, "User restore did not write once");
                // 嵌套进来的步骤沿用同一次操作 这次已经失败的回执不重复写
                using (PerformanceTuning.BeginUserRestore())
                    for (int i = 0; i < 5; i++) Check(!PerformanceTuning.RestoreWithBudget(receipt, 3, fail), "Failure reported restored");
                Check(calls == 1, "One user operation retried a failed receipt");
            }
            for (int i = 0; i < 5; i++) PerformanceTuning.RestoreWithBudget(receipt, 3, fail);
            Check(calls == 4 && AutomaticFailures("119|") == 3, "User operation consumed the automatic budget");
            using (PerformanceTuning.BeginUserRestore()) PerformanceTuning.RestoreWithBudget(receipt, 3, fail);
            Check(calls == 5, "A later user operation did not get its own write");
            for (int i = 0; i < 3; i++) PerformanceTuning.RestoreWithBudget(receipt, 3, fail);
            Check(calls == 5 && AutomaticFailures("119|") == 3, "Failed user restore cleared the exhausted automatic budget");
            // 用户恢复成功说明故障已经消失 自动计数清零 之后的自动失败从 1 开始算 照样封顶三次
            using (PerformanceTuning.BeginUserRestore())
                Check(PerformanceTuning.RestoreWithBudget(receipt, 3, () => { calls++; return true; }), "Exhausted automatic budget blocked the user restore");
            Check(calls == 6 && AutomaticFailures("119|") == 0, "Successful user restore kept the old automatic failures");
            Check(!PerformanceTuning.RestoreWithBudget(receipt, 3, fail) && calls == 7 && AutomaticFailures("119|") == 1,
                "Automatic failure after a successful user restore did not run or did not count from one");
            for (int i = 0; i < 5; i++) PerformanceTuning.RestoreWithBudget(receipt, 3, fail);
            Check(calls == 9 && AutomaticFailures("119|") == 3, "Automatic budget after a successful user restore was not bounded at three");
            PerformanceTuning.BeginRecoveryCycle(120);
            for (int i = 0; i < 5; i++) PerformanceTuning.RestoreWithBudget(receipt, 3, fail);
            Check(calls == 12, "New session did not keep the plain automatic budget");
            // 重置和卸载里的全部恢复也是用户自己点的 同样不被用完的自动预算挡住
            Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.BeginRecoveryCycle(121);
            var backend = new PrivacyBackend(); PrivacyControl.Bind(backend);
            var owned = new PerformanceActionStore.Receipt { Id = PrivacyActionCatalog.Diagnostics, Target = "reset", Original = "original", Expected = "wanted", Committed = true };
            string slot = PerformanceActionStore.Slot(owned.Id, owned.Target);
            Check(Settings.SaveStrDurable(slot, PerformanceActionStore.Pack(owned)), "Memory receipt seed failed");
            for (int i = 0; i < 5; i++) Check(!PrivacyControl.RestoreReceipt(owned), "Failed restore reported success");
            Check(backend.Writes == 3, "Automatic privacy restore ignored its budget");
            Check(PerformanceTuning.RestoreAllFailures().Count == 1 && backend.Writes == 4, "Exhausted automatic budget blocked the reset restore");
            Check(!PrivacyControl.RestoreReceipt(owned) && backend.Writes == 4, "Reset restore refilled the automatic budget");
            backend.Fail = false;
            Check(PerformanceTuning.RestoreAllFailures().Count == 0 && backend.Value == "original" && Settings.LoadStr(slot, "").Length == 0,
                "Reset restore did not finish after the fault cleared");
        }

        private static int Main()
        {
            try { Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                SharedBudget(); QuantumCycle(); PrivacyReceipt(); SuppressionPhases(); HistoricalBudgets(); SuppressionActualRecovery(); StandbyStableCycle(); BackgroundJournalDebt(); RecoveryOwnerReporters(); DanglingIntentWithoutReceipt();
                UserRestoreBudget();
                Console.WriteLine("PASS ConsumerRecoveryChecks checks=" + checks + " production_methods=true transient_settings=true native_writes=0 product_started=false"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
