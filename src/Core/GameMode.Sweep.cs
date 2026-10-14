// 作者：bdth 2074055628@qq.com
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

        internal static bool IsAggressive(PerformancePreset mode, bool aggressiveOn)
        {
            return PresetValue.IsCompetitive(mode)
                || mode == PerformancePreset.Handheld;
        }

        internal static bool IsHandheld(PerformancePreset mode)
        {
            return mode == PerformancePreset.Handheld;
        }

        internal static bool ResolvePowerPlanEnabled(PerformancePreset mode, bool manuallyEnabled)
        {
            return !PowerPlan.NoSwitchSelected && manuallyEnabled;
        }

        internal static SuppressionLevel BackgroundLevel()
        {
            return SuppressionLevel.Isolated;
        }

        private ProcessSnapshot servicePolicySnapshot;
        private List<KeyValuePair<string, int>> servicePolicyHosts;
#if PAVISE_SELFTEST
        internal Func<List<KeyValuePair<string, int>>> ServiceHostsForTest;
#endif

        private List<KeyValuePair<string, int>> BackgroundServiceHosts(ProcessSnapshot all)
        {
            if (!ReferenceEquals(servicePolicySnapshot, all))
            {
                servicePolicyHosts = null;
                bool hasCandidate = false;
                if (all != null)
                    foreach (ProcEntry entry in all.Entries)
                        if (entry.Pid > 4 && entry.Session == 0 && entry.Creation > 0
                            && BackgroundServicePolicy.CandidateName(entry.Name))
                        { hasCandidate = true; break; }
                if (hasCandidate)
                {
#if PAVISE_SELFTEST
                    servicePolicyHosts = ServiceHostsForTest == null ? null : ServiceHostsForTest();
#elif !PAVISE_PERFLAB
                    servicePolicyHosts = SvcQuery.RunningServices();
#endif
                }
                servicePolicySnapshot = all;
            }
            return servicePolicyHosts;
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
            bool aggressive = IsAggressive(mode, sp != null ? sp.Aggressive : aggressiveOn)
                || (adaptiveEscalated && mode == PerformancePreset.Standard);
            WhitelistEvaluation whitelist = EvaluateWhitelist(all);
            int policyEpoch = FamilyPolicyEpoch;
            bool extreme = mode == PerformancePreset.Extreme;
            List<KeyValuePair<string, int>> serviceHosts = BackgroundServicePolicy.SupportedMode(mode) ? BackgroundServiceHosts(all) : null;
            bool familyExempt = true;
            int rendererPid = 0;
            string activeGameRoot = null;
            GameProfile currentFamilyProfile = null;
            var libraryRoots = new List<string>();
            List<GameProfile> protectedProfiles;
            var strongProfiles = new List<GameProfile>();
            lock (sync)
            {
                if (activeDetection != null)
                {
                    rendererPid = activeDetection.RendererPid;
                    GameProfile configured = activeDetection.Profile == null
                        ? null : FindProfileLocked(activeDetection.Profile.Id);
                    currentFamilyProfile = configured == null ? null : configured.Clone();
                    familyExempt = FamilyBoundary.FamilyExemptFor(currentFamilyProfile);
                    if (familyExempt && activeDetection.Profile != null
                        && FamilyBoundary.SafeFamilyDir(activeDetection.Profile.Root))
                        activeGameRoot = activeDetection.Profile.Root;
                }
                // 当前游戏开关不能撤销其他游戏的保护。
                protectedProfiles = new List<GameProfile>();
                foreach (GameProfile profile in profiles)
                {
                    GameProfile copy = profile.Clone();
                    if (extreme) strongProfiles.Add(copy);
                    if (FamilyBoundary.FamilyExemptFor(profile))
                    {
                        protectedProfiles.Add(copy);
                        if (FamilyBoundary.SafeFamilyDir(profile.Root)) libraryRoots.Add(profile.Root);
                    }
                }
            }
            HashSet<int> protectedLibraryFamily = FamilyBoundary.CollectProtectedLibraryFamily(
                protectedProfiles, all, selfPid, selfSession, FamilyEvidence);
            HashSet<int> strongProtected = extreme ? FamilyBoundary.CollectProtectedLibraryFamily(
                strongProfiles, all, selfPid, selfSession, FamilyEvidence, false, true) : EmptyPidSet;
            bool haveSession = rendererPid > 0;
            bool familyExemptionActive = familyExempt && haveSession;
            bool needFamilySets = haveSession && (familyExempt || !aggressive || extreme);
            Dictionary<int, long> creations = needFamilySets ? whitelist.Creations : null;
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
            HashSet<int> userFacingFamily = aggressive && !coreCooperative
                ? EmptyPidSet
                : CollectUserFacingFamily(foregroundPid, whitelist,
                    familyExempt ? 0 : rendererPid);
            FamilyBoundary.FilterUserFacingGameFamily(userFacingFamily, currentFamilyProfile, all,
                rendererPid, selfPid, selfSession, gamePids, gameDescendants,
                gameHostAncestors, FamilyEvidence);
            bool first;
            lock (sync) first = firstSweep;
            int done = 0, denied = 0, retrying = 0, rosterSkipped = 0, serviceEligible = 0;
            var live = new HashSet<int>();
            var pending = new List<BackgroundRequest>();
            var cpuCapTargets = new Dictionary<int, long>();
            var backgroundFpsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var freezeTargets = new Dictionary<int, FreezeTarget>();
            var parentsWithChildren = new HashSet<int>();
            foreach (ProcEntry child in all.Entries) if (child.ParentPid > 4) parentsWithChildren.Add(child.ParentPid);
            ProcEntry freezeAnchor = all.Find(rendererPid);
            HashSet<int> strongVisible = null;
            bool strongVisibilityRead = false;
            var servicePids = new HashSet<int>();
            if (serviceHosts != null) foreach (var service in serviceHosts) servicePids.Add(service.Value);
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
                    if (p.Creation > 0 && nowFileTime - p.Creation < TransientProcessTicks) continue;

                    string nm = processInfo != null ? processInfo.Name : p.Name;

                    if (whitelist.RuntimeProtected.Contains(pid))
                    {
                        bool freezeReleased = backgroundFreeze.Release(pid, p.Creation, "runtime-family");
                        bool capReleased = backgroundCpuCap.Release(pid);
                        BackgroundReleaseState runtimeState = core.ReleaseBackgroundForRenderer(pid, p.Creation, nm);
                        if (runtimeState == BackgroundReleaseState.IdentityMismatch
                            && core.DiscardReusedRendererTracking(pid, p.Creation, nm))
                            runtimeState = core.ReleaseBackgroundForRenderer(pid, p.Creation, nm);
                        bool backgroundReleased = RuntimeBackgroundExempt(runtimeState);
                        if (backgroundReleased) ReportUntrack(pid);
                        RecordRuntimeProtection(p, freezeReleased, capReleased, runtimeState);
                        continue;
                    }

                    if (string.Equals(nm, selfName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid);
                        continue;
                    }

                    bool sameSession = processInfo != null && selfSession >= 0 && processInfo.Session == selfSession;
                    if (processInfo == null) sameSession = selfSession >= 0 && p.Session == selfSession;
                    bool serviceTarget = BackgroundServicePolicy.Eligible(mode, p, serviceHosts,
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                    if (!sameSession && !serviceTarget)
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }

                    // 所有后台写入前按本轮完整进程身份检查候选保护。
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

                    PlatformProcessOwner platformOwner;
                    bool platformRemoved = whitelist.PlatformProcesses.TryGetValue(pid, out platformOwner)
                        && !PlatformRuntimePolicy.Enabled(platformOwner.Platform);
                    bool white = whitelist.Protected.Contains(pid);
                    if (FamilyBoundary.IsGameOrWhitelistProtected(pid, rendererPid, white,
                        protectedLibraryFamily.Contains(pid) || (extreme && gameDescendants.Contains(pid)),
                        familyExempt, gamePids, gameDescendants, platformRemoved))
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
                    if (TryProtectOverlayHost(pid, creation, nm, ipath, familyExempt)) continue;

                    string containRoot = FamilyBoundary.LibraryRootOf(ipath, libraryRoots);
                    if (containRoot == null && familyExempt) containRoot = activeGameRoot;
                    if (!FamilyBoundary.BasicBackgroundEligible(pid, selfPid, nm, ipath,
                        sameSession || serviceTarget ? selfSession : -1, selfSession, foregroundPid,
                        userFacingFamily.Contains(pid), windowsPrefix,
                        familyExemptionActive && gameHostAncestors.Contains(pid),
                        containRoot, aggressive, familyExempt, creation, platformRemoved))
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

                    if (sameSession && !serviceTarget && AutoGpuEligible(nm, ipath, windowsPrefix)
                        && StrongBackgroundPolicy.ApprovedImage(ipath))
                    {
                        bool protectedPath;
                        lock (sync) protectedPath = BackgroundFpsProtectedLocked(nm, ipath);
                        if (!protectedPath) backgroundFpsPaths.Add(ipath);
                    }

                    if (serviceTarget) serviceEligible++;
                    SuppressionLevel desired = EffSuppress
                        ? BackgroundLevel() : SuppressionLevel.None;
                    if (desired != SuppressionLevel.None && haveSession && CpuCapSupported(mode)
                        && !core.HasReason(pid, SuppressReason.AntiCheat))
                    {
                        bool protectedStrong = !platformRemoved && (strongProtected.Contains(pid) || gameDescendants.Contains(pid) || gameHostAncestors.Contains(pid));
                        bool approvedImage = StrongBackgroundPolicy.ApprovedImage(ipath);
                        if (approvedImage && !backgroundFreeze.Owns(pid, creation))
                        {
                            if (!strongVisibilityRead) { strongVisible = CollectVisibleWindowPids(); strongVisibilityRead = true; }
                            if (strongVisible == null || strongVisible.Contains(pid) || pid == foregroundPid) approvedImage = false;
                        }
                        bool admitted = StrongBackgroundPolicy.Admit(mode, creation, protectedStrong,
                            parentsWithChildren.Contains(pid), serviceTarget, approvedImage);
                        if (admitted)
                        {
                            cpuCapTargets[pid] = creation;
                            if (BackgroundFreezePolicy.Eligible(mode, pid, creation, ipath,
                                p.Session, selfSession, protectedStrong, serviceTarget, windowsPrefix, admitted))
                                freezeTargets[pid] = new FreezeTarget { Pid = pid, Creation = creation, Path = ipath };
                        }
                    }
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

            if (!RunBackgroundPolicy(policyEpoch, delegate
            {
                SyncBackgroundFreeze(freezeTargets, rendererPid, freezeAnchor == null ? 0 : freezeAnchor.Creation,
                    cpuCapTargets);
                SyncBackgroundCpuCap(cpuCapTargets, servicePids);
            }))
            { backgroundFreeze.Clear("policy-invalidated"); backgroundCpuCap.Clear(); return; }

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
                        try { request.Result = core.Acquire(request.Pid, request.Name, SuppressReason.Background, null, request.Desired, request.Creation); }
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

            var coreProtected = new HashSet<int>(userFacingFamily);
            SyncPerformanceAutomation(new List<string>(backgroundFpsPaths).ToArray());
            coreProtected.UnionWith(whitelist.Protected);
            foreach (var platform in whitelist.PlatformProcesses)
                if (!PlatformRuntimePolicy.Enabled(platform.Value.Platform) && !whitelist.Protected.Contains(platform.Key))
                    coreProtected.Remove(platform.Key);
            if (!RunBackgroundPolicy(policyEpoch, delegate { SyncCoreBackground(all, coreProtected, foregroundPid); })) return;
            FamilyBoundary.PruneCatalogVerdicts(live);

            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (!live.Contains(pid)) { if (core.Release(pid, SuppressReason.Background)) ReportUntrack(pid); }

            if (first)
            {
                if (EffSuppress)
                {
                    string preset = mode == PerformancePreset.Extreme ? Lang.T("preset.extreme")
                        : PresetValue.IsCompetitive(mode) ? Lang.T("preset.competitive")
                        : mode == PerformancePreset.Handheld ? Lang.T("preset.handheld")
                        : Lang.T("preset.standard");
                    bool strong = PresetValue.IsCompetitive(mode)
                        || mode == PerformancePreset.Handheld;
                    string policy = preset + (strong ? Lang.T("t.gamemodesweep.2") : Lang.T("t.gamemodesweep.3"))
                        + (aggressive ? Lang.T("t.gamemodesweep.5") : "");
                    Logger.Log(Lang.F("background.scope", serviceEligible));
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
            if (pid <= 4 || !OverlayHostCatalog.ShouldProtectProcess(name, imagePath, familyExempt))
                return false;

            // 身份缺失时保留待恢复记录，不操作同 PID 的其他进程。
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
