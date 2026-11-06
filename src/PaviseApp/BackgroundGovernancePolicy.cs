using System.Globalization;

namespace PaviseApp
{
    internal static class BackgroundGovernancePolicy
    {
        internal const string SettingKey = "BackgroundGovernanceMode";
        internal const int Normal = 0;
        internal const int Aggressive = 1;

        internal static int Normalize(int mode) { return mode == Aggressive ? Aggressive : Normal; }

        internal static int Selection(bool masterEnabled, int mode)
        { return masterEnabled ? Normalize(mode) + 1 : 0; }

        internal static int Load()
        { return Parse(Settings.LoadStr(SettingKey, "0")); }

        internal static int Parse(string value)
        {
            int mode;
            return int.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out mode) ? Normalize(mode) : Normal;
        }

        internal static bool Save(int mode)
        {
            if (mode != Normal && mode != Aggressive) return false;
            string value = mode.ToString(CultureInfo.InvariantCulture);
            return Settings.SaveStrDurable(SettingKey, value) && Settings.LoadStr(SettingKey, "") == value;
        }

        // Aggressive disables temporary boosts for ordinary background threads;
        // anti-cheat boost policy is independent.
        internal static bool WantsBackgroundBoostLimit(bool masterEnabled, int mode)
        { return masterEnabled && mode == Aggressive; }
    }
}
