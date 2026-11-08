using System;

namespace PaviseApp
{
	internal sealed class FrameCoordinationRetry
	{
		internal const int BaseDelayMs = 10000;

		private int failures;

		private int lastTraceCode;

		private FrameCaptureIssue lastIssue;

		private long lastLogMs;

		private bool logged;

		internal int DelayMs { get; private set; }

		internal bool ShouldLog { get; private set; }

		internal void Observe(FrameCoordinationSample sample, long elapsedMs)
		{
			FrameCaptureIssue frameCaptureIssue = ((sample == null) ? FrameCaptureIssue.CaptureFailed : sample.Issue);
			int traceCode = ((sample == null) ? (-1) : sample.TraceCode);
			if (sample != null && (sample.Valid || frameCaptureIssue == FrameCaptureIssue.InsufficientEvidence || frameCaptureIssue == FrameCaptureIssue.Canceled || frameCaptureIssue == FrameCaptureIssue.TargetChanged))
			{
				failures = 0;
				DelayMs = 10000;
			}
			else
			{
				if (failures < 5)
				{
					failures++;
				}
				DelayMs = Math.Min(300000, 10000 << failures - 1);
			}
			ShouldLog = !logged || frameCaptureIssue != lastIssue || traceCode != lastTraceCode || elapsedMs - lastLogMs >= 300000;
			if (ShouldLog)
			{
				logged = true;
				lastIssue = frameCaptureIssue;
				lastTraceCode = traceCode;
				lastLogMs = elapsedMs;
			}
		}

		internal void Reset()
		{
			failures = 0;
			logged = false;
			DelayMs = 0;
			ShouldLog = false;
		}
	}
}
