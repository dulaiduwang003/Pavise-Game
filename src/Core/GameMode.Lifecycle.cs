// @author bdth 2074055628@qq.com
// File purpose Worker thread start/stop, shutdown drain and the scan main loop
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        public void Start()
        {
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Start();
        }

        public bool Stop()
        {
            var elapsed = Stopwatch.StartNew();
            stopping = true;
            InvalidateCacheWarm();
            InvalidateStandbyCleanerWork();
            InvalidateEnglishInputWork();
            InvalidateIntelGraphicsWork();
            kick.Set();
            // A commit already in flight must finish under sync before shutdown can proceed
            // When a callback stops its own commit, the restore data must stay intact
            if (Monitor.IsEntered(sync) || !Monitor.TryEnter(sync, 8000)) return false;
            try
            {
                ClearFamilyDiscovery();
                InvalidateRendererHandoff();
            }
            finally { Monitor.Exit(sync); }
            Thread current = worker;
            if (current != null && (current == Thread.CurrentThread
                || !current.Join(RemainingShutdownMs(elapsed, 8000))))
                return false;
            // Thread-pool work may outlive Loop, close admission first, then wait for
            // the native and file changes that already passed their gates to finish
            if (!DrainAsyncShutdown(RemainingShutdownMs(elapsed, 8000))) return false;
            bool runnersClosed = true;
            try { if (!RenderLane.CloseForShutdown(8000)) runnersClosed = false; }
            catch { runnersClosed = false; }
            try { if (!PowerBudgetYieldRunner.CloseForShutdown(8000)) runnersClosed = false; }
            catch { runnersClosed = false; }
            try { if (!VramSpillProbe.CloseForShutdown(8000)) runnersClosed = false; }
            catch { runnersClosed = false; }
            try { if (standbyCleaner != null && !standbyCleaner.Close(8000)) runnersClosed = false; }
            catch { runnersClosed = false; }
            // Try both shutdowns even if one fails, unless both confirm exit
            // their restore records and change boundaries must be kept as-is
            if (!runnersClosed) return false;
            // While Loop is still alive, do not remove the change guard or claim a reset is safe
            bool clean = true;
            try { if (!StopCoreIsolation()) clean = false; } catch { clean = false; }
            RenderLane.ConfigureMutationBoundary(null, null);
            PowerBudgetYieldRunner.ConfigureMutationBoundary(null, null);
            VramShield.ConfigureMutationBoundary(null, null);
            SelfYield.ConfigureMutationBoundary(null, null);
            core.ConfigureMutationBoundary(null, null);
            IrqMutationBoundary.Configure(null, null);
            try { irqProbe.Dispose(); } catch { clean = false; }
            try { if (!RestoreAllIrqProofHardPins()) clean = false; } catch { clean = false; }
            // No concurrent Loop after worker.Join, teardown closes any still-open present session cleanly, no leak
            try { PresentProbe p = presentProbe; presentProbe = null; if (p != null) p.Stop(); } catch { clean = false; }
            return clean;
        }

        private bool DrainAsyncShutdown(int timeoutMs)
        {
            if (!stopping || timeoutMs < 0) return false;
            var elapsed = Stopwatch.StartNew();
            if (cacheWarm != null && !cacheWarm.Drain(RemainingShutdownMs(elapsed, timeoutMs))) return false;
            if (!DrainShutdownGate(autoGpuCommitGate, RemainingShutdownMs(elapsed, timeoutMs))) return false;
            if (!DrainShutdownGate(driverStageGate, RemainingShutdownMs(elapsed, timeoutMs))) return false;
            if (!DrainShutdownGate(powerApplyGate, RemainingShutdownMs(elapsed, timeoutMs))) return false;
            if (!DrainShutdownGate(whiteEvalSync, RemainingShutdownMs(elapsed, timeoutMs))) return false;
            if (!DrainEnglishInput(RemainingShutdownMs(elapsed, timeoutMs))) return false;
            if (!DrainIntelGraphics(RemainingShutdownMs(elapsed, timeoutMs))) return false;
            if (standbyCleaner != null && !standbyCleaner.Close(RemainingShutdownMs(elapsed, timeoutMs))) return false;
            RendererObservationStore store = rendererObservations;
            return store == null || store.Close(RemainingShutdownMs(elapsed, timeoutMs));
        }

        private static int RemainingShutdownMs(Stopwatch elapsed, int timeoutMs)
        {
            return (int)Math.Max(0L, (long)timeoutMs - elapsed.ElapsedMilliseconds);
        }

        private static bool DrainShutdownGate(object gate, int timeoutMs)
        {
            // A stop issued from inside a change can neither wait on itself
            // nor claim its still-running callback has finished
            if (Monitor.IsEntered(gate) || !Monitor.TryEnter(gate, timeoutMs)) return false;
            try { return true; }
            finally { Monitor.Exit(gate); }
        }

        public void Poke() { RequestPolicyApply(); }

        private void Loop()
        {
            while (!stopping)
            {
                try
                {
                    if (panicReq)
                    {

                        int serving = Volatile.Read(ref panicSeq);
                        panicReq = false;
                        panicResult = Deactivate(Lang.T("t.gamemode.42"));
                        Volatile.Write(ref panicServed, serving);
                        panicDone.Set();
                        kick.WaitOne(4000);
                        continue;
                    }
                    TryDomainSwap();
                    if (!enabled || ProfileStoreSaveFailed)
                    {
                        InvalidateRendererHandoff();
                        bool residue;
                        lock (sync) residue = active || gameBoost.Count > 0;
                        if (residue || EnvActive() || core.AnyWith(SuppressReason.Background))
                            RetryDeactivate(Lang.T("t.gamemode.43"));
                    }
                    else
                    {
                        bool fallbackOnly;
                        if (!ShouldRunProcessScan(out fallbackOnly))
                        {
                            kick.WaitOne(ProcessScanWaitMs());
                            continue;
                        }
                        ProcessSnapshot all = null;
                        CountProcessScan();
                        // Pure fallback rounds during a match reuse a recent snapshot, process-set changes force a fresh capture via the event-driven dirty flag
                        //   outside a match and in polling mode nothing changes, see ProcessSnapshotSource.ReuseMaxAgeMs
                        int snapshotReuseMs = fallbackOnly && ProcessEventsAvailable && IsActive
                            ? ProcessSnapshotSource.ReuseMaxAgeMs : 0;
                        try { all = ProcessSnapshotSource.Capture(selfSession, snapshotReuseMs); }
                        catch { all = null; }
                        if (all == null) RequeueProcessScanAfterFailure();
                        else
                        {
                                PruneWhitelistFamilyMembersIfDue(all);
                                HashSet<int> gamePids;
                                string running = FindRunningGame(all, out gamePids);
                                try { StepRogueWatch(all, gamePids); } catch { }
                                // Confirmed learning can trip the existing game library save circuit breaker, until the UI cleanup has run
                                // this round must not apply power, suppress background or boost the old/new target either
                                if (!enabled || stopping || panicReq || ProfileStoreSaveFailed) continue;
                                if (running != null)
                                {
                                    string runningProfileId;
                                    lock (sync)
                                        runningProfileId = activeDetection != null && activeDetection.Profile != null
                                            ? activeDetection.Profile.Id : null;
                                    if (gameGoneSinceTicks != 0)
                                    {
                                        gameGoneSinceTicks = 0;
                                        if (active)
                                        {
                                            Logger.Log(Lang.T("log.gamemode.44"));
                                            bool sameGraceProfile;
                                            string graceGame;
                                            lock (sync)
                                            {
                                                firstSweep = true;
                                                sameGraceProfile = SameReportedProfile(
                                                    repProfileId, runningProfileId);
                                                graceGame = repGame;
                                            }
                                            if (sameGraceProfile)
                                            {
                                                // The sealed prefix is not on disk yet, when the same profile recovers
                                                // within the grace period drop it and reopen a clean epoch from the new
                                                // renderer proof, otherwise one brief missed detection splits a single match into two records
                                                ArmIrqObservation(graceGame ?? running);
                                            }
                                        }
                                    }
                                    if (!active)
                                    {
                                        lock (sync) { active = true; activeGame = running; firstSweep = true; }
                                        Logger.Log(Lang.T("log.gamemode.45") + running);
                                        autoGpuScanned = false;
                                        ResetAdaptiveGuard();
                                        Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
                                        SetAutoGpuSessionStamp(DateTime.UtcNow.Ticks);
                                        try { cpuLimit.Start(); } catch { }
                                        BeginSessionPolicy();
                                        ReportBegin(running);
                                        NotifyExtensionSession(true);
                                        slowEnvAtTicks = DateTime.UtcNow
                                            .AddSeconds(SlowEnvDelaySeconds).Ticks;
                                    }
                                    else if (!SameReportedProfile(repProfileId, runningProfileId))
                                    {
                                        lock (sync) activeGame = running;
                                        Logger.Log(Lang.T("log.gamemode.46") + running);
                                        autoGpuScanned = false;
                                        ResetAdaptiveGuard();
                                        Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
                                        SetAutoGpuSessionStamp(DateTime.UtcNow.Ticks);
                                        // activeDetection already points at the new profile here, the old renderer can no longer be final-checked
                                        // invalidate the old IRQ epoch outright, and the old match must be closed before the new policy is enabled
                                        // A direct A-to-B switch must invalidate A's live epoch, but if A was already
                                        // sealed on first loss of contact, its prefix has a complete end boundary and must be committed
                                        // by the ReportFinish that follows, not wiped by Invalidate
                                        if (!irqProbe.HasSealedPending)
                                            irqProbe.InvalidateGameMask();
                                        ReportFinish();
                                        BeginSessionPolicy();
                                        ReportBegin(running);
                                        NotifyExtensionSession(true);
                                    }
                                    else if (!string.Equals(activeGame, running, StringComparison.Ordinal))
                                    {
                                        // A mid-match rename on the same profile only updates the display, must not fake a match switch
                                        lock (sync) activeGame = running;
                                    }
                                    StepEnglishInputSession();
                                    ApplyEnv();
                                    ApplyIntelGraphicsPolicy();
                                    ApplyStandbyCleanerPolicy();
                                    ApplyCacheWarmPolicy();
                                    string rendererPath;
                                    int rendererPid;
                                    long rendererCreation;
                                    lock (sync)
                                    {
                                        rendererPath = activeDetection != null
                                            ? activeDetection.RendererPath : null;
                                        rendererPid = activeDetection != null
                                            ? activeDetection.RendererPid : 0;
                                        rendererCreation = activeDetection != null
                                            ? activeDetection.RendererCreation : 0;
                                    }
                                    GpuThrottleProbe.SampleIfDue(rendererPath);
                                    // VRAM spill is still measured over the whole family, that is observation not policy, a multi-process game's VRAM has to be summed
                                    VramSpillProbe.SampleIfDue(gamePids);
                                    // The shield only targets the renderer process itself, the reservation is declared per process, attaching it to other family members is pointless
                                    VramShield.SampleIfDue(EffVramShield, rendererPid, rendererCreation);
                                    // Suppression by default only recognizes the renderer process itself, the rest of the family is treated as ordinary background
                                    //   the whole family is let through only once the family exemption switch on the game library page is on, the family set in Sweep
                                    //   is also recomputed from this round's snapshot parent-child relations so child processes do not flip between suppressed and released with each detection cycle
                                    if (EffSuppress) Sweep(all, gamePids);
                                    if (!EffSuppress) ReleaseBackground();
                                    SelfYield.Engage();
                                    // Power slider only for the Esports tier, the Handheld tier leaves it alone, the PL there belongs to the vendor tool and pushing the slider would only fight it
                                    MaybeActivatePowerOverlay(EffPreset == PerformancePreset.Competitive);
                                    // Power yield still applies on the Handheld tier, the direction is right anyway, handheld CPU and iGPU compete for the same budget
                                    //   what the Handheld tier relaxes are the pure power-saving items, EPP still gets the Esports tier's aggressive value, so the premise for yielding still holds
                                    Func<bool> powerYieldAdmission = CapturePowerYieldAdmission(rendererPid, rendererCreation);
                                    PowerBudgetYieldRunner.Start(powerYieldAdmission(),
                                        EffPreset == PerformancePreset.Competitive
                                            || EffPreset == PerformancePreset.Handheld,
                                        rendererPid, rendererCreation,
                                        powerYieldAdmission);
                                    ApplyIrqObservationSettingChange(running);
                                    if (EffBoost) Boost(all);
                                    else
                                    {
                                        if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                                        UnboostGames();
                                    }
                                    ObserveSystemIrq(rendererPid, rendererCreation);
                                    UpdateIrqPresentProbe();
                                    MaybeAutoEnrollBackgroundGpu(rendererPid);
                                    StepAdaptiveGuard();
                                    NotifyIrqObservationChanged(false);
                                }
                                else if (active)
                                {
                                    long nowTicks = DateTime.UtcNow.Ticks;
                                    if (gameGoneSinceTicks == 0)
                                    {
                                        gameGoneSinceTicks = nowTicks;
                                        InvalidateStandbyCleanerWork();
                                        InvalidateCacheWarm();
                                        InvalidateEnglishInputWork();
                                        InvalidateIntelGraphicsWork();
                                        // The exit grace period only guards against game-detection jitter, it is not part of the verifiable match sampling window
                                        // On first loss of contact, immediately seal the epoch matching the latest core placement proof
                                        try { SealIrqObservation(); }
                                        catch { irqProbe.InvalidateGameMask(); }
                                        Logger.Log(Lang.T("log.gamemode.47")
                                            + ExitGraceSeconds + Lang.T("log.gamemode.48"));
                                        gracePreReleased += ReleaseBackground(Lang.T("t.gamemode.49"));
                                    }
                                    else if (nowTicks - gameGoneSinceTicks
                                        >= ExitGraceSeconds * TimeSpan.TicksPerSecond)
                                    {
                                        Deactivate(Lang.T("t.gamemode.50"));
                                    }
                                    if (gameGoneSinceTicks != 0)
                                        Interlocked.Exchange(ref transitionScanPending, 1);
                                }
                                else
                                {
                                    DropVanishedBoosts();
                                    bool boostResidue;
                                    lock (sync) boostResidue = gameBoost.Count > 0;
                                    if (boostResidue || EnvActive() || core.AnyWith(SuppressReason.Background))
                                        RetryDeactivate(Lang.T("t.gamemode.51"));
                                    TryAutoAddForegroundGame();
                                }
                        }
                    }
                }
                catch (Exception ex) { Logger.Log(Lang.T("log.gamemode.52") + ex.Message); }
                kick.WaitOne(enabled
                    ? ProcessScanWaitMs() : PollingSweepIntervalMs);
            }
            bool exitResidue;
            lock (sync) exitResidue = active || gameBoost.Count > 0;
            bool exitClean = true;
            if (exitResidue || core.AnyWith(SuppressReason.Background) || EnvActive())
                exitClean = Deactivate(Lang.T("t.gamemode.53"));
            if (panicReq)
            {
                int servingAtExit = Volatile.Read(ref panicSeq);
                panicReq = false;
                panicResult = exitClean;
                Volatile.Write(ref panicServed, servingAtExit);
                panicDone.Set();
            }
            try { GameDvr.Restore(); } catch { }
            try { Mmcss.Restore(); } catch { }
        }
    }
}
