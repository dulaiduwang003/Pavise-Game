using System;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static class SharedSpiLegacyMigration
    {
        internal sealed class PrimitiveScope : IDisposable
        {
            internal readonly SharedSpiBootstrap.BootstrapProof Proof;

            internal readonly SpiLegacyMigrationPlan Plan;

            internal readonly int ThreadId = Thread.CurrentThread.ManagedThreadId;

            internal bool Active = true;

            internal PrimitiveScope(SharedSpiBootstrap.BootstrapProof proof)
            {
                if (proof == null || proof.Migration == null || current != null || SpiOwnership.HasHeldCapability)
                {
                    throw new InvalidOperationException("Legacy SPI recovery has no unique startup-only capability");
                }
                proof.RequireCurrent();
                Proof = proof;
                Plan = proof.Migration;
                current = this;
            }

            public void Dispose()
            {
                if (Active)
                {
                    if (ThreadId != Thread.CurrentThread.ManagedThreadId || current != this)
                    {
                        throw new InvalidOperationException("Legacy SPI capability changed thread");
                    }
                    Active = false;
                    current = null;
                }
            }
        }

        [ThreadStatic]
        private static PrimitiveScope current;

        internal static void RequirePrimitive(string target, bool writing, string value)
        {
            PrimitiveScope primitiveScope = current;
            if (primitiveScope == null || !primitiveScope.Active || primitiveScope.ThreadId != Thread.CurrentThread.ManagedThreadId)
            {
                throw new InvalidOperationException("Visual SPI primitive has no held manual or restore-only upgrade capability");
            }
            primitiveScope.Proof.RequireCurrent();
            PerformanceActionStore.Receipt receipt = primitiveScope.Plan.Current(target);
            if (writing && (receipt.VisualRestored || value != receipt.Original))
            {
                throw new InvalidOperationException("Legacy SPI recovery permits only its exact original value");
            }
        }

        internal static void RequireReceipt(PerformanceActionStore.Receipt row)
        {
            if (current == null || row == null || PerformanceActionStore.Pack(current.Plan.Current(row.Target)) != PerformanceActionStore.Pack(row))
            {
                throw new InvalidDataException("Legacy SPI original changed before restoration");
            }
            current.Proof.RequireCurrent();
        }
    }
}
