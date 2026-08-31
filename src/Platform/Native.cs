// @author bdth 2074055628@qq.com
// 文件用途 封装项目使用的 Windows 原生接口
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(int access, bool inherit, int tid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true,
            SetLastError = true)]
        private static extern IntPtr GetProcAddress(
            IntPtr module, string procedureName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessIdOfThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetThreadPriority(IntPtr thread, int priority);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int GetThreadPriority(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")]
        public static extern int GetCurrentThreadId();
        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(
            uint processId, out uint sessionId);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(
            IntPtr handle, uint milliseconds);

        private const uint WaitTimeout = 258;

        public static bool TryGetLiveProcessSessionId(
            IntPtr processHandle, int pid, out int sessionId)
        {
            sessionId = -1;
            if (processHandle == IntPtr.Zero || pid <= 0) return false;
            uint value;
            if (!ProcessIdToSessionId((uint)pid, out value)
                || value > int.MaxValue)
                return false;
            if (WaitForSingleObject(processHandle, 0) != WaitTimeout)
                return false;
            sessionId = (int)value;
            return true;
        }

        public static bool LastOpenProcessFailureWasNoSuchProcess()
        {
            return Marshal.GetLastWin32Error() == 87;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetPriorityClass(IntPtr h, uint cls);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetPriorityClass(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessPriorityBoost(IntPtr h, out bool disabled);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessPriorityBoost(IntPtr h, bool disable);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string systemName, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges privileges,
            int bufferLength, IntPtr previous, IntPtr returnLength);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder buf, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PowerThrottlingState info, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessInformation(IntPtr h, int infoClass, ref PowerThrottlingState info, int size);
        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationProcess(IntPtr h, int infoClass, ref int info, int len);
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, ref int info, int len, IntPtr retLen);
        [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
        private static extern int NtQueryInformationProcessBasic(IntPtr h, int infoClass,
            ref PROCESS_BASIC_INFORMATION info, int len, IntPtr retLen);
        [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
        private static extern int NtQueryInformationProcessPower(IntPtr h, int infoClass,
            ref PowerThrottlingState info, int len, IntPtr retLen);

        [StructLayout(LayoutKind.Sequential)]
        private struct PublicObjectBasicInformation
        {
            public uint Attributes;
            public uint GrantedAccess;
            public uint HandleCount;
            public uint PointerCount;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryObject(IntPtr handle, int infoClass,
            IntPtr buffer, int length, out int returned);

        private const int ObjectBasicInformation = 0;
        private const int PublicObjectBasicInformationSize = 14 * 4;

        public static bool TryQueryGrantedAccess(IntPtr handle, out uint granted)
        {
            granted = 0;
            if (handle == IntPtr.Zero) return false;
            IntPtr mem = Marshal.AllocHGlobal(PublicObjectBasicInformationSize);
            try
            {
                for (int i = 0; i < PublicObjectBasicInformationSize; i += 4) Marshal.WriteInt32(mem, i, 0);
                int returned;
                if (NtQueryObject(handle, ObjectBasicInformation, mem,
                        PublicObjectBasicInformationSize, out returned) != 0) return false;
                var info = (PublicObjectBasicInformation)Marshal.PtrToStructure(
                    mem, typeof(PublicObjectBasicInformation));
                granted = info.GrantedAccess;
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(mem); }
        }

        public static bool HandleWriteAccessStripped(IntPtr handle, out uint granted)
        {
            if (!TryQueryGrantedAccess(handle, out granted)) return false;
            return (granted & PROCESS_SET_INFORMATION) == 0;
        }

        private static int boostPrivilegeState;

        public static bool EnsureBoostPrivilege()
        {
            int known = Volatile.Read(ref boostPrivilegeState);
            if (known != 0) return known > 0;
            bool enabled = EnablePrivilege("SeIncreaseBasePriorityPrivilege");
            Interlocked.CompareExchange(ref boostPrivilegeState, enabled ? 1 : -1, 0);
            return Volatile.Read(ref boostPrivilegeState) > 0;
        }

        private static int debugPrivilegeState;

        public static bool TryEnableDebugPrivilege()
        {
            int known = Volatile.Read(ref debugPrivilegeState);
            if (known != 0) return known > 0;
            bool enabled = EnablePrivilege("SeDebugPrivilege");
            Interlocked.CompareExchange(ref debugPrivilegeState, enabled ? 1 : -1, 0);
            return Volatile.Read(ref debugPrivilegeState) > 0;
        }

        private static bool EnablePrivilege(string name)
        {
            const uint TokenAdjustPrivileges = 0x20;
            const uint TokenQuery = 0x8;
            const uint PrivilegeEnabled = 0x2;
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token)) return false;
            try
            {
                Luid luid;
                if (!LookupPrivilegeValue(null, name, out luid)) return false;
                var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = PrivilegeEnabled };
                if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                return Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr procMask, out UIntPtr sysMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters counters);

        public static bool QueryProcessSample(IntPtr h, out long creation, out long cpu, out ulong io)
        {
            creation = cpu = 0; io = 0;
            long exit, kernel, user;
            if (!GetProcessTimes(h, out creation, out exit, out kernel, out user)) return false;
            cpu = kernel + user;
            IoCounters c;
            if (GetProcessIoCounters(h, out c)) io = c.ReadTransferCount + c.WriteTransferCount;
            return true;
        }

        public static ulong QueryAffinity(IntPtr h)
        {
            UIntPtr pm, sm;
            return GetProcessAffinityMask(h, out pm, out sm) ? (ulong)pm : 0UL;
        }
        public static int QueryIoPriority(IntPtr h)
        {
            int v = 0;
            return NtQueryInformationProcess(h, ProcessIoPriorityNt, ref v, 4, IntPtr.Zero) == 0 ? v : -1;
        }
        public static int QueryPagePriority(IntPtr h)
        {
            int v = 0;
            return NtQueryInformationProcess(h, ProcessPagePriorityNt, ref v, 4, IntPtr.Zero) == 0 ? v : -1;
        }

        public static bool TrySetIoPriority(IntPtr process, int priority, out int status)
        {
            status = NtSetInformationProcess(process, ProcessIoPriorityNt, ref priority, sizeof(int));
            return status == 0;
        }

        public static bool TrySetIoPriority(IntPtr process, int priority)
        {
            int status;
            return TrySetIoPriority(process, priority, out status);
        }

        public static bool TrySetPagePriority(IntPtr process, int priority)
        {
            return NtSetInformationProcess(process, ProcessPagePriorityNt, ref priority, sizeof(int)) == 0;
        }

        public static string ImagePath(IntPtr h)
        {
            try
            {
                int cap = 600;
                var sb = new System.Text.StringBuilder(cap);
                if (!QueryFullProcessImageName(h, 0, sb, ref cap))
                {
                    cap = 32768;
                    sb = new System.Text.StringBuilder(cap);
                    if (!QueryFullProcessImageName(h, 0, sb, ref cap)) return null;
                }
                string path = sb.ToString();
                return path.Length == 0 ? null : path;
            }
            catch { return null; }
        }

        public static bool StillActive(IntPtr h)
        {
            uint code;
            if (!GetExitCodeProcess(h, out code)) return true;
            return code == 259;
        }

        public static string ImageName(IntPtr h)
        {
            string path = ImagePath(h);
            if (path == null) return null;
            int slash = path.LastIndexOf('\\');
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file.Substring(0, file.Length - 4);
            return file;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        public static int ParentProcessId(IntPtr processHandle)
        {
            try
            {
                var info = new PROCESS_BASIC_INFORMATION();
                int size = Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION));
                return NtQueryInformationProcessBasic(processHandle, 0, ref info, size, IntPtr.Zero) == 0
                    ? info.InheritedFromUniqueProcessId.ToInt32() : 0;
            }
            catch { return 0; }
        }

        [DllImport("gdi32.dll")] public static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr h, out int cls);
        [DllImport("gdi32.dll")] public static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr h, int cls);

        // WDDM 显存查询与预留 全部走 gdi32 的用户态 D3DKMT 接口 不装驱动 不注入 不读游戏内存
        //   MemorySegmentGroup 0 是本地显存 1 是非本地(系统内存那份) 护盾只关心 0
        //   注意 Query 的 hProcess 是 HANDLE 而 Change 的 hProcess 在头文件里是 UINT64
        //     x64 上宽度一样 但类型照抄头文件 免得将来有人按 HANDLE 去改 Change 那个
        public const uint D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtLuid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtOpenAdapterFromLuid
        {
            public D3dkmtLuid AdapterLuid;
            public uint hAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtCloseAdapter
        {
            public uint hAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtQueryVideoMemoryInfo
        {
            public IntPtr hProcess;
            public uint hAdapter;
            public uint MemorySegmentGroup;
            public ulong Budget;
            public ulong CurrentUsage;
            public ulong CurrentReservation;
            public ulong AvailableForReservation;
            public uint PhysicalAdapterIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtChangeVideoMemoryReservation
        {
            public ulong hProcess;
            public uint hAdapter;
            public uint MemorySegmentGroup;
            public ulong Reservation;
            public uint PhysicalAdapterIndex;
        }

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTOpenAdapterFromLuid(ref D3dkmtOpenAdapterFromLuid p);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter p);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTQueryVideoMemoryInfo(ref D3dkmtQueryVideoMemoryInfo p);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTChangeVideoMemoryReservation(ref D3dkmtChangeVideoMemoryReservation p);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count, out uint required);

        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity
        {
            public UIntPtr Mask;
            public ushort Group;
            public ushort Reserved0;
            public ushort Reserved1;
            public ushort Reserved2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadGroupAffinity(
            IntPtr thread, out GroupAffinity affinity);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadSelectedCpuSets(
            IntPtr thread, uint[] ids, uint count, out uint required);

        internal enum CpuSetMaskQueryResult
        {
            Failed = -1,
            ApiUnavailable = 0,
            Success = 1
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
        private delegate bool CpuSetMaskGetter(
            IntPtr handle, IntPtr masks, ushort count, out ushort required);

        private static readonly object cpuSetMaskApiSync = new object();
        private static volatile bool cpuSetMaskApisResolved;
        // -1 表示连 kernel32 都无法解析 0 才表示老系统确实没有该导出
        private static int processCpuSetMaskApiState = -1;
        private static int threadCpuSetMaskApiState = -1;
        private static CpuSetMaskGetter getProcessDefaultCpuSetMasks;
        private static CpuSetMaskGetter getThreadSelectedCpuSetMasks;

        private static void ResolveCpuSetMaskApis()
        {
            if (cpuSetMaskApisResolved) return;
            lock (cpuSetMaskApiSync)
            {
                if (cpuSetMaskApisResolved) return;
                try
                {
                    IntPtr module = GetModuleHandle("kernel32.dll");
                    if (module != IntPtr.Zero)
                    {
                        IntPtr process = GetProcAddress(
                            module, "GetProcessDefaultCpuSetMasks");
                        IntPtr thread = GetProcAddress(
                            module, "GetThreadSelectedCpuSetMasks");
                        processCpuSetMaskApiState = process == IntPtr.Zero ? 0 : 1;
                        threadCpuSetMaskApiState = thread == IntPtr.Zero ? 0 : 1;
                        if (process != IntPtr.Zero)
                            getProcessDefaultCpuSetMasks =
                                (CpuSetMaskGetter)Marshal.GetDelegateForFunctionPointer(
                                    process, typeof(CpuSetMaskGetter));
                        if (thread != IntPtr.Zero)
                            getThreadSelectedCpuSetMasks =
                                (CpuSetMaskGetter)Marshal.GetDelegateForFunctionPointer(
                                    thread, typeof(CpuSetMaskGetter));
                    }
                }
                catch
                {
                    processCpuSetMaskApiState = -1;
                    threadCpuSetMaskApiState = -1;
                    getProcessDefaultCpuSetMasks = null;
                    getThreadSelectedCpuSetMasks = null;
                }
                cpuSetMaskApisResolved = true;
            }
        }

        private static CpuSetMaskQueryResult QueryCpuSetMasks(
            IntPtr handle, int apiState, CpuSetMaskGetter getter,
            out bool assigned, out ulong mask)
        {
            assigned = false;
            mask = 0;
            if (apiState == 0) return CpuSetMaskQueryResult.ApiUnavailable;
            if (apiState != 1 || getter == null || handle == IntPtr.Zero)
                return CpuSetMaskQueryResult.Failed;
            try
            {
                ushort required;
                bool first = getter(handle, IntPtr.Zero, 0, out required);
                if (required == 0)
                    return first ? CpuSetMaskQueryResult.Success
                        : CpuSetMaskQueryResult.Failed;
                int recordSize = Marshal.SizeOf(typeof(GroupAffinity));
                IntPtr buffer = Marshal.AllocHGlobal(recordSize * required);
                try
                {
                    ushort actual;
                    if (!getter(handle, buffer, required, out actual)
                        || actual != required)
                        return CpuSetMaskQueryResult.Failed;
                    for (int i = 0; i < required; i++)
                    {
                        IntPtr record = (IntPtr)((long)buffer
                            + (long)i * recordSize);
                        var affinity = (GroupAffinity)Marshal.PtrToStructure(
                            record, typeof(GroupAffinity));
                        if (affinity.Group != 0) return CpuSetMaskQueryResult.Failed;
                        ulong bitMask = affinity.Mask.ToUInt64();
                        if (bitMask == 0) return CpuSetMaskQueryResult.Failed;
                        mask |= bitMask;
                    }
                    assigned = true;
                    return mask != 0 ? CpuSetMaskQueryResult.Success
                        : CpuSetMaskQueryResult.Failed;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch { return CpuSetMaskQueryResult.Failed; }
        }

        public static CpuSetMaskQueryResult QueryProcessDefaultCpuSetMasks(
            IntPtr process, out bool assigned, out ulong mask)
        {
            ResolveCpuSetMaskApis();
            return QueryCpuSetMasks(
                process, processCpuSetMaskApiState,
                getProcessDefaultCpuSetMasks, out assigned, out mask);
        }

        public static CpuSetMaskQueryResult QueryThreadSelectedCpuSetMasks(
            IntPtr thread, out bool assigned, out ulong mask)
        {
            ResolveCpuSetMaskApis();
            return QueryCpuSetMasks(
                thread, threadCpuSetMaskApiState,
                getThreadSelectedCpuSetMasks, out assigned, out mask);
        }

        public static bool TrySetCpuSets(IntPtr h, uint[] ids)
        {
            if (ids == null || ids.Length == 0) return false;
            try { return SetProcessDefaultCpuSets(h, ids, (uint)ids.Length); }
            catch { return false; }
        }

        public static bool TryClearCpuSets(IntPtr h)
        {
            try { return SetProcessDefaultCpuSets(h, null, 0); }
            catch { return false; }
        }

        public static uint[] QueryCpuSets(IntPtr h)
        {
            try
            {
                uint required;
                bool first = GetProcessDefaultCpuSets(h, null, 0, out required);
                if (required == 0) return first ? new uint[0] : null;
                var ids = new uint[required];
                uint actual;
                return GetProcessDefaultCpuSets(
                        h, ids, (uint)ids.Length, out actual)
                    && actual == required ? ids : null;
            }
            catch { return null; }
        }

        public static int QueryThreadOwnerPid(IntPtr thread)
        {
            if (thread == IntPtr.Zero) return -1;
            try
            {
                uint pid = GetProcessIdOfThread(thread);
                return pid != 0 && pid <= int.MaxValue ? (int)pid : -1;
            }
            catch { return -1; }
        }

        public static bool TryQueryThreadActive(
            IntPtr thread, out bool active)
        {
            active = false;
            if (thread == IntPtr.Zero) return false;
            try
            {
                // GetExitCodeThread 的 259 也可能是线程真实退出码 带
                // SYNCHRONIZE 的句柄用零超时 wait 才能无歧义区分存活
                uint wait = WaitForSingleObject(thread, 0);
                if (wait == WaitTimeout) { active = true; return true; }
                if (wait == 0) return true;
                return false;
            }
            catch { return false; }
        }

        public static bool TryQueryThreadGroupAffinity(
            IntPtr thread, out ushort group, out ulong mask)
        {
            group = 0;
            mask = 0;
            if (thread == IntPtr.Zero) return false;
            try
            {
                GroupAffinity affinity;
                if (!GetThreadGroupAffinity(thread, out affinity)) return false;
                group = affinity.Group;
                mask = affinity.Mask.ToUInt64();
                return mask != 0;
            }
            catch { return false; }
        }

        // null 表示查询失败 空数组表示线程没有显式 CPU Set 分配
        // 两次调用之间数量发生变化也视为未知 归因链路必须 fail-closed
        public static uint[] QueryThreadSelectedCpuSets(IntPtr thread)
        {
            if (thread == IntPtr.Zero) return null;
            try
            {
                uint required;
                bool first = GetThreadSelectedCpuSets(
                    thread, null, 0, out required);
                if (required == 0) return first ? new uint[0] : null;
                var ids = new uint[required];
                uint actual;
                if (!GetThreadSelectedCpuSets(
                        thread, ids, (uint)ids.Length, out actual)
                    || actual != required)
                    return null;
                return ids;
            }
            catch { return null; }
        }

        public static bool RestoreCpuSets(IntPtr h, uint[] ids)
        {
            return ids != null && (ids.Length == 0 ? TryClearCpuSets(h) : TrySetCpuSets(h, ids));
        }

        public static bool RestoreCpuSetsVerified(IntPtr h, uint[] ids)
        {
            return RestoreCpuSets(h, ids) && CpuSetsMatch(h, ids);
        }

        public static bool TrySetCpuSetsVerified(IntPtr h, uint[] ids)
        {
            return TrySetCpuSets(h, ids) && CpuSetsMatch(h, ids);
        }

        public static bool CpuSetsMatch(IntPtr h, uint[] expected)
        {
            if (expected == null) return false;
            uint[] actual = QueryCpuSets(h);
            if (actual == null || actual.Length != expected.Length) return false;
            var set = new System.Collections.Generic.HashSet<uint>(actual);
            foreach (uint id in expected) if (!set.Contains(id)) return false;
            return true;
        }

        public static bool TryClearCpuSets(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_SET_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return TryClearCpuSets(h); }
            finally { CloseHandle(h); }
        }

        private const uint QosSealMask = 5;
        private static volatile bool timerSealUnavailable;

        public static uint EcoQoSWantMask(bool sealTimer)
        {
            return sealTimer && !timerSealUnavailable ? QosSealMask : 1u;
        }

        public static bool ApplyEcoQoS(IntPtr process)
        {
            return ApplyEcoQoS(process, false);
        }

        public static bool ApplyEcoQoS(IntPtr process, bool sealTimer)
        {
            if (sealTimer && !timerSealUnavailable)
            {
                if (SetPowerThrottling(process, QosSealMask, QosSealMask)) return true;
                if (Marshal.GetLastWin32Error() != 87) return false;
                timerSealUnavailable = true;
            }
            return SetPowerThrottling(process, 1, 1);
        }

        public static int QueryBoostDisabled(IntPtr h)
        {
            bool disabled;
            return GetProcessPriorityBoost(h, out disabled) ? (disabled ? 1 : 0) : -1;
        }

        public static bool TrySetBoostDisabled(IntPtr h, bool disable)
        {
            return SetProcessPriorityBoost(h, disable);
        }

        private const uint TimerExemptBit = 4;
        private static volatile bool timerExemptUnavailable;

        public static bool TimerExemptWanted
        {
            get { return OsBuild() >= 22000 && !timerExemptUnavailable; }
        }

        public static bool TimerExemptOk(int controlMask, int stateMask)
        {
            return ((uint)controlMask & TimerExemptBit) != 0
                && ((uint)stateMask & TimerExemptBit) == 0;
        }

        public static bool HighQoSMasksOk(int controlMask, int stateMask)
        {
            if (!EcoClearedMasksOk(controlMask, stateMask)) return false;
            return !TimerExemptWanted || TimerExemptOk(controlMask, stateMask);
        }

        public static bool EcoClearedMasksOk(int controlMask, int stateMask)
        {
            return (controlMask & 1) != 0 && (stateMask & 1) == 0;
        }

        public static bool ApplyHighQoS(IntPtr process, bool ignoreTimerResolution)
        {
            bool dropped;
            return ApplyHighQoS(process, ignoreTimerResolution, out dropped);
        }

        public static bool ApplyHighQoS(IntPtr process, bool ignoreTimerResolution, out bool timerExemptDropped)
        {
            timerExemptDropped = false;
            if (ignoreTimerResolution && !timerExemptUnavailable)
            {
                if (SetPowerThrottling(process, QosSealMask, 0))
                {
                    int control, state;
                    if (!TryQueryPowerThrottling(process, out control, out state)) return true;
                    if (TimerExemptOk(control, state)) return true;
                }
                else if (Marshal.GetLastWin32Error() != 87) return false;
                timerExemptUnavailable = true;
                timerExemptDropped = true;
            }
            return SetPowerThrottling(process, 1, 0);
        }

        public static bool RestorePowerThrottling(IntPtr process, int controlMask, int stateMask)
        {
            if (controlMask < 0) return SetPowerThrottling(process, 0, 0);
            return SetPowerThrottling(process, (uint)controlMask, (uint)(stateMask < 0 ? 0 : stateMask));
        }

        public static bool TryQueryPowerThrottling(IntPtr process, out int controlMask, out int stateMask)
        {
            int size = Marshal.SizeOf(typeof(PowerThrottlingState));
            var state = new PowerThrottlingState { Version = 1 };
            bool ok = GetProcessInformation(process, ProcessPowerThrottling, ref state, size);
            if (!ok)
            {
                state = new PowerThrottlingState { Version = 1 };
                ok = NtQueryInformationProcessPower(process, ProcessPowerThrottlingNt, ref state, size, IntPtr.Zero) == 0;
            }
            controlMask = (int)state.ControlMask;
            stateMask = (int)state.StateMask;
            return ok;
        }

        public static readonly bool PowerThrottlingSupported = ProbePowerThrottling();

        private static bool ProbePowerThrottling()
        {
            try
            {
                int control, state;
                return TryQueryPowerThrottling((IntPtr)(-1), out control, out state);
            }
            catch { return false; }
        }

        private static bool SetPowerThrottling(IntPtr process, uint controlMask, uint stateMask)
        {
            var state = new PowerThrottlingState
            {
                Version = 1,
                ControlMask = controlMask,
                StateMask = stateMask
            };
            return SetProcessInformation(process, ProcessPowerThrottling, ref state,
                Marshal.SizeOf(typeof(PowerThrottlingState)));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            public int Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string CSD;
        }
        [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OsVersionInfo v);

        private static int osBuild = -1;
        public static int OsBuild()
        {
            if (osBuild < 0)
            {
                try
                {
                    var v = new OsVersionInfo();
                    v.Size = Marshal.SizeOf(typeof(OsVersionInfo));
                    osBuild = RtlGetVersion(ref v) == 0 ? v.Build : 0;
                }
                catch { osBuild = 0; }
            }
            return osBuild;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatusNative
        {
            public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatusNative status);

        [DllImport("netapi32.dll")] private static extern int NetGetJoinInformation(string server, out IntPtr name, out int status);
        [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);

        private static int domainJoined = -1;
        public static bool IsDomainJoined()
        {
            if (domainJoined < 0)
            {
                try
                {
                    IntPtr name; int status;
                    if (NetGetJoinInformation(null, out name, out status) != 0) domainJoined = 0;
                    else
                    {
                        if (name != IntPtr.Zero) NetApiBufferFree(name);
                        domainJoined = status == 3 ? 1 : 0;
                    }
                }
                catch { domainJoined = 0; }
            }
            return domainJoined == 1;
        }

        private static int hasBattery = -1;
        // 当前是不是交流供电 读不到就当插着电 别在判断不了的时候擅自按电池处理
        //   AcLineStatus 0 电池 1 交流 255 未知
        public static bool OnAcPower()
        {
            try
            {
                SystemPowerStatusNative s;
                if (!GetSystemPowerStatus(out s)) return true;
                return s.AcLineStatus != 0;
            }
            catch { return true; }
        }

        public static bool HasSystemBattery()
        {
            if (hasBattery < 0)
            {
                try
                {
                    SystemPowerStatusNative s;
                    hasBattery = GetSystemPowerStatus(out s)
                        && s.BatteryFlag != 128 && s.BatteryFlag != 255
                        && s.AcLineStatus != 255 ? 1 : 0;
                }
                catch { hasBattery = 0; }
            }
            return hasBattery == 1;
        }

        public static bool IsElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public const int PROCESS_SET_QUOTA = 0x0100;
        public const int PROCESS_SET_INFORMATION = 0x0200;
        // D3DKMT 的显存查询与预留不吃 QUERY_LIMITED 本机实测一律 STATUS_ACCESS_DENIED
        //   QueryVideoMemoryInfo 要 QUERY_INFORMATION ChangeVideoMemoryReservation 要 SET_INFORMATION
        //   两者都比 QUERY_LIMITED 更容易被反作弊拒绝 所以护盾的跳过率天然高于提优
        public const int PROCESS_QUERY_INFORMATION = 0x0400;
        public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public const int PROCESS_SET_LIMITED_INFORMATION = 0x2000;
        public const int THREAD_SET_LIMITED_INFORMATION = 0x0400;
        public const int THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
        public const int THREAD_PRIORITY_HIGHEST = 2;
        public const int THREAD_PRIORITY_ERROR_RETURN = 0x7FFFFFFF;
        public const int SYNCHRONIZE = 0x00100000;
        public const int GpuPriorityHigh = 4;
        public const int GpuPriorityIdle = 0;
        public const int GpuPriorityBelowNormal = 1;
        public const int GpuPriorityNormal = 2;
        public const uint IDLE_PRIORITY_CLASS = 0x40;
        public const uint NORMAL_PRIORITY_CLASS = 0x20;
        public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
        public const uint HIGH_PRIORITY_CLASS = 0x80;
        private const int ProcessIoPriorityNt = 33;
        private const int ProcessPagePriorityNt = 39;
        private const int ProcessPowerThrottling = 4;
        private const int ProcessPowerThrottlingNt = 77;
    }

}
