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
            List<string> list = new List<string>();
            foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
            {
                if (item.Id == "MatchVisualEffectsV1")
                {
                    if (item.VisualOwner != manualReceiptOwner || !item.VisualRestored || !item.Committed || item.Expected != item.Original)
                    {
                        throw new InvalidDataException("Manual SPI receipt is not durably settled for this owner");
                    }
                    access.Assert(access.Stamp, false);
                    if (PerformanceActionBackend.Read(item.Id, item.Target) != item.Original)
                    {
                        throw new IOException("Original visual effect readback is not confirmed");
                    }
                    list.Add(PerformanceActionStore.Slot(item.Id, item.Target) + "\t" + SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes(PerformanceActionStore.Pack(item))));
                }
            }
            list.Sort(StringComparer.Ordinal);
            string evidence = access.PublishClosure(SpiManualClosureManifest.Bytes(access.Stamp, list));
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
