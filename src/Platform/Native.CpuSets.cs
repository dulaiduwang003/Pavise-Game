// @author bdth 2074055628@qq.com
// File purpose CPU Sets, thread affinity queries and EcoQoS
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class Native
    {
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
        // -1 means even kernel32 could not be resolved, 0 means an old OS genuinely lacks the export
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
                // 259 from GetExitCodeThread could also be the thread's real exit code, only a zero-timeout
                // wait on a handle with SYNCHRONIZE distinguishes liveness unambiguously
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

        // null means the query failed, an empty array means the thread has no explicit CPU Set assignment
        // a count change between the two calls also counts as unknown, the attribution chain must fail closed
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
        // Whether on AC power now, treat as plugged in when unreadable, never assume battery when it cannot be determined
        //   AcLineStatus 0 battery 1 AC 255 unknown
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
        // D3DKMT VRAM query and reservation do not accept QUERY_LIMITED, measured on this machine as STATUS_ACCESS_DENIED every time
        //   QueryVideoMemoryInfo needs QUERY_INFORMATION, ChangeVideoMemoryReservation needs SET_INFORMATION
        //   both are more readily refused by anti-cheat than QUERY_LIMITED, so the shield's skip rate is naturally higher than boost's
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
