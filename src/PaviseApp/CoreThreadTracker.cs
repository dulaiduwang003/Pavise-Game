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

        // minimumShare 表示线程每个采样窗口需持续占用的单个逻辑处理器份额。
        internal List<CoreThreadCandidate> Observe(ThreadTimes before, ThreadTimes after, int keep, double minimumShare)
        {
            keep = Math.Max(1, keep);
            List<CoreThreadCandidate> stableCandidates = new List<CoreThreadCandidate>();
            if (!ValidShape(before) || !ValidShape(after) || !AutoCores.WindowUsable(before, after))
            {
                tracked.Clear();
                return stableCandidates;
            }
            Dictionary<long, int> beforeIndexByTid = new Dictionary<long, int>();
            for (int i = 0; i < before.Ids.Length; i++)
            {
                beforeIndexByTid[before.Ids[i]] = i;
            }
            Dictionary<long, CoreThreadCandidate> currentCandidatesByTid = new Dictionary<long, CoreThreadCandidate>();
            for (int j = 0; j < after.Ids.Length; j++)
            {
                long threadId = after.Ids[j];
                int beforeIndex;
                if (threadId <= 0 || threadId > int.MaxValue || after.Created[j] <= 0 || !beforeIndexByTid.TryGetValue(threadId, out beforeIndex) || before.Created[beforeIndex] != after.Created[j])
                {
                    continue;
                }
                double cpuShare = (double)(after.Cpu[j] - before.Cpu[beforeIndex]) / (double)(after.Ticks - before.Ticks);
                if (!(cpuShare < minimumShare) && !(cpuShare > 1.1))
                {
                    CoreThreadCandidate trackedCandidate;
                    if (!tracked.TryGetValue(threadId, out trackedCandidate) || trackedCandidate.Creation != after.Created[j] || Math.Abs(trackedCandidate.Last - cpuShare) > 0.25)
                    {
                        CoreThreadCandidate coreThreadCandidate = new CoreThreadCandidate();
                        coreThreadCandidate.Tid = (int)threadId;
                        coreThreadCandidate.Creation = after.Created[j];
                        trackedCandidate = coreThreadCandidate;
                    }
                    trackedCandidate.Mean = (trackedCandidate.Mean * (double)Math.Min(7, trackedCandidate.Windows) + cpuShare) / (double)(Math.Min(7, trackedCandidate.Windows) + 1);
                    trackedCandidate.Windows++;
                    trackedCandidate.Last = cpuShare;
                    currentCandidatesByTid[threadId] = trackedCandidate;
                    if (trackedCandidate.Windows >= 3)
                    {
                        stableCandidates.Add(trackedCandidate);
                    }
                }
            }
            tracked.Clear();
            foreach (KeyValuePair<long, CoreThreadCandidate> candidateEntry in currentCandidatesByTid)
            {
                tracked[candidateEntry.Key] = candidateEntry.Value;
            }
            stableCandidates.Sort((CoreThreadCandidate a, CoreThreadCandidate b) => b.Mean.CompareTo(a.Mean));
            if (stableCandidates.Count > keep)
            {
                stableCandidates.RemoveRange(keep, stableCandidates.Count - keep);
            }
            return stableCandidates;
        }
    }
}
