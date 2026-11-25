using System;

namespace PaviseApp
{
    // 准入只使用未施加干预时冻结的有效观察 放置后复查不能替换角色证据
    internal sealed class UnifiedThreadCaptureEvidence
    {
        internal readonly int Pid, Streams;
        internal readonly long Birth, Observed;
        internal readonly DateTime SampledAt;
        internal readonly bool Valid, StreamsKnown;
        internal readonly HardThreadIdentity Presenter;
        internal readonly string FailureReason;

        internal UnifiedThreadCaptureEvidence(FrameCoordinationSample sample, long observed)
        {
            Observed = observed;
            FailureReason = CaptureFailure(sample);
            if (sample == null) return;
            Pid = sample.Pid; Birth = sample.Birth;
            Streams = sample.PresentStreamCount; StreamsKnown = sample.PresentStreamsKnown;
            Valid = sample.PresentValid && sample.Issue == FrameCaptureIssue.None && sample.LostEvents == 0;
            if (Valid) SampledAt = DateTime.Now;
            long created;
            if (sample.PresentTid > 0 && sample.Threads.TryGetValue(sample.PresentTid, out created) && created > 0)
                Presenter = new HardThreadIdentity(sample.PresentTid, created);
        }

        // 返回码相同的拒绝可能来自系统 也可能来自本程序的采集回执 没有阶段时不替它下结论
        private static string TraceOrigin(FrameCoordinationSample sample)
        {
            string stage = sample.TraceStage ?? "";
            if (stage.Length == 0) return "";
            return Logger.Text("；失败阶段=", "; failureStage=") + stage + (stage.StartsWith("ledger-", StringComparison.Ordinal) ? Logger.Text("（本程序采集回执未就绪，不是系统拒绝）", " (this program's capture receipt not ready, not a system refusal)")
                : stage.StartsWith("channel-", StringComparison.Ordinal) ? Logger.Text("（本程序采集通道占用或已关闭）", " (this program's capture channel busy or closed)") : Logger.Text("（系统调用返回）", " (returned by system call)"));
        }

        private static string CaptureFailure(FrameCoordinationSample sample)
        {
            if (sample == null) return Logger.Text("采样未返回", "sample not returned");
            string reason = sample.Issue == FrameCaptureIssue.None ? "" : FrameCoordinationSample.IssueText(sample.Issue)
                + Logger.Text("；采集错误=", "; captureIssue=") + sample.Issue + Logger.Text("；返回码=", "; rc=") + sample.TraceCode + TraceOrigin(sample);
            if (sample.LostEvents != 0) reason += (reason.Length == 0 ? "" : Logger.Text("；", "; ")) + Logger.Text("丢失事件=", "lostEvents=") + sample.LostEvents;
            if (!string.IsNullOrEmpty(sample.Detail)) reason += (reason.Length == 0 ? "" : Logger.Text("；", "; ")) + sample.Detail;
            if (!string.IsNullOrEmpty(sample.PresentSelectionReason)
                && reason.IndexOf(sample.PresentSelectionReason, StringComparison.Ordinal) < 0)
                reason += (reason.Length == 0 ? "" : Logger.Text("；", "; ")) + Logger.Text("呈现选择=", "presentSelection=") + sample.PresentSelectionReason;
#if PAVISE_SELFTEST
            if (sample.Relation != null && !sample.Relation.Valid && !string.IsNullOrEmpty(sample.Relation.Reason)
                && reason.IndexOf(sample.Relation.Reason, StringComparison.Ordinal) < 0)
                reason += (reason.Length == 0 ? "" : Logger.Text("；", "; ")) + Logger.Text("线程关系=", "threadRelation=") + sample.Relation.Reason;
#endif
            if (!sample.PresentStreamsKnown) reason += (reason.Length == 0 ? "" : Logger.Text("；", "; ")) + Logger.Text("呈现来源字段未核验", "present source field unverified");
            else if (sample.PresentStreamCount != 1) reason += (reason.Length == 0 ? "" : Logger.Text("；", "; ")) + Logger.Text("成功提交流数量=", "successfulStreams=") + sample.PresentStreamCount;
            return reason.Length == 0 ? Logger.Text("采样未通过完整性核验", "sample failed integrity verification") : reason;
        }
    }

    internal sealed class RenderAffinityObservation
    {
        internal readonly DateTime SampledAt;
        internal readonly int PresenterTid, PresentStreamCount;

        internal RenderAffinityObservation(DateTime sampledAt, int presenterTid, int presentStreamCount)
        {
            SampledAt = sampledAt;
            PresenterTid = presenterTid;
            PresentStreamCount = presentStreamCount;
        }
    }

    internal static class UnifiedThreadAdmissionPolicy
    {
        internal static HardThreadIdentity[] Select(UnifiedThreadCaptureEvidence evidence, ThreadTimes current,
            int pid, long birth, long now, out string reason)
        {
            reason = Logger.Text("尚未完成可用于准入的放置前观察", "no pre-placement observation usable for admission yet");
            if (evidence == null) return new HardThreadIdentity[0];
            if (evidence.Pid != pid || evidence.Birth != birth)
            { reason = Logger.Text("放置前观察的进程身份不匹配", "pre-placement observation process identity mismatch"); return new HardThreadIdentity[0]; }
            if (now < evidence.Observed)
            { reason = Logger.Text("放置前观察的时间顺序不可核验", "pre-placement observation time order cannot be verified"); return new HardThreadIdentity[0]; }
            if (now - evidence.Observed > TimeSpan.FromSeconds(65).Ticks)
            { reason = Logger.Text("放置前观察已超过 65 秒新鲜度，不用旧证据接管新角色", "pre-placement observation exceeds the 65 s freshness limit, old evidence is not used to take over new roles"); return new HardThreadIdentity[0]; }
            if (!evidence.Valid)
            { reason = Logger.Text("放置前观察无效：", "pre-placement observation invalid: ") + evidence.FailureReason; return new HardThreadIdentity[0]; }
            if (!CriticalThreadRuntimePolicy.Valid(current, birth, now))
            { reason = Logger.Text("当前线程身份或 CPU 快照不可核验", "current thread identity or CPU snapshot cannot be verified"); return new HardThreadIdentity[0]; }
            if (!evidence.StreamsKnown)
            { reason = Logger.Text("呈现来源字段未核验；不能把提交最多的线程当作游戏主线程", "present source field unverified; the thread with the most presents cannot be treated as the game main thread"); return new HardThreadIdentity[0]; }
            if (evidence.Streams != 1)
            { reason = Logger.Text("成功提交流数量=", "successfulStreams=") + evidence.Streams + Logger.Text("，不满足单一来源条件", ", single-source condition not met"); return new HardThreadIdentity[0]; }
            if (evidence.Presenter == null)
            { reason = Logger.Text("放置前观察缺少固定呈现候选身份", "pre-placement observation lacks a fixed presenter candidate identity"); return new HardThreadIdentity[0]; }
            if (!CriticalThreadRuntimePolicy.Contains(current, evidence.Presenter))
            { reason = Logger.Text("呈现候选身份已变化；不把复用线程号接入旧规则", "presenter candidate identity changed; reused thread IDs are not attached to old rules"); return new HardThreadIdentity[0]; }
            reason = Logger.Text("两半窗口内单一来源及呈现身份均已核验；只准入呈现角色", "single source and presenter identity verified in both half windows; presenter admitted");
            return new[] { evidence.Presenter };
        }
    }
}
