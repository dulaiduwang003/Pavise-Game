using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class ThreadLayoutReadback
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity
        {
            internal UIntPtr Mask;
            internal ushort Group, Reserved0, Reserved1, Reserved2;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessorNumber
        {
            internal ushort Group;
            internal byte Number, Reserved;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadIdealProcessorEx(IntPtr thread, out ProcessorNumber ideal);

        internal static bool TryReadThreadMask(int pid, long birth, HardThreadIdentity identity, out ulong mask)
        {
            mask = 0;
            if (identity == null || NativeFrameThreads.ProcessState(pid, birth) != 0) return false;
            IntPtr thread = Native.OpenThread(2048, false, identity.Tid);
            if (thread == IntPtr.Zero) return false;
            try
            {
                long created, exited, kernel, user; GroupAffinity affinity;
                if (Native.QueryThreadOwnerPid(thread) != pid || !Native.GetThreadTimes(thread, out created, out exited, out kernel, out user)
                    || created != identity.Creation || exited != 0 || !GetThreadGroupAffinity(thread, out affinity) || affinity.Group != 0) return false;
                mask = affinity.Mask.ToUInt64(); return mask != 0;
            }
            catch { return false; }
            finally { Native.CloseHandle(thread); }
        }

        // 只读少量候选，亲和性与优先核不是实际执行位置；实际位置由短时 ETW 摘要另行记录。
        internal static string Read(int pid, long birth, HardThreadChoice[] hard, HardThreadChoice[] ideal)
        {
            bool verified;
            return Read(pid, birth, hard, ideal, out verified);
        }

        internal static string Read(int pid, long birth, HardThreadChoice[] hard, HardThreadChoice[] ideal, out bool verified)
        {
            verified = false;
            try { return ReadCore(pid, birth, hard, ideal, out verified); }
            catch (Exception error) { verified = false; return "附加读取失败=" + error.GetType().Name + "；" + error.Message; }
        }

        private static string ReadCore(int pid, long birth, HardThreadChoice[] hard, HardThreadChoice[] ideal, out bool verified)
        {
            verified = false;
            var parts = new List<string>();
            IntPtr process = Native.OpenProcess(4096, false, pid);
            if (process == IntPtr.Zero) return "进程身份及优先级读取失败；错误码=" + Marshal.GetLastWin32Error();
            try
            {
                if (Native.QueryProcessIdentityState(process, birth) != 0) return "进程身份已变化或无法核验";
                uint priority = Native.GetPriorityClass(process);
                int io = Native.QueryIoPriority(process), memory = Native.QueryPagePriority(process), gpu;
                bool gpuKnown = Native.D3DKMTGetProcessSchedulingPriorityClass(process, out gpu) == 0;
                ulong mask;
                bool affinityKnown = Native.TryQueryAffinity(process, out mask);
                verified = priority != 0 && affinityKnown && io >= 0 && memory >= 1 && gpuKnown;
                parts.Add("进程优先级类别=" + (priority == 0 ? "未知" : "0x" + priority.ToString("X"))
                    + "；进程亲和性=" + (affinityKnown ? "0x" + mask.ToString("X") : "未知")
                    + "；I/O优先级=" + (io < 0 ? "未知" : io.ToString())
                    + "；内存页面优先级=" + (memory < 1 ? "未知" : memory.ToString())
                    + "；GPU调度优先级=" + (gpuKnown ? gpu.ToString() : "未知"));
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (HardThreadChoice[] choices in new HardThreadChoice[][] { hard, ideal })
                    foreach (HardThreadChoice choice in choices ?? new HardThreadChoice[0])
                    {
                        if (choice == null || choice.Thread == null || !seen.Add(choice.Thread.Key)) continue;
                        IntPtr thread = Native.OpenThread(2048, false, choice.Thread.Tid);
                        if (thread == IntPtr.Zero) { verified = false; parts.Add("线程=" + choice.Thread.Tid + "；读取失败=" + Marshal.GetLastWin32Error()); continue; }
                        try
                        {
                            long created, exited, kernel, user;
                            if (Native.QueryThreadOwnerPid(thread) != pid
                                || !Native.GetThreadTimes(thread, out created, out exited, out kernel, out user)
                                || created != choice.Thread.Creation || exited != 0)
                            { verified = false; parts.Add("线程=" + choice.Thread.Tid + "；出生身份无法核验"); continue; }
                            GroupAffinity affinity;
                            ProcessorNumber preferred;
                            int level = Native.GetThreadPriority(thread);
                            bool groupKnown = GetThreadGroupAffinity(thread, out affinity);
                            bool idealKnown = GetThreadIdealProcessorEx(thread, out preferred);
                            verified &= groupKnown && idealKnown && level != int.MaxValue;
                            string placement = groupKnown
                                ? "组=" + affinity.Group + "；亲和性=0x" + affinity.Mask.ToUInt64().ToString("X") : "亲和性=未知";
                            string hint = idealKnown
                                ? preferred.Group + ":CPU" + preferred.Number : "未知";
                            parts.Add("线程=" + choice.Thread.Tid + "；出生=" + created + "；" + placement
                                + "；优先核=" + hint + "；线程优先级=" + (level == int.MaxValue ? "未知" : level.ToString()));
                        }
                        finally { Native.CloseHandle(thread); }
                    }
            }
            finally { Native.CloseHandle(process); }
            return string.Join(" | ", parts.ToArray());
        }
    }
}
