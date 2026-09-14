// @author bdth 2074055628@qq.com
// File purpose Identify GPU driver containers and hardware power/thermal control tools so they are exempt from background suppression
using System;
using System.Collections.Generic;

namespace PaviseApp
{

    internal static class HardwareControlCatalog
    {

        private static readonly HashSet<string> ProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {

            "nvcontainer", "NVDisplay.Container", "nvsphelper64", "nvsphelper",
            "NVIDIA Web Helper", "nvwmi64", "nvsvc64", "nvsvc",
            "igfxEM", "igfxCUIService", "igfxext", "igfxTray", "IntelCpHDCPSvc",
            "atieclxx", "atiesrxx", "amdow", "AMDRSServ", "AMDRSSrcExt",
            "RadeonSoftware",

            "ThrottleStop", "XTU3SERVICE", "XtuService",
            "RyzenMaster", "AMDRyzenMasterDriverV", "ryzenadj",

            "GHelper", "AsusOptimization", "AsusSystemAnalysis", "AsusSystemDiagnosis",
            "LenovoVantageService", "ImController", "LegionZone",
            "PredatorSense", "NitroSense", "OmenCommandCenter", "HPOmen",
            "AWCC", "AlienwareAlienFXController", "DellCommandPower",
            "MSICenter", "MSICenterService", "DragonCenter",
            "FanControl", "SpeedFan", "HWiNFO64", "HWiNFO32",

            // Handheld whole-device management software; Handheld tier suppresses background as hard as Esports, and if these get suppressed
            //   nobody manages the fan curve and TDP, far worse than suppressing a background download
            //   Names collected from each vendor's common process names, not verified unit by unit, add missing ones here
            "ArmouryCrate", "ArmouryCrate.UserSessionHelper", "ArmourySwAgent",
            "AsusAppService", "AsusOSD", "LegionGoQuickSettings",
            "AYASpace", "AYASpaceII", "OneXConsole", "OneXPlayerManager",
            "GPDWinControls", "MSI Center M", "ClawCenter",

            "RTSS", "RTSSHooksLoader64", "MSIAfterburner"
        };

        private static readonly string[] Tokens =
        {

            "nvcontainer", "nvdisplay", "nvsphelper",
            "igfxem", "igfxcui", "atieclxx", "atiesrxx", "amdrs", "radeonsoftware",

            "throttlestop", "ryzenmaster", "ryzenadj",
            "ghelper", "nitrosense", "predatorsense", "dragoncenter",
            "fancontrol", "speedfan", "hwinfo", "hwmonitor",
            "afterburner", "rivatuner", "rtss",

            // Substring matching is more robust on the handheld side, too many version numbers and suffix variations across vendors for an exact list to keep up
            "armoury", "ayaspace", "onexconsole", "onexplayer",
            "gpdwin", "legionspace", "legionzone", "clawcenter"
        };

        internal static bool IsHardwareControlProcess(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (ProcessNames.Contains(name)) return true;
            string low = name.ToLowerInvariant();
            foreach (string t in Tokens) if (low.Contains(t)) return true;
            return false;
        }

        internal static string[] ProcessNamesForDisplay()
        {
            var names = new string[ProcessNames.Count];
            ProcessNames.CopyTo(names);
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }

        internal static string[] TokensForDisplay()
        {
            return (string[])Tokens.Clone();
        }
    }
}
