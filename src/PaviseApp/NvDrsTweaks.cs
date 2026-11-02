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

		private static readonly INvDrsDriver Driver = new NativeNvDrsDriver();

		public static readonly string[] RebarKeys = new string[3] { "rebarfeat", "rebaropt", "rebarsize" };

		public static readonly string[] DlssKeys = new string[2] { "dlssovr", "dlsspreset" };

		public static readonly string[] UltraKeys = new string[3] { "prerender", "ullenable", "lowlatcpl" };

		private static readonly object sync = GameNvNativeGate.SyncRoot;

		private static bool dlssGateLogged;

		private static bool smoothGateLogged;

		private static readonly Dictionary<string, long> restoreRetryAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

		private static readonly Dictionary<string, int> restoreFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		internal static object SessionGate
		{
			get
			{
				return sync;
			}
		}

		public static bool HasGameResidue
		{
			get
			{
				return Settings.LoadStr("NvDrsList", "").Length > 0;
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
			uint num = SettingIdOf(key);
			if (num == 0)
			{
				value = 0u;
				return -1;
			}
			return Driver.TryGetDword(session, profile, num, out value);
		}

		private static bool SetDwordOf(IntPtr session, IntPtr profile, string key, uint value, out int status)
		{
			uint num = SettingIdOf(key);
			if (num == 0)
			{
				status = 0;
				return false;
			}
			return Driver.SetDword(session, profile, num, value, out status);
		}

		private static bool DeleteSettingOf(IntPtr session, IntPtr profile, string key)
		{
			uint num = SettingIdOf(key);
			if (num == 0)
			{
				return false;
			}
			return Driver.DeleteSetting(session, profile, num);
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
				GpuAdapter[] array = GpuInventory.Adapters();
				foreach (GpuAdapter gpuAdapter in array)
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
			uint num = 0u;
			try
			{
				GpuAdapter[] array = GpuInventory.Adapters();
				foreach (GpuAdapter gpuAdapter in array)
				{
					if (gpuAdapter.Vendor == GpuVendor.Nvidia && !string.IsNullOrEmpty(gpuAdapter.Name))
					{
						uint num2 = SmoothMotionMinDriverForName(gpuAdapter.Name);
						if (num2 != 0 && (num == 0 || num2 < num))
						{
							num = num2;
						}
					}
				}
			}
			catch
			{
			}
			return num;
		}

		public static bool SmoothMotionSupported()
		{
			uint num = SmoothMotionMinDriver();
			if (num == 0 || !Driver.Available)
			{
				return false;
			}
			return IsSmoothMotionSupported(num, true, CurrentDriverVersion(), NvApi.SmoothMotionSettingSupported);
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
			string text = "d" + CurrentDriverVersion();
			if (!(Settings.LoadStr("NvUllUnsupDrv", "") == text))
			{
				Settings.SaveStr("NvUllUnsupDrv", text);
				Logger.Log(Lang.T("log.nvdrstweaks.48"));
			}
		}

		internal static string SnapOrig(string stored)
		{
			int num = stored.IndexOf('~');
			if (num >= 0)
			{
				return stored.Substring(0, num);
			}
			return stored;
		}

		internal static bool TrySnapApplied(string stored, out uint applied)
		{
			applied = 0u;
			int num = stored.IndexOf('~');
			if (num < 0)
			{
				return false;
			}
			string text = stored.Substring(num + 1);
			int num2 = text.IndexOf(',');
			return uint.TryParse((num2 < 0) ? text : text.Substring(0, num2), out applied);
		}

		private static bool MatchesApplied(string stored, uint current)
		{
			int num = stored.IndexOf('~');
			if (num < 0)
			{
				return false;
			}
			string[] array = stored.Substring(num + 1).Split(',');
			foreach (string s in array)
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
			string text = desired.ToString();
			uint applied;
			if (TrySnapApplied(stored, out applied) && applied != desired)
			{
				text = text + "," + applied;
			}
			return SnapOrig(stored) + '~' + text;
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
			return DateTime.UtcNow.Ticks;
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
			return exeName + "|" + action;
		}

		private static void ScheduleRestore(string exeName, string action)
		{
			string key = RestoreKey(exeName, action);
			int value;
			restoreFailures.TryGetValue(key, out value);
			value = Math.Min(value + 1, 5);
			restoreFailures[key] = value;
			restoreRetryAt[key] = RestoreClock() + TimeSpan.FromSeconds(Math.Min(4 << value - 1, 60)).Ticks;
		}

		private static bool RestoreDue(string exeName, string action)
		{
			if (ActionFailureStore.IsRetryPending(action, exeName) || ActionFailureStore.IsRetryPending("NvDriverProfile", exeName))
			{
				return true;
			}
			long value;
			if (restoreRetryAt.TryGetValue(RestoreKey(exeName, action), out value))
			{
				return RestoreClock() >= value;
			}
			return true;
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
			if (!force && !RestoreDue(exeName, action))
			{
				return false;
			}
			List<string> list = new List<string>();
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			bool flag = false;
			bool flag2 = true;
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
					int num = TryGetDwordOf(session, profile, key, out value2);
					if (num < 0)
					{
						flag2 = false;
						continue;
					}
					uint applied;
					if (!force && TrySnapApplied(value, out applied) && (num == 0 || (num == 1 && !MatchesApplied(value, value2))))
					{
						list.Add(key);
						hashSet.Add(key);
						continue;
					}
					string text = SnapOrig(value);
					bool num2;
					if (!(text == "absent"))
					{
						if (num != 1)
						{
							goto IL_00e5;
						}
						num2 = value2 == ParseUInt(text);
					}
					else
					{
						num2 = num == 0;
					}
					if (num2)
					{
						list.Add(key);
						continue;
					}
					goto IL_00e5;
					IL_00e5:
					if (!force && (key == "pstate" || key == "forcep2") && !TrySnapApplied(value, out applied))
					{
						flag2 = false;
						continue;
					}
					bool num3;
					if (!(text == "absent"))
					{
						num3 = RestoreDword(session, profile, key, text);
					}
					else
					{
						if (num == 0)
						{
							goto IL_0145;
						}
						num3 = DeleteSettingOf(session, profile, key);
					}
					if (!num3)
					{
						flag2 = false;
						continue;
					}
					goto IL_0145;
					IL_0145:
					list.Add(key);
					flag = true;
				}
				if (flag && !Driver.SaveSession(session))
				{
					ScheduleRestore(exeName, action);
					return false;
				}
				List<string> list2 = new List<string>();
				foreach (string item in list)
				{
					string stored = snapshot[item];
					uint value3;
					int num4 = TryGetDwordOf(session, profile, item, out value3);
					string text2 = SnapOrig(stored);
					if (!hashSet.Contains(item))
					{
						bool num5;
						if (!(text2 == "absent"))
						{
							if (num4 != 1)
							{
								goto IL_0208;
							}
							num5 = value3 == ParseUInt(text2);
						}
						else
						{
							num5 = num4 == 0;
						}
						if (!num5)
						{
							goto IL_0208;
						}
					}
					list2.Add(item);
					continue;
					IL_0208:
					flag2 = false;
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
				if (flag2)
				{
					restoreFailures.Remove(RestoreKey(exeName, action));
					restoreRetryAt.Remove(RestoreKey(exeName, action));
				}
				else
				{
					ScheduleRestore(exeName, action);
				}
				return flag2;
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
			string[] array = value.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string text in array)
			{
				int num = text.IndexOf('=');
				if (num <= 0 || num == text.Length - 1)
				{
					return false;
				}
				string key = text.Substring(0, num);
				string text2 = text.Substring(num + 1);
				uint result;
				if (snapshot.ContainsKey(key) || SettingIdOf(key) == 0 || (SnapOrig(text2) != "absent" && !uint.TryParse(SnapOrig(text2), out result)) || (text2.IndexOf('~') >= 0 && !TrySnapApplied(text2, out result)))
				{
					return false;
				}
				if (text2.IndexOf('~') >= 0)
				{
					string[] array2 = text2.Substring(text2.IndexOf('~') + 1).Split(',');
					if (array2.Length > 2)
					{
						return false;
					}
					string[] array3 = array2;
					foreach (string s in array3)
					{
						if (!uint.TryParse(s, out result))
						{
							return false;
						}
					}
				}
				snapshot[key] = text2;
			}
			return true;
		}

		public static List<string> ApplyForGame(string exePath, NvGamePlan plan, out bool retryLater)
		{
			retryLater = false;
			if (string.IsNullOrEmpty(exePath) || !Driver.Available)
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
				List<KeyValuePair<string, uint>> list = ((plan == null || plan.Empty) ? new List<KeyValuePair<string, uint>>() : BuildDesired(plan));
				Dictionary<string, string> snapshot;
				if (!TryReadGameSnapshot(fileName, out snapshot))
				{
					ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA recovery journal is unreadable or invalid", "NVIDIA DRS | GPU/driver context unavailable");
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
				uint num = CurrentDriverVersion();
				string environmentContext = "NVIDIA DRS | " + ((num == 0) ? "GPU/driver context unavailable" : ("driver " + FormatDriver(num)));
				bool flag = ActionFailureStore.IsRetryPending("NvDriverProfile", fileName);
				bool flag2 = !ActionFailureStore.IsBlocked("NvDriverProfile", fileName);
				bool flag3 = false;
				bool flag4 = flag;
				foreach (string key4 in dictionary.Keys)
				{
					flag3 |= !ActionFailureStore.IsBlocked(key4, fileName);
					flag4 |= ActionFailureStore.IsRetryPending(key4, fileName);
				}
				string text = SatSignature(list);
				bool flag5 = flag2;
				foreach (string key5 in dictionary.Keys)
				{
					flag5 &= !ActionFailureStore.IsBlocked(key5, fileName);
				}
				if (list.Count > 0 && flag5 && !flag4 && Settings.LoadStr(SatKey(fileName), "") == text)
				{
					return new List<string>();
				}
				bool flag6 = flag2 && flag3;
				bool flag7 = false;
				bool flag8 = true;
				string reason;
				if (flag6 && (!flag || snapshot.Count <= 0))
				{
					flag7 = ActionFailureStore.TryBeginApply("NvDriverProfile", fileName, out reason);
				}
				if (flag6 && !flag7 && (!flag || snapshot.Count <= 0))
				{
					flag6 = false;
				}
				if (!flag6 && snapshot.Count == 0)
				{
					return null;
				}
				IntPtr session = IntPtr.Zero;
				try
				{
					if (!Driver.TryOpenSession(out session))
					{
						if (flag7)
						{
							ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA driver session could not be opened", environmentContext);
						}
						else if (flag)
						{
							ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA recovery before retry could not open the driver session", environmentContext);
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
						if (flag7 || flag || snapshot.Count > 0)
						{
							ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA game profile could not be opened", environmentContext);
						}
						retryLater = true;
						return null;
					}
					List<string> list3 = new List<string>();
					if (flag && snapshot.Count > 0)
					{
						if (!RestoreGameKeys(session, profile, fileName, snapshot, new List<string>(snapshot.Keys), "NvDriverProfile", false))
						{
							ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA recovery before retry failed", environmentContext);
							retryLater = true;
							return null;
						}
						flag7 = ActionFailureStore.TryBeginApply("NvDriverProfile", fileName, out reason);
						flag6 = flag7 && flag3;
					}
					HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (KeyValuePair<string, uint> item2 in list)
					{
						if (flag6 && !ActionFailureStore.IsBlocked(FailureActionFor(item2.Key), fileName))
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
						bool flag9 = RestoreDue(fileName, item3.Key);
						if (!RestoreGameKeys(session, profile, fileName, snapshot, item3.Value, item3.Key, false))
						{
							if (flag9)
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
						if (!flag6 || ActionFailureStore.IsBlocked(key3, fileName))
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
						if (ActionFailureStore.IsRetryPending(key3, fileName) && !RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, false))
						{
							ActionFailureStore.Trip(key3, fileName, "NVIDIA action recovery before retry failed", environmentContext);
							foreach (KeyValuePair<string, uint> item6 in item4.Value)
							{
								if (!list3.Contains(item6.Key))
								{
									list3.Add(item6.Key);
								}
							}
							retryLater = true;
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
						bool flag10 = false;
						bool flag11 = false;
						bool flag12 = false;
						bool force = false;
						string text2 = "";
						HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
						try
						{
							foreach (KeyValuePair<string, uint> item8 in item4.Value)
							{
								if (!snapshot.ContainsKey(item8.Key))
								{
									uint value3;
									int num2 = TryGetDwordOf(session, profile, item8.Key, out value3);
									if (num2 < 0)
									{
										flag10 = true;
										text2 = "NVIDIA original setting could not be read: " + item8.Key;
										break;
									}
									snapshot[item8.Key] = ((num2 == 1) ? value3.ToString() : "absent");
								}
							}
							if (!flag10 && !SaveGameSnapshot(fileName, snapshot))
							{
								ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA recovery journal could not be saved", environmentContext);
								ActionFailureStore.CancelApply(key3, fileName);
								ActionFailureStore.WithdrawRetry(key3, fileName);
								RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, false);
								flag6 = false;
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
							if (!flag10)
							{
								foreach (KeyValuePair<string, uint> item10 in item4.Value)
								{
									uint value4;
									int num3 = TryGetDwordOf(session, profile, item10.Key, out value4);
									if (num3 < 0)
									{
										flag10 = true;
										text2 = "NVIDIA current setting became unreadable: " + item10.Key;
										break;
									}
									if (num3 == 1 && value4 == item10.Value)
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
										flag11 = true;
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
									flag10 = true;
									text2 = "NVIDIA setting write failed: " + item10.Key + " (" + status + ")";
									break;
								}
							}
							if (!flag10 && flag11 && !Driver.SaveSession(session))
							{
								ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA driver session could not be saved", environmentContext);
								if (RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, true))
								{
									ActionFailureStore.CancelApply(key3, fileName);
									ActionFailureStore.WithdrawRetry(key3, fileName);
								}
								else
								{
									ActionFailureStore.Trip(key3, fileName, "NVIDIA driver save failed and action rollback remains incomplete", environmentContext);
									flag8 = false;
								}
								flag6 = false;
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
							if (!flag10)
							{
								foreach (KeyValuePair<string, uint> item12 in item4.Value)
								{
									uint value5;
									if (!hashSet2.Contains(item12.Key) && (TryGetDwordOf(session, profile, item12.Key, out value5) != 1 || value5 != item12.Value))
									{
										flag10 = true;
										text2 = "NVIDIA setting verification failed: " + item12.Key;
									}
								}
							}
							if (!flag10 && hashSet2.Count == item4.Value.Count)
							{
								flag12 = true;
							}
							if (!flag10 && !flag12)
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
									flag10 = true;
									text2 = "NVIDIA verified setting receipt could not be saved";
								}
							}
						}
						catch (NotSupportedException)
						{
							flag12 = true;
						}
						catch (OperationCanceledException)
						{
							flag12 = true;
						}
						catch (Exception ex3)
						{
							flag10 = true;
							text2 = ex3.GetType().Name + ": " + ex3.Message;
						}
						if (flag10)
						{
							ActionFailureStore.Trip(key3, fileName, text2, environmentContext);
							if (RestoreGameKeys(session, profile, fileName, snapshot, SnapshotKeysFor(snapshot, key3), key3, force))
							{
								ActionFailureStore.CancelApply(key3, fileName);
							}
							else
							{
								ActionFailureStore.Trip(key3, fileName, text2 + "; rollback remains incomplete", environmentContext);
							}
							foreach (KeyValuePair<string, uint> item14 in item4.Value)
							{
								if (!list3.Contains(item14.Key))
								{
									list3.Add(item14.Key);
								}
							}
							Logger.Warn(key3 + " | " + fileName + " | " + text2);
							retryLater = true;
						}
						else if (flag12)
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
					if (flag7 && flag6 && !ActionFailureStore.CompleteApply("NvDriverProfile", fileName))
					{
						flag6 = false;
						retryLater = true;
						flag8 = RestoreGameKeys(session, profile, fileName, snapshot, new List<string>(snapshot.Keys), "NvDriverProfile", false);
						if (!flag8)
						{
							ActionFailureStore.Trip("NvDriverProfile", fileName, "NVIDIA session completion could not be persisted and rollback remains incomplete", environmentContext);
						}
					}
					bool flag13 = list3.Count == 0 && list.Count > 0 && flag6;
					Settings.SaveStr(SatKey(fileName), flag13 ? text : "");
					return list3;
				}
				catch (NotSupportedException)
				{
					if (flag7)
					{
						ActionFailureStore.CancelApply("NvDriverProfile", fileName);
					}
					return null;
				}
				catch (OperationCanceledException)
				{
					if (flag7)
					{
						ActionFailureStore.CancelApply("NvDriverProfile", fileName);
					}
					return null;
				}
				catch (Exception ex6)
				{
					if (flag7)
					{
						ActionFailureStore.Trip("NvDriverProfile", fileName, ex6.GetType().Name + ": " + ex6.Message, environmentContext);
					}
					retryLater = true;
					return null;
				}
				finally
				{
					if (flag7 && flag8)
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
			if (!Driver.Available)
			{
				return;
			}
			lock (sync)
			{
				string[] array = Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				string[] array2 = array;
				foreach (string text in array2)
				{
					Dictionary<string, string> snapshot;
					if (!TryReadGameSnapshot(text, out snapshot))
					{
						ActionFailureStore.Trip("NvDriverProfile", text, "NVIDIA recovery journal is unreadable or invalid");
					}
					else
					{
						if (!snapshot.ContainsKey(key))
						{
							continue;
						}
						string action = FailureActionFor(key);
						if (!RestoreDue(text, action))
						{
							continue;
						}
						IntPtr session = IntPtr.Zero;
						try
						{
							IntPtr profile;
							if (!Driver.TryOpenSession(out session))
							{
								ActionFailureStore.Trip("NvDriverProfile", text, "NVIDIA recovery could not open the driver session");
							}
							else if (!Driver.FindOrCreateAppProfile(session, text, out profile))
							{
								ActionFailureStore.Trip("NvDriverProfile", text, "NVIDIA recovery could not open the game profile");
							}
							else if (!RestoreGameKeys(session, profile, text, snapshot, new List<string> { key }, action, false))
							{
								ActionFailureStore.Trip(action, text, "NVIDIA setting recovery remains incomplete");
							}
							else if (SnapshotKeysFor(snapshot, action).Count == 0)
							{
								ActionFailureStore.CancelApply(action, text);
							}
						}
						catch (NotSupportedException)
						{
						}
						catch (OperationCanceledException)
						{
						}
						catch (Exception ex3)
						{
							ActionFailureStore.Trip("NvDriverProfile", text, ex3.GetType().Name + ": " + ex3.Message);
						}
						finally
						{
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
				string[] array = Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				string[] array2 = array;
				foreach (string text in array2)
				{
					if (ParseSnapshot(Settings.LoadStr("NvDrs_" + text, "")).ContainsKey(key))
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
				string[] array = Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				if (array.Length == 0)
				{
					return true;
				}
				if (!Driver.Available)
				{
					if (!NvidiaAbsent())
					{
						return false;
					}
					int num = DropAllSnapshots();
					if (num > 0)
					{
						Logger.Log(Lang.T("log.nvdrstweaks.34") + num + Lang.T("log.nvdrstweaks.35"));
					}
					return Settings.LoadStr("NvDrsList", "").Length == 0;
				}
				bool result = true;
				string[] array2 = array;
				foreach (string text in array2)
				{
					Dictionary<string, string> snapshot;
					if (!TryReadGameSnapshot(text, out snapshot))
					{
						ActionFailureStore.Trip("NvDriverProfile", text, "NVIDIA recovery journal is unreadable or invalid");
						result = false;
						continue;
					}
					if (snapshot.Count == 0)
					{
						if (!SaveGameSnapshot(text, snapshot))
						{
							result = false;
						}
						Settings.SaveStr(SatKey(text), "");
						continue;
					}
					IntPtr session = IntPtr.Zero;
					try
					{
						if (!Driver.TryOpenSession(out session))
						{
							ActionFailureStore.Trip("NvDriverProfile", text, "NVIDIA recovery could not open the driver session");
							result = false;
							continue;
						}
						IntPtr profile;
						if (!Driver.FindOrCreateAppProfile(session, text, out profile))
						{
							ActionFailureStore.Trip("NvDriverProfile", text, "NVIDIA recovery could not open the game profile");
							result = false;
							continue;
						}
						Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
						foreach (string key2 in snapshot.Keys)
						{
							string key = FailureActionFor(key2);
							List<string> value;
							if (!dictionary.TryGetValue(key, out value))
							{
								value = (dictionary[key] = new List<string>());
							}
							value.Add(key2);
						}
						foreach (KeyValuePair<string, List<string>> item in dictionary)
						{
							bool flag = RestoreDue(text, item.Key);
							if (RestoreGameKeys(session, profile, text, snapshot, item.Value, item.Key, false))
							{
								ActionFailureStore.CancelApply(item.Key, text);
								continue;
							}
							result = false;
							if (flag)
							{
								ActionFailureStore.Trip(item.Key, text, "NVIDIA action recovery remains incomplete");
							}
						}
						if (snapshot.Count == 0)
						{
							ActionFailureStore.CancelApply("NvDriverProfile", text);
						}
					}
					catch (NotSupportedException)
					{
						result = false;
					}
					catch (OperationCanceledException)
					{
						result = false;
					}
					catch (Exception ex3)
					{
						ActionFailureStore.Trip("NvDriverProfile", text, ex3.GetType().Name + ": " + ex3.Message);
						result = false;
					}
					finally
					{
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
				return result;
			}
		}

		private static bool NvidiaAbsent()
		{
			GpuAdapter[] array = GpuInventory.Adapters();
			if (array == null || array.Length == 0)
			{
				return false;
			}
			GpuAdapter[] array2 = array;
			foreach (GpuAdapter gpuAdapter in array2)
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
				string[] array = Settings.LoadStr("NvDrsList", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				int num = 0;
				string[] array2 = array;
				foreach (string text in array2)
				{
					if (Settings.LoadStr("NvDrs_" + text, "").Length > 0)
					{
						num++;
					}
					Settings.SaveStr("NvDrs_" + text, "");
					Settings.SaveStr(SatKey(text), "");
				}
				Settings.SaveStr("NvDrsList", "");
				return num;
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
			string[] array = raw.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string text in array)
			{
				int num = text.IndexOf('=');
				if (num > 0)
				{
					dictionary[text.Substring(0, num)] = text.Substring(num + 1);
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
				string[] array = value.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				HashSet<string> hashSet = new HashSet<string>(array, StringComparer.OrdinalIgnoreCase);
				if (hashSet.Count != array.Length || hashSet.Count > 4096 || names.Length > 4096)
				{
					return false;
				}
				string[] array2 = names;
				foreach (string text in array2)
				{
					string item = text.Substring("NvDrs_".Length);
					string value2;
					if (!hashSet.Contains(item) && (!Settings.TryLoadStr(text, out value2) || value2.Length != 0))
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
