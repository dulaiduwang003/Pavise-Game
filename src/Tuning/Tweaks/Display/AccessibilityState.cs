namespace PaviseApp
{
    internal sealed class AccessibilityState
    {
        public bool Read;

        public int FilterKeys;

        public int StickyKeys;

        public int ToggleKeys;

        public int DelayBeforeAcceptanceMs;

        public bool AnyNeedsFix
        {
            get
            {
                if (Read)
                {
                    if (!AccessibilityFlags.NeedsFix(FilterKeys) && !AccessibilityFlags.NeedsFix(StickyKeys))
                    {
                        return AccessibilityFlags.NeedsFix(ToggleKeys);
                    }
                    return true;
                }
                return false;
            }
        }

        public bool FilterKeysSwallowing
        {
            get
            {
                if (Read && AccessibilityFlags.IsOn(FilterKeys))
                {
                    return DelayBeforeAcceptanceMs > 0;
                }
                return false;
            }
        }
    }
}
