// 作者：bdth 2074055628@qq.com
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class DpcSampler
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

        public static double[] MeasureLoad(int windowMs, out double cpuBusy,
            Func<bool> cancelled = null, Action<float> progress = null)
        {
            cpuBusy = 0;
            if (cancelled != null && cancelled()) return null;
            if (windowMs < 200) windowMs = 200;
            long[] irq1, idle1, irq2, idle2;
            if (!ReadPerfRows(out irq1, out idle1)) return null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < windowMs)
            {
                if (cancelled != null && cancelled()) return null;
                if (progress != null) progress(Math.Min(1f, clock.ElapsedMilliseconds / (float)windowMs));
                int left = windowMs - (int)clock.ElapsedMilliseconds;
                if (left > 0) System.Threading.Thread.Sleep(Math.Min(200, left));
            }
            if (cancelled != null && cancelled()) return null;
            if (!ReadPerfRows(out irq2, out idle2)) return null;
            double span = clock.ElapsedTicks * (10000000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (irq2.Length != irq1.Length || span <= 0) return null;
            if (progress != null) progress(1f);
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

    }
}
