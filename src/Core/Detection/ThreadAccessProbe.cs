// @author bdth 2074055628@qq.com
// 文件用途 探测能否取得目标游戏线程的调度句柄 只开关句柄不做任何写入

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class ThreadAccessProbe
    {
        internal struct Result
        {
            public bool Enumerated;
            public int ThreadCount;
            public bool CanQuery;
            public bool CanSet;
            public int QueryError;
            public int SetError;
        }

        public static bool TryProbe(int pid, out Result result)
        {
            result = new Result();
            if (pid <= 0) return false;
            int tid = FirstThreadId(pid, out result.ThreadCount);
            if (tid <= 0) return false;
            result.Enumerated = true;
            result.CanQuery = TryOpen(tid, Native.THREAD_QUERY_LIMITED_INFORMATION, out result.QueryError);
            result.CanSet = TryOpen(tid, Native.THREAD_SET_LIMITED_INFORMATION, out result.SetError);
            return true;
        }

        private static bool TryOpen(int tid, int access, out int error)
        {
            error = 0;
            IntPtr h = Native.OpenThread(access, false, tid);
            if (h == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            Native.CloseHandle(h);
            return true;
        }

        private static int FirstThreadId(int pid, out int count)
        {
            count = 0;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    ProcessThreadCollection threads = process.Threads;
                    count = threads.Count;
                    if (count == 0) return 0;
                    return threads[0].Id;
                }
            }
            catch { return 0; }
        }

        internal static string Describe(Result r)
        {
            if (!r.Enumerated) return Lang.T("probe.thread.noenum");
            if (r.CanSet) return Lang.F("probe.thread.ok", r.ThreadCount);
            if (r.CanQuery) return Lang.F("probe.thread.readonly", r.SetError);
            return Lang.F("probe.thread.denied", r.QueryError, r.SetError);
        }

        public static string ProbeAndDescribe(int pid, string game)
        {
            Result r;
            if (!TryProbe(pid, out r)) return null;
            string text = Describe(r);
            Logger.Log("线程句柄探针：" + (game ?? "?") + " pid " + pid
                + " 线程 " + r.ThreadCount
                + " 读=" + (r.CanQuery ? "可" : "拒(" + r.QueryError + ")")
                + " 写=" + (r.CanSet ? "可" : "拒(" + r.SetError + ")"));
            return text;
        }
    }
}
