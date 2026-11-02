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
					string[] array = names;
					foreach (string text in array)
					{
						if (Query(text) != 4)
						{
							continue;
						}
						string text2 = lastRead;
						Receipt receipt = new Receipt();
						receipt.Name = text;
						receipt.State = Phase.Prepared;
						Receipt receipt2 = receipt;
						receipts.Add(text, receipt2);
						if (!Save())
						{
							string value;
							if (Read(out value) && value == text2)
							{
								receipts.Remove(text);
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
						if (Query(text) != 4)
						{
							Settle(receipt2);
							if (!FlushSettled())
							{
								ledgerLost = true;
								RestoreCore(out remain);
								return false;
							}
						}
						else if (Stop(text, out confirmed))
						{
							receipt2.State = Phase.Owned;
							receipt2.CanStart = true;
							receipt2.StopObserved = confirmed || Query(text) == 1;
							justStopped.Add(text);
							if (receipt2.StopObserved)
							{
								confirmedStopped.Add(text);
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
							if (Query(text) == 4)
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
			bool flag = true;
			string[] array = names;
			foreach (string text in array)
			{
				Receipt value;
				if (!receipts.TryGetValue(text, out value) || value.State == Phase.Settled)
				{
					continue;
				}
				int num = Query(text);
				switch (num)
				{
				case 0:
					flag = false;
					continue;
				default:
					value.RunningSeen = 0;
					break;
				case 4:
					break;
				}
				switch (num)
				{
				case 4:
					if (value.State == Phase.Owned && !value.StopObserved && ++value.RunningSeen < 2)
					{
						flag = false;
					}
					else
					{
						Settle(value);
					}
					break;
				default:
					if ((value.State == Phase.Owned && !value.StopObserved && num == 3) || (value.State == Phase.Prepared && (num == 2 || num == 3)) || (value.State == Phase.Restoring && (num == 2 || num == 3)))
					{
						flag = false;
					}
					else
					{
						Settle(value);
					}
					break;
				case 1:
					if (!value.CanStart)
					{
						flag = false;
						break;
					}
					value.StopObserved = true;
					value.State = Phase.Restoring;
					if (!Save())
					{
						flag = false;
						break;
					}
					switch (Query(text))
					{
					case 0:
						flag = false;
						break;
					default:
						Settle(value);
						break;
					case 1:
					{
						value.CanStart = false;
						Start(text);
						int num2 = Query(text);
						if (num2 == 4 || num2 == 7)
						{
							Settle(value);
						}
						else
						{
							flag = false;
						}
						break;
					}
					}
					break;
				}
			}
			if (!FlushSettled())
			{
				flag = false;
			}
			remain = PendingNames();
			if (flag)
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
			string[] array = names;
			foreach (string text in array)
			{
				if (receipts.ContainsKey(text))
				{
					list.Add(text);
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
				string[] array = ((raw == "1" && names.Length == 1) ? new string[1] { names[0] } : raw.Split('|'));
				string[] array2 = array;
				foreach (string text in array2)
				{
					if (!allowed.Contains(text) || result.ContainsKey(text))
					{
						return false;
					}
					result.Add(text, new Receipt
					{
						Name = text,
						State = Phase.Prepared,
						CanStart = true
					});
				}
				return result.Count > 0;
			}
			string[] array3 = raw.Split('\n');
			if (array3.Length < 2 || array3.Length - 1 > allowed.Count)
			{
				return false;
			}
			for (int j = 1; j < array3.Length; j++)
			{
				string[] array4 = array3[j].Split('\t');
				if (array4.Length != 3 || !allowed.Contains(array4[1]) || result.ContainsKey(array4[1]) || (array4[2] != "0" && array4[2] != "1"))
				{
					return false;
				}
				Phase phase;
				if (array4[0] == "P")
				{
					phase = Phase.Prepared;
				}
				else if (array4[0] == "O")
				{
					phase = Phase.Owned;
				}
				else if (array4[0] == "R")
				{
					phase = Phase.Restoring;
				}
				else
				{
					if (!(array4[0] == "S"))
					{
						return false;
					}
					phase = Phase.Settled;
				}
				if (phase == Phase.Prepared && array4[2] != "0")
				{
					return false;
				}
				result.Add(array4[1], new Receipt
				{
					Name = array4[1],
					State = phase,
					CanStart = (phase == Phase.Owned),
					StopObserved = (array4[2] == "1")
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
			string[] array = names;
			foreach (string text in array)
			{
				Receipt value;
				if (receipts.TryGetValue(text, out value))
				{
					string text2 = ((value.State == Phase.Prepared) ? "P" : ((value.State == Phase.Owned) ? "O" : ((value.State == Phase.Restoring) ? "R" : "S")));
					list2.Add(text2 + "\t" + text + "\t" + (value.StopObserved ? "1" : "0"));
				}
			}
			return string.Join("\n", list2.ToArray());
		}

		private bool Save()
		{
			string text = Encode();
			string value;
			if (!Write(text) || !Read(out value) || value != text)
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
