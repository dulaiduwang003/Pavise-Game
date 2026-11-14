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
            internal GameThreadControlRole[] ControlRoles;
            internal int ControlAdmissionAttempts;
            internal long NextControlAdmission;
            internal bool ControlAdmitted;
            internal bool ObservationOnly;
            // 无排队证据且尚未接管时，可在负载升高后使用剩余采集额度重判。
            internal bool AwaitingLoad;
            // 独立核固定到此逻辑 CPU；提优清核时仅据此预留物理核，0 表示不改亲和性。
            internal ulong RenderTarget;
            internal bool RenderClear;
            // 时间关键模式须有固定核；普通模式允许仅提优。
            internal string AffinityNote;
            // 新线程接纳仅失败时扣次数，预算归属原阶段，规则往返不补充。
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
            Logger.Info("游戏调度 " + source.Name + " PID=" + source.Pid + " 负载比首次判定时明显升高并保持，使用剩余一次采集重新判定；"
                + phase.LoadWatch.Describe() + "；此前未施加任何接管");
            phase.Roles = phase.PriorityRoles = phase.ObservedRoles = null;
            phase.ControlRoles = null;
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
        private int unifiedClearedWorkers;
        private bool unifiedClearCoreCoverageKnown;
        private string unifiedClearCoreNote = "";
        private string unifiedLastState;
        internal static readonly long UnifiedHeartbeatTicks = TimeSpan.FromMinutes(10).Ticks;
        private GameThreadControlSession unifiedThreadControl;
        private readonly SchedulingDiagnosticLog unifiedStateLog = new SchedulingDiagnosticLog();
        // 每个游戏仅在内存保留最近一次放置前观察，供方案窗口对照，不参与准入。
        private readonly Dictionary<string, SchedulingBottleneckEvidence> schedulingEvidence
            = new Dictionary<string, SchedulingBottleneckEvidence>(StringComparer.OrdinalIgnoreCase);

        private SchedulingBottleneckEvidence NoteSchedulingEvidence(CriticalRuntimeSource source, UnifiedThreadCaptureEvidence window)
        {
            SchedulingBottleneckEvidence observed = SchedulingBottleneckEvidence.From(window == null ? null : window.Relation,
                window == null ? null : window.Presenter, DateTime.Now);
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

        internal CriticalThreadRule EffectiveGameSchedulingRule(GameProfile profile)
        { return PolicyResolver.GameSchedulingRule(profile); }

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

        // 会话循环已持有同一个进程门禁。控制、关闭和恢复不并发执行，也不靠新建控制器补充失败预算。
        private bool StepUnifiedThreadRuntimeLocked(CriticalRuntimeSource source, string missing)
        {
            try
            {
                if (criticalSource != null && !criticalSource.Same(source))
                {
                    if (unifiedThreadControl != null) unifiedThreadControl.RequestStop();
                    if (!WithdrawCriticalThreadsLocked(source == null ? missing : criticalSource.Difference(source))) return true;
                    FinishCriticalDiagnosticLogs();
                    EndThreadLayoutDiagnostics("统一调度原计划结束", true);
                    criticalSource = null;
                    criticalObservation.Reset();
                    unifiedNextCpuSample = unifiedNextAudit = unifiedNextUiPublish = 0;
                    unifiedLastState = null;
                }
                if (source == null || source.Rule.IsOff)
                {
                    if (unifiedThreadControl != null) unifiedThreadControl.RequestStop();
                    bool restored = WithdrawCriticalThreadsLocked(source == null ? missing : "用户关闭游戏调度");
                    if (source != null) PublishUnifiedState(source, restored ? "off" : "recovery-pending",
                        restored ? "游戏调度已关闭，原设置恢复已核验" : "已停止运行期写入，原设置恢复待确认", false, !restored);
                    return true;
                }
                if (criticalSource == null)
                {
                    criticalSource = source;
                }
                // 撤权后的承载只做收尾，不参与维护或重新获得首次接管额度。
                if (criticalOriginalTarget != null && criticalOriginalTarget.Revoked
                    || criticalSession == null && (criticalThreadResidue || criticalPriorityReservation != null))
                {
                    if (!RestoreCriticalThreadsLocked())
                    {
                        PublishUnifiedState(source, "recovery-pending", "原线程控制已撤销，等待原恢复归属及辅助资源结清；未新增接管", false, true);
                        return true;
                    }
                }
                // 进程设置权限与线程设置权限独立；历史进程拒绝不替代本局线程身份、原值及实际 API 准入。
                BeginThreadLayoutDiagnostics(source);
                UnifiedSchedulingPhase phase = UnifiedPhase(source);
                long now = AutoCores.NowTicks;
                ThreadLayoutCaptureRequest request = EnsureThreadLayoutCapture(source);
                bool foreground = !smartSwitchedOut && RendererForegroundPid() == source.Pid;
                if (request != null) request.SetForeground(foreground, now);
                if (phase.RoleExitPending)
                {
                    ReviewExitedRoles(source, phase, request, now);
                    return true;
                }
                // 在途观察长于核验周期；恢复结清后保活同一请求，节奏门禁不能把已开始的采集取消。
                bool observingRequest = request != null && request.NeedsObserver(now) && foreground;
                if (observingRequest) WantCriticalRoles(source, null);
                if (phase.ObservationOnly)
                {
                    bool released = criticalSession == null && unifiedThreadControl == null || RestoreCriticalThreadsLocked();
                    if (released && phase.AwaitingLoad && StepUnifiedLoadWatch(source, phase, request, foreground, now))
                    {
                        PublishUnifiedState(source, "observing", "负载已明显升高，使用剩余一次采集重新判定；此前未施加任何接管", false, false);
                        return true;
                    }
                    string watching = "调度观察；" + phase.Evidence
                        + (released ? "；未新增线程控制，停止本阶段原基线的线程扫描" : "；已停止空控制计划，恢复归属尚未结清")
                        + (released && phase.AwaitingLoad ? "；负载明显升高并保持一分钟后会用剩余一次采集重新判定" : "");
                    bool readings = released && phase.AwaitingLoad && phase.LoadWatch != null;
                // 界面保留实时读数，日志仅在连续升高计数变化时记录。
                    string watchKey = watching + (readings ? "|" + phase.LoadWatch.Streak : "");
                    PublishUnifiedState(source, released ? "observing" : "recovery-pending",
                        watching + (readings ? "；" + phase.LoadWatch.Describe() : ""), false, !released, watchKey);
                    return true;
                }
                if (criticalSession != null)
                {
                    if (now < unifiedNextAudit) return true;
                    unifiedNextAudit = now + TimeSpan.FromSeconds(2).Ticks;
                    // 时间关键优先级依赖原单核绑定；先纠正范围，再由控制器按实际绑定决定提优或撤回增量。
                    bool maskKnown = !source.Rule.IsTimeCriticalRender || criticalPlan != null && criticalSession.MaintainApplied(now);
                    if (source.Rule.IsTimeCriticalRender && criticalPlan != null && criticalPlan.RequireWorkerCoverage
                        && (maskKnown || criticalSession.RuntimeAdmissionRequired))
                        AdmitClearCoreNewcomers(source, phase, now);
                    bool timeCriticalReady = !source.Rule.IsTimeCriticalRender || !source.Rule.ClearCore
                        || phase.RenderClear && maskKnown && unifiedClearCoreCoverageKnown && !criticalSession.WorkerReclamationPending;
                    if (source.Rule.IsTimeCriticalRender) criticalSession.SetTimeCriticalWorkerCoverage(timeCriticalReady);
                    if (unifiedThreadControl != null && !phase.ControlAdmitted && phase.ControlAdmissionAttempts < 3
                        && timeCriticalReady && now >= phase.NextControlAdmission && !smartSwitchedOut && RendererForegroundPid() == source.Pid)
                    {
                        phase.ControlAdmissionAttempts++;
                        phase.NextControlAdmission = now + TimeSpan.FromSeconds(10).Ticks;
                        phase.ControlAdmitted = unifiedThreadControl.Admit(phase.ControlRoles);
                        if (RejectIncompleteDependencyBoost(source, phase)) return true;
                    }
                    // 已完成准入但没有可接管属性时，渲染线程的固定或清核范围仍可独立核验；尚未准入的未知结果不能据此放行。
                    bool attributesKnown = unifiedThreadControl == null
                        || criticalPlan != null && phase.ControlAdmitted && unifiedThreadControl.ManagedFieldCount == 0
                        || unifiedThreadControl.Tick();
                    if (!source.Rule.IsTimeCriticalRender) maskKnown = criticalPlan == null || criticalSession.MaintainApplied(now);
                    GameThreadControlJournal controlRows = phase.Roles == null || unifiedThreadControl == null ? null : unifiedThreadControl.Snapshot;
                    HardThreadIdentity[] exitedRoles = controlRows == null ? new HardThreadIdentity[0]
                        : phase.Roles.Where(role => controlRows.Rows.Any(row => row.Priority.Phase == GameThreadControlPhase.Gone
                            && row.Thread.Key == role.Key)).ToArray();
                    // 未预留主副核的编排把全部提优成员都列为角色。只有主副候选退出才撤回整套计划；
                    // 其余成员退出由各自字段结清，全部失去接管后再走同一条撤回与复核路径。
                    bool memberOnly = phase.WorkloadPlan != null && phase.WorkloadPlan.Primary == null && phase.Workload != null;
                    bool roleExited = criticalSession.ThreadHasExited || exitedRoles.Length != 0 && (!memberOnly
                        || exitedRoles.Any(role => WorkloadAnchor(phase.Workload, role))
                        || phase.ControlAdmitted && unifiedThreadControl.ManagedFieldCount == 0);
                    if (roleExited)
                    {
                        // 重识别可以耗尽，旧角色退出后的恢复不能因此跳过；未知读值不构成退出证明。
                        phase.RoleExitPending = true;
                        phase.RoleReviewAllowed = source.Rule.IsRenderStrategy ? !phase.RenderReviewUsed : !phase.WorkloadReviewUsed;
                        if (source.Rule.IsRenderStrategy) phase.RenderReviewUsed = true;
                        else phase.WorkloadReviewUsed = true;
                        ReviewExitedRoles(source, phase, request, now);
                        return true;
                    }
                    if (source.Rule.IsTimeCriticalRender && unifiedThreadControl != null && unifiedThreadControl.Stopped)
                    {
                        // 首次写入可能成功但始终读回未知；停止新增后仍先归还优先级，再解除绑定，不能留下未核验的高优先级。
                        phase.ObservationOnly = true;
                        phase.AwaitingLoad = false;
                        phase.Evidence += "；时间关键首次接管未核验且尝试已结束，撤销本阶段写入并按归属恢复";
                        bool restored = RestoreCriticalThreadsLocked();
                        PublishUnifiedState(source, restored ? "observing" : "recovery-pending", phase.Evidence, false, !restored);
                        return true;
                    }
                    // 清核成员由新线程扫描统一接纳；本分支仅处理无清核的显式工作组。
                    if (phase.WorkloadPlan != null && criticalPlan != null && !criticalPlan.RequireWorkerCoverage && criticalSession.RuntimeAdmissionRequired
                        && phase.PlacementAdmissionAttempts < 3 && now >= phase.NextPlacementAdmission && foreground)
                    {
                        phase.PlacementAdmissionAttempts++;
                        phase.NextPlacementAdmission = now + TimeSpan.FromSeconds(10).Ticks;
                        HardThreadIdentity[] members = criticalPlan.Choices.Concat(criticalPlan.WorkerPlacements)
                            .Select(choice => choice.Thread).ToArray();
                        // 原值已符合目标的成员尚无本方写入；后续首次接管仍消耗同一有限账本。
                        criticalSession.AdmitNewThreads(members, criticalPlan.SelectedWorkersOnly
                            ? HardThreadCoverage.Partial : HardThreadCoverage.CandidatesOnly);
                        maskKnown = criticalSession.MaintainApplied(now);
                    }
                    if (!source.Rule.IsTimeCriticalRender && criticalPlan != null && criticalPlan.RequireWorkerCoverage) AdmitClearCoreNewcomers(source, phase, now);
                    int verifiedFields = unifiedThreadControl == null ? 0 : unifiedThreadControl.VerifiedCount;
                    if (verifiedFields > 0 && threadLayoutCapture != null) threadLayoutCapture.MarkApplied(now);
                    string detail = "策略=" + StrategyName(source.Rule) + "；运行期纠偏=无限；原角色冻结；已核验线程属性（优先级/理想核，亲和性另计）=" + verifiedFields
                        + (phase.WorkloadPlan == null ? "" : "；" + DescribeWorkloadPlan(phase))
                        + "；亲和性=" + DescribeUnifiedAffinity(source, phase, maskKnown)
                        + (unifiedThreadControl == null ? "；未新增线程提优或优先核" : "；" + unifiedThreadControl.Detail);
                    if (unifiedThreadControl != null && !phase.ControlAdmitted)
                        detail += "；线程属性首次准入尚未完成，尝试=" + phase.ControlAdmissionAttempts + "/3";
                    if (criticalSession.ThreadHasExited)
                        detail += "；原硬绑角色已确认退出，不将新线程号接入旧角色";
                    if (memberOnly && exitedRoles.Length != 0)
                        detail += "；提优成员已退出=" + exitedRoles.Length + "，各自结清，其余成员继续维护";
                    bool hasManagedFields = criticalPlan != null || unifiedThreadControl != null && unifiedThreadControl.ManagedFieldCount > 0;
                    bool allKnown = attributesKnown && maskKnown && hasManagedFields;
                    if (request != null) request.ObserveIdentitySnapshot(hasManagedFields && (verifiedFields > 0 || criticalPlan != null && maskKnown), now);
                    PublishUnifiedState(source, allKnown ? "active" : hasManagedFields ? "conflict" : "observing", detail,
                        allKnown && (verifiedFields > 0 || criticalPlan != null), false);
                    return true;
                }
                if (CriticalThreadHasRecoveryDebt(false))
                {
                    PublishUnifiedState(source, "recovery-pending", "旧线程恢复记录尚未结清，未新增控制", false, true);
                    return true;
                }
                if (smartSwitchedOut || RendererForegroundPid() != source.Pid)
                {
                    criticalObservation.MarkPaused();
                    PublishUnifiedState(source, "observing", "等待游戏返回前台；既有规则与采样次数保留", false, false);
                    return true;
                }
                if (now < unifiedNextCpuSample) return true;
                if (phase.Roles == null && request == null)
                {
                    PublishUnifiedState(source, "observing", "本局无法建立有界角色观察；未启动线程扫描或新增写入", false, false);
                    return true;
                }
                if (phase.Roles == null && request != null && !request.HasUsableBaseline
                    && (request.ObservationExhausted || now < request.NextCaptureDueTicks))
                {
                    PublishUnifiedState(source, "observing", request.DescribeState(now)
                        + "；等待期间不枚举线程，观察额度耗尽后停止角色采样", false, false);
                    return true;
                }
                if (phase.Roles != null && (phase.InitialAttempts >= 3 || now < phase.NextInitial))
                {
                    if (phase.InitialAttempts >= 3) PublishUnifiedState(source, "conflict", "首次接管次数已用完；本局停止新增接管，保留恢复归属", false, false);
                    return true;
                }
                UnifiedThreadCaptureEvidence admittedBaseline = request == null ? null : request.ReadUnifiedBaseline();
                if (phase.Roles == null && admittedBaseline != null && now - admittedBaseline.Observed > TimeSpan.FromSeconds(65).Ticks)
                {
                    PublishUnifiedState(source, "observing", "角色证据已超过准入新鲜度；停止原基线的线程扫描，不追加采集额度", false, false);
                    return true;
                }
                unifiedNextCpuSample = now + TimeSpan.FromSeconds(5).Ticks;
            // 旁观线程退出不影响已冻结角色的准入。
                ThreadTimes snapshot = phase.Roles == null || phase.Workload != null && !source.Rule.IsDependencyBoost
                    ? ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 1500)
                    : UnifiedThreadRuntimeSampling.Read(source.Pid, source.Birth, UnifiedObservedRoles(phase));
            // 新鲜度以取完快照后的时刻核验，避免使用本轮起点误判新快照。
                long sampledAt = AutoCores.NowTicks;
                bool snapshotKnown = CriticalThreadObservation.Valid(snapshot, source.Birth, sampledAt);
                if (request != null) request.ObserveIdentitySnapshot(snapshotKnown, now);
                if (!observingRequest && request != null && request.NeedsObserver(now)) WantCriticalRoles(source, snapshot);
                if (!snapshotKnown)
                {
                    PublishUnifiedState(source, "observing", "线程身份或 CPU 快照暂不可核验；未新增写入", false, false);
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
                        PublishUnifiedState(source, "observing", reason + "；普通高负载及线程池保留原优先级和运行范围", false, false);
                        return true;
                    }
                    phase.Roles = roles;
                    // 呈现身份沿用有限观察；时间关键模式额外要求固定核，不能在选核失败时仅抬高优先级。
                    phase.PriorityRoles = new[] { roles[0] };
                    phase.Evidence = reason + "；" + (source.Rule.IsTimeCriticalRender
                        ? "独立核时间关键：自动选核固定，仅此线程目标 TIME_CRITICAL，进程类别保持不变" : source.Rule.IsGamePriority
                        ? "游戏优先：进程类别由资源通道另行写入、核验和恢复，本行不代表进程提优已生效；渲染线程自动选核固定，普通提优按角色上限准入"
                        : source.Rule.PinsRenderThread
                        ? "渲染线程独立核：自动选核固定；普通提优按角色上限准入，High 可用 +2，Realtime 保留优先级；进程类别不改"
                        : "渲染线程提优：普通提优按角色上限准入，High 可用 +2，Realtime 保留优先级；进程类别和亲和性不动");
                    PrepareRenderAffinity(source, phase, baseline);
                    if (!string.IsNullOrEmpty(phase.AffinityNote)) phase.Evidence += "；" + phase.AffinityNote;
                    phase.AwaitingLoad = false;
                    phase.ObservationOnly = source.Rule.IsTimeCriticalRender && (phase.RenderTarget == 0 || source.Rule.ClearCore && !phase.RenderClear);
                    Logger.Info("游戏调度 角色准入： " + source.Name + " PID=" + source.Pid + " 创建=" + source.Birth
                        + " 规则=" + source.Rule.Encode() + " 原规则=" + source.RuleText
                        + " 角色=" + string.Join(",", roles.Select(role => role.Key).ToArray())
                        + " 目标CPU=" + (phase.RenderTarget == 0 ? "未改亲和性" : CriticalThreadRule.ProcessorName(phase.RenderTarget))
                        + " 清核=" + (phase.RenderClear ? "开" : "关")
                        + "；" + phase.Evidence + "；" + NoteSchedulingEvidence(source, baseline).Describe() + "；选择证据不作为性能收益证据");
                    }
                }
                if (phase.ObservationOnly)
                {
                    PublishUnifiedState(source, "observing", "本阶段接管条件未满足；未创建线程控制或恢复守护；" + phase.Evidence, false, false);
                    return true;
                }
                if (phase.InitialAttempts >= 3 || now < phase.NextInitial)
                {
                    PublishUnifiedState(source, "conflict", "首次接管尚未完成，当前阶段尝试=" + phase.InitialAttempts
                        + "/3；首次准入与恢复预算有界，运行期已接管字段纠偏不限次数", false, false);
                    return true;
                }
                if (phase.Roles.Any(role => !CriticalThreadRuntimePolicy.Contains(snapshot, role))
                    || phase.PriorityRoles.Any(role => !CriticalThreadRuntimePolicy.Contains(snapshot, role)))
                {
                    phase.InitialAttempts = 3;
                    PublishUnifiedState(source, "observing", "原准入身份已退出；本局不凭新线程号重选或追加采样", false, false);
                    return true;
                }
                phase.InitialAttempts++;
                phase.NextInitial = now + TimeSpan.FromSeconds(10).Ticks;
                StartUnifiedThreadControl(source, snapshot, phase);
            }
            catch (Exception error)
            {
                // 守护启动失败也只计本阶段已实际执行的尝试；退避、恢复债和三次上限沿用同一账本。
                // 已经接管后的异常不进入旧整组撤回路径；关闭或退局仍由原恢复门禁结算。
                if (source != null) PublishUnifiedState(source, "conflict", "统一调度本轮未能核验：" + error.GetType().Name
                    + " " + error.Message + "；保留规则与恢复记录", false, false);
                Logger.RepeatedError("unified-game-scheduling", "游戏调度核验未完成： " + error.Message);
            }
            return true;
        }

            // 仅接管或收尾阶段要求放置结果。
        internal static bool PlacementExpected(string state)
        { return state == "active" || state == "conflict" || state == "coverage-pending" || state == "recovery-pending"; }

        private void StartUnifiedThreadControl(CriticalRuntimeSource source, ThreadTimes snapshot, UnifiedSchedulingPhase phase)
        {
            if (source.Rule.IsTimeCriticalRender && (phase.RenderTarget == 0 || (phase.RenderTarget & (phase.RenderTarget - 1)) != 0
                || source.Rule.ClearCore && !phase.RenderClear
                || phase.Roles == null || phase.Roles.Length != 1 || phase.PriorityRoles == null || phase.PriorityRoles.Length != 1
                || phase.Roles[0].Key != phase.PriorityRoles[0].Key))
            {
                phase.ObservationOnly = true;
                phase.AwaitingLoad = false;
                phase.Evidence += "；单一呈现身份、独立核目标或所选清核条件未确认，未新增时间关键提优";
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return;
            }
            string oldPriorityReason;
            HardThreadIdentity[] reserved = phase.Roles.Concat(phase.PriorityRoles).GroupBy(role => role.Key).Select(group => group.First()).ToArray();
            if (!TryReserveCriticalThreadPriority(source.Pid, source.Birth, reserved, out oldPriorityReason))
            { PublishUnifiedState(source, "recovery-pending", oldPriorityReason, false, true); return; }
            IntPtr process = Native.OpenProcess(0x1000, false, source.Pid);
            if (process == IntPtr.Zero) throw new InvalidOperationException("游戏进程查询句柄不可用");
            try
            {
                // 游戏优先的进程类别由资源通道持有并单独记账，这里不归还；其余方案先归还旧的整进程提优。
                if (!source.Rule.RaisesGameProcess && !CrashGuard.RestoreBoostPriority(process, source.Pid, source.Birth))
                    throw new InvalidOperationException("旧进程 CPU 提优恢复未确认，暂不捕获线程提优基线");
                uint currentClass = Native.GetPriorityClass(process);
                // 普通线程属性分别准入；显式时间关键规则仍沿用原有的类别边界，不扩展到实时等级。
                if (source.Rule.IsTimeCriticalRender && (currentClass == 0x80 || currentClass == 0x100))
                {
                    phase.ObservationOnly = true;
                    phase.Evidence += "；游戏已有High/Realtime调度，本次显式时间关键15未准入；未新增控制";
                    ReleaseCriticalThreadPriorityReservation();
                    PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                    return;
                }
                ulong mask = 0;
                if (!source.Rule.IsDependencyBoost && (!Native.TryQueryAffinity(process, out mask) || mask == 0))
                    throw new InvalidOperationException("游戏当前允许范围不可核验");
                StartThreadLayoutDiagnostics(source, mask);
                var pass = new BoostPass { RendererPid = source.Pid, RendererCreation = source.Birth, RendererName = source.Name };
                // 纯提优计划独立于选核资格；依赖组仅按线程身份授权。
                CriticalThreadTargetLease target = CreateCriticalThreadTarget(pass, process, source.Rule, source.Profile, source.RuleText,
                    mask, source.Epoch, source.Serial, source.Rule.IsDependencyBoost || source.Rule.IsRenderStrategy && phase.RenderTarget == 0);
                if (target == null) throw new InvalidOperationException("游戏身份、范围或当前用户规则未通过写入授权核验");
                criticalSession = CriticalThreadHardAffinitySession.OpenNative(target);
                criticalSession.UseWorkerAdmissionBudget(phase.WorkerAdmissionBudget);
                criticalRestorationConfirmed = false;
                criticalThreadResidue = true;
                criticalGuardianActor = criticalSession.GuardianActor;
                ReleaseCriticalGuardianBackground();
                ThreadTimes fresh = UnifiedThreadRuntimeSampling.Read(source.Pid, source.Birth, reserved);
                if (!CriticalThreadObservation.Valid(fresh, source.Birth, AutoCores.NowTicks)
                    || reserved.Any(role => !CriticalThreadRuntimePolicy.Contains(fresh, role)))
                    throw new InvalidOperationException("守护建立后角色身份或新鲜快照未确认");
                unifiedClearedWorkers = 0;
                unifiedClearCoreCoverageKnown = false;
                unifiedClearCoreNote = "";
                if (phase.WorkloadPlan != null && phase.WorkloadPlan.Primary != null)
                {
                    HardThreadChoice[] choices = phase.WorkloadPlan.Secondary == null
                        ? new[] { phase.WorkloadPlan.Primary } : new[] { phase.WorkloadPlan.Primary, phase.WorkloadPlan.Secondary };
                    if (source.Rule.ClearCore)
                    {
                // 清出已分配主副核所在的物理核；容量检查已扣除这些预留核。
                        ApplyClearCore(source, choices, false);
                        Logger.Info("全线程编排 放置 " + source.Name + " PID=" + source.Pid + "；" + DescribeWorkloadPlan(phase)
                            + "；清核覆盖其余 " + unifiedClearedWorkers + " 个线程；范围读回=" + criticalSession.Detail);
                    }
                    else
                    {
                        HardThreadChoice[] workers = phase.WorkloadPlan.SelectedWorkers;
                        criticalPlan = criticalSession.CreatePlan(choices, false, true, workers, false, workers.Length != 0);
                        HardThreadIdentity[] members = choices.Concat(workers).Select(choice => choice.Thread).ToArray();
                        if (!criticalSession.Apply(criticalPlan, members, workers.Length == 0 ? HardThreadCoverage.CandidatesOnly : HardThreadCoverage.Partial))
                            throw new InvalidOperationException("全线程编排首次范围未核验：" + criticalSession.Detail);
                        Logger.Info("全线程编排 放置 " + source.Name + " PID=" + source.Pid + "；" + DescribeWorkloadPlan(phase)
                            + "；范围读回=" + criticalSession.Detail + "；仅显式工作组，不声明全游戏或系统独占");
                    }
                }
                else if (phase.WorkloadPlan != null && source.Rule.ClearCore)
                    phase.AffinityNote = "编排计划本局未固定核心，没有可清的核";
                else if (phase.RenderTarget != 0)
                {
                    HardThreadChoice[] choices = { new HardThreadChoice(phase.Roles[0], phase.RenderTarget) };
                    // 提优清核不改渲染线程范围，仅预留其物理核并设置理想处理器。
                    if (phase.RenderClear) ApplyClearCore(source, choices, !source.Rule.PinsRenderThread);
                    else
                    {
                        criticalPlan = criticalSession.CreatePlan(choices, false, false, null, true);
                        if (!criticalSession.Apply(criticalPlan, new[] { phase.Roles[0] }, HardThreadCoverage.CandidatesOnly))
                            throw new InvalidOperationException("渲染线程首次固定未核验：" + criticalSession.Detail);
                    }
                }
                PublishBackgroundCoreReservation(source);
                if (phase.PriorityRoles.Length != 0 && phase.ControlAdmissionAttempts < 3)
                {
                    bool render = source.Rule.IsRenderStrategy;
                    GameThreadControlProcessor? ideal = render && criticalPlan != null && criticalPlan.FloatingCandidates
                        ? new GameThreadControlProcessor(0, (byte)LowestCpu(phase.RenderTarget)) : (GameThreadControlProcessor?)null;
                    var roles = phase.PriorityRoles.Select(role => new GameThreadControlRole(role, true, ideal,
                        source.Rule.IsDependencyBoost ? "已确认呈现依赖组" : phase.Workload != null ? "全线程关键依赖组" : render ? source.Rule.PinsRenderThread ? "渲染线程（独立核）" : "渲染线程（提优）"
                            : "稳定呈现提交角色",
                        source.Rule.IsTimeCriticalRender ? 15 : render ? 2 : 1)).ToArray();
                    if (source.Rule.IsTimeCriticalRender)
                        criticalSession.SetTimeCriticalWorkerCoverage(!source.Rule.ClearCore || phase.RenderClear && unifiedClearCoreCoverageKnown);
                    unifiedThreadControl = GameThreadControlSession.CreateNative(criticalSession, source.Rule.IsTimeCriticalRender ? source.Rule : null);
                    phase.ControlRoles = roles;
                    if (!source.Rule.IsTimeCriticalRender || !source.Rule.ClearCore || unifiedClearCoreCoverageKnown)
                    {
                        phase.ControlAdmissionAttempts++;
                        phase.NextControlAdmission = AutoCores.NowTicks + TimeSpan.FromSeconds(10).Ticks;
                        phase.ControlAdmitted = unifiedThreadControl.Admit(roles);
                    }
                    if (RejectIncompleteDependencyBoost(source, phase)) return;
                    if (criticalPlan == null && phase.ControlAdmitted && unifiedThreadControl.ManagedFieldCount == 0)
                    {
                        phase.ObservationOnly = true;
                        phase.Evidence += "；无新增可接管字段，结束空控制计划；" + unifiedThreadControl.Detail;
                        bool released = RestoreCriticalThreadsLocked();
                        PublishUnifiedState(source, released ? "observing" : "recovery-pending", phase.Evidence, false, !released);
                        return;
                    }
                }
                if (threadLayoutCapture != null && (criticalPlan != null || unifiedThreadControl != null && unifiedThreadControl.VerifiedCount > 0))
                    threadLayoutCapture.MarkApplied(AutoCores.NowTicks);
                unifiedNextAudit = 0;
                PublishUnifiedState(source, "coverage-pending", "角色已冻结；亲和性与线程属性分别核验；" + phase.Evidence
                    + (unifiedThreadControl == null ? "" : "；" + unifiedThreadControl.Detail)
                    + "；已接管字段运行期持续纠偏，未新增 ETW 或角色重选次数", false, false);
            }
            catch
            {
                // 首次接管失败沿用同一有限阶段，先结算在途写入与恢复，不能丢掉对象再新建补次数。
                if (unifiedThreadControl == null)
                {
                    if (RestoreCriticalThreadsLocked()) criticalPlan = null;
                }
                throw;
            }
            finally { Native.CloseHandle(process); }
        }

        private bool RejectIncompleteDependencyBoost(CriticalRuntimeSource source, UnifiedSchedulingPhase phase)
        {
            if ((!source.Rule.IsDependencyBoost && !source.Rule.IsTimeCriticalRender) || !phase.ControlAdmitted || unifiedThreadControl == null) return false;
            GameThreadControlJournal snapshot = unifiedThreadControl.Snapshot;
            if (phase.PriorityRoles.All(role => snapshot.Rows.Any(row => row.Thread.Key == role.Key
                && row.Priority.Managed && !row.Priority.Terminal))) return false;
            // 特殊优先级会被通用准入保留；不能把未接管的呈现提优或缺失成员的小组报告为完整策略。
            phase.ObservationOnly = true;
            phase.AwaitingLoad = false;
            phase.Evidence += source.Rule.IsTimeCriticalRender
                ? "；呈现线程原有特殊优先级不接管，按归属恢复绑定，不报告时间关键提优已生效"
                : "；组内存在不能接管的特殊优先级，保留整组原设置，不建立不完整依赖组";
            unifiedThreadControl.RequestStop();
            bool restored = RestoreCriticalThreadsLocked();
            PublishUnifiedState(source, restored ? "observing" : "recovery-pending", phase.Evidence, false, !restored);
            return true;
        }

        private static string StrategyName(CriticalThreadRule rule)
        {
            string name = rule.IsTimeCriticalRender ? "独立核时间关键" : rule.IsDependencyBoost ? "依赖组提优" : rule.IsGamePriority ? "游戏优先" : rule.PinsRenderThread ? "渲染线程独立核" : rule.IsRenderStrategy ? "渲染线程提优" : "主副核编排";
            return name + (rule.ClearCore ? "+清核" : "");
        }

        private static int LowestCpu(ulong mask)
        {
            for (int index = 0; index < 64; index++) if ((mask & (1UL << index)) != 0) return index;
            return 0;
        }

        // 渲染策略的亲和性部分：独立核要选一颗固定核；提优打开清核要选一颗预留核。
        // 仅准入时选核；普通模式可只提优，时间关键模式须核验固定核。
        private void PrepareRenderAffinity(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, UnifiedThreadCaptureEvidence baseline)
        {
            phase.RenderTarget = 0;
            phase.RenderClear = false;
            phase.AffinityNote = null;
            if (!source.Rule.PinsRenderThread && !source.Rule.ClearCore) return;
            string skipped = source.Rule.PinsRenderThread ? "本局不固定核心" + (source.Rule.ClearCore ? "、不清核" : "") : "本局不清核";
            string priorityFallback = source.Rule.IsTimeCriticalRender ? "，未新增时间关键提优" : "，提优照常";
            if (CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
            { phase.AffinityNote = "处理器拓扑不可核验，" + skipped + priorityFallback; return; }
            ulong mask;
            if (!ReadCriticalProcessMask(source.Pid, source.Birth, out mask) || mask == 0)
            { phase.AffinityNote = "游戏当前进程范围不可核验，" + skipped + priorityFallback; return; }
            CriticalThreadAllowedRange scope;
            lock (sync) scope = CriticalThreadAllowedRange.For(CriticalPreferenceProfileLocked(source.Profile));
            if (scope.Error != null)
            { phase.AffinityNote = "游戏核心范围未确认（" + scope.Error + "），" + skipped + priorityFallback; return; }
            ulong[] physical = CpuTopology.PhysicalCoreMasks();
            ulong reserved = AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, physical);
            ulong allowed = mask & scope.Mask & ~reserved;
            string reason;
            ulong target = RenderIsolationPlan.Choose(allowed, physical, CpuTopology.PerfMask, CpuTopology.FavoredMask,
                baseline == null || baseline.Relation == null ? null : baseline.Relation.Presenter, out reason);
            if (target == 0)
            { phase.AffinityNote = "未选出可用核心（" + reason + "），" + skipped + priorityFallback; return; }
            ulong core = physical.First(item => (item & target) != 0);
            bool room = RenderIsolationPlan.LeavesRoomForOthers(allowed, physical, core);
            phase.RenderClear = source.Rule.ClearCore && room;
            if (source.Rule.ClearCore && !room) phase.AffinityNote = "清核后其他线程不足两颗物理核，本局不清核";
            // 提优策略仅在可清核时预留核心。
            phase.RenderTarget = source.Rule.PinsRenderThread || phase.RenderClear ? target : 0;
        }

        // 先收紧其他游戏线程的原范围，让出目标物理核及 SMT 同核线程，再安置关键角色。
        // 不可读或无法避让的线程记为覆盖不足；批次受剩余日志容量限制，后续扫描补齐。
        private void ApplyClearCore(CriticalRuntimeSource source, HardThreadChoice[] choices, bool floating)
        {
            criticalPlan = criticalSession.CreatePlan(choices, true, true, null, false, false, floating);
            ThreadTimes all = ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 0);
            if (!CriticalThreadObservation.Valid(all, source.Birth, AutoCores.NowTicks))
                throw new InvalidOperationException("清核需要可核验的线程快照");
            HardThreadCoverage coverage;
            int uncovered;
            HardThreadIdentity[] batch = ClearCoreBatch(all, criticalPlan, new HashSet<string>(StringComparer.Ordinal),
                criticalSession.JournalRowCount, out coverage, out uncovered);
            if (batch.Length == 0) throw new InvalidOperationException("清核快照缺少关键角色或线程身份重复");
            if (!criticalSession.Apply(criticalPlan, batch, coverage))
                throw new InvalidOperationException("清核首次范围未核验：" + criticalSession.Detail);
            RefreshClearCoreNote(all, uncovered);
            unifiedNextClearCoreScan = AutoCores.NowTicks + TimeSpan.FromSeconds(10).Ticks;
            Logger.Log("游戏调度 " + source.Name + (source.Rule.IsTimeCriticalRender && !unifiedClearCoreCoverageKnown
                ? " 清核覆盖尚未完整核验，时间关键提优未启用：" : " 清核已生效：") + (floating ? "渲染线程保持原范围，理想处理器指向 " : "关键角色固定到 ")
                + string.Join("、", choices.Select(choice => CriticalThreadRule.ProcessorName(choice.Mask)).ToArray())
                + "，其余 " + unifiedClearedWorkers + " 个线程让出所在物理核，可用范围=0x" + criticalPlan.Workers.ToString("X")
                + unifiedClearCoreNote + "；退局逐线程恢复原值");
        }

        // 批内须包含关键角色；线程数与日志行数合计不超过 512 时整批提交。
        // 超限时按剩余行数提交关键角色和未覆盖线程，其余留待后续扫描。
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
            unifiedClearCoreNote = (skipped == 0 ? "" : "；" + skipped + " 个线程读不到或原范围只在目标核，已跳过")
                + (uncovered == 0 ? "" : "；会话记录行数已满，" + uncovered + " 个线程未接纳")
                + "；游戏线程总数=" + sample.Ids.Length;
        }

            // 前台每 10 秒检查新增线程；接纳累计失败三次后停止新增，保留已生效设置。
        private void AdmitClearCoreNewcomers(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, long now)
        {
            if (criticalSession.WorkerReclamationPending)
            {
                unifiedClearCoreCoverageKnown = false;
                unifiedClearCoreNote = "；" + criticalSession.WorkerReclamationDetail;
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
                    if (source.Rule.IsTimeCriticalRender) RecordClearCoreFailure(phase, "清核成员快照未通过身份核验");
                    return;
                }
                var covered = new HashSet<string>(criticalSession.ActiveReceipts.Select(receipt => receipt.Thread.Key), StringComparer.Ordinal);
                int newcomers = 0;
                for (int index = 0; index < all.Ids.Length; index++)
                    if (all.Ids[index] > 0 && all.Ids[index] <= int.MaxValue && all.Created[index] > 0
                        && !covered.Contains(new HardThreadIdentity((int)all.Ids[index], all.Created[index]).Key)) newcomers++;
                if (newcomers == 0) { RefreshClearCoreNote(all, 0); return; }
                HardThreadCoverage coverage;
                int uncovered;
                HardThreadIdentity[] batch = ClearCoreBatch(all, criticalPlan, covered, criticalSession.JournalRowCount, out coverage, out uncovered);
                // 关键角色退出由运行期核验处理；行数用完属于覆盖不足，都不算接纳失败。
                if (batch.Length <= criticalPlan.Choices.Length)
                {
                    if (batch.Length != 0) unifiedClearCoreNote = "；会话记录行数已满，" + newcomers + " 个新线程未接纳；游戏线程总数=" + all.Ids.Length;
                    return;
                }
                if (criticalSession.AdmitNewThreads(batch, coverage)) RefreshClearCoreNote(all, uncovered);
                else RecordClearCoreFailure(phase, criticalSession.Detail);
            }
            catch (Exception error)
            {
                RecordClearCoreFailure(phase, error.Message);
                Logger.RepeatedError("unified-clear-core", "清核接纳新线程未完成： " + error.Message);
            }
        }

        private void RecordClearCoreFailure(UnifiedSchedulingPhase phase, string detail)
        {
            unifiedClearCoreCoverageKnown = false;
            phase.ClearCoreFailures++;
            unifiedClearCoreNote = "；新线程接纳失败 " + phase.ClearCoreFailures + "/3：" + detail
                + (phase.ClearCoreFailures >= 3 ? "；已停止接纳新线程，已生效的固定与清核保持" : "");
        }

        private string DescribeUnifiedAffinity(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, bool maskKnown)
        {
            string note = string.IsNullOrEmpty(phase.AffinityNote) ? "" : "；" + phase.AffinityNote;
            if (criticalPlan == null) return "宽域，未接管掩码" + note;
            if (!maskKnown) return criticalSession.LastRuntimeDetail + note;
            string bound = criticalPlan.FloatingCandidates ? "渲染线程保持原范围，理想处理器指向预留核" : "指定 CPU 已读回";
            if (!criticalPlan.RequireWorkerCoverage) return bound + "；未清核，非整核独占" + note;
            ulong cleared = 0;
            foreach (HardThreadChoice choice in criticalPlan.Choices)
                foreach (ulong core in CpuTopology.PhysicalCoreMasks())
                    if ((core & choice.Mask) != 0) cleared |= core;
            return bound + "；清核：其余 " + unifiedClearedWorkers + " 个线程已让出 CPU" + CpuTopology.DescribeRanges(cleared)
                + unifiedClearCoreNote + note;
        }

        // 含实时读数的说明须提供稳定 logKey，避免每次采样都产生日志。
        private void PublishUnifiedState(CriticalRuntimeSource source, string state, string detail, bool applied, bool recoveryPending,
            string logKey = null)
        {
            if (source == null) return;
            logKey = logKey ?? detail;
            long now = AutoCores.NowTicks;
            string summary;
            string owner = source.Serial + "|" + source.Pid + "|" + source.Birth + "|" + source.Rule.Encode();
            // 每种原因首现即时记录；成功/冲突来回切换不重置各自窗口，变化读数只进详情。
            if (unifiedStateLog.Admit(owner, state, logKey, now,
                state == "active" ? UnifiedHeartbeatTicks : TimeSpan.FromMinutes(2).Ticks, out summary))
            {
                Logger.Info("游戏调度 状态=" + state + " 游戏=" + source.Name + " PID=" + source.Pid + " 创建=" + source.Birth
                    + " 规则=" + source.Rule.Encode() + "；" + detail
                    + (unifiedThreadControl == null ? "" : "；字段汇总：" + unifiedThreadControl.Describe()) + summary);
            }
            if (state == unifiedLastState && now < unifiedNextUiPublish) return;
            unifiedNextUiPublish = now + TimeSpan.FromSeconds(2).Ticks;
            unifiedLastState = state;
            string uiDetail = detail;
            if (Lang.Cur != 0)
            {
                uiDetail = state == "active" ? ThreadLayoutDescriptions.Text("", "Managed fields were read back. Saved rules remain under continuous correction.", "管理項目の読み戻しを確認しました。保存したルールを継続的に維持します。")
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
