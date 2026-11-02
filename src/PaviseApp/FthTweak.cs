using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class FthTweak
	{
		internal sealed class Shim
		{
			public string Exe;

			public string ValueName;

			public string SdbPath;
		}

		internal enum SdbVerdict
		{
			NotFth,
			Fth,
			Unknown
		}

		private const string FthKey = "SOFTWARE\\Microsoft\\FTH";

		private const string FthStateKey = "SOFTWARE\\Microsoft\\FTH\\State";

		private const string CustomKey = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Custom";

		private const string InstalledSdbKey = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\InstalledSDB";

		private const string ExclusionValue = "ExclusionList";

		private const string ExclusionBakKey = "FthExclusionBak";

		private const string CustomBakKey = "FthCustomBak";

		private const string Absent = "__pavise_absent__";

		private const char RecordSep = '\u001e';

		private const char FieldSep = '\u001f';

		private const int CacheMs = 2000;

		private static readonly object lk = new object();

		private static List<Shim> cached;

		private static long cachedTicks;

		public static bool RepairedByPavise
		{
			get
			{
				if (Settings.LoadStr("FthCustomBak", "").Length <= 0)
				{
					return Settings.LoadStr("FthExclusionBak", "").Length > 0;
				}
				return true;
			}
		}

		public static bool FeatureEnabled()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH"))
				{
					if (registryKey == null)
					{
						return false;
					}
					object value = registryKey.GetValue("Enabled");
					return value == null || Convert.ToInt64(value) != 0;
				}
			}
			catch
			{
				return false;
			}
		}

		public static bool TryReadTrigger(out int velocity, out int windowMinutes)
		{
			velocity = 0;
			windowMinutes = 0;
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH"))
				{
					if (registryKey == null)
					{
						return false;
					}
					object value = registryKey.GetValue("CrashVelocity");
					object value2 = registryKey.GetValue("CrashWindowInMinutes");
					if (value == null || value2 == null)
					{
						return false;
					}
					velocity = Convert.ToInt32(value);
					windowMinutes = Convert.ToInt32(value2);
					return velocity > 0 && windowMinutes > 0;
				}
			}
			catch
			{
				return false;
			}
		}

		public static string[] Exclusions()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH"))
				{
					if (registryKey == null)
					{
						return new string[0];
					}
					return (registryKey.GetValue("ExclusionList") as string[]) ?? new string[0];
				}
			}
			catch
			{
				return new string[0];
			}
		}

		public static bool IsExcluded(string exe)
		{
			if (string.IsNullOrEmpty(exe))
			{
				return false;
			}
			string[] array = Exclusions();
			foreach (string a in array)
			{
				if (string.Equals(a, exe, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		private static HashSet<string> StateNames()
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH\\State"))
				{
					if (registryKey == null)
					{
						return hashSet;
					}
					string[] valueNames = registryKey.GetValueNames();
					foreach (string text in valueNames)
					{
						if (text.Length > 0)
						{
							hashSet.Add(text);
						}
					}
					string[] subKeyNames = registryKey.GetSubKeyNames();
					foreach (string text2 in subKeyNames)
					{
						if (text2.Length > 0)
						{
							hashSet.Add(text2);
						}
					}
				}
			}
			catch
			{
			}
			return hashSet;
		}

		internal static SdbVerdict ClassifySdb(string valueName, out string sdbPath)
		{
			sdbPath = null;
			string text = valueName;
			if (text.EndsWith(".sdb", StringComparison.OrdinalIgnoreCase))
			{
				text = text.Substring(0, text.Length - 4);
			}
			if (text.Length == 0)
			{
				return SdbVerdict.Unknown;
			}
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\InstalledSDB\\" + text))
				{
					if (registryKey == null)
					{
						return SdbVerdict.Unknown;
					}
					string text2 = registryKey.GetValue("DatabaseDescription") as string;
					sdbPath = registryKey.GetValue("DatabasePath") as string;
					if (string.IsNullOrEmpty(text2))
					{
						return SdbVerdict.Unknown;
					}
					return (text2.IndexOf("fault tolerant heap", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("faulttolerantheap", StringComparison.OrdinalIgnoreCase) >= 0) ? SdbVerdict.Fth : SdbVerdict.NotFth;
				}
			}
			catch
			{
				return SdbVerdict.Unknown;
			}
		}

		private static void InvalidateCache()
		{
			lock (lk)
			{
				cached = null;
				cachedTicks = 0L;
			}
		}

		public static List<Shim> Detect()
		{
			lock (lk)
			{
				if (cached != null && DateTime.UtcNow.Ticks - cachedTicks < 20000000)
				{
					return new List<Shim>(cached);
				}
			}
			List<Shim> collection = Scan();
			lock (lk)
			{
				cached = collection;
				cachedTicks = DateTime.UtcNow.Ticks;
			}
			return new List<Shim>(collection);
		}

		private static List<Shim> Scan()
		{
			List<Shim> list = new List<Shim>();
			HashSet<string> hashSet = StateNames();
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Custom"))
				{
					if (registryKey == null)
					{
						return list;
					}
					string[] subKeyNames = registryKey.GetSubKeyNames();
					foreach (string text in subKeyNames)
					{
						using (RegistryKey registryKey2 = registryKey.OpenSubKey(text))
						{
							if (registryKey2 == null)
							{
								continue;
							}
							string[] valueNames = registryKey2.GetValueNames();
							foreach (string text2 in valueNames)
							{
								if (text2.EndsWith(".sdb", StringComparison.OrdinalIgnoreCase))
								{
									string sdbPath;
									SdbVerdict sdbVerdict = ClassifySdb(text2, out sdbPath);
									if (sdbVerdict != 0 && (sdbVerdict != SdbVerdict.Unknown || hashSet.Contains(text)))
									{
										list.Add(new Shim
										{
											Exe = text,
											ValueName = text2,
											SdbPath = sdbPath
										});
									}
								}
							}
						}
					}
				}
			}
			catch
			{
			}
			return list;
		}

		public static bool NeedsRepair()
		{
			return Detect().Count > 0;
		}

		public static string Describe()
		{
			if (!FeatureEnabled())
			{
				return Lang.T("fth.off");
			}
			List<Shim> list = Detect();
			if (list.Count == 0)
			{
				if (!RepairedByPavise)
				{
					return Lang.T("fth.clean");
				}
				return Lang.T("fth.cleared");
			}
			List<string> list2 = new List<string>();
			foreach (Shim item in list)
			{
				if (!list2.Contains(item.Exe))
				{
					list2.Add(item.Exe);
				}
			}
			int velocity;
			int windowMinutes;
			if (TryReadTrigger(out velocity, out windowMinutes))
			{
				return Lang.F("fth.hit.why", string.Join(" ", list2.ToArray()), windowMinutes, velocity);
			}
			return Lang.F("fth.hit", string.Join(" ", list2.ToArray()));
		}

		public static bool Repair()
		{
			lock (lk)
			{
				List<Shim> list = Detect();
				if (list.Count == 0)
				{
					return true;
				}
				List<string> list2 = new List<string>();
				foreach (Shim item in list)
				{
					if (!HasName(list2, item.Exe))
					{
						list2.Add(item.Exe);
					}
				}
				if (!AddExclusions(list2))
				{
					Logger.Log(Lang.T("log.fthtweak.1"));
					return false;
				}
				int num = 0;
				foreach (Shim item2 in list)
				{
					if (DropPointer(item2))
					{
						num++;
					}
					else
					{
						Logger.Log(Lang.T("log.fthtweak.2") + item2.Exe + " " + item2.ValueName);
					}
				}
				InvalidateCache();
				Logger.Log(Lang.F("log.fthtweak.3", num, string.Join(" ", list2.ToArray())));
				return num == list.Count;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				bool flag = RestorePointers();
				flag &= RestoreExclusions();
				InvalidateCache();
				if (flag)
				{
					Logger.Log(Lang.T("log.fthtweak.4"));
				}
				else
				{
					Logger.Log(Lang.T("log.fthtweak.5"));
				}
				return flag;
			}
		}

		private static bool AddExclusions(List<string> exes)
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH", true))
				{
					if (registryKey == null)
					{
						return false;
					}
					object value = registryKey.GetValue("ExclusionList");
					if (value != null)
					{
						RegistryValueKind registryValueKind = RegistryValueKind.Unknown;
						try
						{
							registryValueKind = registryKey.GetValueKind("ExclusionList");
						}
						catch
						{
						}
						if (registryValueKind != RegistryValueKind.MultiString)
						{
							Logger.Warn(Lang.T("log.fthtweak.6") + registryValueKind);
							return false;
						}
					}
					string[] array = value as string[];
					if (Settings.LoadStr("FthExclusionBak", "").Length == 0)
					{
						string text = ((array == null) ? "__pavise_absent__" : EncodeList(array));
						Settings.SaveStr("FthExclusionBak", text);
						if (Settings.LoadStr("FthExclusionBak", "") != text)
						{
							return false;
						}
					}
					List<string> list = new List<string>();
					if (array != null)
					{
						list.AddRange(array);
					}
					foreach (string ex in exes)
					{
						if (!HasName(list, ex))
						{
							list.Add(ex);
						}
					}
					if (array != null && ListsEqual(array, list.ToArray()))
					{
						return true;
					}
					registryKey.SetValue("ExclusionList", list.ToArray(), RegistryValueKind.MultiString);
					string[] array2 = registryKey.GetValue("ExclusionList") as string[];
					if (array2 == null)
					{
						return false;
					}
					foreach (string ex2 in exes)
					{
						if (!HasName(array2, ex2))
						{
							return false;
						}
					}
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		private static bool RestoreExclusions()
		{
			string text = Settings.LoadStr("FthExclusionBak", "");
			if (text.Length == 0)
			{
				return true;
			}
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\FTH", true))
				{
					if (registryKey == null)
					{
						return false;
					}
					if (text == "__pavise_absent__")
					{
						registryKey.DeleteValue("ExclusionList", false);
						if (registryKey.GetValue("ExclusionList") != null)
						{
							return false;
						}
					}
					else
					{
						string[] array = DecodeList(text);
						if (array == null)
						{
							return false;
						}
						registryKey.SetValue("ExclusionList", array, RegistryValueKind.MultiString);
						string[] a = registryKey.GetValue("ExclusionList") as string[];
						if (!ListsEqual(a, array))
						{
							return false;
						}
					}
				}
				Settings.Remove("FthExclusionBak");
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool DropPointer(Shim shim)
		{
			try
			{
				string name = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Custom\\" + shim.Exe;
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(name, true))
				{
					if (registryKey == null)
					{
						return false;
					}
					object value = registryKey.GetValue(shim.ValueName);
					if (value == null)
					{
						return true;
					}
					RegistryValueKind valueKind;
					try
					{
						valueKind = registryKey.GetValueKind(shim.ValueName);
					}
					catch
					{
						return false;
					}
					if (valueKind == RegistryValueKind.Unknown)
					{
						return false;
					}
					object[] array = new object[7] { shim.Exe, '\u001f', shim.ValueName, '\u001f', null, null, null };
					int num = (int)valueKind;
					array[4] = num.ToString();
					array[5] = '\u001f';
					array[6] = EncodeValue(value);
					string text = string.Concat(array);
					string text2 = Settings.LoadStr("FthCustomBak", "");
					if (!HasBackupRecord(text2, shim.Exe, shim.ValueName))
					{
						string text3 = ((text2.Length == 0) ? text : (text2 + '\u001e' + text));
						Settings.SaveStr("FthCustomBak", text3);
						if (Settings.LoadStr("FthCustomBak", "") != text3)
						{
							return false;
						}
					}
					registryKey.DeleteValue(shim.ValueName, false);
					return registryKey.GetValue(shim.ValueName) == null;
				}
			}
			catch
			{
				return false;
			}
		}

		private static bool RestorePointers()
		{
			string text = Settings.LoadStr("FthCustomBak", "");
			if (text.Length == 0)
			{
				return true;
			}
			bool flag = true;
			string[] array = text.Split('\u001e');
			foreach (string text2 in array)
			{
				if (text2.Length == 0)
				{
					continue;
				}
				string[] array2 = text2.Split('\u001f');
				if (array2.Length < 4)
				{
					flag = false;
					continue;
				}
				int result;
				if (!int.TryParse(array2[2], out result))
				{
					flag = false;
					continue;
				}
				if (!Enum.IsDefined(typeof(RegistryValueKind), result) || result == 0)
				{
					flag = false;
					continue;
				}
				object obj = DecodeValue(array2[3], (RegistryValueKind)result);
				if (obj == null)
				{
					flag = false;
					continue;
				}
				try
				{
					using (RegistryKey registryKey = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Custom\\" + array2[0]))
					{
						if (registryKey == null)
						{
							flag = false;
							continue;
						}
						registryKey.SetValue(array2[1], obj, (RegistryValueKind)result);
						if (registryKey.GetValue(array2[1]) == null)
						{
							flag = false;
						}
					}
				}
				catch
				{
					flag = false;
				}
			}
			if (flag)
			{
				Settings.Remove("FthCustomBak");
			}
			return flag;
		}

		internal static string EncodeValue(object value)
		{
			if (value is byte[])
			{
				return "b" + Convert.ToBase64String((byte[])value);
			}
			if (value is string[])
			{
				return "m" + EncodeList((string[])value);
			}
			return "s" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture)));
		}

		internal static object DecodeValue(string repr, RegistryValueKind kind)
		{
			try
			{
				if (repr.Length == 0)
				{
					return null;
				}
				if (repr[0] == 'b')
				{
					return (kind == RegistryValueKind.Binary) ? Convert.FromBase64String(repr.Substring(1)) : null;
				}
				if (repr[0] == 'm')
				{
					return (kind == RegistryValueKind.MultiString) ? DecodeList(repr.Substring(1)) : null;
				}
				if (repr[0] != 's')
				{
					return null;
				}
				if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString && kind != RegistryValueKind.DWord && kind != RegistryValueKind.QWord)
				{
					return null;
				}
				string @string = Encoding.UTF8.GetString(Convert.FromBase64String(repr.Substring(1)));
				switch (kind)
				{
				case RegistryValueKind.DWord:
				{
					int result2;
					return int.TryParse(@string, NumberStyles.Integer, CultureInfo.InvariantCulture, out result2) ? ((object)result2) : null;
				}
				case RegistryValueKind.QWord:
				{
					long result;
					return long.TryParse(@string, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? ((object)result) : null;
				}
				default:
					return @string;
				}
			}
			catch
			{
				return null;
			}
		}

		internal static string EncodeList(string[] list)
		{
			StringBuilder stringBuilder = new StringBuilder();
			for (int i = 0; i < list.Length; i++)
			{
				if (i > 0)
				{
					stringBuilder.Append('\0');
				}
				stringBuilder.Append(list[i]);
			}
			return Convert.ToBase64String(Encoding.UTF8.GetBytes(stringBuilder.ToString()));
		}

		internal static string[] DecodeList(string encoded)
		{
			try
			{
				string @string = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
				string[] result;
				if (@string.Length != 0)
				{
					char[] separator = new char[1];
					result = @string.Split(separator);
				}
				else
				{
					result = new string[0];
				}
				return result;
			}
			catch
			{
				return null;
			}
		}

		private static bool ListsEqual(string[] a, string[] b)
		{
			if (a == null || b == null)
			{
				return a == b;
			}
			if (a.Length != b.Length)
			{
				return false;
			}
			for (int i = 0; i < a.Length; i++)
			{
				if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
				{
					return false;
				}
			}
			return true;
		}

		internal static bool HasBackupRecord(string stored, string exe, string valueName)
		{
			if (string.IsNullOrEmpty(stored))
			{
				return false;
			}
			string[] array = stored.Split('\u001e');
			foreach (string text in array)
			{
				if (text.Length != 0)
				{
					string[] array2 = text.Split('\u001f');
					if (array2.Length >= 2 && string.Equals(array2[0], exe, StringComparison.OrdinalIgnoreCase) && string.Equals(array2[1], valueName, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool HasName(IEnumerable<string> list, string value)
		{
			if (list == null)
			{
				return false;
			}
			foreach (string item in list)
			{
				if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}
	}
}
