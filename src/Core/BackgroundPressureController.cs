// @author bdth 2074055628@qq.com
// 文件用途 根据后台资源压力调整压制等级

using System;
using System.Collections.Generic;

namespace AegisApp
{
    internal enum SuppressionLevel
    {
        None = 0,
        Eco = 1,
        Restrained = 2,
        Isolated = 3
    }

    internal sealed class BackgroundPressureController
    {
        // 有效采样窗口：短于 1 秒速率失真，长于 30 秒说明中间断过档、基线不可信
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
            ulong dio = io >= old.Io ? io - old.Io : 0;
            old.Cpu = cpu; old.Io = io; old.At = now;
            // 采样窗口太短的话速率会被放大到失真：扫描不是固定 4 秒一轮，进程频繁启停时
            // 会被事件驱动以 200ms 的合并窗口反复唤醒，dt≈0.2s 时 20ms 的 CPU 占用会算成
            // 0.1 核、直接越过阈值，普通程序几百毫秒内就被升到 Isolated。
            // 窗口不足时只更新基线、不计热度，与 InterferenceSampler 的做法保持一致。
            if (dt < MinSampleTicks || dt > MaxSampleTicks || dcpu < 0)
            {
                if (dcpu < 0) { old.Heat = 0; old.Cool = 0; }
                return SuppressionLevel.None;
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
                old.Heat = Math.Min(5, old.Heat + (preset != PerformancePreset.Standard && severe ? 2 : 1));
            }
            else if (++old.Cool >= 2)
            {
                old.Cool = 0;
                old.Heat = Math.Max(0, old.Heat - 1);
            }

            if (old.Heat >= 3) return SuppressionLevel.Isolated;
            if (old.Heat >= 2) return SuppressionLevel.Restrained;
            if (old.Heat >= 1) return SuppressionLevel.Eco;
            return SuppressionLevel.None;
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
