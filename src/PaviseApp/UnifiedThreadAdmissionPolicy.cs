using System;
using System.Linq;

namespace PaviseApp
{
    // 准入只使用未施加干预时冻结的有效观察；放置后复查不能替换角色证据。
    internal sealed class UnifiedThreadCaptureEvidence
    {
        internal readonly int Pid, PresenterTid, WorkerTid, Streams, StablePartners;
        internal readonly long Birth, Observed;
        internal readonly bool Valid, StreamsKnown;
        internal readonly HardThreadIdentity Presenter, Worker;
        internal readonly ThreadRelationSnapshot Relation;
        internal readonly string FailureReason;

        internal UnifiedThreadCaptureEvidence(FrameCoordinationSample sample, long observed)
        {
            Observed = observed;
            FailureReason = CaptureFailure(sample);
            if (sample == null) return;
            Pid = sample.Pid; Birth = sample.Birth; PresenterTid = sample.PresentTid; WorkerTid = sample.WorkerTid;
            Streams = sample.PresentStreamCount; StreamsKnown = sample.PresentStreamsKnown;
            StablePartners = sample.StablePartnerCount;
            Valid = sample.PresentValid && sample.Issue == FrameCaptureIssue.None && sample.LostEvents == 0;
            Relation = sample.Relation;
            long created;
            if (sample.PresentTid > 0 && sample.Threads.TryGetValue(sample.PresentTid, out created) && created > 0)
                Presenter = new HardThreadIdentity(sample.PresentTid, created);
            if (sample.WorkerTid > 0 && sample.Threads.TryGetValue(sample.WorkerTid, out created) && created > 0)
                Worker = new HardThreadIdentity(sample.WorkerTid, created);
        }

        // 返回码相同的拒绝可能来自系统，也可能来自本程序的采集回执；没有阶段时不替它下结论。
        private static string TraceOrigin(FrameCoordinationSample sample)
        {
            string stage = sample.TraceStage ?? "";
            if (stage.Length == 0) return "";
            return "；失败阶段=" + stage + (stage.StartsWith("ledger-", StringComparison.Ordinal) ? "（本程序采集回执未就绪，不是系统拒绝）"
                : stage.StartsWith("channel-", StringComparison.Ordinal) ? "（本程序采集通道占用或已关闭）" : "（系统调用返回）");
        }

        private static string CaptureFailure(FrameCoordinationSample sample)
        {
            if (sample == null) return "采样未返回";
            string reason = sample.Issue == FrameCaptureIssue.None ? "" : FrameCoordinationSample.IssueText(sample.Issue)
                + "；采集错误=" + sample.Issue + "；返回码=" + sample.TraceCode + TraceOrigin(sample);
            if (sample.LostEvents != 0) reason += (reason.Length == 0 ? "" : "；") + "丢失事件=" + sample.LostEvents;
            if (!string.IsNullOrEmpty(sample.Detail)) reason += (reason.Length == 0 ? "" : "；") + sample.Detail;
            if (!string.IsNullOrEmpty(sample.PresentSelectionReason)
                && reason.IndexOf(sample.PresentSelectionReason, StringComparison.Ordinal) < 0)
                reason += (reason.Length == 0 ? "" : "；") + "呈现选择=" + sample.PresentSelectionReason;
            if (sample.Relation != null && !sample.Relation.Valid && !string.IsNullOrEmpty(sample.Relation.Reason)
                && reason.IndexOf(sample.Relation.Reason, StringComparison.Ordinal) < 0)
                reason += (reason.Length == 0 ? "" : "；") + "线程关系=" + sample.Relation.Reason;
            if (!sample.PresentStreamsKnown) reason += (reason.Length == 0 ? "" : "；") + "呈现来源字段未核验";
            else if (sample.PresentStreamCount != 1) reason += (reason.Length == 0 ? "" : "；") + "成功提交流数量=" + sample.PresentStreamCount;
            return reason.Length == 0 ? "采样未通过完整性核验" : reason;
        }
    }

