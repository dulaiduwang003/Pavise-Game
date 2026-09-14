// @author bdth 2074055628@qq.com
// File purpose Strictly saves and loads the current V5 game config; no migration, no repair, no self-deletion of data
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    // 3 and 5 are tombstone values; no enum member may claim them
    //   3 is the 1.x Extreme tier, more aggressive than Esports and the opposite of Handheld; adopting it would be a silent tier switch
    //   5 is the Extreme tier cut in 2.2.2; all of its features moved to their own independent switches
    //   UI order is handled separately by VisibleOrder and is unrelated to the values here
    internal enum PerformancePreset
    {
        Standard = 0,
        Competitive = 1,
        Custom = 2,
        Handheld = 4
    }

    internal static class PresetValue
    {
        // Values are not contiguous, never write this as a range check; 3 and 5 are Extreme tier tombstones and must keep being rejected
        public static bool IsValid(int raw)
        {
            return raw == 0 || raw == 1 || raw == 2 || raw == 4;
        }

        // The Handheld tier only makes sense on a machine with a battery; without one it is not listed
        //   IsValid(4) stays true; stored values stay on disk as-is and come back immediately on a battery-equipped machine
        //   Unreadable power state counts as unsupported; better to list one tier fewer than to give a desktop an option that changes nothing
        public static bool HandheldSupported
        {
            get { try { return Native.HasSystemBattery(); } catch { return false; } }
        }

        // Stored Extreme tier values always parse as Esports, never Smart
        //   Extreme's suppression criteria are byte-for-byte the same as Esports, so landing on Esports leaves background behavior unchanged while Smart would loosen the scope
        //   Its former five features each have their own switch, default off; veterans enable whichever they want
        //   Handheld gets no such reinterpretation: battery detection is a hardware probe that reports no battery on failure
        //   GetSystemPowerStatus failure or unknown state both count as no battery, and it is cached once per process
        //   If one transient failure reinterpreted Handheld as Esports, it would move the power slider and enable candidate thread boost,
        //   exactly the negative optimization the Handheld tier guards against; so Handheld is only hidden in the picker and stored semantics never change
        public static PerformancePreset From(int raw)
        {
            if (raw == 5) return PerformancePreset.Competitive;
            return IsValid(raw) ? (PerformancePreset)raw : PerformancePreset.Standard;
        }

        // UI tier order; tiers not applicable on this machine are absent; distinct from the full Choices set of KeyPreset
        //   Order and values must share one source, so values derive from the order; do not hand-write another array
        private static PerformancePreset[] Order()
        {
            var list = new List<PerformancePreset>(5);
            list.Add(PerformancePreset.Standard);
            list.Add(PerformancePreset.Competitive);
            // A tier not applicable here but currently selected is still listed, otherwise the user is locked in an invisible tier with no way out
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

        // Only looks at the stored global value on disk; bypasses From and applicability checks to avoid mutual recursion with Order
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

        // Per-game switch, deliberately decoupled from the old global switch
        // Absence in an existing V5 library means protected; never inherited from HKCU
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

        // Override keys of retired features; silently dropped on load, no library reset
        //   Both keys shipped with 2.1.3.3, but the values were plain booleans; dropping them just restores the default, nothing is lost
        //   They should never appear in a live profile; validation treats them as corruption
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
                // Goes through the same circuit breaker as a save failure; Save seeing loadFailed only sets the fault bit
                // and never rewrites the original file; the actual precise directory wipe and exit are allowed only in Program
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

        // Only retry errors that the docs say leave both file names in place
        // 1176 and 1177 may have altered the namespace, so they can only be fatal
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

        // Profiles do not use AtomicFile's compatibility fallback; after a failed Replace the old file must never be backed up,
        // overwritten non-atomically and reported as success; brief sharing violations get bounded retries, other failures keep the fatal protection
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
                            // Re-verify after preparation and after every retry wait, not just once at the Save entry
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
                        // Retired override keys are silently dropped, not library corruption; a dropped boolean just restores the default
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
