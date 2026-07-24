// @author bdth 2074055628@qq.com
// 文件用途 限制传递优化并管理相关服务状态

using System;
using Microsoft.Win32;

namespace AegisApp
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
                bool registryOk = BgBw.Apply(1);
                bool stopped = SvcCtl.StopIfRunning(SvcName);
                if (stopped)
                {
                    Settings.SaveStr(StopFlag, "1");
                    if (Settings.LoadStr(StopFlag, "") != "1")
                    {
                        SvcCtl.EnsureStarted(SvcName);
                        stopped = false;
                        Logger.Log("DoSvc 停止状态无法持久化，已重新启动服务");
                    }
                }
                active = registryOk || stopped;
                Logger.Log(active
                    ? "后台下载已暂停（" + (registryOk ? "传递优化带宽 → 1 KB/s" : "带宽策略写入失败")
                        + (stopped ? "，DoSvc 已停止" : "") + "）"
                    : "后台下载策略写入失败且 DoSvc 未停止，本轮未启用");
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
                    else Logger.Log("DoSvc 未能拉起，标志保留待下次重试");
                }
                if (did) Logger.Log("后台下载已还原");
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
