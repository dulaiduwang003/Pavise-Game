// @author bdth 2074055628@qq.com
// 文件用途 统计 Present 事件的线程归属 判断游戏是否存在单一帧关键线程

using System;
using System.Collections.Generic;

namespace AegisApp
{
    internal struct PresentThreadSummary
    {
        public int DominantTid;
        public int DominantSharePercent;
        public int StabilityPercent;
        public int ThreadCount;
        public int Windows;
        public long Samples;
        public bool Truncated;
    }

    internal sealed class PresentThreadTracker
    {
        internal const long WindowUs = 2000000;
        internal const int MinWindows = 3;
        private const int MaxTrackedThreads = 64;

        private readonly Dictionary<int, int> totals = new Dictionary<int, int>();
        private readonly Dictionary<int, int> current = new Dictionary<int, int>();
        private readonly Dictionary<int, int> wins = new Dictionary<int, int>();
        private long windowStartUs = -1;
        private int windows;
        private long samples;
        private bool truncated;

        public void Reset()
        {
            totals.Clear(); current.Clear(); wins.Clear();
            windowStartUs = -1; windows = 0; samples = 0; truncated = false;
        }

        public void Add(int tid, long atUs)
        {
            if (windowStartUs < 0) windowStartUs = atUs;
            else if (atUs - windowStartUs >= WindowUs) { CloseWindow(); windowStartUs = atUs; }

            samples++;
            Bump(totals, tid);
            Bump(current, tid);
        }

        public void Seal() { CloseWindow(); }

        private void Bump(Dictionary<int, int> map, int tid)
        {
            int n;
            if (map.TryGetValue(tid, out n)) { map[tid] = n + 1; return; }
            if (map.Count >= MaxTrackedThreads) { truncated = true; return; }
            map[tid] = 1;
        }

        private void CloseWindow()
        {
            if (current.Count == 0) return;
            windows++;
            Bump(wins, Leader(current));
            current.Clear();
        }

        private static int Leader(Dictionary<int, int> map)
        {
            int bestTid = 0, best = -1;
            foreach (KeyValuePair<int, int> kv in map)
                if (kv.Value > best || (kv.Value == best && kv.Key < bestTid))
                { best = kv.Value; bestTid = kv.Key; }
            return bestTid;
        }

        public bool TryDescribe(out PresentThreadSummary summary)
        {
            summary = new PresentThreadSummary();
            if (windows < MinWindows || samples <= 0 || totals.Count == 0) return false;
            int dominant = Leader(totals);
            int dominantTotal, dominantWins;
            totals.TryGetValue(dominant, out dominantTotal);
            wins.TryGetValue(dominant, out dominantWins);
            summary.DominantTid = dominant;
            summary.DominantSharePercent = (int)(dominantTotal * 100L / samples);
            summary.StabilityPercent = dominantWins * 100 / windows;
            summary.ThreadCount = totals.Count;
            summary.Windows = windows;
            summary.Samples = samples;
            summary.Truncated = truncated;
            return true;
        }

        internal const int StrongSharePercent = 80;
        internal const int StrongStabilityPercent = 80;

        internal static bool LooksSingleThreaded(PresentThreadSummary s)
        {
            return s.DominantSharePercent >= StrongSharePercent
                && s.StabilityPercent >= StrongStabilityPercent;
        }

        internal static string Describe(PresentThreadSummary s)
        {
            return Lang.F(LooksSingleThreaded(s) ? "ev.present.single" : "ev.present.spread",
                s.DominantTid, s.DominantSharePercent, s.ThreadCount, s.StabilityPercent, s.Windows);
        }
    }
}
