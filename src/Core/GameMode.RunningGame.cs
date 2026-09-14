// @author bdth 2074055628@qq.com
// File purpose Identify running games and game library paths in the process snapshot
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
            // Once the renderer anchor is confirmed gone, the sticky quick rescan round is just
            // the detector's internal buffer, must not be treated as a running round to execute Boost
            // hand it to the main loop's 8-second grace period, which Seals first instead of dropping the match
            if (stickyGraceOnly) return null;
            foreach (int pid in hit.FamilyPids) gamePids.Add(pid);
            if (irqProbe.HasSealedPending)
            {
                bool sameSealedProfile;
                string sealedGame;
                lock (sync)
                {
                    sameSealedProfile = SameReportedProfile(
                        repProfileId,
                        hit.Profile != null ? hit.Profile.Id : null);
                    sealedGame = repGame;
                }
                if (sameSealedProfile)
                {
                    // The renderer may exit after the last snapshot but before OpenProcess
                    // so it is already Sealed but not yet in the gameGone grace, when a new renderer
                    // of the same profile appears the old prefix must still be dropped and observation re-armed
                    ArmIrqObservation(sealedGame ?? hit.Profile.Name);
                }
            }
            if (irqProbe.IsCapturing)
            {
                bool proofStrict;
                ulong proofMask = EffectiveGameMask(sessionPolicy, out proofStrict);
                if (irqProbe.IsSystemObservation) proofMask = 0;
                if (!irqProbe.ProofMatches(
                        proofMask, hit.RendererPid,
                        hit.RendererCreation))
                {
                    bool sameSessionProfile;
                    string sameSessionGame;
                    lock (sync)
                    {
                        sameSessionProfile = SameReportedProfile(
                            repProfileId,
                            hit.Profile != null ? hit.Profile.Id : null);
                        sameSessionGame = repGame;
                    }
                    irqProbe.InvalidateGameMask();
                    // Same match switching from launcher to the real renderer, the old fragment is dropped entirely, but the new
                    // renderer may start an epoch from zero without splitting one match into two ledger records
                    if (sameSessionProfile)
                        ArmIrqObservation(sameSessionGame ?? hit.Profile.Name);
                }
            }
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
                // Within the same profile a match may update from launcher to the real renderer, when switching to another
                // profile the old match's present filter PID must not be overwritten before its ReportFinish
                repRendererPid = UpdateSessionRendererPid(
                    repProfileId, repRendererPid,
                    hit.Profile != null ? hit.Profile.Id : null, hit.RendererPid);
                activeDetection = hit;
            }
            // When the launcher hands off to the real renderer process the extension needs the new identity, with no active match this is a no-op notification
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
        private readonly CpuLimitProbe cpuLimit = new CpuLimitProbe();
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
