using Microsoft.Win32;

namespace PaviseApp
{
    internal static class CfgOffTweak
    {

        private static readonly object lk = new object();

        private static ReversibleReg RegOf(string exe)
        {
            return new ReversibleReg(IfeoStore.Hive, IfeoStore.RootPath + "\\" + exe, "MitigationOptions", RegistryValueKind.Binary, "CfgOpt_" + exe);
        }

        public static bool HasResidue()
        {
            if (Settings.LoadStr("CfgList", "").Length == 0)
            {
                return Settings.Load("CfgOff", false);
            }
            return true;
        }

        public static bool RestoreAll()
        {
            lock (lk)
            {
                bool cfgOffEnabled = Settings.Save("CfgOff", false);
                foreach (string entryText in ParseList(Settings.LoadStr("CfgList", "")))
                {
                    if (!RegOf(entryText).Restore())
                    {
                        cfgOffEnabled = false;
                        continue;
                    }
                    string str = Settings.LoadStr("CfgMk_" + entryText, "1");
                    bool ourExe = str.Length == 0 || str[0] != '1';
                    IfeoStore.CleanupEmpty(entryText, null, false, ourExe);
                    Settings.SaveStr("CfgMk_" + entryText, "");
                    IfeoStore.RemoveFromList("CfgList", entryText);
                    Logger.Log(Lang.T("log.cfgofftweak.6", 0) + entryText);
                }
                if (!cfgOffEnabled)
                {
                    Logger.Log(Lang.T("log.cfgofftweak.8", 0));
                }
                return cfgOffEnabled;
            }
        }

        internal static string[] ParseList(string raw)
        {
            return IfeoStore.ParseList(raw);
        }
    }
}
