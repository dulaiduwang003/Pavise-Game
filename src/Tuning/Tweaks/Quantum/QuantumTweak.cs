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
            // 前台时间片档位已下线 这里不指向任何设置入口
            Logger.Log("时间片校正已退役，本次未写入。", "Time-slice correction is retired; nothing was written. Choose a Foreground quantum profile instead.");
            return false;
        }

        public static bool Restore()
        {
            bool restoreAllSucceeded = QuantumRecovery.RestoreAll();
            if (restoreAllSucceeded && QuantumRecovery.LastWrote)
            {
                Logger.Log(() => Lang.T("log.quantumtweak.7"));
            }
            return restoreAllSucceeded;
        }
    }
}
