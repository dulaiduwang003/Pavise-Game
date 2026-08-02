// @author bdth 2074055628@qq.com
// 文件用途 识别游戏的帧关键线程并单独抬高其调度权重 一次识别 全程不再轮询

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

        private static readonly object sync = new object();
        private static int lanePid;
        private static long laneCreation;
        private static int laneTid;
        private static int laneOriginalPriority;
        private static bool laneApplied;
        private static int triedPid;
        private static long triedCreation;

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

        public static void EnsureForGame(int pid, long creation, string gameName)
        {
            lock (sync)
            {
                if (laneApplied && lanePid == pid && laneCreation == creation) return;
                if (triedPid == pid && triedCreation == creation) return;
                triedPid = pid; triedCreation = creation;
            }
            Candidate best;
            if (!TryIdentify(pid, out best))
            {
                Logger.Log("渲染主权域：无法采样 " + (gameName ?? "?") + " 的线程，本局跳过");
                return;
            }
            if (best.Share < MinDominantShare)
            {
                Logger.Log("渲染主权域：" + (gameName ?? "?") + " 的负载分散在多线程（主导仅 "
                    + (best.Share * 100).ToString("F0") + "%，共 " + best.ThreadCount
                    + " 线程），没有单一帧关键路径，本局不介入");
                return;
            }
            IntPtr h = Native.OpenThread(
                Native.THREAD_SET_LIMITED_INFORMATION | Native.THREAD_QUERY_LIMITED_INFORMATION,
                false, best.Tid);
            if (h == IntPtr.Zero)
            {
                Logger.Log("渲染主权域：线程写句柄被拒（多半被反作弊保护），本局跳过");
                return;
            }
            try
            {
                int original = Native.GetThreadPriority(h);
                if (original == Native.THREAD_PRIORITY_ERROR_RETURN)
                {
                    Logger.Log("渲染主权域：读不到线程原优先级，未做任何写入");
                    return;
                }
                if (original >= Native.THREAD_PRIORITY_ABOVE_NORMAL)
                {
                    Logger.Log("渲染主权域：" + (gameName ?? "?") + " 的帧关键线程已自带高于常规的权重，无需介入");
                    return;
                }
                if (!SaveJournal(pid, creation, best.Tid, original))
                {
                    Logger.Log("渲染主权域：记账无法持久化，已放弃写入");
                    return;
                }
                if (!Native.SetThreadPriority(h, Native.THREAD_PRIORITY_ABOVE_NORMAL))
                {
                    ClearJournal();
                    Logger.Log("渲染主权域：写入线程优先级失败，已清账");
                    return;
                }
                int actual = Native.GetThreadPriority(h);
                if (actual != Native.THREAD_PRIORITY_ABOVE_NORMAL)
                {
                    Native.SetThreadPriority(h, original);
                    ClearJournal();
                    Logger.Log("渲染主权域：回读不符（" + actual + "），已还原");
                    return;
                }
                lock (sync)
                {
                    lanePid = pid; laneCreation = creation;
                    laneTid = best.Tid; laneOriginalPriority = original; laneApplied = true;
                }
                Logger.Log("渲染主权域已建立：" + (gameName ?? "?") + " 帧关键线程 " + best.Tid
                    + "（占进程 CPU " + (best.Share * 100).ToString("F0") + "%，共 " + best.ThreadCount
                    + " 线程）优先级 " + original + " → " + Native.THREAD_PRIORITY_ABOVE_NORMAL);
            }
            finally { Native.CloseHandle(h); }
        }

        public static bool Release()
        {
            int pid, tid, original;
            long creation;
            lock (sync)
            {
                if (!laneApplied) { ClearJournal(); return true; }
                pid = lanePid; creation = laneCreation; tid = laneTid; original = laneOriginalPriority;
            }
            bool ok = RestoreThread(pid, creation, tid, original);
            if (ok)
            {
                lock (sync)
                {
                    laneApplied = false; lanePid = 0; laneCreation = 0; laneTid = 0;
                    triedPid = 0; triedCreation = 0;
                }
                ClearJournal();
                Logger.Log("渲染主权域已撤销：线程 " + tid + " 优先级还原为 " + original);
            }
            else Logger.Log("渲染主权域：线程 " + tid + " 还原失败，记账保留待下次重试");
            return ok;
        }

        public static void HealFromCrash()
        {
            string raw = Settings.LoadStr("RenderLane", "");
            int pid, tid, original;
            long creation;
            if (!ParseJournal(raw, out pid, out creation, out tid, out original)) { ClearJournal(); return; }
            if (RestoreThread(pid, creation, tid, original))
            {
                ClearJournal();
                Logger.Log("渲染主权域：崩溃前的线程 " + tid + " 优先级已还原为 " + original);
            }
        }

        private static bool RestoreThread(int pid, long creation, int tid, int original)
        {
            try
            {
                using (Process target = Process.GetProcessById(pid))
                {
                    if (creation > 0 && target.StartTime.ToUniversalTime().Ticks != creation) return true;
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
