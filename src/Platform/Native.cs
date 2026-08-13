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
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetThreadPriority(IntPtr thread, int priority);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int GetThreadPriority(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
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

        private static int profilePrivilegeState;

        public static bool EnsureProfilePrivilege()
        {
            int known = Volatile.Read(ref profilePrivilegeState);
            if (known != 0) return known > 0;
            bool enabled = EnablePrivilege("SeProfileSingleProcessPrivilege");
            Interlocked.CompareExchange(ref profilePrivilegeState, enabled ? 1 : -1, 0);
            return Volatile.Read(ref profilePrivilegeState) > 0;
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
        [DllImport("ntdll.dll")]
        public static extern int NtResumeProcess(IntPtr h);
        [DllImport("ntdll.dll")]
        public static extern int NtSuspendProcess(IntPtr h);
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count, out uint required);

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
                return GetProcessDefaultCpuSets(h, ids, (uint)ids.Length, out required) ? ids : null;
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

        public static bool ApplyEcoQoS(IntPtr process)
        {
            return SetPowerThrottling(process, 1, 1);
        }

        public static bool ApplyHighQoS(IntPtr process, bool ignoreTimerResolution)
        {
            return SetPowerThrottling(process, ignoreTimerResolution ? 5u : 1u, 0);
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

        public const int PROCESS_SET_QUOTA = 0x0100;
        public const int PROCESS_SET_INFORMATION = 0x0200;
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
