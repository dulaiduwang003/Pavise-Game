// @author bdth 2074055628@qq.com
// File purpose Pause Windows Update related services during the match and resume afterwards; bookkeeping goes through ServicePauser
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class UpdatePause
    {
        private static readonly string[] Names = { "wuauserv", "UsoSvc" };
        // Session journal key is shared with the restore-complete check, rename both sides together
        internal const string Flag = "PrevUpdatePaused";

        private static readonly ServicePauser pauser = new ServicePauser(Names, Flag);

        public static bool HasResidue { get { return pauser.HasResidue; } }

        public static bool Activate()
        {
            List<string> justStopped, confirmed;
            bool ledgerLost;
            bool ok = pauser.Activate(out justStopped, out confirmed, out ledgerLost);
            if (ledgerLost)
            {
                Logger.Log(Lang.T("log.updatepause.1"));
                return false;
            }
            if (justStopped.Count > 0)
                Logger.Log(Lang.T("log.updatepause.2") + string.Join(" + ", justStopped.ToArray()));
            return ok;
        }

        public static bool Restore()
        {
            bool had = pauser.HadLedger;
            List<string> remain;
            bool ok = pauser.Restore(out remain);
            if (had)
            {
                if (ok) Logger.Log(Lang.T("log.updatepause.3"));
                else Logger.Log(Lang.T("log.updatepause.4") + (remain.Count == 0 ? Lang.T("t.versionmigrations.2") : string.Join(",", remain.ToArray())) + Lang.T("log.svcpause.6"));
            }
            return ok;
        }

        public static void HealFromCrash() { if (pauser.HasResidue) Restore(); }
    }
}
