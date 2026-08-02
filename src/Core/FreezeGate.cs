// @author bdth 2074055628@qq.com
// 文件用途 冻结资格判定 只放行长时间零占用且无可见窗口的后台进程

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class FreezeDwellTracker
    {
        internal const double QuietCores = 0.005;
        internal const int DwellSeconds = 30;
        private const long MinSampleTicks = TimeSpan.TicksPerSecond;

        private sealed class Sample
        {
            public string Name;
            public long Creation;
            public long Cpu;
            public long At;
            public long QuietSince;
        }

        private readonly Dictionary<int, Sample> samples = new Dictionary<int, Sample>();

        public bool Observe(int pid, string name, long creation, long cpu, long now)
        {
            Sample previous;
            if (!samples.TryGetValue(pid, out previous)
                || previous.Creation != creation
                || !string.Equals(previous.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                samples[pid] = new Sample
                {
                    Name = name,
                    Creation = creation,
                    Cpu = cpu,
                    At = now
                };
                return false;
            }

            long dt = now - previous.At;
            if (dt < MinSampleTicks) return Settled(previous, now);

            long dcpu = cpu - previous.Cpu;
            previous.Cpu = cpu;
            previous.At = now;
            if (dcpu < 0 || (double)dcpu / dt >= QuietCores)
            {
                previous.QuietSince = 0;
                return false;
            }
            if (previous.QuietSince == 0) previous.QuietSince = now;
            return Settled(previous, now);
        }

        private static bool Settled(Sample sample, long now)
        {
            return sample.QuietSince != 0
                && now - sample.QuietSince >= DwellSeconds * TimeSpan.TicksPerSecond;
        }

        public void Forget(int pid) { samples.Remove(pid); }

        public void Prune(HashSet<int> live)
        {
            var dead = new List<int>();
            foreach (int pid in samples.Keys) if (!live.Contains(pid)) dead.Add(pid);
            foreach (int pid in dead) samples.Remove(pid);
        }

        public void Clear() { samples.Clear(); }
    }
}
