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
        private readonly string noteKey;
        public readonly bool Default;
        public readonly string[] Procs;
        public string Name { get { return Lang.T(nameKey); } }
        public string Note { get { return Lang.T(noteKey); } }
        public AcGroup(string key, string name, string note, bool def, string[] procs)
        {
            Key = key; nameKey = name; noteKey = note; Default = def; Procs = procs;
        }
    }

    internal static class AntiCheatCatalog
    {
        public static readonly AcGroup[] Groups = new AcGroup[]
        {
            new AcGroup("ace", "ac.ace.n",
                "t.anticheatcatalog.1", true,
                new[] { "SGuard64", "SGuardSvc64", "ACE-Tray", "ACE-BASE", "ACE-BASE64", "ACE-PC", "ACE-Helper", "SGuard", "SGuardSvc", "AntiCheatExpert", "AntiCheatExpert.Service" }),
            new AcGroup("tp", "ac.tp.n",
                "t.anticheatcatalog.2", false,
                new[] { "TenSafe", "TenSafe_1", "TenSafe_2", "TASLogin" }),
            new AcGroup("vanguard", "Vanguard (Riot)",
                "t.anticheatcatalog.3", false,
                new[] { "vgc", "vgtray" }),
            new AcGroup("eac", "EasyAntiCheat (Epic)",
                "t.anticheatcatalog.4", false,
                new[] { "EasyAntiCheat", "EasyAntiCheat_EOS" }),
            new AcGroup("battleye", "BattlEye",
                "t.anticheatcatalog.5", false,
                new[] { "BEService", "BEService_x64" }),
            new AcGroup("eaac", "ac.eaac.n",
                "t.anticheatcatalog.6", false,
                new[] { "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher" }),
            new AcGroup("gameguard", "nProtect GameGuard",
                "ac.gameguard.d", false,
                new[] { "GameMon", "GameMon.des", "GameMon64", "GameMon64.des", "npggNT", "npggNT.des", "GameGuard" }),
            new AcGroup("faceit", "ac.faceit.n",
                "ac.faceit.d", false,
                new[] { "faceitservice", "faceitclient", "faceit" }),
            new AcGroup("neac", "ac.neac.n",
                "t.anticheatcatalog.7", false,
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
