using System;
using System.Globalization;

namespace PaviseApp
{
    // 同一阶段共享有限采集预算 渲染策略两次 工作组策略三次 成功 失败和角色退出均不补充次数
    // 未接管时可使用余量重判负载 已接管时仅在角色确认退出 原计划恢复结清后允许一次角色复核
    internal sealed class ThreadLayoutCaptureRequest
    {
        private enum BaselineState { Pending, CoolingDown, Frozen, Exhausted, Intervened }
        private readonly object gate = new object();
        private readonly Action<string, string, bool> report;
        internal readonly int Pid;
        internal readonly long Birth;
        internal readonly string Identity;
        internal bool WorkloadObservation { get { return workload; } }
        internal readonly bool PresentIdentityOnly;
        private readonly long createdAt;
        private readonly bool workload;
        private readonly int maximumAttempts;
        private readonly System.Collections.Generic.List<UnifiedThreadCaptureEvidence> workloadBaselines = new System.Collections.Generic.List<UnifiedThreadCaptureEvidence>();
        private BaselineState baselineState;
        private bool capturing, canceled, foreground = true, captureForegroundLost, rejudged, roleReview, workloadConfirmation;
        private long appliedAt, nextBaselineAt, foregroundSince, lastIdentitySnapshot;
        private int attempts, baselineAttempts, identityConfirmations, activeStage = -1;
        private UnifiedThreadCaptureEvidence unifiedBaseline, lastBaselineAttempt;

        internal ThreadLayoutCaptureRequest(int pid, long birth, string identity, long now,
            Action<string, string, bool> writer, bool workloadMode = false, bool presentIdentityOnly = false)
        {
            if (workloadMode && presentIdentityOnly) throw new ArgumentException(Logger.Text("工作组观察不能使用仅呈现身份采集", "workload observation cannot use present-identity-only capture"), "presentIdentityOnly");
            Pid = pid; Birth = birth; Identity = identity; report = writer;
            PresentIdentityOnly = presentIdentityOnly;
            workload = workloadMode; maximumAttempts = workload ? 3 : 2;
            createdAt = foregroundSince = now;
            nextBaselineAt = now + TimeSpan.FromSeconds(15).Ticks;
        }

        // 重复前台通知不改变稳定起点 切出只撤销观察资格 不重建阶段或补充次数
        internal void SetForeground(bool value, long now)
        {
            lock (gate)
            {
                if (foreground == value) return;
                foreground = value;
                foregroundSince = now;
                identityConfirmations = 0;
                lastIdentitySnapshot = 0;
                if (!value && capturing) captureForegroundLost = true;
            }
        }

        internal bool ReadyForPlacement(long now)
        {
            lock (gate)
            {
                if (canceled) return true;
                return baselineState != BaselineState.Pending && baselineState != BaselineState.CoolingDown;
            }
        }

        // 调用者先核验本请求的 PID 与创建时间 只保留连续有效确认 不在等待时重新枚举所有线程
        internal void ObserveIdentitySnapshot(bool valid, long now)
        {
            lock (gate)
            {
                if (!valid || !foreground || now < lastIdentitySnapshot)
                { identityConfirmations = 0; lastIdentitySnapshot = 0; return; }
                if (now == lastIdentitySnapshot) return;
                if (lastIdentitySnapshot == 0 || now - lastIdentitySnapshot > TimeSpan.FromSeconds(30).Ticks)
                    identityConfirmations = 1;
                else identityConfirmations = Math.Min(2, identityConfirmations + 1);
                lastIdentitySnapshot = now;
            }
        }

        // 开局一两分钟游戏多在编译着色器 加载资源 CPU 最满 测到的线程关系也不稳定 首次采集等对局稳定后再做
        internal static int StartupSettleSeconds = 120;

        private long NextCaptureDueLocked()
        {
            if (canceled || capturing || attempts >= maximumAttempts || !foreground) return long.MaxValue;
            if (appliedAt > 0) return long.MaxValue;
            if (baselineState == BaselineState.Pending)
                return Math.Max(createdAt + TimeSpan.FromSeconds(StartupSettleSeconds).Ticks, foregroundSince + TimeSpan.FromSeconds(15).Ticks);
            if (baselineState == BaselineState.CoolingDown)
                return Math.Max(nextBaselineAt, foregroundSince + TimeSpan.FromSeconds(30).Ticks);
            return long.MaxValue;
        }

