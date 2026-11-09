using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class FrameCoordinationSample
    {
        internal int Pid;

        internal int PresentTid;

        internal int WorkerTid;

        internal long Birth;

        internal bool Valid;

        internal bool NoChangeNeeded;

        internal bool PriorityReserved;

        internal bool PresenterOnly;

        internal string Detail;

        internal double CpuMs;

        internal FrameCaptureIssue Issue;

        internal int TraceCode;

        internal int Events;

        internal int SystemEvents;

        internal int SchedulerSwitches;

        internal uint LostEvents;

        internal Dictionary<int, long> Threads = new Dictionary<int, long>();

        internal FrameCoordinationSample Reject(FrameCaptureIssue issue, string extra = null)
        {
            Valid = false;
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
                return PerformancePolicy.Text("系统暂不允许线程事件采样", "Thread event capture is unavailable");
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
