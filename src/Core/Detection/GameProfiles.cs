// @author bdth 2074055628@qq.com
// 文件用途 严格保存与读取当前 V5 游戏配置 不迁移不修复不自删数据
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    // 掌机跳过 3 排在 4
    //   3 是下架掉的极限档 老配置里可能还留着 那档比专注更激进 跟掌机的方向正相反
    //   接手这个数字等于把老用户静默切成反方向的档 所以让 3 继续走无效值回落
    //   界面上的顺序单独排 智能 专注 掌机 自定义 跟这里的取值无关
    internal enum PerformancePreset
    {
        Standard = 0,
        Competitive = 1,
        Custom = 2,
        Handheld = 4
    }

    internal static class PresetValue
    {
        // 取值不连续 别再写成范围判断 3 必须继续被拒
        public static bool IsValid(int raw)
        {
            return raw == 0 || raw == 1 || raw == 2 || raw == 4;
        }

        public static PerformancePreset From(int raw)
        {
            return IsValid(raw) ? (PerformancePreset)raw : PerformancePreset.Standard;
        }
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

        // A per-game opt-in, deliberately independent of the old global switch.
        // Absent in an existing V5 library means protected, never inherited from HKCU.
        public bool SuppressFamilyBackground
        {
            get
            {
                string value;
                return Overrides.TryGetValue(PolicyCatalog.KeySuppressFamily, out value) && value == "1";
            }
        }

        public string PreferredExecutablePath
        {
            get
            {
                return string.IsNullOrEmpty(LearnedExecutablePath)
                    ? ExecutablePath : LearnedExecutablePath;
            }
        }

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
        private const string HeaderV5 = "PAVISE_PROFILES_V5";
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly string path;

        public GameProfileStore(string dir)
        {
            path = Path.Combine(dir, FileName);
        }

        public List<GameProfile> LoadProfiles()
        {
            List<GameProfile> loaded = Load();
            if (loadFailed)
            {
                // 统一走与保存失败相同的熔断。Save 看到 loadFailed 只置故障位，
                // 不会改写原文件；真正的精确目录清空与退出只允许 Program 执行。
                Save(loaded);
                return new List<GameProfile>();
            }
            if (!File.Exists(path)) Save(loaded);
            return loaded;
        }

        public bool Save(IList<GameProfile> profiles)
        {
            if (SaveFailed) return false;
            if (loadFailed)
            {
                Logger.Log(Lang.T("log.gameprofiles.1"));
                Interlocked.Exchange(ref saveFailed, 1);
                return false;
            }
            try
            {
                ValidateProfiles(profiles);
                var lines = new List<string>();
                var learned = new List<string>();
                var forced = new List<string>();
                var overrides = new List<string>();
                lines.Add(HeaderV5);
                foreach (GameProfile p in profiles)
                {
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
                CommitStrict(lines);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref saveFailed, 1);
                Logger.LogFailure(Lang.T("log.gameprofiles.3"), ex);
                return false;
            }
        }

        private bool loadFailed;
        private int saveFailed;

        public bool LoadFailed { get { return loadFailed; } }
        public bool SaveFailed { get { return Interlocked.CompareExchange(ref saveFailed, 0, 0) != 0; } }

        // 档案不使用 AtomicFile 的兼容回退：Replace 失败后绝不能备份旧档、
        // 非原子覆盖并谎报成功。任何提交失败都由 Save 置致命故障位。
        private void CommitStrict(IList<string> lines)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, StrictUtf8))
                {
                    foreach (string line in lines) sw.WriteLine(line);
                    sw.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static void ValidateProfiles(IList<GameProfile> profiles)
        {
            if (profiles == null) throw new FormatException("profiles null");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile p in profiles)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Id)
                    || string.IsNullOrWhiteSpace(p.Name))
                    throw new FormatException("profile identity missing");
                if (!ids.Add(p.Id)) throw new FormatException("duplicate profile id");
                string identity = IdentityKey(p.Id, p.Root, p.ExecutablePath);
                if (!identities.Add(identity)) throw new FormatException("duplicate profile identity");
                ValidateRootPath(p.Root);
                ValidateExecutablePath(p.ExecutablePath);
                ValidateExecutablePath(p.LearnedExecutablePath);
                if (p.ForceTrigger && string.IsNullOrEmpty(p.PreferredExecutablePath))
                    throw new FormatException("forced profile has no executable");
                foreach (string entry in p.Entries)
                    if (string.IsNullOrWhiteSpace(entry)
                        || entry.IndexOf('\r') >= 0 || entry.IndexOf('\n') >= 0)
                        throw new FormatException("invalid profile entry");
                foreach (KeyValuePair<string, string> kv in p.Overrides)
                {
                    string canonical = PolicyCatalog.Canonical(kv.Key, kv.Value);
                    if (canonical == null || !string.Equals(canonical, kv.Value,
                        StringComparison.Ordinal))
                        throw new FormatException("invalid profile override");
                }
            }
        }

        private static void ValidateRootPath(string value)
        {
            if (value == null) return;
            string normalized = NormalizeRoot(value);
            if (normalized == null || !string.Equals(normalized, value, StringComparison.Ordinal))
                throw new FormatException("invalid profile root");
        }

        private static void ValidateExecutablePath(string value)
        {
            if (value == null) return;
            string normalized = NormalizePath(value);
            if (normalized == null || !string.Equals(normalized, value, StringComparison.Ordinal))
                throw new FormatException("invalid profile executable path");
        }

        private static string IdentityKey(string id, string root, string executablePath)
        {
            if (!string.IsNullOrEmpty(executablePath)) return "E|" + executablePath;
            if (!string.IsNullOrEmpty(root)) return "R|" + root;
            return "I|" + id;
        }

        private List<GameProfile> Load()
        {
            var result = new List<GameProfile>();
            try
            {
                if (!File.Exists(path)) return result;
                string[] lines = File.ReadAllLines(path, StrictUtf8);
                if (lines.Length == 0 || lines[0] != HeaderV5)
                    throw new FormatException("invalid profile header");
                var profilesById = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
                var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var learnedById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var forcedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var overridesById = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) throw new FormatException("empty profile record");
                    string[] a = lines[i].Split('|');
                    if (a[0] == "P")
                    {
                        if (a.Length != 6) throw new FormatException("invalid P record");
                        string id = Decode(a[1]);
                        string name = Decode(a[2]);
                        string root = NullIfEmpty(Decode(a[3]));
                        string executable = NullIfEmpty(Decode(a[4]));
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                            throw new FormatException("profile identity missing");
                        ValidateRootPath(root);
                        ValidateExecutablePath(executable);
                        var p = new GameProfile
                        {
                            Id = id,
                            Name = name,
                            Root = root,
                            ExecutablePath = executable
                        };
                        ParseEntries(p.Entries, Decode(a[5]));
                        if (profilesById.ContainsKey(id))
                            throw new FormatException("duplicate profile id");
                        if (!identities.Add(IdentityKey(id, root, executable)))
                            throw new FormatException("duplicate profile identity");
                        profilesById.Add(id, p);
                        result.Add(p);
                        continue;
                    }
                    if (a[0] == "L")
                    {
                        if (a.Length != 3) throw new FormatException("invalid L record");
                        string id = Decode(a[1]);
                        string learnedPath = NullIfEmpty(Decode(a[2]));
                        if (string.IsNullOrWhiteSpace(id) || learnedPath == null)
                            throw new FormatException("invalid L value");
                        ValidateExecutablePath(learnedPath);
                        if (learnedById.ContainsKey(id))
                            throw new FormatException("duplicate L record");
                        learnedById.Add(id, learnedPath);
                        continue;
                    }
                    if (a[0] == "F")
                    {
                        if (a.Length != 2) throw new FormatException("invalid F record");
                        string id = Decode(a[1]);
                        if (string.IsNullOrWhiteSpace(id) || !forcedIds.Add(id))
                            throw new FormatException("invalid or duplicate F record");
                        continue;
                    }
                    if (a[0] == "O")
                    {
                        if (a.Length != 4) throw new FormatException("invalid O record");
                        string id = Decode(a[1]);
                        string key = Decode(a[2]);
                        string value = Decode(a[3]);
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrEmpty(key))
                            throw new FormatException("invalid O value");
                        string canonical = PolicyCatalog.Canonical(key, value);
                        if (canonical == null || !string.Equals(canonical, value,
                            StringComparison.Ordinal))
                            throw new FormatException("invalid O value");
                        Dictionary<string, string> bag;
                        if (!overridesById.TryGetValue(id, out bag))
                        {
                            bag = new Dictionary<string, string>(StringComparer.Ordinal);
                            overridesById[id] = bag;
                        }
                        if (bag.ContainsKey(key)) throw new FormatException("duplicate O record");
                        bag.Add(key, value);
                        continue;
                    }
                    throw new FormatException("unknown profile record");
                }
                foreach (KeyValuePair<string, string> learned in learnedById)
                {
                    GameProfile p;
                    if (!profilesById.TryGetValue(learned.Key, out p))
                        throw new FormatException("dangling L record");
                    p.LearnedExecutablePath = learned.Value;
                }
                foreach (string id in forcedIds)
                {
                    GameProfile p;
                    if (!profilesById.TryGetValue(id, out p))
                        throw new FormatException("dangling F record");
                    p.ForceTrigger = true;
                }
                foreach (KeyValuePair<string, Dictionary<string, string>> item in overridesById)
                {
                    GameProfile p;
                    if (!profilesById.TryGetValue(item.Key, out p))
                        throw new FormatException("dangling O record");
                    foreach (KeyValuePair<string, string> kv in item.Value)
                        p.Overrides.Add(kv.Key, kv.Value);
                }
                ValidateProfiles(result);
            }
            catch (Exception ex)
            {
                loadFailed = true;
                result.Clear();
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

        private static void ParseEntries(HashSet<string> set, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (string entry in text.Split(new[] { '\n' }, StringSplitOptions.None))
                if (string.IsNullOrWhiteSpace(entry) || entry.IndexOf('\r') >= 0
                    || !set.Add(entry))
                    throw new FormatException("invalid or duplicate profile entry");
        }

        private static string Join(IEnumerable<string> values)
        {
            var a = new List<string>(values);
            a.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", a.ToArray());
        }

        private static string B64(string s)
        {
            return Convert.ToBase64String(StrictUtf8.GetBytes(s ?? ""));
        }

        private static string Decode(string s)
        {
            byte[] bytes = Convert.FromBase64String(s ?? "");
            if (!string.Equals(Convert.ToBase64String(bytes), s, StringComparison.Ordinal))
                throw new FormatException("non-canonical base64");
            return StrictUtf8.GetString(bytes);
        }

        private static string NullIfEmpty(string value)
        {
            return value.Length == 0 ? null : value;
        }
    }
}
