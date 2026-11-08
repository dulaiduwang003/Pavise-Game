using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class RecoveryRecordKeys
	{
		private static readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"PerformanceActionReceiptsV1", "NvDrsList", "AmdSnap", "AppGpuPreferencesV1", "GpuPrefStage", "GpuPowerSnap", "GpuClockLockReceipt", "NvVrrModeSnap", "IntelEnduranceSnap", "LaptopPerfSnap",
			"PowerOverlaySnap", "PrevPowerPlan", "ManagedPlanPolicyV3", "ManagedPresetCreationV1", "ExtremePowerKnobSnap", "CoreParkSnap", "CpuIdleStateV1", "PrevReservedCpuSets", "PrevTimerTick", "PrevRefreshHz", "DisplaySoloSnap",
			"MemShieldSnap", "PrevMMAgent", "PrevDoBgBw", "PrevDoSvcStopped", "PrevUpdatePaused", "PrevMaintDisabled", "PrevSvcPaused", "PrevOptionalServicesPausedV1", "PrevPresenceQos", "RssSteerReceipt",
			"LinkMetricReceipt", "IrqPrio", RetiredBackgroundCpuCap.LegacyReceiptKey, RetiredBackgroundCpuCap.ReceiptKey, "GameAffinityJobReceiptsV1", "AutoCoreThreadPlacementV1", "FrameCoordinationJournalV1", "FrameMemoryProtectionJournalV1", "Crash_ThrottleMasks", "Crash_BoostMask", "Crash_BoostNames", "Crash_BoostEntriesV2", "Crash_BoostPriorityOwnershipV1",
			"FthExclusionBak", "FthCustomBak", "CfgList", "IfeoList", "MsiList", "DpiExeList", "FsoExeList", LaboratoryPower.ReceiptKey, LaboratoryGpu.PowerReceipt, LaboratoryGpu.ClockReceipt, AntiCheatAffinityLease.ReceiptKey,
			LaboratoryFileFilter.ReceiptKey, LaboratoryClockRepair.ReceiptKey, LaboratoryAudioEffects.ReceiptKey,
			"RtssInstallationOwnerV1",
            "DirectXOwned_SwapEffectUpgradeEnable", "DirectXOwned_VRROptimizeEnable", AmdSamPreference.ReceiptKey
		};

		private static readonly string[] prefixes = new string[16]
		{
			"PerformanceReceipt_", "NvDrs_", "CfgMk_", "IfeoMk_", "MsiMk_", "Prev", "Mmcss_", "IfeoCpu_", "IfeoPri_", "IfeoIo_",
			"IfeoPg_", "Msi_", "IrqReloc_", "IrqAuto_", "GpuIrqAuto_", "NicAff_"
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
