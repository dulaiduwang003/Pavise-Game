// @author bdth 2074055628@qq.com
// 文件用途 识别游戏的帧关键线程并单独抬高其调度权重 独立线程连续采样 钉后定期复核
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal enum LaneState
    {
        Idle = 0,
        Trying = 1,
        Engaged = 2,
        Unavailable = 3
    }

    internal enum LaneDecision
    {
        None = 0,
        Pin = 1,
        Unpin = 2
    }

    internal sealed class LaneJudge
    {
        public const int ConfirmWins = 3;
        public const int UnpinStrikes = 2;
        public const int MaxUnpins = 2;
        public const double ReverifyHealthyShare = 0.15;

        private int candidateTid;
        private int streak;
        private int pinnedTid;
        private int strikes;
        private int unpins;

        public int PinnedTid { get { return pinnedTid; } }
        public bool GaveUp { get { return unpins >= MaxUnpins; } }

        public LaneDecision Observe(int winnerTid, double winnerShare, double pinnedShare)
        {
            if (pinnedTid == 0)
            {
                if (winnerTid <= 0 || winnerShare < RenderLane.MinDominantShare)
                { candidateTid = 0; streak = 0; return LaneDecision.None; }
                if (winnerTid == candidateTid) streak++;
                else { candidateTid = winnerTid; streak = 1; }
                if (streak < ConfirmWins) return LaneDecision.None;
                pinnedTid = winnerTid; candidateTid = 0; streak = 0; strikes = 0;
                return LaneDecision.Pin;
            }
            if (pinnedShare >= ReverifyHealthyShare) { strikes = 0; return LaneDecision.None; }
            if (winnerTid == pinnedTid || winnerShare < RenderLane.MinDominantShare)
                return LaneDecision.None;
            if (++strikes < UnpinStrikes) return LaneDecision.None;
            pinnedTid = 0; strikes = 0; unpins++;
            return LaneDecision.Unpin;
        }

        public void PinFailed() { pinnedTid = 0; candidateTid = 0; streak = 0; }
    }

    internal static class RenderLane
    {
        internal const double MinDominantShare = 0.35;
        private const int SampleGapMs = 800;
        private const int MaxThreads = 512;
        private const int BatchWindows = 6;
        internal const int MaxIdentifyTries = 5;
        private const int RetryGapSeconds = 60;
        private const int ReverifyGapSeconds = 30;

        private static readonly object sync = new object();
        private static Action mutationBegin;
        private static Action mutationEnd;
        private static int lanePid;
        private static long laneCreation;
        private static int laneTid;
        private static int laneOriginalPriority;
        private static bool laneApplied;
        private static int laneGen;
        private static int trackPid;
        private static long trackCreation;
        private static int trackGen;
        private static int trackBatch;
        private static int gaveUpPid;
        private static long gaveUpCreation;

        public static void ConfigureMutationBoundary(Action begin, Action end)
        {
            lock (sync)
            {
                mutationBegin = begin;
                mutationEnd = end;
            }
        }

        private static void BeginMutation()
        {
            Action callback;
            lock (sync) callback = mutationBegin;
            if (callback != null) try { callback(); } catch { }
        }

        private static void EndMutation()
        {
            Action callback;
            lock (sync) callback = mutationEnd;
            if (callback != null) try { callback(); } catch { }
        }

        internal struct Candidate
        {
            public int Tid;
            public double Share;
            public int ThreadCount;
        }

        internal static bool TryIdentify(int pid, out Candidate best)
        {
            double ignore;
            return TryIdentify(pid, 0, out best, out ignore);
        }

        internal static bool TryIdentify(int pid, int watchTid, out Candidate best, out double watchShare)
        {
            best = new Candidate();
            watchShare = 0;
            var first = new Dictionary<int, long>();
            if (!SampleThreads(pid, first)) return false;
            Thread.Sleep(SampleGapMs);
            var second = new Dictionary<int, long>();
            if (!SampleThreads(pid, second)) return false;

            long total = 0, bestDelta = -1, watchDelta = 0;
            int bestTid = 0;
            foreach (KeyValuePair<int, long> kv in second)
            {
                long before;
                if (!first.TryGetValue(kv.Key, out before)) continue;
                long delta = kv.Value - before;
                if (delta <= 0) continue;
                total += delta;
                if (delta > bestDelta) { bestDelta = delta; bestTid = kv.Key; }
                if (watchTid != 0 && kv.Key == watchTid) watchDelta = delta;
            }
            if (bestTid == 0 || total <= 0) return false;
            best.Tid = bestTid;
            best.Share = bestDelta / (double)total;
            best.ThreadCount = second.Count;
            if (watchTid != 0) watchShare = watchDelta / (double)total;
            return true;
        }

        private static bool SampleThreads(int pid, Dictionary<int, long> into)
        {
            try
            {
                using (Process target = Process.GetProcessById(pid))
                {
                    ProcessThreadCollection threads = target.Threads;
                    int seen = 0;
                    foreach (ProcessThread t in threads)
                    {
                        if (++seen > MaxThreads) break;
                        IntPtr h = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, t.Id);
                        if (h == IntPtr.Zero) continue;
                        try
                        {
                            long c, e, k, u;
                            if (Native.GetThreadTimes(h, out c, out e, out k, out u)) into[t.Id] = k + u;
                        }
                        finally { Native.CloseHandle(h); }
                    }
                    return into.Count > 0;
                }
            }
            catch { return false; }
        }

        public static bool IsActiveFor(int pid, long creation)
        {
            lock (sync) return laneApplied && lanePid == pid && laneCreation == creation;
        }

        public static LaneState StateFor(int pid, long creation)
        {
            lock (sync)
            {
                if (laneApplied && lanePid == pid && laneCreation == creation) return LaneState.Engaged;
                if (gaveUpPid == pid && gaveUpCreation == creation) return LaneState.Unavailable;
                if (trackPid == pid && trackCreation == creation) return LaneState.Trying;
                return LaneState.Idle;
            }
        }

        public static void EnsureForGame(int pid, long creation, string gameName)
        {
            Thread worker;
            lock (sync)
            {
                if (pid <= 0) return;
                if (laneApplied && lanePid == pid && laneCreation == creation) return;
                if (gaveUpPid == pid && gaveUpCreation == creation) return;
                if (trackPid == pid && trackCreation == creation) return;
                laneGen++;
                int gen = laneGen;
                trackPid = pid; trackCreation = creation; trackGen = gen; trackBatch = 0;
                worker = new Thread(delegate () { Work(pid, creation, gameName, gen); });
                worker.IsBackground = true;
                worker.Name = "Pavise.RenderLane";
                worker.Priority = ThreadPriority.BelowNormal;
            }
            worker.Start();
        }

        private static bool GenAlive(int gen) { lock (sync) return gen == laneGen; }

        private static bool WaitAlive(int ms, int gen)
        {
            int slept = 0;
            while (slept < ms)
            {
                int step = Math.Min(2000, ms - slept);
                Thread.Sleep(step);
                slept += step;
                if (!GenAlive(gen)) return false;
            }
            return true;
        }

        private static void Work(int pid, long creation, string gameName, int gen)
        {
            try { WorkLoop(pid, creation, gameName, gen); }
            catch { }
            finally
            {
                lock (sync)
                    if (trackGen == gen) { trackPid = 0; trackCreation = 0; trackGen = 0; }
            }
        }

        private static void WorkLoop(int pid, long creation, string gameName, int gen)
        {
            var judge = new LaneJudge();
            int batch = 0;
            while (GenAlive(gen))
            {
                bool pinned;
                lock (sync) pinned = laneApplied && lanePid == pid && laneCreation == creation;
                if (!pinned)
                {
                    batch++;
                    lock (sync) if (trackGen == gen) trackBatch = batch;
                    bool logThis = batch == 1 || batch >= MaxIdentifyTries;
                    if (!IdentifyBatch(pid, creation, gameName, gen, judge, logThis, out pinned))
                        return;
                    if (pinned) continue;
                    if (judge.GaveUp) { GiveUp(pid, creation); return; }
                    if (batch >= MaxIdentifyTries) { GiveUp(pid, creation); return; }
                    if (!WaitAlive(RetryGapSeconds * 1000, gen)) return;
                }
                else
                {
                    if (!WaitAlive(ReverifyGapSeconds * 1000, gen)) return;
                    if (!VerifyOnce(pid, creation, gameName, gen, judge)) return;
                }
            }
        }

        private static bool IdentifyBatch(int pid, long creation, string gameName, int gen,
            LaneJudge judge, bool logThis, out bool pinnedNow)
        {
            pinnedNow = false;
            int sampleFails = 0;
            Candidate last = new Candidate();
            bool sawSpread = false;
            for (int w = 0; w < BatchWindows; w++)
            {
                if (!GenAlive(gen)) return false;
                Candidate best;
                double ignore;
                if (!TryIdentify(pid, 0, out best, out ignore)) { sampleFails++; continue; }
                last = best;
                if (best.Share < MinDominantShare) sawSpread = true;
                if (judge.Observe(best.Tid, best.Share, 0) != LaneDecision.Pin) continue;
                PinOutcome outcome = TryPin(pid, creation, best, gameName, gen, logThis);
                if (outcome == PinOutcome.Pinned) { pinnedNow = true; return true; }
                if (outcome == PinOutcome.Fatal) { GiveUp(pid, creation); return false; }
                if (outcome == PinOutcome.Canceled) return false;
                judge.PinFailed();
            }
            if (logThis)
            {
                if (sampleFails >= BatchWindows)
                    Logger.Log(Lang.T("log.renderlane.1") + (gameName ?? "?") + " pid " + pid
                        + Lang.T("log.renderlane.2"));
                else if (sawSpread || last.Tid != 0)
                    Logger.Log(Lang.T("log.renderlane.3") + (gameName ?? "?") + Lang.T("log.renderlane.4")
                        + (last.Share * 100).ToString("F0") + Lang.T("log.renderlane.5") + last.ThreadCount
                        + Lang.T("log.renderlane.6"));
            }
            return true;
        }

        private enum PinOutcome { Pinned = 0, Retryable = 1, Fatal = 2, Canceled = 3 }

        private static PinOutcome TryPin(int pid, long creation, Candidate best, string gameName,
            int gen, bool logThis)
        {
            IntPtr h = Native.OpenThread(
                Native.THREAD_SET_LIMITED_INFORMATION | Native.THREAD_QUERY_LIMITED_INFORMATION,
                false, best.Tid);
            if (h == IntPtr.Zero)
            {
                if (logThis) Logger.Log(Lang.T("log.renderlane.7"));
                return PinOutcome.Fatal;
            }
            try
            {
                int original = Native.GetThreadPriority(h);
                if (original == Native.THREAD_PRIORITY_ERROR_RETURN)
                {
                    if (logThis) Logger.Log(Lang.T("log.renderlane.8"));
                    return PinOutcome.Retryable;
                }
                if (original >= Native.THREAD_PRIORITY_HIGHEST)
                {
                    if (logThis) Logger.Log(Lang.T("log.renderlane.3") + (gameName ?? "?") + Lang.T("log.renderlane.9"));
                    return PinOutcome.Fatal;
                }
                BeginMutation();
                try
                {
                    if (!SaveJournal(pid, creation, best.Tid, original))
                    {
                        if (logThis) Logger.Log(Lang.T("log.renderlane.10"));
                        return PinOutcome.Retryable;
                    }
                    if (!Native.SetThreadPriority(h, Native.THREAD_PRIORITY_HIGHEST))
                    {
                        ClearJournal();
                        if (logThis) Logger.Log(Lang.T("log.renderlane.11"));
                        return PinOutcome.Retryable;
                    }
                    int actual = Native.GetThreadPriority(h);
                    if (actual != Native.THREAD_PRIORITY_HIGHEST)
                    {
                        Native.SetThreadPriority(h, original);
                        ClearJournal();
                        if (logThis) Logger.Log(Lang.T("log.renderlane.12") + actual + Lang.T("log.renderlane.13"));
                        return PinOutcome.Retryable;
                    }
                    bool canceled = false;
                    lock (sync)
                    {
                        if (gen != laneGen) canceled = true;
                        else
                        {
                            lanePid = pid; laneCreation = creation;
                            laneTid = best.Tid; laneOriginalPriority = original; laneApplied = true;
                        }
                    }
                    if (canceled)
                    {
                        Native.SetThreadPriority(h, original);
                        ClearJournal();
                        if (logThis) Logger.Log(Lang.T("log.renderlane.14"));
                        return PinOutcome.Canceled;
                    }
                    Logger.Log(Lang.T("log.renderlane.15") + (gameName ?? "?") + Lang.T("log.renderlane.16") + best.Tid
                        + Lang.T("log.renderlane.17") + (best.Share * 100).ToString("F0") + Lang.T("log.renderlane.5") + best.ThreadCount
                        + Lang.T("log.renderlane.18") + original + " " + Native.THREAD_PRIORITY_HIGHEST);
                    return PinOutcome.Pinned;
                }
                finally { EndMutation(); }
            }
            finally { Native.CloseHandle(h); }
        }

        private static bool VerifyOnce(int pid, long creation, string gameName, int gen, LaneJudge judge)
        {
            int tid;
            int original;
            lock (sync)
            {
                if (!laneApplied || lanePid != pid || laneCreation != creation) return true;
                tid = laneTid; original = laneOriginalPriority;
            }
            Candidate best;
            double pinnedShare;
            if (!TryIdentify(pid, tid, out best, out pinnedShare))
                return ProcessAlive(pid, creation);
            if (judge.Observe(best.Tid, best.Share, pinnedShare) != LaneDecision.Unpin) return true;

            if (!GenAlive(gen)) return false;
            BeginMutation();
            try
            {
                if (!RestoreThread(pid, creation, tid, original))
                {
                    Logger.Log(Lang.T("log.renderlane.21") + tid + Lang.T("log.renderlane.22"));
                    GiveUp(pid, creation);
                    return false;
                }
                lock (sync)
                {
                    if (gen == laneGen && laneApplied && lanePid == pid && laneCreation == creation)
                    { laneApplied = false; lanePid = 0; laneCreation = 0; laneTid = 0; }
                }
                ClearJournal();
                Logger.Log(Lang.T("log.renderlane.25") + tid + Lang.T("log.renderlane.26") + best.Tid
                    + Lang.T("log.renderlane.17") + (best.Share * 100).ToString("F0") + "%");
                if (judge.GaveUp)
                {
                    Logger.Log(Lang.T("log.renderlane.27") + (gameName ?? "?") + Lang.T("log.renderlane.28"));
                    GiveUp(pid, creation);
                    return false;
                }
                return true;
            }
            finally { EndMutation(); }
        }

        private static bool ProcessAlive(int pid, long creation)
        {
            try
            {
                using (Process target = Process.GetProcessById(pid))
                    return creation <= 0 || target.StartTime.ToFileTimeUtc() == creation;
            }
            catch { return false; }
        }

        private static void GiveUp(int pid, long creation)
        {
            lock (sync) { gaveUpPid = pid; gaveUpCreation = creation; }
        }

        public static bool Release()
        {
            int pid, tid, original;
            long creation;
            lock (sync)
            {
                laneGen++;
                gaveUpPid = 0; gaveUpCreation = 0;
                if (!laneApplied) { ClearJournal(); return true; }
                pid = lanePid; creation = laneCreation; tid = laneTid; original = laneOriginalPriority;
            }
            BeginMutation();
            try
            {
                bool ok = RestoreThread(pid, creation, tid, original);
                if (ok)
                {
                    lock (sync)
                    {
                        laneApplied = false; lanePid = 0; laneCreation = 0; laneTid = 0;
                    }
                    ClearJournal();
                    Logger.Log(Lang.T("log.renderlane.19") + tid + Lang.T("log.renderlane.20") + original);
                }
                else Logger.Log(Lang.T("log.renderlane.21") + tid + Lang.T("log.renderlane.22"));
                return ok;
            }
            finally { EndMutation(); }
        }

        public static bool HasResidue() { return Settings.LoadStr("RenderLane", "").Length > 0; }

        public static void HealFromCrash()
        {
            string raw = Settings.LoadStr("RenderLane", "");
            int pid, tid, original;
            long creation;
            if (!ParseJournal(raw, out pid, out creation, out tid, out original)) { ClearJournal(); return; }
            BeginMutation();
            try
            {
                if (RestoreThread(pid, creation, tid, original))
                {
                    ClearJournal();
                    Logger.Log(Lang.T("log.renderlane.23") + tid + Lang.T("log.renderlane.24") + original);
                }
            }
            finally { EndMutation(); }
        }

        private static bool RestoreThread(int pid, long creation, int tid, int original)
        {
            try
            {
                using (Process target = Process.GetProcessById(pid))
                {
                    if (creation > 0 && target.StartTime.ToFileTimeUtc() != creation) return true;
                }
            }
            catch { return true; }
            IntPtr h = Native.OpenThread(
                Native.THREAD_SET_LIMITED_INFORMATION | Native.THREAD_QUERY_LIMITED_INFORMATION, false, tid);
            if (h == IntPtr.Zero) return true;
            try
            {
                if (!Native.SetThreadPriority(h, original)) return false;
                int actual = Native.GetThreadPriority(h);
                return actual == original || actual == Native.THREAD_PRIORITY_ERROR_RETURN;
            }
            finally { Native.CloseHandle(h); }
        }

        private static bool SaveJournal(int pid, long creation, int tid, int original)
        {
            string line = pid + "|" + creation + "|" + tid + "|" + original;
            return Settings.SaveStr("RenderLane", line) && Settings.LoadStr("RenderLane", "") == line;
        }

        private static void ClearJournal() { Settings.SaveStr("RenderLane", ""); }

        internal static bool ParseJournal(string raw, out int pid, out long creation, out int tid, out int original)
        {
            pid = 0; creation = 0; tid = 0; original = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            string[] parts = raw.Split('|');
            return parts.Length == 4
                && int.TryParse(parts[0], out pid) && pid > 0
                && long.TryParse(parts[1], out creation)
                && int.TryParse(parts[2], out tid) && tid > 0
                && int.TryParse(parts[3], out original);
        }
    }
}
