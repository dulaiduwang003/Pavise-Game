using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal sealed class SpiOwnerEvidence
    {
        internal readonly string OwnerDigest;

        internal readonly string EvidenceDigest;

        internal static SpiOwnerEvidence FromRestoredVisualReceipts(SpiOwnership.SpiOwnerAccess access)
        {
            if (access == null || access.Operation != SpiOwnerOperation.Restore || access.Stamp.Identity.Kind != 0)
            {
                throw new InvalidOperationException("Manual closure requires the original restoration capability");
            }
            SpiOwnerRecord spiOwnerRecord = access.RecordForEvidence(false);
            string manualReceiptOwner = access.Stamp.ManualReceiptOwner;
            List<string> texts = new List<string>();
            foreach (PerformanceActionStore.Receipt all in PerformanceActionStore.All())
            {
                if (all.Id == "MatchVisualEffectsV1")
                {
                    if (all.VisualOwner != manualReceiptOwner || !all.VisualRestored || !all.Committed || all.Expected != all.Original)
                    {
                        throw new InvalidDataException("Manual SPI receipt is not durably settled for this owner");
                    }
                    access.Assert(access.Stamp, false);
                    if (PerformanceActionBackend.Read(all.Id, all.Target) != all.Original)
                    {
                        throw new IOException("Original visual effect readback is not confirmed");
                    }
                    texts.Add(PerformanceActionStore.Slot(all.Id, all.Target) + "\t" + SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes(PerformanceActionStore.Pack(all))));
                }
            }
            texts.Sort(StringComparer.Ordinal);
            string evidence = access.PublishClosure(SpiManualClosureManifest.Bytes(access.Stamp, texts));
            return new SpiOwnerEvidence(SpiOwnerRecord.Digest(spiOwnerRecord.Bytes()), evidence);
        }

        private SpiOwnerEvidence(string owner, string evidence)
        {
            SpiOwnerIdentity.Hash(owner);
            SpiOwnerIdentity.Hash(evidence);
            OwnerDigest = owner;
            EvidenceDigest = evidence;
        }
    }
}
