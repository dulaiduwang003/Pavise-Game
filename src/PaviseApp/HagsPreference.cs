using System;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class HagsPreference
    {
        private const string Slot = "PrevHwSch";
        private static readonly object gate = new object();
        private static readonly GraphicsPreferenceTransaction transaction = new GraphicsPreferenceTransaction(new NativeStore());

        private sealed class NativeStore : IGraphicsPreferenceStore
        {
            public string ReadValue()
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers"))
                {
                    object value = key == null ? null : key.GetValue("HwSchMode");
                    if (value == null) return null;
                    if (!(value is int) || (int)value < 0 || (int)value > 2) throw new InvalidOperationException("Unknown HAGS configuration");
                    return ((int)value).ToString(CultureInfo.InvariantCulture);
                }
            }
            public bool WriteValue(string value)
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey("SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers"))
                {
                    if (key == null) return false;
                    if (value == null) key.DeleteValue("HwSchMode", false);
                    else if (value == "0" || value == "1" || value == "2") key.SetValue("HwSchMode", int.Parse(value, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                    else return false;
                    return true;
                }
            }
            public string LoadReceipt()
            {
                string receipt = Settings.LoadStr(Slot, "");
                if (receipt.Length == 0 || receipt.StartsWith("G2|", StringComparison.Ordinal) || receipt.StartsWith("G3|", StringComparison.Ordinal)) return receipt;
                string[] parts = receipt.Split('\u001f');
                if (parts.Length != 2) return receipt; // Unverified legacy records stay recoverable by the legacy restorer.
                string original, applied;
                if (!DecodeLegacy(parts[0], out original) || !DecodeLegacy(parts[1], out applied)) return receipt;
                return GraphicsPreferenceTransaction.Receipt(original, applied, 'A');
            }
            public bool SaveReceipt(string value) { return Settings.SaveStrDurable(Slot, value); }
        }

        private static bool DecodeLegacy(string text, out string value)
        {
            value = null;
            if (text == ReversibleReg.Absent) return true;
            if (text.StartsWith("=", StringComparison.Ordinal)) text = text.Substring(1);
            if (text != "0" && text != "1" && text != "2") return false;
            value = text; return true;
        }

        internal static bool Set(bool on)
        {
            lock (gate)
            {
                try
                {
                    bool supported, enabled;
                    if (!HagsTweak.TryQueryState(out supported, out enabled) || !supported) return false;
                    if (!transaction.Set(on ? "2" : "1")) return false;
                    bool ownedOn = on && Settings.LoadStr(Slot, "").Length != 0;
                    return Settings.SaveStrDurable("HagsOnByPavise", ownedOn ? "1" : "0") && HagsTweak.EnabledByPavise == ownedOn;
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
                    if (!transaction.Restore()) return false;
                    return Settings.SaveStrDurable("HagsOnByPavise", "0") && !HagsTweak.EnabledByPavise;
                }
                catch { return false; }
            }
        }

        internal static bool UnverifiedLegacyReceipt
        {
            get
            {
                string value = Settings.LoadStr(Slot, "");
                return value == ReversibleReg.Absent || value == "=0" || value == "=1" || value == "=2" || value == "0" || value == "1" || value == "2";
            }
        }
    }
}
