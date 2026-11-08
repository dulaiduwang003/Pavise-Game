using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class ServicePauser
	{
		private enum Phase
		{
			Prepared,
			Owned,
			Restoring,
			Settled
		}

		private sealed class Receipt
		{
			internal string Name;

			internal Phase State;

			internal bool CanStart;

			internal bool StopObserved;

			internal int RunningSeen;
		}

		private readonly string[] names;

		private readonly HashSet<string> allowed;

		private readonly string flag;

		private readonly object lk = new object();

		private readonly Dictionary<string, Receipt> receipts = new Dictionary<string, Receipt>(StringComparer.OrdinalIgnoreCase);

		private bool active;

		private bool loaded;

		private bool busy;

		private bool observationDirty;

		private string lastRead = "";

		public bool HasResidue
		{
			get
			{
				lock (lk)
				{
					if (receipts.Count != 0)
					{
						return true;
					}
					string value;
					return !Read(out value) || value == null || value.Length != 0;
				}
			}
		}

		public bool HadLedger
		{
			get
			{
				return HasResidue;
			}
		}

		public ServicePauser(string[] serviceNames, string flagKey)
		{
			if (serviceNames == null)
			{
				throw new ArgumentNullException("serviceNames");
			}
			names = (string[])serviceNames.Clone();
			allowed = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
			flag = flagKey;
		}

        internal string[] CaptureConfirmedStops()
        {
            lock (lk)
            {
                var result = new List<string>();
                if (!active) return result.ToArray();
                foreach (Receipt receipt in receipts.Values)
                    if (receipt.State == Phase.Owned && receipt.CanStart && receipt.StopObserved && Query(receipt.Name) == 1)
                        result.Add(receipt.Name);
                return result.ToArray();
            }
        }

		public bool Activate(out List<string> justStopped, out List<string> confirmedStopped, out bool ledgerLost)
		{
			justStopped = new List<string>();
			confirmedStopped = new List<string>();
			ledgerLost = false;
			lock (lk)
			{
				if (busy)
				{
					return false;
				}
				busy = true;
				try
				{
					if (active)
					{
						return ObserveStops();
					}
					if (!Load())
					{
						return false;
					}
					List<string> remain;
					if (receipts.Count != 0 && !RestoreCore(out remain))
					{
						return false;
					}
					string[] namesSnapshot = names;
					foreach (string entryText in namesSnapshot)
					{
						if (Query(entryText) != 4)
						{
							continue;
						}
						string lastReadSnapshot = lastRead;
						Receipt receipt = new Receipt();
						receipt.Name = entryText;
						receipt.State = Phase.Prepared;
						Receipt receipt2 = receipt;
						receipts.Add(entryText, receipt2);
						if (!Save())
						{
							string value;
							if (Read(out value) && value == lastReadSnapshot)
							{
								receipts.Remove(entryText);
							}
							else
							{
								receipt2.State = Phase.Settled;
							}
							ledgerLost = true;
							RestoreCore(out remain);
							return false;
						}
						bool confirmed;
						if (Query(entryText) != 4)
						{
							Settle(receipt2);
							if (!FlushSettled())
							{
								ledgerLost = true;
								RestoreCore(out remain);
								return false;
							}
						}
						else if (Stop(entryText, out confirmed))
						{
							receipt2.State = Phase.Owned;
							receipt2.CanStart = true;
							receipt2.StopObserved = confirmed || Query(entryText) == 1;
							justStopped.Add(entryText);
							if (receipt2.StopObserved)
							{
								confirmedStopped.Add(entryText);
							}
							if (!Save())
							{
								ledgerLost = true;
								RestoreCore(out remain);
								return false;
							}
						}
						else
						{
							if (Query(entryText) == 4)
							{
								receipt2.State = Phase.Settled;
							}
							if (!FlushSettled())
							{
								ledgerLost = true;
								RestoreCore(out remain);
								return false;
							}
						}
					}
					active = true;
					return true;
				}
				catch
				{
					return false;
				}
				finally
				{
					busy = false;
				}
			}
		}

		public bool Restore(out List<string> remain)
		{
			lock (lk)
			{
				remain = PendingNames();
				if (busy)
				{
					return false;
				}
				busy = true;
				try
				{
					return RestoreCore(out remain);
				}
				catch
				{
					remain = PendingNames();
					return false;
				}
				finally
				{
					busy = false;
				}
			}
		}

		private bool RestoreCore(out List<string> remain)
		{
			active = false;
			remain = PendingNames();
			if (!Load())
			{
				return false;
			}
			bool restoreCoreReady = true;
			string[] namesSnapshot = names;
			foreach (string entryText in namesSnapshot)
			{
				Receipt value;
				if (!receipts.TryGetValue(entryText, out value) || value.State == Phase.Settled)
				{
					continue;
				}
				int queryResult = Query(entryText);
				switch (queryResult)
				{
				case 0:
					restoreCoreReady = false;
					continue;
				default:
					value.RunningSeen = 0;
					break;
				case 4:
					break;
				}
				switch (queryResult)
				{
				case 4:
					if (value.State == Phase.Owned && !value.StopObserved && ++value.RunningSeen < 2)
					{
						restoreCoreReady = false;
					}
					else
					{
						Settle(value);
					}
					break;
				default:
					if ((value.State == Phase.Owned && !value.StopObserved && queryResult == 3) || (value.State == Phase.Prepared && (queryResult == 2 || queryResult == 3)) || (value.State == Phase.Restoring && (queryResult == 2 || queryResult == 3)))
					{
						restoreCoreReady = false;
					}
					else
					{
						Settle(value);
					}
					break;
				case 1:
					if (!value.CanStart)
					{
						restoreCoreReady = false;
						break;
					}
					value.StopObserved = true;
					value.State = Phase.Restoring;
					if (!Save())
					{
						restoreCoreReady = false;
						break;
					}
					switch (Query(entryText))
					{
					case 0:
						restoreCoreReady = false;
						break;
					default:
						Settle(value);
						break;
					case 1:
					{
						value.CanStart = false;
						Start(entryText);
						int queryResult2 = Query(entryText);
						if (queryResult2 == 4 || queryResult2 == 7)
						{
							Settle(value);
						}
						else
						{
							restoreCoreReady = false;
						}
						break;
					}
					}
					break;
				}
			}
			if (!FlushSettled())
			{
				restoreCoreReady = false;
			}
			remain = PendingNames();
			if (restoreCoreReady)
			{
				return receipts.Count == 0;
			}
			return false;
		}

		private bool ObserveStops()
		{
			foreach (Receipt value in receipts.Values)
			{
				if (value.State == Phase.Owned && !value.StopObserved && Query(value.Name) == 1)
				{
					value.StopObserved = true;
					observationDirty = true;
				}
			}
			if (observationDirty)
			{
				return Save();
			}
			return true;
		}

		private static void Settle(Receipt receipt)
		{
			receipt.State = Phase.Settled;
			receipt.CanStart = false;
		}

		private List<string> PendingNames()
		{
			List<string> list = new List<string>();
			string[] namesSnapshot = names;
			foreach (string entryText in namesSnapshot)
			{
				if (receipts.ContainsKey(entryText))
				{
					list.Add(entryText);
				}
			}
			return list;
		}

		private bool Load()
		{
			string value;
			if (!Read(out value) || value == null)
			{
				return false;
			}
			Dictionary<string, Receipt> result;
			if (!Parse(value, out result))
			{
				return false;
			}
			if (!loaded)
			{
				foreach (KeyValuePair<string, Receipt> item in result)
				{
					receipts.Add(item.Key, item.Value);
				}
				loaded = true;
			}
			else
			{
				foreach (KeyValuePair<string, Receipt> item2 in result)
				{
					Receipt value2;
					if (!receipts.TryGetValue(item2.Key, out value2))
					{
						receipts.Add(item2.Key, item2.Value);
					}
					else if (item2.Value.State == Phase.Settled)
					{
						Settle(value2);
					}
				}
			}
			lastRead = value;
			return true;
		}

		private bool Parse(string raw, out Dictionary<string, Receipt> result)
		{
			result = new Dictionary<string, Receipt>(StringComparer.OrdinalIgnoreCase);
			if (raw.Length == 0)
			{
				return true;
			}
			if (raw.Length > 4096)
			{
				return false;
			}
			if (!raw.StartsWith("2\n", StringComparison.Ordinal))
			{
				string[] textEntries = ((raw == "1" && names.Length == 1) ? new string[1] { names[0] } : raw.Split('|'));
				string[] textEntries2 = textEntries;
				foreach (string entryText in textEntries2)
				{
					if (!allowed.Contains(entryText) || result.ContainsKey(entryText))
					{
						return false;
					}
					result.Add(entryText, new Receipt
					{
						Name = entryText,
						State = Phase.Prepared,
						CanStart = true
					});
				}
				return result.Count > 0;
			}
			string[] rawParts = raw.Split('\n');
			if (rawParts.Length < 2 || rawParts.Length - 1 > allowed.Count)
			{
				return false;
			}
			for (int j = 1; j < rawParts.Length; j++)
			{
				string[] parts = rawParts[j].Split('\t');
				if (parts.Length != 3 || !allowed.Contains(parts[1]) || result.ContainsKey(parts[1]) || (parts[2] != "0" && parts[2] != "1"))
				{
					return false;
				}
				Phase phase;
				if (parts[0] == "P")
				{
					phase = Phase.Prepared;
				}
				else if (parts[0] == "O")
				{
					phase = Phase.Owned;
				}
				else if (parts[0] == "R")
				{
					phase = Phase.Restoring;
				}
				else
				{
					if (!(parts[0] == "S"))
					{
						return false;
					}
					phase = Phase.Settled;
				}
				if (phase == Phase.Prepared && parts[2] != "0")
				{
					return false;
				}
				result.Add(parts[1], new Receipt
				{
					Name = parts[1],
					State = phase,
					CanStart = (phase == Phase.Owned),
					StopObserved = (parts[2] == "1")
				});
			}
			return true;
		}

		private string Encode()
		{
			if (receipts.Count == 0)
			{
				return "";
			}
			List<string> list = new List<string>();
			list.Add("2");
			List<string> list2 = list;
			string[] namesSnapshot = names;
			foreach (string entryText in namesSnapshot)
			{
				Receipt value;
				if (receipts.TryGetValue(entryText, out value))
				{
					string message = ((value.State == Phase.Prepared) ? "P" : ((value.State == Phase.Owned) ? "O" : ((value.State == Phase.Restoring) ? "R" : "S")));
					list2.Add(message + "\t" + entryText + "\t" + (value.StopObserved ? "1" : "0"));
				}
			}
			return string.Join("\n", list2.ToArray());
		}

		private bool Save()
		{
			string encodeResult = Encode();
			string value;
			if (!Write(encodeResult) || !Read(out value) || value != encodeResult)
			{
				return false;
			}
			lastRead = value;
			observationDirty = false;
			return true;
		}

		private bool FlushSettled()
		{
			if (receipts.Count == 0)
			{
				return true;
			}
			if (!Save())
			{
				return false;
			}
			List<Receipt> list = new List<Receipt>();
			foreach (Receipt value in receipts.Values)
			{
				if (value.State == Phase.Settled)
				{
					list.Add(value);
				}
			}
			if (list.Count == 0)
			{
				return true;
			}
			foreach (Receipt item in list)
			{
				receipts.Remove(item.Name);
			}
			if (Save())
			{
				return true;
			}
			foreach (Receipt item2 in list)
			{
				receipts.Add(item2.Name, item2);
			}
			return false;
		}

		private int Query(string name)
		{
			try
			{
				return SvcState.Query(name);
			}
			catch
			{
				return 0;
			}
		}

		private bool Stop(string name, out bool confirmed)
		{
			confirmed = false;
			try
			{
				return SvcCtl.StopIfRunning(name, out confirmed);
			}
			catch
			{
				return false;
			}
		}

		private void Start(string name)
		{
			try
			{
				SvcCtl.EnsureStarted(name);
			}
			catch
			{
			}
		}

		private bool Read(out string value)
		{
			value = null;
			try
			{
				return Settings.TryLoadStr(flag, out value);
			}
			catch
			{
				return false;
			}
		}

		private bool Write(string value)
		{
			try
			{
				return Settings.SaveStr(flag, value);
			}
			catch
			{
				return false;
			}
		}
	}
}
