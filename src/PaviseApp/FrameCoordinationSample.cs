using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal sealed class FrameCoordinationSample
    {
        internal int Pid;

        internal int PresentTid;

        internal int WorkerTid;

        internal long Birth;

        internal bool Valid;
        // 单一来源的稳定呈现角色独立核验，关系重建或无关线程桶溢出不代替此字段。
        internal bool PresentValid;

        internal bool NoChangeNeeded;

        internal bool PriorityReserved;

        internal bool PresenterOnly;
        // 完整实验观测只发布诊断与角色线索，不改变既有提优或识别的失败预算。
        internal bool LayoutObservation;

        internal string Detail;

        internal double CpuMs;

        // 消费者与调用线程分别计时；负数表示尚未取得可核验读数。
        internal double ConsumerCpuMs = -1, CallerCpuMs = -1;
        internal int ProcessingYields;
        internal int ThreadIdentityReadFailures, FirstThreadIdentityError, FirstUnverifiedThreadId;
        internal string FirstThreadIdentityStage;

        internal void RecordThreadIdentityFailure(int tid, int error, string stage)
        {
            ThreadIdentityReadFailures++;
            if (ThreadIdentityReadFailures != 1) return;
            FirstUnverifiedThreadId = tid; FirstThreadIdentityError = error; FirstThreadIdentityStage = stage;
        }

        internal FrameCaptureIssue Issue;

        internal int TraceCode;

        // 原始失败阶段与清理证据分开，避免把清回执失败误报成 StartTrace 的返回码。
        internal string TraceStage = "";
        internal string TraceFailureDetail = "";

        internal int Events;

        internal int SystemEvents;

        internal int SchedulerSwitches;

        internal uint LostEvents;

        internal ThreadRelationSnapshot Relation;

        // 呈现次数不能区分游戏画面和同进程辅助窗口；统一调度只准入来源可核验的单一提交流。
        internal bool PresentStreamsKnown;
        internal int PresentStreamCount;
        internal int StablePartnerCount;

        internal string PresentFirstHalf, PresentSecondHalf, PresentSelectionReason, PresentSources;
        internal int PresentSourceReadSuccesses, PresentSourceReadFailures, PresentFlagsReadFailures;
        internal int UntrackedPresentEvents, UnsupportedPresentEvents, UnmatchedPresentStops, ReplacedPresentStarts, UnclosedPresentStarts, ZeroPresentSources;
        // 窗口边界截断的提交单独计数，不影响来源核验。
        internal int EdgePresentStops, EdgePresentStarts;
        internal int PresentSourceCapacityEvents, FirstUnsupportedPresentVersion = -1, FirstPropertyFailureVersion = -1;
        internal uint FirstPropertyFailureCode;

        internal string DescribePresentCapture()
        {
            return "捕获Issue=" + Issue + "；呈现选择=" + (PresentSelectionReason ?? Detail ?? "尚未完成")
                + (TraceFailureDetail.Length == 0 ? "" : "；ETW证据=" + TraceFailureDetail)
                + "；单呈现角色已核验=" + PresentValid
                + "；前半提交=" + (PresentFirstHalf ?? "未汇总") + "；后半提交=" + (PresentSecondHalf ?? "未汇总")
                + "；逐来源提交=" + (PresentSources ?? "未汇总")
                + "；来源读取成功=" + PresentSourceReadSuccesses + "；来源读取失败=" + PresentSourceReadFailures
                + "；Flags读取失败=" + PresentFlagsReadFailures + "；零来源=" + ZeroPresentSources
                + "；首次TDH错误码=" + (FirstPropertyFailureVersion < 0 ? "无" : FirstPropertyFailureCode.ToString(CultureInfo.InvariantCulture))
                + "；首次TDH错误版本=" + (FirstPropertyFailureVersion < 0 ? "无" : FirstPropertyFailureVersion.ToString(CultureInfo.InvariantCulture))
                + "；未跟踪呈现事件=" + UntrackedPresentEvents + "；不支持呈现版本事件=" + UnsupportedPresentEvents
                + "；首次不支持版本=" + (FirstUnsupportedPresentVersion < 0 ? "无" : FirstUnsupportedPresentVersion.ToString(CultureInfo.InvariantCulture))
                + "；未配对结束=" + UnmatchedPresentStops + "；重复开始=" + ReplacedPresentStarts
                + "；采集结束仍未配对开始=" + UnclosedPresentStarts + "；窗口边界提交=" + EdgePresentStops + "/" + EdgePresentStarts
                + "；来源容量事件=" + PresentSourceCapacityEvents;
        }

        internal Dictionary<int, long> Threads = new Dictionary<int, long>();

        private sealed class CpuReading
        {
            internal long Creation, Kernel, User, At;
        }
        private readonly Dictionary<int, CpuReading> cpuBefore = new Dictionary<int, CpuReading>();
        private readonly Dictionary<int, CpuReading> cpuAfter = new Dictionary<int, CpuReading>();

        // 复用身份查询已经返回的计数；前读结束和后读开始分别包住完整事件窗口。
        internal void RecordThreadCpuBefore(int tid, long creation, long kernel, long user, long readAfter)
        {
            cpuBefore[tid] = new CpuReading { Creation = creation, Kernel = kernel, User = user, At = readAfter };
        }
        internal void RecordThreadCpuAfter(int tid, long creation, long kernel, long user, long readBefore)
        {
            cpuAfter[tid] = new CpuReading { Creation = creation, Kernel = kernel, User = user, At = readBefore };
        }
        internal bool CpuZeroGrowthVerified(int tid, long creation, long begin, long end)
        {
            CpuReading before, after;
            return tid > 0 && creation > 0 && begin < end
                && cpuBefore.TryGetValue(tid, out before) && cpuAfter.TryGetValue(tid, out after)
                && before.Creation == creation && after.Creation == creation
                && before.At <= begin && after.At >= end
                && before.Kernel >= 0 && before.User >= 0 && after.Kernel >= before.Kernel && after.User >= before.User
                && after.Kernel == before.Kernel && after.User == before.User;
        }

        internal FrameCoordinationSample Reject(FrameCaptureIssue issue, string extra = null)
        {
            Valid = false;
            PresentValid = false;
            Issue = issue;
            Detail = IssueText(issue) + (string.IsNullOrEmpty(extra) ? "" : (" | " + extra));
            return this;
        }

        internal static string IssueText(FrameCaptureIssue issue)
        {
            switch (issue)
            {
            case FrameCaptureIssue.Canceled:
                return PerformancePolicy.Text("前台或守护状态变化，本轮采样取消", "Focus or guard state changed; capture canceled");
            case FrameCaptureIssue.TargetChanged:
                return PerformancePolicy.Text("游戏进程已退出或切换，本轮采样取消", "Game process exited or changed; capture canceled");
            case FrameCaptureIssue.TargetUnavailable:
                return PerformancePolicy.Text("游戏进程身份暂时无法核验", "Game process identity could not be verified");
            case FrameCaptureIssue.ThreadBudget:
                return PerformancePolicy.Text("游戏线程数量超过采样预算", "Game thread count exceeds the capture budget");
            case FrameCaptureIssue.TraceUnavailable:
                return PerformancePolicy.Text("线程事件采样未能启动", "Thread event capture could not start");
            case FrameCaptureIssue.TraceStopFailed:
                return PerformancePolicy.Text("线程事件采样未正常结束", "Thread event capture did not stop cleanly");
            case FrameCaptureIssue.EventsLost:
                return PerformancePolicy.Text("采样事件丢失，本轮样本弃用", "Capture events were lost; sample discarded");
            case FrameCaptureIssue.EventBudget:
                return PerformancePolicy.Text("游戏相关事件数量超过采样预算，本轮样本弃用", "Game event count exceeds the capture budget; sample discarded");
            case FrameCaptureIssue.SystemEventBudget:
                return PerformancePolicy.Text("系统事件数量超过采样预算，本轮样本弃用", "System event count exceeds the capture budget; sample discarded");
            case FrameCaptureIssue.EdgeBudget:
                return PerformancePolicy.Text("线程关联数量超过采样预算，本轮样本弃用", "Thread dependency count exceeds the capture budget; sample discarded");
            case FrameCaptureIssue.DecodeFailed:
                return PerformancePolicy.Text("线程事件解析异常，本轮样本弃用", "Thread event decoding failed; sample discarded");
            case FrameCaptureIssue.CpuBudget:
                return PerformancePolicy.Text("采样 CPU 耗时超过预算，本轮样本弃用", "Capture CPU time exceeds the budget; sample discarded");
            case FrameCaptureIssue.CaptureFailed:
                return PerformancePolicy.Text("线程采样发生异常", "Thread capture failed");
            default:
                return PerformancePolicy.Text("暂未取得稳定线程证据", "Stable thread evidence is not available yet");
            }
        }
    }
}
