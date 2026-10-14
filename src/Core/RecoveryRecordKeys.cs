using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 还原所需的原值不得随用户偏好清除。
    internal static class RecoveryRecordKeys
    {
        private static readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PerformanceActionReceiptsV1", "NvDrsList", "AmdSnap", "AppGpuPreferencesV1",
            "GpuPrefStage", "GpuPowerSnap", "GpuClockLockReceipt", "NvVrrModeSnap",
            "IntelEnduranceSnap", "LaptopPerfSnap", "PowerOverlaySnap", "PrevPowerPlan",
            "ExtremePowerKnobSnap", "CoreParkSnap", "CpuIdleStateV1", "PrevReservedCpuSets",
            "PrevTimerTick", "PrevRefreshHz", "DisplaySoloSnap", "MemShieldSnap", "PrevMMAgent",
            "PrevDoBgBw", "PrevDoSvcStopped", "PrevUpdatePaused", "PrevMaintDisabled",
            "PrevSvcPaused", "PrevOptionalServicesPausedV1", "PrevPresenceQos", "RssSteerReceipt",
            "LinkMetricReceipt", "IrqPrio", "BackgroundCpuCapJobsV1", "AutoCoreThreadPlacementV1",
            "FrameCoordinationJournalV1", "FrameMemoryProtectionJournalV1",
            "Crash_ThrottleMasks", "Crash_BoostMask", "Crash_BoostNames", "Crash_BoostEntriesV2",
            "FthExclusionBak", "FthCustomBak", "CfgList", "IfeoList", "MsiList",
            "DpiExeList", "FsoExeList"
        };
        private static readonly string[] prefixes = {
            "PerformanceReceipt_", "NvDrs_", "CfgMk_", "IfeoMk_", "MsiMk_",
            "Prev", "Mmcss_", "IfeoCpu_", "IfeoPri_", "IfeoIo_", "IfeoPg_", "Msi_",
            "IrqReloc_", "IrqAuto_", "GpuIrqAuto_", "NicAff_"
        };
        internal static bool Contains(string name)
        {
            if (string.IsNullOrEmpty(name) || IsFailureRecord(name)) return false;
            if (names.Contains(name)) return true;
            foreach (string prefix in prefixes)
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        internal static bool IsFailureRecord(string name)
        {
            return name != null && (name.StartsWith("ActionFailure_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ActionAttempt_", StringComparison.OrdinalIgnoreCase));
        }
    }
}
