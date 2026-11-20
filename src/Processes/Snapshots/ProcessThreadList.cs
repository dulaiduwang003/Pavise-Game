using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 优先枚举目标进程自己的线程 避免逐个目标遍历全系统线程
    // 仅在 64 位下启用 枚举不可用时 由调用方依次回退到系统进程信息查询和 Toolhelp
    internal static class ProcessThreadList
    {
        private const int ProcessHeaderBytes = 256, ThreadBytes = 80, ProcessIdOffset = 80, ThreadOwnerOffset = 40, ThreadIdOffset = 48;
        private const int LengthMismatch = unchecked((int)0xC0000004);
        private static readonly object gate = new object();
        private static IntPtr buffer;
        private static int capacity;

        [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int needed);
        [DllImport("ntdll.dll")] private static extern int NtGetNextThread(IntPtr process, IntPtr thread, uint access, uint attributes, uint flags, out IntPtr next);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern uint GetThreadId(IntPtr thread);
        private const int NoMoreEntries = unchecked((int)0x8000001A);

        // 只遍历目标进程自己的线程 成本与该进程线程数成正比 不必为一个进程解析全系统数千条线程
        // 已结束但对象仍被引用的线程不计入 与系统进程信息查询的口径一致
        // 调用方须在检查期间持有目标进程句柄 进程编号不会被复用 打不开（受保护进程）或遍历失败时返回 false
        // 调用方回退到系统查询 超过 limit 时多返回一条 由调用方按原有上限拒绝
        internal static bool TryReadOwn(int pid, int limit, out List<uint> threads)
        {
            threads = new List<uint>();
            if (IntPtr.Size != 8 || pid <= 4 || limit <= 0) return false;
            IntPtr process = OpenProcess(0x400, false, pid);
            if (process == IntPtr.Zero) return false;
            IntPtr current = IntPtr.Zero;
            try
            {
                while (true)
                {
                    IntPtr next;
                    int status = NtGetNextThread(process, current, 0x100800, 0, 0, out next);
                    if (current != IntPtr.Zero) { CloseHandle(current); current = IntPtr.Zero; }
                    if (status == NoMoreEntries) return threads.Count > 0;
                    if (status != 0 || next == IntPtr.Zero) return false;
                    current = next;
                    uint state = WaitForSingleObject(next, 0);
                    if (state == 0) continue;
                    if (state != 258) return false;
                    uint tid = GetThreadId(next);
                    if (tid == 0) return false;
                    threads.Add(tid);
                    if (threads.Count > limit) return true;
                }
            }
            catch { return false; }
            finally
            {
                if (current != IntPtr.Zero) CloseHandle(current);
                CloseHandle(process);
            }
        }

        // 返回 false 本次查询不可用（非 64 位 查询失败或结构越界） 调用方回退
        // 返回 true 且 found 为 false 查询完整 但快照里没有这个进程
        // systemThreads 是同一快照里的线程总数 调用方沿用原有的条目上限
        internal static bool TryRead(int pid, out bool found, out List<uint> threads, out int systemThreads)
        {
            found = false; threads = new List<uint>(); systemThreads = 0;
            if (IntPtr.Size != 8 || pid <= 0) return false;
            lock (gate)
            {
                try
                {
                    int used;
                    if (!Query(out used)) return false;
                    long offset = 0;
                    while (true)
                    {
                        if (offset < 0 || offset + ProcessHeaderBytes > used) return false;
                        IntPtr entry = new IntPtr(buffer.ToInt64() + offset);
                        int next = Marshal.ReadInt32(entry, 0), count = Marshal.ReadInt32(entry, 4);
                        long end = offset + ProcessHeaderBytes + (long)count * ThreadBytes;
                        if (next < 0 || count < 0 || end > used || (next > 0 && end > offset + next)) return false;
                        systemThreads += count;
                        if (Marshal.ReadIntPtr(entry, ProcessIdOffset).ToInt64() == pid)
                        {
                            // 同一快照里重复出现的进程编号不可信 交回调用方按原路径处理
                            if (found) return false;
                            found = true;
                            for (int index = 0; index < count; index++)
                            {
                                int row = ProcessHeaderBytes + index * ThreadBytes;
                                // 线程行里的所属进程必须与进程行一致 否则说明结构布局与预期不符
                                if (Marshal.ReadIntPtr(entry, row + ThreadOwnerOffset).ToInt64() != pid) return false;
                                threads.Add((uint)Marshal.ReadIntPtr(entry, row + ThreadIdOffset).ToInt64());
                            }
                        }
                        if (next == 0) return true;
                        offset += next;
                    }
                }
                catch { return false; }
            }
        }

        private static bool Query(out int used)
        {
            used = 0;
            // 缓冲不足时按系统给出的长度扩容 最多四次 进程数量剧烈变化时放弃本次查询
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (buffer == IntPtr.Zero)
                {
                    capacity = Math.Max(capacity, 1 << 20);
                    buffer = Marshal.AllocHGlobal(capacity);
                }
                int needed;
                int status = NtQuerySystemInformation(5, buffer, capacity, out needed);
                if (status == 0)
                {
                    if (needed <= 0 || needed > capacity) return false;
                    used = needed;
                    return true;
                }
                if (status != LengthMismatch || needed <= 0 || needed > (1 << 28)) return false;
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                capacity = needed + (1 << 18);
            }
            return false;
        }
    }
}
