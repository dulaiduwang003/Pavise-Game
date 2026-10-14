// 作者：bdth 2074055628@qq.com
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class Native
    {
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

        public static bool RestoreCpuSets(IntPtr h, uint[] ids)
        {
            return ids != null && (ids.Length == 0 ? TryClearCpuSets(h) : TrySetCpuSets(h, ids));
        }

        public static bool RestoreCpuSetsVerified(IntPtr h, uint[] ids)
        {
            return RestoreCpuSets(h, ids) && CpuSetsMatch(h, ids);
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
        // 固件性能切换必须确认正在使用交流电。
        internal static bool ConfirmedAcPower()
        {
            try
            {
                SystemPowerStatusNative status;
                return GetSystemPowerStatus(out status) && status.AcLineStatus == 1;
            }
            catch { return false; }
        }
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
        // 显存查询需要 QUERY_INFORMATION，预留写入需要 SET_INFORMATION。
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
