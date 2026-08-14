// @author bdth 2074055628@qq.com
// 文件用途 Windows 系统进程名单 白名单预设与退役项 极限模式核心进程 桌面壳进程

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class SystemProcessCatalog
    {
        internal static readonly string[] PresetWhitelist =
        {

            "system", "secure system", "registry", "memory compression",
            "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
            "dwm", "fontdrvhost", "audiodg", "wudfhost", "wmiprvse",

            "explorer", "ctfmon", "textinputhost", "sihost", "taskhostw",
            "conhost", "dllhost", "runtimebroker", "applicationframehost",
            "shellexperiencehost", "startmenuexperiencehost", "searchhost",
            "lockapp", "logonui", "securityhealthsystray",

            "vmmem", "vmmemwsl", "wslservice"
        };

        internal static readonly HashSet<string> PurgedPresetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "obs64", "obs32", "streamlabs obs", "xsplit.core", "livehime", "直播伴侣",
            "zoom", "teams", "ms-teams", "wemeetapp", "dingtalk"
        };

        private static readonly HashSet<string> CoreSystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "smss", "csrss", "wininit", "winlogon", "services", "lsass",
            "svchost", "dwm", "audiodg", "fontdrvhost"
        };

        internal static bool IsCoreSystemProcess(string name, string path, string windowsRoot)
        {
            return CoreSystemProcesses.Contains(name)
                && !string.IsNullOrEmpty(windowsRoot) && !string.IsNullOrEmpty(path)
                && path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsShellProcess(string name)
        {
            return string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "applicationframehost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "shellexperiencehost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "startmenuexperiencehost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "searchhost", StringComparison.OrdinalIgnoreCase);
        }
    }
}
