// @author bdth 2074055628@qq.com
// 文件用途 自动中断编排已下架 只保留按收据清退历史钉核 残留报告与清除流程接口
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 自动中断编排 2.1.3.3 上架 随后下架 挪核只走中断页的手动流程
    //   旧版本按裁决写过的注册表钉核仍在机器上 这里负责启动时按收据全部还原
    //   还原失败下次启动重试 清除流程兜底 计划与熔断记录一并清空
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

        // 手动挪核页要把仍未清退的自动钉核当成已被管理 不许二次接管
        public static List<string> TouchedDevices() { return engine.TouchedDevices(); }

        public static bool RevertAll()
        {
            lock (lk)
            {
                bool ok = IrqMutationBoundary.Run(delegate { return engine.Disable(null); });
                // 计划清不掉也算失败 残留条目会一直被当成未清退的账
                if (ok && !Settings.SaveStr(PlanKey, "")) ok = false;
                return ok;
            }
        }

        // 下架后的开机清退 开关静默退役 有账就还 直到还清
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
