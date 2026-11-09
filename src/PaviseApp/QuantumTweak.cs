namespace PaviseApp
{
    internal static class QuantumTweak
    {
        public const int BoostMask = 3;

        public const int BoostMaximum = 2;

        public static bool RepairedByPavise
        {
            get
            {
                return Settings.Load("PrioritySepRepaired", false);
            }
        }

        public static int TargetFor(int current)
        {
            return (current & -4) | 2;
        }

        public static bool NeedsRepair()
        {
            return false;
        }

        public static string Describe()
        {
            return QuantumAudit.Inspect().Note;
        }

        public static bool Repair()
        {
            Logger.Log(PerformancePolicy.Text("时间片校正已退休，未写入；请在「前台时间片」选择档位", "时间片校正已退役，本次未写入。请改用前台时间片配置。"));
            return false;
        }

        public static bool Restore()
        {
            bool restoreAllSucceeded = QuantumRecovery.RestoreAll();
            if (restoreAllSucceeded && QuantumRecovery.LastWrote)
            {
                Logger.Log(Lang.T("log.quantumtweak.7", 0));
            }
            return restoreAllSucceeded;
        }

        public static bool HasRetiredModeResidue()
        {
            if (Settings.LoadStr("QuantumMode", "").Length <= 0)
            {
                return QuantumRecovery.LegacyRecordsForDisplay;
            }
            return true;
        }

        public static bool PurgeRetiredMode()
        {
            if (!QuantumRecovery.RestoreAll())
            {
                return false;
            }
            Settings.Remove("QuantumMode");
            Settings.Save("PrioritySepRepaired", false);
            return !HasRetiredModeResidue();
        }
    }
}
