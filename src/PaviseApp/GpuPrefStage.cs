using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class GpuPrefStage
	{
		internal enum WriteResult
		{
			Written,
			NotIssued,
			Unconfirmed
		}

		private sealed class StageRecord
		{
			internal char Phase;

			internal string Path;

			internal string Original;
		}

		private const string GpuKey = "SOFTWARE\\Microsoft\\DirectX\\UserGpuPreferences";

		internal const string JournalKey = "GpuPrefStage";

		private static readonly object lk = new object();

		private static string pendingApplyReceipt;

		private static string pendingApplyNotIssued;

		private static string pendingRestoreNotIssued;

		private static string settledIdentity;

		internal static object MutationGate
		{
			get
			{
				return lk;
			}
		}

		public static bool Supported
		{
			get
			{
				return GpuInventory.Hybrid;
			}
		}

		public static bool HasResidue
		{
			get
			{
				string value;
				if (Settings.TryLoadStr("GpuPrefStage", out value))
				{
					return value.Length > 0;
				}
				return true;
			}
		}

		public static bool Stage(string exePath)
		{
			if (!ValidExecutablePath(exePath))
			{
				return false;
			}
			lock (lk)
			{
				if (!Supported)
				{
					return true;
				}
				if (AppGpuPreferences.HasManagedPath(exePath))
				{
					return true;
				}
				string value;
				if (!Settings.TryLoadStr("GpuPrefStage", out value))
				{
					return false;
				}
				if (value.Length != 0)
				{
					StageRecord record;
					if (!TryDecodeRecord(value, out record))
					{
						return false;
					}
					if (record.Phase == 'O' && !IsSettled(record) && string.Equals(record.Path, exePath, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
					if (!RestoreLocked())
					{
						return false;
					}
				}
				string text = exePath;
				try
				{
					text = Path.GetFileName(exePath);
				}
				catch
				{
				}
				try
				{
					string value2;
					if (!TryReadPreference(exePath, out value2))
					{
						return false;
					}
					if (!ValidPreferenceFields(value2))
					{
						return false;
					}
					string text2 = PrefFieldText.ReadField(value2, "GpuPreference");
					if (text2 == "1" || text2 == "2")
					{
						return true;
					}
					if (!string.IsNullOrEmpty(text2) && text2 != "0")
					{
						return false;
					}
					string value3 = EncodeJournal(exePath, value2);
					pendingApplyReceipt = (pendingApplyNotIssued = (pendingRestoreNotIssued = (settledIdentity = null)));
					if (!SaveJournal(value3))
					{
						return false;
					}
					string value4;
					if (!TryReadPreference(exePath, out value4) || !string.Equals(value4, value2, StringComparison.Ordinal))
					{
						pendingApplyNotIssued = value3;
						return false;
					}
					WriteResult writeResult = WriteAndVerify(exePath, PrefFieldText.MergeField(value2, "GpuPreference", "2"));
					if (writeResult != 0)
					{
						if (writeResult == WriteResult.NotIssued)
						{
							pendingApplyNotIssued = value3;
						}
						return false;
					}
					pendingApplyReceipt = value3;
					if (!SaveJournal(EncodeRecord('O', exePath, value2)))
					{
						return false;
					}
					pendingApplyReceipt = null;
					Logger.Log(Lang.T("log.gpuprefstage.1") + text + Lang.T("log.gpuprefstage.2"));
					return true;
				}
				catch
				{
					return false;
				}
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				return RestoreLocked();
			}
		}

		internal static bool TryReleaseForManualPreference(string path)
		{
			lock (lk)
			{
				string value;
				if (!Settings.TryLoadStr("GpuPrefStage", out value))
				{
					return false;
				}
				if (value.Length == 0)
				{
					return true;
				}
				StageRecord record;
				if (!TryDecodeRecord(value, out record))
				{
					return false;
				}
				return !string.Equals(path, record.Path, StringComparison.OrdinalIgnoreCase) || RestoreLocked();
			}
		}

		internal static bool TryGetStagedOriginal(string path, out bool staged, out string original)
		{
			staged = false;
			original = null;
			lock (lk)
			{
				string value;
				if (!Settings.TryLoadStr("GpuPrefStage", out value))
				{
					return false;
				}
				if (value.Length == 0)
				{
					return true;
				}
				StageRecord record;
				if (!TryDecodeRecord(value, out record))
				{
					return false;
				}
				if (!string.Equals(path, record.Path, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				if (record.Phase == 'S' || IsSettled(record))
				{
					return true;
				}
				string value2;
				if (!TryReadPreference(path, out value2))
				{
					return false;
				}
				if (PrefFieldText.ReadField(value2, "GpuPreference") != "2")
				{
					return true;
				}
				if (!ValidPreferenceFields(value2))
				{
					return false;
				}
				if (record.Phase == 'P' && string.Equals(value, pendingApplyNotIssued, StringComparison.Ordinal))
				{
					return true;
				}
				if (record.Phase != 'O' && (record.Phase != 'P' || !string.Equals(value, pendingApplyReceipt, StringComparison.Ordinal)) && (record.Phase != 'R' || !string.Equals(value, pendingRestoreNotIssued, StringComparison.Ordinal)))
				{
					return false;
				}
				staged = true;
				original = record.Original;
				return true;
			}
		}

		public static void HealFromCrash()
		{
			lock (lk)
			{
				if (HasResidue && RestoreLocked())
				{
					Logger.Log(Lang.T("log.gpuprefstage.5"));
				}
			}
		}

		internal static bool AbandonUnprovableForReset()
		{
			lock (lk)
			{
				string value;
				if (!Settings.TryLoadStr("GpuPrefStage", out value))
				{
					return false;
				}
				if (value.Length == 0)
				{
					return true;
				}
				string text = null;
				StageRecord record;
				if (TryDecodeRecord(value, out record))
				{
					if ((record.Phase != 'P' || string.Equals(value, pendingApplyReceipt, StringComparison.Ordinal) || string.Equals(value, pendingApplyNotIssued, StringComparison.Ordinal)) && (record.Phase != 'R' || string.Equals(value, pendingRestoreNotIssued, StringComparison.Ordinal)))
					{
						return false;
					}
					text = record.Path;
					try
					{
						text = Path.GetFileName(record.Path);
					}
					catch
					{
					}
				}
				if (!SaveJournal(""))
				{
					return false;
				}
				Logger.Warn(Lang.T("log.gpuprefstage.6") + (text ?? "?") + Lang.T("log.gpuprefstage.7"));
				return true;
			}
		}

		private static bool RestoreLocked()
		{
			string value;
			if (!Settings.TryLoadStr("GpuPrefStage", out value))
			{
				return false;
			}
			if (value.Length == 0)
			{
				return true;
			}
			StageRecord record;
			if (!TryDecodeRecord(value, out record))
			{
				return false;
			}
			if (record.Phase == 'S' || IsSettled(record))
			{
				return Settle(record);
			}
			if (record.Phase == 'P' && string.Equals(value, pendingApplyNotIssued, StringComparison.Ordinal))
			{
				return Settle(record);
			}
			string path = record.Path;
			string original = record.Original;
			string text = path;
			try
			{
				text = Path.GetFileName(path);
			}
			catch
			{
			}
			try
			{
				string value2;
				if (!TryReadPreference(path, out value2))
				{
					return false;
				}
				if (PrefFieldText.ReadField(value2, "GpuPreference") != "2")
				{
					return Settle(record);
				}
				if (!ValidPreferenceFields(value2))
				{
					return false;
				}
				if ((record.Phase == 'R' && !string.Equals(value, pendingRestoreNotIssued, StringComparison.Ordinal)) || (record.Phase == 'P' && !string.Equals(value, pendingApplyReceipt, StringComparison.Ordinal)))
				{
					return false;
				}
				string text2 = (pendingRestoreNotIssued = EncodeRecord('R', path, original));
				if (!SaveJournal(text2))
				{
					return false;
				}
				string value3;
				if (!TryReadPreference(path, out value3))
				{
					MarkRestoreNotIssued(record, text2);
					return false;
				}
				if (PrefFieldText.ReadField(value3, "GpuPreference") != "2")
				{
					return Settle(record);
				}
				if (!ValidPreferenceFields(value3) || !string.Equals(value3, value2, StringComparison.Ordinal))
				{
					MarkRestoreNotIssued(record, text2);
					return false;
				}
				string text3 = ((original != null) ? PrefFieldText.RestoreField(value2, original, "GpuPreference") : PrefFieldText.RemoveField(value2, "GpuPreference"));
				pendingRestoreNotIssued = null;
				WriteResult writeResult = WriteAndVerify(path, (text3.Length == 0 && original == null) ? null : text3);
				if (writeResult != 0)
				{
					if (writeResult == WriteResult.NotIssued)
					{
						MarkRestoreNotIssued(record, text2);
					}
					return false;
				}
				if (!Settle(record))
				{
					return false;
				}
				Logger.Log(Lang.T("log.gpuprefstage.3") + text);
				return true;
			}
			catch
			{
				Logger.Log(Lang.T("log.gpuprefstage.4"));
				return false;
			}
		}

		internal static string EncodeJournal(string exePath, string original)
		{
			return EncodeRecord('P', exePath, original);
		}

		internal static bool DecodeJournal(string raw, out string exePath, out string original)
		{
			exePath = null;
			original = null;
			StageRecord record;
			if (!TryDecodeRecord(raw, out record))
			{
				return false;
			}
			exePath = record.Path;
			original = record.Original;
			return true;
		}

		private static bool TryDecodeRecord(string raw, out StageRecord record)
		{
			record = null;
			if (string.IsNullOrEmpty(raw) || raw.Length > 262144)
			{
				return false;
			}
			string[] array = raw.Split('|');
			bool flag = array.Length == 2;
			if (!flag && (array.Length != 4 || array[0] != "2" || array[1].Length != 1 || "PORS".IndexOf(array[1][0]) < 0))
			{
				return false;
			}
			try
			{
				int num = ((!flag) ? 2 : 0);
				string path = UnB64(array[num]);
				string text = ((array[num + 1] == "-") ? null : UnB64(array[num + 1]));
				if (!ValidExecutablePath(path) || !ValidPreferenceFields(text))
				{
					return false;
				}
				string text2 = PrefFieldText.ReadField(text, "GpuPreference");
				if (text2 == "1" || text2 == "2")
				{
					return false;
				}
				record = new StageRecord
				{
					Phase = (flag ? 'P' : array[1][0]),
					Path = path,
					Original = text
				};
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static string EncodeRecord(char phase, string path, string original)
		{
			return "2|" + phase + "|" + B64(path) + "|" + ((original == null) ? "-" : B64(original));
		}

		private static bool Settle(StageRecord record)
		{
			settledIdentity = EncodeRecord('S', record.Path, record.Original);
			if (!SaveJournal(settledIdentity))
			{
				return false;
			}
			pendingApplyReceipt = (pendingApplyNotIssued = (pendingRestoreNotIssued = null));
			if (!SaveJournal(""))
			{
				return false;
			}
			settledIdentity = null;
			return true;
		}

		private static bool IsSettled(StageRecord record)
		{
			return string.Equals(settledIdentity, EncodeRecord('S', record.Path, record.Original), StringComparison.Ordinal);
		}

		private static void MarkRestoreNotIssued(StageRecord record, string restoring)
		{
			pendingRestoreNotIssued = restoring;
			if (SaveJournal(EncodeRecord('O', record.Path, record.Original)))
			{
				pendingRestoreNotIssued = null;
			}
		}

		private static bool ValidPreferenceFields(string value)
		{
			if (value == null)
			{
				return true;
			}
			if (value.Length > 65536 || value.IndexOf('\0') >= 0)
			{
				return false;
			}
			int num = 0;
			string[] array = value.Split(';');
			foreach (string text in array)
			{
				int num2 = text.IndexOf('=');
				string a = ((num2 < 0) ? text : text.Substring(0, num2)).Trim();
				if (string.Equals(a, "GpuPreference", StringComparison.OrdinalIgnoreCase) && (num2 < 0 || ++num > 1 || text.Substring(num2 + 1).Trim().Length == 0))
				{
					return false;
				}
			}
			return true;
		}

		private static bool ValidExecutablePath(string path)
		{
			try
			{
				return !string.IsNullOrWhiteSpace(path) && path.Length <= 32767 && path.IndexOf('\0') < 0 && Path.IsPathRooted(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		private static bool SaveJournal(string value)
		{
			string value2;
			if (Settings.SaveStr("GpuPrefStage", value) && Settings.TryLoadStr("GpuPrefStage", out value2))
			{
				return string.Equals(value, value2, StringComparison.Ordinal);
			}
			return false;
		}

		private static WriteResult WriteAndVerify(string path, string value)
		{
			WriteResult writeResult = TryWritePreference(path, value);
			if (writeResult != 0)
			{
				return writeResult;
			}
			string value2;
			if (!TryReadPreference(path, out value2) || !string.Equals(value2, value, StringComparison.Ordinal))
			{
				return WriteResult.Unconfirmed;
			}
			return WriteResult.Written;
		}

		private static bool TryReadPreference(string path, out string value)
		{
			value = null;
			try
			{
				using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\DirectX\\UserGpuPreferences"))
				{
					object obj = ((registryKey == null) ? null : registryKey.GetValue(path, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
					if (obj != null && (!(obj is string) || registryKey.GetValueKind(path) != RegistryValueKind.String))
					{
						return false;
					}
					value = obj as string;
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		private static WriteResult TryWritePreference(string path, string value)
		{
			bool flag = false;
			try
			{
				using (RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("SOFTWARE\\Microsoft\\DirectX\\UserGpuPreferences"))
				{
					if (registryKey == null)
					{
						return WriteResult.NotIssued;
					}
					flag = true;
					if (value == null)
					{
						registryKey.DeleteValue(path, false);
					}
					else
					{
						registryKey.SetValue(path, value, RegistryValueKind.String);
					}
					return WriteResult.Written;
				}
			}
			catch
			{
				return (!flag) ? WriteResult.NotIssued : WriteResult.Unconfirmed;
			}
		}

		private static string B64(string s)
		{
			return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
		}

		private static string UnB64(string s)
		{
			return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(s));
		}
	}
}
