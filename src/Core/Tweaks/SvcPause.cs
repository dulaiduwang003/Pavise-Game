// @author bdth 2074055628@qq.com
// File purpose Pause and resume indexing and prefetch services; bookkeeping goes through ServicePauser, only this group's names and wording live here
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class SvcPause
    {
        internal static readonly string[] Names = { "SysMain", "WSearch" };
        private const string Flag = "PrevSvcPaused";

        private static readonly ServicePauser pauser = new ServicePauser(Names, Flag);

        public static bool Activate()
        {
            List<string> justStopped, confirmed;
            bool ledgerLost;
            bool ok = pauser.Activate(out justStopped, out confirmed, out ledgerLost);
            if (ledgerLost)
            {
                Logger.Log(Lang.T("log.svcpause.1"));
                return false;
            }
            if (confirmed.Count > 0)
                Logger.Log(Lang.T("log.svcpause.2") + string.Join(" ", confirmed.ToArray()));
            else if (justStopped.Count > 0)
                Logger.Log(Lang.T("log.svcpause.3") + string.Join(" ", justStopped.ToArray()));
            return ok;
        }

        public static bool Restore()
        {
            bool had = pauser.HadLedger;
            List<string> remain;
            bool ok = pauser.Restore(out remain);
            if (had)
            {
                if (ok) Logger.Log(Lang.T("log.svcpause.4"));
                else Logger.Log(Lang.T("log.svcpause.5") + (remain.Count == 0 ? Lang.T("t.versionmigrations.2") : string.Join(" ", remain.ToArray())) + Lang.T("log.svcpause.6"));
            }
            return ok;
        }

        public static bool HasResidue() { return pauser.HasResidue; }

        public static void HealFromCrash() { if (pauser.HasResidue) Restore(); }
    }

    internal static class SvcState
    {
        public static bool StopTaken(int state) { return state == 1 || state == 3; }

        public static int Query(string name)
        {
            try
            {
                IntPtr scm = OpenSCManagerW(null, null, 1);
                if (scm == IntPtr.Zero) return 0;
                try
                {
                    IntPtr svc = OpenServiceW(scm, name, 0x4);
                    if (svc == IntPtr.Zero) return 0;
                    try
                    {
                        SERVICE_STATUS st;
                        return QueryServiceStatus(svc, out st) ? st.State : 0;
                    }
                    finally { CloseServiceHandle(svc); }
                }
                finally { CloseServiceHandle(scm); }
            }
            catch { return 0; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public int Type, State, ControlsAccepted, Win32ExitCode, SpecificExitCode, CheckPoint, WaitHint;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machine, string db, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
