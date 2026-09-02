// @author bdth 2074055628@qq.com
// 文件用途 工作线程启停 关闭排空与扫描主循环
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
            InvalidateStandbyCleanerWork();
            InvalidateEnglishInputWork();
            InvalidateIntelGraphicsWork();
            kick.Set();
            // 已经在跑的提交要在 sync 下走完 之后关闭才能继续
            // 回调停掉它自己那次提交时 必须让恢复数据完好无损
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
            // 线程池的活可能比 Loop 活得久 先停准入 再等那些
            // 已经进了各自闸门的原生和文件改动做完
            if (!DrainAsyncShutdown(RemainingShutdownMs(elapsed, 8000))) return false;
            bool runnersClosed = true;
            try { if (!RenderLane.CloseForShutdown(8000)) runnersClosed = false; }
            catch { runnersClosed = false; }
            try { if (!PowerBudgetYieldRunner.CloseForShutdown(8000)) runnersClosed = false; }
            catch { runnersClosed = false; }
            try { if (standbyCleaner != null && !standbyCleaner.Close(8000)) runnersClosed = false; }
            catch { runnersClosed = false; }
            // 两个关闭都要试 就算其中一个失败 除非两边都确认退出
            // 否则它们的恢复记录和改动边界都要原样保留
            if (!runnersClosed) return false;
            // Loop 还活着的时候 不要卸掉改动守卫 也别声称重置是安全的
            bool clean = true;
            RenderLane.ConfigureMutationBoundary(null, null);
            PowerBudgetYieldRunner.ConfigureMutationBoundary(null, null);
            VramShield.ConfigureMutationBoundary(null, null);
            SelfYield.ConfigureMutationBoundary(null, null);
            core.ConfigureMutationBoundary(null, null);
            IrqMutationBoundary.Configure(null, null);
            try { irqProbe.Dispose(); } catch { clean = false; }
            try { if (!RestoreAllIrqProofHardPins()) clean = false; } catch { clean = false; }
            // worker.Join 之后没有并发 Loop 收尾把可能仍开着的 present 会话关干净不泄漏
            try { PresentProbe p = presentProbe; presentProbe = null; if (p != null) p.Stop(); } catch { clean = false; }
            return clean;
        }

        private bool DrainAsyncShutdown(int timeoutMs)
        {
            if (!stopping || timeoutMs < 0) return false;
            var elapsed = Stopwatch.StartNew();
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
            // 从一次改动内部发起的停止 既不能等自己
            // 也不能声称它那个还在跑的回调已经结束
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
                        // 对局中的纯兜底轮次复用近期快照 进程集变动由事件走 dirty 强制重拍
                        //   对局外和轮询模式一个字节不变 见 ProcessSnapshotSource.ReuseMaxAgeMs
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
                                // 确认学习可触发既有游戏库保存熔断 UI 收尾尚未执行前
                                // 本轮也不能继续套电源 压后台或提优旧/新目标
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
                                                // Seal 的前缀尚未落盘 同一 profile 在宽限内
                                                // 恢复时丢弃它 并从新 renderer 证明重开干净
                                                // epoch 否则一次短暂漏检会把同一局拆成两条
                                                ArmIrqObservation(graceGame ?? running);
                                            }
                                        }
                                    }
                                    if (!active)
                                    {
                                        lock (sync) { active = true; activeGame = running; firstSweep = true; }
                                        Logger.Log(Lang.T("log.gamemode.45") + running);
                                        autoGpuScanned = false;
                                        cacheWarmDone = false;
                                        ResetAdaptiveGuard();
                                        Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
                                        Interlocked.Exchange(ref sessionStartTicks, DateTime.UtcNow.Ticks);
                                        try { cpuLimit.Start(); } catch { }
                                        BeginSessionPolicy();
                                        ReportBegin(running);
                                        slowEnvAtTicks = DateTime.UtcNow
                                            .AddSeconds(SlowEnvDelaySeconds).Ticks;
                                    }
                                    else if (!SameReportedProfile(repProfileId, runningProfileId))
                                    {
                                        lock (sync) activeGame = running;
                                        Logger.Log(Lang.T("log.gamemode.46") + running);
                                        autoGpuScanned = false;
                                        cacheWarmDone = false;
                                        ResetAdaptiveGuard();
                                        Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
                                        Interlocked.Exchange(ref sessionStartTicks, DateTime.UtcNow.Ticks);
                                        // activeDetection 此时已经指向新 profile 旧 renderer 无法再终验
                                        // 直接作废旧 IRQ epoch 并且必须先结旧局 再启用新策略
                                        // 直接 A→B 时必须作废 A 的 live epoch 但 A 已在首次
                                        // 失联时 Seal 的前缀已有完整结束边界 应由紧接着的
                                        // ReportFinish 提交 不能再被 Invalidate 清掉
                                        if (!irqProbe.HasSealedPending)
                                            irqProbe.InvalidateGameMask();
                                        ReportFinish();
                                        BeginSessionPolicy();
                                        ReportBegin(running);
                                    }
                                    else if (!string.Equals(activeGame, running, StringComparison.Ordinal))
                                    {
                                        // 同一 profile 局内改名只更新展示 不能伪造一次换局
                                        lock (sync) activeGame = running;
                                    }
                                    StepEnglishInputSession();
                                    ApplyEnv();
                                    ApplyIntelGraphicsPolicy();
                                    ApplyStandbyCleanerPolicy();
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
                                    // 显存溢出仍按整个家族测量 那是观测不是策略 多进程游戏的显存要合起来看
                                    VramSpillProbe.SampleIfDue(gamePids);
                                    // 护盾只认渲染进程本体 预留是按进程声明的 给家族其它成员挂没有意义
                                    VramShield.SampleIfDue(EffVramShield, rendererPid, rendererCreation);
                                    // 压制默认只认渲染进程本体 家族其余成员当普通后台
                                    //   游戏库页的家族豁免开关打开后才整族放行 家族集合在 Sweep 里
                                    //   还会拿本轮快照的父子关系补算一遍 免得子进程随检测周期忽压忽放
                                    if (EffSuppress) Sweep(all, gamePids);
                                    if (!EffSuppress) ReleaseBackground();
                                    SelfYield.Engage();
                                    // 电源滑块只认专注 掌机档不传真 那块的 PL 归厂商工具管 拨过去只会跟它顶
                                    MaybeActivatePowerOverlay(EffPreset == PerformancePreset.Competitive
                                        || EffPreset == PerformancePreset.Extreme);
                                    // 功耗让路掌机档照样参与 方向本来就对 掌机 CPU 和集显抢的就是同一份预算
                                    //   掌机档放开的是纯省电项 EPP 仍写专注档的激进值 让路的前提还在
                                    PowerBudgetYieldRunner.Start(EffPowerYield,
                                        EffPreset == PerformancePreset.Competitive
                                            || EffPreset == PerformancePreset.Extreme
                                            || EffPreset == PerformancePreset.Handheld);
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
                                    MaybeWarmCache();
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
                                        InvalidateEnglishInputWork();
                                        InvalidateIntelGraphicsWork();
                                        // 退出宽限只用于避免游戏检测抖动 不属于可验证的对局采样窗
                                        // 首次失联立即封存最近一次落核证明对应的 epoch
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
