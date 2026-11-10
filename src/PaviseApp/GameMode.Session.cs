using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void ApplyGameProcessActions(ProcessSnapshot all)
        {
            if (EffBoost)
            {
                corePlacementOnly = false;
                Boost(all);
                return;
            }
            if (!corePlacementOnly)
            {
                if (!UnboostGames())
                {
                    return;
                }
                corePlacementOnly = true;
            }
            BoostPass pass = PrepareBoostPass();
            DropStaleBoosts(pass);
            RunIndependentCorePlacement(pass);
        }
        private bool SyncGuardScheduling()
        {
            lock (sync)
            {
                SyncAutomationPolicy();
                if (!enabled || stopping || ProfileStoreSaveFailed)
                {
                    return SetGuardScheduling(false, false);
                }
                guardDvrActive = EnvStep("gamedvr", killGameDvr, guardDvrActive || (!killGameDvr && GameDvr.HasResidue), GameDvr.Activate, GameDvr.Restore);
                guardMmcssActive = EnvStep("mmcss", mmcssOn, guardMmcssActive || (!mmcssOn && Mmcss.HasResidue()), Mmcss.Activate, Mmcss.Restore);
                bool dvrSettled = guardDvrActive == killGameDvr && !envFused.Contains("gamedvr") && !envRestorePending.Contains("gamedvr");
                bool mmcssSettled = guardMmcssActive == mmcssOn && !envFused.Contains("mmcss") && !envRestorePending.Contains("mmcss");
                if (dvrSettled)
                {
                    RecordAutomation("gamedvr", killGameDvr ? "applied" : "restored");
                }
                if (mmcssSettled)
                {
                    RecordAutomation("mmcss", mmcssOn ? "applied" : "restored");
                }
                return dvrSettled && mmcssSettled;
            }
        }
        private bool SetGuardScheduling(bool dvr, bool mmcss)
        {
            bool skipped; string reason;
            bool runGuardedSucceeded = dvr ? RunGuarded(GameDvr.Activate) : RunAutomaticRestoreCore("gamedvr", GameDvr.Restore, out skipped, out reason);
            bool runGuardedSucceeded2 = mmcss ? RunGuarded(Mmcss.Activate) : RunAutomaticRestoreCore("mmcss", Mmcss.Restore, out skipped, out reason);
            if (runGuardedSucceeded)
            {
                guardDvrActive = dvr;
                envRestorePending.Remove("gamedvr");
                envFused.Remove("gamedvr");
                envNextAttempt.Remove("gamedvr");
                envFailures.Remove("gamedvr");
            }
            if (runGuardedSucceeded2)
            {
                guardMmcssActive = mmcss;
                envRestorePending.Remove("mmcss");
                envFused.Remove("mmcss");
                envNextAttempt.Remove("mmcss");
                envFailures.Remove("mmcss");
            }
            RecordAutomation("gamedvr", (!runGuardedSucceeded) ? (dvr ? "failed" : "restore-failed") : (dvr ? "applied" : "restored"));
            RecordAutomation("mmcss", (!runGuardedSucceeded2) ? (mmcss ? "failed" : "restore-failed") : (mmcss ? "applied" : "restored"));
            if (runGuardedSucceeded)
            {
                return runGuardedSucceeded2;
            }
            return false;
        }
        private static bool RunGuarded(Func<bool> action)
        {
            try
            {
                return action != null && action();
            }
            catch
            {
                return false;
            }
        }
        private bool ProcessorTuningRequested(PolicySnapshot effective)
        {
            return effective != null && effective.ProcessorTuning;
        }
        private void NotifyExtensionSession(bool active)
        {
            GameProfile gameProfile = null;
            int rendererPid = 0;
            long rendererCreation = 0L;
            GameDetection renderer = null;
            if (active)
            {
                lock (sync)
                {
                    if (this.active && activeDetection != null && activeDetection.RendererCandidateSelected
                        && !activeDetection.RendererSafetyOnly && !activeDetection.RequiresGpuConfirm)
                    {
                        renderer = RendererHandoffTracker.Copy(activeDetection);
                    }
                }
            }
            if (renderer != null && GameExtension.Current != null
                && GameExtension.Current.AppliesTo(renderer.Profile) && HasRendererObservation(renderer.Profile))
            {
                RendererObservationStore observations = rendererObservations;
                if (observations != null)
                {
                    observations.Validate(renderer.Profile.Id, renderer.RendererPath);
                    if (observations.Has(renderer.Profile.Id, renderer.RendererPath))
                    {
                        lock (sync)
                        {
                            if (this.active && !stopping && enabled && !panicReq && !ProfileStoreSaveFailed
                                && RendererHandoffTracker.SameIdentity(renderer, activeDetection)
                                && RendererHandoffTracker.SameProfile(renderer.Profile, activeDetection.Profile))
                            {
                                gameProfile = renderer.Profile;
                                rendererPid = renderer.RendererPid;
                                rendererCreation = renderer.RendererCreation;
                            }
                        }
                    }
                }
            }
            GameExtension.NotifySession(gameProfile, rendererPid, rendererCreation, gameProfile != null);
        }
        private bool SessionServiceSuspended(string envKey, long nowTicks)
        {
            if (!smartSwitchedOut)
            {
                return false;
            }
            if (ForegroundConsumesService(switchedOutForegroundName, envKey))
            {
                return true;
            }
            return SwitchOutDebounceElapsed(nowTicks);
        }
        private bool UpdateSessionForeground(ProcessSnapshot all, int rendererPid, HashSet<int> gamePids)
        {
            int rendererForegroundPidResult = RendererForegroundPid();
            bool hasRendererForeground = rendererForegroundPidResult > 4;
            bool gameForeground = hasRendererForeground && rendererPid > 0 && (rendererForegroundPidResult == rendererPid || (gamePids != null && gamePids.Contains(rendererForegroundPidResult)));
            long ticks = DateTime.UtcNow.Ticks;
            bool returning = sessionForeground.State == SessionForegroundState.SwitchedOut;
            bool observeSucceeded = sessionForeground.Observe(gameForeground, hasRendererForeground, ticks);
            if (hasRendererForeground && !gameForeground)
            {
                switchedOutForegroundPid = rendererForegroundPidResult;
                ProcEntry procEntry = ((all == null) ? null : all.Find(rendererForegroundPidResult));
                switchedOutForegroundName = ((procEntry == null || string.IsNullOrEmpty(procEntry.Name)) ? "" : procEntry.Name);
            }
            bool switchedOut = sessionForeground.State == SessionForegroundState.SwitchedOut;
            bool smart = switchedOut && EffPreset == PerformancePreset.Standard;
            sessionSwitchedOut = switchedOut;
            smartSwitchedOut = smart;
            if (observeSucceeded)
            {
                if (switchedOut)
                {
                    OnSessionSwitchedOut(smart);
                }
                else
                {
                    OnSessionSwitchedBack(returning);
                }
            }
            return !switchedOut;
        }
        private void OnSessionSwitchedOut(bool smart)
        {
            Interlocked.Increment(ref optionalServiceGeneration);
            string switchedOutForegroundNameOffset = " foregroundPid=" + switchedOutForegroundPid + " foregroundExe=" + switchedOutForegroundName;
            if (!smart)
            {
                Logger.Info("前台 [" + AutomationSession + "] 事件=切出 场景=竞技 后台策略=保留 电源策略=保留 亲和性策略=保留" + switchedOutForegroundNameOffset);
                return;
            }
            Logger.Info("前台 [" + AutomationSession + "] 事件=切出 场景=智能 后台策略=释放 电源策略=保留 亲和性策略=保留" + switchedOutForegroundNameOffset);
            ResetAdaptiveGuard();
        }
        private void OnSessionSwitchedBack(bool returning)
        {
            Interlocked.Increment(ref optionalServiceGeneration);
            lock (sync)
            {
                firstSweep = true;
                if (returning && activeDetection != null)
                {
                    gameBoostNextAudit.Remove(activeDetection.RendererPid);
                }
            }
            if (returning)
            {
                exclusiveCheckTicks = 0L;
                Interlocked.Exchange(ref criticalForegroundAuditPending, 1);
                Logger.Info("前台 [" + AutomationSession + "] 事件=切回 场景=" + (EffPreset == PerformancePreset.Standard ? "smart" : "competitive"));
            }
            Interlocked.Exchange(ref urgentProcessScan, 1);
            try
            {
                kick.Set();
            }
            catch
            {
            }
        }
        private void ResetSessionForeground()
        {
            sessionForeground.Reset();
            Interlocked.Exchange(ref criticalForegroundAuditPending, 0);
            smartSwitchedOut = false;
            sessionSwitchedOut = false;
            switchedOutForegroundName = "";
            switchedOutForegroundPid = 0;
        }
        public int AddScannedGames(IList<ScanHit> hits, out string lastError)
        {
            lastError = null;
            int processedCount = 0;
            if (hits == null)
            {
                return 0;
            }
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked())
                {
                    return 0;
                }
                List<GameProfile> next = GetProfiles();
                HashSet<string> nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
                foreach (ScanHit hit in hits)
                {
                    if (hit != null && !string.IsNullOrEmpty(hit.Exe))
                    {
                        string error;
                        if (StageGameExecutable(hit.Name, hit.Exe, hit.Root, next, nextIgnore, out error))
                        {
                            processedCount++;
                        }
                        else if (!string.IsNullOrEmpty(error))
                        {
                            lastError = error;
                        }
                    }
                }
                if (processedCount > 0 && !CommitLibraryLocked(next, nextIgnore))
                {
                    return 0;
                }
            }
            if (processedCount > 0)
            {
                KickLibraryChanged();
            }
            return processedCount;
        }
        public void Start()
        {
            shutdownWritersStopped = false;
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Start();
        }
        public bool Stop()
        {
            shutdownWritersStopped = false;
            Stopwatch elapsed = Stopwatch.StartNew();
            stopping = true;
            InvalidatePowerPlanSession();
            SyncLaboratory();
            RevokeCriticalThreadIntent("Stop");
            SyncPerformanceAutomation();
            InvalidateCacheWarm();
            InvalidateStandbyCleanerWork();
            InvalidateEnglishInputWork();
            kick.Set();
            if (Monitor.IsEntered(sync) || !Monitor.TryEnter(sync, 8000))
            {
                return false;
            }
            try
            {
                ClearFamilyDiscovery();
                InvalidateRendererHandoff();
            }
            finally
            {
                Monitor.Exit(sync);
            }
            Thread thread = worker;
            if (thread != null && (thread == Thread.CurrentThread || !thread.Join(RemainingShutdownMs(elapsed, 8000))))
            {
                return false;
            }
            if (!DrainAsyncShutdown(RemainingShutdownMs(elapsed, 8000)))
            {
                return false;
            }
            bool manualRestored = false;
            try { manualRestored = LaboratoryManualActions.CloseAndRestoreRuntime(8000); }
            catch { }
            // 已停止的写入方仍可持有债务；只有仍在运行的写入方会阻止无关清理。
            if (!manualRestored && LaboratoryManualActions.Busy) return false;
            bool allWorkersStopped = true;
            try { if (laboratory != null && !laboratory.Close(8000)) allWorkersStopped = false; }
            catch { allWorkersStopped = false; }
            try
            {
                if (!FrameCoordination.Close(8000))
                {
                    allWorkersStopped = false;
                }
            }
            catch
            {
                allWorkersStopped = false;
            }
            try
            {
                if (!RenderLane.CloseForShutdown(8000))
                {
                    allWorkersStopped = false;
                }
            }
            catch
            {
                allWorkersStopped = false;
            }
            try
            {
                if (standbyCleaner != null && !standbyCleaner.Close(8000))
                {
                    allWorkersStopped = false;
                }
            }
            catch
            {
                allWorkersStopped = false;
            }
            if (!allWorkersStopped)
            {
                return false;
            }
            if (!performanceActions.Close(8000))
            {
                return false;
            }
            bool stopGameNvDeliverySucceeded = StopGameNvDelivery(RemainingShutdownMs(elapsed, 8000));
            bool drainHistoricalCriticalThreadsSucceeded = DrainHistoricalCriticalThreads(RemainingShutdownMs(elapsed, 8000));
            bool stopCriticalThreadsSucceeded = StopCriticalThreads(RemainingShutdownMs(elapsed, 8000), "Stop");
            if (!stopGameNvDeliverySucceeded || !drainHistoricalCriticalThreadsSucceeded || !stopCriticalThreadsSucceeded)
            {
                return false;
            }
            bool recovered = FinishShutdownRecovery(core.RestoreAutomaticLayouts,
                () => { bool isolationClean = StopCoreIsolation(); bool backgroundClean = core.StopBackgroundAffinityJobs(); bool windowsClean = windowsBackground.Stop(); return coreAffinityJobs.Stop() && backgroundClean && isolationClean && windowsClean; }, RestoreAllHardPins, RestoreGuardScheduling);
            SelfYield.Release();
            return recovered && manualRestored && !SelfYield.HasResidue;
        }
        private bool DrainAsyncShutdown(int timeoutMs)
        {
            if (!stopping || timeoutMs < 0)
            {
                return false;
            }
            Stopwatch elapsed = Stopwatch.StartNew();
            if (cacheWarm != null && !cacheWarm.Drain(RemainingShutdownMs(elapsed, timeoutMs)))
            {
                return false;
            }
            if (!DrainShutdownGate(driverStageGate, RemainingShutdownMs(elapsed, timeoutMs)))
            {
                return false;
            }
            if (!DrainLaptopPerformanceWork(RemainingShutdownMs(elapsed, timeoutMs)))
            {
                return false;
            }
            if (!DrainShutdownGate(powerApplyGate, RemainingShutdownMs(elapsed, timeoutMs)))
            {
                return false;
            }
            if (!DrainShutdownGate(whiteEvalSync, RemainingShutdownMs(elapsed, timeoutMs)))
            {
                return false;
            }
            if (!DrainEnglishInput(RemainingShutdownMs(elapsed, timeoutMs)))
            {
                return false;
            }
            if (standbyCleaner != null && !standbyCleaner.Close(RemainingShutdownMs(elapsed, timeoutMs)))
            {
                return false;
            }
            RendererObservationStore rendererObservationStore = rendererObservations;
            if (rendererObservationStore != null)
            {
                return rendererObservationStore.Close(RemainingShutdownMs(elapsed, timeoutMs));
            }
            return true;
        }
        private static int RemainingShutdownMs(Stopwatch elapsed, int timeoutMs)
        {
            return (int)Math.Max(0L, timeoutMs - elapsed.ElapsedMilliseconds);
        }
        private static bool DrainShutdownGate(object gate, int timeoutMs)
        {
            if (Monitor.IsEntered(gate) || !Monitor.TryEnter(gate, timeoutMs))
            {
                return false;
            }
            try
            {
                return true;
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }
        public void Poke()
        {
            RequestPolicyApply("Poke");
        }
        private void Loop()
        {
            while (!stopping)
            {
                if (manualLaboratoryMutation) { kick.WaitOne(100); continue; }
                IDisposable disposable = PolicyResolver.BeginReadScope();
                try
                {
                    RetryPendingHardPinRestores();
                    ApplyEnvironmentTimerOptions();
                    if (panicReq)
                    {
                        int value = Volatile.Read(ref panicSeq);
                        panicReq = false;
                        panicResult = Deactivate(Lang.T("t.gamemode.42"));
                        Volatile.Write(ref panicServed, value);
                        panicDone.Set();
                        kick.WaitOne(4000);
                        continue;
                    }
                    PrivacyControl.TryRestoreRetired(() => !active && !stopping && !panicReq);
                    SyncPerformanceAutomation();
                    SyncAutomaticFullscreenOptimizations(false, -1L);
                    SyncGameNvDelivery();
                    MaybeRecoverHistoricalCriticalThreads();
                    if (!enabled || ProfileStoreSaveFailed)
                    {
                        InvalidateRendererHandoff();
                        bool cleanupNeeded;
                        lock (sync)
                        {
                            cleanupNeeded = active || gameBoost.Count > 0;
                        }
                        if (cleanupNeeded || EnvActive() || core.AnyWith(SuppressReason.Background))
                        {
                            RetryDeactivate(Lang.T("t.gamemode.43"));
                        }
                    }
                    else if (!ScanGameSession())
                    {
                        continue;
                    }
                    Logger.RepeatedRecovered("GameMode");
                }
                catch (Exception ex)
                {
                    RevokeCriticalThreadIntent("loop-exception");
                    StopCriticalThreads(0, "loop-exception");
                    Logger.RepeatedError("GameMode", Lang.T("log.gamemode.52", 0) + ex.GetType().Name + ": " + ex.Message, -1L);
                }
                finally
                {
                    disposable.Dispose();
                }
                kick.WaitOne(LoopWaitMs(enabled ? ProcessScanWaitMs() : 4000));
            }
            bool shutdownCleanupNeeded;
            lock (sync)
            {
                shutdownCleanupNeeded = active || gameBoost.Count > 0;
            }
            bool deactivated = true;
            if (shutdownCleanupNeeded || core.AnyWith(SuppressReason.Background) || EnvActive())
            {
                deactivated = Deactivate(Lang.T("t.gamemode.53"));
            }
            if (panicReq)
            {
                int value2 = Volatile.Read(ref panicSeq);
                panicReq = false;
                panicResult = deactivated;
                Volatile.Write(ref panicServed, value2);
                panicDone.Set();
            }
            try
            {
                RestoreGuardScheduling();
            }
            catch
            {
            }
        }
        private bool ScanGameSession()
        {
            bool fallbackOnly;
            if (!ShouldRunProcessScan(out fallbackOnly))
            {
                kick.WaitOne(LoopWaitMs(ProcessScanWaitMs()));
                return false;
            }
            ProcessSnapshot processSnapshot = null;
            CountProcessScan();
            int maxAgeMs = ((fallbackOnly && ProcessEventsAvailable && IsActive) ? 5000 : 0);
            try
            {
                processSnapshot = ProcessSnapshotSource.Capture(selfSession, maxAgeMs);
            }
            catch
            {
                processSnapshot = null;
            }
            if (processSnapshot == null)
            {
                RequeueProcessScanAfterFailure();
                return true;
            }
            PruneWhitelistFamilyMembersIfDue(processSnapshot);
            antiCheatProcessSnapshot = processSnapshot;
            HashSet<int> gamePids;
            string runningGame = FindRunningGame(processSnapshot, out gamePids);
            if (!enabled || stopping || panicReq || ProfileStoreSaveFailed)
            {
                return false;
            }
            if (runningGame != null)
            {
                string detectedProfileId;
                lock (sync)
                {
                    detectedProfileId = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Id : null);
                }
                if (gameGoneSinceTicks != 0)
                {
                    gameGoneSinceTicks = 0L;
                    if (active)
                    {
                        Logger.Log(Lang.T("log.gamemode.44", 0));
                        lock (sync)
                        {
                            firstSweep = true;
                        }
                    }
                }
                if (!active)
                {
                    lock (sync)
                    {
                        if (manualLaboratoryMutation) return false;
                        active = true;
                        activeGame = runningGame;
                        firstSweep = true;
                    }
                    Logger.Log(Lang.T("log.gamemode.45", 0) + runningGame);
                    ResetAdaptiveGuard();
                    Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
                    BeginSessionPolicy();
                    ReportBegin(runningGame);
                    NotifyExtensionSession(true);
                    slowEnvAtTicks = DateTime.UtcNow.AddSeconds(20.0).Ticks;
                }
                else if (!SameReportedProfile(repProfileId, detectedProfileId))
                {
                    lock (sync)
                    {
                        activeGame = runningGame;
                    }
                    Logger.Log(Lang.T("log.gamemode.46", 0) + runningGame);
                    ResetAdaptiveGuard();
                    Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
                    ReportFinish();
                    BeginSessionPolicy();
                    ReportBegin(runningGame);
                    NotifyExtensionSession(true);
                }
                else if (!string.Equals(activeGame, runningGame, StringComparison.Ordinal))
                {
                    lock (sync)
                    {
                        activeGame = runningGame;
                    }
                }
                int rendererPid;
                long birth;
                lock (sync)
                {
                    rendererPid = ((activeDetection != null) ? activeDetection.RendererPid : 0);
                    birth = ((activeDetection != null) ? activeDetection.RendererCreation : 0);
                }
                UpdateSessionForeground(processSnapshot, rendererPid, gamePids);
                bool smartSceneSwitchedOut = SmartSceneSwitchedOut;
                StepEnglishInputSession();
                ApplyEnv();
                SyncPerformanceAutomation();
                ApplyStandbyCleanerPolicy();
                ApplyCacheWarmPolicy();
                RefreshAutoCoreMode();
                StepWindowsBackground(processSnapshot, gamePids, smartSceneSwitchedOut);
                if (EffSuppress && !smartSceneSwitchedOut)
                {
                    Sweep(processSnapshot, gamePids);
                }
                else
                {
                    ReleaseBackground(smartSceneSwitchedOut ? SwitchOutReleaseText : Lang.T("t.gamemodeenv.41"));
                }
                SelfYield.TrackHelperRenderer(rendererPid, birth);
                SelfYield.Engage();
                ApplyGameProcessActions(processSnapshot);
                StepCriticalThreadRuntime();
                if (!smartSceneSwitchedOut)
                {
                    StepAdaptiveGuard();
                }
            }
            else if (active)
            {
                StepDisplayAwake(false);
                long ticks = DateTime.UtcNow.Ticks;
                if (gameGoneSinceTicks == 0)
                {
                    gameGoneSinceTicks = ticks;
                    InvalidatePowerPlanSession();
                    RevokeCriticalThreadIntent("session-unconfirmed/exit-grace");
                    StopCriticalThreads(0, "session-unconfirmed/exit-grace");
                    InvalidateStandbyCleanerWork();
                    InvalidateCacheWarm();
                    InvalidateEnglishInputWork();
                    Logger.Log(Lang.T("log.gamemode.47", 0) + ExitGraceSeconds + Lang.T("log.gamemode.48", 0));
                    gracePreReleased += ReleaseBackground(Lang.T("t.gamemode.49"));
                    windowsBackground.Restore();
                }
                if (ExitGraceElapsed(gameGoneSinceTicks, ticks, ExitGraceSeconds))
                {
                    Deactivate(Lang.T("t.gamemode.50"));
                }
                if (gameGoneSinceTicks != 0)
                {
                    Interlocked.Exchange(ref transitionScanPending, 1);
                }
            }
            else
            {
                DropVanishedBoosts();
                bool boostsPending;
                lock (sync)
                {
                    boostsPending = gameBoost.Count > 0;
                }
                if (boostsPending || EnvActive() || core.AnyWith(SuppressReason.Background))
                {
                    RetryDeactivate(Lang.T("t.gamemode.51"));
                }
                TryAutoAddForegroundGame();
            }
            return true;
        }

        public bool NeedsGameProcessIdentity(string name, int session)
        {
            if (session != selfSession || string.IsNullOrEmpty(name))
            {
                return false;
            }
            lock (sync)
            {
                foreach (GameProfile profile in profiles)
                {
                    if (GameSessionDetector.IsProfileEntryName(profile, name))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        private void CountProcessScan()
        {
            Interlocked.Increment(ref processScanCount);
        }
        public void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            if (batch == null || stopping)
            {
                return;
            }
            ObserveWhitelistProcessChanges(batch);
            ObserveGameFamilyChanges(batch);
            bool overflowed = batch.Overflowed;
            bool gameSessionBoundary = false;
            bool transitionChanged = false;
            lock (sync)
            {
                overflowed |= active || armedAwaitingElection;
                ProcessChange[] changes = batch.Changes;
                foreach (ProcessChange processChange in changes)
                {
                    if (processChange == null)
                    {
                        continue;
                    }
                    if (processChange.Kind == ProcessChangeKind.Stopped)
                    {
                        if (activeDetection != null && activeDetection.RendererPid == processChange.Pid)
                        {
                            gameSessionBoundary = true;
                        }
                        continue;
                    }
                    if (IsActiveFamilyChildStart(activeDetection, processChange, selfSession))
                    {
                        overflowed = true;
                        long rendererCreation = activeDetection.RendererCreation;
                        if (!IsSameTransitionEpoch(transitionProbeRendererPid, transitionProbeRendererCreation, activeDetection.RendererPid, rendererCreation))
                        {
                            transitionProbeRendererPid = activeDetection.RendererPid;
                            transitionProbeRendererCreation = rendererCreation;
                            transitionChanged = true;
                        }
                    }
                    if (string.IsNullOrEmpty(processChange.Name))
                    {
                        continue;
                    }
                    foreach (GameProfile profile in profiles)
                    {
                        if (GameSessionDetector.IsProfileEntryProcess(profile, processChange.Name, processChange.Path))
                        {

                            overflowed = true;
                            gameSessionBoundary = true;
                            break;
                        }
                    }
                }
            }
            if (batch.Overflowed)
            {
                Interlocked.Exchange(ref gameDetectionDirty, 1);
            }
            if (transitionChanged)
            {
                Interlocked.Exchange(ref gameDetectionDirty, 1);
                Interlocked.Exchange(ref transitionScanPending, 1);
            }
            if (ProcessEventNeedsImmediateScan(gameSessionBoundary))
            {
                Interlocked.Exchange(ref gameDetectionDirty, 1);
                Interlocked.Exchange(ref urgentProcessScan, 1);
            }
            if (overflowed)
            {
                Interlocked.Exchange(ref processSetDirty, 1);
                kick.Set();
            }
        }
        internal static bool IsSameTransitionEpoch(int storedPid, long storedCreation, int currentPid, long currentCreation)
        {
            if (storedPid <= 0 || storedPid != currentPid)
            {
                return false;
            }
            if (storedCreation > 0 && currentCreation > 0)
            {
                return storedCreation == currentCreation;
            }
            return true;
        }
        internal static bool ShouldRearmLauncherTransition(GameDetection previous, GameDetection next)
        {
            if (next != null)
            {
                if (previous != null)
                {
                    return !RendererHandoffTracker.SameIdentity(previous, next);
                }
                return true;
            }
            return false;
        }
        internal static bool ProcessEventNeedsImmediateScan(bool gameSessionBoundary)
        {
            return gameSessionBoundary;
        }
        internal static int ProcessScanIntervalMs(bool eventsAvailable)
        {
            if (!eventsAvailable)
            {
                return 4000;
            }
            return 20000;
        }
        internal static int ProcessScanIntervalMs(bool eventsAvailable, bool gameActive)
        {
            if (gameActive)
            {
                if (!eventsAvailable)
                {
                    return 500;
                }
                return 1000;
            }
            return ProcessScanIntervalMs(eventsAvailable);
        }
        internal static bool DirtyScanDue(bool eventsAvailable, bool dirty, long elapsedMs)
        {
            if (!eventsAvailable || !dirty)
            {
                return false;
            }
            if (elapsedMs >= 0)
            {
                return elapsedMs >= 500;
            }
            return true;
        }
        private bool ShouldRunProcessScan(out bool fallbackOnly)
        {
            fallbackOnly = false;
            long ticks = DateTime.UtcNow.Ticks;
            long processScanRetryAfterTicksSnapshot = Interlocked.Read(ref processScanRetryAfterTicks);
            if (processScanRetryAfterTicksSnapshot > ticks)
            {
                return false;
            }
            if (processScanRetryAfterTicksSnapshot > 0)
            {
                Interlocked.CompareExchange(ref processScanRetryAfterTicks, 0L, processScanRetryAfterTicksSnapshot);
            }
            long lastProcessScanTicksSnapshot = Interlocked.Read(ref lastProcessScanTicks);
            long remainingTicks = ticks - lastProcessScanTicksSnapshot;
            bool urgent = Interlocked.Exchange(ref urgentProcessScan, 0) != 0;
            bool processesChanged = Interlocked.Exchange(ref processSetDirty, 0) != 0;
            if (urgent)
            {
                Interlocked.Exchange(ref transitionScanPending, 0);
                Interlocked.Exchange(ref lastProcessScanTicks, ticks);
                return true;
            }
            if (Interlocked.CompareExchange(ref transitionScanPending, 0, 0) != 0 && (lastProcessScanTicksSnapshot <= 0 || remainingTicks < 0 || remainingTicks >= 50000000))
            {
                Interlocked.Exchange(ref transitionScanPending, 0);
                Interlocked.Exchange(ref lastProcessScanTicks, ticks);
                return true;
            }
            if (DirtyScanDue(ProcessEventsAvailable, processesChanged, ElapsedMsOrMax(lastProcessScanTicksSnapshot, remainingTicks)))
            {
                Interlocked.Exchange(ref transitionScanPending, 0);
                Interlocked.Exchange(ref lastProcessScanTicks, ticks);
                return true;
            }
            int processScanIntervalMsResult = ProcessScanIntervalMs(ProcessEventsAvailable, IsActive);
            if (armedAwaitingElection)
            {
                processScanIntervalMsResult = Math.Min(processScanIntervalMsResult, 4000);
            }
            long scaledProcessScanIntervalMsResult = (long)processScanIntervalMsResult * 10000L;
            if (lastProcessScanTicksSnapshot <= 0 || remainingTicks < 0 || remainingTicks >= scaledProcessScanIntervalMsResult)
            {
                fallbackOnly = !armedAwaitingElection;
                Interlocked.Exchange(ref transitionScanPending, 0);
                Interlocked.Exchange(ref lastProcessScanTicks, ticks);
                return true;
            }
            if (processesChanged)
            {
                Interlocked.Exchange(ref processSetDirty, 1);
            }
            return false;
        }
        private int ProcessScanWaitMs()
        {
            long ticks = DateTime.UtcNow.Ticks;
            long processScanRetryAfterTicksSnapshot = Interlocked.Read(ref processScanRetryAfterTicks);
            if (processScanRetryAfterTicksSnapshot > ticks)
            {
                return TicksToWaitMilliseconds(processScanRetryAfterTicksSnapshot - ticks);
            }
            if (Interlocked.CompareExchange(ref urgentProcessScan, 0, 0) != 0)
            {
                return 1;
            }
            int processScanIntervalMsResult = ProcessScanIntervalMs(ProcessEventsAvailable, IsActive);
            if (armedAwaitingElection)
            {
                processScanIntervalMsResult = Math.Min(processScanIntervalMsResult, 4000);
            }
            long lastProcessScanTicksSnapshot = Interlocked.Read(ref lastProcessScanTicks);
            if (lastProcessScanTicksSnapshot <= 0 || ticks < lastProcessScanTicksSnapshot)
            {
                return 1;
            }
            long ratio = (ticks - lastProcessScanTicksSnapshot) / 10000;
            long remainingProcessScanIntervalMsResult = processScanIntervalMsResult - ratio;
            if (Interlocked.CompareExchange(ref transitionScanPending, 0, 0) != 0)
            {
                remainingProcessScanIntervalMsResult = Math.Min(remainingProcessScanIntervalMsResult, 5000 - ratio);
            }
            if (ProcessEventsAvailable && Interlocked.CompareExchange(ref processSetDirty, 0, 0) != 0)
            {
                remainingProcessScanIntervalMsResult = Math.Min(remainingProcessScanIntervalMsResult, 500 - ratio);
            }
            if (remainingProcessScanIntervalMsResult <= 0)
            {
                return 1;
            }
            return (int)Math.Min(processScanIntervalMsResult, remainingProcessScanIntervalMsResult);
        }
        private void RequeueProcessScanAfterFailure()
        {
            Interlocked.Exchange(ref urgentProcessScan, 1);
            Interlocked.Exchange(ref processSetDirty, 1);
            Interlocked.Exchange(ref processScanRetryAfterTicks, DateTime.UtcNow.AddMilliseconds(1000.0).Ticks);
        }
        private void RequestPolicyApply([CallerMemberName] string caller = null)
        {
            RevokeCriticalThreadIntent("RequestPolicyApply/" + caller);
            Interlocked.Exchange(ref corePolicyDirty, 1);
            Interlocked.Exchange(ref gameDetectionDirty, 1);
            Interlocked.Exchange(ref urgentProcessScan, 1);
            Interlocked.Exchange(ref processSetDirty, 1);
            try
            {
                kick.Set();
            }
            catch
            {
            }
        }
        private void BeginSessionPolicy()
        {
            powerSessionEnding = true;
            InvalidatePowerPlanSession();
            corePlacementStopReason = null;
            corePlacementOnly = false;
            lock (sync)
            {
                RememberModeSwitchLockLocked(activeDetection);
            }
            ResetSessionForeground();
            BeginRetrySession();
            BeginLaptopPerformanceSession();
            bool isolationClean = StopCoreIsolation();
            InvalidateCacheWarm();
            Interlocked.Increment(ref optionalServiceGeneration);
            InvalidateStandbyCleanerWork();
            GameProfile gameProfile;
            lock (sync)
            {
                gameProfile = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Clone() : null);
            }
            PolicySnapshot policySnapshot;
            lock (sync)
            {
                policySnapshot = sessionPolicy = ((gameProfile != null) ? PolicyResolver.For(gameProfile) : PolicyResolver.Global());
                CaptureSessionCorePreferencesLocked(gameProfile, policySnapshot);
            }
            SyncAutomationPolicy();
            BeginAutomationEvidence();
            BeginEnglishInputSession();
            if (!policySnapshot.IsGlobal)
            {
                Logger.Log(Lang.T("log.gamemode.34", 0) + policySnapshot.ProfileName + Lang.T("log.gamemode.35", 0) + policySnapshot.OverrideCount + Lang.T("log.gamemode.36", 0));
            }
            BeginAutoCores(policySnapshot, isolationClean);
            lock (sync)
            {
                lastPowerPolicyKey = -1;
                powerSessionEnding = false;
            }
        }
        private void ReportBegin(string game)
        {
            BeginCacheWarmSession();
            ResetBoostIdentity();
            long currentProcessCpuTicksResult = CurrentProcessCpuTicks();
            lock (sync)
            {
                repCpu.Clear();
                repCreation.Clear();
                repProc.Clear();
                repSealed.Clear();
                repGame = game;
                repStart = Stopwatch.GetTimestamp();
                repPaviseCpuStart = currentProcessCpuTicksResult;
                repProfileId = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Id : null);
            }
        }
        private void ReportUntrack(int pid)
        {
            lock (sync)
            {
                repCpu.Remove(pid);
                repCreation.Remove(pid);
                repProc.Remove(pid);
                repSealed.Remove(pid);
            }
        }
        private void ReportSeal(int pid)
        {
            long value;
            long value2;
            lock (sync)
            {
                if (!repCpu.TryGetValue(pid, out value))
                {
                    return;
                }
                repCreation.TryGetValue(pid, out value2);
                repCpu.Remove(pid);
                repCreation.Remove(pid);
            }
            long numericValue = 0L;
            long ticks;
            long creation;
            if (CpuTicks(pid, out ticks, out creation) && creation == value2 && ticks > value)
            {
                numericValue = ticks - value;
            }
            lock (sync)
            {
                long value3;
                repSealed.TryGetValue(pid, out value3);
                repSealed[pid] = value3 + numericValue;
            }
        }
        private void ReportTrack(int pid, string name)
        {
            lock (sync)
            {
                if (repGame == null || repCpu.ContainsKey(pid))
                {
                    return;
                }
            }
            long ticks;
            long creation;
            if (!CpuTicks(pid, out ticks, out creation))
            {
                return;
            }
            lock (sync)
            {
                if (!repCpu.ContainsKey(pid))
                {
                    repCpu[pid] = ticks;
                    repCreation[pid] = creation;
                    repProc[pid] = name;
                }
            }
        }
        private void ReportFinish()
        {
            string repGameSnapshot;
            long repStartSnapshot;
            Dictionary<int, long> dictionary;
            Dictionary<int, string> dictionary2;
            Dictionary<int, long> dictionary3;
            Dictionary<int, long> dictionary4;
            long repPaviseCpuStartSnapshot;
            lock (sync)
            {
                repGameSnapshot = repGame;
                repStartSnapshot = repStart;
                dictionary = new Dictionary<int, long>(repCpu);
                dictionary2 = new Dictionary<int, string>(repProc);
                dictionary3 = new Dictionary<int, long>(repCreation);
                dictionary4 = new Dictionary<int, long>(repSealed);
                repPaviseCpuStartSnapshot = repPaviseCpuStart;
                repCpu.Clear();
                repCreation.Clear();
                repProc.Clear();
                repSealed.Clear();
                repGame = null;
                repPaviseCpuStart = 0L;
                repProfileId = null;
            }
            if (repGameSnapshot == null)
            {
                return;
            }
            TimeSpan timeSpan = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - repStartSnapshot) / (double)Stopwatch.Frequency);
            foreach (KeyValuePair<int, long> item in dictionary)
            {
                long value;
                if (!dictionary4.TryGetValue(item.Key, out value))
                {
                    value = 0L;
                    dictionary4[item.Key] = 0L;
                }
                long ticks;
                long creation;
                long value2;
                if (CpuTicks(item.Key, out ticks, out creation) && dictionary3.TryGetValue(item.Key, out value2) && creation == value2)
                {
                    long remainingTicks = ticks - item.Value;
                    if (remainingTicks >= 0)
                    {
                        dictionary4[item.Key] = value + remainingTicks;
                    }
                }
            }
            long numericValue = 0L;
            long numericValue2 = 0L;
            string value3Snapshot = null;
            foreach (KeyValuePair<int, long> item2 in dictionary4)
            {
                numericValue += item2.Value;
                if (item2.Value > numericValue2)
                {
                    numericValue2 = item2.Value;
                    string value3;
                    if (dictionary2.TryGetValue(item2.Key, out value3))
                    {
                        value3Snapshot = value3;
                    }
                }
            }
            string repDoneText = Lang.F("rep.done", repGameSnapshot, FmtDur(timeSpan), dictionary4.Count, FmtCpu(numericValue));
            if (value3Snapshot != null && numericValue2 >= 10000000)
            {
                repDoneText += Lang.F("rep.top", value3Snapshot, FmtCpu(numericValue2));
            }
            long currentProcessCpuTicksResult = CurrentProcessCpuTicks();
            long cpuTicks = ((repPaviseCpuStartSnapshot > 0 && currentProcessCpuTicksResult >= repPaviseCpuStartSnapshot) ? (currentProcessCpuTicksResult - repPaviseCpuStartSnapshot) : 0);
            double averageCpuPercentResult = AverageCpuPercent(cpuTicks, timeSpan);
            repDoneText += Lang.F("rep.pavise.cpu", averageCpuPercentResult.ToString("0.00", CultureInfo.InvariantCulture));
            Logger.Log(Lang.T("log.gamemodesession.1", 0) + repDoneText);
            if (!(timeSpan.TotalSeconds >= 60.0))
            {
                return;
            }
            string repBriefText = Lang.F("rep.brief", repGameSnapshot, FmtDur(timeSpan), dictionary4.Count);
            try
            {
                Settings.SaveStr("LastSessionBrief", repBriefText);
            }
            catch
            {
            }
            Action<string> sessionBriefed = this.SessionBriefed;
            if (sessionBriefed != null)
            {
                try
                {
                    sessionBriefed(repBriefText);
                }
                catch
                {
                }
            }
            Action<string> sessionEnded = this.SessionEnded;
            if (sessionEnded == null)
            {
                return;
            }
            try
            {
                sessionEnded(repDoneText);
            }
            catch
            {
            }
        }
        internal static bool IsExitGraceChoice(int seconds)
        {
            if (seconds != 0 && seconds != 2 && seconds != 5 && seconds != 8 && seconds != 15)
            {
                return seconds == 30;
            }
            return true;
        }
        public bool TrySetExitGraceSeconds(int seconds)
        {
            if (!IsExitGraceChoice(seconds))
            {
                return false;
            }
            lock (sync)
            {
                if (stopping || !Settings.SaveStr("GameExitGraceSeconds", seconds.ToString(CultureInfo.InvariantCulture)))
                {
                    return false;
                }
                exitGraceSeconds = seconds;
            }
            KickGameDetectionNow();
            return true;
        }
        internal static bool ExitGraceElapsed(long since, long now, int seconds)
        {
            if (since > 0 && now >= since)
            {
                return now - since >= (long)seconds * 10000000L;
            }
            return false;
        }
        internal static bool IsTrustedPresetProcessPath(string name, string imagePath)
        {
            string imagePath2 = WhitelistRule.NormalizeImagePath(imagePath);
            if (imagePath2.Length == 0)
            {
                return false;
            }
            string folderPath2 = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(folderPath2))
            {
                folderPath2 = "C:\\Windows";
            }
            folderPath2 = folderPath2.TrimEnd('\\');
            if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase) && WhitelistRule.PathEquals(imagePath2, Path.Combine(folderPath2, "explorer.exe")))
            {
                return true;
            }
            if (FamilyBoundary.UnderRoot(imagePath2, Path.Combine(folderPath2, "System32")) || FamilyBoundary.UnderRoot(imagePath2, Path.Combine(folderPath2, "SysWOW64")) || FamilyBoundary.UnderRoot(imagePath2, Path.Combine(folderPath2, "SystemApps")))
            {
                return true;
            }
            if (string.Equals(name, "wslservice", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "vmmem", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "vmmemwsl", StringComparison.OrdinalIgnoreCase))
            {
                string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (string.IsNullOrEmpty(folderPath))
                {
                    return false;
                }
                if (FamilyBoundary.UnderRoot(imagePath2, Path.Combine(folderPath, "WindowsApps")))
                {
                    return true;
                }
                if (string.Equals(name, "wslservice", StringComparison.OrdinalIgnoreCase))
                {
                    return WhitelistRule.PathEquals(imagePath2, Path.Combine(folderPath, "WSL", "wslservice.exe"));
                }
                return false;
            }
            return false;
        }
        private static Dictionary<int, long> CaptureStoppedProcessIdentity(IEnumerable<int> pids)
        {
            Dictionary<int, long> dictionary = new Dictionary<int, long>();
            if (pids == null)
            {
                return dictionary;
            }
            foreach (int pid in pids)
            {
                IntPtr intPtr = Native.OpenProcess(4096, false, pid);
                if (intPtr == IntPtr.Zero)
                {
                    if (Native.LastOpenProcessFailureWasNoSuchProcess())
                    {
                        dictionary[pid] = 0L;
                    }
                    continue;
                }
                try
                {
                    long creation;
                    long cpu;
                    ulong io;
                    if (Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation > 0)
                    {
                        dictionary[pid] = creation;
                    }
                }
                finally
                {
                    Native.CloseHandle(intPtr);
                }
            }
            return dictionary;
        }
    }
}
