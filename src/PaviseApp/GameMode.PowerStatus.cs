using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private string powerFailureLogSignature;

        internal string PowerPlanExecutionState
        {
            get
            {
                lock (sync)
                {
                    if (envRestorePending.Contains("powerplan")) return "restore-failed";
                    // 掌机跳过策略可清除历史应用失败，但不能清除未完成的恢复债务。
                    if (PowerPlan.HandheldBypass) return "unchanged";
                    // 正在执行的应用属于待完成，不属于已停用。
                    if (ActionFailureStore.IsBlocked("PowerPlanOn", "system") && !ActionFailureStore.IsRunning("PowerPlanOn", "system")) return "stopped";
                    if (!planSwitch || PowerPlan.NoSwitchSelected) return "unchanged";
                    if (!enabled || !active || stopping || panicReq || powerSessionEnding || gameGoneSinceTicks != 0) return "";
                    return planActive && Volatile.Read(ref powerApplyInFlight) == 0
                        && Volatile.Read(ref powerPlanNotificationPending) == 0
                        && Volatile.Read(ref laptopPerfPowerGeneration) == Volatile.Read(ref powerSessionGen)
                        ? "applied" : "unverified";
                }
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

        internal static string PowerPlanFailureDetail(string state)
        {
            if (state != "stopped" && state != "restore-failed") return "";
            string reason = ActionFailureStore.FailureReason("PowerPlanOn", "system");
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
