// @author bdth 2074055628@qq.com
// 文件用途 保存游戏配置并迁移旧版数据

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal enum PerformancePreset
    {
        Standard = 0,
        Competitive = 1,
        Custom = 2,
        Extreme = 3
    }

    internal sealed class GameProfile
    {
        public string Id;
        public string Name;
        public string Root;
        public string ExecutablePath;
        public string LearnedExecutablePath;
        public bool ForceTrigger;
        public readonly HashSet<string> Entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public GameProfile Clone()
        {
            var p = new GameProfile
            {
                Id = Id,
                Name = Name,
                Root = Root,
                ExecutablePath = ExecutablePath,
                LearnedExecutablePath = LearnedExecutablePath,
                ForceTrigger = ForceTrigger
            };
            foreach (string s in Entries) p.Entries.Add(s);
            return p;
        }

        public bool ContainsPath(string path)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(Root)) return false;
            string prefix = Root.TrimEnd('\\') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class GameProfileStore
    {
        internal const string FileName = "Pavise.profiles.dat";
        private const string HeaderPrefix = "PAVISE_PROFILES_";
        private const string HeaderV1 = "PAVISE_PROFILES_V1";
        private const string HeaderV2 = "PAVISE_PROFILES_V2";
        private const string HeaderV3 = "PAVISE_PROFILES_V3";
        private const string HeaderV4 = "PAVISE_PROFILES_V4";
        private readonly string path;

        public GameProfileStore(string dir)
        {
            path = Path.Combine(dir, FileName);
        }

        public bool ClearedLegacyLibrary
        {
            get { return legacyCleared; }
        }

        public List<GameProfile> LoadOrMigrate(string legacyPath)
        {
            bool repaired;
            List<GameProfile> loaded = Normalize(Load(), out repaired);

            if (!legacyCleared && !loadFailed && !File.Exists(path)
                && LegacyGamesFileHasEntries(legacyPath))
            {
                legacyCleared = true;
                TryBackup(legacyPath, legacyPath + ".pre-election.bak");
            }
            if (legacyCleared)
            {
                loaded.Clear();
                Save(loaded);
                Logger.Log("检测到旧版本的游戏库 已备份并自动清空 识别机制已重构为证据选举制 "
                    + "旧档案不再适用 打开游戏进到画面即可自动重建");
                return loaded;
            }

            if (loadFailed || loaded.Count > 0 || File.Exists(path))
            {
                if (!loadFailed && repaired)
                {
                    if (loaded.Count == 0 && File.Exists(path))
                        TryBackup(path, path + ".corrupt.bak");
                    Save(loaded);
                }
                return loaded;
            }

            Save(loaded);
            return loaded;
        }

        private static bool LegacyGamesFileHasEntries(string legacyPath)
        {
            try
            {
                if (string.IsNullOrEmpty(legacyPath) || !File.Exists(legacyPath)) return false;
                foreach (string line in File.ReadAllLines(legacyPath))
                {
                    string name, root;
                    if (GameMode.TryParseGameLine(line, out name, out root)) return true;
                }
            }
            catch { }
            return false;
        }

        private static void TryBackup(string source, string backup)
        {
            try { if (!File.Exists(backup)) File.Copy(source, backup, false); }
            catch { }
        }

        public void Save(IList<GameProfile> profiles)
        {
            if (loadFailed)
            {
                Logger.Log("游戏档案此前读取失败 本次保存已跳过以免覆盖原文件 重启 Pavise 后重试");
                return;
            }
            try
            {
                var lines = new List<string>();
                var learned = new List<string>();
                var forced = new List<string>();
                lines.Add(HeaderV4);
                foreach (GameProfile p in profiles)
                {
                    if (p == null || string.IsNullOrEmpty(p.Id) || string.IsNullOrEmpty(p.Name)) continue;
                    lines.Add("P|" + B64(p.Id) + "|" + B64(p.Name) + "|" + B64(p.Root)
                        + "|" + B64(p.ExecutablePath) + "|" + B64(Join(p.Entries)));
                    if (!string.IsNullOrEmpty(p.LearnedExecutablePath))
                        learned.Add("L|" + B64(p.Id) + "|" + B64(p.LearnedExecutablePath));
                    if (p.ForceTrigger) forced.Add("F|" + B64(p.Id));
                }
                lines.AddRange(learned);
                lines.AddRange(forced);
                AtomicFile.WriteLines(path, lines.ToArray(), "游戏档案");
            }
            catch (Exception ex) { Logger.LogFailure("游戏档案保存失败", ex); }
        }

        private bool loadFailed;
        private bool legacyCleared;

        private List<GameProfile> Load()
        {
            var result = new List<GameProfile>();
            try
            {
                if (!File.Exists(path)) return result;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0) return result;
                if (lines[0] == HeaderV1 || lines[0] == HeaderV2 || lines[0] == HeaderV3)
                {
                    legacyCleared = true;
                    TryBackup(path, path + ".pre-election.bak");
                    return result;
                }
                if (lines[0] != HeaderV4)
                {
                    if (lines[0].StartsWith(HeaderPrefix, StringComparison.Ordinal))
                    {
                        loadFailed = true;
                        Logger.Log("游戏档案由更高版本的 Pavise 写入 " + lines[0]
                            + " 本版本已切换为只读 不会改写该文件");
                    }
                    return result;
                }
                var learnedById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var forcedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] a = lines[i].Split('|');
                    if (a[0] == "L" && a.Length == 3)
                    {
                        string id = Un64(a[1]);
                        string learnedPath = NormalizePath(Un64(a[2]));
                        if (!string.IsNullOrEmpty(id) && learnedPath != null) learnedById[id] = learnedPath;
                        continue;
                    }
                    if (a[0] == "F" && a.Length == 2)
                    {
                        string id = Un64(a[1]);
                        if (!string.IsNullOrEmpty(id)) forcedIds.Add(id);
                        continue;
                    }
                    if (a[0] != "P") continue;
                    if (a.Length != 6 && a.Length != 7) continue;
                    var p = new GameProfile
                    {
                        Id = Un64(a[1]), Name = Un64(a[2]), Root = NormalizeRoot(Un64(a[3])),
                        ExecutablePath = NormalizePath(Un64(a[4])),
                        LearnedExecutablePath = a.Length == 7 ? NormalizePath(Un64(a[6])) : null
                    };
                    AddLines(p.Entries, Un64(a[5]));
                    if (!string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.Name)) result.Add(p);
                }
                foreach (GameProfile p in result)
                {
                    string learnedPath;
                    if (p.LearnedExecutablePath == null && p.Id != null
                        && learnedById.TryGetValue(p.Id, out learnedPath))
                        p.LearnedExecutablePath = learnedPath;
                    if (p.Id != null && forcedIds.Contains(p.Id)) p.ForceTrigger = true;
                }
            }
            catch (Exception ex)
            {
                loadFailed = true;
                Logger.LogFailure("游戏档案读取失败 已保护现有文件不被覆盖", ex);
            }
            return result;
        }

        public static GameProfile NewProfile(string name, string root)
        {
            return NewProfile(name, root, null);
        }

        public static GameProfile NewProfile(string name, string root, string executablePath)
        {
            var p = new GameProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrEmpty(name) ? "Game" : name,
                Root = NormalizeRoot(root),
                ExecutablePath = NormalizePath(executablePath)
            };
            if (!string.IsNullOrEmpty(name)) p.Entries.Add(StripExe(name));
            return p;
        }

        private static List<GameProfile> Normalize(List<GameProfile> source, out bool changed)
        {
            changed = false;
            var result = new List<GameProfile>();
            var byKey = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile raw in source)
            {
                if (raw == null || string.IsNullOrWhiteSpace(raw.Name)) { changed = true; continue; }
                raw.Root = NormalizeRoot(raw.Root);
                raw.ExecutablePath = NormalizePath(raw.ExecutablePath);
                raw.LearnedExecutablePath = NormalizePath(raw.LearnedExecutablePath);
                if (raw.LearnedExecutablePath != null && string.Equals(
                        raw.LearnedExecutablePath, raw.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    raw.LearnedExecutablePath = null;
                    changed = true;
                }
                if (raw.LearnedExecutablePath != null && !string.IsNullOrEmpty(raw.Root))
                {
                    bool rootAlive = false, learnedAlive = false;
                    try
                    {
                        rootAlive = Directory.Exists(raw.Root);
                        learnedAlive = File.Exists(raw.LearnedExecutablePath);
                    }
                    catch { }
                    if (rootAlive && !learnedAlive)
                    {
                        raw.LearnedExecutablePath = null;
                        changed = true;
                    }
                }
                if (string.IsNullOrEmpty(raw.ExecutablePath))
                {
                    string migratedExecutable = FindExistingExecutable(raw.Root, raw.Entries);
                    if (!string.IsNullOrEmpty(migratedExecutable))
                    {
                        raw.ExecutablePath = migratedExecutable;
                        changed = true;
                    }
                }
                if (raw.ForceTrigger && string.IsNullOrEmpty(raw.ExecutablePath)
                    && string.IsNullOrEmpty(raw.LearnedExecutablePath))
                {
                    raw.ForceTrigger = false;
                    changed = true;
                }
                string key = !string.IsNullOrEmpty(raw.ExecutablePath) ? "E|" + raw.ExecutablePath
                    : (!string.IsNullOrEmpty(raw.Root) ? "R|" + raw.Root : "I|" + raw.Id);
                GameProfile keep;
                if (!byKey.TryGetValue(key, out keep))
                {
                    byKey[key] = raw;
                    result.Add(raw);
                    continue;
                }
                changed = true;
                foreach (string entry in raw.Entries) keep.Entries.Add(entry);
                if (string.IsNullOrEmpty(keep.ExecutablePath)) keep.ExecutablePath = raw.ExecutablePath;
                if (string.IsNullOrEmpty(keep.LearnedExecutablePath)) keep.LearnedExecutablePath = raw.LearnedExecutablePath;
                if (raw.ForceTrigger) keep.ForceTrigger = true;
            }
            return result;
        }

        private static string FindExistingExecutable(string root, IEnumerable<string> entries)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            string best = null;
            foreach (string entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                try
                {
                    string[] matches = Directory.GetFiles(root, StripExe(entry) + ".exe", SearchOption.AllDirectories);
                    foreach (string match in matches)
                        if (GameExecutableResolver.IsPortableExecutable(match)
                            && (best == null || match.Length < best.Length)) best = NormalizePath(match);
                }
                catch { }
            }
            return best;
        }

        internal static string NormalizeRoot(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try { return Path.GetFullPath(value.Trim().Trim('"')).TrimEnd('\\'); }
            catch { return null; }
        }

        internal static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try { return Path.GetFullPath(value.Trim().Trim('"')); }
            catch { return null; }
        }

        private static string StripExe(string s)
        {
            string n = (s ?? "").Trim();
            return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;
        }

        private static void AddLines(HashSet<string> set, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (string s in text.Split('\n'))
            {
                string n = StripExe(s.TrimEnd('\r'));
                if (n.Length > 0) set.Add(n);
            }
        }

        private static string Join(IEnumerable<string> values)
        {
            var a = new List<string>(values);
            a.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", a.ToArray());
        }

        private static string B64(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        }

        private static string Un64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s ?? "")); }
            catch { return ""; }
        }
    }
}
