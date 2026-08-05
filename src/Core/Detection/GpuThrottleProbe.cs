// @author bdth 2074055628@qq.com
// 文件用途 按会话累计 NVIDIA GPU 降频原因采样 归因功耗墙温度墙电池限制

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class GpuThrottleProbe
    {
        private const int MinIntervalSeconds = 15;
        private const int MinSamplesForVerdict = 4;
        private static readonly object lk = new object();
        private static IntPtr[] gpus;
        private static bool gpusResolved;
        private static long nextSampleTicks;
        private static int samples, thermalHits, powerHits, batteryHits;

        public static void Reset()
        {
            lock (lk)
            {
                samples = 0; thermalHits = 0; powerHits = 0; batteryHits = 0;
                nextSampleTicks = 0;
            }
        }

        public static void SampleIfDue()
        {
            if (!NvApi.Available) return;
            long now = DateTime.UtcNow.Ticks;
            lock (lk)
            {
                if (now < nextSampleTicks) return;
                nextSampleTicks = now + MinIntervalSeconds * TimeSpan.TicksPerSecond;
            }
            uint mask;
            if (!TryReadMask(out mask)) return;
            lock (lk)
            {
                samples++;
                if ((mask & NvApi.PerfDecreaseThermal) != 0) thermalHits++;
                if ((mask & (NvApi.PerfDecreasePower | NvApi.PerfDecreaseInsufficientPower)) != 0) powerHits++;
                if ((mask & NvApi.PerfDecreaseAcBatt) != 0) batteryHits++;
            }
        }

        public static bool TryReadMask(out uint combined)
        {
            combined = 0;
            if (!NvApi.Available) return false;
            IntPtr[] handles;
            lock (lk)
            {
                if (!gpusResolved) { gpus = NvApi.EnumGpuHandles(); gpusResolved = true; }
                handles = gpus;
            }
            if (handles == null) return false;
            bool any = false;
            foreach (IntPtr h in handles)
            {
                uint mask;
                if (NvApi.TryGetPerfDecrease(h, out mask)) { combined |= mask; any = true; }
            }
            return any;
        }

        public static string Summarize()
        {
            int n, t, p, b;
            lock (lk) { n = samples; t = thermalHits; p = powerHits; b = batteryHits; }
            if (n < MinSamplesForVerdict) return null;
            var parts = new List<string>();
            if (p > 0) parts.Add("功耗墙 " + Percent(p, n));
            if (t > 0) parts.Add("温度墙 " + Percent(t, n));
            if (b > 0) parts.Add("电池限制 " + Percent(b, n));
            if (parts.Count == 0) return null;
            return string.Join("、", parts.ToArray()) + "（这局共查 " + n + " 次）";
        }

        public static string InstantText()
        {
            uint mask;
            if (!TryReadMask(out mask)) return null;
            if (mask == 0) return "无限制";
            var parts = new List<string>();
            if ((mask & NvApi.PerfDecreaseThermal) != 0) parts.Add("温度墙");
            if ((mask & (NvApi.PerfDecreasePower | NvApi.PerfDecreaseInsufficientPower)) != 0) parts.Add("功耗墙");
            if ((mask & NvApi.PerfDecreaseAcBatt) != 0) parts.Add("电池限制");
            if ((mask & NvApi.PerfDecreaseApi) != 0) parts.Add("软件限制");
            if (parts.Count == 0) return "其他限制 (0x" + mask.ToString("X") + ")";
            return string.Join("、", parts.ToArray());
        }

        internal static string Percent(int hits, int total)
        {
            if (total <= 0) return "0%";
            return (hits * 100 / total) + "%";
        }
    }
}
