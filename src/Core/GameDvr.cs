// @author bdth 2074055628@qq.com
// 文件用途 关闭并恢复游戏后台录制设置

using System;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class GameDvr
    {
        private static readonly ReversibleReg Dvr = new ReversibleReg(
            Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", RegistryValueKind.DWord, "PrevGameDvr");
        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                active = Dvr.Apply(0);
                Logger.Log(active ? "Game DVR 后台录制已关闭" : "Game DVR 写入或回读失败，本轮未关闭");
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (Dvr.HasBackup && Dvr.Restore()) Logger.Log("Game DVR 设置已还原");
                active = false;
                return !Dvr.HasBackup;
            }
        }

        public static void HealFromCrash() { if (Dvr.HasBackup) Restore(); }
    }
}
