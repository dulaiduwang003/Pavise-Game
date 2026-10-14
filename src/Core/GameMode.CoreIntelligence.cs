using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private CoreThreadTracker coreThreadTracker = new CoreThreadTracker();
        private CoreThreadPlacement coreThreadPlacement = new CoreThreadPlacement();
        private CoreWorkBudget coreBudget = new CoreWorkBudget();
        private CoreGameLearning coreLearning;
        private CoreFrameCapture coreFrames;
        private CoreFrameWindow coreFrameBaseline;
        private double coreFrameLatestP99;
        private int coreFrameTrials, coreFrameRegressions;
        private int coreLearningWindows;
        private double corePeak, coreCollectorCpuMs, coreFrameLoadSum, coreFrameParseMs;
        private int coreFrameLoadCount;
        private bool coreRefinementStopped;
        private string coreFeedback = "observing";
        private long coreIntelligenceNext;
        private readonly Dictionary<int, ProcEntry> coreBackgroundPrevious = new Dictionary<int, ProcEntry>();
        private long coreBackgroundStamp;

        private void ResetCoreIntelligence()
        {
            if (coreFrames != null) { coreFrames.Dispose(); coreFrames = null; }
            if (coreThreadPlacement != null) coreThreadPlacement.Restore();
            if (coreLearning != null && corePeak > .25 && coreLearningWindows >= 4 && coreGovernor != null)
                coreLearning.Save(coreGovernor.Capacity, corePeak, coreFrameRegressions >= 2);
            coreLearning = null;
            coreThreadTracker = new CoreThreadTracker();
            coreBudget = new CoreWorkBudget();
            coreFrameBaseline = null;
            coreFrameLatestP99 = 0;
            coreFrameTrials = coreFrameRegressions = coreFrameLoadCount = 0;
            coreLearningWindows = 0;
            corePeak = coreCollectorCpuMs = coreFrameLoadSum = coreFrameParseMs = 0;
            coreRefinementStopped = false; coreFeedback = "observing"; coreIntelligenceNext = 0;
            coreBackgroundPrevious.Clear(); coreBackgroundStamp = 0;
        }
        private bool StopCoreRefinement()
        {
            if (coreFrames != null) { coreFrames.Dispose(); coreFrames = null; }
            if (coreThreadPlacement == null) return true;
            bool clean = coreThreadPlacement.Restore();
            if (coreThreadPlacement.UnownedPending) clean &= CoreThreadPlacement.Recover();
            return clean;
        }
        private void LoadCoreLearning()
        {
            if (coreLearning != null || coreObservationOnly || activeDetection == null || corePlan == null) return;
            coreLearning = CoreGameLearning.Load(activeDetection.RendererPath, corePreset.ToString());
            if (coreLearning.Loaded)
            {
                GrowCoreCapacity(corePlan.Cores, Math.Min(coreLearning.Peak, coreLearning.Capacity * .85));
                if (coreLearning.Regressions >= 2) { coreRefinementStopped = true; coreFeedback = "history-regression"; }
                if (coreLearning.AccessRetryAt > DateTime.UtcNow.Ticks) { coreRefinementStopped = true; coreFeedback = "history-access-denied"; }
            }
        }
        private void ObserveCoreThreads(ThreadTimes before, ThreadTimes after, double load)
        {
            LoadCoreLearning(); corePeak = Math.Max(corePeak, load);
            coreLearningWindows++;
            List<CoreThreadCandidate> candidates = coreThreadTracker.Observe(before, after);
            if (!coreEngaged || coreBackgroundOnly || CorePending || coreRefinementStopped) return;
            if (coreThreadPlacement.Active)
            {
                if (coreThreadPlacement.Mask != coreGameMask || !coreThreadPlacement.Audit(after)
                    || load > Math.Max(1, AutoCores.PhysicalCount(coreGameMask, corePhysical) - coreThreadPlacement.Candidates) * .9)
                {
                    StopCoreRefinement(); coreRefinementStopped = true; coreFeedback = "refinement-withdrawn";
                }
                return;
            }
            if (candidates.Count == 0 || coreFrames != null) return;
            if (coreFrameBaseline == null)
            {
                coreFrames = new CoreFrameCapture();
                if (!coreFrames.Start(coreWatchPid)) { coreFeedback = coreFrames.State; coreFrames = null; coreRefinementStopped = true; }
                else { coreFeedback = "baseline"; coreFrameLoadSum = 0; coreFrameLoadCount = 0; }
                return;
            }
            if (!coreFrameBaseline.Usable) { coreRefinementStopped = true; return; }
            int total = AutoCores.PhysicalCount(coreGameMask, corePhysical);
            if (total < 4 || load > (total - Math.Min(2, candidates.Count)) * .85) return;
            ulong verified = 0;
            lock (sync) { HardPin pin; if (hardPins.TryGetValue(coreWatchPid, out pin)) verified = pin.AppliedAffinity; }
            if (verified != coreGameMask) return;
            if (coreThreadPlacement.Apply(coreWatchPid, coreWatchCreation, candidates, verified, corePhysical, CpuTopology.FavoredMask))
            {
                coreFeedback = "trial"; StartCoreTrial();
                Logger.Log("AutoCore thread refinement verified candidates=" + coreThreadPlacement.Candidates + " mask=0x" + verified.ToString("X"));
            }
            else
            {
                coreFeedback = coreThreadPlacement.State; coreRefinementStopped = true;
                if (coreLearning != null && coreFeedback == "access-denied") coreLearning.AccessRetryAt = DateTime.UtcNow.AddMinutes(30).Ticks;
            }
        }
        private void StartCoreTrial()
        {
            coreFrames = new CoreFrameCapture();
            coreFrameLoadSum = 0; coreFrameLoadCount = 0;
            if (!coreFrames.Start(coreWatchPid))
            { coreFeedback = coreFrames.State; StopCoreRefinement(); coreRefinementStopped = true; }
        }
        private void PollCoreFeedback()
        {
            long now = AutoCores.NowTicks;
            if (now < coreIntelligenceNext || coreFrames == null) return;
            coreIntelligenceNext = now + TimeSpan.TicksPerSecond;
            double load = coreFrameLoadCount == 0 ? coreLoadGuard.Load : coreFrameLoadSum / coreFrameLoadCount;
            if (!coreFrames.Poll(load))
            {
                if (coreFrames.State == "collector-failed") { StopCoreRefinement(); coreRefinementStopped = true; coreFeedback = "collector-failed"; }
                return;
            }
            CoreFrameWindow result = coreFrames.Result;
            if (result.Usable) coreFrameLatestP99 = result.P99;
            coreCollectorCpuMs += result.CpuMs;
            coreFrameParseMs += result.ParseMs;
            coreFrames.Dispose(); coreFrames = null;
            if (coreFrameBaseline == null)
            {
                coreFrameBaseline = result; coreFeedback = result.Usable ? "baseline-ready" : result.Failure ?? "insufficient";
                if (coreLearning != null && coreFeedback == "capture-access-denied") coreLearning.AccessRetryAt = DateTime.UtcNow.AddMinutes(30).Ticks;
                Logger.Log("AutoCore frame baseline state=" + coreFeedback + " frames=" + result.Frames.Count + " collectorCpuMs=" + result.CpuMs.ToString("F1"));
                return;
            }
            coreFrameTrials++;
            coreFeedback = result.Failure ?? CoreFrameWindow.Compare(coreFrameBaseline, result);
            if (coreFeedback == "regressed") coreFrameRegressions++;
            Logger.Log("AutoCore frame guard verdict=" + coreFeedback + " baselineP99=" + coreFrameBaseline.P99.ToString("F2")
                + " trialP99=" + result.P99.ToString("F2") + " frames=" + result.Frames.Count + " collectorCpuMs=" + result.CpuMs.ToString("F1"));
            if (coreFrameRegressions >= 2 || !result.Usable || coreFeedback == "phase-changed")
            { StopCoreRefinement(); coreRefinementStopped = true; }
            else if (coreFrameTrials < 2) StartCoreTrial();
        }
        private List<int> RankCoreBackground(ProcessSnapshot snapshot)
        {
            var pids = core.PidsWith(SuppressReason.Background);
            var rank = new Dictionary<int, long>();
            foreach (int pid in pids)
            {
                ProcEntry p = snapshot.Find(pid), previous;
                if (p != null && coreBackgroundPrevious.TryGetValue(pid, out previous) && p.Creation == previous.Creation
                    && snapshot.MonotonicTicks > coreBackgroundStamp && p.Cpu >= previous.Cpu)
                    rank[pid] = p.Cpu - previous.Cpu;
            }
            long elapsed = snapshot.MonotonicTicks - coreBackgroundStamp;
            pids.Sort(delegate(int a, int b) { long x, y; rank.TryGetValue(a, out x); rank.TryGetValue(b, out y); return y.CompareTo(x); });
            coreBackgroundPrevious.Clear();
            foreach (int pid in pids) { ProcEntry p = snapshot.Find(pid); if (p != null) coreBackgroundPrevious[pid] = p; }
            coreBackgroundStamp = snapshot.MonotonicTicks;
            pids.RemoveAll(delegate(int pid) { long cpu; return !core.IsSqueezed(pid) && (!rank.TryGetValue(pid, out cpu) || elapsed <= 0 || elapsed > 30 * TimeSpan.TicksPerSecond || cpu < elapsed * .01); });
            return pids;
        }
    }
}
