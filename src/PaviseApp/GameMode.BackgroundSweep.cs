using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void SweepWithStableWhitelist(ProcessSnapshot all, HashSet<int> gamePids)
        {
            using (SuccessLogBatch.Begin("background-sweep session=" + AutomationSession))
                SweepWithStableWhitelistCore(all, gamePids);
        }
        private void SweepWithStableWhitelistCore(ProcessSnapshot all, HashSet<int> gamePids)
        {
            PolicySnapshot policy = sessionPolicy;
            PerformancePreset preset = ((policy != null) ? policy.Preset : ActivePreset);
            SuppressionCore.BackgroundBoostLimit = (policy ?? PolicyResolver.Global()).BackgroundBoostLimitRequested;
            bool preserveOnForeground = preset == PerformancePreset.Competitive;
            int foregroundPid = GameSessionDetector.ForegroundPid();
            bool wideScope = WideBackgroundScope(policy ?? PolicyResolver.Global());
            WhitelistEvaluation whitelist = EvaluateWhitelist(all);
            int epoch = FamilyPolicyEpoch;
            List<KeyValuePair<string, int>> services = (BackgroundServicePolicy.SupportedMode(preset) ? BackgroundServiceHosts(all) : null);
            bool protectGameFamily = true;
            int rendererPid = 0;
            string activeGameRoot = null;
            GameProfile activeProfile = null;
            List<string> protectedRoots = new List<string>();
            List<GameProfile> exactGameProfiles = new List<GameProfile>();
            List<GameProfile> protectedProfiles;
            lock (sync)
            {
                if (activeDetection != null)
                {
                    rendererPid = activeDetection.RendererPid;
                    GameProfile gameProfile = ((activeDetection.Profile == null) ? null : FindProfileLocked(activeDetection.Profile.Id));
                    activeProfile = ((gameProfile == null) ? null : gameProfile.Clone());
                    protectGameFamily = FamilyBoundary.FamilyExemptFor(activeProfile);
                    if (protectGameFamily && activeDetection.Profile != null && FamilyBoundary.SafeFamilyDir(activeDetection.Profile.Root))
                    {
                        activeGameRoot = activeDetection.Profile.Root;
                    }
                }
                protectedProfiles = new List<GameProfile>();
                foreach (GameProfile profile2 in profiles)
                {
                    GameProfile snapshotProfile = profile2.Clone();
                    exactGameProfiles.Add(snapshotProfile);
                    if (FamilyBoundary.FamilyExemptFor(profile2))
                    {
                        protectedProfiles.Add(snapshotProfile);
                        if (FamilyBoundary.SafeFamilyDir(profile2.Root))
                        {
                            protectedRoots.Add(profile2.Root);
                        }
                    }
                }
            }
            HashSet<int> libraryFamily = FamilyBoundary.CollectProtectedLibraryFamily(protectedProfiles, all, selfPid, selfSession, FamilyEvidence);
            // 即使没有可用游戏库根目录，也保护已登记或已学习的游戏本体，防止家族压制。
            HashSet<int> exactGameProcesses = FamilyBoundary.CollectProtectedLibraryFamily(exactGameProfiles, all, selfPid, selfSession, FamilyEvidence, true, true);
            libraryFamily.UnionWith(exactGameProcesses);
            var gameCreationSeeds = new HashSet<int>(exactGameProcesses);
            gameCreationSeeds.UnionWith(gamePids ?? EmptyPidSet);
            if (rendererPid > 0) gameCreationSeeds.Add(rendererPid);
            HashSet<int> gameCreationHosts = FamilyBoundary.CollectGameCreationHosts(all, gameCreationSeeds, selfPid, selfSession);
            bool hasRenderer = rendererPid > 0;
            bool protectRendererAncestors = protectGameFamily && hasRenderer;
            bool inspectGameFamily = hasRenderer && (protectGameFamily || !wideScope);
            Dictionary<int, long> creations = (inspectGameFamily ? whitelist.Creations : null);
            HashSet<int> rendererAncestors = (inspectGameFamily ? FamilyBoundary.WalkAncestorChain(whitelist.Parents, rendererPid, selfPid, 24, creations) : EmptyPidSet);
            HashSet<int> gameFamilyRoots = null;
            if (inspectGameFamily)
            {
                gameFamilyRoots = new HashSet<int>(gamePids ?? EmptyPidSet);
                gameFamilyRoots.Add(rendererPid);
            }
            HashSet<int> gameDescendants = (inspectGameFamily ? FamilyBoundary.WalkDescendants(whitelist.Parents, gameFamilyRoots, selfPid, 24, creations) : EmptyPidSet);
            HashSet<int> userFacingFamily = (wideScope ? EmptyPidSet : CollectUserFacingFamily(foregroundPid, whitelist, (!protectGameFamily) ? rendererPid : 0));
            FamilyBoundary.FilterUserFacingGameFamily(userFacingFamily, activeProfile, all, rendererPid, selfPid, selfSession, gamePids, gameDescendants, rendererAncestors, FamilyEvidence);
            bool firstSweepSnapshot;
            lock (sync)
            {
                firstSweepSnapshot = firstSweep;
            }
            int throttledCount = 0;
            int protectedCount = 0;
            int failedCount = 0;
            int selfProtectedCount = 0;
            int serviceCount = 0;
            HashSet<int> observedPids = new HashSet<int>();
            List<BackgroundRequest> pending = new List<BackgroundRequest>();
            long scanTime = DateTime.UtcNow.ToFileTimeUtc();
            ProcEntry[] entries = all.Entries;
            foreach (ProcEntry procEntry in entries)
            {
                try
                {
                    int pid = procEntry.Pid;
                    WhitelistProcessInfo processInfo;
                    whitelist.Processes.TryGetValue(pid, out processInfo);
                    observedPids.Add(pid);
                    if (pid <= 4 || pid == selfPid)
                    {
                        continue;
                    }
                    string nm;
                    long creation;
                    long cpu;
                    SuppressionLevel suppressionLevel;
                    if (IsCriticalThreadGuardian(pid, procEntry.Creation))
                    {
                        ReleaseCriticalGuardianBackground();
                        continue;
                    }
                    if (GameAffinityJobLease.IsGuardian(pid, procEntry.Creation))
                    {
                        ReleaseJobGuardianBackground(pid, procEntry.Creation, processInfo != null ? processInfo.Name : procEntry.Name);
                        continue;
                    }
                    if (procEntry.Creation > 0 && scanTime - procEntry.Creation < 20000000)
                    {
                        continue;
                    }
                    nm = ((processInfo != null) ? processInfo.Name : procEntry.Name);
                    if (whitelist.RuntimeProtected.Contains(pid))
                    {
                        BackgroundReleaseState backgroundReleaseState = core.ReleaseBackgroundForRenderer(pid, procEntry.Creation, nm);
                        if (backgroundReleaseState == BackgroundReleaseState.IdentityMismatch && core.DiscardReusedRendererTracking(pid, procEntry.Creation, nm))
                        {
                            backgroundReleaseState = core.ReleaseBackgroundForRenderer(pid, procEntry.Creation, nm);
                        }
                        if (RuntimeBackgroundExempt(backgroundReleaseState))
                        {
                            ReportUntrack(pid);
                        }
                        RecordRuntimeProtection(procEntry, backgroundReleaseState);
                        continue;
                    }
                    if (string.Equals(nm, selfName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (core.Release(pid, SuppressReason.Background))
                        {
                            ReportUntrack(pid);
                        }
                        continue;
                    }
                    bool sameSession = processInfo != null && selfSession >= 0 && processInfo.Session == selfSession;
                    if (processInfo == null)
                    {
                        sameSession = selfSession >= 0 && procEntry.Session == selfSession;
                    }
                    string servicePath = (sameSession ? null : ServiceImagePath(procEntry, preset));
                    bool eligibleService = !sameSession && BackgroundServicePolicy.Eligible(preset, procEntry, servicePath, services, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                    if (!sameSession && !eligibleService)
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }
                    long observedCreation = ((processInfo != null) ? processInfo.Creation : procEntry.Creation);
                    string observedPath = ((processInfo != null) ? processInfo.Path : procEntry.Path);
                    if (IsRendererHandoffProtected(pid, observedCreation, observedPath))
                    {
                        BackgroundReleaseState backgroundReleaseState2 = core.ReleaseBackgroundForRenderer(pid, observedCreation, nm);
                        if (backgroundReleaseState2 == BackgroundReleaseState.Ready || backgroundReleaseState2 == BackgroundReleaseState.Gone)
                        {
                            ReportUntrack(pid);
                        }
                        continue;
                    }
                    bool isInGameBoost;
                    lock (sync)
                    {
                        isInGameBoost = gameBoost.ContainsKey(pid);
                    }
                    if (isInGameBoost)
                    {
                        continue;
                    }
                    PlatformProcessOwner platformOwner;
                    // 保护已核验且存活的游戏创建者和必要启动后端，避免新游戏继承被限制状态。
                    if (exactGameProcesses.Contains(pid) || gameCreationHosts.Contains(pid) || FamilyBoundary.IsRequiredGameLaunchHost(nm, observedPath))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }
                    bool platformRemoved = whitelist.PlatformProcesses.TryGetValue(pid, out platformOwner) && !PlatformRuntimePolicy.Enabled(platformOwner.Platform);
                    bool whitelisted = whitelist.Protected.Contains(pid) || (eligibleService && ServiceWhitelisted(nm, servicePath));
                    if (FamilyBoundary.IsGameOrWhitelistProtected(pid, rendererPid, whitelisted, libraryFamily.Contains(pid), protectGameFamily, gamePids, gameDescendants, platformRemoved))
                    {
                        if (core.Release(pid, SuppressReason.Background))
                        {
                            ReportUntrack(pid);
                        }
                        continue;
                    }
                    string imagePath = ((processInfo != null) ? processInfo.Path : null);
                    creation = ((processInfo != null) ? processInfo.Creation : 0);
                    cpu = ((processInfo != null) ? processInfo.Cpu : 0);
                    ulong io = ((processInfo != null) ? processInfo.Io : 0);
                    if (processInfo == null)
                    {
                        IntPtr intPtr = Native.OpenProcess(4096, false, pid);
                        if (intPtr != IntPtr.Zero)
                        {
                            try
                            {
                                imagePath = Native.ImagePath(intPtr);
                                Native.QueryProcessSample(intPtr, out creation, out cpu, out io);
                            }
                            finally
                            {
                                Native.CloseHandle(intPtr);
                            }
                        }
                    }
                    if (SelfBuildGuard.IsOwnBuild(nm, imagePath))
                    {
                        if (core.Release(pid, SuppressReason.Background))
                        {
                            ReportUntrack(pid);
                        }
                        continue;
                    }
                    if (!PerformanceScopeAllows(imagePath))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }
                    if (TryProtectOverlayHost(pid, creation, nm, imagePath, protectGameFamily))
                    {
                        continue;
                    }
                    string libraryRoot = FamilyBoundary.LibraryRootOf(imagePath, protectedRoots);
                    if (libraryRoot == null && protectGameFamily)
                    {
                        libraryRoot = activeGameRoot;
                    }
                    if (!FamilyBoundary.BasicBackgroundEligible(pid, selfPid, nm, imagePath, (sameSession || eligibleService) ? selfSession : (-1), selfSession, foregroundPid, userFacingFamily.Contains(pid), windowsPrefix, protectRendererAncestors && rendererAncestors.Contains(pid), libraryRoot, wideScope, protectGameFamily, creation, platformRemoved, preserveOnForeground))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }
                    if (SelfProtectedRoster.Contains(nm))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        selfProtectedCount++;
                        continue;
                    }
                    if (eligibleService)
                    {
                        serviceCount++;
                    }
                    suppressionLevel = (EffSuppress ? BackgroundLevel() : SuppressionLevel.None);
                    string trackedName = core.NameOf(pid);
                    if (trackedName != null)
                    {
                        if (!string.Equals(trackedName, nm, StringComparison.OrdinalIgnoreCase))
                        {
                            ReportUntrack(pid);
                        }
                        else if (suppressionLevel != SuppressionLevel.None && core.HasReason(pid, SuppressReason.Background) && core.LevelOf(pid, SuppressReason.Background) == suppressionLevel)
                        {
                            bool reconciled = false;
                            if (!RunBackgroundPolicy(epoch, delegate
                            {
                                reconciled = core.Reconcile(pid, nm, SuppressReason.Background);
                            }))
                            {
                                return;
                            }
                            if (reconciled) continue;
                        }
                    }
                    if (suppressionLevel == SuppressionLevel.None)
                    {
                        if (core.Release(pid, SuppressReason.Background))
                        {
                            ReportUntrack(pid);
                        }
                    }
                    else if (EffSuppress)
                    {
                        pending.Add(new BackgroundRequest
                        {
                            Pid = pid,
                            Name = nm,
                            Creation = creation,
                            Cpu = cpu,
                            Desired = suppressionLevel,
                            Previous = core.LevelOf(pid, SuppressReason.Background),
                            HadBackgroundReason = core.HasReason(pid, SuppressReason.Background)
                        });
                    }
                }
                catch
                {
                }
            }
            SuppressionCore.BatchResult batchResult = null;
            if (!RunBackgroundPolicy(epoch, delegate
            {
                core.BeginBatch();
                try
                {
                    foreach (BackgroundRequest item in pending)
                    {
                        try
                        {
                            item.Result = core.Acquire(item.Pid, item.Name, SuppressReason.Background, null, item.Desired, item.Creation);
                        }
                        catch
                        {
                            item.Result = AcquireResult.AlreadyProtected;
                        }
                    }
                }
                finally
                {
                    batchResult = core.EndBatch();
                }
            }))
            {
                return;
            }
            foreach (BackgroundRequest request in pending)
            {
                if ((request.Result == AcquireResult.NewlyThrottled || request.Result == AcquireResult.AlreadyThrottled) && (batchResult == null || !batchResult.WasApplied(request.Pid)))
                {
                    string failureDetailValue = ((batchResult != null) ? batchResult.FailureOf(request.Pid) : "batch-missing");
                    if (failureDetailValue == "self-protected")
                    {
                        request.Result = AcquireResult.NewlyProtected;
                        continue;
                    }
                    request.Result = AcquireResult.ApplyFailed;
                    request.FailureDetail = failureDetailValue;
                }
            }
            foreach (BackgroundRequest request in pending)
            {
                if (request.Result == AcquireResult.NewlyThrottled)
                {
                    throttledCount++;
                    ReportTrack(request.Pid, request.Name);
                    SuccessLogBatch.Record("background-applied", request.Name, request.Pid, request.Creation,
                        "level=" + request.Desired);
                }
                else if (request.Result == AcquireResult.AlreadyThrottled)
                {
                    if (!request.HadBackgroundReason)
                    {
                        ReportTrack(request.Pid, request.Name);
                        SuccessLogBatch.Record("background-applied", request.Name, request.Pid, request.Creation,
                            "level=" + request.Desired);
                    }
                    else if (request.Previous != request.Desired)
                    {
                        SuccessLogBatch.Record("background-level-changed", request.Name, request.Pid, request.Creation,
                            "from=" + request.Previous + " to=" + request.Desired);
                    }
                }
                else if (request.Result == AcquireResult.NewlyProtected)
                {
                    protectedCount++;
                }
                else if (request.Result == AcquireResult.ApplyFailed)
                {
                    failedCount++;
                    SuccessLogBatch.FlushPending();
                    Logger.Log(Lang.T("log.gamemodesweep.1", 0) + request.Name + "（进程编号 " + request.Pid + ") " + ApplyFailureText.Of(request.FailureDetail) + Lang.T("log.gamemodeboost.24", 0));
                }
            }
            HashSet<int> coreProtected = new HashSet<int>(userFacingFamily);
            coreProtected.UnionWith(whitelist.Protected);
            foreach (KeyValuePair<int, PlatformProcessOwner> platformProcess in whitelist.PlatformProcesses)
            {
                if (!PlatformRuntimePolicy.Enabled(platformProcess.Value.Platform) && !whitelist.Protected.Contains(platformProcess.Key))
                {
                    coreProtected.Remove(platformProcess.Key);
                }
            }
            if (!RunBackgroundPolicy(epoch, delegate
            {
                SyncCoreBackground(coreProtected, foregroundPid);
            }))
            {
                return;
            }
            FamilyBoundary.PruneCatalogVerdicts(observedPids);
            foreach (int trackedPid in core.PidsWith(SuppressReason.Background))
            {
                if (!observedPids.Contains(trackedPid) && core.Release(trackedPid, SuppressReason.Background))
                {
                    ReportUntrack(trackedPid);
                }
            }
            if (!firstSweepSnapshot)
            {
                return;
            }
            if (EffSuppress)
            {
                string presetExtremeText = ((preset == PerformancePreset.Extreme) ? Lang.T("preset.extreme") : (PresetValue.IsCompetitive(preset) ? Lang.T("preset.competitive") : ((preset == PerformancePreset.Handheld) ? Lang.T("preset.handheld") : Lang.T("preset.standard"))));
                bool sustainedGovernance = PresetValue.IsCompetitive(preset) || preset == PerformancePreset.Handheld;
                string message = presetExtremeText + (sustainedGovernance ? Lang.T("t.gamemodesweep.2") : Lang.T("t.gamemodesweep.3")) + (wideScope ? Lang.T("t.gamemodesweep.5") : "");
                Logger.Log(string.Format(Lang.T("background.scope", 0), serviceCount, SuppressionCore.DisableDynamicBoost(BackgroundLevel(), false)));
                Logger.Log(Lang.T("log.gamemodesweep.1", 0) + message + (SuppressionCore.GpuDemoteEnabled ? Lang.T("log.gamemodesweep.6", 0) : "") + Lang.T("log.gamemodesweep.7", 0) + throttledCount + Lang.T("log.gamemodesweep.8", 0) + ((failedCount > 0) ? (" " + failedCount + Lang.T("log.gamemodesweep.9", 0)) : "") + ((protectedCount > 0) ? (" " + protectedCount + Lang.T("log.gamemodesweep.10", 0)) : "") + ((selfProtectedCount > 0) ? (" " + selfProtectedCount + Lang.T("t.gamemodesweep.11", 0)) : ""));
            }
            lock (sync)
            {
                firstSweep = false;
            }
        }
        private HashSet<int> CollectUserFacingFamily(int foregroundPid, WhitelistEvaluation whitelist, int excludeRootPid)
        {
            return CollectUserFacingFamily(foregroundPid, GameSessionDetector.VisibleWindowPids(true), whitelist, excludeRootPid);
        }
        private HashSet<int> CollectUserFacingFamily(int foregroundPid, HashSet<int> visible, WhitelistEvaluation whitelist, int excludeRootPid)
        {
            HashSet<int> hashSet = new HashSet<int>();
            foreach (KeyValuePair<int, WhitelistProcessInfo> process in whitelist.Processes)
            {
                try
                {
                    int key = process.Key;
                    if (excludeRootPid <= 0 || key != excludeRootPid)
                    {
                        WhitelistProcessInfo value = process.Value;
                        if (selfSession >= 0 && value.Session == selfSession && (key == foregroundPid || visible.Contains(key)))
                        {
                            hashSet.Add(key);
                        }
                    }
                }
                catch
                {
                }
            }
            return FamilyBoundary.ExpandUserFacingFamily(whitelist.Parents, whitelist.Names, hashSet);
        }

    }
}
