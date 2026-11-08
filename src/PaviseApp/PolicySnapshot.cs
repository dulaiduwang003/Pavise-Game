using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class PolicySnapshot
	{
		private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

		private readonly HashSet<string> overridden = new HashSet<string>(StringComparer.Ordinal);

		public readonly string ProfileId;

		public readonly string ProfileName;

		internal readonly ManualCorePlan ManualCores;

		public int OverrideCount
		{
			get
			{
				return overridden.Count;
			}
		}

		public bool IsGlobal
		{
			get
			{
				return overridden.Count == 0;
			}
		}

		public PerformancePreset Preset
		{
			get
			{
				int result;
				if (!int.TryParse(ValueOf("PerformancePreset"), out result))
				{
					return PerformancePreset.Standard;
				}
				return PresetValue.From(result);
			}
		}

		public bool SuppressBackground
		{
			get
			{
				if (PolicyResolver.GlobalValue("GmSuppress") == "1")
				{
					return On("GmSuppress");
				}
				return false;
			}
		}

		public bool BoostGame
		{
			get
			{
				return On("GmBoost");
			}
		}

		internal int BackgroundGovernanceMode
		{
			get { return ValueOf(PolicyCatalog.KeyBackgroundGovernance) == "1" ? BackgroundGovernancePolicy.Aggressive : BackgroundGovernancePolicy.Normal; }
		}

		internal bool BackgroundBoostLimitRequested
		{
			get { return BackgroundGovernancePolicy.WantsBackgroundBoostLimit(SuppressBackground, BackgroundGovernanceMode); }
		}

		public bool WideBackgroundScope
		{
			get
			{
				if (SuppressBackground)
				{
					return PerformancePolicy.SceneUsesWideBackgroundScope(Preset);
				}
				return false;
			}
		}

		public bool AdaptiveEscalation
		{
			get
			{
				if (SuppressBackground)
				{
					return PerformancePolicy.SceneUsesAdaptiveEscalation(Preset);
				}
				return false;
			}
		}

		public bool ProcessorTuning
		{
			get
			{
				if (PowerPlanOn)
				{
					return PerformancePolicy.SceneTunesProcessor(Preset);
				}
				return false;
			}
		}

		public bool GpuDemote
		{
			get
			{
				return SuppressBackground;
			}
		}

		public bool ThreadBoostOn
		{
			get
			{
				return ThreadBoostSelection.IsOn(ValueOf("GmThreadBoostStrategyV1"));
			}
		}

		public bool RenderLane
		{
			get
			{
				return ThreadBoostOn;
			}
		}

		public bool FrameCoordinationOn
		{
			get
			{
				return ThreadBoostOn;
			}
		}

		public bool PowerPlanOn
		{
			get
			{
				return !PowerPlan.NoSwitchSelected;
			}
		}

		public bool StandbyCleaner
		{
			get
			{
				return On("GmStandbyCleanerV2");
			}
		}

		public bool PauseDownloads
		{
			get
			{
				return SuppressBackground;
			}
		}

		public bool PauseUpdate
		{
			get
			{
				return On("GmPauseUpdate");
			}
		}

		public bool PauseMaintenance
		{
			get
			{
				return On("GmPauseMaintenanceV1");
			}
		}

		public bool PauseServices
		{
			get
			{
				return false;
			}
		}

		public bool Awake
		{
			get
			{
				return true;
			}
		}

		public bool EnglishInput
		{
			get
			{
				return On("GmEnglishInputV1");
			}
		}

		public bool AutoCoresAllowed
		{
			get
			{
				if (ManualCores != null)
				{
					return On("GmAutoCoresV2");
				}
				return false;
			}
		}

		public bool GpuPowerMax
		{
			get
			{
				return false;
			}
		}

		public bool DwmBoost
		{
			get
			{
				return On("GmDwmBoost");
			}
		}

		public bool NvMaxPerf
		{
			get
			{
				return On("NvMaxPerf");
			}
		}

		public string NvLowLatMode
		{
			get
			{
				if (!(ValueOf("NvLowLat") == "on"))
				{
					return "off";
				}
				return "on";
			}
		}

		public bool NvSmoothMotion
		{
			get
			{
				return false;
			}
		}

		public bool NvShaderCacheMax
		{
			get
			{
				return On("NvShaderCache");
			}
		}

		public bool NvRebar
		{
			get
			{
				return On("GameNvRebarV1");
			}
		}

		public string NvDlssMode
		{
			get
			{
				return "off";
			}
		}

		public bool AmdAntiLag
		{
			get
			{
				return false;
			}
		}

		public bool AmdAfmf
		{
			get
			{
				return false;
			}
		}

		public bool IntelEnduranceOff
		{
			get
			{
				return On("GmIntelEnduranceV1");
			}
		}

		public bool CacheWarm
		{
			get
			{
				return On("GmCacheWarmV2");
			}
		}

		public bool GlobalTimerLease
		{
			get
			{
				return On("AutoGlobalTimer");
			}
		}

		public bool EffSuppress
		{
			get
			{
				return SuppressBackground;
			}
		}

		public bool EffBoost
		{
			get
			{
				return BoostGame;
			}
		}

		public bool EffLane
		{
			get
			{
				return RenderLane;
			}
		}

		internal PolicySnapshot(GameProfile profile)
		{
			if (profile == null)
			{
				return;
			}
			ManualCores = ManualCorePlan.Parse(ManualCorePlan.Raw(profile));
			if (ManualCores != null)
			{
				overridden.Add("GmManualCorePlanV2");
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(profile.Overrides, StringComparer.Ordinal);
			if (!ThreadBoostSelection.MigrateOverrides(dictionary))
			{
				dictionary["GmThreadBoostStrategyV1"] = "off";
			}
			foreach (PolicyItem item in PolicyCatalog.All)
			{
				string value;
				if (!(item.Key == "PerformancePreset") && !(item.Key == "PowerPlanOn") && dictionary.TryGetValue(item.Key, out value))
				{
					string canonicalResult = PolicyCatalog.Canonical(item.Key, value);
					values[item.Key] = canonicalResult ?? item.Fallback;
					overridden.Add(item.Key);
				}
			}
			ProfileId = profile.Id;
			ProfileName = profile.Name;
		}

		public bool HasOverride(string key)
		{
			if (key != null)
			{
				return overridden.Contains(key);
			}
			return false;
		}

		public string ValueOf(string key)
		{
			if (key == null)
			{
				return null;
			}
			if (PerformancePolicy.IsRetired(key))
			{
				return "0";
			}
			string resolveResult = PerformancePolicy.Resolve(key, RawValueOf);
			if (resolveResult != null)
			{
				return resolveResult;
			}
			PolicyItem policyItem = PolicyCatalog.ItemOf(key);
			if (policyItem != null)
			{
				return policyItem.Fallback;
			}
			return null;
		}

		internal string RawValueOf(string key)
		{
			if (PerformancePolicy.IsRetired(key))
			{
				return "0";
			}
			if (key == "PowerPlanOn")
			{
				return PolicyResolver.RawGlobalValue(key);
			}
			string value;
			if (!values.TryGetValue(key, out value))
			{
				return PolicyResolver.RawGlobalValue(key);
			}
			return value;
		}

		private bool On(string key)
		{
			return ValueOf(key) == "1";
		}
	}
}
