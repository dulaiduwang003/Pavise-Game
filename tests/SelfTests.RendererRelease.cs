// 文件用途 渲染交接的恢复测试 只有内存条目和假的还原结果
// 这些测试不打开 不压制 不还原 也不结束任何进程
#if PAVISE_SELFTEST
using System;
using System.Collections;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // 单跑某一组的 runner 可以直接调这个方法
        // 不用进完整自测 不用走生产 Main 也不碰任何改系统的流程
        internal static int RunRendererReleaseRegressionTests()
        {
            string previousLog = Logger.LogPath;
            Logger.LogPath = null;
            try
            {
                Action[] tests =
                {
                    TestRendererReleaseAbsentAndInvalid,
                    TestRendererReleaseIdentityMismatch,
                    TestRendererReleaseImmediateRestored,
                    TestRendererReleaseGone,
                    TestRendererReleasePendingBackoff,
                    TestRendererReleaseExistingPending,
                    TestRendererReleaseParked,
                    TestRendererReleaseOtherReason,
                    TestRendererReleaseUntouchedEntries,
                    TestRendererReleaseFailedApplyDebt,
                    TestRendererReleaseReplacedIdentity,
                    TestRendererReleaseReacquiredSameIdentity,
                    TestRendererReleaseLegacyBoolean,
                    TestRendererDiscardReusedRecords,
                    TestRendererDiscardCurrentLifecycle,
                    TestRendererDiscardNativeMismatch,
                    TestRendererDiscardUntouchedPlaceholder,
                    TestRendererDiscardUnknownDebt,
                    TestRendererDiscardConcurrentReplacement,
                    TestRendererDiscardInPlaceReplacement,
                    TestRendererDiscardAbsentAndInvalid,
                    TestRendererRestoreReleaseRetrySingleFlight,
                    TestRendererRestoreRetryRetrySingleFlight,
                    TestRendererRestoreStaleSnapshot,
                    TestRendererRestoreExceptionClearsFlight,
                    TestRendererRestoreRetryRechecksBackoff
                };
                foreach (Action test in tests) test();
                return tests.Length;
            }
            finally { Logger.LogPath = previousLog; }
        }

        private static void TestRendererReleaseAbsentAndInvalid()
        {
            var fake = new RendererReleaseFixture();
            RendererReleaseEq(BackgroundReleaseState.Ready, fake.Release());
            RendererReleaseEq(BackgroundReleaseState.IdentityMismatch,
                fake.Core.ReleaseBackgroundForRenderer(0, RendererReleaseFixture.Creation, RendererReleaseFixture.Name));
            RendererReleaseEq(BackgroundReleaseState.IdentityMismatch,
                fake.Core.ReleaseBackgroundForRenderer(RendererReleaseFixture.Pid, 0, RendererReleaseFixture.Name));
            RendererReleaseEq(BackgroundReleaseState.IdentityMismatch,
                fake.Core.ReleaseBackgroundForRenderer(RendererReleaseFixture.Pid, RendererReleaseFixture.Creation, " "));
            RendererReleaseEq(0, fake.RestoreCalls);
        }

        private static void TestRendererReleaseIdentityMismatch()
        {
            var fake = new RendererReleaseFixture();
            object entry = fake.Seed(SuppressReason.Background);
            RendererReleaseEq(BackgroundReleaseState.IdentityMismatch,
                fake.Core.ReleaseBackgroundForRenderer(RendererReleaseFixture.Pid, RendererReleaseFixture.Creation + 1, RendererReleaseFixture.Name));
            RendererReleaseEq(BackgroundReleaseState.IdentityMismatch,
                fake.Core.ReleaseBackgroundForRenderer(RendererReleaseFixture.Pid, RendererReleaseFixture.Creation, "DifferentRenderer"));
            RendererReleaseEq(SuppressReason.Background, RendererReleaseField<SuppressReason>(entry, "Reasons"));
            RendererReleaseSet(entry, "Creation", 0L);
            RendererReleaseEq(BackgroundReleaseState.IdentityMismatch, fake.Release());
            RendererReleaseEq(0, fake.RestoreCalls);
        }

        private static void TestRendererReleaseImmediateRestored()
        {
            var fake = new RendererReleaseFixture();
            fake.Seed(SuppressReason.Background);
            RendererReleaseEq(BackgroundReleaseState.Ready,
                fake.Core.ReleaseBackgroundForRenderer(RendererReleaseFixture.Pid, RendererReleaseFixture.Creation,
                    RendererReleaseFixture.Name.ToUpperInvariant()));
            RendererReleaseEq(1, fake.RestoreCalls);
            RendererReleaseEq(RendererReleaseFixture.Creation, fake.LastCreation);
            RendererReleaseEq(false, fake.Core.HasReason(RendererReleaseFixture.Pid, SuppressReason.Background));
            RendererReleaseEq(null, fake.Peek());
            RendererReleaseEq(BackgroundReleaseState.Ready, fake.Release());
            RendererReleaseEq(1, fake.RestoreCalls);
        }

        private static void TestRendererReleaseGone()
        {
            var fake = new RendererReleaseFixture { Result = SuppressionCore.RestoreResult.Gone };
            fake.Seed(SuppressReason.Background);
            RendererReleaseEq(BackgroundReleaseState.Gone, fake.Release());
            RendererReleaseEq(null, fake.Peek());
            RendererReleaseEq(1, fake.RestoreCalls);
            // 核心里没这个条目 不等于断言了它活着
            // 交接的调用方在 Ready 之后照样要验 PID 和创建时间
            RendererReleaseEq(BackgroundReleaseState.Ready, fake.Release());
        }

        private static void TestRendererReleasePendingBackoff()
        {
            var fake = new RendererReleaseFixture { Result = SuppressionCore.RestoreResult.Protected };
            object entry = fake.Seed(SuppressReason.Background);
            RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
            RendererReleaseEq(SuppressReason.None, RendererReleaseField<SuppressReason>(entry, "Reasons"));
            RendererReleaseEq(SuppressionLevel.None, RendererReleaseField<SuppressionLevel>(entry, "BackgroundLevel"));
            RendererReleaseEq(1, fake.RestoreCalls);
            RendererReleaseEq(1, RendererReleaseField<int>(entry, "ProtectedRetries"));
            long retryAt = RendererReleaseField<long>(entry, "NextRetryTicks");
            RendererReleaseEq(true, retryAt > DateTime.UtcNow.Ticks);
            for (int i = 0; i < 12; i++)
                RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
            RendererReleaseEq(retryAt, RendererReleaseField<long>(entry, "NextRetryTicks"));
            RendererReleaseEq(1, fake.RestoreCalls);
            fake.Core.RetryPending();
            RendererReleaseEq(1, fake.RestoreCalls);

            fake.Result = SuppressionCore.RestoreResult.Restored;
            RendererReleaseSet(entry, "NextRetryTicks", 0L);
            fake.Core.RetryPending();
            RendererReleaseEq(2, fake.RestoreCalls);
            RendererReleaseEq(BackgroundReleaseState.Ready, fake.Release());
            RendererReleaseEq(null, fake.Peek());
        }

        private static void TestRendererReleaseExistingPending()
        {
            var fake = new RendererReleaseFixture();
            object entry = fake.Seed(SuppressReason.None);
            RendererReleaseSet(entry, "Applied", false);
            // 这模拟的是从台账恢复出来的条目 或者只还原了一半的进程
            // 没有原因位 加上 Applied=false 并不能把这笔债抹掉
            RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
            RendererReleaseEq(0, fake.RestoreCalls);
            RendererReleaseEq(entry, fake.Peek());
        }

        private static void TestRendererReleaseParked()
        {
            var fake = new RendererReleaseFixture();
            object entry = fake.Seed(SuppressReason.None);
            RendererReleaseSet(entry, "ProtectedRetries", 8);
            RendererReleaseSet(entry, "NextRetryTicks", DateTime.MaxValue.Ticks);
            for (int i = 0; i < 12; i++)
                RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
            fake.Core.RetryPending();
            RendererReleaseEq(0, fake.RestoreCalls);
            RendererReleaseEq(8, RendererReleaseField<int>(entry, "ProtectedRetries"));
            RendererReleaseEq(DateTime.MaxValue.Ticks, RendererReleaseField<long>(entry, "NextRetryTicks"));
        }

        private static void TestRendererReleaseOtherReason()
        {
            var fake = new RendererReleaseFixture();
            object entry = fake.Seed(SuppressReason.AntiCheat | SuppressReason.Background);
            // 受保护的占位从来没拿到过可写的调度状态
            // 所以这个测试不可能漏到任何原生调整上去
            RendererReleaseSet(entry, "OrigPri", uint.MaxValue);
            RendererReleaseSet(entry, "Applied", false);
            RendererReleaseEq(BackgroundReleaseState.OtherReasonActive, fake.Release());
            RendererReleaseEq(SuppressReason.AntiCheat, RendererReleaseField<SuppressReason>(entry, "Reasons"));
            RendererReleaseEq(SuppressionLevel.Isolated, RendererReleaseField<SuppressionLevel>(entry, "AntiCheatLevel"));
            RendererReleaseEq(SuppressionLevel.Isolated, RendererReleaseField<SuppressionLevel>(entry, "Level"));
            RendererReleaseEq("keep-other-reason", RendererReleaseField<string>(entry, "Group"));
            RendererReleaseEq(BackgroundReleaseState.OtherReasonActive, fake.Release());
            RendererReleaseEq(0, fake.RestoreCalls);
        }

        private static void TestRendererReleaseUntouchedEntries()
        {
            var protectedFake = new RendererReleaseFixture();
            object protectedEntry = protectedFake.Seed(SuppressReason.Background);
            RendererReleaseSet(protectedEntry, "OrigPri", uint.MaxValue);
            RendererReleaseEq(BackgroundReleaseState.Ready, protectedFake.Release());
            RendererReleaseEq(0, protectedFake.RestoreCalls);

            var unjournaledFake = new RendererReleaseFixture();
            object unjournaled = unjournaledFake.Seed(SuppressReason.Background);
            RendererReleaseSet(unjournaled, "Journaled", false);
            RendererReleaseSet(unjournaled, "Applied", false);
            RendererReleaseEq(BackgroundReleaseState.Ready, unjournaledFake.Release());
            RendererReleaseEq(0, unjournaledFake.RestoreCalls);
        }

        private static void TestRendererReleaseFailedApplyDebt()
        {
            var fake = new RendererReleaseFixture { Result = SuppressionCore.RestoreResult.Protected };
            object entry = fake.Seed(SuppressReason.Background);
            RendererReleaseSet(entry, "Applied", false);
            RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
            RendererReleaseEq(1, fake.RestoreCalls);
            RendererReleaseEq(true, RendererReleaseField<bool>(entry, "Journaled"));
        }

        private static void TestRendererReleaseReplacedIdentity()
        {
            var fake = new RendererReleaseFixture();
            fake.Seed(SuppressReason.Background);
            object replacement = null;
            fake.OnRestore = delegate
            {
                replacement = fake.Seed(SuppressReason.Background);
                RendererReleaseSet(replacement, "Creation", RendererReleaseFixture.Creation + 1);
            };
            RendererReleaseEq(BackgroundReleaseState.IdentityMismatch, fake.Release());
            RendererReleaseEq(replacement, fake.Peek());
            RendererReleaseEq(SuppressReason.Background, RendererReleaseField<SuppressReason>(replacement, "Reasons"));
            RendererReleaseEq(BackgroundReleaseState.IdentityMismatch, fake.Release());
            RendererReleaseEq(1, fake.RestoreCalls);
        }

        private static void TestRendererReleaseReacquiredSameIdentity()
        {
            var fake = new RendererReleaseFixture();
            fake.Seed(SuppressReason.Background);
            fake.OnRestore = delegate { fake.Seed(SuppressReason.Background); };
            // 就算某个调用方违反租约 在还原过程中把同一个身份换掉了
            // 也不能给它一个假的 Ready
            RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
            RendererReleaseEq(true, fake.Core.HasReason(RendererReleaseFixture.Pid, SuppressReason.Background));
            RendererReleaseEq(1, fake.RestoreCalls);
        }

        private static void TestRendererReleaseLegacyBoolean()
        {
            var fake = new RendererReleaseFixture { Result = SuppressionCore.RestoreResult.Protected };
            fake.Seed(SuppressReason.Background);
            RendererReleaseEq(true, fake.Core.ReleaseIfCreation(RendererReleaseFixture.Pid,
                SuppressReason.Background, RendererReleaseFixture.Creation));
            RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
            RendererReleaseEq(1, fake.RestoreCalls);
        }

        private static void TestRendererDiscardReusedRecords()
        {
            foreach (SuppressReason reasons in new[] { SuppressReason.None, SuppressReason.Background,
                SuppressReason.AntiCheat, SuppressReason.Background | SuppressReason.AntiCheat })
            {
                var fake = new RendererReleaseFixture();
                object other = fake.Seed(SuppressReason.AntiCheat);
                RendererReleaseField<IDictionary>(fake.Core, "map")[RendererReleaseFixture.Pid + 1] = other;
                object old = fake.Seed(reasons);
                RendererReleaseSet(old, "Creation", RendererReleaseFixture.Creation - 1);
                RendererReleaseSet(old, "Name", "PreviousPidOwner");
                RendererReleaseField<IDictionary>(fake.Core, "batchApply")[RendererReleaseFixture.Pid] = "PreviousPidOwner";
                RendererReleaseField<IDictionary>(fake.Core, "batchApplyResults")[RendererReleaseFixture.Pid] = true;
                RendererReleaseField<IDictionary>(fake.Core, "batchApplyErrors")[RendererReleaseFixture.Pid] = "old-result";
                RendererReleaseEq(BackgroundReleaseState.IdentityMismatch, fake.Release());
                RendererReleaseEq(true, fake.Discard());
                RendererReleaseEq(null, fake.Peek());
                RendererReleaseEq(other, RendererReleaseField<IDictionary>(fake.Core, "map")[RendererReleaseFixture.Pid + 1]);
                RendererReleaseEq(reasons, RendererReleaseField<SuppressReason>(old, "Reasons"));
                RendererReleaseEq(0, RendererReleaseField<IDictionary>(fake.Core, "batchApply").Count);
                RendererReleaseEq(0, RendererReleaseField<IDictionary>(fake.Core, "batchApplyResults").Count);
                RendererReleaseEq(0, RendererReleaseField<IDictionary>(fake.Core, "batchApplyErrors").Count);
                RendererReleaseEq(BackgroundReleaseState.Ready, fake.Release());
                RendererReleaseEq(0, fake.Core.CountThrottled(SuppressReason.Background));
                int throttled, protectedCount;
                fake.Core.AntiCheatGroupCountsCached("keep-other-reason", out throttled, out protectedCount);
                RendererReleaseEq(1, throttled);
                RendererReleaseEq(0, protectedCount);
            }
        }

        private static void TestRendererDiscardCurrentLifecycle()
        {
            foreach (SuppressReason reasons in new[] { SuppressReason.None, SuppressReason.Background,
                SuppressReason.AntiCheat, SuppressReason.Background | SuppressReason.AntiCheat })
            {
                var fake = new RendererReleaseFixture();
                object current = fake.Seed(reasons);
                RendererReleaseEq(false, fake.Discard());
                RendererReleaseEq(current, fake.Peek());
                RendererReleaseSet(current, "Name", "SameCreationWrongName");
                RendererReleaseEq(false, fake.Discard());
                RendererReleaseEq(current, fake.Peek());
                RendererReleaseEq(reasons, RendererReleaseField<SuppressReason>(current, "Reasons"));
                RendererReleaseEq(true, RendererReleaseField<bool>(current, "Applied"));
                RendererReleaseEq(true, RendererReleaseField<bool>(current, "Journaled"));
            }
        }

        private static void TestRendererDiscardNativeMismatch()
        {
            for (int variant = 0; variant < 3; variant++)
            {
                var fake = new RendererReleaseFixture();
                object old = fake.Seed(SuppressReason.Background);
                RendererReleaseSet(old, "Creation", RendererReleaseFixture.Creation - 1);
                if (variant == 0) fake.IdentityReadable = false;
                if (variant == 1) fake.LiveCreation++;
                if (variant == 2) fake.LiveName = "DifferentLiveImage";
                RendererReleaseEq(false, fake.Discard());
                RendererReleaseEq(old, fake.Peek());
                RendererReleaseEq(1, fake.IdentityCalls);
                RendererReleaseEq(SuppressReason.Background, RendererReleaseField<SuppressReason>(old, "Reasons"));
            }
            var withoutSeam = new RendererReleaseFixture();
            withoutSeam.Core.RendererDiscardIdentityForTest = null;
            // 内存核心上缺了接缝就得失败关闭
            // 不能去打开一个碰巧跟合成数字对上的宿主 PID
            RendererReleaseEq(false, withoutSeam.Discard());
            RendererReleaseEq(0, withoutSeam.IdentityCalls);
        }

        private static void TestRendererDiscardUntouchedPlaceholder()
        {
            foreach (SuppressReason reasons in new[] { SuppressReason.Background, SuppressReason.AntiCheat,
                SuppressReason.Background | SuppressReason.AntiCheat })
            {
                var fake = new RendererReleaseFixture();
                object placeholder = fake.Seed(reasons);
                RendererReleaseSet(placeholder, "Creation", 0L);
                RendererReleaseSet(placeholder, "OrigPri", uint.MaxValue);
                RendererReleaseSet(placeholder, "Applied", false);
                RendererReleaseSet(placeholder, "Journaled", false);
                RendererReleaseEq(true, fake.Discard());
                RendererReleaseEq(null, fake.Peek());
                RendererReleaseEq(BackgroundReleaseState.Ready, fake.Release());
                RendererReleaseEq(reasons, RendererReleaseField<SuppressReason>(placeholder, "Reasons"));
            }
        }

        private static void TestRendererDiscardUnknownDebt()
        {
            for (int variant = 0; variant < 5; variant++)
            {
                var fake = new RendererReleaseFixture();
                object debt = fake.Seed(SuppressReason.None);
                RendererReleaseSet(debt, "Creation", 0L);
                if (variant == 1) RendererReleaseSet(debt, "Applied", false);
                if (variant == 2)
                {
                    RendererReleaseSet(debt, "Applied", false);
                    RendererReleaseSet(debt, "Journaled", false);
                }
                if (variant >= 3)
                {
                    RendererReleaseSet(debt, "OrigPri", uint.MaxValue);
                    RendererReleaseSet(debt, variant == 3 ? "Applied" : "Journaled", false);
                }
                RendererReleaseEq(false, fake.Discard());
                RendererReleaseEq(debt, fake.Peek());
            }
        }

        private static void TestRendererDiscardConcurrentReplacement()
        {
            for (int variant = 0; variant < 3; variant++)
            {
                var fake = new RendererReleaseFixture();
                if (variant != 2)
                    RendererReleaseSet(fake.Seed(SuppressReason.Background), "Creation", RendererReleaseFixture.Creation - 1);
                object replacement = null;
                int selectedVariant = variant;
                fake.OnIdentity = delegate
                {
                    replacement = fake.Seed(SuppressReason.Background | SuppressReason.AntiCheat);
                    if (selectedVariant == 1)
                        RendererReleaseSet(replacement, "Creation", RendererReleaseFixture.Creation + 1);
                    RendererReleaseField<IDictionary>(fake.Core, "batchApply")[RendererReleaseFixture.Pid] = RendererReleaseFixture.Name;
                };
                RendererReleaseEq(false, fake.Discard());
                RendererReleaseEq(replacement, fake.Peek());
                RendererReleaseEq(SuppressReason.Background | SuppressReason.AntiCheat,
                    RendererReleaseField<SuppressReason>(replacement, "Reasons"));
                RendererReleaseEq(1, RendererReleaseField<IDictionary>(fake.Core, "batchApply").Count);
            }
        }

        private static void TestRendererDiscardInPlaceReplacement()
        {
            foreach (long newCreation in new[] { RendererReleaseFixture.Creation, RendererReleaseFixture.Creation + 1 })
            {
                var fake = new RendererReleaseFixture();
                object placeholder = fake.Seed(SuppressReason.Background);
                RendererReleaseSet(placeholder, "Creation", 0L);
                RendererReleaseSet(placeholder, "OrigPri", uint.MaxValue);
                RendererReleaseSet(placeholder, "Applied", false);
                RendererReleaseSet(placeholder, "Journaled", false);
                fake.OnIdentity = delegate
                {
                    RendererReleaseSet(placeholder, "Creation", newCreation);
                    RendererReleaseSet(placeholder, "OrigPri", 32U);
                    RendererReleaseSet(placeholder, "Applied", true);
                    RendererReleaseSet(placeholder, "Journaled", true);
                };
                RendererReleaseEq(false, fake.Discard());
                RendererReleaseEq(placeholder, fake.Peek());
                RendererReleaseEq(newCreation, RendererReleaseField<long>(placeholder, "Creation"));
            }
        }

        private static void TestRendererDiscardAbsentAndInvalid()
        {
            var fake = new RendererReleaseFixture();
            RendererReleaseEq(false, fake.Core.DiscardReusedRendererTracking(0,
                RendererReleaseFixture.Creation, RendererReleaseFixture.Name));
            RendererReleaseEq(false, fake.Core.DiscardReusedRendererTracking(RendererReleaseFixture.Pid,
                0, RendererReleaseFixture.Name));
            RendererReleaseEq(false, fake.Core.DiscardReusedRendererTracking(RendererReleaseFixture.Pid,
                RendererReleaseFixture.Creation, " "));
            RendererReleaseEq(0, fake.IdentityCalls);
            RendererReleaseEq(true, fake.Discard());
            RendererReleaseEq(true, fake.Discard());
            RendererReleaseEq(2, fake.IdentityCalls);
            fake.IdentityReadable = false;
            RendererReleaseEq(false, fake.Discard());
            RendererReleaseEq(null, fake.Peek());

            fake = new RendererReleaseFixture();
            RendererReleaseSet(fake.Seed(SuppressReason.Background), "Creation", RendererReleaseFixture.Creation - 1);
            fake.OnIdentity = delegate { RendererReleaseField<IDictionary>(fake.Core, "map").Remove(RendererReleaseFixture.Pid); };
            RendererReleaseEq(true, fake.Discard());
            RendererReleaseEq(null, fake.Peek());
        }

        private static void TestRendererRestoreReleaseRetrySingleFlight()
        {
            RendererReleaseConcurrentRestore(true, SuppressionCore.RestoreResult.Restored);
            RendererReleaseConcurrentRestore(true, SuppressionCore.RestoreResult.Protected);
        }

        private static void TestRendererRestoreRetryRetrySingleFlight()
        {
            RendererReleaseConcurrentRestore(false, SuppressionCore.RestoreResult.Restored);
            RendererReleaseConcurrentRestore(false, SuppressionCore.RestoreResult.Protected);
        }

        private static void RendererReleaseConcurrentRestore(bool startWithRelease, SuppressionCore.RestoreResult result)
        {
            var fake = new RendererReleaseFixture { Result = result };
            object entry = fake.Seed(startWithRelease ? SuppressReason.Background : SuppressReason.None);
            using (var started = new ManualResetEvent(false))
            using (var finish = new ManualResetEvent(false))
            {
                fake.OnRestore = delegate
                {
                    if (Volatile.Read(ref fake.RestoreCalls) != 1) return;
                    started.Set();
                    if (!finish.WaitOne(3000)) throw new Exception("Fake restore gate timed out");
                };
                Exception workerError = null;
                BackgroundReleaseState first = BackgroundReleaseState.Pending;
                var worker = new Thread(delegate()
                {
                    try
                    {
                        if (startWithRelease) first = fake.Release();
                        else fake.Core.RetryPending();
                    }
                    catch (Exception error) { workerError = error; }
                });
                worker.IsBackground = true;
                worker.Start();
                try
                {
                    RendererReleaseEq(true, started.WaitOne(3000));
                    RendererReleaseEq(true, RendererReleaseField<bool>(entry, "RestoreInFlight"));
                    for (int i = 0; i < 3; i++)
                    {
                        fake.Core.RetryPending();
                        RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
                    }
                    RendererReleaseEq(1, fake.RestoreCalls);
                    RendererReleaseEq(entry, fake.Peek());
                    RendererReleaseEq(0, RendererReleaseField<int>(entry, "ProtectedRetries"));
                }
                finally
                {
                    finish.Set();
                    if (!worker.Join(3000)) throw new Exception("Fake restore worker did not exit");
                }
                if (workerError != null) throw workerError;
                RendererReleaseEq(false, RendererReleaseField<bool>(entry, "RestoreInFlight"));
                RendererReleaseEq(1, fake.RestoreCalls);
                RendererReleaseEq(0L, fake.Core.ApplyOperations);
                if (result == SuppressionCore.RestoreResult.Restored)
                {
                    if (startWithRelease) RendererReleaseEq(BackgroundReleaseState.Ready, first);
                    RendererReleaseEq(null, fake.Peek());
                    RendererReleaseEq(BackgroundReleaseState.Ready, fake.Release());
                }
                else
                {
                    if (startWithRelease) RendererReleaseEq(BackgroundReleaseState.Pending, first);
                    RendererReleaseEq(entry, fake.Peek());
                    RendererReleaseEq(1, RendererReleaseField<int>(entry, "ProtectedRetries"));
                    RendererReleaseEq(true, RendererReleaseField<long>(entry, "NextRetryTicks") > DateTime.UtcNow.Ticks);
                    fake.Core.RetryPending();
                    RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
                    RendererReleaseEq(1, fake.RestoreCalls);
                }
            }
        }

        private static void TestRendererRestoreStaleSnapshot()
        {
            for (int variant = 0; variant < 4; variant++)
            {
                var fake = new RendererReleaseFixture();
                object captured = fake.Seed(SuppressReason.None);
                object current = null;
                if (variant == 0)
                    RendererReleaseField<IDictionary>(fake.Core, "map").Remove(RendererReleaseFixture.Pid);
                else if (variant == 1 || variant == 2)
                {
                    current = fake.Seed(SuppressReason.None);
                    if (variant == 1) RendererReleaseSet(current, "Creation", RendererReleaseFixture.Creation + 1);
                }
                else
                {
                    current = captured;
                    RendererReleaseSet(current, "Reasons", SuppressReason.Background);
                    // 这道保护要是退化了 Protected 仍然只是个假结果
                    // 在测试里进不了原生的 reThrottle 路径
                    fake.Result = SuppressionCore.RestoreResult.Protected;
                }
                MethodInfo restore = typeof(SuppressionCore).GetMethod("TryRestore",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(int), captured.GetType(), typeof(bool) }, null);
                RendererReleaseEq(false, (bool)restore.Invoke(fake.Core,
                    new object[] { RendererReleaseFixture.Pid, captured, true }));
                RendererReleaseEq(0, fake.RestoreCalls);
                RendererReleaseEq(current, fake.Peek());
                RendererReleaseEq(false, RendererReleaseField<bool>(captured, "RestoreInFlight"));
                RendererReleaseEq(0, RendererReleaseField<int>(captured, "ProtectedRetries"));
            }
        }

        private static void TestRendererRestoreExceptionClearsFlight()
        {
            var fake = new RendererReleaseFixture();
            object entry = fake.Seed(SuppressReason.Background);
            fake.OnRestore = delegate { throw new InvalidOperationException("fake-restore-exception"); };
            bool threw = false;
            try { fake.Release(); }
            catch (InvalidOperationException error) { threw = error.Message == "fake-restore-exception"; }
            RendererReleaseEq(true, threw);
            RendererReleaseEq(false, RendererReleaseField<bool>(entry, "RestoreInFlight"));
            RendererReleaseEq(0, RendererReleaseField<int>(entry, "ProtectedRetries"));
            RendererReleaseEq(entry, fake.Peek());
            RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
            RendererReleaseEq(1, fake.RestoreCalls);
            fake.OnRestore = null;
            fake.Core.RetryPending();
            RendererReleaseEq(2, fake.RestoreCalls);
            RendererReleaseEq(false, RendererReleaseField<bool>(entry, "RestoreInFlight"));
            RendererReleaseEq(BackgroundReleaseState.Ready, fake.Release());
            RendererReleaseEq(null, fake.Peek());
        }

        private static void TestRendererRestoreRetryRechecksBackoff()
        {
            var fake = new RendererReleaseFixture { Result = SuppressionCore.RestoreResult.Protected };
            // 线程 1 在该重试的时候抓住了这个 Entry 然后停住了
            object captured = fake.Seed(SuppressReason.None);
            // 线程 2 把整次尝试做完 发布了新的退避
            fake.Core.RetryPending();
            RendererReleaseEq(1, fake.RestoreCalls);
            long retryAt = RendererReleaseField<long>(captured, "NextRetryTicks");
            RendererReleaseEq(true, retryAt > DateTime.UtcNow.Ticks);
            RendererReleaseEq(false, RendererReleaseField<bool>(captured, "RestoreInFlight"));
            MethodInfo restore = typeof(SuppressionCore).GetMethod("TryRestore",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(int), captured.GetType(), typeof(bool) }, null);
            // 迟到的快照进的是同一把单飞准入锁
            RendererReleaseEq(false, (bool)restore.Invoke(fake.Core,
                new object[] { RendererReleaseFixture.Pid, captured, true }));
            RendererReleaseEq(1, fake.RestoreCalls);
            RendererReleaseEq(1, RendererReleaseField<int>(captured, "ProtectedRetries"));
            RendererReleaseEq(retryAt, RendererReleaseField<long>(captured, "NextRetryTicks"));
            RendererReleaseEq(BackgroundReleaseState.Pending, fake.Release());
            fake.Result = SuppressionCore.RestoreResult.Restored;
            RendererReleaseSet(captured, "NextRetryTicks", 0L);
            fake.Core.RetryPending();
            RendererReleaseEq(2, fake.RestoreCalls);
            RendererReleaseEq(BackgroundReleaseState.Ready, fake.Release());

            // 最开始那次显式的 Background 释放不算周期重试
            fake = new RendererReleaseFixture();
            object initial = fake.Seed(SuppressReason.Background);
            RendererReleaseSet(initial, "NextRetryTicks", DateTime.MaxValue.Ticks);
            RendererReleaseEq(BackgroundReleaseState.Ready, fake.Release());
            RendererReleaseEq(1, fake.RestoreCalls);
        }

        private static void RendererReleaseEq<T>(T expected, T actual)
        {
            if (!object.Equals(expected, actual))
                throw new Exception("renderer release: expected " + expected + ", actual " + actual);
        }

        private const BindingFlags RendererReleaseFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static T RendererReleaseField<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, RendererReleaseFields).GetValue(target);
        }

        private static void RendererReleaseSet(object target, string name, object value)
        {
            target.GetType().GetField(name, RendererReleaseFields).SetValue(target, value);
        }

        private sealed class RendererReleaseFixture
        {
            internal const int Pid = 45101;
            internal const long Creation = 132500000001234567L;
            internal const string Name = "RendererReleaseFake";
            internal readonly SuppressionCore Core;
            internal SuppressionCore.RestoreResult Result = SuppressionCore.RestoreResult.Restored;
            internal int RestoreCalls;
            internal long LastCreation;
            internal Action OnRestore;
            internal int IdentityCalls;
            internal bool IdentityReadable = true;
            internal long LiveCreation = Creation;
            internal string LiveName = Name;
            internal Action OnIdentity;
            private int mutationCalls;

            internal RendererReleaseFixture()
            {
                Core = new SuppressionCore(delegate(int pid, long creation, string name)
                {
                    RendererReleaseEq(Pid, pid);
                    RendererReleaseEq(Name, name);
                    Interlocked.Increment(ref RestoreCalls);
                    LastCreation = creation;
                    if (OnRestore != null) OnRestore();
                    return Result;
                }, true);
                Core.RendererDiscardIdentityForTest = delegate(int pid, long expectedCreation, string expectedName)
                {
                    RendererReleaseEq(Pid, pid);
                    IdentityCalls++;
                    bool matches = IdentityReadable && LiveCreation == expectedCreation
                        && string.Equals(LiveName, expectedName, StringComparison.OrdinalIgnoreCase);
                    if (OnIdentity != null) OnIdentity();
                    return matches;
                };
                Core.ConfigureMutationBoundary(delegate { mutationCalls++; }, delegate { mutationCalls++; });
                RendererReleaseEq(false, RendererReleaseField<bool>(Core, "marked"));
                RendererReleaseEq(null, RendererReleaseField<string>(Core, "journalPath"));
            }

            internal BackgroundReleaseState Release()
            {
                return Core.ReleaseBackgroundForRenderer(Pid, Creation, Name);
            }

            internal bool Discard()
            {
                bool result = Core.DiscardReusedRendererTracking(Pid, Creation, Name);
                RendererReleaseEq(0, RestoreCalls);
                RendererReleaseEq(0L, Core.ApplyOperations);
                RendererReleaseEq(0, mutationCalls);
                return result;
            }

            internal object Peek()
            {
                return RendererReleaseField<IDictionary>(Core, "map")[Pid];
            }

            internal object Seed(SuppressReason reasons)
            {
                Type type = typeof(SuppressionCore).GetNestedType("Entry", BindingFlags.NonPublic);
                object entry = Activator.CreateInstance(type, true);
                RendererReleaseSet(entry, "Name", Name);
                RendererReleaseSet(entry, "Group", "keep-other-reason");
                RendererReleaseSet(entry, "Creation", Creation);
                RendererReleaseSet(entry, "OrigPri", 32U);
                RendererReleaseSet(entry, "Reasons", reasons);
                RendererReleaseSet(entry, "BackgroundLevel", (reasons & SuppressReason.Background) != 0 ? SuppressionLevel.Isolated : SuppressionLevel.None);
                RendererReleaseSet(entry, "AntiCheatLevel", (reasons & SuppressReason.AntiCheat) != 0 ? SuppressionLevel.Isolated : SuppressionLevel.None);
                RendererReleaseSet(entry, "Level", reasons != SuppressReason.None ? SuppressionLevel.Isolated : SuppressionLevel.None);
                RendererReleaseSet(entry, "Applied", true);
                RendererReleaseSet(entry, "Journaled", true);
                RendererReleaseField<IDictionary>(Core, "map")[Pid] = entry;
                return entry;
            }
        }
    }
}
#endif
