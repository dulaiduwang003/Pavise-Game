using System;
using System.Linq;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool PrepareWorkloadRoles(CriticalRuntimeSource source, UnifiedSchedulingPhase phase,
            ThreadLayoutCaptureRequest request, ThreadTimes current, long now)
        {
            bool currentBaselineUsable = false;
            UnifiedThreadCaptureEvidence[] windows = request == null ? new UnifiedThreadCaptureEvidence[0]
                : request.ReadWorkloadBaselines(out currentBaselineUsable);
            // 到期仅代表可以开始采集 异步观察器尚未开始或仍在采集时 旧窗口不能代替已排队的确认
            if (windows.Length >= 2 && !currentBaselineUsable)
            {
                PublishUnifiedState(source, "observing", Logger.Text("等待本次工作组确认完成并冻结；旧窗口不授权新增写入；", "waiting for this workload confirmation to complete and freeze; old windows do not authorize new writes; ") + request.DescribeState(now), false, false);
                return false;
            }
            if (phase.PreviousWorkload != null && (windows.Length == 0 || windows[windows.Length - 1].Observed <= phase.WorkloadReviewAfter))
            {
                PublishUnifiedState(source, "observing", Logger.Text("等待阶段变化后的新工作图；历史基线不能授权新阶段写入", "waiting for a new workload graph after the phase change; historical baselines cannot authorize writes in the new phase"), false, false);
                return false;
            }
            if (windows.Length < 2)
            {
                bool queued = request != null && windows.Length == 1 && request.RequestWorkloadConfirmation(now);
                if (request != null && request.ObservationExhausted && !queued)
                {
                    phase.ObservationOnly = true;
                    phase.Evidence = Logger.Text("全线程工作组未取得两个完整基线，发现预算已用尽；未新增放置或提优", "the full-thread workload group did not get two complete baselines and the discovery budget is exhausted; no placement or boost added");
                }
                PublishUnifiedState(source, "observing", StrategyName(source.Rule) + Logger.Text("等待两次独立工作组确认；已取得=", ": waiting for two independent workload confirmations; obtained=") + windows.Length
                    + Logger.Text("；", "; ") + (request == null ? Logger.Text("采集请求不可用", "capture request unavailable") : request.DescribeState(now)), false, false);
                return false;
            }
            UnifiedWorkloadSet previous = phase.PreviousWorkload != null ? phase.PreviousWorkload
                : UnifiedWorkloadDiscovery.Select(windows[windows.Length - 2], current, source.Pid, source.Birth, now);
            UnifiedWorkloadSet selected = UnifiedWorkloadDiscovery.Select(windows[windows.Length - 1], current, source.Pid, source.Birth, now);
            // 工作组策略同样留下渲染线程的放置前读数 准入与否都保存 供方案窗口对照
            SchedulingBottleneckEvidence observed = NoteSchedulingEvidence(source, windows[windows.Length - 1]);
            if (source.Rule.IsDependencyBoost)
                return PrepareDependencyBoostRoles(source, phase, request, previous, selected,
                    windows[windows.Length - 2], windows[windows.Length - 1]);
            bool reviewed = phase.PreviousWorkload != null;
            string confirmation = DescribeWorkloadConfirmation(previous, selected);
            bool changed = !previous.Valid || !selected.Valid
                || WorkloadRoleKey(previous.Main) != WorkloadRoleKey(selected.Main)
                || WorkloadRoleKey(previous.Secondary) != WorkloadRoleKey(selected.Secondary)
                || WorkloadRoleKey(previous.Tertiary) != WorkloadRoleKey(selected.Tertiary);
            if (!reviewed && changed && request != null && request.RequestWorkloadRoleConfirmation(now))
            {
                phase.Workload = null;
                phase.AwaitingLoad = false;
                phase.Evidence = confirmation + Logger.Text("；尚未写入，使用原三次预算的最后一次确认，不要求总CPU负载升高", "; nothing written yet; using the last confirmation of the original three-attempt budget without requiring higher total CPU load");
                Logger.Info(Logger.Format("全线程编排 角色复核 {0} PID={1} 创建={2}；{3}", "All-thread orchestration role recheck {0} PID={1} birth={2}; {3}", source.Name, source.Pid, source.Birth, phase.Evidence));
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return false;
            }
            selected = ConfirmWorkloadRoles(previous, selected, reviewed);
            if (!selected.Valid || selected.Main == null && selected.Critical.Length == 0)
            {
                phase.Workload = selected;
                phase.ObservationOnly = true;
                phase.AwaitingLoad = false;
                phase.Evidence = confirmation + Logger.Text("；没有通过确认的固定角色；本阶段停止新增放置", "; no fixed role passed confirmation; new placement stopped for this phase");
                Logger.Info(Logger.Format("全线程编排 角色未准入 {0} PID={1} 创建={2}；{3}", "All-thread orchestration role not admitted {0} PID={1} birth={2}; {3}", source.Name, source.Pid, source.Birth, phase.Evidence));
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return false;
            }
            if (CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
            {
                phase.ObservationOnly = true;
                phase.Evidence = Logger.Text("全线程编排的物理拓扑不可核验；保留候选，不建立核心放置", "physical topology for full-thread orchestration cannot be verified; candidates retained, no core placement established");
                PublishUnifiedState(source, "unsupported", phase.Evidence, false, false);
                return false;
            }
            ulong mask;
            if (!ReadCriticalProcessMask(source.Pid, source.Birth, out mask) || mask == 0)
            {
                PublishUnifiedState(source, "observing", Logger.Text("全线程编排的当前进程范围未知；未建立写入资格", "current process scope for full-thread orchestration is unknown; no write eligibility established"), false, false);
                return false;
            }
            CriticalThreadAllowedRange scope;
            lock (sync) scope = CriticalThreadAllowedRange.For(CriticalPreferenceProfileLocked(source.Profile));
            if (scope.Error != null)
            {
                PublishUnifiedState(source, "unsupported", Logger.Text("全线程编排允许范围未确认；", "allowed range for full-thread orchestration unconfirmed; ") + scope.Error, false, false);
                return false;
            }
            ulong reserved = AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, CpuTopology.PhysicalCoreMasks());
            UnifiedWorkloadPlan plan = UnifiedWorkloadPlan.Create(selected, mask & scope.Mask & ~reserved,
                CpuTopology.PhysicalCoreMasks(), CpuTopology.PerfMask, CpuTopology.CriticalPlacementPreferredMask);
            phase.Workload = selected; phase.WorkloadPlan = plan;
            // 没有可独占核心的计划就只观察
            if (!plan.Valid || plan.Primary == null)
            {
                phase.ObservationOnly = true;
                phase.Evidence = Logger.Text("全线程放置未准入；", "full-thread placement not admitted; ") + plan.Reason + Logger.Text("；", "; ") + selected.Describe();
                // 自动模式记住结论 之后几局开局不再为它做完整采集
                if (source.Rule.IsOrchestration)
                {
                    WorkloadVerdictMemory.Remember(source.Profile, source.Path);
                    phase.Evidence += Logger.Text("；已记住本游戏不适合留核，", "; remembered that this game is unsuitable for reserved cores; no capture at game start for ") + WorkloadVerdictMemory.ValidDays + Logger.Text(" 天内或游戏更新前开局不再采集", " days or until the game updates");
                }
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return false;
            }
            phase.Roles = plan.Choices.Select(choice => choice.Thread).ToArray();
            // 这里不预留也不写线程优先级
            phase.PriorityRoles = new HardThreadIdentity[0];
            phase.ObservedRoles = plan.ObservedRoles;
            phase.ObservationOnly = phase.AwaitingLoad = false;
            phase.Evidence = confirmation + Logger.Text("；", "; ") + selected.Describe() + Logger.Text("；", "; ") + plan.Describe()
                + (reviewed ? Logger.Text("；已观察成员完成一次新阶段复核，全部属性重新读取原值；旧图仅核对成员身份", "; observed members completed one new-phase review and all attributes re-read original values; the old graph only checks member identity") : Logger.Text("；两次基线分别确认固定资格与提优身份", "; the two baselines separately confirmed fixed eligibility and boost identity"))
                + Logger.Text("；帧率收益未验证", "; FPS benefit unverified");
            Logger.Info(Logger.Format("全线程编排 角色准入 {0} PID={1} 创建={2} 规则={3}；{4}；{5}", "All-thread orchestration role admitted {0} PID={1} birth={2} rule={3}; {4}; {5}", source.Name, source.Pid, source.Birth, source.Rule.Encode(), phase.Evidence, observed.Describe()));
            // 候选明细作为一个事件保存 导出仍含每个线程的完整观测字段 避免逐线程追加文件和刷新列表
            Logger.Info(Logger.Format("全线程编排 候选批次 {0} PID={1} 创建={2} 数量={3}；明细=[{4}]", "All-thread orchestration candidate batch {0} PID={1} birth={2} count={3}; details=[{4}]", source.Name, source.Pid, source.Birth, selected.Observed.Length, string.Join(" || ", selected.Observed.Select(node => node.Describe()).ToArray())));
            return true;
        }

        private static string WorkloadRoleKey(UnifiedWorkloadNode node)
        { return node == null ? "无" : node.Thread.Key; }

        // 角色键要参与前后窗比较 只在拼说明时换成当前语言
        private static string WorkloadRoleText(UnifiedWorkloadNode node)
        { return node == null ? Logger.Text("无", "none") : node.Thread.Key; }

        private static string DescribeWorkloadConfirmation(UnifiedWorkloadSet previous, UnifiedWorkloadSet current)
        {
            bool mainChanged = WorkloadRoleKey(previous.Main) != WorkloadRoleKey(current.Main);
            bool secondaryChanged = WorkloadRoleKey(previous.Secondary) != WorkloadRoleKey(current.Secondary);
            bool tertiaryChanged = WorkloadRoleKey(previous.Tertiary) != WorkloadRoleKey(current.Tertiary);
            return Logger.Text("前窗主=", "previousMain=") + WorkloadRoleText(previous.Main) + Logger.Text(" 副=", " secondary=") + WorkloadRoleText(previous.Secondary) + Logger.Text(" 第三=", " tertiary=") + WorkloadRoleText(previous.Tertiary)
                + Logger.Text("；后窗主=", "; currentMain=") + WorkloadRoleText(current.Main) + Logger.Text(" 副=", " secondary=") + WorkloadRoleText(current.Secondary) + Logger.Text(" 第三=", " tertiary=") + WorkloadRoleText(current.Tertiary)
                + Logger.Text("；差异=", "; difference=") + (!previous.Valid || !current.Valid ? Logger.Text("工作图无效", "workload graph invalid") : mainChanged && secondaryChanged ? Logger.Text("主副角色变化", "main and secondary roles changed")
                    : mainChanged ? Logger.Text("主角色变化", "main role changed") : secondaryChanged ? Logger.Text("副角色变化", "secondary role changed") : tertiaryChanged ? Logger.Text("第三角色变化", "tertiary role changed") : Logger.Text("角色身份一致", "role identities consistent"))
                + Logger.Text("；前窗依据=", "; previousBasis=") + previous.Reason + Logger.Text("；后窗依据=", "; currentBasis=") + current.Reason;
        }

        private static UnifiedWorkloadSet ConfirmWorkloadRoles(UnifiedWorkloadSet previous, UnifiedWorkloadSet current, bool reviewed)
        {
            if (!previous.Valid || !current.Valid)
                return new UnifiedWorkloadSet(false, Logger.Text("前后工作图未同时有效", "previous and current workload graphs are not both valid"), null, null, null, 0, 0, false, 0);
            var priorRoles = new[] { previous.Main, previous.Secondary, previous.Tertiary }.Where(node => node != null).ToArray();
            var currentRoles = new[] { current.Main, current.Secondary, current.Tertiary }.Where(node => node != null).ToArray();
            // 固定资格只取两窗各自合格角色的身份交集 保留前窗顺序 排名互换不触发主副抖动
            var fixedKeys = reviewed ? currentRoles.Where(node => previous.Observed.Any(prior => prior.Thread.Key == node.Thread.Key))
                    .Select(node => node.Thread.Key).ToArray()
                : priorRoles.Where(node => currentRoles.Any(next => next.Thread.Key == node.Thread.Key)).Select(node => node.Thread.Key).ToArray();
            // 提优与固定资格独立 首次确认要求两窗均为关键成员 不能借用一次CPU排名或无关观察身份
            var priorPriority = reviewed ? previous.Observed : previous.Critical;
            var nodes = current.Observed.Select(node => new UnifiedWorkloadNode(node.Metrics, node.Kind, node.CpuShare,
                node.Priority && priorPriority.Any(prior => prior.Thread.Key == node.Thread.Key), node.Reason)).ToArray();
            var fixedRoles = fixedKeys.Select(key => nodes.First(node => node.Thread.Key == key)).ToArray();
            UnifiedWorkloadNode main = fixedRoles.Length == 0 ? null : fixedRoles[0];
            UnifiedWorkloadNode secondary = fixedRoles.Length < 2 ? null : fixedRoles[1];
            UnifiedWorkloadNode tertiary = fixedRoles.Length < 3 ? null : fixedRoles[2];
            string detail = Logger.Text("已确认固定角色=", "confirmedFixedRoles=") + (fixedRoles.Length == 0 ? Logger.Text("无", "none") : string.Join(",", fixedKeys));
            return new UnifiedWorkloadSet(true, detail, main, secondary, nodes, current.CandidateCoverage,
                current.TotalRunningCores, current.Complete, current.TrackedThreads, current.Ledger,
                current.LedgerCoverage, current.WindowMs, current.LedgerComplete, current.LedgerCapacityComplete, current.CapacityGap, tertiary);
        }

        // 本模式只接受有呈现锚点的小型依赖组 不凭主副核排名 核数或工作池吞吐授予提优资格
        private bool PrepareDependencyBoostRoles(CriticalRuntimeSource source, UnifiedSchedulingPhase phase,
            ThreadLayoutCaptureRequest request, UnifiedWorkloadSet previous, UnifiedWorkloadSet selected,
            UnifiedThreadCaptureEvidence previousWindow, UnifiedThreadCaptureEvidence currentWindow)
        {
            bool reviewed = phase.PreviousWorkload != null;
            HardThreadIdentity priorPresenter = DependencyPresenter(previousWindow, previous);
            HardThreadIdentity presenter = DependencyPresenter(currentWindow, selected);
            string failure = !previous.Valid || !selected.Valid ? Logger.Text("前后工作图尚未有效；前窗=", "previous and current workload graphs not yet valid; previous=") + previous.Reason + Logger.Text("；后窗=", "; current=") + selected.Reason
                : !previous.Complete || !selected.Complete ? Logger.Text("工作图有截断或候选身份缺失，不能据此确认完整小组", "workload graph is truncated or candidate identities are missing; a complete group cannot be confirmed from it")
                : priorPresenter == null || presenter == null ? Logger.Text("未取得稳定、完整的单来源呈现身份", "no stable, complete single-source presenter identity obtained")
                : priorPresenter.Key != presenter.Key ? Logger.Text("呈现身份发生变化，剩余单次复核不能替代新身份的两窗确认", "presenter identity changed; the single remaining review cannot replace two-window confirmation of the new identity") : null;
            HardThreadIdentity[] roles = new HardThreadIdentity[0];
            if (failure == null)
            {
                var priorMembers = previous.Observed.Where(node => reviewed
                    || node.Priority && node.Kind != UnifiedWorkloadKind.ParallelWorker)
                    .Select(node => node.Thread.Key).ToArray();
                HardThreadIdentity[] dependencies = selected.Critical.Where(node => node.Thread.Key != presenter.Key
                    && node.Kind != UnifiedWorkloadKind.ParallelWorker && priorMembers.Contains(node.Thread.Key))
                    .Select(node => node.Thread).ToArray();
                roles = new[] { presenter }.Concat(dependencies).ToArray();
                if (dependencies.Length == 0) failure = Logger.Text("未取得重复确认的非呈现依赖成员，不把单线程提优当作依赖组", "no repeatedly confirmed non-presenter dependency member; a single-thread boost is not treated as a dependency group");
                else if (roles.Length > 4) failure = Logger.Text("呈现依赖组超过4个线程，不按CPU排名截取或扩大整池提优", "presenter dependency group exceeds 4 threads; not trimmed by CPU rank or widened into a pool-wide boost");
            }
            phase.Workload = selected;
            phase.WorkloadPlan = null;
            if (failure != null)
            {
                phase.ObservationOnly = true;
                phase.AwaitingLoad = request != null && request.RejudgeAvailable;
                phase.Evidence = Logger.Text("依赖组未准入；", "dependency group not admitted; ") + failure + Logger.Text("；原优先级和核心范围保留", "; original priorities and core ranges retained");
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return false;
            }
            // 呈现锚点也属于提优组 退出由已有字段维护读回识别 不另加身份轮询
            phase.Roles = phase.PriorityRoles = phase.ObservedRoles = roles;
            phase.RenderTarget = 0; phase.RenderClear = false; phase.AffinityNote = null;
            phase.ObservationOnly = phase.AwaitingLoad = false;
            phase.Evidence = Logger.Text("呈现依赖组=", "presenterDependencyGroup=") + string.Join(",", roles.Select(role => role.Key).ToArray())
                + Logger.Text("；相对优先级目标+1，保留更高原值；不固定核心、不设置理想处理器、不清核", "; relative priority target +1, higher original values kept; no core pinning, no ideal processor, no core clearing")
                + (reviewed ? Logger.Text("；原呈现身份不变，新窗口复核已观察成员；旧图仅核对成员身份", "; original presenter identity unchanged; the new window reviewed observed members; the old graph only checks member identity")
                    : Logger.Text("；两窗呈现身份与依赖资格重复确认", "; presenter identity and dependency eligibility confirmed in both windows"))
                + Logger.Text("；依赖线索不等于已证明的帧关键路径，收益待实测", "; dependency hints are not a proven frame-critical path; benefit awaits measurement");
            Logger.Info(Logger.Format("依赖组提优 角色准入 游戏={0} PID={1} 创建={2} 规则={3}；{4}；{5}", "Dependency group boost role admitted game={0} PID={1} birth={2} rule={3}; {4}; {5}", source.Name, source.Pid, source.Birth, source.Rule.Encode(), phase.Evidence, NoteSchedulingEvidence(source, currentWindow).Describe()));
            return true;
        }

        private static HardThreadIdentity DependencyPresenter(UnifiedThreadCaptureEvidence window, UnifiedWorkloadSet selected)
        {
            if (window == null || !window.Valid || !window.StreamsKnown || window.Streams != 1 || window.Presenter == null
                || window.Relation == null || window.Relation.LostEvents != 0 || window.Relation.OverflowedThreads != 0
                || window.Relation.WorkloadTruncated || selected == null || !selected.Valid) return null;
            UnifiedWorkloadNode anchor = selected.Observed.FirstOrDefault(node => node.Kind == UnifiedWorkloadKind.Presenter
                && node.Thread.Key == window.Presenter.Key);
            return anchor != null && UnifiedWorkloadDiscovery.Stable(window.Relation, anchor.Metrics) ? anchor.Thread : null;
        }

        private static string DescribeWorkloadPlan(UnifiedSchedulingPhase phase)
        {
            return phase.WorkloadPlan.Describe()
                + Logger.Text("；未写成员首次接管=", "; unwrittenMemberAdmissions=") + phase.PlacementAdmissionAttempts + "/3"
                + Logger.Text("；仅已确认成员持续维护，未声明系统独占", "; only confirmed members are maintained; no system exclusivity claimed");
        }

        private void ReviewExitedRoles(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, ThreadLayoutCaptureRequest request, long now)
        {
            if (!WithdrawCriticalThreadsLocked(Logger.Text("关键角色已退出，先撤回原计划及清核限制，再检查剩余复核预算", "critical role exited; withdrawing the original plan and core-clearing limits before checking the remaining review budget")))
            {
                PublishUnifiedState(source, "recovery-pending", Logger.Text("关键角色退出；原计划恢复未结清，未启动新角色观察或写入", "critical role exited; restore of the original plan is unsettled; no new role observation or writes started"), false, true);
                return;
            }
            phase.RoleExitPending = false;
            phase.PreviousWorkload = phase.Workload;
            phase.WorkloadReviewAfter = now;
            phase.Workload = null; phase.WorkloadPlan = null;
            phase.Roles = phase.PriorityRoles = phase.ObservedRoles = null;
            phase.RenderTarget = 0; phase.RenderClear = false; phase.AffinityNote = null;
            phase.AwaitingLoad = false;
            // 恢复结清后才能使新证据获得准入资格 沿用原阶段全部预算 不沿用旧线程的原值或核心选择
            bool queued = phase.RoleReviewAllowed && request != null && (source.Rule.IsRenderStrategy
                ? request.RequestRenderRoleReview(now) : request.RequestWorkloadRoleReview(now));
            phase.RoleReviewAllowed = false;
            phase.ObservationOnly = !queued;
            phase.Evidence = queued ? Logger.Text("原角色退出，原计划及清核归属已结清；等待剩余次数取得的新角色证据", "original role exited; original plan and core-clearing ownership settled; waiting for new role evidence from remaining attempts")
                : Logger.Text("原角色退出；无剩余角色复核资格，原计划及清核归属已结清", "original role exited; no role review eligibility left; original plan and core-clearing ownership settled");
            criticalObservation.Reset();
            unifiedClearedWorkers = unifiedClearCoreQuickScans = 0; unifiedClearCoreNote = "";
            unifiedNextCpuSample = unifiedNextClearCoreScan = 0;
            PublishUnifiedState(source, "observing", phase.ObservationOnly ? phase.Evidence
                : Logger.Text("原计划已结清；使用本阶段剩余一次角色复核，不凭新线程号沿用旧原值", "original plan settled; using this phase's one remaining role review without carrying old original values over by new thread ID"), false, false);
        }

    }
}
