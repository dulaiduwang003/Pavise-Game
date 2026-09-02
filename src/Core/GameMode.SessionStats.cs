// @author bdth 2074055628@qq.com
// 文件用途 对局时长与 CPU 占用统计 测试探针入口
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private static bool CpuTicks(int pid, out long ticks, out long creation)
        {
            ticks = 0; creation = 0;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                long e, k, u;
                if (!GetProcessTimes(h, out creation, out e, out k, out u)) return false;
                ticks = k + u;
                return true;
            }
            finally { Native.CloseHandle(h); }
        }

        private static long CurrentProcessCpuTicks()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    return process.TotalProcessorTime.Ticks;
            }
            catch { return 0; }
        }

        internal static double AverageCpuPercent(
            long cpuTicks, TimeSpan duration)
        {
            if (cpuTicks <= 0 || duration.Ticks <= 0) return 0;
            int processors = Math.Max(1, Environment.ProcessorCount);
            double percent = cpuTicks * 100.0
                / (duration.Ticks * (double)processors);
            return Math.Max(0, Math.Min(100, percent));
        }

        private static string FmtDur(TimeSpan t)
        {
            if (t.TotalHours >= 1) return (int)t.TotalHours + "h" + t.Minutes.ToString("00") + "m";
            if (t.TotalMinutes >= 1) return t.Minutes + "m" + t.Seconds.ToString("00") + "s";
            return t.Seconds + "s";
        }

        private static string FmtCpu(long ticks)
        {
            TimeSpan t = TimeSpan.FromTicks(ticks);
            if (t.TotalSeconds < 1) return "<1s";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + "m" + t.Seconds.ToString("00") + "s";
            return t.TotalSeconds.ToString("0.0") + "s";
        }

#if PAVISE_SELFTEST
        internal void ProbeSessionBegin(string game) { ReportBegin(game); }

        internal void ProbeSessionTrack(int pid, string name) { ReportTrack(pid, name); }

        internal void ProbeSessionSeal(int pid) { ReportSeal(pid); }

        internal void ProbeSessionUntrack(int pid) { ReportUntrack(pid); }

        internal void ProbeSessionFinish() { ReportFinish(); }
#endif

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
    }
}
