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

            // 掌机的整机管理软件 掌机档把后台压得跟专注一样狠 这些要是被压掉
            //   风扇曲线和 TDP 就没人管了 比压掉一个后台下载严重得多
            //   名字按各家常见进程名收的 没有逐台实机核对 发现漏的往这里补
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

            // 掌机侧走子串更稳 各家版本号和后缀花样太多 精确名单赶不上
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
