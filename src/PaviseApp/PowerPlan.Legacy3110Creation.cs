using System;

namespace PaviseApp
{
    internal interface IManagedPowerLegacy3110Plans : IManagedPowerPlans
    {
        bool CreateLegacy3110(Guid destination);
    }

    internal static partial class PowerPlan
    {
        private const string Legacy3110Source = "legacy-3.1.1.0";

        // Create from a standard template; tune with the legacy parameter table.
        private static bool CreateLegacy3110Native(Guid destination)
        {
            if (HandheldBypass) return false;
            Guid baseline;
            if (!TryReadPresetCreationIntent(destination, out baseline))
                return PowerApplyFailed("legacy3110-creation", "Missing durable creation record");
            try
            {
                var writer = new NativePowerPresetWriter();
                Guid source;
                string failure;
                if (!PowerPresetWriter.TryCopyTemplate(destination, Ultimate, HighPerf, baseline, writer, out source, out failure))
                {
                    applyFailurePermanent |= PowerPresetWriter.PermanentNativeFailure(PowerPresetWriter.LastFailureNativeCode);
                    return PowerApplyFailed("legacy3110-creation", failure);
                }
                Logger.Info("POWER source=" + Legacy3110Source + " method=duplicate-template copySource=" + source.ToString("D"));
                return true;
            }
            catch (Exception error)
            {
                return PowerApplyFailed("legacy3110-creation", error.GetType().Name + ": " + error.Message);
            }
        }
    }
}
