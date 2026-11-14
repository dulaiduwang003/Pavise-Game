using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class UnifiedThreadRuntimeSampling
    {
        // 全线程编排最多冻结 32 个详细候选；读回必须容纳整个候选组，同时保持句柄和查询次数有界。
        internal const int MaximumRoles = 32;

        // 只读进程累计 CPU 时间，供负载观察使用；身份对不上或读不到都返回 false。
        internal static bool ReadProcessCpu(int pid, long birth, out long processCpu)
        {
            processCpu = 0;
            if (pid <= 0 || birth <= 0) return false;
            IntPtr process = Native.OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                long currentBirth;
                ulong io;
                return Native.QueryProcessSample(process, out currentBirth, out processCpu, out io) && currentBirth == birth && processCpu >= 0;
            }
            finally { Native.CloseHandle(process); }
        }

        // 角色冻结后只读这些身份；诊断不再触发系统线程表枚举，也不新增任何线程的治理归属。
        internal static ThreadTimes Read(int pid, long birth, HardThreadIdentity[] roles)
        {
            if (pid <= 0 || birth <= 0 || roles == null || roles.Length == 0 || roles.Length > MaximumRoles) return null;
            IntPtr process = Native.OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                long currentBirth, processCpu;
                ulong io;
                if (!Native.QueryProcessSample(process, out currentBirth, out processCpu, out io) || currentBirth != birth) return null;
                var ids = new List<long>(roles.Length);
                var created = new List<long>(roles.Length);
                var cpu = new List<long>(roles.Length);
                var seen = new HashSet<int>();
                foreach (HardThreadIdentity role in roles)
                {
                    if (role == null || !seen.Add(role.Tid)) return null;
                    IntPtr thread = Native.OpenThread(0x800, false, role.Tid);
                    if (thread == IntPtr.Zero) return null;
                    try
                    {
                        long threadBirth, exited, kernel, user;
                        if (Native.QueryThreadOwnerPid(thread) != pid || !Native.GetThreadTimes(thread, out threadBirth, out exited, out kernel, out user)
                            || threadBirth != role.Creation || exited != 0 || kernel < 0 || user < 0) return null;
                        ids.Add(role.Tid); created.Add(threadBirth); cpu.Add(checked(kernel + user));
                    }
                    finally { Native.CloseHandle(thread); }
                }
                if (Native.QueryProcessIdentityState(process, birth) != 0) return null;
                return new ThreadTimes { Creation = birth, ProcessCpu = processCpu, Ticks = AutoCores.NowTicks,
                    Ids = ids.ToArray(), Created = created.ToArray(), Cpu = cpu.ToArray() };
            }
            catch (OverflowException) { return null; }
            finally { Native.CloseHandle(process); }
        }
    }
}
