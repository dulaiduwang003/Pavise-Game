using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal sealed class SpiLegacyMigrationPlan
	{
		internal readonly string UserSid;

		internal readonly string Nonce;

		internal readonly byte[] Manifest;

		internal readonly SortedDictionary<string, SpiLegacyMigrationMember> Members;

		private SpiLegacyMigrationPlan(string sid, string nonce, SortedDictionary<string, SpiLegacyMigrationMember> members)
		{
			SpiOwnerIdentity.Sid(sid);
			SpiOwnerIdentity.Nonce(nonce);
			if (members.Count == 0 || members.Count > 10)
			{
				throw new InvalidDataException("Legacy SPI migration member set is empty or oversized");
			}
			UserSid = sid;
			Nonce = nonce;
			Members = members;
			StringBuilder stringBuilder = new StringBuilder("pavise-spi-legacy-migration-1\nuser_sid\t" + sid + "\nnonce\t" + nonce + "\n");
			foreach (SpiLegacyMigrationMember value in members.Values)
			{
				stringBuilder.Append(value.Slot).Append('\t').Append(value.Target)
					.Append('\t')
					.Append(value.SourceHash)
					.Append('\t')
					.Append(value.TerminalHash)
					.Append('\n');
			}
			Manifest = new UTF8Encoding(false, true).GetBytes(stringBuilder.ToString());
		}

		internal static string ReceiptHash(PerformanceActionStore.Receipt row)
		{
			return SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes(PerformanceActionStore.ExactVisualBytes(row)));
		}

		private static void ValidateTarget(string target)
		{
			MatchVisualEffects.Effect[] effects = MatchVisualEffects.Effects;
			foreach (MatchVisualEffects.Effect effect in effects)
			{
				if (effect.Name == target)
				{
					return;
				}
			}
			throw new InvalidDataException("Unknown legacy SPI target");
		}

		internal static SpiLegacyMigrationPlan Prepare(string sid, SpiOwnerFileStore store, IList<PerformanceActionStore.Receipt> rows, bool resume)
		{
			SpiLegacyMigrationPlan spiLegacyMigrationPlan;
			if (resume)
			{
				spiLegacyMigrationPlan = Read(sid, store.ReadLegacy("legacy-migration.tsv"));
			}
			else
			{
				SortedDictionary<string, SpiLegacyMigrationMember> sortedDictionary = new SortedDictionary<string, SpiLegacyMigrationMember>(StringComparer.Ordinal);
				foreach (PerformanceActionStore.Receipt row in rows)
				{
					ValidateTarget(row.Target);
					if (row.Id != "MatchVisualEffectsV1" || !string.IsNullOrEmpty(row.VisualOwner) || row.VisualRestored || (row.Original != "0" && row.Original != "1") || row.Expected != "0")
					{
						throw new InvalidDataException("Unowned legacy SPI receipt is not the exact former reduced-effects shape");
					}
					PerformanceActionStore.Receipt receipt = new PerformanceActionStore.Receipt();
					receipt.Id = row.Id;
					receipt.Target = row.Target;
					receipt.Original = row.Original;
					receipt.Expected = row.Original;
					receipt.Committed = true;
					receipt.VisualRestored = true;
					PerformanceActionStore.Receipt r = receipt;
					SpiLegacyMigrationMember spiLegacyMigrationMember = new SpiLegacyMigrationMember(row.Target, ReceiptHash(row), SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes(PerformanceActionStore.Pack(r))));
					if (sortedDictionary.ContainsKey(spiLegacyMigrationMember.Slot))
					{
						throw new InvalidDataException("Duplicate legacy SPI receipt");
					}
					sortedDictionary.Add(spiLegacyMigrationMember.Slot, spiLegacyMigrationMember);
				}
				spiLegacyMigrationPlan = new SpiLegacyMigrationPlan(sid, Guid.NewGuid().ToString("N"), sortedDictionary);
			}
			spiLegacyMigrationPlan.RequireAllCurrent();
			return spiLegacyMigrationPlan;
		}

		internal static SpiLegacyMigrationPlan Read(string sid, byte[] bytes)
		{
			if (bytes == null || bytes.Length == 0 || bytes.Length > 32768)
			{
				throw new InvalidDataException("Legacy SPI migration evidence missing");
			}
			string[] getStringParts = new UTF8Encoding(false, true).GetString(bytes).Split('\n');
			if (getStringParts.Length < 5 || getStringParts.Length > 14 || getStringParts[0] != "pavise-spi-legacy-migration-1" || getStringParts[1] != "user_sid\t" + sid || !getStringParts[2].StartsWith("nonce\t", StringComparison.Ordinal) || getStringParts[getStringParts.Length - 1] != "")
			{
				throw new InvalidDataException("Invalid legacy SPI migration header");
			}
			SortedDictionary<string, SpiLegacyMigrationMember> sortedDictionary = new SortedDictionary<string, SpiLegacyMigrationMember>(StringComparer.Ordinal);
			for (int i = 3; i < getStringParts.Length - 1; i++)
			{
				string[] parts = getStringParts[i].Split('\t');
				if (parts.Length != 4)
				{
					throw new InvalidDataException("Invalid legacy SPI coordinate");
				}
				ValidateTarget(parts[1]);
				SpiLegacyMigrationMember spiLegacyMigrationMember = new SpiLegacyMigrationMember(parts[1], parts[2], parts[3]);
				if (parts[0] != spiLegacyMigrationMember.Slot || sortedDictionary.ContainsKey(spiLegacyMigrationMember.Slot))
				{
					throw new InvalidDataException("Legacy SPI coordinate hash differs");
				}
				sortedDictionary.Add(spiLegacyMigrationMember.Slot, spiLegacyMigrationMember);
			}
			SpiLegacyMigrationPlan spiLegacyMigrationPlan = new SpiLegacyMigrationPlan(sid, getStringParts[2].Substring(6), sortedDictionary);
			if (!SpiOwnerRecord.Equal(spiLegacyMigrationPlan.Manifest, bytes))
			{
				throw new InvalidDataException("Noncanonical legacy SPI migration bytes");
			}
			return spiLegacyMigrationPlan;
		}

		internal PerformanceActionStore.Receipt Current(string target)
		{
			string key = PerformanceActionStore.Slot("MatchVisualEffectsV1", target);
			SpiLegacyMigrationMember value;
			if (!Members.TryGetValue(key, out value) || value.Target != target)
			{
				throw new InvalidDataException("Target is not in this exact legacy restore set");
			}
			foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
			{
				if (!(item.Id == "MatchVisualEffectsV1") || !(item.Target == target))
				{
					continue;
				}
				if (!string.IsNullOrEmpty(item.VisualOwner) || (item.Original != "0" && item.Original != "1") || (!item.VisualRestored && item.Expected != "0") || (item.VisualRestored && item.Expected != item.Original))
				{
					throw new InvalidDataException("Legacy SPI receipt domain is invalid");
				}
				string receiptHashResult = ReceiptHash(item);
				if (item.VisualRestored ? (receiptHashResult != value.TerminalHash) : (receiptHashResult != value.SourceHash))
				{
					throw new InvalidDataException("Legacy SPI receipt changed or was reassigned");
				}
				PerformanceActionStore.Receipt receipt = new PerformanceActionStore.Receipt();
				receipt.Id = item.Id;
				receipt.Target = item.Target;
				receipt.Original = item.Original;
				receipt.Expected = item.Original;
				receipt.Committed = true;
				receipt.VisualRestored = true;
				PerformanceActionStore.Receipt r = receipt;
				if (SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes(PerformanceActionStore.Pack(r))) != value.TerminalHash)
				{
					throw new InvalidDataException("Legacy terminal digest cannot bind these originals");
				}
				bool currentReady = false;
				bool[] conditionMetItems = new bool[2] { false, true };
				foreach (bool committed in conditionMetItems)
				{
					PerformanceActionStore.Receipt receipt2 = new PerformanceActionStore.Receipt();
					receipt2.Id = item.Id;
					receipt2.Target = item.Target;
					receipt2.Original = item.Original;
					receipt2.Expected = "0";
					receipt2.Committed = committed;
					PerformanceActionStore.Receipt r2 = receipt2;
					if (SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes(PerformanceActionStore.Pack(r2))) == value.SourceHash)
					{
						currentReady = true;
					}
				}
				if (!currentReady)
				{
					throw new InvalidDataException("Legacy source digest is not the exact former receipt shape");
				}
				return item;
			}
			throw new InvalidDataException("Registered legacy SPI original is missing before closure");
		}

		internal void RequireAllCurrent()
		{
			int processedCount = 0;
			foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
			{
				if (item.Id == "MatchVisualEffectsV1")
				{
					Current(item.Target);
					processedCount++;
				}
			}
			if (processedCount != Members.Count)
			{
				throw new InvalidDataException("Legacy SPI snapshot is incomplete; absence is not no debt");
			}
		}

		internal byte[] ClosureBytes()
		{
			StringBuilder stringBuilder = new StringBuilder("pavise-spi-legacy-close-1\nuser_sid\t" + UserSid + "\nmigration\t" + SpiOwnerRecord.Digest(Manifest) + "\n");
			foreach (SpiLegacyMigrationMember value in Members.Values)
			{
				stringBuilder.Append(value.Slot).Append('\t').Append(value.TerminalHash)
					.Append('\n');
			}
			return new UTF8Encoding(false, true).GetBytes(stringBuilder.ToString());
		}

		internal void ClearClosedMetadata(SpiOwnerFileStore store)
		{
			if (!SpiOwnerRecord.Equal(store.ReadLegacy("legacy-close.tsv"), ClosureBytes()))
			{
				throw new InvalidDataException("Legacy SPI closure does not bind the migration members");
			}
			List<PerformanceActionStore.Receipt> list = new List<PerformanceActionStore.Receipt>();
			foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
			{
				if (item.Id == "MatchVisualEffectsV1")
				{
					SpiLegacyMigrationMember value;
					if (!string.IsNullOrEmpty(item.VisualOwner) || !item.VisualRestored || !Members.TryGetValue(PerformanceActionStore.Slot(item.Id, item.Target), out value) || ReceiptHash(item) != value.TerminalHash)
					{
						throw new InvalidDataException("Closed legacy metadata is not an exact terminal member");
					}
					list.Add(item);
				}
			}
			foreach (PerformanceActionStore.Receipt item2 in list)
			{
				if (!PerformanceActionStore.ClearLegacyVisualTerminal(item2))
				{
					throw new IOException("Legacy SPI terminal metadata cleanup is incomplete");
				}
			}
		}
	}
}