        internal long NextCaptureDueTicks
        { get { lock (gate) return NextCaptureDueLocked(); } }

        internal bool HasUsableBaseline
        { get { lock (gate) return !canceled && unifiedBaseline != null; } }

        internal bool ObservationExhausted
        {
            get
            {
                lock (gate) return !capturing && (canceled || attempts >= maximumAttempts
                    || baselineState == BaselineState.Exhausted || baselineState == BaselineState.Intervened);
            }
        }

        internal bool NeedsCapture(long now)
        {
            ReadyForPlacement(now);
            lock (gate) return now >= NextCaptureDueLocked() && IdentityReadyLocked(now);
        }

        internal bool NeedsObserver(long now)
        {
            ReadyForPlacement(now);
            // 在途采集即使用完总次数 也必须保活到完成或取消 冷却期不保留 ETW 观察器
            lock (gate) return !canceled && (capturing || now >= NextCaptureDueLocked() && IdentityReadyLocked(now));
        }

        private bool IdentityReadyLocked(long now)
        { return identityConfirmations >= 2 && now >= lastIdentitySnapshot && now - lastIdentitySnapshot <= TimeSpan.FromSeconds(30).Ticks; }

        internal bool RejudgeAvailable
        {
            get
            {
                lock (gate) return !canceled && !capturing && !rejudged && appliedAt == 0 && attempts < maximumAttempts
                    && baselineState == BaselineState.Frozen;
            }
        }

        // 尚未干预且负载明显升高时 可用剩余额度作废旧基线并重新判定
        internal bool TryRejudge(long now)
        {
            lock (gate)
            {
                if (canceled || capturing || rejudged || appliedAt != 0 || attempts >= maximumAttempts || baselineState != BaselineState.Frozen) return false;
                rejudged = true;
                workloadConfirmation = false;
                unifiedBaseline = null;
                lastBaselineAttempt = null;
                baselineState = BaselineState.CoolingDown; nextBaselineAt = now;
                return true;
            }
        }

        internal int WorkloadBaselineCount { get { lock (gate) return workloadBaselines.Count; } }

        internal UnifiedThreadCaptureEvidence[] ReadWorkloadBaselines()
        {
            bool usable;
            return ReadWorkloadBaselines(out usable);
        }

        internal UnifiedThreadCaptureEvidence[] ReadWorkloadBaselines(out bool currentBaselineUsable)
        {
            lock (gate)
            {
                // 完成回调可并发发布第三窗 窗口数组与可准入状态必须来自同一次锁内快照
                currentBaselineUsable = !canceled && unifiedBaseline != null;
                return canceled ? new UnifiedThreadCaptureEvidence[0] : workloadBaselines.ToArray();
            }
        }

        internal bool RequestWorkloadConfirmation(long now)
        {
            lock (gate)
            {
                if (!workload || canceled || capturing || appliedAt != 0 || attempts >= maximumAttempts
                    || baselineState != BaselineState.Frozen || workloadBaselines.Count >= 2) return false;
                unifiedBaseline = lastBaselineAttempt = null;
                workloadConfirmation = true;
                baselineState = BaselineState.CoolingDown; nextBaselineAt = now + TimeSpan.FromSeconds(5).Ticks;
                return true;
            }
        }

        // 两窗角色不同只使用原预算最后一次确认 尚未写入时采集 不能混入本方干预后的布局
        internal bool RequestWorkloadRoleConfirmation(long now)
        {
            lock (gate)
            {
                if (!workload || canceled || capturing || rejudged || appliedAt != 0 || attempts >= maximumAttempts
                    || baselineState != BaselineState.Frozen || workloadBaselines.Count < 2) return false;
                rejudged = workloadConfirmation = true;
                unifiedBaseline = lastBaselineAttempt = null;
                baselineState = BaselineState.CoolingDown; nextBaselineAt = now + TimeSpan.FromSeconds(5).Ticks;
                return true;
            }
        }

        internal bool RequestWorkloadRoleReview(long now)
        { return RequestRoleReview(now, true); }

