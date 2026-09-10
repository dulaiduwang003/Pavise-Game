// @author bdth 2074055628@qq.com
// 文件用途 反作弊压制的全局强度档 三档共用同一批有效成分 递进的只是介入深度
using System;

namespace PaviseApp
{
    // 有效成分是 EcoQoS 小核限频与磁盘 IO 降级 三档一档都不少
    //   递进的是介入深度 温和不动调度优先级 均衡降到低于正常 隔离再加极低 IO 与绑核
    //   见 SuppressionCore.Apply 顶部那段注释 压反作弊不能喂到饿死
    //   扫描型反作弊挂起游戏线程时自己分不到时间片 挂起窗口会从几百毫秒拖到几秒
    internal enum AntiCheatMode
    {
        Gentle = 0,
        Balanced = 1,
        Isolated = 2,
    }

    internal static class AntiCheatModes
    {
        public const string Key = "AcModeV1";
        // 默认保持隔离 它等于本功能一直以来的行为 升级不改变已生效的设置
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

        // 读不出或读到不认识的值都退回默认 不猜用户想要哪一档
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

        // 温和只降 IO 不动优先级 均衡降优先级 隔离再加极低 IO
        //   这三个值恰好是既有的 SuppressionLevel 压制构成代码一行不用改
        public static SuppressionLevel LevelOf(AntiCheatMode mode)
        {
            switch (mode)
            {
                case AntiCheatMode.Gentle: return SuppressionLevel.Eco;
                case AntiCheatMode.Balanced: return SuppressionLevel.Restrained;
                default: return SuppressionLevel.Isolated;
            }
        }

        // 绑核是三项里最容易被反作弊自身保护拒绝的 只在最高档做
        public static bool PinsCores(AntiCheatMode mode) { return mode == AntiCheatMode.Isolated; }

        // 从绑核的档降到不绑核的档时 已有落点必须主动释放
        //   只停止新增是不够的 SqueezeAff 不清零 DesiredAffinity 会一直返回落点 每轮对账又写回来
        public static bool ShouldReleasePins(AntiCheatMode from, AntiCheatMode to)
        {
            return PinsCores(from) && !PinsCores(to);
        }

        public static string NameOf(AntiCheatMode mode) { return Lang.T("ac.mode." + Token(mode)); }
    }
}
