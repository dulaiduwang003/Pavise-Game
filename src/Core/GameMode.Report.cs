// @author bdth 2074055628@qq.com
// 文件用途 记录游戏会话中的调度结果和资源变化

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AegisApp
{
    internal partial class GameMode
    {
        private readonly Dictionary<int, long> repCpu = new Dictionary<int, long>();
        private readonly Dictionary<int, long> repCreation = new Dictionary<int, long>();
        private readonly Dictionary<int, string> repProc = new Dictionary<int, string>();
        private long repStart;
        private string repGame;
        private bool repBoosted;
        private long repAegisCpuStart;

        public event Action<string> SessionEnded;

        private void ReportBegin(string game)
        {
            long aegisCpu = CurrentProcessCpuTicks();
            lock (sync)
            {
                repCpu.Clear();
                repCreation.Clear();
                repProc.Clear();
                repGame = game;
                repStart = Stopwatch.GetTimestamp();
                repBoosted = false;
                repAegisCpuStart = aegisCpu;
            }
        }

        private void ReportBoostVerified()
        {
            lock (sync) if (repGame != null) repBoosted = true;
        }

        private void ReportUntrack(int pid)
        {
            lock (sync)
            {
                repCpu.Remove(pid);
                repCreation.Remove(pid);
                repProc.Remove(pid);
            }
        }

        private void ReportTrack(int pid, string name)
        {
            lock (sync) { if (repGame == null || repCpu.ContainsKey(pid)) return; }
            long t, creation;
            if (!CpuTicks(pid, out t, out creation)) return;
            lock (sync)
            {
                if (!repCpu.ContainsKey(pid))
                {
                    repCpu[pid] = t; repCreation[pid] = creation; repProc[pid] = name;
                }
            }
        }

        private void ReportFinish()
        {
            Dictionary<int, long> cpu;
            Dictionary<int, string> names;
            Dictionary<int, long> creations;
            string game;
            long t0;
            bool boosted;
            long aegisCpuStart;
            lock (sync)
            {
                game = repGame;
                t0 = repStart;
                cpu = new Dictionary<int, long>(repCpu);
                names = new Dictionary<int, string>(repProc);
                creations = new Dictionary<int, long>(repCreation);
                boosted = repBoosted;
                aegisCpuStart = repAegisCpuStart;
                repCpu.Clear();
                repCreation.Clear();
                repProc.Clear();
                repGame = null;
                repBoosted = false;
                repAegisCpuStart = 0;
            }
            if (game == null) return;

            TimeSpan dur = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - t0) / Stopwatch.Frequency);
            long total = 0, top = 0;
            string topName = null;
            foreach (var kv in cpu)
            {
                long now, creation;
                if (!CpuTicks(kv.Key, out now, out creation)) continue;
                long expectedCreation;
                if (!creations.TryGetValue(kv.Key, out expectedCreation) || creation != expectedCreation) continue;
                long d = now - kv.Value;
                if (d < 0) continue;
                total += d;
                if (d > top)
                {
                    top = d;
                    string nm;
                    if (names.TryGetValue(kv.Key, out nm)) topName = nm;
                }
            }

            string msg = Lang.F("rep.done", game, FmtDur(dur), cpu.Count, FmtCpu(total));
            if (topName != null && top >= TimeSpan.TicksPerSecond)
                msg += Lang.F("rep.top", topName, FmtCpu(top));
            long aegisCpuEnd = CurrentProcessCpuTicks();
            long aegisCpuDelta = aegisCpuStart > 0
                && aegisCpuEnd >= aegisCpuStart
                ? aegisCpuEnd - aegisCpuStart : 0;
            double aegisCpuPercent = AverageCpuPercent(
                aegisCpuDelta, dur);
            msg += Lang.F(
                "rep.aegis.cpu",
                aegisCpuPercent.ToString("0.00", CultureInfo.InvariantCulture));
            Logger.Log("会话报告：" + msg);
            PerformancePreset usedPreset;
            lock (sync) usedPreset = preset;
            SessionReportStore.Append(
                dataDir, game, usedPreset, dur, boosted, cpu.Count,
                aegisCpuPercent);

            if (dur.TotalSeconds >= 60)
            {
                var h = SessionEnded;
                if (h != null) { try { h(msg); } catch { } }
            }
        }

        private static bool CpuTicks(int pid, out long ticks, out long creation)
        {
            ticks = 0; creation = 0;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                long e, k, u;
                if (!GetProcessTimes(h, out creation, out e, out k, out u)) return false;
                ticks = k + u;
                return true;
            }
            finally { Native.CloseHandle(h); }
        }

        private static long CurrentProcessCpuTicks()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    return process.TotalProcessorTime.Ticks;
            }
            catch { return 0; }
        }

        internal static double AverageCpuPercent(
            long cpuTicks, TimeSpan duration)
        {
            if (cpuTicks <= 0 || duration.Ticks <= 0) return 0;
            int processors = Math.Max(1, Environment.ProcessorCount);
            double percent = cpuTicks * 100.0
                / (duration.Ticks * (double)processors);
            return Math.Max(0, Math.Min(100, percent));
        }

        private static string FmtDur(TimeSpan t)
        {
            if (t.TotalHours >= 1) return (int)t.TotalHours + "h" + t.Minutes.ToString("00") + "m";
            if (t.TotalMinutes >= 1) return t.Minutes + "m" + t.Seconds.ToString("00") + "s";
            return t.Seconds + "s";
        }

        private static string FmtCpu(long ticks)
        {
            TimeSpan t = TimeSpan.FromTicks(ticks);
            if (t.TotalSeconds < 1) return "<1s";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + "m" + t.Seconds.ToString("00") + "s";
            return t.TotalSeconds.ToString("0.0") + "s";
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
    }

    internal static class SessionReportStore
    {
        public const string FileName = "Aegis.reports.log";
        private static readonly object FileSync = new object();
        private static readonly HashSet<string> MigrationChecked =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Append(string dataDir, string game, PerformancePreset preset, TimeSpan duration,
            bool boostVerified, int suppressed, double aegisCpuPercent)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + Safe(game) + " | "
                    + PresetName(preset) + " | " + FormatDuration(duration) + " | "
                    + Lang.T(boostVerified ? "report.boost.ok" : "report.boost.missed") + " | "
                    + Lang.F("report.control", suppressed) + " | "
                    + Lang.F(
                        "report.aegis.cpu",
                        aegisCpuPercent.ToString(
                            "0.00", CultureInfo.InvariantCulture));
                lock (FileSync)
                {
                    string path = CurrentPathLocked(dataDir);
                    File.AppendAllText(
                        path, line + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch (Exception error)
            {
                Logger.LogFailure("会话报告写入失败", error);
            }
        }

        public static string ReadTail(string dataDir, int maxLines)
        {
            try
            {
                lock (FileSync)
                {
                    string path = CurrentPathLocked(dataDir);
                    if (!File.Exists(path)) return Lang.T("report.none");
                    string tail = ReadTailLocked(path, maxLines);
                    return tail.Length == 0 ? Lang.T("report.none") : tail;
                }
            }
            catch { return Lang.T("report.read.error"); }
        }

        // 磁盘格式是单行管道分隔，等宽字体下中文占双倍宽，整行远超报告框可用宽度。
        // 展示时拆成「时间 + 游戏」和缩进明细两行；无法解析的行原样保留。
        public static string FormatForDisplay(string tail)
        {
            if (string.IsNullOrEmpty(tail)) return tail;
            var display = new StringBuilder();
            foreach (string raw in tail.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                string[] fields = line.Split(new[] { " | " }, StringSplitOptions.None);
                if (fields.Length < 3) { display.AppendLine(line); continue; }
                display.AppendLine(fields[0] + "  " + fields[1]);
                display.Append("  ");
                for (int i = 2; i < fields.Length; i++)
                {
                    if (i > 2) display.Append(" | ");
                    display.Append(fields[i]);
                }
                display.AppendLine();
            }
            string text = display.ToString().TrimEnd('\r', '\n');
            return text.Length == 0 ? tail : text;
        }

        private static string CurrentPathLocked(string dataDir)
        {
            string path = Path.Combine(dataDir, FileName);
            if (MigrationChecked.Contains(path)) return path;
            MigrationChecked.Add(path);
            if (!File.Exists(path)) return path;
            try
            {
                bool legacyTelemetry = false;
                foreach (string line in File.ReadLines(path, Encoding.UTF8))
                    if (line.IndexOf("1% Low", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf(" FPS |", StringComparison.OrdinalIgnoreCase) >= 0)
                    { legacyTelemetry = true; break; }
                if (!legacyTelemetry) return path;
                string backup = path + ".telemetry.bak";
                if (File.Exists(backup)) backup = path + ".telemetry-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak";
                File.Move(path, backup);
                Logger.Log("旧帧采样报告已归档：" + backup);
            }
            catch { }
            return path;
        }

        private static string ReadTailLocked(string path, int maxLines)
        {
            if (maxLines <= 0) return "";
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long start = 0;
                long length = stream.Length;
                if (length > 0)
                {
                    var buffer = new byte[4096];
                    int breaks = 0;
                    long cursor = length;
                    while (cursor > 0 && breaks < maxLines)
                    {
                        int take = (int)Math.Min(buffer.Length, cursor);
                        cursor -= take;
                        stream.Position = cursor;
                        int read = stream.Read(buffer, 0, take);
                        for (int i = read - 1; i >= 0; i--)
                        {
                            long absolute = cursor + i;
                            if (buffer[i] != (byte)'\n'
                                || absolute == length - 1)
                                continue;
                            breaks++;
                            if (breaks == maxLines)
                            {
                                start = absolute + 1;
                                break;
                            }
                        }
                    }
                }
                stream.Position = start;
                using (var reader = new StreamReader(
                    stream, Encoding.UTF8, true, 4096, false))
                    return reader.ReadToEnd().TrimEnd('\r', '\n');
            }
        }

        private static string PresetName(PerformancePreset preset)
        {
            return preset == PerformancePreset.Competitive ? Lang.T("preset.competitive")
                : (preset == PerformancePreset.Custom ? Lang.T("preset.custom") : Lang.T("preset.standard"));
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1) return (int)duration.TotalHours + "h" + duration.Minutes.ToString("00") + "m";
            if (duration.TotalMinutes >= 1) return duration.Minutes + "m" + duration.Seconds.ToString("00") + "s";
            return Math.Max(0, duration.Seconds) + "s";
        }

        private static string Safe(string value)
        {
            return (value ?? Lang.T("report.unknown.game")).Replace("|", "／").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
