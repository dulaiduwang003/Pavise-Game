// Per-core whole-system utilization, weighted by valid observed time. This is
// optional evidence for the exact IRQ capture epoch, not a renderer CPU metric.
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class IrqCoreLoadRecord
    {
        public int Cpu;
        public double AveragePercent;
        public long ObservedTicks;
        public int Samples;
    }

    internal sealed class IrqCoreLoadCapture : IDisposable
    {
        internal const long PollTicks = 2 * TimeSpan.TicksPerSecond;
        private ICoreLoadSource source;
        private readonly ulong systemMask;
        private readonly long began;
        private long previous;
        private readonly double[] weighted = new double[64];
        private readonly long[] observed = new long[64];
        private readonly int[] samples = new int[64];
        private bool closed;

        internal IrqCoreLoadCapture(ICoreLoadSource source, long began, ulong systemMask)
        { this.source = source; this.began = previous = began; this.systemMask = systemMask; }

        internal void Poll(long now) { Read(now, false); }

        private void Read(long now, bool final)
        {
            if (closed || source == null || now <= previous) return;
            long duration = now - previous;
            if (!final && duration < PollTicks) return;
            previous = now;
            Dictionary<int, double> values;
            try { values = source.Read(); }
            catch
            {
                // A failed optional counter must not lose earlier valid intervals
                // or invalidate the independently collected IRQ observation.
                ICoreLoadSource failed = source;
                source = null;
                try { failed.Dispose(); } catch { }
                return;
            }
            if (values == null) return;
            foreach (var item in values)
            {
                int cpu = item.Key;
                double value = item.Value;
                if (cpu < 0 || cpu >= 64 || (systemMask & (1UL << cpu)) == 0
                    || double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 100
                    || long.MaxValue - observed[cpu] < duration || samples[cpu] == int.MaxValue) continue;
                weighted[cpu] += value * duration;
                observed[cpu] += duration;
                samples[cpu]++;
            }
        }

        internal void Finish(long now, IrqSessionRecord target)
        {
            if (closed || target == null) return;
            try
            {
                Read(now, true);
                if (now <= began) return;
                target.CoreLoadWindowTicks = now - began;
                for (int cpu = 0; cpu < 64; cpu++)
                    if (observed[cpu] > 0 && samples[cpu] > 0)
                        target.CoreLoads.Add(new IrqCoreLoadRecord { Cpu = cpu,
                            AveragePercent = weighted[cpu] / observed[cpu],
                            ObservedTicks = observed[cpu], Samples = samples[cpu] });
            }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            if (closed) return;
            closed = true;
            ICoreLoadSource old = source;
            source = null;
            if (old != null) try { old.Dispose(); } catch { }
        }
    }
}
