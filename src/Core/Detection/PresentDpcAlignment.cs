// @author bdth 2074055628@qq.com
// File purpose Build present long-frame intervals and align them with DPC to find driver modules hitting long frames; pure computation, independent of match state
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // Intersect present long-frame intervals with the DPC timeline, counting which modules' DPCs landed in each long frame, grouped by module
    //   Active-set sweep: intervals are QPC-ascending and non-overlapping, DPCs sorted by effective StartQpc
    //   Output: top modules hitting long frames, per module the number of frames hit LongFrameHits and total DPC count DpcCount
    internal sealed class PresentDpcAlignment
    {
        public bool Ok;
        public int LongFrames;
        public int TotalDpcInLongFrames;
        public Dictionary<string, int> LongFrameHits;   // Module -> number of long-frame intervals hit
        public Dictionary<string, int> DpcCounts;       // Module -> total DPCs inside long frames
        public bool UnknownModuleInLongFrames;          // Intervals contain DPCs that cannot be mapped to a driver; zero hits unreliable
        public bool SwapchainIdentityReliable;          // May only be set true once the target swapchain identity is resolved
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
            IrqFrameEvidence ignored;
            return BuildLongFrameIntervals(frames, freq, rendererPid, minCoverageSeconds, out ignored);
        }

        internal static List<long[]> BuildLongFrameIntervals(
            List<PresentFrame> frames, long freq, int rendererPid, double minCoverageSeconds,
            out IrqFrameEvidence evidence)
        {
            evidence = null;
            if (frames == null || freq <= 0 || rendererPid <= 0 || minCoverageSeconds < 0
                || double.IsNaN(minCoverageSeconds) || double.IsInfinity(minCoverageSeconds)) return null;

            var qpcs = new List<long>(frames.Count);
            foreach (PresentFrame f in frames) if (f.Pid == rendererPid) qpcs.Add(f.Qpc);
            if (qpcs.Count < MinPresentIntervalsForAlignment + 1) return null;
            qpcs.Sort();

            double msPerTick = 1000.0 / freq;
            // Tens of seconds without presents after Alt-Tab or minimize is not a frame; split into segments at huge gaps and use only one segment
            // Accept only a continuously active present stream with enough samples, so an idle gap cannot fabricate coverage and long frames together
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

            // Time-ordered adjacent frame intervals; loQ/hiQ keep the endpoint QPCs for long-frame interval alignment
            var ft = new List<double>(active.Count);
            foreach (long[] pair in active) ft.Add((pair[1] - pair[0]) * msPerTick);
            int n = ft.Count;

            var sorted = new List<double>(ft);
            sorted.Sort();
            double median = sorted[n / 2];
            if ((n & 1) == 0) median = (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
            double longThresh = median * 2.0;
            // Long-frame intervals are taken from the time-ordered ft; sorted breaks adjacency and cannot be used
            var intervals = new List<long[]>();
            for (int i = 0; i < n; i++)
                if (ft[i] > longThresh) intervals.Add(active[i]);
            evidence = new IrqFrameEvidence { Intervals = n, LongFrames = intervals.Count,
                Seconds = activeCoverage / (double)freq,
                P99Ms = sorted[Math.Min(n - 1, (int)Math.Ceiling(n * .99) - 1)],
                P999Ms = sorted[Math.Min(n - 1, (int)Math.Ceiling(n * .999) - 1)],
                IdentityReliable = false }; // PID-only Event 184 cannot prove one swapchain
            return intervals;
        }

        internal static PresentDpcAlignment AlignDpcToLongFrames(
            List<long[]> intervals, List<InterruptAttribution.DpcTimelineEntry> dpc,
            bool swapchainIdentityReliable = false)
        {
            // null means a capture chain is unavailable altogether; a non-null empty set means
            // it was usable for positive-hit alignment but found nothing; whether that counts as negative evidence is decided separately by swapchain reliability
            if (intervals == null) return null;

            var r = new PresentDpcAlignment();
            r.Ok = true;
            r.SwapchainIdentityReliable = swapchainIdentityReliable;
            r.LongFrames = intervals.Count;
            r.LongFrameHits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            r.DpcCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // present has already shown no long frames; whether the DPC probe is usable or not, no long-frame hit can be hiding
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
            foreach (long[] iv in intervals)   // Intervals already QPC-ascending
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
                    // Half-open overlap rule: DPC.Start < frame.End && DPC.End > frame.Start
                    // A DPC straddling the frame boundary is exactly the one that most needs catching; looking only at EndQpc would miss it
                    if (entry.StartQpc >= hi || entry.EndQpc <= lo) continue;
                    string m = entry.Module;
                    if (string.IsNullOrWhiteSpace(m) || m == "?")
                    {
                        m = "?";
                        r.UnknownModuleInLongFrames = true;
                    }
                    // One DPC can straddle two adjacent long frames; frame hits count once each, but the event total
                    // counts only once, otherwise the log reports 1 straddling DPC as 2
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