    internal static class UnifiedThreadAdmissionPolicy
    {
        internal static HardThreadIdentity[] Select(UnifiedThreadCaptureEvidence evidence, ThreadTimes current,
            CriticalThreadHotspot[] hotspots, int pid, long birth, long now, bool manual, out string reason)
        {
            reason = "尚未完成可用于准入的放置前观察；CPU 热点仅作候选";
            if (evidence == null) return new HardThreadIdentity[0];
            if (evidence.Pid != pid || evidence.Birth != birth)
            { reason = "放置前观察的进程身份不匹配"; return new HardThreadIdentity[0]; }
            if (now < evidence.Observed)
            { reason = "放置前观察的时间顺序不可核验"; return new HardThreadIdentity[0]; }
            if (now - evidence.Observed > TimeSpan.FromSeconds(65).Ticks)
            { reason = "放置前观察已超过 65 秒新鲜度，不用旧证据接管新角色"; return new HardThreadIdentity[0]; }
            if (!evidence.Valid)
            { reason = "放置前观察无效：" + evidence.FailureReason; return new HardThreadIdentity[0]; }
            if (!CriticalThreadObservation.Valid(current, birth, now))
            { reason = "当前线程身份或 CPU 快照不可核验"; return new HardThreadIdentity[0]; }
            if (!evidence.StreamsKnown)
            { reason = "呈现来源字段未核验；不能把提交最多的线程当作游戏主线程"; return new HardThreadIdentity[0]; }
            if (evidence.Streams != 1)
            { reason = "成功提交流数量=" + evidence.Streams + "，不满足单一来源条件"; return new HardThreadIdentity[0]; }
            if (evidence.Presenter == null)
            { reason = "放置前观察缺少固定呈现候选身份"; return new HardThreadIdentity[0]; }
            if (!CriticalThreadRuntimePolicy.Contains(current, evidence.Presenter))
            { reason = "呈现候选身份已变化；不把复用线程号接入旧规则"; return new HardThreadIdentity[0]; }
            reason = manual ? "已确认单一呈现提交候选；手动目标仅固定该角色，不声称整核独占或完整关键路径"
                : "两半窗口内单一来源及呈现身份均已核验；只准入呈现角色，不据此确认伙伴或完整关键路径";
            return new[] { evidence.Presenter };
        }

