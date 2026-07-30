// @author bdth 2074055628@qq.com
// 文件用途 探测能否取得目标游戏线程的调度句柄 只开关句柄不做任何写入

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AegisApp
{
    // 线程级车道（SetThreadSelectedCpuSets）要求对游戏线程拿到 THREAD_SET_LIMITED_INFORMATION。
    // 带内核组件的反作弊通常按进程整体剥夺访问权，所以这里只需要问一次"能不能开"，
    // 不需要挑特定线程。探针只打开再关闭句柄，不读线程状态、不写任何调度参数，
    // 失败就照实记录错误码——这是决定线程级方案是否值得实现的唯一依据。
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

        // 线程枚举走的是系统进程信息，不需要游戏进程句柄
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

        // 只在证据模式下调用；结果同时写运行日志，便于跨会话对比不同反作弊的表现
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
