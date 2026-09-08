// @author bdth 2074055628@qq.com
// 文件用途 退优 恐慌还原与对局停用
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool UnboostGames()
        {
            return UnboostGames(0, 0, null);
        }

        private static bool IsKeptBoost(
            KeyValuePair<int, Snap> boosted, int keepPid, long keepCreation, string keepName)
        {
            return keepPid > 0
                && boosted.Key == keepPid
                && boosted.Value.Creation == keepCreation
                && string.Equals(boosted.Value.Name, keepName, StringComparison.OrdinalIgnoreCase);
        }

        private bool UnboostGames(int keepPid, long keepCreation, string keepName)
        {
            bool isolationClean = keepPid > 0 || StopCoreIsolation();
            List<KeyValuePair<int, Snap>> boosts;
            Dictionary<int, int> gpus;
            lock (sync)
            {
                if (gameBoost.Count == 0 && gameGpu.Count == 0) return isolationClean;
                boosts = new List<KeyValuePair<int, Snap>>();
                foreach (KeyValuePair<int, Snap> boosted in gameBoost)
                    if (!IsKeptBoost(boosted, keepPid, keepCreation, keepName))
                        boosts.Add(boosted);
                gpus = new Dictionary<int, int>();
                foreach (KeyValuePair<int, int> gpu in gameGpu)
                    if (keepPid <= 0 || gpu.Key != keepPid)
                        gpus[gpu.Key] = gpu.Value;
                if (boosts.Count == 0 && gpus.Count == 0) return isolationClean;
                if (keepPid <= 0)
                {
                    boostFail.Clear(); boostDenied.Clear(); boostStateWarned.Clear();
                    boostStateVerified.Clear(); gameBoostNextAudit.Clear();
                    tweakApplied.Clear(); boostHandleStripped.Clear(); boostEcoGaveUp.Clear();
                    placementFail.Clear(); placementGaveUp.Clear(); boostStateFail.Clear();
                }
                else
                    foreach (KeyValuePair<int, Snap> stale in boosts)
                    {
                        boostFail.Remove(stale.Key); boostDenied.Remove(stale.Key);
                        boostStateWarned.Remove(stale.Key); boostStateVerified.Remove(stale.Key);
                        gameBoostNextAudit.Remove(stale.Key); tweakApplied.Remove(stale.Key);
                        boostHandleStripped.Remove(stale.Key); boostEcoGaveUp.Remove(stale.Key);
                        placementFail.Remove(stale.Key); placementGaveUp.Remove(stale.Key);
                        boostStateFail.Remove(stale.Key);
                    }
            }
            foreach (var kv in boosts)
                if (RenderLane.IsActiveFor(kv.Key, kv.Value.Creation)) RenderLane.Release();
            // 新 OpenProcess 可能已被反作弊剥权 优先使用首次 hard pin 前
            // 留下的 pid+creation 绑定句柄恢复 affinity 并精确读回 失败时
            // 保留句柄 后面的普通恢复仍可尝试 不能提前丢失唯一恢复能力
            foreach (var kv in boosts)
                RestoreIrqProofHardPin(IntPtr.Zero, kv.Key, true);
            foreach (var kv in boosts)
            {
                int pid = kv.Key;
                bool done;
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                {
                    IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (hq == IntPtr.Zero)
                    {
                        done = Native.LastOpenProcessFailureWasNoSuchProcess();
                    }
                    else
                    {
                        try
                        {
                            string name = Native.ImageName(hq);
                            long creation, cpu; ulong disk;
                            bool sampled = Native.QueryProcessSample(
                                hq, out creation, out cpu, out disk);
                            bool identityKnown = name != null && sampled;
                            bool same = identityKnown
                                && string.Equals(
                                    name, kv.Value.Name,
                                    StringComparison.OrdinalIgnoreCase)
                                && creation == kv.Value.Creation;
                            done = identityKnown && !same;
                            if (same) Logger.Log(Lang.T("log.gamemodeboost.38") + kv.Value.Name + " pid " + pid + Lang.T("log.gamemodeboost.39"));
                        }
                        finally { Native.CloseHandle(hq); }
                    }
                }
                else
                {
                    try
                    {
                        string cur = Native.ImageName(h);
                        long creation, cpu; ulong disk;
                        bool sampled = Native.QueryProcessSample(
                            h, out creation, out cpu, out disk);
                        bool identityKnown = cur != null && sampled;
                        bool identity = identityKnown
                            && string.Equals(
                                cur, kv.Value.Name,
                                StringComparison.OrdinalIgnoreCase)
                            && creation == kv.Value.Creation;
                        if (!identityKnown)
                        {
                            // 句柄开得出来但身份读不到 多半是进程已退出只剩对象 没有东西可还原
                            done = !Native.StillActive(h);
                        }
                        else if (identity)
                        {
                            done = SuppressionCore.RestoreValues(h, kv.Value.Pri, kv.Value.Aff, kv.Value.Io,
                                kv.Value.Pg, allMask, kv.Value.CpuSets, kv.Value.QoSControl, kv.Value.QoSState);
                            int gpuOld;
                            if (done && gpus.TryGetValue(pid, out gpuOld))
                                done = Native.D3DKMTSetProcessSchedulingPriorityClass(h, gpuOld) == 0;
                        }
                        else done = true;
                    }
                    finally { Native.CloseHandle(h); }
                }
                if (done)
                {
                    if (coreIsolation != null && !coreIsolation.Drop(pid, kv.Value.Creation)) isolationClean = false;
                    // RestoreValues 已精确还原该 identity 的 affinity 关闭 retained
                    // handle 之前再移除其绑定 避免退出路径泄漏
                    ForgetIrqProofHardPin(pid);
                    CrashGuard.ReleaseBoostProcess(pid, kv.Value.Creation);
                    lock (sync)
                    {
                        gameBoost.Remove(pid); gameGpu.Remove(pid);
                        gamePlacement.Remove(pid); gamePlacementStrict.Remove(pid);
                        gameBoostNextAudit.Remove(pid);
                    }
                }
            }
            lock (sync) return isolationClean && gameBoost.Count == 0;
        }

        private bool partitionHintLogged;

        private void WarnIfPartitionHurtsWideGame(string name, ProcessSnapshot all, int pid, ulong mask)
        {
            if (partitionHintLogged || all == null) return;
            ProcEntry entry = all.Find(pid);
            if (entry == null) return;
            int given = CpuTopology.CountSetBits(mask);
            int total = CpuTopology.CountSetBits(CpuTopology.AllMask);
            if (!PartitionLikelyHurts(entry.Threads, given, total)) return;
            partitionHintLogged = true;
            Logger.Log(Lang.T("log.gamemodeboost.40") + (name ?? "?") + Lang.T("log.gamemodeboost.41") + entry.Threads
                + Lang.T("log.gamemodeboost.42") + given + Lang.T("log.gamemodeboost.43") + total
                + Lang.T("log.gamemodeboost.44") + (100 - given * 100 / total)
                + Lang.T("log.gamemodeboost.45"));
        }

        public bool PanicRestore()
        {
            // 还原可能先做很慢的注册表和原生操作 才把请求投给 Loop
            // 这整段时间内都要挡住新的清理
            Interlocked.Increment(ref standbyCleanerRestorePending);
            InvalidateCacheWarm();
            InvalidateStandbyCleanerWork();
            InvalidateEnglishInputWork();
            BeginIntelGraphicsRestore();
            try { return PanicRestoreCore(); }
            finally
            {
                EndIntelGraphicsRestore();
                Interlocked.Decrement(ref standbyCleanerRestorePending);
            }
        }

        private bool PanicRestoreCore()
        {
            int cleared = SelfProtectedRoster.Clear();
            if (cleared > 0)
                Logger.Log(Lang.T("log.gamemodeboost.46") + cleared + Lang.T("log.gamemodeboost.47"));
            // 已下架的 IFEO/CFG 历史残留也归紧急恢复管 写注册表前须停 IRQ capture
            bool legacyOk = true;
            if (IfeoBoost.HasResidue())
                legacyOk &= IrqMutationBoundary.Run<bool>(IfeoBoost.RestoreAll);
            if (CfgOffTweak.HasResidue())
                legacyOk &= IrqMutationBoundary.Run<bool>(CfgOffTweak.RestoreAll);
            int fusesCleared;
            lock (sync)
            {
                // 清掉的缓存没法还原 重置别的环境熔断时
                // 不能让一个失败的清理器或者损坏的开启状态复活
                bool keepStandbyFuse = envFused.Contains("standby");
                fusesCleared = envFused.Count - (keepStandbyFuse ? 1 : 0);
                envFused.Clear();
                if (keepStandbyFuse) envFused.Add("standby");
            }
            foreach (string envKey in EnvKeys)
                if (envKey != "standby" && Settings.Load("EnvFuse_" + envKey, false))
                    Settings.Save("EnvFuse_" + envKey, false);
            SaveCounter(PowerFailStreakKey, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPState, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPreRender, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyAnsel, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyRebarFeat, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyDlssOvr, 0);
            if (fusesCleared > 0)
                Logger.Warn(Lang.T("log.gamemodeboost.50") + fusesCleared + Lang.T("log.gamemodeboost.51"));
            lock (panicCallGate)
            {
                int mine = Interlocked.Increment(ref panicSeq);
                panicDone.Reset();
                panicResult = false;
                lock (sync)
                {
                    panicReq = true;
                    InvalidateStandbyCleanerWork();
                    InvalidateEnglishInputWork();
                    InvalidateIntelGraphicsWork();
                    InvalidateRendererHandoff();
                }
                kick.Set();

                long deadline = DateTime.UtcNow.Ticks + 12000L * TimeSpan.TicksPerMillisecond;
                while (true)
                {
                    long left = (deadline - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
                    if (left <= 0) return false;
                    if (!panicDone.WaitOne((int)left)) return false;
                    if (Volatile.Read(ref panicServed) == mine) return panicResult && legacyOk;
                    panicDone.Reset();
                }
            }
        }

        private bool Deactivate(string reason)
        {
            return Deactivate(reason, false);
        }

        private bool Deactivate(string reason, bool quiet)
        {
            InvalidateCacheWarm();
            SetAutoGpuSessionStamp(0);
            InvalidateStandbyCleanerWork();
            EndEnglishInputSession();
            InvalidateIntelGraphicsWork();
            InvalidateRendererHandoff();
            // 先封账再做任何恢复 避免把 Pavise 自己撤电源/核心/网络设置产生的 DPC
            // 记到刚结束的游戏里 ReportFinish 内部按 Present→DPC 收口
            Exception reportFailure = null;
            try { ReportFinish(); }
            catch (Exception ex) { reportFailure = ex; }
            finally
            {
                // ReportFinish 任何中途异常都不能把两个 ETW 探针留到下一局
                List<long[]> abandoned;
                try { CollectLongFrames(0, TimeSpan.Zero, out abandoned); } catch { }
                try { irqProbe.TakeSummary(); } catch { }
            }
            PowerBudgetYieldRunner.Stop();
            VramShield.Release();
            RestorePowerOverlay();
            SelfYield.Release();
            lock (sync)
            {
                active = false;
                activeGame = null;
                firstSweep = true;
            }
            sessionPolicy = null;
            RestoreGlobalCoreMask();
            SuppressionCore.GpuDemoteEnabled = gpuDemoteOn;
            gameGoneSinceTicks = 0;
            cpuSaturation.Reset();
            boostPriorityTarget = Native.HIGH_PRIORITY_CLASS;
            ResetBoostIdentity();
            Interlocked.Exchange(ref boostFirstStampTicks, 0);
            Interlocked.Exchange(ref nvTweakRetryAtTicks, 0);
            preStagedNvPath = null;
            try { cpuLimit.Stop(); } catch { }
            autoGpuScanned = false;
            ResetAdaptiveGuard();
            partitionHintLogged = false;

            bool clean = UnboostGames();
            slowEnvAtTicks = 0;
            List<int> background = core.PidsWith(SuppressReason.Background);
            int ok = core.ReleaseReason(SuppressReason.Background);
            bool backgroundClean = true;
            foreach (int pid in background) if (core.IsThrottled(pid)) { backgroundClean = false; break; }
            bool envClean = RestoreEnv();
            ClearEnvRetryState();
            if (clean) CrashGuard.ClearBoost();
            int restoredTotal = ok + gracePreReleased;
            gracePreReleased = 0;
            if (!quiet || restoredTotal > 0)
                Logger.Log(Lang.T("log.gamemodeboost.52") + reason + Lang.T("log.gamemodeboost.53") + restoredTotal
                    + Lang.T("log.gamemodeboost.54"));
            lock (sync)
            {
                activeDetection = null;
                transitionProbeRendererPid = 0;
                transitionProbeRendererCreation = 0;
            }
            ClearSticky();
            NotifyExtensionSession(false);
            bool standbyClean = standbyCleaner == null || standbyCleaner.Drain(8000);
            bool inputClean = DrainEnglishInput(8000);
            bool intelClean = DrainIntelGraphics(8000);
            bool cacheWarmClean = cacheWarm == null || cacheWarm.Drain(8000);
            // 维持旧语义 恢复动作已经完成后 再把原本会由 ReportFinish 抛出的异常交给上层
            if (reportFailure != null) throw reportFailure;
            return clean && envClean && backgroundClean && standbyClean && inputClean && intelClean && cacheWarmClean;
        }
    }
}
