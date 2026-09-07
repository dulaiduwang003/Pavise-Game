// @author bdth 2074055628@qq.com
// 文件用途 压制后台进程的一次性工作集修剪 页面移入待机列表 物理内存腾给游戏
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

        // 内存不紧时修剪只有代价 被压程序切回时重缺页 游戏那边一页都没多拿到
        //   绝对可用量低于 4 GiB 且低于总量 1/8 才修剪；任一仍充足就不动。
        //   采样 5 秒缓存一次，不给每个进程都查。
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

        // 每个进程实例只修剪一次 修剪后再涨的工作集说明它真的又跑过
        //   用户主动用过的内存不重复清 反作弊进程由调用方挡在外面
        //   压制句柄没有 SET_QUOTA 权限 单独开一个短命句柄 开不了就跳过
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
