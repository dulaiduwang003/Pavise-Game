using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class IrqPriorityTweak
	{
		private const string EnumRoot = "SYSTEM\\CurrentControlSet\\Enum\\";

		private const string PolicySub = "\\Device Parameters\\Interrupt Management\\Affinity Policy";

		private const string ValueName = "DevicePriority";

		private const string JournalKey = "IrqPrio";

		private static readonly object lk = new object();

		public static bool HasResidue
		{
			get
			{
				return Settings.LoadStr("IrqPrio", "").Length > 0;
			}
		}

		public static bool RestoreOnly(string deviceId)
		{
			if (!string.IsNullOrEmpty(deviceId))
			{
				return RestoreScope(deviceId);
			}
			return false;
		}

		public static bool RestoreAll()
		{
			return RestoreScope(null);
		}

		internal static bool TryTouchedDevices(out List<string> deviceIds)
		{
			lock (lk)
			{
				deviceIds = new List<string>();
				string value;
				List<KeyValuePair<string, string>> entries;
				if (!Settings.TryLoadStr("IrqPrio", out value) || !TryJournal(value, out entries))
				{
					return false;
				}
				foreach (KeyValuePair<string, string> item in entries)
				{
					deviceIds.Add(item.Key);
				}
				return true;
			}
		}

		private static bool RestoreScope(string deviceId)
		{
			lock (lk)
			{
				string value;
				if (!Settings.TryLoadStr("IrqPrio", out value))
				{
					return false;
				}
				string remaining;
				bool restoreJournalSucceeded = RestoreJournal(value, deviceId, RestoreOne, out remaining);
				return Settings.SaveStr("IrqPrio", remaining) && Settings.LoadStr("IrqPrio", "") == remaining && restoreJournalSucceeded;
			}
		}

		private static bool TryJournal(string raw, out List<KeyValuePair<string, string>> entries)
		{
			entries = DecodeJournal(raw);
			if (!string.IsNullOrEmpty(raw) && entries.Count != raw.Split(';').Length)
			{
				return false;
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, string> entry in entries)
			{
				int result;
				if (!hashSet.Add(entry.Key) || (entry.Value != "-" && !int.TryParse(entry.Value, out result)))
				{
					return false;
				}
			}
			return true;
		}

		internal static bool RestoreJournal(string raw, string selected, Func<string, string, bool> restore, out string remaining)
		{
			remaining = raw ?? "";
			List<KeyValuePair<string, string>> entries;
			if (!TryJournal(raw, out entries))
			{
				return false;
			}
			bool result = true;
			for (int entriesIndex = entries.Count - 1; entriesIndex >= 0; entriesIndex--)
			{
				if (selected == null || string.Equals(entries[entriesIndex].Key, selected, StringComparison.OrdinalIgnoreCase))
				{
					bool restoreJournalReady = false;
					try
					{
						restoreJournalReady = restore(entries[entriesIndex].Key, entries[entriesIndex].Value);
					}
					catch
					{
					}
					if (restoreJournalReady)
					{
						entries.RemoveAt(entriesIndex);
					}
					else
					{
						result = false;
					}
				}
			}
			remaining = EncodeJournal(entries);
			return result;
		}

		private static bool RestoreOne(string deviceId, string original)
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + deviceId + "\\Device Parameters\\Interrupt Management\\Affinity Policy", true))
				{
					if (registryKey == null)
					{
						return false;
					}
					int result = 0;
					if (original != "-" && !int.TryParse(original, out result))
					{
						return false;
					}
					if (original == "-")
					{
						registryKey.DeleteValue("DevicePriority", false);
					}
					else
					{
						registryKey.SetValue("DevicePriority", result, RegistryValueKind.DWord);
					}
					return (original == "-") ? (registryKey.GetValue("DevicePriority") == null) : object.Equals(registryKey.GetValue("DevicePriority"), result);
				}
			}
			catch
			{
				return false;
			}
		}

		internal static string EncodeJournal(List<KeyValuePair<string, string>> entries)
		{
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, string> entry in entries)
			{
				list.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Key)) + "|" + entry.Value);
			}
			return string.Join(";", list.ToArray());
		}

		internal static List<KeyValuePair<string, string>> DecodeJournal(string raw)
		{
			List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
			if (string.IsNullOrEmpty(raw))
			{
				return list;
			}
			string[] rawParts = raw.Split(';');
			foreach (string entryText in rawParts)
			{
				string[] entryTextParts = entryText.Split('|');
				if (entryTextParts.Length != 2)
				{
					continue;
				}
				try
				{
					string @string = Encoding.UTF8.GetString(Convert.FromBase64String(entryTextParts[0]));
					if (@string.Length > 0)
					{
						list.Add(new KeyValuePair<string, string>(@string, entryTextParts[1]));
					}
				}
				catch
				{
				}
			}
			return list;
		}
	}
}
