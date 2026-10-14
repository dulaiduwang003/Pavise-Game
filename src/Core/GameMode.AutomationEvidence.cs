using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly Dictionary<string, string> automationEvidence = new Dictionary<string, string>(StringComparer.Ordinal);
        private string automationSession = "";
        private string automationSessionName = "";
        private bool automationSessionEnding;
        private long awakeAppliedLogTicks, awakeRestoredLogTicks;
        private bool awakeFinalRestoreLogged;
#if PAVISE_SELFTEST
        internal Func<long> AutomationLogNowForTest;
#endif
        internal string AutomationSession { get { lock (sync) return automationSession; } }
        private void RecordAutomation(string key, string state)
        {
            if (!AutomaticPolicy.Enabled) return;
            lock (sync)
            {
                string previous;
                automationEvidence.TryGetValue(key, out previous);
                bool finalAwakeRestore = key == "awake" && state == "restored"
                    && automationSessionEnding && !awakeFinalRestoreLogged;
                if (previous == state && !finalAwakeRestore) return;
                automationEvidence[key] = state;
                if (key == "awake" && !ShouldLogAwakeTransition(state, previous)) return;
                if (string.IsNullOrEmpty(state) || state == "unchanged") return;
                if (key == "session" && !string.IsNullOrEmpty(automationSession))
                {
                    string phase = state == "started" ? "start" : state;
                    string boundary = "SESSION [" + automationSession + "] " + phase + " | " + automationSessionName;
                    if (state == "restore-failed") Logger.Warn(boundary);
                    else if (state == "restored") Logger.Log(Logger.SuccessTag + boundary);
                    else Logger.Info(boundary);
                }
                string line = "AUTO [" + automationSession + "] " + AutomationActionLabel(key) + " | " + AutomationStateLabel(state);
                if (key == PolicyCatalog.KeyGlobalTimer)
                {
                    string detail = GlobalTimerResolutionDetail;
                    if (detail.Length > 0) line += " | " + detail;
                }
                if (state == "failed" || state == "restore-failed") Logger.Warn(line);
                else if (state == "applied" || state == "restored" || state == "started" || state == "timer-locked") Logger.Log(Logger.SuccessTag + line);
                else Logger.Info(line);
            }
        }

        private bool ShouldLogAwakeTransition(string state, string previous)
        {
            if (state != "applied" && state != "restored") return true;
            bool finalRestore = state == "restored" && automationSessionEnding;
            bool recovered = previous == "failed" || previous == "restore-failed";
            if (finalRestore && awakeFinalRestoreLogged && !recovered) return false;
            long now = DateTime.UtcNow.Ticks;
#if PAVISE_SELFTEST
            if (AutomationLogNowForTest != null) now = AutomationLogNowForTest();
#endif
            long last = state == "applied" ? awakeAppliedLogTicks : awakeRestoredLogTicks;
            if (!finalRestore && !recovered && last != 0 && now >= last
                && now - last < TimeSpan.TicksPerMinute) return false;
            if (state == "applied") awakeAppliedLogTicks = now;
            else awakeRestoredLogTicks = now;
            if (finalRestore) awakeFinalRestoreLogged = true;
            return true;
        }

        internal static string AutomationActionLabel(string key)
        {
            if (key == "session") return Lang.T("auto.log.session");
            if (key == AutomaticFsoKey) return Lang.T("auto.fso");
            PolicyItem item = PolicyCatalog.ItemOf(key);
            return item != null ? Lang.T(item.LangKey) : EnvLabel(key);
        }

        internal static string AutomationStateLabel(string state)
        {
            switch (state ?? "")
            {
                case "": return Lang.T("auto.log.empty");
                case "started": return Lang.T("auto.log.started");
                case "applied": return Lang.T("auto.log.applied");
                case "configured": return Lang.T("auto.log.configured");
                case "fso-configured": return Lang.T("auto.fso.configured");
                case "restored": return Lang.T("auto.log.restored");
                case "failed": return Lang.T("auto.log.failed");
                case "restore-failed": return Lang.T("auto.log.restore-failed");
                case "pending-reboot": return Lang.T("auto.log.pending-reboot");
                case "restore-pending-reboot": return Lang.T("auto.log.restore-pending-reboot");
                case "ready": return Lang.T("auto.log.ready");
                case "unverified": return Lang.T("env.performance.unverified");
                case "timer-requested": return Lang.T("auto.log.timer-requested");
                case "timer-locked": return Lang.T("auto.log.timer-locked");
                case "timer-limited": return Lang.T("auto.log.timer-limited");
                case "timer-unverified": return Lang.T("auto.log.timer-unverified");
                case "unsupported": return Lang.T("auto.log.unsupported");
                case "unchanged": return Lang.T("auto.log.unchanged");
                case "stopped": return Lang.T("auto.log.stopped");
                default: return state;
            }
        }
        internal string AutomaticExecutionState(string key)
        {
            // Service admission can acquire sync while holding its engine lock.
            // Sample its status before taking sync to avoid reversing that order.
            bool serviceActive = key == PolicyCatalog.KeyPauseServices && OptionalServicePause.Active;
            int pendingServices = serviceActive ? OptionalServicePause.PendingStopCount : 0;
            lock (sync)
            {
                if (key == AutomaticFsoKey) return automaticFsoState;
                if (key == PolicyCatalog.KeyTimerTick) return timerMode.TickState;
                if (key == PolicyCatalog.KeyGlobalTimer)
                {
                    string leaseState = timerLease.State;
                    return leaseState.Length > 0 ? leaseState : timerMode.GlobalState;
                }
                if (key == PolicyCatalog.KeyIdlePolicy)
                    return PowerPlan.IdlePolicyActive ? "applied" : PowerPlan.IdlePolicyHasResidue ? "restore-failed" : "";
                string env = null;
                foreach (string candidate in EnvKeys)
                    if (EnvPolicyKey(candidate) == key) { env = candidate; break; }
                string value;
                if (env != null && envRestorePending.Contains(env)) return "restore-failed";
                if (env != null && envFused.Contains(env)) return "stopped";
                if (serviceActive) return pendingServices > 0 ? "unverified" : "applied";
                if (key == PolicyCatalog.KeyPowerPlan && powerStoppedKeys.Contains(lastPowerPolicyKey)) return "stopped";
                if (automationEvidence.TryGetValue(env ?? key, out value)) return value;
                return "";
            }
        }
        internal string GlobalTimerResolutionDetail
        {
            get
            {
                bool verified; uint actual, finest;
                if (!timerLease.Readback(out verified, out actual, out finest)) return "";
                if (!verified) return Lang.T("env.gtimer.query-unverified");
                return Lang.F("env.gtimer.readback",
                    (TimerResolutionLease.Desired / 10000.0).ToString("F4", CultureInfo.InvariantCulture),
                    (actual / 10000.0).ToString("F4", CultureInfo.InvariantCulture),
                    (finest / 10000.0).ToString("F4", CultureInfo.InvariantCulture));
            }
        }
        private void BeginAutomationEvidence()
        {
            if (!AutomaticPolicy.Enabled) return;
            lock (sync)
            {
                EndAutomationEvidence();
                automationEvidence.Clear();
                automationSession = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                automationSessionName = activeGame ?? (sessionPolicy == null ? "" : sessionPolicy.ProfileName) ?? "";
                automationSessionEnding = false;
                awakeAppliedLogTicks = awakeRestoredLogTicks = 0;
                awakeFinalRestoreLogged = false;
            }
            RecordAutomation("session", "started");
            PolicySnapshot policy = AutomationPolicySnapshot;
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                if (AutomaticPolicy.IsIntent(item.Key) || AutomaticPolicy.IsRetiredInAuto(item.Key)) continue;
                string value = policy.ValueOf(item.Key);
                if (value == "0" || value == "off") RecordAutomation(item.Key, "unchanged");
            }
        }
        private void EndAutomationEvidence()
        {
            lock (sync)
            {
                if (string.IsNullOrEmpty(automationSession) || automationSessionEnding) return;
                automationSessionEnding = true;
                Logger.Info("SESSION [" + automationSession + "] end | " + automationSessionName);
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct AutomationLastInput { internal uint Size, Time; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref AutomationLastInput input);
        private static bool AutomaticQuietDesktop()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return false;
#else
            var input = new AutomationLastInput { Size = (uint)Marshal.SizeOf(typeof(AutomationLastInput)) };
            return GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Time) >= 30000;
#endif
        }
    }
}
