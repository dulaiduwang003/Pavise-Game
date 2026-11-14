using System;
using System.Globalization;

namespace PaviseApp
{
    // 同一阶段共享有限采集预算：渲染策略两次，工作组策略三次；成功、失败和角色退出均不补充次数。
    // 未接管时可使用余量重判负载；已接管时仅在角色确认退出、原计划恢复结清后允许一次角色复核。
    internal sealed class ThreadLayoutCaptureRequest
    {
        private enum BaselineState { Pending, CoolingDown, Frozen, Exhausted, Intervened }
        private readonly object gate = new object();
        private readonly Action<string, string, bool> report;
        internal readonly int Pid;
        internal readonly long Birth;
        internal readonly string Identity;
        internal bool WorkloadObservation { get { return workload; } }
        private readonly long createdAt;
        private readonly bool workload;
        private readonly int maximumAttempts;
        private readonly System.Collections.Generic.List<UnifiedThreadCaptureEvidence> workloadBaselines = new System.Collections.Generic.List<UnifiedThreadCaptureEvidence>();
        private BaselineState baselineState;
        private bool capturing, canceled, foreground = true, captureForegroundLost, rejudged, roleReview, workloadConfirmation;
        private long appliedAt, nextBaselineAt, foregroundSince, lastIdentitySnapshot;
        private int attempts, baselineAttempts, identityConfirmations, activeStage = -1;
        private readonly CriticalThreadAuxiliaryEvidence[] evidence = new CriticalThreadAuxiliaryEvidence[2];
        private UnifiedThreadCaptureEvidence unifiedBaseline, lastBaselineAttempt;

        internal ThreadLayoutCaptureRequest(int pid, long birth, string identity, long now,
            Action<string, string, bool> writer, bool workloadMode = false)
        {
            Pid = pid; Birth = birth; Identity = identity; report = writer;
            workload = workloadMode; maximumAttempts = workload ? 3 : 2;
            createdAt = foregroundSince = now;
            nextBaselineAt = now + TimeSpan.FromSeconds(15).Ticks;
        }

        // 重复前台通知不改变稳定起点；切出只撤销观察资格，不重建阶段或补充次数。
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

        // 调用者先核验本请求的 PID 与创建时间。只保留连续有效确认，不在等待时重新枚举所有线程。
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

