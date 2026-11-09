namespace PaviseApp
{
    internal static class AmdSamTweak
    {
        public static bool EnabledByPavise
        {
            get
            {
                return Settings.Load("AmdSamByPavise", false);
            }
        }

        public static bool HasResidue()
        {
            return EnabledByPavise || Settings.LoadStr(AmdSamPreference.ReceiptKey, "").Length != 0;
        }

        public static bool Supported()
        {
            bool supported;
            bool enabled;
            if (AdlxTweaks.SamGet(out supported, out enabled))
            {
                return supported;
            }
            return false;
        }

        public static bool CurrentlyOn()
        {
            bool supported;
            bool enabled;
            if (AdlxTweaks.SamGet(out supported, out enabled) && supported)
            {
                return enabled;
            }
            return false;
        }

        public static string Describe()
        {
            bool supported;
            bool enabled;
            if (!AdlxTweaks.SamGet(out supported, out enabled) || !supported)
            {
                return Lang.T("t.amdsam.unsupported");
            }
            if (enabled && EnabledByPavise)
            {
                return Lang.T("t.amdsam.byus");
            }
            if (enabled)
            {
                return Lang.T("t.amdsam.external");
            }
            if (EnabledByPavise)
            {
                return Lang.T("t.amdsam.reverted");
            }
            return Lang.T("t.amdsam.off");
        }

        public static bool Enable()
        {
            return AmdSamPreference.Set(true);
        }

        public static bool Disable() { return AmdSamPreference.Set(false); }

        public static bool Restore()
        {
            return AmdSamPreference.Restore();
        }
    }
}
