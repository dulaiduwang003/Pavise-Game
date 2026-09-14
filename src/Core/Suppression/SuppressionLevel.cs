// @author bdth 2074055628@qq.com
// File purpose Background suppression level definitions and their labels
using System;

namespace PaviseApp
{
    // Since 2.0 the background side uses only None and Isolated; the two middle levels stay because the anti-cheat side still uses them
    //   Tamer's per-group suppression levels persist as eco / res tags; removing enum values would break reading old config
    //   Frozen was removed together with the match-freeze feature; suppression stops at Isolated, no process is ever suspended
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
