using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class CoreWorkBudget
    {
        private long windowStart, spent;
        internal long TotalTicks;
        internal int Operations, Deferrals;
        internal double MaxMs;
        internal bool Admit(long now)
        {
            if (windowStart == 0 || now - windowStart >= 5 * TimeSpan.TicksPerSecond) { windowStart = now; spent = 0; }
            if (spent < 25 * TimeSpan.TicksPerMillisecond) return true;
            Deferrals++; return false;
        }
        internal void Record(long start)
        {
            long cost = Math.Max(0, AutoCores.NowTicks - start);
            spent += cost; TotalTicks += cost; Operations++;
            MaxMs = Math.Max(MaxMs, cost / (double)TimeSpan.TicksPerMillisecond);
        }
        internal double TotalMs { get { return TotalTicks / (double)TimeSpan.TicksPerMillisecond; } }
    }
    internal sealed class CoreGameLearning
    {
        internal string Key;
        internal int Sessions, Capacity, Regressions;
        internal long AccessRetryAt;
        internal bool Loaded;
        internal double Peak;
        internal static string Fingerprint(string path)
        {
            try
            {
                var file = new FileInfo(path); if (!file.Exists) return null;
                string text = "v3|" + file.FullName.ToLowerInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks
                    + "|" + Native.OsBuild() + "|" + CpuTopology.AllMask + "|" + CpuTopology.PerfMask + "|" + CpuTopology.FavoredMask
                    + "|" + CpuTopology.BigL3Mask + "|" + string.Join(",", CpuTopology.PhysicalCoreMasks())
                    + "|" + string.Join(",", CpuTopology.L3Masks()) + "|" + Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
                    + "|" + Native.OnAcPower() + "|" + Native.IsElevated();
                using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
            }
            catch { return null; }
        }
        internal static CoreGameLearning Load(string path, string mode = "")
        {
            var l = new CoreGameLearning { Key = Fingerprint(path) };
            if (l.Key == null) return l;
            using (var sha = SHA256.Create())
                l.Key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(l.Key + "|" + mode))).Replace("-", "");
            string[] row = Settings.LoadStr("CoreLearn_" + l.Key, "").Split('|');
            int sessions, capacity, regressions; double peak; long stamp, retry;
            if (row.Length == 6 && int.TryParse(row[0], out sessions) && int.TryParse(row[1], out capacity)
                && int.TryParse(row[2], out regressions) && double.TryParse(row[3], NumberStyles.Float, CultureInfo.InvariantCulture, out peak)
                && long.TryParse(row[4], out stamp) && stamp <= DateTime.UtcNow.Ticks && DateTime.UtcNow.Ticks - stamp < TimeSpan.FromDays(30).Ticks
                && long.TryParse(row[5], out retry) && retry >= 0 && retry <= DateTime.UtcNow.AddHours(1).Ticks
                && sessions > 0 && capacity >= 2 && capacity <= 64 && regressions >= 0 && regressions <= sessions
                && !double.IsNaN(peak) && peak >= 0 && peak <= 64)
            { l.Sessions = sessions; l.Capacity = capacity; l.Regressions = regressions; l.Peak = peak; l.AccessRetryAt = retry; l.Loaded = true; }
            return l;
        }
        internal void Save(int capacity, double peak, bool regression)
        {
            if (Key == null || capacity < 2 || double.IsNaN(peak)) return;
            Sessions++; Capacity = capacity; Peak = peak; if (regression) Regressions++;
            Settings.SaveStr("CoreLearn_" + Key, Sessions + "|" + Capacity + "|" + Regressions + "|"
                + Peak.ToString("R", CultureInfo.InvariantCulture) + "|" + DateTime.UtcNow.Ticks + "|" + AccessRetryAt);
            var index = new List<string>();
            foreach (string key in Settings.LoadStr("CoreLearnIndex", "").Split(';'))
                if (key.Length == 64 && key != Key && !index.Contains(key)) index.Add(key);
            index.Add(Key);
            while (index.Count > 128) { Settings.Remove("CoreLearn_" + index[0]); index.RemoveAt(0); }
            Settings.SaveStr("CoreLearnIndex", string.Join(";", index));
        }
    }
    internal sealed class CoreFrameWindow
    {
        internal readonly List<double> Frames = new List<double>();
        internal double CpuMs, WallMs, Load, ParseMs;
        internal bool Complete, Failed;
        internal string SwapChain, Failure;
        internal double P99 { get { return Percentile(.99); } }
        internal double Median { get { return Percentile(.5); } }
        internal double Percentile(double p)
        {
            if (Frames.Count == 0) return 0;
            double[] sorted = Frames.ToArray(); Array.Sort(sorted);
            return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * p) - 1)];
        }
        internal bool Usable { get { return Complete && !Failed && Frames.Count >= 300 && WallMs >= 10000; } }
        internal static string Compare(CoreFrameWindow baseline, CoreFrameWindow trial)
        {
            if (baseline == null || trial == null || !baseline.Usable || !trial.Usable) return "insufficient";
            if (baseline.SwapChain != null && trial.SwapChain != baseline.SwapChain) return "phase-changed";
            if (Math.Abs(baseline.Load - trial.Load) > Math.Max(.4, baseline.Load * .25)
                || trial.Median / baseline.Median < .7 || trial.Median / baseline.Median > 1.3) return "phase-changed";
            // 前后性能比较仅作回归检查，不证明因果收益。
            return trial.P99 > baseline.P99 * 1.20 + 1 ? "regressed" : "no-regression-observed";
        }
    }
    internal sealed class CoreFrameCapture : IDisposable
    {
        internal const string ToolHash = "B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF";
        private readonly object gate = new object();
        private Process process;
        private readonly Stopwatch elapsed = new Stopwatch();
        private string[] header;
        private readonly Dictionary<string, List<double>> chains = new Dictionary<string, List<double>>();
        private int pid;
        private bool invalid;
        private string failure;
        private long parseTicks;
        private string sessionName;
        internal CoreFrameWindow Result;
        internal string State = "idle";
#if PAVISE_SELFTEST
        internal static Func<int, CoreFrameWindow> CaptureForTest;
#endif
        private static string checkedTool;
        private static bool validTool;
        internal static string ToolPath
        { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "PresentMon.exe"); } }
        internal bool Start(int targetPid)
        {
#if PAVISE_SELFTEST
            if (CaptureForTest != null) { Result = CaptureForTest(targetPid); State = "capturing"; return true; }
#endif
            if (process != null) return false;
            string path = ToolPath;
            try
            {
                if (checkedTool != path)
                {
                    checkedTool = path;
                    using (var file = File.OpenRead(path)) using (var sha = SHA256.Create())
                        validTool = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "") == ToolHash;
                }
                if (!validTool) { State = "collector-unavailable"; return false; }
                pid = targetPid;
                sessionName = "PaviseFrames-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
                var info = new ProcessStartInfo(path, "--process_id " + pid + " --output_stdout --v1_metrics --no_console_stats --no_track_input --no_track_gpu"
                    + " --timed 20 --terminate_after_timed --terminate_on_proc_exit --session_name " + sessionName)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                process = new Process { StartInfo = info };
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Parse(e.Data); };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    if (e.Data.IndexOf("access denied", StringComparison.OrdinalIgnoreCase) >= 0) failure = "capture-access-denied";
                    if (e.Data.IndexOf("lost", StringComparison.OrdinalIgnoreCase) >= 0 || e.Data.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) invalid = true;
                };
                process.Start(); process.PriorityClass = ProcessPriorityClass.BelowNormal;
                elapsed.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); State = "capturing"; return true;
            }
            catch { State = "collector-unavailable"; Dispose(); return false; }
        }
        internal void Parse(string line)
        {
            long start = AutoCores.NowTicks;
            try { ParseLine(line); }
            finally { System.Threading.Interlocked.Add(ref parseTicks, AutoCores.NowTicks - start); }
        }
        private void ParseLine(string line)
        {
            lock (gate)
            {
                string[] cells = SplitCsv(line);
                if (header == null)
                { if (Array.IndexOf(cells, "MsBetweenPresents") >= 0 && Array.IndexOf(cells, "ProcessID") >= 0) header = cells; return; }
                int p = Array.IndexOf(header, "ProcessID"), f = Array.IndexOf(header, "MsBetweenPresents"), c = Array.IndexOf(header, "SwapChainAddress");
                int owner; double value;
                if (cells.Length != header.Length || p < 0 || f < 0 || c < 0 || !int.TryParse(cells[p], out owner) || owner != pid
                    || !double.TryParse(cells[f], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || value <= 0) return;
                if (value > 1000) { invalid = true; failure = "phase-changed"; return; }
                List<double> frames;
                if (!chains.TryGetValue(cells[c], out frames)) { if (chains.Count >= 8) return; chains[cells[c]] = frames = new List<double>(); }
                if (frames.Count < 20000) frames.Add(value); else invalid = true;
            }
        }
        internal static string[] SplitCsv(string line)
        {
            var result = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
                else if (c == ',' && !quoted) { result.Add(cell.ToString()); cell.Length = 0; }
                else cell.Append(c);
            }
            result.Add(cell.ToString()); return result.ToArray();
        }
        internal bool Poll(double load)
        {
            if (process == null || Result != null) return Result != null;
            try
            {
                double cpu = process.TotalProcessorTime.TotalMilliseconds;
                if (!process.HasExited && (elapsed.ElapsedMilliseconds > 28000 || cpu > 1500
                    || System.Threading.Interlocked.Read(ref parseTicks) > 200 * TimeSpan.TicksPerMillisecond))
                { invalid = true; failure = "capture-budget"; process.Kill(); StopTrace(); }
                if (!process.HasExited) return false;
                process.WaitForExit();
                var result = new CoreFrameWindow { Complete = true, Failed = invalid || process.ExitCode != 0,
                    CpuMs = cpu, WallMs = elapsed.Elapsed.TotalMilliseconds, Load = load, Failure = failure,
                    ParseMs = System.Threading.Interlocked.Read(ref parseTicks) / (double)TimeSpan.TicksPerMillisecond };
                lock (gate)
                {
                    List<double> largest = null;
                    foreach (var chain in chains) if (largest == null || chain.Value.Count > largest.Count) { largest = chain.Value; result.SwapChain = chain.Key; }
                    if (largest != null) result.Frames.AddRange(largest);
                }
                Result = result; State = result.Usable ? "measured" : "insufficient";
                process.Dispose(); process = null; StopTrace(); return true;
            }
            catch { State = "collector-failed"; Dispose(); return false; }
        }
        public void Dispose()
        {
            if (process == null) return;
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } } catch { }
            process.Dispose(); process = null; StopTrace();
        }
        private void StopTrace()
        {
            if (sessionName == null) return;
            int size = Marshal.SizeOf(typeof(Native.EventTraceProperties)), total = size + 2048;
            IntPtr buffer = Marshal.AllocHGlobal(total);
            try
            {
                for (int i = 0; i < total; i++) Marshal.WriteByte(buffer, i, 0);
                var p = new Native.EventTraceProperties { WnodeBufferSize = (uint)total, LoggerNameOffset = (uint)size };
                Marshal.StructureToPtr(p, buffer, false);
                Native.ControlTrace(0, sessionName, buffer, Native.EventTraceControlStop);
            }
            catch { }
            finally { Marshal.FreeHGlobal(buffer); sessionName = null; }
        }
    }
}
