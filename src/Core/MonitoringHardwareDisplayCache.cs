using System;

namespace PaviseApp
{
    internal sealed class MonitoringHardwareDisplayCache
    {
        internal const int RetentionMilliseconds = 5000;
        private static readonly MonitoringMetrics[] Fields = {
            MonitoringMetrics.CpuPower, MonitoringMetrics.CpuTemperature,
            MonitoringMetrics.GpuUsage, MonitoringMetrics.GpuTemperature,
            MonitoringMetrics.GpuClock, MonitoringMetrics.GpuMemoryClock,
            MonitoringMetrics.GpuPower, MonitoringMetrics.GpuFan,
            MonitoringMetrics.Vram, MonitoringMetrics.VramUsage
        };
        private const int FanIndex = 7;
        private readonly double?[] values = new double?[Fields.Length];
        private readonly long[] sampledAt = new long[Fields.Length];
        private double? fanPercent;
        private MonitoringMetrics available;
        private bool hasSource, hasClock;
        private long sourceGeneration, lastClock;

        internal MonitoringMetrics Apply(long generation, MonitoringMetrics selected, MonitoringSensorReading reading, long now, bool fresh = true)
        {
            if (!hasSource || generation != sourceGeneration || (hasClock && now < lastClock)) Reset();
            hasSource = hasClock = true; sourceGeneration = generation; lastClock = now;
            selected &= MonitoringNativeSensors.HardwareMetrics;
            MonitoringMetrics retained = 0;
            for (int i = 0; i < Fields.Length; i++)
            {
                MonitoringMetrics field = Fields[i];
                if ((selected & field) == 0) { Clear(i); continue; }
                if (reading == null) continue;
                // 传输层复用不算新读数，不能延长显示缓存有效期。
                if (!fresh)
                {
                    Set(reading, i, null);
                    if (i == FanIndex) reading.GpuFanPercent = null;
                }
                double? value = Get(reading, i), alternate = i == FanIndex ? reading.GpuFanPercent : null;
                if (value.HasValue || alternate.HasValue)
                {
                    values[i] = value; sampledAt[i] = now; available |= field;
                    if (i == FanIndex) fanPercent = alternate;
                    continue;
                }
                long age = now - sampledAt[i];
                if ((available & field) != 0 && age >= 0 && age < RetentionMilliseconds)
                {
                    Set(reading, i, values[i]);
                    if (i == FanIndex) reading.GpuFanPercent = fanPercent;
                    retained |= field;
                }
                else Clear(i);
            }
            return retained;
        }
        internal void Reset()
        {
            Array.Clear(values, 0, values.Length); Array.Clear(sampledAt, 0, sampledAt.Length);
            fanPercent = null; available = 0; hasSource = hasClock = false;
            sourceGeneration = lastClock = 0;
        }
        private void Clear(int index)
        {
            values[index] = null; sampledAt[index] = 0; available &= ~Fields[index];
            if (index == FanIndex) fanPercent = null;
        }
        private static double? Get(MonitoringSensorReading reading, int index)
        {
            switch (index)
            {
                case 0: return reading.CpuPower;
                case 1: return reading.CpuTemperature;
                case 2: return reading.GpuUsage;
                case 3: return reading.GpuTemperature;
                case 4: return reading.GpuClock;
                case 5: return reading.GpuMemoryClock;
                case 6: return reading.GpuPower;
                case 7: return reading.GpuFanRpm;
                case 8: return reading.VramMiB;
                default: return reading.VramUsage;
            }
        }
        private static void Set(MonitoringSensorReading reading, int index, double? value)
        {
            switch (index)
            {
                case 0: reading.CpuPower = value; break;
                case 1: reading.CpuTemperature = value; break;
                case 2: reading.GpuUsage = value; break;
                case 3: reading.GpuTemperature = value; break;
                case 4: reading.GpuClock = value; break;
                case 5: reading.GpuMemoryClock = value; break;
                case 6: reading.GpuPower = value; break;
                case 7: reading.GpuFanRpm = value; break;
                case 8: reading.VramMiB = value; break;
                default: reading.VramUsage = value; break;
            }
        }
    }
}
