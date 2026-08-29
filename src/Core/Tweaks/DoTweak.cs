// @author bdth 2074055628@qq.com
// 文件用途 限制传递优化并管理相关服务状态
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class DoTweak
    {
        // 会话日记键由恢复完成判定共同引用 改名必须两边一起
        internal const string BandwidthJournalKey = "PrevDoBgBw";
        internal const string StopFlag = "PrevDoSvcStopped";
        private static readonly ReversibleReg BgBw = new ReversibleReg(
            Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization",
            "DOMaxBackgroundDownloadBandwidth", RegistryValueKind.DWord, BandwidthJournalKey);
        private const string SvcName = "DoSvc";
        private static readonly ServicePauser pauser = new ServicePauser(new[] { SvcName }, StopFlag);
        private static readonly object lk = new object();
        private static bool active;

        public static bool HasResidue
        {
            get { lock (lk) { return HasBandwidthBackup() || pauser.HasResidue; } }
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                bool registryOk = false;
                if (!IsDomainJoined()) registryOk = ApplyBandwidth();
                else Logger.Log(Lang.T("log.dotweak.9"));
                List<string> stopped, confirmed;
                bool ledgerLost;
                bool serviceOk = pauser.Activate(out stopped, out confirmed, out ledgerLost);
                if (ledgerLost) Logger.Log(Lang.T("log.dotweak.1"));
                active = registryOk || (serviceOk && stopped.Count > 0);
                Logger.Log(active
                    ? Lang.T("log.dotweak.2") + (registryOk ? Lang.T("log.dotweak.3") : Lang.T("log.dotweak.4"))
                        + (serviceOk && confirmed.Count > 0 ? Lang.T("log.dotweak.5") : "") + " "
                    : Lang.T("log.dotweak.6"));
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool hadBandwidth = HasBandwidthBackup();
                bool bandwidthOk = !hadBandwidth || RestoreBandwidth();
                bool hadService = pauser.HasResidue;
                List<string> remain;
                bool serviceOk = pauser.Restore(out remain);
                if (!serviceOk) Logger.Log(Lang.T("log.dotweak.7"));
                active = false;
                bool complete = bandwidthOk && !HasBandwidthBackup() && serviceOk && !pauser.HasResidue;
                if (complete && (hadBandwidth || hadService)) Logger.Log(Lang.T("log.dotweak.8"));
                return complete;
            }
        }

        public static void HealFromCrash()
        {
            if (HasResidue) Restore();
        }

        private static bool IsDomainJoined()
        {
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return DomainJoinedForTest == null || DomainJoinedForTest();
#else
                return Native.IsDomainJoined();
#endif
            }
            catch { return true; }
        }

        private static bool HasBandwidthBackup()
        {
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (BandwidthHasBackupForTest != null) return BandwidthHasBackupForTest();
#endif
                return BgBw.HasBackup;
            }
            catch { return true; }
        }

        private static bool ApplyBandwidth()
        {
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return BandwidthApplyForTest != null && BandwidthApplyForTest();
#else
                return BgBw.Apply(1);
#endif
            }
            catch { return false; }
        }

        private static bool RestoreBandwidth()
        {
            try
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return BandwidthRestoreForTest != null && BandwidthRestoreForTest();
#else
                return BgBw.Restore();
#endif
            }
            catch { return false; }
        }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static Func<bool> DomainJoinedForTest, BandwidthHasBackupForTest;
        internal static Func<bool> BandwidthApplyForTest, BandwidthRestoreForTest;
        internal static ServicePauser PauserForTest { get { return pauser; } }

        internal static void ResetForTest()
        {
            lock (lk)
            {
                active = false; pauser.ResetForTest();
                DomainJoinedForTest = null; BandwidthHasBackupForTest = null;
                BandwidthApplyForTest = null; BandwidthRestoreForTest = null;
            }
        }
#endif
    }
}
