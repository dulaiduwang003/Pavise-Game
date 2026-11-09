using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class SystemProcessCatalog
    {
        internal static readonly string[] PresetWhitelist = new string[]
        {
            "system", "secure system", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
            "svchost", "dwm", "fontdrvhost", "audiodg", "wudfhost", "wmiprvse", "explorer", "ctfmon", "textinputhost", "sihost",
            "taskhostw", "conhost", "dllhost", "runtimebroker", "applicationframehost", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "lockapp", "logonui", "securityhealthsystray", "vmmem",
            "vmmemwsl", "wslservice"
        };

        internal static readonly HashSet<string> PurgedPresetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "obs64", "obs32", "streamlabs obs", "xsplit.core", "livehime", "直播伴侣", "zoom", "teams", "ms-teams", "wemeetapp",
            "dingtalk"
        };

        private static readonly HashSet<string> CoreSystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "audiodg", "fontdrvhost" };

        internal static bool IsCoreSystemProcess(string name, string path, string windowsRoot)
        {
            if (string.Equals(WhitelistRule.NormalizeName(name), "smartscreen", StringComparison.OrdinalIgnoreCase))
            {
                // 仅信任 Windows 组件，不信任同名应用或 Windows\Temp 下的文件。
                string root = WhitelistRule.NormalizeImagePath((windowsRoot ?? "").TrimEnd('\\', '/'));
                return root.Length > 0 && WhitelistRule.PathEquals(path, root + "\\System32\\smartscreen.exe");
            }
            if (CoreSystemProcesses.Contains(name) && !string.IsNullOrEmpty(windowsRoot) && !string.IsNullOrEmpty(path))
            {
                return path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        internal static bool IsShellProcess(string name)
        {
            if (!string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "applicationframehost", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "shellexperiencehost", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "startmenuexperiencehost", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(name, "searchhost", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
    }
}
