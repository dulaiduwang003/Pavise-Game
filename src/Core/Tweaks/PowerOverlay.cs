// @author bdth 2074055628@qq.com
// File purpose In Esports tier switch the power slider to Best performance, original value read from the registry, restored on exit
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class PowerOverlay
    {
        private const string SchemeKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        private const string AcValue = "ActiveOverlayAcPowerScheme";
        private const string SnapKey = "PowerOverlaySnap";

        private static readonly Guid Max = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

        private static readonly object lk = new object();
        private static int support;

        public static bool Supported()
        {
            lock (lk)
            {
                if (support != 0) return support > 0;
                bool ok = false;
                try
                {
                    Guid current;
                    ok = TryReadActive(out current);
                }
                catch { }
                support = ok ? 1 : -1;
                if (!ok) Logger.Log(Lang.T("log.poweroverlay.1"));
                return ok;
            }
        }

        internal static bool TryReadActive(out Guid value)
        {
            value = Guid.Empty;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(SchemeKey))
                {
                    if (key == null) return false;
                    string raw = key.GetValue(AcValue) as string;
                    if (string.IsNullOrEmpty(raw)) return false;
                    value = new Guid(raw);
                    return true;
                }
            }
            catch { return false; }
        }

        // Only laptops are worth touching this slider; desktops have no DTT / DPTF so flipping it is mostly a no-op
        //   It signals Intel DTT via GUID_POWER_SAVING_STATUS to relax the CPU power limits
        //   and reduce skin-temperature throttling, one of the few OS-side knobs on a laptop that really affects PL
        //   1.8.0.2 enabled it globally and was later pulled; now only when laptop, on AC and Esports tier all hold at once
        //   Untouched on battery, that would run opposite to the Esports-tier battery branch relaxing power-saving items
        internal static bool ShouldActivate(bool laptop, bool onAc, bool competitive)
        {
            return laptop && onAc && competitive;
        }

        public static bool Activate()
        {
            if (!Supported()) return false;
            lock (lk)
            {
                Guid before;
                if (!TryReadActive(out before)) return false;
                if (before == Max) return true;
                if (Settings.LoadStr(SnapKey, "").Length == 0
                    && !Settings.SaveStr(SnapKey, before.ToString()))
                {
                    Logger.Log(Lang.T("log.poweroverlay.2"));
                    return false;
                }
                uint status;
                try { status = PowerSetActiveOverlayScheme(Max); }
                catch (EntryPointNotFoundException) { support = -1; return false; }
                catch { return false; }
                Guid after;
                if (status != 0 || !TryReadActive(out after) || after != Max)
                {
                    Logger.Warn(Lang.T("log.poweroverlay.3") + status + " ");
                    return false;
                }
                Logger.Log(Lang.T("log.poweroverlay.4"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string saved = Settings.LoadStr(SnapKey, "");
                if (saved.Length == 0) return true;
                Guid original;
                try { original = new Guid(saved); }
                catch { Settings.SaveStr(SnapKey, ""); return true; }
                uint status;
                try { status = PowerSetActiveOverlayScheme(original); }
                catch { return false; }
                Guid after;
                if (status != 0 || !TryReadActive(out after) || after != original)
                {
                    Logger.Log(Lang.T("log.poweroverlay.5"));
                    return false;
                }
                Settings.SaveStr(SnapKey, "");
                Logger.Log(Lang.T("log.poweroverlay.6"));
                return true;
            }
        }

        public static void HealFromCrash()
        {
            if (Settings.LoadStr(SnapKey, "").Length == 0) return;
            if (Restore()) Logger.Log(Lang.T("log.poweroverlay.7"));
        }
    }
}
