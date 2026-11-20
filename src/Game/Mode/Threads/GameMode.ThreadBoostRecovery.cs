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
            // 存活候选的所有权由 ThreadPriorityReservation 管理 不由历史日志扫描接管
            CriticalThreadTargetLease own = Volatile.Read(ref criticalOriginalTarget);
            bool ownTarget = own != null && !own.Revoked && own.ProcessId == pid && own.ProcessCreation == birth;
            ThreadPriorityReservation reservation = Volatile.Read(ref criticalPriorityReservation);
            bool reserved = reservation != null && ThreadPriorityReservation.HasProcess(pid, birth);
            CriticalThreadHardAffinitySession live = Volatile.Read(ref criticalSession);
            if (live != null)
            {
                HardThreadStatus state = live.Status;
                if (state == HardThreadStatus.AppliedCandidates || state == HardThreadStatus.Applied || state == HardThreadStatus.CoveragePending
                    || state == HardThreadStatus.Ready)
                {
                    // 亲和性会话不写线程优先级也不预留 游戏线程提优照常准入
                    // 历史恢复债在会话准入前已核验结清 这里不读账本
                    return ownTarget && (reserved || reservation == null && PlacementOnlySessionFor(pid, birth));
                }
                return false;
            }
            if (reservation != null && (!ownTarget || !reserved)) return false;
            if (AutoCores.NowTicks >= Interlocked.Read(ref threadBoostRecoveryValidUntil)) return false;
            HardThreadBoostRecoverySnapshot snapshot = Volatile.Read(ref threadBoostRecoverySnapshot);
            return snapshot != null && snapshot.Allows(pid, birth);
        }

        // 当前统一调度来源是本进程的亲和性会话 且没有线程属性控制 线程协同的工作线程调用 不拿会话锁
        private bool PlacementOnlySessionFor(int pid, long birth)
        {
            CriticalRuntimeSource source = Volatile.Read(ref criticalSource);
            return source != null && source.Pid == pid && source.Birth == birth;
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
