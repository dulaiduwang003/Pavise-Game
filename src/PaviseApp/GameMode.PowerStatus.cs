using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private string powerFailureLogSignature;

        internal struct PowerPlanStatusSnapshot
        {
            internal readonly string State, Detail;
            internal PowerPlanStatusSnapshot(string state, string detail)
            { State = state; Detail = detail; }
        }

        internal string PowerPlanExecutionState
        {
            get { return ReadPowerPlanStatus().State; }
        }

        internal PowerPlanStatusSnapshot ReadPowerPlanStatus()
        {
            lock (sync)
            {
                // 掌机绕过时不读取应用状态；未完成的恢复仍须保留。
                if (!envRestorePending.Contains("powerplan") && PowerPlan.HandheldBypass)
                    return new PowerPlanStatusSnapshot("unchanged", "");
                ActionFailureStore.ExecutionSnapshot execution = ActionFailureStore.ReadExecutionSnapshot("PowerPlanOn", "system");
                string state;
                if (envRestorePending.Contains("powerplan")) state = "restore-failed";
                else if (execution.Stopped) state = "stopped";
                else if (!planSwitch || PowerPlan.NoSwitchSelected) state = "unchanged";
                else if (!enabled || !active || stopping || panicReq || powerSessionEnding || gameGoneSinceTicks != 0) state = "";
                else state = !execution.Running && planActive && Volatile.Read(ref powerApplyInFlight) == 0
                        && Volatile.Read(ref powerPlanNotificationPending) == 0
                        && Volatile.Read(ref laptopPerfPowerGeneration) == Volatile.Read(ref powerSessionGen)
                        ? "applied" : "unverified";
                return new PowerPlanStatusSnapshot(state, PowerPlanFailureDetail(state, execution));
            }
        }

        private void RecordPowerPlanStatus()
        {
            lock (sync)
            {
                PowerPlanStatusSnapshot snapshot = ReadPowerPlanStatus();
                RecordAutomationSnapshot("PowerPlanOn", snapshot.State, snapshot.Detail);
            }
        }

        internal string PowerPlanStatusText
        {
            get
            {
                switch (PowerPlanExecutionState)
                {
                    case "restore-failed": return Lang.T("st.power.restore.failed");
                    case "stopped": return Lang.T("st.power.stopped");
                    case "applied": return Lang.T("st.power.verified");
                    case "unverified": return Lang.T("st.power.pending");
                    case "unchanged": return Lang.T("st.power.unchanged");
                    default: return Lang.T("st.power.waiting");
                }
            }
        }

        private static string PowerPlanFailureDetail(string state, ActionFailureStore.ExecutionSnapshot execution)
        {
            if (state != "stopped" && state != "restore-failed") return "";
            string reason = execution.FailureReason;
            if (string.IsNullOrEmpty(reason)) reason = "电源计划恢复尚未确认";
            return "reason=" + reason + " automatic-attempts=bounded recovery=retained";
        }

        private static bool RunPowerPlanAction(Func<bool> action, out bool skipped, out string reason)
        {
            PowerPlan.ClearApplyFailureReason();
            bool ok = RunEnvironmentAction(action, out skipped, out reason);
            if (!ok && !skipped && PowerPlan.LastApplyFailureReason.Length > 0)
                reason = PowerPlan.LastApplyFailureReason;
            return ok;
        }
    }
}
