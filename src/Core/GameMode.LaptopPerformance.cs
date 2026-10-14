using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool laptopPerfActive;

        public bool LaptopPerformanceEnabled
        { get { return Settings.LoadCached(LaptopPerfMode.PreferenceKey, false); } }

        public bool TrySetLaptopPerformanceEnabled(bool value)
        {
            lock (sync)
            {
                if (stopping || !Settings.Save(LaptopPerfMode.PreferenceKey, value)) return false;
            }
            if (value) ClearEnvFuse("laptopperf");
            RequestPolicyApply();
            return true;
        }

        private bool LaptopPerformanceAdmitted()
        {
            return !PowerPlan.NoSwitchSelected && LaptopPerformanceEnabled && enabled && active && !stopping && !panicReq
                && !ProfileStoreSaveFailed;
        }

        private void SyncLaptopPerformance(bool powerReady)
        {
            bool wanted = LaptopPerformanceAdmitted() && LaptopPerfMode.CanActivate;
            if (wanted && !laptopPerfActive && !LaptopPerfMode.HasResidue && !powerReady) return;
            if (!laptopPerfActive && LaptopPerfMode.HasResidue) wanted = false;
            laptopPerfActive = EnvStep("laptopperf", wanted,
                laptopPerfActive || (!wanted && LaptopPerfMode.HasResidue),
                delegate { return LaptopPerfMode.Activate(LaptopPerformanceAdmitted); }, LaptopPerfMode.Restore);
        }
    }
}
