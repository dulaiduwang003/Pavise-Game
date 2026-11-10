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
            foreach (SpiLegacyMigrationMember migrationItem in members.Values)
            {
                stringBuilder.Append(migrationItem.Slot).Append('\t').Append(migrationItem.Target)
                    .Append('\t')
                    .Append(migrationItem.SourceHash)
                    .Append('\t')
                    .Append(migrationItem.TerminalHash)
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
            foreach (MatchVisualEffects.Effect effect in MatchVisualEffects.Effects)
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
                    PerformanceActionStore.Receipt r = new PerformanceActionStore.Receipt();
                    r.Id = row.Id;
                    r.Target = row.Target;
                    r.Original = row.Original;
                    r.Expected = row.Original;
                    r.Committed = true;
                    r.VisualRestored = true;
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
            SpiLegacyMigrationMember Member;
            if (!Members.TryGetValue(key, out Member) || Member.Target != target)
            {
                throw new InvalidDataException("Target is not in this exact legacy restore set");
            }
            foreach (PerformanceActionStore.Receipt all in PerformanceActionStore.All())
            {
                if (all.Id != "MatchVisualEffectsV1" || all.Target != target)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(all.VisualOwner) || (all.Original != "0" && all.Original != "1") || (!all.VisualRestored && all.Expected != "0") || (all.VisualRestored && all.Expected != all.Original))
                {
                    throw new InvalidDataException("Legacy SPI receipt domain is invalid");
                }
                string receiptHashResult = ReceiptHash(all);
                if (all.VisualRestored ? (receiptHashResult != Member.TerminalHash) : (receiptHashResult != Member.SourceHash))
                {
                    throw new InvalidDataException("Legacy SPI receipt changed or was reassigned");
                }
                PerformanceActionStore.Receipt r = new PerformanceActionStore.Receipt();
                r.Id = all.Id;
                r.Target = all.Target;
                r.Original = all.Original;
                r.Expected = all.Original;
                r.Committed = true;
                r.VisualRestored = true;
                if (SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes(PerformanceActionStore.Pack(r))) != Member.TerminalHash)
                {
                    throw new InvalidDataException("Legacy terminal digest cannot bind these originals");
                }
                bool sourceDigestMatched = false;
                foreach (bool committed in new bool[2] { false, true })
                {
                    PerformanceActionStore.Receipt r2 = new PerformanceActionStore.Receipt();
                    r2.Id = all.Id;
                    r2.Target = all.Target;
                    r2.Original = all.Original;
                    r2.Expected = "0";
                    r2.Committed = committed;
                    if (SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes(PerformanceActionStore.Pack(r2))) == Member.SourceHash)
                    {
                        sourceDigestMatched = true;
                    }
                }
                if (!sourceDigestMatched)
                {
                    throw new InvalidDataException("Legacy source digest is not the exact former receipt shape");
                }
                return all;
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
            List<PerformanceActionStore.Receipt> receipts = new List<PerformanceActionStore.Receipt>();
            foreach (PerformanceActionStore.Receipt all in PerformanceActionStore.All())
            {
                if (all.Id == "MatchVisualEffectsV1")
                {
                    SpiLegacyMigrationMember Member;
                    if (!string.IsNullOrEmpty(all.VisualOwner) || !all.VisualRestored || !Members.TryGetValue(PerformanceActionStore.Slot(all.Id, all.Target), out Member) || ReceiptHash(all) != Member.TerminalHash)
                    {
                        throw new InvalidDataException("Closed legacy metadata is not an exact terminal member");
                    }
                    receipts.Add(all);
                }
            }
            foreach (PerformanceActionStore.Receipt receipt in receipts)
            {
                if (!PerformanceActionStore.ClearLegacyVisualTerminal(receipt))
                {
                    throw new IOException("Legacy SPI terminal metadata cleanup is incomplete");
                }
            }
        }
    }
}
