// @author bdth 2074055628@qq.com
// 文件用途 后台压制的等级定义与对应文案
using System;

namespace PaviseApp
{
    // 2.0 起后台侧只用 None 和 Isolated 两态 中间两档留着是因为反作弊那套还在用
    //   Tamer 的逐组压制等级会持久化成 eco / res 标签 删掉枚举值会读不回旧配置
    internal enum SuppressionLevel
    {
        None = 0,
        Eco = 1,
        Restrained = 2,
        Isolated = 3,
        Frozen = 4
    }

    internal static class ApplyFailureText
    {
        public static string Of(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return Lang.T("t.backgroundpressurecontroller.1");
            bool write = detail.IndexOf("-write", StringComparison.Ordinal) >= 0;
            bool readback = detail.IndexOf("-readback", StringComparison.Ordinal) >= 0;
            if (write && readback) return Lang.T("t.backgroundpressurecontroller.2");
            if (readback) return Lang.T("t.backgroundpressurecontroller.3");
            if (write) return Lang.T("t.backgroundpressurecontroller.4");
            return Lang.T("t.backgroundpressurecontroller.1") + "(" + detail + ")";
        }
    }

    internal static class SuppressionLevelText
    {
        public static string Of(SuppressionLevel level)
        {
            switch (level)
            {
                case SuppressionLevel.Eco: return Lang.T("t.backgroundpressurecontroller.5");
                case SuppressionLevel.Restrained: return Lang.T("t.backgroundpressurecontroller.6");
                case SuppressionLevel.Isolated: return Lang.T("t.backgroundpressurecontroller.7");
                case SuppressionLevel.Frozen: return Lang.T("t.backgroundpressurecontroller.8");
                default: return Lang.T("t.backgroundpressurecontroller.9");
            }
        }
    }
}
