// @author bdth 2074055628@qq.com
// 文件用途 为游戏本体按 exe 关闭控制流保护 CFG 经 IFEO MitigationOptions 由内核在进程创建时应用
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class CfgOffTweak
    {
        private const string ValName = "MitigationOptions";
        private const string ListKey = "CfgList";
        private const string EnableKey = "CfgOff";

        private const int CfgByteIndex = 5;
        private const byte CfgMask = 0x03;
        private const byte CfgAlwaysOff = 0x02;

#if PAVISE_SELFTEST
        internal static RegistryKey Hive
        {
            get { return IfeoStore.Hive; }
            set { IfeoStore.Hive = value; }
        }
        internal static string RootOverride
        {
            get { return IfeoStore.RootOverride; }
            set { IfeoStore.RootOverride = value; }
        }
#endif

        private static readonly object lk = new object();

        public static bool Enabled
        {
            get { return Settings.Load(EnableKey, false); }
        }

        public static void Enable()
        {
            Settings.Save(EnableKey, true);
        }

        public static bool Disable()
        {
            Settings.Save(EnableKey, false);
            return RestoreAll();
        }

        internal static string NormalizeExe(string rendererName)
        {
            return IfeoStore.NormalizeExe(rendererName);
        }

        private static ReversibleReg RegOf(string exe)
        {
            return new ReversibleReg(IfeoStore.Hive, IfeoStore.RootPath + "\\" + exe,
                ValName, RegistryValueKind.Binary, "CfgOpt_" + exe);
        }

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
                using (var k = IfeoStore.Hive.OpenSubKey(IfeoStore.RootPath + "\\" + exe))
                    return k == null ? null : k.GetValue(ValName) as byte[];
            }
            catch { return null; }
        }

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

        internal static bool NeedsApply(string rendererName)
        {
            if (!Enabled) return false;
            string exe = NormalizeExe(rendererName);
            if (string.IsNullOrEmpty(exe)) return false;
            lock (lk) return !Listed(exe);
        }

        private static bool ApplyFor(string exe)
        {
            try
            {
                if (!IfeoStore.RootReachable()) return false;
                bool keyExisted = IfeoStore.KeyExists(exe);

                byte[] merged = MergeCfgOff(CurrentOptions(exe));
                if (!RegOf(exe).Apply(merged))
                {
                    Logger.Log(Lang.T("log.cfgofftweak.1") + exe + Lang.T("log.cfgofftweak.2"));
                    return false;
                }
                if (!Settings.SaveStr("CfgMk_" + exe, keyExisted ? "1" : "0")
                    || !IfeoStore.AddToList(ListKey, exe))
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
                    string marker = Settings.LoadStr("CfgMk_" + exe, "1");
                    bool ourExe = marker.Length == 0 || marker[0] != '1';
                    IfeoStore.CleanupEmpty(exe, null, false, ourExe);
                    Settings.SaveStr("CfgMk_" + exe, "");
                    IfeoStore.RemoveFromList(ListKey, exe);
                    Logger.Log(Lang.T("log.cfgofftweak.6") + exe);
                }
                if (!all) Logger.Log(Lang.T("log.cfgofftweak.8"));
                return all;
            }
        }

        private static bool Listed(string exe)
        {
            return IfeoStore.Listed(ListKey, exe);
        }

        internal static string[] ParseList(string raw)
        {
            return IfeoStore.ParseList(raw);
        }
    }
}
