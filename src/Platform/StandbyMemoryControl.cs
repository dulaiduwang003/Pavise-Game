// 作者：bdth 2074055628@qq.com
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class StandbyMemorySnapshot
    {
        public ulong TotalBytes, AvailableBytes, FreeBytes, StandbyBytes, ListBytes;
    }

    internal interface IStandbyMemoryControl
    {
        bool TryQuery(out StandbyMemorySnapshot snapshot);
        bool TryPurge(Func<bool> mayContinue, out int nativeStatus);
    }

    internal interface IStandbyMemoryNative
    {
        bool TryGetPerformanceInfo(out StandbyPerformanceInformation information);
        int QueryMemoryList(out StandbyMemoryListInformation information, out int returnedBytes);
        bool TryAcquirePurgePrivilege(out IDisposable lease);
        int SetSystemInformation(int informationClass, ref int command, int length);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StandbyPerformanceInformation
    {
        public uint Size;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak;
        public UIntPtr PhysicalTotal, PhysicalAvailable, SystemCache;
        public UIntPtr KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    // SIZE_T 按指针宽度声明；累计复用次数不是内存容量。
    [StructLayout(LayoutKind.Sequential)]
    internal struct StandbyMemoryListInformation
    {
        public UIntPtr ZeroPageCount, FreePageCount, ModifiedPageCount;
        public UIntPtr ModifiedNoWritePageCount, BadPageCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public UIntPtr[] PageCountByPriority;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public UIntPtr[] RepurposedPagesByPriority;
        public UIntPtr ModifiedPageCountPageFile;
    }

    internal sealed class StandbyMemoryControl : IStandbyMemoryControl
    {
        internal const int MemoryListInformationClass = 0x50;
        internal const int PurgeStandbyCommand = 4;
        private const int StatusPrivilegeNotHeld = unchecked((int)0xC0000061);
        private readonly IStandbyMemoryNative native;
        private volatile bool unhealthy;

        public StandbyMemoryControl() : this(new WindowsStandbyMemoryNative()) { }

        internal StandbyMemoryControl(IStandbyMemoryNative native)
        {
            if (native == null) throw new ArgumentNullException("native");
            this.native = native;
        }

        public bool TryQuery(out StandbyMemorySnapshot snapshot)
        {
            snapshot = null;
            if (unhealthy) return false;
            try
            {
                StandbyPerformanceInformation performance;
                if (!native.TryGetPerformanceInfo(out performance)) return false;
                if (performance.Size != (uint)Marshal.SizeOf(typeof(StandbyPerformanceInformation)))
                    return false;
                ulong pageSize = performance.PageSize.ToUInt64();
                if (pageSize == 0 || (pageSize & (pageSize - 1)) != 0) return false;

                StandbyMemoryListInformation memory;
                int returnedBytes;
                int status = native.QueryMemoryList(out memory, out returnedBytes);
                if (status < 0 || returnedBytes < 13 * IntPtr.Size
                    || returnedBytes > Marshal.SizeOf(typeof(StandbyMemoryListInformation))
                    || memory.PageCountByPriority == null || memory.PageCountByPriority.Length != 8)
                    return false;

                ulong standbyPages = 0;
                checked
                {
                    foreach (UIntPtr count in memory.PageCountByPriority)
                        standbyPages += count.ToUInt64();
                    snapshot = new StandbyMemorySnapshot
                    {
                        TotalBytes = performance.PhysicalTotal.ToUInt64() * pageSize,
                        AvailableBytes = performance.PhysicalAvailable.ToUInt64() * pageSize,
                        // 可用内存已含待机页，不能再扣除系统缓存。
                        FreeBytes = (memory.ZeroPageCount.ToUInt64() + memory.FreePageCount.ToUInt64()) * pageSize,
                        StandbyBytes = standbyPages * pageSize,
                        ListBytes = performance.SystemCache.ToUInt64() * pageSize
                    };
                }
                if (StandbyCleanerEngine.ValidSnapshot(snapshot)) return true;
            }
            catch { }
            snapshot = null;
            return false;
        }

        public bool TryPurge(Func<bool> mayContinue, out int nativeStatus)
        {
            if (unhealthy)
            {
                nativeStatus = StandbyCleanerEngine.StatusUnsuccessful;
                return false;
            }
            nativeStatus = StandbyCleanerEngine.StatusCancelled;
            if (!StandbyCleanerEngine.MayContinue(mayContinue)) return false;
            IDisposable lease = null;
            bool purged = false;
            try
            {
                nativeStatus = StatusPrivilegeNotHeld;
                if (!native.TryAcquirePurgePrivilege(out lease) || lease == null) return false;
                // 清理权限获取后重新确认游戏和开关状态。
                if (!StandbyCleanerEngine.MayContinue(mayContinue))
                {
                    nativeStatus = StandbyCleanerEngine.StatusCancelled;
                    return false;
                }
                int command = PurgeStandbyCommand;
                nativeStatus = native.SetSystemInformation(MemoryListInformationClass, ref command, sizeof(int));
                purged = nativeStatus == 0;
            }
            catch
            {
                nativeStatus = StandbyCleanerEngine.StatusUnsuccessful;
            }
            finally
            {
                if (lease != null)
                {
                    try { lease.Dispose(); }
                    catch
                    {
                        // 已成功清理的结果不能在取消时改写。
                        unhealthy = true;
                        nativeStatus = StandbyCleanerEngine.StatusUnsuccessful;
                    }
                }
            }
            return purged;
        }

        private sealed class WindowsStandbyMemoryNative : IStandbyMemoryNative
        {
            public bool TryGetPerformanceInfo(out StandbyPerformanceInformation information)
            {
                RefuseNativeInTests();
                information = new StandbyPerformanceInformation();
                information.Size = (uint)Marshal.SizeOf(typeof(StandbyPerformanceInformation));
                return GetPerformanceInfo(ref information, information.Size);
            }

            public int QueryMemoryList(out StandbyMemoryListInformation information, out int returnedBytes)
            {
                RefuseNativeInTests();
                return NtQuerySystemInformation(MemoryListInformationClass, out information,
                    Marshal.SizeOf(typeof(StandbyMemoryListInformation)), out returnedBytes);
            }

            public bool TryAcquirePurgePrivilege(out IDisposable lease)
            {
                RefuseNativeInTests();
                lease = null;
                // 权限只临时作用于当前线程。
                var acquired = new PrivilegeLease();
                uint privilege = 13;
                IntPtr state;
                int status = RtlAcquirePrivilege(ref privilege, 1, 0, out state);
                if (status != 0 || state == IntPtr.Zero) return false;
                acquired.State = state;
                lease = acquired;
                return true;
            }

            public int SetSystemInformation(int informationClass, ref int command, int length)
            {
                RefuseNativeInTests();
                // 此接口不允许清空工作集或刷新修改页。
                if (informationClass != MemoryListInformationClass || command != PurgeStandbyCommand
                    || length != sizeof(int)) return StandbyCleanerEngine.StatusInvalidParameter;
                return NtSetSystemInformation(informationClass, ref command, length);
            }

            private sealed class PrivilegeLease : IDisposable
            {
                internal IntPtr State;
                public void Dispose()
                {
                    RefuseNativeInTests();
                    IntPtr state = State;
                    State = IntPtr.Zero;
                    if (state != IntPtr.Zero) RtlReleasePrivilege(state);
                }
            }

            private static void RefuseNativeInTests()
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                throw new InvalidOperationException("Standby memory native access requires an injected test double.");
#endif
            }

            [DllImport("psapi.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetPerformanceInfo(ref StandbyPerformanceInformation information, uint size);
            [DllImport("ntdll.dll", ExactSpelling = true)]
            private static extern int NtQuerySystemInformation(int informationClass,
                out StandbyMemoryListInformation information, int length, out int returnedBytes);
            [DllImport("ntdll.dll", ExactSpelling = true)]
            private static extern int NtSetSystemInformation(int informationClass, ref int command, int length);
            [DllImport("ntdll.dll", ExactSpelling = true)]
            private static extern int RtlAcquirePrivilege(ref uint privileges, uint count, uint flags, out IntPtr state);
            [DllImport("ntdll.dll", ExactSpelling = true)]
            private static extern void RtlReleasePrivilege(IntPtr state);
        }
    }
}
