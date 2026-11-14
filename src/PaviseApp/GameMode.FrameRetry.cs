using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private string FrameAutomaticConfiguration()
        {
            lock (sync)
            {
                GameDetection detection = activeDetection;
                return "thread-boost:" + FrameCoordinationEnabled + "|roles:"
                    + (detection == null || detection.Profile == null ? "" : CriticalThreadRule.Raw(CriticalPreferenceProfileLocked(detection.Profile.Id)));
            }
        }
    }
}
