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

        private const uint SC_MANAGER_ENUMERATE_SERVICE = 4u;

        private const int SC_ENUM_PROCESS_INFO = 0;

        private const uint SERVICE_WIN32 = 48u;

        private const uint SERVICE_ACTIVE = 1u;

        private const int ERROR_MORE_DATA = 234;
        private const uint SC_MANAGER_CONNECT = 1u;
        private const uint SERVICE_QUERY_STATUS = 4u;
        private const uint SERVICE_RUNNING = 4u;
        private const uint SERVICE_WIN32_OWN_PROCESS = 0x10u;
        private const uint SERVICE_USER_OWN_PROCESS_INSTANCE = 0xD0u;
        private const uint SERVICE_INTERACTIVE_PROCESS = 0x100u;

        internal static int RunningOwnProcessId(string name)
        {
            IntPtr manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (manager == IntPtr.Zero) return 0;
            try
            {
                IntPtr service = OpenServiceW(manager, name, SERVICE_QUERY_STATUS);
                if (service == IntPtr.Zero) return 0;
                try
                {
                    SERVICE_STATUS_PROCESS status;
                    uint needed;
                    if (!QueryServiceStatusEx(service, SC_ENUM_PROCESS_INFO, out status, (uint)Marshal.SizeOf(typeof(SERVICE_STATUS_PROCESS)), out needed)
                        || status.CurrentState != SERVICE_RUNNING) return 0;
                    uint hostType = status.ServiceType & ~SERVICE_INTERACTIVE_PROCESS;
                    return hostType == SERVICE_WIN32_OWN_PROCESS || hostType == SERVICE_USER_OWN_PROCESS_INSTANCE ? (int)status.ProcessId : 0;
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }

        public static List<KeyValuePair<string, int>> RunningServices()
        {
            IntPtr intPtr = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
            if (intPtr == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                uint resumeHandle = 0u;
                uint bytesNeeded;
                uint servicesReturned;
                EnumServicesStatusExW(intPtr, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, IntPtr.Zero, 0u, out bytesNeeded, out servicesReturned, ref resumeHandle, null);
                if (Marshal.GetLastWin32Error() != ERROR_MORE_DATA || bytesNeeded == 0)
                {
                    return null;
                }
                IntPtr intPtr2 = Marshal.AllocHGlobal((int)bytesNeeded);
                try
                {
                    resumeHandle = 0u;
                    if (!EnumServicesStatusExW(intPtr, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, intPtr2, bytesNeeded, out bytesNeeded, out servicesReturned, ref resumeHandle, null))
                    {
                        return null;
                    }
                    List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>((int)servicesReturned);
                    int sizeOfResult = Marshal.SizeOf(typeof(ENUM_SERVICE_STATUS_PROCESS));
                    for (uint iterationIndex = 0u; iterationIndex < servicesReturned; iterationIndex++)
                    {
                        ENUM_SERVICE_STATUS_PROCESS eNUM_SERVICE_STATUS_PROCESS = (ENUM_SERVICE_STATUS_PROCESS)Marshal.PtrToStructure(new IntPtr(intPtr2.ToInt64() + iterationIndex * sizeOfResult), typeof(ENUM_SERVICE_STATUS_PROCESS));
                        string ptrToStringUniResult = Marshal.PtrToStringUni(eNUM_SERVICE_STATUS_PROCESS.ServiceName);
                        if (!string.IsNullOrEmpty(ptrToStringUniResult))
                        {
                            list.Add(new KeyValuePair<string, int>(ptrToStringUniResult, (int)eNUM_SERVICE_STATUS_PROCESS.Status.ProcessId));
                        }
                    }
                    return list;
                }
                finally
                {
                    Marshal.FreeHGlobal(intPtr2);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseServiceHandle(intPtr);
            }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machine, string db, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out SERVICE_STATUS_PROCESS status, uint size, out uint needed);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint type, uint state, IntPtr buffer, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string groupName);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
