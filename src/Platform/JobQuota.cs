// @author bdth 2074055628@qq.com
// 文件用途 用作业对象给一组后台进程施加 CPU 硬配额 内核在配额用尽后停止派发时间片
// 进程一旦加入作业便无法移出 因此解除限制靠清空控制标志 而不是关闭句柄

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class JobQuota : IDisposable
    {
        private IntPtr job = IntPtr.Zero;
        private readonly List<int> members = new List<int>();
        private uint appliedRate;

        public int MemberCount { get { return members.Count; } }
        public bool IsOpen { get { return job != IntPtr.Zero; } }
        public string LastError { get; private set; }

        public bool Open()
        {
            if (job != IntPtr.Zero) return true;
            job = Native.CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                LastError = "CreateJobObject 失败 win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            return true;
        }

        public bool Add(int pid)
        {
            if (job == IntPtr.Zero && !Open()) return false;
            if (members.Contains(pid)) return true;
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_TERMINATE, false, pid);
            if (h == IntPtr.Zero)
            {
                LastError = "pid " + pid + " 打不开句柄 win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            try
            {
                if (!Native.AssignProcessToJobObject(job, h))
                {
                    LastError = "pid " + pid + " 加入作业失败 win32=" + Marshal.GetLastWin32Error();
                    return false;
                }
                members.Add(pid);
                return true;
            }
            finally { Native.CloseHandle(h); }
        }

        public bool SetCap(double percent)
        {
            if (job == IntPtr.Zero) return false;
            if (percent <= 0 || percent > 100) return false;
            uint rate = (uint)Math.Max(1, Math.Min(10000, Math.Round(percent * 100.0)));
            var info = new Native.JobCpuRateControl();
            info.ControlFlags = Native.JobCpuRateEnable | Native.JobCpuRateHardCap;
            info.RateOrWeight = rate;
            if (!Native.SetInformationJobObject(job, Native.JobObjectCpuRateControlInformation,
                ref info, Marshal.SizeOf(typeof(Native.JobCpuRateControl))))
            {
                LastError = "设配额失败 win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            appliedRate = rate;
            return true;
        }

        public bool VerifyCap(out double readBackPercent)
        {
            readBackPercent = 0;
            if (job == IntPtr.Zero) return false;
            var info = new Native.JobCpuRateControl();
            if (!Native.QueryInformationJobObject(job, Native.JobObjectCpuRateControlInformation,
                ref info, Marshal.SizeOf(typeof(Native.JobCpuRateControl)), IntPtr.Zero))
            {
                LastError = "回读配额失败 win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            readBackPercent = info.RateOrWeight / 100.0;
            return (info.ControlFlags & Native.JobCpuRateHardCap) != 0 && info.RateOrWeight == appliedRate;
        }

        public bool Clear()
        {
            if (job == IntPtr.Zero) return true;
            var info = new Native.JobCpuRateControl();
            info.ControlFlags = 0;
            info.RateOrWeight = 0;
            if (!Native.SetInformationJobObject(job, Native.JobObjectCpuRateControlInformation,
                ref info, Marshal.SizeOf(typeof(Native.JobCpuRateControl))))
            {
                LastError = "清除配额失败 win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            appliedRate = 0;
            return true;
        }

        public void Dispose()
        {
            if (job == IntPtr.Zero) return;
            try { Clear(); }
            catch { }
            Native.CloseHandle(job);
            job = IntPtr.Zero;
            members.Clear();
        }
    }
}
