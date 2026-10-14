// 作者：bdth 2074055628@qq.com
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

        // 内存充足时不裁剪工作集，避免切回应用后重新缺页。
        internal const ulong FloorBytes = 4UL * 1024 * 1024 * 1024;
        internal const long SampleIntervalTicks = TimeSpan.TicksPerSecond * 5;
        private sealed class IdleSample { internal long Creation, Cpu, Since; internal ulong Io; }
        private static readonly Dictionary<int, IdleSample> idle = new Dictionary<int, IdleSample>();
        internal static bool ObserveIdle(int pid, long creation, long cpu, ulong io, long now)
        {
            lock (lk)
            {
                IdleSample previous;
                if (!idle.TryGetValue(pid, out previous) || previous.Creation != creation
                    || previous.Cpu != cpu || previous.Io != io || now < previous.Since)
                {
                    if (idle.Count >= 2048) idle.Clear();
                    idle[pid] = new IdleSample { Creation = creation, Cpu = cpu, Io = io, Since = now };
                    return false;
                }
                return now - previous.Since >= TimeSpan.TicksPerMinute;
            }
        }
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

        // 每个进程实例只裁剪一次，重新增长的工作集不再清理。
        public static void MaybeTrim(IntPtr suppressedHandle)
        {
            if (!Enabled || suppressedHandle == IntPtr.Zero) return;
            if (!UnderPressure()) return;
            int pid = GetProcessId(suppressedHandle);
            if (pid <= 4) return;
            long creation, cpu; ulong io;
            if (!Native.QueryProcessSample(suppressedHandle, out creation, out cpu, out io)) return;
            if (AutomaticPolicy.Enabled && !ObserveIdle(pid, creation, cpu, io, DateTime.UtcNow.Ticks)) return;
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
