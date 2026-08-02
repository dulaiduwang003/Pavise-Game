// @author bdth 2074055628@qq.com
// 文件用途 句柄被反作弊拒绝时的后备提优 经 IFEO PerfOptions 由系统在进程创建时应用高优先级

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class IfeoBoost
    {
        private const string Root = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        private const string ListKey = "IfeoList";
        private const int HighPriority = 3;

        internal static RegistryKey Hive = Registry.LocalMachine;
        internal static string RootOverride;

        private static readonly object lk = new object();

        private static string RootPath { get { return RootOverride ?? Root; } }

        private static ReversibleReg RegOf(string exe)
        {
            return new ReversibleReg(Hive, RootPath + "\\" + exe + "\\PerfOptions",
                "CpuPriorityClass", RegistryValueKind.DWord, "IfeoPri_" + exe);
        }

        public static void EnsureForGame(string rendererName)
        {
            if (string.IsNullOrEmpty(rendererName)) return;
            string exe = rendererName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? rendererName : rendererName + ".exe";
            lock (lk)
            {
                if (Listed(exe)) return;
                try
                {
                    bool keyExisted, perfExisted;
                    using (var root = Hive.OpenSubKey(RootPath))
                    {
                        if (root == null && RootOverride == null) return;
                        using (var k = root == null ? null : root.OpenSubKey(exe))
                        {
                            keyExisted = k != null;
                            perfExisted = k != null && k.OpenSubKey("PerfOptions") != null;
                        }
                    }
                    if (!RegOf(exe).Apply(HighPriority))
                    {
                        Logger.Log("后备提优：IFEO 写入失败（" + exe + "），本轮跳过");
                        return;
                    }
                    string marker = (keyExisted ? "1" : "0") + (perfExisted ? "1" : "0");
                    if (!Settings.SaveStr("IfeoMk_" + exe, marker) || !AddToList(exe))
                    {
                        RegOf(exe).Restore();
                        Logger.Log("后备提优：记账无法持久化，已还原 IFEO（" + exe + "）");
                        return;
                    }
                    Logger.Log("后备提优已登记：" + exe + " 自下次启动起由系统以高优先级创建（IFEO PerfOptions）");
                }
                catch { }
            }
        }

        public static bool RestoreAll()
        {
            lock (lk)
            {
                bool all = true;
                foreach (string exe in ParseList(Settings.LoadStr(ListKey, "")))
                {
                    if (!RegOf(exe).Restore()) { all = false; continue; }
                    CleanupEmpty(exe, Settings.LoadStr("IfeoMk_" + exe, "11"));
                    Settings.SaveStr("IfeoMk_" + exe, "");
                    RemoveFromList(exe);
                    Logger.Log("后备提优已撤销：" + exe);
                }
                if (!all) Logger.Log("后备提优：部分 IFEO 还原失败，快照保留待下次重试");
                return all;
            }
        }

        private static void CleanupEmpty(string exe, string marker)
        {
            try
            {
                using (var root = Hive.OpenSubKey(RootPath, true))
                {
                    if (root == null) return;
                    if (marker.Length < 2 || marker[1] == '0')
                        using (var k = root.OpenSubKey(exe, true))
                        {
                            if (k != null)
                                using (var p = k.OpenSubKey("PerfOptions"))
                                    if (p != null && p.ValueCount == 0 && p.SubKeyCount == 0)
                                        k.DeleteSubKey("PerfOptions", false);
                        }
                    if (marker.Length < 1 || marker[0] == '0')
                        using (var k = root.OpenSubKey(exe))
                        {
                            if (k != null && k.ValueCount == 0 && k.SubKeyCount == 0)
                                root.DeleteSubKey(exe, false);
                        }
                }
            }
            catch { }
        }

        private static bool Listed(string exe)
        {
            foreach (string s in ParseList(Settings.LoadStr(ListKey, "")))
                if (string.Equals(s, exe, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool AddToList(string exe)
        {
            string cur = Settings.LoadStr(ListKey, "");
            string next = cur.Length == 0 ? exe : cur + ";" + exe;
            return Settings.SaveStr(ListKey, next) && Settings.LoadStr(ListKey, "") == next;
        }

        private static void RemoveFromList(string exe)
        {
            var keep = new System.Collections.Generic.List<string>();
            foreach (string s in ParseList(Settings.LoadStr(ListKey, "")))
                if (!string.Equals(s, exe, StringComparison.OrdinalIgnoreCase)) keep.Add(s);
            Settings.SaveStr(ListKey, string.Join(";", keep.ToArray()));
        }

        internal static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
