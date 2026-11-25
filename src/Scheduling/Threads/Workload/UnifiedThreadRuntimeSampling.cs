using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class UnifiedThreadRuntimeSampling
    {
        internal static ThreadTimes Read(int pid, long birth, HardThreadIdentity role)
        {
            if (Check(pid, birth, role) != HardThreadRead.Known) return null;
            return new ThreadTimes { Creation = birth, Ticks = AutoCores.NowTicks,
                Ids = new long[] { role.Tid }, Created = new[] { role.Creation } };
        }

        // 只核验采集确认的呈现线程身份
        internal static HardThreadRead Check(int pid, long birth, HardThreadIdentity role)
        {
            if (pid <= 0 || birth <= 0 || role == null) return HardThreadRead.Unknown;
            IntPtr process = Native.OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return OpenFailure();
            try
            {
                HardThreadRead state = ProcessState(process, birth);
                if (state != HardThreadRead.Known) return state;
                IntPtr thread = Native.OpenThread(0x800, false, role.Tid);
                if (thread == IntPtr.Zero) return OpenFailure();
                try
                {
                    int owner = Native.QueryThreadOwnerPid(thread);
                    if (owner <= 0) return HardThreadRead.Unknown;
                    if (owner != pid) return HardThreadRead.Gone;
                    long threadBirth, exited, kernel, user;
                    if (!Native.GetThreadTimes(thread, out threadBirth, out exited, out kernel, out user))
                        return HardThreadRead.Unknown;
                    if (exited != 0) return HardThreadRead.Gone;
                    if (threadBirth <= 0) return HardThreadRead.Unknown;
                    if (threadBirth != role.Creation) return HardThreadRead.Gone;
                    return ProcessState(process, birth);
                }
                finally { Native.CloseHandle(thread); }
            }
            finally { Native.CloseHandle(process); }
        }

        private static HardThreadRead OpenFailure()
        {
            return Marshal.GetLastWin32Error() == 87 ? HardThreadRead.Gone : HardThreadRead.Unknown;
        }

        private static HardThreadRead ProcessState(IntPtr process, long birth)
        {
            int state = Native.QueryProcessIdentityState(process, birth);
            return state == 0 ? HardThreadRead.Known : state > 0 ? HardThreadRead.Gone : HardThreadRead.Unknown;
        }
    }
}
