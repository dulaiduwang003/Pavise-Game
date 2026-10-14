using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal static bool RuntimeBackgroundExempt(BackgroundReleaseState state)
        {
            return state == BackgroundReleaseState.Ready || state == BackgroundReleaseState.Gone
                || state == BackgroundReleaseState.OtherReasonActive;
        }
        private void RecordRuntimeProtection(ProcEntry process, bool freezeReleased,
            bool capReleased, BackgroundReleaseState backgroundState)
        {
            string source = "platform-runtime-" + process.Pid + "-" + process.Creation;
            if (!freezeReleased || !capReleased || !RuntimeBackgroundExempt(backgroundState))
            {
                string identity = "pid=" + process.Pid + " born=" + process.Creation
                    + " exe=" + Path.GetFileName(process.Path ?? "");
                string detail = " background=" + backgroundState + " job=" + (capReleased ? "released" : "pending")
                    + " freeze=" + (freezeReleased ? "released" : "pending");
                Logger.RepeatedError(source, "AUTO [" + AutomationSession + "] [PLATFORM] restore-pending " + identity + detail);
                return;
            }
            Logger.RepeatedRecovered(source);
        }
    }
}
