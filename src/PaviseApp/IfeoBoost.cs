using Microsoft.Win32;

namespace PaviseApp
{
    internal static class IfeoBoost
    {

        private static readonly object lk = new object();

        private static string PerfPath(string exe)
        {
            return IfeoStore.RootPath + "\\" + exe + "\\PerfOptions";
        }

        private static ReversibleReg RegOf(string exe)
        {
            return new ReversibleReg(IfeoStore.Hive, PerfPath(exe), "CpuPriorityClass", RegistryValueKind.DWord, "IfeoPri_" + exe);
        }

        private static ReversibleReg IoRegOf(string exe)
        {
            return new ReversibleReg(IfeoStore.Hive, PerfPath(exe), "IoPriority", RegistryValueKind.DWord, "IfeoIo_" + exe);
        }

        private static ReversibleReg PageRegOf(string exe)
        {
            return new ReversibleReg(IfeoStore.Hive, PerfPath(exe), "PagePriority", RegistryValueKind.DWord, "IfeoPg_" + exe);
        }

        public static bool HasResidue()
        {
            if (Settings.LoadStr("IfeoList", "").Length == 0 && Settings.LoadStr("IfeoArm", "").Length == 0)
            {
                return Settings.Load("GmIfeoBoost", false);
            }
            return true;
        }

        public static bool RestoreAll()
        {
            lock (lk)
            {
                bool gmIfeoBoostEnabled = Settings.Save("GmIfeoBoost", false);
                gmIfeoBoostEnabled &= Settings.SaveStr("IfeoArm", "");
                foreach (string entryText in ParseList(Settings.LoadStr("IfeoList", "")))
                {
                    bool restoreSucceeded = RegOf(entryText).Restore();
                    restoreSucceeded &= IoRegOf(entryText).Restore();
                    if (!(restoreSucceeded & PageRegOf(entryText).Restore()))
                    {
                        gmIfeoBoostEnabled = false;
                        continue;
                    }
                    string str = Settings.LoadStr("IfeoMk_" + entryText, "11");
                    bool ourSub = str.Length < 2 || str[1] == '0';
                    bool ourExe = str.Length < 1 || str[0] == '0';
                    IfeoStore.CleanupEmpty(entryText, "PerfOptions", ourSub, ourExe);
                    Settings.SaveStr("IfeoMk_" + entryText, "");
                    IfeoStore.RemoveFromList("IfeoList", entryText);
                    Logger.Log(Lang.T("log.ifeoboost.8", 0) + entryText);
                }
                if (!gmIfeoBoostEnabled)
                {
                    Logger.Log(Lang.T("log.ifeoboost.9", 0));
                }
                return gmIfeoBoostEnabled;
            }
        }

        internal static string[] ParseList(string raw)
        {
            return IfeoStore.ParseList(raw);
        }
    }
}
