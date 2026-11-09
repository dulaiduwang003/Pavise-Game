namespace PaviseApp
{
    internal static class AntiCheatModes
    {
        public const string Key = "AcModeV1";

        public const AntiCheatMode Default = AntiCheatMode.Isolated;

        public static AntiCheatMode Current
        {
            get
            {
                return Parse(Settings.LoadStr("AcModeV1", ""));
            }
        }

        public static void Save(AntiCheatMode mode)
        {
            Settings.SaveStr("AcModeV1", Token(mode));
        }

        internal static string Token(AntiCheatMode mode)
        {
            switch (mode)
            {
            case AntiCheatMode.Gentle:
                return "gentle";
            case AntiCheatMode.Balanced:
                return "balanced";
            default:
                return "isolated";
            }
        }

        internal static AntiCheatMode Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return AntiCheatMode.Isolated;
            }
            switch (raw.Trim().ToLowerInvariant())
            {
            case "gentle":
                return AntiCheatMode.Gentle;
            case "balanced":
                return AntiCheatMode.Balanced;
            case "isolated":
                return AntiCheatMode.Isolated;
            default:
                return AntiCheatMode.Isolated;
            }
        }

        public static SuppressionLevel LevelOf(AntiCheatMode mode)
        {
            switch (mode)
            {
            case AntiCheatMode.Gentle:
                return SuppressionLevel.Eco;
            case AntiCheatMode.Balanced:
                return SuppressionLevel.Restrained;
            default:
                return SuppressionLevel.Isolated;
            }
        }

        public static bool PinsCores(AntiCheatMode mode)
        {
            // 旧版挤核所有权保持停用；会话硬亲和性租约不随压制强度变化，并独立负责恢复。

            return false;
        }

        internal static int CoreLimitOf(AntiCheatMode mode)
        {
            return mode == AntiCheatMode.Gentle ? 4 : (mode == AntiCheatMode.Balanced ? 2 : 1);
        }

        public static bool ShouldReleasePins(AntiCheatMode from, AntiCheatMode to)
        {
            if (from == AntiCheatMode.Isolated)
            {
                return !PinsCores(to);
            }
            return false;
        }

        public static string NameOf(AntiCheatMode mode)
        {
            return Lang.T("ac.mode." + Token(mode));
        }
    }
}
