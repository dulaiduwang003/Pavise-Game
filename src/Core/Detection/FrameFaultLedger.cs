// @author bdth 2074055628@qq.com
// 文件用途 把长中断与游戏线程等不到 CPU 的区间 与每一帧的间隔做交集 判断谁造成了这批慢帧
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal enum FaultConclusion
    {
        NoSlowFrames = 0,
        Elsewhere = 1,
        Inconclusive = 2,
        Interrupts = 3,
        CpuPreemption = 4,
        Mixed = 5,
        DiskStall = 6
    }

    internal struct Span
    {
        public long Start;
        public long End;
    }

    internal sealed class FrameFaultVerdict
    {
        public int SlowFrames;
        public int NormalFrames;
        public double SlowLimitMs;
        public double SlowIntrShare;
        public double NormalIntrShare;
        public double SlowPreemptShare;
        public double NormalPreemptShare;
        public double WorstFrameShare;
        public double IntrExplained;
        public double PreemptExplained;
        public double DiskExplained;
        public double CombinedExplained;
        public double BothExplained;
        public double OverlapExplained;
        public double TotalExcessMs;
        public int WorstFrameIndex = -1;
        public uint TopOffenderPid;
        public double TopOffenderMs;
        public readonly Dictionary<uint, double> OffenderMs = new Dictionary<uint, double>();

        public double SlowDiskShare;
        public double NormalDiskShare;

        public int SlowHit;
        public int NormalHit;
        public bool HasPreemptData;
        public bool HasDiskData;
        public bool IntrUnconstrained;
        public bool EligibleKnown;
        public double PreemptEligibleExplained;
        public double PreemptFrameThreadExplained;
        public double PreemptPresentThreadExplained;
        public bool PresentThreadEligible;
        public int EligibleCount;
        public bool PreemptHitEligible;
        public int DiskIoUnproven;
        public int DiskOffThread;
        public bool DiskThreadUnconstrained;
        public double DiskIoObservedMs;
        public int HitMask;

        public bool PathKnown;
        public string PathWhy = "";
        public double PathCertShare;
        public double PathCoverage;
        public double PathSpineExplained;
        public double PathSpineReadyMs;
        public int PathSpineCount;
        public int PathSlowWalked;
        public uint PathAnchorTid;

        public double FrameThreadRunShare;
        public double FrameThreadReadyShare;
        public double FrameThreadWaitShare;
        public uint FrameThreadTid;
        public int IntrDroppedOffCore;
        public FaultConclusion Conclusion;

        public double SlowHitRate { get { return SlowFrames > 0 ? (double)SlowHit / SlowFrames : 0; } }
        public double NormalHitRate { get { return NormalFrames > 0 ? (double)NormalHit / NormalFrames : 0; } }

        public double ExplainedBy(uint pid)
        {
            double ms;
            if (pid == 0 || TotalExcessMs <= 0 || !OffenderMs.TryGetValue(pid, out ms)) return 0;
            double r = ms / TotalExcessMs;
            return r > 1 ? 1 : r;
        }
    }

    internal static class FrameFaultLedger
    {
        internal const double SlowFactor = 1.5;

        internal const double ExonerateShare = 0.05;
        internal const double ConvictShare = 0.15;
        internal const double ConvictRatio = 3.0;
        internal const double ExplainConvict = 0.50;

        public static FrameFaultVerdict Attribute(long[] stamps, InterruptSample[] samples,
            double budgetMs, long qpcFrequency)
        {
            return Attribute(stamps, samples, null, null, budgetMs, qpcFrequency);
        }

        public static FrameFaultVerdict Attribute(long[] stamps, InterruptSample[] samples,
            PreemptSpan[] preempts, double budgetMs, long qpcFrequency)
        {
            return Attribute(stamps, samples, preempts, null, budgetMs, qpcFrequency);
        }

        public static FrameFaultVerdict Attribute(long[] stamps, InterruptSample[] samples,
            PreemptSpan[] preempts, RunSpan[] runs, double budgetMs, long qpcFrequency)
        {
            return Attribute(stamps, samples, preempts, runs, null, budgetMs, qpcFrequency);
        }

        public static FrameFaultVerdict Attribute(long[] stamps, InterruptSample[] samples,
            PreemptSpan[] preempts, RunSpan[] runs, StallSpan[] stalls,
            double budgetMs, long qpcFrequency)
        {
            return Attribute(stamps, samples, preempts, runs, stalls, null, budgetMs, qpcFrequency);
        }

        public static FrameFaultVerdict Attribute(long[] stamps, InterruptSample[] samples,
            PreemptSpan[] preempts, RunSpan[] runs, StallSpan[] stalls,
            HashSet<uint> eligible, double budgetMs, long qpcFrequency)
        {
            return Attribute(stamps, samples, preempts, runs, stalls, null, eligible,
                budgetMs, qpcFrequency);
        }

        public static FrameFaultVerdict Attribute(long[] stamps, InterruptSample[] samples,
            PreemptSpan[] preempts, RunSpan[] runs, StallSpan[] stalls, WaitSpan[] waits,
            HashSet<uint> eligible, double budgetMs, long qpcFrequency)
        {
            return Attribute(stamps, samples, preempts, runs, stalls, waits, eligible, 0,
                budgetMs, qpcFrequency);
        }

        public static FrameFaultVerdict Attribute(long[] stamps, InterruptSample[] samples,
            PreemptSpan[] preempts, RunSpan[] runs, StallSpan[] stalls, WaitSpan[] waits,
            HashSet<uint> eligible, uint presentTid, double budgetMs, long qpcFrequency)
        {
            var v = new FrameFaultVerdict();
            if (stamps == null || stamps.Length < 2 || budgetMs <= 0 || qpcFrequency <= 0) return v;
            if (samples == null) samples = new InterruptSample[0];
            v.HasPreemptData = preempts != null;
            if (preempts == null) preempts = new PreemptSpan[0];

            v.HasDiskData = stalls != null;

            long winFrom = stamps[0], winTo = stamps[stamps.Length - 1];
            HashSet<uint> hot = eligible != null && eligible.Count > 0
                ? eligible : HotThreads(runs, winFrom, winTo);
            v.EligibleKnown = eligible != null && eligible.Count > 0;
            v.EligibleCount = hot.Count;
            uint frameTid = BusiestThread(runs, winFrom, winTo, hot);
            v.FrameThreadTid = frameTid;

            RunSpan[] winRuns = ClipRuns(runs, winFrom, winTo);
            int dropped;
            bool intrLoose;
            Span[] intr = BuildInterruptSpans(samples, winRuns, out dropped, out intrLoose);
            v.IntrDroppedOffCore = dropped;
            v.IntrUnconstrained = samples.Length > 0 && intrLoose;
            Span[] pre = Union(ToSpans(preempts));
            Span[] preElig = hot.Count > 0 ? Union(ToSpans(FilterByVictim(preempts, hot))) : pre;
            Span[] preFrame = frameTid != 0
                ? Union(ToSpans(FilterByVictimTid(preempts, frameTid))) : new Span[0];
            Span[] prePresent = presentTid != 0
                ? Union(ToSpans(FilterByVictimTid(preempts, presentTid))) : new Span[0];
            v.PresentThreadEligible = presentTid != 0 && hot.Contains(presentTid);
            int unproven, offThread;
            Span[] ioObserved;
            Span[] disk = BuildDiskSpans(stalls, hot, runs, out ioObserved, out unproven, out offThread);
            v.DiskIoUnproven = unproven;
            v.DiskOffThread = offThread;
            v.DiskThreadUnconstrained = stalls != null && hot.Count == 0;
            Span[] both = Intersect(intr, pre);
            Span[] any = Union(Concat(Concat(intr, pre), disk));

            double msPerTick = 1000.0 / qpcFrequency;
            v.SlowLimitMs = budgetMs * SlowFactor;

            var byPid = BuildPerPid(preempts);
            double slowMs = 0, normalMs = 0;
            double slowIntrMs = 0, normalIntrMs = 0, slowPreMs = 0, normalPreMs = 0;
            double slowDiskMs = 0, normalDiskMs = 0;
            double excessMs = 0, intrExMs = 0, preExMs = 0, anyExMs = 0, bothExMs = 0, diskExMs = 0;

            double ioObsMs = 0;
            double preEligExMs = 0, preFrameExMs = 0, prePresentExMs = 0;
            int ai = 0, ap = 0, ab = 0, aa = 0, ad = 0, ao = 0, ae = 0, af = 0, ag = 0;
            for (int i = 0; i + 1 < stamps.Length; i++)
            {
                long lo = stamps[i], hi = stamps[i + 1];
                if (hi <= lo) continue;
                double frameMs = (hi - lo) * msPerTick;
                double intrMs = OverlapMs(intr, ref ai, lo, hi, msPerTick);
                double preMs = OverlapMs(pre, ref ap, lo, hi, msPerTick);
                double preEligMs = OverlapMs(preElig, ref ae, lo, hi, msPerTick);
                double preFrameMs = OverlapMs(preFrame, ref af, lo, hi, msPerTick);
                double prePresentMs = OverlapMs(prePresent, ref ag, lo, hi, msPerTick);
                double bothMs = OverlapMs(both, ref ab, lo, hi, msPerTick);
                double diskMs = OverlapMs(disk, ref ad, lo, hi, msPerTick);
                double ioMs = OverlapMs(ioObserved, ref ao, lo, hi, msPerTick);
                double anyMs = OverlapMs(any, ref aa, lo, hi, msPerTick);

                bool isSlow = frameMs > v.SlowLimitMs;
                if (isSlow)
                {
                    v.SlowFrames++; slowMs += frameMs; slowIntrMs += intrMs; slowPreMs += preMs;
                    slowDiskMs += diskMs;
                    ioObsMs += ioMs;
                    if (anyMs > 0) v.SlowHit++;
                    double share = anyMs / frameMs;
                    if (share > v.WorstFrameShare) { v.WorstFrameShare = share; v.WorstFrameIndex = i; }
                    double excess = frameMs - budgetMs;
                    if (excess > 0)
                    {
                        excessMs += excess;
                        intrExMs += intrMs < excess ? intrMs : excess;
                        preExMs += preMs < excess ? preMs : excess;
                        preEligExMs += preEligMs < excess ? preEligMs : excess;
                        preFrameExMs += preFrameMs < excess ? preFrameMs : excess;
                        prePresentExMs += prePresentMs < excess ? prePresentMs : excess;
                        diskExMs += diskMs < excess ? diskMs : excess;
                        bothExMs += bothMs < excess ? bothMs : excess;
                        anyExMs += anyMs < excess ? anyMs : excess;
                    }
                    AccumulatePerPid(byPid, lo, hi, msPerTick, frameMs - budgetMs);
                }
                else
                {
                    v.NormalFrames++; normalMs += frameMs; normalIntrMs += intrMs; normalPreMs += preMs;
                    normalDiskMs += diskMs;
                    if (anyMs > 0) v.NormalHit++;
                }
            }

            v.SlowIntrShare = slowMs > 0 ? slowIntrMs / slowMs : 0;
            v.NormalIntrShare = normalMs > 0 ? normalIntrMs / normalMs : 0;
            v.SlowPreemptShare = slowMs > 0 ? slowPreMs / slowMs : 0;
            v.NormalPreemptShare = normalMs > 0 ? normalPreMs / normalMs : 0;
            v.SlowDiskShare = slowMs > 0 ? slowDiskMs / slowMs : 0;
            v.NormalDiskShare = normalMs > 0 ? normalDiskMs / normalMs : 0;
            v.TotalExcessMs = excessMs;
            v.IntrExplained = excessMs > 0 ? intrExMs / excessMs : 0;
            v.PreemptExplained = excessMs > 0 ? preExMs / excessMs : 0;
            v.DiskExplained = excessMs > 0 ? diskExMs / excessMs : 0;
            v.PreemptEligibleExplained = excessMs > 0 ? preEligExMs / excessMs : 0;
            v.PreemptFrameThreadExplained = excessMs > 0 ? preFrameExMs / excessMs : 0;
            v.PreemptPresentThreadExplained = excessMs > 0 ? prePresentExMs / excessMs : 0;
            v.DiskIoObservedMs = ioObsMs;
            v.CombinedExplained = excessMs > 0 ? anyExMs / excessMs : 0;
            v.BothExplained = excessMs > 0 ? bothExMs / excessMs : 0;
            double over = v.IntrExplained + v.PreemptExplained + v.DiskExplained - v.CombinedExplained;
            v.OverlapExplained = over > 0 ? over : 0;

            foreach (KeyValuePair<uint, PidCoverage> kv in byPid)
            {
                if (kv.Key == 0) continue;
                v.OffenderMs[kv.Key] = kv.Value.Ms;
                if (kv.Value.Ms > v.TopOffenderMs) { v.TopOffenderMs = kv.Value.Ms; v.TopOffenderPid = kv.Key; }
            }

            SplitFrameThreadTime(v, stamps, runs, preempts, waits, hot, msPerTick, budgetMs);
            v.Conclusion = Judge(v);
            return v;
        }

        private sealed class PidCoverage
        {
            public Span[] Spans;
            public int Cursor;
            public double Ms;
            public double Pending;
        }

        private static Dictionary<uint, PidCoverage> BuildPerPid(PreemptSpan[] preempts)
        {
            var raw = new Dictionary<uint, List<Span>>();
            for (int i = 0; i < preempts.Length; i++)
            {
                PreemptSpan x = preempts[i];
                if (x.DirectByPid == 0) continue;
                long end = x.DirectEndQpc;
                if (end > x.EndQpc) end = x.EndQpc;
                if (end <= x.StartQpc) continue;
                List<Span> list;
                if (!raw.TryGetValue(x.DirectByPid, out list)) { list = new List<Span>(); raw[x.DirectByPid] = list; }
                var sp = new Span();
                sp.Start = x.StartQpc; sp.End = end;
                list.Add(sp);
            }
            var outp = new Dictionary<uint, PidCoverage>();
            foreach (KeyValuePair<uint, List<Span>> kv in raw)
            {
                var c = new PidCoverage();
                c.Spans = Union(kv.Value.ToArray());
                outp[kv.Key] = c;
            }
            return outp;
        }

        private static void AccumulatePerPid(Dictionary<uint, PidCoverage> byPid,
            long lo, long hi, double msPerTick, double excessMs)
        {
            if (excessMs <= 0) { AdvanceCursors(byPid, lo, hi, msPerTick); return; }
            double sum = 0;
            foreach (KeyValuePair<uint, PidCoverage> kv in byPid)
            {
                PidCoverage c = kv.Value;
                int cur = c.Cursor;
                c.Pending = OverlapMs(c.Spans, ref cur, lo, hi, msPerTick);
                c.Cursor = cur;
                if (c.Pending > excessMs) c.Pending = excessMs;
                sum += c.Pending;
            }
            double scale = sum > excessMs && sum > 0 ? excessMs / sum : 1.0;
            foreach (KeyValuePair<uint, PidCoverage> kv in byPid)
            {
                kv.Value.Ms += kv.Value.Pending * scale;
                kv.Value.Pending = 0;
            }
        }

        private static void AdvanceCursors(Dictionary<uint, PidCoverage> byPid,
            long lo, long hi, double msPerTick)
        {
            foreach (KeyValuePair<uint, PidCoverage> kv in byPid)
            {
                PidCoverage c = kv.Value;
                int cur = c.Cursor;
                OverlapMs(c.Spans, ref cur, lo, hi, msPerTick);
                c.Cursor = cur;
            }
        }

        internal static Span[] BuildInterruptSpans(InterruptSample[] samples, RunSpan[] runs, out int dropped)
        {
            bool loose;
            return BuildInterruptSpans(samples, runs, out dropped, out loose);
        }

        internal static Span[] BuildInterruptSpans(InterruptSample[] samples, RunSpan[] runs,
            out int dropped, out bool unconstrained)
        {
            dropped = 0;
            unconstrained = false;
            if (samples == null || samples.Length == 0) return new Span[0];
            if (runs == null || runs.Length == 0)
            {
                unconstrained = true;
                var all = new Span[samples.Length];
                for (int i = 0; i < samples.Length; i++)
                { all[i].Start = samples[i].StartQpc; all[i].End = samples[i].EndQpc; }
                return Union(all);
            }

            var byCpu = new Dictionary<ushort, List<Span>>();
            for (int i = 0; i < runs.Length; i++)
            {
                if (runs[i].EndQpc <= runs[i].StartQpc) continue;
                List<Span> list;
                if (!byCpu.TryGetValue(runs[i].Cpu, out list)) { list = new List<Span>(); byCpu[runs[i].Cpu] = list; }
                var sp = new Span();
                sp.Start = runs[i].StartQpc; sp.End = runs[i].EndQpc;
                list.Add(sp);
            }
            var merged = new Dictionary<ushort, Span[]>();
            foreach (KeyValuePair<ushort, List<Span>> kv in byCpu) merged[kv.Key] = Union(kv.Value.ToArray());

            var keep = new List<Span>();
            foreach (InterruptSample x in samples)
            {
                if (x.EndQpc <= x.StartQpc) continue;
                Span[] onCpu;
                if (!merged.TryGetValue(x.Cpu, out onCpu)) { dropped++; continue; }
                int cur = 0;
                bool hit = false;
                for (int k = 0; k < onCpu.Length; k++)
                {
                    if (onCpu[k].End <= x.StartQpc) continue;
                    if (onCpu[k].Start >= x.EndQpc) break;
                    var sp = new Span();
                    sp.Start = x.StartQpc > onCpu[k].Start ? x.StartQpc : onCpu[k].Start;
                    sp.End = x.EndQpc < onCpu[k].End ? x.EndQpc : onCpu[k].End;
                    if (sp.End > sp.Start) { keep.Add(sp); hit = true; }
                }
                if (cur == 0 && !hit) dropped++;
            }
            return Union(keep.ToArray());
        }

        internal static Span[] BuildDiskSpans(StallSpan[] stalls, RunSpan[] runs, out int unproven)
        {
            int ignored;
            Span[] io;
            return BuildDiskSpans(stalls, runs, out io, out unproven, out ignored);
        }

        internal static Span[] BuildDiskSpans(StallSpan[] stalls, RunSpan[] runs,
            out int unproven, out int offThread)
        {
            Span[] io;
            return BuildDiskSpans(stalls, runs, out io, out unproven, out offThread);
        }

        internal const double HotThreadShare = 0.25;

        internal static HashSet<uint> HotThreads(RunSpan[] runs)
        {
            return HotThreads(runs, 0, long.MaxValue);
        }

        internal static HashSet<uint> HotThreads(RunSpan[] runs, long fromQpc, long toQpc)
        {
            var set = new HashSet<uint>();
            if (runs == null || runs.Length == 0) return set;
            var ticks = new Dictionary<uint, long>();
            long top = 0;
            for (int i = 0; i < runs.Length; i++)
            {
                long a = runs[i].StartQpc > fromQpc ? runs[i].StartQpc : fromQpc;
                long b = runs[i].EndQpc < toQpc ? runs[i].EndQpc : toQpc;
                long d = b - a;
                if (d <= 0) continue;
                long cur;
                ticks.TryGetValue(runs[i].Tid, out cur);
                cur += d;
                ticks[runs[i].Tid] = cur;
                if (cur > top) top = cur;
            }
            if (top <= 0) return set;
            double floor = top * HotThreadShare;
            foreach (KeyValuePair<uint, long> kv in ticks)
                if (kv.Value >= floor) set.Add(kv.Key);
            return set;
        }

        internal static Span[] BuildDiskSpans(StallSpan[] stalls, RunSpan[] runs,
            out Span[] ioObserved, out int unproven, out int offThread)
        {
            return BuildDiskSpans(stalls, HotThreads(runs), runs, out ioObserved, out unproven, out offThread);
        }

        internal static Span[] BuildDiskSpans(StallSpan[] stalls, HashSet<uint> hot, RunSpan[] runs,
            out Span[] ioObserved, out int unproven, out int offThread)
        {
            unproven = 0;
            offThread = 0;
            ioObserved = new Span[0];
            if (stalls == null || stalls.Length == 0) return new Span[0];
            if (hot == null) hot = new HashSet<uint>();
            var keep = new List<Span>();
            var io = new List<Span>();
            Dictionary<uint, Span[]> runByTid = null;
            for (int i = 0; i < stalls.Length; i++)
            {
                StallSpan x = stalls[i];
                if (x.EndQpc <= x.StartQpc) continue;
                if (hot.Count > 0 && !hot.Contains(x.Tid)) { offThread++; continue; }
                if (x.Kind == StallKind.HardFault)
                {
                    var sp = new Span();
                    sp.Start = x.StartQpc; sp.End = x.EndQpc;
                    keep.Add(sp);
                    continue;
                }
                if (runs == null || runs.Length == 0) { unproven++; continue; }
                if (runByTid == null) runByTid = BuildRunByTid(runs);
                Span[] mine;
                if (!runByTid.TryGetValue(x.Tid, out mine)) mine = new Span[0];
                var whole = new Span();
                whole.Start = x.StartQpc; whole.End = x.EndQpc;
                int before = io.Count;
                Subtract(whole, mine, io);
                if (io.Count == before) unproven++;
            }
            ioObserved = Union(io.ToArray());
            return Union(keep.ToArray());
        }

        private static Dictionary<uint, Span[]> BuildRunByTid(RunSpan[] runs)
        {
            var raw = new Dictionary<uint, List<Span>>();
            for (int i = 0; i < runs.Length; i++)
            {
                if (runs[i].EndQpc <= runs[i].StartQpc) continue;
                List<Span> list;
                if (!raw.TryGetValue(runs[i].Tid, out list)) { list = new List<Span>(); raw[runs[i].Tid] = list; }
                var sp = new Span();
                sp.Start = runs[i].StartQpc; sp.End = runs[i].EndQpc;
                list.Add(sp);
            }
            var outp = new Dictionary<uint, Span[]>();
            foreach (KeyValuePair<uint, List<Span>> kv in raw) outp[kv.Key] = Union(kv.Value.ToArray());
            return outp;
        }

        internal static void Subtract(Span whole, Span[] cut, List<Span> sink)
        {
            long cur = whole.Start;
            for (int i = 0; i < cut.Length && cur < whole.End; i++)
            {
                if (cut[i].End <= cur) continue;
                if (cut[i].Start >= whole.End) break;
                if (cut[i].Start > cur)
                {
                    var sp = new Span();
                    sp.Start = cur;
                    sp.End = cut[i].Start < whole.End ? cut[i].Start : whole.End;
                    if (sp.End > sp.Start) sink.Add(sp);
                }
                if (cut[i].End > cur) cur = cut[i].End;
            }
            if (cur < whole.End)
            {
                var tail = new Span();
                tail.Start = cur; tail.End = whole.End;
                sink.Add(tail);
            }
        }

        internal static RunSpan[] ClipRuns(RunSpan[] runs, long from, long to)
        {
            if (runs == null || runs.Length == 0) return runs;
            var keep = new List<RunSpan>();
            for (int i = 0; i < runs.Length; i++)
            {
                long a = runs[i].StartQpc > from ? runs[i].StartQpc : from;
                long b = runs[i].EndQpc < to ? runs[i].EndQpc : to;
                if (b <= a) continue;
                RunSpan r = runs[i];
                r.StartQpc = a; r.EndQpc = b;
                keep.Add(r);
            }
            return keep.ToArray();
        }

        private static void SplitFrameThreadTime(FrameFaultVerdict v, long[] stamps,
            RunSpan[] runs, PreemptSpan[] preempts, WaitSpan[] waits,
            HashSet<uint> hot, double msPerTick, double budgetMs)
        {
            if (v.SlowFrames == 0 || runs == null || runs.Length == 0) return;
            uint best = v.FrameThreadTid;
            if (best == 0) return;

            var runList = new List<Span>();
            for (int i = 0; i < runs.Length; i++)
                if (runs[i].Tid == best && runs[i].EndQpc > runs[i].StartQpc)
                    runList.Add(MakeSpan(runs[i].StartQpc, runs[i].EndQpc));
            var readyList = new List<Span>();
            if (preempts != null)
                for (int i = 0; i < preempts.Length; i++)
                    if (preempts[i].VictimTid == best && preempts[i].EndQpc > preempts[i].StartQpc)
                        readyList.Add(MakeSpan(preempts[i].StartQpc, preempts[i].EndQpc));
            var waitList = new List<Span>();
            if (waits != null)
                for (int i = 0; i < waits.Length; i++)
                    if (waits[i].Tid == best && waits[i].EndQpc > waits[i].StartQpc)
                        waitList.Add(MakeSpan(waits[i].StartQpc, waits[i].EndQpc));

            Span[] run = Union(runList.ToArray());
            Span[] ready = Union(readyList.ToArray());
            Span[] wait = Union(waitList.ToArray());

            double slowMs = 0, runMs = 0, readyMs = 0, waitMs = 0;
            int cr = 0, cd = 0, cw = 0;
            double limit = budgetMs * SlowFactor;
            for (int i = 0; i + 1 < stamps.Length; i++)
            {
                long lo = stamps[i], hi = stamps[i + 1];
                if (hi <= lo) continue;
                double frameMs = (hi - lo) * msPerTick;
                if (frameMs <= limit) continue;
                slowMs += frameMs;
                runMs += OverlapMs(run, ref cr, lo, hi, msPerTick);
                readyMs += OverlapMs(ready, ref cd, lo, hi, msPerTick);
                waitMs += OverlapMs(wait, ref cw, lo, hi, msPerTick);
            }
            if (slowMs <= 0) return;
            v.FrameThreadRunShare = runMs / slowMs;
            v.FrameThreadReadyShare = readyMs / slowMs;
            v.FrameThreadWaitShare = waitMs / slowMs;
        }

        internal static uint BusiestThread(RunSpan[] runs, long from, long to, HashSet<uint> hot)
        {
            if (runs == null || runs.Length == 0) return 0;
            var ticks = new Dictionary<uint, long>();
            uint best = 0;
            long bestTicks = 0;
            for (int i = 0; i < runs.Length; i++)
            {
                long a = runs[i].StartQpc > from ? runs[i].StartQpc : from;
                long b = runs[i].EndQpc < to ? runs[i].EndQpc : to;
                if (b <= a) continue;
                if (hot != null && hot.Count > 0 && !hot.Contains(runs[i].Tid)) continue;
                long cur;
                ticks.TryGetValue(runs[i].Tid, out cur);
                cur += b - a;
                ticks[runs[i].Tid] = cur;
                if (cur > bestTicks) { bestTicks = cur; best = runs[i].Tid; }
            }
            return best;
        }

        private static PreemptSpan[] FilterByVictimTid(PreemptSpan[] a, uint tid)
        {
            if (a == null || a.Length == 0) return new PreemptSpan[0];
            var keep = new List<PreemptSpan>();
            for (int i = 0; i < a.Length; i++) if (a[i].VictimTid == tid) keep.Add(a[i]);
            return keep.ToArray();
        }

        private static Span MakeSpan(long a, long b)
        {
            var s = new Span();
            s.Start = a; s.End = b;
            return s;
        }

        private static Span[] ToSpans(PreemptSpan[] a)
        {
            if (a == null) return new Span[0];
            var outp = new Span[a.Length];
            for (int i = 0; i < a.Length; i++) { outp[i].Start = a[i].StartQpc; outp[i].End = a[i].EndQpc; }
            return outp;
        }

        private static PreemptSpan[] FilterByVictim(PreemptSpan[] a, HashSet<uint> hot)
        {
            if (a == null || a.Length == 0) return new PreemptSpan[0];
            var keep = new List<PreemptSpan>();
            for (int i = 0; i < a.Length; i++)
                if (a[i].VictimTid == 0 || hot.Contains(a[i].VictimTid)) keep.Add(a[i]);
            return keep.ToArray();
        }

        private static Span[] Concat(Span[] a, Span[] b)
        {
            var outp = new Span[a.Length + b.Length];
            Array.Copy(a, 0, outp, 0, a.Length);
            Array.Copy(b, 0, outp, a.Length, b.Length);
            return outp;
        }

        internal static Span[] Union(Span[] a)
        {
            if (a == null || a.Length == 0) return new Span[0];
            var t = new Span[a.Length];
            Array.Copy(a, t, a.Length);
            Array.Sort(t, delegate(Span x, Span y) { return x.Start.CompareTo(y.Start); });
            var outp = new Span[t.Length];
            int k = -1;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i].End <= t[i].Start) continue;
                if (k >= 0 && t[i].Start <= outp[k].End)
                {
                    if (t[i].End > outp[k].End) outp[k].End = t[i].End;
                    continue;
                }
                k++;
                outp[k] = t[i];
            }
            k++;
            if (k == outp.Length) return outp;
            var trimmed = new Span[k];
            Array.Copy(outp, trimmed, k);
            return trimmed;
        }

        internal static Span[] Intersect(Span[] a, Span[] b)
        {
            var outp = new List<Span>();
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                long lo = a[i].Start > b[j].Start ? a[i].Start : b[j].Start;
                long hi = a[i].End < b[j].End ? a[i].End : b[j].End;
                if (hi > lo) { var sp = new Span(); sp.Start = lo; sp.End = hi; outp.Add(sp); }
                if (a[i].End < b[j].End) i++; else j++;
            }
            return outp.ToArray();
        }

        internal static double OverlapMs(Span[] spans, ref int cursor, long lo, long hi, double msPerTick)
        {
            if (spans == null || spans.Length == 0) return 0;
            while (cursor < spans.Length && spans[cursor].End <= lo) cursor++;
            double ms = 0;
            for (int k = cursor; k < spans.Length; k++)
            {
                if (spans[k].Start >= hi) break;
                long a = spans[k].Start > lo ? spans[k].Start : lo;
                long b = spans[k].End < hi ? spans[k].End : hi;
                if (b > a) ms += (b - a) * msPerTick;
            }
            return ms;
        }

        private static bool Convicts(double slowShare, double normalShare)
        {
            return slowShare >= ConvictShare && slowShare > normalShare * ConvictRatio;
        }

        private static FaultConclusion Judge(FrameFaultVerdict v)
        {
            if (v.SlowFrames == 0) return FaultConclusion.NoSlowFrames;
            bool intr = !v.IntrUnconstrained
                && (v.IntrExplained >= ExplainConvict
                    || Convicts(v.SlowIntrShare, v.NormalIntrShare));
            bool pre = v.HasPreemptData
                && (v.PreemptExplained >= ExplainConvict
                    || Convicts(v.SlowPreemptShare, v.NormalPreemptShare));
            bool disk = v.HasDiskData && !v.DiskThreadUnconstrained
                && (v.DiskExplained >= ExplainConvict
                    || Convicts(v.SlowDiskShare, v.NormalDiskShare));
            v.HitMask = (intr ? 1 : 0) | (pre ? 2 : 0) | (disk ? 4 : 0);
            v.PreemptHitEligible = v.HasPreemptData && v.EligibleKnown
                && v.PreemptEligibleExplained >= ExplainConvict;
            int hits = (intr ? 1 : 0) + (pre ? 1 : 0) + (disk ? 1 : 0);
            if (hits > 1) return FaultConclusion.Mixed;
            if (intr) return FaultConclusion.Interrupts;
            if (pre) return FaultConclusion.CpuPreemption;
            if (disk) return FaultConclusion.DiskStall;
            if (v.IntrUnconstrained || (v.HasDiskData && v.DiskThreadUnconstrained))
                return FaultConclusion.Inconclusive;
            if (!v.HasPreemptData) return FaultConclusion.Inconclusive;
            if (v.SlowIntrShare < ExonerateShare && v.IntrExplained < ExonerateShare
                && (v.SlowPreemptShare < ExonerateShare && v.PreemptExplained < ExonerateShare)
                && (!v.HasDiskData
                    || (v.SlowDiskShare < ExonerateShare && v.DiskExplained < ExonerateShare)))
                return FaultConclusion.Elsewhere;
            return FaultConclusion.Inconclusive;
        }

        private static string DescribeMixed(FrameFaultVerdict v)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Lang.T("t.framefault.19"));
            bool first = true;
            if ((v.HitMask & 1) != 0)
            { sb.Append(Lang.T("t.framefault.20")).Append((v.IntrExplained * 100).ToString("F0")).Append('%'); first = false; }
            if ((v.HitMask & 2) != 0)
            {
                if (!first) sb.Append(Lang.T("t.framefault.23"));
                sb.Append(Lang.T("t.framefault.21")).Append((v.PreemptExplained * 100).ToString("F0")).Append('%');
                first = false;
            }
            if ((v.HitMask & 4) != 0)
            {
                if (!first) sb.Append(Lang.T("t.framefault.23"));
                sb.Append(Lang.T("t.framefault.22")).Append((v.DiskExplained * 100).ToString("F0")).Append('%');
            }
            sb.Append(Lang.T("t.framefault.16"));
            return sb.ToString();
        }

        public static string Describe(FrameFaultVerdict v)
        {
            if (v == null) return Lang.T("t.framefault.1");
            switch (v.Conclusion)
            {
                case FaultConclusion.NoSlowFrames:
                    return Lang.T("t.framefault.2");
                case FaultConclusion.Elsewhere:
                    return Lang.T("t.framefault.3") + (v.SlowIntrShare * 100).ToString("F2")
                        + Lang.T("t.framefault.4")
                        + (v.HasPreemptData
                            ? Lang.T("t.framefault.11") + (v.SlowPreemptShare * 100).ToString("F2") + "%"
                            : "");
                case FaultConclusion.Interrupts:
                    return Lang.T("t.framefault.5") + (v.SlowIntrShare * 100).ToString("F1")
                        + Lang.T("t.framefault.6") + (v.NormalIntrShare * 100).ToString("F2")
                        + Lang.T("t.framefault.7");
                case FaultConclusion.CpuPreemption:
                    return Lang.T("t.framefault.12") + (v.PreemptExplained * 100).ToString("F0")
                        + Lang.T("t.framefault.13");
                case FaultConclusion.DiskStall:
                    return Lang.T("t.framefault.17") + (v.DiskExplained * 100).ToString("F0")
                        + Lang.T("t.framefault.18");
                case FaultConclusion.Mixed:
                    return DescribeMixed(v);
                default:
                    return Lang.T("t.framefault.8") + (v.SlowIntrShare * 100).ToString("F2")
                        + Lang.T("t.framefault.9");
            }
        }
    }
}
