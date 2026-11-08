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
			ulong numericValue = 0uL;
			ulong numericValue2 = 0uL;
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			ulong[] physicalCores = machine.PhysicalCores;
			foreach (ulong numericValueEntry in physicalCores)
			{
				if (numericValueEntry == 0 || (numericValue2 & numericValueEntry) != 0)
				{
					throw new InvalidOperationException("invalid physical topology");
				}
				numericValue2 |= numericValueEntry;
			}
			if ((target.AllowedMask & ~numericValue2) != 0)
			{
				throw new InvalidOperationException("allowed range has unknown physical processors");
			}
			foreach (HardThreadChoice hardThreadChoice in selected)
			{
				if (hardThreadChoice == null || hardThreadChoice.Thread == null || !hashSet.Add(hardThreadChoice.Thread.Key) || hardThreadChoice.Mask == 0 || (hardThreadChoice.Mask & (hardThreadChoice.Mask - 1)) != 0 || (hardThreadChoice.Mask & ~target.AllowedMask) != 0)
				{
					throw new InvalidOperationException("invalid candidate identity or logical processor");
				}
				ulong numericValue3 = 0uL;
				ulong[] physicalCores2 = machine.PhysicalCores;
				foreach (ulong numericValueEntry2 in physicalCores2)
				{
					if ((numericValueEntry2 & hardThreadChoice.Mask) != 0)
					{
						numericValue3 = numericValueEntry2;
					}
				}
				if (numericValue3 == 0 || (numericValue & numericValue3) != 0)
				{
					throw new InvalidOperationException("candidates must use distinct physical cores");
				}
				numericValue |= numericValue3;
			}
			ulong allowedMaskMask = target.AllowedMask & ~numericValue;
			int processedCount = 0;
			ulong[] physicalCores3 = machine.PhysicalCores;
			foreach (ulong numericValueEntry3 in physicalCores3)
			{
				if ((allowedMaskMask & numericValueEntry3) != 0)
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
			return new HardThreadPlan(target, selected, allowedMaskMask, machine.ProcessMask, machine.Group, workerMode, requireWorkerCoverage);
		}

		internal bool IsCandidate(HardThreadIdentity thread)
		{
			HardThreadChoice[] choicesSnapshot = choices;
			foreach (HardThreadChoice hardThreadChoice in choicesSnapshot)
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
			HardThreadChoice[] choicesSnapshot = choices;
			foreach (HardThreadChoice hardThreadChoice in choicesSnapshot)
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
