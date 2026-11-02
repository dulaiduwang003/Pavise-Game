using System;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal sealed class SpiOwnerClaimEvidence
	{
		internal readonly string ClosedDigest;

		internal readonly string ClearedReceiptDigest;

		internal static SpiOwnerClaimEvidence FromEmptyVisualIndex(SpiOwnerRecord closed)
		{
			if (closed == null || closed.Phase != SpiOwnerPhase.Closed)
			{
				throw new InvalidDataException("Manual claim requires a verified closed SPI slot");
			}
			foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
			{
				if (item.Id == "MatchVisualEffectsV1")
				{
					throw new InvalidDataException("SPI owner still has manual recovery receipts");
				}
			}
			return new SpiOwnerClaimEvidence(SpiOwnerRecord.Digest(closed.Bytes()), SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes("manual-visual-receipts-cleared-1\n" + closed.UserSid + "\n")));
		}

		internal static SpiOwnerClaimEvidence FromClearedVisualReceipts(SpiOwnership.SpiOwnerAccess closedAccess)
		{
			if (closedAccess == null || closedAccess.Stamp.Identity.Kind != 0)
			{
				throw new InvalidOperationException("Manual claim proof requires the previous closed owner");
			}
			SpiOwnerRecord spiOwnerRecord = closedAccess.RecordForEvidence(true);
			foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
			{
				if (item.Id == "MatchVisualEffectsV1")
				{
					throw new InvalidDataException("Closed SPI owner still has uncleared terminal or foreign receipts");
				}
			}
			return new SpiOwnerClaimEvidence(SpiOwnerRecord.Digest(spiOwnerRecord.Bytes()), SpiOwnerRecord.Digest(Encoding.UTF8.GetBytes("manual-visual-receipts-cleared-1\n" + spiOwnerRecord.UserSid + "\n")));
		}

		private SpiOwnerClaimEvidence(string closed, string cleared)
		{
			SpiOwnerIdentity.Hash(closed);
			SpiOwnerIdentity.Hash(cleared);
			ClosedDigest = closed;
			ClearedReceiptDigest = cleared;
		}
	}
}