        internal static HardThreadIdentity[] SelectEnhanced(UnifiedThreadCaptureEvidence evidence, ThreadTimes current,
            CriticalThreadHotspot[] hotspots, int pid, long birth, long now, out string reason)
        {
            HardThreadIdentity[] presenter = Select(evidence, current, hotspots, pid, birth, now, false, out reason);
            if (presenter.Length == 0) return presenter;
            CriticalThreadHotspot[] busy = hotspots ?? new CriticalThreadHotspot[0];
            CriticalThreadHotspot first = busy.FirstOrDefault(item => item != null);
            if (first != null && busy.Count(item => item != null && Math.Abs(item.CpuShare - first.CpuShare) <= 0.10) >= 3)
            { reason = "多个相近高负载候选存在可互换线程池歧义；不启用伙伴增强，独立呈现角色另行判断"; return new HardThreadIdentity[0]; }
            if (evidence.StablePartners != 1)
            { reason = "关联工作组为空或超过首版两角色额度；不从已知组中任意截取伙伴"; return new HardThreadIdentity[0]; }
            ThreadRelationSnapshot relation = evidence.Relation;
            if (relation == null)
            { reason = "放置前观察缺少线程关系摘要"; return new HardThreadIdentity[0]; }
            if (!relation.Valid || relation.LostEvents != 0)
            { reason = "线程关系摘要无效：" + relation.Reason + "；丢失事件=" + relation.LostEvents; return new HardThreadIdentity[0]; }
            if (relation.OverflowedThreads != 0)
            { reason = "关系观察存在溢出或完整性未知；不把竞争候选缺失当作唯一伙伴；溢出桶=" + relation.OverflowedThreads;
                return new HardThreadIdentity[0]; }
            if (relation.Pid != pid || relation.Birth != birth
                || relation.Presenter == null || relation.Worker == null || evidence.Worker == null
                || relation.Presenter.Tid != evidence.Presenter.Tid || relation.Presenter.Creation != evidence.Presenter.Creation
                || relation.Worker.Tid != evidence.Worker.Tid || relation.Worker.Creation != evidence.Worker.Creation
                || !CriticalThreadRuntimePolicy.Contains(current, evidence.Worker))
            { reason = "固定关键角色组尚未确认；不依据 CPU 排名或无效 Ready 摘要提优"; return new HardThreadIdentity[0]; }
            if (relation.Presenter.StateConflicts != 0 || relation.Worker.StateConflicts != 0)
            { reason = "关键角色调度状态存在歧义：呈现=" + relation.Presenter.StateConflicts + "；伙伴=" + relation.Worker.StateConflicts;
                return new HardThreadIdentity[0]; }
            if (relation.ForwardFirst < 3 || relation.ForwardSecond < 3 || relation.ReverseFirst < 3 || relation.ReverseSecond < 3)
            { reason = "两半窗口的双向关联不足：呈现到伙伴=" + relation.ForwardFirst + "/" + relation.ForwardSecond
                + "；伙伴到呈现=" + relation.ReverseFirst + "/" + relation.ReverseSecond; return new HardThreadIdentity[0]; }
            if (relation.PresentIntervals < 16)
            { reason = "成功 Present 间隔不足：" + relation.PresentIntervals + "/16"; return new HardThreadIdentity[0]; }
            if (relation.Presenter.ReadyIntervals < 3 && relation.Worker.ReadyIntervals < 3
                || relation.Presenter.ReadyMs + relation.Worker.ReadyMs <= 0)
            { reason = "当前没有足够的 CPU 就绪排队证据；等待 GPU 或限帧不启用伙伴增强"; return new HardThreadIdentity[0]; }
            string presenterPressure, workerPressure;
            bool presentQueued = UnifiedThreadPressurePolicy.Accept(relation, relation.Presenter, out presenterPressure);
            bool workerQueued = UnifiedThreadPressurePolicy.Accept(relation, relation.Worker, out workerPressure);
            if (!presentQueued && !workerQueued)
            { reason = "关联角色未取得持续CPU排队资格；" + presenterPressure + "；" + workerPressure; return new HardThreadIdentity[0]; }
            reason = "首次窗口存在稳定双向关联及持续CPU排队；" + (presentQueued ? presenterPressure : workerPressure);
            // 先接管可能提供工作的一方，再接管等待方；不截取更大的已知工作组。
            return new[] { evidence.Worker, evidence.Presenter };
        }

        internal static HardThreadIdentity[] SelectPresenterPriority(UnifiedThreadCaptureEvidence evidence, ThreadTimes current,
            int pid, long birth, long now, out string reason)
        {
            HardThreadIdentity[] presenter = Select(evidence, current, null, pid, birth, now, false, out reason);
            if (presenter.Length == 0) return presenter;
            ThreadRelationSnapshot relation = evidence.Relation;
            if (relation == null || relation.Pid != pid || relation.Birth != birth || relation.Presenter == null
                || relation.Presenter.Tid != presenter[0].Tid || relation.Presenter.Creation != presenter[0].Creation
                || !UnifiedThreadPressurePolicy.Accept(relation, relation.Presenter, out reason))
            { reason = "呈现角色已识别，持续排队资格未确认；" + reason; return new HardThreadIdentity[0]; }
            reason = "仅呈现角色具有持续CPU排队资格，温和提优且不写优先核；" + reason;
            return presenter;
        }
    }
}
