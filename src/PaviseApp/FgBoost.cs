namespace PaviseApp
{
    internal static class FgBoost
    {
        public static bool Restore()
        {
            return QuantumRecovery.RestoreAll();
        }

        public static bool HasResidue()
        {
            return Settings.LoadStr("PrevWin32PriSep", "").Length > 0;
        }
    }
}
