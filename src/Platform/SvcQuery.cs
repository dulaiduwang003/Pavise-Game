// @author bdth 2074055628@qq.com
// 文件用途 枚举正在运行的服务及其宿主进程号

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class SvcQuery
    {
        private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x4;
        private const int SC_ENUM_PROCESS_INFO = 0;
        private const uint SERVICE_WIN32 = 0x30;
        private const uint SERVICE_ACTIVE = 0x1;
        private const int ERROR_MORE_DATA = 234;

        public static List<KeyValuePair<string, int>> RunningServices()
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
            if (scm == IntPtr.Zero) return null;
            try
            {
                uint needed, count, resume = 0;
                EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE,
                    IntPtr.Zero, 0, out needed, out count, ref resume, null);
                if (Marshal.GetLastWin32Error() != ERROR_MORE_DATA || needed == 0) return null;

                IntPtr buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    resume = 0;
                    if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE,
                        buf, needed, out needed, out count, ref resume, null)) return null;

                    var list = new List<KeyValuePair<string, int>>((int)count);
                    int stride = Marshal.SizeOf(typeof(ENUM_SERVICE_STATUS_PROCESS));
                    for (uint i = 0; i < count; i++)
                    {
                        var entry = (ENUM_SERVICE_STATUS_PROCESS)Marshal.PtrToStructure(
                            new IntPtr(buf.ToInt64() + (long)i * stride), typeof(ENUM_SERVICE_STATUS_PROCESS));
                        string name = Marshal.PtrToStringUni(entry.ServiceName);
                        if (string.IsNullOrEmpty(name)) continue;
                        list.Add(new KeyValuePair<string, int>(name, (int)entry.Status.ProcessId));
                    }
                    return list;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return null; }
            finally { CloseServiceHandle(scm); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS_PROCESS
        {
            public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
                ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ENUM_SERVICE_STATUS_PROCESS
        {
            public IntPtr ServiceName;
            public IntPtr DisplayName;
            public SERVICE_STATUS_PROCESS Status;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machine, string db, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint type, uint state,
            IntPtr buffer, uint bufSize, out uint bytesNeeded, out uint servicesReturned,
            ref uint resumeHandle, string groupName);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
