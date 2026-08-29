// Explicit CPU idle opt-in. Native writes and restoration share the power worker gate.
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
                bool global = key == PolicyCatalog.KeyDisableCpuIdle ? disableCpuIdleOn : planSwitch;
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

        private Func<bool> CaptureCpuIdleAdmission(bool allowPowerPlan)
        {
            int generation = Volatile.Read(ref cpuIdleGeneration);
            // Read the live profile once, before taking PowerPlan's native lock.
            // Every user change invalidates this token, including an off/on pair.
            bool wanted = allowPowerPlan && EffDisableCpuIdle
                && LiveCpuIdlePreference(PolicyCatalog.KeyPowerPlan);
            return delegate
            {
                return wanted && generation == Volatile.Read(ref cpuIdleGeneration)
                    && active && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed;
            };
        }

        private void ApplyCpuIdlePolicy(bool allowPowerPlan)
        {
            Func<bool> mayContinue = CaptureCpuIdleAdmission(allowPowerPlan);
            if (!mayContinue() && !cpuIdleActive && !PowerPlan.CpuIdleHasResidue) return;
            lock (powerApplyGate)
            {
                bool want = mayContinue();
                lock (sync) { if (envFused.Contains("cpuidle")) want = false; }
                // Unsupported, battery-powered and user-selected plans are skips,
                // not failed activations. Restore any earlier owned value first.
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
                bool applied = want ? PowerPlan.CpuIdleActive : cpuIdleActive;
                if (!want && PowerPlan.CpuIdleHasResidue) applied = true;
                bool result = EnvStep("cpuidle", want, applied,
                    delegate { return PowerPlan.TryDisableCpuIdle(mayContinue); }, PowerPlan.RestoreCpuIdle);
                // Successful cancellation/rollback and an already-disabled user
                // setting do not grant Pavise ownership of a setting change.
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
