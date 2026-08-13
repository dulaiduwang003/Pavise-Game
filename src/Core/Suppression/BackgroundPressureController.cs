// @author bdth 2074055628@qq.com
// 文件用途 根据后台资源压力调整压制等级

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal enum SuppressionLevel
    {
        None = 0,
        Eco = 1,
        Restrained = 2,
        Isolated = 3,
        Frozen = 4
    }

    internal static class ApplyFailureText
    {
        public static string Of(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return "原因未知";
            bool write = detail.IndexOf("-write", StringComparison.Ordinal) >= 0;
            bool readback = detail.IndexOf("-readback", StringComparison.Ordinal) >= 0;
            if (write && readback) return "写入被拒 多半是安全软件在保护自己";
            if (readback) return "写入后回读不符 被别的程序改回去了";
            if (write) return "写入被拒";
            return "原因未知";
        }
    }

    internal static class SuppressionLevelText
    {
        public static string Of(SuppressionLevel level)
        {
            switch (level)
            {
                case SuppressionLevel.Eco: return "省电";
                case SuppressionLevel.Restrained: return "限流";
                case SuppressionLevel.Isolated: return "隔离";
                case SuppressionLevel.Frozen: return "冻结";
                default: return "放行";
            }
        }
    }

    internal sealed class BackgroundPressureController
    {

        internal const long MinSampleTicks = TimeSpan.TicksPerSecond;
        internal const long MaxSampleTicks = TimeSpan.TicksPerSecond * 30;

        private sealed class Sample
        {
            public string Name;
            public long Creation;
            public long Cpu;
            public ulong Io;
            public long At;
            public int Heat;
            public int Cool;
            public SuppressionLevel Level;
        }

        private readonly Dictionary<int, Sample> samples = new Dictionary<int, Sample>();

        public SuppressionLevel Observe(int pid, string name, long creation, long cpu, ulong io, long now, PerformancePreset preset)
        {
            Sample old;
            if (!samples.TryGetValue(pid, out old) || old.Creation != creation || !string.Equals(old.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                samples[pid] = new Sample { Name = name, Creation = creation, Cpu = cpu, Io = io, At = now };
                return SuppressionLevel.None;
            }

            long dt = now - old.At;
            long dcpu = cpu - old.Cpu;

            if (dt < MinSampleTicks && dcpu >= 0) return old.Level;

            ulong dio = io >= old.Io ? io - old.Io : 0;
            old.Cpu = cpu; old.Io = io; old.At = now;
            if (dt < MinSampleTicks || dt > MaxSampleTicks || dcpu < 0)
            {
                if (dcpu < 0) { old.Heat = 0; old.Cool = 0; old.Level = SuppressionLevel.None; }
                return old.Level;
            }

            double cpuCores = (double)dcpu / dt;
            double ioMbSec = (double)dio / (1024.0 * 1024.0) / (dt / (double)TimeSpan.TicksPerSecond);
            double cpuThreshold = preset == PerformancePreset.Standard ? 0.08 : 0.05;
            double ioThreshold = preset == PerformancePreset.Standard ? 4.0 : 2.0;
            bool hot = cpuCores >= cpuThreshold || ioMbSec >= ioThreshold;
            bool severe = cpuCores >= 0.35 || ioMbSec >= 32.0;

            if (hot)
            {
                old.Cool = 0;
                old.Heat = Math.Min(5, old.Heat + (severe ? 2 : 1));
            }
            else if (++old.Cool >= 2)
            {
                old.Cool = 0;
                old.Heat = Math.Max(0, old.Heat - 1);
            }

            old.Level = Resolve(old.Heat, old.Level);
            return old.Level;
        }

        internal static SuppressionLevel Resolve(int heat, SuppressionLevel current)
        {
            if (heat >= 3) return SuppressionLevel.Isolated;
            if (heat >= 2 && current < SuppressionLevel.Restrained) return SuppressionLevel.Restrained;
            if (heat >= 1 && current < SuppressionLevel.Eco) return SuppressionLevel.Eco;
            if (current == SuppressionLevel.Isolated)
                return heat <= 1 ? SuppressionLevel.Restrained : current;
            if (current >= SuppressionLevel.Eco)
                return heat <= 0 ? (SuppressionLevel)((int)current - 1) : current;
            return heat >= 1 ? SuppressionLevel.Eco : SuppressionLevel.None;
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
