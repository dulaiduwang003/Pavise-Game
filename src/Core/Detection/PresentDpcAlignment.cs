// @author bdth 2074055628@qq.com
// 文件用途 呈现长帧区间构建与 DPC 对齐 找出撞长帧的驱动模块 纯计算不依赖对局状态
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // present 长帧区间 ∩ DPC 时间线 数每个长帧里落了哪些模块的 DPC 归类
    //   活跃集扫描 区间按 QPC 递增且不重叠 DPC 按有效 StartQpc 排序
    //   产出 撞长帧 top 模块 每模块记 撞了几帧(LongFrameHits) 与总 DPC 数(DpcCount)
    internal sealed class PresentDpcAlignment
    {
        public bool Ok;
        public int LongFrames;
        public int TotalDpcInLongFrames;
        public Dictionary<string, int> LongFrameHits;   // 模块 -> 命中多少个长帧区间
        public Dictionary<string, int> DpcCounts;       // 模块 -> 长帧内 DPC 总数
        public bool UnknownModuleInLongFrames;          // 区间内有无法映射到驱动的 DPC 零命中不可靠
        public bool SwapchainIdentityReliable;          // 只有已解决目标 swapchain 身份时才可置 true
        public string TopModule;
        public int TopModuleLongFrameHits;
        public int TopModuleDpcCount;

        private const int MinPresentIntervalsForAlignment = 30;
        private const double MaxContinuousPresentGapMs = 2000.0;

        internal static bool PresentCaptureComplete(bool drainCompleted, bool consumerExitedEarly,
            bool truncated, uint eventsLost, uint buffersLost)
        {
            return drainCompleted && !consumerExitedEarly && !truncated
                && eventsLost == 0 && buffersLost == 0;
        }

        internal static List<long[]> BuildLongFrameIntervals(
            List<PresentFrame> frames, long freq, int rendererPid, double minCoverageSeconds)
        {
            if (frames == null || freq <= 0 || rendererPid <= 0 || minCoverageSeconds < 0
                || double.IsNaN(minCoverageSeconds) || double.IsInfinity(minCoverageSeconds)) return null;

            var qpcs = new List<long>(frames.Count);
            foreach (PresentFrame f in frames) if (f.Pid == rendererPid) qpcs.Add(f.Qpc);
            if (qpcs.Count < MinPresentIntervalsForAlignment + 1) return null;
            qpcs.Sort();

            double msPerTick = 1000.0 / freq;
            // Alt-Tab/最小化后几十秒不呈现不是一帧 按超大 gap 切段 只用一段
            // 只认连续活跃且样本够的呈现流 免得空窗把 coverage 和长帧一起伪造出来
            var segments = new List<List<long[]>>();
            var current = new List<long[]>();
            for (int i = 1; i < qpcs.Count; i++)
            {
                long d = qpcs[i] - qpcs[i - 1];
                if (d <= 0) continue;
                if (d * msPerTick > MaxContinuousPresentGapMs)
                {
                    if (current.Count > 0) segments.Add(current);
                    current = new List<long[]>();
                    continue;
                }
                current.Add(new long[] { qpcs[i - 1], qpcs[i] });
            }
            if (current.Count > 0) segments.Add(current);

            List<long[]> active = null;
            long activeCoverage = -1;
            foreach (List<long[]> segment in segments)
            {
                if (segment.Count < MinPresentIntervalsForAlignment) continue;
                long coverage = segment[segment.Count - 1][1] - segment[0][0];
                if (coverage > activeCoverage) { active = segment; activeCoverage = coverage; }
            }
            if (active == null || activeCoverage <= 0
                || activeCoverage / (double)freq < minCoverageSeconds) return null;

            // 时间序相邻帧间隔 loQ/hiQ 保留端点 QPC 供长帧区间对齐
            var ft = new List<double>(active.Count);
            foreach (long[] pair in active) ft.Add((pair[1] - pair[0]) * msPerTick);
            int n = ft.Count;

            var sorted = new List<double>(ft);
            sorted.Sort();
            double median = sorted[n / 2];
            if ((n & 1) == 0) median = (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
            double longThresh = median * 2.0;
            // 长帧区间从时间序 ft 取(sorted 会打乱相邻关系不能用)
            var intervals = new List<long[]>();
            for (int i = 0; i < n; i++)
                if (ft[i] > longThresh) intervals.Add(active[i]);
            return intervals;
        }

        internal static PresentDpcAlignment AlignDpcToLongFrames(
            List<long[]> intervals, List<InterruptAttribution.DpcTimelineEntry> dpc,
            bool swapchainIdentityReliable = false)
        {
            // null 表示某条采集链根本不可用 非 null 空集合表示
            // 可用于正命中对齐但结果为零 是否能作负证据由 swapchain 可靠性单独决定
            if (intervals == null) return null;

            var r = new PresentDpcAlignment();
            r.Ok = true;
            r.SwapchainIdentityReliable = swapchainIdentityReliable;
            r.LongFrames = intervals.Count;
            r.LongFrameHits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            r.DpcCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // present 已经明确没长帧 DPC 探针能不能用都藏不住撞长帧
            if (intervals.Count == 0) return r;
            if (dpc == null) return null;
            if (dpc.Count == 0) return r;

            dpc.Sort(delegate (InterruptAttribution.DpcTimelineEntry a, InterruptAttribution.DpcTimelineEntry b)
            {
                int byStart = a.StartQpc.CompareTo(b.StartQpc);
                return byStart != 0 ? byStart : a.EndQpc.CompareTo(b.EndQpc);
            });

            Dictionary<string, int> hits = r.LongFrameHits;
            Dictionary<string, int> counts = r.DpcCounts;
            int addIndex = 0, nDpc = dpc.Count, totalIn = 0;
            var activeDpc = new List<int>();
            var countedDpc = new HashSet<int>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (long[] iv in intervals)   // 区间已按 QPC 递增
            {
                if (iv == null || iv.Length < 2 || iv[1] <= iv[0]) return null;
                long lo = iv[0], hi = iv[1];
                while (addIndex < nDpc && dpc[addIndex].StartQpc < hi)
                    activeDpc.Add(addIndex++);
                for (int i = activeDpc.Count - 1; i >= 0; i--)
                    if (dpc[activeDpc[i]].EndQpc <= lo) activeDpc.RemoveAt(i);
                seen.Clear();
                foreach (int dpcIndex in activeDpc)
                {
                    InterruptAttribution.DpcTimelineEntry entry = dpc[dpcIndex];
                    // 半开重叠规则 DPC.Start < frame.End && DPC.End > frame.Start
                    // 跨过帧边界才是最需被捕获的 DPC 只看 EndQpc 会漏掉它
                    if (entry.StartQpc >= hi || entry.EndQpc <= lo) continue;
                    string m = entry.Module;
                    if (string.IsNullOrWhiteSpace(m) || m == "?")
                    {
                        m = "?";
                        r.UnknownModuleInLongFrames = true;
                    }
                    // 一条 DPC 可以横跨两个相邻长帧 帧命中应各算一次 但事件总数
                    // 只能算一次 不然日志会把 1 条跨帧 DPC 报成 2 条
                    if (countedDpc.Add(dpcIndex))
                    {
                        int c; counts.TryGetValue(m, out c); counts[m] = c + 1;
                        totalIn++;
                    }
                    if (seen.Add(m)) { int h; hits.TryGetValue(m, out h); hits[m] = h + 1; }
                }
            }

            r.TotalDpcInLongFrames = totalIn;
            foreach (KeyValuePair<string, int> kv in hits)
            {
                int dc; counts.TryGetValue(kv.Key, out dc);
                if (kv.Value > r.TopModuleLongFrameHits
                    || (kv.Value == r.TopModuleLongFrameHits && dc > r.TopModuleDpcCount))
                {
                    r.TopModule = kv.Key;
                    r.TopModuleLongFrameHits = kv.Value;
                    r.TopModuleDpcCount = dc;
                }
            }
            return r;
        }
    }
}
