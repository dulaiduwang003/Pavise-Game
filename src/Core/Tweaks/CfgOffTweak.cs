// @author bdth 2074055628@qq.com
// File purpose Retired CFG-off feature; keeps only legacy policy recovery, no longer disables Control Flow Guard
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class CfgOffTweak
    {
        private const string ValName = "MitigationOptions";
        private const string ListKey = "CfgList";
        private const string EnableKey = "CfgOff";

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

        private static ReversibleReg RegOf(string exe)
        {
            return new ReversibleReg(IfeoStore.Hive, IfeoStore.RootPath + "\\" + exe,
                ValName, RegistryValueKind.Binary, "CfgOpt_" + exe);
        }

        // Either the ledger or the old switch existing counts as residue; the recovery entry decides from this whether to touch the registry
        public static bool HasResidue()
        {
            return Settings.LoadStr(ListKey, "").Length != 0
                || Settings.Load(EnableKey, false);
        }

        public static bool RestoreAll()
        {
            lock (lk)
            {
                bool all = Settings.Save(EnableKey, false);
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

        internal static string[] ParseList(string raw)
        {
            return IfeoStore.ParseList(raw);
        }
    }
}
