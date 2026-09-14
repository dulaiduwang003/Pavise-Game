// @author bdth 2074055628@qq.com
// File purpose Enables AMD Smart Access Memory, effective after reboot; turning the switch off reverts per record
using System;

namespace PaviseApp
{
    // SAM is AMD's Resizable BAR; NVIDIA writes per-game driver config, AMD is a global switch that needs a reboot
    //   Offered only when ADLX reports support; not ledgered if the user already had it on; turning the switch off only disables what Pavise enabled
    internal static class AmdSamTweak
    {
        private const string OnKey = "AmdSamByPavise";
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(OnKey, false); } }
        public static bool HasResidue() { return EnabledByPavise; }

        public static bool Supported()
        {
            bool supported, enabled;
            return AdlxTweaks.SamGet(out supported, out enabled) && supported;
        }

        public static bool CurrentlyOn()
        {
            bool supported, enabled;
            return AdlxTweaks.SamGet(out supported, out enabled) && supported && enabled;
        }

        public static string Describe()
        {
            bool supported, enabled;
            if (!AdlxTweaks.SamGet(out supported, out enabled) || !supported) return Lang.T("t.amdsam.unsupported");
            if (enabled && EnabledByPavise) return Lang.T("t.amdsam.byus");
            if (enabled) return Lang.T("t.amdsam.external");
            if (EnabledByPavise) return Lang.T("t.amdsam.reverted");
            return Lang.T("t.amdsam.off");
        }

        public static bool Enable()
        {
            lock (lk)
            {
                bool supported, enabled;
                if (!AdlxTweaks.SamGet(out supported, out enabled) || !supported)
                {
                    Logger.Log(Lang.T("log.amdsam.1"));
                    return false;
                }
                if (enabled) { Logger.Log(Lang.T("log.amdsam.2")); return true; }
                if (!AdlxTweaks.SamSet(true)) { Logger.Log(Lang.T("log.amdsam.3")); return false; }
                Settings.Save(OnKey, true);
                if (!Settings.Load(OnKey, false)) { AdlxTweaks.SamSet(false); return false; }
                Logger.Log(Lang.T("log.amdsam.4"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (!EnabledByPavise) return true;
                bool supported, enabled;
                if (AdlxTweaks.SamGet(out supported, out enabled) && supported && enabled
                    && !AdlxTweaks.SamSet(false))
                {
                    Logger.Log(Lang.T("log.amdsam.5"));
                    return false;
                }
                Settings.Save(OnKey, false);
                Logger.Log(Lang.T("log.amdsam.6"));
                return true;
            }
        }
    }
}
