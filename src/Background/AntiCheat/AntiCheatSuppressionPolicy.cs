namespace PaviseApp
{
    // 反作弊压制是逐游戏开关 默认关 只在该游戏对局期间对它实际用到的 ACE EasyAntiCheat BattlEye 服务生效
    // 退局 游戏退出或关掉开关时撤销本方写入并按回执恢复 Vanguard 只保护 其他反作弊只保护或只记录
    internal static class AntiCheatSuppressionPolicy
    {
        internal const string Key = "GameAntiCheatSuppressionV2";

        internal const string Off = "0";

        internal const string On = "1";

        internal static bool Enabled(GameProfile profile)
        {
            string value;
            return profile != null && profile.Overrides.TryGetValue(Key, out value) && value == On;
        }
    }
}
