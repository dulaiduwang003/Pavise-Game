using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal long GuardActivitySession { get { return Interlocked.Read(ref gameSessionStarts); } }

        // SwitchOuts preserves out/back/out transitions between UI observations; Competitive retains its policy.
        internal long GuardActivityForegroundVersion
        { get { return ((long)sessionForeground.SwitchOuts << 1) | (SmartSceneSwitchedOut ? 1L : 0L); } }

        internal GuardActivitySnapshot CaptureGuardActivity()
        {
            long session = GuardActivitySession;
            long foregroundVersion = GuardActivityForegroundVersion;
            bool switchedOut = SmartSceneSwitchedOut;
            if (!Enabled || !IsActive) return null;
            var processes = new List<KeyValuePair<int, long>>(core.CaptureAppliedActivity());
            var services = new List<string>(SvcPause.CaptureConfirmedStops());
            services.AddRange(DoTweak.CaptureConfirmedStops());
            services.AddRange(UpdatePause.CaptureConfirmedStops());
            services.AddRange(OptionalServicePause.CaptureConfirmedStops());
            ProcessSnapshot scan = Volatile.Read(ref whiteEvalSnapshot);
            ProcessSnapshot antiCheatScan = antiCheatProcessSnapshot;
            if (scan == null || (antiCheatScan != null && antiCheatScan.TakenTicks > scan.TakenTicks)) scan = antiCheatScan;
            bool releasePending = switchedOut && core.BackgroundReleasePending;
            if (!Enabled || !IsActive || session != GuardActivitySession
                || foregroundVersion != GuardActivityForegroundVersion || switchedOut != SmartSceneSwitchedOut) return null;
            return GuardActivitySnapshot.Create(session, scan, processes, services, foregroundVersion, switchedOut, releasePending);
        }
    }
}
