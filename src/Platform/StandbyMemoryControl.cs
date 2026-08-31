// @author bdth 2074055628@qq.com
// 文件用途 读内存列表 唯一的写操作是 MemoryPurgeStandbyList 80/4
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

    // 留这道原始接缝 是为了让页计数 特权生命周期 以及具体的
    // 信息类和命令号组合都能被测到 不用碰宿主内存
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

    // phnt/ntexapi.h 的 SYSTEM_MEMORY_LIST_INFORMATION SIZE_T 字段在
    // x86 和 x64 上都要保持指针宽度 复用的计数是累计值 不是内存量
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
                // 到八个待机优先级为止的字段是必需的
                // 更新的系统版本可能在后面追加字段 这个读取器一概不用
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
                        // Available 本身就含待机 再减 SystemCache
                        // 会把系统工作集也一起错减掉
                        FreeBytes = (memory.ZeroPageCount.ToUInt64() + memory.FreePageCount.ToUInt64()) * pageSize,
                        StandbyBytes = standbyPages * pageSize,
                        // GetPerformanceInfo 文档里写明这是待机加系统工作集
                        // 这里只是观测 系统工作集不会被清理
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
                // 拿权限可能要花时间 协调器放行之后 游戏已经退出
                // 或者选项已经关掉的话 不要再清
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
                        // 已经观察到的成功清理要保留 它撤不回来
                        // 也不能在收尾时改标成取消
                        // 此刻线程的特权状态已经不确定 这个适配层
                        // 不能再去查询或者改动原生内存
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
                // phnt/ntrtl.h 标志位 0 用的是临时线程令牌 保留原身份
                // 不要申请进程级的调整
                var acquired = new PrivilegeLease();
                uint privilege = 13; // SeProfileSingleProcessPrivilege only.
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
                // 不要从这道接缝里放出通往 MemoryEmptyWorkingSets 刷新修改页
                // 或者别的系统信息命令的路
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
