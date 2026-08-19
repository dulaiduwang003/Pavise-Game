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
        Custom = 2
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
        public readonly Dictionary<string, string> Overrides =
            new Dictionary<string, string>(StringComparer.Ordinal);

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
            foreach (KeyValuePair<string, string> kv in Overrides) p.Overrides[kv.Key] = kv.Value;
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
        private const string HeaderV5 = "PAVISE_PROFILES_V5";
        private readonly string path;

        public GameProfileStore(string dir)
        {
            path = Path.Combine(dir, FileName);
        }

        public List<GameProfile> LoadOrMigrate(string legacyPath)
        {
            bool repaired;
            List<GameProfile> loaded = Normalize(Load(), out repaired);

            if (loadFailed || loaded.Count > 0 || File.Exists(path))
            {
                if (!loadFailed && (repaired || legacyCleared))
                {
                    if (!legacyCleared && loaded.Count == 0 && File.Exists(path))
                        TryBackup(path, path + ".corrupt.bak");
                    Save(loaded);
                }
                return loaded;
            }

            Save(loaded);
            return loaded;
        }

        private static bool IsLegacyHeader(string header)
        {
            return header == HeaderPrefix + "V1" || header == HeaderPrefix + "V2"
                || header == HeaderPrefix + "V3" || header == HeaderPrefix + "V4";
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
                Logger.Log(Lang.T("log.gameprofiles.1"));
                return;
            }
            try
            {
                var lines = new List<string>();
                var learned = new List<string>();
                var forced = new List<string>();
                var overrides = new List<string>();
                lines.Add(HeaderV5);
                foreach (GameProfile p in profiles)
                {
                    if (p == null || string.IsNullOrEmpty(p.Id) || string.IsNullOrEmpty(p.Name)) continue;
                    lines.Add("P|" + B64(p.Id) + "|" + B64(p.Name) + "|" + B64(p.Root)
                        + "|" + B64(p.ExecutablePath) + "|" + B64(Join(p.Entries)));
                    if (!string.IsNullOrEmpty(p.LearnedExecutablePath))
                        learned.Add("L|" + B64(p.Id) + "|" + B64(p.LearnedExecutablePath));
                    if (p.ForceTrigger) forced.Add("F|" + B64(p.Id));
                    foreach (KeyValuePair<string, string> kv in p.Overrides)
                        overrides.Add("O|" + B64(p.Id) + "|" + B64(kv.Key) + "|" + B64(kv.Value));
                }
                lines.AddRange(learned);
                lines.AddRange(forced);
                lines.AddRange(overrides);
                AtomicFile.WriteLines(path, lines.ToArray(), Lang.T("t.gameprofiles.2"));
            }
            catch (Exception ex) { Logger.LogFailure(Lang.T("log.gameprofiles.3"), ex); }
        }

        private bool loadFailed;
        private bool legacyCleared;

        public bool LoadFailed { get { return loadFailed; } }

        private List<GameProfile> Load()
        {
            var result = new List<GameProfile>();
            try
            {
                if (!File.Exists(path)) return result;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0) return result;
                if (lines[0] != HeaderV5)
                {
                    if (IsLegacyHeader(lines[0]))
                    {
                        TryBackup(path, path + ".legacy.bak");
                        legacyCleared = true;
                        Logger.Log(Lang.T("log.gameprofiles.4") + lines[0]
                            + Lang.T("log.gameprofiles.5"));
                        return result;
                    }
                    loadFailed = true;
                    if (lines[0].StartsWith(HeaderPrefix, StringComparison.Ordinal))
                        Logger.Log(Lang.T("log.gameprofiles.6") + lines[0]
                            + Lang.T("log.gameprofiles.7"));
                    else
                    {
                        TryBackup(path, path + ".corrupt.bak");
                        Logger.Log(Lang.T("log.gameprofiles.8"));
                    }
                    return result;
                }
                var learnedById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var forcedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var overridesById = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
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
                    if (a[0] == "O" && a.Length == 4)
                    {
                        string id = Un64(a[1]);
                        string key = Un64(a[2]);
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key)) continue;
                        Dictionary<string, string> bag;
                        if (!overridesById.TryGetValue(id, out bag))
                        {
                            bag = new Dictionary<string, string>(StringComparer.Ordinal);
                            overridesById[id] = bag;
                        }
                        string overrideValue;
                        if (TryUn64(a[3], out overrideValue)) bag[key] = overrideValue;
                        continue;
                    }
                    if (a[0] != "P") continue;
                    if (a.Length != 6) continue;
                    var p = new GameProfile
                    {
                        Id = Un64(a[1]), Name = Un64(a[2]), Root = NormalizeRoot(Un64(a[3])),
                        ExecutablePath = NormalizePath(Un64(a[4]))
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
                    Dictionary<string, string> bag;
                    if (p.Id != null && overridesById.TryGetValue(p.Id, out bag))
                    {
                        foreach (KeyValuePair<string, string> kv in bag) p.Overrides[kv.Key] = kv.Value;
                        PolicyResolver.Sanitize(p);
                    }
                }
            }
            catch (Exception ex)
            {
                loadFailed = true;
                Logger.LogFailure(Lang.T("log.gameprofiles.9"), ex);
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
                foreach (KeyValuePair<string, string> kv in raw.Overrides)
                    if (!keep.Overrides.ContainsKey(kv.Key)) keep.Overrides[kv.Key] = kv.Value;
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

        private static bool TryUn64(string s, out string value)
        {
            try
            {
                value = Encoding.UTF8.GetString(Convert.FromBase64String(s ?? ""));
                return true;
            }
            catch { value = null; return false; }
        }
    }
}
