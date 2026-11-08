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
					bool applySucceeded = false;
					try
					{
						applySucceeded = PerformanceActionStore.ChangeVisualOwned(access, target, wanted, () => PerformanceActionBackend.Read("MatchVisualEffectsV1", target), (string value) => PerformanceActionBackend.Write("MatchVisualEffectsV1", target, value));
						return applySucceeded;
					}
					finally
					{
						if (!applySucceeded)
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
						bool clearVisualTerminalAfterCloseSucceeded = PerformanceActionStore.ClearVisualTerminalAfterClose(closedAccess);
						if (clearVisualTerminalAfterCloseSucceeded && liveOwner != null && liveOwner.Same(spiOwnerStamp))
						{
							liveOwner = null;
						}
						return clearVisualTerminalAfterCloseSucceeded;
					}
				}
				spiOwnership.RestrictToRestore(spiOwnerStamp);
				bool restoreReady = false;
				using (SpiOwnership.SpiOwnerAccess access = spiOwnership.Enter(spiOwnerStamp, SpiOwnerOperation.Restore))
				{
					if (!PerformanceActionStore.RestoreVisualOwned(access, receipt, () => PerformanceActionBackend.Read(receipt.Id, receipt.Target), (string value) => PerformanceActionBackend.Write(receipt.Id, receipt.Target, value)))
					{
						return false;
					}
					bool restoreReady2 = true;
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
								restoreReady2 = false;
							}
						}
					}
					if (restoreReady2)
					{
						spiOwnership.Close(spiOwnerStamp, SpiOwnerEvidence.FromRestoredVisualReceipts(access));
						restoreReady = true;
					}
				}
				if (!restoreReady)
				{
					return true;
				}
				using (SpiOwnership.SpiOwnerAccess closedAccess2 = spiOwnership.EnterClosedMetadata(spiOwnerStamp))
				{
					bool clearVisualTerminalAfterCloseSucceeded2 = PerformanceActionStore.ClearVisualTerminalAfterClose(closedAccess2);
					if (clearVisualTerminalAfterCloseSucceeded2 && liveOwner != null && liveOwner.Same(spiOwnerStamp))
					{
						liveOwner = null;
					}
					return clearVisualTerminalAfterCloseSucceeded2;
				}
			}
		}
	}
}
