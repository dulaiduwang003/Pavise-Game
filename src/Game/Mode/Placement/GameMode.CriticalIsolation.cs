using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // 线程放置可能获取此锁 隔离代码持有此锁时 不能反向取得线程放置锁

        private readonly object coreIsolationGate = new object();
        private bool criticalOwnsCoreIsolation;
        private bool criticalBorrowsCoreIsolation;
        private object criticalCoreIsolationOwner;
        private int criticalCoreIsolationPid;
        private long criticalCoreIsolationBirth;
        private string criticalCoreIsolationError;

        private object CriticalCoreIsolationRecoveryOwner
        {
            get { lock (coreIsolationGate) return criticalOwnsCoreIsolation ? criticalCoreIsolationOwner : null; }
        }

        private bool CriticalCoreIsolationRefused(string reason)
        {
            criticalCoreIsolationError = reason;
            return false;
        }

        private bool StopCriticalCoreIsolation()
        {
            if (!Monitor.IsEntered(criticalProcessGate)) throw new InvalidOperationException("Original placement gate required");
            lock (coreIsolationGate)
            {
                if (criticalOwnsCoreIsolation)
                {
                    if (coreIsolation == null) return CriticalCoreIsolationRefused("critical-isolation-owner-lost");
                    if (!coreIsolation.Stop())
                        return CriticalCoreIsolationRefused(coreIsolation.LastError ?? "critical-isolation-restore-pending");
                    coreIsolation = null;
                    criticalOwnsCoreIsolation = false;
                    criticalCoreIsolationOwner = null;
                }
                // 借用的整游戏租约归普通功能所有
                criticalBorrowsCoreIsolation = false;
                criticalCoreIsolationPid = 0;
                criticalCoreIsolationBirth = 0;
                criticalCoreIsolationError = null;
                return true;
            }
        }
    }
}
