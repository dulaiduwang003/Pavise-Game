using System;
using System.Collections.Generic;
using System.Linq;

namespace PaviseApp
{
    // 辅助角色允许短突发低均值；只保存最近六个有效窗口，不把加载期峰值永久带进对局。
    internal sealed class CriticalThreadAuxiliaryObservation
    {
        private const int WindowLimit = 6;
        private sealed class Activity
        {
            internal HardThreadIdentity Thread;
            internal readonly double[] Shares = new double[WindowLimit];
            internal int Count, Next;
            internal double Latest;
        }
        private readonly Dictionary<string, Activity> activity = new Dictionary<string, Activity>(StringComparer.Ordinal);
        private ThreadTimes previous;
        internal long LatestTicks { get; private set; }

        internal void Reset()
        {
            previous = null;
            activity.Clear();
            LatestTicks = 0;
        }

        internal void Observe(ThreadTimes sample, long birth, long now)
        {
            if (!CriticalThreadObservation.Valid(sample, birth, now) || sample.Ids.Length > 512)
            { Reset(); return; }
            if (previous == null || previous.Creation != birth || sample.Ticks <= previous.Ticks
                || sample.ProcessCpu < previous.ProcessCpu
                || sample.Ticks - previous.Ticks > TimeSpan.FromSeconds(60).Ticks)
            {
                if (previous != null && sample.Creation == previous.Creation && sample.Ticks == previous.Ticks
                    && sample.ProcessCpu == previous.ProcessCpu && sample.Ids.SequenceEqual(previous.Ids)
                    && sample.Created.SequenceEqual(previous.Created) && sample.Cpu.SequenceEqual(previous.Cpu)) return;
                Reset(); previous = Copy(sample); return;
            }
            var before = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < previous.Ids.Length; index++)
                before.Add(new HardThreadIdentity((int)previous.Ids[index], previous.Created[index]).Key, index);
            var present = new HashSet<string>(StringComparer.Ordinal);
            long elapsed = sample.Ticks - previous.Ticks;
            for (int index = 0; index < sample.Ids.Length; index++)
            {
                var thread = new HardThreadIdentity((int)sample.Ids[index], sample.Created[index]);
                int oldIndex;
                if (!before.TryGetValue(thread.Key, out oldIndex)) continue;
                long delta = sample.Cpu[index] - previous.Cpu[oldIndex];
                double share = (double)delta / elapsed;
                if (delta < 0 || share > 1.1) continue;
                Activity current;
                if (!activity.TryGetValue(thread.Key, out current))
                    activity.Add(thread.Key, current = new Activity { Thread = thread });
                current.Shares[current.Next] = current.Latest = share;
                current.Next = (current.Next + 1) % WindowLimit;
                current.Count = Math.Min(WindowLimit, current.Count + 1);
                present.Add(thread.Key);
            }
            foreach (string key in activity.Keys.Where(key => !present.Contains(key)).ToArray()) activity.Remove(key);
            previous = Copy(sample);
            LatestTicks = sample.Ticks;
        }

        internal CriticalThreadAuxiliaryCandidate[] Candidates()
        {
            return activity.Values.Where(item => item.Count >= CriticalThreadAuxiliaryPolicy.MinimumWindows)
                .Select(item => new CriticalThreadAuxiliaryCandidate { Thread = item.Thread, Windows = item.Count,
                    MeanShare = item.Shares.Take(item.Count).Average(), PeakShare = item.Shares.Take(item.Count).Max() })
                .OrderByDescending(item => item.MeanShare).ThenBy(item => item.Thread.Tid).ToArray();
        }

        internal bool LatestDemand(HardThreadChoice[] members, out double demand, out int present)
        {
            demand = 0; present = 0;
            foreach (HardThreadChoice member in members ?? new HardThreadChoice[0])
            {
                Activity current;
                if (member != null && activity.TryGetValue(member.Thread.Key, out current))
                { demand += current.Latest; present++; }
            }
            return LatestTicks > 0 && present == (members == null ? 0 : members.Length);
        }

        private static ThreadTimes Copy(ThreadTimes sample)
        {
            return new ThreadTimes { Creation = sample.Creation, Ticks = sample.Ticks, ProcessCpu = sample.ProcessCpu,
                Ids = (long[])sample.Ids.Clone(), Created = (long[])sample.Created.Clone(), Cpu = (long[])sample.Cpu.Clone() };
        }
    }
}
