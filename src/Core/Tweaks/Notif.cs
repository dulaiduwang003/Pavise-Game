// @author bdth 2074055628@qq.com
// 文件用途 关闭并恢复系统通知弹窗

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class Notif
    {
        private static readonly ReversibleReg Toast = new ReversibleReg(
            Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications",
            "ToastEnabled", RegistryValueKind.DWord, "PrevToast");
        private static readonly object lk = new object();
        private static bool active;

        public static bool Quiet()
        {
            lock (lk)
            {
                if (active) return true;
                active = Toast.Apply(0);
                Logger.Log(active ? "游戏免打扰：已禁用通知弹窗" : "游戏免打扰写入或回读失败，本轮未启用");
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (Toast.HasBackup && Toast.Restore()) Logger.Log("通知弹窗已还原");
                active = false;
                return !Toast.HasBackup;
            }
        }

        public static void HealFromCrash() { if (Toast.HasBackup) Restore(); }
    }
}
