// @author bdth 2074055628@qq.com
// 文件用途 采样 DPC 干扰并生成需要避开的核心掩码

using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class DpcSampler
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessorPerf
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public uint InterruptCount;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

        private long[] previous;
        private long lastAt;
        private ulong noisyMask;
        private int candidate = -1;
        private int candidateHits;
        private int cleanHits;
        private int noisyCore = -1;

        public ulong NoisyPhysicalMask { get { return noisyMask; } }

        internal static long[] ReadBusyTicks()
        {
            int count = Math.Min(64, Environment.ProcessorCount);
            int stride = Marshal.SizeOf(typeof(ProcessorPerf));
            IntPtr mem = Marshal.AllocHGlobal(stride * count);
            try
            {
                int returned;
                if (NtQuerySystemInformation(8, mem, stride * count, out returned) != 0) return null;
                int actual = Math.Min(count, returned / stride);
                if (actual <= 0) return null;
                var now = new long[actual];
                for (int i = 0; i < actual; i++)
                {
                    var row = (ProcessorPerf)Marshal.PtrToStructure((IntPtr)((long)mem + i * stride), typeof(ProcessorPerf));
                    now[i] = row.DpcTime + row.InterruptTime;
                }
                return now;
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(mem); }
        }

        public static double[] MeasureInterruptRates(int windowMs)
        {
            double busy;
            return MeasureLoad(windowMs, out busy);
        }

        internal static bool ReadPerfRows(out long[] irq, out long[] idle)
        {
            irq = null; idle = null;
            int count = Math.Min(64, Environment.ProcessorCount);
            int stride = Marshal.SizeOf(typeof(ProcessorPerf));
            IntPtr mem = Marshal.AllocHGlobal(stride * count);
            try
            {
                int returned;
                if (NtQuerySystemInformation(8, mem, stride * count, out returned) != 0) return false;
                int actual = Math.Min(count, returned / stride);
                if (actual <= 0) return false;
                irq = new long[actual]; idle = new long[actual];
                for (int i = 0; i < actual; i++)
                {
                    var row = (ProcessorPerf)Marshal.PtrToStructure((IntPtr)((long)mem + i * stride), typeof(ProcessorPerf));
                    irq[i] = row.DpcTime + row.InterruptTime;
                    idle[i] = row.IdleTime;
                }
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(mem); }
        }

        public static double[] MeasureLoad(int windowMs, out double cpuBusy)
        {
            cpuBusy = 0;
            if (windowMs < 200) windowMs = 200;
            long[] irq1, idle1, irq2, idle2;
            if (!ReadPerfRows(out irq1, out idle1)) return null;
            long startAt = DateTime.UtcNow.Ticks;
            System.Threading.Thread.Sleep(windowMs);
            if (!ReadPerfRows(out irq2, out idle2)) return null;
            long endAt = DateTime.UtcNow.Ticks;
            if (irq2.Length != irq1.Length || endAt <= startAt) return null;
            double span = endAt - startAt;
            var rates = new double[irq1.Length];
            double idleSum = 0;
            for (int i = 0; i < irq1.Length; i++)
            {
                rates[i] = Math.Max(0, irq2[i] - irq1[i]) / span;
                idleSum += Math.Max(0, idle2[i] - idle1[i]) / span;
            }
            cpuBusy = Math.Max(0, Math.Min(1, 1.0 - idleSum / irq1.Length));
            return rates;
        }

        public void Sample()
        {
            int count = Math.Min(64, Environment.ProcessorCount);
            int stride = Marshal.SizeOf(typeof(ProcessorPerf));
            IntPtr mem = Marshal.AllocHGlobal(stride * count);
            try
            {
                int returned;
                if (NtQuerySystemInformation(8, mem, stride * count, out returned) != 0) return;
                int actual = Math.Min(count, returned / stride);
                var now = new long[actual];
                for (int i = 0; i < actual; i++)
                {
                    var row = (ProcessorPerf)Marshal.PtrToStructure((IntPtr)((long)mem + i * stride), typeof(ProcessorPerf));
                    now[i] = row.DpcTime + row.InterruptTime;
                }
                long at = DateTime.UtcNow.Ticks;
                if (previous != null && previous.Length == actual && at > lastAt)
                {
                    var rates = new double[actual];
                    for (int i = 0; i < actual; i++) rates[i] = Math.Max(0, now[i] - previous[i]) / (double)(at - lastAt);
                    int noisy = FindNoisy(rates);
                    ObserveCandidate(noisy);
                }
                previous = now; lastAt = at;
            }
            catch { }
            finally { Marshal.FreeHGlobal(mem); }
        }

        internal static int FindNoisy(double[] rates)
        {
            if (rates == null || rates.Length == 0) return -1;
            double sum = 0, max = 0; int index = -1;
            for (int i = 0; i < rates.Length; i++)
            {
                double v = Math.Max(0, rates[i]); sum += v;
                if (v > max) { max = v; index = i; }
            }
            double avg = sum / rates.Length;
            return max >= 0.04 && max >= avg * 2.0 ? index : -1;
        }

        internal void ObserveCandidate(int noisy)
        {
            if (noisy >= 0)
            {
                if (noisy == noisyCore) cleanHits = 0;
                else if (++cleanHits >= 2) { noisyMask = 0; noisyCore = -1; }
                if (candidate == noisy) candidateHits++;
                else { candidate = noisy; candidateHits = 1; }
                if (candidateHits >= 2)
                {
                    noisyMask = CpuTopology.ExpandPhysicalCoreMask(1UL << noisy);
                    noisyCore = noisy;
                    cleanHits = 0;
                }
            }
            else
            {
                candidate = -1;
                candidateHits = 0;
                if (++cleanHits >= 2) { noisyMask = 0; noisyCore = -1; }
            }
        }
    }
}
