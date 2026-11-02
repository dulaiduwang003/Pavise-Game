using System;
using System.Collections.Generic;
using System.Linq;

namespace PaviseApp
{
	internal static class CriticalThreadRuntimePolicy
	{
		internal static HardThreadChoice[] Select(CriticalThreadRule rule, CriticalThreadHotspot[] hotspots, CriticalThreadRoleFact[] roles)
		{
			if (rule == null || hotspots == null)
			{
				return new HardThreadChoice[0];
			}
			roles = roles ?? new CriticalThreadRoleFact[0];
			CriticalThreadHotspot[] array = (from x in hotspots
				where x != null && x.Tid > 0 && x.Creation > 0 && x.Windows >= 3 && x.CpuShare >= 0.35
				orderby roles.Any((CriticalThreadRoleFact y) => y.Tid == x.Tid && y.Creation == x.Creation && y.Role == "presentation-associated") descending, roles.Any((CriticalThreadRoleFact y) => y.Tid == x.Tid && y.Creation == x.Creation) descending, x.CpuShare descending, x.Tid
				select x).ToArray();
			if (array.Length < rule.CandidateCount || array.Select((CriticalThreadHotspot x) => x.Tid).Distinct().Count() != array.Length)
			{
				return new HardThreadChoice[0];
			}
			ulong[] masks = rule.Targets;
			return array.Take(masks.Length).Select((CriticalThreadHotspot x, int index) => new HardThreadChoice(new HardThreadIdentity(x.Tid, x.Creation), masks[index])).ToArray();
		}

		internal static bool Contains(ThreadTimes sample, HardThreadIdentity thread)
		{
			int num = 0;
			while (sample != null && num < sample.Ids.Length)
			{
				if (sample.Ids[num] == thread.Tid && sample.Created[num] == thread.Creation)
				{
					return true;
				}
				num++;
			}
			return false;
		}

		internal static HardThreadIdentity[] Batch(ThreadTimes sample, HardThreadPlan plan, HardThreadOwnership[] receipts, out HardThreadCoverage coverage)
		{
			if (plan.WorkerMode == HardThreadWorkerMode.CandidatesOnly)
			{
				coverage = HardThreadCoverage.CandidatesOnly;
				return plan.Choices.Select((HardThreadChoice x) => x.Thread).ToArray();
			}
			HashSet<string> hashSet = new HashSet<string>((receipts ?? new HardThreadOwnership[0]).Select((HardThreadOwnership x) => x.Thread.Key), StringComparer.Ordinal);
			List<HardThreadIdentity> list = new List<HardThreadIdentity>();
			int num = 0;
			HardThreadChoice[] choices = plan.Choices;
			foreach (HardThreadChoice hardThreadChoice in choices)
			{
				list.Add(hardThreadChoice.Thread);
			}
			for (int j = 0; j < sample.Ids.Length; j++)
			{
				HardThreadIdentity hardThreadIdentity = new HardThreadIdentity((int)sample.Ids[j], sample.Created[j]);
				if (plan.IsCandidate(hardThreadIdentity))
				{
					continue;
				}
				bool flag = hashSet.Contains(hardThreadIdentity.Key);
				if ((flag || num < 8) && list.Count < 512)
				{
					list.Add(hardThreadIdentity);
					if (!flag)
					{
						num++;
					}
				}
			}
			coverage = ((list.Count != sample.Ids.Length) ? HardThreadCoverage.Partial : HardThreadCoverage.Complete);
			return list.ToArray();
		}

		internal static bool Verified(ThreadTimes sample, HardThreadPlan plan, HardThreadOwnership[] receipts, long auditStartedUtc, out int candidates, out int workers, out bool complete)
		{
			candidates = (workers = 0);
			complete = false;
			if (plan == null || sample == null || !CoreThreadTracker.ValidShape(sample) || auditStartedUtc <= 0 || receipts == null || receipts.Length > 512)
			{
				return false;
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			foreach (HardThreadOwnership hardThreadOwnership in receipts)
			{
				if (hardThreadOwnership == null || hardThreadOwnership.Thread == null || !hashSet.Add(hardThreadOwnership.Thread.Key))
				{
					return false;
				}
			}
			HardThreadOwnership[] array = (receipts ?? new HardThreadOwnership[0]).Where((HardThreadOwnership x) => x != null && x.Group == plan.Group && x.VerifiedUtcTicks >= auditStartedUtc && Contains(sample, x.Thread)).ToArray();
			HardThreadOwnership[] array2 = array;
			foreach (HardThreadOwnership hardThreadOwnership2 in array2)
			{
				if (plan.IsCandidate(hardThreadOwnership2.Thread))
				{
					if (hardThreadOwnership2.Mask == plan.MaskFor(hardThreadOwnership2.Thread))
					{
						candidates++;
					}
				}
				else if (plan.WorkerMode == HardThreadWorkerMode.AvoidWorkers && hardThreadOwnership2.Mask == plan.Workers)
				{
					workers++;
				}
			}
			complete = plan.WorkerMode == HardThreadWorkerMode.AvoidWorkers && candidates == plan.Choices.Length && candidates + workers == sample.Ids.Length;
			return candidates == plan.Choices.Length;
		}
	}
}
