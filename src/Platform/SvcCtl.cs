// @author bdth 2074055628@qq.com
// 文件用途 控制 Windows 服务并等待目标状态

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AegisApp
{
    internal static class SvcCtl
    {
        public static bool StopIfRunning(string name)
        {
            IntPtr scm = OpenSCManagerW(null, null, 1 );
            if (scm == IntPtr.Zero) return false;
            try
            {
                IntPtr svc = OpenServiceW(scm, name, 0x24 );
                if (svc == IntPtr.Zero) return false;
                try
                {
                    SERVICE_STATUS st;
                    if (!QueryServiceStatus(svc, out st)) return false;
                    if (st.State == 1 ) return false;
                    if (!ControlService(svc, 1 , ref st)) return false;
                    for (int i = 0; i < 10; i++)
                    {
                        Thread.Sleep(500);
                        if (!QueryServiceStatus(svc, out st)) return false;
                        if (st.State == 1 ) return true;
                    }
                    return false;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        public static bool EnsureStarted(string name)
        {
            IntPtr scm = OpenSCManagerW(null, null, 1);
            if (scm == IntPtr.Zero) return false;
            try
            {
                IntPtr svc = OpenServiceW(scm, name, 0x14 );
                if (svc == IntPtr.Zero) return false;
                try
                {
                    for (int i = 0; i < 10; i++)
                    {
                        SERVICE_STATUS st;
                        if (!QueryServiceStatus(svc, out st)) return false;
                        if (st.State == 4 ) return true;
                        if (st.State == 1 )
                        {
                            if (!StartServiceW(svc, 0, IntPtr.Zero)) return false;
                            for (int j = 0; j < 10; j++)
                            {
                                Thread.Sleep(500);
                                if (!QueryServiceStatus(svc, out st)) return false;
                                if (st.State == 4 ) return true;
                            }
                            return false;
                        }
                        Thread.Sleep(500);
                    }
                    return false;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public int Type, State, ControlsAccepted, Win32ExitCode, SpecificExitCode, CheckPoint, WaitHint;
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
