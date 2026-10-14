// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class GpuThrottleProbe
    {
        private static readonly object lk = new object();
        private static IntPtr[] gpus;
        private static bool gpusResolved;

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
    }
}
