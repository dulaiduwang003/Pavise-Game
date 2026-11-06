using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private HardThreadBoostRecoverySnapshot threadBoostRecoverySnapshot;
        private long threadBoostRecoveryNextRead;
        private long threadBoostRecoveryValidUntil;
        private long threadBoostRecoveryGeneration;

        private bool ThreadBoostRecoveryAllowed(int pid, long birth)
        {
            if (pid <= 0 || birth <= 0 || stopping || criticalRestorationConfirmed) return false;
            // Live candidate ownership is handled by ThreadPriorityReservation, not historical journal scanning.
            CriticalThreadTargetLease own = Volatile.Read(ref criticalOriginalTarget);
            bool ownTarget = own != null && !own.Revoked && own.ProcessId == pid && own.ProcessCreation == birth;
            ThreadPriorityReservation reservation = Volatile.Read(ref criticalPriorityReservation);
            bool reserved = reservation != null && ThreadPriorityReservation.HasProcess(pid, birth);
            CriticalThreadHardAffinitySession live = Volatile.Read(ref criticalSession);
            if (live != null)
            {
                HardThreadStatus state = live.Status;
                if (state == HardThreadStatus.AppliedCandidates || state == HardThreadStatus.Applied || state == HardThreadStatus.CoveragePending)
                {
                    return ownTarget && reserved;
                }
                if (state == HardThreadStatus.Ready) return ownTarget && reserved;
                return false;
            }
            if (reservation != null && (!ownTarget || !reserved)) return false;
            if (AutoCores.NowTicks >= Interlocked.Read(ref threadBoostRecoveryValidUntil)) return false;
            HardThreadBoostRecoverySnapshot snapshot = Volatile.Read(ref threadBoostRecoverySnapshot);
            return snapshot != null && snapshot.Allows(pid, birth);
        }

        private void InvalidateThreadBoostRecoveryAdmission()
        {
            lock (ThreadPriorityReservation.Gate)
            {
                Interlocked.Increment(ref threadBoostRecoveryGeneration);
                Interlocked.Exchange(ref threadBoostRecoveryNextRead, 0L);
                Interlocked.Exchange(ref threadBoostRecoveryValidUntil, 0L);
                Volatile.Write(ref threadBoostRecoverySnapshot, HardThreadBoostRecoverySnapshot.Unknown);
            }
        }

        private void ScheduleThreadBoostRecoveryRefresh()
        {
            lock (ThreadPriorityReservation.Gate)
            {
                Interlocked.Increment(ref threadBoostRecoveryGeneration);
                Interlocked.Exchange(ref threadBoostRecoveryNextRead, 0L);
            }
        }

        private void RefreshThreadBoostRecoveryAdmission()
        {
            if (stopping || Volatile.Read(ref criticalSession) != null || sync == null || criticalProcessGate == null
                || Monitor.IsEntered(sync) || Monitor.IsEntered(criticalProcessGate) || Monitor.IsEntered(ThreadPriorityReservation.Gate)) return;
            long now = AutoCores.NowTicks;
            if (now < Interlocked.Read(ref threadBoostRecoveryNextRead)) return;
            long generation = Interlocked.Read(ref threadBoostRecoveryGeneration);
            HardThreadBoostRecoverySnapshot snapshot = HardThreadBoostRecoverySnapshot.Read(CriticalThreadStoreRoot);
            lock (ThreadPriorityReservation.Gate)
            {
                if (generation != Interlocked.Read(ref threadBoostRecoveryGeneration)) return;
                Volatile.Write(ref threadBoostRecoverySnapshot, snapshot);
                Interlocked.Exchange(ref threadBoostRecoveryNextRead, AutoCores.NowTicks + 150000000L);
                Interlocked.Exchange(ref threadBoostRecoveryValidUntil, AutoCores.NowTicks + 300000000L);
            }
        }
    }
}
