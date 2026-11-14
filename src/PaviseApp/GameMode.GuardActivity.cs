using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal long GuardActivitySession { get { return Interlocked.Read(ref gameSessionStarts); } }

        // SwitchOuts 保留两次界面观测间的切出、切回、再切出过程；竞技策略继续保持。
        internal long GuardActivityForegroundVersion
        { get { return ((long)sessionForeground.SwitchOuts << 1) | (SmartSceneSwitchedOut ? 1L : 0L); } }

        internal GuardActivitySnapshot CaptureGuardActivity()
        {
            long session = GuardActivitySession;
            long foregroundVersion = GuardActivityForegroundVersion;
            bool switchedOut = SmartSceneSwitchedOut;
            if (!Enabled || !IsActive) return null;
            var processes = new List<KeyValuePair<int, long>>(core.CaptureAppliedActivity());
            // 微软后台由独立 Job 治理，合并身份回执后继续统一去重与校验 PID 复用。
            WindowsBackgroundActivitySnapshot windowsActivity = windowsBackground.CaptureActivity();
            bool windowsSession = windowsActivity.Session == session;
            if (windowsSession) processes.AddRange(windowsActivity.Processes);
            var services = new List<string>(SvcPause.CaptureConfirmedStops());
            services.AddRange(DoTweak.CaptureConfirmedStops());
            services.AddRange(UpdatePause.CaptureConfirmedStops());
            services.AddRange(OptionalServicePause.CaptureConfirmedStops());
            ProcessSnapshot scan = Volatile.Read(ref whiteEvalSnapshot);
            ProcessSnapshot antiCheatScan = antiCheatProcessSnapshot;
            if (scan == null || (antiCheatScan != null && antiCheatScan.TakenTicks > scan.TakenTicks)) scan = antiCheatScan;
            bool releasePending = switchedOut && (core.BackgroundReleasePending || windowsActivity.RecoveryPending
                || windowsSession && windowsActivity.Processes.Count != 0);
            bool activityPending = windowsActivity.RecoveryPending || windowsSession && windowsActivity.Pending;
            if (!Enabled || !IsActive || session != GuardActivitySession
                || foregroundVersion != GuardActivityForegroundVersion || switchedOut != SmartSceneSwitchedOut
                || !object.ReferenceEquals(windowsActivity, windowsBackground.CaptureActivity())) return null;
            return GuardActivitySnapshot.Create(session, scan, processes, services, foregroundVersion, switchedOut, releasePending, activityPending);
        }
    }
}
