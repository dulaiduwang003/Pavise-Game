// @author bdth 2074055628@qq.com
// 文件用途 对局中的帧归因编排 开帧时钟与中断归因 周期评估 结束报告给一句结论
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace PaviseApp
{
    internal static class FrameDiagnostics
    {
        internal static int EvalIntervalSeconds = 30;
        internal static int BudgetIntervalSeconds = 5;
        private const double AttributedShare = FrameFaultLedger.ConvictShare;

        private static readonly object lk = new object();
        private static FrameClock clock;
        private static InterruptAttribution intr;
        private static CpuContentionProbe cpu;
        private static DiskStallProbe disk;
        private static ObserverBudget budget;
        private static long nextEvalTicks;
        private static long nextBudgetTicks;
        private static bool running;
        private static bool gaveUp;
        private static string stopReason;

        private static int windows;
        private static int slowFramesTotal;
        private static int framesTotal;
        private static int attributedWindows;
        private static int intrWindows;
        private static int preemptWindows;
        private static int mixedWindows;
        private static int diskWindows;
        private static double diskStallMs;
        private static bool diskOn;
        private static uint topOffenderPid;
        private static double topOffenderMs;
        private static double lastPreemptExplained;
        private static double lastIntrExplained;
        private static string cpuWhy;
        private const int MaxWindowRecords = 240;
        private static readonly List<string> windowLog = new List<string>();
        private static long lastEvalQpc;
        private static long lastWindowEndQpc;
        private static readonly Dictionary<uint, double> prevOcc = new Dictionary<uint, double>();
        private static double occTopMs;
        private static string occTopName;
        private static int freqMismatch;
        private static int cpuTries;
        private static int targetPidForCpu;
        private const int MaxCpuTries = 5;
        private static string topOffenderName;
        private static double worstShare;
        private static double worstMaxUs;
        private static string worstDriver;
        private static double peakSelfCpu;
        private static long lostEvents;
        private static bool actionFrozen;
        private static double budgetMs;
        private const double NotableOfBudget = 0.25;
        private const int MinSlowFramesToAct = 20;
        private const double MinWindowSecondsToAct = 5.0;
        private const double MinStarvedMsToAct = 50.0;
        private const double MaxUnknownShareToAct = 0.35;
        private const int MinCertifiedFramesToAct = 20;
        private const double MinCertShareToAct = 0.6;
        private const double MinSpineExplainedToAct = 0.5;
        private static HashSet<uint> eligibleTids;
        private static FrameFaultVerdict lastVerdict;
        private static DriverInterrupt lastWorstDriver;
        private static FrameWindow lastWindow;

        public static bool Running { get { lock (lk) return running; } }

        public static RemedyPlan CurrentRemedy()
        {
            FrameFaultVerdict v;
            DriverInterrupt worst;
            lock (lk) { v = lastVerdict; worst = lastWorstDriver; }
            return FrameRemedy.Plan(v, worst);
        }

        public static FrameFaultVerdict LastVerdict { get { lock (lk) return lastVerdict; } }

        public static FrameWindow LastWindow { get { lock (lk) return lastWindow; } }
        public static string[] WindowRows() { lock (lk) return windowLog.ToArray(); }
        public static double PeakSelfCpu { get { lock (lk) return peakSelfCpu; } }
        public static long LostEvents { get { lock (lk) return lostEvents; } }
        public static bool ActionFrozen { get { lock (lk) return actionFrozen; } }
        public static string StopReason { get { lock (lk) return stopReason; } }

        internal static string DebugState()
        {
            CpuContentionProbe cc;
            int wins, iw, pw, mw;
            double pe, ie;
            lock (lk) { cc = cpu; wins = windows; iw = intrWindows; pw = preemptWindows; mw = mixedWindows;
                        pe = lastPreemptExplained; ie = lastIntrExplained; }
            string s = "窗口 " + wins + "  中断窗 " + iw + "  抢占窗 " + pw + "  混合窗 " + mw
                + "  磁盘窗 " + diskWindows + (diskOn ? "" : "(未启用)")
                + "  末次超额解释 中断 " + (ie * 100).ToString("F1") + "% 抢占 " + (pe * 100).ToString("F1") + "%";
            if (cc == null) return s + "  CPU探针=未启动 " + (cpuWhy ?? "?");
            try
            {
                CpuContentionResult r = cc.Snapshot();
                return s + "  CPU探针 tid=" + r.TargetTid + " 命中切换 " + r.TargetSwitches
                    + " 排队段 " + r.Preempts.Length;
            }
            catch { return s + "  CPU探针=快照失败"; }
        }

        public static void Reset()
        {
            Stop();
            lock (lk)
            {
                windows = 0; slowFramesTotal = 0; framesTotal = 0; attributedWindows = 0;
                worstShare = 0; worstMaxUs = 0; worstDriver = null;
                peakSelfCpu = 0; lostEvents = 0; stopReason = null; budgetMs = 0;
                intrWindows = 0; preemptWindows = 0; mixedWindows = 0;
                diskWindows = 0; diskStallMs = 0; diskOn = false;
                topOffenderPid = 0; topOffenderMs = 0; topOffenderName = null; cpuWhy = null;
                cpuTries = 0; targetPidForCpu = 0; lastEvalQpc = 0; freqMismatch = 0;
                lastWindowEndQpc = 0;
                actionFrozen = false;
                prevOcc.Clear(); occTopMs = 0; occTopName = null;
                eligibleTids = null;
                windowLog.Clear();
                gaveUp = false;
            }
        }

        public static void EnsureAndSample(bool enabled, bool actEnabled, int rendererPid)
        {
            if (!enabled)
            {
                if (Running) { Stop(); FrameOffenderPolicy.Reset(false); }
                return;
            }
            if (!Running) { TryStart(rendererPid); FrameOffenderPolicy.Reset(actEnabled); }
            else if (FrameOffenderPolicy.Enabled != actEnabled) FrameOffenderPolicy.Reset(actEnabled);
            SampleIfDue();
        }

        private static void TryStart(int rendererPid)
        {
            lock (lk) { if (gaveUp || running) return; }
            if (rendererPid <= 0) return;
            if (!Native.IsElevated())
            {
                lock (lk) { stopReason = Lang.T("t.framediag.1"); gaveUp = true; }
                return;
            }
            HealFromCrash();

            int hzCur = 0, hzBest = 0;
            try { DisplayGuard.QueryRefreshRates(out hzCur, out hzBest); } catch { }
            double hz = hzCur > 1 ? hzCur : 0;

            var fc = new FrameClock();
            var ia = new InterruptAttribution();
            bool fcOn = false, iaOn = false;
            try { fcOn = fc.Start(rendererPid, hz); } catch { }
            if (!fcOn) { lock (lk) { stopReason = Lang.T("t.framediag.2"); gaveUp = true; } return; }
            try { iaOn = ia.Start(); } catch { }
            if (!iaOn)
            {
                try { fc.Stop(); } catch { }
                lock (lk) { stopReason = Lang.T("t.framediag.3"); gaveUp = true; }
                return;
            }

            targetPidForCpu = rendererPid;
            cpuTries = 0;
            CpuContentionProbe cc = TryStartCpuProbe(rendererPid, null);

            DiskStallProbe ds = new DiskStallProbe();
            bool dsOn = false;
            try { dsOn = ds.Start(rendererPid); } catch { }
            if (!dsOn) { try { ds.Stop(); } catch { } ds = null; }

            long now = DateTime.UtcNow.Ticks;
            var ob = new ObserverBudget("PaviseFrameClock", "PaviseInterruptProbe",
                "PaviseCpuContention", "PaviseDiskStall");
            ob.Sample();
            lock (lk)
            {
                clock = fc; intr = ia; cpu = cc; disk = ds; diskOn = ds != null;
                budget = ob; running = true;
                budgetMs = hz > 1 ? 1000.0 / hz : 0;
                nextEvalTicks = now + EvalIntervalSeconds * TimeSpan.TicksPerSecond;
                nextBudgetTicks = now + BudgetIntervalSeconds * TimeSpan.TicksPerSecond;
            }
            Logger.Log(Lang.T("log.framediag.1") + rendererPid + (hz > 1 ? "  " + hz + "Hz" : ""));
        }

        private static CpuContentionProbe TryStartCpuProbe(int rendererPid, FrameClock fc)
        {
            if (rendererPid <= 0) return null;
            cpuTries++;
            var probe = new CpuContentionProbe();
            bool ccOn = false;
            string err = null;
            try { ccOn = probe.Start(rendererPid); }
            catch (Exception ex) { err = ex.GetType().Name; }
            if (ccOn) { cpuWhy = null; return probe; }
            try { probe.Stop(); } catch { }
            cpuWhy = "会话起不来 第 " + cpuTries + " 次" + (err != null ? " " + err : "");
            return null;
        }

        private static void SampleIfDue()
        {
            FrameClock fc;
            InterruptAttribution ia;
            CpuContentionProbe cc;
            DiskStallProbe ds;
            ObserverBudget ob;
            bool evalDue = false, budgetDue = false;
            long now = DateTime.UtcNow.Ticks;
            lock (lk)
            {
                if (!running) return;
                fc = clock; ia = intr; cc = cpu; ds = disk; ob = budget;
                if (now >= nextBudgetTicks)
                {
                    budgetDue = true;
                    nextBudgetTicks = now + BudgetIntervalSeconds * TimeSpan.TicksPerSecond;
                }
                if (now >= nextEvalTicks)
                {
                    evalDue = true;
                    nextEvalTicks = now + EvalIntervalSeconds * TimeSpan.TicksPerSecond;
                }
            }
            if (fc == null || ia == null) return;

            if (budgetDue && ob != null)
            {
                ObserverCost c = ob.Sample();
                lock (lk)
                {
                    if (c.SelfCpuShare > peakSelfCpu) peakSelfCpu = c.SelfCpuShare;
                    long add = c.EventsLost + c.RealTimeBuffersLost;
                    lostEvents += add;
                    if (add > 0)
                    {
                        actionFrozen = true;
                        FrameOffenderPolicy.Freeze(Lang.T("t.framediag.18"));
                    }
                }
                if (ob.ShouldDegrade)
                {
                    Logger.Log(Lang.T("log.framediag.2") + ObserverBudget.Describe(c));
                    lock (lk) { stopReason = Lang.T("t.framediag.4"); gaveUp = true; }
                    Stop();
                    return;
                }
            }

            if (evalDue && cc == null && cpuTries < MaxCpuTries)
            {
                CpuContentionProbe retry = TryStartCpuProbe(targetPidForCpu, fc);
                if (retry != null)
                {
                    lock (lk) { if (running && cpu == null) { cpu = retry; cc = retry; } else retry.Stop(); }
                    if (cc != null) Logger.Log(Lang.T("log.framediag.3") + cpuTries);
                }
            }
            if (evalDue) Evaluate(fc, ia, cc, ds);
        }

        private static void Evaluate(FrameClock fc, InterruptAttribution ia,
            CpuContentionProbe cc, DiskStallProbe ds)
        {
            FrameWindow w;
            long[] stamps;
            uint[] stampTids;
            try
            {
                w = fc.Snapshot(200000);
                stamps = fc.CopyRecentStamps(200000);
                stampTids = fc.CopyRecentStampTids(200000);
                if (stamps != null && stampTids != null && stamps.Length != stampTids.Length)
                {
                    int n = Math.Min(stamps.Length, stampTids.Length);
                    var s2 = new long[n]; var t2 = new uint[n];
                    Array.Copy(stamps, stamps.Length - n, s2, 0, n);
                    Array.Copy(stampTids, stampTids.Length - n, t2, 0, n);
                    stamps = s2; stampTids = t2;
                }
            }
            catch { return; }
            if (w == null || !w.Enough || w.BudgetMs <= 0) return;

            InterruptSample[] samples = ia.SnapshotLongSamples();
            PreemptSpan[] preempts = null;
            RunSpan[] runSpans = null;
            Dictionary<uint, double> occ = null;
            WaitSpan[] waitSpans = null;
            WakeEdge[] wakeEdges = null;
            long preemptFrom = 0;
            long runFrom = 0;
            long occOverflow = 0, waitOverflow = 0;
            int starveCpus = 0;
            if (cc != null)
            {
                try
                {
                    CpuContentionResult cr = cc.Snapshot();
                    preempts = cr.TargetSwitches > 0 ? cr.Preempts : null;
                    waitSpans = cr.Waits;
                    wakeEdges = cr.Wakes;
                    occ = cr.StarvedOccupancyMs;
                    occOverflow = cr.OccupancyOverflow;
                    waitOverflow = cr.OpenWaitOverflow;
                    starveCpus = cr.StarveCpuCount;
                    try
                    {
                        using (Process gp = Process.GetProcessById(targetPidForCpu))
                            cc.SetAffinityMask((long)gp.ProcessorAffinity);
                    }
                    catch { }
                    runSpans = cr.Runs;
                    if (cr.PreemptsDropped > 0 && preempts.Length > 0) preemptFrom = preempts[0].EndQpc;
                    if (cr.RunsDropped > 0 && runSpans.Length > 0) runFrom = runSpans[0].EndQpc;
                }
                catch { }
            }
            StallSpan[] stalls = null;
            long stallFrom = 0;
            if (ds != null)
            {
                try
                {
                    DiskStallResult dr = ds.Snapshot();
                    stalls = dr.Stalls;
                    if (dr.StallsDropped > 0 && stalls.Length > 0) stallFrom = stalls[0].EndQpc;
                }
                catch { }
            }
            long from = lastEvalQpc;
            if (preemptFrom > from) from = preemptFrom;
            if (runFrom > from) from = runFrom;
            if (stallFrom > from) from = stallFrom;
            long[] aligned = ClipFrom(stamps, from);
            uint[] alignedTids = ClipTidsFrom(stamps, stampTids, from);
            if (aligned.Length >= 2) lastEvalQpc = aligned[aligned.Length - 1];
            long freq = ia.QpcFrequency > 0 ? ia.QpcFrequency : fc.QpcFrequency;
            if (!FreqAgrees(freq, fc.QpcFrequency) || (cc != null && !FreqAgrees(freq, cc.QpcFrequency))
                || (ds != null && !FreqAgrees(freq, ds.QpcFrequency)))
            {
                lock (lk) { if (freqMismatch == 0) Logger.Log(Lang.T("log.framediag.5")); freqMismatch++; }
                return;
            }
            HashSet<uint> elig;
            lock (lk) { elig = eligibleTids; }
            uint presentTid = 0;
            try { presentTid = (uint)fc.TopPresentThread(); }
            catch { }
            FrameFaultVerdict v = FrameFaultLedger.Attribute(aligned, samples, preempts, runSpans,
                stalls, waitSpans, elig, presentTid, w.BudgetMs, freq);

            FramePathResult path = null;
            try
            {
                path = FramePathProver.Prove(aligned, alignedTids, runSpans, preempts,
                    wakeEdges, w.BudgetMs, freq);
            }
            catch { path = null; }
            if (path != null)
            {
                v.PathKnown = path.Known;
                v.PathWhy = path.Why;
                v.PathCertShare = path.SlowWalked > 0
                    ? (double)path.SlowCertified / path.SlowWalked : 0;
                v.PathCoverage = path.Coverage;
                v.PathSpineExplained = path.SpineExplained;
                v.PathSpineReadyMs = path.SpineReadyMs;
                v.PathSpineCount = path.Spine.Count;
                v.PathSlowWalked = path.SlowWalked;
                v.PathAnchorTid = path.AnchorTid;
            }

            DriverInterrupt worst = ia.PeekWorstByDuration();
            string offender = v.TopOffenderPid != 0 ? ProcName(v.TopOffenderPid) : null;
            int newFrames = aligned.Length > 1 ? aligned.Length - 1 : 0;
            lock (lk)
            {
                windows++;
                framesTotal += newFrames;
                slowFramesTotal += v.SlowFrames;
                if (v.SlowFrames > 0 && v.SlowIntrShare >= AttributedShare) attributedWindows++;
                if (v.SlowIntrShare > worstShare) worstShare = v.SlowIntrShare;
                lastVerdict = v;
                lastWorstDriver = worst;
                lastWindow = w;
                lastPreemptExplained = v.PreemptExplained;
                lastIntrExplained = v.IntrExplained;
                if (v.Conclusion == FaultConclusion.Interrupts) intrWindows++;
                else if (v.Conclusion == FaultConclusion.CpuPreemption) preemptWindows++;
                else if (v.Conclusion == FaultConclusion.Mixed) mixedWindows++;
                else if (v.Conclusion == FaultConclusion.DiskStall) diskWindows++;
                if (v.SlowFrames > 0) diskStallMs += v.DiskExplained * v.TotalExcessMs;
                if (v.TopOffenderMs > topOffenderMs)
                {
                    topOffenderMs = v.TopOffenderMs;
                    topOffenderPid = v.TopOffenderPid;
                    if (offender != null) topOffenderName = offender;
                }
                if (worst != null && worst.DpcMaxUs > worstMaxUs)
                {
                    worstMaxUs = worst.DpcMaxUs;
                    worstDriver = worst.Driver;
                }
            }

            if (aligned.Length < 2) return;

            uint actPid = 0;
            double actShare = 0, actMs = 0, idleShare = 0, occTotal = 0;
            Dictionary<uint, double> occDelta = null;
            if (occ != null)
            {
                occDelta = WindowDelta(occ, out occTotal);
                PickByOccupancy(occDelta, occTotal, out actPid, out actShare, out actMs, out idleShare);
            }
            string actName = actPid != 0 ? ProcName(actPid) : null;
            double unknownShare = occTotal > 0 ? ShareOf(occDelta, occTotal, 0xFFFFFFFEu) : 1.0;
            double selfShare = ShareOf(occDelta, occTotal, CpuContentionProbe.SelfPid);
            lock (lk)
            {
                if (actMs > occTopMs) { occTopMs = actMs; occTopName = actName; }
            }

            RecordWindow(v, w, aligned, offender, actName, actShare, idleShare, occTotal,
                unknownShare, selfShare, starveCpus);

            double windowSeconds = freq > 0 && aligned.Length >= 2
                ? (aligned[aligned.Length - 1] - aligned[0]) / (double)freq : 0;
            bool clipped = from > lastWindowEndQpc && lastWindowEndQpc > 0;
            bool windowUsable = !actionFrozen
                && windowSeconds >= MinWindowSecondsToAct
                && unknownShare <= MaxUnknownShareToAct
                && !clipped
                && v.EligibleKnown
                && occOverflow == 0 && waitOverflow == 0;
            bool pathOk = v.PathKnown
                && v.PathSlowWalked >= MinCertifiedFramesToAct
                && v.PathCertShare >= MinCertShareToAct
                && v.PathSpineExplained >= MinSpineExplainedToAct;
            bool mayAct = windowUsable
                && v.SlowFrames >= MinSlowFramesToAct
                && occTotal >= MinStarvedMsToAct
                && actShare > idleShare
                && v.PreemptHitEligible
                && pathOk;
            lastWindowEndQpc = aligned[aligned.Length - 1];

            HashSet<uint> nextElig = FrameFaultLedger.HotThreads(runSpans, aligned[0],
                aligned[aligned.Length - 1]);
            lock (lk) { eligibleTids = nextElig.Count > 0 ? nextElig : null; }
            if (cc != null) { try { cc.SetEligibleThreads(nextElig); } catch { } }

            if (!windowUsable) FrameOffenderPolicy.CancelPending();
            else FrameOffenderPolicy.ReportWindow(
                ShareOf(occDelta, occTotal, FrameOffenderPolicy.PidForTest),
                v.SlowFrames + v.NormalFrames > 0
                    ? v.TotalExcessMs / (v.SlowFrames + v.NormalFrames) : 0);
            if (mayAct && actPid != 0
                && (v.Conclusion == FaultConclusion.CpuPreemption || v.Conclusion == FaultConclusion.Mixed))
            {
                int frames = v.SlowFrames + v.NormalFrames;
                DateTime winStart = DateTime.Now;
                try
                {
                    double back = (System.Diagnostics.Stopwatch.GetTimestamp() - aligned[0]) / (double)freq;
                    if (back > 0 && back < 86400) winStart = DateTime.Now.AddSeconds(-back);
                }
                catch { }
                FrameOffenderPolicy.Nominate(actPid, actName, actMs, actShare,
                    frames > 0 ? v.TotalExcessMs / frames : 0, winStart);
            }
        }

        private static Dictionary<uint, double> WindowDelta(Dictionary<uint, double> cumulative,
            out double total)
        {
            var delta = new Dictionary<uint, double>();
            total = 0;
            foreach (KeyValuePair<uint, double> kv in cumulative)
            {
                double before;
                prevOcc.TryGetValue(kv.Key, out before);
                double d = kv.Value - before;
                if (d < 0) d = 0;
                delta[kv.Key] = d;
                total += d;
            }
            foreach (KeyValuePair<uint, double> kv in cumulative) prevOcc[kv.Key] = kv.Value;
            return delta;
        }

        private static void PickByOccupancy(Dictionary<uint, double> delta, double total,
            out uint pid, out double share, out double ms, out double idleShare)
        {
            pid = 0; share = 0; ms = 0; idleShare = 0;
            if (delta == null || total <= 0) return;
            double idle;
            delta.TryGetValue(CpuContentionProbe.IdlePid, out idle);
            idleShare = idle / total;
            foreach (KeyValuePair<uint, double> kv in delta)
            {
                if (kv.Key == CpuContentionProbe.IdlePid || kv.Key == CpuContentionProbe.UnknownPid
                    || kv.Key == CpuContentionProbe.SelfPid || kv.Key == 0) continue;
                if (kv.Value > ms) { ms = kv.Value; pid = kv.Key; }
            }
            share = ms / total;
        }

        private static double ShareOf(Dictionary<uint, double> delta, double total, uint who)
        {
            double ms;
            if (who == 0 || delta == null || total <= 0 || !delta.TryGetValue(who, out ms)) return 0;
            return ms / total;
        }

        private static bool FreqAgrees(long a, long b)
        {
            return a > 0 && b > 0 && a == b;
        }

        internal static uint[] ClipTidsFrom(long[] stamps, uint[] tids, long fromQpc)
        {
            if (stamps == null || tids == null || tids.Length != stamps.Length) return new uint[0];
            if (fromQpc <= 0 || stamps.Length == 0) return tids;
            int i = 0;
            while (i < stamps.Length && stamps[i] < fromQpc) i++;
            if (i == 0) return tids;
            var outp = new uint[stamps.Length - i];
            Array.Copy(tids, i, outp, 0, outp.Length);
            return outp;
        }

        internal static long[] ClipFrom(long[] stamps, long fromQpc)
        {
            if (fromQpc <= 0 || stamps == null || stamps.Length == 0) return stamps;
            int i = 0;
            while (i < stamps.Length && stamps[i] < fromQpc) i++;
            if (i == 0) return stamps;
            if (i >= stamps.Length - 1) return new long[0];
            var outp = new long[stamps.Length - i];
            Array.Copy(stamps, i, outp, 0, outp.Length);
            return outp;
        }

        private static void RecordWindow(FrameFaultVerdict v, FrameWindow w, long[] aligned,
            string offender, string actName, double actShare, double idleShare, double occTotal,
            double unknownShare, double selfShare, int starveCpus)
        {
            var sb = new StringBuilder();
            sb.Append("窗口 ").Append(windows)
              .Append("  本窗帧 ").Append(aligned.Length > 1 ? aligned.Length - 1 : 0)
              .Append("  慢帧 ").Append(v.SlowFrames)
              .Append("  慢帧判据 ").Append(v.SlowLimitMs.ToString("F2")).Append("ms")
              .Append("  超额合计 ").Append(v.TotalExcessMs.ToString("F0")).Append("ms");
            sb.Append("  |  中断占慢帧 ").Append((v.SlowIntrShare * 100).ToString("F2")).Append("%")
              .Append(" 超额解释 ").Append((v.IntrExplained * 100).ToString("F1")).Append("%");
            if (v.HasPreemptData)
                sb.Append("  |  抢占占慢帧 ").Append((v.SlowPreemptShare * 100).ToString("F2")).Append("%")
                  .Append(" 超额解释 ").Append((v.PreemptExplained * 100).ToString("F1")).Append("%")
                  .Append(" 只算干活线程 ").Append((v.PreemptEligibleExplained * 100).ToString("F1")).Append("%")
                  .Append(" 只算最忙线程 ").Append((v.PreemptFrameThreadExplained * 100).ToString("F1")).Append("%")
                  .Append(" 只算呈现线程 ").Append((v.PreemptPresentThreadExplained * 100).ToString("F1")).Append("%")
                  .Append(v.PresentThreadEligible ? "(在资格集合里)" : "(不在资格集合里)")
                  .Append(" 干活线程数 ").Append(v.EligibleCount);
            else sb.Append("  |  抢占 无数据");
            if (v.PathSlowWalked > 0 || v.PathKnown)
                sb.Append("  |  关键路径 证书 ").Append((v.PathCertShare * 100).ToString("F0")).Append("%")
                  .Append(" 覆盖 ").Append((v.PathCoverage * 100).ToString("F0")).Append("%")
                  .Append(" spine 排队解释 ").Append((v.PathSpineExplained * 100).ToString("F1")).Append("%")
                  .Append(" spine 线程 ").Append(v.PathSpineCount)
                  .Append(" 锚点 tid ").Append(v.PathAnchorTid)
                  .Append(v.PathKnown ? "" : "  不可用 " + v.PathWhy);
            else sb.Append("  |  关键路径 无数据");
            if (v.HasDiskData)
            {
                sb.Append("  |  磁盘占慢帧 ").Append((v.SlowDiskShare * 100).ToString("F2")).Append("%")
                  .Append(" 超额解释 ").Append((v.DiskExplained * 100).ToString("F1")).Append("%");
                sb.Append(" 磁盘 I O 只观察 ").Append(v.DiskIoObservedMs.ToString("F0")).Append("ms");
                if (v.DiskIoUnproven > 0) sb.Append(" 证不出阻塞 ").Append(v.DiskIoUnproven);
                if (v.DiskOffThread > 0) sb.Append(" 挡的不是干活线程 ").Append(v.DiskOffThread);
                if (v.DiskThreadUnconstrained) sb.Append(" 无在核数据 未做线程约束");
            }
            else sb.Append("  |  磁盘 无数据");
            sb.Append("  |  合计解释 ").Append((v.CombinedExplained * 100).ToString("F1")).Append("%")
              .Append(" 重复计入 ").Append((v.OverlapExplained * 100).ToString("F1")).Append("%");
            if (v.IntrUnconstrained) sb.Append("  中断无在核数据 本窗不许定罪");
            if (v.HasDiskData && v.DiskThreadUnconstrained) sb.Append("  磁盘认不出干活线程 本窗不许定罪");
            if (actionFrozen) sb.Append("  丢过事件 自动处置已冻结");
            if (v.IntrDroppedOffCore > 0)
                sb.Append("  中断被同核约束丢弃 ").Append(v.IntrDroppedOffCore);
            if (offender != null)
                sb.Append("  顶替者证得实 ").Append(offender)
                  .Append("(").Append(v.TopOffenderMs.ToString("F0")).Append("ms)");
            if (actName != null)
                sb.Append("  饿着时占核最多 ").Append(actName)
                  .Append("(").Append((actShare * 100).ToString("F0")).Append("%)");
            if (idleShare > 0.01)
                sb.Append("  其中核是空的 ").Append((idleShare * 100).ToString("F0")).Append("%");
            if (actName != null)
                sb.Append("  饥饿样本 ").Append(occTotal.ToString("F0")).Append("ms")
                  .Append(" 可用核 ").Append(starveCpus);
            if (unknownShare > 0.01)
                sb.Append("  认不出是谁 ").Append((unknownShare * 100).ToString("F0")).Append("%");
            if (selfShare > 0.01)
                sb.Append("  自己占着 ").Append((selfShare * 100).ToString("F0")).Append("%");
            if (v.FrameThreadTid != 0)
                sb.Append("  |  最忙线程 tid ").Append(v.FrameThreadTid)
                  .Append(" 慢帧里 在跑 ").Append((v.FrameThreadRunShare * 100).ToString("F0"))
                  .Append("% 排队 ").Append((v.FrameThreadReadyShare * 100).ToString("F0"))
                  .Append("% 主动等 ").Append((v.FrameThreadWaitShare * 100).ToString("F0")).Append("%");
            sb.Append("  =>  ").Append(v.Conclusion);
            lock (lk)
            {
                if (windowLog.Count >= MaxWindowRecords) windowLog.RemoveAt(0);
                windowLog.Add(sb.ToString());
            }
        }

        public static bool ExportTo(string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(Lang.T("t.framediag.15"));
                sb.AppendLine("Pavise " + App.Version + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine();
                string[] rows;
                string why;
                double cpuPeak;
                long lost;
                lock (lk) { rows = windowLog.ToArray(); why = stopReason; cpuPeak = peakSelfCpu; lost = lostEvents; }
                sb.AppendLine("观察层峰值占整机 CPU " + (cpuPeak * 100).ToString("F3")
                    + "%   预算 " + (ObserverBudget.CpuShareLimit * 100).ToString("F2")
                    + "%   丢事件 " + lost);
                if (why != null) sb.AppendLine("停止原因 " + why);
                sb.AppendLine();
                if (rows.Length == 0) sb.AppendLine(Lang.T("t.framediag.16"));
                foreach (string r in rows) sb.AppendLine(r);
                sb.AppendLine();
                string line = Summarize();
                if (line != null) sb.AppendLine("会话结论  " + line);
                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
                Logger.Log(Lang.T("log.framediag.4") + path);
                return true;
            }
            catch { return false; }
        }

        public static int WindowRecordCount { get { lock (lk) return windowLog.Count; } }

        public static string Summarize()
        {
            int wins, slow, frames, iw, pw, mw, dw;
            double dms;
            double budgetUs;
            double share, maxUs, cpuPct, offMs;
            long lost;
            string driver, why, offender;
            lock (lk)
            {
                dw = diskWindows; dms = diskStallMs;
                wins = windows; slow = slowFramesTotal; frames = framesTotal;
                share = worstShare; maxUs = worstMaxUs; driver = worstDriver;
                cpuPct = peakSelfCpu; lost = lostEvents; why = stopReason;
                iw = intrWindows; pw = preemptWindows; mw = mixedWindows;
                offender = occTopName ?? topOffenderName;
                offMs = occTopName != null ? occTopMs : topOffenderMs;
                budgetUs = budgetMs * 1000.0;
            }
            if (why != null && wins == 0) return why;
            if (wins == 0 || frames < 100) return null;

            if (lost > 0) return Lang.T("t.framediag.5");

            if (slow == 0)
                return Lang.F("t.framediag.6", frames.ToString(), (cpuPct * 100).ToString("0.000"));

            string head = Lang.F("t.framediag.7", slow.ToString(), frames.ToString());

            string act = FrameOffenderPolicy.Summarize();
            string tail = act != null ? Lang.F("t.framediag.14", act) : "";
            if (mw > 0 && mw >= iw && mw >= pw)
                return head + Lang.F("t.framediag.11", mw.ToString(), wins.ToString()) + tail;
            if (dw > 0 && dw >= iw && dw >= pw && dw >= mw)
                return head + Lang.F("t.framediag.17", dw.ToString(), wins.ToString(),
                    dms.ToString("0")) + tail;
            if (pw > 0 && pw >= iw)
                return head + (offender != null
                    ? Lang.F("t.framediag.12", pw.ToString(), wins.ToString(), offender, offMs.ToString("0"))
                    : Lang.F("t.framediag.13", pw.ToString(), wins.ToString())) + tail;
            if (iw > 0 && driver != null)
                return head + Lang.F("t.framediag.8", iw.ToString(), wins.ToString(),
                    driver, maxUs.ToString("0"), (share * 100).ToString("0.0"));
            if (driver != null && budgetUs > 0 && maxUs >= budgetUs * NotableOfBudget)
                return head + Lang.F("t.framediag.9", driver, maxUs.ToString("0"));
            return head + Lang.T("t.framediag.10");
        }

        private static string ProcName(uint pid)
        {
            try
            {
                using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch { return null; }
        }

        internal static string OffenderStateForTest()
        {
            return FrameOffenderPolicy.StateForTest + " pid=" + FrameOffenderPolicy.PidForTest;
        }

        public static void Stop()
        {
            FrameClock fc;
            InterruptAttribution ia;
            CpuContentionProbe cc;
            DiskStallProbe ds;
            lock (lk)
            {
                fc = clock; ia = intr; cc = cpu; ds = disk;
                clock = null; intr = null; cpu = null; disk = null;
                budget = null; running = false;
            }
            if (fc != null) { try { fc.Stop(); } catch { } }
            if (ia != null) { try { ia.Stop(); } catch { } }
            if (cc != null) { try { cc.Stop(); } catch { } }
            if (ds != null) { try { ds.Stop(); } catch { } }
        }

        public static void HealFromCrash()
        {
            try { FrameClock.HealFromCrash(); } catch { }
            try { InterruptAttribution.HealFromCrash(); } catch { }
            try { CpuContentionProbe.HealFromCrash(); } catch { }
            try { DiskStallProbe.HealFromCrash(); } catch { }
        }
    }
}
