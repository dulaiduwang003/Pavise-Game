// @author bdth 2074055628@qq.com
// 文件用途 逐 exe 的 IFEO 键共享管理层 名单 键存在性标记 与空键清理的唯一实现
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

        public static string NormalizeExe(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string s = name;
            int slash = s.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) s = s.Substring(slash + 1);
            s = s.Trim();
            if (s.Length == 0 || s.IndexOf(';') >= 0) return null;
            return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : s + ".exe";
        }

        public static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public static bool Listed(string listKey, string exe)
        {
            foreach (string s in ParseList(Settings.LoadStr(listKey, "")))
                if (string.Equals(s, exe, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool AddToList(string listKey, string exe)
        {
            string cur = Settings.LoadStr(listKey, "");
            string next = cur.Length == 0 ? exe : cur + ";" + exe;
            return Settings.SaveStr(listKey, next) && Settings.LoadStr(listKey, "") == next;
        }

        public static void RemoveFromList(string listKey, string exe)
        {
            var keep = new List<string>();
            foreach (string s in ParseList(Settings.LoadStr(listKey, "")))
                if (!string.Equals(s, exe, StringComparison.OrdinalIgnoreCase)) keep.Add(s);
            Settings.SaveStr(listKey, string.Join(";", keep.ToArray()));
        }

        public static bool KeyExists(string exe)
        {
            try
            {
                using (var root = Hive.OpenSubKey(RootPath))
                {
                    if (root == null) return false;
                    using (var k = root.OpenSubKey(exe)) return k != null;
                }
            }
            catch { return false; }
        }

        public static bool SubKeyExists(string exe, string sub)
        {
            try
            {
                using (var root = Hive.OpenSubKey(RootPath))
                {
                    if (root == null) return false;
                    using (var k = root.OpenSubKey(exe))
                    {
                        if (k == null) return false;
                        using (var p = k.OpenSubKey(sub)) return p != null;
                    }
                }
            }
            catch { return false; }
        }

        public static bool RootReachable()
        {
            try
            {
                using (var root = Hive.OpenSubKey(RootPath))
                    return root != null || RootOverride != null;
            }
            catch { return false; }
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
