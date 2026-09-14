// @author bdth 2074055628@qq.com
// File purpose One-time working set trim of suppressed background processes; pages move to the standby list, physical memory goes to the game
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class WsTrim
    {
        public static volatile bool Enabled;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int GetProcessId(IntPtr process);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        private static readonly object lk = new object();
        private static readonly Dictionary<int, long> done = new Dictionary<int, long>();

        // With memory not tight, trimming is pure cost: the suppressed app re-faults on switch-back and the game gets not one extra page
        //   Trim only when absolute available is below 4 GiB and below 1/8 of total; if either side is enough, leave it
        //   Sample cached for 5 seconds, no per-process query
        internal const ulong FloorBytes = 4UL * 1024 * 1024 * 1024;
        internal const long SampleIntervalTicks = TimeSpan.TicksPerSecond * 5;
        private static long sampledTicks;
        private static bool pressureCached;

        internal static bool ShouldTrim(ulong totalPhys, ulong availPhys)
        {
            if (totalPhys == 0 || availPhys > totalPhys) return false;
            return availPhys < FloorBytes && availPhys < totalPhys / 8;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        private static bool UnderPressure()
        {
            long now = DateTime.UtcNow.Ticks;
            lock (lk)
            {
                if (now - sampledTicks < SampleIntervalTicks) return pressureCached;
                sampledTicks = now;
                bool pressure = false;
                try
                {
                    var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                    if (GlobalMemoryStatusEx(ref status))
                        pressure = ShouldTrim(status.TotalPhys, status.AvailPhys);
                }
                catch { }
                pressureCached = pressure;
                return pressure;
            }
        }

        // Trim each process instance once; a working set that grows again after trimming means it really ran again
        //   Memory the user actively used is not cleared again; anti-cheat processes are kept out by the caller
        //   The suppression handle lacks SET_QUOTA; open a separate short-lived handle, skip if it won't open
        public static void MaybeTrim(IntPtr suppressedHandle)
        {
            if (!Enabled || suppressedHandle == IntPtr.Zero) return;
            if (!UnderPressure()) return;
            int pid = GetProcessId(suppressedHandle);
            if (pid <= 4) return;
            long creation, cpu; ulong io;
            if (!Native.QueryProcessSample(suppressedHandle, out creation, out cpu, out io)) return;
            lock (lk)
            {
                long seen;
                if (done.TryGetValue(pid, out seen) && seen == creation) return;
                if (done.Count > 2048) done.Clear();
                done[pid] = creation;
            }
            IntPtr h = OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION,
                false, pid);
            if (h == IntPtr.Zero) return;
            try { EmptyWorkingSet(h); }
            catch { }
            finally { CloseHandle(h); }
        }
    }
}
