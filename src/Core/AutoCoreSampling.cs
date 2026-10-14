using System;

namespace PaviseApp
{
    internal sealed class AutoCoreSampling
    {
        internal const int StableIntervalMs = 15000;
        internal const int FailureIntervalMs = 30000;
        private int stable, failures, lastActive = -1;
        private double lastLoad;
        private bool slow;
        private long next, totalCost, lastAttempt;
        internal int Count { get; private set; }
        internal int Reused { get; private set; }
        internal int IntervalMs { get; private set; }
        internal double MaxMs { get; private set; }
        internal double AverageMs { get { return Count == 0 ? 0 : totalCost / (double)TimeSpan.TicksPerMillisecond / Count; } }

        internal AutoCoreSampling() { IntervalMs = AutoCoreGovernor.WindowMs; }
        internal bool Due(long now) { return now >= next; }
        internal void Burst(long now)
        {
            if (failures >= 3 || slow) return;
            stable = 0; IntervalMs = AutoCoreGovernor.WindowMs;
            next = Math.Max(lastAttempt + IntervalMs * TimeSpan.TicksPerMillisecond, Math.Min(next, now));
        }
        internal void Record(long now, bool valid, int active, double load, long cost, bool reused)
        {
            Count++; if (reused) Reused++;
            lastAttempt = now;
            cost = Math.Max(0, cost); totalCost += cost;
            MaxMs = Math.Max(MaxMs, cost / (double)TimeSpan.TicksPerMillisecond);
            if (!valid)
            {
                stable = 0; failures++;
                IntervalMs = failures >= 3 ? FailureIntervalMs : AutoCoreGovernor.WindowMs;
            }
            else
            {
                failures = 0;
                stable = lastActive == active && Math.Abs(lastLoad - load) <= 0.35 ? stable + 1 : 0;
                lastActive = active; lastLoad = load;
                IntervalMs = stable >= 3 ? StableIntervalMs : AutoCoreGovernor.WindowMs;
            }
            // 查询耗时超出预算时退避。
            slow = cost > 4 * TimeSpan.TicksPerMillisecond;
            if (slow) IntervalMs = Math.Max(IntervalMs, StableIntervalMs);
            next = now + IntervalMs * TimeSpan.TicksPerMillisecond;
        }
    }

    internal sealed class AutoCoreLoadGuard
    {
        private long lastTime, lastCpu, pressureSince;
        private double lastLoad;
        internal double Load { get; private set; }
        internal bool Burst { get; private set; }
        internal bool Observe(long cpu, long time, int capacity, bool engaged)
        {
            Burst = false;
            if (time <= lastTime) return false;
            long delta = time - lastTime;
            if (lastTime == 0 || cpu < lastCpu || delta > 30 * TimeSpan.TicksPerSecond)
            { lastTime = time; lastCpu = cpu; pressureSince = 0; return false; }
            Load = (cpu - lastCpu) / (double)delta;
            lastTime = time; lastCpu = cpu;
            Burst = Math.Abs(Load - lastLoad) > 0.75; lastLoad = Load;
            if (!engaged || capacity <= 0 || Load <= capacity * 1.05)
            { pressureSince = 0; return false; }
            Burst = true;
            if (pressureSince == 0) pressureSince = time;
            return time - pressureSince >= 3 * TimeSpan.TicksPerSecond;
        }
    }
}
