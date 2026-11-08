using System;
using System.Collections.Generic;
using System.Linq;

namespace PaviseApp
{
	internal static class CriticalThreadRuntimePolicy
	{
		private struct SampleThreadIdentity : IEquatable<SampleThreadIdentity>
		{
			private readonly long tid, creation;

			internal SampleThreadIdentity(long tid, long creation)
			{ this.tid = tid; this.creation = creation; }

			public bool Equals(SampleThreadIdentity other)
			{ return tid == other.tid && creation == other.creation; }

			public override bool Equals(object value)
			{ return value is SampleThreadIdentity && Equals((SampleThreadIdentity)value); }

			public override int GetHashCode()
			{ unchecked { return tid.GetHashCode() * 397 ^ creation.GetHashCode(); } }
		}

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
			if (plan.RequireWorkerCoverage)
			{
				coverage = HardThreadCoverage.Partial;
				if (sample == null || !CoreThreadTracker.ValidShape(sample) || sample.Ids.Length > 512
					|| sample.Ids.Any((long tid) => tid <= 0 || tid > int.MaxValue) || sample.Created.Any((long birth) => birth <= 0))
					return new HardThreadIdentity[0];
				HashSet<SampleThreadIdentity> identities = new HashSet<SampleThreadIdentity>();
				for (int index = 0; index < sample.Ids.Length; index++)
					if (!identities.Add(new SampleThreadIdentity(sample.Ids[index], sample.Created[index])))
						return new HardThreadIdentity[0];
				HardThreadChoice[] selected = plan.Choices;
				if (selected.Any((HardThreadChoice x) => !Contains(sample, x.Thread)))
					return new HardThreadIdentity[0];
				HardThreadIdentity[] ordered = new HardThreadIdentity[sample.Ids.Length];
				HardThreadIdentity[] candidates = new HardThreadIdentity[selected.Length];
				int workerCount = 0, candidateCount = 0;
				for (int index = 0; index < sample.Ids.Length; index++)
				{
					HardThreadIdentity thread = new HardThreadIdentity((int)sample.Ids[index], sample.Created[index]);
					if (plan.IsCandidate(thread)) candidates[candidateCount++] = thread;
					else ordered[workerCount++] = thread;
				}
				Array.Copy(candidates, 0, ordered, workerCount, candidateCount);
				coverage = HardThreadCoverage.Complete;
				return ordered;
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
			HashSet<SampleThreadIdentity> hashSet = new HashSet<SampleThreadIdentity>();
			int eligibleReceipts = 0;
			foreach (HardThreadOwnership hardThreadOwnership in receipts)
			{
				if (hardThreadOwnership == null || hardThreadOwnership.Thread == null
					|| !hashSet.Add(new SampleThreadIdentity(hardThreadOwnership.Thread.Tid, hardThreadOwnership.Thread.Creation)))
				{
					return false;
				}
				if (hardThreadOwnership.Group == plan.Group && hardThreadOwnership.VerifiedUtcTicks >= auditStartedUtc)
					eligibleReceipts++;
			}
			HashSet<SampleThreadIdentity> sampleIdentities = null;
			if (eligibleReceipts >= 8 && sample.Ids.Length >= 16 && eligibleReceipts * 2 >= sample.Ids.Length)
			{
				sampleIdentities = new HashSet<SampleThreadIdentity>();
				for (int index = 0; index < sample.Ids.Length; index++)
					sampleIdentities.Add(new SampleThreadIdentity(sample.Ids[index], sample.Created[index]));
			}
			HardThreadOwnership[] array = receipts.Where((HardThreadOwnership x) => x != null && x.Group == plan.Group
				&& x.VerifiedUtcTicks >= auditStartedUtc && (sampleIdentities == null ? Contains(sample, x.Thread)
					: sampleIdentities.Contains(new SampleThreadIdentity(x.Thread.Tid, x.Thread.Creation)))).ToArray();
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
			return candidates == plan.Choices.Length && (!plan.RequireWorkerCoverage || complete);
		}
	}
}
