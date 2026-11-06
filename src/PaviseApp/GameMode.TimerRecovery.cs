namespace PaviseApp
{
    internal partial class GameMode
    {
        private string timerFailureLogSignature;

        private void ApplyEnvironmentTimerOptions()
        {
            SyncTimerAutomaticCycle();
            timerMode.SyncEnvironmentPreferences();
            RecordAutomation("AutoTimerTick", timerMode.TickState);
            RecordAutomation("AutoGlobalTimer", AutomaticExecutionState("AutoGlobalTimer"));
        }

        private void SyncTimerAutomaticCycle()
        {
            string cycle = System.Threading.Interlocked.Read(ref gameSessionStarts).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\ntimer";
            timerMode.BeginAutomaticCycle(cycle);
            timerLease.BeginAutomaticCycle(cycle);
        }

        private string GlobalTimerExecutionState()
        {
            bool residue;
            string lease = timerLease.ReadState(out residue);
            if (residue) return lease.Length > 0 ? lease : "restore-failed";
            if (!PerformancePolicy.EnvironmentPreference("AutoGlobalTimer")) return timerMode.GlobalState;
            if (!timerMode.GlobalReady) return timerMode.GlobalState.Length > 0 ? timerMode.GlobalState : "unverified";
            if (ActionFailureStore.IsBlocked(TimerResolutionLease.FailureAction, "system")) return "stopped";
            return lease.Length > 0 ? lease : timerMode.GlobalState;
        }

        private string GlobalTimerFailureDetail(string state)
        {
            if (state == "timer-locked" || state == "timer-limited" || state == "timer-unverified")
                return timerLease.AcquisitionDetail;
            if (state != "stopped" && state != "restore-failed" && state != "failed") return "";
            string detail = timerLease.FailureDetail;
            string boot = ActionFailureStore.FailureReason("AutoGlobalTimer", "system");
            return detail.Length == 0 ? boot : detail + (boot.Length == 0 ? "" : " boot={" + boot + "}");
        }
    }
}
