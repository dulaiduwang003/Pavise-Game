using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class SvcQuery
    {
#pragma warning disable 0649
        private struct SERVICE_STATUS_PROCESS
        {
            public uint ServiceType;

            public uint CurrentState;

            public uint ControlsAccepted;

            public uint Win32ExitCode;

            public uint ServiceSpecificExitCode;

            public uint CheckPoint;

            public uint WaitHint;

            public uint ProcessId;

            public uint ServiceFlags;
        }

        private struct ENUM_SERVICE_STATUS_PROCESS
        {
            public IntPtr ServiceName;

            public IntPtr DisplayName;

            public SERVICE_STATUS_PROCESS Status;
        }
#pragma warning restore 0649

        public static List<KeyValuePair<string, int>> RunningServices()
        {
            IntPtr openSCManagerWHandle = OpenSCManagerW(null, null, 4u);
            if (openSCManagerWHandle == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                uint resumeHandle = 0u;
                uint bytesNeeded;
                uint servicesReturned;
                EnumServicesStatusExW(openSCManagerWHandle, 0, 48u, 1u, IntPtr.Zero, 0u, out bytesNeeded, out servicesReturned, ref resumeHandle, null);
                if (Marshal.GetLastWin32Error() != 234 || bytesNeeded == 0)
                {
                    return null;
                }
                IntPtr nativeBuffer = Marshal.AllocHGlobal((int)bytesNeeded);
                try
                {
                    resumeHandle = 0u;
                    if (!EnumServicesStatusExW(openSCManagerWHandle, 0, 48u, 1u, nativeBuffer, bytesNeeded, out bytesNeeded, out servicesReturned, ref resumeHandle, null))
                    {
                        return null;
                    }
                    List<KeyValuePair<string, int>> keyValuePairs = new List<KeyValuePair<string, int>>((int)servicesReturned);
                    int sizeOfResult = Marshal.SizeOf(typeof(ENUM_SERVICE_STATUS_PROCESS));
                    for (uint iterationIndex = 0u; iterationIndex < servicesReturned; iterationIndex++)
                    {
                        ENUM_SERVICE_STATUS_PROCESS eNUM_SERVICE_STATUS_PROCESS = (ENUM_SERVICE_STATUS_PROCESS)Marshal.PtrToStructure(new IntPtr(nativeBuffer.ToInt64() + iterationIndex * sizeOfResult), typeof(ENUM_SERVICE_STATUS_PROCESS));
                        string ptrToStringUniResult = Marshal.PtrToStringUni(eNUM_SERVICE_STATUS_PROCESS.ServiceName);
                        if (!string.IsNullOrEmpty(ptrToStringUniResult))
                        {
                            keyValuePairs.Add(new KeyValuePair<string, int>(ptrToStringUniResult, (int)eNUM_SERVICE_STATUS_PROCESS.Status.ProcessId));
                        }
                    }
                    return keyValuePairs;
                }
                finally
                {
                    Marshal.FreeHGlobal(nativeBuffer);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseServiceHandle(openSCManagerWHandle);
            }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machine, string db, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint type, uint state, IntPtr buffer, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string groupName);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
