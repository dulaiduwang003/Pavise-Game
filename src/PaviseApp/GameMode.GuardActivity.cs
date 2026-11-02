using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal long GuardActivitySession { get { return Interlocked.Read(ref gameSessionStarts); } }

        internal GuardActivitySnapshot CaptureGuardActivity()
        {
            long session = GuardActivitySession;
            if (!Enabled || !IsActive) return null;
            var processes = new List<KeyValuePair<int, long>>(core.CaptureAppliedActivity());
            processes.AddRange(backgroundCpuCap.CaptureAppliedActivity());
            processes.AddRange(backgroundFreeze.CaptureAppliedActivity());
            var services = new List<string>(SvcPause.CaptureConfirmedStops());
            services.AddRange(DoTweak.CaptureConfirmedStops());
            services.AddRange(UpdatePause.CaptureConfirmedStops());
            services.AddRange(OptionalServicePause.CaptureConfirmedStops());
            ProcessSnapshot scan = Volatile.Read(ref whiteEvalSnapshot);
            ProcessSnapshot antiCheatScan = antiCheatProcessSnapshot;
            if (scan == null || (antiCheatScan != null && antiCheatScan.TakenTicks > scan.TakenTicks)) scan = antiCheatScan;
            if (!Enabled || !IsActive || session != GuardActivitySession) return null;
            return GuardActivitySnapshot.Create(session, scan, processes, services);
        }
    }
}
