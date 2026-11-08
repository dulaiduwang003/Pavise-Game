using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal static class PerformanceActionStore
	{
		internal sealed class Receipt
		{
			internal string Id;

			internal string Target;

			internal string Original;

			internal string Expected;

			internal bool Committed;

			internal string VisualOwner;

			internal bool VisualRestored;
		}

		internal const string ListKey = "PerformanceActionReceiptsV1";

		private static readonly object gate = new object();

		[ThreadStatic]
		private static string lastChangeFailure;

		internal static string LastChangeFailure
		{
			get
			{
				return lastChangeFailure;
			}
		}

		private static bool ChangeFailed(string reason)
		{
			lastChangeFailure = reason;
			return false;
		}

		internal static string Slot(string id, string target)
		{
			using (SHA256 sHA = SHA256.Create())
			{
				return "PerformanceReceipt_" + BitConverter.ToString(sHA.ComputeHash(Encoding.UTF8.GetBytes(id + "\n" + target.ToUpperInvariant()))).Replace("-", "");
			}
		}

		private static string Encode(string value)
		{
			return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
		}

		private static string Decode(string value)
		{
			return Encoding.UTF8.GetString(Convert.FromBase64String(value));
		}

		internal static string Pack(Receipt r)
		{
			if (r.VisualRestored && string.IsNullOrEmpty(r.VisualOwner))
			{
				if (r.Id != "MatchVisualEffectsV1" || !r.Committed || r.Original != r.Expected || (r.Original != "0" && r.Original != "1"))
				{
					throw new FormatException("Invalid legacy visual terminal receipt");
				}
				return "visual-legacy-terminal1|1|" + Encode(r.Id) + "|" + Encode(r.Target) + "|" + Encode(r.Original) + "|" + Encode(r.Expected);
			}
			if (!string.IsNullOrEmpty(r.VisualOwner) || r.VisualRestored)
			{
				ValidateVisualOwner(r);
				return "visual2|" + (r.Committed ? "1" : "0") + "|" + Encode(r.Id) + "|" + Encode(r.Target) + "|" + Encode(r.Original) + "|" + Encode(r.Expected) + "|" + Encode(r.VisualOwner) + "|" + (r.VisualRestored ? "1" : "0");
			}
			return (r.Committed ? "1" : "0") + "|" + Encode(r.Id) + "|" + Encode(r.Target) + "|" + Encode(r.Original) + "|" + Encode(r.Expected);
		}

		private static void ValidateVisualOwner(Receipt r)
		{
			if (r.Id != "MatchVisualEffectsV1" || (r.Original != "0" && r.Original != "1") || (r.Expected != "0" && r.Expected != "1") || (r.VisualRestored && (!r.Committed || r.Expected != r.Original)))
			{
				throw new FormatException("Invalid owned visual effect receipt");
			}
			string[] parts = (r.VisualOwner ?? "").Split(':');
			long result;
			Guid result2;
			Guid result3;
			if (parts.Length != 4 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out result) || result <= 0 || result.ToString(CultureInfo.InvariantCulture) != parts[1] || !Guid.TryParseExact(parts[2], "N", out result2) || result2 == Guid.Empty || result2.ToString("N") != parts[2] || !Guid.TryParseExact(parts[3], "N", out result3) || result3 == Guid.Empty || result3.ToString("N") != parts[3] || new SecurityIdentifier(parts[0]).Value != parts[0])
			{
				throw new FormatException("Invalid owned visual effect receipt epoch");
			}
		}

		internal static Receipt Unpack(string text)
		{
			string[] parts = (text ?? "").Split('|');
			if (parts.Length == 6 && parts[0] == "visual-legacy-terminal1" && parts[1] == "1")
			{
				Receipt receipt = new Receipt();
				receipt.Id = Decode(parts[2]);
				receipt.Target = Decode(parts[3]);
				receipt.Original = Decode(parts[4]);
				receipt.Expected = Decode(parts[5]);
				receipt.Committed = true;
				receipt.VisualRestored = true;
				Receipt receipt2 = receipt;
				if (Pack(receipt2) != text)
				{
					throw new FormatException("Noncanonical legacy visual terminal receipt");
				}
				return receipt2;
			}
			if (parts.Length == 8 && parts[0] == "visual2")
			{
				if ((parts[1] != "0" && parts[1] != "1") || (parts[7] != "0" && parts[7] != "1"))
				{
					throw new FormatException("Invalid owned visual effect receipt state");
				}
				Receipt receipt3 = new Receipt();
				receipt3.Committed = parts[1] == "1";
				receipt3.Id = Decode(parts[2]);
				receipt3.Target = Decode(parts[3]);
				receipt3.Original = Decode(parts[4]);
				receipt3.Expected = Decode(parts[5]);
				receipt3.VisualOwner = Decode(parts[6]);
				receipt3.VisualRestored = parts[7] == "1";
				Receipt receipt4 = receipt3;
				ValidateVisualOwner(receipt4);
				if (Pack(receipt4) != text)
				{
					throw new FormatException("Noncanonical owned visual effect receipt");
				}
				return receipt4;
			}
			if (parts.Length != 5 || (parts[0] != "0" && parts[0] != "1"))
			{
				throw new FormatException("Invalid performance receipt");
			}
			Receipt receipt5 = new Receipt();
			receipt5.Committed = parts[0] == "1";
			receipt5.Id = Decode(parts[1]);
			receipt5.Target = Decode(parts[2]);
			receipt5.Original = Decode(parts[3]);
			receipt5.Expected = Decode(parts[4]);
			return receipt5;
		}

		private static string Read(string key)
		{
			string value;
			if (!Settings.TryLoadStr(key, out value))
			{
				throw new InvalidOperationException("Unreadable performance receipt");
			}
			return value ?? "";
		}

		private static bool Save(string key, string value)
		{
			if (Settings.SaveStrDurable(key, value))
			{
				return Read(key) == value;
			}
			return false;
		}

		internal static List<Receipt> All()
		{
			lock (gate)
			{
				List<Receipt> list = new List<Receipt>();
				List<string> list2 = new List<string>();
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				string[] readParts = Read("PerformanceActionReceiptsV1").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (string item in readParts)
				{
					if (hashSet.Add(item))
					{
						list2.Add(item);
					}
				}
				string[] names;
				if (!Settings.TryLoadStoredNames("PerformanceReceipt_", out names))
				{
					throw new InvalidOperationException("Unreadable performance receipt names");
				}
				string[] namesSnapshot = names;
				foreach (string item2 in namesSnapshot)
				{
					if (hashSet.Add(item2))
					{
						list2.Add(item2);
					}
				}
				foreach (string item3 in list2)
				{
					bool matchesStartsWith = item3.StartsWith("PerformanceReceipt_", StringComparison.OrdinalIgnoreCase) && item3.Length == "PerformanceReceipt_".Length + 64;
					int processedCount = "PerformanceReceipt_".Length;
					while (matchesStartsWith && processedCount < item3.Length)
					{
						matchesStartsWith = (item3[processedCount] >= '0' && item3[processedCount] <= '9') || (item3[processedCount] >= 'a' && item3[processedCount] <= 'f') || (item3[processedCount] >= 'A' && item3[processedCount] <= 'F');
						processedCount++;
					}
					if (!matchesStartsWith)
					{
						throw new FormatException("Invalid performance receipt key: " + item3);
					}
					string readResult = Read(item3);
					if (readResult.Length != 0)
					{
						Receipt receipt = Unpack(readResult);
						if (string.IsNullOrEmpty(receipt.Id) || string.IsNullOrEmpty(receipt.Target) || !string.Equals(item3, Slot(receipt.Id, receipt.Target), StringComparison.OrdinalIgnoreCase))
						{
							throw new FormatException("Invalid performance receipt ownership: " + item3);
						}
						list.Add(receipt);
					}
				}
				return list;
			}
		}

		internal static bool TryAll(out List<Receipt> receipts)
		{
			receipts = null;
			if (!Monitor.TryEnter(gate))
			{
				return false;
			}
			try
			{
				receipts = All();
				return true;
			}
			finally
			{
				Monitor.Exit(gate);
			}
		}

		private static bool Track(string key)
		{
			List<string> list = new List<string>(Read("PerformanceActionReceiptsV1").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries));
			if (!list.Contains(key))
			{
				list.Add(key);
			}
			return Save("PerformanceActionReceiptsV1", string.Join(";", list.ToArray()));
		}

		private static bool Clear(string key, string expected = null)
		{
			if (expected != null)
			{
				string readResult = Read(key);
				if (readResult.Length == 0)
				{
					return true;
				}
				if (readResult != expected)
				{
					return false;
				}
			}
			if (!Save(key, ""))
			{
				return false;
			}
			List<string> list = new List<string>(Read("PerformanceActionReceiptsV1").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries));
			list.Remove(key);
			return Save("PerformanceActionReceiptsV1", string.Join(";", list.ToArray()));
		}

		internal static bool Change(string id, string target, string wanted, Func<string> read, Func<string, bool> write, Func<string, bool> satisfied)
		{
			return Change(id, target, wanted, read, write, satisfied, null);
		}

		internal static bool Change(string id, string target, string wanted, Func<string> read, Func<string, bool> write, Func<string, bool> satisfied, Func<Receipt, string, Receipt> reconcile)
		{
			lock (gate)
			{
				lastChangeFailure = null;
				string key = Slot(id, target);
				string readResult = Read(key);
				Receipt receipt = ((readResult.Length == 0) ? null : Unpack(readResult));
				if (receipt != null && (!string.IsNullOrEmpty(receipt.VisualOwner) || receipt.VisualRestored))
				{
					return ChangeFailed("Owned visual receipts require the shared SPI capability path");
				}
				string originalValue = read();
				if (receipt != null && (receipt.Id != id || !string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase)))
				{
					return ChangeFailed("Recovery record ownership does not match the target");
				}
				if (satisfied(originalValue))
				{
					if (receipt == null || receipt.Committed)
					{
						return true;
					}
					return Save(key, Pack(new Receipt
					{
						Id = id,
						Target = target,
						Original = receipt.Original,
						Expected = originalValue,
						Committed = true
					})) || ChangeFailed("Could not commit the recovery record");
				}
				bool changeReady = false;
				if (receipt != null && reconcile != null)
				{
					for (int i = 0; i < 3; i++)
					{
						Receipt receipt2 = reconcile(receipt, originalValue);
						if (receipt2 == null || receipt2.Id != id || !string.Equals(receipt2.Target, target, StringComparison.OrdinalIgnoreCase))
						{
							return ChangeFailed("Recovery snapshot cannot be reconciled");
						}
						string readResult2 = read();
						if (readResult2 != originalValue)
						{
							originalValue = readResult2;
							continue;
						}
						if (!Track(key) || !Save(key, Pack(receipt2)))
						{
							return ChangeFailed("Could not save the reconciled recovery record");
						}
						receipt = receipt2;
						changeReady = true;
						break;
					}
					if (!changeReady)
					{
						return ChangeFailed("Configuration kept changing during reconciliation");
					}
				}
				if (!changeReady && receipt != null && id == "AutoGameMitigationsV1" && wanted == "off32" && receipt.Original.Split(';').Length == 5)
				{
					return ChangeFailed("Legacy recovery record must be reconciled before enabling SimExec");
				}
				if (!changeReady && receipt != null && receipt.Committed && originalValue != receipt.Expected)
				{
					if (id != "AutoGameMitigationsV1")
					{
						return ChangeFailed(Clear(key, readResult) ? "Configuration changed externally; previous ownership released" : "Configuration changed externally; recovery record could not be cleared");
					}
					return ChangeFailed("Configuration changed externally; recovery record retained");
				}
				if (receipt == null)
				{
					Receipt receipt3 = new Receipt();
					receipt3.Id = id;
					receipt3.Target = target;
					receipt3.Original = originalValue;
					receipt3.Expected = wanted;
					receipt = receipt3;
				}
				if (!changeReady && (!Track(key) || !Save(key, Pack(receipt))))
				{
					return ChangeFailed("Could not save the recovery record");
				}
				if (!satisfied(originalValue))
				{
					if (!changeReady && (receipt.Committed || receipt.Expected != wanted))
					{
						Receipt receipt4 = new Receipt();
						receipt4.Id = id;
						receipt4.Target = target;
						receipt4.Original = receipt.Original;
						receipt4.Expected = wanted;
						receipt4.Committed = false;
						receipt = receipt4;
						if (!Save(key, Pack(receipt)))
						{
							return ChangeFailed("Could not prepare the recovery record");
						}
					}
					if (!write(wanted))
					{
						return ChangeFailed("Configuration write did not complete");
					}
				}
				originalValue = read();
				if (!satisfied(originalValue))
				{
					return ChangeFailed("Configuration did not match the requested state after writing");
				}
				receipt.Expected = originalValue;
				receipt.Committed = true;
				return Save(key, Pack(receipt)) || ChangeFailed("Could not commit the recovery record");
			}
		}

		internal static bool Restore(Receipt r, Func<string> read, Func<string, bool> write, Func<string, bool> preparedMatch)
		{
			if (!string.IsNullOrEmpty(r.VisualOwner) || r.VisualRestored)
			{
				return false;
			}
			lock (gate)
			{
				string readResult = Read(Slot(r.Id, r.Target));
				if (readResult.Length == 0)
				{
					return true;
				}
				if (readResult != Pack(r))
				{
					return false;
				}
				string readResult2 = read();
				if ((r.Committed ? (readResult2 == r.Expected) : preparedMatch(readResult2)) && readResult2 != r.Original && (!write(r.Original) || read() != r.Original))
				{
					return false;
				}
				return Clear(Slot(r.Id, r.Target), readResult);
			}
		}

		internal static bool RestoreComponents(Receipt r, Func<string> read, Func<string, bool> write, Func<string, string> merge)
		{
			if (!string.IsNullOrEmpty(r.VisualOwner) || r.VisualRestored)
			{
				return false;
			}
			lock (gate)
			{
				string readResult = Read(Slot(r.Id, r.Target));
				if (readResult.Length == 0)
				{
					return true;
				}
				if (readResult != Pack(r))
				{
					return false;
				}
				for (int i = 0; i < 3; i++)
				{
					string readResult2 = read();
					string mergeResult = merge(readResult2);
					if (mergeResult == null)
					{
						return false;
					}
					if (!(read() != readResult2))
					{
						if (mergeResult != readResult2 && (!write(mergeResult) || read() != mergeResult))
						{
							return false;
						}
						return Clear(Slot(r.Id, r.Target), readResult);
					}
				}
				return false;
			}
		}

		internal static bool ChangeVisualOwned(SpiOwnership.SpiOwnerAccess access, string target, string wanted, Func<string> read, Func<string, bool> write)
		{
			if (access == null || access.Operation != 0)
			{
				throw new InvalidOperationException("Visual apply needs its exact SPI owner capability");
			}
			access.Assert(access.Stamp, false);
			string manualReceiptOwner = access.Stamp.ManualReceiptOwner;
			if (!(wanted != "0") || !(wanted != "1"))
			{
				lock (gate)
				{
					lastChangeFailure = null;
					string key = Slot("MatchVisualEffectsV1", target);
					string readResult = Read(key);
					Receipt receipt = ((readResult.Length == 0) ? null : Unpack(readResult));
					if (receipt != null && (receipt.Id != "MatchVisualEffectsV1" || receipt.Target != target || receipt.VisualOwner != manualReceiptOwner || receipt.VisualRestored || !receipt.Committed))
					{
						return ChangeFailed("Visual recovery owner or pending state differs");
					}
					string originalValue = read();
					if (originalValue != "0" && originalValue != "1")
					{
						return ChangeFailed("Visual original readback unavailable");
					}
					if (receipt != null && originalValue != receipt.Expected)
					{
						return ChangeFailed("Visual value changed; retain exact recovery owner");
					}
					if (receipt == null)
					{
						Receipt receipt2 = new Receipt();
						receipt2.Id = "MatchVisualEffectsV1";
						receipt2.Target = target;
						receipt2.Original = originalValue;
						receipt2.Expected = wanted;
						receipt2.VisualOwner = manualReceiptOwner;
						receipt = receipt2;
					}
					else
					{
						if (originalValue == wanted)
						{
							return true;
						}
						receipt.Expected = wanted;
						receipt.Committed = false;
					}
					if (!Track(key) || !Save(key, Pack(receipt)))
					{
						return ChangeFailed("Could not save owned visual write intent");
					}
					access.Assert(access.Stamp, true);
					if (originalValue != wanted && !write(wanted))
					{
						return ChangeFailed("Owned visual write was not confirmed");
					}
					if (read() != wanted)
					{
						return ChangeFailed("Owned visual readback differs");
					}
					receipt.Committed = true;
					return Save(key, Pack(receipt)) || ChangeFailed("Could not commit owned visual recovery record");
				}
			}
			throw new FormatException("Invalid visual effect value");
		}

		internal static bool RestoreVisualOwned(SpiOwnership.SpiOwnerAccess access, Receipt receipt, Func<string> read, Func<string, bool> write)
		{
			if (access == null || access.Operation != SpiOwnerOperation.Restore || receipt == null)
			{
				throw new InvalidOperationException("Visual restoration needs its original SPI owner capability");
			}
			access.Assert(access.Stamp, false);
			if (receipt.VisualOwner != access.Stamp.ManualReceiptOwner)
			{
				return false;
			}
			lock (gate)
			{
				string key = Slot(receipt.Id, receipt.Target);
				string readResult = Read(key);
				if (readResult.Length == 0 || readResult != Pack(receipt) || receipt.Id != "MatchVisualEffectsV1")
				{
					return false;
				}
				if (receipt.VisualRestored)
				{
					return true;
				}
				string readResult2 = read();
				if (readResult2 != receipt.Original)
				{
					if (readResult2 != receipt.Expected)
					{
						return false;
					}
					access.Assert(access.Stamp, true);
					if (!write(receipt.Original) || read() != receipt.Original)
					{
						return false;
					}
				}
				Receipt receipt2 = new Receipt();
				receipt2.Id = receipt.Id;
				receipt2.Target = receipt.Target;
				receipt2.Original = receipt.Original;
				receipt2.Expected = receipt.Original;
				receipt2.VisualOwner = receipt.VisualOwner;
				receipt2.Committed = true;
				receipt2.VisualRestored = true;
				Receipt r = receipt2;
				return Save(key, Pack(r));
			}
		}

		internal static bool ClearVisualTerminalAfterClose(SpiOwnership.SpiOwnerAccess closedAccess)
		{
			if (closedAccess == null)
			{
				throw new ArgumentNullException("closedAccess");
			}
			closedAccess.AssertMetadata(closedAccess.Stamp, true);
			string manualReceiptOwner = closedAccess.Stamp.ManualReceiptOwner;
			IDictionary<string, string> dictionary = SpiManualClosureManifest.Read(closedAccess);
			lock (gate)
			{
				List<Receipt> list = All();
				foreach (Receipt item in list)
				{
					string value;
					if (item.Id == "MatchVisualEffectsV1" && (item.VisualOwner != manualReceiptOwner || !item.VisualRestored || !dictionary.TryGetValue(Slot(item.Id, item.Target), out value) || SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes(Pack(item))) != value))
					{
						return false;
					}
				}
				foreach (Receipt item2 in list)
				{
					if (item2.Id == "MatchVisualEffectsV1" && !Clear(Slot(item2.Id, item2.Target), Pack(item2)))
					{
						return false;
					}
				}
				return true;
			}
		}

		internal static string ExactVisualBytes(Receipt row)
		{
			lock (gate)
			{
				string readResult = Read(Slot(row.Id, row.Target));
				if (row.Id != "MatchVisualEffectsV1" || readResult != Pack(row))
				{
					throw new InvalidDataException("Visual receipt is not its exact canonical stored bytes");
				}
				return readResult;
			}
		}

		internal static bool RestoreLegacyVisualBeforeInitialization(Receipt row)
		{
			SharedSpiLegacyMigration.RequireReceipt(row);
			lock (gate)
			{
				if (!string.IsNullOrEmpty(row.VisualOwner) || Read(Slot(row.Id, row.Target)) != Pack(row))
				{
					return false;
				}
				string readResult = PerformanceActionBackend.Read(row.Id, row.Target);
				if (row.VisualRestored)
				{
					return readResult == row.Original;
				}
				if (readResult != row.Original && (readResult != row.Expected || !PerformanceActionBackend.Write(row.Id, row.Target, row.Original) || PerformanceActionBackend.Read(row.Id, row.Target) != row.Original))
				{
					return false;
				}
				SharedSpiLegacyMigration.RequireReceipt(row);
				return Save(Slot(row.Id, row.Target), Pack(new Receipt
				{
					Id = row.Id,
					Target = row.Target,
					Original = row.Original,
					Expected = row.Original,
					Committed = true,
					VisualRestored = true
				}));
			}
		}

		internal static bool ClearLegacyVisualTerminal(Receipt row)
		{
			lock (gate)
			{
				if (row == null || row.Id != "MatchVisualEffectsV1" || !string.IsNullOrEmpty(row.VisualOwner) || !row.VisualRestored)
				{
					return false;
				}
				return Clear(Slot(row.Id, row.Target), Pack(row));
			}
		}
	}
}
