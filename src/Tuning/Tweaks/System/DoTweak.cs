using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class DoTweak
    {
        internal const string BandwidthJournalKey = "PrevDoBgBw";

        internal const string StopFlag = "PrevDoSvcStopped";

        private const string SvcName = "DoSvc";

        private static readonly ReversibleReg BgBw = new ReversibleReg(Registry.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization", "DOMaxBackgroundDownloadBandwidth", RegistryValueKind.DWord, "PrevDoBgBw");

        private static readonly ServicePauser pauser = new ServicePauser(new string[1] { SvcName }, "PrevDoSvcStopped");

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
                    Logger.Log(() => Lang.T("log.dotweak.9"));
                }
                List<string> justStopped;
                List<string> confirmedStopped;
                bool ledgerLost;
                bool activateSucceeded = pauser.Activate(out justStopped, out confirmedStopped, out ledgerLost);
                if (ledgerLost)
                {
                    Logger.Log(() => Lang.T("log.dotweak.1"));
                }
                active = bandwidthApplied || (activateSucceeded && justStopped.Count > 0);
                Logger.Log(() => active ? (Lang.T("log.dotweak.2") + (bandwidthApplied ? Lang.T("log.dotweak.3") : Lang.T("log.dotweak.4")) + ((activateSucceeded && confirmedStopped.Count > 0) ? Lang.T("log.dotweak.5") : "") + " ") : Lang.T("log.dotweak.6"));
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
                    Logger.Log(() => Lang.T("log.dotweak.7"));
                }
                active = false;
                bool recoveryComplete = bandwidthRestored && !HasBandwidthBackup() && restoreSucceeded && !pauser.HasResidue;
                if (recoveryComplete && (hasBandwidthBackup || hasResidue))
                {
                    Logger.Log(() => Lang.T("log.dotweak.8"));
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

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        // 自检构建不查真实的域成员身份 也不写 HKLM 的传递优化带宽策略 都交给替身
        internal static System.Func<bool> DomainJoinedForTest = null;
        internal static System.Func<bool> BandwidthApplyForTest = null, BandwidthRestoreForTest = null;
#endif

        private static bool IsDomainJoined()
        {
#if PAVISE_SELFTEST
            if (DomainJoinedForTest == null) throw new System.InvalidOperationException("Unmocked domain membership read");
#endif
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return DomainJoinedForTest == null || DomainJoinedForTest();
#else
                return Native.IsDomainJoined();
#endif
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
#if PAVISE_SELFTEST
            if (BandwidthApplyForTest == null) throw new System.InvalidOperationException("Unmocked delivery optimization bandwidth write");
#endif
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return BandwidthApplyForTest != null && BandwidthApplyForTest();
#else
                return BgBw.Apply(1);
#endif
            }
            catch
            {
                return false;
            }
        }

        private static bool RestoreBandwidth()
        {
#if PAVISE_SELFTEST
            if (BandwidthRestoreForTest == null) throw new System.InvalidOperationException("Unmocked delivery optimization bandwidth restore");
#endif
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return BandwidthRestoreForTest != null && BandwidthRestoreForTest();
#else
                return BgBw.Restore();
#endif
            }
            catch
            {
                return false;
            }
        }
    }
}
