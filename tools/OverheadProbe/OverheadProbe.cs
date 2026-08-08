// @author bdth 2074055628@qq.com
// 文件用途 黑盒测量 Pavise 自身及其外包给系统服务的常驻开销

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseOverheadProbe
{
    internal sealed class Row
    {
        public int Pid;
        public int Session;
        public int Threads;
        public int Handles;
        public long Cpu;
        public long WorkingSet;
        public string Name;
    }

    internal sealed class Target
    {
        public string Label;
        public string[] Names;
        public readonly Dictionary<int, long> LastCpu = new Dictionary<int, long>();
        public double TotalCpuMs;
        public int LastThreads;
        public int LastHandles;
        public long LastWorkingSet;
        public int PeakThreads;
        public int PeakHandles;
        public long PeakWorkingSet;
        public int Instances;
    }

    internal static class Program
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int cls, IntPtr buffer, int len, out int ret);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryTimerResolution(
            out uint min, out uint max, out uint current);

        private const int SystemProcessInformation = 5;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const string Schema = "pavise-overhead-probe-v2";

        private static int Main(string[] args)
        {
            int seconds = 60;
            int intervalMs = 1000;
            string label = "run";
            string outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--seconds" && i + 1 < args.Length)
                    seconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--interval" && i + 1 < args.Length)
                    intervalMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--label" && i + 1 < args.Length)
                    label = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length)
                    outPath = args[++i];
            }

            var targets = new List<Target>
            {
                new Target { Label = "pavise",   Names = new[] { "Pavise", "Pavise.base", "Pavise.dev", "Pavise.fix" } },
                new Target { Label = "wmiprvse", Names = new[] { "WmiPrvSE" } },
                new Target { Label = "dwm",      Names = new[] { "dwm" } },
                new Target { Label = "explorer", Names = new[] { "explorer" } },
            };

            uint tmin, tmax, tcur;
            NtQueryTimerResolution(out tmin, out tmax, out tcur);
            double startTimerMs = tcur / 10000.0;

            var sb = new StringBuilder();
            sb.AppendLine("# " + Schema);
            sb.AppendLine("# label=" + label);
            sb.AppendLine("seconds,target,cpu_ms_delta,threads,handles,ws_mb,instances");

            Console.WriteLine("probe label=" + label + " seconds=" + seconds
                + " interval=" + intervalMs + "ms timer_res=" + startTimerMs.ToString("F3") + "ms");

            var sw = Stopwatch.StartNew();
            Sample(targets, null, 0);
            double last = 0;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(intervalMs);
                double now = sw.Elapsed.TotalSeconds;
                last = now;
                Sample(targets, sb, now);
            }

            NtQueryTimerResolution(out tmin, out tmax, out tcur);
            double endTimerMs = tcur / 10000.0;
            double elapsed = sw.Elapsed.TotalSeconds;

            Console.WriteLine();
            Console.WriteLine("=== " + label + " : " + elapsed.ToString("F1") + "s ===");
            Console.WriteLine("timer resolution: start " + startTimerMs.ToString("F3")
                + "ms  end " + endTimerMs.ToString("F3") + "ms");
            Console.WriteLine(string.Format("{0,-10} {1,10} {2,10} {3,6} {4,7} {5,8} {6,5}",
                "target", "cpu_ms", "cpu_pct", "thr", "hnd", "ws_mb", "n"));
            foreach (Target t in targets)
            {
                double pct = elapsed > 0 ? t.TotalCpuMs / (elapsed * 1000.0) * 100.0 : 0;
                Console.WriteLine(string.Format(
                    "{0,-10} {1,10} {2,10} {3,6} {4,7} {5,8} {6,5}",
                    t.Label,
                    t.TotalCpuMs.ToString("F1", CultureInfo.InvariantCulture),
                    pct.ToString("F4", CultureInfo.InvariantCulture) + "%",
                    t.LastThreads, t.LastHandles,
                    (t.LastWorkingSet / 1048576.0).ToString("F1", CultureInfo.InvariantCulture),
                    t.Instances));
            }

            sb.AppendLine("# summary elapsed=" + elapsed.ToString("F2", CultureInfo.InvariantCulture));
            sb.AppendLine("# timer_res_start_ms=" + startTimerMs.ToString("F4", CultureInfo.InvariantCulture));
            sb.AppendLine("# timer_res_end_ms=" + endTimerMs.ToString("F4", CultureInfo.InvariantCulture));
            foreach (Target t in targets)
            {
                double pct = elapsed > 0 ? t.TotalCpuMs / (elapsed * 1000.0) * 100.0 : 0;
                sb.AppendLine("# total " + t.Label
                    + " cpu_ms=" + t.TotalCpuMs.ToString("F2", CultureInfo.InvariantCulture)
                    + " cpu_pct=" + pct.ToString("F5", CultureInfo.InvariantCulture)
                    + " last_thr=" + t.LastThreads
                    + " last_hnd=" + t.LastHandles
                    + " peak_thr=" + t.PeakThreads
                    + " peak_hnd=" + t.PeakHandles
                    + " last_ws_mb=" + (t.LastWorkingSet / 1048576.0).ToString("F2", CultureInfo.InvariantCulture));
            }

            if (outPath != null)
            {
                File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
                Console.WriteLine("csv -> " + outPath);
            }
            return 0;
        }

        private static void Sample(List<Target> targets, StringBuilder sb, double now)
        {
            List<Row> rows = Enumerate();
            foreach (Target t in targets)
            {
                double cpuDelta = 0;
                int threads = 0, handles = 0, instances = 0;
                long ws = 0;
                foreach (Row r in rows)
                {
                    bool match = false;
                    foreach (string n in t.Names)
                        if (string.Equals(n, r.Name, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                    if (!match) continue;
                    instances++;
                    threads += r.Threads;
                    handles += r.Handles;
                    ws += r.WorkingSet;
                    long prev;
                    if (t.LastCpu.TryGetValue(r.Pid, out prev) && r.Cpu >= prev)
                        cpuDelta += (r.Cpu - prev) / 10000.0;
                    t.LastCpu[r.Pid] = r.Cpu;
                }
                t.TotalCpuMs += cpuDelta;
                t.LastThreads = threads;
                t.LastHandles = handles;
                t.LastWorkingSet = ws;
                t.Instances = instances;
                if (threads > t.PeakThreads) t.PeakThreads = threads;
                if (handles > t.PeakHandles) t.PeakHandles = handles;
                if (ws > t.PeakWorkingSet) t.PeakWorkingSet = ws;
                if (sb != null)
                    sb.AppendLine(string.Join(",", new[]
                    {
                        now.ToString("F2", CultureInfo.InvariantCulture),
                        t.Label,
                        cpuDelta.ToString("F3", CultureInfo.InvariantCulture),
                        threads.ToString(CultureInfo.InvariantCulture),
                        handles.ToString(CultureInfo.InvariantCulture),
                        (ws / 1048576.0).ToString("F2", CultureInfo.InvariantCulture),
                        instances.ToString(CultureInfo.InvariantCulture),
                    }));
            }
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
                    ushort nameLen = (ushort)Marshal.ReadInt16(cursor, 0x38);
                    IntPtr namePtr = Marshal.ReadIntPtr(cursor, 0x40);
                    var row = new Row
                    {
                        Threads = Marshal.ReadInt32(cursor, 4),
                        Cpu = Marshal.ReadInt64(cursor, 0x28) + Marshal.ReadInt64(cursor, 0x30),
                        Pid = Marshal.ReadIntPtr(cursor, 0x50).ToInt32(),
                        Handles = Marshal.ReadInt32(cursor, 0x60),
                        Session = Marshal.ReadInt32(cursor, 0x64),
                        WorkingSet = Marshal.ReadIntPtr(cursor, 0x90).ToInt64(),
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
