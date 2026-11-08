using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // Thread placement may take this gate; isolation code must not take the
        // thread-placement gate while holding it.
        private readonly object coreIsolationGate = new object();
        private bool criticalOwnsCoreIsolation;
        private bool criticalBorrowsCoreIsolation;
        private object criticalCoreIsolationOwner;
        private int criticalCoreIsolationPid;
        private long criticalCoreIsolationBirth;
        private ulong criticalCoreIsolationMask;
        private string criticalCoreIsolationError;

        private object CriticalCoreIsolationRecoveryOwner
        {
            get { lock (coreIsolationGate) return criticalOwnsCoreIsolation ? criticalCoreIsolationOwner : null; }
        }

        private bool HasCriticalCoreIsolationLease
        {
            get { lock (coreIsolationGate) return criticalOwnsCoreIsolation || criticalBorrowsCoreIsolation; }
        }

        private bool EnsureCriticalCoreIsolation(CriticalRuntimeSource source, HardThreadPlan plan)
        {
            if (!Monitor.IsEntered(criticalProcessGate)) throw new InvalidOperationException("Original placement gate required");
            if (source == null || plan == null || plan.WorkerMode != HardThreadWorkerMode.AvoidWorkers
                || plan.Target.ProcessId != source.Pid || plan.Target.ProcessCreation != source.Birth
                || criticalOriginalTarget == null)
                return CriticalCoreIsolationRefused("critical-isolation-admission");
            if (plan.Target.SessionId != criticalOriginalTarget.SessionId
                || plan.Target.ProcessId != criticalOriginalTarget.ProcessId
                || plan.Target.ProcessCreation != criticalOriginalTarget.ProcessCreation
                || plan.Target.AllowedMask != criticalOriginalTarget.AllowedMask
                || !string.Equals(plan.Target.ExecutablePath, criticalOriginalTarget.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return CriticalCoreIsolationRefused("critical-isolation-original-target");

            criticalOriginalTarget.RequireActive();
            ulong selected = 0;
            foreach (HardThreadChoice choice in plan.Choices) selected |= choice.Mask;
            ulong isolated = AutoCores.WholeCores(selected, CpuTopology.PhysicalCoreMasks());
            if (selected == 0 || isolated == 0 || (selected & ~isolated) != 0
                || (plan.Workers & isolated) != 0)
                return CriticalCoreIsolationRefused("critical-isolation-topology");

            lock (coreIsolationGate)
            {
                criticalOriginalTarget.RequireActive();
                if (criticalOwnsCoreIsolation)
                {
                    if (coreIsolation == null || criticalCoreIsolationPid != source.Pid
                        || criticalCoreIsolationBirth != source.Birth || criticalCoreIsolationMask != isolated)
                        return CriticalCoreIsolationRefused("critical-isolation-previous-owner-pending");
                    bool verified = coreIsolation.Ensure(isolated, source.Pid, source.Birth, plan.ProcessMask, true)
                        && CoreIsolationClient.State == "schedule.isolation.active"
                        && CoreIsolationClient.ActiveMask == isolated;
                    if (!verified) return CriticalCoreIsolationRefused(coreIsolation.LastError ?? "critical-isolation-readback");
                    criticalOriginalTarget.RequireActive();
                    criticalCoreIsolationError = null;
                    return true;
                }

                if (coreIsolation != null)
                {
                    // The whole-game feature keeps ownership of its existing lease.
                    // Ensure only admits the game; it cannot resize an active lease.
                    ulong activeMask = CoreIsolationClient.ActiveMask;
                    if (coreManual == null || !coreManual.Isolation || !coreEngaged
                        || CoreIsolationClient.State != "schedule.isolation.active"
                        || (isolated & ~activeMask) != 0)
                        return CriticalCoreIsolationRefused("critical-isolation-other-owner-pending");
                    if (!coreIsolation.Ensure(activeMask, source.Pid, source.Birth, plan.ProcessMask, true)
                        || CoreIsolationClient.State != "schedule.isolation.active"
                        || CoreIsolationClient.ActiveMask != activeMask)
                        return CriticalCoreIsolationRefused(coreIsolation.LastError ?? "critical-isolation-shared-readback");
                    criticalBorrowsCoreIsolation = true;
                    criticalCoreIsolationPid = source.Pid;
                    criticalCoreIsolationBirth = source.Birth;
                    criticalCoreIsolationMask = isolated;
                    criticalOriginalTarget.RequireActive();
                    criticalCoreIsolationError = null;
                    return true;
                }

                if (criticalBorrowsCoreIsolation)
                    return CriticalCoreIsolationRefused("critical-isolation-shared-owner-lost");
                if (CoreIsolationClient.State == "schedule.isolation.pending"
                    || (coreManual != null && coreManual.Isolation))
                    return CriticalCoreIsolationRefused("critical-isolation-regular-owner-pending");

                // Publish ownership before starting the helper: even a failed
                // admission can have a live helper or a durable recovery receipt.
                coreIsolation = new CoreIsolationClient();
                criticalOwnsCoreIsolation = true;
                criticalCoreIsolationOwner = new object();
                criticalCoreIsolationPid = source.Pid;
                criticalCoreIsolationBirth = source.Birth;
                criticalCoreIsolationMask = isolated;
                if (!coreIsolation.Ensure(isolated, source.Pid, source.Birth, plan.ProcessMask, true)
                    || CoreIsolationClient.State != "schedule.isolation.active"
                    || CoreIsolationClient.ActiveMask != isolated)
                    return CriticalCoreIsolationRefused(coreIsolation.LastError ?? "critical-isolation-start");
                criticalOriginalTarget.RequireActive();
                criticalCoreIsolationError = null;
                return true;
            }
        }

        private bool CriticalCoreIsolationRefused(string reason)
        {
            criticalCoreIsolationError = reason;
            return false;
        }

        private bool StopCriticalCoreIsolation()
        {
            if (!Monitor.IsEntered(criticalProcessGate)) throw new InvalidOperationException("Original placement gate required");
            lock (coreIsolationGate)
            {
                if (criticalOwnsCoreIsolation)
                {
                    if (coreIsolation == null) return CriticalCoreIsolationRefused("critical-isolation-owner-lost");
                    if (!coreIsolation.Stop())
                        return CriticalCoreIsolationRefused(coreIsolation.LastError ?? "critical-isolation-restore-pending");
                    coreIsolation = null;
                    criticalOwnsCoreIsolation = false;
                    criticalCoreIsolationOwner = null;
                }
                // A borrowed whole-game lease belongs to the regular feature.
                criticalBorrowsCoreIsolation = false;
                criticalCoreIsolationPid = 0;
                criticalCoreIsolationBirth = 0;
                criticalCoreIsolationMask = 0;
                criticalCoreIsolationError = null;
                return true;
            }
        }
    }
}
