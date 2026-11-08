using System;

namespace PaviseApp
{
	internal static class DataOperationCompletion
	{
		internal static void CompleteReset(ResetDataReport report, bool runtimeStopped, Action<string> warn, Action requestRestart, Action finish)
		{
			try
			{
				if (report == null || !report.Completed || report.Warnings > 0)
				{
					string wipeCleanupfailText = ((report == null) ? Lang.T("wipe.cleanupfail") : ShortReason(report.Failure));
					if (warn != null)
					{
						warn(Lang.F("wipe.failed", wipeCleanupfailText));
					}
				}
				if (runtimeStopped && requestRestart != null)
				{
					requestRestart();
				}
			}
			finally
			{
				if (finish != null)
				{
					finish();
				}
			}
		}

		internal static void CompleteUninstall(UninstallReport report, bool finalizerStarted, string finalizerFailure, Action<bool, string> showResult, Action finish)
		{
			try
			{
				bool matchesRuntimeStopped = report != null && report.RuntimeStopped;
				bool arg = !matchesRuntimeStopped || !report.Cleared || report.Warnings > 0 || !finalizerStarted;
				string uninstallFailedText = ((!matchesRuntimeStopped) ? Lang.F("uninstall.failed", ShortReason((report == null) ? null : report.Failure)) : ((report.Warnings <= 0 && report.Cleared) ? Lang.T("uninstall.done") : Lang.F("uninstall.partial", Math.Max(1, report.Warnings))));
				if (matchesRuntimeStopped)
				{
					uninstallFailedText = uninstallFailedText + "\r\n\r\n" + (finalizerStarted ? Lang.T("uninstall.selfdelete") : Lang.F("uninstall.manualdelete", ShortReason(finalizerFailure)));
				}
				if (showResult != null)
				{
					showResult(arg, uninstallFailedText);
				}
			}
			finally
			{
				if (finish != null)
				{
					finish();
				}
			}
		}

		private static string ShortReason(string reason)
		{
			if (string.IsNullOrWhiteSpace(reason))
			{
				return Lang.T("wipe.cleanupfail");
			}
			string trimmed = reason.Replace('\r', '\n').Split('\n')[0].Trim();
			if (trimmed.Length <= 180)
			{
				return trimmed;
			}
			return trimmed.Substring(0, 180) + "…";
		}
	}
}