        internal bool RequestRenderRoleReview(long now)
        { return RequestRoleReview(now, false); }

        // 调用方须先证明角色退出并结清旧写入 共享原发现预算 保留前台和进程身份门禁
        private bool RequestRoleReview(long now, bool workloadMode)
        {
            lock (gate)
            {
                if (workload != workloadMode || canceled || capturing || rejudged || attempts >= maximumAttempts) return false;
                rejudged = roleReview = true;
                appliedAt = 0;
                unifiedBaseline = lastBaselineAttempt = null;
                baselineState = BaselineState.CoolingDown; nextBaselineAt = now + TimeSpan.FromSeconds(5).Ticks;
                return true;
            }
        }

        internal bool TryBegin(long now, out int stage)
        {
            stage = -1;
            ReadyForPlacement(now);
            lock (gate)
            {
                if (now < NextCaptureDueLocked() || !IdentityReadyLocked(now)) return false;
                stage = baselineState == BaselineState.Pending || baselineState == BaselineState.CoolingDown ? 0 : 1;
                capturing = true; captureForegroundLost = false; activeStage = stage; attempts++;
                if (stage == 0) baselineAttempts++;
                return true;
            }
        }

        internal void Complete(int stage, FrameCoordinationSample sample)
        { Complete(stage, sample, AutoCores.NowTicks); }

