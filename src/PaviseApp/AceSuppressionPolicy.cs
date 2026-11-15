namespace PaviseApp
{
    // ACE 压制是逐游戏设置：只在该游戏对局期间按所选强度生效，游戏退出即撤销并恢复。
    // EAC、BattlEye 等其他反作弊只保护、不压制，不再有对应设置。
    internal static class AceSuppressionPolicy
    {
        internal const string Key = "GameAceSuppressionV1";

        internal const string Off = "off";

        internal static readonly string[] Choices = new string[4] { Off, "gentle", "balanced", "isolated" };

        private static readonly object legacySync = new object();

        private static int legacyGeneration = -1;

        private static string legacyValue;

        // 没有单独设置过的游戏沿用旧版全局 ACE 开关与强度；旧版未开启 ACE 时为关闭。
        // 反作弊引擎每轮都会读取，设置未变化时复用上次结果。
        internal static string LegacyDefault()
        {
            int generation = Settings.MutationGeneration;
            lock (legacySync)
            {
                if (legacyValue != null && legacyGeneration == generation)
                {
                    return legacyValue;
                }
            }
            string value = Settings.Load("Tame_ace", false) ? AntiCheatModes.Token(AntiCheatModes.Current) : Off;
            lock (legacySync)
            {
                legacyGeneration = generation;
                legacyValue = value;
            }
            return value;
        }

        internal static AntiCheatMode? Parse(string value)
        {
            switch (value)
            {
            case "gentle":
                return AntiCheatMode.Gentle;
            case "balanced":
                return AntiCheatMode.Balanced;
            case "isolated":
                return AntiCheatMode.Isolated;
            default:
                return null;
            }
        }

        internal static string Label(string value)
        {
            AntiCheatMode? mode = Parse(value);
            if (!mode.HasValue)
            {
                return PerformancePolicy.Text("关闭", "Off");
            }
            return Lang.T("ac.mode." + AntiCheatModes.Token(mode.Value));
        }
    }
}
