// @author bdth 2074055628@qq.com
// 文件用途 维护反作弊进程目录和分组配置
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal class AcGroup
    {
        public readonly string Key;
        private readonly string nameKey;
        public readonly bool Default;
        public readonly string[] Procs;
        public string Name { get { return Lang.T(nameKey); } }
        public AcGroup(string key, string name, bool def, string[] procs)
        {
            Key = key; nameKey = name; Default = def; Procs = procs;
        }
    }

    internal static class AntiCheatCatalog
    {
        public static readonly AcGroup[] Groups = new AcGroup[]
        {
            new AcGroup("ace", "ac.ace.n",
                false,
                // 本目录只收用户态进程 内核驱动写在这里永远扫不到 只会让界面显得能压驱动
                //   ACE 的驱动是 ACE-*.sys 装在 System32\drivers 下 本机实测有 ACE-BASE.sys ACE-ADVT.sys
                //   ACE-BASE / ACE-BASE64 属于驱动族 已移除 ACE-Tray 与 ACE-Helper 是真进程 日志里出现过
                new[] { "SGuard64", "SGuardSvc64", "ACE-Tray", "ACE-PC", "ACE-Helper", "SGuard", "SGuardSvc", "AntiCheatExpert", "AntiCheatExpert.Service" }),
            new AcGroup("tp", "ac.tp.n",
                false,
                new[] { "TenSafe", "TenSafe_1", "TenSafe_2", "TASLogin" }),
            new AcGroup("vanguard", "Vanguard (Riot)",
                false,
                new[] { "vgc", "vgtray" }),
            new AcGroup("eac", "EasyAntiCheat (Epic)",
                false,
                new[] { "EasyAntiCheat", "EasyAntiCheat_EOS" }),
            new AcGroup("battleye", "BattlEye",
                false,
                new[] { "BEService", "BEService_x64" }),
            new AcGroup("eaac", "ac.eaac.n",
                false,
                new[] { "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher" }),
            new AcGroup("gameguard", "nProtect GameGuard",
                false,
                new[] { "GameMon", "GameMon.des", "GameMon64", "GameMon64.des", "npggNT", "npggNT.des", "GameGuard" }),
            new AcGroup("faceit", "ac.faceit.n",
                false,
                new[] { "faceitservice", "faceitclient", "faceit" }),
            new AcGroup("neac", "ac.neac.n",
                false,
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

        private static readonly string[] NameTokens =
        {
            "anticheat", "anti-cheat", "sguard", "tensafe", "easyanticheat",
            "beservice", "battleye", "gameguard", "gamemon", "vgtray", "ace-helper", "ace-base"
        };

        internal static bool ContainsToken(string lowered)
        {
            if (string.IsNullOrEmpty(lowered)) return false;
            foreach (string token in NameTokens) if (lowered.Contains(token)) return true;
            return false;
        }

        internal static bool IsAntiCheatLikeName(string name)
        {
            if (IsKnownProcess(name)) return true;
            return ContainsToken((name ?? "").ToLowerInvariant());
        }

        internal static string[] NameTokensForDisplay()
        {
            return (string[])NameTokens.Clone();
        }
    }

}
