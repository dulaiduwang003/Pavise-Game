namespace PaviseApp
{
    internal enum FrameCaptureIssue
    {
        None,
        InsufficientEvidence,
        Canceled,
        TargetChanged,
        TargetUnavailable,
        ThreadBudget,
        TraceUnavailable,
        TraceStopFailed,
        EventsLost,
        EventBudget,
        EdgeBudget,
        DecodeFailed,
        CpuBudget,
        CaptureFailed,
        SystemEventBudget
    }
}