        internal void Complete(int stage, FrameCoordinationSample sample, long now)
        {
            bool usable, changedDuringCapture, lostForeground, frozen = false;
            lock (gate)
            {
                if (!capturing || stage != activeStage) return;
                capturing = false; activeStage = -1;
                if (sample != null && (sample.PresentIdentityOnly != PresentIdentityOnly
                    || PresentIdentityOnly && (!sample.LayoutObservation || sample.PresenterOnly || sample.Relation != null)))
                    sample.Reject(FrameCaptureIssue.CaptureFailed, Logger.Text("采集用途与原观察请求不一致，本轮样本弃用", "capture purpose does not match the original observation request; this round's sample discarded"));
                changedDuringCapture = stage == 0 && appliedAt > 0;
                lostForeground = captureForegroundLost || !foreground;
                if (stage == 0 && !canceled && !changedDuringCapture && !lostForeground)
                {
                    lastBaselineAttempt = new UnifiedThreadCaptureEvidence(sample, now);
                    // 工作组策略继续核验关系摘要 渲染策略只要求单来源 呈现身份及采集完整性
                    frozen = sample != null && sample.Pid == Pid && sample.Birth == Birth && (workload
                        ? (sample.Issue == FrameCaptureIssue.None || sample.Issue == FrameCaptureIssue.InsufficientEvidence)
                            && sample.LostEvents == 0 && sample.Relation != null && sample.Relation.WorkloadValid
                        : lastBaselineAttempt.Valid && lastBaselineAttempt.Presenter != null && sample.PresentStreamsKnown && sample.PresentStreamCount == 1);
                    if (frozen)
                    {
                        unifiedBaseline = lastBaselineAttempt; baselineState = BaselineState.Frozen;
                        if (workload) workloadBaselines.Add(lastBaselineAttempt);
                    }
                }
                if (stage == 0 && !frozen)
                {
                    if (changedDuringCapture) baselineState = BaselineState.Intervened;
                    else if (attempts >= maximumAttempts) baselineState = BaselineState.Exhausted;
                    else
                    {
                        baselineState = BaselineState.CoolingDown;
                        nextBaselineAt = now + TimeSpan.FromSeconds(30).Ticks;
                    }
                }
                usable = !canceled && !lostForeground && sample != null && sample.Pid == Pid && sample.Birth == Birth
                    && !changedDuringCapture && sample.Valid && sample.Issue == FrameCaptureIssue.None
                    && sample.LostEvents == 0 && sample.Relation != null && sample.Relation.Valid
                    && sample.Relation.Pid == Pid && sample.Relation.Birth == Birth && sample.Relation.LostEvents == 0;
            }
            string context = stage == 0 ? Logger.Text("角色基线", "role baseline") : Logger.Text("角色复核", "role review");
            string detail = Logger.Text("采集次数=", "captures=") + Attempts + "/" + maximumAttempts + Logger.Text("；基线已冻结=", "; baselineFrozen=") + frozen
                + Logger.Text("；关系可用=", "; relationUsable=") + usable;
            if (PresentIdentityOnly) detail += Logger.Text("；采集用途=仅呈现身份；调度关系、CPU排队与换核分布=未采集", "; capturePurpose=present identity only; scheduling relations, CPU queueing and core migration=not captured");
            if (sample != null)
            {
                detail += Logger.Text("；丢失事件=", "; lostEvents=") + sample.LostEvents + Logger.Text("；返回码=", "; traceCode=") + sample.TraceCode
                    + Logger.Text("；来源字段已核验=", "; sourceFieldsVerified=") + sample.PresentStreamsKnown + Logger.Text("；成功提交流数量=", "; presentStreamCount=") + sample.PresentStreamCount
                    + Logger.Text("；呈现候选=", "; presenterCandidate=") + sample.PresentTid
                    + Logger.Text("；相关事件=", "; relevantEvents=") + sample.Events + Logger.Text("（参考=", " (reference=") + FrameCoordinationSampler.MaxRelevantEvents + Logger.Text("）", ")")
                    + Logger.Text("；系统事件=", "; systemEvents=") + sample.SystemEvents + Logger.Text("（参考=", " (reference=") + FrameCoordinationSampler.MaxSystemEvents + Logger.Text("）", ")")
                    + Logger.Text("；采样CPU毫秒=", "; sampleCpuMs=") + CpuText(sample.CpuMs) + Logger.Text("（参考=", " (reference=") + CpuText(FrameCoordinationSampler.MaxCaptureCpuMs) + Logger.Text("）", ")")
                    + Logger.Text("；事件消费CPU毫秒=", "; consumerCpuMs=") + CpuText(sample.ConsumerCpuMs)
                    + Logger.Text("；采集调用CPU毫秒=", "; callerCpuMs=") + CpuText(sample.CallerCpuMs)
                    + Logger.Text("；后处理让出次数=", "; processingYields=") + sample.ProcessingYields
                    + Logger.Text("；线程身份未核验=", "; unverifiedThreadIdentities=") + sample.ThreadIdentityReadFailures;
                if (sample.ThreadIdentityReadFailures > 0)
                    detail += Logger.Text("；首个未核验TID=", "; firstUnverifiedTid=") + sample.FirstUnverifiedThreadId + Logger.Text("；身份错误码=", "; identityError=") + sample.FirstThreadIdentityError
                        + Logger.Text("；身份失败阶段=", "; identityFailurePhase=") + sample.FirstThreadIdentityStage;
                if (sample.Relation != null)
                {
                    detail += Logger.Text("；工作图可用=", "; workloadGraphValid=") + sample.Relation.WorkloadValid + Logger.Text("；详细候选=", "; detailedCandidates=") + sample.Relation.WorkloadThreads.Length
                        + Logger.Text("；溢出线程桶=", "; overflowedThreadBuckets=") + sample.Relation.OverflowedThreads + Logger.Text("；工作图截断=", "; workloadGraphTruncated=") + sample.Relation.WorkloadTruncated
                        + Logger.Text("；状态转换=", "; stateTransitions=") + sample.Relation.RecordedTransitions + Logger.Text("（参考=", " (reference=") + ThreadRelationCollector.MaximumTransitions + Logger.Text("）", ")")
                        + Logger.Text("；状态块=", "; stateBlocks=") + sample.Relation.AllocatedBlocks + Logger.Text("（参考=", " (reference=") + ThreadRelationCollector.MaximumBlocks + Logger.Text("）", ")");
                    if (workload && !sample.Relation.WorkloadValid)
                        detail += Logger.Text("；工作图原因=", "; workloadGraphReason=") + ShortReason(sample.Relation.WorkloadReason);
                }
                if (!frozen && !usable)
                {
                    string failure = new UnifiedThreadCaptureEvidence(sample, now).FailureReason;
                    detail += Logger.Text("；原因=", "; reason=") + ShortReason(failure);
                }
            }
            else detail += Logger.Text("；原因=采样未返回", "; reason=sample not returned");
            if (changedDuringCapture) detail += Logger.Text("；采集期间已施加干预，本轮不能作为原布局基线", "; intervention applied during capture; this round cannot serve as the original layout baseline");
            if (lostForeground) detail += Logger.Text("；采集期间前台资格中断，本轮不能作为准入基线", "; foreground eligibility interrupted during capture; this round cannot serve as an admission baseline");
            Write(context, detail, workload || PresentIdentityOnly ? frozen : usable);
        }

