// @author bdth 2074055628@qq.com
// 文件用途 关闭无输入时的前台降级 官方 QoS 文档给出的开关 可逆

using Microsoft.Win32;

namespace PaviseApp
{
    internal static class PresenceQos
    {
        private static readonly ReversibleReg Switch = new ReversibleReg(
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
            "DisableUserPresenceQos", RegistryValueKind.DWord, "PrevPresenceQos");
        private static readonly object lk = new object();
        private static bool active;

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                active = Switch.Apply(1);
                Logger.Log(active
                    ? "无输入降级已关闭：长时间不动键鼠也不会把游戏降级"
                    : "无输入降级开关写入或回读失败，本轮未启用");
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (Switch.HasBackup && Switch.Restore()) Logger.Log("无输入降级开关已还原");
                active = false;
                return !Switch.HasBackup;
            }
        }

        public static void HealFromCrash() { if (Switch.HasBackup) Restore(); }

        public static bool CurrentlyDisabled()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling"))
                {
                    if (key == null) return false;
                    object raw = key.GetValue("DisableUserPresenceQos");
                    return raw != null && System.Convert.ToInt32(raw) == 1;
                }
            }
            catch { return false; }
        }
    }
}
