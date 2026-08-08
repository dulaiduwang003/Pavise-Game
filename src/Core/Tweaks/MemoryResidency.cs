// @author bdth 2074055628@qq.com
// 文件用途 内存紧张时给游戏进程设硬性最小工作集 防对局中被自动换出 台架实测换出回读代价175倍

using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class MemoryResidency
    {
        public const double TightFraction = 0.18;
        public const long TightFloorBytes = 3L * 1024 * 1024 * 1024;
        public const long MaxPinBytes = 2L * 1024 * 1024 * 1024;
        public const long SlackBytes = 256L * 1024 * 1024;
        private const uint QuotaMinEnable = 0x1;
        private const uint QuotaMinDisable = 0x2;
        private const uint QuotaMaxDisable = 0x8;
        private const int ProcessSetQuota = 0x0100;

        private static readonly object lk = new object();
        private static int pinnedPid;
        private static long pinnedCreation;
        private static long pinnedBytes;

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

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSizeEx(IntPtr process,
            IntPtr min, IntPtr max, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessWorkingSetSizeEx(IntPtr process,
            out IntPtr min, out IntPtr max, out uint flags);

        [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo")]
        private static extern bool GetProcessMemoryInfoPsapi(IntPtr process,
            out MemCounters counters, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemCounters
        {
            public int cb;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
        }

        public static long AvailPhysBytes()
        {
            var status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            return GlobalMemoryStatusEx(ref status) ? (long)status.AvailPhys : 0;
        }

        public static bool MemoryTight()
        {
            var status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return false;
            return IsTight((long)status.AvailPhys, (long)status.TotalPhys);
        }

        internal static bool IsTight(long avail, long total)
        {
            if (total <= 0 || avail < 0) return false;
            long threshold = Math.Max(TightFloorBytes, (long)(total * TightFraction));
            return avail < threshold;
        }

        internal static long PinTarget(long workingSet, long avail)
        {
            if (workingSet <= 0) return 0;
            long target = workingSet + SlackBytes;
            if (target > MaxPinBytes) target = MaxPinBytes;
            if (avail > 0 && target > workingSet + avail / 2) target = workingSet + avail / 2;
            return target;
        }

        public static void Tick(int pid, long creation)
        {
            lock (lk)
            {
                if (pid <= 0) { ReleaseLocked(); return; }
                if (pinnedPid == pid && pinnedCreation == creation) return;
                if (pinnedPid != 0) ReleaseLocked();
                if (!MemoryTight()) return;

                IntPtr h = Native.OpenProcess(
                    ProcessSetQuota | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return;
                try
                {
                    MemCounters counters;
                    if (!GetProcessMemoryInfoPsapi(h, out counters, Marshal.SizeOf(typeof(MemCounters))))
                        return;
                    var status = new MemoryStatusEx();
                    status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                    GlobalMemoryStatusEx(ref status);
                    long target = PinTarget((long)counters.WorkingSetSize, (long)status.AvailPhys);
                    if (target <= 0) return;
                    if (!SetProcessWorkingSetSizeEx(h, (IntPtr)target, (IntPtr)(target * 2),
                        QuotaMinEnable | QuotaMaxDisable)) return;
                    pinnedPid = pid;
                    pinnedCreation = creation;
                    pinnedBytes = target;
                    Logger.Log("内存驻留：可用内存紧张，已给游戏进程 pid " + pid
                        + " 锁定最小工作集 " + (target / 1048576) + "MB，防对局中被换出（退出还原）");
                }
                finally { Native.CloseHandle(h); }
            }
        }

        public static void ReleaseAll()
        {
            lock (lk) ReleaseLocked();
        }

        private static void ReleaseLocked()
        {
            if (pinnedPid == 0) return;
            IntPtr h = Native.OpenProcess(
                ProcessSetQuota | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pinnedPid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    SetProcessWorkingSetSizeEx(h, (IntPtr)(1024 * 1024), (IntPtr)(256L * 1024 * 1024),
                        QuotaMinDisable | QuotaMaxDisable);
                    Logger.Log("内存驻留：已解除游戏进程 pid " + pinnedPid + " 的工作集锁定");
                }
                finally { Native.CloseHandle(h); }
            }
            pinnedPid = 0;
            pinnedCreation = 0;
            pinnedBytes = 0;
        }
    }
}
