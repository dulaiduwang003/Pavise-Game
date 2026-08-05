// @author bdth 2074055628@qq.com
// 文件用途 Steam 客户端家族的内置豁免 名字加注册表安装路径双重校验

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class SteamCatalog
    {
        private static readonly HashSet<string> SteamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamservice", "steamwebhelper", "gameoverlayui"
        };

        private static readonly object sync = new object();
        private static List<string> resolvedRoots;
        private static bool exemptionLogged;

        public static bool IsSteamFamily(string name, string path)
        {
            if (!IsSteamName(name)) return false;
            if (!UnderAnyRoot(path, Roots())) return false;
            if (!exemptionLogged)
            {
                exemptionLogged = true;
                Logger.Log("Steam 内置豁免生效：steam / steamservice / steamwebhelper / gameoverlayui 不进入后台压制"
                    + "（Valve 官方警告：压制 Steam 客户端会引发优先级反转掉帧，steamservice 还承载 VAC）");
            }
            return true;
        }

        internal static bool IsSteamName(string name)
        {
            return !string.IsNullOrEmpty(name) && SteamNames.Contains(name.Trim());
        }

        internal static bool IsSteamFamilyWithRoots(string name, string path, IList<string> roots)
        {
            return IsSteamName(name) && UnderAnyRoot(path, roots);
        }

        private static bool UnderAnyRoot(string path, IList<string> roots)
        {
            if (string.IsNullOrEmpty(path) || roots == null) return false;
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string prefix = root.TrimEnd('\\') + "\\";
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static List<string> Roots()
        {
            lock (sync)
            {
                if (resolvedRoots != null) return resolvedRoots;
                var found = new List<string>();
                AddRegistryRoot(found, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
                AddRegistryRoot(found, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
                AddRegistryRoot(found, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
                AddCommonFilesRoot(found, Environment.SpecialFolder.CommonProgramFilesX86);
                AddCommonFilesRoot(found, Environment.SpecialFolder.CommonProgramFiles);
                resolvedRoots = found;
                return resolvedRoots;
            }
        }

        private static void AddRegistryRoot(List<string> into, RegistryKey hive, string subKey, string valueName)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(subKey))
                {
                    if (key == null) return;
                    string raw = key.GetValue(valueName) as string;
                    if (string.IsNullOrEmpty(raw)) return;
                    string normalized = Path.GetFullPath(raw.Trim().Trim('"').Replace('/', '\\')).TrimEnd('\\');
                    if (normalized.Length <= 3) return;
                    foreach (string existing in into)
                        if (string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)) return;
                    into.Add(normalized);
                }
            }
            catch { }
        }

        private static void AddCommonFilesRoot(List<string> into, Environment.SpecialFolder folder)
        {
            try
            {
                string common = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(common)) return;
                string candidate = Path.Combine(common, "Steam");
                if (!Directory.Exists(candidate)) return;
                string normalized = Path.GetFullPath(candidate).TrimEnd('\\');
                foreach (string existing in into)
                    if (string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)) return;
                into.Add(normalized);
            }
            catch { }
        }
    }
}
