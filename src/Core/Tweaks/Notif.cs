// @author bdth 2074055628@qq.com
// File purpose Residue cleanup for the withdrawn notification do-not-disturb feature; only restores the registry changed by older versions
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
