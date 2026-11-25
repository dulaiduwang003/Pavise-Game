using System;
using System.Globalization;

namespace PaviseApp
{
    // 同一阶段共享两次有界采集额度
    internal sealed class ThreadLayoutCaptureRequest
    {
        private enum BaselineState { Pending, CoolingDown, TraceRetry, Frozen, Exhausted, TraceExhausted, Intervened }
        private readonly object gate = new object();
        private readonly Action<string, string, bool> report;
        internal readonly int Pid;
        internal readonly long Birth;
        internal readonly string Identity;
        internal readonly FrameLayoutCapture Capture;
        private readonly long createdAt;
        private const int MaximumAttempts = 2;
        private static readonly int[] TraceRetrySeconds = { 30, 60, 120 };
        private BaselineState baselineState;
        private bool capturing, canceled, foreground = true, captureForegroundLost, roleReview;
        private long appliedAt, nextBaselineAt, foregroundSince, lastIdentitySnapshot;
        private int attempts, traceRetries, identityConfirmations;
        private UnifiedThreadCaptureEvidence unifiedBaseline;

        internal ThreadLayoutCaptureRequest(int pid, long birth, string identity, long now,
            Action<string, string, bool> writer, FrameLayoutCapture capture = FrameLayoutCapture.Presenter)
        {
            Pid = pid; Birth = birth; Identity = identity; report = writer; Capture = capture;
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
            if (canceled || capturing || attempts >= MaximumAttempts || !foreground) return long.MaxValue;
            if (appliedAt > 0) return long.MaxValue;
            if (baselineState == BaselineState.Pending)
                return Math.Max(createdAt + TimeSpan.FromSeconds(StartupSettleSeconds).Ticks, foregroundSince + TimeSpan.FromSeconds(15).Ticks);
            if (baselineState == BaselineState.CoolingDown || baselineState == BaselineState.TraceRetry)
                return Math.Max(nextBaselineAt, foregroundSince + TimeSpan.FromSeconds(30).Ticks);
            return long.MaxValue;
        }

        internal long NextCaptureDueTicks
        { get { lock (gate) return NextCaptureDueLocked(); } }

        internal bool NeedsIdentitySnapshot(long now)
        {
            lock (gate)
            {
                long due = NextCaptureDueLocked();
                if (due == long.MaxValue) return false;
                if (now >= due) return true;
                // 复用既有核验节奏 在启动退避结束前确认两次身份
                return baselineState == BaselineState.TraceRetry
                    && now >= due - TimeSpan.FromSeconds(10).Ticks && !IdentityReadyLocked(due);
            }
        }

        internal bool HasUsableBaseline
        { get { lock (gate) return !canceled && unifiedBaseline != null; } }

        internal bool ObservationExhausted
        {
            get
            {
                lock (gate) return !capturing && (canceled || attempts >= MaximumAttempts
                    || baselineState == BaselineState.Exhausted || baselineState == BaselineState.TraceExhausted
                    || baselineState == BaselineState.Intervened);
            }
        }

        internal bool NeedsCapture(long now)
        {
            lock (gate) return now >= NextCaptureDueLocked() && IdentityReadyLocked(now);
        }

        internal bool NeedsObserver(long now)
        {
            // 在途采集即使用完总次数 也必须保活到完成或取消 冷却期不保留 ETW 观察器
            lock (gate) return !canceled && foreground && (capturing && !captureForegroundLost
                || now >= NextCaptureDueLocked() && IdentityReadyLocked(now));
        }

        private bool IdentityReadyLocked(long now)
        { return identityConfirmations >= 2 && now >= lastIdentitySnapshot && now - lastIdentitySnapshot <= TimeSpan.FromSeconds(30).Ticks; }

        // 调用方须先证明角色退出并结清旧写入 共享原发现预算 保留前台和进程身份门禁
        internal bool RequestRenderRoleReview(long now)
        {
            lock (gate)
            {
                if (canceled || capturing || roleReview || attempts >= MaximumAttempts || baselineState == BaselineState.TraceExhausted) return false;
                roleReview = true;
                appliedAt = 0;
                unifiedBaseline = null;
                baselineState = BaselineState.CoolingDown; nextBaselineAt = now + TimeSpan.FromSeconds(5).Ticks;
                return true;
            }
        }

