// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal enum AcCategory
    {
        Suppressible = 0,
        ProtectOnly = 1,
        RecordOnly = 2,
    }

    internal class AcGroup
    {
        public readonly string Key;
        private readonly string nameKey;
        public readonly bool Default;
        public readonly string[] Procs;
        public readonly AcCategory Category;
        public string Name { get { return Lang.T(nameKey); } }
        public bool Suppressible { get { return Category == AcCategory.Suppressible; } }
        public bool RecordOnly { get { return Category == AcCategory.RecordOnly; } }
        public AcGroup(string key, string name, bool def, string[] procs)
            : this(key, name, def, procs, AcCategory.Suppressible)
        {
        }
        public AcGroup(string key, string name, bool def, string[] procs, AcCategory category)
        {
            Key = key; nameKey = name; Default = def; Procs = procs; Category = category;
        }
    }

    internal sealed class AcProtectionGroup
    {
        public readonly string Key;
        public readonly string[] Procs;
        public readonly string[] Prefixes;
        public readonly string[] Directories;
        public string Name { get { return Lang.T("ac." + Key + ".n"); } }

        public AcProtectionGroup(string key, string[] procs, string[] prefixes, string[] directories)
        {
            Key = key; Procs = procs; Prefixes = prefixes; Directories = directories;
        }

        public string[] PatternsForDisplay()
        {
            var patterns = new List<string>(Procs);
            foreach (string prefix in Prefixes) patterns.Add(prefix + "*");
            foreach (string directory in Directories) patterns.Add("\\" + directory + "\\*");
            return patterns.ToArray();
        }
    }

    internal static class AntiCheatCatalog
    {
        public static readonly AcGroup[] Groups = new AcGroup[]
        {
            new AcGroup("ace", "ac.ace.n",
                false,
                new[] { "SGuard64", "SGuardSvc64", "ACE-Tray", "ACE-PC", "ACE-Helper", "ACE-Service64", "SGuard", "SGuardSvc", "AntiCheatExpert", "AntiCheatExpert.Service" }),
            new AcGroup("tp", "ac.tp.n",
                false,
                new[] { "TenSafe", "TenSafe_1", "TenSafe_2", "TASLogin", "TP3Helper", "TPHelper" }, AcCategory.RecordOnly),
            // Vanguard 仅豁免压制，降低其资源可能阻止游戏启动。
            new AcGroup("vanguard", "Vanguard (Riot)",
                false,
                new[] { "vgc", "vgtray" }, AcCategory.ProtectOnly),
            new AcGroup("eac", "EasyAntiCheat (Epic)",
                false,
                new[] { "EasyAntiCheat", "EasyAntiCheat_EOS" }),
            new AcGroup("battleye", "BattlEye",
                false,
                new[] { "BEService", "BEService_x64" }),
            new AcGroup("eaac", "ac.eaac.n",
                false,
                new[] { "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher" }, AcCategory.RecordOnly),
            new AcGroup("gameguard", "nProtect GameGuard",
                false,
                new[] { "GameMon", "GameMon.des", "GameMon64", "GameMon64.des", "npggNT", "npggNT.des", "GameGuard" }, AcCategory.RecordOnly),
            new AcGroup("faceit", "ac.faceit.n",
                false,
                new[] { "faceitservice", "faceitclient", "faceit" }, AcCategory.RecordOnly),
            new AcGroup("neac", "ac.neac.n",
                false,
                new[] { "NeacSafe64", "NeacSafe", "nac", "NeacClient", "OWNeacClient", "NeacProtect" }, AcCategory.RecordOnly),
        };

        public static readonly AcProtectionGroup[] ProtectionOnlyGroups =
        {
            new AcProtectionGroup("punkbuster",
                new[] { "PnkBstrA", "PnkBstrB" },
                new[] { "PnkBstr", "PunkBuster" },
                new[] { "PunkBuster" }),
            new AcProtectionGroup("ngs",
                new[] { "BlackCipher", "BlackCipher.aes", "BlackCipher64", "BlackCipher64.aes",
                    "BlackXchg", "BlackXchg.aes" },
                new[] { "BlackCipher", "BlackCall", "BlackXchg" },
                new[] { "BlackCipher", "Nexon Game Security" }),
            new AcProtectionGroup("wellbia",
                new string[0],
                new[] { "XIGNCODE", "Uncheater", "ucldr_" },
                new[] { "XIGNCODE", "XIGNCODE3", "UNCHEATER", "Wellbia", "Wellbia.com" }),
            new AcProtectionGroup("pwarena",
                new[] { "完美世界竞技平台" },
                new[] { "完美世界竞技平台" },
                new string[0]),
            new AcProtectionGroup("5e",
                new[] { "5EClient" },
                new[] { "5EClient" },
                new[] { "5EClient" }),
            new AcProtectionGroup("b5",
                new[] { "B5AntiCheat", "B5GameService", "B5GameServiceLoader", "B5esportsMain", "B5CSGO" },
                new[] { "B5AntiCheat", "B5GameService" },
                new string[0]),
        };

        private static readonly string[] SharedDirectories =
        {
            "AntiCheatExpert", "TenProtect", "Riot Vanguard", "EasyAntiCheat", "EasyAntiCheat_EOS",
            "BattlEye", "EA\\AC", "GameGuard", "FACEIT AC"
        };

        private static readonly HashSet<string> ProcessNames = BuildProcessNames();

        public static bool IsKnownProcess(string name) { return ProcessNames.Contains(NormalizeName(name)); }

        internal static AcGroup RecordOnlyGroupOf(string name)
        {
            string normalized = NormalizeName(name);
            foreach (AcGroup group in Groups)
                if (group.RecordOnly)
                    foreach (string process in group.Procs)
                        if (string.Equals(process, normalized, StringComparison.OrdinalIgnoreCase)) return group;
            return null;
        }

        private static HashSet<string> BuildProcessNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AcGroup g in Groups) foreach (string p in g.Procs) names.Add(p);
            foreach (AcProtectionGroup g in ProtectionOnlyGroups) foreach (string p in g.Procs) names.Add(p);
            return names;
        }

        // 系统快照只移除 .exe，其他扩展名属于进程名。
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4) : name;
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
            string normalized = NormalizeName(name);
            if (ProcessNames.Contains(normalized) || ContainsToken(normalized.ToLowerInvariant())) return true;
            foreach (AcProtectionGroup group in ProtectionOnlyGroups)
                foreach (string prefix in group.Prefixes)
                    if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static bool IsAntiCheatProcess(string name, string path)
        {
            return IsAntiCheatLikeName(name) || IsAntiCheatPath(path);
        }

        // 按完整专用目录段豁免，不能放行整个厂商目录。
        internal static bool IsAntiCheatPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string normalized = path.Replace('/', '\\');
            if (normalized.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                normalized = "\\\\" + normalized.Substring(8);
            else if (normalized.StartsWith("\\\\?\\", StringComparison.Ordinal))
                normalized = normalized.Substring(4);
            if (normalized.StartsWith("\\\\.\\", StringComparison.Ordinal)) return false;
            bool driveRooted = normalized.Length >= 3
                && (normalized[0] >= 'A' && normalized[0] <= 'Z' || normalized[0] >= 'a' && normalized[0] <= 'z')
                && normalized[1] == ':' && normalized[2] == '\\';
            bool unc = normalized.StartsWith("\\\\", StringComparison.Ordinal);
            if (!driveRooted && !unc || normalized.EndsWith("\\", StringComparison.Ordinal)) return false;
            string[] parts = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            int firstDirectory = driveRooted ? 1 : 2;
            if (parts.Length <= firstDirectory) return false;
            foreach (string part in parts) if (part == "." || part == "..") return false;
            for (int i = firstDirectory; i < parts.Length - 1; i++)
            {
                foreach (string directory in SharedDirectories)
                    if (MatchesDirectory(parts, i, directory)) return true;
                foreach (AcProtectionGroup group in ProtectionOnlyGroups)
                    foreach (string directory in group.Directories)
                        if (MatchesDirectory(parts, i, directory)) return true;
            }
            return false;
        }

        private static bool MatchesDirectory(string[] parts, int index, string directory)
        {
            int slash = directory.IndexOf('\\');
            if (slash < 0) return string.Equals(parts[index], directory, StringComparison.OrdinalIgnoreCase);
            return index + 1 < parts.Length - 1
                && string.Equals(parts[index], directory.Substring(0, slash), StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[index + 1], directory.Substring(slash + 1), StringComparison.OrdinalIgnoreCase);
        }

        internal static string[] DirectoryNamesForDisplay()
        {
            var directories = new List<string>(SharedDirectories);
            foreach (AcProtectionGroup group in ProtectionOnlyGroups) directories.AddRange(group.Directories);
            return directories.ToArray();
        }

        internal static string[] NameTokensForDisplay()
        {
            return (string[])NameTokens.Clone();
        }
    }

}
