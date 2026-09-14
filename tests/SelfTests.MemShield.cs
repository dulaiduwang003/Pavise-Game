// File purpose Quota queries and quota writes are all injected
// Never touches native process APIs, the registry or windows
// Memory residency has been retired, this only regresses the crash-residue quota restore path, snapshots are seeded directly by the test
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

        // Fake of a target process left over from an old-version in-match crash: quota still carries the hard minimum, writes really change later queries
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

        // Snapshot seeding = residue left by a crash during old-version locking, records target identity and original quota
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
            // PID reused by the system, creation time mismatches, must never write the quota onto an unrelated process
            var fake = new MemShieldFake { Creation = 9 };
            fake.Install();
            SeedCrashSnapshot(4242, 7);
            MemCheck(MemShield.HealFromCrash(), "a reused pid must settle as gone");
            MemCheck(!MemShield.HasResidue() && fake.SetCalls == 0,
                "a reused pid must clear the record without writing");
        }

        private static void MemShieldHealClearsWhenQuotaAlreadyUnlocked()
        {
            // Hard minimum already cleared by an outside party, no debt left to repay
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
            // Write rejected, read-back still shows the locked state, the record must be kept for the next startup
            var fake = new MemShieldFake { IgnoreSet = true };
            fake.Install();
            SeedCrashSnapshot(4242, 7);
            MemCheck(!MemShield.HealFromCrash(), "an unconfirmed restore must fail");
            MemCheck(MemShield.HasResidue(), "a failed restore must keep the record as the debt");
        }
    }
}
#endif
