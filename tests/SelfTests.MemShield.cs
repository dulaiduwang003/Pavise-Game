// All quota queries and quota writes are injected. No native process access,
// registry or windows are used.
// 内存驻留已下架 这里只回归崩溃残账的配额还原路径 快照由测试直接播种
#if PAVISE_SELFTEST
using System;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int memShieldChecks;
        private const ulong MemGiB = 1024UL * 1024UL * 1024UL;

        internal static int RunMemShieldRegressionTests()
        {
            Action[] tests =
            {
                MemShieldEmptySnapshotHealsClean,
                MemShieldHealRestoresQuotaOnLiveTarget,
                MemShieldHealClearsWhenGameGone,
                MemShieldHealClearsOnPidReuse,
                MemShieldHealClearsWhenQuotaAlreadyUnlocked,
                MemShieldCorruptSnapshotStaysAsDebt,
                MemShieldFailedRestoreKeepsDebt
            };
            memShieldChecks = 0;
            foreach (Action test in tests)
            {
                Settings.UseTransientStoreForCurrentProcess();
                Lang.Cur = 0;
                MemShield.ResetForTest();
                try { test(); }
                finally { MemShield.ResetForTest(); }
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS mem-shield assertions=" + memShieldChecks
                + " native_memory=mocked settings=transient windows_shown=false");
            return tests.Length;
        }

        private static void MemCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Mem shield regression: " + message);
            Interlocked.Increment(ref memShieldChecks);
        }

        // 旧版本对局中崩溃后的目标进程假件 配额上还挂着硬下限 写入会真的改变后续查询
        private sealed class MemShieldFake
        {
            internal bool Gone;
            internal long Creation = 7;
            internal ulong Min = 2 * MemGiB, Max = 3 * MemGiB;
            internal uint Flags = MemShield.HardMinEnable | 0x8;
            internal bool IgnoreSet;
            internal int SetCalls;

            internal void Install()
            {
                MemShield.QueryForTest = delegate(int pid, out bool gone, out long creation,
                    out ulong workingSet, out ulong min, out ulong max, out uint flags)
                {
                    gone = Gone; creation = Creation; workingSet = 2 * MemGiB;
                    min = Min; max = Max; flags = Flags;
                    return !Gone;
                };
                MemShield.SetForTest = delegate(int pid, long creation, ulong min, ulong max, uint flags)
                {
                    SetCalls++;
                    if (IgnoreSet) return;
                    Min = min; Max = max; Flags = flags;
                };
            }
        }

        // 快照播种 = 旧版本锁定期间崩溃留下的残账 记录目标身份与原配额
        private static void SeedCrashSnapshot(int pid, long creation)
        {
            Settings.SaveStr(MemShield.SnapKey,
                MemShield.EncodeSnapshot(pid, creation, 200 * 1024 * 1024, MemGiB, 0));
        }

        private static void MemShieldEmptySnapshotHealsClean()
        {
            var fake = new MemShieldFake();
            fake.Install();
            MemCheck(MemShield.HealFromCrash(), "an empty snapshot must heal as a no-op");
            MemCheck(!MemShield.HasResidue() && fake.SetCalls == 0,
                "an empty snapshot must not touch any quota");
        }

        private static void MemShieldHealRestoresQuotaOnLiveTarget()
        {
            var fake = new MemShieldFake();
            fake.Install();
            SeedCrashSnapshot(4242, 7);
            MemCheck(MemShield.HealFromCrash(), "healing a live locked target must succeed");
            MemCheck(fake.SetCalls == 1 && (fake.Flags & MemShield.HardMinEnable) == 0,
                "healing must write back the recorded original quota exactly once");
            MemCheck(!MemShield.HasResidue(), "a healed snapshot must be cleared");
        }

        private static void MemShieldHealClearsWhenGameGone()
        {
            var fake = new MemShieldFake { Gone = true };
            fake.Install();
            SeedCrashSnapshot(4242, 7);
            MemCheck(MemShield.HealFromCrash(), "a gone target must settle: the quota died with it");
            MemCheck(!MemShield.HasResidue() && fake.SetCalls == 0,
                "a gone target must clear the record without writing");
        }

        private static void MemShieldHealClearsOnPidReuse()
        {
            // PID 被系统复用 创建时间对不上 绝不能把配额写到无关进程身上
            var fake = new MemShieldFake { Creation = 9 };
            fake.Install();
            SeedCrashSnapshot(4242, 7);
            MemCheck(MemShield.HealFromCrash(), "a reused pid must settle as gone");
            MemCheck(!MemShield.HasResidue() && fake.SetCalls == 0,
                "a reused pid must clear the record without writing");
        }

        private static void MemShieldHealClearsWhenQuotaAlreadyUnlocked()
        {
            // 硬下限已被外力清掉 无债可还
            var fake = new MemShieldFake { Flags = 0 };
            fake.Install();
            SeedCrashSnapshot(4242, 7);
            MemCheck(MemShield.HealFromCrash(), "an already-unlocked target must settle");
            MemCheck(!MemShield.HasResidue() && fake.SetCalls == 0,
                "an already-unlocked target must clear the record without writing");
        }

        private static void MemShieldCorruptSnapshotStaysAsDebt()
        {
            var fake = new MemShieldFake();
            fake.Install();
            Settings.SaveStr(MemShield.SnapKey, "garbage");
            MemCheck(!MemShield.HealFromCrash(), "an unparsable snapshot must not restore blindly");
            MemCheck(MemShield.HasResidue() && MemShield.RecoveryBlockedForTest && fake.SetCalls == 0,
                "an unparsable snapshot must stay recorded and never reach the target");
        }

        private static void MemShieldFailedRestoreKeepsDebt()
        {
            // 写入被拒 回读仍是锁定态 记录必须保留给下次启动
            var fake = new MemShieldFake { IgnoreSet = true };
            fake.Install();
            SeedCrashSnapshot(4242, 7);
            MemCheck(!MemShield.HealFromCrash(), "an unconfirmed restore must fail");
            MemCheck(MemShield.HasResidue(), "a failed restore must keep the record as the debt");
        }
    }
}
#endif
