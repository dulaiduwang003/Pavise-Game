// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly Dictionary<int, long> repCpu = new Dictionary<int, long>();
        private readonly Dictionary<int, long> repCreation = new Dictionary<int, long>();
        private readonly Dictionary<int, string> repProc = new Dictionary<int, string>();
        private readonly Dictionary<int, long> repSealed = new Dictionary<int, long>();
        private long repStart;
        private string repGame;
        private long repPaviseCpuStart;
        private string repProfileId;

        public event Action<string> SessionEnded;

        internal const string LastSessionKey = "LastSessionBrief";

        public static string LastSessionBrief
        {
            get { return Settings.LoadStr(LastSessionKey, ""); }
        }

        public event Action<string> SessionBriefed;

        internal static bool SameReportedProfile(string sessionProfileId, string detectedProfileId)
        {
            return !string.IsNullOrEmpty(sessionProfileId)
                && !string.IsNullOrEmpty(detectedProfileId)
                && string.Equals(sessionProfileId, detectedProfileId, StringComparison.OrdinalIgnoreCase);
        }

        private void ReportBegin(string game)
        {
            BeginCacheWarmSession();
            ResetBoostIdentity();
            long paviseCpu = CurrentProcessCpuTicks();
            lock (sync)
            {
                repCpu.Clear();
                repCreation.Clear();
                repProc.Clear();
                repSealed.Clear();
                repGame = game;
                repStart = Stopwatch.GetTimestamp();
                repPaviseCpuStart = paviseCpu;
                repProfileId = activeDetection != null && activeDetection.Profile != null
                    ? activeDetection.Profile.Id : null;
            }
        }

        private void ReportUntrack(int pid)
        {
            lock (sync)
            {
                repCpu.Remove(pid);
                repCreation.Remove(pid);
                repProc.Remove(pid);
                repSealed.Remove(pid);
            }
        }

        private void ReportSeal(int pid)
        {
            long start, creation;
            lock (sync)
            {
                if (!repCpu.TryGetValue(pid, out start)) return;
                repCreation.TryGetValue(pid, out creation);
                repCpu.Remove(pid);
                repCreation.Remove(pid);
            }
            long now, nowCreation, delta = 0;
            if (CpuTicks(pid, out now, out nowCreation)
                && nowCreation == creation && now > start)
                delta = now - start;
            lock (sync)
            {
                long prev;
                repSealed.TryGetValue(pid, out prev);
                repSealed[pid] = prev + delta;
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
            Dictionary<int, long> used;
            string game;
            long t0;
            long paviseCpuStart;
            lock (sync)
            {
                game = repGame;
                t0 = repStart;
                cpu = new Dictionary<int, long>(repCpu);
                names = new Dictionary<int, string>(repProc);
                creations = new Dictionary<int, long>(repCreation);
                used = new Dictionary<int, long>(repSealed);
                paviseCpuStart = repPaviseCpuStart;
                repCpu.Clear();
                repCreation.Clear();
                repProc.Clear();
                repSealed.Clear();
                repGame = null;
                repPaviseCpuStart = 0;
                repProfileId = null;
            }
            if (game == null) return;

            TimeSpan dur = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - t0) / Stopwatch.Frequency);
            foreach (var kv in cpu)
            {
                long prev;
                if (!used.TryGetValue(kv.Key, out prev)) { prev = 0; used[kv.Key] = 0; }
                long now, creation;
                if (!CpuTicks(kv.Key, out now, out creation)) continue;
                long expectedCreation;
                if (!creations.TryGetValue(kv.Key, out expectedCreation) || creation != expectedCreation) continue;
                long d = now - kv.Value;
                if (d < 0) continue;
                used[kv.Key] = prev + d;
            }

            long total = 0, top = 0;
            string topName = null;
            foreach (var kv in used)
            {
                total += kv.Value;
                if (kv.Value > top)
                {
                    top = kv.Value;
                    string nm;
                    if (names.TryGetValue(kv.Key, out nm)) topName = nm;
                }
            }

            string msg = Lang.F("rep.done", game, FmtDur(dur), used.Count, FmtCpu(total));
            if (topName != null && top >= TimeSpan.TicksPerSecond)
                msg += Lang.F("rep.top", topName, FmtCpu(top));
            long paviseCpuEnd = CurrentProcessCpuTicks();
            long paviseCpuDelta = paviseCpuStart > 0
                && paviseCpuEnd >= paviseCpuStart
                ? paviseCpuEnd - paviseCpuStart : 0;
            double paviseCpuPercent = AverageCpuPercent(
                paviseCpuDelta, dur);
            msg += Lang.F(
                "rep.pavise.cpu",
                paviseCpuPercent.ToString("0.00", CultureInfo.InvariantCulture));
            Logger.Log(Lang.T("log.gamemodesession.1") + msg);

            if (dur.TotalSeconds >= 60)
            {
                string brief = Lang.F("rep.brief", game, FmtDur(dur), used.Count);
                try { Settings.SaveStr(LastSessionKey, brief); } catch { }
                var b = SessionBriefed;
                if (b != null) { try { b(brief); } catch { } }
                var h = SessionEnded;
                if (h != null) { try { h(msg); } catch { } }
            }
        }
    }
}
