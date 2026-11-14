using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class Native
    {

        private struct PowerThrottlingState
        {
            public uint Version;

            public uint ControlMask;

            public uint StateMask;
        }

        public static uint EcoQoSWantMask(bool sealTimer)
        {
            if (!sealTimer || timerSealUnavailable)
            {
                return 1u;
            }
            return 5u;
        }

        public static bool ApplyEcoQoS(IntPtr process)
        {
            return ApplyEcoQoS(process, false);
        }

        public static bool ApplyEcoQoS(IntPtr process, bool sealTimer)
        {
            if (sealTimer && !timerSealUnavailable)
            {
                if (SetPowerThrottling(process, 5u, 5u))
                {
                    return true;
                }
                if (Marshal.GetLastWin32Error() != 87)
                {
                    return false;
                }
                timerSealUnavailable = true;
            }
            return SetPowerThrottling(process, 1u, 1u);
        }

        public static int QueryBoostDisabled(IntPtr h)
        {
            bool disabled;
            if (!GetProcessPriorityBoost(h, out disabled))
            {
                return -1;
            }
            if (!disabled)
            {
                return 0;
            }
            return 1;
        }

        public static bool TrySetBoostDisabled(IntPtr h, bool disable)
        {
            return SetProcessPriorityBoost(h, disable);
        }

        public static bool TimerExemptOk(int controlMask, int stateMask)
        {
            if (((uint)controlMask & 4u) != 0)
            {
                return (stateMask & 4) == 0;
            }
            return false;
        }

        public static bool HighQoSMasksOk(int controlMask, int stateMask)
        {
            if (!EcoClearedMasksOk(controlMask, stateMask))
            {
                return false;
            }
            if (TimerExemptWanted)
            {
                return TimerExemptOk(controlMask, stateMask);
            }
            return true;
        }

        public static bool EcoClearedMasksOk(int controlMask, int stateMask)
        {
            if (((uint)controlMask & (true ? 1u : 0u)) != 0)
            {
                return (stateMask & 1) == 0;
            }
            return false;
        }

        public static bool ApplyHighQoS(IntPtr process, bool ignoreTimerResolution)
        {
            bool timerExemptDropped;
            return ApplyHighQoS(process, ignoreTimerResolution, out timerExemptDropped);
        }

        public static bool ApplyHighQoS(IntPtr process, bool ignoreTimerResolution, out bool timerExemptDropped)
        {
            timerExemptDropped = false;
            if (ignoreTimerResolution && !timerExemptUnavailable)
            {
                if (SetPowerThrottling(process, 5u, 0u))
                {
                    int controlMask;
                    int stateMask;
                    if (!TryQueryPowerThrottling(process, out controlMask, out stateMask))
                    {
                        return true;
                    }
                    if (TimerExemptOk(controlMask, stateMask))
                    {
                        return true;
                    }
                }
                else if (Marshal.GetLastWin32Error() != 87)
                {
                    return false;
                }
                timerExemptUnavailable = true;
                timerExemptDropped = true;
            }
            return SetPowerThrottling(process, 1u, 0u);
        }

        public static bool RestorePowerThrottling(IntPtr process, int controlMask, int stateMask)
        {
            if (controlMask < 0)
            {
                return SetPowerThrottling(process, 0u, 0u);
            }
            return SetPowerThrottling(process, (uint)controlMask, (stateMask >= 0) ? ((uint)stateMask) : 0u);
        }

        public static bool TryQueryPowerThrottling(IntPtr process, out int controlMask, out int stateMask)
        {
            int win32, ntStatus;
            return TryQueryPowerThrottling(process, out controlMask, out stateMask, out win32, out ntStatus);
        }

        internal static bool RestorePowerThrottling(IntPtr process, int controlMask, int stateMask, out int win32)
        {
            bool restored = RestorePowerThrottling(process, controlMask, stateMask);
            win32 = restored ? 0 : Marshal.GetLastWin32Error();
            return restored;
        }

        internal static bool TryQueryPowerThrottling(IntPtr process, out int controlMask, out int stateMask, out int win32, out int ntStatus)
        {
            int sizeOfResult = Marshal.SizeOf(typeof(PowerThrottlingState));
            PowerThrottlingState powerThrottlingState = default(PowerThrottlingState);
            powerThrottlingState.Version = 1u;
            PowerThrottlingState info = powerThrottlingState;
            bool processInformation = GetProcessInformation(process, 4, ref info, sizeOfResult);
            win32 = processInformation ? 0 : Marshal.GetLastWin32Error();
            ntStatus = 0;
            if (!processInformation)
            {
                PowerThrottlingState powerThrottlingState2 = default(PowerThrottlingState);
                powerThrottlingState2.Version = 1u;
                info = powerThrottlingState2;
                ntStatus = NtQueryInformationProcessPower(process, 77, ref info, sizeOfResult, IntPtr.Zero);
                processInformation = ntStatus == 0;
            }
            controlMask = (int)info.ControlMask;
            stateMask = (int)info.StateMask;
            return processInformation;
        }

        private static bool ProbePowerThrottling()
        {
            try
            {
                int controlMask;
                int stateMask;
                return TryQueryPowerThrottling((IntPtr)(-1), out controlMask, out stateMask);
            }
            catch
            {
                return false;
            }
        }

        private static bool SetPowerThrottling(IntPtr process, uint controlMask, uint stateMask)
        {
            PowerThrottlingState powerThrottlingState = default(PowerThrottlingState);
            powerThrottlingState.Version = 1u;
            powerThrottlingState.ControlMask = controlMask;
            powerThrottlingState.StateMask = stateMask;
            PowerThrottlingState info = powerThrottlingState;
            return SetProcessInformation(process, 4, ref info, Marshal.SizeOf(typeof(PowerThrottlingState)));
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PowerThrottlingState info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessInformation(IntPtr h, int infoClass, ref PowerThrottlingState info, int size);

        [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
        private static extern int NtQueryInformationProcessPower(IntPtr h, int infoClass, ref PowerThrottlingState info, int len, IntPtr retLen);

    }
}
