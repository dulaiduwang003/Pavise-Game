using System;

namespace PaviseApp
{
    internal static class ApplyFailureText
    {
        public static string Of(string detail)
        {
            if (string.IsNullOrEmpty(detail))
            {
                return Lang.T("t.backgroundpressurecontroller.1");
            }
            bool writeFailed = detail.IndexOf("-write", StringComparison.Ordinal) >= 0;
            bool readbackFailed = detail.IndexOf("-readback", StringComparison.Ordinal) >= 0;
            if (writeFailed && readbackFailed)
            {
                return Lang.T("t.backgroundpressurecontroller.2");
            }
            if (readbackFailed)
            {
                return Lang.T("t.backgroundpressurecontroller.3");
            }
            if (writeFailed)
            {
                return Lang.T("t.backgroundpressurecontroller.4");
            }
            return Lang.T("t.backgroundpressurecontroller.1") + "(" + detail + ")";
        }
    }
}