        private static string ShortReason(string reason)
        {
            string value = (reason ?? Logger.Text("未知", "unknown")).Replace('\r', ' ').Replace('\n', ' ');
            return value.Length > 256 ? value.Substring(0, 256) : value;
        }

        private static string CpuText(double value)
        { return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? Logger.Text("未知", "unknown") : value.ToString("F3", CultureInfo.InvariantCulture); }

        internal string DescribeState() { return DescribeState(AutoCores.NowTicks); }

        internal string DescribeState(long now)
        {
            lock (gate)
            {
                string state = canceled ? Logger.Text("已取消", "canceled") : capturing ? Logger.Text("正在采集角色基线，尚未完成", "capturing role baseline; not yet complete")
                    : baselineState == BaselineState.Pending ? Logger.Text("等待首次基线", "waiting for the first baseline")
                    : baselineState == BaselineState.CoolingDown ? (roleReview ? Logger.Text("原角色已退出，等待稳定前台后用剩余次数复核新角色", "original role exited; the new role will be reviewed with remaining attempts after a stable foreground")
                        : workloadConfirmation ? Logger.Text("等待稳定前台后使用剩余次数确认工作组角色", "waiting for a stable foreground to confirm workload roles with remaining attempts")
                        : rejudged ? Logger.Text("负载已升高，等待稳定前台后用剩余次数重新判定", "load increased; waiting for a stable foreground to re-judge with remaining attempts") : Logger.Text("基线未通过，等待稳定前台后使用剩余次数", "baseline failed; waiting for a stable foreground to use remaining attempts"))
                    : baselineState == BaselineState.Frozen ? Logger.Text("有效基线已冻结", "valid baseline frozen")
                    : baselineState == BaselineState.Exhausted ? Logger.Text("基线未通过，完整采集预算已耗尽", "baseline failed; full-capture budget exhausted")
                    : Logger.Text("已施加干预，禁止重建原布局基线", "intervention applied; rebuilding the original layout baseline is not allowed");
                return Logger.Text("观察阶段=", "observationPhase=") + state + Logger.Text("；完整采集次数=", "; fullCaptures=") + attempts + "/" + maximumAttempts + Logger.Text("；基线采集次数=", "; baselineCaptures=") + baselineAttempts
                    + Logger.Text("；连续身份确认=", "; consecutiveIdentityConfirmations=") + identityConfirmations + "/2"
                    + Logger.Text("；下次采集=", "; nextCapture=") + (NextCaptureDueLocked() == long.MaxValue ? Logger.Text("当前无新增采集", "none scheduled")
                        : Logger.Text("至少等待", "in at least ") + Math.Ceiling(Math.Max(0, NextCaptureDueLocked() - now) / (double)TimeSpan.TicksPerSecond) + Logger.Text("秒", " s"))
                    + Logger.Text("；接管后观察=", "; postTakeoverObservation=") + (appliedAt > 0 ? Logger.Text("已停止追加", "no further captures") : Logger.Text("尚未施加干预", "no intervention yet"));
            }
        }

        internal UnifiedThreadCaptureEvidence ReadUnifiedBaseline()
        { lock (gate) return canceled || baselineState == BaselineState.Intervened ? null : unifiedBaseline ?? lastBaselineAttempt; }

        internal void MarkApplied(long now)
        {
            lock (gate)
            {
                if (canceled || appliedAt != 0) return;
                appliedAt = now;
                if (unifiedBaseline == null) { baselineState = BaselineState.Intervened; lastBaselineAttempt = null; }
            }
        }

        internal void Cancel() { lock (gate) canceled = true; }
        internal int Attempts { get { lock (gate) return attempts; } }

        private void Write(string stage, string detail, bool usable)
        {
            // 日志出口异常不能推进识别 写入或恢复的失败预算
            try { if (report != null) report(stage, detail, usable); }
            catch { }
        }
    }
}
