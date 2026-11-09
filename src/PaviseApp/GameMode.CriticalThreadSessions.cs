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
        private void StepCriticalThreadRuntime()
        {
            string missing;
            CriticalRuntimeSource criticalRuntimeSource = CaptureCriticalThreadSource(out missing);
            if (!Monitor.TryEnter(criticalProcessGate, 0))
            {
                return;
            }
            try
            {
                if (criticalStability == null) criticalStability = new CriticalThreadStabilityPolicy();
                if (criticalSession == null && criticalPriorityReservation != null && !RestoreCriticalThreadsLocked()) return;
                if (Interlocked.Exchange(ref criticalForegroundAuditPending, 0) != 0)
                {
                    criticalNextSample = 0L;
                }
                if (criticalRestorationConfirmed)
                {
                    if (!RestoreCriticalThreadsLocked())
                    {
                        return;
                    }
                    criticalObservation.Reset();
                    criticalNextSample = 0L;
                }
                if (criticalSource != null && !criticalSource.Same(criticalRuntimeSource))
                {
                    if (!WithdrawCriticalThreadsLocked((criticalRuntimeSource == null) ? missing : criticalSource.Difference(criticalRuntimeSource)))
                    {
                        return;
                    }
                    criticalSource = null;
                    criticalObservation.Reset();
                    criticalNextSample = 0L;
                }
                if (criticalRuntimeSource == null)
                {
                    WithdrawCriticalThreadsLocked(missing);
                    return;
                }
                if (criticalSource == null)
                {
                    criticalSource = criticalRuntimeSource;
                    criticalReassertions = 0;
                    criticalObservationMisses = 0;
                }
                CriticalRuntimeSource criticalRuntimeSource2 = criticalSource;
                if (criticalStability.IsBlocked(criticalSource.Profile, criticalSource.Pid, criticalSource.Birth, criticalSource.Serial, criticalSource.RuleText))
                {
                    if (WithdrawCriticalThreadsLocked("repeated thread placement conflict"))
                        PublishCriticalThreadState(criticalSource, "conflict", PerformancePolicy.Text("本局线程覆盖或归属未能确认，已恢复原设置并停止重新绑定", "Thread coverage or ownership could not be confirmed; original settings were restored and binding is stopped for this session"), false, false, 0, 0L);
                    return;
                }
                if (criticalSession == null && CriticalThreadHasRecoveryDebt(false))
                {
                    PublishCriticalThreadState(criticalRuntimeSource2, "recovery-pending", PerformancePolicy.Text("旧线程恢复记录尚未结清；未新增绑定", "Previous thread recovery is unresolved; no new binding is applied"), false, true, 0, 0L);
                    return;
                }
                if (ProtectedGameRoster.Contains(criticalRuntimeSource2.Name))
                {
                    if (WithdrawCriticalThreadsLocked("the game is on the protected roster"))
                    {
                        criticalObservation.Reset();
                        PublishCriticalThreadState(criticalRuntimeSource2, "unsupported", PerformancePolicy.Text("该游戏此前拒绝或剥夺过写入权限；未施加线程硬绑定", "This game previously denied or stripped write access; no hard thread binding is applied"), false, false, 0, 0L);
                    }
                    return;
                }
                if (criticalGuardianLaunchBlocked)
                {
                    if (WithdrawCriticalThreadsLocked("independent guardian launch unavailable"))
                    {
                        criticalObservation.Reset();
                        ResetCriticalAuto();
                        PublishCriticalThreadState(criticalRuntimeSource2, "unsupported", CriticalGuardianLaunchBlockedDetail(), false, false, 0, 0L);
                    }
                    return;
                }
                if (smartSwitchedOut || RendererForegroundPid() != criticalRuntimeSource2.Pid)
                {
                    // 最小化可能使采样无效；保留现有租约和观测历史，等待游戏返回。
                    if (criticalSession == null)
                    {
                        PublishCriticalThreadState(criticalRuntimeSource2, "observing", PerformancePolicy.Text("等待游戏回到前台后重新观察", "Observations restart when the game returns to the foreground"), false, false, 0, 0L);
                    }
                    return;
                }
                bool observerRequested = false;
                if (criticalRuntimeSource2.Rule.Automatic && criticalSession == null && criticalObservation.Hotspots.Length > 0)
                {
                    criticalRolesTouched = true;
                    if (!FrameCoordination.HasCriticalPresenterCandidate(criticalRuntimeSource2.Pid, criticalRuntimeSource2.Birth, AutoCores.NowTicks))
                    {
                        WantCriticalRoles(criticalRuntimeSource2, null);
                        observerRequested = true;
                    }
                }
                long nowTicks = AutoCores.NowTicks;
                if (nowTicks < criticalNextSample)
                {
                    return;
                }
                criticalNextSample = nowTicks + 50000000;
                if (criticalRuntimeSource2.Rule.Automatic && criticalSession != null && criticalPlan != null
                    && criticalPlan.WorkerMode == HardThreadWorkerMode.CandidatesOnly)
                {
                    AuditAutomaticCriticalThread(criticalRuntimeSource2);
                    return;
                }
                long sampleStarted = Stopwatch.GetTimestamp();
                ThreadTimes threadTimes = ProcessSnapshotSource.SampleThreads(criticalRuntimeSource2.Pid, criticalRuntimeSource2.Birth, 1500);
                bool observed = criticalObservation.Observe(threadTimes, criticalRuntimeSource2.Birth, AutoCores.NowTicks);
                TraceCriticalRuntime("sample", observed ? "ok" : "unavailable", criticalRuntimeSource2.Pid, sampleStarted);
                if (!observed)
                {
                    if (criticalSession != null && ++criticalObservationMisses < 3)
                    {
                        PublishCriticalThreadState(criticalRuntimeSource2, "coverage-pending", PerformancePolicy.Text("线程观测暂不可用，暂停新增与纠偏，等待重新核验", "Thread observations are temporarily unavailable; new bindings and corrections are paused until verification resumes"), false, false, 0, 0L);
                        return;
                    }
                    if (criticalSession != null) criticalStability.Block(criticalSource.Profile, criticalSource.Pid, criticalSource.Birth, criticalSource.Serial, criticalSource.RuleText);
                    if (WithdrawCriticalThreadsLocked("thread observation unavailable"))
                    {
                        PublishCriticalThreadState(criticalRuntimeSource2, "observing", PerformancePolicy.Text("线程观测暂不可用；未继续新增绑定", "Thread observations are unavailable; further bindings are stopped"), false, false, 0, 0L);
                    }
                }
                else if (criticalSession != null)
                {
                    AuditCriticalThreadSession(criticalRuntimeSource2, threadTimes);
                }
                else if (criticalRuntimeSource2.Rule.Automatic)
                {
                    if (!observerRequested && criticalObservation.Hotspots.Length > 0) WantCriticalRoles(criticalRuntimeSource2, threadTimes);
                    BeginAutomaticCriticalThreadSession(criticalRuntimeSource2, threadTimes);
                }
                else
                {
                    BeginCriticalThreadSession(criticalRuntimeSource2, threadTimes);
                }
            }
            catch (OperationCanceledException ex)
            {
                WithdrawCriticalThreadsLocked(ex.Message);
            }
            catch (HardThreadOperationException ex2)
            {
                if (ex2.Kind == HardThreadFailureKind.RecoveryPending)
                {
                    criticalHistoricalDebt = true;
                    criticalDebtNextRead = 0L;
                    WithdrawCriticalThreadsLocked(ex2.Message);
                    PublishCriticalThreadState(criticalSource, "recovery-pending", ex2.Message, false, true, 0, 0L);
                }
                else if (WithdrawCriticalThreadsLocked(string.Concat(ex2.Kind, ": ", ex2.Message)))
                {
                    PublishCriticalThreadState(criticalSource, "unsupported", ex2.Message, false, false, 0, 0L);
                }
            }
            catch (HardThreadStorageDomainException ex3)
            {
                if (WithdrawCriticalThreadsLocked("storage domain: " + ex3.Message))
                {
                    PublishCriticalThreadState(criticalSource, "unsupported", PerformancePolicy.Text("关键线程硬绑定需要本地数据目录；当前目录未获支持", "Hard thread placement requires a supported local data directory"), false, false, 0, 0L);
                }
                Logger.RepeatedError("critical-thread-storage-domain", ex3.Message, -1L);
                criticalNextSample = AutoCores.NowTicks + 300000000;
            }
            catch (Exception ex4)
            {
                if (HardThreadGuardianLaunchBlockedException.Contains(ex4))
                {
                    criticalGuardianLaunchBlocked = true;
                    bool withdrawCriticalThreadsLockedSucceeded = WithdrawCriticalThreadsLocked("independent guardian launch unavailable");
                    criticalObservation.Reset();
                    ResetCriticalAuto();
                    if (criticalSource != null && withdrawCriticalThreadsLockedSucceeded)
                    {
                        PublishCriticalThreadState(criticalSource, "unsupported", CriticalGuardianLaunchBlockedDetail(), false, false, 0, 0L);
                    }
                    Logger.RepeatedError("critical-thread-guardian-launch", "线程硬亲和性： " + ex4.Message, -1L);
                }
                else
                {
                    bool withdrawCriticalThreadsLockedSucceeded2 = WithdrawCriticalThreadsLocked(ex4.GetType().Name + ": " + ex4.Message);
                    if (criticalSource != null && withdrawCriticalThreadsLockedSucceeded2)
                    {
                        PublishCriticalThreadState(criticalSource, "unsupported", PerformancePolicy.Text("本次未应用：", "Not applied: ") + ex4.Message, false, false, 0, 0L);
                    }
                    Logger.RepeatedError("critical-thread-runtime", "线程硬亲和性： " + ex4.GetType().Name + ": " + ex4.Message, -1L);
                    criticalNextSample = AutoCores.NowTicks + 300000000;
                }
            }
            finally
            {
                SettleCriticalRoles();
                Monitor.Exit(criticalProcessGate);
            }
        }
        private static string CriticalGuardianLaunchBlockedDetail()
        {
            return PerformancePolicy.Text("Pavise 被启动它的程序放进了禁止脱离的作业（例如计划任务），独立恢复器无法启动；本次运行停止重试。退出 Pavise 后从桌面或开始菜单直接打开即可恢复", "Pavise was started inside a job that forbids breakaway (for example by Task Scheduler), so the independent recovery helper cannot start; retries are stopped for this run. Exit Pavise and open it directly from the desktop or Start menu to restore it.");
        }
        private void BeginCriticalThreadSession(CriticalRuntimeSource source, ThreadTimes snapshot)
        {
            CriticalThreadRoleFact[] roles = FrameCoordination.ReadCriticalRoles(source.Pid, source.Birth, snapshot, AutoCores.NowTicks);
            HardThreadChoice[] choices = CriticalThreadRuntimePolicy.Select(source.Rule, criticalObservation.Candidates, roles);
            if (choices.Length != source.Rule.CandidateCount)
            {
                PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("确认持续高负载线程后再绑定", "Binding waits for sustained CPU hotspot observations"), false, false, 0, 0L);
            }
            else
            {
                BindCriticalThreads(source, snapshot, source.Rule, choices, roles);
            }
        }
        private void BindCriticalThreads(CriticalRuntimeSource source, ThreadTimes snapshot, CriticalThreadRule rule, HardThreadChoice[] choices, CriticalThreadRoleFact[] roles)
        {
            string launchKey = "critical-launch:" + source.Serial + ":" + source.Profile + ":" + source.Pid + ":" + source.Birth + ":" + source.Rule.Encode();
            if (!AutomaticWorkAllowed(launchKey)) return;
            if (criticalSession == null && criticalPriorityReservation != null && !RestoreCriticalThreadsLocked()) return;
            criticalFailureTarget = source.Profile + "|" + source.Path;
            ActionFailureStore.BeginAutomaticCycle("game-critical-thread-hard-affinity", criticalFailureTarget,
                source.Serial + "\n" + source.Profile + ":" + source.Pid + ":" + source.Birth + ":" + source.Rule.Encode());
            if (ActionFailureStore.IsBlocked("game-critical-thread-hard-affinity", criticalFailureTarget))
            {
                PublishCriticalThreadState(source, "conflict", ActionFailureStore.FailureReason("game-critical-thread-hard-affinity", criticalFailureTarget), false, false, 0, 0L);
                return;
            }
            IntPtr intPtr = Native.OpenProcess(4096, false, source.Pid);
            if (intPtr == IntPtr.Zero)
            {
                RecordAutomaticWorkFailure(launchKey, "process-open-failed");
                PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text("无法核验该游戏的进程权限与身份", "The game's process identity and permissions cannot be verified"), false, false, 0, 0L);
                return;
            }
            bool launchAttempted = false;
            try
            {
                ulong mask;
                if (!Native.TryQueryAffinity(intPtr, out mask))
                {
                    RecordAutomaticWorkFailure(launchKey, "affinity-read-failed");
                    PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text("无法读取游戏进程的处理器亲和性；未施加线程硬绑定", "The game's processor affinity cannot be read; no hard thread binding is applied"), false, false, 0, 0L);
                    return;
                }
                BoostPass boostPass = new BoostPass();
                boostPass.RendererPid = source.Pid;
                boostPass.RendererCreation = source.Birth;
                boostPass.RendererName = source.Name;
                BoostPass pass = boostPass;
                CriticalThreadTargetLease criticalThreadTargetLease = CreateCriticalThreadTarget(pass, intPtr, rule, source.Profile, source.RuleText, mask, source.Epoch, source.Serial);
                if (criticalThreadTargetLease == null)
                {
                    PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("绑定前核对游戏身份与规则未通过；本次未绑定，稍后重新核对", "Game identity and rule checks before binding did not pass; nothing was bound and they will be checked again"), false, false, 0, 0L);
                    return;
                }
                string priorityReason;
                if (!TryReserveCriticalThreadPriority(source.Pid, source.Birth, choices.Select(x => x.Thread).ToArray(), out priorityReason))
                {
                    criticalOriginalTarget.Revoke();
                    RestoreCriticalThreadsLocked();
                    PublishCriticalThreadState(source, "recovery-pending", priorityReason, false, true, 0, 0L);
                    return;
                }
                launchAttempted = true;
                criticalSession = CriticalThreadHardAffinitySession.OpenNative(criticalThreadTargetLease);
                criticalRestorationConfirmed = false;
                criticalThreadResidue = true;
                criticalGuardianActor = criticalSession.GuardianActor;
                if (!ReleaseCriticalGuardianBackground())
                {
                    throw new InvalidOperationException("Guardian background restoration is unconfirmed");
                }
                criticalPlan = criticalSession.CreatePlan(choices, source.Rule.Automatic && source.Rule.WorkerAvoidance);
                criticalChoiceLabels = choices.Select(delegate(HardThreadChoice choice)
                {
                    CriticalThreadRoleFact criticalThreadRoleFact = roles.FirstOrDefault((CriticalThreadRoleFact x) => x.Tid == choice.Thread.Tid && x.Creation == choice.Thread.Creation);
                    return "TID " + choice.Thread.Tid + ((criticalThreadRoleFact == null) ? PerformancePolicy.Text(" · CPU 热点", " · CPU hotspot") : ((criticalThreadRoleFact.Role == "presentation-associated") ? PerformancePolicy.Text(" · 呈现关联", " · Presentation-associated") : PerformancePolicy.Text(" · 依赖关联", " · Dependency-associated")));
                }).ToArray();
                criticalChoiceFrameAssociated = CriticalThreadBoostCouplingPolicy.FrameAssociated(choices, roles);
                criticalWorkerBudgetReached = false;
                ThreadTimes admitted = ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 0);
                if (!CriticalThreadObservation.Valid(admitted, source.Birth, AutoCores.NowTicks) || choices.Any((HardThreadChoice x) => !CriticalThreadRuntimePolicy.Contains(admitted, x.Thread)))
                {
                    RestoreCriticalThreadsLocked();
                    criticalObservation.Reset();
                    LogCriticalThreadState("observing", "the snapshot after the guardian handshake was stale or lost a candidate");
                }
                else
                {
                    criticalThreadTargetLease.RequireActive();
                    ApplyCriticalThreadBatch(source, admitted, true);
                }
            }
            catch (Exception ex)
            {
                if (launchAttempted) RecordAutomaticWorkFailure(launchKey, ex.GetType().Name + ": " + ex.Message);
                throw;
            }
            finally
            {
                Native.CloseHandle(intPtr);
            }
        }
        private void AuditCriticalThreadSession(CriticalRuntimeSource source, ThreadTimes snapshot)
        {
            if (criticalPlan == null || criticalPlan.Choices.Any((HardThreadChoice x) => !CriticalThreadRuntimePolicy.Contains(snapshot, x.Thread)))
            {
                if (WithdrawCriticalThreadsLocked((criticalPlan == null) ? "no binding plan" : "a candidate thread is missing from the sample"))
                {
                    criticalObservation.Reset();
                    PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("原候选线程已退出；恢复后重新观察", "An original candidate exited; observing again after restoration"), false, false, 0, 0L);
                }
            }
            else
            {
                ApplyCriticalThreadBatch(source, snapshot, false);
            }
        }
        private void AuditAutomaticCriticalThread(CriticalRuntimeSource source)
        {
            long started = Stopwatch.GetTimestamp();
            string detail;
            HardThreadCandidateAudit result = criticalSession.AuditCandidates(out detail);
            Volatile.Write(ref criticalLastAuditDetail, result + ": " + detail);
            TraceCriticalRuntime("candidate-audit", result.ToString(), source.Pid, started);
            if (result == HardThreadCandidateAudit.Verified)
            {
                criticalObservationMisses = 0;
                string hintChanges = criticalIdealHints.Audit();
                if (hintChanges.Length != 0)
                {
                    Logger.Info("线程软放置核验： " + source.Name + " " + hintChanges + " 剩余=" + criticalIdealHints.Count);
                }
                PublishCriticalThreadState(source, "active", "", true, false, 0, AutoCores.NowTicks, CriticalThreadCoverageState.CandidatesOnly);
                return;
            }
            if (result == HardThreadCandidateAudit.Unavailable && ++criticalObservationMisses < 3)
            {
                PublishCriticalThreadState(source, "coverage-pending", PerformancePolicy.Text("原候选暂不可核验，保留已有绑定并暂停写入：", "The original candidate is temporarily unverifiable; the existing binding is retained and writes are paused: ") + detail, false, false, 0, 0L);
                return;
            }
            if (result == HardThreadCandidateAudit.Gone)
            {
                if (!WithdrawCriticalThreadsLocked("original candidate exited: " + detail)) return;
                bool blocked = criticalStability.RecordRediscovery(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
                criticalObservation.Reset();
                PublishCriticalThreadState(source, blocked ? "conflict" : "observing", blocked
                    ? PerformancePolicy.Text("候选线程反复更换，本局停止重新绑定", "Candidate threads changed repeatedly; rebinding is stopped for this session")
                    : PerformancePolicy.Text("原候选已确认退出，恢复后重新观察", "The original candidate has exited; observing again after restoration"), false, false, 0, 0L);
                return;
            }
            criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
            if (WithdrawCriticalThreadsLocked("automatic thread ownership yielded: " + detail))
                PublishCriticalThreadState(source, "conflict", result == HardThreadCandidateAudit.Changed
                    ? PerformancePolicy.Text("游戏已重新指定线程分配，已让出绑定，本局不再改回", "The game changed thread placement; the binding has been yielded and will not be reasserted this session")
                    : PerformancePolicy.Text("原候选连续无法核验，已恢复并停止本局重绑", "The original candidate could not be verified repeatedly; original settings were restored and rebinding is stopped for this session"), false, false, 0, 0L);
        }
        private void ApplyCriticalThreadBatch(CriticalRuntimeSource source, ThreadTimes snapshot, bool initial)
        {
            if (criticalStability == null) criticalStability = new CriticalThreadStabilityPolicy();
            criticalSession.BeforeReassertion = delegate
            {
                if (source.Rule.Automatic)
                {
                    criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
                    return false;
                }
                return !criticalStability.RecordReassertion(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
            };
            HardThreadOwnership[] activeReceipts = criticalSession.ActiveReceipts;
            HardThreadCoverage coverage;
            HardThreadIdentity[] batch = CriticalThreadRuntimePolicy.Batch(snapshot, criticalPlan, activeReceipts, out coverage);
            if (criticalWorkerBudgetReached)
            {
                batch = criticalPlan.Choices.Select((HardThreadChoice x) => x.Thread).ToArray();
                coverage = HardThreadCoverage.Partial;
            }
            HashSet<string> owned = new HashSet<string>(activeReceipts.Select((HardThreadOwnership x) => x.Thread.Key), StringComparer.Ordinal);
            if (batch.Any((HardThreadIdentity x) => !owned.Contains(x.Key)) && !criticalFailureAdmitted)
            {
                string reason;
                if (!ActionFailureStore.TryBeginApply("game-critical-thread-hard-affinity", criticalFailureTarget, out reason))
                {
                    RestoreCriticalThreadsLocked();
                    PublishCriticalThreadState(source, "conflict", reason, false, false, 0, 0L);
                    return;
                }
                criticalFailureAdmitted = true;
            }
            long nowTicks = AutoCores.NowTicks;
            long ticks = DateTime.UtcNow.Ticks;
            long batchStarted = Stopwatch.GetTimestamp();
            bool applySucceeded = (initial ? criticalSession.Apply(criticalPlan, batch, coverage) : criticalSession.AdmitNewThreads(batch, coverage));
            TraceCriticalRuntime("batch", applySucceeded ? "ok" : criticalSession.LastFailureKind.ToString(), source.Pid, batchStarted, criticalSession.LastOperationNativeAttempt);
            HardThreadFailureKind lastFailureKind = criticalSession.LastFailureKind;
            if (!applySucceeded && !criticalPlan.RequireWorkerCoverage && lastFailureKind == HardThreadFailureKind.InvalidRequest && !criticalSession.LastOperationWriteIntent && !criticalSession.LastOperationNativeAttempt && !initial)
            {
                criticalWorkerBudgetReached = true;
                PublishCriticalThreadState(source, "coverage-pending", PerformancePolicy.Text("保留原候选绑定；本局不再增加工作线程", "Original candidate bindings retained; no more worker bindings this session"), false, false, 0, 0L);
                return;
            }
            if (!applySucceeded)
            {
                string detail = criticalSession.Detail;
                if (criticalPlan.RequireWorkerCoverage)
                    detail += " [snapshotThreads=" + snapshot.Ids.Length + " batchThreads=" + batch.Length
                        + " requestedCoverage=" + coverage + " ownedBefore=" + activeReceipts.Length
                        + " unknownBirth=" + snapshot.Created.Count(birth => birth <= 0) + "]";
                if (criticalPlan.RequireWorkerCoverage)
                    criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
                if (initial && source.Rule.Automatic && lastFailureKind == HardThreadFailureKind.ObservationUnavailable)
                    criticalStability.RecordRediscovery(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
                if (lastFailureKind == HardThreadFailureKind.WriteOrDurabilityFault)
                {
                    ActionFailureStore.Trip("game-critical-thread-hard-affinity", criticalFailureTarget, detail);
                    criticalFailureAdmitted = false;
                }
                if (WithdrawCriticalThreadsLocked(string.Concat(initial ? "apply failed " : "worker admission failed ", lastFailureKind, ": ", detail)))
                {
                    criticalObservation.Reset();
                    criticalNextSample = AutoCores.NowTicks + 100000000;
                    PublishCriticalThreadState(source, (lastFailureKind == HardThreadFailureKind.WriteOrDurabilityFault) ? "conflict" : "observing", detail, false, false, 0, 0L);
                }
                return;
            }
            string lastReassertion = (initial ? null : criticalSession.LastReassertion);
            if (lastReassertion != null)
            {
                int criticalReassertionsSnapshot = ++criticalReassertions;
                if (criticalReassertionsSnapshot <= 3 || criticalReassertionsSnapshot % 20 == 0)
                {
                    LogCriticalThreadState("reasserted", lastReassertion + " (this session: " + criticalReassertionsSnapshot + ")");
                }
            }
            criticalOriginalTarget.RequireActive();
            int candidates;
            int workers;
            bool complete;
            ThreadTimes verifiedSnapshot = criticalPlan.RequireWorkerCoverage
                ? ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 0) : snapshot;
            if (!CriticalThreadObservation.Valid(verifiedSnapshot, source.Birth, AutoCores.NowTicks))
            {
                verifiedSnapshot = ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 1500);
                if (!CriticalThreadObservation.Valid(verifiedSnapshot, source.Birth, AutoCores.NowTicks))
                {
                    if (++criticalObservationMisses >= 3)
                    {
                        criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
                        if (WithdrawCriticalThreadsLocked("fresh thread verification repeatedly unavailable"))
                            PublishCriticalThreadState(source, "conflict", PerformancePolicy.Text("线程状态连续无法核验，已恢复原设置并停止本局重绑", "Thread state could not be verified repeatedly; original settings were restored and rebinding is stopped for this session"), false, false, 0, 0L);
                        return;
                    }
                    PublishCriticalThreadState(source, "coverage-pending", PerformancePolicy.Text("写入已回读，等待新的线程快照；暂停新增与纠偏", "Writes were read back; a fresh thread snapshot is pending and further bindings and corrections are paused"), false, false, 0, 0L);
                    return;
                }
            }
            bool verifiedSucceeded = CriticalThreadRuntimePolicy.Verified(verifiedSnapshot, criticalPlan, criticalSession.ActiveReceipts, ticks, out candidates, out workers, out complete);
            if (!verifiedSucceeded)
            {
                if (criticalPlan.RequireWorkerCoverage)
                    criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
                if (WithdrawCriticalThreadsLocked("thread coverage unconfirmed (candidates=" + candidates + "/" + criticalPlan.Choices.Length
                    + " 工作线程数=" + workers + " snapshotThreads=" + verifiedSnapshot.Ids.Length + " complete=" + complete + ")"))
                {
                    PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("绑定回读或采样时效未确认，已撤回", "Readback or sample freshness was not confirmed; bindings were withdrawn"), false, false, 0, 0L);
                }
                return;
            }
            criticalObservationMisses = 0;
            if (criticalPlan.RequireWorkerCoverage && !EnsureCriticalCoreIsolation(source, criticalPlan))
            {
                string isolationFailure = criticalCoreIsolationError;
                criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
                if (WithdrawCriticalThreadsLocked("critical core isolation unavailable: " + isolationFailure))
                    PublishCriticalThreadState(source, "conflict", PerformancePolicy.Text("核心隔离未确认，已撤回线程绑定：", "Core isolation was not confirmed; thread bindings were withdrawn: ") + isolationFailure, false, false, 0, 0L);
                return;
            }
            if (initial && criticalPlan.RequireWorkerCoverage)
                Logger.Info("线程整核隔离已核验： " + source.Name + " 进程编号=" + source.Pid
                    + " 物理核掩码=0x" + criticalCoreIsolationMask.ToString("X") + " 工作线程数=" + workers
                    + " 工作核掩码=0x" + criticalPlan.Workers.ToString("X")
                    + " 激活耗时毫秒=" + ((Stopwatch.GetTimestamp() - batchStarted) * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture)
                    + " 范围=本次快照；新线程等待准入；不隔离系统中断");
            if (criticalFailureAdmitted && (!source.Rule.WorkerAvoidance || complete))
            {
                if (!ActionFailureStore.CompleteApply("game-critical-thread-hard-affinity", criticalFailureTarget))
                {
                    RestoreCriticalThreadsLocked();
                    LogCriticalThreadState("withdrawn", "the apply completion could not be recorded");
                    return;
                }
                criticalFailureAdmitted = false;
            }
            CriticalThreadCoverageState coverage2 = ((!source.Rule.WorkerAvoidance) ? CriticalThreadCoverageState.CandidatesOnly : (complete ? CriticalThreadCoverageState.WorkersSnapshotComplete : CriticalThreadCoverageState.WorkersPartial));
            PublishCriticalThreadState(source, (source.Rule.WorkerAvoidance && !complete) ? "coverage-pending" : "active", "", true, false, workers, nowTicks, coverage2);
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
            return restored;
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
        private void PublishCriticalThreadState(CriticalRuntimeSource source, string state, string detail, bool applied = false, bool pending = false, int workers = 0, long observed = 0L, CriticalThreadCoverageState coverage = CriticalThreadCoverageState.None)
        {
            if (source == null)
            {
                return;
            }
            LogCriticalThreadState(state, detail);
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
            string detailOffset = state + "|" + detail;
            long nowTicks = AutoCores.NowTicks;
            lock (criticalStateLogGate)
            {
                if (criticalStateLogged == null || criticalStateLogged.Count > 64)
                {
                    criticalStateLogged = new Dictionary<string, long>(StringComparer.Ordinal);
                }
                long value;
                if (detailOffset == criticalStateLast || (criticalStateLogged.TryGetValue(detailOffset, out value) && nowTicks - value < 1200000000))
                {
                    return;
                }
                criticalStateLogged[detailOffset] = nowTicks;
                criticalStateLast = detailOffset;
            }
            Logger.Info("线程 [" + AutomationSession + "] 结果=" + state + (string.IsNullOrEmpty(detail) ? "" : (" 详情=" + detail)));
        }
        private CriticalThreadBoostCoupling CriticalThreadCoupling(int pid, long birth, bool applied, out int sameTid)
        {
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
