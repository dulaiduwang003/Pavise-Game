using System;

namespace PaviseApp
{
    internal sealed class IrqWriteResult
    {
        internal int Succeeded, Failed, RolledBack, RollbackFailed, Unchanged, Attempted;
        internal string Failure = "";
        internal string Describe()
        {
            return Lang.F("irq.write.detail", Succeeded, Failed, RolledBack, RollbackFailed)
                + (Failure.Length == 0 ? "" : " · " + Failure);
        }
    }

    // Pure coordinator both restore steps run even when the first throws
    internal sealed class IrqChangeResult
    {
        internal bool Affinity, Priority, PriorityRequested, AffinityCompleted, PriorityAttempted;
        internal static IrqChangeResult Run(Func<bool> affinity, Func<bool> priority, bool restore)
        {
            var r = new IrqChangeResult { PriorityRequested = priority != null };
            try { r.Affinity = affinity != null && affinity(); r.AffinityCompleted = affinity != null; } catch { }
            if (priority != null && (restore || r.Affinity))
            {
                r.PriorityAttempted = true;
                try { r.Priority = priority(); } catch { }
            }
            return r;
        }
        internal bool Success { get { return Affinity && (!PriorityRequested || Priority); } }
        internal string Describe(bool restore)
        {
            return Lang.F(restore ? "irq.restore.result" : "irq.change.result",
                Lang.T(Affinity ? "irq.step.ok" : "irq.step.failed"),
                Lang.T(!PriorityAttempted ? "irq.step.skipped" : Priority ? "irq.step.ok" : "irq.step.failed"));
        }
    }
}
