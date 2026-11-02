using System;

namespace PaviseApp
{
    internal static class AmdSamPreference
    {
        internal const string ReceiptKey = "AmdSamPreferenceV2";
        private const string OnKey = "AmdSamByPavise";
        private static readonly object gate = new object();
        private static readonly GraphicsPreferenceTransaction transaction = new GraphicsPreferenceTransaction(new NativeStore());

        private sealed class NativeStore : IGraphicsPreferenceStore
        {
            public string ReadValue()
            {
                bool supported, enabled;
                if (!AdlxTweaks.SamGet(out supported, out enabled) || !supported) throw new InvalidOperationException("SAM state unavailable");
                return enabled ? "1" : "0";
            }
            public bool WriteValue(string value)
            { return (value == "0" || value == "1") && AdlxTweaks.SamSet(value == "1"); }
            public string LoadReceipt() { return Settings.LoadStr(ReceiptKey, ""); }
            public bool SaveReceipt(string value) { return Settings.SaveStrDurable(ReceiptKey, value); }
        }

        private static bool MigrateLegacy()
        {
            if (!AmdSamTweak.EnabledByPavise || Settings.LoadStr(ReceiptKey, "").Length != 0) return true;
            // The legacy marker was saved only after enabling from an observed Off state.
            string receipt = GraphicsPreferenceTransaction.Receipt("0", "1", 'A');
            return Settings.SaveStrDurable(ReceiptKey, receipt) && Settings.LoadStr(ReceiptKey, "") == receipt;
        }

        internal static bool Set(bool on)
        {
            lock (gate)
            {
                try
                {
                    bool supported, enabled;
                    if (!AdlxTweaks.SamGet(out supported, out enabled) || !supported || !MigrateLegacy()) return false;
                    if (!transaction.Set(on ? "1" : "0")) return false;
                    bool ownedOn = on && Settings.LoadStr(ReceiptKey, "").Length != 0;
                    return Settings.SaveStrDurable(OnKey, ownedOn ? "1" : "0") && Settings.Load(OnKey, !ownedOn) == ownedOn;
                }
                catch { return false; }
            }
        }

        internal static bool Restore()
        {
            lock (gate)
            {
                try
                {
                    if (!MigrateLegacy() || !transaction.Restore()) return false;
                    return Settings.SaveStrDurable(OnKey, "0") && !Settings.Load(OnKey, true);
                }
                catch { return false; }
            }
        }
    }
}
