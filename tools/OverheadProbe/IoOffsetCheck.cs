// @author bdth 2074055628@qq.com
// 文件用途 验证 SYSTEM_PROCESS_INFORMATION 的 IO 计数器偏移与内核 API 一致

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseIoOffsetCheck
{
    internal static class Program
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int len, out int ret);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        private const int SystemProcessInformation = 5;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static int Main()
        {
            var readXfer = new Dictionary<int, ulong>();
            var writeXfer = new Dictionary<int, ulong>();
            var readOps = new Dictionary<int, ulong>();

            int size = 1 << 20;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int need, status;
                while ((status = NtQuerySystemInformation(SystemProcessInformation, buf, size, out need))
                    == StatusInfoLengthMismatch)
                {
                    Marshal.FreeHGlobal(buf);
                    size = need + (1 << 17);
                    buf = Marshal.AllocHGlobal(size);
                }
                if (status != 0) { Console.WriteLine("NtQSI failed 0x" + status.ToString("X")); return 1; }

                IntPtr cursor = buf;
                while (true)
                {
                    int nextOffset = Marshal.ReadInt32(cursor, 0);
                    int pid = Marshal.ReadIntPtr(cursor, 0x50).ToInt32();
                    if (pid > 0)
                    {
                        readOps[pid] = (ulong)Marshal.ReadInt64(cursor, 0xD0);
                        readXfer[pid] = (ulong)Marshal.ReadInt64(cursor, 0xE8);
                        writeXfer[pid] = (ulong)Marshal.ReadInt64(cursor, 0xF0);
                    }
                    if (nextOffset == 0) break;
                    cursor = new IntPtr(cursor.ToInt64() + nextOffset);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }

            int checkedCount = 0, readOk = 0, writeOk = 0, opsOk = 0, nonZero = 0;
            var bad = new List<string>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        IoCounters c;
                        if (!GetProcessIoCounters(h, out c)) continue;
                        ulong r, w, o;
                        if (!readXfer.TryGetValue(p.Id, out r)) continue;
                        writeXfer.TryGetValue(p.Id, out w);
                        readOps.TryGetValue(p.Id, out o);
                        checkedCount++;
                        if (c.ReadTransferCount > 0 || c.WriteTransferCount > 0) nonZero++;
                        bool rOk = Close(r, c.ReadTransferCount);
                        bool wOk = Close(w, c.WriteTransferCount);
                        bool oOk = Close(o, c.ReadOperationCount);
                        if (rOk) readOk++;
                        if (wOk) writeOk++;
                        if (oOk) opsOk++;
                        if ((!rOk || !wOk) && bad.Count < 10)
                            bad.Add("pid=" + p.Id + " " + p.ProcessName
                                + " ntqsi_r=" + r + " api_r=" + c.ReadTransferCount
                                + " ntqsi_w=" + w + " api_w=" + c.WriteTransferCount);
                    }
                    finally { CloseHandle(h); }
                }
                catch { }
                finally { p.Dispose(); }
            }

            Console.WriteLine("checked=" + checkedCount + " nonzero_io=" + nonZero);
            Console.WriteLine("read_transfer_match=" + readOk + "  write_transfer_match=" + writeOk
                + "  read_ops_match=" + opsOk);
            foreach (string s in bad) Console.WriteLine("  MISMATCH " + s);
            return 0;
        }

        private static bool Close(ulong a, ulong b)
        {
            ulong diff = a > b ? a - b : b - a;
            ulong scale = a > b ? a : b;
            if (diff == 0) return true;
            return scale > 0 && diff * 1000 <= scale;
        }
    }
}
