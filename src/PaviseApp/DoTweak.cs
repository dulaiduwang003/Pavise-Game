using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class DoTweak
    {
        internal const string BandwidthJournalKey = "PrevDoBgBw";

        internal const string StopFlag = "PrevDoSvcStopped";

        private static readonly ReversibleReg BgBw = new ReversibleReg(Registry.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization", "DOMaxBackgroundDownloadBandwidth", RegistryValueKind.DWord, "PrevDoBgBw");

        private static readonly ServicePauser pauser = new ServicePauser(new string[1] { "DoSvc" }, "PrevDoSvcStopped");

        private static readonly object lk = new object();

        private static bool active;

        public static bool HasResidue
        {
            get
            {
                lock (lk)
                {
                    return HasBandwidthBackup() || pauser.HasResidue;
                }
            }
        }

        internal static string[] CaptureConfirmedStops() { return pauser.CaptureConfirmedStops(); }

        public static bool Activate()
        {
            lock (lk)
            {
                if (active)
                {
                    return true;
                }
                bool bandwidthApplied = false;
                if (!IsDomainJoined())
                {
                    bandwidthApplied = ApplyBandwidth();
                }
                else
                {
                    Logger.Log(Lang.T("log.dotweak.9", 0));
                }
                List<string> justStopped;
                List<string> confirmedStopped;
                bool ledgerLost;
                bool activateSucceeded = pauser.Activate(out justStopped, out confirmedStopped, out ledgerLost);
                if (ledgerLost)
                {
                    Logger.Log(Lang.T("log.dotweak.1", 0));
                }
                active = bandwidthApplied || (activateSucceeded && justStopped.Count > 0);
                Logger.Log(active ? (Lang.T("log.dotweak.2", 0) + (bandwidthApplied ? Lang.T("log.dotweak.3", 0) : Lang.T("log.dotweak.4", 0)) + ((activateSucceeded && confirmedStopped.Count > 0) ? Lang.T("log.dotweak.5", 0) : "") + " ") : Lang.T("log.dotweak.6", 0));
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool hasBandwidthBackup = HasBandwidthBackup();
                bool bandwidthRestored = !hasBandwidthBackup || RestoreBandwidth();
                bool hasResidue = pauser.HasResidue;
                List<string> remain;
                bool restoreSucceeded = pauser.Restore(out remain);
                if (!restoreSucceeded)
                {
                    Logger.Log(Lang.T("log.dotweak.7", 0));
                }
                active = false;
                bool recoveryComplete = bandwidthRestored && !HasBandwidthBackup() && restoreSucceeded && !pauser.HasResidue;
                if (recoveryComplete && (hasBandwidthBackup || hasResidue))
                {
                    Logger.Log(Lang.T("log.dotweak.8", 0));
                }
                return recoveryComplete;
            }
        }

        public static void HealFromCrash()
        {
            if (HasResidue)
            {
                Restore();
            }
        }

        private static bool IsDomainJoined()
        {
            try
            {
                return Native.IsDomainJoined();
            }
            catch
            {
                return true;
            }
        }

        private static bool HasBandwidthBackup()
        {
            try
            {
                return BgBw.HasBackup;
            }
            catch
            {
                return true;
            }
        }

        private static bool ApplyBandwidth()
        {
            try
            {
                return BgBw.Apply(1);
            }
            catch
            {
                return false;
            }
        }

        private static bool RestoreBandwidth()
        {
            try
            {
                return BgBw.Restore();
            }
            catch
            {
                return false;
            }
        }
    }
}
