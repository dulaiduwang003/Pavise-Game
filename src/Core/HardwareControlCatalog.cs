// @author bdth 2074055628@qq.com
// 文件用途 识别显卡驱动容器与硬件电源散热控制工具 使其免于后台压制
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

            "RTSS", "RTSSHooksLoader64", "MSIAfterburner"
        };

        private static readonly string[] Tokens =
        {

            "nvcontainer", "nvdisplay", "nvsphelper",
            "igfxem", "igfxcui", "atieclxx", "atiesrxx", "amdrs", "radeonsoftware",

            "throttlestop", "ryzenmaster", "ryzenadj",
            "ghelper", "nitrosense", "predatorsense", "dragoncenter",
            "fancontrol", "speedfan", "hwinfo", "hwmonitor",
            "afterburner", "rivatuner", "rtss"
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
