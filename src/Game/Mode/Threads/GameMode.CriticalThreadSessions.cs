using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly SchedulingDiagnosticLog criticalStateLog = new SchedulingDiagnosticLog();
        private void StepCriticalThreadRuntime()
        {
            string missing;
            CriticalRuntimeSource source = CaptureCriticalThreadSource(out missing);
            if (!Monitor.TryEnter(criticalProcessGate, 0)) return;
            try
            {
                // 游戏切回前台后 拿到门禁的这一轮把采样截止时间清零 立刻取一次快照当新基线
                if (Interlocked.Exchange(ref criticalForegroundAuditPending, 0) != 0) unifiedNextCpuSample = 0;
                StepUnifiedThreadRuntimeLocked(source, missing);
            }
            finally
            {
                SettleCriticalRoles();
                Monitor.Exit(criticalProcessGate);
            }
        }
        private bool WithdrawCriticalThreadsLocked(string reason)
        {
            bool track = criticalSession != null || criticalPriorityReservation != null || criticalRestorationConfirmed;
            long started = Stopwatch.GetTimestamp();
            if (criticalSession != null && !criticalRestorationConfirmed)
            {
                LogCriticalThreadState("withdrawn", reason);
            }
            bool restored = RestoreCriticalThreadsLocked();
            if (track) TraceCriticalRuntime("restore", restored ? "ok" : "pending", criticalSource == null ? 0 : criticalSource.Pid, started);
            if (track && restored) FinishCriticalDiagnosticLogs();
            return restored;
        }
        private void FinishCriticalDiagnosticLogs()
        {
            string summary = unifiedStateLog.Finish() + criticalStateLog.Finish();
            if (summary.Length != 0) Logger.Info(Logger.Format("线程诊断阶段结束{0}", "Thread diagnostics phase ended{0}", summary));
        }
        private bool StopCriticalThreads(int timeoutMs, [CallerMemberName] string caller = null)
        {
            if (Monitor.IsEntered(sync))
            {
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                return false;
            }
            if (!Monitor.TryEnter(criticalProcessGate, Math.Max(0, timeoutMs)))
            {
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                return false;
            }
            try
            {
                bool withdrawCriticalThreadsLockedSucceeded = WithdrawCriticalThreadsLocked("stopped by " + caller);
                if (withdrawCriticalThreadsLockedSucceeded)
                {
                    FinishCriticalDiagnosticLogs();
                    EndThreadLayoutDiagnostics(Logger.Text("功能停止：", "feature stopped: ") + caller, true);
                    criticalSource = null;
                    criticalObservation.Reset();
                }
                ResetCriticalAuto();
                return withdrawCriticalThreadsLockedSucceeded;
            }
            finally
            {
                Monitor.Exit(criticalProcessGate);
            }
        }
        private bool PrepareCriticalThreadProcessMaskMutation(int pid, long birth)
        {
            if (!Monitor.IsEntered(criticalProcessGate))
            {
                throw new InvalidOperationException("Original placement gate required");
            }
            CriticalThreadTargetLease criticalThreadTargetLease = criticalOriginalTarget;
            if (criticalSession == null)
            {
                return !CriticalThreadHasRecoveryDebt(true);
            }
            if (criticalThreadTargetLease == null || criticalThreadTargetLease.ProcessId != pid || criticalThreadTargetLease.ProcessCreation != birth)
            {
                return true;
            }
            RevokeCriticalThreadIntent("PrepareCriticalThreadProcessMaskMutation");
            if (!RestoreCriticalThreadsLocked())
            {
                return false;
            }
            criticalSource = null;
            criticalObservation.Reset();
            return true;
        }
        private void PublishCriticalThreadState(CriticalRuntimeSource source, string state, string detail, bool applied = false, bool pending = false, int workers = 0, long observed = 0L, CriticalThreadCoverageState coverage = CriticalThreadCoverageState.None, bool logState = true)
        {
            if (source == null)
            {
                return;
            }
            if (logState) LogCriticalThreadState(state, detail);
            int sameTid = 0;
            bool threadBoost;
            lock (sync) threadBoost = PolicyResolver.For(CriticalPreferenceProfileLocked(source.Profile)).ThreadBoostOn;
            CriticalThreadBoostCoupling coupling = threadBoost ? CriticalThreadBoostCoupling.Classic : CriticalThreadBoostCoupling.BoostOff;
            CriticalThreadUiState view = new CriticalThreadUiState(source.Profile, state, detail, true, applied, pending, workers, observed, (criticalPlan == null) ? 0 : criticalPlan.Workers, (applied && criticalPlan != null) ? criticalPlan.Choices.Select((HardThreadChoice x) => x.Mask).ToArray() : null, applied ? criticalChoiceLabels : null, true, source.Rule.WorkerAvoidance, coverage, source.Token, source.Serial, source.Epoch, source.Pid, source.Birth, source.RuleText, coupling, sameTid);
            lock (sync)
            {
                if (pending || CriticalThreadSnapshotCurrent(view, source.Profile))
                {
                    criticalThreadUi = view;
                }
            }
        }
        private void LogCriticalThreadState(string state, string detail)
        {
            if (state == "observing") return;
            string summary;
            CriticalRuntimeSource source = criticalSource;
            string owner = AutomationSession + "|" + Interlocked.Read(ref criticalIntentEpoch)
                + "|" + (source == null ? "" : source.Pid + "|" + source.Birth);
            if (!criticalStateLog.Admit(owner, state, detail, AutoCores.NowTicks, TimeSpan.FromMinutes(2).Ticks, out summary)) return;
            Logger.Info(Logger.Format("线程 [{0}] 结果={1}{2}{3}", "THREAD [{0}] result={1}{2}{3}", AutomationSession, state, (string.IsNullOrEmpty(detail) ? "" : (Logger.Text(" 详情=", " detail=") + detail)), summary));
        }

    }
}
