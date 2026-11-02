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
					string text = ((report == null) ? Lang.T("wipe.cleanupfail") : ShortReason(report.Failure));
					if (warn != null)
					{
						warn(Lang.F("wipe.failed", text));
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
				bool flag = report != null && report.RuntimeStopped;
				bool arg = !flag || !report.Cleared || report.Warnings > 0 || !finalizerStarted;
				string text = ((!flag) ? Lang.F("uninstall.failed", ShortReason((report == null) ? null : report.Failure)) : ((report.Warnings <= 0 && report.Cleared) ? Lang.T("uninstall.done") : Lang.F("uninstall.partial", Math.Max(1, report.Warnings))));
				if (flag)
				{
					text = text + "\r\n\r\n" + (finalizerStarted ? Lang.T("uninstall.selfdelete") : Lang.F("uninstall.manualdelete", ShortReason(finalizerFailure)));
				}
				if (showResult != null)
				{
					showResult(arg, text);
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
			string text = reason.Replace('\r', '\n').Split('\n')[0].Trim();
			if (text.Length <= 180)
			{
				return text;
			}
			return text.Substring(0, 180) + "…";
		}
	}
}
