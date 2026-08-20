// @author bdth 2074055628@qq.com
// 文件用途 逐 exe 的 IFEO 键共享管理层 名单 键存在性标记 与空键清理的唯一实现
//
// 为什么要有这一层
//   IfeoBoost(PerfOptions) 与 CfgOffTweak(MitigationOptions) 写的是同一个
//   IFEO\<exe> 键 由同一轮 Boost 对同一个渲染进程调用 各自维护一套名单
//   标记与空键清理 重复约九成 而且两边的 NormalizeExe 行为并不一致
//   IfeoBoost 不剥路径 CfgOff 剥 同一个进程名在两边可能算出不同的键名
//   于是"这个键是不是我建的"这件事有两套互不知情的账 将来再多一个写入方就会互相误判
//
// 值不撞 所以两边各自的 Apply/Restore 语义保留在各自文件里 这里只收公共账
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

        // 统一取剥路径的那一版 IFEO 的键名必须是纯映像名 带路径写进去不会生效
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

        // 动手前记下这一层原本在不在 只有原本不在的才轮得到我们删
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

        // 由内向外删空键 subKey 传 null 表示只处理 exe 这一层
        // ourSub / ourExe 为真才删 也就是这一层是我们建出来的
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
