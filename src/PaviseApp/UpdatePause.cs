using System.Collections.Generic;

namespace PaviseApp
{
    internal static class UpdatePause
    {
        internal const string Flag = "PrevUpdatePaused";

        private static readonly string[] Names = new string[2] { "wuauserv", "UsoSvc" };

        private static readonly ServicePauser pauser = new ServicePauser(Names, "PrevUpdatePaused");

        // 恢复说明未变化时只做周期性记录。
        private static string lastPendingLine = "";

        private static long nextPendingRepeat;

        public static bool HasResidue
        {
            get
            {
                return pauser.HasResidue;
            }
        }

        internal static string[] CaptureConfirmedStops() { return pauser.CaptureConfirmedStops(); }

        public static bool Activate()
        {
            List<string> justStopped;
            List<string> confirmedStopped;
            bool ledgerLost;
            bool result = pauser.Activate(out justStopped, out confirmedStopped, out ledgerLost);
            if (ledgerLost)
            {
                Logger.Log(Lang.T("log.updatepause.1", 0));
                return false;
            }
            if (justStopped.Count > 0)
            {
                Logger.Log(Lang.T("log.updatepause.2", 0) + string.Join(" + ", justStopped.ToArray()));
            }
            return result;
        }

        public static bool Restore()
        {
            bool hadLedger = pauser.HadLedger;
            List<string> remain;
            bool restoreSucceeded = pauser.Restore(out remain);
            if (hadLedger)
            {
                if (restoreSucceeded)
                {
                    lastPendingLine = "";
                    Logger.Log(Lang.T("log.updatepause.3", 0));
                }
                else
                {
                    string detail = pauser.DescribePending();
                    string line = Lang.T("log.updatepause.4", 0) + ((remain.Count == 0) ? Lang.T("t.versionmigrations.2", 0) : string.Join(",", remain.ToArray()))
                        + (detail.Length == 0 ? "" : "（" + detail + "）") + Lang.T("log.svcpause.6", 0);
                    long now = System.DateTime.UtcNow.Ticks;
                    if (line != lastPendingLine || now >= nextPendingRepeat)
                    {
                        lastPendingLine = line;
                        nextPendingRepeat = now + System.TimeSpan.FromMinutes(10).Ticks;
                        Logger.Log(line);
                    }
                }
            }
                    // 服务启停中的等待交还调度方，不计为恢复失败。
            if (!restoreSucceeded) pauser.ReportWait();
            return restoreSucceeded;
        }

        public static void HealFromCrash()
        {
            if (pauser.HasResidue)
            {
                Restore();
            }
        }
    }
}
