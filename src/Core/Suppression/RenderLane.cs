// @author bdth 2074055628@qq.com
// 文件用途 识别游戏的帧关键线程并单独抬高其调度权重 识别失败限次限频重试 成功后全程不再轮询

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
    internal static class RenderLane
    {
        internal const double MinDominantShare = 0.35;
        private const int SampleGapMs = 800;
        private const int MaxThreads = 512;
        // 加载期解压/编译并行度高 常无主导线程 进入正局后重试更容易认准 限次限频防止无界枚举
        private const int MaxIdentifyTries = 5;
        private const int RetryGapSeconds = 60;

        private static readonly object sync = new object();
        private static int lanePid;
        private static long laneCreation;
        private static int laneTid;
        private static int laneOriginalPriority;
        private static bool laneApplied;
        private static int triedPid;
        private static long triedCreation;
        private static int tryCount;
        private static long nextTryTicks;

        internal struct Candidate
        {
            public int Tid;
            public double Share;
            public int ThreadCount;
        }

        internal static bool TryIdentify(int pid, out Candidate best)
        {
            best = new Candidate();
            var first = new Dictionary<int, long>();
            if (!SampleThreads(pid, first)) return false;
            System.Threading.Thread.Sleep(SampleGapMs);
            var second = new Dictionary<int, long>();
            if (!SampleThreads(pid, second)) return false;

            long total = 0, bestDelta = -1;
            int bestTid = 0;
            foreach (KeyValuePair<int, long> kv in second)
            {
                long before;
                if (!first.TryGetValue(kv.Key, out before)) continue;
                long delta = kv.Value - before;
                if (delta <= 0) continue;
                total += delta;
                if (delta > bestDelta) { bestDelta = delta; bestTid = kv.Key; }
            }
            if (bestTid == 0 || total <= 0) return false;
            best.Tid = bestTid;
            best.Share = bestDelta / (double)total;
            best.ThreadCount = second.Count;
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

        // 结论不会随重试改变的终态直接耗尽额度 免掉后续每次 800ms 采样与句柄打开
        private static void ExhaustTries(int pid, long creation)
        {
            lock (sync)
                if (triedPid == pid && triedCreation == creation) tryCount = MaxIdentifyTries;
        }

        private static int laneGen;

        public static void EnsureForGame(int pid, long creation, string gameName)
        {
            int gen;
            bool logThis;
            lock (sync)
            {
                if (laneApplied && lanePid == pid && laneCreation == creation) return;
                if (triedPid == pid && triedCreation == creation)
                {
                    if (tryCount >= MaxIdentifyTries || DateTime.UtcNow.Ticks < nextTryTicks) return;
                }
                else { triedPid = pid; triedCreation = creation; tryCount = 0; }
                tryCount++;
                nextTryTicks = DateTime.UtcNow.AddSeconds(RetryGapSeconds).Ticks;
                logThis = tryCount == 1 || tryCount >= MaxIdentifyTries;
                gen = laneGen;
            }
            Candidate best;
            if (!TryIdentify(pid, out best))
            {
                if (logThis) Logger.Log(Lang.T("log.renderlane.1") + (gameName ?? "?") + " pid " + pid
                    + Lang.T("log.renderlane.2"));
                return;
            }
            if (best.Share < MinDominantShare)
            {
                if (logThis) Logger.Log(Lang.T("log.renderlane.3") + (gameName ?? "?") + Lang.T("log.renderlane.4")
                    + (best.Share * 100).ToString("F0") + Lang.T("log.renderlane.5") + best.ThreadCount
                    + Lang.T("log.renderlane.6"));
                return;
            }
            IntPtr h = Native.OpenThread(
                Native.THREAD_SET_LIMITED_INFORMATION | Native.THREAD_QUERY_LIMITED_INFORMATION,
                false, best.Tid);
            if (h == IntPtr.Zero)
            {
                // 反作弊拒写句柄不会随重试改变 耗尽额度 避免每 60 秒对受保护进程重开写句柄
                ExhaustTries(pid, creation);
                if (logThis) Logger.Log(Lang.T("log.renderlane.7"));
                return;
            }
            try
            {
                int original = Native.GetThreadPriority(h);
                if (original == Native.THREAD_PRIORITY_ERROR_RETURN)
                {
                    if (logThis) Logger.Log(Lang.T("log.renderlane.8"));
                    return;
                }
                if (original >= Native.THREAD_PRIORITY_HIGHEST)
                {
                    // 线程已是最高权重属终态 重试不会改变结论
                    ExhaustTries(pid, creation);
                    if (logThis) Logger.Log(Lang.T("log.renderlane.3") + (gameName ?? "?") + Lang.T("log.renderlane.9"));
                    return;
                }
                if (!SaveJournal(pid, creation, best.Tid, original))
                {
                    if (logThis) Logger.Log(Lang.T("log.renderlane.10"));
                    return;
                }
                if (!Native.SetThreadPriority(h, Native.THREAD_PRIORITY_HIGHEST))
                {
                    ClearJournal();
                    if (logThis) Logger.Log(Lang.T("log.renderlane.11"));
                    return;
                }
                int actual = Native.GetThreadPriority(h);
                if (actual != Native.THREAD_PRIORITY_HIGHEST)
                {
                    Native.SetThreadPriority(h, original);
                    ClearJournal();
                    if (logThis) Logger.Log(Lang.T("log.renderlane.12") + actual + Lang.T("log.renderlane.13"));
                    return;
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
                    return;
                }
                Logger.Log(Lang.T("log.renderlane.15") + (gameName ?? "?") + Lang.T("log.renderlane.16") + best.Tid
                    + Lang.T("log.renderlane.17") + (best.Share * 100).ToString("F0") + Lang.T("log.renderlane.5") + best.ThreadCount
                    + Lang.T("log.renderlane.18") + original + " " + Native.THREAD_PRIORITY_HIGHEST);
            }
            finally { Native.CloseHandle(h); }
        }

        public static bool Release()
        {
            int pid, tid, original;
            long creation;
            lock (sync)
            {
                laneGen++;
                if (!laneApplied) { ClearJournal(); return true; }
                pid = lanePid; creation = laneCreation; tid = laneTid; original = laneOriginalPriority;
            }
            bool ok = RestoreThread(pid, creation, tid, original);
            if (ok)
            {
                lock (sync)
                {
                    laneApplied = false; lanePid = 0; laneCreation = 0; laneTid = 0;
                    triedPid = 0; triedCreation = 0; tryCount = 0; nextTryTicks = 0;
                }
                ClearJournal();
                Logger.Log(Lang.T("log.renderlane.19") + tid + Lang.T("log.renderlane.20") + original);
            }
            else Logger.Log(Lang.T("log.renderlane.21") + tid + Lang.T("log.renderlane.22"));
            return ok;
        }

        public static bool HasResidue() { return Settings.LoadStr("RenderLane", "").Length > 0; }

        public static void HealFromCrash()
        {
            string raw = Settings.LoadStr("RenderLane", "");
            int pid, tid, original;
            long creation;
            if (!ParseJournal(raw, out pid, out creation, out tid, out original)) { ClearJournal(); return; }
            if (RestoreThread(pid, creation, tid, original))
            {
                ClearJournal();
                Logger.Log(Lang.T("log.renderlane.23") + tid + Lang.T("log.renderlane.24") + original);
            }
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
