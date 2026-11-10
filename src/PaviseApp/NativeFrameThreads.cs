using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class NativeFrameThreads : IFrameThreads, IFrameThreadMemory
    {
        private struct MemoryPriorityInfo
        {
            internal uint Priority;
        }

        internal static int ProcessState(int pid, long birth)
        {
            IntPtr processHandle = Native.OpenProcess(4096, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() != 87)
                {
                    return -1;
                }
                return 1;
            }
            try
            {
                return Native.QueryProcessIdentityState(processHandle, birth);
            }
            finally
            {
                Native.CloseHandle(processHandle);
            }
        }

        private static int ThreadState(IntPtr h, FrameThreadReceipt id, out int priority)
        {
            priority = 0;
            int threadOwnerPid = Native.QueryThreadOwnerPid(h);
            if (threadOwnerPid < 0)
            {
                return -1;
            }
            if (threadOwnerPid != id.Pid)
            {
                return 1;
            }
            long creation;
            long exit;
            long kernel;
            long user;
            if (!Native.GetThreadTimes(h, out creation, out exit, out kernel, out user))
            {
                return -1;
            }
            if (creation != id.ThreadBirth || exit != 0)
            {
                return 1;
            }
            priority = Native.GetThreadPriority(h);
            if (priority != int.MaxValue)
            {
                return 0;
            }
            return -1;
        }

        public int Read(FrameThreadReceipt id, out int priority)
        {
            priority = 0;
            int processStateResult = ProcessState(id.Pid, id.ProcessBirth);
            if (processStateResult != 0)
            {
                return processStateResult;
            }
            IntPtr threadHandle = Native.OpenThread(2048, false, id.Tid);
            if (threadHandle == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() != 87)
                {
                    return -1;
                }
                return 1;
            }
            try
            {
                return ThreadState(threadHandle, id, out priority);
            }
            finally
            {
                Native.CloseHandle(threadHandle);
            }
        }

        public bool Set(FrameThreadReceipt id, int expected, int desired)
        {
            if (ProcessState(id.Pid, id.ProcessBirth) != 0)
            {
                return false;
            }
            IntPtr threadHandle = Native.OpenThread(3072, false, id.Tid);
            if (threadHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                int priority;
                return ThreadState(threadHandle, id, out priority) == 0 && priority == expected && Native.SetThreadPriority(threadHandle, desired) && Native.GetThreadPriority(threadHandle) == desired;
            }
            finally
            {
                Native.CloseHandle(threadHandle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadInformation(IntPtr thread, int kind, out MemoryPriorityInfo info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadInformation(IntPtr thread, int kind, ref MemoryPriorityInfo info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessWorkingSetSizeEx(IntPtr process, out UIntPtr min, out UIntPtr max, out uint flags);

        public bool UnderPressure(int pid, long birth)
        {
            if (ProcessState(pid, birth) != 0)
            {
                return false;
            }
            double usedRatio;
            double totalGb;
            double availGb;
            if (SystemAudit.TryMemory(out usedRatio, out totalGb, out availGb) && usedRatio >= 0.85 && availGb <= 2.0)
            {
                return true;
            }
            IntPtr processHandle = Native.OpenProcess(4096, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                long creation;
                long cpu;
                ulong io;
                UIntPtr min;
                UIntPtr max;
                uint flags;
                return Native.QueryProcessSample(processHandle, out creation, out cpu, out io) && creation == birth && GetProcessWorkingSetSizeEx(processHandle, out min, out max, out flags) && (flags & 4) != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Native.CloseHandle(processHandle);
            }
        }

        public int ReadMemory(FrameThreadReceipt id, out int priority)
        {
            priority = 0;
            int processStateResult = ProcessState(id.Pid, id.ProcessBirth);
            if (processStateResult != 0)
            {
                return processStateResult;
            }
            IntPtr threadHandle = Native.OpenThread(2112, false, id.Tid);
            if (threadHandle == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() != 87)
                {
                    return -1;
                }
                return 1;
            }
            try
            {
                int priority2;
                processStateResult = ThreadState(threadHandle, id, out priority2);
                if (processStateResult != 0)
                {
                    return processStateResult;
                }
                MemoryPriorityInfo info;
                if (!GetThreadInformation(threadHandle, 0, out info, 4u) || info.Priority < 1 || info.Priority > 5)
                {
                    return -1;
                }
                priority = (int)info.Priority;
                return 0;
            }
            catch
            {
                return -1;
            }
            finally
            {
                Native.CloseHandle(threadHandle);
            }
        }

        public bool SetMemory(FrameThreadReceipt id, int expected, int desired)
        {
            if (expected < 1 || expected > 5 || desired < 1 || desired > 5 || ProcessState(id.Pid, id.ProcessBirth) != 0)
            {
                return false;
            }
            IntPtr threadHandle = Native.OpenThread(2144, false, id.Tid);
            if (threadHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                int priority;
                MemoryPriorityInfo info;
                if (ThreadState(threadHandle, id, out priority) != 0 || !GetThreadInformation(threadHandle, 0, out info, 4u) || info.Priority != expected)
                {
                    return false;
                }
                MemoryPriorityInfo info2 = default(MemoryPriorityInfo);
                info2.Priority = (uint)desired;
                return SetThreadInformation(threadHandle, 0, ref info2, 4u) && GetThreadInformation(threadHandle, 0, out info, 4u) && info.Priority == desired;
            }
            catch
            {
                return false;
            }
            finally
            {
                Native.CloseHandle(threadHandle);
            }
        }
    }
}
