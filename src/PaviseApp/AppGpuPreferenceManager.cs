using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal sealed class AppGpuPreferenceManager
	{
		private sealed class Record
		{
			internal char Phase;

			internal string Path;

			internal string Name;

			internal string Original;

			internal string RestoreTarget;

			internal bool Receipt;
		}

		private readonly IAppGpuPreferenceControl control;

		private readonly IAppGpuPreferenceLedger ledger;

		private List<Record> records = new List<Record>();

		private string observedLedger;

		private string attemptedLedger;

		private bool loaded;

		private bool dirty;

		private bool busy;

		private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

		public bool Supported
		{
			get
			{
				try
				{
					return control.Supported;
				}
				catch
				{
					return false;
				}
			}
		}

		public bool HasResidue
		{
			get
			{
				lock (GpuPrefStage.MutationGate)
				{
					return !Load() || records.Count != 0 || dirty;
				}
			}
		}

		internal AppGpuPreferenceManager(IAppGpuPreferenceControl control, IAppGpuPreferenceLedger ledger)
		{
			if (control == null || ledger == null)
			{
				throw new ArgumentNullException();
			}
			this.control = control;
			this.ledger = ledger;
		}

		public bool HasManagedPath(string path)
		{
			lock (GpuPrefStage.MutationGate)
			{
				return !Load() || Find(path) != null;
			}
		}

		public AppGpuPreferenceResult Prepare(string exePath, out AppGpuPreferenceChange change)
		{
			change = null;
			lock (GpuPrefStage.MutationGate)
			{
				if (busy)
				{
					return AppGpuPreferenceResult.Busy;
				}
				busy = true;
				try
				{
					string path;
					if (!NormalizePath(exePath, out path))
					{
						return AppGpuPreferenceResult.InvalidPath;
					}
					if (!Load())
					{
						return AppGpuPreferenceResult.JournalFailed;
					}
					if (Find(path) != null)
					{
						return AppGpuPreferenceResult.AlreadyPresent;
					}
					if (records.Count >= 128)
					{
						return AppGpuPreferenceResult.JournalFailed;
					}
					if (!Supported)
					{
						return AppGpuPreferenceResult.Unsupported;
					}
					if (!control.IsExecutablePresent(path))
					{
						return AppGpuPreferenceResult.InvalidPath;
					}
					string value;
					if (!Read(path, out value))
					{
						return AppGpuPreferenceResult.ReadFailed;
					}
					bool staged;
					string original;
					if (!control.TryGetStagedOriginal(path, out staged, out original))
					{
						return AppGpuPreferenceResult.Busy;
					}
					string text = (staged ? RestoreText(value, original) : value);
					if (!ValidPreference(text))
					{
						return AppGpuPreferenceResult.ReadFailed;
					}
					change = new AppGpuPreferenceChange(this, path, value, text, staged, original);
					return AppGpuPreferenceResult.Success;
				}
				catch
				{
					return AppGpuPreferenceResult.ReadFailed;
				}
				finally
				{
					busy = false;
				}
			}
		}

		public AppGpuPreferenceResult Apply(AppGpuPreferenceChange change, bool confirmExisting)
		{
			return Apply(change, confirmExisting, null);
		}

		internal AppGpuPreferenceResult Apply(AppGpuPreferenceChange change, bool confirmExisting, Func<bool> stillEligible)
		{
			lock (GpuPrefStage.MutationGate)
			{
				if (busy)
				{
					return AppGpuPreferenceResult.Busy;
				}
				busy = true;
				try
				{
					if (change == null || !object.ReferenceEquals(change.Owner, this))
					{
						return AppGpuPreferenceResult.Changed;
					}
					if (!Load())
					{
						return AppGpuPreferenceResult.JournalFailed;
					}
					if (Find(change.ExePath) != null)
					{
						return AppGpuPreferenceResult.AlreadyPresent;
					}
					if (records.Count >= 128)
					{
						return AppGpuPreferenceResult.JournalFailed;
					}
					if (change.NeedsConfirmation && !confirmExisting)
					{
						return AppGpuPreferenceResult.NeedsConfirmation;
					}
					if (!Supported)
					{
						return AppGpuPreferenceResult.Unsupported;
					}
					if (!control.IsExecutablePresent(change.ExePath))
					{
						return AppGpuPreferenceResult.InvalidPath;
					}
					string value;
					if (!Read(change.ExePath, out value))
					{
						return AppGpuPreferenceResult.ReadFailed;
					}
					if (!string.Equals(value, change.CurrentRaw, StringComparison.Ordinal))
					{
						return AppGpuPreferenceResult.Changed;
					}
					bool staged;
					string original;
					if (!control.TryGetStagedOriginal(change.ExePath, out staged, out original))
					{
						return AppGpuPreferenceResult.Busy;
					}
					if (staged != change.Staged || (staged && original != change.StageOriginal))
					{
						return AppGpuPreferenceResult.Changed;
					}
					if (stillEligible != null && !stillEligible())
					{
						return AppGpuPreferenceResult.Changed;
					}
					if (!control.TryReleaseStage(change.ExePath))
					{
						return AppGpuPreferenceResult.Busy;
					}
					if (!Read(change.ExePath, out value))
					{
						return AppGpuPreferenceResult.ReadFailed;
					}
					if (!string.Equals(value, change.BaselineRaw, StringComparison.Ordinal))
					{
						return AppGpuPreferenceResult.Changed;
					}
					Record record = new Record();
					record.Path = change.ExePath;
					record.Name = change.Name;
					record.Original = value;
					record.Phase = (change.AlreadyLowPower ? 'U' : 'P');
					Record record2 = record;
					records.Add(record2);
					if (!Save())
					{
						return AppGpuPreferenceResult.JournalFailed;
					}
					if (record2.Phase == 'U')
					{
						return AppGpuPreferenceResult.Success;
					}
					string replacement = PrefFieldText.MergeField(value, "GpuPreference", "1");
					AppGpuPreferenceWriteResult appGpuPreferenceWriteResult = Write(record2.Path, value, replacement);
					switch (appGpuPreferenceWriteResult)
					{
					case AppGpuPreferenceWriteResult.NotIssued:
					case AppGpuPreferenceWriteResult.Changed:
						if (!Settle(record2))
						{
							return AppGpuPreferenceResult.JournalFailed;
						}
						return (appGpuPreferenceWriteResult == AppGpuPreferenceWriteResult.Changed) ? AppGpuPreferenceResult.Changed : AppGpuPreferenceResult.WriteFailed;
					default:
						return AppGpuPreferenceResult.RecoveryPending;
					case AppGpuPreferenceWriteResult.Written:
						record2.Receipt = true;
						if (!Read(record2.Path, out value))
						{
							return AppGpuPreferenceResult.ReadFailed;
						}
						if (PrefFieldText.ReadField(value, "GpuPreference") != "1")
						{
							record2.Phase = 'E';
							return Save() ? AppGpuPreferenceResult.Changed : AppGpuPreferenceResult.JournalFailed;
						}
						record2.Phase = 'O';
						return (!Save()) ? AppGpuPreferenceResult.JournalFailed : AppGpuPreferenceResult.Success;
					}
				}
				catch
				{
					return AppGpuPreferenceResult.RecoveryPending;
				}
				finally
				{
					busy = false;
				}
			}
		}

		public bool TryGetEntries(out List<AppGpuPreferenceEntry> entries)
		{
			entries = new List<AppGpuPreferenceEntry>();
			lock (GpuPrefStage.MutationGate)
			{
				if (busy || !Load())
				{
					return false;
				}
				busy = true;
				try
				{
					bool flag = false;
					foreach (Record record in records)
					{
						string value;
						bool flag2 = Read(record.Path, out value);
						if (flag2 && (record.Phase == 'O' || record.Phase == 'U') && PrefFieldText.ReadField(value, "GpuPreference") != "1")
						{
							record.Phase = 'E';
							flag = true;
						}
						AppGpuPreferenceState state = State(record);
						entries.Add(new AppGpuPreferenceEntry(record.Path, record.Name, state, value, !flag2));
					}
					return !flag || Save();
				}
				finally
				{
					busy = false;
				}
			}
		}

		public AppGpuPreferenceResult Remove(string exePath)
		{
			lock (GpuPrefStage.MutationGate)
			{
				if (busy)
				{
					return AppGpuPreferenceResult.Busy;
				}
				busy = true;
				try
				{
					if (!Load())
					{
						return AppGpuPreferenceResult.JournalFailed;
					}
					Record record = Find(exePath);
					return (record == null) ? AppGpuPreferenceResult.NotFound : RemoveRecord(record);
				}
				finally
				{
					busy = false;
				}
			}
		}

		public AppGpuPreferenceResult Forget(string exePath)
		{
			lock (GpuPrefStage.MutationGate)
			{
				if (busy)
				{
					return AppGpuPreferenceResult.Busy;
				}
				busy = true;
				try
				{
					if (!Load())
					{
						return AppGpuPreferenceResult.JournalFailed;
					}
					Record record = Find(exePath);
					if (record == null)
					{
						return AppGpuPreferenceResult.NotFound;
					}
					if (record.Phase != 'P' && record.Phase != 'R' && record.Phase != 'E')
					{
						return AppGpuPreferenceResult.Busy;
					}
					return (!Settle(record)) ? AppGpuPreferenceResult.JournalFailed : AppGpuPreferenceResult.Success;
				}
				finally
				{
					busy = false;
				}
			}
		}

		public bool RestoreAll()
		{
			lock (GpuPrefStage.MutationGate)
			{
				if (busy)
				{
					return false;
				}
				busy = true;
				try
				{
					if (!Load())
					{
						return false;
					}
					bool flag = true;
					foreach (Record item in new List<Record>(records))
					{
						if (RemoveRecord(item) != 0)
						{
							flag = false;
						}
					}
					return flag && records.Count == 0 && !dirty;
				}
				finally
				{
					busy = false;
				}
			}
		}

		public bool AbandonUnprovableForReset()
		{
			lock (GpuPrefStage.MutationGate)
			{
				if (busy)
				{
					return false;
				}
				busy = true;
				try
				{
					if (!Load())
					{
						return false;
					}
					foreach (Record item in new List<Record>(records))
					{
						string value;
						if ((item.Phase == 'R' || (item.Phase == 'P' && !item.Receipt)) && Read(item.Path, out value) && !(PrefFieldText.ReadField(value, "GpuPreference") != "1") && (item.Phase != 'P' || !(value == item.Original)) && (item.Phase != 'R' || !(value == item.RestoreTarget)))
						{
							Logger.Warn(Lang.T("log.appgpuabandon.1") + item.Name + Lang.T("log.appgpuabandon.2"));
							if (!Settle(item))
							{
								return false;
							}
						}
					}
					return records.Count == 0 && !dirty;
				}
				finally
				{
					busy = false;
				}
			}
		}

		public bool HealFromCrash()
		{
			lock (GpuPrefStage.MutationGate)
			{
				if (busy)
				{
					return false;
				}
				busy = true;
				try
				{
					if (!Load())
					{
						return false;
					}
					bool result = true;
					bool flag = false;
					foreach (Record item in new List<Record>(records))
					{
						if (item.Phase == 'S')
						{
							if (!Settle(item))
							{
								result = false;
							}
							continue;
						}
						string value;
						if (!Read(item.Path, out value))
						{
							result = false;
							continue;
						}
						string text = PrefFieldText.ReadField(value, "GpuPreference");
						if (item.Phase == 'P')
						{
							if (item.Receipt && text == "1")
							{
								item.Phase = 'O';
								flag = true;
							}
							else if (value == item.Original || text != "1")
							{
								if (!Settle(item))
								{
									result = false;
								}
							}
							else
							{
								result = false;
							}
						}
						else if (item.Phase == 'R')
						{
							if (value == item.RestoreTarget || text != "1")
							{
								if (!Settle(item))
								{
									result = false;
								}
							}
							else
							{
								result = false;
							}
						}
						else if ((item.Phase == 'O' || item.Phase == 'U') && text != "1")
						{
							item.Phase = 'E';
							flag = true;
						}
					}
					if ((flag || dirty) && !Save())
					{
						result = false;
					}
					return result;
				}
				finally
				{
					busy = false;
				}
			}
		}

		private AppGpuPreferenceResult RemoveRecord(Record record)
		{
			if (record.Phase == 'U' || record.Phase == 'E' || record.Phase == 'S')
			{
				if (!Settle(record))
				{
					return AppGpuPreferenceResult.JournalFailed;
				}
				return AppGpuPreferenceResult.Success;
			}
			string value;
			if (!Read(record.Path, out value))
			{
				return AppGpuPreferenceResult.ReadFailed;
			}
			string text = PrefFieldText.ReadField(value, "GpuPreference");
			if (text != "1" || (record.Phase == 'P' && value == record.Original) || (record.Phase == 'R' && value == record.RestoreTarget))
			{
				if (!Settle(record))
				{
					return AppGpuPreferenceResult.JournalFailed;
				}
				return AppGpuPreferenceResult.Success;
			}
			if (record.Phase == 'R' || (record.Phase == 'P' && !record.Receipt))
			{
				return AppGpuPreferenceResult.RecoveryPending;
			}
			string replacement = (record.RestoreTarget = RestoreText(value, record.Original));
			record.Phase = 'R';
			if (!Save())
			{
				record.Phase = 'O';
				record.RestoreTarget = null;
				return AppGpuPreferenceResult.JournalFailed;
			}
			switch (Write(record.Path, value, replacement))
			{
			case AppGpuPreferenceWriteResult.NotIssued:
				record.Phase = 'O';
				record.RestoreTarget = null;
				if (!Save())
				{
					return AppGpuPreferenceResult.JournalFailed;
				}
				return AppGpuPreferenceResult.WriteFailed;
			case AppGpuPreferenceWriteResult.Changed:
				if (!Read(record.Path, out value))
				{
					return AppGpuPreferenceResult.ReadFailed;
				}
				if (PrefFieldText.ReadField(value, "GpuPreference") != "1")
				{
					if (!Settle(record))
					{
						return AppGpuPreferenceResult.JournalFailed;
					}
					return AppGpuPreferenceResult.Success;
				}
				record.Phase = 'O';
				record.RestoreTarget = null;
				if (!Save())
				{
					return AppGpuPreferenceResult.JournalFailed;
				}
				return AppGpuPreferenceResult.Changed;
			default:
				return AppGpuPreferenceResult.RecoveryPending;
			case AppGpuPreferenceWriteResult.Written:
				if (!Read(record.Path, out value))
				{
					return AppGpuPreferenceResult.ReadFailed;
				}
				if (PrefFieldText.ReadField(value, "GpuPreference") == "1")
				{
					return AppGpuPreferenceResult.RecoveryPending;
				}
				if (!Settle(record))
				{
					return AppGpuPreferenceResult.JournalFailed;
				}
				return AppGpuPreferenceResult.Success;
			}
		}

		private bool Settle(Record record)
		{
			record.Phase = 'S';
			record.Receipt = false;
			record.RestoreTarget = null;
			if (!Save())
			{
				return false;
			}
			int val = records.IndexOf(record);
			records.Remove(record);
			if (Save())
			{
				return true;
			}
			records.Insert(Math.Min(val, records.Count), record);
			dirty = true;
			return false;
		}

		private bool Read(string path, out string value)
		{
			value = null;
			try
			{
				return control.TryRead(path, out value) && ValidPreference(value);
			}
			catch
			{
				return false;
			}
		}

		private AppGpuPreferenceWriteResult Write(string path, string expected, string replacement)
		{
			try
			{
				return control.CompareExchange(path, expected, replacement);
			}
			catch
			{
				return AppGpuPreferenceWriteResult.Unknown;
			}
		}

		private Record Find(string path)
		{
			foreach (Record record in records)
			{
				if (string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase))
				{
					return record;
				}
			}
			return null;
		}

		private static AppGpuPreferenceState State(Record record)
		{
			switch (record.Phase)
			{
			case 'O':
				return AppGpuPreferenceState.Owned;
			case 'U':
				return AppGpuPreferenceState.AlreadyLowPower;
			case 'E':
				return AppGpuPreferenceState.ExternalChange;
			case 'R':
				return AppGpuPreferenceState.PendingRestore;
			case 'S':
				return AppGpuPreferenceState.CleanupPending;
			default:
				return AppGpuPreferenceState.PendingApply;
			}
		}

		private static string RestoreText(string current, string original)
		{
			string text = PrefFieldText.RestoreField(current, original, "GpuPreference");
			if (text.Length != 0 || original != null)
			{
				return text;
			}
			return null;
		}

		private static bool NormalizePath(string input, out string path)
		{
			path = null;
			if (string.IsNullOrWhiteSpace(input) || input.Length > 32760 || input.IndexOf('\0') >= 0)
			{
				return false;
			}
			try
			{
				bool flag = input.Length >= 3 && char.IsLetter(input[0]) && input[1] == ':' && (input[2] == '\\' || input[2] == '/');
				bool flag2 = input.StartsWith("\\\\", StringComparison.Ordinal) && !input.StartsWith("\\\\.\\", StringComparison.Ordinal) && !input.StartsWith("\\\\?\\", StringComparison.Ordinal);
				if ((!flag && !flag2) || !string.Equals(Path.GetExtension(input), ".exe", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				path = Path.GetFullPath(input);
				return path.Length > 3 && path.IndexOf(':', 2) < 0 && Path.GetFileName(path).Length <= 260;
			}
			catch
			{
				return false;
			}
		}

		private static bool ValidPreference(string raw)
		{
			if (raw == null)
			{
				return true;
			}
			if (raw.Length > 16384 || raw.IndexOf('\0') >= 0)
			{
				return false;
			}
			int num = 0;
			string[] array = raw.Split(';');
			foreach (string text in array)
			{
				int num2 = text.IndexOf('=');
				if (num2 > 0 && string.Equals(text.Substring(0, num2).Trim(), "GpuPreference", StringComparison.OrdinalIgnoreCase) && ++num > 1)
				{
					return false;
				}
			}
			return true;
		}

		private bool Load()
		{
			string value;
			try
			{
				if (!ledger.TryRead(out value) || value == null)
				{
					return false;
				}
			}
			catch
			{
				return false;
			}
			if (loaded && value == observedLedger)
			{
				return true;
			}
			if (loaded && attemptedLedger != null && value == attemptedLedger)
			{
				observedLedger = value;
				dirty = false;
				attemptedLedger = null;
				return true;
			}
			if (loaded && dirty)
			{
				return false;
			}
			List<Record> items;
			if (!Decode(value, out items))
			{
				return false;
			}
			records = items;
			observedLedger = value;
			attemptedLedger = null;
			loaded = true;
			dirty = false;
			return true;
		}

		private bool Save()
		{
			string text = (attemptedLedger = Encode(records));
			dirty = true;
			try
			{
				ledger.TryWrite(text);
			}
			catch
			{
			}
			try
			{
				string value;
				if (!ledger.TryRead(out value) || value != text)
				{
					return false;
				}
				observedLedger = value;
				attemptedLedger = null;
				loaded = true;
				dirty = false;
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static string Encode(List<Record> items)
		{
			if (items.Count == 0)
			{
				return "";
			}
			StringBuilder stringBuilder = new StringBuilder("1\n");
			foreach (Record item in items)
			{
				stringBuilder.Append(item.Phase).Append('\t').Append(B64(item.Path))
					.Append('\t')
					.Append(B64(item.Name))
					.Append('\t')
					.Append(B64(item.Original))
					.Append('\t')
					.Append(B64(item.RestoreTarget))
					.Append('\n');
			}
			return stringBuilder.ToString();
		}

		private static bool Decode(string raw, out List<Record> items)
		{
			items = new List<Record>();
			if (raw.Length == 0)
			{
				return true;
			}
			if (raw.Length > 4194304 || !raw.StartsWith("1\n", StringComparison.Ordinal))
			{
				return false;
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string[] array = raw.Split('\n');
			if (array.Length < 3 || array[array.Length - 1] != "" || array.Length > 130)
			{
				return false;
			}
			try
			{
				for (int i = 1; i < array.Length - 1; i++)
				{
					string[] array2 = array[i].Split('\t');
					if (array2.Length != 5 || array2[0].Length != 1 || "UOPRES".IndexOf(array2[0][0]) < 0)
					{
						return false;
					}
					string text = UnB64(array2[1]);
					string text2 = UnB64(array2[2]);
					string text3 = UnB64(array2[3]);
					string text4 = UnB64(array2[4]);
					string path;
					if (!NormalizePath(text, out path) || !string.Equals(text, path, StringComparison.OrdinalIgnoreCase) || !hashSet.Add(text) || text2 == null || text2.Length > 260 || !ValidPreference(text3) || !ValidPreference(text4))
					{
						return false;
					}
					char c = array2[0][0];
					string text5 = PrefFieldText.ReadField(text3, "GpuPreference");
					if ((c == 'U' && text5 != "1") || ((c == 'O' || c == 'P' || c == 'R') && text5 == "1"))
					{
						return false;
					}
					if (c != 'R' && text4 != null)
					{
						return false;
					}
					if (c == 'R' && PrefFieldText.ReadField(text4, "GpuPreference") != text5)
					{
						return false;
					}
					items.Add(new Record
					{
						Path = text,
						Name = text2,
						Original = text3,
						RestoreTarget = text4,
						Phase = c,
						Receipt = (c == 'O')
					});
				}
				return true;
			}
			catch
			{
				items.Clear();
				return false;
			}
		}

		private static string B64(string value)
		{
			if (value != null)
			{
				return Convert.ToBase64String(StrictUtf8.GetBytes(value));
			}
			return "-";
		}

		private static string UnB64(string value)
		{
			if (!(value == "-"))
			{
				return StrictUtf8.GetString(Convert.FromBase64String(value));
			}
			return null;
		}
	}
}
