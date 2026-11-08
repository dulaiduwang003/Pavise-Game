using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
	internal static class PolicyCatalog
	{
		public const string KeyPreset = "PerformancePreset";

		public const string KeyTimerTick = "AutoTimerTick";

		public const string KeyGlobalTimer = "AutoGlobalTimer";

		public const string KeySuppress = "GmSuppress";

		public const string KeyBackgroundGovernance = "BackgroundGovernanceMode";

		public const string KeySuppressFamily = "GameFamilyBackground";

		public const string KeyGameFullscreenOptimizations = "GameDisableFullscreenOptimizationsV1";

		public const string KeyBoost = "GmBoost";

		public const string KeyBoostCpu = "GmBoostCpuV1";

		public const string KeyBoostMemory = "GmBoostMemoryV1";

		public const string KeyBoostIo = "GmBoostIoV1";

		public const string KeyBoostGpu = "GmBoostGpuV1";

		public const string KeyAutoCores = "GmAutoCoresV2";

		public const string KeyAggressive = "GmAggressive";

		public const string KeyGpuDemote = "GmGpuDemote";

		public const string KeyAdaptiveEscalate = "GmAdaptiveEscalateV1";

		public const string KeyRenderLane = "GmRenderLane";

		public const string KeyThreadBoost = "GmThreadBoostStrategyV1";

		public const string KeyPowerPlan = "PowerPlanOn";

		public const string KeyStandbyCleaner = "GmStandbyCleanerV2";

		public const string KeyCacheWarm = "GmCacheWarmV2";

		public const string KeyPauseDl = "GmPauseDl";

		public const string KeyPauseUpdate = "GmPauseUpdate";

		public const string KeyPauseMaintenance = "GmPauseMaintenanceV1";

		public const string KeyPauseServices = "GmPauseServices";

		public const string KeyAwake = "GmAwake";

		public const string KeyEnglishInput = "GmEnglishInputV1";

		public const string KeyNvMaxPerf = "NvMaxPerf";

		public const string KeyNvLowLat = "NvLowLat";

		public const string KeyNvSmoothMotion = "NvSmoothMotion";

		public const string KeyNvShaderCache = "NvShaderCache";

		public const string KeyNvRebar = "NvRebar";

		public const string KeyGameRebar = "GameNvRebarV1";

		public const string KeyNvDlss = "NvDlss";

		public const string KeyAmdAntiLag = "AmdAntiLag";

		public const string KeyAmdAfmf = "AmdAfmf";

		public const string KeyIntelEndurance = "GmIntelEnduranceV1";

		public const string KeyLaptopPerf = "GmLaptopPerfV1";

		public const string KeyIdlePolicy = "GmIdlePolicyV1";

		public const string KeyGpuPowerMax = "GmGpuPowerMax";

		public const string KeyDwmBoost = "GmDwmBoost";

		public const string KeyFrameCoordination = "FrameCoordination";

		public const string KeyWsTrim = "GmWsTrimV1";

		public const string GroupMode = "cfg.group.mode";

		public const string GroupBackground = "cfg.group.bg";

		public const string GroupMemPower = "cfg.group.mempower";

		public const string GroupEnvironment = "cfg.group.env";

		public const string GroupGraphics = "cfg.group.gpu";

		public static readonly string[] HandheldBlocked = new string[0];

		private static readonly PolicyItem[] Items = new PolicyItem[23]
		{
			new PolicyItem("PerformancePreset", PolicyValueKind.Enum, "0", "cfg.mode", "cfg.group.mode", new string[2] { "0", "1" }),
			new PolicyItem("GmSuppress", PolicyValueKind.Bool, "1", "v14.bg.master", "cfg.group.bg", null),
			new PolicyItem("BackgroundGovernanceMode", PolicyValueKind.Enum, "0", "background.governance.mode", "cfg.group.bg", new string[2] { "0", "1" }),
			new PolicyItem("GmThreadBoostStrategyV1", PolicyValueKind.Choice, "on", "auto.threadboost", "cfg.group.bg", ThreadBoostSelection.Choices),
			new PolicyItem("GmEnglishInputV1", PolicyValueKind.Bool, "0", "gm.englishinput", "cfg.group.env", null),
			new PolicyItem("GameFamilyBackground", PolicyValueKind.Bool, "0", "lib.family.suppress", "cfg.group.bg", null),
			new PolicyItem("GameDisableFullscreenOptimizationsV1", PolicyValueKind.Bool, "0", "auto.fso", "cfg.group.gpu", null),
			new PolicyItem("GmAutoCoresV2", PolicyValueKind.Bool, "0", "schedule.auto.allow", "cfg.group.bg", null),
			new PolicyItem("GameNvRebarV1", PolicyValueKind.Bool, "0", "set.nvrebar", "cfg.group.gpu", null),
			new PolicyItem("GmBoost", PolicyValueKind.Bool, "1", "gm.boost", "cfg.group.bg", null),
			new PolicyItem("PowerPlanOn", PolicyValueKind.Bool, "1", "plan.pick.title", "cfg.group.mempower", null),
			new PolicyItem("GmCacheWarmV2", PolicyValueKind.Bool, "0", "gm.cachewarm", "cfg.group.mempower", null),
			new PolicyItem("GmStandbyCleanerV2", PolicyValueKind.Bool, "0", "gm.standbycleaner", "cfg.group.env", null),
			new PolicyItem("GmDwmBoost", PolicyValueKind.Bool, "0", "extreme.dwm", "cfg.group.env", null),
			new PolicyItem("AutoTimerTick", PolicyValueKind.Bool, "0", "set.timertick", "cfg.group.env", null),
			new PolicyItem("AutoGlobalTimer", PolicyValueKind.Bool, "0", "set.gtimer", "cfg.group.env", null),
			new PolicyItem("GmPauseUpdate", PolicyValueKind.Bool, "0", "gm.pausewu", "cfg.group.env", null),
			new PolicyItem("GmIntelEnduranceV1", PolicyValueKind.Bool, "0", "set.intel.endurance", "cfg.group.gpu", null),
			new PolicyItem("NvLowLat", PolicyValueKind.Choice, "off", "set.nvll", "cfg.group.gpu", new string[2] { "off", "on" }),
			new PolicyItem("AmdAntiLag", PolicyValueKind.Bool, "0", "set.amdalag", "cfg.group.gpu", null),
			new PolicyItem("GmPauseMaintenanceV1", PolicyValueKind.Bool, "1", "gm.pausemaint", "cfg.group.env", null),
			new PolicyItem("NvMaxPerf", PolicyValueKind.Bool, "0", "set.nvmax", "cfg.group.gpu", null),
			new PolicyItem("NvShaderCache", PolicyValueKind.Bool, "0", "auto.nvshader", "cfg.group.gpu", null)
		};

		private static readonly Dictionary<string, PolicyItem> ByKey = BuildIndex();

		public static int Count
		{
			get
			{
				return Items.Length;
			}
		}

		public static IList<PolicyItem> All
		{
			get
			{
				return Items;
			}
		}

		public static bool IsHandheldBlocked(string key)
		{
			return false;
		}

		private static Dictionary<string, PolicyItem> BuildIndex()
		{
			Dictionary<string, PolicyItem> dictionary = new Dictionary<string, PolicyItem>(StringComparer.Ordinal);
			PolicyItem[] items = Items;
			foreach (PolicyItem policyItem in items)
			{
				dictionary[policyItem.Key] = policyItem;
			}
			return dictionary;
		}

		public static PolicyItem ItemOf(string key)
		{
			PolicyItem value;
			if (key == null || !ByKey.TryGetValue(key, out value))
			{
				return null;
			}
			return value;
		}

		public static string Canonical(string key, string value)
		{
			if (key == "GameCriticalThreadHardRuleV1")
			{
				CriticalThreadRule criticalThreadRule = CriticalThreadRule.Parse(value);
				if (criticalThreadRule != null)
				{
					return criticalThreadRule.Encode();
				}
				return null;
			}
			if (key == "GmManualCorePlanV2")
			{
				ManualCorePlan manualCorePlan = ManualCorePlan.Parse(value);
				if (manualCorePlan != null)
				{
					return manualCorePlan.Encode();
				}
				return null;
			}
			PolicyItem policyItem = ItemOf(key);
			if (policyItem == null)
			{
				return null;
			}
			string trimmed = (value ?? "").Trim();
			switch (policyItem.Kind)
			{
			case PolicyValueKind.Bool:
				if (trimmed.Length != 0 && !(trimmed == "0") && !trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
				{
					return "1";
				}
				return "0";
			case PolicyValueKind.Enum:
			{
				int result;
				if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
				{
					return policyItem.Fallback;
				}
				string resultText = result.ToString(CultureInfo.InvariantCulture);
				string[] choices2 = policyItem.Choices;
				foreach (string entryText in choices2)
				{
					if (entryText == resultText)
					{
						return resultText;
					}
				}
				if (!(key == "PerformancePreset"))
				{
					return policyItem.Fallback;
				}
				return ((int)PresetValue.From(result)).ToString(CultureInfo.InvariantCulture);
			}
			case PolicyValueKind.Choice:
			{
				string[] choices = policyItem.Choices;
				foreach (string entryText2 in choices)
				{
					if (string.Equals(entryText2, trimmed, StringComparison.OrdinalIgnoreCase))
					{
						return entryText2;
					}
				}
				return policyItem.Fallback;
			}
			default:
				return null;
			}
		}
	}
}
