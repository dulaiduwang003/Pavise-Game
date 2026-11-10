using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class HardThreadPlan
    {
        private readonly HardThreadChoice[] choices;

        internal readonly HardThreadTarget Target;

        internal readonly ulong Workers;

        internal readonly ulong ProcessMask;

        internal readonly ushort Group;

        internal readonly HardThreadWorkerMode WorkerMode;

        internal readonly bool RequireWorkerCoverage;

        internal HardThreadChoice[] Choices
        {
            get
            {
                return (HardThreadChoice[])choices.Clone();
            }
        }

        private HardThreadPlan(HardThreadTarget target, HardThreadChoice[] choices, ulong workers, ulong processMask, ushort group, HardThreadWorkerMode workerMode, bool requireWorkerCoverage)
        {
            Target = target;
            this.choices = (HardThreadChoice[])choices.Clone();
            Workers = workers;
            ProcessMask = processMask;
            Group = group;
            WorkerMode = workerMode;
            RequireWorkerCoverage = requireWorkerCoverage;
        }

        internal static HardThreadPlan Create(HardThreadTarget target, HardThreadMachine machine, HardThreadChoice[] selected, HardThreadWorkerMode workerMode = HardThreadWorkerMode.CandidatesOnly, bool requireWorkerCoverage = false)
        {
            if (target == null || machine == null || machine.Read != 0 || !machine.CpuSetsEmpty)
            {
                throw new InvalidOperationException("known original process and empty CPU Sets required");
            }
            if (machine.GroupCount != 1 || machine.Group != 0)
            {
                throw new NotSupportedException("multiple processor groups are unsupported");
            }
            if (machine.ProcessMask == 0 || (target.AllowedMask & ~machine.ProcessMask) != 0)
            {
                throw new InvalidOperationException("original process mask does not contain the allowed range");
            }
            if (selected == null || selected.Length < 1 || selected.Length > CriticalThreadAutoPolicy.MaximumIsolated)
            {
                throw new ArgumentException("one to " + CriticalThreadAutoPolicy.MaximumIsolated + " candidates required");
            }
            ulong selectedCoreMask = 0uL;
            ulong topologyMask = 0uL;
            HashSet<string> textsSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (ulong physicalCore in machine.PhysicalCores)
            {
                if (physicalCore == 0 || (topologyMask & physicalCore) != 0)
                {
                    throw new InvalidOperationException("invalid physical topology");
                }
                topologyMask |= physicalCore;
            }
            if ((target.AllowedMask & ~topologyMask) != 0)
            {
                throw new InvalidOperationException("allowed range has unknown physical processors");
            }
            foreach (HardThreadChoice hardThreadChoice in selected)
            {
                if (hardThreadChoice == null || hardThreadChoice.Thread == null || !textsSet.Add(hardThreadChoice.Thread.Key) || hardThreadChoice.Mask == 0 || (hardThreadChoice.Mask & (hardThreadChoice.Mask - 1)) != 0 || (hardThreadChoice.Mask & ~target.AllowedMask) != 0)
                {
                    throw new InvalidOperationException("invalid candidate identity or logical processor");
                }
                ulong physicalCoreMask = 0uL;
                foreach (ulong currentPhysicalCoreMask in machine.PhysicalCores)
                {
                    if ((currentPhysicalCoreMask & hardThreadChoice.Mask) != 0)
                    {
                        physicalCoreMask = currentPhysicalCoreMask;
                    }
                }
                if (physicalCoreMask == 0 || (selectedCoreMask & physicalCoreMask) != 0)
                {
                    throw new InvalidOperationException("candidates must use distinct physical cores");
                }
                selectedCoreMask |= physicalCoreMask;
            }
            ulong remainingAllowedMask = target.AllowedMask & ~selectedCoreMask;
            int processedCount = 0;
            foreach (ulong candidatePhysicalCoreMask in machine.PhysicalCores)
            {
                if ((remainingAllowedMask & candidatePhysicalCoreMask) != 0)
                {
                    processedCount++;
                }
            }
            if (processedCount < 2)
            {
                throw new InvalidOperationException("logical processors on at least two other physical cores required");
            }
            if (!Enum.IsDefined(typeof(HardThreadWorkerMode), workerMode))
            {
                throw new ArgumentException("worker mode");
            }
            if (requireWorkerCoverage && workerMode != HardThreadWorkerMode.AvoidWorkers)
            {
                throw new ArgumentException("complete worker coverage requires worker avoidance");
            }
            return new HardThreadPlan(target, selected, remainingAllowedMask, machine.ProcessMask, machine.Group, workerMode, requireWorkerCoverage);
        }

        internal bool IsCandidate(HardThreadIdentity thread)
        {
            foreach (HardThreadChoice hardThreadChoice in choices)
            {
                if (hardThreadChoice.Thread.Tid == thread.Tid && hardThreadChoice.Thread.Creation == thread.Creation)
                {
                    return true;
                }
            }
            return false;
        }

        internal ulong MaskFor(HardThreadIdentity thread)
        {
            foreach (HardThreadChoice hardThreadChoice in choices)
            {
                if (hardThreadChoice.Thread.Tid == thread.Tid && hardThreadChoice.Thread.Creation == thread.Creation)
                {
                    return hardThreadChoice.Mask;
                }
            }
            if (WorkerMode != HardThreadWorkerMode.AvoidWorkers)
            {
                throw new InvalidOperationException("candidate-only plan cannot bind workers");
            }
            return Workers;
        }
    }
}
