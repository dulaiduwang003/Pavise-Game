// @author bdth 2074055628@qq.com
// 文件用途 验证单次 NtQuerySystemInformation 可替代逐进程句柄查询并量化收益

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseSnapshotBench
{
    internal sealed class Row
    {
        public int Pid;
        public int Parent;
        public int Session;
        public long Creation;
        public long Cpu;
        public ulong Io;
        public string Name;
        public int Threads;
    }

    internal static class Program
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int cls, IntPtr buffer, int len, out int ret);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(
            IntPtr h, int flags, StringBuilder buffer, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long creation,
            out long exit, out long kernel, out long user);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr h, int cls, out ProcessBasicInformation info, int len, out int ret);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessBasicInformation
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int SystemProcessInformation = 5;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        private static int Main(string[] args)
        {
            int rounds = 15;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--rounds" && i + 1 < args.Length)
                    rounds = int.Parse(args[++i], CultureInfo.InvariantCulture);

            Console.WriteLine("pavise-snapshot-bench-v1 rounds=" + rounds);

            List<Row> rows = Enumerate();
            Console.WriteLine("rows = " + rows.Count);

            int self = Process.GetCurrentProcess().SessionId;
            int selfPid = Process.GetCurrentProcess().Id;
            int inSession = 0;
            foreach (Row r in rows) if (r.Session == self) inSession++;
            Console.WriteLine("in-session rows = " + inSession + " (self session " + self + ")");

            Console.WriteLine();
            Console.WriteLine("--- correctness cross-check vs Process/handle path ---");
            int checkedCount = 0, parentOk = 0, sessionOk = 0, creationOk = 0, nameOk = 0;
            var mismatches = new List<string>();
            Process[] all = Process.GetProcesses();
            var byPid = new Dictionary<int, Row>();
            foreach (Row r in rows) byPid[r.Pid] = r;
            foreach (Process p in all)
            {
                try
                {
                    Row r;
                    if (!byPid.TryGetValue(p.Id, out r)) continue;
                    if (p.Id <= 4) continue;
                    int session;
                    try { session = p.SessionId; } catch { continue; }
                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        long c, e, k, u;
                        if (!GetProcessTimes(h, out c, out e, out k, out u)) continue;
                        ProcessBasicInformation pbi;
                        int ret;
                        int st = NtQueryInformationProcess(h, 0, out pbi,
                            Marshal.SizeOf(typeof(ProcessBasicInformation)), out ret);
                        checkedCount++;
                        if (r.Session == session) sessionOk++;
                        else mismatches.Add("session pid=" + p.Id + " ntqsi=" + r.Session + " api=" + session);
                        if (r.Creation == c) creationOk++;
                        else mismatches.Add("creation pid=" + p.Id);
                        if (string.Equals(r.Name, p.ProcessName, StringComparison.OrdinalIgnoreCase)) nameOk++;
                        else mismatches.Add("name pid=" + p.Id + " ntqsi='" + r.Name + "' api='" + p.ProcessName + "'");
                        if (st == 0)
                        {
                            int apiParent = pbi.InheritedFromUniqueProcessId.ToInt32();
                            if (r.Parent == apiParent) parentOk++;
                            else mismatches.Add("parent pid=" + p.Id + " ntqsi=" + r.Parent + " api=" + apiParent);
                        }
                        else parentOk++;
                    }
                    finally { CloseHandle(h); }
                }
                catch { }
            }
            foreach (Process p in all) p.Dispose();
            Console.WriteLine("checked=" + checkedCount + " session_match=" + sessionOk
                + " creation_match=" + creationOk + " name_match=" + nameOk
                + " parent_match=" + parentOk);
            for (int i = 0; i < mismatches.Count && i < 12; i++)
                Console.WriteLine("  MISMATCH " + mismatches[i]);
            if (mismatches.Count > 12)
                Console.WriteLine("  ... +" + (mismatches.Count - 12) + " more");

            Console.WriteLine();
            Console.WriteLine("--- timings ---");
            Report("A. NtQuerySystemInformation full parse (all fields)", rounds,
                delegate { var sw = Stopwatch.StartNew(); Enumerate(); sw.Stop(); return sw.Elapsed.TotalMilliseconds; });

            Report("B. Process.GetProcesses() + per-proc OpenProcess path", rounds,
                delegate { return LegacyPath(self); });

            var pathCache = new Dictionary<long, string>();
            Report("C. NtQSI + ImagePath for uncached pids only (warm)", rounds,
                delegate { return CachedPath(self, pathCache); });

            Console.WriteLine();
            Console.WriteLine("path cache entries = " + pathCache.Count);
            return 0;
        }

        private static void Report(string label, int rounds, Func<double> body)
        {
            body();
            var samples = new List<double>();
            for (int i = 0; i < rounds; i++) samples.Add(body());
            samples.Sort();
            double sum = 0;
            foreach (double v in samples) sum += v;
            Console.WriteLine(string.Format("{0,-52} min {1,8:F3}ms  med {2,8:F3}ms  avg {3,8:F3}ms",
                label, samples[0], samples[samples.Count / 2], sum / samples.Count));
        }

        private static double LegacyPath(int self)
        {
            var sw = Stopwatch.StartNew();
            Process[] all = Process.GetProcesses();
            var buffer = new StringBuilder(1024);
            foreach (Process p in all)
            {
                try
                {
                    int pid = p.Id;
                    if (pid <= 4) continue;
                    int session;
                    try { session = p.SessionId; } catch { continue; }
                    if (session != self) continue;
                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        int cap = buffer.Capacity;
                        buffer.Length = 0;
                        QueryFullProcessImageName(h, 0, buffer, ref cap);
                        long c, e, k, u;
                        GetProcessTimes(h, out c, out e, out k, out u);
                    }
                    finally { CloseHandle(h); }
                }
                catch { }
            }
            foreach (Process p in all) p.Dispose();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static double CachedPath(int self, Dictionary<long, string> cache)
        {
            var sw = Stopwatch.StartNew();
            List<Row> rows = Enumerate();
            var buffer = new StringBuilder(1024);
            var live = new HashSet<long>();
            foreach (Row r in rows)
            {
                if (r.Session != self || r.Pid <= 4) continue;
                long key = ((long)r.Pid << 20) ^ r.Creation;
                live.Add(key);
                if (cache.ContainsKey(key)) continue;
                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, r.Pid);
                if (h == IntPtr.Zero) { cache[key] = ""; continue; }
                try
                {
                    int cap = buffer.Capacity;
                    buffer.Length = 0;
                    cache[key] = QueryFullProcessImageName(h, 0, buffer, ref cap)
                        ? buffer.ToString() : "";
                }
                finally { CloseHandle(h); }
            }
            var drop = new List<long>();
            foreach (long key in cache.Keys) if (!live.Contains(key)) drop.Add(key);
            foreach (long key in drop) cache.Remove(key);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static List<Row> Enumerate()
        {
            var result = new List<Row>();
            int size = 512 * 1024;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int need;
                int status;
                while ((status = NtQuerySystemInformation(
                    SystemProcessInformation, buf, size, out need)) == StatusInfoLengthMismatch)
                {
                    Marshal.FreeHGlobal(buf);
                    size = need + 128 * 1024;
                    buf = Marshal.AllocHGlobal(size);
                }
                if (status != 0) return result;

                IntPtr cursor = buf;
                while (true)
                {
                    int nextOffset = Marshal.ReadInt32(cursor, 0);
                    int threads = Marshal.ReadInt32(cursor, 4);
                    long createTime = Marshal.ReadInt64(cursor, 0x20);
                    long userTime = Marshal.ReadInt64(cursor, 0x28);
                    long kernelTime = Marshal.ReadInt64(cursor, 0x30);
                    ushort nameLen = (ushort)Marshal.ReadInt16(cursor, 0x38);
                    IntPtr namePtr = Marshal.ReadIntPtr(cursor, 0x40);
                    IntPtr pid = Marshal.ReadIntPtr(cursor, 0x50);
                    IntPtr parent = Marshal.ReadIntPtr(cursor, 0x58);
                    int session = Marshal.ReadInt32(cursor, 0x64);

                    var row = new Row
                    {
                        Pid = pid.ToInt32(),
                        Parent = parent.ToInt32(),
                        Session = session,
                        Creation = createTime,
                        Cpu = userTime + kernelTime,
                        Threads = threads,
                        Name = namePtr != IntPtr.Zero && nameLen > 0
                            ? Marshal.PtrToStringUni(namePtr, nameLen / 2) : "",
                    };
                    if (row.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        row.Name = row.Name.Substring(0, row.Name.Length - 4);
                    result.Add(row);

                    if (nextOffset == 0) break;
                    cursor = new IntPtr(cursor.ToInt64() + nextOffset);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return result;
        }
    }
}
