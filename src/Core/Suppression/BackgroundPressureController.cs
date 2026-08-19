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
            if (string.IsNullOrEmpty(detail)) return Lang.T("t.backgroundpressurecontroller.1");
            bool write = detail.IndexOf("-write", StringComparison.Ordinal) >= 0;
            bool readback = detail.IndexOf("-readback", StringComparison.Ordinal) >= 0;
            if (write && readback) return Lang.T("t.backgroundpressurecontroller.2");
            if (readback) return Lang.T("t.backgroundpressurecontroller.3");
            if (write) return Lang.T("t.backgroundpressurecontroller.4");
            return Lang.T("t.backgroundpressurecontroller.1") + "(" + detail + ")";
        }
    }

    internal static class SuppressionLevelText
    {
        public static string Of(SuppressionLevel level)
        {
            switch (level)
            {
                case SuppressionLevel.Eco: return Lang.T("t.backgroundpressurecontroller.5");
                case SuppressionLevel.Restrained: return Lang.T("t.backgroundpressurecontroller.6");
                case SuppressionLevel.Isolated: return Lang.T("t.backgroundpressurecontroller.7");
                case SuppressionLevel.Frozen: return Lang.T("t.backgroundpressurecontroller.8");
                default: return Lang.T("t.backgroundpressurecontroller.9");
            }
        }
    }

    internal sealed class BackgroundPressureController
    {

        internal const long MinSampleTicks = TimeSpan.TicksPerSecond;
        internal const long MaxSampleTicks = TimeSpan.TicksPerSecond * 30;

        internal const long MinDwellTicks = TimeSpan.TicksPerSecond * 15;

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
            public long LevelAt;
            public PerformancePreset Preset;
            public bool PresetSeen;
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
            bool crushing = cpuCores >= 1.5 || ioMbSec >= 64.0;

            if (hot)
            {
                old.Cool = 0;
                old.Heat = Math.Min(5, old.Heat + (crushing ? 3 : severe ? 2 : 1));
            }
            else if (++old.Cool >= 2)
            {
                old.Cool = 0;
                old.Heat = Math.Max(0, old.Heat - 1);
            }

            bool presetChanged = old.PresetSeen && old.Preset != preset;
            old.Preset = preset;
            old.PresetSeen = true;

            if (presetChanged)
            {
                old.Level = Resolve(old.Heat, SuppressionLevel.None);
                old.LevelAt = now;
                return old.Level;
            }

            SuppressionLevel desired = Resolve(old.Heat, old.Level);
            if (desired > old.Level && (old.LevelAt == 0 || now - old.LevelAt >= MinDwellTicks))
            {
                old.Level = desired;
                old.LevelAt = now;
            }
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
