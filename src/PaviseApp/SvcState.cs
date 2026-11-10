using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class SvcState
    {
        private struct SERVICE_STATUS
        {
            public int Type;

            public int State;

            public int ControlsAccepted;

            public int Win32ExitCode;

            public int SpecificExitCode;

            public int CheckPoint;

            public int WaitHint;
        }

        public static bool StopTaken(int state)
        {
            if (state != 1)
            {
                return state == 3;
            }
            return true;
        }

        public static int Query(string name)
        {
            try
            {
                IntPtr openSCManagerWHandle = OpenSCManagerW(null, null, 1u);
                if (openSCManagerWHandle == IntPtr.Zero)
                {
                    return 0;
                }
                try
                {
                    IntPtr openServiceWHandle = OpenServiceW(openSCManagerWHandle, name, 4u);
                    if (openServiceWHandle == IntPtr.Zero)
                    {
                        return 0;
                    }
                    try
                    {
                        SERVICE_STATUS status;
                        return QueryServiceStatus(openServiceWHandle, out status) ? status.State : 0;
                    }
                    finally
                    {
                        CloseServiceHandle(openServiceWHandle);
                    }
                }
                finally
                {
                    CloseServiceHandle(openSCManagerWHandle);
                }
            }
            catch
            {
                return 0;
            }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machine, string db, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
