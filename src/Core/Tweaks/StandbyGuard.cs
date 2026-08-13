// @author bdth 2074055628@qq.com
// 文件用途 对局中盯着待机列表 堆过阈值且可用内存见底才整个清空 ISLC 的做法
// 只读结构里 Win7 到 Win11 都稳定的前 13 个字段 读不到就整项跳过

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class StandbyGuard
    {
        public const long StandbyThresholdBytes = 1024L * 1024 * 1024;
        public const long AvailThresholdBytes = 1024L * 1024 * 1024;
        public const int CheckIntervalSeconds = 5;

        private const int SystemMemoryListInformation = 0x50;
        private const int MemoryPurgeStandbyList = 4;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const int PriorityLevels = 8;
        private const int StandbyFirstIndex = 5;
        private const int RequiredFields = StandbyFirstIndex + PriorityLevels;

        private static readonly object lk = new object();
        private static volatile bool busy;
        private static bool unavailableLogged;

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile,
                TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemInfo
        {
            public ushort ProcessorArchitecture;
            public ushort Reserved;
            public uint PageSize;
            public IntPtr MinimumApplicationAddress;
            public IntPtr MaximumApplicationAddress;
            public IntPtr ActiveProcessorMask;
            public uint NumberOfProcessors;
            public uint ProcessorType;
            public uint AllocationGranularity;
            public ushort ProcessorLevel;
            public ushort ProcessorRevision;
        }

        [DllImport("kernel32.dll")]
        private static extern void GetSystemInfo(ref SystemInfo info);

        private static int pageSize;

        public static int PageSize
        {
            get
            {
                if (pageSize == 0)
                {
                    var info = new SystemInfo();
                    GetSystemInfo(ref info);
                    pageSize = info.PageSize > 0 ? (int)info.PageSize : 4096;
                }
                return pageSize;
            }
        }

        public static long StandbyBytes()
        {
            int bytes = IntPtr.Size * 64;
            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                int returned;
                int status = NtQuerySystemInformation(
                    SystemMemoryListInformation, buffer, bytes, out returned);
                if (status == StatusInfoLengthMismatch && returned > bytes)
                {
                    Marshal.FreeHGlobal(buffer);
                    bytes = returned;
                    buffer = Marshal.AllocHGlobal(bytes);
                    status = NtQuerySystemInformation(
                        SystemMemoryListInformation, buffer, bytes, out returned);
                }
                if (status != 0) return -1;
                if (returned < RequiredFields * IntPtr.Size) return -1;
                long pages = 0;
                for (int i = 0; i < PriorityLevels; i++)
                {
                    IntPtr slot = Marshal.ReadIntPtr(buffer, (StandbyFirstIndex + i) * IntPtr.Size);
                    long value = IntPtr.Size == 8 ? slot.ToInt64() : (uint)slot.ToInt32();
                    if (value < 0) return -1;
                    pages += value;
                }
                return pages * PageSize;
            }
            catch { return -1; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static long AvailBytes()
        {
            var status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            if (!GlobalMemoryStatusEx(ref status)) return -1;
            return (long)status.AvailPhys;
        }

        internal static bool ShouldPurge(long standbyBytes, long availBytes)
        {
            if (standbyBytes < 0 || availBytes < 0) return false;
            return standbyBytes >= StandbyThresholdBytes && availBytes <= AvailThresholdBytes;
        }

        public static void Tick()
        {
            if (busy) return;
            long standby = StandbyBytes();
            if (standby < 0)
            {
                if (!unavailableLogged)
                {
                    unavailableLogged = true;
                    Logger.Log("待机列表守护 本系统读不出待机列表大小 该项静默跳过");
                }
                return;
            }
            long avail = AvailBytes();
            if (!ShouldPurge(standby, avail)) return;
            lock (lk)
            {
                if (busy) return;
                busy = true;
            }
            var worker = new Thread(delegate () { PurgeAll(standby, avail); });
            worker.IsBackground = true;
            worker.Priority = ThreadPriority.BelowNormal;
            worker.Start();
        }

        private static void PurgeAll(long standbyBefore, long availBefore)
        {
            try
            {
                if (!Native.EnsureProfilePrivilege())
                {
                    Logger.Log("待机列表守护 权限不足 该项静默跳过");
                    return;
                }
                var sw = Stopwatch.StartNew();
                int command = MemoryPurgeStandbyList;
                int status = NtSetSystemInformation(
                    SystemMemoryListInformation, ref command, sizeof(int));
                sw.Stop();
                if (status != 0)
                {
                    Logger.Log("待机列表守护 清空失败 系统返回 0x" + status.ToString("X8"));
                    return;
                }
                long standbyAfter = StandbyBytes();
                Logger.Log("待机列表守护 待机列表 " + Mb(standbyBefore) + "MB 可用内存 "
                    + Mb(availBefore) + "MB 双双越线 已整个清空 用时 " + sw.ElapsedMilliseconds
                    + " 毫秒 清完待机列表 " + (standbyAfter < 0 ? "读不到" : Mb(standbyAfter) + "MB")
                    + " 文件缓存已丢弃 接下来的读盘要重新读");
            }
            catch (Exception ex)
            {
                Logger.Log("待机列表守护 中止 " + ex.Message + " ");
            }
            finally { busy = false; }
        }

        private static long Mb(long bytes) { return bytes / 1048576; }
    }
}
