// @author bdth 2074055628@qq.com
// 文件用途 已下架的后备提优仅保留历史 IFEO 快照恢复 不再预置或施加
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class IfeoBoost
    {
        private const string Sub = "PerfOptions";
        private const string ListKey = "IfeoList";
        private const string ArmKey = "IfeoArm";

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

        // 账本或旧开关任一存在即为残留 恢复入口据此决定要不要动注册表
        public static bool HasResidue()
        {
            return Settings.LoadStr(ListKey, "").Length != 0
                || Settings.LoadStr(ArmKey, "").Length != 0
                || Settings.Load("GmIfeoBoost", false);
        }

        public static bool RestoreAll()
        {
            lock (lk)
            {
                bool all = Settings.Save("GmIfeoBoost", false);
                all &= Settings.SaveStr(ArmKey, "");
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

        internal static string[] ParseList(string raw)
        {
            return IfeoStore.ParseList(raw);
        }
    }
}
