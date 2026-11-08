namespace PaviseApp
{
    // Retired CPU-cap/freeze receipts are dropped at startup.
    // Their named Jobs cannot be reopened after the last owner handle closes.
    internal static class RetiredBackgroundCpuCap
    {
        internal const string ReceiptKey = "BackgroundCpuCapGroupsV2";
        internal const string LegacyReceiptKey = "BackgroundCpuCapJobsV1";

        internal static bool HasReceipts()
        {
            string value;
            return (Settings.TryLoadStr(ReceiptKey, out value) && !string.IsNullOrEmpty(value))
                || (Settings.TryLoadStr(LegacyReceiptKey, out value) && !string.IsNullOrEmpty(value));
        }

        internal static bool Retire()
        {
            if (!HasReceipts()) return true;
            Settings.Remove(ReceiptKey);
            Settings.Remove(LegacyReceiptKey);
            bool clean = !HasReceipts();
            if (clean) Logger.Info("STARTUP event=retired-cpu-cap-receipts result=removed");
            else Logger.Warn("STARTUP event=retired-cpu-cap-receipts result=retained");
            return clean;
        }
    }
}
