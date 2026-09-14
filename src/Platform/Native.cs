// @author bdth 2074055628@qq.com
// File purpose Process and thread handles, privilege elevation and object information
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
    }
}
