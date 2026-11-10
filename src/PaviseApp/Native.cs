using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static partial class Native
    {

#pragma warning disable 0649
        private struct PublicObjectBasicInformation
        {
            public uint Attributes;

            public uint GrantedAccess;

            public uint HandleCount;

            public uint PointerCount;
        }
#pragma warning restore 0649

        private struct IoCounters
        {
            public ulong ReadOperationCount;

            public ulong WriteOperationCount;

            public ulong OtherOperationCount;

            public ulong ReadTransferCount;

            public ulong WriteTransferCount;

            public ulong OtherTransferCount;
        }

        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;

            public IntPtr PebBaseAddress;

            public IntPtr AffinityMask;

            public IntPtr BasePriority;

            public IntPtr UniqueProcessId;

            public IntPtr InheritedFromUniqueProcessId;
        }

        public const int PROCESS_SET_QUOTA = 256;

        public const int PROCESS_SET_INFORMATION = 512;

        public const int PROCESS_QUERY_INFORMATION = 1024;

        public const int PROCESS_QUERY_LIMITED_INFORMATION = 4096;

        public const int PROCESS_SET_LIMITED_INFORMATION = 8192;

        public const int THREAD_SET_LIMITED_INFORMATION = 1024;

        public const int THREAD_QUERY_LIMITED_INFORMATION = 2048;

        public const int THREAD_PRIORITY_HIGHEST = 2;

        public const int THREAD_PRIORITY_ERROR_RETURN = int.MaxValue;

        public const int SYNCHRONIZE = 1048576;

        public const int GpuPriorityHigh = 4;

        public const int GpuPriorityIdle = 0;

        public const int GpuPriorityBelowNormal = 1;

        public const int GpuPriorityNormal = 2;

        public const uint IDLE_PRIORITY_CLASS = 64u;

        public const uint NORMAL_PRIORITY_CLASS = 32u;

        public const uint BELOW_NORMAL_PRIORITY_CLASS = 16384u;

        public const uint HIGH_PRIORITY_CLASS = 128u;

        public const int WM_DROPFILES = 563;

        public const int WM_NCLBUTTONDOWN = 161;

        public const int HT_CAPTION = 2;

        public const int WM_ENTERSIZEMOVE = 561;

        public const int WM_EXITSIZEMOVE = 562;

        public const uint SPI_GETUIEFFECTS = 4158u;

        public const uint SPI_SETUIEFFECTS = 4159u;

        public const uint SPIF_SENDCHANGE = 2u;

        public const uint WnodeFlagTracedGuid = 131072u;

        public const uint EventTraceRealTimeMode = 256u;

        public const uint ProcessTraceModeRealTime = 256u;

        public const uint ProcessTraceModeEventRecord = 268435456u;

        public const uint EventControlCodeEnableProvider = 1u;

        public const uint EventTraceControlStop = 1u;

        public const ulong InvalidProcessTraceHandle = ulong.MaxValue;

        public const uint D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL = 0u;

        private static volatile bool timerSealUnavailable;

        private static volatile bool timerExemptUnavailable;

        public static readonly bool PowerThrottlingSupported = ProbePowerThrottling();

        private static int osBuild = -1;

        private static int domainJoined = -1;

        private static int hasBattery = -1;

        private static int boostPrivilegeState;

        private static int debugPrivilegeState;

        public static bool TimerExemptWanted
        {
            get
            {
                if (OsBuild() >= 22000)
                {
                    return !timerExemptUnavailable;
                }
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(int access, bool inherit, int tid);

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
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        public static bool TryGetLiveProcessSessionId(IntPtr processHandle, int pid, out int sessionId)
        {
            sessionId = -1;
            if (processHandle == IntPtr.Zero || pid <= 0)
            {
                return false;
            }
            uint sessionId2;
            if (!ProcessIdToSessionId((uint)pid, out sessionId2) || sessionId2 > int.MaxValue)
            {
                return false;
            }
            if (WaitForSingleObject(processHandle, 0u) != 258)
            {
                return false;
            }
            sessionId = (int)sessionId2;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationProcess(IntPtr h, int infoClass, ref int info, int len);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, ref int info, int len, IntPtr retLen);

        [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
        private static extern int NtQueryInformationProcessBasic(IntPtr h, int infoClass, ref PROCESS_BASIC_INFORMATION info, int len, IntPtr retLen);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returned);

        public static bool TryQueryGrantedAccess(IntPtr handle, out uint granted)
        {
            granted = 0u;
            if (handle == IntPtr.Zero)
            {
                return false;
            }
            IntPtr nativeBuffer = Marshal.AllocHGlobal(56);
            try
            {
                for (int i = 0; i < 56; i += 4)
                {
                    Marshal.WriteInt32(nativeBuffer, i, 0);
                }
                int returned;
                if (NtQueryObject(handle, 0, nativeBuffer, 56, out returned) != 0)
                {
                    return false;
                }
                granted = ((PublicObjectBasicInformation)Marshal.PtrToStructure(nativeBuffer, typeof(PublicObjectBasicInformation))).GrantedAccess;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBuffer);
            }
        }

        public static bool HandleWriteAccessStripped(IntPtr handle, out uint granted)
        {
            if (!TryQueryGrantedAccess(handle, out granted))
            {
                return false;
            }
            return (granted & 0x200) == 0;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr procMask, out UIntPtr sysMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint ms);

        [DllImport("ntdll.dll")]
        public static extern int NtSetTimerResolution(uint desired, [MarshalAs(UnmanagedType.U1)] bool set, out uint actual);

        [DllImport("ntdll.dll")]
        public static extern int NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint ms);

        internal static IntPtr OpenAffinityProcess(int pid)
        {
            return OpenProcess(4608, false, pid);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters counters);

        public static bool QueryProcessSample(IntPtr h, out long creation, out long cpu, out ulong io)
        {
            creation = (cpu = 0L);
            io = 0uL;
            long exit;
            long kernel;
            long user;
            if (!GetProcessTimes(h, out creation, out exit, out kernel, out user))
            {
                return false;
            }
            cpu = kernel + user;
            IoCounters counters;
            if (GetProcessIoCounters(h, out counters))
            {
                io = counters.ReadTransferCount + counters.WriteTransferCount;
            }
            return true;
        }

        internal static int QueryProcessIdentityState(IntPtr handle, long expectedCreation)
        {
            long creation;
            long exit;
            long kernel;
            long user;
            if (expectedCreation <= 0 || !GetProcessTimes(handle, out creation, out exit, out kernel, out user))
            {
                return -1;
            }
            if (creation != expectedCreation || exit != 0)
            {
                return 1;
            }
            return 0;
        }

        public static ulong QueryAffinity(IntPtr h)
        {
            ulong mask;
            if (!TryQueryAffinity(h, out mask))
            {
                return 0uL;
            }
            return mask;
        }

        public static bool TryQueryAffinity(IntPtr h, out ulong mask)
        {
            UIntPtr procMask;
            UIntPtr sysMask;
            bool processAffinityMask = GetProcessAffinityMask(h, out procMask, out sysMask);
            mask = (processAffinityMask ? ((ulong)procMask) : 0);
            return processAffinityMask;
        }

        public static int QueryIoPriority(IntPtr h)
        {
            int info = 0;
            if (NtQueryInformationProcess(h, 33, ref info, 4, IntPtr.Zero) != 0)
            {
                return -1;
            }
            return info;
        }

        public static int QueryPagePriority(IntPtr h)
        {
            int info = 0;
            if (NtQueryInformationProcess(h, 39, ref info, 4, IntPtr.Zero) != 0)
            {
                return -1;
            }
            return info;
        }

        public static bool IsProtectedProcess(IntPtr h)
        {
            int info = 0;
            if (NtQueryInformationProcess(h, 61, ref info, 1, IntPtr.Zero) == 0)
            {
                return (info & 0xFF) != 0;
            }
            return false;
        }

        public static bool TrySetIoPriority(IntPtr process, int priority, out int status)
        {
            status = NtSetInformationProcess(process, 33, ref priority, 4);
            return status == 0;
        }

        public static bool TrySetIoPriority(IntPtr process, int priority)
        {
            int status;
            return TrySetIoPriority(process, priority, out status);
        }

        public static bool TrySetPagePriority(IntPtr process, int priority)
        {
            int status;
            return TrySetPagePriority(process, priority, out status);
        }

        public static bool TrySetPagePriority(IntPtr process, int priority, out int status)
        {
            status = NtSetInformationProcess(process, 39, ref priority, 4);
            return status == 0;
        }

        public static string ImagePath(IntPtr h)
        {
            try
            {
                int size = 600;
                StringBuilder stringBuilder = new StringBuilder(size);
                if (!QueryFullProcessImageName(h, 0, stringBuilder, ref size))
                {
                    size = 32768;
                    stringBuilder = new StringBuilder(size);
                    if (!QueryFullProcessImageName(h, 0, stringBuilder, ref size))
                    {
                        return null;
                    }
                }
                string stringBuilderText = stringBuilder.ToString();
                return (stringBuilderText.Length == 0) ? null : stringBuilderText;
            }
            catch
            {
                return null;
            }
        }

        public static bool StillActive(IntPtr h)
        {
            uint code;
            if (!GetExitCodeProcess(h, out code))
            {
                return true;
            }
            return code == 259;
        }

        public static string ImageName(IntPtr h)
        {
            string imagePathResult = ImagePath(h);
            if (imagePathResult == null)
            {
                return null;
            }
            int matchIndex = imagePathResult.LastIndexOf('\\');
            string substringResult = ((matchIndex >= 0) ? imagePathResult.Substring(matchIndex + 1) : imagePathResult);
            if (substringResult.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                substringResult = substringResult.Substring(0, substringResult.Length - 4);
            }
            return substringResult;
        }

        public static int ParentProcessId(IntPtr processHandle)
        {
            try
            {
                PROCESS_BASIC_INFORMATION info = default(PROCESS_BASIC_INFORMATION);
                int len = Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION));
                return (NtQueryInformationProcessBasic(processHandle, 0, ref info, len, IntPtr.Zero) == 0) ? info.InheritedFromUniqueProcessId.ToInt32() : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
