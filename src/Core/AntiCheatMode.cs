// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal enum AntiCheatMode
    {
        Gentle = 0,
        Balanced = 1,
        Isolated = 2,
    }

    internal static class AntiCheatModes
    {
        public const string Key = "AcModeV1";
        public const AntiCheatMode Default = AntiCheatMode.Isolated;

        public static AntiCheatMode Current
        {
            get { return Parse(Settings.LoadStr(Key, "")); }
        }

        public static void Save(AntiCheatMode mode) { Settings.SaveStr(Key, Token(mode)); }

        internal static string Token(AntiCheatMode mode)
        {
            switch (mode)
            {
                case AntiCheatMode.Gentle: return "gentle";
                case AntiCheatMode.Balanced: return "balanced";
                default: return "isolated";
            }
        }

        internal static AntiCheatMode Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Default;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "gentle": return AntiCheatMode.Gentle;
                case "balanced": return AntiCheatMode.Balanced;
                case "isolated": return AntiCheatMode.Isolated;
                default: return Default;
            }
        }

        public static SuppressionLevel LevelOf(AntiCheatMode mode)
        {
            switch (mode)
            {
                case AntiCheatMode.Gentle: return SuppressionLevel.Eco;
                case AntiCheatMode.Balanced: return SuppressionLevel.Restrained;
                default: return SuppressionLevel.Isolated;
            }
        }

        public static bool PinsCores(AntiCheatMode mode) { return mode == AntiCheatMode.Isolated; }

        // 从亲和性档降级时主动释放已有分配。
        public static bool ShouldReleasePins(AntiCheatMode from, AntiCheatMode to)
        {
            return PinsCores(from) && !PinsCores(to);
        }

        public static string NameOf(AntiCheatMode mode) { return Lang.T("ac.mode." + Token(mode)); }
    }
}