        internal bool TryBegin(long now, out int stage)
        {
            stage = -1;
            lock (gate)
            {
                if (now < NextCaptureDueLocked() || !IdentityReadyLocked(now)) return false;
                stage = 0;
                capturing = true; captureForegroundLost = false; attempts++;
                return true;
            }
        }

        internal void Complete(int stage, FrameCoordinationSample sample)
        { Complete(stage, sample, AutoCores.NowTicks); }

        internal void Complete(int stage, FrameCoordinationSample sample, long now)
        {
            UnifiedThreadCaptureEvidence evidence;
            bool usable, changedDuringCapture, lostForeground, traceExhausted = false, frozen = false;
            int retrySeconds = 0;
            lock (gate)
            {
                if (!capturing || stage != 0) return;
                capturing = false;
                changedDuringCapture = appliedAt > 0;
                lostForeground = captureForegroundLost || !foreground;
                evidence = new UnifiedThreadCaptureEvidence(sample, now);
                if (!canceled && !changedDuringCapture && !lostForeground)
                {
                    frozen = sample != null && sample.Pid == Pid && sample.Birth == Birth
                        && evidence.Valid && evidence.Presenter != null
                        && sample.PresentStreamsKnown && sample.PresentStreamCount == 1;
                    if (frozen)
                    {
                        unifiedBaseline = evidence;
                        baselineState = BaselineState.Frozen;
                    }
                }
                bool emptyTraceFailure = Capture == FrameLayoutCapture.Presenter && sample != null
                    && sample.Issue == FrameCaptureIssue.TraceUnavailable
                    && sample.Events == 0 && sample.SystemEvents == 0 && sample.LostEvents == 0;
                if (emptyTraceFailure)
                {
                    attempts--;
                    if (changedDuringCapture) baselineState = BaselineState.Intervened;
                    else if (!canceled)
                    {
                        traceExhausted = traceRetries >= TraceRetrySeconds.Length;
                        baselineState = traceExhausted ? BaselineState.TraceExhausted : BaselineState.TraceRetry;
                        if (!traceExhausted)
                        {
                            retrySeconds = TraceRetrySeconds[traceRetries++];
                            nextBaselineAt = now + TimeSpan.FromSeconds(retrySeconds).Ticks;
                        }
                    }
                }
                else if (!frozen)
                {
                    if (changedDuringCapture) baselineState = BaselineState.Intervened;
                    else if (attempts >= MaximumAttempts) baselineState = BaselineState.Exhausted;
                    else
                    {
                        baselineState = BaselineState.CoolingDown;
                        nextBaselineAt = now + TimeSpan.FromSeconds(30).Ticks;
                    }
                }
                usable = frozen;
#if PAVISE_SELFTEST
                if (sample != null && sample.Relation != null)
                    usable = !canceled && !lostForeground && sample.Pid == Pid && sample.Birth == Birth
                        && !changedDuringCapture && sample.Valid && sample.Issue == FrameCaptureIssue.None
                        && sample.LostEvents == 0 && sample.Relation.Valid
                        && sample.Relation.Pid == Pid && sample.Relation.Birth == Birth && sample.Relation.LostEvents == 0;
#endif
            }
            string detail = Logger.Text("采集次数=", "captures=") + Attempts + "/" + MaximumAttempts + Logger.Text("；基线已冻结=", "; baselineFrozen=") + frozen;
            detail += Logger.Text("；采集用途=呈现身份", "; capturePurpose=presenter identity");
            if (traceExhausted)
                detail += Logger.Text("；会话启动重试已用完，本阶段停止采集", "; trace startup retries exhausted, capture stopped for this phase");
            else if (retrySeconds > 0)
                detail += Logger.Format("；会话启动失败，至少等待 {0} 秒重试", "; trace startup failed, retry in at least {0} s", retrySeconds);
            if (sample != null)
            {
                detail += Logger.Text("；丢失事件=", "; lostEvents=") + sample.LostEvents + Logger.Text("；返回码=", "; traceCode=") + sample.TraceCode
                    + Logger.Text("；采集会话阶段=", "; traceStage=") + sample.TraceStage
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
#if PAVISE_SELFTEST
                if (sample.Relation != null)
                {
                    detail += Logger.Text("；关系可用=", "; relationUsable=") + usable
                        + Logger.Text("；状态转换=", "; stateTransitions=") + sample.Relation.RecordedTransitions
                        + Logger.Text("；状态块=", "; stateBlocks=") + sample.Relation.AllocatedBlocks;
                }
#endif
                if (!frozen && !usable)
                {
                    detail += Logger.Text("；原因=", "; reason=") + ShortReason(evidence.FailureReason);
                }
            }
            else detail += Logger.Text("；原因=采样未返回", "; reason=sample not returned");
            if (changedDuringCapture) detail += Logger.Text("；采集期间已施加干预，本轮不能作为原布局基线", "; intervention applied during capture; this round cannot serve as the original layout baseline");
            if (lostForeground) detail += Logger.Text("；采集期间前台资格中断，本轮不能作为准入基线", "; foreground eligibility interrupted during capture; this round cannot serve as an admission baseline");
            Write(Logger.Text("角色基线", "role baseline"), detail, usable);
        }

