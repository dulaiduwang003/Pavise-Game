// @author bdth 2074055628@qq.com
// 文件用途 对局中在托管电源方案上禁用处理器空闲 用户自行开启 默认关闭 只在专注档且插电时写入
using System;

namespace PaviseApp
{
    internal static class IdleStateTweak
    {
        private const string EnabledKey = "GmIdleDisable";
        private const string LegacyFuseKey = "IdleDisableFused";
        private const string LegacyVerifiedKey = "IdleDisableVerified";
        private const string LegacyBaselineKey = "IdleDisableBaseMhz";

        private static readonly object lk = new object();

        public static bool Enabled { get { return Settings.Load(EnabledKey, false); } }

        public static void SetEnabled(bool on)
        {
            lock (lk)
            {
                Settings.Save(EnabledKey, on);
                PurgeLegacyKeys();
            }
            Logger.Log(Lang.T(on ? "log.idlestate.1" : "log.idlestate.2"));
        }

        public static uint DesiredAc(bool aggressive)
        {
            return aggressive && Enabled ? 1u : 0u;
        }

        private static void PurgeLegacyKeys()
        {
            Settings.Remove(LegacyFuseKey);
            Settings.Remove(LegacyVerifiedKey);
            Settings.Remove(LegacyBaselineKey);
        }
    }
}
