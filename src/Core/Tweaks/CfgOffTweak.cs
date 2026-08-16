// @author bdth 2074055628@qq.com
// 文件用途 为游戏本体按 exe 关闭控制流保护 CFG 经 IFEO MitigationOptions 由内核在进程创建时应用
// 只清 CFG 那一位 读改写保留同一 QWORD 里用户已有的其它缓解设置 全程可还原并带所有权守卫

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class CfgOffTweak
    {
        private const string Root = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        private const string ValName = "MitigationOptions";
        private const string ListKey = "CfgList";
        private const string EnableKey = "CfgOff";

        // IFEO MitigationOptions 是小端 QWORD 位域 CFG 占 nibble 10 即 byte5 低半字节
        // 掩码 0x3<<40 强制关 ALWAYS_OFF 0x2<<40 与 Windows Exploit Protection 写入的同一格式
        private const int CfgByteIndex = 5;
        private const byte CfgMask = 0x03;
        private const byte CfgAlwaysOff = 0x02;

#if PAVISE_SELFTEST
        internal static RegistryKey Hive = Registry.LocalMachine;
        internal static string RootOverride;
#else
        private static readonly RegistryKey Hive = Registry.LocalMachine;
        private static readonly string RootOverride = null;
#endif

        private static readonly object lk = new object();

        private static string RootPath { get { return RootOverride ?? Root; } }

        public static bool Enabled
        {
            get { return Settings.Load(EnableKey, false); }
        }

        public static void Enable()
        {
            Settings.Save(EnableKey, true);
        }

        // 关开关即把已写入的所有游戏本体逐个还原 无论开关记录如何都尽力清残留
        public static bool Disable()
        {
            Settings.Save(EnableKey, false);
            return RestoreAll();
        }

        internal static string NormalizeExe(string rendererName)
        {
            if (string.IsNullOrEmpty(rendererName)) return null;
            string s = rendererName;
            int slash = s.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) s = s.Substring(slash + 1);
            s = s.Trim();
            if (s.Length == 0 || s.IndexOf(';') >= 0) return null;
            return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : s + ".exe";
        }

        private static ReversibleReg RegOf(string exe)
        {
            return new ReversibleReg(Hive, RootPath + "\\" + exe,
                ValName, RegistryValueKind.Binary, "CfgOpt_" + exe);
        }

        // 读当前值 只把 CFG 那一位改成强制关 其余位原样保留
        private static byte[] MergeCfgOff(byte[] current)
        {
            byte[] m;
            if (current != null && current.Length >= 8) m = (byte[])current.Clone();
            else
            {
                m = new byte[8];
                if (current != null) Array.Copy(current, m, Math.Min(current.Length, 8));
            }
            m[CfgByteIndex] = (byte)((m[CfgByteIndex] & ~CfgMask) | CfgAlwaysOff);
            return m;
        }

        private static byte[] CurrentOptions(string exe)
        {
            try
            {
                using (var k = Hive.OpenSubKey(RootPath + "\\" + exe))
                    return k == null ? null : k.GetValue(ValName) as byte[];
            }
            catch { return null; }
        }

        // 开关开启后 检测到游戏本体即写入 已写过的直接跳过
        public static void EnsureForGame(string rendererName)
        {
            if (!Enabled) return;
            string exe = NormalizeExe(rendererName);
            if (string.IsNullOrEmpty(exe)) return;
            lock (lk)
            {
                if (Listed(exe)) return;
                ApplyFor(exe);
            }
        }

        private static bool ApplyFor(string exe)
        {
            try
            {
                bool keyExisted;
                using (var root = Hive.OpenSubKey(RootPath))
                {
                    if (root == null && RootOverride == null) return false;
                    using (var k = root == null ? null : root.OpenSubKey(exe))
                        keyExisted = k != null;
                }

                byte[] merged = MergeCfgOff(CurrentOptions(exe));
                if (!RegOf(exe).Apply(merged))
                {
                    Logger.Log(Lang.T("log.cfgofftweak.1") + exe + Lang.T("log.cfgofftweak.2"));
                    return false;
                }
                if (!Settings.SaveStr("CfgMk_" + exe, keyExisted ? "1" : "0") || !AddToList(exe))
                {
                    RegOf(exe).Restore();
                    Logger.Log(Lang.T("log.cfgofftweak.3") + exe);
                    return false;
                }
                Logger.Log(Lang.T("log.cfgofftweak.4") + exe + Lang.T("log.cfgofftweak.5"));
                return true;
            }
            catch { return false; }
        }

        public static bool HasResidue()
        {
            lock (lk) return ParseList(Settings.LoadStr(ListKey, "")).Length > 0;
        }

        public static bool RestoreAll()
        {
            lock (lk)
            {
                bool all = true;
                foreach (string exe in ParseList(Settings.LoadStr(ListKey, "")))
                {
                    if (!RegOf(exe).Restore()) { all = false; continue; }
                    CleanupEmpty(exe, Settings.LoadStr("CfgMk_" + exe, "1"));
                    Settings.SaveStr("CfgMk_" + exe, "");
                    RemoveFromList(exe);
                    Logger.Log(Lang.T("log.cfgofftweak.6") + exe);
                }
                if (!all) Logger.Log(Lang.T("log.cfgofftweak.8"));
                return all;
            }
        }

        // exe 键是我们为写缓解位新建的 且现已空 则连键一起清掉 不留 IFEO 痕迹
        private static void CleanupEmpty(string exe, string marker)
        {
            if (marker.Length > 0 && marker[0] == '1') return;
            try
            {
                using (var root = Hive.OpenSubKey(RootPath, true))
                {
                    if (root == null) return;
                    using (var k = root.OpenSubKey(exe))
                        if (k == null || k.ValueCount != 0 || k.SubKeyCount != 0) return;
                    root.DeleteSubKey(exe, false);
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
            var keep = new List<string>();
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
