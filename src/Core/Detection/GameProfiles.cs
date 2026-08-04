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
        public readonly HashSet<string> Entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public GameProfile Clone()
        {
            var p = new GameProfile
            {
                Id = Id,
                Name = Name,
                Root = Root,
                ExecutablePath = ExecutablePath
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
        private const string HeaderV1 = "PAVISE_PROFILES_V1";
        private const string HeaderV2 = "PAVISE_PROFILES_V2";
        private readonly string path;

        public GameProfileStore(string dir)
        {
            path = Path.Combine(dir, FileName);
        }

        public List<GameProfile> LoadOrMigrate(string legacyPath)
        {
            bool repaired;
            List<GameProfile> loaded = Normalize(Load(), out repaired);
            bool legacyFormat = false;

            if (!loadFailed && File.Exists(path))
            {
                legacyFormat = headerLine == HeaderV1;
                if (headerLine != HeaderV2) repaired = true;
            }
            if (loaded.Count > 0 || File.Exists(path))
            {
                if (repaired)
                {
                    if (legacyFormat)
                    {
                        try
                        {
                            string backup = path + ".v1.bak";
                            if (!File.Exists(backup)) File.Copy(path, backup, false);
                        }
                        catch { }
                    }
                    else if (loaded.Count == 0 && File.Exists(path))
                    {
                        try
                        {
                            string backup = path + ".corrupt.bak";
                            if (!File.Exists(backup)) File.Copy(path, backup, false);
                        }
                        catch { }
                    }
                    Save(loaded);
                }
                return loaded;
            }

            var profiles = new List<GameProfile>();
            var byRoot = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(legacyPath))
                {
                    foreach (string line in File.ReadAllLines(legacyPath))
                    {
                        string name, root;
                        if (!GameMode.TryParseGameLine(line, out name, out root)) continue;
                        GameProfile profile = null;
                        if (root != null) byRoot.TryGetValue(root, out profile);
                        if (profile == null)
                        {
                            profile = NewProfile(name, root);
                            profiles.Add(profile);
                            if (root != null) byRoot[root] = profile;
                        }
                        profile.Entries.Add(name);
                    }
                }
            }
            catch { }
            profiles = Normalize(profiles, out repaired);
            Save(profiles);
            return profiles;
        }

        public void Save(IList<GameProfile> profiles)
        {
            if (loadFailed)
            {
                Logger.Log("游戏档案此前读取失败，本次保存已跳过以免覆盖原文件（重启 Pavise 后重试）");
                return;
            }
            try
            {
                var lines = new List<string>();
                lines.Add(HeaderV2);
                foreach (GameProfile p in profiles)
                {
                    if (p == null || string.IsNullOrEmpty(p.Id) || string.IsNullOrEmpty(p.Name)) continue;
                    lines.Add("P|" + B64(p.Id) + "|" + B64(p.Name) + "|" + B64(p.Root)
                        + "|" + B64(p.ExecutablePath) + "|" + B64(Join(p.Entries)));
                }
                AtomicFile.WriteLines(path, lines.ToArray(), "游戏档案");
            }
            catch (Exception ex) { Logger.LogFailure("游戏档案保存失败", ex); }
        }

        private bool loadFailed;
        private string headerLine;

        private List<GameProfile> Load()
        {
            var result = new List<GameProfile>();
            headerLine = null;
            try
            {
                if (!File.Exists(path)) return result;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length > 0) headerLine = lines[0];
                if (lines.Length == 0 || (lines[0] != HeaderV1 && lines[0] != HeaderV2)) return result;
                bool legacy = lines[0] == HeaderV1;
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] a = lines[i].Split('|');
                    if (a[0] != "P") continue;
                    GameProfile p;
                    if (!legacy && a.Length == 6)
                    {
                        p = new GameProfile
                        {
                            Id = Un64(a[1]), Name = Un64(a[2]), Root = NormalizeRoot(Un64(a[3])),
                            ExecutablePath = NormalizePath(Un64(a[4]))
                        };
                        AddLines(p.Entries, Un64(a[5]));
                    }
                    else if (legacy && a.Length == 10)
                    {
                        p = new GameProfile
                        {
                            Id = Un64(a[1]), Name = Un64(a[2]), Root = NormalizeRoot(Un64(a[3])),
                            ExecutablePath = null
                        };
                        AddLines(p.Entries, Un64(a[8]));
                    }
                    else continue;
                    if (!string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.Name)) result.Add(p);
                }
            }
            catch (Exception ex)
            {
                loadFailed = true;
                Logger.LogFailure("游戏档案读取失败，已保护现有文件不被覆盖", ex);
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
                if (string.IsNullOrEmpty(raw.ExecutablePath))
                {
                    string migratedExecutable = FindExistingExecutable(raw.Root, raw.Entries);
                    if (!string.IsNullOrEmpty(migratedExecutable))
                    {
                        raw.ExecutablePath = migratedExecutable;
                        changed = true;
                    }
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
