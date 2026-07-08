// @author bdth 2074055628@qq.com
// 文件用途 记录游戏会话中的调度结果和资源变化

using System;
using System.Collections.Generic;
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
        private readonly Dictionary<int, string> repFrozen = new Dictionary<int, string>();
        private DateTime repStart;
        private string repGame;
        private bool repBoosted;

        public event Action<string> SessionEnded;

        private void ReportBegin(string game)
        {
            lock (sync)
            {
                repCpu.Clear();
                repCreation.Clear();
                repProc.Clear();
                repFrozen.Clear();
                repGame = game;
                repStart = DateTime.Now;
                repBoosted = false;
            }
        }

        private void ReportBoostVerified()
        {
            lock (sync) if (repGame != null) repBoosted = true;
        }

        private void ReportFreeze(int pid, string name, string reason)
        {
            lock (sync)
            {
                if (repGame != null) repFrozen[pid] = name + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");
            }
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
            Dictionary<int, string> frozen;
            string game;
            DateTime t0;
            bool boosted;
            lock (sync)
            {
                game = repGame;
                t0 = repStart;
                cpu = new Dictionary<int, long>(repCpu);
                names = new Dictionary<int, string>(repProc);
                creations = new Dictionary<int, long>(repCreation);
                frozen = new Dictionary<int, string>(repFrozen);
                boosted = repBoosted;
                repCpu.Clear();
                repCreation.Clear();
                repProc.Clear();
                repFrozen.Clear();
                repGame = null;
                repBoosted = false;
            }
            if (game == null) return;

            TimeSpan dur = DateTime.Now - t0;
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
            if (frozen.Count > 0) msg += Lang.F("rep.freeze", frozen.Count);
            Logger.Log("会话报告：" + msg);
            PerformancePreset usedPreset;
            lock (sync) usedPreset = preset;
            SessionReportStore.Append(dataDir, game, usedPreset, dur, boosted, cpu.Count, frozen.Count);

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

        public static void Append(string dataDir, string game, PerformancePreset preset, TimeSpan duration,
            bool boostVerified, int suppressed, int frozen)
        {
            try
            {
                string path = CurrentPath(dataDir);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + Safe(game) + " | "
                    + PresetName(preset) + " | " + FormatDuration(duration) + " | "
                    + Lang.T(boostVerified ? "report.boost.ok" : "report.boost.missed") + " | "
                    + Lang.F("report.control", suppressed, frozen);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        public static string ReadTail(string dataDir, int maxLines)
        {
            try
            {
                string path = CurrentPath(dataDir);
                if (!File.Exists(path)) return Lang.T("report.none");
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                int start = Math.Max(0, lines.Length - maxLines);
                return string.Join(Environment.NewLine, lines, start, lines.Length - start);
            }
            catch { return Lang.T("report.read.error"); }
        }

        private static string CurrentPath(string dataDir)
        {
            string path = Path.Combine(dataDir, FileName);
            if (!File.Exists(path)) return path;
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                bool legacyTelemetry = false;
                foreach (string line in lines)
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
