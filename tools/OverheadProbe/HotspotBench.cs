// @author bdth 2074055628@qq.com
// 文件用途 量化 Pavise 常驻循环里各原语在本机的真实单次成本

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseHotspotBench
{
    internal static class Program
    {
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

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int cls, IntPtr buffer, int len, out int ret);

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
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

        private const int Rounds = 12;

        private static int Main(string[] args)
        {
            int rounds = Rounds;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--rounds" && i + 1 < args.Length)
                    rounds = int.Parse(args[++i], CultureInfo.InvariantCulture);

            Console.WriteLine("pavise-hotspot-bench-v1  rounds=" + rounds);
            Console.WriteLine("process count = " + Process.GetProcesses().Length);
            Console.WriteLine();

            Report("Process.GetProcesses() + Dispose", rounds, BenchGetProcesses);
            Report("NtQuerySystemInformation(SystemProcessInformation)", rounds, BenchNtQuery);
            Report("Snapshot: OpenProcess+ImagePath+Parent+Times over session", rounds, BenchSnapshot);
            Report("Logger.Log() single line (FileInfo+AppendAllText)", rounds, BenchLogAppend);
            Report("Logger.Log() 20 lines burst", rounds, BenchLogBurst);
            Report("EnumWindows + per-window attrs", rounds, BenchEnumWindows);
            return 0;
        }

        private static void Report(string label, int rounds, Func<double> body)
        {
            body();
            var samples = new List<double>();
            for (int i = 0; i < rounds; i++) samples.Add(body());
            samples.Sort();
            double min = samples[0];
            double med = samples[samples.Count / 2];
            double max = samples[samples.Count - 1];
            double sum = 0;
            foreach (double v in samples) sum += v;
            Console.WriteLine(string.Format(
                "{0,-52} min {1,8:F3}ms  med {2,8:F3}ms  max {3,8:F3}ms  avg {4,8:F3}ms",
                label, min, med, max, sum / samples.Count));
        }

        private static double BenchGetProcesses()
        {
            var sw = Stopwatch.StartNew();
            Process[] all = Process.GetProcesses();
            foreach (Process p in all) p.Dispose();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static double BenchNtQuery()
        {
            var sw = Stopwatch.StartNew();
            int size = 1 << 20;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int need;
                while (NtQuerySystemInformation(SystemProcessInformation, buf, size, out need)
                    == unchecked((int)0xC0000004))
                {
                    Marshal.FreeHGlobal(buf);
                    size = need + 65536;
                    buf = Marshal.AllocHGlobal(size);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static double BenchSnapshot()
        {
            Process[] all = Process.GetProcesses();
            int self = Process.GetCurrentProcess().SessionId;
            var sw = Stopwatch.StartNew();
            var parents = new Dictionary<int, int>();
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
                        ProcessBasicInformation pbi;
                        int ret;
                        if (NtQueryInformationProcess(h, 0, out pbi, Marshal.SizeOf(typeof(ProcessBasicInformation)), out ret) == 0)
                            parents[pid] = pbi.InheritedFromUniqueProcessId.ToInt32();
                        long c, e, k, u;
                        GetProcessTimes(h, out c, out e, out k, out u);
                    }
                    finally { CloseHandle(h); }
                }
                catch { }
            }
            sw.Stop();
            foreach (Process p in all) p.Dispose();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static string logPath;

        private static string LogPath()
        {
            if (logPath == null)
            {
                logPath = Path.Combine(Path.GetTempPath(), "pavise-bench-log.txt");
                File.WriteAllText(logPath, "");
            }
            return logPath;
        }

        private static void LogLine(string path, string msg)
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 512 * 1024)
            {
                string old = path + ".old";
                try
                {
                    if (File.Exists(old)) File.Delete(old);
                    fi.MoveTo(old);
                }
                catch { try { fi.Delete(); } catch { } }
            }
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
        }

        private static double BenchLogAppend()
        {
            string path = LogPath();
            var sw = Stopwatch.StartNew();
            LogLine(path, "bench line");
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static double BenchLogBurst()
        {
            string path = LogPath();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) LogLine(path, "bench burst line " + i);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr h);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        private static double BenchEnumWindows()
        {
            var result = new HashSet<int>();
            var sw = Stopwatch.StartNew();
            EnumWindows(delegate(IntPtr window, IntPtr state)
            {
                try
                {
                    if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero
                        || (GetWindowLong(window, -20) & 0x80) != 0 || IsIconic(window))
                        return true;
                    int cloaked;
                    if (DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                        return true;
                    Rect rect;
                    if (!GetWindowRect(window, out rect) || rect.Right <= rect.Left) return true;
                    uint pid;
                    GetWindowThreadProcessId(window, out pid);
                    if (pid > 0) result.Add((int)pid);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
    }
}
