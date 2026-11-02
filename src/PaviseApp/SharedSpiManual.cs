using System;
using System.Security.Principal;

namespace PaviseApp
{
	internal static class SharedSpiManual
	{
		private static readonly object sync = new object();

		private static SpiOwnerStamp liveOwner;

		internal static void RequirePrimitive(string target, bool writing, string value = null)
		{
			if (SpiOwnership.HasHeldCapability)
			{
				SpiOwnership.RequireManualPrimitive(writing);
			}
			else
			{
				SharedSpiLegacyMigration.RequirePrimitive(target, writing, value);
			}
		}

		private static SpiOwnership Owner()
		{
			using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
			{
				if (windowsIdentity.User == null)
				{
					throw new UnauthorizedAccessException("Manual SPI user is unknown");
				}
				string value = windowsIdentity.User.Value;
				return new SpiOwnership(new SpiOwnerFileStore(SpiOwnerFileStore.CanonicalPath(value)), value);
			}
		}

		internal static bool Apply(string target, string wanted)
		{
			lock (sync)
			{
				SpiOwnership spiOwnership = Owner();
				if (liveOwner == null)
				{
					SpiOwnerRecord spiOwnerRecord = spiOwnership.Read();
					if (spiOwnerRecord.Phase != SpiOwnerPhase.Closed)
					{
						throw new InvalidOperationException("Earlier SPI owner restoration is still pending");
					}
					liveOwner = spiOwnership.ClaimManualFromClosed(spiOwnerRecord);
				}
				using (SpiOwnership.SpiOwnerAccess access = spiOwnership.Enter(liveOwner, SpiOwnerOperation.Apply))
				{
					bool flag = false;
					try
					{
						flag = PerformanceActionStore.ChangeVisualOwned(access, target, wanted, () => PerformanceActionBackend.Read("MatchVisualEffectsV1", target), (string value) => PerformanceActionBackend.Write("MatchVisualEffectsV1", target, value));
						return flag;
					}
					finally
					{
						if (!flag)
						{
							spiOwnership.RestrictToRestore(liveOwner);
						}
					}
				}
			}
		}

		internal static bool Restore(PerformanceActionStore.Receipt receipt)
		{
			if (receipt == null || string.IsNullOrEmpty(receipt.VisualOwner))
			{
				return false;
			}
			lock (sync)
			{
				SpiOwnerStamp spiOwnerStamp = SpiOwnerStamp.FromManualReceiptOwner(receipt.VisualOwner);
				SpiOwnership spiOwnership = Owner();
				SpiOwnerRecord spiOwnerRecord = spiOwnership.Read();
				if (spiOwnerRecord.Phase == SpiOwnerPhase.Closed)
				{
					using (SpiOwnership.SpiOwnerAccess closedAccess = spiOwnership.EnterClosedMetadata(spiOwnerStamp))
					{
						bool flag = PerformanceActionStore.ClearVisualTerminalAfterClose(closedAccess);
						if (flag && liveOwner != null && liveOwner.Same(spiOwnerStamp))
						{
							liveOwner = null;
						}
						return flag;
					}
				}
				spiOwnership.RestrictToRestore(spiOwnerStamp);
				bool flag2 = false;
				using (SpiOwnership.SpiOwnerAccess access = spiOwnership.Enter(spiOwnerStamp, SpiOwnerOperation.Restore))
				{
					if (!PerformanceActionStore.RestoreVisualOwned(access, receipt, () => PerformanceActionBackend.Read(receipt.Id, receipt.Target), (string value) => PerformanceActionBackend.Write(receipt.Id, receipt.Target, value)))
					{
						return false;
					}
					bool flag3 = true;
					foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
					{
						if (item.Id == "MatchVisualEffectsV1")
						{
							if (item.VisualOwner != spiOwnerStamp.ManualReceiptOwner)
							{
								return false;
							}
							if (!item.VisualRestored)
							{
								flag3 = false;
							}
						}
					}
					if (flag3)
					{
						spiOwnership.Close(spiOwnerStamp, SpiOwnerEvidence.FromRestoredVisualReceipts(access));
						flag2 = true;
					}
				}
				if (!flag2)
				{
					return true;
				}
				using (SpiOwnership.SpiOwnerAccess closedAccess2 = spiOwnership.EnterClosedMetadata(spiOwnerStamp))
				{
					bool flag4 = PerformanceActionStore.ClearVisualTerminalAfterClose(closedAccess2);
					if (flag4 && liveOwner != null && liveOwner.Same(spiOwnerStamp))
					{
						liveOwner = null;
					}
					return flag4;
				}
			}
		}
	}
}
