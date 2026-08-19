// @author bdth 2074055628@qq.com
// 文件用途 对局中禁用处理器空闲已下架 本文件只负责清掉旧版本留下的残留
using System;

namespace PaviseApp
{
    internal static class IdleStateTweak
    {
        private const string EnabledKey = "GmIdleDisable";
        private const string FuseKey = "IdleDisableFused";
        private const string VerifiedKey = "IdleDisableVerified";
        private const string BaselineKey = "IdleDisableBaseMhz";

        public static bool HasResidue()
        {
            if (Settings.Load(EnabledKey, false)) return true;
            if (Settings.Load(FuseKey, false)) return true;
            if (Settings.Load(VerifiedKey, false)) return true;
            if (Settings.LoadStr(BaselineKey, "").Length > 0) return true;
            return PowerPlan.ManagedIdleDisableOn();
        }

        public static bool Restore()
        {
            Settings.Remove(EnabledKey);
            Settings.Remove(FuseKey);
            Settings.Remove(VerifiedKey);
            Settings.Remove(BaselineKey);
            PowerPlan.ClearIdleDisable();
            return !HasResidue();
        }
    }
}
