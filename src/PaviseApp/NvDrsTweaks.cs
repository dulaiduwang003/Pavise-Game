using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PaviseApp
{
	internal static class NvDrsTweaks
	{
		internal const string ListKey = "NvDrsList";

		private const string SnapPrefix = "NvDrs_";

		public const string KeyPState = "pstate";

		public const string KeyFrl = "frl";

		public const string KeyPreRender = "prerender";

		public const string KeyLowLatCpl = "lowlatcpl";

		public const string KeyUllEnable = "ullenable";

		public const string KeySmooth = "smooth";

		public const string KeyShaderCache = "shadercache";

		public const string KeyAnsel = "ansel";

		public const string KeyRebarFeat = "rebarfeat";

		public const string KeyRebarOpt = "rebaropt";

		public const string KeyRebarSize = "rebarsize";

		public const string KeyDlssOvr = "dlssovr";

		public const string KeyDlssPreset = "dlsspreset";

		public const string KeyForceP2 = "forcep2";

		public const string KeyShaderOn = "shaderon";

		public const string KeyVrrApp = "vrrapp";

		internal const string UllUnsupKey = "NvUllUnsupDrv";

		private const char AppliedSep = '~';

		internal const string DriverFailureAction = "NvDriverProfile";

		private const string InvalidJournalReason = "NVIDIA recovery journal is unreadable or invalid";

		private static readonly INvDrsDriver Driver = new NativeNvDrsDriver();

		public static readonly string[] RebarKeys = new string[3] { "rebarfeat", "rebaropt", "rebarsize" };

		public static readonly string[] DlssKeys = new string[2] { "dlssovr", "dlsspreset" };

		public static readonly string[] UltraKeys = new string[3] { "prerender", "ullenable", "lowlatcpl" };

		private static readonly object sync = GameNvNativeGate.SyncRoot;

		private static bool dlssGateLogged;

		private static bool smoothGateLogged;

		private static readonly Dictionary<string, long> restoreRetryAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

		private static readonly Dictionary<string, int> restoreFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		private static readonly Dictionary<string, string> automaticPhases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, long> automaticSessions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		private static long automaticSession;

		internal static void BeginAutomaticSession(long session)
		{
			lock (sync)
			{
				if (session <= automaticSession) return;
				automaticSession = session;
				foreach (string name in new List<string>(automaticPhases.Keys))
				{
					string phase = automaticPhases[name];
					automaticPhases[name] = session + phase.Substring(phase.IndexOf(':'));
					automaticSessions[name] = session;
				}
			}
		}

		// A configuration keeps its budget when it is revisited in the same real game session.
		internal static void SyncAutomaticPhase(string executable, long session, int configuration)
		{
			if (string.IsNullOrEmpty(executable)) return;
			lock (sync)
			{
				if (session < automaticSession) return;
				BeginAutomaticSession(session);
				string name = Path.GetFileName(executable);
				long previous;
				if (automaticSessions.TryGetValue(name, out previous) && session < previous) return;
				automaticSessions[name] = session;
				automaticPhases[name] = session + ":" + configuration;
			}
		}

		internal static object SessionGate
		{
			get
			{
				return sync;
			}
		}

		private static void IsolateInvalidJournal(string executable)
		{
			// Preserve the original failure and recovery receipt while the same fault is isolated.
			if (ActionFailureStore.IsBlocked(DriverFailureAction, executable)
				&& ActionFailureStore.FailureReason(DriverFailureAction, executable) == InvalidJournalReason) return;
			ActionFailureStore.Trip(DriverFailureAction, executable, InvalidJournalReason,
				"NVIDIA DRS | GPU/driver context unavailable");
		}

		public static bool HasGameResidue
		{
			get
			{
				return Settings.LoadStr("NvDrsList", "").Length > 0;
			}
		}

		internal static bool PlanSatisfied(string executable, NvGamePlan plan)
		{
			if (string.IsNullOrEmpty(executable) || plan == null || plan.Empty) return false;
			lock (sync)
			{
				string name = Path.GetFileName(executable);
				List<KeyValuePair<string, uint>> desired = BuildDesired(plan);
				if (desired.Count == 0 || ActionFailureStore.IsBlocked(DriverFailureAction, name)) return false;
				foreach (var setting in desired) if (ActionFailureStore.IsBlocked(FailureActionFor(setting.Key), name)) return false;
				return Settings.LoadStr(SatKey(name), "") == SatSignature(desired);
			}
		}

		private static uint CurrentDriverVersion()
		{
			return NvApi.DriverVersion();
		}

		internal static uint SettingIdOf(string key)
		{
			switch (key)
			{
			case "pstate":
				return 274197361u;
			case "frl":
				return 277041154u;
			case "prerender":
				return 8102046u;
			case "lowlatcpl":
				return 390467u;
			case "ullenable":
				return 277041152u;
			case "smooth":
				return 2966652096u;
			case "shadercache":
				return 11306135u;
			case "ansel":
				return 271965065u;
			case "rebarfeat":
				return 983226u;
			case "rebaropt":
				return 983227u;
			case "rebarsize":
				return 983295u;
			case "dlssovr":
				return 283385345u;
			case "dlsspreset":
				return 283385331u;
			case "forcep2":
				return 1343646814u;
			case "shaderon":
				return 1675263u;
			case "vrrapp":
				return 279476687u;
			default:
				return 0u;
			}
		}

		private static int TryGetDwordOf(IntPtr session, IntPtr profile, string key, out uint value)
		{
			uint settingIdOfResult = SettingIdOf(key);
			if (settingIdOfResult == 0)
			{
				value = 0u;
				return -1;
			}
			return Driver.TryGetDword(session, profile, settingIdOfResult, out value);
		}

		private static bool SetDwordOf(IntPtr session, IntPtr profile, string key, uint value, out int status)
		{
			uint settingIdOfResult = SettingIdOf(key);
			if (settingIdOfResult == 0)
			{
				status = 0;
				return false;
			}
			return Driver.SetDword(session, profile, settingIdOfResult, value, out status);
		}

		private static bool DeleteSettingOf(IntPtr session, IntPtr profile, string key)
		{
			uint settingIdOfResult = SettingIdOf(key);
			if (settingIdOfResult == 0)
			{
				return false;
			}
			return Driver.DeleteSetting(session, profile, settingIdOfResult);
		}

		internal static bool IsDlssCapableName(string gpuName)
		{
			if (string.IsNullOrEmpty(gpuName))
			{
				return false;
			}
			return Regex.IsMatch(gpuName, "\\bRTX\\b", RegexOptions.IgnoreCase);
		}

		public static bool DlssGpuCapable()
		{
			try
			{
				GpuAdapter[] adaptersResult = GpuInventory.Adapters();
				foreach (GpuAdapter gpuAdapter in adaptersResult)
				{
					if (gpuAdapter.Vendor == GpuVendor.Nvidia && IsDlssCapableName(gpuAdapter.Name))
					{
						return true;
					}
				}
			}
			catch
			{
			}
			return false;
		}

		public static bool DlssDriverSupported()
		{
			if (Driver.Available)
			{
				return CurrentDriverVersion() >= 56614;
			}
			return false;
		}

		public static bool DlssOverrideSupported()
		{
			if (DlssDriverSupported())
			{
				return DlssGpuCapable();
			}
			return false;
		}

		internal static bool IsSmoothMotionCapableName(string gpuName)
		{
			return SmoothMotionMinDriverForName(gpuName) != 0;
		}

		internal static uint SmoothMotionMinDriverForName(string gpuName)
		{
			if (string.IsNullOrEmpty(gpuName))
			{
				return 0u;
			}
			if (Regex.IsMatch(gpuName, "\\bRTX\\s*5\\d{3}\\b", RegexOptions.IgnoreCase))
			{
				return 57186u;
			}
			if (!Regex.IsMatch(gpuName, "\\bRTX\\s*4\\d{3}\\b", RegexOptions.IgnoreCase))
			{
				return 0u;
			}
			return 58108u;
		}

		public static bool SmoothMotionGpuCapable()
		{
			return SmoothMotionMinDriver() != 0;
		}

		internal static uint SmoothMotionMinDriver()
		{
			uint smoothMotionMinDriverResult = 0u;
			try
			{
				GpuAdapter[] adaptersResult = GpuInventory.Adapters();
				foreach (GpuAdapter gpuAdapter in adaptersResult)
				{
					if (gpuAdapter.Vendor == GpuVendor.Nvidia && !string.IsNullOrEmpty(gpuAdapter.Name))
					{
						uint smoothMotionMinDriverForNameResult = SmoothMotionMinDriverForName(gpuAdapter.Name);
						if (smoothMotionMinDriverForNameResult != 0 && (smoothMotionMinDriverResult == 0 || smoothMotionMinDriverForNameResult < smoothMotionMinDriverResult))
						{
							smoothMotionMinDriverResult = smoothMotionMinDriverForNameResult;
						}
					}
				}
			}
			catch
			{
			}
			return smoothMotionMinDriverResult;
		}

		public static bool SmoothMotionSupported()
		{
			uint smoothMotionMinDriverResult = SmoothMotionMinDriver();
			if (smoothMotionMinDriverResult == 0 || !Driver.Available)
			{
				return false;
			}
			return IsSmoothMotionSupported(smoothMotionMinDriverResult, true, CurrentDriverVersion(), NvApi.SmoothMotionSettingSupported);
		}

		internal static bool IsSmoothMotionSupported(uint minDriver, bool available, uint driverVersion, Func<bool> settingSupported)
		{
			if (minDriver == 0 || !available || driverVersion < minDriver || settingSupported == null)
			{
				return false;
			}
			try
			{
				return settingSupported();
			}
			catch
			{
				return false;
			}
		}

		internal static List<KeyValuePair<string, uint>> BuildDesired(NvGamePlan plan)
		{
			List<KeyValuePair<string, uint>> list = new List<KeyValuePair<string, uint>>();
			if (plan == null)
			{
				return list;
			}
			if (plan.MaxPerf)
			{
				list.Add(new KeyValuePair<string, uint>("pstate", 1u));
				if (!plan.Automatic)
				{
					list.Add(new KeyValuePair<string, uint>("forcep2", 0u));
				}
			}
			string lowLatMode = plan.LowLatMode;
			if (lowLatMode == "on" || lowLatMode == "ultra")
			{
				list.Add(new KeyValuePair<string, uint>("prerender", 1u));
				if (lowLatMode == "ultra" && !UltraDriverGone())
				{
					list.Add(new KeyValuePair<string, uint>("ullenable", 1u));
					list.Add(new KeyValuePair<string, uint>("lowlatcpl", 2u));
				}
			}
			if (plan.SmoothMotion)
			{
				if (SmoothMotionSupported())
				{
					list.Add(new KeyValuePair<string, uint>("smooth", 1u));
				}
				else if (!smoothGateLogged)
				{
					smoothGateLogged = true;
					Logger.Log((!SmoothMotionGpuCapable()) ? Lang.T("log.nvdrstweaks.1") : ((CurrentDriverVersion() >= SmoothMotionMinDriver()) ? (Lang.T("set.nvsmooth.unavailable") + Lang.T("log.nvdrstweaks.4")) : (Lang.T("log.nvdrstweaks.2") + FormatDriver(SmoothMotionMinDriver()) + Lang.T("log.nvdrstweaks.3") + FormatDriver(CurrentDriverVersion()) + Lang.T("log.nvdrstweaks.4"))));
				}
			}
			if (plan.ShaderCacheMax)
			{
				list.Add(new KeyValuePair<string, uint>("shaderon", 1u));
				list.Add(new KeyValuePair<string, uint>("shadercache", uint.MaxValue));
			}
			if (plan.WindowedVrr)
			{
				list.Add(new KeyValuePair<string, uint>("vrrapp", 0u));
			}
			if (plan.Rebar)
			{
				list.Add(new KeyValuePair<string, uint>("rebarfeat", 1u));
				list.Add(new KeyValuePair<string, uint>("rebaropt", 1u));
				list.Add(new KeyValuePair<string, uint>("rebarsize", 1073741824u));
			}
			string dlssMode = plan.DlssMode;
			switch (dlssMode)
			{
			case "latest":
			case "j":
			case "k":
				if (DlssOverrideSupported())
				{
					list.Add(new KeyValuePair<string, uint>("dlssovr", 1u));
					list.Add(new KeyValuePair<string, uint>("dlsspreset", (dlssMode == "j") ? 10u : ((dlssMode == "k") ? 11u : 16777215u)));
				}
				else if (!dlssGateLogged)
				{
					dlssGateLogged = true;
					Logger.Log((!DlssGpuCapable()) ? Lang.T("log.nvdrstweaks.5") : (Lang.T("log.nvdrstweaks.6") + FormatDriver(CurrentDriverVersion()) + Lang.T("log.nvdrstweaks.4")));
				}
				break;
			}
			return list;
		}

		internal static string FormatDriver(uint version)
		{
			if (version == 0)
			{
				return Lang.T("t.nvdrstweaks.7");
			}
			return version / 100 + "." + (version % 100).ToString("00");
		}

		private static string SatKey(string exeName)
		{
			return "NvDrsSat_" + exeName;
		}

		internal static bool UltraDriverGone()
		{
			return Settings.LoadStr("NvUllUnsupDrv", "") == "d" + CurrentDriverVersion();
		}

		private static void MarkUltraGone()
		{
			string currentDriverVersionOffset = "d" + CurrentDriverVersion();
			if (!(Settings.LoadStr("NvUllUnsupDrv", "") == currentDriverVersionOffset))
			{
				Settings.SaveStr("NvUllUnsupDrv", currentDriverVersionOffset);
				Logger.Log(Lang.T("log.nvdrstweaks.48"));
			}
		}

		internal static string SnapOrig(string stored)
		{
			int matchIndex = stored.IndexOf('~');
			if (matchIndex >= 0)
			{
				return stored.Substring(0, matchIndex);
			}
			return stored;
		}

		internal static bool TrySnapApplied(string stored, out uint applied)
		{
			applied = 0u;
			int matchIndex = stored.IndexOf('~');
			if (matchIndex < 0)
			{
				return false;
			}
			string substringResult = stored.Substring(matchIndex + 1);
			int matchIndex2 = substringResult.IndexOf(',');
			return uint.TryParse((matchIndex2 < 0) ? substringResult : substringResult.Substring(0, matchIndex2), out applied);
		}

		private static bool MatchesApplied(string stored, uint current)
		{
			int matchIndex = stored.IndexOf('~');
			if (matchIndex < 0)
			{
				return false;
			}
			string[] substringParts = stored.Substring(matchIndex + 1).Split(',');
			foreach (string s in substringParts)
			{
				uint result;
				if (uint.TryParse(s, out result) && result == current)
				{
					return true;
				}
			}
			return false;
		}

		private static string PrepareWriteReceipt(string stored, uint desired)
		{
			string desiredText = desired.ToString();
			uint applied;
			if (TrySnapApplied(stored, out applied) && applied != desired)
			{
				desiredText = desiredText + "," + applied;
			}
			return SnapOrig(stored) + '~' + desiredText;
		}

		private static string SatSignature(List<KeyValuePair<string, uint>> desired)
		{
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, uint> item in desired)
			{
				list.Add(item.Key + "=" + item.Value);
			}
			list.Sort(StringComparer.Ordinal);
			return "d" + CurrentDriverVersion() + "|" + string.Join(",", list.ToArray());
		}

		public static List<string> ApplyForGame(string exePath, NvGamePlan plan)
		{
			bool retryLater;
			return ApplyForGame(exePath, plan, out retryLater);
		}

		private static long RestoreClock()
		{
			return AutoCores.NowTicks;
		}

		internal static string FailureActionFor(string key)
		{
			switch (key)
			{
			case "pstate":
			case "forcep2":
				return "NvMaxPerf";
			case "prerender":
			case "ullenable":
			case "lowlatcpl":
				return "NvLowLat";
			case "shadercache":
			case "shaderon":
				return "NvShaderCache";
			case "smooth":
				return "NvSmoothMotion";
			case "rebarfeat":
			case "rebaropt":
			case "rebarsize":
				return "NvRebar";
			case "dlssovr":
			case "dlsspreset":
				return "NvDlss";
			case "vrrapp":
				return "NvVrrWindowed";
			default:
				return "NvSetting_" + key;
			}
		}

		private static string RestoreKey(string exeName, string action)
		{
			string phase;
			if (!automaticPhases.TryGetValue(exeName, out phase))
			{
				phase = automaticSession + ":startup";
				automaticPhases[exeName] = phase;
				automaticSessions[exeName] = automaticSession;
			}
			return exeName + "|" + phase + "|" + action;
		}

		private static void ScheduleRestore(string exeName, string action)
		{
			string key = RestoreKey(exeName, action);
			int value;
			restoreFailures.TryGetValue(key, out value);
			value = Math.Min(value + 1, 3);
			restoreFailures[key] = value;
			restoreRetryAt[key] = RestoreClock() + TimeSpan.FromSeconds(4 << value - 1).Ticks;
			ReportRestoreSchedule(exeName, action);
			if (value == 3) Logger.Log("NVIDIA recovery paused after 3 failures: " + exeName + " / " + action + "; recovery receipt retained");
		}

		private static bool RestoreExhausted(string exeName, string action)
		{
			int failures;
			return restoreFailures.TryGetValue(RestoreKey(exeName, action), out failures) && failures >= 3;
		}

		private static bool RestoreDue(string exeName, string action)
		{
			if (RestoreExhausted(exeName, action)) { ReportRestoreSchedule(exeName, action); return false; }
			long value;
			if (restoreRetryAt.TryGetValue(RestoreKey(exeName, action), out value))
			{
				if (RestoreClock() < value) { ReportRestoreSchedule(exeName, action); return false; }
			}
			return true;
		}

        private static void ReportRestoreSchedule(string executable, string action)
        {
            if (RestoreExhausted(executable, action)) { RecoveryAttemptTrace.Exhausted(); return; }
            long due;
            if (restoreRetryAt.TryGetValue(RestoreKey(executable, action), out due))
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(Math.Max(0, due - RestoreClock())));
        }

		private static bool SaveGameSnapshot(string exeName, Dictionary<string, string> snapshot)
		{
			if (!Settings.SaveStrDurable("NvDrs_" + exeName, SerializeSnapshot(snapshot)))
			{
				return false;
			}
			if (snapshot.Count > 0)
			{
				return AddToList(exeName);
			}
			List<string> list = new List<string>(Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries));
			list.RemoveAll((string game) => string.Equals(game, exeName, StringComparison.OrdinalIgnoreCase));
			return Settings.SaveStrDurable("NvDrsList", string.Join(";", list.ToArray()));
		}

		private static bool RestoreGameKeys(IntPtr session, IntPtr profile, string exeName, Dictionary<string, string> snapshot, List<string> keys, string action, bool force)
		{
			if (keys.Count == 0)
			{
				return true;
			}
			if (RestoreExhausted(exeName, action) || (!force && !RestoreDue(exeName, action)))
			{
				ReportRestoreSchedule(exeName, action);
				return false;
			}
			List<string> list = new List<string>();
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			bool restoreGameKeysReady = false;
			bool restoreGameKeysSucceeded = true;
			try
			{
				foreach (string key in keys)
				{
					string value;
					if (!snapshot.TryGetValue(key, out value))
					{
						continue;
					}
					uint value2;
					int dwordOf = TryGetDwordOf(session, profile, key, out value2);
					if (dwordOf < 0)
					{
						restoreGameKeysSucceeded = false;
						continue;
					}
					uint applied;
					if (!force && TrySnapApplied(value, out applied) && (dwordOf == 0 || (dwordOf == 1 && !MatchesApplied(value, value2))))
					{
						list.Add(key);
						hashSet.Add(key);
						continue;
					}
					string snapOrigResult = SnapOrig(value);
					bool matchesValue2;
					if (!(snapOrigResult == "absent"))
					{
						if (dwordOf != 1)
						{
							goto VerifyAppliedSnapshot;
						}
						matchesValue2 = value2 == ParseUInt(snapOrigResult);
					}
					else
					{
						matchesValue2 = dwordOf == 0;
					}
					if (matchesValue2)
					{
						list.Add(key);
						continue;
					}
					goto VerifyAppliedSnapshot;
					VerifyAppliedSnapshot:
					if (!force && (key == "pstate" || key == "forcep2") && !TrySnapApplied(value, out applied))
					{
						restoreGameKeysSucceeded = false;
						continue;
					}
					bool restoreDwordSucceeded;
					if (!(snapOrigResult == "absent"))
					{
						restoreDwordSucceeded = RestoreDword(session, profile, key, snapOrigResult);
					}
					else
					{
						if (dwordOf == 0)
						{
							goto RecordRestoredSetting;
						}
						restoreDwordSucceeded = DeleteSettingOf(session, profile, key);
					}
					if (!restoreDwordSucceeded)
					{
						restoreGameKeysSucceeded = false;
						continue;
					}
					goto RecordRestoredSetting;
					RecordRestoredSetting:
					list.Add(key);
					restoreGameKeysReady = true;
				}
				if (restoreGameKeysReady && !Driver.SaveSession(session))
				{
					ScheduleRestore(exeName, action);
					return false;
				}
				List<string> list2 = new List<string>();
				foreach (string item in list)
				{
					string stored = snapshot[item];
					uint value3;
					int dwordOf2 = TryGetDwordOf(session, profile, item, out value3);
					string snapOrigResult2 = SnapOrig(stored);
					if (!hashSet.Contains(item))
					{
						bool matchesValue3;
						if (!(snapOrigResult2 == "absent"))
						{
							if (dwordOf2 != 1)
							{
								goto RecordRestoreFailure;
							}
							matchesValue3 = value3 == ParseUInt(snapOrigResult2);
						}
						else
						{
							matchesValue3 = dwordOf2 == 0;
						}
						if (!matchesValue3)
						{
							goto RecordRestoreFailure;
						}
					}
					list2.Add(item);
					continue;
					RecordRestoreFailure:
					restoreGameKeysSucceeded = false;
				}
				Dictionary<string, string> dictionary = new Dictionary<string, string>(snapshot, StringComparer.OrdinalIgnoreCase);
				foreach (string item2 in list2)
				{
					dictionary.Remove(item2);
				}
				if (!SaveGameSnapshot(exeName, dictionary))
				{
					ScheduleRestore(exeName, action);
					return false;
				}
				foreach (string item3 in list2)
				{
					snapshot.Remove(item3);
				}
				Settings.SaveStr(SatKey(exeName), "");
				if (restoreGameKeysSucceeded)
				{
					if (action == DriverFailureAction ? snapshot.Count == 0 : SnapshotKeysFor(snapshot, action).Count == 0)
					{
						restoreFailures.Remove(RestoreKey(exeName, action));
						restoreRetryAt.Remove(RestoreKey(exeName, action));
					}
				}
				else
				{
					ScheduleRestore(exeName, action);
				}
				return restoreGameKeysSucceeded;
			}
			catch
			{
				ScheduleRestore(exeName, action);
				return false;
			}
		}

		private static List<string> SnapshotKeysFor(Dictionary<string, string> snapshot, string action)
		{
			List<string> list = new List<string>();
			foreach (string key in snapshot.Keys)
			{
				if (FailureActionFor(key) == action)
				{
					list.Add(key);
				}
			}
			return list;
		}

		private static bool TryReadGameSnapshot(string exeName, out Dictionary<string, string> snapshot)
		{
			snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			string value;
			if (!Settings.TryLoadStr("NvDrs_" + exeName, out value) || value == null || value.Length > 262144)
			{
				return false;
			}
			string[] valueParts = value.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string entryText in valueParts)
			{
				int matchIndex = entryText.IndexOf('=');
				if (matchIndex <= 0 || matchIndex == entryText.Length - 1)
				{
					return false;
				}
				string key = entryText.Substring(0, matchIndex);
				string substringResult = entryText.Substring(matchIndex + 1);
				uint result;
				if (snapshot.ContainsKey(key) || SettingIdOf(key) == 0 || (SnapOrig(substringResult) != "absent" && !uint.TryParse(SnapOrig(substringResult), out result)) || (substringResult.IndexOf('~') >= 0 && !TrySnapApplied(substringResult, out result)))
				{
					return false;
				}
				if (substringResult.IndexOf('~') >= 0)
				{
					string[] substringParts = substringResult.Substring(substringResult.IndexOf('~') + 1).Split(',');
					if (substringParts.Length > 2)
					{
						return false;
					}
					string[] textEntries = substringParts;
					foreach (string s in textEntries)
					{
						if (!uint.TryParse(s, out result))
						{
							return false;
						}
					}
				}
				snapshot[key] = substringResult;
			}
			return true;
		}

		public static List<string> ApplyForGame(string exePath, NvGamePlan plan, out bool retryLater)
		{
			retryLater = false;
			if (string.IsNullOrEmpty(exePath))
			{
				return null;
			}
			string fileName = Path.GetFileName(exePath);
			if (string.IsNullOrEmpty(fileName))
			{
				return null;
			}
			lock (sync)
			{
				if (!RestoreDue(fileName, DriverFailureAction)) { retryLater = !RestoreExhausted(fileName, DriverFailureAction); return null; }
				try
				{
					if (!Driver.Available)
					{
						string raw;
						bool ownsRecovery = !Settings.TryLoadStr(SnapPrefix + fileName, out raw) || raw.Length > 0;
						if (ownsRecovery) ScheduleRestore(fileName, DriverFailureAction);
						retryLater = ownsRecovery || !NvidiaAbsent();
						return null;
					}
				}
				catch
				{
					string raw;
					if (!Settings.TryLoadStr(SnapPrefix + fileName, out raw) || raw.Length > 0) ScheduleRestore(fileName, DriverFailureAction);
					throw;
				}
				List<KeyValuePair<string, uint>> list = ((plan == null || plan.Empty) ? new List<KeyValuePair<string, uint>>() : BuildDesired(plan));
				Dictionary<string, string> snapshot;
				if (!TryReadGameSnapshot(fileName, out snapshot))
				{
					IsolateInvalidJournal(fileName);
					ScheduleRestore(fileName, DriverFailureAction);
					retryLater = true;
					return null;
				}
				if (list.Count == 0 && snapshot.Count == 0)
				{
					return null;
				}
				Dictionary<string, List<KeyValuePair<string, uint>>> dictionary = new Dictionary<string, List<KeyValuePair<string, uint>>>(StringComparer.OrdinalIgnoreCase);
				foreach (KeyValuePair<string, uint> item in list)
				{
					string key = FailureActionFor(item.Key);
					List<KeyValuePair<string, uint>> value;
					if (!dictionary.TryGetValue(key, out value))
					{
						value = (dictionary[key] = new List<KeyValuePair<string, uint>>());
					}
					value.Add(item);
				}
				uint currentDriverVersionResult = CurrentDriverVersion();
				string environmentContext = "NVIDIA DRS | " + ((currentDriverVersionResult == 0) ? "GPU/driver context unavailable" : ("driver " + FormatDriver(currentDriverVersionResult)));
				bool isNotIsBlocked = !ActionFailureStore.IsBlocked("NvDriverProfile", fileName);
				bool applyForGameReady = false;
				foreach (string key4 in dictionary.Keys)
				{
					applyForGameReady |= !ActionFailureStore.IsBlocked(key4, fileName);
				}
				string satSignatureResult = SatSignature(list);
				bool applyForGameReady2 = isNotIsBlocked;
				foreach (string key5 in dictionary.Keys)
				{
					applyForGameReady2 &= !ActionFailureStore.IsBlocked(key5, fileName);
				}
				if (list.Count > 0 && applyForGameReady2 && Settings.LoadStr(SatKey(fileName), "") == satSignatureResult)
				{
					return new List<string>();
				}
				bool matchesIsNotIsBlocked = isNotIsBlocked && applyForGameReady;
				bool applyForGameReady3 = false;
				bool applyForGameReady4 = true;
				string reason;
				if (matchesIsNotIsBlocked)
				{
					applyForGameReady3 = ActionFailureStore.TryBeginApply("NvDriverProfile", fileName, out reason);
				}
				if (matchesIsNotIsBlocked && !applyForGameReady3)
				{
					matchesIsNotIsBlocked = false;
				}
				if (!matchesIsNotIsBlocked && snapshot.Count == 0)
				{
					return null;
				}
				if (!matchesIsNotIsBlocked)
				{
					bool recoveryDue = false;
					foreach (string key in snapshot.Keys) recoveryDue |= RestoreDue(fileName, FailureActionFor(key));
					if (!recoveryDue) return null;
				}
				IntPtr session = IntPtr.Zero;
				try
				{
					if (!Driver.TryOpenSession(out session))
					{
						ScheduleRestore(fileName, DriverFailureAction);
						if (applyForGameReady3)
						{
							ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA driver session could not be opened", environmentContext);
						}
						else if (snapshot.Count > 0)
						{
							ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA recovery could not open the driver session", environmentContext);
						}
						retryLater = true;
						return null;
					}
					IntPtr profile;
					if (!Driver.FindOrCreateAppProfile(session, fileName, out profile))
					{
						ScheduleRestore(fileName, DriverFailureAction);
						if (applyForGameReady3 || snapshot.Count > 0)
						{
							ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA game profile could not be opened", environmentContext);
						}
						retryLater = true;
						return null;
					}
					List<string> list3 = new List<string>();
					HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (KeyValuePair<string, uint> item2 in list)
					{
						if (matchesIsNotIsBlocked && !ActionFailureStore.IsBlocked(FailureActionFor(item2.Key), fileName))
						{
							hashSet.Add(item2.Key);
						}
					}
					Dictionary<string, List<string>> dictionary2 = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
					foreach (string key6 in snapshot.Keys)
					{
						if (!hashSet.Contains(key6))
						{
							string key2 = FailureActionFor(key6);
							List<string> value2;
							if (!dictionary2.TryGetValue(key2, out value2))
							{
								value2 = (dictionary2[key2] = new List<string>());
							}
							value2.Add(key6);
						}
					}
					foreach (KeyValuePair<string, List<string>> item3 in dictionary2)
					{
						bool restoreDueSucceeded = RestoreDue(fileName, item3.Key);
						if (!RestoreGameKeys(session, profile, fileName, snapshot, item3.Value, item3.Key, false))
						{
							if (restoreDueSucceeded)
							{
								ActionFailureStore.Trip(item3.Key, fileName, "NVIDIA stale setting recovery remains incomplete", environmentContext);
							}
							list3.AddRange(item3.Value);
							retryLater = true;
						}
						else
						{
							ActionFailureStore.CancelApply(item3.Key, fileName);
						}
					}
					foreach (KeyValuePair<string, List<KeyValuePair<string, uint>>> item4 in dictionary)
					{
						string key3 = item4.Key;
						if (!matchesIsNotIsBlocked || ActionFailureStore.IsBlocked(key3, fileName))
						{
							foreach (KeyValuePair<string, uint> item5 in item4.Value)
							{
								if (!list3.Contains(item5.Key))
								{
									list3.Add(item5.Key);
								}
							}
							continue;
						}
						if (!ActionFailureStore.TryBeginApply(key3, fileName, out reason))
						{
							foreach (KeyValuePair<string, uint> item7 in item4.Value)
							{
								if (!list3.Contains(item7.Key))
								{
									list3.Add(item7.Key);
								}
							}
							continue;
						}
						bool applyForGameReady5 = false;
						bool applyForGameReady6 = false;
						bool applyForGameReady7 = false;
						bool force = false;
						string message = "";
						HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
						try
						{
							foreach (KeyValuePair<string, uint> item8 in item4.Value)
							{
								if (!snapshot.ContainsKey(item8.Key))
								{
									uint value3;
									int dwordOf = TryGetDwordOf(session, profile, item8.Key, out value3);
									if (dwordOf < 0)
									{
										applyForGameReady5 = true;
										message = "NVIDIA original setting could not be read: " + item8.Key;
										break;
									}
									snapshot[item8.Key] = ((dwordOf == 1) ? value3.ToString() : "absent");
								}
							}
							if (!applyForGameReady5 && !SaveGameSnapshot(fileName, snapshot))
							{
								ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA recovery journal could not be saved", environmentContext);
								ActionFailureStore.CancelApply(key3, fileName);
								RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, false);
								matchesIsNotIsBlocked = false;
								retryLater = true;
								foreach (KeyValuePair<string, uint> item9 in item4.Value)
								{
									if (!list3.Contains(item9.Key))
									{
										list3.Add(item9.Key);
									}
								}
								continue;
							}
							if (!applyForGameReady5)
							{
								foreach (KeyValuePair<string, uint> item10 in item4.Value)
								{
									uint value4;
									int dwordOf2 = TryGetDwordOf(session, profile, item10.Key, out value4);
									if (dwordOf2 < 0)
									{
										applyForGameReady5 = true;
										message = "NVIDIA current setting became unreadable: " + item10.Key;
										break;
									}
									if (dwordOf2 == 1 && value4 == item10.Value)
									{
										continue;
									}
									snapshot[item10.Key] = PrepareWriteReceipt(snapshot[item10.Key], item10.Value);
									if (!SaveGameSnapshot(fileName, snapshot))
									{
										throw new IOException("NVIDIA write-ahead receipt could not be saved: " + item10.Key);
									}
									force = true;
									int status;
									if (SetDwordOf(session, profile, item10.Key, item10.Value, out status))
									{
										applyForGameReady6 = true;
										continue;
									}
									if (status == -160)
									{
										hashSet2.Add(item10.Key);
										if (item10.Key == "ullenable" || item10.Key == "lowlatcpl")
										{
											MarkUltraGone();
										}
										continue;
									}
									applyForGameReady5 = true;
									message = "NVIDIA setting write failed: " + item10.Key + " (" + status + ")";
									break;
								}
							}
							if (!applyForGameReady5 && applyForGameReady6 && !Driver.SaveSession(session))
							{
								ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA driver session could not be saved", environmentContext);
								if (RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, true))
								{
									ActionFailureStore.CancelApply(key3, fileName);
								}
								else
								{
									ActionFailureStore.Trip(key3, fileName, "NVIDIA driver save failed and action rollback remains incomplete", environmentContext);
									applyForGameReady4 = false;
								}
								matchesIsNotIsBlocked = false;
								retryLater = true;
								foreach (KeyValuePair<string, uint> item11 in item4.Value)
								{
									if (!list3.Contains(item11.Key))
									{
										list3.Add(item11.Key);
									}
								}
								continue;
							}
							if (!applyForGameReady5)
							{
								foreach (KeyValuePair<string, uint> item12 in item4.Value)
								{
									uint value5;
									if (!hashSet2.Contains(item12.Key) && (TryGetDwordOf(session, profile, item12.Key, out value5) != 1 || value5 != item12.Value))
									{
										applyForGameReady5 = true;
										message = "NVIDIA setting verification failed: " + item12.Key;
									}
								}
							}
							if (!applyForGameReady5 && hashSet2.Count == item4.Value.Count)
							{
								applyForGameReady7 = true;
							}
							if (!applyForGameReady5 && !applyForGameReady7)
							{
								foreach (KeyValuePair<string, uint> item13 in item4.Value)
								{
									string value6;
									if (!hashSet2.Contains(item13.Key) && snapshot.TryGetValue(item13.Key, out value6))
									{
										snapshot[item13.Key] = SnapOrig(value6) + '~' + item13.Value;
									}
								}
								if (!SaveGameSnapshot(fileName, snapshot))
								{
									applyForGameReady5 = true;
									message = "NVIDIA verified setting receipt could not be saved";
								}
							}
						}
						catch (NotSupportedException)
						{
							applyForGameReady7 = true;
						}
						catch (OperationCanceledException)
						{
							applyForGameReady7 = true;
						}
						catch (Exception ex3)
						{
							applyForGameReady5 = true;
							message = ex3.GetType().Name + ": " + ex3.Message;
						}
						if (applyForGameReady5)
						{
							ActionFailureStore.Trip(key3, fileName, message, environmentContext);
							if (RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, force))
							{
								ActionFailureStore.CancelApply(key3, fileName);
							}
							else
							{
								ActionFailureStore.Trip(key3, fileName, message + "; rollback remains incomplete", environmentContext);
							}
							foreach (KeyValuePair<string, uint> item14 in item4.Value)
							{
								if (!list3.Contains(item14.Key))
								{
									list3.Add(item14.Key);
								}
							}
							Logger.Warn(key3 + " | " + fileName + " | " + message);
							retryLater = true;
						}
						else if (applyForGameReady7)
						{
							if (RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, force))
							{
								ActionFailureStore.CancelApply(key3, fileName);
							}
							else
							{
								ActionFailureStore.Trip(key3, fileName, "NVIDIA recovery after skip or cancellation remains incomplete", environmentContext);
							}
							foreach (KeyValuePair<string, uint> item15 in item4.Value)
							{
								if (!list3.Contains(item15.Key))
								{
									list3.Add(item15.Key);
								}
							}
						}
						else if (!ActionFailureStore.CompleteApply(key3, fileName))
						{
							if (RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, true))
							{
								ActionFailureStore.CancelApply(key3, fileName);
							}
							else
							{
								ActionFailureStore.Trip(key3, fileName, "NVIDIA completion could not be persisted and rollback remains incomplete", environmentContext);
							}
							foreach (KeyValuePair<string, uint> item16 in item4.Value)
							{
								if (!list3.Contains(item16.Key))
								{
									list3.Add(item16.Key);
								}
							}
							retryLater = true;
						}
						foreach (string item17 in hashSet2)
						{
							if (!list3.Contains(item17))
							{
								list3.Add(item17);
							}
						}
					}
					if (applyForGameReady3 && matchesIsNotIsBlocked && !ActionFailureStore.CompleteApply("NvDriverProfile", fileName))
					{
						matchesIsNotIsBlocked = false;
						retryLater = true;
						applyForGameReady4 = RestoreGameKeys(session, profile, fileName, snapshot, new List<string>(snapshot.Keys), "NvDriverProfile", false);
						if (!applyForGameReady4)
						{
							ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA session completion could not be persisted and rollback remains incomplete", environmentContext);
						}
					}
					bool matchesMatchesIsNotIsBlocked = list3.Count == 0 && list.Count > 0 && matchesIsNotIsBlocked;
					Settings.SaveStr(SatKey(fileName), matchesMatchesIsNotIsBlocked ? satSignatureResult : "");
					return list3;
				}
				catch (NotSupportedException)
				{
					ScheduleRestore(fileName, DriverFailureAction);
					if (applyForGameReady3)
					{
						ActionFailureStore.CancelApply("NvDriverProfile", fileName);
					}
					return null;
				}
				catch (OperationCanceledException)
				{
					ScheduleRestore(fileName, DriverFailureAction);
					if (applyForGameReady3)
					{
						ActionFailureStore.CancelApply("NvDriverProfile", fileName);
					}
					return null;
				}
				catch (Exception ex6)
				{
					ScheduleRestore(fileName, DriverFailureAction);
					if (applyForGameReady3)
					{
						ActionFailureStore.Trip("NvDriverProfile", fileName, ex6.GetType().Name + ": " + ex6.Message, environmentContext);
					}
					retryLater = true;
					return null;
				}
				finally
				{
					if (applyForGameReady3 && applyForGameReady4)
					{
						ActionFailureStore.CancelApply("NvDriverProfile", fileName);
					}
					if (session != IntPtr.Zero)
					{
						try
						{
							Driver.CloseSession(session);
						}
						catch
						{
						}
					}
				}
			}
		}

		private static bool DesiredHasDlss(List<KeyValuePair<string, uint>> desired)
		{
			foreach (KeyValuePair<string, uint> item in desired)
			{
				if (item.Key == "dlssovr")
				{
					return true;
				}
			}
			return false;
		}

		internal static string DlssText(string mode)
		{
			switch (mode)
			{
			case "latest":
				return Lang.T("t.nvdrstweaks.23");
			case "j":
				return Lang.T("t.nvdrstweaks.24");
			case "k":
				return Lang.T("t.nvdrstweaks.25");
			default:
				return Lang.T("t.nvdrstweaks.26");
			}
		}

		internal static bool ContainsAny(List<string> list, string[] keys)
		{
			foreach (string item in keys)
			{
				if (list.Contains(item))
				{
					return true;
				}
			}
			return false;
		}

        public static void RestoreKind(string key)
        {
            lock (sync)
                foreach (string executable in Settings.LoadStr(ListKey, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    RestoreTarget(executable, key);
        }

        private static bool RestoreTarget(string executable, string onlyKey)
        {
            if (!RestoreDue(executable, DriverFailureAction)) return false;
            Dictionary<string, string> snapshot;
            if (!TryReadGameSnapshot(executable, out snapshot))
            {
                IsolateInvalidJournal(executable);
                ScheduleRestore(executable, DriverFailureAction);
                return false;
            }
            if (snapshot.Count == 0)
            {
                if (SaveGameSnapshot(executable, snapshot))
                {
                    restoreFailures.Remove(RestoreKey(executable, DriverFailureAction));
                    restoreRetryAt.Remove(RestoreKey(executable, DriverFailureAction));
                    return true;
                }
                ScheduleRestore(executable, DriverFailureAction);
                return false;
            }
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            bool due = false;
            foreach (string key in snapshot.Keys)
            {
                if (onlyKey != null && key != onlyKey) continue;
                string action = FailureActionFor(key);
                List<string> keys;
                if (!groups.TryGetValue(action, out keys)) groups[action] = keys = new List<string>();
                keys.Add(key);
                due |= RestoreDue(executable, action);
            }
            if (groups.Count == 0) return true;
            // Exhausted/deferred receipts must not repeatedly open a native session.
            if (!due) return false;
            IntPtr session = IntPtr.Zero;
            try
            {
                IntPtr profile;
                if (!Driver.Available || !Driver.TryOpenSession(out session)
                    || !Driver.FindOrCreateAppProfile(session, executable, out profile))
                {
                    ActionFailureStore.Trip(DriverFailureAction, executable, "NVIDIA recovery driver/profile unavailable");
                    ScheduleRestore(executable, DriverFailureAction);
                    return false;
                }
                bool restored = true;
                foreach (var group in groups)
                {
                    bool admitted = RestoreDue(executable, group.Key);
                    if (RestoreGameKeys(session, profile, executable, snapshot, group.Value, group.Key, false))
                    {
                        if (SnapshotKeysFor(snapshot, group.Key).Count == 0) ActionFailureStore.CancelApply(group.Key, executable);
                    }
                    else
                    {
                        restored = false;
                        if (admitted) ActionFailureStore.Trip(group.Key, executable, "NVIDIA action recovery remains incomplete");
                    }
                }
                if (snapshot.Count == 0)
                {
                    ActionFailureStore.CancelApply(DriverFailureAction, executable);
                    restoreFailures.Remove(RestoreKey(executable, DriverFailureAction));
                    restoreRetryAt.Remove(RestoreKey(executable, DriverFailureAction));
                }
                return restored;
            }
            catch (Exception error)
            {
                ActionFailureStore.Trip(DriverFailureAction, executable, error.GetType().Name + ": " + error.Message);
                ScheduleRestore(executable, DriverFailureAction);
                return false;
            }
            finally
            {
                if (session != IntPtr.Zero) try { Driver.CloseSession(session); } catch { }
            }
        }

		public static void RestoreKinds(string[] keys)
		{
			foreach (string key in keys)
			{
				RestoreKind(key);
			}
		}

		public static bool HasSnapshotFor(string key)
		{
			lock (sync)
			{
				string[] loadStrParts = Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				string[] textEntries = loadStrParts;
				foreach (string entryText in textEntries)
				{
					if (ParseSnapshot(Settings.LoadStr("NvDrs_" + entryText, "")).ContainsKey(key))
					{
						return true;
					}
				}
				return false;
			}
		}

		internal static bool PresetKindsRestored(string[] kinds)
		{
			lock (sync)
			{
				string raw; string[] names;
				if (!Settings.TryLoadStr(ListKey, out raw) || !Settings.TryLoadStoredNames("NvDrs_", out names)) return false;
				string[] indexed = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				HashSet<string> all = new HashSet<string>(indexed, StringComparer.OrdinalIgnoreCase);
				if (indexed.Length != all.Count || all.Count > 4096 || names.Length > 4096) return false;
				foreach (string name in names)
				{
					string value;
					if (!Settings.TryLoadStr(name, out value)) return false;
					if (!string.IsNullOrEmpty(value)) all.Add(name.Substring("NvDrs_".Length));
				}
				foreach (string executable in all)
				{
					Dictionary<string, string> snapshot;
					if (Path.GetFileName(executable) != executable || !TryReadGameSnapshot(executable, out snapshot)) return false;
					foreach (string kind in kinds) if (snapshot.ContainsKey(kind)) return false;
				}
				return true;
			}
		}

        public static bool RestoreAllGames()
        {
            lock (sync)
            {
                bool restored = true;
                foreach (string executable in Settings.LoadStr(ListKey, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    restored &= RestoreTarget(executable, null);
                return restored;
            }
        }

		private static bool NvidiaAbsent()
		{
			GpuAdapter[] adaptersResult = GpuInventory.Adapters();
			if (adaptersResult == null || adaptersResult.Length == 0)
			{
				return false;
			}
			GpuAdapter[] gpuAdapterItems = adaptersResult;
			foreach (GpuAdapter gpuAdapter in gpuAdapterItems)
			{
				if (gpuAdapter.Vendor == GpuVendor.Nvidia)
				{
					return false;
				}
			}
			return true;
		}

		private static int DropAllSnapshots()
		{
			lock (sync)
			{
				string[] loadStrParts = Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				int processedCount = 0;
				string[] textEntries = loadStrParts;
				foreach (string entryText in textEntries)
				{
					if (Settings.LoadStr("NvDrs_" + entryText, "").Length > 0)
					{
						processedCount++;
					}
					Settings.SaveStr("NvDrs_" + entryText, "");
					Settings.SaveStr(SatKey(entryText), "");
				}
				Settings.SaveStr("NvDrsList", "");
				return processedCount;
			}
		}

		private static bool RestoreDword(IntPtr session, IntPtr profile, string key, string orig)
		{
			int status;
			if (!SetDwordOf(session, profile, key, ParseUInt(orig), out status))
			{
				return status == -160;
			}
			return true;
		}

		private static uint ParseUInt(string value)
		{
			uint result;
			if (!uint.TryParse(value, out result))
			{
				return 0u;
			}
			return result;
		}

		internal static Dictionary<string, string> ParseSnapshot(string raw)
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrEmpty(raw))
			{
				return dictionary;
			}
			string[] rawParts = raw.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string entryText in rawParts)
			{
				int matchIndex = entryText.IndexOf('=');
				if (matchIndex > 0)
				{
					dictionary[entryText.Substring(0, matchIndex)] = entryText.Substring(matchIndex + 1);
				}
			}
			return dictionary;
		}

		internal static string SerializeSnapshot(Dictionary<string, string> map)
		{
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, string> item in map)
			{
				list.Add(item.Key + "=" + item.Value);
			}
			list.Sort(StringComparer.OrdinalIgnoreCase);
			return string.Join(";", list.ToArray());
		}

		private static bool AddToList(string exeName)
		{
			List<string> list = new List<string>(Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries));
			foreach (string item in list)
			{
				if (string.Equals(item, exeName, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			list.Add(exeName);
			return Settings.SaveStrDurable("NvDrsList", string.Join(";", list.ToArray()));
		}

		private static void RemoveFromList(string exeName)
		{
			List<string> list = new List<string>(Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries));
			list.RemoveAll((string g) => string.Equals(g, exeName, StringComparison.OrdinalIgnoreCase));
			Settings.SaveStr("NvDrsList", string.Join(";", list.ToArray()));
		}

		internal static bool TryGameNvDebt(out string[] debt)
		{
			lock (sync)
			{
				debt = new string[0];
				string[] names;
				string value;
				if (!Settings.TryLoadStoredNames("NvDrs_", out names) || !Settings.TryLoadStr("NvDrsList", out value))
				{
					return false;
				}
				string[] valueParts = value.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				HashSet<string> hashSet = new HashSet<string>(valueParts, StringComparer.OrdinalIgnoreCase);
				if (hashSet.Count != valueParts.Length || hashSet.Count > 4096 || names.Length > 4096)
				{
					return false;
				}
				string[] namesSnapshot = names;
				foreach (string entryText in namesSnapshot)
				{
					string item = entryText.Substring("NvDrs_".Length);
					string value2;
					if (!hashSet.Contains(item) && (!Settings.TryLoadStr(entryText, out value2) || value2.Length != 0))
					{
						return false;
					}
				}
				List<string> list = new List<string>();
				foreach (string item2 in hashSet)
				{
					if (Path.GetFileName(item2) != item2 || !item2.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					Dictionary<string, string> snapshot;
					if (!TryReadGameSnapshot(item2, out snapshot))
					{
						return false;
					}
					if (snapshot.ContainsKey("pstate") || snapshot.ContainsKey("forcep2"))
					{
						list.Add(item2);
					}
				}
				debt = list.ToArray();
				return true;
			}
		}
	}
}
