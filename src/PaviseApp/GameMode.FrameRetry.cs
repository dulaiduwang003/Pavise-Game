using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private string FrameAutomaticConfiguration()
        {
            GameDetection detection = Volatile.Read(ref activeDetection);
            return "thread-boost:" + FrameCoordinationEnabled + "|roles:"
                + (detection == null || detection.Profile == null ? "" : CriticalThreadRule.Raw(detection.Profile));
        }
    }
}
