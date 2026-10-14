using System;

namespace PaviseApp
{
    internal static class DataOperationCompletion
    {
        internal static void CompleteReset(ResetDataReport report, bool runtimeStopped,
            Action<string> warn, Action requestRestart, Action finish)
        {
            try
            {
                if (report == null || !report.Completed || report.Warnings > 0)
                {
                    string failure = report == null ? Lang.T("wipe.cleanupfail") : ShortReason(report.Failure);
                    if (warn != null) warn(Lang.F("wipe.failed", failure));
                }
                if (runtimeStopped && requestRestart != null) requestRestart();
            }
            finally { if (finish != null) finish(); }
        }

        internal static void CompleteUninstall(UninstallReport report, bool finalizerStarted,
            string finalizerFailure, Action<bool, string> showResult, Action finish)
        {
            try
            {
                bool stopped = report != null && report.RuntimeStopped;
                bool warning = !stopped || !report.Cleared || report.Warnings > 0 || !finalizerStarted;
                string message;
                if (!stopped)
                    message = Lang.F("uninstall.failed", ShortReason(report == null ? null : report.Failure));
                else if (report.Warnings > 0 || !report.Cleared)
                    message = Lang.F("uninstall.partial", Math.Max(1, report.Warnings));
                else message = Lang.T("uninstall.done");
                if (stopped)
                    message += "\r\n\r\n" + (finalizerStarted ? Lang.T("uninstall.selfdelete")
                        : Lang.F("uninstall.manualdelete", ShortReason(finalizerFailure)));
                if (showResult != null) showResult(warning, message);
            }
            finally { if (finish != null) finish(); }
        }

        private static string ShortReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return Lang.T("wipe.cleanupfail");
            string first = reason.Replace('\r', '\n').Split('\n')[0].Trim();
            return first.Length > 180 ? first.Substring(0, 180) + "…" : first;
        }
    }
}
