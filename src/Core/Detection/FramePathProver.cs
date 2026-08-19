// @author bdth 2074055628@qq.com
// 文件用途 逐帧回溯出调度因果链 给抢占维度开证书 而不是靠"这根线程当前很忙"去猜
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal enum PathLegKind
    {
        Running = 0,
        ReadyWait = 1,
        BlockedWait = 2,
    }

    internal enum PathStop
    {
        FrameStart = 0,
        Timer = 1,
        DpcContext = 2,
        ForeignWaker = 3,
        DepthLimit = 4,
        NoRunSpan = 5,
        NoWake = 6,
        NoAnchor = 7,
        UnknownWaker = 8,
    }

    internal struct PathLeg
    {
        public long StartQpc;
        public long EndQpc;
        public uint Tid;
        public PathLegKind Kind;
    }

    internal struct FrameCertificate
    {
        public int FrameIndex;
        public long FromQpc;
        public long ToQpc;
        public double FrameMs;
        public double ExcessMs;
        public double SpineReadyMs;
        public double SpineRunMs;
        public double SpineBlockedMs;
        public double Coverage;
        public double TiledMs;
        public double TileErrorMs;
        public PathStop Stop;
        public bool Clean;
        public bool Slow;
        public int Depth;
    }

    internal sealed class FramePathResult
    {
        public bool Known;
        public string Why = "";
        public int FramesWalked;
        public int SlowWalked;
        public int SlowCertified;
        public double Coverage;
        public double SlowCoverage;
        public double SpineExplained;
        public double SpineReadyMs;
        public double SlowExcessMs;
        public readonly Dictionary<uint, int> FrameHits = new Dictionary<uint, int>();
        public readonly Dictionary<uint, double> SpineReadyByTid = new Dictionary<uint, double>();
        public readonly HashSet<uint> Spine = new HashSet<uint>();
        public readonly int[] Stops = new int[9];
        public int DirtyEdges;
        public int DpcEdges;
        public int ForeignEdges;
        public uint AnchorTid;
        public int AnchorFrames;
        public int AnchorThreads;
        public List<FrameCertificate> Certs = new List<FrameCertificate>();
    }

    internal static class FramePathProver
    {
        private const double SlowFactor = 1.5;
        private const int MaxDepth = 24;
        private const int MaxSlowWalk = 250;
        private const int MaxNormalWalk = 150;
        private const double SpineRate = 0.5;
        internal const double MinCertifiedShare = 0.5;
        private const double CleanCoverage = 0.95;
        private const double TileTolerance = 0.05;

        public static FramePathResult Prove(long[] stamps, uint[] stampTids, RunSpan[] runs,
            PreemptSpan[] preempts, WakeEdge[] wakes, double budgetMs, long qpcFrequency)
        {
            var r = new FramePathResult();
            if (stamps == null || stampTids == null || stamps.Length < 2
                || stampTids.Length != stamps.Length)
            { r.Why = "没有带线程号的帧边界"; return r; }
            if (runs == null || runs.Length == 0) { r.Why = "没有在核区间"; return r; }
            if (qpcFrequency <= 0) { r.Why = "时钟频率无效"; return r; }

            double msPerTick = 1000.0 / qpcFrequency;
            var runIx = IndexRuns(runs);
            var preIx = IndexPreempts(preempts);
            var wakeIx = IndexWakes(wakes);
            var ourTids = new HashSet<uint>();
            foreach (uint t in runIx.Keys) ourTids.Add(t);

            double limit = budgetMs * SlowFactor;
            var slowIx = new List<int>();
            var normIx = new List<int>();
            for (int i = 0; i + 1 < stamps.Length; i++)
            {
                if (stamps[i + 1] <= stamps[i]) continue;
                if (stampTids[i + 1] == 0) continue;
                double ms = (stamps[i + 1] - stamps[i]) * msPerTick;
                if (ms > limit) slowIx.Add(i); else normIx.Add(i);
            }

            var tally = new Dictionary<uint, int>();
            for (int i = 1; i < stampTids.Length; i++)
            {
                uint t = stampTids[i];
                if (t == 0) continue;
                int c; tally.TryGetValue(t, out c); c++; tally[t] = c;
                if (c > r.AnchorFrames) { r.AnchorFrames = c; r.AnchorTid = t; }
            }
            r.AnchorThreads = tally.Count;

            var legs = new List<PathLeg>();
            double covSum = 0; int covN = 0;
            double slowCovSum = 0;

            for (int k = 0; k < slowIx.Count && k < MaxSlowWalk; k++)
            {
                int i = slowIx[k];
                legs.Clear();
                FrameCertificate c = Walk(i, stamps[i], stamps[i + 1], stampTids[i + 1],
                    runIx, preIx, wakeIx, ourTids, legs, r, msPerTick, budgetMs, true);
                slowCovSum += c.Coverage; covSum += c.Coverage; covN++;
                r.SlowWalked++;
                if (c.Clean) r.SlowCertified++;
                r.Certs.Add(c);
                Tally(legs, r, msPerTick, c.Clean);
            }
            int step = normIx.Count > MaxNormalWalk ? normIx.Count / MaxNormalWalk : 1;
            for (int k = 0; k < normIx.Count; k += step)
            {
                int i = normIx[k];
                legs.Clear();
                FrameCertificate c = Walk(i, stamps[i], stamps[i + 1], stampTids[i + 1],
                    runIx, preIx, wakeIx, ourTids, legs, r, msPerTick, budgetMs, false);
                covSum += c.Coverage; covN++;
                Tally(legs, r, msPerTick, c.Clean);
            }

            r.FramesWalked = covN;
            r.Coverage = covN > 0 ? covSum / covN : 0;
            r.SlowCoverage = r.SlowWalked > 0 ? slowCovSum / r.SlowWalked : 0;
            if (covN == 0) { r.Why = "没有可回溯的帧"; return r; }

            foreach (KeyValuePair<uint, int> kv in r.FrameHits)
                if ((double)kv.Value / covN >= SpineRate) r.Spine.Add(kv.Key);
            if (r.AnchorTid != 0 && ourTids.Contains(r.AnchorTid)) r.Spine.Add(r.AnchorTid);

            double ex = 0, spine = 0;
            for (int i = 0; i < r.Certs.Count; i++)
            {
                FrameCertificate c = r.Certs[i];
                if (!c.Slow) continue;
                ex += c.ExcessMs;
                if (!c.Clean) continue;
                spine += c.SpineReadyMs < c.ExcessMs ? c.SpineReadyMs : c.ExcessMs;
            }
            r.SlowExcessMs = ex;
            r.SpineReadyMs = spine;
            r.SpineExplained = ex > 0 ? spine / ex : 0;

            double certShare = r.SlowWalked > 0 ? (double)r.SlowCertified / r.SlowWalked : 0;
            r.Known = r.Spine.Count > 0 && (r.SlowWalked == 0 || certShare >= MinCertifiedShare);
            if (!r.Known)
                r.Why = r.Spine.Count == 0 ? "没有线程进入 spine"
                    : "慢帧证书率不足 " + (certShare * 100).ToString("F0") + "%";
            return r;
        }

        private static FrameCertificate Walk(int frameIndex, long f0, long f1, uint presentTid,
            Dictionary<uint, RunSpan[]> runIx, Dictionary<uint, PreemptSpan[]> preIx,
            Dictionary<uint, WakeEdge[]> wakeIx, HashSet<uint> ourTids,
            List<PathLeg> legs, FramePathResult r, double msPerTick, double budgetMs, bool slow)
        {
            var cert = new FrameCertificate();
            cert.FrameIndex = frameIndex;
            cert.FromQpc = f0; cert.ToQpc = f1;
            cert.FrameMs = (f1 - f0) * msPerTick;
            cert.ExcessMs = cert.FrameMs - budgetMs;
            if (cert.ExcessMs < 0) cert.ExcessMs = 0;
            cert.Slow = slow;

            if (f1 <= f0 || presentTid == 0 || !runIx.ContainsKey(presentTid))
            { cert.Stop = PathStop.NoAnchor; r.Stops[(int)PathStop.NoAnchor]++; return cert; }

            uint cur = presentTid;
            long t = f1;
            int depth = 0;
            PathStop stop = PathStop.DepthLimit;

            while (t > f0)
            {
                if (depth >= MaxDepth) { stop = PathStop.DepthLimit; break; }
                long before = t;

                long rs, re;
                if (!FindRunCovering(runIx, cur, t, out rs, out re))
                { stop = PathStop.NoRunSpan; break; }
                Emit(legs, cur, rs > f0 ? rs : f0, t, PathLegKind.Running);
                t = rs;
                if (t <= f0) { stop = PathStop.FrameStart; break; }

                long ready = t;
                long qstart;
                if (FindPreemptEndingAt(preIx, cur, t, out qstart) && qstart < t)
                {
                    Emit(legs, cur, qstart > f0 ? qstart : f0, t, PathLegKind.ReadyWait);
                    ready = qstart;
                    t = qstart;
                    if (t <= f0) { stop = PathStop.FrameStart; break; }
                }

                WakeEdge w;
                if (FindWakeAt(wakeIx, cur, ready, out w))
                {
                    bool usable = !w.FromDpc && !w.Unverified
                        && w.WakerTid != 0 && w.WakerTid != cur && ourTids.Contains(w.WakerTid);
                    if (usable)
                    {
                        cur = w.WakerTid;
                        t = ready;
                        depth++;
                    }
                    else
                    {
                        long prevEnd = PrevRunEnd(runIx, cur, ready);
                        long bs = prevEnd > f0 ? prevEnd : f0;
                        if (ready > bs) Emit(legs, cur, bs, ready, PathLegKind.BlockedWait);
                        t = bs;
                        if (t <= f0) { stop = PathStop.FrameStart; break; }
                        if (w.FromDpc) { r.DpcEdges++; stop = PathStop.DpcContext; break; }
                        if (w.Unverified) { r.DirtyEdges++; stop = PathStop.UnknownWaker; break; }
                        if (w.WakerTid == 0 || w.WakerTid == cur) { stop = PathStop.Timer; break; }
                        r.ForeignEdges++; stop = PathStop.ForeignWaker; break;
                    }
                }
                else if (ready == before)
                {
                    stop = PathStop.NoWake;
                    break;
                }
                else
                {
                    depth++;
                }

                if (t >= before) { stop = PathStop.NoRunSpan; break; }
            }
            if (t <= f0) stop = PathStop.FrameStart;

            r.Stops[(int)stop]++;
            cert.Stop = stop;
            cert.Depth = depth;
            long tail = t > f0 ? t : f0;
            cert.Coverage = f1 > f0 ? (double)(f1 - tail) / (f1 - f0) : 0;
            if (cert.Coverage > 1) cert.Coverage = 1;
            if (cert.Coverage < 0) cert.Coverage = 0;

            for (int i = 0; i < legs.Count; i++)
            {
                double ms = (legs[i].EndQpc - legs[i].StartQpc) * msPerTick;
                if (legs[i].Kind == PathLegKind.ReadyWait) cert.SpineReadyMs += ms;
                else if (legs[i].Kind == PathLegKind.Running) cert.SpineRunMs += ms;
                else cert.SpineBlockedMs += ms;
            }
            cert.TiledMs = cert.SpineRunMs + cert.SpineReadyMs + cert.SpineBlockedMs;
            double covered = (f1 - tail) * msPerTick;
            cert.TileErrorMs = cert.TiledMs - covered;

            double tol = covered * TileTolerance + 0.05;
            cert.Clean = cert.Coverage >= CleanCoverage
                && cert.TileErrorMs < tol && cert.TileErrorMs > -tol;
            return cert;
        }

        private static void Emit(List<PathLeg> legs, uint tid, long a, long b, PathLegKind kind)
        {
            if (b <= a) return;
            var l = new PathLeg();
            l.Tid = tid; l.StartQpc = a; l.EndQpc = b; l.Kind = kind;
            legs.Add(l);
        }

        private static void Tally(List<PathLeg> legs, FramePathResult r, double msPerTick, bool clean)
        {
            var seen = new HashSet<uint>();
            for (int i = 0; i < legs.Count; i++)
            {
                seen.Add(legs[i].Tid);
                if (!clean || legs[i].Kind != PathLegKind.ReadyWait) continue;
                double ms = (legs[i].EndQpc - legs[i].StartQpc) * msPerTick;
                double cur;
                r.SpineReadyByTid.TryGetValue(legs[i].Tid, out cur);
                r.SpineReadyByTid[legs[i].Tid] = cur + ms;
            }
            foreach (uint t in seen)
            {
                int c; r.FrameHits.TryGetValue(t, out c);
                r.FrameHits[t] = c + 1;
            }
        }

        private static Dictionary<uint, RunSpan[]> IndexRuns(RunSpan[] a)
        {
            var by = new Dictionary<uint, List<RunSpan>>();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].EndQpc <= a[i].StartQpc) continue;
                List<RunSpan> l;
                if (!by.TryGetValue(a[i].Tid, out l)) { l = new List<RunSpan>(); by[a[i].Tid] = l; }
                l.Add(a[i]);
            }
            var outp = new Dictionary<uint, RunSpan[]>();
            foreach (KeyValuePair<uint, List<RunSpan>> kv in by)
            {
                RunSpan[] arr = kv.Value.ToArray();
                bool sorted = true;
                for (int i = 1; i < arr.Length; i++)
                    if (arr[i].StartQpc < arr[i - 1].StartQpc) { sorted = false; break; }
                if (!sorted)
                    Array.Sort(arr, delegate (RunSpan x, RunSpan y) { return x.StartQpc.CompareTo(y.StartQpc); });
                outp[kv.Key] = arr;
            }
            return outp;
        }

        private static Dictionary<uint, PreemptSpan[]> IndexPreempts(PreemptSpan[] a)
        {
            var outp = new Dictionary<uint, PreemptSpan[]>();
            if (a == null) return outp;
            var by = new Dictionary<uint, List<PreemptSpan>>();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].EndQpc <= a[i].StartQpc) continue;
                List<PreemptSpan> l;
                if (!by.TryGetValue(a[i].VictimTid, out l)) { l = new List<PreemptSpan>(); by[a[i].VictimTid] = l; }
                l.Add(a[i]);
            }
            foreach (KeyValuePair<uint, List<PreemptSpan>> kv in by)
            {
                PreemptSpan[] arr = kv.Value.ToArray();
                bool sorted = true;
                for (int i = 1; i < arr.Length; i++)
                    if (arr[i].EndQpc < arr[i - 1].EndQpc) { sorted = false; break; }
                if (!sorted)
                    Array.Sort(arr, delegate (PreemptSpan x, PreemptSpan y) { return x.EndQpc.CompareTo(y.EndQpc); });
                outp[kv.Key] = arr;
            }
            return outp;
        }

        private static Dictionary<uint, WakeEdge[]> IndexWakes(WakeEdge[] a)
        {
            var outp = new Dictionary<uint, WakeEdge[]>();
            if (a == null) return outp;
            var by = new Dictionary<uint, List<WakeEdge>>();
            for (int i = 0; i < a.Length; i++)
            {
                List<WakeEdge> l;
                if (!by.TryGetValue(a[i].WokenTid, out l)) { l = new List<WakeEdge>(); by[a[i].WokenTid] = l; }
                l.Add(a[i]);
            }
            foreach (KeyValuePair<uint, List<WakeEdge>> kv in by)
            {
                WakeEdge[] arr = kv.Value.ToArray();
                bool sorted = true;
                for (int i = 1; i < arr.Length; i++)
                    if (arr[i].Qpc < arr[i - 1].Qpc) { sorted = false; break; }
                if (!sorted)
                    Array.Sort(arr, delegate (WakeEdge x, WakeEdge y) { return x.Qpc.CompareTo(y.Qpc); });
                outp[kv.Key] = arr;
            }
            return outp;
        }

        private static bool FindRunCovering(Dictionary<uint, RunSpan[]> ix, uint tid, long t,
            out long start, out long end)
        {
            start = 0; end = 0;
            RunSpan[] a;
            if (!ix.TryGetValue(tid, out a)) return false;
            int lo = 0, hi = a.Length - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (a[mid].StartQpc < t) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (best < 0) return false;
            if (a[best].EndQpc < t) return false;
            start = a[best].StartQpc; end = a[best].EndQpc;
            return true;
        }

        private static bool FindPreemptEndingAt(Dictionary<uint, PreemptSpan[]> ix, uint tid, long t,
            out long start)
        {
            start = 0;
            PreemptSpan[] a;
            if (!ix.TryGetValue(tid, out a)) return false;
            int lo = 0, hi = a.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (a[mid].EndQpc == t) { start = a[mid].StartQpc; return true; }
                if (a[mid].EndQpc < t) lo = mid + 1; else hi = mid - 1;
            }
            return false;
        }

        private static bool FindWakeAt(Dictionary<uint, WakeEdge[]> ix, uint tid, long t, out WakeEdge w)
        {
            w = new WakeEdge();
            WakeEdge[] a;
            if (!ix.TryGetValue(tid, out a)) return false;
            int lo = 0, hi = a.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (a[mid].Qpc == t) { w = a[mid]; return true; }
                if (a[mid].Qpc < t) lo = mid + 1; else hi = mid - 1;
            }
            return false;
        }

        private static long PrevRunEnd(Dictionary<uint, RunSpan[]> ix, uint tid, long t)
        {
            RunSpan[] a;
            if (!ix.TryGetValue(tid, out a)) return 0;
            long best = 0;
            int lo = 0, hi = a.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (a[mid].StartQpc < t)
                {
                    if (a[mid].EndQpc <= t && a[mid].EndQpc > best) best = a[mid].EndQpc;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return best;
        }
    }
}
