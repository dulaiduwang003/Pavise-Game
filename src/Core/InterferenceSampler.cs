// @author bdth 2074055628@qq.com
// 文件用途 采样后台进程对游戏造成的资源争抢

using System;
using System.Collections.Generic;

namespace AegisApp
{
    [Flags]
    internal enum InterferenceKind
    {
        None = 0,
        Cpu = 1,
        Io = 2
    }

    internal struct InterferenceResult
    {
        public InterferenceKind Kind;
        public double CpuCores;
        public double IoBytesPerSecond;

        public string Describe()
        {
            if (Kind == (InterferenceKind.Cpu | InterferenceKind.Io))
                return "CPU " + (CpuCores * 100).ToString("0") + "% 单核 + IO " + FmtRate(IoBytesPerSecond);
            if ((Kind & InterferenceKind.Cpu) != 0)
                return "CPU " + (CpuCores * 100).ToString("0") + "% 单核";
            if ((Kind & InterferenceKind.Io) != 0)
                return "IO " + FmtRate(IoBytesPerSecond);
            return "";
        }

        private static string FmtRate(double bytes)
        {
            return (bytes / 1024d / 1024d).ToString("0.0") + " MB/s";
        }
    }

    internal sealed class InterferenceSampler
    {
        private sealed class Sample
        {
            public string Name;
            public long Creation;
            public long Cpu;
            public ulong Io;
            public long Wall;
        }

        private readonly Dictionary<int, Sample> samples = new Dictionary<int, Sample>();
        private readonly double cpuCoreThreshold;
        private readonly double ioBytesThreshold;

        public InterferenceSampler()
            : this(0.35d, 32d * 1024d * 1024d) { }

        internal InterferenceSampler(double cpuCoreThreshold, double ioBytesThreshold)
        {
            this.cpuCoreThreshold = cpuCoreThreshold;
            this.ioBytesThreshold = ioBytesThreshold;
        }

        public InterferenceResult Observe(int pid, string name, long creation, long cpu, ulong io, long wall)
        {
            var none = new InterferenceResult();
            Sample old;
            if (!samples.TryGetValue(pid, out old)
                || old.Creation != creation
                || !string.Equals(old.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                samples[pid] = NewSample(name, creation, cpu, io, wall);
                return none;
            }

            long elapsed = wall - old.Wall;
            long cpuDelta = cpu - old.Cpu;
            if (elapsed >= 0 && elapsed < TimeSpan.TicksPerSecond && cpuDelta >= 0 && io >= old.Io) return none;
            bool reset = elapsed < TimeSpan.TicksPerSecond || elapsed > TimeSpan.TicksPerSecond * 30
                      || cpuDelta < 0 || io < old.Io;
            samples[pid] = NewSample(name, creation, cpu, io, wall);
            if (reset) return none;

            double cpuCores = (double)cpuDelta / elapsed;
            double ioRate = (double)(io - old.Io) * TimeSpan.TicksPerSecond / elapsed;
            InterferenceKind kind = InterferenceKind.None;
            if (cpuCores >= cpuCoreThreshold) kind |= InterferenceKind.Cpu;
            if (ioRate >= ioBytesThreshold) kind |= InterferenceKind.Io;
            return new InterferenceResult { Kind = kind, CpuCores = cpuCores, IoBytesPerSecond = ioRate };
        }

        public void Forget(int pid) { samples.Remove(pid); }

        public void Prune(HashSet<int> live)
        {
            List<int> dead = null;
            foreach (int pid in samples.Keys)
                if (!live.Contains(pid)) { if (dead == null) dead = new List<int>(); dead.Add(pid); }
            if (dead != null) foreach (int pid in dead) samples.Remove(pid);
        }

        public void Clear() { samples.Clear(); }

        private static Sample NewSample(string name, long creation, long cpu, ulong io, long wall)
        {
            return new Sample { Name = name, Creation = creation, Cpu = cpu, Io = io, Wall = wall };
        }
    }
}
