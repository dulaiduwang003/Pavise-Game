using System;
using System.Security.Principal;

namespace PaviseApp
{
    internal static class SharedSpiManual
    {
        private static readonly object sync = new object();

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

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static Func<SpiOwnership> OwnerForTest;
        internal static void ResetForTest() { lock (sync) { OwnerForTest = null; } }
#endif
        private static SpiOwnership Owner()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (OwnerForTest == null) throw new InvalidOperationException("Unmocked shared manual SPI storage is forbidden in isolated checks"); return OwnerForTest();
#endif
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
                        return clearVisualTerminalAfterCloseSucceeded;
                    }
                }
                spiOwnership.RestrictToRestore(spiOwnerStamp);
                bool ownerClosed = false;
                using (SpiOwnership.SpiOwnerAccess access = spiOwnership.Enter(spiOwnerStamp, SpiOwnerOperation.Restore))
                {
                    if (!PerformanceActionStore.RestoreVisualOwned(access, receipt, () => PerformanceActionBackend.Read(receipt.Id, receipt.Target), (string value) => PerformanceActionBackend.Write(receipt.Id, receipt.Target, value)))
                    {
                        return false;
                    }
                    bool allVisualsRestored = true;
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
                                allVisualsRestored = false;
                            }
                        }
                    }
                    if (allVisualsRestored)
                    {
                        spiOwnership.Close(spiOwnerStamp, SpiOwnerEvidence.FromRestoredVisualReceipts(access));
                        ownerClosed = true;
                    }
                }
                if (!ownerClosed)
                {
                    return true;
                }
                using (SpiOwnership.SpiOwnerAccess closedAccess2 = spiOwnership.EnterClosedMetadata(spiOwnerStamp))
                {
                    bool clearVisualTerminalAfterCloseSucceeded2 = PerformanceActionStore.ClearVisualTerminalAfterClose(closedAccess2);
                    return clearVisualTerminalAfterCloseSucceeded2;
                }
            }
        }
    }
}
