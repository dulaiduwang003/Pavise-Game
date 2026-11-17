using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private static bool GovernedGameLauncher(string name, string path)
        {
            if (!string.Equals(WhitelistRule.NormalizeName(name), "wegame", StringComparison.OrdinalIgnoreCase)) return false;
            string image = WhitelistRule.NormalizeImagePath(path);
            return image.Length == 0 || string.Equals(WhitelistRule.NormalizeName(System.IO.Path.GetFileName(image)), "wegame", StringComparison.OrdinalIgnoreCase);
        }
        private void SweepWithStableWhitelist(ProcessSnapshot all, HashSet<int> gamePids)
        {
            using (SuccessLogBatch.Begin("background-sweep session=" + AutomationSession))
                SweepWithStableWhitelistCore(all, gamePids);
        }
        // 深度调优里的“反作弊压制”按当前保存的游戏设置逐轮读取，开关一改下一轮扫描就按新值治理或放手；
        // 没有对应游戏时用本局或全局设置，默认开启。
        private bool AceGovernanceForSweep(PolicySnapshot policy)
        {
            GameProfile current = null;
            lock (sync)
            {
                if (activeDetection != null && activeDetection.Profile != null)
                {
                    GameProfile stored = FindProfileLocked(activeDetection.Profile.Id);
                    if (stored != null) current = stored.Clone();
                }
            }
            if (current != null) return PolicyResolver.For(current).AceGovernanceOn;
            return (policy ?? PolicyResolver.Global()).AceGovernanceOn;
        }
        private void SweepWithStableWhitelistCore(ProcessSnapshot all, HashSet<int> gamePids)
        {
            if (!ServiceWhitelistForwardAllowed()) return;
            PolicySnapshot policy = sessionPolicy;
            PerformancePreset preset = ((policy != null) ? policy.Preset : ActivePreset);
            SuppressionCore.BackgroundBoostLimit = (policy ?? PolicyResolver.Global()).BackgroundBoostLimitRequested;
            bool preserveOnForeground = preset == PerformancePreset.Competitive;
            int foregroundPid = GameSessionDetector.ForegroundPid();
            bool wideScope = WideBackgroundScope(policy ?? PolicyResolver.Global());
            // 保护判断要先知道本轮是否治理 ACE，所以开关在算白名单之前写入。
            bool governAce = AceGovernanceForSweep(policy);
            AntiCheatCatalog.AceGovernanceActive = governAce;
            WhitelistEvaluation whitelist = EvaluateWhitelist(all);
            Dictionary<int, WindowsBackgroundTarget> windowsTargets = SelectWindowsBackground(all, gamePids, whitelist);
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
                    if (ServiceWhitelistBlocksOrdinary(pid)) continue;
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
                    // verifiedBackground 是身份已经核实、按普通后台处理的两类对象：微软后台，以及本局开着“反作弊压制”时的 ACE。
                    // 它们不受系统预设名单、会话和 Windows 目录门槛豁免；用户手工规则在核实阶段排除，
                    // 游戏家族、运行期保护和服务白名单照常生效。
                    WindowsBackgroundTarget windowsTarget;
                    bool verifiedBackground = windowsTargets.TryGetValue(pid, out windowsTarget) && windowsTarget.Process.Creation == procEntry.Creation;
                    // ACE 要核实 Program Files\AntiCheatExpert 下的身份；用户手工加入白名单的 ACE 照常豁免。
                    // 开关关着时 ACE 已在上面的运行期保护里放过，这里不会走到。
                    if (!verifiedBackground && governAce && AntiCheatCatalog.IsGovernedAceName(nm))
                    {
                        string acePath = (processInfo != null ? processInfo.Path : null) ?? ReadWindowsBackgroundPath(procEntry);
                        verifiedBackground = AntiCheatCatalog.IsGovernedAce(nm, acePath) && !ExplicitWindowsBackgroundProtection(procEntry, acePath);
                    }
                    // WeGame 主进程是英雄联盟的祖先且常有可见窗口，但每局游戏由 LeagueClient 创建；
                    // 因此不按游戏祖先（创建者、渲染祖先、游戏库家族）或可见窗口家族豁免，只有它本身在前台时不压。
                    bool governedLauncher = GovernedGameLauncher(nm, (processInfo != null ? processInfo.Path : null) ?? procEntry.Path);
                    if (TryProtectHardwareControlBackground(pid, procEntry.Creation, nm)) continue;
                    if (processInfo == null)
                    {
                        sameSession = selfSession >= 0 && procEntry.Session == selfSession;
                    }
                    string servicePath = (sameSession ? null : ServiceImagePath(procEntry, preset));
                    bool eligibleService = !sameSession && BackgroundServicePolicy.Eligible(preset, procEntry, servicePath, services, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                    if (!verifiedBackground && !sameSession && !eligibleService)
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
                    if (exactGameProcesses.Contains(pid) || gameCreationHosts.Contains(pid) && !governedLauncher || FamilyBoundary.IsRequiredGameLaunchHost(nm, observedPath))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }
                    bool platformRemoved = whitelist.PlatformProcesses.TryGetValue(pid, out platformOwner) && !PlatformRuntimePolicy.Enabled(platformOwner.Platform);
                    bool whitelisted = !verifiedBackground && (whitelist.Protected.Contains(pid) || (eligibleService && ServiceWhitelisted(nm, servicePath)));
                    if (FamilyBoundary.IsGameOrWhitelistProtected(pid, rendererPid, whitelisted, libraryFamily.Contains(pid) && !governedLauncher, protectGameFamily, gamePids, gameDescendants, platformRemoved))
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
                    // 系统服务进程常读不到进程样本；已核实的微软对象改用同一快照里的创建时间核对身份，不按 0 放行。
                    if (verifiedBackground && creation <= 0) creation = procEntry.Creation;
                    if (TryProtectAmdNoiseSuppression(pid, creation, nm, imagePath))
                    {
                        continue;
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
                    if (verifiedBackground ? (!preserveOnForeground && pid == foregroundPid)
                        : !FamilyBoundary.BasicBackgroundEligible(pid, selfPid, nm, imagePath, (sameSession || eligibleService) ? selfSession : (-1), selfSession, foregroundPid, userFacingFamily.Contains(pid) && !governedLauncher, windowsPrefix, protectRendererAncestors && rendererAncestors.Contains(pid) && !governedLauncher, libraryRoot, wideScope, protectGameFamily, creation, platformRemoved, preserveOnForeground))
                    {
                        ReleaseBackgroundExemption(pid, nm, null);
                        continue;
                    }
                    if (!verifiedBackground && SelfProtectedRoster.Contains(nm))
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
                Logger.Log(string.Format(Lang.T("background.scope", 0), serviceCount, SuppressionCore.DisableDynamicBoost(BackgroundLevel())));
                Logger.Log(Lang.T("log.gamemodesweep.1", 0) + message + (SuppressionCore.GpuDemoteEnabled ? Lang.T("log.gamemodesweep.6", 0) : "") + Lang.T("log.gamemodesweep.7", 0) + throttledCount + Lang.T("log.gamemodesweep.8", 0) + ((failedCount > 0) ? (" " + failedCount + Lang.T("log.gamemodesweep.9", 0)) : "") + ((protectedCount > 0) ? (" " + protectedCount + Lang.T("log.gamemodesweep.10", 0)) : "") + ((selfProtectedCount > 0) ? (" " + selfProtectedCount + Lang.T("t.gamemodesweep.11", 0)) : ""));
            }
            lock (sync)
            {
                firstSweep = false;
            }
        }
        private bool TryProtectHardwareControlBackground(int pid, long creation, string name)
        {
            if (!HardwareControlCatalog.IsHardwareControlProcess(name)) return false;
            // 未知身份只阻止新的普通限制；恢复只使用本方持有且创建时间一致的记录。
            if (creation <= 0 || core.CreationOf(pid) != creation) return true;
            BackgroundReleaseState state = core.ReleaseBackgroundForRenderer(pid, creation, name);
            if (RuntimeBackgroundExempt(state)) ReportUntrack(pid);
            return true;
        }
        private bool TryProtectAmdNoiseSuppression(int pid, long creation, string name, string path)
        {
            if (!PeripheralCatalog.IsAmdNoiseSuppressionProcess(name, path)) return false;
            // 撤销整条普通后台资格，优先级、QoS、GPU、计时器与收核仍沿既有原值及恢复预算结清。
            // Pending 只表示恢复债尚在，不能把不再接管误报成已经恢复。
            BackgroundReleaseState state = core.ReleaseBackgroundForRenderer(pid, creation, name);
            if (state == BackgroundReleaseState.Ready || state == BackgroundReleaseState.Gone) ReportUntrack(pid);
            return true;
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
            return hashSet.Count == 0 ? hashSet
                : FamilyBoundary.ExpandUserFacingFamily(whitelist.Parents, whitelist.Names, hashSet, whitelist.FamilyDescendants);
        }

    }
}
