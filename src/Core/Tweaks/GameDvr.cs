// @author bdth 2074055628@qq.com
// 文件用途 关闭并恢复游戏后台录制设置

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GameDvr
    {
        private static readonly ReversibleReg Dvr = new ReversibleReg(
            Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", RegistryValueKind.DWord, "PrevGameDvr");
        private static readonly ReversibleReg Cap = new ReversibleReg(
            Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", RegistryValueKind.DWord, "PrevGameDvrCap");
        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                bool dvrOk = Dvr.Apply(0);
                bool capOk = Cap.Apply(0);
                active = dvrOk && capOk;
                Logger.Log(active ? Lang.T("log.gamedvr.1")
                    : dvrOk ? Lang.T("log.gamedvr.2")
                    : Lang.T("log.gamedvr.3"));
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool had = Dvr.HasBackup || Cap.HasBackup;
                bool dvrOk = !Dvr.HasBackup || Dvr.Restore();
                bool capOk = !Cap.HasBackup || Cap.Restore();
                if (had && dvrOk && capOk) Logger.Log(Lang.T("log.gamedvr.4"));
                active = false;
                return !Dvr.HasBackup && !Cap.HasBackup;
            }
        }

        public static void HealFromCrash() { if (Dvr.HasBackup || Cap.HasBackup) Restore(); }
    }
}
