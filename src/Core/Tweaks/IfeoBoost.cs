// @author bdth 2074055628@qq.com
// 文件用途 内核反作弊下的本体提优主路径 经 IFEO PerfOptions 由内核在进程创建时应用
// 内核在 NtCreateUserProcess 阶段读这些值 早于反作弊驱动为该进程注册句柄保护 因此拦不住
// 代价是只对"下次启动"生效 所以必须在游戏启动前就位 见 PreArmAll

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class IfeoBoost
    {
        private const string Root = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        private const string ListKey = "IfeoList";
        private const string ArmKey = "IfeoArm";
        private const int HighPriority = 3;
        private const int HighIoPriority = 3;
        private const int HighPagePriority = 5;

#if PAVISE_SELFTEST
        internal static RegistryKey Hive = Registry.LocalMachine;
        internal static string RootOverride;
#else
        private static readonly RegistryKey Hive = Registry.LocalMachine;
        private static readonly string RootOverride = null;
#endif

        private static readonly object lk = new object();

        private static string RootPath { get { return RootOverride ?? Root; } }

        private static ReversibleReg RegOf(string exe)
        {
            return new ReversibleReg(Hive, RootPath + "\\" + exe + "\\PerfOptions",
                "CpuPriorityClass", RegistryValueKind.DWord, "IfeoPri_" + exe);
        }

        private static ReversibleReg IoRegOf(string exe)
        {
            return new ReversibleReg(Hive, RootPath + "\\" + exe + "\\PerfOptions",
                "IoPriority", RegistryValueKind.DWord, "IfeoIo_" + exe);
        }

        private static ReversibleReg PageRegOf(string exe)
        {
            return new ReversibleReg(Hive, RootPath + "\\" + exe + "\\PerfOptions",
                "PagePriority", RegistryValueKind.DWord, "IfeoPg_" + exe);
        }

        internal static string NormalizeExe(string rendererName)
        {
            if (string.IsNullOrEmpty(rendererName)) return null;
            return rendererName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? rendererName : rendererName + ".exe";
        }

        public static bool Arm(string rendererName)
        {
            string exe = NormalizeExe(rendererName);
            if (string.IsNullOrEmpty(exe) || exe.IndexOf(';') >= 0) return false;
            lock (lk)
            {
                foreach (string s in ParseList(Settings.LoadStr(ArmKey, "")))
                    if (string.Equals(s, exe, StringComparison.OrdinalIgnoreCase)) return false;
                string cur = Settings.LoadStr(ArmKey, "");
                string next = cur.Length == 0 ? exe : cur + ";" + exe;
                return Settings.SaveStr(ArmKey, next);
            }
        }

        public static string[] Armed()
        {
            lock (lk) return ParseList(Settings.LoadStr(ArmKey, ""));
        }

        public static int ClearArmed()
        {
            lock (lk)
            {
                int n = ParseList(Settings.LoadStr(ArmKey, "")).Length;
                Settings.SaveStr(ArmKey, "");
                return n;
            }
        }

        public static int PreArmAll()
        {
            int armed = 0;
            foreach (string exe in Armed())
            {
                if (Listed(exe)) { armed++; continue; }
                if (ApplyFor(exe, true)) armed++;
            }
            return armed;
        }

        public static void EnsureForGame(string rendererName)
        {
            string exe = NormalizeExe(rendererName);
            if (string.IsNullOrEmpty(exe)) return;
            lock (lk)
            {
                if (Listed(exe)) return;
            }
            ApplyFor(exe, false);
        }

        private static bool ApplyFor(string exe, bool preArm)
        {
            lock (lk)
            {
                if (Listed(exe)) return true;
                try
                {
                    bool keyExisted, perfExisted;
                    using (var root = Hive.OpenSubKey(RootPath))
                    {
                        if (root == null && RootOverride == null) return false;
                        using (var k = root == null ? null : root.OpenSubKey(exe))
                        {
                            keyExisted = k != null;
                            perfExisted = k != null && k.OpenSubKey("PerfOptions") != null;
                        }
                    }
                    if (!RegOf(exe).Apply(HighPriority))
                    {
                        Logger.Log("后备提优：IFEO 写入失败（" + exe + "），本轮跳过");
                        return false;
                    }
                    bool ioOk = IoRegOf(exe).Apply(HighIoPriority);
                    bool pgOk = PageRegOf(exe).Apply(HighPagePriority);
                    string marker = (keyExisted ? "1" : "0") + (perfExisted ? "1" : "0");
                    if (!Settings.SaveStr("IfeoMk_" + exe, marker) || !AddToList(exe))
                    {
                        RegOf(exe).Restore();
                        if (ioOk) IoRegOf(exe).Restore();
                        if (pgOk) PageRegOf(exe).Restore();
                        Logger.Log("后备提优：记账无法持久化，已还原 IFEO（" + exe + "）");
                        return false;
                    }
                    string extra = "高优先级" + (ioOk ? " + 高IO" : "") + (pgOk ? " + 高页面优先级" : "");
                    Logger.Log((preArm ? "后备提优已预置：" : "后备提优已登记：") + exe + "（" + extra + "）");
                    return true;
                }
                catch { return false; }
            }
        }

        public static bool RestoreAll()
        {
            lock (lk)
            {
                bool all = true;
                foreach (string exe in ParseList(Settings.LoadStr(ListKey, "")))
                {
                    bool ok = RegOf(exe).Restore();
                    ok &= IoRegOf(exe).Restore();
                    ok &= PageRegOf(exe).Restore();
                    if (!ok) { all = false; continue; }
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
