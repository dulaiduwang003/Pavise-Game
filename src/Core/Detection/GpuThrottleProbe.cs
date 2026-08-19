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

        public static void SampleIfDue(string gameExePath)
        {
            if (!NvApi.Available) return;
            long now = DateTime.UtcNow.Ticks;
            lock (lk)
            {
                if (now < nextSampleTicks) return;
                nextSampleTicks = now + MinIntervalSeconds * TimeSpan.TicksPerSecond;
            }
            if (GpuInventory.Hybrid && GameExeTweaks.PrefersIntegrated(gameExePath)) return;
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
            if (handles == null)
            {
                lock (lk) { gpusResolved = false; }
                return false;
            }
            bool any = false;
            foreach (IntPtr h in handles)
            {
                uint mask;
                if (NvApi.TryGetPerfDecrease(h, out mask)) { combined |= mask; any = true; }
            }
            if (!any)
                lock (lk) { gpusResolved = false; gpus = null; }
            return any;
        }

        public static string Summarize()
        {
            int n, t, p, b;
            lock (lk) { n = samples; t = thermalHits; p = powerHits; b = batteryHits; }
            if (n < MinSamplesForVerdict) return null;
            var parts = new List<string>();
            if (p > 0) parts.Add(Lang.T("t.gputhrottleprobe.1") + Percent(p, n));
            if (t > 0) parts.Add(Lang.T("t.gputhrottleprobe.2") + Percent(t, n));
            if (b > 0) parts.Add(Lang.T("t.gputhrottleprobe.3") + Percent(b, n));
            if (parts.Count == 0) return null;
            return string.Join(" ", parts.ToArray()) + Lang.T("t.gputhrottleprobe.4") + n + Lang.T("t.gputhrottleprobe.5");
        }

        public static string InstantText()
        {
            uint mask;
            if (!TryReadMask(out mask)) return null;
            if (mask == 0) return Lang.T("t.gputhrottleprobe.6");
            var parts = new List<string>();
            if ((mask & NvApi.PerfDecreaseThermal) != 0) parts.Add(Lang.T("t.gputhrottleprobe.7"));
            if ((mask & (NvApi.PerfDecreasePower | NvApi.PerfDecreaseInsufficientPower)) != 0) parts.Add(Lang.T("t.gputhrottleprobe.8"));
            if ((mask & NvApi.PerfDecreaseAcBatt) != 0) parts.Add(Lang.T("t.gputhrottleprobe.9"));
            if ((mask & NvApi.PerfDecreaseApi) != 0) parts.Add(Lang.T("t.gputhrottleprobe.10"));
            if (parts.Count == 0) return Lang.T("t.gputhrottleprobe.11") + mask.ToString("X");
            return string.Join(" ", parts.ToArray());
        }

        internal static string Percent(int hits, int total)
        {
            if (total <= 0) return "0%";
            return (hits * 100 / total) + "%";
        }
    }
}
