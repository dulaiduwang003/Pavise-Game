// @author bdth 2074055628@qq.com
// 文件用途 关闭无输入时的前台降级 官方 QoS 文档给出的开关 可逆
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class PresenceQos
    {
        // 会话日记键由恢复完成判定共同引用 改名必须两边一起
        internal const string JournalKey = "PrevPresenceQos";
        private static readonly ReversibleReg Switch = new ReversibleReg(
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
            "DisableUserPresenceQos", RegistryValueKind.DWord, JournalKey);
        private static readonly object lk = new object();
        private static bool active;

        public static bool HasResidue { get { return Switch.HasBackup; } }

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                active = Switch.Apply(1);
                Logger.Log(active
                    ? Lang.T("log.presenceqos.1")
                    : Lang.T("log.presenceqos.2"));
                return active;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (Switch.HasBackup && Switch.Restore()) Logger.Log(Lang.T("log.presenceqos.3"));
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
