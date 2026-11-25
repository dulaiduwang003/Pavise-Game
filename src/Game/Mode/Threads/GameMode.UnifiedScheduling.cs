using System;
using System.Collections.Generic;
using System.Linq;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class UnifiedSchedulingPhase
        {
            internal int InitialAttempts;
            internal long NextInitial;
            internal HardThreadIdentity[] Roles;
            internal string Evidence;
            internal bool ObservationOnly;
            // 逻辑 CPU 掩码 0 表示本阶段无法放置
            internal ulong RenderTarget;
            internal bool RenderClear;
            // 本局没能放置时的原因说明 供界面详情和日志使用
            internal string AffinityNote, PreflightError;
            // 新线程接纳仅失败时扣次数 预算归属原阶段 规则往返不补充
            internal int ClearCoreFailures;
            internal bool RenderReviewUsed, RoleExitPending, RoleReviewAllowed;
            internal readonly HardThreadWorkerAdmissionBudget WorkerAdmissionBudget = new HardThreadWorkerAdmissionBudget();
        }

        private readonly Dictionary<string, UnifiedSchedulingPhase> unifiedSchedulingPhases = new Dictionary<string, UnifiedSchedulingPhase>(StringComparer.Ordinal);
        private long unifiedPhaseSerial, unifiedNextRoleCheck, unifiedNextAudit, unifiedNextUiPublish, unifiedNextClearCoreScan;
        private int unifiedClearedWorkers, unifiedClearCoreQuickScans;
        private bool unifiedClearCoreCoverageKnown;
        private string unifiedClearCoreNote = "";
        private string unifiedLastState;
        internal static readonly long UnifiedHeartbeatTicks = TimeSpan.FromMinutes(10).Ticks;

        private readonly SchedulingDiagnosticLog unifiedStateLog = new SchedulingDiagnosticLog();
        // 按游戏和模式保留最近一次已准入的放置前读数
        private readonly Dictionary<string, RenderAffinityObservation> singleCoreObservations =
            new Dictionary<string, RenderAffinityObservation>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RenderAffinityObservation> manualObservations =
            new Dictionary<string, RenderAffinityObservation>(StringComparer.OrdinalIgnoreCase);

        private void NoteSchedulingEvidence(CriticalRuntimeSource source, UnifiedThreadCaptureEvidence baseline)
        {
            bool manual = source.Rule.IsManualPin;
            var observations = manual ? manualObservations : singleCoreObservations;
            var observation = new RenderAffinityObservation(baseline.SampledAt, baseline.Presenter.Tid, baseline.Streams);
            lock (observations) observations[source.Profile] = observation;
        }

        internal RenderAffinityObservation LastSchedulingEvidence(string profileId, bool manual)
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            var observations = manual ? manualObservations : singleCoreObservations;
            RenderAffinityObservation observed;
            lock (observations) return observations.TryGetValue(profileId, out observed) ? observed : null;
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
                    unifiedNextRoleCheck = unifiedNextAudit = unifiedNextUiPublish = 0;
                    unifiedLastState = null;
                }
                if (source == null || !source.Rule.PinsRenderThread)
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
                UnifiedSchedulingPhase phase = UnifiedPhase(source);
                if (criticalSession == null && threadLayoutIdentity != ThreadLayoutIdentity(source))
                {
                    if (phase.PreflightError == null) phase.PreflightError = RenderAffinityPreflightError(source);
                    if (phase.PreflightError != null)
                    {
                        PublishUnifiedState(source, "unsupported", phase.PreflightError, false, false);
                        return true;
                    }
                }
                BeginThreadLayoutDiagnostics(source);
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
                bool observingRequest = request != null && request.NeedsObserver(now) && foreground;
                // 已在途的观察要完成原窗口 不另开采集
                bool captureInFlight = observingRequest && !request.NeedsCapture(now);
                if (captureInFlight) WantCriticalRoles(source);
                if (phase.ObservationOnly)
                {
                    bool released = criticalSession == null || RestoreCriticalThreadsLocked();
                    string detail = Logger.Text("调度观察；", "Scheduling observation; ") + phase.Evidence
                        + (released ? Logger.Text("；已停止线程扫描", "; thread scanning stopped") : Logger.Text("；恢复归属尚未结清", "; restore ownership not yet settled"));
                    PublishUnifiedState(source, released ? "observing" : "recovery-pending", detail, false, !released);
                    return true;
                }
                if (criticalSession != null)
                {
                    if (now < unifiedNextAudit) return true;
                    unifiedNextAudit = now + TimeSpan.FromSeconds(2).Ticks;
                    bool maskKnown = criticalPlan == null || criticalSession.MaintainApplied(now);
                    if (criticalSession.ThreadHasExited)
                    {
                        // 重识别可以耗尽 旧角色退出后的恢复不能因此跳过 未知读值不构成退出证明
                        phase.RoleExitPending = true;
                        phase.RoleReviewAllowed = !phase.RenderReviewUsed;
                        phase.RenderReviewUsed = true;
                        ReviewExitedRoles(source, phase, request, now);
                        return true;
                    }
                    if (criticalPlan != null && criticalPlan.RequireWorkerCoverage) AdmitClearCoreNewcomers(source, phase, now);
                    string detail = Logger.Text("策略=", "strategy=") + StrategyName(source.Rule) + Logger.Text("；运行期纠偏=无限；原角色冻结", "; runtimeCorrection=unlimited; original roles frozen")
                        + Logger.Text("；亲和性=", "; affinity=") + DescribeUnifiedAffinity(phase, maskKnown)
                        + Logger.Text("；本计划仅维护 CPU 范围", "; this plan only maintains CPU ranges");
                    bool allKnown = maskKnown && criticalPlan != null;
                    // 复用现有核验结果 保留前台角色复核的身份确认
                    if (request != null) request.ObserveIdentitySnapshot(allKnown, now);
                    PublishUnifiedState(source, allKnown ? "active" : criticalPlan != null ? "conflict" : "observing", detail, allKnown, false);
                    return true;
                }
                if (CriticalThreadHasRecoveryDebt(false))
                {
                    PublishUnifiedState(source, "recovery-pending", Logger.Text("旧线程恢复记录尚未结清，未新增控制", "Old thread restore records not yet settled, no new control"), false, true);
                    return true;
                }
                if (!foreground)
                {
                    PublishUnifiedState(source, "observing", Logger.Text("等待游戏返回前台；既有规则与采样次数保留", "Waiting for the game to return to the foreground; existing rules and capture counts retained"), false, false);
                    return true;
                }
                if (phase.Roles == null && request == null)
                {
                    PublishUnifiedState(source, "observing", Logger.Text("本局无法建立有界角色观察；未启动线程扫描或新增写入", "Bounded role observation cannot be set up this session; no thread scan started and no new writes"), false, false);
                    return true;
                }
                if (phase.InitialAttempts >= 3)
                {
                    PublishUnifiedState(source, "conflict", Logger.Text("首次接管次数已用完；本局停止新增接管，保留恢复归属", "Initial acquisition attempts used up; no new takeovers this session, restore ownership retained"), false, false);
                    return true;
                }
                if (phase.Roles == null && !request.HasUsableBaseline)
                {
                    // 采集前只核验进程身份 呈现线程身份由采集器确认
                    if (captureInFlight || now < unifiedNextRoleCheck) return true;
                    bool needsObserver = observingRequest;
                    if (!needsObserver && request.NeedsIdentitySnapshot(now))
                    {
                        unifiedNextRoleCheck = now + TimeSpan.FromSeconds(5).Ticks;
                        long captureDue = request.NextCaptureDueTicks;
                        if (now < captureDue) unifiedNextRoleCheck = Math.Min(unifiedNextRoleCheck, captureDue);
                        request.ObserveIdentitySnapshot(NativeFrameThreads.ProcessState(source.Pid, source.Birth) == 0, now);
                        needsObserver = request.NeedsObserver(now);
                    }
                    if (needsObserver) WantCriticalRoles(source);
                    string detail = request.DescribeState(now)
                        + Logger.Text("；等待期间不枚举线程，观察额度耗尽后停止角色采样", "; threads are not enumerated while waiting, role capture stops once the observation budget is used up");
                    PublishUnifiedState(source, "observing", detail, false, false);
                    return true;
                }
                if (now < phase.NextInitial || now < unifiedNextRoleCheck) return true;
                UnifiedThreadCaptureEvidence baseline = phase.Roles == null ? request.ReadUnifiedBaseline() : null;
                if (baseline != null && now - baseline.Observed > TimeSpan.FromSeconds(65).Ticks)
                {
                    phase.ObservationOnly = true;
                    phase.Evidence = Logger.Text("角色证据已超过准入新鲜度；停止原基线的线程核验，保留本阶段采集预算",
                        "Role evidence is past admission freshness; thread verification for the original baseline stopped, existing capture budget retained");
                    PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                    return true;
                }
                unifiedNextRoleCheck = now + TimeSpan.FromSeconds(5).Ticks;
                if (phase.Roles == null)
                {
                    ThreadTimes snapshot = baseline == null || baseline.Presenter == null ? null
                        : UnifiedThreadRuntimeSampling.Read(source.Pid, source.Birth, baseline.Presenter);
                    long sampledAt = AutoCores.NowTicks;
                    string reason;
                    HardThreadIdentity[] roles = UnifiedThreadAdmissionPolicy.Select(baseline, snapshot, source.Pid, source.Birth, sampledAt, out reason);
                    request.ObserveIdentitySnapshot(roles.Length != 0, now);
                    if (roles.Length == 0)
                    {
                        PublishUnifiedState(source, "observing", reason, false, false);
                        return true;
                    }
                    phase.Roles = roles;
                    NoteSchedulingEvidence(source, baseline);
                    phase.Evidence = reason + Logger.Text("；", "; ") + (source.Rule.IsManualPin
                        ? Logger.Text("手动亲和性：按所选逻辑 CPU 调整呈现线程的核心范围", "Manual affinity adjusts the presenter's CPU range to the selected logical processors")
                        : Logger.Text("渲染单核：按物理核选择结果调整呈现线程的核心范围", "Single render core adjusts the presenter's CPU range based on physical-core selection"));
                    PrepareRenderAffinity(source, phase);
                    if (!string.IsNullOrEmpty(phase.AffinityNote)) phase.Evidence += Logger.Text("；", "; ") + phase.AffinityNote;
                    phase.ObservationOnly = phase.RenderTarget == 0;
                    Logger.Info(Logger.Format("游戏调度 角色准入： {0} PID={1} 创建={2} 规则={3} 角色={4} 目标CPU={5} 计划整核避让={6}；{7}；性能收益需实测",
                        "Game scheduling role admission: {0} PID={1} birth={2} rule={3} role={4} targetCpu={5} plannedWholeCoreAvoidance={6}; {7}; performance gains require measurement",
                        source.Name, source.Pid, source.Birth, source.Rule.Encode(), roles[0].Key,
                        phase.RenderTarget == 0 ? Logger.Text("无", "none") : TargetProcessors(phase.RenderTarget),
                        phase.RenderClear ? Logger.Text("开", "on") : Logger.Text("关", "off"), phase.Evidence));
                }
                else
                {
                    HardThreadRead identity = UnifiedThreadRuntimeSampling.Check(source.Pid, source.Birth, phase.Roles[0]);
                    if (request != null) request.ObserveIdentitySnapshot(identity == HardThreadRead.Known, now);
                    if (identity == HardThreadRead.Unknown)
                    {
                        PublishUnifiedState(source, "observing", Logger.Text("原呈现线程身份暂不可核验；等待下一轮", "The original presenter identity is temporarily unavailable; waiting for the next check"), false, false);
                        return true;
                    }
                    if (identity == HardThreadRead.Gone)
                    {
                        phase.InitialAttempts = 3;
                        PublishUnifiedState(source, "observing", Logger.Text("原呈现线程已退出或身份已变化；本局停止新增接管", "The original presenter exited or changed identity; no further takeovers this session"), false, false);
                        return true;
                    }
                }
                if (phase.ObservationOnly)
                {
                    PublishUnifiedState(source, "observing", Logger.Text("本阶段接管条件未满足；", "Takeover conditions not met for this phase; ") + phase.Evidence, false, false);
                    return true;
                }
                phase.InitialAttempts++;
                phase.NextInitial = now + TimeSpan.FromSeconds(10).Ticks;
                StartUnifiedThreadControl(source, phase);
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

        private void ReviewExitedRoles(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, ThreadLayoutCaptureRequest request, long now)
        {
            if (!WithdrawCriticalThreadsLocked(Logger.Text("关键角色已退出，先撤回原计划及清核限制，再检查剩余复核预算", "critical role exited; withdrawing the original plan and core-clearing limits before checking the remaining review budget")))
            {
                PublishUnifiedState(source, "recovery-pending", Logger.Text("关键角色退出；原计划恢复未结清，未启动新角色观察或写入", "critical role exited; restore of the original plan is unsettled; no new role observation or writes started"), false, true);
                return;
            }
            phase.RoleExitPending = false;
            phase.Roles = null;
            phase.RenderTarget = 0; phase.RenderClear = false; phase.AffinityNote = null;
            // 恢复结清后才能使新证据获得准入资格 沿用原阶段全部预算 不沿用旧线程的原值或核心选择
            bool queued = phase.RoleReviewAllowed && request != null && request.RequestRenderRoleReview(now);
            phase.RoleReviewAllowed = false;
            phase.ObservationOnly = !queued;
            phase.Evidence = queued ? Logger.Text("原角色退出，原计划及清核归属已结清；等待剩余次数取得的新角色证据", "original role exited; original plan and core-clearing ownership settled; waiting for new role evidence from remaining attempts")
                : Logger.Text("原角色退出；无剩余角色复核资格，原计划及清核归属已结清", "original role exited; no role review eligibility left; original plan and core-clearing ownership settled");
            unifiedClearedWorkers = unifiedClearCoreQuickScans = 0; unifiedClearCoreNote = "";
            unifiedNextRoleCheck = unifiedNextClearCoreScan = 0;
            PublishUnifiedState(source, "observing", phase.ObservationOnly ? phase.Evidence
                : Logger.Text("原计划已结清；使用本阶段剩余一次角色复核，不凭新线程号沿用旧原值", "original plan settled; using this phase's one remaining role review without carrying old original values over by new thread ID"), false, false);
        }

        private void StartUnifiedThreadControl(CriticalRuntimeSource source, UnifiedSchedulingPhase phase)
        {
            IntPtr process = Native.OpenProcess(0x1000, false, source.Pid);
            if (process == IntPtr.Zero) throw new InvalidOperationException(Logger.Text("游戏进程查询句柄不可用", "Game process query handle unavailable"));
            try
            {
                ulong mask = 0;
                if (!Native.TryQueryAffinity(process, out mask) || mask == 0)
                    throw new InvalidOperationException(Logger.Text("游戏当前允许范围不可核验", "Game's current allowed range cannot be verified"));
                StartThreadLayoutDiagnostics(source, mask);
                var pass = new BoostPass { RendererPid = source.Pid, RendererCreation = source.Birth, RendererName = source.Name };
                CriticalThreadTargetLease target = CreateCriticalThreadTarget(pass, process, source.Rule, source.Profile, source.RuleText,
                    mask, source.Epoch, source.Serial);
                if (target == null) throw new InvalidOperationException(Logger.Text("游戏身份、范围或当前用户规则未通过写入授权核验", "Game identity, range or current user rule failed write authorization verification"));
                criticalSession = CriticalThreadHardAffinitySession.OpenNative(target);
                criticalSession.UseWorkerAdmissionBudget(phase.WorkerAdmissionBudget);
                criticalRestorationConfirmed = false;
                criticalThreadResidue = true;
                unifiedClearedWorkers = 0;
                unifiedClearCoreCoverageKnown = false;
                unifiedClearCoreNote = "";
                HardThreadChoice[] choices = { new HardThreadChoice(phase.Roles[0], phase.RenderTarget) };
                if (phase.RenderClear) ApplyClearCore(source, choices);
                else
                {
                    criticalPlan = criticalSession.CreatePlan(choices, false, false, null, true);
                    if (!criticalSession.Apply(criticalPlan, phase.Roles, HardThreadCoverage.CandidatesOnly))
                        throw new InvalidOperationException(Logger.Text("渲染线程首次固定未核验：", "Initial render thread pin not verified: ") + criticalSession.Detail);
                }
                PublishBackgroundCoreReservation(source);
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
            string name = rule.IsManualPin ? Logger.Text("手动亲和性", "Manual affinity") : Logger.Text("渲染单核", "Single render core");
            return name + (rule.ClearCore ? Logger.Text("+整核避让", "+whole-core avoidance") : "");
        }

        // 固定目标的位数 只有手动可以是多个逻辑 CPU 而且是 1 到 4 个 渲染单核选出来的必须是单个 CPU
        // 硬绑定计划本身接受多位候选 这里把住只有手动多选才会交出多位目标 返回空表示合规
        private static string RenderTargetShapeError(CriticalThreadRule rule, ulong target)
        {
            int count = AutoCores.CountBits(target);
            if (rule.IsManualPin)
                return count >= 1 && count <= CriticalThreadRule.MaximumIsolated ? null
                    : Logger.Text("手动所选 CPU 有 ", "Manual selection has ") + count + Logger.Text(" 个，只支持 1 到 ", " CPUs; only 1 to ") + CriticalThreadRule.MaximumIsolated + Logger.Text(" 个", " are supported");
            return count == 1 ? null : Logger.Text("渲染单核选出的目标不是单个 CPU（0x", "the render single-core target is not a single CPU (0x") + target.ToString("X") + Logger.Text("）", ")");
        }

        // 开始观察前只核验固定条件 实时进程范围和核心占用留到准入时核验
        private string RenderAffinityPreflightError(CriticalRuntimeSource source)
        {
            ulong all = CpuTopology.AllMask;
            ulong[] physical = CpuTopology.PhysicalCoreMasks();
            if (IntPtr.Size != 8 || CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree
                || CpuTopology.AllMaskReconciled || all == 0 || physical.Length == 0)
                return Logger.Text("处理器拓扑不可核验", "Processor topology cannot be verified");
            CriticalThreadRule rule = source.Rule;
            if (rule.IsManualPin && rule.Topology != ManualCorePlan.Stamp(all, physical))
                return Logger.Text("处理器拓扑已变化，请重新选择手动 CPU", "Processor topology changed, choose the manual CPUs again");
            CriticalThreadAllowedRange scope;
            lock (sync) scope = CriticalThreadAllowedRange.FromProfile(CriticalPreferenceProfileLocked(source.Profile),
                all, physical, 0, AutoCores.Supported, true);
            if (scope.Error != null) return scope.Error;
            return rule.IsManualPin && (rule.ManualMask & scope.Mask) != rule.ManualMask
                ? Logger.Text("所选 CPU 不在本局配置的游戏核心范围内", "Selected CPUs are outside this session's configured game core range")
                : null;
        }

        // 渲染单核与手动的选核 渲染单核选一颗最合适的物理核 手动核验所选 CPU 仍在游戏范围内
        // 只在准入时选一次 选不出就本局不放置 原因写进 AffinityNote
        private void PrepareRenderAffinity(CriticalRuntimeSource source, UnifiedSchedulingPhase phase)
        {
            phase.RenderTarget = 0;
            phase.RenderClear = false;
            phase.AffinityNote = null;
            string skipped = Logger.Text("本局不固定核心", "no core pinned this session") + (source.Rule.ClearCore ? Logger.Text("、不清核", ", no core clearing") : "");
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
                target = RenderIsolationPlan.Choose(allowed, physical, CpuTopology.PerfMask, CpuTopology.CriticalPlacementPreferredMask, out reason);
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
            phase.RenderTarget = target;
        }

        // 先收紧其他游戏线程的原范围 让出目标物理核及 SMT 同核线程 再安置关键角色
        // 不可读或无法避让的线程记为覆盖不足 批次受剩余日志容量限制 后续扫描补齐
        private void ApplyClearCore(CriticalRuntimeSource source, HardThreadChoice[] choices)
        {
            criticalPlan = criticalSession.CreatePlan(choices, true, true);
            ThreadTimes all = ProcessSnapshotSource.SampleThreadIdentities(source.Pid, source.Birth, 0);
            if (!CriticalThreadRuntimePolicy.Valid(all, source.Birth, AutoCores.NowTicks))
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
            Logger.Log(() => Logger.Format("游戏调度 {0} 清核已生效：渲染线程固定到 {1}，其余 {2} 个线程让出所在物理核，可用范围=0x{3}{4}；退局逐线程恢复原值", "Game scheduling {0} core clearing active: render thread pinned to {1}, the other {2} threads yield their physical cores, allowedMask=0x{3}{4}; exit restores original values per thread", source.Name, TargetProcessors(choices[0].Mask), unifiedClearedWorkers, criticalPlan.Workers.ToString("X"), unifiedClearCoreNote));
        }

        // 批内须包含关键角色 线程数与日志行数合计不超过 512 时整批提交
        // 超限时按剩余行数提交关键角色和未覆盖线程 其余留待后续扫描
        private static HardThreadIdentity[] ClearCoreBatch(ThreadTimes sample, HardThreadPlan plan, HashSet<string> covered,
            int journalRows, out HardThreadCoverage coverage, out int uncovered)
        {
            coverage = HardThreadCoverage.Partial;
            uncovered = 0;
            if (sample == null || !CriticalThreadRuntimePolicy.ValidShape(sample)) return new HardThreadIdentity[0];
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
            unifiedClearCoreCoverageKnown = uncovered == 0 && CriticalThreadRuntimePolicy.ValidShape(sample);
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
                ThreadTimes all = ProcessSnapshotSource.SampleThreadIdentities(source.Pid, source.Birth, 1500);
                if (!CriticalThreadRuntimePolicy.Valid(all, source.Birth, AutoCores.NowTicks)) return;
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

        // 覆盖不完整时允许 2 秒后补扫
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

        private string DescribeUnifiedAffinity(UnifiedSchedulingPhase phase, bool maskKnown)
        {
            string note = string.IsNullOrEmpty(phase.AffinityNote) ? "" : Logger.Text("；", "; ") + phase.AffinityNote;
            if (criticalPlan == null) return Logger.Text("宽域，未接管掩码", "Wide scope, mask not managed") + note;
            if (!maskKnown) return criticalSession.LastRuntimeDetail + note;
            string bound = Logger.Text("指定 CPU 已读回", "Assigned CPU read back");
            if (!criticalPlan.RequireWorkerCoverage) return bound + Logger.Text("；未清核，非整核独占", "; no core clearing, not whole-core exclusive") + note;
            ulong cleared = 0;
            foreach (HardThreadChoice choice in criticalPlan.Choices)
                foreach (ulong core in CpuTopology.PhysicalCoreMasks())
                    if ((core & choice.Mask) != 0) cleared |= core;
            return bound + Logger.Text("；清核：其余 ", "; core clearing: the other ") + unifiedClearedWorkers + Logger.Text(" 个线程已让出 CPU", " threads vacated CPU") + CpuTopology.DescribeRanges(cleared)
                + unifiedClearCoreNote + note;
        }

        private void PublishUnifiedState(CriticalRuntimeSource source, string state, string detail, bool applied, bool recoveryPending)
        {
            if (source == null) return;
            long now = AutoCores.NowTicks;
            string summary;
            string owner = source.Serial + "|" + source.Pid + "|" + source.Birth + "|" + source.Rule.Encode();
            // 每种原因首现即时记录 成功/冲突来回切换不重置各自窗口 变化读数只进详情
            if (unifiedStateLog.Admit(owner, state, detail, now,
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
