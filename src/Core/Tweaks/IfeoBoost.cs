// @author bdth 2074055628@qq.com
// 文件用途 受保护游戏的本体提优路径 经 IFEO PerfOptions 由内核在进程创建时应用
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class IfeoBoost
    {
        private const string Sub = "PerfOptions";
        private const string ListKey = "IfeoList";
        private const string ArmKey = "IfeoArm";
        private const int HighPriority = 6;
        private const int HighIoPriority = 3;
        private const int HighPagePriority = 5;

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

        private static string PerfPath(string exe)
        {
            return IfeoStore.RootPath + "\\" + exe + "\\" + Sub;
        }

        private static ReversibleReg RegOf(string exe)
        {
            return new ReversibleReg(IfeoStore.Hive, PerfPath(exe),
                "CpuPriorityClass", RegistryValueKind.DWord, "IfeoPri_" + exe);
        }

        private static ReversibleReg IoRegOf(string exe)
        {
            return new ReversibleReg(IfeoStore.Hive, PerfPath(exe),
                "IoPriority", RegistryValueKind.DWord, "IfeoIo_" + exe);
        }

        private static ReversibleReg PageRegOf(string exe)
        {
            return new ReversibleReg(IfeoStore.Hive, PerfPath(exe),
                "PagePriority", RegistryValueKind.DWord, "IfeoPg_" + exe);
        }

        internal static string NormalizeExe(string rendererName)
        {
            return IfeoStore.NormalizeExe(rendererName);
        }

        public static bool Arm(string rendererName)
        {
            string exe = NormalizeExe(rendererName);
            if (string.IsNullOrEmpty(exe)) return false;
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
                    if (!IfeoStore.RootReachable()) return false;
                    bool keyExisted = IfeoStore.KeyExists(exe);
                    bool perfExisted = IfeoStore.SubKeyExists(exe, Sub);

                    if (RegOf(exe).Matches(HighPriority) && !RegOf(exe).HasBackup
                        && IoRegOf(exe).Matches(HighIoPriority) && !IoRegOf(exe).HasBackup
                        && PageRegOf(exe).Matches(HighPagePriority) && !PageRegOf(exe).HasBackup)
                        return true;

                    if (!RegOf(exe).Apply(HighPriority))
                    {
                        Logger.Log(Lang.T("log.ifeoboost.1") + exe + Lang.T("log.ifeoboost.2"));
                        return false;
                    }
                    bool ioOk = IoRegOf(exe).Apply(HighIoPriority);
                    bool pgOk = PageRegOf(exe).Apply(HighPagePriority);
                    string marker = (keyExisted ? "1" : "0") + (perfExisted ? "1" : "0");
                    if (!Settings.SaveStr("IfeoMk_" + exe, marker) || !IfeoStore.AddToList(ListKey, exe))
                    {
                        RegOf(exe).Restore();
                        if (ioOk) IoRegOf(exe).Restore();
                        if (pgOk) PageRegOf(exe).Restore();
                        Logger.Log(Lang.T("log.ifeoboost.3") + exe + " ");
                        return false;
                    }
                    string extra = Lang.T("log.gamemodeboost.28") + (ioOk ? Lang.T("t.ifeoboost.4") : "") + (pgOk ? Lang.T("t.ifeoboost.5") : "");
                    Logger.Log((preArm ? Lang.T("log.ifeoboost.6") : Lang.T("log.ifeoboost.7")) + exe + " " + extra + " ");
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
                    string marker = Settings.LoadStr("IfeoMk_" + exe, "11");
                    bool ourSub = marker.Length < 2 || marker[1] == '0';
                    bool ourExe = marker.Length < 1 || marker[0] == '0';
                    IfeoStore.CleanupEmpty(exe, Sub, ourSub, ourExe);
                    Settings.SaveStr("IfeoMk_" + exe, "");
                    IfeoStore.RemoveFromList(ListKey, exe);
                    Logger.Log(Lang.T("log.ifeoboost.8") + exe);
                }
                if (!all) Logger.Log(Lang.T("log.ifeoboost.9"));
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
