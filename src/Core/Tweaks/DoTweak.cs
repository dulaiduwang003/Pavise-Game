// @author bdth 2074055628@qq.com
// 文件用途 限制传递优化并管理相关服务状态

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class DoTweak
    {
        private static readonly ReversibleReg BgBw = new ReversibleReg(
            Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization",
            "DOMaxBackgroundDownloadBandwidth", RegistryValueKind.DWord, "PrevDoBgBw");
        private const string SvcName = "DoSvc";
        private const string StopFlag = "PrevDoSvcStopped";
        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                // 域机上该策略键归组策略管 刷新周期会覆盖本地写入并与还原互相打架 只走服务停启通道
                bool registryOk = false;
                if (!Native.IsDomainJoined()) registryOk = BgBw.Apply(1);
                else Logger.Log(Lang.T("log.dotweak.9"));
                int before = SvcState.Query(SvcName);
                if (before == 4) Settings.SaveStr(StopFlag, "1");
                bool confirmedStop;
                bool stopped = SvcCtl.StopIfRunning(SvcName, out confirmedStop);
                if (!confirmedStop && before == 4 && SvcState.StopTaken(SvcState.Query(SvcName)))
                    confirmedStop = true;
                if (!stopped) stopped = confirmedStop;
                if (stopped)
                {
                    Settings.SaveStr(StopFlag, "1");
                    if (Settings.LoadStr(StopFlag, "") != "1")
                    {
                        SvcCtl.EnsureStarted(SvcName);
                        stopped = false;
                        Logger.Log(Lang.T("log.dotweak.1"));
                    }
                }
                else if (before == 4) Settings.SaveStr(StopFlag, "");
                active = registryOk || stopped;
                Logger.Log(active
                    ? Lang.T("log.dotweak.2") + (registryOk ? Lang.T("log.dotweak.3") : Lang.T("log.dotweak.4"))
                        + (confirmedStop ? Lang.T("log.dotweak.5") : "") + " "
                    : Lang.T("log.dotweak.6"));
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool did = false;
                if (BgBw.HasBackup && BgBw.Restore()) did = true;
                if (Settings.LoadStr(StopFlag, "").Length > 0)
                {
                    if (SvcCtl.EnsureStarted(SvcName)) { Settings.SaveStr(StopFlag, ""); did = true; }
                    else Logger.Log(Lang.T("log.dotweak.7"));
                }
                if (did) Logger.Log(Lang.T("log.dotweak.8"));
                active = false;
                return !BgBw.HasBackup && Settings.LoadStr(StopFlag, "").Length == 0;
            }
        }

        public static void HealFromCrash()
        {
            if (BgBw.HasBackup || Settings.LoadStr(StopFlag, "").Length > 0) Restore();
        }

    }
}