        private static string ShortReason(string reason)
        {
            string value = (reason ?? Logger.Text("未知", "unknown")).Replace('\r', ' ').Replace('\n', ' ');
            return value.Length > 256 ? value.Substring(0, 256) : value;
        }

        private static string CpuText(double value)
        { return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? Logger.Text("未知", "unknown") : value.ToString("F3", CultureInfo.InvariantCulture); }

        internal string DescribeState(long now)
        {
            lock (gate)
            {
                string state = canceled ? Logger.Text("已取消", "canceled") : capturing ? Logger.Text("正在采集角色基线，尚未完成", "capturing role baseline; not yet complete")
                    : baselineState == BaselineState.Pending ? Logger.Text("等待首次基线", "waiting for the first baseline")
                    : baselineState == BaselineState.CoolingDown ? (roleReview ? Logger.Text("原角色已退出，等待稳定前台后用剩余次数复核新角色", "original role exited; the new role will be reviewed with remaining attempts after a stable foreground")
                        : Logger.Text("基线未通过，等待稳定前台后使用剩余次数", "baseline failed; waiting for a stable foreground to use remaining attempts"))
                    : baselineState == BaselineState.TraceRetry ? Logger.Text("会话启动失败，等待重试", "trace startup failed, waiting to retry")
                    : baselineState == BaselineState.TraceExhausted ? Logger.Text("会话启动重试已用完，本阶段停止采集", "trace startup retries exhausted, capture stopped for this phase")
                    : baselineState == BaselineState.Frozen ? Logger.Text("有效基线已冻结", "valid baseline frozen")
                    : baselineState == BaselineState.Exhausted ? Logger.Text("基线未通过，角色采集预算已耗尽", "baseline failed; role capture budget exhausted")
                    : Logger.Text("已施加干预，禁止重建原布局基线", "intervention applied; rebuilding the original layout baseline is not allowed");
                return Logger.Text("观察阶段=", "observationPhase=") + state + Logger.Text("；角色采集次数=", "; roleCaptures=") + attempts + "/" + MaximumAttempts
                    + Logger.Text("；连续身份确认=", "; consecutiveIdentityConfirmations=") + identityConfirmations + "/2"
                    + Logger.Text("；下次采集=", "; nextCapture=") + (NextCaptureDueLocked() == long.MaxValue ? Logger.Text("当前无新增采集", "none scheduled")
                        : Logger.Text("至少等待", "in at least ") + Math.Ceiling(Math.Max(0, NextCaptureDueLocked() - now) / (double)TimeSpan.TicksPerSecond) + Logger.Text("秒", " s"))
                    + Logger.Text("；接管后观察=", "; postTakeoverObservation=") + (appliedAt > 0 ? Logger.Text("已停止追加", "no further captures") : Logger.Text("尚未施加干预", "no intervention yet"));
            }
        }

        internal UnifiedThreadCaptureEvidence ReadUnifiedBaseline()
        { lock (gate) return canceled ? null : unifiedBaseline; }

        internal void MarkApplied(long now)
        {
            lock (gate)
            {
                if (canceled || appliedAt != 0) return;
                appliedAt = now;
                if (unifiedBaseline == null) baselineState = BaselineState.Intervened;
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
