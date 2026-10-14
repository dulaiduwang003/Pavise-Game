// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal enum SuppressionLevel
    {
        None = 0,
        Eco = 1,
        Restrained = 2,
        Isolated = 3
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
                default: return Lang.T("t.backgroundpressurecontroller.9");
            }
        }
    }
}
