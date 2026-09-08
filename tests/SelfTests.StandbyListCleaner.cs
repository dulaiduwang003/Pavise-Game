// 文件用途 待机列表回归 采样 清理和策略改动都是注入的
// 这套测试不启动应用 不碰真实内存 也不建窗口
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int standbyCleanerChecks;
        private const int StandbyCancelledStatus = unchecked((int)0xC0000120);
        private const ulong StandbyMiB = 1024UL * 1024UL;

        internal static int RunStandbyListCleanerRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseStandbyCleaner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string previousLog = Logger.LogPath;
            Func<List<string>> previousRestore = LegacyPurge.RestoreHook;
            Func<bool> previousRegistry = LegacyPurge.DeleteRegistryHook;
            bool previousSkip = LegacyPurge.SkipRegistryDelete;
            Action<string> previousStrictRead = Settings.BeforeStrictStringReadForTest;
            Action<string>[] tests =
            {
                StandbyOptionsAndUnits,
                StandbyThresholdsUseFreeAndBothConditions,
                StandbyThresholdsKeepBytePrecision,
                StandbyQueryFailureCannotPurge,
                StandbyInvalidSnapshotsFailClosed,
                StandbyCancellationAtEachBoundary,
                StandbyMutationBoundaryOnlyWrapsPurge,
                StandbyPurgeOutcomeIsNotRewritten,
                StandbyNextPollUsesFreshMemoryAndOptions,
                StandbyPurgeCooldownLimitsRate,
                StandbyWorkerDoesNotOverlapOrCatchUp,
                StandbyPauseRevokesSlowQuery,
                StandbyCloseTimeoutKeepsWorker,
                StandbyWorkerCannotJoinItself,
                StandbyFaultNeedsConsecutiveFailures,
                StandbyPurgeFaultRejectsSameGeneration,
                StandbyNativePageAccounting,
                StandbyNativeRejectsInvalidData,
                StandbyNativePrivilegeAndCommand,
                StandbyNativeCancellationAndCleanup,
                StandbyPolicyDefaultsAndCorruptOptions,
                StandbyParameterCommitAndGeneration,
                StandbySessionAdmissionAndOverrides,
                StandbyLiveFailureFuseAndStaleResults,
                StandbyShutdownBlocksResetUntilDrained,
                StandbyUiConfirmationAndInheritance,
                StandbyUiParametersAreNotConsent
            };
            standbyCleanerChecks = 0;
            try
            {
                foreach (Action<string> test in tests)
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest();
                    Logger.LogPath = Path.Combine(root, "decisions.log");
                    Lang.Cur = 0;
                    LegacyPurge.SkipRegistryDelete = false;
                    LegacyPurge.RestoreHook = delegate { throw new InvalidOperationException("Unmocked standby-test restoration"); };
                    LegacyPurge.DeleteRegistryHook = delegate { throw new InvalidOperationException("Unmocked standby-test registry deletion"); };
                    test(root);
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                Console.WriteLine("PASS standby-cleaner assertions=" + standbyCleanerChecks
                    + " native_memory=mocked settings=transient application_started=false windows_shown=false");
                return tests.Length;
            }
            finally
            {
                LegacyPurge.RestoreHook = previousRestore;
                LegacyPurge.DeleteRegistryHook = previousRegistry;
                LegacyPurge.SkipRegistryDelete = previousSkip;
                Settings.BeforeStrictStringReadForTest = previousStrictRead;
                Logger.ResetWriteBarrierForTest(); Logger.LogPath = previousLog;
                Settings.UseTransientStoreForCurrentProcess();
                string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (actual.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(actual).StartsWith("PaviseStandbyCleaner-", StringComparison.Ordinal)
                    && Directory.Exists(actual)) Directory.Delete(actual, true);
            }
        }

        private static void StandbyCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Standby cleaner regression: " + message);
            Interlocked.Increment(ref standbyCleanerChecks);
        }

        private static StandbyMemorySnapshot StandbySample(ulong list, ulong free)
        {
            return new StandbyMemorySnapshot
            {
                TotalBytes = 16UL * 1024UL * StandbyMiB,
                AvailableBytes = list + free,
                FreeBytes = free,
                StandbyBytes = list,
                ListBytes = list
            };
        }

        private sealed class StandbyMemoryFake : IStandbyMemoryControl
        {
            internal StandbyMemorySnapshot Sample = StandbySample(4UL * 1024UL * StandbyMiB, 512UL * StandbyMiB);
            internal int Queries, PurgeEntries, Purges, NativeStatus;
            internal bool DenyQuery, ThrowQuery, DenyPurge, ThrowPurge;
            internal Action BeforeQuery, AfterQuery, BeforePurge, AfterPurge;
            internal readonly List<string> Calls = new List<string>();

            public bool TryQuery(out StandbyMemorySnapshot snapshot)
            {
                Interlocked.Increment(ref Queries);
                Calls.Add("query");
                snapshot = null;
                if (BeforeQuery != null) BeforeQuery();
                if (ThrowQuery) throw new IOException("Simulated memory query failure");
                if (DenyQuery) return false;
                snapshot = Sample;
                if (AfterQuery != null) AfterQuery();
                return true;
            }

            public bool TryPurge(Func<bool> mayContinue, out int nativeStatus)
            {
                Interlocked.Increment(ref PurgeEntries);
                Calls.Add("purge-entry");
                if (BeforePurge != null) BeforePurge();
                nativeStatus = StandbyCancelledStatus;
                if (mayContinue != null && !mayContinue()) return false;
                Interlocked.Increment(ref Purges);
                Calls.Add("purge");
                if (ThrowPurge) throw new IOException("Simulated memory purge failure");
                nativeStatus = NativeStatus;
                if (AfterPurge != null) AfterPurge();
                return !DenyPurge;
            }
        }

        private static StandbyCleanerResult StandbyPoll(StandbyCleanerEngine engine, StandbyCleanerOptions options,
            Func<bool> admission)
        {
            StandbyMemorySnapshot snapshot; int status;
            return engine.Poll(options, admission, out snapshot, out status);
        }

        private static void StandbyOptionsAndUnits(string root)
        {
            StandbyCleanerOptions defaults = StandbyCleanerOptions.Default;
            StandbyCheck(defaults.IsValid && defaults.ListMegabytes == 1024 && defaults.FreeMegabytes == 1024
                && defaults.PollingMilliseconds == 4000, "default thresholds or polling interval changed");
            foreach (StandbyCleanerOptions options in new[]
            {
                defaults,
                new StandbyCleanerOptions(0, 0, 250),
                new StandbyCleanerOptions(1048576, 1048576, 300000),
                new StandbyCleanerOptions(2049, 4097, 1250)
            })
            {
                StandbyCleanerOptions parsed;
                StandbyCheck(options.IsValid && StandbyCleanerOptions.TryParse(options.Serialize(), out parsed)
                    && parsed.ListMegabytes == options.ListMegabytes && parsed.FreeMegabytes == options.FreeMegabytes
                    && parsed.PollingMilliseconds == options.PollingMilliseconds,
                    "valid options did not round-trip as a single snapshot");
            }
            foreach (StandbyCleanerOptions options in new[]
            {
                new StandbyCleanerOptions(-1, 1, 4000),
                new StandbyCleanerOptions(1, -1, 4000),
                new StandbyCleanerOptions(1048577, 1, 4000),
                new StandbyCleanerOptions(1, 1048577, 4000),
                new StandbyCleanerOptions(1, 1, 249),
                new StandbyCleanerOptions(1, 1, 300001),
                new StandbyCleanerOptions(int.MaxValue, int.MaxValue, int.MaxValue)
            })
            {
                var control = new StandbyMemoryFake();
                StandbyCheck(!options.IsValid && StandbyPoll(new StandbyCleanerEngine(control), options, null)
                    == StandbyCleanerResult.InvalidOptions && control.Queries == 0 && control.Purges == 0,
                    "invalid options reached sampling or purge");
            }
            foreach (string text in new[] { null, "", "garbage", "-1", "1|2", "1|2|3|4|5", new string('9', 1000) })
            {
                StandbyCleanerOptions parsed;
                StandbyCheck(!StandbyCleanerOptions.TryParse(text, out parsed), "malformed settings were accepted: " + text);
            }
            var none = new StandbyMemoryFake();
            StandbyCheck(StandbyPoll(new StandbyCleanerEngine(none), null, null) == StandbyCleanerResult.InvalidOptions
                && none.Queries == 0 && none.Purges == 0, "absent options reached memory operations");
        }

        private static void StandbyThresholdsUseFreeAndBothConditions(string root)
        {
            var control = new StandbyMemoryFake();
            var engine = new StandbyCleanerEngine(control);
            ulong limit = 1024UL * StandbyMiB;
            foreach (bool enoughList in new[] { false, true })
            foreach (bool lowFree in new[] { false, true })
            {
                control.Sample = StandbySample(enoughList ? limit : limit - 1, lowFree ? limit - 1 : limit);
                int before = control.Purges;
                StandbyCleanerResult result = StandbyPoll(engine, StandbyCleanerOptions.Default, null);
                bool expected = enoughList && lowFree;
                StandbyCheck(result == (expected ? StandbyCleanerResult.Purged : StandbyCleanerResult.BelowThreshold)
                    && control.Purges == before + (expected ? 1 : 0),
                    "thresholds used OR, reversed comparison, or rounded the byte sample");
            }
            control.Sample = StandbySample(6UL * 1024UL * StandbyMiB, 512UL * StandbyMiB);
            StandbyCheck(control.Sample.AvailableBytes > limit && control.Sample.FreeBytes < limit
                && StandbyPoll(engine, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.Purged,
                "Available memory replaced true free/zero memory in the low-free condition");
            control.Sample = StandbySample(6UL * 1024UL * StandbyMiB, 2UL * 1024UL * StandbyMiB);
            StandbyCheck(StandbyPoll(engine, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.BelowThreshold,
                "large standby alone caused an unnecessary purge");
            control.Sample = StandbySample(4UL * 1024UL * StandbyMiB, 512UL * StandbyMiB);
            control.Sample.StandbyBytes = 256UL * StandbyMiB;
            StandbyCheck(StandbyPoll(engine, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.Purged,
                "the configured list metric was replaced by standby-only bytes");
            control.Sample = StandbySample(512UL * StandbyMiB, 256UL * StandbyMiB);
            control.Sample.StandbyBytes = 4UL * 1024UL * StandbyMiB;
            StandbyCheck(StandbyPoll(engine, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.BelowThreshold,
                "standby-only bytes bypassed the configured list threshold");
        }

        private static void StandbyThresholdsKeepBytePrecision(string root)
        {
            var control = new StandbyMemoryFake();
            var engine = new StandbyCleanerEngine(control);
            var options = new StandbyCleanerOptions(2049, 4097, 250);
            ulong list = 2049UL * StandbyMiB, free = 4097UL * StandbyMiB;
            control.Sample = StandbySample(list, free - 1);
            StandbyCheck(StandbyPoll(engine, options, null) == StandbyCleanerResult.Purged,
                "MiB multiplication overflowed a signed 32-bit byte count");
            control.Sample = StandbySample(list - 1, free - 1);
            StandbyCheck(StandbyPoll(engine, options, null) == StandbyCleanerResult.BelowThreshold,
                "one byte below the list limit rounded up");
            control.Sample = StandbySample(list, free);
            StandbyCheck(StandbyPoll(engine, options, null) == StandbyCleanerResult.BelowThreshold,
                "free memory equal to the limit was treated as below it");
            control.Sample = StandbySample(list, free + 1);
            StandbyCheck(StandbyPoll(engine, options, null) == StandbyCleanerResult.BelowThreshold,
                "one byte above the free limit rounded down");
            control.Sample = StandbySample(0, 0);
            StandbyCheck(StandbyPoll(engine, new StandbyCleanerOptions(0, 1, 250), null) == StandbyCleanerResult.Purged,
                "zero list threshold did not retain its explicit meaning");
            StandbyCheck(StandbyPoll(engine, new StandbyCleanerOptions(0, 0, 250), null) == StandbyCleanerResult.BelowThreshold,
                "zero free threshold admitted an impossible negative-free condition");
        }

        private static void StandbyQueryFailureCannotPurge(string root)
        {
            foreach (string failure in new[] { "false", "exception", "null" })
            {
                var control = new StandbyMemoryFake();
                if (failure == "false") control.DenyQuery = true;
                else if (failure == "exception") control.ThrowQuery = true;
                else control.Sample = null;
                StandbyMemorySnapshot snapshot; int status;
                StandbyCheck(new StandbyCleanerEngine(control).Poll(StandbyCleanerOptions.Default, null, out snapshot, out status)
                    == StandbyCleanerResult.QueryFailed && control.Queries == 1 && control.PurgeEntries == 0,
                    "failed or absent memory sample reached the purge adapter: " + failure);
            }
        }

        private static void StandbyInvalidSnapshotsFailClosed(string root)
        {
            foreach (string field in new[] { "total-zero", "available", "free", "standby", "list", "free-plus-standby", "sum-overflow" })
            {
                var control = new StandbyMemoryFake();
                StandbyMemorySnapshot sample = control.Sample;
                if (field == "total-zero") sample.TotalBytes = 0;
                else if (field == "available") sample.AvailableBytes = sample.TotalBytes + 1;
                else if (field == "free") sample.FreeBytes = sample.TotalBytes + 1;
                else if (field == "standby") sample.StandbyBytes = sample.TotalBytes + 1;
                else if (field == "list") sample.ListBytes = sample.TotalBytes + 1;
                else if (field == "free-plus-standby") sample.StandbyBytes = sample.TotalBytes;
                else
                {
                    sample.TotalBytes = ulong.MaxValue;
                    sample.FreeBytes = ulong.MaxValue - 1;
                    sample.StandbyBytes = 2;
                }
                StandbyCheck(StandbyPoll(new StandbyCleanerEngine(control), StandbyCleanerOptions.Default, null)
                    == StandbyCleanerResult.QueryFailed && control.PurgeEntries == 0,
                    "invalid snapshot reached a memory purge: " + field);
            }
            var staggered = new StandbyMemoryFake();
            staggered.Sample.AvailableBytes = staggered.Sample.FreeBytes - 1;
            StandbyCheck(StandbyPoll(new StandbyCleanerEngine(staggered), StandbyCleanerOptions.Default, null)
                == StandbyCleanerResult.Purged, "slightly staggered Available/Free samples were treated as a corrupt native struct");
        }

        private static void StandbyCancellationAtEachBoundary(string root)
        {
            foreach (string stage in new[] { "initial", "after-query", "mutation-gate", "inside-purge" })
            {
                bool proceed = stage != "initial";
                var control = new StandbyMemoryFake();
                if (stage == "after-query") control.AfterQuery = delegate { proceed = false; };
                if (stage == "inside-purge") control.BeforePurge = delegate { proceed = false; };
                Func<Func<bool>, bool> boundary = delegate(Func<bool> action)
                {
                    if (stage == "mutation-gate") proceed = false;
                    return action();
                };
                StandbyCleanerResult result = StandbyPoll(new StandbyCleanerEngine(control, boundary),
                    StandbyCleanerOptions.Default, delegate { return proceed; });
                StandbyCheck(result == StandbyCleanerResult.Cancelled && control.Purges == 0,
                    "expired admission reached the native purge: " + stage);
                StandbyCheck(control.Queries == (stage == "initial" ? 0 : 1)
                    && control.PurgeEntries == (stage == "inside-purge" ? 1 : 0),
                    "admission was not rechecked at the intended slow boundary: " + stage);
            }
        }

        private static void StandbyMutationBoundaryOnlyWrapsPurge(string root)
        {
            var control = new StandbyMemoryFake();
            int boundaries = 0; bool deny = false;
            var engine = new StandbyCleanerEngine(control, delegate(Func<bool> action)
            {
                boundaries++;
                StandbyCheck(control.Queries > 0, "sampling was incorrectly held inside the mutation gate");
                return !deny && action();
            });
            control.Sample = StandbySample(1UL * StandbyMiB, 4UL * 1024UL * StandbyMiB);
            StandbyCheck(StandbyPoll(engine, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.BelowThreshold
                && boundaries == 0, "read-only below-threshold polling entered a mutation gate");
            control.Sample = StandbySample(4UL * 1024UL * StandbyMiB, 1UL * StandbyMiB);
            deny = true;
            StandbyCheck(StandbyPoll(engine, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.Cancelled
                && boundaries == 1 && control.Purges == 0, "rejected mutation gate issued a purge");
            deny = false;
            StandbyCheck(StandbyPoll(engine, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.Purged
                && boundaries == 2 && control.Purges == 1, "a permitted purge missed the mutation boundary");
            var reentrantControl = new StandbyMemoryFake();
            StandbyCleanerEngine reentrant = null;
            StandbyCleanerResult nested = StandbyCleanerResult.Purged;
            reentrantControl.AfterQuery = delegate
            { nested = StandbyPoll(reentrant, StandbyCleanerOptions.Default, null); };
            reentrant = new StandbyCleanerEngine(reentrantControl);
            StandbyCheck(StandbyPoll(reentrant, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.Purged
                && nested == StandbyCleanerResult.Cancelled && reentrantControl.Queries == 1 && reentrantControl.Purges == 1,
                "same-thread callback reentry started a nested query/purge");
        }

        private static void StandbyPurgeOutcomeIsNotRewritten(string root)
        {
            foreach (string after in new[] { "cancel", "wrapper-false" })
            {
                bool proceed = true;
                var control = new StandbyMemoryFake();
                control.AfterPurge = delegate { if (after == "cancel") proceed = false; };
                var engine = new StandbyCleanerEngine(control, delegate(Func<bool> action)
                { bool result = action(); return after == "wrapper-false" ? false : result; });
                StandbyCheck(StandbyPoll(engine, StandbyCleanerOptions.Default, delegate { return proceed; })
                    == StandbyCleanerResult.Purged && control.Purges == 1,
                    "an issued successful purge was hidden by a later state change: " + after);
            }
            foreach (string failure in new[] { "status", "exception", "cancelled" })
            {
                var control = new StandbyMemoryFake();
                control.DenyPurge = true;
                control.NativeStatus = failure == "cancelled" ? StandbyCancelledStatus : unchecked((int)0xC0000022);
                control.ThrowPurge = failure == "exception";
                StandbyMemorySnapshot snapshot; int status;
                StandbyCleanerResult result = new StandbyCleanerEngine(control).Poll(StandbyCleanerOptions.Default,
                    null, out snapshot, out status);
                StandbyCheck(result == (failure == "cancelled" ? StandbyCleanerResult.Cancelled : StandbyCleanerResult.PurgeFailed)
                    && control.Purges == 1, "purge failure was reported as success: " + failure);
                if (failure != "exception") StandbyCheck(status == control.NativeStatus,
                    "the native failure status was lost: " + failure);
            }
            var throwing = new StandbyMemoryFake(); throwing.ThrowPurge = true;
            var swallowingBoundary = new StandbyCleanerEngine(throwing, delegate(Func<bool> action)
            { try { return action(); } catch { return false; } });
            StandbyCheck(StandbyPoll(swallowingBoundary, StandbyCleanerOptions.Default, null)
                == StandbyCleanerResult.PurgeFailed && throwing.Purges == 1,
                "a coordinator swallowing an adapter exception relabelled it as harmless cancellation");
        }

        private static void StandbyNextPollUsesFreshMemoryAndOptions(string root)
        {
            var control = new StandbyMemoryFake();
            var engine = new StandbyCleanerEngine(control);
            StandbyCheck(StandbyPoll(engine, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.Purged
                && StandbyPoll(engine, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.Purged
                && control.Purges == 2 && control.Queries == 2,
                "engine introduced a hidden timer/cooldown or reused an old sample");
            control.Sample = StandbySample(8UL * 1024UL * StandbyMiB, 2UL * 1024UL * StandbyMiB);
            StandbyCheck(StandbyPoll(engine, StandbyCleanerOptions.Default, null) == StandbyCleanerResult.BelowThreshold
                && control.Purges == 2, "recovered free memory did not stop the next purge");
            StandbyCheck(StandbyPoll(engine, new StandbyCleanerOptions(8193, 4096, 4000), null)
                == StandbyCleanerResult.BelowThreshold && control.Purges == 2,
                "new list threshold did not affect the next sample");
            StandbyCheck(StandbyPoll(engine, new StandbyCleanerOptions(8192, 4096, 4000), null)
                == StandbyCleanerResult.Purged && control.Purges == 3,
                "new free/list thresholds were not applied together");
        }

        // 冷却是调度层的事 引擎保持纯策略 上面那些测试已经把这点钉死了
        //   Runner 在成功清理之后的 max 60s 和 8 倍间隔那段时间里整轮跳过 失败的清理不进冷却
        private static void StandbyPurgeCooldownLimitsRate(string root)
        {
            var control = new StandbyMemoryFake();
            long now = 0;
            using (var purged = new ManualResetEvent(false))
            {
                control.AfterPurge = delegate { purged.Set(); };
                var runner = new StandbyCleanerRunner(new StandbyCleanerEngine(control), null,
                    delegate { return Volatile.Read(ref now); });
                try
                {
                    var options = new StandbyCleanerOptions(1024, 1024, 250);
                    StandbyCheck(StandbyCleanerRunner.CooldownMilliseconds(options) == 60000
                        && StandbyCleanerRunner.CooldownMilliseconds(new StandbyCleanerOptions(1024, 1024, 10000)) == 80000,
                        "cooldown is not the larger of the floor and eight polling intervals");
                    StandbyCheck(runner.Update(41, options, delegate { return true; }) && purged.WaitOne(3000),
                        "cooldown fixture never reached its first purge");
                    purged.Reset();
                    StandbyCheck(!purged.WaitOne(1200) && control.Purges == 1,
                        "a second purge ran inside the cooldown window");
                    Volatile.Write(ref now, StandbyCleanerRunner.CooldownMilliseconds(options) + 1);
                    StandbyCheck(purged.WaitOne(3000) && control.Purges == 2,
                        "an elapsed cooldown did not allow the next purge");
                }
                finally { StandbyFinish(runner); }
            }
        }

        private static Thread StandbyOwnedWorker(StandbyCleanerRunner runner)
        {
            foreach (FieldInfo field in typeof(StandbyCleanerRunner).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.FieldType == typeof(Thread)) return (Thread)field.GetValue(runner);
            throw new InvalidOperationException("Missing isolated standby worker reference");
        }

        private static StandbyCleanerOptions StandbyFastOptions()
        { return new StandbyCleanerOptions(1024, 1024, 250); }

        private static void StandbyFinish(StandbyCleanerRunner runner)
        {
            StandbyCheck(runner.Close(3000) && !runner.HasWorkerForTest, "owned fake standby worker did not drain");
        }

        private static void StandbyWorkerDoesNotOverlapOrCatchUp(string root)
        {
            var control = new StandbyMemoryFake();
            control.Sample = StandbySample(1, 1);
            var runner = new StandbyCleanerRunner(new StandbyCleanerEngine(control), null);
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var second = new ManualResetEvent(false))
            {
                long completed = 0, next = 0;
                control.BeforeQuery = delegate
                {
                    if (control.Queries == 2) { next = Stopwatch.GetTimestamp(); second.Set(); }
                };
                control.AfterQuery = delegate
                {
                    if (control.Queries != 1) return;
                    entered.Set();
                    if (!release.WaitOne(3000)) throw new TimeoutException("Owned slow memory sample was not released");
                    completed = Stopwatch.GetTimestamp();
                };
                try
                {
                    StandbyCheck(!runner.HasWorkerForTest && control.Queries == 0,
                        "constructing a cleaner started background work");
                    StandbyCheck(runner.Update(1, new StandbyCleanerOptions(1024, 1024, 250), delegate { return true; })
                        && entered.WaitOne(3000), "mock standby worker did not enter the first query");
                    Thread first = StandbyOwnedWorker(runner);
                    for (int i = 0; i < 10; i++)
                        StandbyCheck(runner.Update(1, new StandbyCleanerOptions(1024, 1024, 250), delegate { return true; }),
                            "equivalent current options were rejected");
                    StandbyCheck(ReferenceEquals(first, StandbyOwnedWorker(runner)) && !second.WaitOne(300)
                        && control.Queries == 1, "an in-flight sample overlapped or a duplicate worker was created");
                    release.Set();
                    StandbyCheck(second.WaitOne(3000), "next sample did not resume after the slow query");
                    double delay = (next - completed) * 1000.0 / Stopwatch.Frequency;
                    StandbyCheck(completed > 0 && delay >= 250.0,
                        "worker caught up missed ticks or restarted its cycle on identical Update; delay=" + delay);
                    StandbyCheck(control.Purges == 0, "below-threshold worker test issued a purge");
                }
                finally { release.Set(); StandbyFinish(runner); }
            }
        }

        private static void StandbyPauseRevokesSlowQuery(string root)
        {
            var control = new StandbyMemoryFake();
            var runner = new StandbyCleanerRunner(new StandbyCleanerEngine(control), null);
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                control.AfterQuery = delegate
                {
                    entered.Set();
                    if (!release.WaitOne(3000)) throw new TimeoutException("Owned paused query was not released");
                };
                try
                {
                    StandbyCheck(runner.Update(1, StandbyFastOptions(), delegate { return true; })
                        && entered.WaitOne(3000), "pause fixture did not start its memory query");
                    runner.Pause();
                    release.Set();
                    StandbyCheck(runner.Drain(3000) && control.Queries == 1 && control.PurgeEntries == 0,
                        "pause left a previously admitted slow query able to purge");
                    StandbyFinish(runner);
                }
                finally { release.Set(); StandbyFinish(runner); }
            }
        }

        private static void StandbyCloseTimeoutKeepsWorker(string root)
        {
            foreach (bool holdPurge in new[] { false, true })
            {
                var control = new StandbyMemoryFake();
                var runner = new StandbyCleanerRunner(new StandbyCleanerEngine(control), null);
                using (var entered = new ManualResetEvent(false))
                using (var release = new ManualResetEvent(false))
                {
                    Action hold = delegate
                    {
                        entered.Set();
                        if (!release.WaitOne(3000)) throw new TimeoutException("Owned close-timeout operation was not released");
                    };
                    if (holdPurge) control.AfterPurge = hold;
                    else control.AfterQuery = hold;
                    try
                    {
                        StandbyCheck(runner.Update(7, StandbyFastOptions(), delegate { return true; })
                            && entered.WaitOne(3000), "close fixture did not enter the held operation");
                        Thread owned = StandbyOwnedWorker(runner);
                        StandbyCheck(!runner.Close(20) && runner.HasWorkerForTest && owned.IsAlive
                            && ReferenceEquals(owned, StandbyOwnedWorker(runner)),
                            "timed-out close discarded a live sample/purge worker");
                        StandbyCheck(runner.HasInFlight && !runner.Drain(20),
                            "in-flight sample/purge was incorrectly reported drained");
                        StandbyCheck(!runner.Update(8, StandbyFastOptions(), delegate { return true; }),
                            "terminal close admitted a replacement worker after timeout");
                        release.Set(); StandbyFinish(runner);
                        StandbyCheck(!owned.IsAlive && StandbyOwnedWorker(runner) == null
                            && control.Queries == 1 && control.Purges == (holdPurge ? 1 : 0),
                            "retry close lost an issued purge or permitted a late new purge");
                        StandbyCheck(runner.Close(0), "completed close was not idempotent");
                    }
                    finally { release.Set(); StandbyFinish(runner); }
                }
            }
        }

        private static void StandbyWorkerCannotJoinItself(string root)
        {
            var control = new StandbyMemoryFake();
            StandbyCleanerRunner runner = null;
            bool selfClosed = true, retained = false;
            using (var closed = new ManualResetEvent(false))
            {
                control.AfterQuery = delegate
                {
                    selfClosed = runner.Close(20);
                    retained = runner.HasWorkerForTest && ReferenceEquals(Thread.CurrentThread, StandbyOwnedWorker(runner));
                    closed.Set();
                };
                runner = new StandbyCleanerRunner(new StandbyCleanerEngine(control), null);
                try
                {
                    StandbyCheck(runner.Update(1, StandbyFastOptions(), delegate { return true; })
                        && closed.WaitOne(3000), "self-close fixture did not run");
                    StandbyCheck(!selfClosed && retained, "worker claimed its own join had completed");
                    StandbyFinish(runner);
                    StandbyCheck(control.PurgeEntries == 0, "self-close allowed a late purge after its query");
                }
                finally { StandbyFinish(runner); }
            }
        }

        private static void StandbyFaultNeedsConsecutiveFailures(string root)
        {
            var control = new StandbyMemoryFake();
            control.Sample = StandbySample(1, 1);
            control.BeforeQuery = delegate { control.DenyQuery = control.Queries == 1 || control.Queries >= 3; };
            int faults = 0, atQuery = 0, faultGeneration = 0;
            StandbyCleanerResult faultResult = StandbyCleanerResult.Cancelled;
            using (var failed = new ManualResetEvent(false))
            {
                var runner = new StandbyCleanerRunner(new StandbyCleanerEngine(control),
                    delegate(int generation, StandbyCleanerResult result, int status)
                    {
                        Interlocked.Increment(ref faults); atQuery = control.Queries;
                        faultGeneration = generation; faultResult = result; failed.Set();
                    });
                try
                {
                    StandbyCheck(runner.Update(13, new StandbyCleanerOptions(1024, 1024, 250), delegate { return true; })
                        && failed.WaitOne(3000), "consecutive query failures never reached the safety fuse");
                    StandbyCheck(faults == 1 && atQuery == 4 && faultGeneration == 13
                        && faultResult == StandbyCleanerResult.QueryFailed && control.Purges == 0,
                        "successful below-threshold sample did not reset the consecutive failure count");
                    StandbyCheck(!runner.Update(13, new StandbyCleanerOptions(1024, 1024, 250), delegate { return true; }),
                        "the same faulted generation restarted its failed request");
                    StandbyFinish(runner);
                    StandbyCheck(faults == 1 && control.Queries == 4, "faulted request continued polling or reported repeatedly");
                }
                finally { StandbyFinish(runner); }
            }
        }

        private static void StandbyPurgeFaultRejectsSameGeneration(string root)
        {
            var control = new StandbyMemoryFake();
            control.DenyPurge = true; control.NativeStatus = unchecked((int)0xC0000061);
            int faults = 0, reportedStatus = 0;
            StandbyCleanerResult reportedResult = StandbyCleanerResult.Cancelled;
            using (var failed = new ManualResetEvent(false))
            using (var retried = new ManualResetEvent(false))
            {
                var runner = new StandbyCleanerRunner(new StandbyCleanerEngine(control),
                    delegate(int generation, StandbyCleanerResult result, int status)
                    { Interlocked.Increment(ref faults); reportedStatus = status; reportedResult = result; failed.Set(); });
                try
                {
                    var options = new StandbyCleanerOptions(1024, 1024, 250);
                    StandbyCheck(runner.Update(21, options, delegate { return true; }) && failed.WaitOne(3000),
                        "native purge denial never tripped the failure fuse");
                    StandbyCheck(control.Purges == 2 && faults == 1 && reportedStatus == control.NativeStatus
                        && reportedResult == StandbyCleanerResult.PurgeFailed,
                        "native purge failure lost its status or tripped at the wrong count");
                    StandbyCheck(!runner.Update(21, options, delegate { return true; }),
                        "faulted generation was silently re-enabled");
                    control.DenyPurge = false; control.NativeStatus = 0;
                    control.AfterPurge = delegate { retried.Set(); };
                    StandbyCheck(runner.Update(22, options, delegate { return true; }) && retried.WaitOne(3000),
                        "a new explicit policy generation could not retry after failure");
                    StandbyFinish(runner);
                    StandbyCheck(faults == 1 && control.Purges == 3, "fresh generation inherited or repeated the old fault");
                }
                finally { StandbyFinish(runner); }
            }
            // 清理是做了 但权限回滚失败了 再往下做就不安全
            var unsafeControl = new StandbyMemoryFake(); unsafeControl.NativeStatus = unchecked((int)0xC0000001);
            using (var unsafeFault = new ManualResetEvent(false))
            {
                StandbyCleanerResult result = StandbyCleanerResult.Cancelled; int status = 0;
                var unsafeRunner = new StandbyCleanerRunner(new StandbyCleanerEngine(unsafeControl),
                    delegate(int generation, StandbyCleanerResult observed, int nativeStatus)
                    { result = observed; status = nativeStatus; unsafeFault.Set(); });
                try
                {
                    StandbyCheck(unsafeRunner.Update(31, StandbyFastOptions(), delegate { return true; })
                        && unsafeFault.WaitOne(3000), "failed privilege rollback did not immediately stop the worker");
                    StandbyCheck(unsafeControl.Purges == 1 && result == StandbyCleanerResult.Purged && status != 0
                        && !unsafeRunner.Update(31, StandbyFastOptions(), delegate { return true; }),
                        "completed purge was hidden or unsafe privilege state was retried");
                }
                finally { StandbyFinish(unsafeRunner); }
            }
        }

        private sealed class StandbyNativeFake : IStandbyMemoryNative, IDisposable
        {
            internal StandbyPerformanceInformation Performance;
            internal StandbyMemoryListInformation Memory;
            internal int ReturnedBytes = Marshal.SizeOf(typeof(StandbyMemoryListInformation));
            internal int QueryStatus, PurgeStatus, PerformanceCalls, QueryCalls, AcquireCalls, SetCalls, Releases;
            internal int InformationClass, Command, Length;
            internal bool DenyPerformance, ThrowPerformance, ThrowQuery, DenyPrivilege, MissingLease, ThrowPrivilege, ThrowSet, ThrowRelease;
            internal Action AfterAcquire, AfterSet;

            internal StandbyNativeFake()
            {
                Performance = new StandbyPerformanceInformation
                {
                    Size = (uint)Marshal.SizeOf(typeof(StandbyPerformanceInformation)),
                    PhysicalTotal = new UIntPtr(1048576U), PhysicalAvailable = new UIntPtr(262144U),
                    SystemCache = new UIntPtr(131072U), PageSize = new UIntPtr(4096U)
                };
                Memory = new StandbyMemoryListInformation
                {
                    ZeroPageCount = new UIntPtr(128U), FreePageCount = new UIntPtr(256U),
                    PageCountByPriority = new UIntPtr[8], RepurposedPagesByPriority = new UIntPtr[8]
                };
                for (int i = 0; i < 8; i++) Memory.PageCountByPriority[i] = new UIntPtr((uint)((i + 1) * 1000));
            }

            public bool TryGetPerformanceInfo(out StandbyPerformanceInformation information)
            {
                PerformanceCalls++; information = Performance;
                if (ThrowPerformance) throw new IOException("Fake performance query failed");
                return !DenyPerformance;
            }
            public int QueryMemoryList(out StandbyMemoryListInformation information, out int returnedBytes)
            {
                QueryCalls++; information = Memory; returnedBytes = ReturnedBytes;
                if (ThrowQuery) throw new IOException("Fake memory-list query failed");
                return QueryStatus;
            }
            public bool TryAcquirePurgePrivilege(out IDisposable lease)
            {
                AcquireCalls++; lease = MissingLease ? null : this;
                if (ThrowPrivilege) throw new IOException("Fake privilege acquisition failed");
                if (AfterAcquire != null) AfterAcquire();
                return !DenyPrivilege;
            }
            public int SetSystemInformation(int informationClass, ref int command, int length)
            {
                SetCalls++; InformationClass = informationClass; Command = command; Length = length;
                if (ThrowSet) throw new IOException("Fake native purge failed");
                if (AfterSet != null) AfterSet();
                return PurgeStatus;
            }
            public void Dispose()
            {
                Releases++;
                if (ThrowRelease) throw new IOException("Fake privilege release failed");
            }
        }

        private static void StandbyNativePageAccounting(string root)
        {
            var native = new StandbyNativeFake();
            UIntPtr huge = IntPtr.Size == 8 ? new UIntPtr(ulong.MaxValue) : new UIntPtr(uint.MaxValue);
            native.Memory.ModifiedPageCount = native.Memory.ModifiedNoWritePageCount = native.Memory.BadPageCount = huge;
            native.Memory.ModifiedPageCountPageFile = huge;
            for (int i = 0; i < 8; i++) native.Memory.RepurposedPagesByPriority[i] = huge;
            StandbyMemorySnapshot sample;
            StandbyCheck(new StandbyMemoryControl(native).TryQuery(out sample) && sample != null,
                "valid native page sample was rejected");
            StandbyCheck(sample.TotalBytes == 1048576UL * 4096 && sample.AvailableBytes == 262144UL * 4096
                && sample.FreeBytes == (128UL + 256UL) * 4096 && sample.StandbyBytes == 36000UL * 4096
                && sample.ListBytes == 131072UL * 4096,
                "native accounting substituted Available/cache for true Free or counted repurposed/modified pages");
            StandbyCheck(native.PerformanceCalls == 1 && native.QueryCalls == 1 && native.SetCalls == 0
                && native.AcquireCalls == 0, "read-only sampling enabled a privilege or issued a mutation");
            native.ReturnedBytes = 13 * IntPtr.Size;
            StandbyCheck(new StandbyMemoryControl(native).TryQuery(out sample),
                "valid short native struct through all eight priority counts was rejected");
        }

        private static void StandbyNativeRejectsInvalidData(string root)
        {
            foreach (string failure in new[]
            {
                "performance-false", "performance-exception", "size", "page-zero", "page-not-power-two",
                "query-status", "query-exception", "short", "long", "priorities-null", "priorities-short",
                "total-zero", "available-over-total", "cache-over-total", "free-over-total", "standby-over-total",
                "sum-overflow", "multiply-overflow"
            })
            {
                var native = new StandbyNativeFake();
                if (failure == "performance-false") native.DenyPerformance = true;
                else if (failure == "performance-exception") native.ThrowPerformance = true;
                else if (failure == "size") native.Performance.Size = 0;
                else if (failure == "page-zero") native.Performance.PageSize = UIntPtr.Zero;
                else if (failure == "page-not-power-two") native.Performance.PageSize = new UIntPtr(3000U);
                else if (failure == "query-status") native.QueryStatus = unchecked((int)0xC0000003);
                else if (failure == "query-exception") native.ThrowQuery = true;
                else if (failure == "short") native.ReturnedBytes = 13 * IntPtr.Size - 1;
                else if (failure == "long") native.ReturnedBytes++;
                else if (failure == "priorities-null") native.Memory.PageCountByPriority = null;
                else if (failure == "priorities-short") native.Memory.PageCountByPriority = new UIntPtr[7];
                else if (failure == "total-zero") native.Performance.PhysicalTotal = UIntPtr.Zero;
                else if (failure == "available-over-total") native.Performance.PhysicalAvailable = new UIntPtr(1048577U);
                else if (failure == "cache-over-total") native.Performance.SystemCache = new UIntPtr(1048577U);
                else if (failure == "free-over-total") native.Memory.FreePageCount = new UIntPtr(1048577U);
                else if (failure == "standby-over-total") native.Memory.PageCountByPriority[0] = new UIntPtr(1048577U);
                else if (failure == "sum-overflow")
                {
                    native.Memory.ZeroPageCount = IntPtr.Size == 8 ? new UIntPtr(ulong.MaxValue) : new UIntPtr(uint.MaxValue);
                    native.Memory.FreePageCount = new UIntPtr(1U);
                }
                else
                {
                    native.Performance.PhysicalTotal = IntPtr.Size == 8 ? new UIntPtr(ulong.MaxValue) : new UIntPtr(uint.MaxValue);
                    native.Performance.PageSize = IntPtr.Size == 8 ? new UIntPtr(1UL << 63) : new UIntPtr(1U << 31);
                    for (int i = 0; i < 8; i++) native.Memory.PageCountByPriority[i] =
                        IntPtr.Size == 8 ? new UIntPtr(ulong.MaxValue) : new UIntPtr(uint.MaxValue);
                }
                StandbyMemorySnapshot sample;
                StandbyCheck(!new StandbyMemoryControl(native).TryQuery(out sample) && sample == null
                    && native.SetCalls == 0 && native.AcquireCalls == 0,
                    "invalid native struct did not fail closed: " + failure);
            }
        }

        private static void StandbyNativePrivilegeAndCommand(string root)
        {
            foreach (string outcome in new[] { "success", "denied", "lease-null", "privilege-exception", "set-failure", "positive-status", "set-exception" })
            {
                var native = new StandbyNativeFake();
                native.DenyPrivilege = outcome == "denied";
                native.MissingLease = outcome == "lease-null";
                native.ThrowPrivilege = outcome == "privilege-exception";
                native.ThrowSet = outcome == "set-exception";
                native.PurgeStatus = outcome == "set-failure" ? unchecked((int)0xC0000022) : outcome == "positive-status" ? 1 : 0;
                int status;
                bool purged = new StandbyMemoryControl(native).TryPurge(null, out status);
                bool reachedSet = outcome == "success" || outcome == "set-failure" || outcome == "positive-status" || outcome == "set-exception";
                StandbyCheck(purged == (outcome == "success") && native.SetCalls == (reachedSet ? 1 : 0)
                    && native.Releases == (native.MissingLease ? 0 : 1),
                    "native command/privilege outcome was incorrect or leaked its lease: " + outcome);
                if (reachedSet) StandbyCheck(native.InformationClass == 80 && native.Command == 4 && native.Length == 4,
                    "cleaner issued a working-set/modified-page command or used the wrong ABI length");
                StandbyCheck(native.PerformanceCalls == 0 && native.QueryCalls == 0,
                    "purge adapter performed unrelated sampling");
            }
        }

        private static void StandbyNativeCancellationAndCleanup(string root)
        {
            foreach (string stage in new[] { "initial", "admission-exception", "acquire", "after-set", "release-exception" })
            {
                bool admitted = stage != "initial";
                var native = new StandbyNativeFake();
                native.AfterAcquire = delegate { if (stage == "acquire") admitted = false; };
                native.AfterSet = delegate { if (stage == "after-set") admitted = false; };
                native.ThrowRelease = stage == "release-exception";
                int status;
                var adapter = new StandbyMemoryControl(native);
                bool purged = adapter.TryPurge(delegate
                {
                    if (stage == "admission-exception") throw new InvalidOperationException("Fake admission failed");
                    return admitted;
                }, out status);
                bool issued = stage == "after-set" || stage == "release-exception";
                bool acquired = stage != "initial" && stage != "admission-exception";
                StandbyCheck(purged == issued && native.SetCalls == (issued ? 1 : 0)
                    && native.AcquireCalls == (acquired ? 1 : 0) && native.Releases == (acquired ? 1 : 0),
                    "native cancellation boundary issued a late command or lost a lease: " + stage);
                StandbyCheck(status == (stage == "release-exception" ? unchecked((int)0xC0000001)
                    : issued ? 0 : StandbyCancelledStatus),
                    "completed purge was relabelled after cancellation/cleanup: " + stage);
                if (stage == "release-exception")
                {
                    StandbyMemorySnapshot sample;
                    StandbyCheck(!adapter.TryQuery(out sample) && sample == null && !adapter.TryPurge(null, out status)
                        && native.SetCalls == 1 && native.AcquireCalls == 1 && native.PerformanceCalls == 0 && native.QueryCalls == 0,
                        "unhealthy privilege state admitted further native work");
                }
            }
        }

        private sealed class StandbyPolicyFixture : IDisposable
        {
            internal readonly FamilyPolicyFixture Family;
            internal readonly StandbyMemoryFake Control = new StandbyMemoryFake();
            internal readonly StandbyCleanerRunner Runner;
            internal Action FaultObserved;
            internal Exception FaultError;
            internal GameMode Mode { get { return Family.Mode; } }

            internal StandbyPolicyFixture(string root, string name)
            {
                Family = new FamilyPolicyFixture(root, name);
                var original = (StandbyCleanerRunner)FamilyPolicyGetField(Mode, "standbyCleaner");
                StandbyCheck(original != null && !original.HasWorkerForTest && original.Close(0),
                    "GameMode constructor started a native cleaner worker");
                Runner = new StandbyCleanerRunner(new StandbyCleanerEngine(Control),
                    delegate(int generation, StandbyCleanerResult result, int status)
                    {
                        try { FamilyPolicyInvoke(Mode, "OnStandbyCleanerFault", generation, result, status); }
                        catch (Exception error) { FaultError = error; }
                        finally { if (FaultObserved != null) FaultObserved(); }
                    });
                FamilyPolicySetField(Mode, "standbyCleaner", Runner);
            }

            internal void Ready()
            {
                Mode.ClearProfileOverrides("first");
                Mode.ProbeSessionPolicyApply(Family.Current("first"));
                FamilyPolicySetField(Mode, "activeDetection", FamilyObservationTarget(Family.Current("first")));
                FamilyPolicySetField(Mode, "enabled", true);
                FamilyPolicySetField(Mode, "active", true);
            }

            internal Func<bool> Capture()
            {
                StandbyCleanerOptions options; int generation;
                return Mode.CaptureStandbyCleanerAdmissionForTest(out options, out generation);
            }

            public void Dispose()
            {
                StandbyFinish(Runner);
                StandbyCheck(FamilyPolicyGetField(Mode, "worker") == null,
                    "isolated standby policy test started the application runtime worker");
                Family.Dispose();
            }
        }

        private static void StandbyPolicyDefaultsAndCorruptOptions(string root)
        {
            using (var f = new StandbyPolicyFixture(root, "standby-defaults"))
            {
                StandbyCheck(!f.Mode.StandbyCleanerEnabled && f.Mode.StandbyCleaningOptionsValid
                    && !PolicyResolver.Global().StandbyCleaner && !f.Capture()(),
                    "new installation opted into standby cleaning");
                Settings.Save("GmStandbyGuard", true); Settings.Save("GmStandbySweep", true);
                GameProfile retired = f.Family.Current("first");
                retired.Overrides["GmStandbyGuard"] = "1"; retired.Overrides["GmStandbySweep"] = "1";
                StandbyCheck(!PolicyResolver.For(retired).StandbyCleaner,
                    "retired per-game switches silently granted new memory-cleaner consent");
                var legacyReload = new GameMode(f.Family.DirectoryPath, new SuppressionCore());
                try { StandbyCheck(!legacyReload.StandbyCleanerEnabled, "retired global switches enabled the new cleaner"); }
                finally { StandbyFinish((StandbyCleanerRunner)FamilyPolicyGetField(legacyReload, "standbyCleaner")); }
                StandbyCheck(f.Mode.SetProfileOverride("first", PolicyCatalog.KeyStandbyCleaner, "1"),
                    "could not prepare an old per-game opt-in before corrupt-parameter reload");

                foreach (string problem in new[] { "malformed", "wrong-type", "unreadable" })
                {
                    Settings.Save(PolicyCatalog.KeyStandbyCleaner, true);
                    Settings.SaveStr(GameMode.StandbyCleanerOptionsKey, "broken|options");
                    if (problem == "wrong-type") Settings.Save(GameMode.StandbyCleanerOptionsKey, true);
                    Action<string> oldRead = Settings.BeforeStrictStringReadForTest;
                    if (problem == "unreadable") Settings.BeforeStrictStringReadForTest = delegate(string name)
                    { if (name == GameMode.StandbyCleanerOptionsKey) throw new IOException("Fake settings access failure"); };
                    GameMode loaded = null;
                    try
                    {
                        loaded = new GameMode(f.Family.DirectoryPath, new SuppressionCore());
                        StandbyCheck(!loaded.StandbyCleanerEnabled && !loaded.StandbyCleaningOptionsValid,
                            "bad saved options silently substituted defaults and ran: " + problem);
                        loaded.StandbyCleanerEnabled = true;
                        StandbyCheck(!loaded.StandbyCleanerEnabled
                            && !loaded.SetProfileOverride("first", PolicyCatalog.KeyStandbyCleaner, "1"),
                            "invalid options admitted a direct/global per-game opt-in: " + problem);
                        StandbyCheck(loaded.TrySetStandbyCleanerOptions(StandbyFastOptions())
                            && loaded.StandbyCleaningOptionsValid && !loaded.StandbyCleanerEnabled,
                            "repairing parameters failed or implicitly granted consent: " + problem);
                        loaded.ProbeSessionPolicyApply(f.Family.Current("first"));
                        FamilyPolicySetField(loaded, "activeDetection", FamilyObservationTarget(f.Family.Current("first")));
                        FamilyPolicySetField(loaded, "enabled", true); FamilyPolicySetField(loaded, "active", true);
                        StandbyCleanerOptions repaired; int generation;
                        StandbyCheck(Settings.Load("EnvFuse_standby", false)
                            && !loaded.CaptureStandbyCleanerAdmissionForTest(out repaired, out generation)(),
                            "parameter repair silently revived an older per-game opt-in: " + problem);
                        loaded.StandbyCleanerEnabled = true;
                        StandbyCheck(loaded.StandbyCleanerEnabled && !Settings.Load("EnvFuse_standby", false)
                            && loaded.CaptureStandbyCleanerAdmissionForTest(out repaired, out generation)(),
                            "repaired settings could not be explicitly enabled: " + problem);
                    }
                    finally
                    {
                        Settings.BeforeStrictStringReadForTest = oldRead;
                        if (loaded != null) StandbyFinish((StandbyCleanerRunner)FamilyPolicyGetField(loaded, "standbyCleaner"));
                    }
                }
                // 设置不可用的时候 构造函数可能没能把保险状态存下来
                // 修复得先把关闭和熔断写进去 再去换那条坏的参数记录
                Settings.Save(PolicyCatalog.KeyStandbyCleaner, true);
                Settings.Save("EnvFuse_standby", false);
                Settings.SaveStr(GameMode.StandbyCleanerOptionsKey, "repair-after-failed-startup-write");
                Settings.SuspendWritesForReset();
                GameMode failedStartup = null;
                try
                {
                    failedStartup = new GameMode(f.Family.DirectoryPath, new SuppressionCore());
                    StandbyCleanerOptions unchanged = failedStartup.StandbyCleaningOptions;
                    StandbyCheck(!failedStartup.StandbyCleanerEnabled && !failedStartup.StandbyCleaningOptionsValid
                        && Settings.Load(PolicyCatalog.KeyStandbyCleaner, false) && !Settings.Load("EnvFuse_standby", false),
                        "fixture did not reproduce rejected startup off/fuse persistence");
                    StandbyCheck(!failedStartup.TrySetStandbyCleanerOptions(StandbyFastOptions())
                        && !failedStartup.StandbyCleaningOptionsValid && ReferenceEquals(unchanged, failedStartup.StandbyCleaningOptions)
                        && Settings.LoadStr(GameMode.StandbyCleanerOptionsKey, "") == "repair-after-failed-startup-write",
                        "repair published new parameters while fail-safe persistence was still blocked");
                    StandbyResumeTransientWrites();
                    StandbyCheck(failedStartup.TrySetStandbyCleanerOptions(StandbyFastOptions())
                        && !failedStartup.StandbyCleanerEnabled && !Settings.Load(PolicyCatalog.KeyStandbyCleaner, true)
                        && Settings.Load("EnvFuse_standby", false),
                        "repair did not persist disabled/fused before committing valid parameters");
                    var afterRepair = new GameMode(f.Family.DirectoryPath, new SuppressionCore());
                    try
                    {
                        afterRepair.ProbeSessionPolicyApply(f.Family.Current("first"));
                        FamilyPolicySetField(afterRepair, "activeDetection", FamilyObservationTarget(f.Family.Current("first")));
                        FamilyPolicySetField(afterRepair, "enabled", true); FamilyPolicySetField(afterRepair, "active", true);
                        StandbyCleanerOptions options; int generation;
                        StandbyCheck(afterRepair.StandbyCleaningOptionsValid && !afterRepair.StandbyCleanerEnabled
                            && !afterRepair.CaptureStandbyCleanerAdmissionForTest(out options, out generation)(),
                            "restart after repair revived the old global/per-game opt-in");
                        afterRepair.StandbyCleanerEnabled = true;
                        StandbyCheck(afterRepair.CaptureStandbyCleanerAdmissionForTest(out options, out generation)(),
                            "repaired and restarted settings could not receive fresh explicit consent");
                    }
                    finally { StandbyFinish((StandbyCleanerRunner)FamilyPolicyGetField(afterRepair, "standbyCleaner")); }
                }
                finally
                {
                    StandbyResumeTransientWrites();
                    if (failedStartup != null) StandbyFinish((StandbyCleanerRunner)FamilyPolicyGetField(failedStartup, "standbyCleaner"));
                }
                StandbyCheck(f.Control.Queries == 0 && f.Control.Purges == 0 && !f.Runner.HasWorkerForTest,
                    "settings load/save started memory work without a game");
            }
        }

        private static void StandbyResumeTransientWrites()
        {
            StandbyCheck(ResetFlowGetStatic(typeof(Settings), "transientValues") != null,
                "test attempted to reopen a live settings writer");
            lock (ResetFlowGetStatic(typeof(Settings), "writeSync"))
                ResetFlowSetStatic(typeof(Settings), "writesSuspendedForReset", false);
        }

        private static void StandbyParameterCommitAndGeneration(string root)
        {
            using (var f = new StandbyPolicyFixture(root, "standby-parameters"))
            {
                f.Ready(); f.Mode.StandbyCleanerEnabled = true;
                Func<bool> initial = f.Capture();
                var value = new StandbyCleanerOptions(2049, 4097, 500);
                StandbyCheck(initial() && f.Mode.TrySetStandbyCleanerOptions(value) && !initial(),
                    "parameter commit left an old cleanup request admitted");
                StandbyCleanerOptions stored;
                StandbyCheck(ReferenceEquals(value, f.Mode.StandbyCleaningOptions)
                    && StandbyCleanerOptions.TryParse(Settings.LoadStr(GameMode.StandbyCleanerOptionsKey, ""), out stored)
                    && stored.ListMegabytes == 2049 && stored.FreeMegabytes == 4097 && stored.PollingMilliseconds == 500,
                    "three parameters did not publish and persist as one immutable snapshot");
                string before = Settings.LoadStr(GameMode.StandbyCleanerOptionsKey, "");
                StandbyCheck(!f.Mode.TrySetStandbyCleanerOptions(new StandbyCleanerOptions(-1, 1, 4000))
                    && !f.Mode.TrySetStandbyCleanerOptions(null)
                    && Settings.LoadStr(GameMode.StandbyCleanerOptionsKey, "") == before,
                    "invalid parameter input partially changed the saved snapshot");
                Func<bool> prior = f.Capture();
                Settings.SuspendWritesForReset();
                StandbyCheck(!f.Mode.TrySetStandbyCleanerOptions(StandbyFastOptions()) && !prior()
                    && ReferenceEquals(value, f.Mode.StandbyCleaningOptions)
                    && Settings.LoadStr(GameMode.StandbyCleanerOptionsKey, "") == before,
                    "failed parameter save published a partial snapshot or kept its stale request live");
                f.Mode.StandbyCleanerEnabled = false;
                StandbyCheck(!f.Mode.StandbyCleanerEnabled && !f.Capture()(),
                    "failed persisted opt-out left runtime cleanup enabled");
                f.Mode.StandbyCleanerEnabled = true;
                StandbyCheck(!f.Mode.StandbyCleanerEnabled && !f.Capture()(),
                    "failed opt-in persistence was treated as consent");
                StandbyCheck(f.Control.Queries == 0 && f.Control.Purges == 0 && !f.Runner.HasWorkerForTest,
                    "editing parameters ran the memory worker synchronously");
            }
        }

        private static void StandbySessionAdmissionAndOverrides(string root)
        {
            using (var f = new StandbyPolicyFixture(root, "standby-admission"))
            {
                f.Mode.StandbyCleanerEnabled = true;
                f.Mode.StepStandbyCleanerForTest();
                StandbyCheck(!f.Capture()() && !f.Runner.HasWorkerForTest && f.Control.Queries == 0,
                    "opt-in without a verified active game started cleanup");
                f.Ready();
                Func<bool> initial = f.Capture();
                StandbyCheck(initial(), "verified opted-in game was not admitted");
                foreach (string field in new[] { "enabled", "active", "stopping", "panicReq", "stickyGraceOnly", "gameGoneSinceTicks", "profileSaveFailureSignaled", "standbyCleanerRestorePending" })
                {
                    object old = FamilyPolicyGetField(f.Mode, field);
                    object changed = field == "gameGoneSinceTicks" ? (object)1L
                        : field == "profileSaveFailureSignaled" || field == "standbyCleanerRestorePending" ? (object)1
                        : (object)(field != "enabled" && field != "active");
                    FamilyPolicySetField(f.Mode, field, changed);
                    StandbyCheck(!initial() && !f.Capture()(), "inactive/unsafe game state admitted cleanup: " + field);
                    FamilyPolicySetField(f.Mode, field, old);
                }
                object target = FamilyPolicyGetField(f.Mode, "activeDetection");
                FamilyPolicySetField(f.Mode, "activeDetection", null);
                StandbyCheck(!initial() && !f.Capture()(), "absent game identity admitted cleanup");
                FamilyPolicySetField(f.Mode, "activeDetection", FamilyObservationTarget(f.Family.Current("second")));
                StandbyCheck(!initial() && !f.Capture()(), "old session policy admitted a different active game");
                FamilyPolicySetField(f.Mode, "activeDetection", target);
                f.Mode.StandbyCleanerEnabled = false; f.Mode.StandbyCleanerEnabled = true;
                StandbyCheck(!initial() && f.Capture()(), "global off/on revived an earlier cleanup request");

                Func<bool> beforeOverride = f.Capture();
                StandbyCheck(f.Mode.SetProfileOverride("first", PolicyCatalog.KeyStandbyCleaner, "0")
                    && !beforeOverride() && !f.Capture()(), "live per-game opt-out was hidden by the session snapshot");
                StandbyCheck(f.Mode.SetProfileOverride("first", PolicyCatalog.KeyStandbyCleaner, "1")
                    && !beforeOverride() && f.Capture()(), "per-game off/on revived a stale cleanup request");
                Func<bool> beforeClear = f.Capture();
                StandbyCheck(f.Mode.ClearProfileOverride("first", PolicyCatalog.KeyStandbyCleaner)
                    && !beforeClear() && f.Capture()(), "clearing an override did not revoke its old generation");
                f.Mode.SetProfileOverride("first", PolicyCatalog.KeyStandbyCleaner, "1");
                Func<bool> beforeBulk = f.Capture();
                StandbyCheck(f.Mode.ClearProfileOverrides("first") > 0 && !beforeBulk() && f.Capture()(),
                    "bulk override reset retained an obsolete cleanup request");
                Func<bool> beforeExit = f.Capture();
                FamilyPolicyInvoke(f.Mode, "InvalidateStandbyCleanerWork");
                StandbyCheck(!beforeExit() && f.Capture()(), "session invalidation did not revoke a captured request permanently");
                StandbyCleanerOptions capturedOptions; int beforeRestoreGeneration, afterRestoreGeneration;
                Func<bool> beforeRestore = f.Mode.CaptureStandbyCleanerAdmissionForTest(out capturedOptions, out beforeRestoreGeneration);
                FamilyPolicySetField(f.Mode, "standbyCleanerRestorePending", 1);
                StandbyCheck(!beforeRestore() && !f.Capture()(),
                    "early restore pending admitted cleanup before panicReq was set");
                FamilyPolicyInvoke(f.Mode, "InvalidateStandbyCleanerWork");
                FamilyPolicySetField(f.Mode, "standbyCleanerRestorePending", 0);
                Func<bool> afterRestore = f.Mode.CaptureStandbyCleanerAdmissionForTest(out capturedOptions, out afterRestoreGeneration);
                StandbyCheck(!beforeRestore() && afterRestore() && afterRestoreGeneration != beforeRestoreGeneration,
                    "completed restore revived a pre-restore cleanup token instead of requiring a new generation");
                StandbyCheck(f.Control.Queries == 0 && f.Control.Purges == 0,
                    "policy admission checks themselves touched memory");
            }
        }

        private static void StandbyLiveFailureFuseAndStaleResults(string root)
        {
            using (var f = new StandbyPolicyFixture(root, "standby-live-fuse"))
            using (var failed = new ManualResetEvent(false))
            {
                f.Ready();
                StandbyCheck(f.Family.Current("first").Overrides.Count == 0,
                    "late-override fixture did not begin with a truly inherited session snapshot");
                f.Mode.StandbyCleanerEnabled = true;
                StandbyCleanerOptions options; int oldGeneration;
                f.Mode.CaptureStandbyCleanerAdmissionForTest(out options, out oldGeneration);
                f.Mode.TrySetStandbyCleanerOptions(StandbyFastOptions());
                FamilyPolicyInvoke(f.Mode, "OnStandbyCleanerFault", oldGeneration, StandbyCleanerResult.PurgeFailed, -1);
                StandbyCheck(f.Mode.StandbyCleanerEnabled && !Settings.Load("EnvFuse_standby", false),
                    "obsolete fault disabled the current policy generation");
                f.Mode.StandbyCleanerEnabled = false;
                StandbyCheck(f.Mode.SetProfileOverride("first", PolicyCatalog.KeyStandbyCleaner, "1") && f.Capture()(),
                    "late per-game opt-in was ignored because the original snapshot had no overrides");
                f.Control.DenyPurge = true; f.Control.NativeStatus = unchecked((int)0xC0000061);
                f.FaultObserved = delegate { failed.Set(); };
                f.Mode.StepStandbyCleanerForTest();
                StandbyCheck(failed.WaitOne(3000), "live cleanup failures did not reach the GameMode fuse");
                StandbyCheck(f.FaultError == null && Settings.Load("EnvFuse_standby", false)
                    && !f.Mode.StandbyCleanerEnabled && !f.Family.Current("first").Overrides.ContainsKey(PolicyCatalog.KeyStandbyCleaner)
                    && !f.Capture()() && f.Control.Purges == 2,
                    "failure fuse did not disable both runtime intent and the current late override");
                int before = f.Control.Queries;
                f.Mode.StepStandbyCleanerForTest();
                StandbyFinish(f.Runner);
                StandbyCheck(f.Control.Queries == before, "fused policy restarted a failed memory worker");
            }
        }

        private static void StandbyShutdownBlocksResetUntilDrained(string root)
        {
            using (var f = new StandbyPolicyFixture(root, "standby-shutdown"))
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                var reset = new ResetFlowFixture(root, "standby-reset");
                f.Ready(); f.Mode.StandbyCleanerEnabled = true; f.Mode.TrySetStandbyCleanerOptions(StandbyFastOptions());
                f.Control.AfterPurge = delegate
                {
                    entered.Set();
                    if (!release.WaitOne(3000)) throw new TimeoutException("Owned reset-blocking purge was not released");
                };
                try
                {
                    f.Mode.StepStandbyCleanerForTest();
                    StandbyCheck(entered.WaitOne(3000), "reset fixture never entered its fake purge");
                    Thread owned = StandbyOwnedWorker(f.Runner);
                    Func<bool> old = f.Capture();
                    FamilyPolicySetField(f.Mode, "stopping", true);
                    FamilyPolicyInvoke(f.Mode, "InvalidateStandbyCleanerWork");
                    int files; string failure;
                    StandbyCheck(!Program.TryResetUserData(reset.DirectoryPath,
                        delegate { return (bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 20); }, out files, out failure),
                        "reset succeeded while a standby purge was still in flight");
                    reset.AssertOriginalFiles(); reset.AssertRegistryPresent();
                    StandbyCheck(!old() && ReferenceEquals(owned, StandbyOwnedWorker(f.Runner))
                        && owned.IsAlive && reset.RestoreCalls == 0 && reset.RegistryCalls == 0,
                        "failed shutdown discarded its worker or began deleting recovery state");
                    release.Set();
                    StandbyCheck(Program.TryResetUserData(reset.DirectoryPath,
                        delegate { return (bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 3000); }, out files, out failure),
                        "reset could not retry after the owned purge drained");
                    reset.AssertOwnedFilesGone(); reset.AssertForeignFiles();
                    StandbyCheck(!f.Runner.HasWorkerForTest && !owned.IsAlive && f.Control.Purges == 1
                        && reset.RestoreCalls == 1 && reset.RegistryCalls == 1,
                        "successful shutdown/reset lost or repeated the in-flight purge");
                }
                finally { release.Set(); StandbyFinish(f.Runner); }
            }
        }

        private static PanelForm StandbyUi(StandbyPolicyFixture fixture)
        {
            // 不构造 Form 不建句柄 不进模态循环 不截图 不发桌面输入
            var form = (PanelForm)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PanelForm));
            GC.SuppressFinalize(form);
            ResetFlowCpuIdleUiSet(form, "gameMode", fixture.Mode);
            ResetFlowCpuIdleUiSet(form, "elevated", true);
            ResetFlowCpuIdleUiSet(form, "cfgProfileId", "first");
            ResetFlowCpuIdleUiSet(form, "cfgProfile", fixture.Family.Current("first"));
            form.StandbyCleanerInvalidOptionsForTest = delegate { throw new InvalidOperationException("Unexpected invalid-options warning"); };
            form.StandbyCleanerOptionsEditorForTest = delegate { throw new InvalidOperationException("Unexpected parameter editor"); };
            form.StandbyCleanerConfirmationForTest = delegate { throw new InvalidOperationException("Unexpected opt-in confirmation"); };
            return form;
        }

        private static void StandbyUiConfirmationAndInheritance(string root)
        {
            using (var f = new StandbyPolicyFixture(root, "standby-ui-consent"))
            {
                PanelForm form = StandbyUi(f);
                int prompts = 0; bool accept = false;
                Action before = delegate { StandbyCheck(!f.Mode.StandbyCleanerEnabled,
                    "global preference changed before confirmation completed"); };
                form.StandbyCleanerConfirmationForTest = delegate
                { prompts++; if (before != null) before(); return accept; };
                ResetFlowCpuIdleUiCall(form, "OnStandbyCleanerToggle", true);
                StandbyCheck(prompts == 1 && !f.Mode.StandbyCleanerEnabled, "canceled enable warning saved consent");
                accept = true; ResetFlowCpuIdleUiCall(form, "OnStandbyCleanerToggle", true);
                StandbyCheck(prompts == 2 && f.Mode.StandbyCleanerEnabled, "accepted enable warning was not saved");
                ResetFlowCpuIdleUiCall(form, "OnStandbyCleanerToggle", false);
                StandbyCheck(prompts == 2 && !f.Mode.StandbyCleanerEnabled, "disabling requested a warning or failed");
                string original = File.ReadAllText(f.Family.LibraryFile);
                before = delegate { StandbyCheck(File.ReadAllText(f.Family.LibraryFile) == original,
                    "per-game configuration changed before confirmation"); };
                accept = false;
                StandbyCheck(!(bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyStandbyCleaner, "1")
                    && prompts == 3 && !f.Family.Current("first").Overrides.ContainsKey(PolicyCatalog.KeyStandbyCleaner),
                    "per-game opt-in bypassed its canceled warning");
                accept = true;
                StandbyCheck((bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyStandbyCleaner, "1")
                    && prompts == 4, "accepted per-game opt-in was rejected");
                ResetFlowCpuIdleUiSet(form, "cfgProfile", f.Family.Current("first"));
                StandbyCheck((bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyStandbyCleaner, "0")
                    && prompts == 4, "per-game opt-out prompted or failed");
                f.Mode.StandbyCleanerEnabled = true;
                ResetFlowCpuIdleUiSet(form, "cfgProfile", f.Family.Current("first"));
                original = File.ReadAllText(f.Family.LibraryFile); accept = false;
                StandbyCheck(PanelForm.CfgStandbyCleanerInheritanceNeedsConfirmation(f.Family.Current("first"))
                    && !(bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyStandbyCleaner, null)
                    && prompts == 5 && f.Family.Current("first").Overrides[PolicyCatalog.KeyStandbyCleaner] == "0",
                    "clearing explicit off silently enabled inherited cleanup");
                accept = true;
                StandbyCheck((bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyStandbyCleaner, null)
                    && prompts == 6 && !f.Family.Current("first").Overrides.ContainsKey(PolicyCatalog.KeyStandbyCleaner),
                    "accepted inherited opt-in did not clear the override");

                // 批量重置写任何东西之前 那些带保护的继承选项都得先确认过
                f.Mode.SetProfileOverride("first", PolicyCatalog.KeyStandbyCleaner, "0");
                f.Mode.DisableCpuIdle = true;
                f.Mode.SetProfileOverride("first", PolicyCatalog.KeyDisableCpuIdle, "0");
                ResetFlowCpuIdleUiSet(form, "cfgProfile", f.Family.Current("first"));
                original = File.ReadAllText(f.Family.LibraryFile);
                int cpuPrompts = 0;
                form.DisableCpuIdleConfirmationForTest = delegate { cpuPrompts++; before(); return true; };
                accept = false;
                StandbyCheck(!(bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgClearAllOverrides") && cpuPrompts == 1
                    && prompts == 7 && File.ReadAllText(f.Family.LibraryFile) == original,
                    "canceling the second bulk-reset warning partially cleared the profile");
                accept = true;
                StandbyCheck((bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgClearAllOverrides") && cpuPrompts == 2
                    && prompts == 8 && f.Family.Current("first").Overrides.Count == 0,
                    "fully confirmed bulk reset did not clear the profile");

                before = null; ResetFlowCpuIdleUiSet(form, "elevated", false);
                ResetFlowCpuIdleUiCall(form, "OnStandbyCleanerToggle", false);
                ResetFlowCpuIdleUiCall(form, "OnStandbyCleanerToggle", true);
                StandbyCheck(!f.Mode.StandbyCleanerEnabled && prompts == 8, "non-admin enabled cleanup or could not opt out");
                ResetFlowCpuIdleUiSet(form, "cfgProfile", f.Family.Current("first"));
                StandbyCheck(!(bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyStandbyCleaner, "1")
                    && prompts == 8, "per-game enable bypassed the administrator gate");
                f.Mode.SetProfileOverride("first", PolicyCatalog.KeyStandbyCleaner, "1");
                ResetFlowCpuIdleUiSet(form, "cfgProfile", f.Family.Current("first"));
                StandbyCheck((bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyStandbyCleaner, "0")
                    && prompts == 8, "non-admin could not turn off an earlier per-game opt-in");
                f.Mode.StandbyCleanerEnabled = true;
                ResetFlowCpuIdleUiSet(form, "cfgProfile", f.Family.Current("first"));
                StandbyCheck(!(bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyStandbyCleaner, null)
                    && prompts == 8 && f.Family.Current("first").Overrides[PolicyCatalog.KeyStandbyCleaner] == "0",
                    "non-admin bypassed enable admission by choosing inheritance");
                StandbyCheck(f.Control.Queries == 0 && f.Control.Purges == 0 && !f.Runner.HasWorkerForTest,
                    "confirmation UI executed memory work");
            }
        }

        private static void StandbyUiParametersAreNotConsent(string root)
        {
            using (var f = new StandbyPolicyFixture(root, "standby-ui-options"))
            {
                PanelForm form = StandbyUi(f);
                string original = Settings.LoadStr(GameMode.StandbyCleanerOptionsKey, "");
                StandbyCleanerOptions current = f.Mode.StandbyCleaningOptions;
                int edits = 0;
                form.StandbyCleanerOptionsEditorForTest = delegate(StandbyCleanerOptions input)
                {
                    edits++; StandbyCheck(ReferenceEquals(input, current), "editor received a torn option snapshot");
                    return null;
                };
                StandbyCheck(!(bool)ResetFlowCpuIdleUiCall(form, "EditStandbyCleanerOptions") && edits == 1
                    && Settings.LoadStr(GameMode.StandbyCleanerOptionsKey, "") == original && !f.Mode.StandbyCleanerEnabled,
                    "canceled parameter editor changed settings or enabled cleanup");
                // 默认值按钮只填托管的文本框 不需要构造 Form 也不需要句柄
                var editor = (StandbyCleanerOptionsDialog)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(StandbyCleanerOptionsDialog));
                GC.SuppressFinalize(editor);
                using (var listInput = new System.Windows.Forms.TextBox())
                using (var freeInput = new System.Windows.Forms.TextBox())
                using (var pollInput = new System.Windows.Forms.TextBox())
                {
                    BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(StandbyCleanerOptionsDialog).GetField("listBox", fields).SetValue(editor, listInput);
                    typeof(StandbyCleanerOptionsDialog).GetField("freeBox", fields).SetValue(editor, freeInput);
                    typeof(StandbyCleanerOptionsDialog).GetField("pollingBox", fields).SetValue(editor, pollInput);
                    int saves = 0;
                    typeof(StandbyCleanerOptionsDialog).GetField("save", fields).SetValue(editor,
                        new Func<StandbyCleanerOptions, bool>(delegate { saves++; return true; }));
                    listInput.Text = "2048"; freeInput.Text = "512"; pollInput.Text = "1000";
                    typeof(StandbyCleanerOptionsDialog).GetMethod("SetInputs", fields).Invoke(editor,
                        new object[] { StandbyCleanerOptions.Default });
                    StandbyCheck(listInput.Text == "1024" && freeInput.Text == "1024" && pollInput.Text == "4000"
                        && saves == 0 && Settings.LoadStr(GameMode.StandbyCleanerOptionsKey, "") == original
                        && ReferenceEquals(current, f.Mode.StandbyCleaningOptions),
                        "defaults button saved parameters before the user accepted the editor");
                    StandbyCheck(!listInput.IsHandleCreated && !freeInput.IsHandleCreated && !pollInput.IsHandleCreated
                        && typeof(System.Windows.Forms.Control).GetField("window", fields).GetValue(editor) == null,
                        "isolated defaults fixture created a native window");
                }
                var selected = new StandbyCleanerOptions(2048, 512, 1000);
                form.StandbyCleanerOptionsEditorForTest = delegate { edits++; return selected; };
                StandbyCheck((bool)ResetFlowCpuIdleUiCall(form, "EditStandbyCleanerOptions") && edits == 2
                    && ReferenceEquals(f.Mode.StandbyCleaningOptions, selected) && !f.Mode.StandbyCleanerEnabled,
                    "parameter save failed or implicitly enabled cleanup");
                StandbyCheck(!(bool)ResetFlowCpuIdleUiCall(form, "ApplyStandbyCleanerOptions", new StandbyCleanerOptions(1, 1, 249))
                    && ReferenceEquals(f.Mode.StandbyCleaningOptions, selected), "invalid UI parameters overwrote a valid snapshot");
                int invalidWarnings = 0, enablePrompts = 0;
                form.StandbyCleanerInvalidOptionsForTest = delegate { invalidWarnings++; };
                form.StandbyCleanerConfirmationForTest = delegate { enablePrompts++; return true; };
                FamilyPolicySetField(f.Mode, "standbyCleanerOptionsValid", false);
                ResetFlowCpuIdleUiCall(form, "OnStandbyCleanerToggle", true);
                StandbyCheck(!f.Mode.StandbyCleanerEnabled && invalidWarnings == 1 && enablePrompts == 0,
                    "invalid saved parameters were allowed through the normal enable warning");
                StandbyCheck(!(bool)ResetFlowCpuIdleUiCall(form, "ApplyCfgPolicyChoice", PolicyCatalog.KeyStandbyCleaner, "1")
                    && invalidWarnings == 2 && enablePrompts == 0,
                    "per-game enable bypassed the invalid-parameter gate");
                StandbyCheck((bool)ResetFlowCpuIdleUiCall(form, "ApplyStandbyCleanerOptions", StandbyCleanerOptions.Default)
                    && f.Mode.StandbyCleaningOptionsValid && !f.Mode.StandbyCleanerEnabled,
                    "repairing parameters did not remain a separate action from consent");
                foreach (int language in new[] { 0, 1 })
                {
                    Lang.Cur = language;
                    StandbyCleanerOptions parsed;
                    StandbyCheck(StandbyCleanerOptionsDialog.TryParseInput(" 2048 ", "0", "250", out parsed)
                        && parsed.ListMegabytes == 2048 && parsed.FreeMegabytes == 0 && parsed.PollingMilliseconds == 250,
                        "valid UI numeric input depended on language or lost its units");
                    foreach (string invalid in new[] { "", "-1", "+1", "1.5", "1,024", "1e3", "1048577", "999999999999" })
                        StandbyCheck(!StandbyCleanerOptionsDialog.TryParseInput(invalid, "1024", "4000", out parsed),
                            "invalid threshold input was accepted: " + invalid);
                    StandbyCheck(!StandbyCleanerOptionsDialog.TryParseInput("1024", "1024", "249", out parsed)
                        && !StandbyCleanerOptionsDialog.TryParseInput("1024", "1024", "300001", out parsed),
                        "polling input escaped the validated range");
                }
                StandbyCheck(f.Control.Queries == 0 && f.Control.Purges == 0 && !f.Runner.HasWorkerForTest,
                    "parameter UI created a worker or touched memory");
                StandbyCleanerOptions saved = f.Mode.StandbyCleaningOptions;
                Settings.SuspendWritesForReset();
                StandbyCheck(!(bool)ResetFlowCpuIdleUiCall(form, "ApplyStandbyCleanerOptions", selected)
                    && ReferenceEquals(saved, f.Mode.StandbyCleaningOptions) && !f.Mode.StandbyCleanerEnabled,
                    "failed UI save published new parameters or enabled cleanup");
            }
        }
    }
}
#endif
