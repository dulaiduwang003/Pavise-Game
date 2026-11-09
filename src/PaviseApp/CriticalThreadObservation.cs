using System.Collections.Generic;
using System.Linq;

namespace PaviseApp
{
    internal sealed class CriticalThreadObservation
    {
        internal const int MaximumThreads = 16384;

        internal const int MaximumHotspots = 8;

        internal const long MaximumSampleAge = 15000000L;

        private CoreThreadTracker tracker = new CoreThreadTracker();

        private ThreadTimes previous;

        private CriticalThreadHotspot[] candidates = new CriticalThreadHotspot[0];

        private CriticalThreadHotspot[] hotspots = new CriticalThreadHotspot[0];

        internal string Detail { get; private set; }

        internal CriticalThreadHotspot[] Candidates
        {
            get
            {
                return (CriticalThreadHotspot[])candidates.Clone();
            }
        }

        internal CriticalThreadHotspot[] Hotspots
        {
            get
            {
                return (CriticalThreadHotspot[])hotspots.Clone();
            }
        }

        internal static bool Valid(ThreadTimes sample, long processBirth, long now)
        {
            if (!CoreThreadTracker.ValidShape(sample) || sample.Creation != processBirth || processBirth <= 0 || sample.Ids.Length == 0 || sample.Ids.Length > 16384 || sample.ProcessCpu < 0 || sample.Ticks <= 0 || now < sample.Ticks || now - sample.Ticks > 15000000)
            {
                return false;
            }
            HashSet<long> hashSet = new HashSet<long>();
            for (int i = 0; i < sample.Ids.Length; i++)
            {
                if (sample.Ids[i] <= 0 || sample.Ids[i] > int.MaxValue || sample.Created[i] <= 0 || sample.Cpu[i] < 0 || !hashSet.Add(sample.Ids[i]))
                {
                    return false;
                }
            }
            return true;
        }

        internal bool Observe(ThreadTimes sample, long processBirth, long now)
        {
            if (!Valid(sample, processBirth, now))
            {
                Reset();
                Detail = "unknown or over-budget thread snapshot";
                return false;
            }
            ThreadTimes threadTimes = new ThreadTimes();
            threadTimes.Creation = sample.Creation;
            threadTimes.Ticks = sample.Ticks;
            threadTimes.ProcessCpu = sample.ProcessCpu;
            threadTimes.Ids = (long[])sample.Ids.Clone();
            threadTimes.Created = (long[])sample.Created.Clone();
            threadTimes.Cpu = (long[])sample.Cpu.Clone();
            ThreadTimes threadTimes2 = threadTimes;
            if (previous == null || previous.Creation != threadTimes2.Creation)
            {
                Reset();
                previous = threadTimes2;
                Detail = "first original thread snapshot";
                return true;
            }
            if (threadTimes2.Ticks < previous.Ticks || threadTimes2.ProcessCpu < previous.ProcessCpu)
            {
                Reset();
                Detail = "thread snapshot clock or process counter moved backwards";
                return false;
            }
            if (threadTimes2.Ticks == previous.Ticks)
            {
                Detail = "shared snapshot has not advanced";
                return true;
            }
            hotspots = (from x in tracker.Observe(previous, threadTimes2, 8, CriticalThreadAutoPolicy.MinimumShare)
                select new CriticalThreadHotspot(x)).ToArray();
            // 手动规则在 CriticalThreadRuntimePolicy.Select 中保留独立的 35% 准入条件。
            candidates = hotspots.Take(2).ToArray();
            previous = threadTimes2;
            Detail = ((candidates.Length == 0) ? "waiting for sustained CPU hotspot windows" : "CPU hotspot candidates; roles unverified");
            return true;
        }

        internal void Reset()
        {
            tracker = new CoreThreadTracker();
            previous = null;
            candidates = new CriticalThreadHotspot[0];
            hotspots = new CriticalThreadHotspot[0];
            Detail = "";
        }
    }
}
