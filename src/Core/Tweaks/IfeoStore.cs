// @author bdth 2074055628@qq.com
// 文件用途 已下架 IFEO 功能的历史名单与空键清理 不提供新策略写入
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class IfeoStore
    {
        private const string Root = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

#if PAVISE_SELFTEST
        internal static RegistryKey Hive = Registry.LocalMachine;
        internal static string RootOverride;
#else
        internal static readonly RegistryKey Hive = Registry.LocalMachine;
        internal static readonly string RootOverride = null;
#endif

        public static string RootPath { get { return RootOverride ?? Root; } }

        public static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public static void RemoveFromList(string listKey, string exe)
        {
            var keep = new List<string>();
            foreach (string s in ParseList(Settings.LoadStr(listKey, "")))
                if (!string.Equals(s, exe, StringComparison.OrdinalIgnoreCase)) keep.Add(s);
            Settings.SaveStr(listKey, string.Join(";", keep.ToArray()));
        }

        public static void CleanupEmpty(string exe, string subKey, bool ourSub, bool ourExe)
        {
            try
            {
                using (var root = Hive.OpenSubKey(RootPath, true))
                {
                    if (root == null) return;
                    if (subKey != null && ourSub)
                    {
                        using (var k = root.OpenSubKey(exe, true))
                        {
                            if (k != null)
                                using (var p = k.OpenSubKey(subKey))
                                    if (p != null && p.ValueCount == 0 && p.SubKeyCount == 0)
                                        k.DeleteSubKey(subKey, false);
                        }
                    }
                    if (ourExe)
                    {
                        using (var k = root.OpenSubKey(exe))
                            if (k == null || k.ValueCount != 0 || k.SubKeyCount != 0) return;
                        root.DeleteSubKey(exe, false);
                    }
                }
            }
            catch { }
        }
    }
}
