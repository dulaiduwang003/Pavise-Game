namespace PaviseApp
{
    // Shared CPU-cap Jobs (Aggressive background governance) and background freezing
    // were removed in 3.3.0.7. A process can never leave a Job, and once the owning
    // Pavise and its guardian exit, the Job's name disappears with its last handle and
    // it cannot be reopened, so the stored receipts carry no recovery value. They are
    // dropped at startup.
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