        private long NextCaptureDueLocked()
        {
            if (canceled || capturing || attempts >= maximumAttempts || !foreground) return long.MaxValue;
            if (appliedAt > 0) return long.MaxValue;
            if (baselineState == BaselineState.Pending)
                return Math.Max(createdAt + TimeSpan.FromSeconds(15).Ticks, foregroundSince + TimeSpan.FromSeconds(15).Ticks);
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
            // 在途采集即使用完总次数，也必须保活到完成或取消；冷却期不保留 ETW 观察器。
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

        // 尚未干预且负载明显升高时，可用剩余额度作废旧基线并重新判定。
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
                // 完成回调可并发发布第三窗；窗口数组与可准入状态必须来自同一次锁内快照。
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

        // 两窗角色不同只使用原预算最后一次确认；尚未写入时采集，不能混入本方干预后的布局。
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

        // 调用方须先证明角色退出并结清旧写入。共享原发现预算，保留前台和进程身份门禁。
        private bool RequestRoleReview(long now, bool workloadMode)
        {
            lock (gate)
            {
                if (workload != workloadMode || canceled || capturing || rejudged || attempts >= maximumAttempts) return false;
                rejudged = roleReview = true;
                appliedAt = 0;
                unifiedBaseline = lastBaselineAttempt = null;
                Array.Clear(evidence, 0, evidence.Length);
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
                changedDuringCapture = stage == 0 && appliedAt > 0;
                lostForeground = captureForegroundLost || !foreground;
                if (stage == 0 && !canceled && !changedDuringCapture && !lostForeground)
                {
                    lastBaselineAttempt = new UnifiedThreadCaptureEvidence(sample, now);
                    // 工作组策略继续核验关系摘要；三种渲染策略只要求单来源、呈现身份及采集完整性。
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
                evidence[stage] = new CriticalThreadAuxiliaryEvidence { Relation = usable ? sample.Relation : null, ObservedTicks = now };
            }
            string context = stage == 0 ? "角色基线" : "角色复核";
            string detail = "采集次数=" + Attempts + "/" + maximumAttempts + "；基线已冻结=" + frozen
                + "；关系可用=" + usable;
            if (sample != null)
            {
                detail += "；丢失事件=" + sample.LostEvents + "；返回码=" + sample.TraceCode
                    + "；来源字段已核验=" + sample.PresentStreamsKnown + "；成功提交流数量=" + sample.PresentStreamCount
                    + "；呈现候选=" + sample.PresentTid
                    + "；相关事件=" + sample.Events + "（参考=" + FrameCoordinationSampler.MaxRelevantEvents + "）"
                    + "；系统事件=" + sample.SystemEvents + "（参考=" + FrameCoordinationSampler.MaxSystemEvents + "）"
                    + "；采样CPU毫秒=" + CpuText(sample.CpuMs) + "（参考=" + CpuText(FrameCoordinationSampler.MaxCaptureCpuMs) + "）"
                    + "；事件消费CPU毫秒=" + CpuText(sample.ConsumerCpuMs)
                    + "；采集调用CPU毫秒=" + CpuText(sample.CallerCpuMs)
                    + "；后处理让出次数=" + sample.ProcessingYields
                    + "；线程身份未核验=" + sample.ThreadIdentityReadFailures;
                if (sample.ThreadIdentityReadFailures > 0)
                    detail += "；首个未核验TID=" + sample.FirstUnverifiedThreadId + "；身份错误码=" + sample.FirstThreadIdentityError
                        + "；身份失败阶段=" + sample.FirstThreadIdentityStage;
                if (sample.Relation != null)
                {
                    detail += "；工作图可用=" + sample.Relation.WorkloadValid + "；详细候选=" + sample.Relation.WorkloadThreads.Length
                        + "；溢出线程桶=" + sample.Relation.OverflowedThreads + "；工作图截断=" + sample.Relation.WorkloadTruncated
                        + "；状态转换=" + sample.Relation.RecordedTransitions + "（参考=" + ThreadRelationCollector.MaximumTransitions + "）"
                        + "；状态块=" + sample.Relation.AllocatedBlocks + "（参考=" + ThreadRelationCollector.MaximumBlocks + "）";
                    if (workload && !sample.Relation.WorkloadValid)
                        detail += "；工作图原因=" + ShortReason(sample.Relation.WorkloadReason);
                }
                if (!frozen && !usable)
                {
                    string failure = new UnifiedThreadCaptureEvidence(sample, now).FailureReason;
                    detail += "；原因=" + ShortReason(failure);
                }
            }
            else detail += "；原因=采样未返回";
            if (changedDuringCapture) detail += "；采集期间已施加干预，本轮不能作为原布局基线";
            if (lostForeground) detail += "；采集期间前台资格中断，本轮不能作为准入基线";
            Write(context, detail, workload ? frozen : usable);
        }

        private static string ShortReason(string reason)
        {
            string value = (reason ?? "未知").Replace('\r', ' ').Replace('\n', ' ');
            return value.Length > 256 ? value.Substring(0, 256) : value;
        }

        private static string CpuText(double value)
        { return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? "未知" : value.ToString("F3", CultureInfo.InvariantCulture); }

        internal string DescribeState() { return DescribeState(AutoCores.NowTicks); }

        internal string DescribeState(long now)
        {
            lock (gate)
            {
                string state = canceled ? "已取消" : capturing ? "正在采集角色基线，尚未完成"
                    : baselineState == BaselineState.Pending ? "等待首次基线"
                    : baselineState == BaselineState.CoolingDown ? (roleReview ? "原角色已退出，等待稳定前台后用剩余次数复核新角色"
                        : workloadConfirmation ? "等待稳定前台后使用剩余次数确认工作组角色"
                        : rejudged ? "负载已升高，等待稳定前台后用剩余次数重新判定" : "基线未通过，等待稳定前台后使用剩余次数")
                    : baselineState == BaselineState.Frozen ? "有效基线已冻结"
                    : baselineState == BaselineState.Exhausted ? "基线未通过，完整采集预算已耗尽"
                    : "已施加干预，禁止重建原布局基线";
                return "观察阶段=" + state + "；完整采集次数=" + attempts + "/" + maximumAttempts + "；基线采集次数=" + baselineAttempts
                    + "；连续身份确认=" + identityConfirmations + "/2"
                    + "；下次采集=" + (NextCaptureDueLocked() == long.MaxValue ? "当前无新增采集"
                        : "至少等待" + Math.Ceiling(Math.Max(0, NextCaptureDueLocked() - now) / (double)TimeSpan.TicksPerSecond) + "秒")
                    + "；接管后观察=" + (appliedAt > 0 ? "已停止追加" : "尚未施加干预");
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

        internal CriticalThreadAuxiliaryEvidence[] ReadAuxiliaryEvidence()
        {
            lock (gate)
            {
                if (canceled) return new CriticalThreadAuxiliaryEvidence[0];
                var result = new System.Collections.Generic.List<CriticalThreadAuxiliaryEvidence>(2);
                foreach (CriticalThreadAuxiliaryEvidence item in evidence)
                    if (item != null) result.Add(new CriticalThreadAuxiliaryEvidence { Relation = item.Relation, ObservedTicks = item.ObservedTicks });
                return result.ToArray();
            }
        }

        private void Write(string stage, string detail, bool usable)
        {
            // 日志出口异常不能推进识别、写入或恢复的失败预算。
            try { if (report != null) report(stage, detail, usable); }
            catch { }
        }
    }
}
