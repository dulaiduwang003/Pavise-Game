// @author bdth 2074055628@qq.com
// 文件用途 按会话采样游戏进程溢出到系统内存的共享显存 峰值超阈值时在结束报告归因
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class VramSpillProbe
    {
        private const int MinIntervalSeconds = 20;
        internal const long WarnBytes = 1536L * 1024 * 1024;

        private static readonly object lk = new object();
        private static long nextSampleTicks;
        private static long peakShared;
        private static int samples;

        public static void Reset()
        {
            lock (lk) { peakShared = 0; samples = 0; nextSampleTicks = 0; }
        }

        public static void SampleIfDue(ICollection<int> gamePids)
        {
            if (gamePids == null || gamePids.Count == 0) return;
            if (GpuInventory.IntegratedOnly) return;
            long now = DateTime.UtcNow.Ticks;
            lock (lk)
            {
                if (now < nextSampleTicks) return;
                nextSampleTicks = now + MinIntervalSeconds * TimeSpan.TicksPerSecond;
            }
            Dictionary<int, double> shared = ReadSharedByPid();
            if (shared == null) return;
            long best = 0;
            foreach (int pid in gamePids)
            {
                double bytes;
                if (shared.TryGetValue(pid, out bytes) && bytes > best) best = (long)bytes;
            }
            lock (lk)
            {
                samples++;
                if (best > peakShared) peakShared = best;
            }
        }

        public static string Summarize()
        {
            long peak;
            int n;
            lock (lk) { peak = peakShared; n = samples; }
            if (n < 2 || peak < WarnBytes) return null;
            return (peak / 1024.0 / 1024.0 / 1024.0).ToString("0.0") + " GB";
        }

        private static Dictionary<int, double> ReadSharedByPid()
        {
            IntPtr query = IntPtr.Zero;
            try
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 || query == IntPtr.Zero) return null;
                IntPtr counter;
                if (PdhAddEnglishCounterW(query, @"\GPU Process Memory(*)\Shared Usage",
                        IntPtr.Zero, out counter) != 0)
                    return null;
                if (PdhCollectQueryData(query) != 0) return null;
                uint bufferSize = 0;
                uint itemCount = 0;
                uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE,
                    ref bufferSize, ref itemCount, IntPtr.Zero);
                if (status != PDH_MORE_DATA || bufferSize == 0) return null;
                IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
                try
                {
                    status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE,
                        ref bufferSize, ref itemCount, buffer);
                    if (status != 0) return null;
                    var result = new Dictionary<int, double>();
                    int itemSize = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
                    for (uint i = 0; i < itemCount; i++)
                    {
                        var item = (PdhFmtCounterValueItem)Marshal.PtrToStructure(
                            new IntPtr(buffer.ToInt64() + (long)i * itemSize), typeof(PdhFmtCounterValueItem));
                        if (item.Value.CStatus > 1) continue;
                        int pid = GpuEvidence.ParsePid(Marshal.PtrToStringUni(item.Name));
                        if (pid <= 0) continue;
                        double value = item.Value.DoubleValue;
                        if (value < 0) continue;
                        double sum;
                        result.TryGetValue(pid, out sum);
                        result[pid] = sum + value;
                    }
                    return result;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch { return null; }
            finally { if (query != IntPtr.Zero) { try { PdhCloseQuery(query); } catch { } } }
        }

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_MORE_DATA = 0x800007D2;

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValue
        {
            public uint CStatus;
            public double DoubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValueItem
        {
            public IntPtr Name;
            public PdhFmtCounterValue Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
            ref uint bufferSize, ref uint itemCount, IntPtr buffer);
        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
