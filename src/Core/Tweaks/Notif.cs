// @author bdth 2074055628@qq.com
// 文件用途 已下架的通知免打扰残留清理 只负责还原旧版本改过的注册表

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

        public static bool HasResidue()
        {
            lock (lk) { return Toast.HasBackup; }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (Toast.HasBackup && Toast.Restore()) Logger.Log(Lang.T("log.notif.1"));
                return !Toast.HasBackup;
            }
        }
    }
}
