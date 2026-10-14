using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
    internal sealed class ActionFailureRecord
    {
        internal string Key, Action, Target, Environment, Reason;
        internal DateTime OccurredUtc;
        internal bool PersistenceFailed, RetryPending;
    }

    // A failure record is independent of preferences and restoration journals. A retry is a
    // process-local, single-use permission; the durable isolation remains until verified success.
    internal static class ActionFailureStore
    {
        internal const string Prefix = "ActionFailure_";
        internal const string AttemptPrefix = "ActionAttempt_";
        private const string MigrationPrefix = Prefix + "Migrated_";
        private static readonly object gate = new object();
        internal const int RecordKeyCacheLimit = 512, RecordKeyCacheMaxChars = 2048;
        private static readonly Dictionary<string, string> recordKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Queue<string> recordKeyOrder = new Queue<string>();
        private static readonly Dictionary<string, ActionFailureRecord> volatileFailures = new Dictionary<string, ActionFailureRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> permits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> retries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> admissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> failedCompletions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool migrated;
        private static long retryGeneration;
        internal static long RetryGeneration { get { lock (gate) return retryGeneration; } }
        internal static bool HasRetryPermits { get { lock (gate) return permits.Count != 0; } }
#if PAVISE_SELFTEST
        static ActionFailureStore() { Settings.AfterTransientResetForTest += ReloadForTest; }
        internal static Func<string, string, bool> SaveForTest;
        internal static Func<DateTime> ClockForTest;
        internal static int RecordKeyCacheCountForTest { get { lock (gate) return recordKeys.Count; } }
        internal static void ReloadForTest()
        { lock (gate) { volatileFailures.Clear(); permits.Clear(); retries.Clear(); admissions.Clear(); failedCompletions.Clear(); recordKeys.Clear(); recordKeyOrder.Clear(); migrated = false; SaveForTest = null; ClockForTest = null; } }
#endif
        internal static string CurrentEnvironment()
        { return Environment.OSVersion.VersionString + " | " + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + " | CPU " + Environment.ProcessorCount; }
        private static string Normalize(string value) { return (value ?? "").Trim().ToUpperInvariant(); }
        internal static string RecordKey(string action, string target)
        {
            string identity = Normalize(action) + "\n" + Normalize(target);
            lock (gate)
            {
                string key;
                if (recordKeys.TryGetValue(identity, out key)) return key;
                key = ComputeRecordKey(identity);
                // 只缓存不可变的键名，故障和恢复记录仍实时读取。
                if (identity.Length <= RecordKeyCacheMaxChars)
                {
                    if (recordKeys.Count >= RecordKeyCacheLimit) recordKeys.Remove(recordKeyOrder.Dequeue());
                    recordKeys.Add(identity, key); recordKeyOrder.Enqueue(identity);
                }
                return key;
            }
        }
        private static string ComputeRecordKey(string identity)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(identity);
            using (SHA256 hash = SHA256.Create())
            {
                var text = new StringBuilder();
                foreach (byte value in hash.ComputeHash(bytes)) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return Prefix + text;
            }
        }
        private static string Encode(string text) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? "")); }
        private static string Decode(string text) { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
        private static string IntentKey(string action, string target) { return AttemptPrefix + RecordKey(action, target).Substring(Prefix.Length); }
        private static string Serialize(ActionFailureRecord record)
        { return "1|" + Encode(record.Action) + "|" + Encode(record.Target) + "|" + Encode(record.Environment) + "|" + record.OccurredUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Encode(record.Reason); }
        private static ActionFailureRecord Parse(string key, string value)
        {
            try
            {
                if (value.Length > 32768) return null;
                string[] parts = value.Split('|'); long ticks;
                if (parts.Length != 6 || parts[0] != "1" || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out ticks)) return null;
                var record = new ActionFailureRecord { Key = key, Action = Decode(parts[1]), Target = Decode(parts[2]), Environment = Decode(parts[3]), OccurredUtc = new DateTime(ticks, DateTimeKind.Utc), Reason = Decode(parts[5]) };
                if (string.IsNullOrEmpty(record.Action) || string.IsNullOrEmpty(record.Target) || !string.Equals(RecordKey(record.Action, record.Target), key, StringComparison.OrdinalIgnoreCase)) return null;
                return record;
            }
            catch { return null; }
        }
        private static bool Save(string key, string text)
        {
            bool saved;
#if PAVISE_SELFTEST
            if (SaveForTest != null) saved = SaveForTest(key, text);
            else
#endif
                saved = Settings.SaveStrDurable(key, text);
            string observed;
            return saved && Settings.TryLoadStr(key, out observed) && observed == text;
        }
        private static ActionFailureRecord Read(string key, string action, string target)
        {
            ActionFailureRecord record;
            if (volatileFailures.TryGetValue(key, out record)) return record;
            string value;
            if (!Settings.TryLoadStr(key, out value))
                return new ActionFailureRecord { Key = key, Action = action, Target = target, Environment = CurrentEnvironment(), Reason = "Isolation record unreadable; retained", PersistenceFailed = true };
            if (string.IsNullOrEmpty(value)) return null;
            return Parse(key, value) ?? new ActionFailureRecord { Key = key, Action = action, Target = target, Environment = CurrentEnvironment(), Reason = "Isolation record invalid; retained", PersistenceFailed = true };
        }
        private static string BlockKey(string action, string target)
        {
            string exact = RecordKey(action, target);
            if (!admissions.ContainsKey(exact)) RecoverIntent(IntentKey(action, target), action, target);
            if (Read(exact, action, target) != null) return exact;
            string legacy = RecordKey(action, "*");
            return Read(legacy, action, "*") != null ? legacy : null;
        }
        internal static bool IsBlocked(string action, string target)
        {
            lock (gate) { MigrateLegacy(); string key = BlockKey(action, target); return key != null && !permits.Contains(key); }
        }
        internal static bool IsIsolated(string action, string target)
        { lock (gate) { MigrateLegacy(); return BlockKey(action, target) != null; } }
        internal static string FailureReason(string action, string target)
        { lock (gate) { MigrateLegacy(); string key = BlockKey(action, target); return key == null ? "" : Read(key, action, target).Reason; } }
        internal static bool IsRetryPending(string action, string target)
        {
            lock (gate) { MigrateLegacy(); string key = BlockKey(action, target); return key != null && permits.Contains(key); }
        }
        internal static bool TryBeginApply(string action, string target, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(target)) { reason = "Missing action identity"; return false; }
            lock (gate)
            {
                MigrateLegacy();
                string exact = RecordKey(action, target);
                if (admissions.ContainsKey(exact)) { reason = "Action already running"; return false; }
                string blocked = BlockKey(action, target);
                if (blocked != null)
                {
                    if (!permits.Contains(blocked)) { reason = Read(blocked, action, target).Reason; return false; }
                }
                var intent = NewRecord(action, target, "Execution admitted; verified completion pending", null);
                string serialized = Serialize(intent), observed, intentKey = IntentKey(action, target);
                if (!Save(intentKey, serialized) || !Settings.TryLoadStr(intentKey, out observed) || observed != serialized)
                { Trip(action, target, "Execution intent could not be persisted; no apply attempted"); reason = "Execution intent could not be persisted"; if (blocked != null) permits.Remove(blocked); return false; }
                if (blocked != null) { permits.Remove(blocked); retries[exact] = blocked; }
                admissions[exact] = blocked;
                return true;
            }
        }
        internal static void CancelApply(string action, string target)
        {
            lock (gate)
            {
                string exact = RecordKey(action, target), key;
                if (failedCompletions.Remove(exact))
                {
                    key = retries.ContainsKey(exact) ? retries[exact] : exact;
                    ActionFailureRecord completion = Read(key, action, target);
                    if (completion != null && completion.PersistenceFailed)
                    { completion.PersistenceFailed = !Save(key, Serialize(completion)); volatileFailures[key] = completion; }
                    // A caller reaches cancellation here only after confirmed rollback. Keep the
                    // intent if the durable failure cannot replace it; never return this retry permit.
                    if (completion != null && !completion.PersistenceFailed) Save(IntentKey(action, target), "");
                    admissions.Remove(exact); retries.Remove(exact); permits.Remove(key); return;
                }
                ActionFailureRecord prior = Read(exact, action, target);
                if (prior != null && prior.PersistenceFailed && !admissions.ContainsKey(exact)) return;
                if (!Save(IntentKey(action, target), ""))
                { Trip(action, target, "Cancelled execution intent could not be cleared"); return; }
                if (retries.TryGetValue(exact, out key)) { retries.Remove(exact); if (Read(key, action, target) != null) permits.Add(key); }
                admissions.Remove(exact);
            }
        }
        internal static void WithdrawRetry(string action, string target)
        {
            lock (gate)
            {
                string exact = RecordKey(action, target), key;
                if (failedCompletions.Contains(exact)) { CancelApply(action, target); return; }
                ActionFailureRecord prior = Read(exact, action, target);
                if (prior != null && prior.PersistenceFailed && !admissions.ContainsKey(exact)) { permits.Remove(exact); return; }
                if (!Save(IntentKey(action, target), ""))
                { Trip(action, target, "Withdrawn execution intent could not be cleared"); return; }
                if (retries.TryGetValue(exact, out key)) { permits.Remove(key); retries.Remove(exact); }
                key = BlockKey(action, target); if (key != null) permits.Remove(key);
                admissions.Remove(exact);
            }
        }
        internal static bool CompleteApply(string action, string target)
        {
            lock (gate)
            {
                string exact = RecordKey(action, target), key;
                if (!admissions.ContainsKey(exact)) return false;
                bool retry = retries.TryGetValue(exact, out key);
                string intent;
                if (!Settings.TryLoadStr(IntentKey(action, target), out intent) || string.IsNullOrEmpty(intent))
                { PreserveCompletionFailure(action, target, retry ? key : exact, null, "Verified execution intent unreadable or missing"); return false; }
                // Keep the unfinished intent until durable removal of the older failure is verified.
                if (retry && !Save(key, ""))
                { PreserveCompletionFailure(action, target, key, intent, "Verified failure record could not be cleared"); return false; }
                if (!Save(IntentKey(action, target), ""))
                { PreserveCompletionFailure(action, target, retry ? key : exact, intent, "Verified execution intent could not be cleared"); return false; }
                admissions.Remove(exact); retries.Remove(exact); failedCompletions.Remove(exact);
                if (retry) { volatileFailures.Remove(key); permits.Remove(key); }
                return true;
            }
        }
        private static void PreserveCompletionFailure(string action, string target, string failureKey, string intent, string reason)
        {
            string exact = RecordKey(action, target), storedTarget = string.Equals(exact, failureKey, StringComparison.OrdinalIgnoreCase) ? target : "*";
            if (string.IsNullOrEmpty(intent)) intent = Serialize(NewRecord(action, target, "Completion could not be confirmed; recovery required", null));
            // SetValue may have emptied a value even when Flush or the verification read failed.
            // Put back a nonempty intent before returning failure; the caller must then roll back.
            Save(IntentKey(action, target), intent);
            ActionFailureRecord failure = NewRecord(action, storedTarget, reason, null);
            failure.PersistenceFailed = !Save(failureKey, Serialize(failure)); volatileFailures[failureKey] = failure;
            failedCompletions.Add(exact); permits.Remove(failureKey);
        }
        internal static void Trip(string action, string target, string reason, string environmentContext = null)
        {
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(target)) return;
            lock (gate)
            {
                string key = RecordKey(action, target), storedTarget = target;
                string retry;
                if (retries.TryGetValue(key, out retry))
                { permits.Remove(retry); retries.Remove(key); if (!string.Equals(retry, key, StringComparison.OrdinalIgnoreCase)) storedTarget = "*"; }
                else if (target != "*" && Read(RecordKey(action, "*"), action, "*") != null) storedTarget = "*";
                admissions.Remove(key); permits.Remove(key); failedCompletions.Remove(key);
                key = RecordKey(action, storedTarget);
                permits.Remove(key);
                var record = NewRecord(action, storedTarget, reason, environmentContext);
                record.PersistenceFailed = !Save(key, Serialize(record));
                volatileFailures[key] = record;
                if (!record.PersistenceFailed) Save(IntentKey(action, target), "");
            }
        }
        private static ActionFailureRecord NewRecord(string action, string target, string reason, string environmentContext)
        {
            DateTime occurred = DateTime.UtcNow;
#if PAVISE_SELFTEST
            if (ClockForTest != null) occurred = ClockForTest().ToUniversalTime();
#endif
            var record = new ActionFailureRecord { Key = RecordKey(action, target), Action = action, Target = target, Environment = CurrentEnvironment() + " | " + (string.IsNullOrEmpty(environmentContext) ? "GPU/driver context unavailable" : PsRunner.NormalizeError(environmentContext)), Reason = string.IsNullOrEmpty(reason) ? "Apply or verification failed" : PsRunner.NormalizeError(reason), OccurredUtc = occurred };
            if (record.Reason.Length > 4096) record.Reason = record.Reason.Substring(0, 4096);
            return record;
        }
        private static void RecoverIntent(string intentKey, string action, string target)
        {
            string text;
            if (!Settings.TryLoadStr(intentKey, out text))
            {
                if (!string.IsNullOrEmpty(action) && !string.IsNullOrEmpty(target))
                { ActionFailureRecord failed = NewRecord(action, target, "Execution intent unreadable; apply remains isolated", null); failed.PersistenceFailed = true; volatileFailures[failed.Key] = failed; }
                else throw new InvalidOperationException("Unfinished execution intent unreadable: " + intentKey);
                return;
            }
            if (string.IsNullOrEmpty(text)) return;
            string failureKey = Prefix + intentKey.Substring(AttemptPrefix.Length);
            if (admissions.ContainsKey(failureKey)) return;
            ActionFailureRecord intent = Parse(failureKey, text);
            if (intent == null)
            {
                if (!string.IsNullOrEmpty(action) && !string.IsNullOrEmpty(target))
                { ActionFailureRecord failed = NewRecord(action, target, "Execution intent invalid; apply remains isolated", null); failed.PersistenceFailed = true; volatileFailures[failed.Key] = failed; }
                else throw new InvalidOperationException("Invalid unfinished execution intent: " + intentKey);
                return;
            }
            ActionFailureRecord record = Read(failureKey, intent.Action, intent.Target)
                ?? Read(RecordKey(intent.Action, "*"), intent.Action, "*");
            if (record == null)
                Trip(intent.Action, intent.Target, "Previous execution was not confirmed; recovery required", intent.Environment);
            record = Read(failureKey, intent.Action, intent.Target) ?? Read(RecordKey(intent.Action, "*"), intent.Action, "*");
            if (record != null && !record.PersistenceFailed) Save(intentKey, "");
        }
        internal static IList<ActionFailureRecord> Records()
        {
            lock (gate)
            {
                MigrateLegacy();
                string[] pending;
                if (!Settings.TryLoadStoredNames(AttemptPrefix, out pending)) throw new InvalidOperationException("Unfinished execution intents could not be read; isolation remains active.");
                foreach (string intent in pending) RecoverIntent(intent, null, null);
                var records = new Dictionary<string, ActionFailureRecord>(StringComparer.OrdinalIgnoreCase);
                string[] names;
                if (!Settings.TryLoadStoredNames(Prefix, out names)) throw new InvalidOperationException("Action isolation records could not be read; isolation remains active.");
                foreach (string key in names)
                    {
                        if (key.StartsWith(MigrationPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        ActionFailureRecord record = Read(key, null, null);
                        if (record != null) records[key] = record;
                    }
                foreach (KeyValuePair<string, ActionFailureRecord> item in volatileFailures) records[item.Key] = item.Value;
                var list = new List<ActionFailureRecord>();
                foreach (ActionFailureRecord source in records.Values)
                    list.Add(new ActionFailureRecord { Key = source.Key, Action = source.Action, Target = source.Target, Environment = source.Environment, Reason = source.Reason, OccurredUtc = source.OccurredUtc, PersistenceFailed = source.PersistenceFailed, RetryPending = permits.Contains(source.Key) || retries.ContainsValue(source.Key) });
                list.Sort(delegate(ActionFailureRecord a, ActionFailureRecord b) { return b.OccurredUtc.CompareTo(a.OccurredUtc); });
                return list;
            }
        }
        internal static bool RequestRetry(string recordKey)
        {
            if (string.IsNullOrEmpty(recordKey)) return false;
            lock (gate)
            {
                MigrateLegacy();
                ActionFailureRecord record = Read(recordKey, null, null);
                if (record == null || string.IsNullOrEmpty(record.Action) || string.IsNullOrEmpty(record.Target)
                    || permits.Contains(recordKey) || retries.ContainsValue(recordKey)) return false;
                // A retry needs a persistent failure record so a crash still leaves it isolated.
                if (!Save(recordKey, Serialize(record))) { record.PersistenceFailed = true; volatileFailures[recordKey] = record; return false; }
                record.PersistenceFailed = false;
                permits.Add(recordKey); retryGeneration++; return true;
            }
        }
        internal static void RegisterLegacy(string action, string target, string reason)
        { lock (gate) if (BlockKey(action, target) == null) Trip(action, target, reason); }
        private static void ImportLegacy(string oldKey, string action, bool failed)
        {
            string marker = MigrationPrefix + oldKey, text;
            if (!failed || !Settings.TryLoadStr(marker, out text) || text == "1") return;
            RegisterLegacy(action, "*", "Legacy failure isolation | " + oldKey);
            ActionFailureRecord record = Read(RecordKey(action, "*"), action, "*");
            if (record != null && !record.PersistenceFailed) Save(marker, "1");
        }
        private static void MigrateLegacy()
        {
            if (migrated) return;
            migrated = true;
            string[] keys = { "do", "wu", "services", "standby", "awake", "dwmboost", "gpupower", "amdalag", "amdafmf", "maint", "intelend", "laptopperf", "gamedvr", "mmcss", "pqos", "rsr", "nvvrr" };
            string[] actions = { PolicyCatalog.KeyPauseDl, PolicyCatalog.KeyPauseUpdate, PolicyCatalog.KeyPauseServices, PolicyCatalog.KeyStandbyCleaner, PolicyCatalog.KeyAwake, PolicyCatalog.KeyDwmBoost, PolicyCatalog.KeyGpuPowerMax, PolicyCatalog.KeyAmdAntiLag, PolicyCatalog.KeyAmdAfmf, PolicyCatalog.KeyPauseMaintenance, PolicyCatalog.KeyIntelEndurance, PolicyCatalog.KeyLaptopPerf, "GameDvrOff", "GmMmcss", "PresenceQos", "GmRsr", "NvVrrWindowed" };
            for (int index = 0; index < keys.Length; index++) ImportLegacy("EnvFuse_" + keys[index], actions[index], Settings.Load("EnvFuse_" + keys[index], false));
            ImportLegacy("PowerPlanFailStreak", PolicyCatalog.KeyPowerPlan, CounterFailed("PowerPlanFailStreak"));
            string[] nv = { PolicyCatalog.KeyNvMaxPerf, PolicyCatalog.KeyNvLowLat, PolicyCatalog.KeyNvSmoothMotion, PolicyCatalog.KeyNvShaderCache, PolicyCatalog.KeyNvRebar, PolicyCatalog.KeyNvDlss, "NvAnsel" };
            string[] oldNv = { "pstate", "prerender", "smooth", "shadercache", "rebarfeat", "dlssovr", "ansel" };
            for (int index = 0; index < nv.Length; index++) ImportLegacy("NvFailStreak_" + oldNv[index], nv[index], CounterFailed("NvFailStreak_" + oldNv[index]));
        }
        private static bool CounterFailed(string key)
        { int value; return int.TryParse(Settings.LoadStr(key, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0; }
    }
}
