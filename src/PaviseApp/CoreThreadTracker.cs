using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class CoreThreadTracker
	{
		private readonly Dictionary<long, CoreThreadCandidate> tracked = new Dictionary<long, CoreThreadCandidate>();

		internal static bool ValidShape(ThreadTimes sample)
		{
			if (sample != null && sample.Ids != null && sample.Created != null && sample.Cpu != null && sample.Ids.Length <= 16384 && sample.Ids.Length == sample.Created.Length)
			{
				return sample.Ids.Length == sample.Cpu.Length;
			}
			return false;
		}

		internal List<CoreThreadCandidate> Observe(ThreadTimes before, ThreadTimes after)
		{
			return Observe(before, after, 2);
		}

		internal List<CoreThreadCandidate> Observe(ThreadTimes before, ThreadTimes after, int keep)
		{
			return Observe(before, after, keep, 0.35);
		}

		// minimumShare is the per-window share of one logical CPU a thread must keep to stay tracked.
		internal List<CoreThreadCandidate> Observe(ThreadTimes before, ThreadTimes after, int keep, double minimumShare)
		{
			keep = Math.Max(1, keep);
			List<CoreThreadCandidate> list = new List<CoreThreadCandidate>();
			if (!ValidShape(before) || !ValidShape(after) || !AutoCores.WindowUsable(before, after))
			{
				tracked.Clear();
				return list;
			}
			Dictionary<long, int> dictionary = new Dictionary<long, int>();
			for (int i = 0; i < before.Ids.Length; i++)
			{
				dictionary[before.Ids[i]] = i;
			}
			Dictionary<long, CoreThreadCandidate> dictionary2 = new Dictionary<long, CoreThreadCandidate>();
			for (int j = 0; j < after.Ids.Length; j++)
			{
				long numericValue = after.Ids[j];
				int value;
				if (numericValue <= 0 || numericValue > int.MaxValue || after.Created[j] <= 0 || !dictionary.TryGetValue(numericValue, out value) || before.Created[value] != after.Created[j])
				{
					continue;
				}
				double lastValue = (double)(after.Cpu[j] - before.Cpu[value]) / (double)(after.Ticks - before.Ticks);
				if (!(lastValue < minimumShare) && !(lastValue > 1.1))
				{
					CoreThreadCandidate value2;
					if (!tracked.TryGetValue(numericValue, out value2) || value2.Creation != after.Created[j] || Math.Abs(value2.Last - lastValue) > 0.25)
					{
						CoreThreadCandidate coreThreadCandidate = new CoreThreadCandidate();
						coreThreadCandidate.Tid = (int)numericValue;
						coreThreadCandidate.Creation = after.Created[j];
						value2 = coreThreadCandidate;
					}
					value2.Mean = (value2.Mean * (double)Math.Min(7, value2.Windows) + lastValue) / (double)(Math.Min(7, value2.Windows) + 1);
					value2.Windows++;
					value2.Last = lastValue;
					dictionary2[numericValue] = value2;
					if (value2.Windows >= 3)
					{
						list.Add(value2);
					}
				}
			}
			tracked.Clear();
			foreach (KeyValuePair<long, CoreThreadCandidate> item in dictionary2)
			{
				tracked[item.Key] = item.Value;
			}
			list.Sort((CoreThreadCandidate a, CoreThreadCandidate b) => b.Mean.CompareTo(a.Mean));
			if (list.Count > keep)
			{
				list.RemoveRange(keep, list.Count - keep);
			}
			return list;
		}
	}
}
