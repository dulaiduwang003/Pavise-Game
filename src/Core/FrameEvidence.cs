// @author bdth 2074055628@qq.com
// 文件用途 汇聚目标游戏进程的逐帧时间并计算 1% Low 等统计

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace AegisApp
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
                if (targetPid != pid) { targetPid = pid; lastQpc = 0; baseQpc = 0; }
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
            lock (sync)
            {
                t = trace;
                trace = null;
                snapshot = intervalsUs.ToArray();
                intervalsUs = new List<int>();
                lastQpc = 0;
                baseQpc = 0;
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
            if (t == null || snapshot.Length < 30) return null;
            int excludedUs;
            int[] gameplay = ExcludeLoadingClusters(snapshot, out excludedUs);
            if (gameplay.Length < 30) gameplay = snapshot;
            double avgFps, low1, low01;
            if (!ComputeStats(gameplay, out avgFps, out low1, out low01)) return null;
            string frames = gameplay.Length.ToString();
            if (excludedUs > 0)
                frames += Lang.F("ev.fps.excluded",
                    (excludedUs / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture));
            return Lang.F("ev.fps",
                avgFps.ToString("0", CultureInfo.InvariantCulture),
                low1.ToString("0", CultureInfo.InvariantCulture),
                low01.ToString("0", CultureInfo.InvariantCulture),
                frames);
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
                if (baseQpc == 0) baseQpc = qpc;
                if (qpc >= baseQpc)
                    threads.Add(tid, (qpc - baseQpc) * 1000000 / Stopwatch.Frequency);
                if (lastQpc != 0 && qpc > lastQpc)
                {
                    long us = (qpc - lastQpc) * 1000000 / Stopwatch.Frequency;
                    if (us > 0 && us <= MaxIntervalUs && intervalsUs.Count < MaxFrames)
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
