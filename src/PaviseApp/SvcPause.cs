using System.Collections.Generic;

namespace PaviseApp
{
    internal static class SvcPause
    {

        internal static readonly string[] Names = new string[2] { "SysMain", "WSearch" };

        private static readonly ServicePauser pauser = new ServicePauser(Names, "PrevSvcPaused");

        internal static string[] CaptureConfirmedStops() { return pauser.CaptureConfirmedStops(); }

        public static bool Activate()
        {
            List<string> justStopped;
            List<string> confirmedStopped;
            bool ledgerLost;
            bool result = pauser.Activate(out justStopped, out confirmedStopped, out ledgerLost);
            if (ledgerLost)
            {
                Logger.Log(Lang.T("log.svcpause.1", 0));
                return false;
            }
            if (confirmedStopped.Count > 0)
            {
                Logger.Log(Lang.T("log.svcpause.2", 0) + string.Join(" ", confirmedStopped.ToArray()));
            }
            else if (justStopped.Count > 0)
            {
                Logger.Log(Lang.T("log.svcpause.3", 0) + string.Join(" ", justStopped.ToArray()));
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
                    Logger.Log(Lang.T("log.svcpause.4", 0));
                }
                else
                {
                    Logger.Log(Lang.T("log.svcpause.5", 0) + ((remain.Count == 0) ? Lang.T("t.versionmigrations.2", 0) : string.Join(" ", remain.ToArray())) + Lang.T("log.svcpause.6", 0));
                }
            }
            return restoreSucceeded;
        }

        public static bool HasResidue()
        {
            return pauser.HasResidue;
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
