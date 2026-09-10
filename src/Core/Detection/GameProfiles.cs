// @author bdth 2074055628@qq.com
// 文件用途 严格保存与读取当前 V5 游戏配置 不迁移不修复不自删数据
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    // 3 和 5 都是墓碑值 不许有枚举成员认领
    //   3 是 1.x 那个极限档 比电竞更激进 跟掌机方向正相反 接手它等于静默换档
    //   5 是 2.2.2 砍掉的极限档 它原有的功能全部落到了各自的独立开关
    //   界面上的顺序由 VisibleOrder 单独排 跟这里的取值无关
    internal enum PerformancePreset
    {
        Standard = 0,
        Competitive = 1,
        Custom = 2,
        Handheld = 4
    }

    internal static class PresetValue
    {
        // 取值不连续 别再写成范围判断 3 和 5 都是极限档的墓碑值 必须继续被拒
        public static bool IsValid(int raw)
        {
            return raw == 0 || raw == 1 || raw == 2 || raw == 4;
        }

        // 掌机档只在带电池的机器上有意义 没电池就不列出来
        //   IsValid(4) 仍为真 存量取值原样留在盘上 换到带电池的机器立刻恢复
        //   读不出电源状态按不支持算 宁可少列一档 也不给台式机一个什么都不改的选项
        public static bool HandheldSupported
        {
            get { try { return Native.HasSystemBattery(); } catch { return false; } }
        }

        // 极限档存量取值一律解析为电竞 不落到智能
        //   极限的压制口径与电竞逐字节相同 落到电竞不改变后台行为 落到智能会放宽范围
        //   它原有的五项功能各自有了开关且默认关 老用户要哪一项自己开
        //   掌机不做这种重解析 电池检测是硬件探测且失败即报无电池
        //   GetSystemPowerStatus 失败或状态未知都算没电池 还按进程缓存一次
        //   一次瞬时失败若把掌机重解析成电竞 就会去拨电源滑块并启用候选线程提优
        //   那正是掌机档要防的负优化 所以掌机只在选择器里隐藏 存量语义一律不动
        public static PerformancePreset From(int raw)
        {
            if (raw == 5) return PerformancePreset.Competitive;
            return IsValid(raw) ? (PerformancePreset)raw : PerformancePreset.Standard;
        }

        // 界面档位顺序 本机不适用的档不出现 与 KeyPreset 的 Choices 全集是两回事
        //   顺序与取值两份必须同源 所以取值从顺序推 别再各写一份硬编码数组
        private static PerformancePreset[] Order()
        {
            var list = new List<PerformancePreset>(5);
            list.Add(PerformancePreset.Standard);
            list.Add(PerformancePreset.Competitive);
            // 本机不适用但当前就停在这一档时照样列出来 否则用户被关在一个看不见的档里换不出去
            if (HandheldSupported || StoredIs(PerformancePreset.Handheld))
                list.Add(PerformancePreset.Handheld);
            list.Add(PerformancePreset.Custom);
            return list.ToArray();
        }

        public static string[] VisibleChoices()
        {
            PerformancePreset[] order = Order();
            var choices = new string[order.Length];
            for (int i = 0; i < order.Length; i++)
                choices[i] = ((int)order[i]).ToString(CultureInfo.InvariantCulture);
            return choices;
        }

        public static PerformancePreset[] VisibleOrder() { return Order(); }

        // 只看盘上那个全局取值 不经 From 也不查适用性 避免与 Order 互相递归
        private static bool StoredIs(PerformancePreset mode)
        {
            int parsed;
            return int.TryParse(Settings.LoadStr(PolicyCatalog.KeyPreset, ""), NumberStyles.None,
                CultureInfo.InvariantCulture, out parsed) && parsed == (int)mode;
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

        // 逐游戏的开关 故意和老的全局开关脱钩
        // 现有 V5 库里没有这一项就表示受保护 绝不从 HKCU 继承
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

        // 已下架功能的覆盖键 加载时静默丢弃 不触发库重置
        //   两个键都随 2.1.3.3 发布过 但值只是布尔开关 丢弃即回默认 无信息可失
        //   活档案里不该再出现它们 校验时按损坏处理
        private static readonly string[] RetiredOverrideKeys = { "GmDisplaySolo", "GmMemShield", "GmGpuClockLockV1", "GmCacheWarm" };

        internal static bool IsRetiredOverrideKey(string key)
        {
            foreach (string retired in RetiredOverrideKeys)
                if (string.Equals(retired, key, StringComparison.Ordinal)) return true;
            return false;
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
        private readonly object saveSync = new object();

        public GameProfileStore(string dir)
        {
            path = Path.Combine(dir, FileName);
        }

        public List<GameProfile> LoadProfiles()
        {
            return LoadProfiles(true);
        }

        internal List<GameProfile> LoadProfiles(bool createIfMissing)
        {
            List<GameProfile> loaded = Load();
            if (loadFailed)
            {
                // 统一走与保存失败相同的熔断 Save 看到 loadFailed 只置故障位
                // 不会改写原文件 真正的精确目录清空与退出只允许 Program 执行
                Save(loaded);
                return new List<GameProfile>();
            }
            if (createIfMissing && !File.Exists(path)) Save(loaded);
            return loaded;
        }

        public bool Save(IList<GameProfile> profiles)
        {
            return Save(profiles, null);
        }

        internal bool Save(IList<GameProfile> profiles, Func<bool> canCommit)
        {
            lock (saveSync) return SaveCore(profiles, canCommit);
        }

        private bool SaveCore(IList<GameProfile> profiles, Func<bool> canCommit)
        {
            if (SaveFailed) return false;
            Interlocked.Exchange(ref retryableSaveFailure, 0);
            Interlocked.Exchange(ref saveCanceled, 0);
            if (loadFailed)
            {
                Logger.Log(Lang.T("log.gameprofiles.1"));
                Interlocked.Exchange(ref saveFailed, 1);
                return false;
            }
            try
            {
                byte[] snapshot = SnapshotBytes(profiles);
                if (!CommitStrict(snapshot, canCommit))
                {
                    Interlocked.Exchange(ref saveCanceled, 1);
                    return false;
                }
                return true;
            }
            catch (ProfileCommitBusyException ex)
            {
                Interlocked.Exchange(ref retryableSaveFailure, 1);
                Logger.LogFailure(Lang.T("log.gameprofiles.busy"), ex.InnerException);
                return false;
            }
            catch (Exception ex)
            {
                MarkSaveFailed(ex);
                return false;
            }
        }

        internal void MarkSaveFailed(Exception error)
        {
            Interlocked.Exchange(ref saveFailed, 1);
            Logger.LogFailure(Lang.T("log.gameprofiles.3"), error);
        }

        internal static byte[] SnapshotBytes(IList<GameProfile> profiles)
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
            return StrictUtf8.GetBytes(string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine);
        }

        private bool loadFailed;
        private int saveFailed;
        private int retryableSaveFailure;
        private int saveCanceled;

        public bool LoadFailed { get { return loadFailed; } }
        public bool SaveFailed { get { return Interlocked.CompareExchange(ref saveFailed, 0, 0) != 0; } }
        internal bool RetryableSaveFailure { get { return Volatile.Read(ref retryableSaveFailure) != 0; } }
        internal bool SaveCanceled { get { return Volatile.Read(ref saveCanceled) != 0; } }

        private static bool CommitAllowed(Func<bool> canCommit)
        {
            if (canCommit == null) return true;
            try { return canCommit(); }
            catch (Exception ex)
            {
                Logger.Warn("Profile commit eligibility check failed: " + ex.Message);
                return false;
            }
        }

        private sealed class ProfileCommitBusyException : IOException
        {
            internal ProfileCommitBusyException(IOException inner) : base("Profile commit busy", inner) { }
        }

        // 只重试那些文档写明两个文件名都还在的错误
        // 1176 和 1177 可能把命名空间改掉 只能当致命错误
        internal static bool IsRetryableReplaceError(IOException error)
        {
            uint hr = unchecked((uint)error.HResult);
            if ((hr & 0xFFFF0000U) != 0x80070000U) return false;
            uint code = hr & 0xFFFFU;
            return code == 32 || code == 33 || code == 1175;
        }

#if PAVISE_SELFTEST
        internal Action<int> BeforeReplaceForTest;
        internal Action<int> RetryWaitForTest;
#endif

        // 档案不使用 AtomicFile 的兼容回退 Replace 失败后绝不能备份旧档
        // 非原子覆盖并谎报成功 短暂占用有界重试 其他失败保留致命保护
        private bool CommitStrict(byte[] snapshot, Func<bool> canCommit)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (!CommitAllowed(canCommit)) return false;
                using (var fs = new FileStream(tmp, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    fs.Write(snapshot, 0, snapshot.Length);
                    fs.Flush(true);
                }
                if (!File.Exists(path))
                {
                    if (!CommitAllowed(canCommit)) return false;
                    File.Move(tmp, path);
                }
                else
                {
                    int[] delays = { 25, 50, 100, 200 };
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
#if PAVISE_SELFTEST
                            if (BeforeReplaceForTest != null) BeforeReplaceForTest(attempt);
#endif
                            // 准备完和每次重试等待之后都要重验 不能只在 Save 入口验一次
                            if (!CommitAllowed(canCommit)) return false;
                            File.Replace(tmp, path, null);
                            break;
                        }
                        catch (IOException ex)
                        {
                            if (!IsRetryableReplaceError(ex) || !File.Exists(tmp) || !File.Exists(path)) throw;
                            if (attempt == delays.Length) throw new ProfileCommitBusyException(ex);
#if PAVISE_SELFTEST
                            if (RetryWaitForTest != null) RetryWaitForTest(attempt);
                            else
#endif
                                Thread.Sleep(delays[attempt]);
                        }
                    }
                }
                return true;
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
                    if (GameProfile.IsRetiredOverrideKey(kv.Key))
                        throw new FormatException("retired override key in live profile");
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
                        // 已下架的覆盖键 静默丢弃 不算库损坏 布尔开关丢弃即回默认
                        if (GameProfile.IsRetiredOverrideKey(key)) continue;
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
