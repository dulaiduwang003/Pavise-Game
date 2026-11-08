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

		private const string MigrationPrefix = "ActionFailure_Migrated_";

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

		// cycleIdentity is a genuine session identity, a newline, then material configuration.
		// A -> B -> A within that session selects the original spent budget rather than refilling it.
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
			// A current-process wildcard failure covers default-cycle targets as well.
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
			string text = Normalize(action) + "\n" + Normalize(target);
			lock (gate)
			{
				string value;
				if (recordKeys.TryGetValue(text, out value))
				{
					return value;
				}
				value = ComputeRecordKey(text);
				if (text.Length <= 2048)
				{
					if (recordKeys.Count >= 512)
					{
						recordKeys.Remove(recordKeyOrder.Dequeue());
					}
					recordKeys.Add(text, value);
					recordKeyOrder.Enqueue(text);
				}
				return value;
			}
		}

		private static string ComputeRecordKey(string identity)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(identity);
			using (SHA256 sHA = SHA256.Create())
			{
				StringBuilder stringBuilder = new StringBuilder();
				byte[] array = sHA.ComputeHash(bytes);
				foreach (byte b in array)
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
				string[] array = value.Split('|');
				long result;
				if (array.Length != 6 || array[0] != "1" || !long.TryParse(array[4], NumberStyles.None, CultureInfo.InvariantCulture, out result))
				{
					return null;
				}
				ActionFailureRecord actionFailureRecord = new ActionFailureRecord();
				actionFailureRecord.Key = key;
				actionFailureRecord.Action = Decode(array[1]);
				actionFailureRecord.Target = Decode(array[2]);
				actionFailureRecord.Environment = Decode(array[3]);
				actionFailureRecord.OccurredUtc = new DateTime(result, DateTimeKind.Utc);
				actionFailureRecord.Reason = Decode(array[5]);
				ActionFailureRecord actionFailureRecord2 = actionFailureRecord;
				if (string.IsNullOrEmpty(actionFailureRecord2.Action) || string.IsNullOrEmpty(actionFailureRecord2.Target) || !string.Equals(RecordKey(actionFailureRecord2.Action, actionFailureRecord2.Target), key, StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}
				return actionFailureRecord2;
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
			ActionFailureRecord value;
			if (volatileFailures.TryGetValue(key, out value))
			{
				return value;
			}
			string value2;
			if (!Settings.TryLoadStr(key, out value2))
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
			if (string.IsNullOrEmpty(value2))
			{
				return null;
			}
			ActionFailureRecord actionFailureRecord2 = Parse(key, value2);
			if (actionFailureRecord2 == null)
			{
				ActionFailureRecord actionFailureRecord3 = new ActionFailureRecord();
				actionFailureRecord3.Key = key;
				actionFailureRecord3.Action = action;
				actionFailureRecord3.Target = target;
				actionFailureRecord3.Environment = CurrentEnvironment();
				actionFailureRecord3.Reason = "Isolation record invalid; retained";
				actionFailureRecord3.PersistenceFailed = true;
				actionFailureRecord2 = actionFailureRecord3;
			}
			return actionFailureRecord2;
		}

		private static string BlockKey(string action, string target)
		{
			string text = RecordKey(action, target);
			if (!admissions.ContainsKey(text))
			{
				RecoverIntent(IntentKey(action, target), action, target);
			}
			if (Read(text, action, target) != null)
			{
				return text;
			}
			string text2 = RecordKey(action, "*");
			if (Read(text2, action, "*") == null)
			{
				return null;
			}
			return text2;
		}

		internal static bool IsBlocked(string action, string target)
		{
			lock (gate)
			{
				MigrateLegacy();
				string text = BlockKey(action, target);
				return Blocked(action, target, text);
			}
		}

		// An admitted apply that has not completed yet. Blocked() also covers it, but it is work in
		// progress, not a stopped action.
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
				string text = BlockKey(action, target);
				return (text == null) ? "" : Read(text, action, target).Reason;
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
				string text = BlockKey(action, target);
				if (Blocked(action, target, text) || (text != null && admissions.ContainsValue(text)))
				{
					reason = text == null ? "Automatic phase stopped or action already running" : Read(text, action, target).Reason;
					return false;
				}
				ActionFailureRecord record = NewRecord(action, target, "Execution admitted; verified completion pending", null);
				Phase(action, target).Attempts++;
				string text2 = Serialize(record);
				string name = IntentKey(action, target);
				string value;
				if (!SaveIntent(action, target, text2) || !Settings.TryLoadStr(name, out value) || value != text2)
				{
					Trip(action, target, "Execution intent could not be persisted; no apply attempted");
					reason = "Execution intent could not be persisted";
					return false;
				}
				admissions[key] = text;
				return true;
			}
		}

		internal static void CancelApply(string action, string target)
		{
			lock (gate)
			{
				string text = RecordKey(action, target);
				string text2;
				if (failedCompletions.Remove(text))
				{
					text2 = admissions.ContainsKey(text) && admissions[text] != null ? admissions[text] : text;
					ActionFailureRecord actionFailureRecord = Read(text2, action, target);
					if (actionFailureRecord != null && actionFailureRecord.PersistenceFailed)
					{
						actionFailureRecord.PersistenceFailed = !Save(text2, Serialize(actionFailureRecord));
						volatileFailures[text2] = actionFailureRecord;
					}
					if (actionFailureRecord != null && !actionFailureRecord.PersistenceFailed)
					{
						if (SaveIntent(action, target, "")) recoveredIntents.Remove(text);
					}
					admissions.Remove(text);
					return;
				}
				ActionFailureRecord actionFailureRecord2 = Read(text, action, target);
				if (actionFailureRecord2 != null && actionFailureRecord2.PersistenceFailed && !admissions.ContainsKey(text))
				{
					return;
				}
				if (!SaveIntent(action, target, ""))
				{
					Trip(action, target, "Cancelled execution intent could not be cleared");
					return;
				}
				admissions.Remove(text);
				recoveredIntents.Remove(text);
			}
		}

		internal static bool CompleteApply(string action, string target)
		{
			lock (gate)
			{
				string text = RecordKey(action, target);
				if (!admissions.ContainsKey(text))
				{
					return false;
				}
				string value = admissions[text];
				bool flag = value != null;
				string value2;
				if (!Settings.TryLoadStr(IntentKey(action, target), out value2) || string.IsNullOrEmpty(value2))
				{
					PreserveCompletionFailure(action, target, flag ? value : text, null, "Verified execution intent unreadable or missing");
					return false;
				}
				if (flag && !Save(value, ""))
				{
					PreserveCompletionFailure(action, target, value, value2, "Verified failure record could not be cleared");
					return false;
				}
				if (!SaveIntent(action, target, ""))
				{
					PreserveCompletionFailure(action, target, flag ? value : text, value2, "Verified execution intent could not be cleared");
					return false;
				}
				admissions.Remove(text);
				failedCompletions.Remove(text);
				// A successful observation does not consume the failure budget or refund earlier failures.
				Phase(action, target).Attempts = Math.Max(0, Phase(action, target).Attempts - 1);
				if (flag)
				{
					volatileFailures.Remove(value);
				}
				return true;
			}
		}

		private static void PreserveCompletionFailure(string action, string target, string failureKey, string intent, string reason)
		{
			string text = RecordKey(action, target);
			string target2 = (string.Equals(text, failureKey, StringComparison.OrdinalIgnoreCase) ? target : "*");
			if (string.IsNullOrEmpty(intent))
			{
				intent = Serialize(NewRecord(action, target, "Completion could not be confirmed; recovery required", null));
			}
			SaveIntent(action, target, intent);
			ActionFailureRecord actionFailureRecord = NewRecord(action, target2, reason, null);
			actionFailureRecord.PersistenceFailed = !Save(failureKey, Serialize(actionFailureRecord));
			volatileFailures[failureKey] = actionFailureRecord;
			failedCompletions.Add(text);
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
				string text = RecordKey(action, target);
				string target2 = target;
				string value;
				if (admissions.TryGetValue(text, out value) && value != null)
				{
					if (!string.Equals(value, text, StringComparison.OrdinalIgnoreCase))
					{
						target2 = "*";
					}
				}
				else if (target != "*" && Read(RecordKey(action, "*"), action, "*") != null)
				{
					target2 = "*";
				}
				bool admitted = admissions.Remove(text);
				failedCompletions.Remove(text);
				text = RecordKey(action, target2);
				ActionFailureRecord previous;
				AutomaticPhase phase = Phase(action, target);
				// Without an admission ticket there is no safely attributable retry allowance.
				// Stop this phase; duplicate logging must not create an unlimited external path.
				phase.Attempts = Math.Max(admitted ? 1 : phase.MaxFailures, phase.Attempts);
				phase.FailureSequence = ++automaticSequence;
				if (!admitted && volatileFailures.TryGetValue(text, out previous) && previous.Reason == reason) return;
				ActionFailureRecord actionFailureRecord = NewRecord(action, target2, reason, environmentContext);
				actionFailureRecord.PersistenceFailed = !Save(text, Serialize(actionFailureRecord));
				volatileFailures[text] = actionFailureRecord;
				if (!actionFailureRecord.PersistenceFailed)
				{
					SaveIntent(action, target, "");
				}
			}
		}

		private static ActionFailureRecord NewRecord(string action, string target, string reason, string environmentContext)
		{
			DateTime utcNow = DateTime.UtcNow;
			ActionFailureRecord actionFailureRecord = new ActionFailureRecord();
			actionFailureRecord.Key = RecordKey(action, target);
			actionFailureRecord.Action = action;
			actionFailureRecord.Target = target;
			actionFailureRecord.Environment = CurrentEnvironment() + " | " + (string.IsNullOrEmpty(environmentContext) ? "GPU/driver context unavailable" : PsRunner.NormalizeError(environmentContext));
			actionFailureRecord.Reason = (string.IsNullOrEmpty(reason) ? "Apply or verification failed" : PsRunner.NormalizeError(reason));
			actionFailureRecord.OccurredUtc = utcNow;
			ActionFailureRecord actionFailureRecord2 = actionFailureRecord;
			if (actionFailureRecord2.Reason.Length > 4096)
			{
				actionFailureRecord2.Reason = actionFailureRecord2.Reason.Substring(0, 4096);
			}
			return actionFailureRecord2;
		}

		private static void RecoverIntent(string intentKey, string action, string target)
		{
			string value;
			if (!Settings.TryLoadStr(intentKey, out value))
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
				if (string.IsNullOrEmpty(value))
				{
					return;
				}
				string key = "ActionFailure_" + intentKey.Substring("ActionAttempt_".Length);
				if (admissions.ContainsKey(key))
				{
					return;
				}
				ActionFailureRecord actionFailureRecord2 = Parse(key, value);
				if (actionFailureRecord2 == null)
				{
					if (!string.IsNullOrEmpty(action) && !string.IsNullOrEmpty(target))
					{
						ActionFailureRecord actionFailureRecord3 = NewRecord(action, target, "Execution intent invalid; apply remains isolated", null);
						actionFailureRecord3.PersistenceFailed = true;
						volatileFailures[actionFailureRecord3.Key] = actionFailureRecord3;
						return;
					}
					throw new InvalidOperationException("Invalid unfinished execution intent: " + intentKey);
				}
				recoveredIntents.Add(key);
				ActionFailureRecord actionFailureRecord4 = Read(key, actionFailureRecord2.Action, actionFailureRecord2.Target) ?? Read(RecordKey(actionFailureRecord2.Action, "*"), actionFailureRecord2.Action, "*");
				if (actionFailureRecord4 == null)
				{
					actionFailureRecord4 = NewRecord(actionFailureRecord2.Action, actionFailureRecord2.Target, "Previous execution was not confirmed; recovery required", actionFailureRecord2.Environment);
					actionFailureRecord4.PersistenceFailed = !Save(key, Serialize(actionFailureRecord4));
					volatileFailures[key] = actionFailureRecord4;
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
				string[] array = names;
				foreach (string intentKey in array)
				{
					RecoverIntent(intentKey, null, null);
				}
				Dictionary<string, ActionFailureRecord> dictionary = new Dictionary<string, ActionFailureRecord>(StringComparer.OrdinalIgnoreCase);
				string[] names2;
				if (!Settings.TryLoadStoredNames("ActionFailure_", out names2))
				{
					throw new InvalidOperationException("Action isolation records could not be read; isolation remains active.");
				}
				string[] array2 = names2;
				foreach (string text in array2)
				{
					if (!text.StartsWith("ActionFailure_Migrated_", StringComparison.OrdinalIgnoreCase))
					{
						ActionFailureRecord actionFailureRecord = Read(text, null, null);
						if (actionFailureRecord != null)
						{
							dictionary[text] = actionFailureRecord;
						}
					}
				}
				foreach (KeyValuePair<string, ActionFailureRecord> volatileFailure in volatileFailures)
				{
					dictionary[volatileFailure.Key] = volatileFailure.Value;
				}
				List<ActionFailureRecord> list = new List<ActionFailureRecord>();
				foreach (ActionFailureRecord value in dictionary.Values)
				{
					list.Add(new ActionFailureRecord
					{
						Key = value.Key,
						Action = value.Action,
						Target = value.Target,
						Environment = value.Environment,
						Reason = value.Reason,
						OccurredUtc = value.OccurredUtc,
						PersistenceFailed = value.PersistenceFailed
					});
				}
				list.Sort((ActionFailureRecord a, ActionFailureRecord b) => b.OccurredUtc.CompareTo(a.OccurredUtc));
				return list;
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
			string text = "ActionFailure_Migrated_" + oldKey;
			string value;
			if (failed && Settings.TryLoadStr(text, out value) && !(value == "1"))
			{
				// Migration is diagnostic history, not a newly attempted execution.
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
					Save(text, "1");
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
			string[] array = names;
			foreach (string name in array)
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
				string[] array = new string[17]
				{
					"do", "wu", "services", "standby", "awake", "dwmboost", "gpupower", "amdalag", "amdafmf", "maint",
					"intelend", "laptopperf", "gamedvr", "mmcss", "pqos", "rsr", "nvvrr"
				};
				string[] array2 = new string[17]
				{
					"GmPauseDl", "GmPauseUpdate", "GmPauseServices", "GmStandbyCleanerV2", "GmAwake", "GmDwmBoost", "GmGpuPowerMax", "AmdAntiLag", "AmdAfmf", "GmPauseMaintenanceV1",
					"GmIntelEnduranceV1", "GmLaptopPerfV1", "GameDvrOff", "GmMmcss", "PresenceQos", "GmRsr", "NvVrrWindowed"
				};
				for (int i = 0; i < array.Length; i++)
				{
					ImportLegacy("EnvFuse_" + array[i], array2[i], Settings.Load("EnvFuse_" + array[i], false));
				}
				ImportLegacy("PowerPlanFailStreak", "PowerPlanOn", CounterFailed("PowerPlanFailStreak"));
				string[] array3 = new string[7] { "NvMaxPerf", "NvLowLat", "NvSmoothMotion", "NvShaderCache", "NvRebar", "NvDlss", "NvAnsel" };
				string[] array4 = new string[7] { "pstate", "prerender", "smooth", "shadercache", "rebarfeat", "dlssovr", "ansel" };
				for (int j = 0; j < array3.Length; j++)
				{
					ImportLegacy("NvFailStreak_" + array4[j], array3[j], CounterFailed("NvFailStreak_" + array4[j]));
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
