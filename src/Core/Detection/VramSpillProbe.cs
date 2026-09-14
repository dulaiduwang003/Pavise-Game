// @author bdth 2074055628@qq.com
// File purpose Per-session sampling of game-process shared VRAM spilled into system memory, attributed in the end-of-match report when the peak exceeds the threshold
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

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
        private static int generation;
        private static bool busy, accepting, closed;

        public static void Reset()
        {
            lock (lk)
            {
                generation++;
                peakShared = 0; samples = 0; nextSampleTicks = 0;
                accepting = !closed;
            }
        }

        public static void Seal()
        { lock (lk) { accepting = false; generation++; } }

        public static void SampleIfDue(ICollection<int> gamePids)
        { SampleIfDueAt(gamePids, DateTime.UtcNow.Ticks); }

        internal static void SampleIfDueAt(ICollection<int> gamePids, long now)
        {
            if (gamePids == null || gamePids.Count == 0) return;
            lock (lk)
            {
                if (!accepting || closed || busy || now < nextSampleTicks) return;
                int mine = generation;
                // Freeze the PID set so the main scan thread mutating its own copy afterwards is harmless
                int[] pids = new int[gamePids.Count];
                gamePids.CopyTo(pids, 0);
                nextSampleTicks = now + MinIntervalSeconds * TimeSpan.TicksPerSecond;
                busy = true;
                try
                {
                    if (ThreadPool.QueueUserWorkItem(delegate { SampleWorker(mine, pids); })) return;
                }
                catch { }
                busy = false;
                Monitor.PulseAll(lk);
            }
        }

        private static void SampleWorker(int mine, int[] pids)
        {
            try
            {
                lock (lk) if (!accepting || closed || mine != generation) return;
                Dictionary<int, double> shared;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (ReadForTest != null) shared = ReadForTest();
                else
#endif
                {
                    // The first GPU inventory enumeration also goes to the background so a read-only diagnostic never stalls Boost
                    if (GpuInventory.IntegratedOnly) return;
                    shared = ReadSharedByPid();
                }
                if (shared == null) return;
                long best = 0;
                foreach (int pid in pids)
                {
                    double bytes;
                    if (shared.TryGetValue(pid, out bytes) && !double.IsNaN(bytes)
                        && !double.IsInfinity(bytes) && bytes > best && bytes < long.MaxValue)
                        best = (long)bytes;
                }
                lock (lk)
                {
                    if (!accepting || closed || mine != generation) return;
                    samples++;
                    if (best > peakShared) peakShared = best;
                }
            }
            catch { } // Diagnostic failure doesn't affect the match but must still release the single-flight slot
            finally
            {
                lock (lk) { busy = false; Monitor.PulseAll(lk); }
            }
        }

        internal static bool CloseForShutdown(int timeoutMs)
        {
            lock (lk) { closed = true; accepting = false; generation++; }
            return WaitForIdle(timeoutMs);
        }

        internal static bool WaitForIdle(int timeoutMs)
        {
            if (timeoutMs < 0) return false;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            lock (lk)
                while (busy)
                {
                    int left = (int)Math.Max(0L, timeoutMs - elapsed.ElapsedMilliseconds);
                    if (left == 0 || !Monitor.Wait(lk, left)) return !busy;
                }
            return true;
        }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static Func<Dictionary<int, double>> ReadForTest;
        internal static int SamplesForTest { get { lock (lk) return samples; } }
        internal static void ResetForTest()
        {
            lock (lk)
            {
                if (busy) throw new InvalidOperationException("VRAM worker is still active");
                ReadForTest = null; closed = false;
                Reset();
            }
        }
#endif

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
                        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) continue;
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
