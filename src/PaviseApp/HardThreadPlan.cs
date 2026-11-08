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
			ulong num = 0uL;
			ulong num2 = 0uL;
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			ulong[] physicalCores = machine.PhysicalCores;
			foreach (ulong num3 in physicalCores)
			{
				if (num3 == 0 || (num2 & num3) != 0)
				{
					throw new InvalidOperationException("invalid physical topology");
				}
				num2 |= num3;
			}
			if ((target.AllowedMask & ~num2) != 0)
			{
				throw new InvalidOperationException("allowed range has unknown physical processors");
			}
			foreach (HardThreadChoice hardThreadChoice in selected)
			{
				if (hardThreadChoice == null || hardThreadChoice.Thread == null || !hashSet.Add(hardThreadChoice.Thread.Key) || hardThreadChoice.Mask == 0 || (hardThreadChoice.Mask & (hardThreadChoice.Mask - 1)) != 0 || (hardThreadChoice.Mask & ~target.AllowedMask) != 0)
				{
					throw new InvalidOperationException("invalid candidate identity or logical processor");
				}
				ulong num4 = 0uL;
				ulong[] physicalCores2 = machine.PhysicalCores;
				foreach (ulong num5 in physicalCores2)
				{
					if ((num5 & hardThreadChoice.Mask) != 0)
					{
						num4 = num5;
					}
				}
				if (num4 == 0 || (num & num4) != 0)
				{
					throw new InvalidOperationException("candidates must use distinct physical cores");
				}
				num |= num4;
			}
			ulong num6 = target.AllowedMask & ~num;
			int num7 = 0;
			ulong[] physicalCores3 = machine.PhysicalCores;
			foreach (ulong num8 in physicalCores3)
			{
				if ((num6 & num8) != 0)
				{
					num7++;
				}
			}
			if (num7 < 2)
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
			return new HardThreadPlan(target, selected, num6, machine.ProcessMask, machine.Group, workerMode, requireWorkerCoverage);
		}

		internal bool IsCandidate(HardThreadIdentity thread)
		{
			HardThreadChoice[] array = choices;
			foreach (HardThreadChoice hardThreadChoice in array)
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
			HardThreadChoice[] array = choices;
			foreach (HardThreadChoice hardThreadChoice in array)
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
