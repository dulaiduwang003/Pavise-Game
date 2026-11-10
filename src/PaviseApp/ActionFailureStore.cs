using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
    internal static class ActionFailureStore
    {
        internal const string Prefix = "ActionFailure_";

        internal const string AttemptPrefix = "ActionAttempt_";

        internal const int RecordKeyCacheLimit = 512;

        internal const int RecordKeyCacheMaxChars = 2048;

        private static readonly object gate = new object();

        private static readonly Dictionary<string, string> recordKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Queue<string> recordKeyOrder = new Queue<string>();

        private static readonly Dictionary<string, ActionFailureRecord> volatileFailures = new Dictionary<string, ActionFailureRecord>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> admissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> failedCompletions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> recoveredIntents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool migrated;

        private sealed class AutomaticPhase
        {
            internal int Attempts;
            internal int MaxFailures = 1;
            internal long Sequence;
            internal long FailureSequence;
        }

        private static readonly Dictionary<string, string> automaticCycles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, AutomaticPhase>> automaticPhases = new Dictionary<string, Dictionary<string, AutomaticPhase>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> automaticSessions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static long automaticSequence;

        // cycleIdentity 由真实会话标识、换行和有效配置组成。同一会话内从 A 切到 B 再切回 A 时，继续使用 A 已消耗的预算。

        internal static bool BeginAutomaticCycle(string action, string target, string cycleIdentity, int maxFailures = 1)
        {
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(target) || string.IsNullOrEmpty(cycleIdentity)) return false;
            long session;
            if (!long.TryParse(SessionPart(cycleIdentity), NumberStyles.None, CultureInfo.InvariantCulture, out session) || session < 0) return false;
            if (maxFailures < 1 || maxFailures > 3) throw new ArgumentOutOfRangeException("maxFailures");
            lock (gate)
            {
                string key = RecordKey(action, target), previous;
                MigrateLegacy();
                long seenSession;
                if (automaticSessions.TryGetValue(key, out seenSession) && session < seenSession) return false;
                if (automaticCycles.TryGetValue(key, out previous) && previous == cycleIdentity) return false;
                string failureKey = BlockKey(action, target);
                if (admissions.ContainsKey(key) || (failureKey != null && admissions.ContainsValue(failureKey))) return false;
                automaticCycles[key] = cycleIdentity;
                automaticSessions[key] = session;
                Dictionary<string, AutomaticPhase> phases;
                if (!automaticPhases.TryGetValue(key, out phases)) automaticPhases[key] = phases = new Dictionary<string, AutomaticPhase>(StringComparer.Ordinal);
                if (previous != null && session > seenSession) phases.Clear();
                AutomaticPhase phase;
                if (!phases.TryGetValue(cycleIdentity, out phase)) phases[cycleIdentity] = new AutomaticPhase { MaxFailures = maxFailures, Sequence = ++automaticSequence };
                return true;
            }
        }

        private static string SessionPart(string identity)
        {
            int split = identity.IndexOf('\n');
            return split < 0 ? "" : identity.Substring(0, split);
        }

        private static AutomaticPhase Phase(string action, string target)
        {
            string key = RecordKey(action, target), cycle;
            if (!automaticCycles.TryGetValue(key, out cycle)) cycle = "";
            Dictionary<string, AutomaticPhase> phases;
            if (!automaticPhases.TryGetValue(key, out phases)) automaticPhases[key] = phases = new Dictionary<string, AutomaticPhase>(StringComparer.Ordinal);
            AutomaticPhase phase;
            if (!phases.TryGetValue(cycle, out phase)) phases[cycle] = phase = new AutomaticPhase { Sequence = ++automaticSequence };
            return phase;
        }

        private static bool Blocked(string action, string target, string failureKey)
        {
            if (admissions.ContainsKey(RecordKey(action, target)) || recoveredIntents.Contains(RecordKey(action, target))) return true;
            if (failureKey != null)
            {
                ActionFailureRecord failure = Read(failureKey, action, target);
                if (failure != null && failure.PersistenceFailed) return true;
            }
            AutomaticPhase phase = Phase(action, target);
            if (phase.Attempts >= phase.MaxFailures) return true;
            // 当前进程的通配失败记录也覆盖默认周期中的目标。
            string cycle;
            AutomaticPhase wildcard = Phase(action, "*");
            return target != "*" && failureKey == RecordKey(action, "*") && wildcard.Attempts >= wildcard.MaxFailures
                && (!automaticCycles.TryGetValue(RecordKey(action, target), out cycle) || wildcard.FailureSequence >= phase.Sequence);
        }

        internal static string CurrentEnvironment()
        {
            return Environment.OSVersion.VersionString + " | " + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + " | CPU " + Environment.ProcessorCount;
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim().ToUpperInvariant();
        }

        internal static string RecordKey(string action, string target)
        {
            string normalizeOffset = Normalize(action) + "\n" + Normalize(target);
            lock (gate)
            {
                string recordKey;
                if (recordKeys.TryGetValue(normalizeOffset, out recordKey))
                {
                    return recordKey;
                }
                recordKey = ComputeRecordKey(normalizeOffset);
                if (normalizeOffset.Length <= 2048)
                {
                    if (recordKeys.Count >= 512)
                    {
                        recordKeys.Remove(recordKeyOrder.Dequeue());
                    }
                    recordKeys.Add(normalizeOffset, recordKey);
                    recordKeyOrder.Enqueue(normalizeOffset);
                }
                return recordKey;
            }
        }

        private static string ComputeRecordKey(string identity)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(identity);
            using (SHA256 sHA = SHA256.Create())
            {
                StringBuilder stringBuilder = new StringBuilder();
                foreach (byte b in sHA.ComputeHash(bytes))
                {
                    stringBuilder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return "ActionFailure_" + stringBuilder;
            }
        }

        private static string Encode(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private static string Decode(string text)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }

        private static string IntentKey(string action, string target)
        {
            return "ActionAttempt_" + RecordKey(action, target).Substring("ActionFailure_".Length);
        }

        private static string Serialize(ActionFailureRecord record)
        {
            return "1|" + Encode(record.Action) + "|" + Encode(record.Target) + "|" + Encode(record.Environment) + "|" + record.OccurredUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Encode(record.Reason);
        }

        private static ActionFailureRecord Parse(string key, string value)
        {
            try
            {
                if (value.Length > 32768)
                {
                    return null;
                }
                string[] valueParts = value.Split('|');
                long result;
                if (valueParts.Length != 6 || valueParts[0] != "1" || !long.TryParse(valueParts[4], NumberStyles.None, CultureInfo.InvariantCulture, out result))
                {
                    return null;
                }
                ActionFailureRecord currentActionFailureRecord = new ActionFailureRecord();
                currentActionFailureRecord.Key = key;
                currentActionFailureRecord.Action = Decode(valueParts[1]);
                currentActionFailureRecord.Target = Decode(valueParts[2]);
                currentActionFailureRecord.Environment = Decode(valueParts[3]);
                currentActionFailureRecord.OccurredUtc = new DateTime(result, DateTimeKind.Utc);
                currentActionFailureRecord.Reason = Decode(valueParts[5]);
                if (string.IsNullOrEmpty(currentActionFailureRecord.Action) || string.IsNullOrEmpty(currentActionFailureRecord.Target) || !string.Equals(RecordKey(currentActionFailureRecord.Action, currentActionFailureRecord.Target), key, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return currentActionFailureRecord;
            }
            catch
            {
                return null;
            }
        }

        private static bool Save(string key, string text, bool durable = true)
        {
            string value;
            if ((string.IsNullOrEmpty(text) ? Settings.TryRemove(key, durable) : (durable ? Settings.SaveStrDurable(key, text) : Settings.SaveStr(key, text))) && Settings.TryLoadStr(key, out value))
            {
                return value == (text ?? "");
            }
            return false;
        }

        private static bool DurableIntent(string action)
        {
            return !string.Equals(action, "BackgroundSuppression", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SaveIntent(string action, string target, string text)
        {
            return Save(IntentKey(action, target), text, DurableIntent(action));
        }

        private static ActionFailureRecord Read(string key, string action, string target)
        {
            ActionFailureRecord volatileFailure;
            if (volatileFailures.TryGetValue(key, out volatileFailure))
            {
                return volatileFailure;
            }
            string text;
            if (!Settings.TryLoadStr(key, out text))
            {
                ActionFailureRecord actionFailureRecord = new ActionFailureRecord();
                actionFailureRecord.Key = key;
                actionFailureRecord.Action = action;
                actionFailureRecord.Target = target;
                actionFailureRecord.Environment = CurrentEnvironment();
                actionFailureRecord.Reason = "Isolation record unreadable; retained";
                actionFailureRecord.PersistenceFailed = true;
                return actionFailureRecord;
            }
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            ActionFailureRecord parse = Parse(key, text);
            if (parse == null)
            {
                ActionFailureRecord currentActionFailureRecord = new ActionFailureRecord();
                currentActionFailureRecord.Key = key;
                currentActionFailureRecord.Action = action;
                currentActionFailureRecord.Target = target;
                currentActionFailureRecord.Environment = CurrentEnvironment();
                currentActionFailureRecord.Reason = "Isolation record invalid; retained";
                currentActionFailureRecord.PersistenceFailed = true;
                parse = currentActionFailureRecord;
            }
            return parse;
        }

        private static string BlockKey(string action, string target)
        {
            string recordKeyResult = RecordKey(action, target);
            if (!admissions.ContainsKey(recordKeyResult))
            {
                RecoverIntent(IntentKey(action, target), action, target);
            }
            if (Read(recordKeyResult, action, target) != null)
            {
                return recordKeyResult;
            }
            string recordKeyResult2 = RecordKey(action, "*");
            if (Read(recordKeyResult2, action, "*") == null)
            {
                return null;
            }
            return recordKeyResult2;
        }

        internal static bool IsBlocked(string action, string target)
        {
            lock (gate)
            {
                MigrateLegacy();
                string blockKeyResult = BlockKey(action, target);
                return Blocked(action, target, blockKeyResult);
            }
        }

        // 该操作已获准进入，但尚未完成。Blocked() 也会拦截它；这是进行中的工作，不是已停用动作。

        internal static bool IsRunning(string action, string target)
        {
            lock (gate)
            {
                return admissions.ContainsKey(RecordKey(action, target));
            }
        }

        internal static bool IsIsolated(string action, string target)
        {
            lock (gate)
            {
                MigrateLegacy();
                return BlockKey(action, target) != null;
            }
        }

        internal static string FailureReason(string action, string target)
        {
            lock (gate)
            {
                MigrateLegacy();
                string blockKeyResult = BlockKey(action, target);
                return (blockKeyResult == null) ? "" : Read(blockKeyResult, action, target).Reason;
            }
        }

        internal static bool TryBeginApply(string action, string target, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(target))
            {
                reason = "Missing action identity";
                return false;
            }
            lock (gate)
            {
                MigrateLegacy();
                string key = RecordKey(action, target);
                if (admissions.ContainsKey(key))
                {
                    reason = "Action already running";
                    return false;
                }
                string blockKeyResult = BlockKey(action, target);
                if (Blocked(action, target, blockKeyResult) || (blockKeyResult != null && admissions.ContainsValue(blockKeyResult)))
                {
                    reason = blockKeyResult == null ? "Automatic phase stopped or action already running" : Read(blockKeyResult, action, target).Reason;
                    return false;
                }
                ActionFailureRecord record = NewRecord(action, target, "Execution admitted; verified completion pending", null);
                Phase(action, target).Attempts++;
                string serializeResult = Serialize(record);
                string name = IntentKey(action, target);
                string text;
                if (!SaveIntent(action, target, serializeResult) || !Settings.TryLoadStr(name, out text) || text != serializeResult)
                {
                    Trip(action, target, "Execution intent could not be persisted; no apply attempted");
                    reason = "Execution intent could not be persisted";
                    return false;
                }
                admissions[key] = blockKeyResult;
                return true;
            }
        }

        internal static void CancelApply(string action, string target)
        {
            lock (gate)
            {
                string recordKeyResult = RecordKey(action, target);
                string message;
                if (failedCompletions.Remove(recordKeyResult))
                {
                    message = admissions.ContainsKey(recordKeyResult) && admissions[recordKeyResult] != null ? admissions[recordKeyResult] : recordKeyResult;
                    ActionFailureRecord actionFailureRecord = Read(message, action, target);
                    if (actionFailureRecord != null && actionFailureRecord.PersistenceFailed)
                    {
                        actionFailureRecord.PersistenceFailed = !Save(message, Serialize(actionFailureRecord));
                        volatileFailures[message] = actionFailureRecord;
                    }
                    if (actionFailureRecord != null && !actionFailureRecord.PersistenceFailed)
                    {
                        if (SaveIntent(action, target, "")) recoveredIntents.Remove(recordKeyResult);
                    }
                    admissions.Remove(recordKeyResult);
                    return;
                }
                ActionFailureRecord currentActionFailureRecord = Read(recordKeyResult, action, target);
                if (currentActionFailureRecord != null && currentActionFailureRecord.PersistenceFailed && !admissions.ContainsKey(recordKeyResult))
                {
                    return;
                }
                if (!SaveIntent(action, target, ""))
                {
                    Trip(action, target, "Cancelled execution intent could not be cleared");
                    return;
                }
                admissions.Remove(recordKeyResult);
                recoveredIntents.Remove(recordKeyResult);
            }
        }

        internal static bool CompleteApply(string action, string target)
        {
            lock (gate)
            {
                string recordKeyResult = RecordKey(action, target);
                if (!admissions.ContainsKey(recordKeyResult))
                {
                    return false;
                }
                string admission = admissions[recordKeyResult];
                bool hasRecord = admission != null;
                string text;
                if (!Settings.TryLoadStr(IntentKey(action, target), out text) || string.IsNullOrEmpty(text))
                {
                    PreserveCompletionFailure(action, target, hasRecord ? admission : recordKeyResult, null, "Verified execution intent unreadable or missing");
                    return false;
                }
                if (hasRecord && !Save(admission, ""))
                {
                    PreserveCompletionFailure(action, target, admission, text, "Verified failure record could not be cleared");
                    return false;
                }
                if (!SaveIntent(action, target, ""))
                {
                    PreserveCompletionFailure(action, target, hasRecord ? admission : recordKeyResult, text, "Verified execution intent could not be cleared");
                    return false;
                }
                admissions.Remove(recordKeyResult);
                failedCompletions.Remove(recordKeyResult);
                // 成功观测不消耗失败预算，也不退还此前的失败次数。
                Phase(action, target).Attempts = Math.Max(0, Phase(action, target).Attempts - 1);
                if (hasRecord)
                {
                    volatileFailures.Remove(admission);
                }
                return true;
            }
        }

        private static void PreserveCompletionFailure(string action, string target, string failureKey, string intent, string reason)
        {
            string recordKeyResult = RecordKey(action, target);
            string target2 = (string.Equals(recordKeyResult, failureKey, StringComparison.OrdinalIgnoreCase) ? target : "*");
            if (string.IsNullOrEmpty(intent))
            {
                intent = Serialize(NewRecord(action, target, "Completion could not be confirmed; recovery required", null));
            }
            SaveIntent(action, target, intent);
            ActionFailureRecord actionFailureRecord = NewRecord(action, target2, reason, null);
            actionFailureRecord.PersistenceFailed = !Save(failureKey, Serialize(actionFailureRecord));
            volatileFailures[failureKey] = actionFailureRecord;
            failedCompletions.Add(recordKeyResult);
            AutomaticPhase phase = Phase(action, target);
            phase.Attempts = Math.Max(phase.Attempts, phase.MaxFailures);
            phase.FailureSequence = ++automaticSequence;
        }

        internal static void Trip(string action, string target, string reason, string environmentContext = null)
        {
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(target))
            {
                return;
            }
            lock (gate)
            {
                string recordKeyResult = RecordKey(action, target);
                string target2 = target;
                string admission;
                if (admissions.TryGetValue(recordKeyResult, out admission) && admission != null)
                {
                    if (!string.Equals(admission, recordKeyResult, StringComparison.OrdinalIgnoreCase))
                    {
                        target2 = "*";
                    }
                }
                else if (target != "*" && Read(RecordKey(action, "*"), action, "*") != null)
                {
                    target2 = "*";
                }
                bool admitted = admissions.Remove(recordKeyResult);
                failedCompletions.Remove(recordKeyResult);
                recordKeyResult = RecordKey(action, target2);
                ActionFailureRecord previous;
                AutomaticPhase phase = Phase(action, target);
                // 缺少准入凭据时，无法安全归属重试次数。停止本阶段，避免重复记日志形成无限执行通道。

                phase.Attempts = Math.Max(admitted ? 1 : phase.MaxFailures, phase.Attempts);
                phase.FailureSequence = ++automaticSequence;
                if (!admitted && volatileFailures.TryGetValue(recordKeyResult, out previous) && previous.Reason == reason) return;
                ActionFailureRecord actionFailureRecord = NewRecord(action, target2, reason, environmentContext);
                actionFailureRecord.PersistenceFailed = !Save(recordKeyResult, Serialize(actionFailureRecord));
                volatileFailures[recordKeyResult] = actionFailureRecord;
                if (!actionFailureRecord.PersistenceFailed)
                {
                    SaveIntent(action, target, "");
                }
            }
        }

        private static ActionFailureRecord NewRecord(string action, string target, string reason, string environmentContext)
        {
            DateTime utcNow = DateTime.UtcNow;
            ActionFailureRecord currentActionFailureRecord = new ActionFailureRecord();
            currentActionFailureRecord.Key = RecordKey(action, target);
            currentActionFailureRecord.Action = action;
            currentActionFailureRecord.Target = target;
            currentActionFailureRecord.Environment = CurrentEnvironment() + " | " + (string.IsNullOrEmpty(environmentContext) ? "GPU/driver context unavailable" : PsRunner.NormalizeError(environmentContext));
            currentActionFailureRecord.Reason = (string.IsNullOrEmpty(reason) ? "Apply or verification failed" : PsRunner.NormalizeError(reason));
            currentActionFailureRecord.OccurredUtc = utcNow;
            if (currentActionFailureRecord.Reason.Length > 4096)
            {
                currentActionFailureRecord.Reason = currentActionFailureRecord.Reason.Substring(0, 4096);
            }
            return currentActionFailureRecord;
        }

        private static void RecoverIntent(string intentKey, string action, string target)
        {
            string text;
            if (!Settings.TryLoadStr(intentKey, out text))
            {
                if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(target))
                {
                    throw new InvalidOperationException("Unfinished execution intent unreadable: " + intentKey);
                }
                ActionFailureRecord actionFailureRecord = NewRecord(action, target, "Execution intent unreadable; apply remains isolated", null);
                actionFailureRecord.PersistenceFailed = true;
                volatileFailures[actionFailureRecord.Key] = actionFailureRecord;
            }
            else
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }
                string key = "ActionFailure_" + intentKey.Substring("ActionAttempt_".Length);
                if (admissions.ContainsKey(key))
                {
                    return;
                }
                ActionFailureRecord parse = Parse(key, text);
                if (parse == null)
                {
                    if (!string.IsNullOrEmpty(action) && !string.IsNullOrEmpty(target))
                    {
                        ActionFailureRecord newRecord = NewRecord(action, target, "Execution intent invalid; apply remains isolated", null);
                        newRecord.PersistenceFailed = true;
                        volatileFailures[newRecord.Key] = newRecord;
                        return;
                    }
                    throw new InvalidOperationException("Invalid unfinished execution intent: " + intentKey);
                }
                recoveredIntents.Add(key);
                ActionFailureRecord currentActionFailureRecord = Read(key, parse.Action, parse.Target) ?? Read(RecordKey(parse.Action, "*"), parse.Action, "*");
                if (currentActionFailureRecord == null)
                {
                    currentActionFailureRecord = NewRecord(parse.Action, parse.Target, "Previous execution was not confirmed; recovery required", parse.Environment);
                    currentActionFailureRecord.PersistenceFailed = !Save(key, Serialize(currentActionFailureRecord));
                    volatileFailures[key] = currentActionFailureRecord;
                }
            }
        }

        internal static IList<ActionFailureRecord> Records()
        {
            lock (gate)
            {
                MigrateLegacy();
                string[] names;
                if (!Settings.TryLoadStoredNames("ActionAttempt_", out names))
                {
                    throw new InvalidOperationException("Unfinished execution intents could not be read; isolation remains active.");
                }
                foreach (string intentKey in names)
                {
                    RecoverIntent(intentKey, null, null);
                }
                Dictionary<string, ActionFailureRecord> actionFailureRecordsByEntryText = new Dictionary<string, ActionFailureRecord>(StringComparer.OrdinalIgnoreCase);
                string[] names2;
                if (!Settings.TryLoadStoredNames("ActionFailure_", out names2))
                {
                    throw new InvalidOperationException("Action isolation records could not be read; isolation remains active.");
                }
                foreach (string entryText in names2)
                {
                    if (!entryText.StartsWith("ActionFailure_Migrated_", StringComparison.OrdinalIgnoreCase))
                    {
                        ActionFailureRecord actionFailureRecord = Read(entryText, null, null);
                        if (actionFailureRecord != null)
                        {
                            actionFailureRecordsByEntryText[entryText] = actionFailureRecord;
                        }
                    }
                }
                foreach (KeyValuePair<string, ActionFailureRecord> volatileFailure in volatileFailures)
                {
                    actionFailureRecordsByEntryText[volatileFailure.Key] = volatileFailure.Value;
                }
                List<ActionFailureRecord> actionFailureRecords = new List<ActionFailureRecord>();
                foreach (ActionFailureRecord failureRecord in actionFailureRecordsByEntryText.Values)
                {
                    actionFailureRecords.Add(new ActionFailureRecord
                    {
                        Key = failureRecord.Key,
                        Action = failureRecord.Action,
                        Target = failureRecord.Target,
                        Environment = failureRecord.Environment,
                        Reason = failureRecord.Reason,
                        OccurredUtc = failureRecord.OccurredUtc,
                        PersistenceFailed = failureRecord.PersistenceFailed
                    });
                }
                actionFailureRecords.Sort((ActionFailureRecord a, ActionFailureRecord b) => b.OccurredUtc.CompareTo(a.OccurredUtc));
                return actionFailureRecords;
            }
        }

        internal static void RegisterLegacy(string action, string target, string reason)
        {
            lock (gate)
            {
                if (BlockKey(action, target) == null)
                {
                    Trip(action, target, reason);
                }
            }
        }

        private static void ImportLegacy(string oldKey, string action, bool failed)
        {
            string oldKeyOffset = "ActionFailure_Migrated_" + oldKey;
            string text;
            if (failed && Settings.TryLoadStr(oldKeyOffset, out text) && text != "1")
            {
                // 迁移记录只补充诊断历史，不算一次新的执行尝试。
                string key = RecordKey(action, "*");
                if (Read(key, action, "*") == null)
                {
                    ActionFailureRecord imported = NewRecord(action, "*", "Legacy failure isolation | " + oldKey, null);
                    imported.PersistenceFailed = !Save(key, Serialize(imported));
                    volatileFailures[key] = imported;
                }
                ActionFailureRecord actionFailureRecord = Read(RecordKey(action, "*"), action, "*");
                if (actionFailureRecord != null && !actionFailureRecord.PersistenceFailed)
                {
                    Save(oldKeyOffset, "1");
                }
            }
        }

        private static void PruneEmptyRecords(string prefix)
        {
            string[] names;
            if (!Settings.TryLoadStoredNames(prefix, out names))
            {
                return;
            }
            foreach (string name in names)
            {
                string value;
                if (Settings.TryLoadStr(name, out value) && value.Length == 0)
                {
                    Settings.TryRemove(name, false);
                }
            }
        }

        private static void MigrateLegacy()
        {
            if (!migrated)
            {
                migrated = true;
                PruneEmptyRecords("ActionAttempt_");
                PruneEmptyRecords("ActionFailure_");
                string[] legacyActionIds = new string[17]
                {
                    "do", "wu", "services", "standby", "awake", "dwmboost", "gpupower", "amdalag", "amdafmf", "maint",
                    "intelend", "laptopperf", "gamedvr", "mmcss", "pqos", "rsr", "nvvrr"
                };
                string[] legacySettingKeys = new string[17]
                {
                    "GmPauseDl", "GmPauseUpdate", "GmPauseServices", "GmStandbyCleanerV2", "GmAwake", "GmDwmBoost", "GmGpuPowerMax", "AmdAntiLag", "AmdAfmf", "GmPauseMaintenanceV1",
                    "GmIntelEnduranceV1", "GmLaptopPerfV1", "GameDvrOff", "GmMmcss", "PresenceQos", "GmRsr", "NvVrrWindowed"
                };
                for (int i = 0; i < legacyActionIds.Length; i++)
                {
                    ImportLegacy("EnvFuse_" + legacyActionIds[i], legacySettingKeys[i], Settings.Load("EnvFuse_" + legacyActionIds[i], false));
                }
                ImportLegacy("PowerPlanFailStreak", "PowerPlanOn", CounterFailed("PowerPlanFailStreak"));
                string[] legacyNvSettingKeys = new string[7] { "NvMaxPerf", "NvLowLat", "NvSmoothMotion", "NvShaderCache", "NvRebar", "NvDlss", "NvAnsel" };
                string[] legacyNvActionIds = new string[7] { "pstate", "prerender", "smooth", "shadercache", "rebarfeat", "dlssovr", "ansel" };
                for (int j = 0; j < legacyNvSettingKeys.Length; j++)
                {
                    ImportLegacy("NvFailStreak_" + legacyNvActionIds[j], legacyNvSettingKeys[j], CounterFailed("NvFailStreak_" + legacyNvActionIds[j]));
                }
            }
        }

        private static bool CounterFailed(string key)
        {
            int result;
            if (int.TryParse(Settings.LoadStr(key, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return result > 0;
            }
            return false;
        }
    }
}
