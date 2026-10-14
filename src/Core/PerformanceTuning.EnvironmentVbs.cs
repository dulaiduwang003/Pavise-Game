using System;

namespace PaviseApp
{
    internal static partial class PerformanceTuning
    {
        internal const string EnvironmentVbsKey = "EnvVbsDisabledV1";
#if PAVISE_SELFTEST
        internal static Func<bool> VbsConfiguredForTest;
        internal static Func<bool, bool> VbsChangeForTest;
#endif
        private static bool VbsConfigured()
        {
#if PAVISE_SELFTEST
            if (VbsConfiguredForTest == null) throw new InvalidOperationException("Unmocked VBS configuration read");
            return VbsConfiguredForTest();
#else
            return VbsTweak.CurrentlyConfiguredOff();
#endif
        }
        private static bool ChangeVbs(bool on)
        {
#if PAVISE_SELFTEST
            if (VbsChangeForTest == null) throw new InvalidOperationException("Unmocked VBS configuration write");
            return VbsChangeForTest(on);
#else
            return on ? VbsTweak.Disable() : VbsTweak.Restore();
#endif
        }
        private static bool RestoreOwnedVbs()
        { return !VbsTweak.OwnsState || ChangeVbs(false) && !VbsTweak.OwnsState; }
        private static bool SkipVbsApply(bool on, string state)
        {
            if (!on) { SetState(EnvironmentVbsKey, "restore-failed"); return false; }
            bool restored;
            try { restored = RestoreOwnedVbs(); } catch { restored = false; }
            if (restored) ActionFailureStore.CancelApply(EnvironmentVbsKey, "system");
            else ActionFailureStore.Trip(EnvironmentVbsKey, "system", "Recovery incomplete after skipped VBS apply");
            SetState(EnvironmentVbsKey, restored ? state : "restore-failed"); return false;
        }
        internal static bool ApplyEnvironmentVbs(bool on)
        {
            if (EnvironmentWorkClosing) return false;
            string reason;
            if (on)
            {
                try
                {
                    if (ActionFailureStore.IsRetryPending(EnvironmentVbsKey, "system") && !RestoreOwnedVbs())
                        throw new InvalidOperationException("Recovery incomplete before retry");
                }
                catch (Exception error) { ActionFailureStore.Trip(EnvironmentVbsKey, "system", error.Message); SetState(EnvironmentVbsKey, "restore-failed"); return false; }
                if (!ActionFailureStore.TryBeginApply(EnvironmentVbsKey, "system", out reason)) { SetState(EnvironmentVbsKey, VbsTweak.OwnsState ? "restore-failed" : "stopped"); return false; }
            }
            try
            {
                bool changed = on ? !VbsConfigured() : VbsTweak.OwnsState;
                if (!changed)
                {
                    string previous = Settings.LoadStr(EnvironmentVbsKey + "State", "");
                    bool completed = !on || ActionFailureStore.CompleteApply(EnvironmentVbsKey, "system");
                    SetState(EnvironmentVbsKey, !completed ? "stopped" : (on ? previous == "configured" || VbsTweak.OwnsState : previous == "restored")
                        ? on ? "configured" : "restored" : on ? "external" : "off");
                    return completed;
                }
                string boot = Boot();
                if (!BootSessionIdentity.Valid(boot) || EnvironmentWorkClosing)
                { if (on) ActionFailureStore.CancelApply(EnvironmentVbsKey, "system"); SetState(EnvironmentVbsKey, on ? "unverified" : "restore-failed"); return false; }
                bool ok = Settings.SaveStrDurable(EnvironmentVbsKey + "Boot", boot) && ChangeVbs(on);
                if (ok) ok = on ? VbsConfigured() : !VbsTweak.OwnsState;
                if (!ok) throw new InvalidOperationException("VBS apply or readback failed");
                if (on)
                {
                    ok = ActionFailureStore.CompleteApply(EnvironmentVbsKey, "system");
                    if (!ok && !RestoreOwnedVbs()) { SetState(EnvironmentVbsKey, "restore-failed"); return false; }
                }
                SetState(EnvironmentVbsKey, ok ? on ? "configured" : "restored" : "stopped"); return ok;
            }
            catch (NotSupportedException) { return SkipVbsApply(on, "unsupported"); }
            catch (OperationCanceledException) { return SkipVbsApply(on, ""); }
            catch (Exception error)
            {
                if (on)
                {
                    ActionFailureStore.Trip(EnvironmentVbsKey, "system", error.Message);
                    try { if (!RestoreOwnedVbs()) { SetState(EnvironmentVbsKey, "restore-failed"); return false; } } catch { SetState(EnvironmentVbsKey, "restore-failed"); return false; }
                }
                SetState(EnvironmentVbsKey, on ? "stopped" : "restore-failed"); return false;
            }
        }
    }
}
