// File purpose Explicitly enabled Disable CPU idle, native write and restore share the power worker thread gate
using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private int cpuIdleGeneration;
        private bool cpuIdleActive;
        private bool cpuIdleWanted;

        private bool EffDisableCpuIdle
        {
            get { return LiveCpuIdlePreference(PolicyCatalog.KeyDisableCpuIdle); }
        }

        private bool LiveCpuIdlePreference(string key)
        {
            lock (sync)
            {
                PolicySnapshot snapshot = sessionPolicy;
                bool global = disableCpuIdleOn;
                // Not offered on Handheld tier, live resolution also reads as off, consistent with the snapshot criteria
                if (snapshot != null && snapshot.Preset == PerformancePreset.Handheld
                    && PolicyCatalog.IsHandheldBlocked(key)) return false;
                if (snapshot == null || string.IsNullOrEmpty(snapshot.ProfileId)) return global;
                foreach (GameProfile profile in profiles)
                    if (string.Equals(profile.Id, snapshot.ProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        string value;
                        return profile.Overrides.TryGetValue(key, out value) ? value == "1" : global;
                    }
                return false;
            }
        }

        // No longer requires power plan takeover to be on, the target scheme is resolved by PowerPlan from the currently active scheme
        private Func<bool> CaptureCpuIdleAdmission(bool ready)
        {
            int generation = Volatile.Read(ref cpuIdleGeneration);
            // Read the current profile once before taking PowerPlan's native lock
            // Every user change invalidates this token, including off-then-on
            bool wanted = ready && EffDisableCpuIdle;
            return delegate
            {
                return wanted && generation == Volatile.Read(ref cpuIdleGeneration)
                    && active && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed;
            };
        }

        private void ApplyCpuIdlePolicy(bool ready)
        {
            Func<bool> mayContinue = CaptureCpuIdleAdmission(ready);
            if (!mayContinue() && !cpuIdleActive && !PowerPlan.CpuIdleHasResidue) return;
            lock (powerApplyGate)
            {
                bool want = mayContinue();
                lock (sync) { if (envFused.Contains("cpuidle")) want = false; }
                // Target scheme unreadable or value changed externally counts as skipped
                // not an activation failure, restore the previously owned value first
                want = want && PowerPlan.CpuIdleEligible;
                lock (sync)
                {
                    if (cpuIdleWanted != want)
                    {
                        cpuIdleWanted = want;
                        envNextAttempt.Remove("cpuidle");
                        envFailures.Remove("cpuidle");
                    }
                }
                bool applied = want ? PowerPlan.CpuIdleActive && !PowerPlan.CpuIdleSchemeDrifted : cpuIdleActive;
                if (!want && PowerPlan.CpuIdleHasResidue) applied = true;
                bool result = EnvStep("cpuidle", want, applied,
                    delegate { return PowerPlan.TryDisableCpuIdle(mayContinue); }, PowerPlan.RestoreCpuIdle);
                // Cancelled or rolled back successfully, and the user having this setting off to begin with
                // none of them give Pavise ownership of this setting change
                cpuIdleActive = want ? result && PowerPlan.CpuIdleActive : result;
            }
        }

#if PAVISE_SELFTEST
        internal bool ProbeEffDisableCpuIdle { get { return EffDisableCpuIdle; } }
        internal Func<bool> CaptureCpuIdleAdmissionForTest() { return CaptureCpuIdleAdmission(true); }
        internal bool StepCpuIdleForTest(bool allowPowerPlan)
        {
            ApplyCpuIdlePolicy(allowPowerPlan);
            return cpuIdleActive;
        }
#endif
    }
}
