using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal sealed class OptionalServicePauseEngine
	{
		private sealed class Entry
		{
			internal string Name;

			internal string Configuration;

			internal bool Owned;

			internal bool StopObserved;

			internal bool Restoring;

			internal long AcceptedAt;

			internal long NextAuditAt;
		}

		private sealed class RunningWindow
		{
			internal string Configuration;

			internal long Since;

			internal long LastSeen;
		}

		internal const string PcaPreferenceKey = "MatchPausePcaV1";

		private const long StopVerificationSeconds = 30L;

		private const int StartSettleWaitMs = 1500;

		private const int StartSettleStepMs = 100;

		internal static readonly string[] Names = new string[7] { "PrintNotify", "Spooler", "WSearch", "WMPNetworkSvc", "MapsBroker", "DiagTrack", "RetailDemo" };

		private static readonly long SettledAuditTicks = TimeSpan.FromSeconds(30.0).Ticks;

		private static readonly Stopwatch stopClock = Stopwatch.StartNew();

		private readonly IOptionalServiceControl control;

		private readonly IOptionalServiceLedger ledger;

		private readonly object gate = new object();

		private readonly Dictionary<string, Entry> receipts = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

		private readonly HashSet<string> settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private readonly HashSet<string> startNotIssued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<string, RunningWindow> unobservedRunning = new Dictionary<string, RunningWindow>(StringComparer.OrdinalIgnoreCase);

		private bool active;

		private int activeSelection;

		private bool stopObservationDirty;

		private string receiptBoot;

		private string lastWarning;

		private string lastFailure;

		private readonly Func<long> nowTicks;

		public bool Active
		{
			get
			{
				lock (gate)
				{
					return active;
				}
			}
		}

		public string LastFailure
		{
			get
			{
				lock (gate)
				{
					return lastFailure;
				}
			}
		}

		public int PendingStopCount
		{
			get
			{
				lock (gate)
				{
					int processedCount = 0;
					foreach (Entry value in receipts.Values)
					{
						if (!value.StopObserved)
						{
							processedCount++;
						}
					}
					return processedCount;
				}
			}
		}

		public bool HasResidue
		{
			get
			{
				lock (gate)
				{
					string raw;
					return receipts.Count != 0 || settled.Count != 0 || !ReadRaw(out raw) || raw.Length != 0;
				}
			}
		}

		internal OptionalServicePauseEngine(IOptionalServiceControl control, IOptionalServiceLedger ledger)
			: this(control, ledger, () => stopClock.Elapsed.Ticks)
		{
		}

		internal OptionalServicePauseEngine(IOptionalServiceControl control, IOptionalServiceLedger ledger, Func<long> nowTicks)
		{
			if (control == null || ledger == null || nowTicks == null)
			{
				throw new ArgumentNullException();
			}
			this.control = control;
			this.ledger = ledger;
			this.nowTicks = nowTicks;
		}

        internal string[] CaptureConfirmedStops()
        {
            lock (gate)
            {
                var result = new List<string>();
                if (!active) return result.ToArray();
                foreach (Entry entry in receipts.Values)
                {
                    OptionalServiceSnapshot snapshot;
                    if (entry.Owned && entry.StopObserved && !entry.Restoring && !settled.Contains(entry.Name)
                        && Query(entry.Name, out snapshot) && snapshot.Exists && snapshot.State == 1
                        && snapshot.Configuration == entry.Configuration)
                        result.Add(entry.Name);
                }
                return result.ToArray();
            }
        }

		internal static bool IsAllowed(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			switch (name.ToLowerInvariant())
			{
			case "printnotify":
			case "spooler":
			case "wsearch":
			case "wmpnetworksvc":
			case "mapsbroker":
			case "diagtrack":
			case "retaildemo":
			case "pcasvc":
				return true;
			default:
				return false;
			}
		}

		internal bool MatchesSelection(bool includePca, bool includeBase)
		{
			lock (gate)
			{
				return active && activeSelection == (includeBase ? 1 : 0);
			}
		}

		public bool Activate()
		{
			return Activate(null);
		}

		public bool Activate(Func<bool> mayContinue)
		{
			return Activate(mayContinue, false);
		}

		public bool Activate(Func<bool> mayContinue, bool includePca)
		{
			return Activate(mayContinue, includePca, true);
		}

		public bool Activate(Func<bool> mayContinue, bool includePca, bool includeBase)
		{
			return false;
		}

		public bool Restore()
		{
			lock (gate)
			{
				return RestoreCore();
			}
		}

		public bool AuditStops(Func<bool> mayContinue)
		{
			lock (gate)
			{
				if (!active || !MayContinue(mayContinue))
				{
					return true;
				}
				long nowTicksResult = nowTicks();
				foreach (Entry value in receipts.Values)
				{
					if (!MayContinue(mayContinue))
					{
						return true;
					}
					if (value.Restoring || settled.Contains(value.Name) || (value.StopObserved && nowTicksResult < value.NextAuditAt && nowTicksResult >= value.NextAuditAt - SettledAuditTicks))
					{
						continue;
					}
					OptionalServiceSnapshot snapshot;
					if (!Query(value.Name, out snapshot))
					{
						lastFailure = value.Name + ": paused state could not be verified";
						return false;
					}
					if (!snapshot.Exists || snapshot.StartType == 4 || snapshot.Configuration != value.Configuration)
					{
						lastFailure = value.Name + ": service ownership changed during the match";
						return false;
					}
					if (snapshot.State == 1)
					{
						if (!value.StopObserved)
						{
							value.StopObserved = true;
							stopObservationDirty = true;
						}
						value.NextAuditAt = nowTicksResult + SettledAuditTicks;
						continue;
					}
					if (value.StopObserved)
					{
						lastFailure = value.Name + ": service resumed after its confirmed stop";
						return false;
					}
					if (nowTicksResult - value.AcceptedAt < TimeSpan.FromSeconds(30.0).Ticks)
					{
						continue;
					}
					lastFailure = value.Name + ": stop confirmation timed out";
					return false;
				}
				bool saveStopObservationsSucceeded = SaveStopObservations();
				if (!saveStopObservationsSucceeded)
				{
					lastFailure = "Service stop confirmation could not be persisted";
				}
				return saveStopObservationsSucceeded;
			}
		}

		public bool ObserveStops()
		{
			lock (gate)
			{
				if (!active)
				{
					return true;
				}
				foreach (Entry value in receipts.Values)
				{
					OptionalServiceSnapshot snapshot;
					if (!value.StopObserved && !value.Restoring && !settled.Contains(value.Name) && Query(value.Name, out snapshot) && snapshot.Exists && snapshot.State == 1)
					{
						value.StopObserved = true;
						value.NextAuditAt = nowTicks() + SettledAuditTicks;
						stopObservationDirty = true;
					}
				}
				return SaveStopObservations();
			}
		}

		private bool SaveStopObservations()
		{
			if (!stopObservationDirty)
			{
				return true;
			}
			string raw;
			string boot;
			List<Entry> entries;
			if (!ReadRaw(out raw) || !Parse(raw, out boot, out entries) || boot != receiptBoot)
			{
				Warn("log.pausesvc.ledger", null);
				return false;
			}
			foreach (Entry item in entries)
			{
				Entry value;
				if (receipts.TryGetValue(item.Name, out value) && item.Configuration == value.Configuration)
				{
					item.StopObserved |= value.StopObserved;
				}
			}
			bool saveSucceeded = Save(entries, boot);
			if (saveSucceeded)
			{
				stopObservationDirty = false;
			}
			if (!saveSucceeded)
			{
				Warn("log.pausesvc.ledger", null);
			}
			return saveSucceeded;
		}

		private bool AbortAfterLedgerFailure()
		{
			Warn("log.pausesvc.ledger", null);
			RestoreCore();
			return false;
		}

		private bool RestoreCore()
		{
			active = false;
			activeSelection = 0;
			Dictionary<string, RunningWindow> dictionary = new Dictionary<string, RunningWindow>(unobservedRunning, StringComparer.OrdinalIgnoreCase);
			unobservedRunning.Clear();
			string raw;
			string boot;
			List<Entry> entries;
			if (!ReadRaw(out raw) || !Parse(raw, out boot, out entries))
			{
				Warn("log.pausesvc.ledger", null);
				return false;
			}
			if (entries.Count == 0 && receipts.Count == 0)
			{
				lastWarning = null;
				bool matchesSave = raw.Length == 0 || Save(entries, boot);
				if (matchesSave)
				{
					settled.Clear();
					startNotIssued.Clear();
					stopObservationDirty = false;
				}
				return matchesSave;
			}
			string boot2;
			if (!ReadBoot(out boot2))
			{
				return false;
			}
			bool allConditionsMet = boot != null && BootSessionIdentity.Valid(boot) != BootSessionIdentity.Valid(boot2);
			string boot3 = (allConditionsMet ? boot : boot2);
			if (!allConditionsMet && boot != null && boot != boot2)
			{
				if (!Save(new List<Entry>(), boot2))
				{
					return false;
				}
				entries.Clear();
				settled.Clear();
				startNotIssued.Clear();
				stopObservationDirty = false;
				dictionary.Clear();
				Logger.Log(Lang.T("log.pausesvc.boot"));
			}
			if (receiptBoot != boot2)
			{
				receipts.Clear();
				startNotIssued.Clear();
				if (receiptBoot != null)
				{
					settled.Clear();
					dictionary.Clear();
				}
			}
			receiptBoot = boot2;
			foreach (Entry receipt in receipts.Values)
			{
				List<Entry> list = entries;
				Predicate<Entry> match = (Entry e) => string.Equals(e.Name, receipt.Name, StringComparison.OrdinalIgnoreCase);
				Entry entry = list.Find(match);
				if (entry == null)
				{
					entries.Add(receipt);
					continue;
				}
				if (string.Equals(entry.Configuration, receipt.Configuration, StringComparison.Ordinal))
				{
					entry.Owned = true;
					entry.StopObserved |= receipt.StopObserved;
					if (startNotIssued.Contains(receipt.Name))
					{
						entry.Restoring = false;
					}
					else
					{
						entry.Restoring |= receipt.Restoring;
					}
					continue;
				}
				Warn("log.pausesvc.ledger", null);
				return false;
			}
			List<string> list2 = new List<string>();
			List<string> list3 = new List<string>();
			List<string> list4 = new List<string>();
			for (int entriesIndex = entries.Count - 1; entriesIndex >= 0; entriesIndex--)
			{
				Entry entry2 = entries[entriesIndex];
				if (settled.Contains(entry2.Name))
				{
					entries.RemoveAt(entriesIndex);
					continue;
				}
				OptionalServiceSnapshot snapshot;
				if (!Query(entry2.Name, out snapshot))
				{
					list2.Add(entry2.Name);
					continue;
				}
				bool restoreCoreReady = false;
				if (!snapshot.Exists || snapshot.StartType == 4 || !string.Equals(snapshot.Configuration, entry2.Configuration, StringComparison.Ordinal))
				{
					Logger.Log(Lang.F("log.pausesvc.changed", entry2.Name));
					restoreCoreReady = true;
				}
				else if (entry2.Owned && !entry2.Restoring && ((entry2.StopObserved && snapshot.State >= 2 && snapshot.State <= 7) || snapshot.State == 2 || snapshot.State == 5 || snapshot.State == 6 || snapshot.State == 7))
				{
					restoreCoreReady = true;
				}
				else if (entry2.Owned && !entry2.StopObserved && !entry2.Restoring && snapshot.State != 1)
				{
					RunningWindow value;
					dictionary.TryGetValue(entry2.Name, out value);
					if (snapshot.State == 4 && StableRunning(entry2, value))
					{
						Logger.Log(Lang.F("log.pausesvc.running", entry2.Name));
						restoreCoreReady = true;
					}
					else
					{
						list2.Add(entry2.Name);
					}
				}
				else if (snapshot.State == 4 || snapshot.State == 7)
				{
					restoreCoreReady = true;
				}
				else if (allConditionsMet || !entry2.Owned || (entry2.Restoring && snapshot.State == 1))
				{
					list3.Add(entry2.Name);
				}
				else if (snapshot.State == 1)
				{
					entry2.StopObserved = true;
					entry2.Restoring = true;
					receipts[entry2.Name] = entry2;
					startNotIssued.Remove(entry2.Name);
					if (!Save(entries, boot3))
					{
						entry2.Restoring = false;
						startNotIssued.Add(entry2.Name);
						Warn("log.pausesvc.ledger", null);
						return false;
					}
					OptionalServiceStartResult optionalServiceStartResult;
					try
					{
						optionalServiceStartResult = control.TryStart(entry2.Name, entry2.Configuration);
					}
					catch
					{
						list3.Add(entry2.Name);
						continue;
					}
					switch (optionalServiceStartResult)
					{
					case OptionalServiceStartResult.OwnershipChanged:
						restoreCoreReady = true;
						break;
					case OptionalServiceStartResult.NotIssued:
						entry2.Restoring = false;
						startNotIssued.Add(entry2.Name);
						break;
					}
					if (!restoreCoreReady)
					{
						OptionalServiceSnapshot snapshot2;
						bool querySucceeded = Query(entry2.Name, out snapshot2);
						if (querySucceeded && optionalServiceStartResult == OptionalServiceStartResult.Accepted && snapshot2.State == 2)
						{
							querySucceeded = WaitStartSettled(entry2.Name, out snapshot2);
						}
						if (querySucceeded)
						{
							bool anyConditionMet = !snapshot2.Exists || snapshot2.StartType == 4 || !string.Equals(snapshot2.Configuration, entry2.Configuration, StringComparison.Ordinal);
							bool allConditionsMet2 = optionalServiceStartResult == OptionalServiceStartResult.NotIssued && snapshot2.State >= 2 && snapshot2.State <= 7;
							if (anyConditionMet || allConditionsMet2 || snapshot2.State == 4 || snapshot2.State == 7)
							{
								if (anyConditionMet)
								{
									Logger.Log(Lang.F("log.pausesvc.changed", entry2.Name));
								}
								else if (snapshot2.State == 4 && optionalServiceStartResult == OptionalServiceStartResult.Accepted)
								{
									list4.Add(entry2.Name);
								}
								restoreCoreReady = true;
							}
						}
						if (!restoreCoreReady)
						{
							list2.Add(entry2.Name);
						}
					}
				}
				else
				{
					list2.Add(entry2.Name);
				}
				if (restoreCoreReady)
				{
					settled.Add(entry2.Name);
					startNotIssued.Remove(entry2.Name);
					receipts.Remove(entry2.Name);
					entries.RemoveAt(entriesIndex);
				}
			}
			if (list4.Count != 0)
			{
				Logger.Log(Lang.F("log.pausesvc.restore", string.Join(", ", list4.ToArray())));
			}
			bool saveSucceeded = Save(entries, boot3);
			if (saveSucceeded)
			{
				settled.Clear();
				stopObservationDirty = false;
			}
			if (!saveSucceeded)
			{
				Warn("log.pausesvc.ledger", null);
			}
			else if (list3.Count != 0)
			{
				Warn(allConditionsMet ? "log.pausesvc.unknownboot" : "log.pausesvc.unowned", string.Join(", ", list3.ToArray()));
			}
			else if (list2.Count != 0)
			{
				Warn("log.pausesvc.pending", string.Join(", ", list2.ToArray()));
			}
			else
			{
				lastWarning = null;
			}
			if (saveSucceeded)
			{
				return entries.Count == 0;
			}
			return false;
		}

		private bool StableRunning(Entry entry, RunningWindow window)
		{
			long sinceValue = nowTicks();
			if (window == null || sinceValue < window.LastSeen || !string.Equals(window.Configuration, entry.Configuration, StringComparison.Ordinal))
			{
				RunningWindow runningWindow = new RunningWindow();
				runningWindow.Configuration = entry.Configuration;
				runningWindow.Since = sinceValue;
				window = runningWindow;
			}
			window.LastSeen = sinceValue;
			if (sinceValue - window.Since >= TimeSpan.FromSeconds(30.0).Ticks)
			{
				return true;
			}
			unobservedRunning[entry.Name] = window;
			return false;
		}

		private bool WaitStartSettled(string name, out OptionalServiceSnapshot snapshot)
		{
			snapshot = null;
			bool waitStartSettledSucceeded = false;
			for (int i = 0; i < 1500; i += 100)
			{
				Thread.Sleep(100);
				waitStartSettledSucceeded = Query(name, out snapshot);
				if (!waitStartSettledSucceeded || snapshot.State != 2)
				{
					return waitStartSettledSucceeded;
				}
			}
			return waitStartSettledSucceeded;
		}

		private bool Query(string name, out OptionalServiceSnapshot snapshot)
		{
			snapshot = null;
			try
			{
				return IsAllowed(name) && control.TryQuery(name, out snapshot) && snapshot != null;
			}
			catch
			{
				return false;
			}
		}

		private bool PrintingIdle()
		{
			try
			{
				return control.IsPrintingIdle();
			}
			catch
			{
				return false;
			}
		}

		private static bool IsPrinting(string name)
		{
			if (!string.Equals(name, "PrintNotify", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(name, "Spooler", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		private static bool MayContinue(Func<bool> mayContinue)
		{
			try
			{
				return mayContinue == null || mayContinue();
			}
			catch
			{
				return false;
			}
		}

		private bool ReadBoot(out string boot)
		{
			boot = null;
			try
			{
				string identity;
				return control.TryGetBootIdentity(out identity) && NormalizeBoot(identity, out boot);
			}
			catch
			{
				return false;
			}
		}

		private static bool NormalizeBoot(string value, out string boot)
		{
			boot = null;
			if (BootSessionIdentity.Valid(value))
			{
				string[] valueParts = value.Split(':');
				boot = BootSessionIdentity.Create(new Guid(valueParts[1]), uint.Parse(valueParts[2], CultureInfo.InvariantCulture));
				return true;
			}
			Guid result;
			if (!Guid.TryParse(value, out result) || result == Guid.Empty)
			{
				return false;
			}
			boot = result.ToString("D");
			return true;
		}

		private bool ReadRaw(out string raw)
		{
			raw = null;
			try
			{
				return ledger.TryRead(out raw) && raw != null;
			}
			catch
			{
				return false;
			}
		}

		private bool Save(List<Entry> entries, string boot)
		{
			string message = "";
			if (entries.Count != 0)
			{
				if (string.IsNullOrEmpty(boot))
				{
					return false;
				}
				StringBuilder stringBuilder = new StringBuilder((BootSessionIdentity.Valid(boot) ? "v2\t" : "v1\t") + boot);
				foreach (Entry entry in entries)
				{
					stringBuilder.Append('\n').Append((!entry.Owned) ? 'P' : (entry.Restoring ? 'R' : (entry.StopObserved ? 'O' : 'A'))).Append('\t')
						.Append(entry.Name)
						.Append('\t')
						.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Configuration)));
				}
				message = stringBuilder.ToString();
			}
			try
			{
				string raw;
				return ledger.TryWrite(message) && ReadRaw(out raw) && string.Equals(message, raw, StringComparison.Ordinal);
			}
			catch
			{
				return false;
			}
		}

		private static bool Parse(string raw, out string boot, out List<Entry> entries)
		{
			boot = null;
			entries = new List<Entry>();
			if (raw.Length == 0)
			{
				return true;
			}
			if (raw.Length > 262144)
			{
				return false;
			}
			try
			{
				string[] rawParts = raw.Split('\n');
				string[] parts = rawParts[0].Split('\t');
				if (parts.Length != 2 || (parts[0] != "v1" && parts[0] != "v2") || !NormalizeBoot(parts[1], out boot) || parts[0] == "v2" != BootSessionIdentity.Valid(boot) || rawParts.Length > 9)
				{
					return false;
				}
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				for (int i = 1; i < rawParts.Length; i++)
				{
					string[] parts2 = rawParts[i].Split('\t');
					if (parts2.Length != 3 || (parts2[0] != "O" && parts2[0] != "P" && parts2[0] != "A" && parts2[0] != "R") || !IsAllowed(parts2[1]) || !hashSet.Add(parts2[1]))
					{
						return false;
					}
					string @string = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(parts2[2]));
					if (@string.Length == 0 || @string.Length > 8192)
					{
						return false;
					}
					entries.Add(new Entry
					{
						Name = parts2[1],
						Configuration = @string,
						Owned = (parts2[0] != "P"),
						StopObserved = (parts2[0] == "O" || parts2[0] == "R"),
						Restoring = (parts2[0] == "R")
					});
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		private void Warn(string key, string names)
		{
			string tResult = ((names == null) ? Lang.T(key) : Lang.F(key, names));
			if (!(tResult == lastWarning))
			{
				lastWarning = tResult;
				Logger.Log(tResult);
			}
		}
	}
}
