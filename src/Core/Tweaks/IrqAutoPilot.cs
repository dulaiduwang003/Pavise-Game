// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class IrqAutoPilot
    {
        internal const string EnabledKey = "IrqAutoOn";
        internal const string PlanKey = "IrqAutoPlanV1";
        internal const string FuseKey = "IrqAutoFuseV1";

        private static readonly object lk = new object();
        private static readonly IrqAffinityEngine engine =
            new IrqAffinityEngine("IrqAutoAppliedV1", "IrqAuto_", Lang.T("irqauto.logprefix"));

        public static bool HasResidue
        {
            get { return engine.HasResidue || Settings.LoadStr(PlanKey, "").Length != 0; }
        }

        // 旧自动分配收据仍归原恢复路径管理。
        public static List<string> TouchedDevices() { return engine.TouchedDevices(); }

        public static bool RevertAll()
        {
            lock (lk)
            {
                bool ok = engine.Disable(null);
                if (ok && !Settings.SaveStr(PlanKey, "")) ok = false;
                return ok;
            }
        }

        public static void HealFromCrash()
        {
            try
            {
                Settings.Save(EnabledKey, false);
                if (HasResidue && RevertAll()) Settings.SaveStr(FuseKey, "");
            }
            catch { }
        }

        public static void ClearForReset()
        {
            lock (lk)
            {
                Settings.SaveStr(PlanKey, "");
                Settings.SaveStr(FuseKey, "");
                Settings.Save(EnabledKey, false);
            }
        }
    }
}
