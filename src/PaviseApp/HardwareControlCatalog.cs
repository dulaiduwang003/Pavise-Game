using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class HardwareControlCatalog
	{
		private static readonly HashSet<string> ProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"nvcontainer", "NVDisplay.Container", "nvsphelper64", "nvsphelper", "NVIDIA Web Helper", "nvwmi64", "nvsvc64", "nvsvc", "igfxEM", "igfxCUIService",
			"igfxext", "igfxTray", "IntelCpHDCPSvc", "atieclxx", "atiesrxx", "amdow", "AMDRSServ", "AMDRSSrcExt", "RadeonSoftware", "ThrottleStop",
			"XTU3SERVICE", "XtuService", "RyzenMaster", "AMDRyzenMasterDriverV", "ryzenadj", "GHelper", "AsusOptimization", "AsusSystemAnalysis", "AsusSystemDiagnosis", "LenovoVantageService",
			"ImController", "LegionZone", "PredatorSense", "NitroSense", "OmenCommandCenter", "HPOmen", "AWCC", "AlienwareAlienFXController", "DellCommandPower", "MSICenter",
			"MSICenterService", "DragonCenter", "FanControl", "SpeedFan", "HWiNFO64", "HWiNFO32", "ArmouryCrate", "ArmouryCrate.UserSessionHelper", "ArmourySwAgent", "AsusAppService",
			"AsusOSD", "LegionGoQuickSettings", "AYASpace", "AYASpaceII", "OneXConsole", "OneXPlayerManager", "GPDWinControls", "MSI Center M", "ClawCenter", "ArmouryCrateService",
			"ArmouryCrateSE", "ArmouryCrate.Service", "ArmourySocketServer", "LegionSpace", "LegionSpace.Service", "HandheldCompanion", "PowerControl", "SteamController", "PerformanceOverlay", "RTSS",
			"RTSSHooksLoader64", "MSIAfterburner"
		};

		private static readonly string[] Tokens = new string[32]
		{
			"nvcontainer", "nvdisplay", "nvsphelper", "igfxem", "igfxcui", "atieclxx", "atiesrxx", "amdrs", "radeonsoftware", "throttlestop",
			"ryzenmaster", "ryzenadj", "ghelper", "nitrosense", "predatorsense", "dragoncenter", "fancontrol", "speedfan", "hwinfo", "hwmonitor",
			"afterburner", "rivatuner", "rtss", "armoury", "ayaspace", "onexconsole", "onexplayer", "gpdwin", "legionspace", "legionzone",
			"clawcenter", "handheldcompanion"
		};

		internal static bool IsHardwareControlProcess(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			if (ProcessNames.Contains(name))
			{
				return true;
			}
			string toLowerInvariantResult = name.ToLowerInvariant();
			string[] tokens = Tokens;
			foreach (string value in tokens)
			{
				if (toLowerInvariantResult.Contains(value))
				{
					return true;
				}
			}
			return false;
		}

		internal static string[] ProcessNamesForDisplay()
		{
			string[] textEntries = new string[ProcessNames.Count];
			ProcessNames.CopyTo(textEntries);
			Array.Sort(textEntries, StringComparer.OrdinalIgnoreCase);
			return textEntries;
		}

		internal static string[] TokensForDisplay()
		{
			return (string[])Tokens.Clone();
		}
	}
}
