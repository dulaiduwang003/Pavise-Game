// @author bdth 2074055628@qq.com
// 文件用途 校正被优化教程改坏的多媒体网络限流值 回到系统默认

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class NetTweak
    {
        public const int SystemDefault = 10;

        private static readonly ReversibleReg Throttle = new ReversibleReg(
            Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "NetworkThrottlingIndex", RegistryValueKind.DWord, "PrevNetThrottle");
        private static readonly object lk = new object();

        public static bool RepairedByPavise { get { return Settings.Load("NetThrottleRepaired", false); } }

        public static int? Current()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"))
                {
                    if (k == null) return null;
                    object v = k.GetValue("NetworkThrottlingIndex");
                    return v is int ? (int?)(int)v : null;
                }
            }
            catch { return null; }
        }

        public static bool NeedsRepair()
        {
            int? cur = Current();
            if (!cur.HasValue) return false;
            return cur.Value < 1 || cur.Value > 70;
        }

        public static string Describe()
        {
            int? cur = Current();
            if (!cur.HasValue) return Lang.T("netthrottle.absent");
            if (cur.Value < 1 || cur.Value > 70)
                return Lang.F("netthrottle.broken", "0x" + cur.Value.ToString("X"));
            return Lang.F("netthrottle.ok", cur.Value);
        }

        public static bool Repair()
        {
            lock (lk)
            {
                if (!NeedsRepair())
                {
                    Logger.Log("网络限流校正 当前值正常 无需改动");
                    return true;
                }
                if (!Throttle.Apply(SystemDefault))
                {
                    Logger.Log("网络限流校正 写入或回读失败 未改动");
                    return false;
                }
                Settings.Save("NetThrottleRepaired", true);
                Logger.Log("网络限流校正 已改回系统默认 " + SystemDefault + " 重启后生效");
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool ok = !Throttle.HasBackup || Throttle.Restore();
                if (ok)
                {
                    Settings.Save("NetThrottleRepaired", false);
                    Logger.Log("网络限流校正 已还原为改动前的值");
                }
                return ok;
            }
        }

        public static void HealFromCrash() { }
    }
}
