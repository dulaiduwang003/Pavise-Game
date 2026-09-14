// @author bdth 2074055628@qq.com
// File purpose Sweep and suppress background processes outside the game
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
#if PAVISE_PERFLAB
        private HashSet<string> performanceSuppressionScope;
#endif

#if PAVISE_PERFLAB
        internal void RestrictBackgroundSuppressionToPaths(
            IEnumerable<string> executablePaths)
        {
            var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (executablePaths != null)
                foreach (string path in executablePaths)
                {
                    string normalized = WhitelistRule.NormalizeImagePath(path);
                    if (!string.IsNullOrEmpty(normalized)) next.Add(normalized);
                }
            lock (sync) performanceSuppressionScope = next;
        }
#endif

        private bool PerformanceScopeAllows(string imagePath)
        {
#if PAVISE_PERFLAB
            lock (sync)
            {
                if (performanceSuppressionScope == null) return true;
                return performanceSuppressionScope.Contains(
                    WhitelistRule.NormalizeImagePath(imagePath));
            }
#else
            return true;
#endif
        }

        private sealed class BackgroundRequest
        {
            public int Pid;
            public string Name;
            public long Creation;
            public long Cpu;
            public SuppressionLevel Desired;
            public SuppressionLevel Previous;
            public bool HadBackgroundReason;
            public AcquireResult Result;
            public string FailureDetail;
        }

        private static readonly HashSet<int> EmptyPidSet = new HashSet<int>();

        // Handheld tier suppresses background as hard as Esports: fewer cores, so every bit the background grabs hurts more, and suppressing background saves power anyway
        //   Handheld vs Esports differs only on the power side, not the suppression side, see the IsHandheld hook points
        internal static bool IsAggressive(PerformancePreset mode, bool aggressiveOn)
        {
            return mode == PerformancePreset.Competitive
                || mode == PerformancePreset.Handheld
                || (mode == PerformancePreset.Custom && aggressiveOn);
        }

        // Power side yields by default: no power slider change, pure power-saving items relaxed even on AC, CPU idle governed by its own confirmed policy
        internal static bool IsHandheld(PerformancePreset mode)
        {
            return mode == PerformancePreset.Handheld;
        }

        internal static bool ResolvePowerPlanEnabled(PerformancePreset mode, bool manuallyEnabled)
        {
            return manuallyEnabled;
        }

        // Since 2.0 background has only two states: anything past the protection boundary is isolated outright, no more heat check and no more stepping up from eco
        //   Do not wait for it to eat resources for a dozen seconds first, enumerate once at match start and suppress everything
        //   Cold processes get suppressed too, affinity left alone by default; a cold process with no ready threads burns no CPU anyway
        //   When it occasionally wakes it can run on any core with no higher-priority work, no queueing behind others
        //   The only remaining gate is the BasicBackgroundEligible protection boundary
        //   Anti-cheat, system core processes, input/audio peripheral chain, accelerators, hardware control, whitelist and other logon accounts are never touched
        //   Every game keeps family protection by default, members confirmed by path and valid same-session parent-child identity
        //   Once the user turns on Suppress family background for an item, that item's family exemption is dropped, the other safety boundaries stay
        //   Tier differences no longer show in suppression strength, only in which processes are eligible to be touched
        internal static SuppressionLevel BackgroundLevel()
        {
            return SuppressionLevel.Isolated;
        }

        private const long TransientProcessTicks = 2 * TimeSpan.TicksPerSecond;

        private void Sweep(ProcessSnapshot all, HashSet<int> gamePids)
        {

            lock (whiteEvalSync)
                SweepWithStableWhitelist(all, gamePids);
        }

        private void SweepWithStableWhitelist(
            ProcessSnapshot all, HashSet<int> gamePids)
        {
            PolicySnapshot sp = sessionPolicy;
            PerformancePreset mode = sp != null ? sp.Preset : ActivePreset;
            int foregroundPid = GameSessionDetector.ForegroundPid();
            // Adaptive suppression escalation, Smart tier only, see AdaptiveGuard; the transition cycle after switching presets mid-match is not let through either
            bool aggressive = IsAggressive(mode, sp != null ? sp.Aggressive : aggressiveOn)
                || (adaptiveEscalated && mode == PerformancePreset.Standard);
            WhitelistEvaluation whitelist = EvaluateWhitelist(all);
            rogueTrustedPids = whitelist.Protected;
            int policyEpoch = FamilyPolicyEpoch;
            bool familyExempt = true;
            int rendererPid = 0;
            string activeGameRoot = null;
            GameProfile currentFamilyProfile = null;
            var libraryRoots = new List<string>();
            List<GameProfile> protectedProfiles;
            lock (sync)
            {
                if (activeDetection != null)
                {
                    rendererPid = activeDetection.RendererPid;
                    GameProfile configured = activeDetection.Profile == null
                        ? null : FindProfileLocked(activeDetection.Profile.Id);
                    currentFamilyProfile = configured == null ? null : configured.Clone();
                    familyExempt = FamilyBoundary.FamilyExemptFor(currentFamilyProfile);
                    if (familyExempt && activeDetection.Profile != null)
                        activeGameRoot = activeDetection.Profile.Root;
                }
                // Other games' default protection must not be switched off by this game's toggle
                // When root directories overlap and ownership is unclear, always lean toward protecting
                protectedProfiles = new List<GameProfile>();
                foreach (GameProfile profile in profiles)
                    if (FamilyBoundary.FamilyExemptFor(profile))
                    {
                        protectedProfiles.Add(profile.Clone());
                        if (FamilyBoundary.SafeFamilyDir(profile.Root)) libraryRoots.Add(profile.Root);
                    }
            }
            HashSet<int> protectedLibraryFamily = FamilyBoundary.CollectProtectedLibraryFamily(
                protectedProfiles, all, selfPid, selfSession, FamilyEvidence);
            bool haveSession = rendererPid > 0;
            // The name means "family exemption is active this match", not "is there a match"; the two differ only while the toggle is off
            bool familyExemptionActive = familyExempt && haveSession;
            // These three sets serve only two purposes: letting through while the toggle is on, subtracting from the visible-window family while it is off
            //   Esports and Handheld tiers have the whole visible-window exemption off, nothing to subtract, so with the toggle also off nobody needs them
            //   This is the 500ms per-round hot path during a match, skip the work when possible, back to the zero overhead of 2.1
            bool needFamilySets = haveSession && (familyExempt || !aggressive);
            // Use the one already computed by whitelist evaluation, do not walk every process again to build a second copy
            Dictionary<int, long> creations = needFamilySets ? whitelist.Creations : null;
            // Ancestor chain and descendants are computed live from this round's snapshot parent-child links, not the 20-second lag of the family set
            //   Add rendererPid to the seeds so child processes spawned mid-match are recognized as family on the very next sweep
            //   Seeding only from gamePids, refreshed every 20 seconds, would isolate the same child first and let it through later
            HashSet<int> gameHostAncestors = needFamilySets
                ? FamilyBoundary.WalkAncestorChain(whitelist.Parents, rendererPid, selfPid, 24, creations)
                : EmptyPidSet;
            HashSet<int> familySeeds = null;
            if (needFamilySets)
            {
                familySeeds = new HashSet<int>(gamePids ?? EmptyPidSet);
                familySeeds.Add(rendererPid);
            }
            HashSet<int> gameDescendants = needFamilySets
                ? FamilyBoundary.WalkDescendants(whitelist.Parents, familySeeds, selfPid, 24, creations)
                : EmptyPidSet;
            // With family exemption off, the whole family is barred from being the root of the "window the user is looking at" family
            //   Excluding the seeds is not enough: if the game's parent is a visible bundled launcher, the game gets added back along the parent-child chain
            //   Once it is back its children follow; the host ancestor is itself a family member, subtract it too
            //   The game itself was already let through above by rendererPid, subtracting does not affect the game
            //   The only one not subtracted is pid == foreground, that is the "window the user is operating right now" protection
            //     Not the same thing as family exemption; mid-match the foreground is usually the game itself, and a real settings popup should be allowed to run
            HashSet<int> userFacingFamily = aggressive
                ? EmptyPidSet
                : CollectUserFacingFamily(foregroundPid, whitelist,
                    familyExempt ? 0 : rendererPid);
            FamilyBoundary.FilterUserFacingGameFamily(userFacingFamily, currentFamilyProfile, all,
                rendererPid, selfPid, selfSession, gamePids, gameDescendants,
                gameHostAncestors, FamilyEvidence);
            bool first;
            lock (sync) first = firstSweep;
            int done = 0, denied = 0, retrying = 0, rosterSkipped = 0;
            var live = new HashSet<int>();
            var pending = new List<BackgroundRequest>();
            long nowFileTime = DateTime.UtcNow.ToFileTimeUtc();

            foreach (ProcEntry p in all.Entries)
            {
                try
                {
                    int pid = p.Pid;
                    WhitelistProcessInfo processInfo;
                    whitelist.Processes.TryGetValue(pid, out processInfo);
                    live.Add(pid);
                    if (pid <= 4 || pid == selfPid) continue;
                    // Skip processes younger than two seconds: things like tasklist exit within a few hundred ms and vanish mid-write
                    //   Anything surviving two seconds is picked up normally next round; already suppressed processes are unaffected, they are all older than two seconds
                    if (p.Creation > 0 && nowFileTime - p.Creation < TransientProcessTicks) continue;

                    string nm = processInfo != null ? processInfo.Name : p.Name;

                    if (string.Equals(nm, selfName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    bool sameSession = processInfo != null && selfSession >= 0 && processInfo.Session == selfSession;
                    if (processInfo == null) sameSession = selfSession >= 0 && p.Session == selfSession;
                    if (!sameSession)
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }

                    // Candidate protection precedes every background write, independent of family exemption/mode/boost toggle
                    // Match only this round's identity PID+creation time+full path, a reused PID must not be let through
                    long candidateCreation = processInfo != null ? processInfo.Creation : p.Creation;
                    string candidatePath = processInfo != null ? processInfo.Path : p.Path;
                    if (IsRendererHandoffProtected(pid, candidateCreation, candidatePath))
                    {
                        BackgroundReleaseState release = core.ReleaseBackgroundForRenderer(pid, candidateCreation, nm);
                        if (release == BackgroundReleaseState.Ready || release == BackgroundReleaseState.Gone)
                            ReportUntrack(pid);
                        continue;
                    }

                    bool boosted;
                    lock (sync) boosted = gameBoost.ContainsKey(pid);
                    if (boosted) continue;

                    bool white = whitelist.Protected.Contains(pid);
                    // The user toggle only drops the current profile's family protection, never other profiles'
                    //   boosted above is true only when the boost actually landed; it is false when boost is off or anti-cheat blocks the handle
                    //   So this pid check cannot be skipped, otherwise on those machines the game itself gets suppressed as background
                    if (FamilyBoundary.IsGameOrWhitelistProtected(pid, rendererPid, white,
                        protectedLibraryFamily.Contains(pid), familyExempt, gamePids, gameDescendants))
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    string ipath = processInfo != null ? processInfo.Path : null;
                    long creation = processInfo != null ? processInfo.Creation : 0;
                    long cpu = processInfo != null ? processInfo.Cpu : 0;
                    ulong io = processInfo != null ? processInfo.Io : 0;
                    if (processInfo == null)
                    {
                        IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                        if (hq != IntPtr.Zero)
                        {
                            try
                            {
                                ipath = Native.ImagePath(hq);
                                Native.QueryProcessSample(hq, out creation, out cpu, out io);
                            }
                            finally { Native.CloseHandle(hq); }
                        }
                    }

                    // The same-named self was already excluded above by selfName; this blocks another build under a different file name
                    //   Run-mode child processes bypass the single-instance lock and would be suppressed as ordinary background, skewing measurements
                    if (SelfBuildGuard.IsOwnBuild(nm, ipath))
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    if (!PerformanceScopeAllows(ipath))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }
                    // Helper processes integrated into the platform follow this match's family choice
                    // Standalone recording hosts stay protected, reuse the snapshot directly
                    // Neither reads game modules nor lets through a whole folder
                    if (TryProtectOverlayHost(pid, creation, nm, ipath, familyExempt)) continue;

                    string containRoot = FamilyBoundary.LibraryRootOf(ipath, libraryRoots);
                    if (containRoot == null && familyExempt) containRoot = activeGameRoot;
                    if (!FamilyBoundary.BasicBackgroundEligible(pid, selfPid, nm, ipath,
                        sameSession ? selfSession : -1, selfSession, foregroundPid,
                        userFacingFamily.Contains(pid), windowsPrefix,
                        familyExemptionActive && gameHostAncestors.Contains(pid),
                        containRoot, aggressive, familyExempt, creation))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }

                    if (SelfProtectedRoster.Contains(nm))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        rosterSkipped++;
                        continue;
                    }

                    SuppressionLevel desired = EffSuppress
                        ? BackgroundLevel() : SuppressionLevel.None;
                    string tracked = core.NameOf(pid);
                    if (tracked != null)
                    {
                        if (string.Equals(tracked, nm, StringComparison.OrdinalIgnoreCase))
                        {
                            if (desired != SuppressionLevel.None && core.HasReason(pid, SuppressReason.Background)
                                && core.LevelOf(pid, SuppressReason.Background) == desired)
                            {
                                bool reconciled = false;
                                if (!RunBackgroundPolicy(policyEpoch, delegate
                                    { reconciled = core.Reconcile(pid, nm, SuppressReason.Background); })) return;
                                if (reconciled) continue;
                            }
                        }
                        else ReportUntrack(pid);
                    }

                    if (desired == SuppressionLevel.None)
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    if (!EffSuppress) continue;

                    pending.Add(new BackgroundRequest
                    {
                        Pid = pid,
                        Name = nm,
                        Creation = creation,
                        Cpu = cpu,
                        Desired = desired,
                        Previous = core.LevelOf(pid, SuppressReason.Background),
                        HadBackgroundReason = core.HasReason(pid, SuppressReason.Background)
                    });
                }
                catch { }
            }

            if (pending.Count > 1)
                pending.Sort(delegate (BackgroundRequest x, BackgroundRequest y)
                {
                    return y.Desired.CompareTo(x.Desired);
                });

            SuppressionCore.BatchResult batchResult = null;
            if (!RunBackgroundPolicy(policyEpoch, delegate
            {
                core.BeginBatch();
                try
                {
                    foreach (BackgroundRequest request in pending)
                    {
                        try { request.Result = core.Acquire(request.Pid, request.Name, SuppressReason.Background, null, request.Desired); }
                        catch { request.Result = AcquireResult.AlreadyProtected; }
                    }
                }
                finally { batchResult = core.EndBatch(); }
            })) return;

            foreach (BackgroundRequest request in pending)
                if ((request.Result == AcquireResult.NewlyThrottled || request.Result == AcquireResult.AlreadyThrottled)
                    && (batchResult == null || !batchResult.WasApplied(request.Pid)))
                {
                    string detail = batchResult != null ? batchResult.FailureOf(request.Pid) : "batch-missing";
                    if (detail == SuppressionCore.SelfProtectedDetail)
                    {
                        request.Result = AcquireResult.NewlyProtected;
                        continue;
                    }
                    request.Result = AcquireResult.ApplyFailed;
                    request.FailureDetail = detail;
                }

            foreach (BackgroundRequest request in pending)
            {
                if (request.Result == AcquireResult.NewlyThrottled)
                {
                    done++;
                    ReportTrack(request.Pid, request.Name);
                    if (!first) Logger.Log(Lang.T("log.gamemodesweep.1") + request.Name + "(pid " + request.Pid + ") "
                        + SuppressionLevelText.Of(request.Desired));
                }
                else if (request.Result == AcquireResult.AlreadyThrottled)
                {
                    if (!request.HadBackgroundReason) ReportTrack(request.Pid, request.Name);
                    if (!first && request.Previous != request.Desired)
                        Logger.Log(Lang.T("log.gamemodesweep.1") + request.Name + "(pid " + request.Pid + ") "
                            + SuppressionLevelText.Of(request.Previous) + " → "
                            + SuppressionLevelText.Of(request.Desired));
                }
                else if (request.Result == AcquireResult.NewlyProtected) denied++;
                else if (request.Result == AcquireResult.ApplyFailed)
                {
                    retrying++;
                    Logger.Log(Lang.T("log.gamemodesweep.1") + request.Name + "(pid " + request.Pid + ") "
                        + ApplyFailureText.Of(request.FailureDetail) + Lang.T("log.gamemodeboost.24"));
                }
            }

            // Hard affinity is off by default; when off only stale placements are cleaned, no new background affinity limits are created
            if (!RunBackgroundPolicy(policyEpoch, ApplyBackgroundHardAffinity)) return;
            FamilyBoundary.PruneCatalogVerdicts(live);

            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (!live.Contains(pid)) { if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid); }

            if (first)
            {
                if (EffSuppress)
                {
                    string preset = mode == PerformancePreset.Competitive ? Lang.T("preset.competitive")
                        : mode == PerformancePreset.Handheld ? Lang.T("preset.handheld")
                        : mode == PerformancePreset.Custom ? Lang.T("preset.custom") : Lang.T("preset.standard");
                    bool strong = mode == PerformancePreset.Competitive
                        || mode == PerformancePreset.Handheld
                        || (mode == PerformancePreset.Custom && aggressive);
                    // The "background goes to background cores" section was removed together with core migration, background no longer has dedicated cores
                    string policy = preset + (strong ? Lang.T("t.gamemodesweep.2") : Lang.T("t.gamemodesweep.3"))
                        + (aggressive ? Lang.T("t.gamemodesweep.5") : "");
                    Logger.Log(Lang.T("log.gamemodesweep.1") + policy
                        + (SuppressionCore.GpuDemoteEnabled ? Lang.T("log.gamemodesweep.6") : "")
                        + Lang.T("log.gamemodesweep.7") + done + Lang.T("log.gamemodesweep.8")
                        + (retrying > 0 ? " " + retrying + Lang.T("log.gamemodesweep.9") : "")
                        + (denied > 0 ? " " + denied + Lang.T("log.gamemodesweep.10") : "")
                        + (rosterSkipped > 0 ? " " + rosterSkipped + Lang.T("t.gamemodesweep.11") : ""));
                }
                lock (sync) firstSweep = false;
            }
        }

        private HashSet<int> CollectUserFacingFamily(
            int foregroundPid, WhitelistEvaluation whitelist, int excludeRootPid)
        {
            var roots = new HashSet<int>();
            HashSet<int> visible = GameSessionDetector.VisibleWindowPids(true);
            foreach (var pair in whitelist.Processes)
            {
                try
                {
                    int pid = pair.Key;
                    if (excludeRootPid > 0 && pid == excludeRootPid) continue;
                    WhitelistProcessInfo info = pair.Value;
                    if (selfSession < 0 || info.Session != selfSession) continue;
                    if (pid == foregroundPid || visible.Contains(pid))
                        roots.Add(pid);
                }
                catch { }
            }
            return FamilyBoundary.ExpandUserFacingFamily(
                whitelist.Parents, whitelist.Names, roots);
        }

        private bool TryProtectOverlayHost(int pid, long creation, string name, string imagePath, bool familyExempt)
        {
            // Explicit family suppression only removes the integrated-platform layer of exemption
            // Standalone recording and communication tools stay protected as before
            if (pid <= 4 || !OverlayHostCatalog.ShouldProtectProcess(name, imagePath, familyExempt))
                return false;

            // With identity missing or the PID recycled, never use it to release another process's record
            // Leave the unresolved restore debt and retry with the next round's snapshot
            if (creation > 0 && core.ReleaseIfCreation(pid, SuppressReason.Background, creation))
                ReportUntrack(pid);
            return true;
        }

        private void ReleaseBackgroundExemption(int pid, string name, string reason)
        {
            if (core.Release(pid, SuppressReason.Background))
            {
                ReportUntrack(pid);
                if (!string.IsNullOrEmpty(reason)) Logger.Log(reason + Lang.T("log.gamemodesweep.12") + name + " pid " + pid);
            }
        }

    }
}
