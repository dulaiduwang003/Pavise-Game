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
            // 线程准入拒绝是已知原因：目标自行安排了线程位置，本方按设计不改写，不能归入“原因未知”。
            if (detail.IndexOf(ThreadPlacementRefusal, StringComparison.Ordinal) >= 0)
            {
                return Lang.T("t.backgroundpressurecontroller.10") + "(" + detail + ")";
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
            // 带有具体细节但未归类的失败，不再与“没有任何细节”共用同一句话。
            return Lang.T("t.backgroundpressurecontroller.11") + "(" + detail + ")";
        }

        // 与 GameThreadAffinityAdmission.RefusalPrefix 保持一致；这里只做文本归类，不参与准入判定。
        internal const string ThreadPlacementRefusal = "game-thread-affinity-";
    }
}
