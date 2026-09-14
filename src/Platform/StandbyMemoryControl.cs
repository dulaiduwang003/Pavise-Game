// @author bdth 2074055628@qq.com
// File purpose Reads memory lists, the only write is MemoryPurgeStandbyList 80/4
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

    // This raw seam exists so page counts, privilege lifecycle and the exact
    // info class and command combination can be tested without touching host memory
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

    // phnt/ntexapi.h SYSTEM_MEMORY_LIST_INFORMATION, SIZE_T fields must
    // keep pointer width on both x86 and x64, the reuse counts are cumulative, not memory amounts
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
                // Fields up to the eight standby priorities are required
                // Newer OS versions may append fields, this reader never uses them
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
                        // Available already includes standby, subtracting SystemCache too
                        // would wrongly subtract the system working set as well
                        FreeBytes = (memory.ZeroPageCount.ToUInt64() + memory.FreePageCount.ToUInt64()) * pageSize,
                        StandbyBytes = standbyPages * pageSize,
                        // GetPerformanceInfo docs state this is standby plus system working set
                        // Observation only, the system working set is never purged
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
                // Acquiring the privilege may take time, after the coordinator releases us the game may have exited
                // or the option may be off, do not purge in that case
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
                        // An already observed successful purge must be kept, it cannot be undone
                        // and must not be relabeled as cancelled at teardown
                        // The thread's privilege state is now uncertain, this adapter
                        // must not query or modify native memory anymore
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
                // phnt/ntrtl.h flag 0 uses a temporary thread token and keeps the original identity
                // Do not request a process-level adjustment
                var acquired = new PrivilegeLease();
                uint privilege = 13; // SeProfileSingleProcessPrivilege only
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
                // Do not let this seam open a path to MemoryEmptyWorkingSets, flushing modified pages
                // or any other system information command
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
