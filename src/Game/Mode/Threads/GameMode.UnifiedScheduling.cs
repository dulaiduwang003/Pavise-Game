using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class UnifiedSchedulingPhase
        {
            internal int InitialAttempts;
            internal long NextInitial;
            internal HardThreadIdentity[] Roles, PriorityRoles, ObservedRoles;
            internal string Evidence;
            internal bool ObservationOnly;
            // 自动模式已用理想处理器软放置关键角色 没有硬绑定会话 也不做运行期纠正
            internal bool SoftPlaced;
            // 无排队证据且尚未接管时 可在负载升高后使用剩余采集额度重判
            internal bool AwaitingLoad;
            // 独立核固定到此逻辑 CPU 提优清核时仅据此预留物理核 0 表示不改亲和性
            internal ulong RenderTarget;
            internal bool RenderClear;
            // 本局没能放置时的原因说明 供界面详情和日志使用
            internal string AffinityNote;
            // 新线程接纳仅失败时扣次数 预算归属原阶段 规则往返不补充
            internal int ClearCoreFailures;
            internal SchedulingLoadWatch LoadWatch;
            internal long NextLoadSample;
            internal UnifiedWorkloadSet Workload;
            internal UnifiedWorkloadPlan WorkloadPlan;
            internal int PlacementAdmissionAttempts;
            internal long NextPlacementAdmission;
            internal bool WorkloadReviewUsed, RenderReviewUsed, RoleExitPending, RoleReviewAllowed;
            internal UnifiedWorkloadSet PreviousWorkload;
            internal long WorkloadReviewAfter;
            internal readonly HardThreadWorkerAdmissionBudget WorkerAdmissionBudget = new HardThreadWorkerAdmissionBudget();
        }

        private bool StepUnifiedLoadWatch(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, ThreadLayoutCaptureRequest request,
            bool foreground, long now)
        {
            if (request == null || !request.RejudgeAvailable) { phase.AwaitingLoad = false; return false; }
            if (now < phase.NextLoadSample) return false;
            phase.NextLoadSample = now + TimeSpan.FromSeconds(15).Ticks;
            if (phase.LoadWatch == null) phase.LoadWatch = new SchedulingLoadWatch();
            long cpu = 0;
            bool known = foreground && UnifiedThreadRuntimeSampling.ReadProcessCpu(source.Pid, source.Birth, out cpu);
            if (!phase.LoadWatch.Observe(known, cpu, now)) return false;
            phase.AwaitingLoad = false;
            if (!request.TryRejudge(now)) return false;
            Logger.Info(Logger.Format("游戏调度 {0} PID={1} 负载比首次判定时明显升高并保持，使用剩余一次采集重新判定；{2}；此前未施加任何接管", "Game scheduling {0} PID={1} load rose clearly above the first decision and stayed there, using the one remaining capture to decide again; {2}; nothing had been taken over before", source.Name, source.Pid, phase.LoadWatch.Describe()));
            phase.Roles = phase.PriorityRoles = phase.ObservedRoles = null;
            phase.PreviousWorkload = phase.Workload;
            phase.WorkloadReviewAfter = now;
            phase.Workload = null;
            phase.WorkloadPlan = null;
            phase.ObservationOnly = false;
            phase.Evidence = null;
            criticalObservation.Reset();
            unifiedNextCpuSample = 0;
            return true;
        }

        private readonly Dictionary<string, UnifiedSchedulingPhase> unifiedSchedulingPhases = new Dictionary<string, UnifiedSchedulingPhase>(StringComparer.Ordinal);
        private long unifiedPhaseSerial, unifiedNextCpuSample, unifiedNextAudit, unifiedNextUiPublish, unifiedNextClearCoreScan;
        private int unifiedClearedWorkers, unifiedClearCoreQuickScans;
        private bool unifiedClearCoreCoverageKnown;
        private string unifiedClearCoreNote = "";
        private string unifiedLastState;
        internal static readonly long UnifiedHeartbeatTicks = TimeSpan.FromMinutes(10).Ticks;

        private readonly SchedulingDiagnosticLog unifiedStateLog = new SchedulingDiagnosticLog();
        // 每个游戏仅在内存保留最近一次放置前观察 供方案窗口对照 不参与准入
        private readonly Dictionary<string, SchedulingBottleneckEvidence> schedulingEvidence
            = new Dictionary<string, SchedulingBottleneckEvidence>(StringComparer.OrdinalIgnoreCase);

        private SchedulingBottleneckEvidence NoteSchedulingEvidence(CriticalRuntimeSource source, UnifiedThreadCaptureEvidence window)
        {
            SchedulingBottleneckEvidence observed = SchedulingBottleneckEvidence.From(window == null ? null : window.Relation,
                window == null ? null : window.Presenter, DateTime.Now, window != null && window.PresentIdentityOnly);
            lock (schedulingEvidence) schedulingEvidence[source.Profile] = observed;
            return observed;
        }

        internal SchedulingBottleneckEvidence LastSchedulingEvidence(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            SchedulingBottleneckEvidence observed;
            lock (schedulingEvidence) return schedulingEvidence.TryGetValue(profileId, out observed) ? observed : null;
        }

        private static HardThreadIdentity[] UnifiedObservedRoles(UnifiedSchedulingPhase phase)
        {
            if (phase.Roles == null) return null;
            if (phase.ObservedRoles == null)
                phase.ObservedRoles = phase.Roles.Concat(phase.PriorityRoles ?? new HardThreadIdentity[0])
                    .GroupBy(role => role.Key).Select(group => group.First()).ToArray();
            return phase.ObservedRoles;
        }

        private static string EffectiveGameSchedulingRaw(GameProfile profile)
        {
            CriticalThreadRule effective = PolicyResolver.GameSchedulingRule(profile);
            return effective == null ? null : effective.Encode();
        }

        private UnifiedSchedulingPhase UnifiedPhase(CriticalRuntimeSource source)
        {
            if (unifiedPhaseSerial != source.Serial)
            { unifiedSchedulingPhases.Clear(); unifiedPhaseSerial = source.Serial; }
            string key = source.Profile + "|" + source.Pid + "|" + source.Birth + "|" + source.Rule.Encode();
            UnifiedSchedulingPhase phase;
            if (!unifiedSchedulingPhases.TryGetValue(key, out phase))
                unifiedSchedulingPhases.Add(key, phase = new UnifiedSchedulingPhase());
            return phase;
        }

        // 会话循环已持有同一个进程门禁 控制 关闭和恢复不并发执行 也不靠新建控制器补充失败预算
        private bool StepUnifiedThreadRuntimeLocked(CriticalRuntimeSource source, string missing)
        {
            // 每轮重新判定是否仍在等角色采集 提前返回的分支都按不在等处理
            placementCaptureSource = null;
            try
            {
                if (criticalSource != null && !criticalSource.Same(source))
                {
                    if (!WithdrawCriticalThreadsLocked(source == null ? missing : criticalSource.Difference(source))) return true;
                    FinishCriticalDiagnosticLogs();
                    EndThreadLayoutDiagnostics(Logger.Text("统一调度原计划结束", "Original unified scheduling plan ended"), true);
                    criticalSource = null;
                    criticalObservation.Reset();
                    unifiedNextCpuSample = unifiedNextAudit = unifiedNextUiPublish = 0;
                    unifiedLastState = null;
                }
                if (source == null || source.Rule.IsOff)
                {
                    bool restored = WithdrawCriticalThreadsLocked(source == null ? missing : Logger.Text("用户关闭渲染线程亲和性", "User turned off render thread affinity"));
                    if (source != null) PublishUnifiedState(source, restored ? "off" : "recovery-pending",
                        restored ? Logger.Text("渲染线程亲和性已关闭，原设置恢复已核验", "Render thread affinity off, restore of original settings verified") : Logger.Text("已停止运行期写入，原设置恢复待确认", "Runtime writes stopped, restore of original settings awaiting confirmation"), false, !restored);
                    return true;
                }
                if (criticalSource == null)
                {
                    criticalSource = source;
                }
                // 撤权后的承载只做收尾 不参与维护或重新获得首次接管额度
                if (criticalOriginalTarget != null && criticalOriginalTarget.Revoked
                    || criticalSession == null && (criticalThreadResidue || criticalPriorityReservation != null))
                {
                    if (!RestoreCriticalThreadsLocked())
                    {
                        PublishUnifiedState(source, "recovery-pending", Logger.Text("原线程控制已撤销，等待原恢复归属及辅助资源结清；未新增接管", "Original thread control revoked, waiting for the original restore ownership and helper resources to settle; no new takeover"), false, true);
                        return true;
                    }
                }
                // 进程设置权限与线程设置权限独立 历史进程拒绝不替代本局线程身份 原值及实际 API 准入
                BeginThreadLayoutDiagnostics(source);
                UnifiedSchedulingPhase phase = UnifiedPhase(source);
                long now = AutoCores.NowTicks;
                ThreadLayoutCaptureRequest request = EnsureThreadLayoutCapture(source);
                // 亲和性还没选定角色 仍有采集额度时 线程提优通道先让出采集线程（见 PlacementCaptureFirst）
                if (phase.Roles == null && !phase.ObservationOnly && phase.InitialAttempts < 3
                    && request != null && !request.ObservationExhausted && criticalSession == null)
                    placementCaptureSource = source;
                bool foreground = !smartSwitchedOut && RendererForegroundPid() == source.Pid;
                if (request != null) request.SetForeground(foreground, now);
                if (phase.RoleExitPending)
                {
                    ReviewExitedRoles(source, phase, request, now);
                    return true;
                }
                if (phase.SoftPlaced && TickSoftPlacement(source, phase, request, now)) return true;
                // 在途观察长于核验周期 恢复结清后保活同一请求 节奏门禁不能把已开始的采集取消
                bool observingRequest = request != null && request.NeedsObserver(now) && foreground;
                if (observingRequest) WantCriticalRoles(source, null);
                if (phase.ObservationOnly)
                {
                    bool released = criticalSession == null || RestoreCriticalThreadsLocked();
                    if (released && phase.AwaitingLoad && StepUnifiedLoadWatch(source, phase, request, foreground, now))
                    {
                        PublishUnifiedState(source, "observing", Logger.Text("负载已明显升高，使用剩余一次采集重新判定；此前未施加任何接管", "Load has risen markedly, re-judging with the one remaining capture; no takeover was applied before"), false, false);
                        return true;
                    }
                    string watching = Logger.Text("调度观察；", "Scheduling observation; ") + phase.Evidence
                        + (released ? Logger.Text("；未新增线程控制，停止本阶段原基线的线程扫描", "; no new thread control, thread scanning for this phase's original baseline stopped") : Logger.Text("；已停止空控制计划，恢复归属尚未结清", "; empty control plan stopped, restore ownership not yet settled"))
                        + (released && phase.AwaitingLoad ? Logger.Text("；负载明显升高并保持一分钟后会用剩余一次采集重新判定", "; if load rises markedly and stays up for one minute, the one remaining capture is used to re-judge") : "");
                    bool readings = released && phase.AwaitingLoad && phase.LoadWatch != null;
                // 界面保留实时读数 日志仅在连续升高计数变化时记录
                    string watchKey = watching + (readings ? "|" + phase.LoadWatch.Streak : "");
                    PublishUnifiedState(source, released ? "observing" : "recovery-pending",
                        watching + (readings ? Logger.Text("；", "; ") + phase.LoadWatch.Describe() : ""), false, !released, watchKey);
                    return true;
                }
                if (criticalSession != null)
                {
                    if (now < unifiedNextAudit) return true;
                    unifiedNextAudit = now + TimeSpan.FromSeconds(2).Ticks;
                    // 时间关键优先级依赖原单核绑定 先纠正范围 再由控制器按实际绑定决定提优或撤回增量
                    bool maskKnown = !source.Rule.IsTimeCriticalRender || criticalPlan != null && criticalSession.MaintainApplied(now);
                    if (source.Rule.IsTimeCriticalRender && criticalPlan != null && criticalPlan.RequireWorkerCoverage
                        && (maskKnown || criticalSession.RuntimeAdmissionRequired))
                        AdmitClearCoreNewcomers(source, phase, now);
                    if (!source.Rule.IsTimeCriticalRender) maskKnown = criticalPlan == null || criticalSession.MaintainApplied(now);
                    if (criticalSession.ThreadHasExited)
                    {
                        // 重识别可以耗尽 旧角色退出后的恢复不能因此跳过 未知读值不构成退出证明
                        phase.RoleExitPending = true;
                        phase.RoleReviewAllowed = source.Rule.IsRenderStrategy ? !phase.RenderReviewUsed : !phase.WorkloadReviewUsed;
                        if (source.Rule.IsRenderStrategy) phase.RenderReviewUsed = true;
                        else phase.WorkloadReviewUsed = true;
                        ReviewExitedRoles(source, phase, request, now);
                        return true;
                    }
                    // 清核成员由新线程扫描统一接纳 本分支仅处理无清核的显式工作组
                    if (phase.WorkloadPlan != null && criticalPlan != null && !criticalPlan.RequireWorkerCoverage && criticalSession.RuntimeAdmissionRequired
                        && phase.PlacementAdmissionAttempts < 3 && now >= phase.NextPlacementAdmission && foreground)
                    {
                        phase.PlacementAdmissionAttempts++;
                        phase.NextPlacementAdmission = now + TimeSpan.FromSeconds(10).Ticks;
                        HardThreadIdentity[] members = criticalPlan.Choices.Concat(criticalPlan.WorkerPlacements)
                            .Select(choice => choice.Thread).ToArray();
                        // 原值已符合目标的成员尚无本方写入 后续首次接管仍消耗同一有限账本
                        criticalSession.AdmitNewThreads(members, criticalPlan.SelectedWorkersOnly
                            ? HardThreadCoverage.Partial : HardThreadCoverage.CandidatesOnly);
                        maskKnown = criticalSession.MaintainApplied(now);
                    }
                    if (!source.Rule.IsTimeCriticalRender && criticalPlan != null && criticalPlan.RequireWorkerCoverage) AdmitClearCoreNewcomers(source, phase, now);
                    string detail = Logger.Text("策略=", "strategy=") + StrategyName(source.Rule) + Logger.Text("；运行期纠偏=无限；原角色冻结", "; runtimeCorrection=unlimited; original roles frozen")
                        + (phase.WorkloadPlan == null ? "" : Logger.Text("；", "; ") + DescribeWorkloadPlan(phase))
                        + Logger.Text("；亲和性=", "; affinity=") + DescribeUnifiedAffinity(source, phase, maskKnown)
                        + Logger.Text("；未新增线程提优或优先核", "; no new thread boost or preferred core");
                    bool allKnown = maskKnown && criticalPlan != null;
                    if (request != null) request.ObserveIdentitySnapshot(allKnown, now);
                    PublishUnifiedState(source, allKnown ? "active" : criticalPlan != null ? "conflict" : "observing", detail, allKnown, false);
                    return true;
                }
                if (CriticalThreadHasRecoveryDebt(false))
                {
                    PublishUnifiedState(source, "recovery-pending", Logger.Text("旧线程恢复记录尚未结清，未新增控制", "Old thread restore records not yet settled, no new control"), false, true);
                    return true;
                }
                if (smartSwitchedOut || RendererForegroundPid() != source.Pid)
                {
                    criticalObservation.MarkPaused();
                    PublishUnifiedState(source, "observing", Logger.Text("等待游戏返回前台；既有规则与采样次数保留", "Waiting for the game to return to the foreground; existing rules and capture counts retained"), false, false);
                    return true;
                }
                if (now < unifiedNextCpuSample) return true;
                if (phase.Roles == null && request == null)
                {
                    PublishUnifiedState(source, "observing", threadLayoutSkipReason ?? Logger.Text("本局无法建立有界角色观察；未启动线程扫描或新增写入", "Bounded role observation cannot be set up this session; no thread scan started and no new writes"), false, false);
                    return true;
                }
                if (phase.Roles == null && request != null && !request.HasUsableBaseline
                    && (request.ObservationExhausted || now < request.NextCaptureDueTicks))
                {
                    PublishUnifiedState(source, "observing", request.DescribeState(now)
                        + Logger.Text("；等待期间不枚举线程，观察额度耗尽后停止角色采样", "; threads are not enumerated while waiting, role capture stops once the observation budget is used up"), false, false);
                    return true;
                }
                if (phase.Roles != null && (phase.InitialAttempts >= 3 || now < phase.NextInitial))
                {
                    if (phase.InitialAttempts >= 3) PublishUnifiedState(source, "conflict", Logger.Text("首次接管次数已用完；本局停止新增接管，保留恢复归属", "Initial acquisition attempts used up; no new takeovers this session, restore ownership retained"), false, false);
                    return true;
                }
                // 失败窗口仅留作诊断 不能用其过期时间挡住冷却后尚余的身份确认与采集次数
                UnifiedThreadCaptureEvidence admittedBaseline = request == null || !request.HasUsableBaseline ? null : request.ReadUnifiedBaseline();
                if (phase.Roles == null && admittedBaseline != null && now - admittedBaseline.Observed > TimeSpan.FromSeconds(65).Ticks)
                {
                    PublishUnifiedState(source, "observing", Logger.Text("角色证据已超过准入新鲜度；停止原基线的线程扫描，不追加采集额度", "Role evidence is past admission freshness; thread scanning for the original baseline stopped, no extra capture budget"), false, false);
                    return true;
                }
                unifiedNextCpuSample = now + TimeSpan.FromSeconds(5).Ticks;
            // 旁观线程退出不影响已冻结角色的准入
                ThreadTimes snapshot = phase.Roles == null || phase.Workload != null && !source.Rule.IsDependencyBoost
                    ? ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 1500)
                    : UnifiedThreadRuntimeSampling.Read(source.Pid, source.Birth, UnifiedObservedRoles(phase));
            // 新鲜度以取完快照后的时刻核验 避免使用本轮起点误判新快照
                long sampledAt = AutoCores.NowTicks;
                bool snapshotKnown = CriticalThreadObservation.Valid(snapshot, source.Birth, sampledAt);
                if (request != null) request.ObserveIdentitySnapshot(snapshotKnown, now);
                if (!observingRequest && request != null && request.NeedsObserver(now)) WantCriticalRoles(source, snapshot);
                if (!snapshotKnown)
                {
                    PublishUnifiedState(source, "observing", Logger.Text("线程身份或 CPU 快照暂不可核验；未新增写入", "Thread identity or CPU snapshot cannot be verified yet; no new writes"), false, false);
                    return true;
                }
                if (phase.Roles == null)
                {
                    if (source.Rule.UsesWorkloadDiscovery)
                    {
                        if (!PrepareWorkloadRoles(source, phase, request, snapshot, sampledAt)) return true;
                    }
                    else
                    {
                    string reason;
                    UnifiedThreadCaptureEvidence baseline = request == null ? null : request.ReadUnifiedBaseline();
                    HardThreadIdentity[] roles = UnifiedThreadAdmissionPolicy.Select(baseline, snapshot, null,
                        source.Pid, source.Birth, sampledAt, false, out reason);
                    if (roles.Length == 0)
                    {
                        PublishUnifiedState(source, "observing", reason + Logger.Text("；普通高负载及线程池保留原优先级和运行范围", "; ordinary high-load threads and thread pools keep their original priority and running range"), false, false);
                        return true;
                    }
                    phase.Roles = roles;
                    // 这里不预留也不写线程优先级
                    phase.PriorityRoles = new HardThreadIdentity[0];
                    phase.Evidence = reason + Logger.Text("；", "; ") + (source.Rule.IsManualPin
                        ? Logger.Text("手动亲和性：渲染线程固定到所选逻辑 CPU，只改核心范围，不改优先级和进程类别", "Manual affinity: render thread pinned to the selected logical CPU, only the core range changes, priority and process class unchanged")
                        : Logger.Text("渲染单核：自动选出最合适的物理核固定渲染线程，只改核心范围，不改优先级和进程类别", "Single render core: the most suitable physical core is chosen automatically to pin the render thread, only the core range changes, priority and process class unchanged"));
                    PrepareRenderAffinity(source, phase, baseline);
                    if (!string.IsNullOrEmpty(phase.AffinityNote)) phase.Evidence += Logger.Text("；", "; ") + phase.AffinityNote;
                    phase.AwaitingLoad = false;
                    // 选不出可用核心时没有任何可做的放置 本阶段只观察
                    phase.ObservationOnly = phase.RenderTarget == 0;
                    Logger.Info(Logger.Format("游戏调度 角色准入： {0} PID={1} 创建={2} 规则={3} 原规则={4} 角色={5} 目标CPU={6} 清核={7}；{8}；{9}；选择证据不作为性能收益证据", "Game scheduling role admission: {0} PID={1} birth={2} rule={3} originalRule={4} roles={5} targetCpu={6} coreClearing={7}; {8}; {9}; selection evidence is not evidence of a performance gain", source.Name, source.Pid, source.Birth, source.Rule.Encode(), source.RuleText, string.Join(",", roles.Select(role => role.Key).ToArray()), (phase.RenderTarget == 0 ? Logger.Text("未改亲和性", "affinity unchanged") : TargetProcessors(phase.RenderTarget)), (phase.RenderClear ? Logger.Text("开", "on") : Logger.Text("关", "off")), phase.Evidence, NoteSchedulingEvidence(source, baseline).Describe()));
                    }
                }
                if (phase.ObservationOnly)
                {
                    PublishUnifiedState(source, "observing", Logger.Text("本阶段接管条件未满足；未创建线程控制；", "Takeover conditions not met for this phase; no thread control created; ") + phase.Evidence, false, false);
                    return true;
                }
                if (phase.InitialAttempts >= 3 || now < phase.NextInitial)
                {
                    PublishUnifiedState(source, "conflict", Logger.Text("首次接管尚未完成，当前阶段尝试=", "Initial acquisition not yet complete, current phase attempt=") + phase.InitialAttempts
                        + Logger.Text("/3；首次准入与恢复预算有界，运行期已接管字段纠偏不限次数", "/3; initial admission and restore budgets are bounded, runtime correction of managed fields is unlimited"), false, false);
                    return true;
                }
                if (phase.Roles.Any(role => !CriticalThreadRuntimePolicy.Contains(snapshot, role))
                    || phase.PriorityRoles.Any(role => !CriticalThreadRuntimePolicy.Contains(snapshot, role)))
                {
                    phase.InitialAttempts = 3;
                    PublishUnifiedState(source, "observing", Logger.Text("原准入身份已退出；本局不凭新线程号重选或追加采样", "Originally admitted identities have exited; no reselection or extra capture by new thread IDs this session"), false, false);
                    return true;
                }
                phase.InitialAttempts++;
                phase.NextInitial = now + TimeSpan.FromSeconds(10).Ticks;
                StartUnifiedThreadControl(source, snapshot, phase);
            }
            catch (Exception error)
            {
                // 会话启动失败也只计本阶段已实际执行的尝试 退避 恢复债和三次上限沿用同一账本
                // 已经接管后的异常不整组撤回 关闭或退局仍由原恢复门禁结算
                if (source != null) PublishUnifiedState(source, "conflict", Logger.Text("统一调度本轮未能核验：", "Unified scheduling could not be verified this round: ") + error.GetType().Name
                    + " " + error.Message + Logger.Text("；保留规则与恢复记录", "; rules and restore records retained"), false, false);
                Logger.RepeatedError("unified-game-scheduling", Logger.Format("游戏调度核验未完成： {0}", "Game scheduling verification did not complete: {0}", error.Message));
            }
            return true;
        }

        // 自动模式只做软放置 每个关键角色把理想处理器指向为它选出的物理核 所选核忙时调度器照样把它挪走
        // 不改任何线程的亲和性 不建立硬绑定会话 不写恢复回执 也不做运行期纠正 游戏或别的程序改了理想处理器就放手
        // 后台治理仍避开这些物理核 让关键角色想去的核尽量空着
        private void StartSoftPlacement(CriticalRuntimeSource source, UnifiedSchedulingPhase phase)
        {
            HardThreadChoice[] choices = phase.WorkloadPlan == null || phase.WorkloadPlan.Primary == null
                ? new HardThreadChoice[0] : phase.WorkloadPlan.Choices;
            // 理想处理器只能指向一个逻辑 CPU 取所选范围里编号最小的那个
            HardThreadChoice[] hints = choices.Where(choice => choice != null && choice.Mask != 0)
                .Select(choice => new HardThreadChoice(choice.Thread, choice.Mask & (~choice.Mask + 1))).ToArray();
            if (hints.Length == 0)
            {
                phase.ObservationOnly = true;
                phase.Evidence += Logger.Text("；本局没有为关键角色选出物理核，未新增放置", "; no physical core chosen for critical roles this session, no new placement");
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return;
            }
            string applied = criticalIdealHints.Apply(source.Pid, source.Birth, hints);
            if (criticalIdealHints.Count == 0)
            {
                phase.ObservationOnly = true;
                phase.Evidence += Logger.Text("；理想处理器提示没有写入（", "; ideal processor hints not written (") + applied + Logger.Text("），本局未新增放置", "), no new placement this session");
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return;
            }
            phase.SoftPlaced = true;
            // 只为真正写上（或原本就对）的提示留核 写失败的角色不占后台核心
            PublishSoftCoreReservation(source, criticalIdealHints.TargetMask);
            if (threadLayoutCapture != null) threadLayoutCapture.MarkApplied(AutoCores.NowTicks);
            unifiedNextAudit = AutoCores.NowTicks + TimeSpan.FromSeconds(SoftPlacementAuditSeconds).Ticks;
            Logger.Info(Logger.Format("游戏调度 软放置 {0} PID={1}；{2}；{3}；只设理想处理器，不改亲和性，调度器可随时迁移；后台避开这些物理核；退局恢复仍是本方写入的值", "Game scheduling soft placement {0} PID={1}; {2}; {3}; only ideal processors set, affinity unchanged, the scheduler may migrate at any time; background avoids these physical cores; exit restore still uses the values we wrote", source.Name, source.Pid, DescribeWorkloadPlan(phase), applied));
            PublishUnifiedState(source, "active", Logger.Text("自动（软放置）：", "Automatic (soft placement): ") + applied + Logger.Text("；不限制任何线程的运行范围", "; no thread's running range is restricted"), true, false);
        }

        // 软放置只读核对的间隔 提示被改或线程退出就放手 不重新写入
        private const int SoftPlacementAuditSeconds = 10;

        // 返回 false 表示本阶段的提示已在别处撤回（换配置后又切回 恢复流程） 交回普通路径按剩余首次放置次数重新放置
        private bool TickSoftPlacement(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, ThreadLayoutCaptureRequest request, long now)
        {
            if (criticalIdealHints.Count == 0)
            {
                phase.SoftPlaced = false;
                return false;
            }
            if (now < unifiedNextAudit) return true;
            unifiedNextAudit = now + TimeSpan.FromSeconds(SoftPlacementAuditSeconds).Ticks;
            int[] exited;
            string audit = criticalIdealHints.Audit(out exited);
            if (audit.Length != 0) Logger.Info(Logger.Format("游戏调度 软放置核对 {0}：{1}；已放手，不重新写入", "Game scheduling soft placement check {0}: {1}; released, not written again", source.Name, audit));
            // 关键角色的线程退出 多半是游戏换图或重建了渲染器 放下其余提示 用本阶段剩余的一次复核重新找角色
            if (exited.Length != 0)
            {
                phase.SoftPlaced = false;
                phase.RoleExitPending = true;
                phase.RoleReviewAllowed = !phase.WorkloadReviewUsed;
                phase.WorkloadReviewUsed = true;
                ReviewExitedRoles(source, phase, request, now);
                return true;
            }
            // 游戏自己改了理想处理器就交给游戏 全部被改后本局不再放置
            if (criticalIdealHints.Count == 0)
            {
                phase.SoftPlaced = false;
                phase.ObservationOnly = true;
                ClearSoftCoreReservation();
                phase.Evidence += Logger.Text("；软放置的角色已退出或理想处理器已被改动，本局不再接管", "; soft-placed roles exited or their ideal processor was changed, no further takeover this session");
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return true;
            }
            // 放手了一部分 后台预留缩到仍有效的提示所在的核
            if (audit.Length != 0)
            {
                ulong remaining = criticalIdealHints.TargetMask;
                PublishSoftCoreReservation(source, remaining);
            }
            PublishUnifiedState(source, "active", Logger.Text("自动（软放置）：仍有效的理想处理器提示=", "Automatic (soft placement): validIdealProcessorHints=") + criticalIdealHints.Count
                + Logger.Text("；不限制任何线程的运行范围；每 ", "; no thread's running range is restricted; read-only check every ") + SoftPlacementAuditSeconds + Logger.Text(" 秒只读核对一次", " seconds"), true, false);
            return true;
        }

        private void StartUnifiedThreadControl(CriticalRuntimeSource source, ThreadTimes snapshot, UnifiedSchedulingPhase phase)
        {
            if (source.Rule.IsOrchestration)
            {
                StartSoftPlacement(source, phase);
                return;
            }
            // 线程优先级和进程类别分别归游戏线程提优和性能优先级
            // 这里不预留线程优先级 不归还进程提优 也不建立线程属性控制
            HardThreadIdentity[] reserved = phase.Roles.GroupBy(role => role.Key).Select(group => group.First()).ToArray();
            IntPtr process = Native.OpenProcess(0x1000, false, source.Pid);
            if (process == IntPtr.Zero) throw new InvalidOperationException(Logger.Text("游戏进程查询句柄不可用", "Game process query handle unavailable"));
            try
            {
                ulong mask = 0;
                if (!source.Rule.IsDependencyBoost && (!Native.TryQueryAffinity(process, out mask) || mask == 0))
                    throw new InvalidOperationException(Logger.Text("游戏当前允许范围不可核验", "Game's current allowed range cannot be verified"));
                StartThreadLayoutDiagnostics(source, mask);
                var pass = new BoostPass { RendererPid = source.Pid, RendererCreation = source.Birth, RendererName = source.Name };
                // 纯提优计划独立于选核资格 依赖组仅按线程身份授权
                CriticalThreadTargetLease target = CreateCriticalThreadTarget(pass, process, source.Rule, source.Profile, source.RuleText,
                    mask, source.Epoch, source.Serial, source.Rule.IsDependencyBoost || source.Rule.IsRenderStrategy && phase.RenderTarget == 0);
                if (target == null) throw new InvalidOperationException(Logger.Text("游戏身份、范围或当前用户规则未通过写入授权核验", "Game identity, range or current user rule failed write authorization verification"));
                criticalSession = CriticalThreadHardAffinitySession.OpenNative(target);
                criticalSession.UseWorkerAdmissionBudget(phase.WorkerAdmissionBudget);
                criticalRestorationConfirmed = false;
                criticalThreadResidue = true;
                ThreadTimes fresh = UnifiedThreadRuntimeSampling.Read(source.Pid, source.Birth, reserved);
                if (!CriticalThreadObservation.Valid(fresh, source.Birth, AutoCores.NowTicks)
                    || reserved.Any(role => !CriticalThreadRuntimePolicy.Contains(fresh, role)))
                    throw new InvalidOperationException(Logger.Text("会话建立后角色身份或新鲜快照未确认", "Role identity or fresh snapshot not confirmed after the session was set up"));
                unifiedClearedWorkers = 0;
                unifiedClearCoreCoverageKnown = false;
                unifiedClearCoreNote = "";
                // 自动模式在入口就转去软放置 这里只剩渲染单核的硬固定
                if (phase.RenderTarget != 0)
                {
                    HardThreadChoice[] choices = { new HardThreadChoice(phase.Roles[0], phase.RenderTarget) };
                    // 提优清核不改渲染线程范围 仅预留其物理核并设置理想处理器
                    if (phase.RenderClear) ApplyClearCore(source, choices, !source.Rule.PinsRenderThread);
                    else
                    {
                        criticalPlan = criticalSession.CreatePlan(choices, false, false, null, true);
                        if (!criticalSession.Apply(criticalPlan, new[] { phase.Roles[0] }, HardThreadCoverage.CandidatesOnly))
                            throw new InvalidOperationException(Logger.Text("渲染线程首次固定未核验：", "Initial render thread pin not verified: ") + criticalSession.Detail);
                    }
                }
                PublishBackgroundCoreReservation(source);
                // 没有建立任何放置时不保留空会话
                if (criticalPlan == null)
                {
                    phase.ObservationOnly = true;
                    phase.Evidence += Logger.Text("；本局没有可固定或可避让的核心，未新增放置", "; no core to pin or avoid this session, no new placement");
                    bool released = RestoreCriticalThreadsLocked();
                    PublishUnifiedState(source, released ? "observing" : "recovery-pending", phase.Evidence, false, !released);
                    return;
                }
                if (threadLayoutCapture != null) threadLayoutCapture.MarkApplied(AutoCores.NowTicks);
                unifiedNextAudit = 0;
                PublishUnifiedState(source, "coverage-pending", Logger.Text("角色已冻结；亲和性逐线程核验；", "Roles frozen; affinity verified per thread; ") + phase.Evidence
                    + Logger.Text("；已接管字段运行期持续纠偏，未新增 ETW 或角色重选次数", "; managed fields are continuously corrected at runtime, no extra ETW or role reselection attempts"), false, false);
            }
            catch
            {
                // 首次接管失败沿用同一有限阶段 先结算在途写入与恢复 不能丢掉对象再新建补次数
                if (RestoreCriticalThreadsLocked()) criticalPlan = null;
                throw;
            }
            finally { Native.CloseHandle(process); }
        }

        // 日志里的目标 CPU 单个写成 CPU2 手动多选写成 CPU2,4 这样的列表
        private static string TargetProcessors(ulong mask)
        {
            return (mask & (mask - 1)) == 0 ? CriticalThreadRule.ProcessorName(mask) : "CPU" + CpuTopology.DescribeRanges(mask);
        }

        private static string StrategyName(CriticalThreadRule rule)
        {
            // 运行副本只会是自动 渲染单核或手动
            string name = rule.IsManualPin ? Logger.Text("手动亲和性", "Manual affinity") : rule.PinsRenderThread ? Logger.Text("渲染单核", "Single render core") : rule.IsOrchestration ? Logger.Text("自动（软放置）", "Automatic (soft placement)")
                : rule.IsTimeCriticalRender ? Logger.Text("独立核时间关键", "Dedicated-core time-critical") : rule.IsDependencyBoost ? Logger.Text("依赖组提优", "Dependency group boost") : rule.IsGamePriority ? Logger.Text("游戏优先", "Game priority") : rule.IsRenderStrategy ? Logger.Text("渲染线程提优", "Render thread boost") : Logger.Text("主副核编排", "Primary/secondary core orchestration");
            return name + (rule.ClearCore && !rule.IsOrchestration ? Logger.Text("+整核避让", "+whole-core avoidance") : "");
        }

        // 固定目标的位数 只有手动可以是多个逻辑 CPU 而且是 1 到 4 个 渲染单核选出来的必须是单个 CPU
        // 硬绑定计划本身接受多位候选 这里把住只有手动多选才会交出多位目标 返回空表示合规
        private static string RenderTargetShapeError(CriticalThreadRule rule, ulong target)
        {
            int count = AutoCores.CountBits(target);
            if (rule.IsManualPin)
                return count >= 1 && count <= CriticalThreadAutoPolicy.MaximumIsolated ? null
                    : Logger.Text("手动所选 CPU 有 ", "Manual selection has ") + count + Logger.Text(" 个，只支持 1 到 ", " CPUs; only 1 to ") + CriticalThreadAutoPolicy.MaximumIsolated + Logger.Text(" 个", " are supported");
            return count == 1 ? null : Logger.Text("渲染单核选出的目标不是单个 CPU（0x", "the render single-core target is not a single CPU (0x") + target.ToString("X") + Logger.Text("）", ")");
        }

        // 渲染单核与手动的选核 渲染单核选一颗最合适的物理核 手动核验所选 CPU 仍在游戏范围内
        // 只在准入时选一次 选不出就本局不放置 原因写进 AffinityNote
        private void PrepareRenderAffinity(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, UnifiedThreadCaptureEvidence baseline)
        {
            phase.RenderTarget = 0;
            phase.RenderClear = false;
            phase.AffinityNote = null;
            if (!source.Rule.PinsRenderThread && !source.Rule.ClearCore) return;
            string skipped = source.Rule.PinsRenderThread ? Logger.Text("本局不固定核心", "no core pinned this session") + (source.Rule.ClearCore ? Logger.Text("、不清核", ", no core clearing") : "") : Logger.Text("本局不清核", "no core clearing this session");
            if (CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
            { phase.AffinityNote = Logger.Text("处理器拓扑不可核验，", "Processor topology cannot be verified, ") + skipped; return; }
            ulong mask;
            if (!ReadCriticalProcessMask(source.Pid, source.Birth, out mask) || mask == 0)
            { phase.AffinityNote = Logger.Text("游戏当前进程范围不可核验，", "Game's current process range cannot be verified, ") + skipped; return; }
            CriticalThreadAllowedRange scope;
            lock (sync) scope = CriticalThreadAllowedRange.For(CriticalPreferenceProfileLocked(source.Profile));
            if (scope.Error != null)
            { phase.AffinityNote = Logger.Text("游戏核心范围未确认（", "Game core range not confirmed (") + scope.Error + Logger.Text("），", "), ") + skipped; return; }
            ulong[] physical = CpuTopology.PhysicalCoreMasks();
            ulong reserved = AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, physical);
            ulong allowed = mask & scope.Mask & ~reserved;
            string reason;
            ulong target;
            if (source.Rule.IsManualPin)
            {
                // 手动选核只在保存时的处理器拓扑上有效 所选 CPU 必须都在游戏当前可用 且未被其他调度占用的范围内
                ulong chosen = source.Rule.ManualMask;
                if (source.Rule.Topology != ManualCorePlan.Stamp(CpuTopology.AllMask, physical))
                { phase.AffinityNote = Logger.Text("处理器拓扑已变化，手动选核作废，请重新选择；", "Processor topology changed, manual core selection is void, please choose again; ") + skipped; return; }
                string shape = RenderTargetShapeError(source.Rule, chosen);
                if (shape != null) { phase.AffinityNote = shape + Logger.Text("，", ", ") + skipped; return; }
                if (chosen == 0 || (chosen & allowed) != chosen)
                { phase.AffinityNote = Logger.Text("所选 CPU 不在游戏当前可用范围内或已被其他调度占用，", "Selected CPUs are outside the game's current usable range or taken by other scheduling, ") + skipped; return; }
                target = chosen;
            }
            else
            {
                target = RenderIsolationPlan.Choose(allowed, physical, CpuTopology.PerfMask, CpuTopology.CriticalPlacementPreferredMask,
                    baseline == null || baseline.Relation == null ? null : baseline.Relation.Presenter, out reason);
                if (target == 0)
                { phase.AffinityNote = Logger.Text("未选出可用核心（", "No usable core chosen (") + reason + Logger.Text("），", "), ") + skipped; return; }
                string shape = RenderTargetShapeError(source.Rule, target);
                if (shape != null) { phase.AffinityNote = shape + Logger.Text("，", ", ") + skipped; return; }
            }
            // 整核避让以物理核为单位 所选逻辑 CPU 所在的每颗物理核都留给渲染线程
            ulong core = physical.Where(item => (item & target) != 0).Aggregate(0UL, (all, item) => all | item);
            bool room = RenderIsolationPlan.LeavesRoomForOthers(allowed, physical, core);
            phase.RenderClear = source.Rule.ClearCore && room;
            if (source.Rule.ClearCore && !room) phase.AffinityNote = Logger.Text("整核避让后其他线程不足两颗物理核，本局不避让", "Whole-core avoidance would leave other threads fewer than two physical cores, no avoidance this session");
            // 提优策略仅在可清核时预留核心
            phase.RenderTarget = source.Rule.PinsRenderThread || phase.RenderClear ? target : 0;
        }

        // 先收紧其他游戏线程的原范围 让出目标物理核及 SMT 同核线程 再安置关键角色
        // 不可读或无法避让的线程记为覆盖不足 批次受剩余日志容量限制 后续扫描补齐
        private void ApplyClearCore(CriticalRuntimeSource source, HardThreadChoice[] choices, bool floating)
        {
            criticalPlan = criticalSession.CreatePlan(choices, true, true, null, false, false, floating);
            ThreadTimes all = ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 0);
            if (!CriticalThreadObservation.Valid(all, source.Birth, AutoCores.NowTicks))
                throw new InvalidOperationException(Logger.Text("清核需要可核验的线程快照", "Core clearing needs a verifiable thread snapshot"));
            HardThreadCoverage coverage;
            int uncovered;
            HardThreadIdentity[] batch = ClearCoreBatch(all, criticalPlan, new HashSet<string>(StringComparer.Ordinal),
                criticalSession.JournalRowCount, out coverage, out uncovered);
            if (batch.Length == 0) throw new InvalidOperationException(Logger.Text("清核快照缺少关键角色或线程身份重复", "Core clearing snapshot lacks critical roles or has duplicate thread identities"));
            if (!criticalSession.Apply(criticalPlan, batch, coverage))
                throw new InvalidOperationException(Logger.Text("清核首次范围未核验：", "Initial core clearing range not verified: ") + criticalSession.Detail);
            RefreshClearCoreNote(all, uncovered);
            unifiedClearCoreQuickScans = 0;
            ScheduleClearCoreScan(AutoCores.NowTicks, uncovered);
            Logger.Log(() => Logger.Format("游戏调度 {0}{1}{2}{3}，其余 {4} 个线程让出所在物理核，可用范围=0x{5}{6}；退局逐线程恢复原值", "Game scheduling {0}{1}{2}{3}, the other {4} threads yield their physical cores, allowedMask=0x{5}{6}; exit restores original values per thread", source.Name, (source.Rule.IsTimeCriticalRender && !unifiedClearCoreCoverageKnown
                ? Logger.Text(" 清核覆盖尚未完整核验，时间关键提优未启用：", " core-clearing coverage not fully verified, time-critical boost not enabled: ") : Logger.Text(" 清核已生效：", " core clearing active: ")), (floating ? Logger.Text("渲染线程保持原范围，理想处理器指向 ", "render thread keeps its original range, ideal processor set to ") : Logger.Text("关键角色固定到 ", "critical roles pinned to ")), string.Join(Logger.Text("、", ", "), choices.Select(choice => TargetProcessors(choice.Mask)).ToArray()), unifiedClearedWorkers, criticalPlan.Workers.ToString("X"), unifiedClearCoreNote));
        }

        // 批内须包含关键角色 线程数与日志行数合计不超过 512 时整批提交
        // 超限时按剩余行数提交关键角色和未覆盖线程 其余留待后续扫描
        private static HardThreadIdentity[] ClearCoreBatch(ThreadTimes sample, HardThreadPlan plan, HashSet<string> covered,
            int journalRows, out HardThreadCoverage coverage, out int uncovered)
        {
            coverage = HardThreadCoverage.Partial;
            uncovered = 0;
            if (sample == null || !CoreThreadTracker.ValidShape(sample)) return new HardThreadIdentity[0];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new List<HardThreadIdentity>();
            var known = new List<HardThreadIdentity>();
            var fresh = new List<HardThreadIdentity>();
            for (int index = 0; index < sample.Ids.Length; index++)
            {
                if (sample.Ids[index] <= 0 || sample.Ids[index] > int.MaxValue || sample.Created[index] <= 0) continue;
                var thread = new HardThreadIdentity((int)sample.Ids[index], sample.Created[index]);
                if (!seen.Add(thread.Key)) return new HardThreadIdentity[0];
                if (plan.IsCandidate(thread)) candidates.Add(thread);
                else if (covered.Contains(thread.Key)) known.Add(thread);
                else fresh.Add(thread);
            }
            if (candidates.Count != plan.Choices.Length) return new HardThreadIdentity[0];
            if (journalRows + seen.Count <= 512)
            {
                coverage = seen.Count == sample.Ids.Length ? HardThreadCoverage.Complete : HardThreadCoverage.Partial;
                return known.Concat(fresh).Concat(candidates).ToArray();
            }
            int room = Math.Max(0, 512 - journalRows - candidates.Count);
            uncovered = Math.Max(0, fresh.Count - room);
            return fresh.Take(room).Concat(candidates).ToArray();
        }

        private void RefreshClearCoreNote(ThreadTimes sample, int uncovered)
        {
            HardThreadOwnership[] receipts = criticalSession.ActiveReceipts;
            unifiedClearedWorkers = Math.Max(0, receipts.Length - (criticalPlan == null ? 0 : criticalPlan.Choices.Length));
            var covered = new HashSet<string>(receipts.Select(receipt => receipt.Thread.Key), StringComparer.Ordinal);
            unifiedClearCoreCoverageKnown = uncovered == 0 && CoreThreadTracker.ValidShape(sample);
            if (unifiedClearCoreCoverageKnown)
                for (int index = 0; index < sample.Ids.Length; index++)
                    if (sample.Ids[index] <= 0 || sample.Ids[index] > int.MaxValue || sample.Created[index] <= 0
                        || !covered.Contains(new HardThreadIdentity((int)sample.Ids[index], sample.Created[index]).Key))
                    { unifiedClearCoreCoverageKnown = false; break; }
            int skipped = criticalSession.SkippedThreads;
            unifiedClearCoreNote = (skipped == 0 ? "" : Logger.Text("；", "; ") + skipped + Logger.Text(" 个线程读不到或原范围只在目标核，已跳过", " threads unreadable or originally confined to the target cores, skipped"))
                + (uncovered == 0 ? "" : Logger.Text("；会话记录行数已满，", "; session journal rows full, ") + uncovered + Logger.Text(" 个线程未接纳", " threads not admitted"))
                + Logger.Text("；游戏线程总数=", "; totalGameThreads=") + sample.Ids.Length;
        }

            // 前台每 10 秒检查新增线程 覆盖不完整时提前补扫 接纳累计失败三次后停止新增 保留已生效设置
        private void AdmitClearCoreNewcomers(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, long now)
        {
            if (criticalSession.WorkerReclamationPending)
            {
                unifiedClearCoreCoverageKnown = false;
                unifiedClearCoreNote = Logger.Text("；", "; ") + criticalSession.WorkerReclamationDetail;
                return;
            }
            if (phase.ClearCoreFailures >= 3 || now < unifiedNextClearCoreScan || smartSwitchedOut || RendererForegroundPid() != source.Pid) return;
            unifiedNextClearCoreScan = now + TimeSpan.FromSeconds(10).Ticks;
            try
            {
                unifiedClearCoreCoverageKnown = false;
                ThreadTimes all = ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 1500);
                if (!CriticalThreadObservation.Valid(all, source.Birth, AutoCores.NowTicks))
                {
                    if (source.Rule.IsTimeCriticalRender) RecordClearCoreFailure(phase, Logger.Text("清核成员快照未通过身份核验", "Core clearing member snapshot failed identity verification"));
                    return;
                }
                var covered = new HashSet<string>(criticalSession.ActiveReceipts.Select(receipt => receipt.Thread.Key), StringComparer.Ordinal);
                int newcomers = 0;
                for (int index = 0; index < all.Ids.Length; index++)
                    if (all.Ids[index] > 0 && all.Ids[index] <= int.MaxValue && all.Created[index] > 0
                        && !covered.Contains(new HardThreadIdentity((int)all.Ids[index], all.Created[index]).Key)) newcomers++;
                if (newcomers == 0) { RefreshClearCoreNote(all, 0); ScheduleClearCoreScan(now, 0); return; }
                HardThreadCoverage coverage;
                int uncovered;
                HardThreadIdentity[] batch = ClearCoreBatch(all, criticalPlan, covered, criticalSession.JournalRowCount, out coverage, out uncovered);
                // 关键角色退出由运行期核验处理 行数用完属于覆盖不足 都不算接纳失败
                if (batch.Length <= criticalPlan.Choices.Length)
                {
                    if (batch.Length != 0) unifiedClearCoreNote = Logger.Text("；会话记录行数已满，", "; session journal rows full, ") + newcomers + Logger.Text(" 个新线程未接纳；游戏线程总数=", " new threads not admitted; totalGameThreads=") + all.Ids.Length;
                    return;
                }
                if (criticalSession.AdmitNewThreads(batch, coverage)) { RefreshClearCoreNote(all, uncovered); ScheduleClearCoreScan(now, uncovered); }
                else RecordClearCoreFailure(phase, criticalSession.Detail);
            }
            catch (Exception error)
            {
                RecordClearCoreFailure(phase, error.Message);
                Logger.RepeatedError("unified-clear-core", Logger.Format("清核接纳新线程未完成： {0}", "Core clearing admission of new threads did not complete: {0}", error.Message));
            }
        }

        // 覆盖不完整多半是游戏刚开就退出的短线程 这期间渲染线程会退回原优先级 2 秒后补扫就能恢复 不必等满 10 秒
        // 补扫最多连续两次 仍不完整（读不到或只能跑在目标核的线程）就回到 10 秒一轮 扫描不会因此变密
        private void ScheduleClearCoreScan(long now, int uncovered)
        {
            bool quick = !unifiedClearCoreCoverageKnown && uncovered == 0 && unifiedClearCoreQuickScans < 2;
            if (unifiedClearCoreCoverageKnown) unifiedClearCoreQuickScans = 0;
            else if (quick) unifiedClearCoreQuickScans++;
            unifiedNextClearCoreScan = now + TimeSpan.FromSeconds(quick ? 2 : 10).Ticks;
        }

        private void RecordClearCoreFailure(UnifiedSchedulingPhase phase, string detail)
        {
            unifiedClearCoreCoverageKnown = false;
            phase.ClearCoreFailures++;
            unifiedClearCoreNote = Logger.Text("；新线程接纳失败 ", "; new thread admission failed ") + phase.ClearCoreFailures + Logger.Text("/3：", "/3: ") + detail
                + (phase.ClearCoreFailures >= 3 ? Logger.Text("；已停止接纳新线程，已生效的固定与清核保持", "; new thread admission stopped, active pinning and core clearing kept") : "");
        }

        private string DescribeUnifiedAffinity(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, bool maskKnown)
        {
            string note = string.IsNullOrEmpty(phase.AffinityNote) ? "" : Logger.Text("；", "; ") + phase.AffinityNote;
            if (criticalPlan == null) return Logger.Text("宽域，未接管掩码", "Wide scope, mask not managed") + note;
            if (!maskKnown) return criticalSession.LastRuntimeDetail + note;
            string bound = criticalPlan.FloatingCandidates ? Logger.Text("渲染线程保持原范围，理想处理器指向预留核", "Render thread keeps its original range, ideal processor points at the reserved core") : Logger.Text("指定 CPU 已读回", "Assigned CPU read back");
            if (!criticalPlan.RequireWorkerCoverage) return bound + Logger.Text("；未清核，非整核独占", "; no core clearing, not whole-core exclusive") + note;
            ulong cleared = 0;
            foreach (HardThreadChoice choice in criticalPlan.Choices)
                foreach (ulong core in CpuTopology.PhysicalCoreMasks())
                    if ((core & choice.Mask) != 0) cleared |= core;
            return bound + Logger.Text("；清核：其余 ", "; core clearing: the other ") + unifiedClearedWorkers + Logger.Text(" 个线程已让出 CPU", " threads vacated CPU") + CpuTopology.DescribeRanges(cleared)
                + unifiedClearCoreNote + note;
        }

        // 含实时读数的说明须提供稳定 logKey 避免每次采样都产生日志
        private void PublishUnifiedState(CriticalRuntimeSource source, string state, string detail, bool applied, bool recoveryPending,
            string logKey = null, string englishDetail = null)
        {
            if (source == null) return;
            logKey = logKey ?? detail;
            long now = AutoCores.NowTicks;
            string summary;
            string owner = source.Serial + "|" + source.Pid + "|" + source.Birth + "|" + source.Rule.Encode();
            // 每种原因首现即时记录 成功/冲突来回切换不重置各自窗口 变化读数只进详情
            if (unifiedStateLog.Admit(owner, state, logKey, now,
                state == "active" ? UnifiedHeartbeatTicks : TimeSpan.FromMinutes(2).Ticks, out summary))
            {
                Logger.Info(Logger.Format("游戏调度 状态={0} 游戏={1} PID={2} 创建={3} 规则={4}；{5}{6}", "Game scheduling state={0} game={1} PID={2} birth={3} rule={4}; {5}{6}", state, source.Name, source.Pid, source.Birth, source.Rule.Encode(), detail, summary));
            }
            if (state == unifiedLastState && now < unifiedNextUiPublish) return;
            unifiedNextUiPublish = now + TimeSpan.FromSeconds(2).Ticks;
            unifiedLastState = state;
            string uiDetail = detail;
            if (Lang.Cur != 0)
            {
                uiDetail = englishDetail != null ? englishDetail
                    : state == "active" ? ThreadLayoutDescriptions.Text("", "Managed fields were read back. Saved rules remain under continuous correction.", "管理項目の読み戻しを確認しました。保存したルールを継続的に維持します。")
                    : state == "off" ? ThreadLayoutDescriptions.Text("", "Game scheduling is off; owned settings were restored.", "ゲームスケジューリングは無効で、所有する設定を復元しました。")
                    : recoveryPending ? ThreadLayoutDescriptions.Text("", "New writes stopped; restoration is awaiting verification.", "新規書き込みを停止し、復元の確認を待っています。")
                    : state == "unsupported" ? ThreadLayoutDescriptions.Text("", "This thread-control stage could not be started. Existing recovery ownership is retained; background governance is unaffected.", "")
                    : state == "observing" ? ThreadLayoutDescriptions.Text("", "No new thread control is active. Observation and restoration details are recorded in the log.", "")
                    : ThreadLayoutDescriptions.Text("", "Some fields are unverified or contested. The saved rule is retained; see the log for the affected field.", "一部の項目が未確認または競合中です。保存したルールを維持し、対象項目はログに記録します。");
            }
            bool cleared = applied && criticalPlan != null && criticalPlan.RequireWorkerCoverage;
            bool partial = cleared && criticalSession != null && criticalSession.Status == HardThreadStatus.CoveragePending;
            PublishCriticalThreadState(source, state, uiDetail, applied, recoveryPending, cleared ? unifiedClearedWorkers : 0, now,
                partial ? CriticalThreadCoverageState.WorkersPartial
                    : cleared ? CriticalThreadCoverageState.WorkersSnapshotComplete
                    : applied ? CriticalThreadCoverageState.CandidatesOnly : CriticalThreadCoverageState.None, false);
        }
    }
}
