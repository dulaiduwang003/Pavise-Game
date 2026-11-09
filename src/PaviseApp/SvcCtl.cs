using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static class SvcCtl
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

        public static bool StopIfRunning(string name)
        {
            bool confirmedStopped;
            return StopIfRunning(name, out confirmedStopped);
        }

        public static bool StopIfRunning(string name, out bool confirmedStopped)
        {
            confirmedStopped = false;
            IntPtr intPtr = OpenSCManagerW(null, null, 1u);
            if (intPtr == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                IntPtr intPtr2 = OpenServiceW(intPtr, name, 36u);
                if (intPtr2 == IntPtr.Zero)
                {
                    return false;
                }
                try
                {
                    SERVICE_STATUS status;
                    if (!QueryServiceStatus(intPtr2, out status))
                    {
                        return false;
                    }
                    if (status.State != 4)
                    {
                        return false;
                    }
                    if (!ControlService(intPtr2, 1, ref status))
                    {
                        return false;
                    }
                    for (int i = 0; i < 25; i++)
                    {
                        if (!QueryServiceStatus(intPtr2, out status))
                        {
                            break;
                        }
                        if (status.State == 1)
                        {
                            confirmedStopped = true;
                            break;
                        }
                        Thread.Sleep(100);
                    }
                    return true;
                }
                finally
                {
                    CloseServiceHandle(intPtr2);
                }
            }
            finally
            {
                CloseServiceHandle(intPtr);
            }
        }

        public static bool EnsureStarted(string name)
        {
            IntPtr intPtr = OpenSCManagerW(null, null, 1u);
            if (intPtr == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                IntPtr intPtr2 = OpenServiceW(intPtr, name, 20u);
                if (intPtr2 == IntPtr.Zero)
                {
                    return false;
                }
                try
                {
                    for (int i = 0; i < 10; i++)
                    {
                        SERVICE_STATUS status;
                        if (!QueryServiceStatus(intPtr2, out status))
                        {
                            return false;
                        }
                        if (status.State == 4)
                        {
                            return true;
                        }
                        if (status.State == 1)
                        {
                            if (!StartServiceW(intPtr2, 0, IntPtr.Zero))
                            {
                                return false;
                            }
                            for (int j = 0; j < 10; j++)
                            {
                                Thread.Sleep(500);
                                if (!QueryServiceStatus(intPtr2, out status))
                                {
                                    return false;
                                }
                                if (status.State == 4)
                                {
                                    return true;
                                }
                            }
                            return false;
                        }
                        Thread.Sleep(500);
                    }
                    return false;
                }
                finally
                {
                    CloseServiceHandle(intPtr2);
                }
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
        private static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr svc, int control, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool StartServiceW(IntPtr svc, int argc, IntPtr argv);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
