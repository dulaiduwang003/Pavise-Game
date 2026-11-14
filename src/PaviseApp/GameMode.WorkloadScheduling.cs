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
            // 到期仅代表可以开始采集；异步观察器尚未开始或仍在采集时，旧窗口不能代替已排队的确认。
            if (windows.Length >= 2 && !currentBaselineUsable)
            {
                PublishUnifiedState(source, "observing", "等待本次工作组确认完成并冻结；旧窗口不授权新增写入；" + request.DescribeState(now), false, false);
                return false;
            }
            if (phase.PreviousWorkload != null && (windows.Length == 0 || windows[windows.Length - 1].Observed <= phase.WorkloadReviewAfter))
            {
                PublishUnifiedState(source, "observing", "等待阶段变化后的新工作图；历史基线不能授权新阶段写入", false, false);
                return false;
            }
            if (windows.Length < 2)
            {
                bool queued = request != null && windows.Length == 1 && request.RequestWorkloadConfirmation(now);
                if (request != null && request.ObservationExhausted && !queued)
                {
                    phase.ObservationOnly = true;
                    phase.Evidence = "全线程工作组未取得两个完整基线，发现预算已用尽；未新增放置或提优";
                }
                PublishUnifiedState(source, "observing", StrategyName(source.Rule) + "等待两次独立工作组确认；已取得=" + windows.Length
                    + "；" + (request == null ? "采集请求不可用" : request.DescribeState(now)), false, false);
                return false;
            }
            UnifiedWorkloadSet previous = phase.PreviousWorkload != null ? phase.PreviousWorkload
                : UnifiedWorkloadDiscovery.Select(windows[windows.Length - 2], current, source.Pid, source.Birth, now);
            UnifiedWorkloadSet selected = UnifiedWorkloadDiscovery.Select(windows[windows.Length - 1], current, source.Pid, source.Birth, now);
            // 工作组策略同样留下渲染线程的放置前读数；准入与否都保存，供方案窗口对照。
            SchedulingBottleneckEvidence observed = NoteSchedulingEvidence(source, windows[windows.Length - 1]);
            if (source.Rule.IsDependencyBoost)
                return PrepareDependencyBoostRoles(source, phase, request, previous, selected,
                    windows[windows.Length - 2], windows[windows.Length - 1]);
            bool reviewed = phase.PreviousWorkload != null;
            string confirmation = DescribeWorkloadConfirmation(previous, selected);
            bool changed = !previous.Valid || !selected.Valid
                || WorkloadRoleKey(previous.Main) != WorkloadRoleKey(selected.Main)
                || WorkloadRoleKey(previous.Secondary) != WorkloadRoleKey(selected.Secondary);
            if (!reviewed && changed && request != null && request.RequestWorkloadRoleConfirmation(now))
            {
                phase.Workload = null;
                phase.AwaitingLoad = false;
                phase.Evidence = confirmation + "；尚未写入，使用原三次预算的最后一次确认，不要求总CPU负载升高";
                Logger.Info("全线程编排 角色复核 " + source.Name + " PID=" + source.Pid + " 创建=" + source.Birth + "；" + phase.Evidence);
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return false;
            }
            selected = ConfirmWorkloadRoles(previous, selected, reviewed);
            if (!selected.Valid || selected.Main == null && selected.Critical.Length == 0)
            {
                phase.Workload = selected;
                phase.ObservationOnly = true;
                phase.AwaitingLoad = false;
                phase.Evidence = confirmation + "；没有分别通过确认的固定角色或提优成员；本阶段停止新增接管";
                Logger.Info("全线程编排 角色未准入 " + source.Name + " PID=" + source.Pid + " 创建=" + source.Birth + "；" + phase.Evidence);
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return false;
            }
            if (CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
            {
                phase.ObservationOnly = true;
                phase.Evidence = "全线程编排的物理拓扑不可核验；保留候选，不建立核心放置";
                PublishUnifiedState(source, "unsupported", phase.Evidence, false, false);
                return false;
            }
            ulong mask;
            if (!ReadCriticalProcessMask(source.Pid, source.Birth, out mask) || mask == 0)
            {
                PublishUnifiedState(source, "observing", "全线程编排的当前进程范围未知；未建立写入资格", false, false);
                return false;
            }
            CriticalThreadAllowedRange scope;
            lock (sync) scope = CriticalThreadAllowedRange.For(CriticalPreferenceProfileLocked(source.Profile));
            if (scope.Error != null)
            {
                PublishUnifiedState(source, "unsupported", "全线程编排允许范围未确认；" + scope.Error, false, false);
                return false;
            }
            ulong reserved = AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, CpuTopology.PhysicalCoreMasks());
            UnifiedWorkloadPlan plan = UnifiedWorkloadPlan.Create(selected, mask & scope.Mask & ~reserved,
                CpuTopology.PhysicalCoreMasks(), CpuTopology.PerfMask, CpuTopology.FavoredMask);
            phase.Workload = selected; phase.WorkloadPlan = plan;
            if (!plan.Valid || plan.Primary == null && plan.PriorityRoles.Length == 0)
            {
                phase.ObservationOnly = true;
                phase.Evidence = "全线程放置未准入；" + plan.Reason + "；" + selected.Describe();
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return false;
            }
            phase.Roles = plan.Primary == null ? plan.PriorityRoles
                : plan.Secondary == null ? new[] { plan.Primary.Thread } : new[] { plan.Primary.Thread, plan.Secondary.Thread };
            phase.PriorityRoles = plan.PriorityRoles;
            phase.ObservedRoles = plan.ObservedRoles;
            phase.ObservationOnly = phase.AwaitingLoad = false;
            phase.Evidence = confirmation + "；" + selected.Describe() + "；" + plan.Describe()
                + (reviewed ? "；已观察成员完成一次新阶段复核，全部属性重新读取原值；旧图仅核对成员身份" : "；两次基线分别确认固定资格与提优身份")
                + "；帧率收益未验证";
            Logger.Info("全线程编排 角色准入 " + source.Name + " PID=" + source.Pid + " 创建=" + source.Birth
                + " 规则=" + source.Rule.Encode() + "；" + phase.Evidence + "；" + observed.Describe());
            // 候选明细作为一个事件保存，导出仍含每个线程的完整观测字段，避免逐线程追加文件和刷新列表。
            Logger.Info("全线程编排 候选批次 " + source.Name + " PID=" + source.Pid + " 创建=" + source.Birth
                + " 数量=" + selected.Observed.Length + "；明细=["
                + string.Join(" || ", selected.Observed.Select(node => node.Describe()).ToArray()) + "]");
            return true;
        }

        private static string WorkloadRoleKey(UnifiedWorkloadNode node)
        { return node == null ? "无" : node.Thread.Key; }

        private static string DescribeWorkloadConfirmation(UnifiedWorkloadSet previous, UnifiedWorkloadSet current)
        {
            bool mainChanged = WorkloadRoleKey(previous.Main) != WorkloadRoleKey(current.Main);
            bool secondaryChanged = WorkloadRoleKey(previous.Secondary) != WorkloadRoleKey(current.Secondary);
            return "前窗主=" + WorkloadRoleKey(previous.Main) + " 副=" + WorkloadRoleKey(previous.Secondary)
                + "；后窗主=" + WorkloadRoleKey(current.Main) + " 副=" + WorkloadRoleKey(current.Secondary)
                + "；差异=" + (!previous.Valid || !current.Valid ? "工作图无效" : mainChanged && secondaryChanged ? "主副角色变化"
                    : mainChanged ? "主角色变化" : secondaryChanged ? "副角色变化" : "角色身份一致")
                + "；前窗依据=" + previous.Reason + "；后窗依据=" + current.Reason;
        }

        private static UnifiedWorkloadSet ConfirmWorkloadRoles(UnifiedWorkloadSet previous, UnifiedWorkloadSet current, bool reviewed)
        {
            if (!previous.Valid || !current.Valid)
                return new UnifiedWorkloadSet(false, "前后工作图未同时有效", null, null, null, 0, 0, false, 0);
            var priorRoles = new[] { previous.Main, previous.Secondary }.Where(node => node != null).ToArray();
            var currentRoles = new[] { current.Main, current.Secondary }.Where(node => node != null).ToArray();
            // 固定资格只取两窗各自合格角色的身份交集。保留前窗顺序，排名互换不触发主副抖动。
            var fixedKeys = reviewed ? currentRoles.Where(node => previous.Observed.Any(prior => prior.Thread.Key == node.Thread.Key))
                    .Select(node => node.Thread.Key).ToArray()
                : priorRoles.Where(node => currentRoles.Any(next => next.Thread.Key == node.Thread.Key)).Select(node => node.Thread.Key).ToArray();
            // 提优与固定资格独立；首次确认要求两窗均为关键成员，不能借用一次CPU排名或无关观察身份。
            var priorPriority = reviewed ? previous.Observed : previous.Critical;
            var nodes = current.Observed.Select(node => new UnifiedWorkloadNode(node.Metrics, node.Kind, node.CpuShare,
                node.Priority && priorPriority.Any(prior => prior.Thread.Key == node.Thread.Key), node.Reason)).ToArray();
            var fixedRoles = fixedKeys.Select(key => nodes.First(node => node.Thread.Key == key)).ToArray();
            UnifiedWorkloadNode main = fixedRoles.Length == 0 ? null : fixedRoles[0];
            UnifiedWorkloadNode secondary = fixedRoles.Length < 2 ? null : fixedRoles[1];
            string detail = "已确认固定角色=" + (fixedRoles.Length == 0 ? "无" : string.Join(",", fixedKeys))
                + "；已确认提优成员=" + string.Join(",", nodes.Where(node => node.Priority).Select(node => node.Thread.Key).ToArray());
            return new UnifiedWorkloadSet(true, detail, main, secondary, nodes, current.CandidateCoverage,
                current.TotalRunningCores, current.Complete, current.TrackedThreads, current.Ledger,
                current.LedgerCoverage, current.WindowMs, current.LedgerComplete, current.LedgerCapacityComplete, current.CapacityGap);
        }

        // 本模式只接受有呈现锚点的小型依赖组，不凭主副核排名、核数或工作池吞吐授予提优资格。
        private bool PrepareDependencyBoostRoles(CriticalRuntimeSource source, UnifiedSchedulingPhase phase,
            ThreadLayoutCaptureRequest request, UnifiedWorkloadSet previous, UnifiedWorkloadSet selected,
            UnifiedThreadCaptureEvidence previousWindow, UnifiedThreadCaptureEvidence currentWindow)
        {
            bool reviewed = phase.PreviousWorkload != null;
            HardThreadIdentity priorPresenter = DependencyPresenter(previousWindow, previous);
            HardThreadIdentity presenter = DependencyPresenter(currentWindow, selected);
            string failure = !previous.Valid || !selected.Valid ? "前后工作图尚未有效；前窗=" + previous.Reason + "；后窗=" + selected.Reason
                : !previous.Complete || !selected.Complete ? "工作图有截断或候选身份缺失，不能据此确认完整小组"
                : priorPresenter == null || presenter == null ? "未取得稳定、完整的单来源呈现身份"
                : priorPresenter.Key != presenter.Key ? "呈现身份发生变化，剩余单次复核不能替代新身份的两窗确认" : null;
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
                if (dependencies.Length == 0) failure = "未取得重复确认的非呈现依赖成员，不把单线程提优当作依赖组";
                else if (roles.Length > 4) failure = "呈现依赖组超过4个线程，不按CPU排名截取或扩大整池提优";
            }
            phase.Workload = selected;
            phase.WorkloadPlan = null;
            if (failure != null)
            {
                phase.ObservationOnly = true;
                phase.AwaitingLoad = request != null && request.RejudgeAvailable;
                phase.Evidence = "依赖组未准入；" + failure + "；原优先级和核心范围保留";
                PublishUnifiedState(source, "observing", phase.Evidence, false, false);
                return false;
            }
            // 呈现锚点也属于提优组，退出由已有字段维护读回识别，不另加身份轮询。
            phase.Roles = phase.PriorityRoles = phase.ObservedRoles = roles;
            phase.RenderTarget = 0; phase.RenderClear = false; phase.AffinityNote = null;
            phase.ObservationOnly = phase.AwaitingLoad = false;
            phase.Evidence = "呈现依赖组=" + string.Join(",", roles.Select(role => role.Key).ToArray())
                + "；相对优先级目标+1，保留更高原值；不固定核心、不设置理想处理器、不清核"
                + (reviewed ? "；原呈现身份不变，新窗口复核已观察成员；旧图仅核对成员身份"
                    : "；两窗呈现身份与依赖资格重复确认")
                + "；依赖线索不等于已证明的帧关键路径，收益待实测";
            Logger.Info("依赖组提优 角色准入 游戏=" + source.Name + " PID=" + source.Pid + " 创建=" + source.Birth
                + " 规则=" + source.Rule.Encode() + "；" + phase.Evidence + "；" + NoteSchedulingEvidence(source, currentWindow).Describe());
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
                + "；未写成员首次接管=" + phase.PlacementAdmissionAttempts + "/3"
                + "；仅已确认成员持续维护，未声明系统独占";
        }

        private static bool WorkloadAnchor(UnifiedWorkloadSet workload, HardThreadIdentity role)
        {
            return workload.Main != null && workload.Main.Thread.Key == role.Key
                || workload.Secondary != null && workload.Secondary.Thread.Key == role.Key;
        }

        private void ReviewExitedRoles(CriticalRuntimeSource source, UnifiedSchedulingPhase phase, ThreadLayoutCaptureRequest request, long now)
        {
            if (!WithdrawCriticalThreadsLocked("关键角色已退出，先撤回原计划及清核限制，再检查剩余复核预算"))
            {
                PublishUnifiedState(source, "recovery-pending", "关键角色退出；原计划恢复未结清，未启动新角色观察或写入", false, true);
                return;
            }
            phase.RoleExitPending = false;
            phase.PreviousWorkload = phase.Workload;
            phase.WorkloadReviewAfter = now;
            phase.Workload = null; phase.WorkloadPlan = null;
            phase.Roles = phase.PriorityRoles = phase.ObservedRoles = null;
            phase.ControlAdmitted = false; phase.ControlRoles = null;
            phase.RenderTarget = 0; phase.RenderClear = false; phase.AffinityNote = null;
            phase.AwaitingLoad = false;
            // 恢复结清后才能使新证据获得准入资格。沿用原阶段全部预算，不沿用旧线程的原值或核心选择。
            bool queued = phase.RoleReviewAllowed && request != null && (source.Rule.IsRenderStrategy
                ? request.RequestRenderRoleReview(now) : request.RequestWorkloadRoleReview(now));
            phase.RoleReviewAllowed = false;
            phase.ObservationOnly = !queued;
            phase.Evidence = queued ? "原角色退出，原计划及清核归属已结清；等待剩余次数取得的新角色证据"
                : "原角色退出；无剩余角色复核资格，原计划及清核归属已结清";
            criticalObservation.Reset();
            unifiedClearedWorkers = 0; unifiedClearCoreNote = "";
            unifiedNextCpuSample = unifiedNextClearCoreScan = 0;
            PublishUnifiedState(source, "observing", phase.ObservationOnly ? phase.Evidence
                : "原计划已结清；使用本阶段剩余一次角色复核，不凭新线程号沿用旧原值", false, false);
        }

    }
}
