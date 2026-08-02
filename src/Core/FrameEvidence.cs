// @author bdth 2074055628@qq.com
// 文件用途 汇聚目标游戏进程的逐帧时间并计算 1% Low 等统计

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal static class FrameEvidence
    {
        private const int MaxFrames = 1000000;
        private const long MaxIntervalUs = 1000000;

        private static readonly object sync = new object();
        private static EtwFrameTrace trace;
        private static List<int> intervalsUs = new List<int>();
        private static long lastQpc;
        private static long baseQpc;
        private static int targetPid;
        private static readonly FocusIntervalFilter focus = new FocusIntervalFilter();
        private static long focusCheckQpc;
        private static readonly PresentThreadTracker threads = new PresentThreadTracker();
        private static string threadProbeNote;
        private static int threadProbeDone;

        public static void NoteRendererPid(int pid)
        {
            NoteRendererPid(pid, null);
        }

        public static void NoteRendererPid(int pid, string rendererName)
        {
            bool probe;
            lock (sync)
            {
                if (targetPid != pid) { targetPid = pid; lastQpc = 0; baseQpc = 0; focusCheckQpc = 0; }
                probe = trace != null;
            }
            if (probe && Interlocked.Exchange(ref threadProbeDone, 1) == 0)
                QueueThreadProbe(pid, rendererName);
        }

        private static void QueueThreadProbe(int pid, string rendererName)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string note = null;
                try { note = ThreadAccessProbe.ProbeAndDescribe(pid, rendererName); }
                catch { }
                lock (sync) threadProbeNote = note;
            });
        }

        public static void Begin()
        {
            lock (sync)
            {
                if (trace != null) return;
                intervalsUs = new List<int>();
                lastQpc = 0;
                baseQpc = 0;
                focus.Reset();
                focusCheckQpc = 0;
                threads.Reset();
                threadProbeNote = null;
                Interlocked.Exchange(ref threadProbeDone, 0);
                var t = new EtwFrameTrace(OnPresent);
                if (t.Start()) trace = t;
            }
        }

        public static string Finish()
        {
            string note;
            return Finish(out note);
        }

        public static string Finish(out string threadNote)
        {
            EtwFrameTrace t;
            int[] snapshot;
            PresentThreadSummary threadSummary;
            bool haveThreads;
            string probeNote;
            long unfocusedUs;
            int unfocusedFrames;
            lock (sync)
            {
                t = trace;
                trace = null;
                snapshot = intervalsUs.ToArray();
                intervalsUs = new List<int>();
                lastQpc = 0;
                baseQpc = 0;
                unfocusedUs = focus.UnfocusedUs;
                unfocusedFrames = focus.UnfocusedFrames;
                focus.Reset();
                focusCheckQpc = 0;
                threads.Seal();
                haveThreads = threads.TryDescribe(out threadSummary);
                threads.Reset();
                probeNote = threadProbeNote;
                threadProbeNote = null;
            }
            if (t != null) t.Stop();
            threadNote = null;
            if (t != null)
            {
                string present = haveThreads ? PresentThreadTracker.Describe(threadSummary) : null;
                if (present != null && probeNote != null) threadNote = present + "，" + probeNote;
                else threadNote = present != null ? present : probeNote;
            }
            if (t == null) return null;
            if (snapshot.Length < 30)
                return unfocusedFrames >= 30
                    ? Lang.F("ev.fps.unfocused.only",
                        (unfocusedUs / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture))
                    : null;
            int warmupUs;
            int[] settled = ExcludeWarmup(snapshot, out warmupUs);
            int excludedUs;
            int[] gameplay = ExcludeLoadingClusters(settled, out excludedUs);
            if (gameplay.Length < 30) gameplay = snapshot;
            double avgFps, low1, low01;
            if (!ComputeStats(gameplay, out avgFps, out low1, out low01)) return null;
            string frames = Lang.F("ev.fps.frames", gameplay.Length);
            if (warmupUs > 0)
                frames += Lang.F("ev.fps.warmup",
                    (warmupUs / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture));
            if (excludedUs > 0)
                frames += Lang.F("ev.fps.excluded",
                    (excludedUs / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture));
            if (unfocusedUs > 0)
            {
                long sessionUs = unfocusedUs + excludedUs;
                foreach (int us in snapshot) sessionUs += us;
                int pct = sessionUs > 0 ? (int)(unfocusedUs * 100 / sessionUs) : 0;
                frames += Lang.F("ev.fps.unfocused",
                    (unfocusedUs / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture), pct);
            }
            return Lang.F("ev.fps",
                avgFps.ToString("0", CultureInfo.InvariantCulture),
                low1.ToString("0", CultureInfo.InvariantCulture),
                low01.ToString("0", CultureInfo.InvariantCulture),
                frames);
        }

        // 开局预热窗口。加载画面、进图后的着色器编译会产出大量 50~240ms 的帧，
        // 它们够慢到污染 0.1%Low，却够不上加载簇的 250ms 门槛，于是原样计入统计。
        // 更糟的是这种污染在两次对照之间不对称：一局的卡顿刚好跨过门槛被剔掉、
        // 另一局刚好没跨过，量出来的差异就完全是量具造成的。统一切掉开局这段。
        private const int WarmupUs = 60 * 1000000;

        internal static int[] ExcludeWarmup(int[] intervalsUs, out int warmupUs)
        {
            warmupUs = 0;
            if (intervalsUs == null || intervalsUs.Length == 0) return new int[0];
            long elapsed = 0;
            int cut = 0;
            while (cut < intervalsUs.Length && elapsed < WarmupUs)
            {
                elapsed += intervalsUs[cut];
                cut++;
            }
            // 整局都没超过预热窗口：短会话宁可保留全部样本，也不要清空后无据可依
            if (cut >= intervalsUs.Length) return intervalsUs;
            warmupUs = (int)Math.Min(elapsed, int.MaxValue);
            var rest = new int[intervalsUs.Length - cut];
            Array.Copy(intervalsUs, cut, rest, 0, rest.Length);
            return rest;
        }

        private const int ClusterThresholdUs = 250000;
        private const int ClusterMinRun = 3;

        internal static int[] ExcludeLoadingClusters(int[] intervalsUs, out int excludedUs)
        {
            excludedUs = 0;
            if (intervalsUs == null || intervalsUs.Length == 0) return new int[0];
            var keep = new bool[intervalsUs.Length];
            for (int i = 0; i < keep.Length; i++) keep[i] = true;
            int runStart = -1;
            for (int i = 0; i <= intervalsUs.Length; i++)
            {
                bool over = i < intervalsUs.Length && intervalsUs[i] >= ClusterThresholdUs;
                if (over && runStart < 0) runStart = i;
                else if (!over && runStart >= 0)
                {
                    if (i - runStart >= ClusterMinRun)
                        for (int k = runStart; k < i; k++) keep[k] = false;
                    runStart = -1;
                }
            }
            var result = new List<int>(intervalsUs.Length);
            long excluded = 0;
            for (int i = 0; i < intervalsUs.Length; i++)
            {
                if (keep[i]) result.Add(intervalsUs[i]);
                else excluded += intervalsUs[i];
            }
            excludedUs = (int)Math.Min(excluded, int.MaxValue);
            return result.ToArray();
        }

        private static void OnPresent(int pid, int tid, long qpc)
        {
            lock (sync)
            {
                if (pid != targetPid) return;
                if (focusCheckQpc == 0 || qpc - focusCheckQpc >= Stopwatch.Frequency / 10)
                {
                    focusCheckQpc = qpc;
                    focus.NoteFocus(GameSessionDetector.ForegroundPid() == pid);
                }
                if (baseQpc == 0) baseQpc = qpc;
                if (qpc >= baseQpc)
                    threads.Add(tid, (qpc - baseQpc) * 1000000 / Stopwatch.Frequency);
                if (lastQpc != 0 && qpc > lastQpc)
                {
                    long us = (qpc - lastQpc) * 1000000 / Stopwatch.Frequency;
                    if (us > 0 && us <= MaxIntervalUs && intervalsUs.Count < MaxFrames
                        && focus.Admit((int)us))
                        intervalsUs.Add((int)us);
                }
                lastQpc = qpc;
            }
        }

        internal static bool ComputeStats(int[] intervalsUs, out double avgFps, out double low1Fps, out double low01Fps)
        {
            avgFps = 0; low1Fps = 0; low01Fps = 0;
            if (intervalsUs == null || intervalsUs.Length == 0) return false;
            long sum = 0;
            foreach (int us in intervalsUs) sum += us;
            if (sum <= 0) return false;
            avgFps = intervalsUs.Length * 1000000.0 / sum;
            var sorted = (int[])intervalsUs.Clone();
            Array.Sort(sorted);
            Array.Reverse(sorted);
            low1Fps = TailFps(sorted, 0.01);
            low01Fps = TailFps(sorted, 0.001);
            return true;
        }

        private static double TailFps(int[] sortedDescUs, double fraction)
        {
            int take = Math.Max(1, (int)(sortedDescUs.Length * fraction));
            long sum = 0;
            for (int i = 0; i < take; i++) sum += sortedDescUs[i];
            return sum <= 0 ? 0 : take * 1000000.0 / sum;
        }
    }
}
