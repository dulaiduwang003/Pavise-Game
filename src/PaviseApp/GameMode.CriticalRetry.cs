using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class CriticalRecoveryPhase
        {
            internal int Failures, ClosureWaits;
            internal long Next, ExitProbe;
            internal bool Exited;
        }
        // 清理等待先观察辅助进程退出，不消耗失败预算；失败阶段最多尝试三次，间隔 30 秒。

        private const int CriticalClosureWaitPolls = 15;
        private readonly Dictionary<object, CriticalRecoveryPhase> criticalRecoveryPhases = new Dictionary<object, CriticalRecoveryPhase>();

        private bool RestoreCriticalThreadsLocked()
        {
            if (!Monitor.IsEntered(criticalProcessGate)) throw new InvalidOperationException("Original placement gate required");
            if (criticalOriginalTarget != null) criticalOriginalTarget.Revoke();
            // 软提示没有恢复债务，无论硬绑定状态如何，都先处理软提示。
            string hints = criticalIdealHints.Release();
            if (hints.Length != 0) Logger.Info("线程软放置已释放： " + hints);
            object owner = (object)criticalSession ?? criticalPriorityReservation ?? CriticalCoreIsolationRecoveryOwner;
            if (owner == null)
            {
                bool neutral = RestoreCriticalThreadsCore();
                if (!neutral && !criticalThreadResidue && criticalHistoricalDebt)
                    RecoveryAttemptTrace.Exhausted(() => !criticalHistoricalDebt);
                return neutral;
            }
            CriticalRecoveryPhase phase;
            if (!criticalRecoveryPhases.TryGetValue(owner, out phase)) criticalRecoveryPhases[owner] = phase = new CriticalRecoveryPhase();
            long now = AutoCores.NowTicks;
            if (now >= phase.ExitProbe && !phase.Exited && criticalOriginalTarget != null)
            {
                phase.ExitProbe = now + TimeSpan.FromSeconds(30).Ticks;
                if (ProbeBoostTarget(criticalOriginalTarget.ProcessId, criticalOriginalTarget.ProcessCreation) == BoostTarget.Gone)
                { phase.Exited = true; phase.Failures = 0; phase.Next = 0; }
            }
            if (phase.Failures >= 3 || now < phase.Next)
            { criticalThreadResidue = true; ReportCriticalRecovery(owner, phase); return false; }
            bool restored = false;
            using (var child = new RecoveryAttemptTrace())
            {
            criticalClosureObserveOnly = criticalHelperClosureWaiting && phase.ClosureWaits < CriticalClosureWaitPolls;
            try { restored = RestoreCriticalThreadsCore(); return restored; }
            finally
            {
                criticalClosureObserveOnly = false;
                if (restored || (criticalSession == null && criticalPriorityReservation == null && CriticalCoreIsolationRecoveryOwner == null && !criticalThreadResidue))
                {
                    criticalRecoveryPhases.Remove(owner);
                    if (!restored && criticalHistoricalDebt) RecoveryAttemptTrace.Exhausted(() => !criticalHistoricalDebt);
                }
                else if (child.Reported) criticalThreadResidue = true;
                else if (criticalHelperClosureWaiting && phase.ClosureWaits < CriticalClosureWaitPolls)
                {
                    phase.ClosureWaits++;
                    phase.Next = AutoCores.NowTicks + TimeSpan.FromSeconds(1).Ticks;
                    criticalThreadResidue = true;
                    RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                }
                else
                {
                    phase.Failures++;
                    phase.Next = AutoCores.NowTicks + TimeSpan.FromSeconds(30).Ticks;
                    criticalThreadResidue = true;
                    ReportCriticalRecovery(owner, phase);
                    Logger.Warn("关键线程恢复失败=" + phase.Failures + "/3"
                        + (phase.Failures >= 3 ? "；已停止主动尝试，保留原会话、句柄和日志" : "；等待恢复"));
                }
                if (!restored) child.Propagate();
            }
            }
        }
        private void ReportCriticalRecovery(object owner, CriticalRecoveryPhase phase)
        {
            if (phase.Failures < 3) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(Math.Max(0, phase.Next - AutoCores.NowTicks))); return; }
            RecoveryAttemptTrace.Exhausted(delegate
            {
                lock (criticalProcessGate)
                {
                    if (!criticalRecoveryPhases.ContainsKey(owner)) return true;
                    return !phase.Exited && criticalOriginalTarget != null
                        && ProbeBoostTarget(criticalOriginalTarget.ProcessId, criticalOriginalTarget.ProcessCreation) == BoostTarget.Gone;
                }
            });
        }
    }
}
