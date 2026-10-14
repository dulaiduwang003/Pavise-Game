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
        private volatile bool shutdownWritersStopped;
        internal bool ShutdownWritersStopped { get { return shutdownWritersStopped; } }

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
            var elapsed = Stopwatch.StartNew();
            stopping = true;
            SyncPerformanceAutomation();
            backgroundFreeze.Clear("application-stop");
            InvalidateCacheWarm();
            InvalidateStandbyCleanerWork();
            InvalidateEnglishInputWork();
            kick.Set();
            // 运行中的提交必须结束后才能删除恢复数据。
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
            // 先禁止新任务，再等待已获准写入结束。
            if (!DrainAsyncShutdown(RemainingShutdownMs(elapsed, 8000))) return false;
            bool runnersClosed = true;
            try { if (!FrameCoordination.Close(8000)) runnersClosed = false; }
            catch { runnersClosed = false; }
            try { if (!RenderLane.CloseForShutdown(8000)) runnersClosed = false; }
            catch { runnersClosed = false; }
            try { if (standbyCleaner != null && !standbyCleaner.Close(8000)) runnersClosed = false; }
            catch { runnersClosed = false; }
            // 两条关闭路径都尝试，未确认退出时保留恢复记录。
            if (!runnersClosed) return false;
            if (!performanceActions.Close(8000)) return false;
            return FinishShutdownRecovery(core.RestoreAutomaticLayouts,
                () => backgroundFreeze.Clear("application-stop"), backgroundCpuCap.Stop,
                StopCoreIsolation, RestoreAllHardPins, RestoreGuardScheduling);
        }

        internal bool FinishShutdownRecovery(Func<bool> restoreLayouts, Func<bool> stopFreeze,
            Func<bool> stopCpuCap, Func<bool> stopIsolation, Func<bool> restorePins, Func<bool> restoreGuard)
        {
            shutdownWritersStopped = false;
            bool clean = true, writersStopped = true;
            ShutdownRecoveryStep(restoreLayouts, false, ref clean, ref writersStopped);
            ShutdownRecoveryStep(stopFreeze, true, ref clean, ref writersStopped);
            ShutdownRecoveryStep(stopCpuCap, true, ref clean, ref writersStopped);
            ShutdownRecoveryStep(stopIsolation, true, ref clean, ref writersStopped);
            ShutdownRecoveryStep(restorePins, false, ref clean, ref writersStopped);
            ShutdownRecoveryStep(restoreGuard, false, ref clean, ref writersStopped);
            shutdownWritersStopped = writersStopped;
            return clean;
        }

        private static void ShutdownRecoveryStep(Func<bool> work, bool requiresWriterExit,
            ref bool clean, ref bool writersStopped)
        {
            try
            {
                if (work != null && work()) return;
            }
            catch { }
            clean = false;
            if (requiresWriterExit) writersStopped = false;
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
            // 回调不能等待自身结束或声称自身已排空。
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
                    ApplyEnvironmentTimerOptions();
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
                    SyncPerformanceAutomation();
                    SyncAutomaticFullscreenOptimizations();
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
                                // 游戏库保存熔断后，本轮禁止继续执行调度和压制。
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
                                            lock (sync) firstSweep = true;
                                        }
                                    }
                                    if (!active)
                                    {
                                        lock (sync) { active = true; activeGame = running; firstSweep = true; }
                                        PerformanceMonitoring.WakeForGameStart();
                                        Logger.Log(Lang.T("log.gamemode.45") + running);
                                        autoGpuScanned = false;
                                        ResetAdaptiveGuard();
                                        Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
                                        SetAutoGpuSessionStamp(DateTime.UtcNow.Ticks);
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
                                        ReportFinish();
                                        BeginSessionPolicy();
                                        ReportBegin(running);
                                        NotifyExtensionSession(true);
                                    }
                                    else if (!string.Equals(activeGame, running, StringComparison.Ordinal))
                                    {
                                        lock (sync) activeGame = running;
                                    }
                                    StepEnglishInputSession();
                                    ApplyEnv();
                                    SyncPerformanceAutomation();
                                    ApplyStandbyCleanerPolicy();
                                    ApplyCacheWarmPolicy();
                                    int rendererPid;
                                    long rendererCreation;
                                    lock (sync)
                                    {
                                        rendererPid = activeDetection != null
                                            ? activeDetection.RendererPid : 0;
                                        rendererCreation = activeDetection != null
                                            ? activeDetection.RendererCreation : 0;
                                    }
                                    RefreshAutoCoreMode();
                                    if (EffSuppress) Sweep(all, gamePids);
                                    if (!EffSuppress) ReleaseBackground();
                                    SelfYield.TrackHelperRenderer(rendererPid, rendererCreation);
                                    SelfYield.Engage();
                                    MaybeActivatePowerOverlay(PresetValue.IsCompetitive(EffPreset));
                                    if (EffBoost) Boost(all);
                                    else UnboostGames();
                                    MaybeAutoEnrollBackgroundGpu(rendererPid);
                                    StepAdaptiveGuard();
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
                                        Logger.Log(Lang.T("log.gamemode.47")
                                            + ExitGraceSeconds + Lang.T("log.gamemode.48"));
                                        gracePreReleased += ReleaseBackground(Lang.T("t.gamemode.49"));
                                    }
                                    if (ExitGraceElapsed(gameGoneSinceTicks, nowTicks, ExitGraceSeconds))
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
                    Logger.RepeatedRecovered("GameMode");
                }
                catch (Exception ex) { backgroundFreeze.Clear("scan-exception"); backgroundCpuCap.Clear(); Logger.RepeatedError("GameMode", Lang.T("log.gamemode.52") + ex.GetType().Name + ": " + ex.Message); }
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
            try { RestoreGuardScheduling(); } catch { }
        }
    }
}
