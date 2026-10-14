// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private string FindRunningGame(ProcessSnapshot all, out HashSet<int> gamePids)
        {
            List<GameProfile> copy;
            lock (sync)
            {
                copy = new List<GameProfile>();
                foreach (GameProfile p in profiles) copy.Add(p.Clone());
                CaptureGameFamily(all, copy);
            }
            ObserveMonitoringLaunches(all, copy);
            gamePids = new HashSet<int>();
            ObserveRendererForegroundChange();
            string armedName = null;
            string armedVia = null;
            GameDetection raw = null;
            bool fullDetection = ShouldRunFullGameDetection();
            if (fullDetection)
            {
                raw = GameSessionDetector.Detect(
                    all, copy, selfSession, out armedName, out armedVia, FamilyEvidence);
            }
            int epoch;
            GameDetection proposal = ResolveRendererHandoff(all, copy, raw, out epoch);
            if (proposal != null && !FinalizeRendererSelection(proposal, epoch)) proposal = null;
            if (epoch != Volatile.Read(ref rendererHandoffEpoch)
                || stopping || !enabled || panicReq || ProfileStoreSaveFailed) return null;
            GameDetection hit = ApplyStickiness(proposal);
            if (hit != null && !stickyGraceOnly && !VerifyRendererCandidate(hit))
            {
                ClearSticky();
                RequestFullGameDetection();
                return null;
            }
            if (proposal != null && RendererHandoffTracker.SameIdentity(proposal, hit))
                CompleteRendererSelection(hit);
            if (fullDetection)
            {
                armedAwaitingElection = armedName != null && hit == null;
                UpdateArmedStatus(hit == null ? armedName : null, armedVia, hit != null);
                if (hit == null && armedName != null) PreStageDriverTuning(copy, armedName);
            }
            else if (hit != null && armedAwaitingElection)
            {
                armedAwaitingElection = false;
                UpdateArmedStatus(null, null, true);
            }
            if (hit == null) return null;
            if (stickyGraceOnly) return null;
            foreach (int pid in hit.FamilyPids) gamePids.Add(pid);
            lock (sync)
            {
                if (!RendererEpochCurrent(epoch) || hit.Profile == null
                    || !RendererHandoffTracker.SameProfile(hit.Profile, FindProfileLocked(hit.Profile.Id))) return null;
                if (ShouldRearmLauncherTransition(
                        activeDetection, hit))
                {
                    transitionProbeRendererPid = 0;
                    transitionProbeRendererCreation = 0;
                }
                activeDetection = hit;
            }
            SyncAutomaticFullscreenOptimizations(true);
            NotifyExtensionSession(true);
            MaybeObserveRendererActivity(hit);
            return hit.Profile.Name;
        }

        private volatile string armedGameName;
        private string lastArmedLogged;

        public string ArmedGame
        {
            get { lock (sync) return active ? null : armedGameName; }
        }

        private volatile bool gpuPrefStageOn;
        private long sessionStartTicks;

        public List<string> LibraryExecutablePaths()
        {
            var paths = new List<string>();
            lock (sync)
                foreach (GameProfile p in profiles)
                {
                    string path = p.PreferredExecutablePath;
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
            return paths;
        }
    }
}
