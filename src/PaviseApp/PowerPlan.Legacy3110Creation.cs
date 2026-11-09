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

        // 从标准模板创建，使用旧参数表调节。
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
                Logger.Info("电源 来源=" + Legacy3110Source + " 方法=复制模板；复制来源=" + source.ToString("D"));
                return true;
            }
            catch (Exception error)
            {
                return PowerApplyFailed("legacy3110-creation", error.GetType().Name + ": " + error.Message);
            }
        }
    }
}
