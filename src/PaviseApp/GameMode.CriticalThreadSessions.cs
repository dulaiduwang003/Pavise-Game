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
            if (summary.Length != 0) Logger.Info("线程诊断阶段结束" + summary);
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
                    EndThreadLayoutDiagnostics("功能停止：" + caller, true);
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
            criticalNextSample = 0L;
            return true;
        }
        private void PublishCriticalThreadState(CriticalRuntimeSource source, string state, string detail, bool applied = false, bool pending = false, int workers = 0, long observed = 0L, CriticalThreadCoverageState coverage = CriticalThreadCoverageState.None, bool logState = true)
        {
            if (source == null)
            {
                return;
            }
            if (logState) LogCriticalThreadState(state, detail);
            int sameTid;
            CriticalThreadBoostCoupling coupling = CriticalThreadCoupling(source.Pid, source.Birth, applied, out sameTid);
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
            Logger.Info("线程 [" + AutomationSession + "] 结果=" + state + (string.IsNullOrEmpty(detail) ? "" : (" 详情=" + detail)) + summary);
        }
        private CriticalThreadBoostCoupling CriticalThreadCoupling(int pid, long birth, bool applied, out int sameTid)
        {
            if (criticalSource != null && criticalSource.Rule.IsUnified)
            { sameTid = 0; return CriticalThreadBoostCoupling.Unified; }
            PolicySnapshot policySnapshot = sessionPolicy;
            if (policySnapshot == null)
            {
                sameTid = 0;
                return CriticalThreadBoostCoupling.Unknown;
            }
            string value = policySnapshot.ValueOf("GmThreadBoostStrategyV1");
            string mode = ((!ThreadBoostSelection.IsOn(value)) ? "off" : (LaneFallbackFor(pid, birth) ? "candidate" : "frame"));
            int pid2;
            long processBirth;
            int tid;
            long threadBirth;
            CriticalThreadLaneThread lane = (RenderLane.TryGetAppliedThread(out pid2, out processBirth, out tid, out threadBirth) ? new CriticalThreadLaneThread(pid2, processBirth, tid, threadBirth) : null);
            HardThreadPlan hardThreadPlan = criticalPlan;
            return CriticalThreadBoostCouplingPolicy.Classify(policySnapshot.EffBoost, mode, LaneSupported(policySnapshot.Preset), pid, birth, lane, (applied && hardThreadPlan != null) ? hardThreadPlan.Choices.Select((HardThreadChoice x) => x.Thread).ToArray() : null, criticalChoiceFrameAssociated, out sameTid);
        }

    }
}
