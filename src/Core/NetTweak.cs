// @author bdth 2074055628@qq.com
// 文件用途 调整并恢复系统网络节流设置

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class NetTweak
    {
        private static readonly ReversibleReg Throttle = new ReversibleReg(
            Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "NetworkThrottlingIndex", RegistryValueKind.DWord, "PrevNetThrottle");
        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                active = Throttle.Apply(unchecked((int)0xFFFFFFFF));
                Logger.Log(active ? "网络节流已关闭" : "网络节流写入或回读失败，本轮未启用");
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (Throttle.HasBackup && Throttle.Restore()) Logger.Log("网络节流已还原");
                active = false;
                return !Throttle.HasBackup;
            }
        }

        public static void HealFromCrash() { if (Throttle.HasBackup) Restore(); }
    }
}
