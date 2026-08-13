// @author bdth 2074055628@qq.com
// 文件用途 维护反作弊进程目录和分组配置

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal class AcGroup
    {
        public readonly string Key;
        public readonly string Name;
        public readonly string Note;
        public readonly bool Default;
        public readonly string[] Procs;
        public AcGroup(string key, string name, string note, bool def, string[] procs)
        {
            Key = key; Name = name; Note = note; Default = def; Procs = procs;
        }
    }

    internal static class AntiCheatCatalog
    {
        public static readonly AcGroup[] Groups = new AcGroup[]
        {
            new AcGroup("ace", "ACE 反作弊专家 腾讯",
                "英雄联盟国服 无畏契约国服 三角洲行动 CF 等", true,
                new[] { "SGuard64", "SGuardSvc64", "ACE-Tray", "ACE-BASE", "ACE-BASE64", "ACE-PC", "ACE-Helper", "SGuard", "SGuardSvc", "AntiCheatExpert", "AntiCheatExpert.Service" }),
            new AcGroup("tp", "TenProtect TP 腾讯",
                "ACE 前身 穿越火线等老游戏仍在用", false,
                new[] { "TenSafe", "TenSafe_1", "TenSafe_2", "TASLogin" }),
            new AcGroup("vanguard", "Vanguard (Riot)",
                "无畏契约 英雄联盟 vgk 为内核驱动无法压制", false,
                new[] { "vgc", "vgtray" }),
            new AcGroup("eac", "EasyAntiCheat (Epic)",
                "俗称 小蓝熊 Apex 堡垒之夜 永劫无间 幻兽帕鲁 等", false,
                new[] { "EasyAntiCheat", "EasyAntiCheat_EOS" }),
            new AcGroup("battleye", "BattlEye",
                "PUBG 彩虹六号 DayZ 逃离塔科夫 BEDaisy 为驱动", false,
                new[] { "BEService", "BEService_x64" }),
            new AcGroup("eaac", "EA Javelin EA 反作弊",
                "战地 2042 战地 6 FC 等 EA 游戏 另有内核驱动", false,
                new[] { "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher" }),
            new AcGroup("gameguard", "nProtect GameGuard",
                "DNF 等部分韩系网游", false,
                new[] { "GameMon", "GameMon.des", "GameMon64", "GameMon64.des", "npggNT", "npggNT.des", "GameGuard" }),
            new AcGroup("faceit", "FACEIT 反作弊",
                "CS2 第三方竞技平台", false,
                new[] { "faceitservice", "faceitclient", "faceit" }),
            new AcGroup("neac", "NEAC 反作弊 网易",
                "部分网易游戏 另有内核组件", false,
                new[] { "NeacSafe64", "NeacSafe", "nac" }),
        };

        private static readonly HashSet<string> ProcessNames = BuildProcessNames();

        public static bool IsKnownProcess(string name) { return ProcessNames.Contains(name); }

        private static HashSet<string> BuildProcessNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AcGroup g in Groups) foreach (string p in g.Procs) names.Add(p);
            return names;
        }

    }

}
