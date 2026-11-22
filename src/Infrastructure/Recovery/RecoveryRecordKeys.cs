using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class RecoveryRecordKeys
    {
        private static readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PerformanceActionReceiptsV1", "NvDrsList", "AmdSnap", "AppGpuPreferencesV1", "LaptopPerfSnap",
            "PowerOverlaySnap", "PrevPowerPlan", "ManagedPlanPolicyV3", "ManagedPresetCreationV1", "ExtremePowerKnobSnap", "CoreParkSnap", "PrevTimerTick", "PrevRefreshHz",
            "PrevMMAgent", "PrevDoBgBw", "PrevDoSvcStopped", "PrevUpdatePaused", "PrevMaintDisabled", "PrevOptionalServicesPausedV1", "PrevPresenceQos",
            "LinkMetricReceipt", RetiredBackgroundCpuCap.LegacyReceiptKey, RetiredBackgroundCpuCap.ReceiptKey, "GameAffinityJobReceiptsV1", "AutoCoreThreadPlacementV1", "FrameCoordinationJournalV1", "FrameMemoryProtectionJournalV1", "Crash_ThrottleMasks", "Crash_BoostMask", "Crash_BoostNames", "Crash_BoostEntriesV2", "Crash_BoostPriorityOwnershipV1",
            "FthExclusionBak", "FthCustomBak", "MsiList", "FsoExeList", CfgTweak.ListKey, LaboratoryPower.ReceiptKey, LaboratoryGpu.PowerReceipt, LaboratoryGpu.ClockReceipt, AntiCheatAffinityLease.ReceiptKey,
            LaboratoryFileFilter.ReceiptKey, LaboratoryClockRepair.ReceiptKey, LaboratoryAudioEffects.ReceiptKey,
            "RtssInstallationOwnerV1"
        };

        private static readonly string[] prefixes = new string[6]
        {
            "PerformanceReceipt_", "NvDrs_", "MsiMk_", "Prev", "Msi_", "GpuIrqAuto_"
        };

        internal static bool Contains(string name)
        {
            if (string.IsNullOrEmpty(name) || IsFailureRecord(name))
            {
                return false;
            }
            if (names.Contains(name))
            {
                return true;
            }
            string[] prefixesSnapshot = prefixes;
            foreach (string value in prefixesSnapshot)
            {
                if (name.StartsWith(value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool IsFailureRecord(string name)
        {
            if (name != null)
            {
                if (!name.StartsWith("ActionFailure_", StringComparison.OrdinalIgnoreCase))
                {
                    return name.StartsWith("ActionAttempt_", StringComparison.OrdinalIgnoreCase);
                }
                return true;
            }
            return false;
        }
    }
}
