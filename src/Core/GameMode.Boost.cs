// @author bdth 2074055628@qq.com
// 文件用途 负责游戏提优 环境调整和退出恢复
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class BoostPass
        {
            public bool NvMaxPerf;
            public string NvLowLat;
            public bool NvSmooth;
            public bool NvShader;
            public bool NvRebar;
            public string NvDlss;
            public bool UseStrict;
            public ulong DesiredMask;
            public int RendererPid;
            public long RendererCreation;
            public string RendererName;
            public string RendererPath;
            public string RendererProfileId;
            public bool RendererLearnable;
            public bool WriteDenied;
            public uint PriorityTarget;
        }

        private void Boost(ProcessSnapshot all)
        {
            var live = new HashSet<int>();
            BoostPass pass = PrepareBoostPass();
            bool rendererSeen = false;
            if (irqProbe.RequiresPlacementAudit
                && !IrqSessionProbe.CanConfirmMaskShape(
                    pass.DesiredMask, allMask))
                irqProbe.InvalidateGameMask();
            // 已开采 epoch 必须在恢复旧 renderer 之前先绑定同一份 proof；
            // renderer 换代或配置换 mask 时，不能让 DropStale 的恢复动作混入旧局。
            if (irqProbe.IsPlacementCapturing
                && !irqProbe.ProofMatches(
                    pass.DesiredMask,
                    pass.RendererPid, pass.RendererCreation))
                irqProbe.InvalidateGameMask();
            // 旧 renderer 的恢复也会触发调度/GPU 写入。本轮只要发现过
            // stale 状态，就不允许新的 IRQ epoch 起采；下轮确认已无
            // stale 后才能开始，给恢复写入留出完整的采样边界。
            bool staleRestoreThisPass = DropStaleBoosts(pass);
            if (!irqProbe.RequiresPlacementAudit)
                RestoreOrphanedIrqProofHardPin(pass);
            if (irqProbe.IsPlacementCapturing)
            {
                // 采集中先只审计身份、硬亲和与调优状态；若确实需要写，
                // AuditActiveIrqCapture 会先 RestartCurrentEpoch 并同步停掉旧 ETW，
                // 然后才允许本轮落入 priority/IO/GPU/QoS/lane setter。
                if (AuditActiveIrqCapture(all, pass)) return;
            }
            foreach (ProcEntry p in all.Entries)
            {
                try
                {
                    int pid = p.Pid;
                    live.Add(pid);
                    if (pass.RendererPid <= 0 || pid != pass.RendererPid) continue;
                    rendererSeen = true;
                    if (irqProbe.IsPlacementCapturing
                        && CfgOffTweak.NeedsApply(pass.RendererName))
                        irqProbe.InvalidateGameMask();
                    if (CfgOffTweak.Enabled) CfgOffTweak.EnsureForGame(pass.RendererName);
                    bool known, needTweak, needPlacement;
                    bool auditDue = ComputeAuditDue(pid, pass, out known, out needTweak, out needPlacement);
                    bool placementAudit = irqProbe.RequiresPlacementAudit;
                    if (!auditDue && !placementAudit) continue;
                    IntPtr h = OpenBoostHandle(pid, pass);
                    if (h == IntPtr.Zero)
                    {
                        // 尚未开采时没有数据可被污染，保留 armed 等下一轮；只有已开采
                        // 后失去读回能力，才必须永久废弃本局 epoch。
                        if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                        continue;
                    }
                    try
                    {
                        long currentCreation;
                        if (!VerifyRendererIdentity(h, pid, pass, out currentCreation))
                        {
                            if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                            continue;
                        }

                        if (placementAudit)
                        {
                            bool normalPlacement = PlacementMatches(h, pass);
                            if (AttributionPlacementMatches(h, pass))
                            {
                                lock (sync)
                                {
                                    gamePlacement[pid] = pass.DesiredMask;
                                    gamePlacementStrict[pid] = pass.UseStrict;
                                    placementFail.Remove(pid);
                                    placementGaveUp.Remove(pid);
                                }
                                needPlacement = false;
                                if (!auditDue)
                                {
                                    bool wouldMutate = AuditWouldMutateRenderer(
                                        h, pid, currentCreation,
                                        pass, known, needTweak);
                                    if (wouldMutate)
                                    {
                                        if (irqProbe.IsPlacementCapturing)
                                            irqProbe.RestartCurrentEpoch();
                                        lock (sync)
                                            gameBoostNextAudit.Remove(pid);
                                        auditDue = true;
                                    }
                                    else
                                    {
                                        if (!staleRestoreThisPass)
                                            ConfirmIrqCapture(
                                                h, pass, pid, currentCreation);
                                        continue;
                                    }
                                }
                            }
                            else
                            {
                                // 已开采的 epoch 只要观察到一次漂移便永久废弃；尚未开采时
                                // 清掉缓存，让本轮正常落核流程重新施加并读回验证。
                                if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                                // 软 CPU Sets 可以是有效的普通落核，但不足以支撑
                                // IRQ 归因。只有普通读回也失败时才清缓存重写，
                                // 否则保持 armed 等待，避免每 500ms 重复写 CPU Sets。
                                if (!normalPlacement)
                                    lock (sync)
                                    {
                                        gamePlacement.Remove(pid);
                                        gamePlacementStrict.Remove(pid);
                                        needPlacement = !pass.WriteDenied
                                            && !placementGaveUp.Contains(pid);
                                    }
                                else if (!auditDue)
                                    // 普通软落核稳定，只是达不到 IRQ 归因的严格
                                    // proof。保留 armed，但不要因 placementAudit 在每次
                                    // 进程扫描里重跑整套 boost 读写。
                                    continue;
                            }
                        }
                        HandlePidReuse(pid, currentCreation, ref known, ref needPlacement);
                        if (irqProbe.IsPlacementCapturing
                            && AuditWouldMutateRenderer(
                                h, pid, currentCreation,
                                pass, known, needTweak))
                            irqProbe.InvalidateGameMask();
                        bool newlyTracked, gpuOk;
                        if (!CaptureAndTrack(h, pid, currentCreation, pass, known, out newlyTracked, out gpuOk)) continue;
                        bool stateOk, firstVerified;
                        if (!ApplyBoostStateStage(h, pid, pass, needTweak, out stateOk, out firstVerified))
                        {
                            if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                            continue;
                        }
                        string placementText;
                        bool placementVerified;
                        if (!ApplyPlacementStage(h, pid, pass, needPlacement, newlyTracked,
                            out placementText, out placementVerified)) continue;
                        if (!placementVerified && irqProbe.IsPlacementCapturing)
                            irqProbe.InvalidateGameMask();
                        else if (!placementVerified)
                        {
                            bool gaveUp;
                            lock (sync) gaveUp = placementGaveUp.Contains(pid);
                            if (gaveUp && irqProbe.RequiresPlacementAudit)
                                irqProbe.InvalidateGameMask();
                        }
                        bool ecoCleared = ClearEfficiencyMode(h, pid, pass);
                        EngageLaneAndReport(h, all, pid, currentCreation, pass, stateOk, firstVerified, gpuOk, ecoCleared, placementText);
                        ApplyGameTweaks(h, pid, pass, needTweak);

                        bool tuningPending;
                        lock (sync) tuningPending = !tweakApplied.Contains(pid);
                        if (tuningPending)
                        {
                            if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                            continue;
                        }
                        if (AuditWouldMutateRenderer(
                                h, pid, currentCreation,
                                pass, true, false))
                        {
                            if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                            continue;
                        }

                        // 首次 ETW 必须晚于本轮所有 Pavise 调优写入；随后再读回同一
                        // renderer 身份与落核，避免把初始化驱动/调度产生的 DPC 算成游戏证据。
                        long finalCreation;
                        bool finalIdentity = VerifyRendererIdentity(
                            h, pid, pass, out finalCreation);
                        bool finalPlacement = finalIdentity
                            && AttributionPlacementMatches(h, pass);
                        if (finalPlacement && !staleRestoreThisPass)
                            ConfirmIrqCapture(
                                h, pass, pid, finalCreation);
                        else
                        {
                            if (irqProbe.IsPlacementCapturing)
                                irqProbe.InvalidateGameMask();
                            RestoreIrqProofHardPin(h, pid);
                        }
                    }
                    finally { Native.CloseHandle(h); }
                }
                catch
                {
                    // 无法完成本轮身份/落核复核时，宁可丢弃整局中断样本。
                    if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                }
            }
            if (!rendererSeen && irqProbe.IsPlacementCapturing)
                // 快照中渲染进程消失是正常退出/短暂漏检边界，
                // 先封存但不落盘。后续确认退出才提交，恢复则丢前缀重开。
                SealIrqObservation();
            if (!irqProbe.IsPlacementCapturing) PruneDeadBoosts(live);
        }

        private BoostPass PrepareBoostPass()
        {
            var pass = new BoostPass();
            PolicySnapshot sp = sessionPolicy;
            pass.NvMaxPerf = sp != null ? sp.NvMaxPerf : nvMaxPerf;
            pass.NvLowLat = sp != null ? sp.NvLowLatMode : nvLowLatMode;
            pass.NvSmooth = sp != null ? sp.NvSmoothMotion : nvSmoothMotion;
            pass.NvShader = sp != null ? sp.NvShaderCacheMax : nvShaderCacheMax;
            pass.NvRebar = sp != null ? sp.NvRebar : nvRebarOn;
            pass.NvDlss = sp != null ? sp.NvDlssMode : nvDlssMode;
            pass.DesiredMask = EffectiveGameMask(sp, out pass.UseStrict);
            pass.RendererPid = -1;
            pass.RendererCreation = 0;
            pass.RendererName = null;
            pass.RendererPath = null;
            pass.RendererProfileId = null;
            pass.RendererLearnable = false;
            lock (sync)
                if (activeDetection != null && activeDetection.RendererCandidateSelected)
                {
                    pass.RendererPid = activeDetection.RendererPid;
                    pass.RendererCreation =
                        activeDetection.RendererCreation;
                    pass.RendererName =
                        activeDetection.RendererName;
                    pass.RendererPath =
                        activeDetection.RendererPath;
                    pass.RendererProfileId =
                        activeDetection.Profile != null
                            ? activeDetection.Profile.Id : null;
                    pass.RendererLearnable =
                        activeDetection.RendererLearnable;
                }
            pass.WriteDenied = pass.RendererPid > 0 && ProtectedGameRoster.Contains(pass.RendererName);
            ResolvePriorityTarget(pass);
            return pass;
        }

        private ulong EffectiveGameMask(PolicySnapshot sp, out bool useStrict)
        {
            ulong customMask = CpuTopology.CustomMask;
            useStrict = customMask != 0
                || ShouldUseCorePartition(sp != null ? sp.StrictCores : corePartitionOn,
                    CpuTopology.HasSafeBackgroundPartition());
            return customMask != 0 ? customMask : useStrict ? strictMask : gameMask;
        }

        private readonly CpuSaturation cpuSaturation = new CpuSaturation();
        private uint boostPriorityTarget = Native.HIGH_PRIORITY_CLASS;
        private long boostFirstStampTicks;

        internal static uint BoostPriorityTarget(bool saturated, bool laneActive)
        {
            return saturated && !laneActive
                ? Native.NORMAL_PRIORITY_CLASS : Native.HIGH_PRIORITY_CLASS;
        }

        private void ResolvePriorityTarget(BoostPass pass)
        {
            bool saturated = cpuSaturation.Update(cpuSaturation.Sample(), DateTime.UtcNow.Ticks);
            bool laneActive = pass.RendererPid > 0
                && RenderLane.IsActiveFor(pass.RendererPid, pass.RendererCreation);
            uint priorityTarget = BoostPriorityTarget(saturated, laneActive);
            if (priorityTarget != boostPriorityTarget)
            {
                boostPriorityTarget = priorityTarget;
                if (pass.RendererPid > 0)
                {
                    lock (sync)
                    {
                        boostStateVerified.Remove(pass.RendererPid);
                        gameBoostNextAudit.Remove(pass.RendererPid);
                    }
                    Logger.Log(priorityTarget == Native.NORMAL_PRIORITY_CLASS
                        ? Lang.T("log.boostsat.1") : Lang.T("log.boostsat.2"));
                }
            }
            pass.PriorityTarget = priorityTarget;
        }

        private bool DropStaleBoosts(BoostPass pass)
        {
            bool staleBoost = false;
            lock (sync)
                foreach (KeyValuePair<int, Snap> boosted
                    in gameBoost)
                    if (boosted.Key != pass.RendererPid
                        || boosted.Value.Creation
                            != pass.RendererCreation
                        || !string.Equals(
                            boosted.Value.Name,
                            pass.RendererName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        staleBoost = true;
                        break;
                    }
            if (!staleBoost) return false;
            if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
            UnboostGames(pass.RendererPid, pass.RendererCreation, pass.RendererName);
            return true;
        }

        private bool ComputeAuditDue(int pid, BoostPass pass,
            out bool known, out bool needTweak, out bool needPlacement)
        {
            bool retryEco, auditDue, writeBlocked;
            lock (sync)
            {
                writeBlocked = pass.WriteDenied || boostHandleStripped.Contains(pid);
                known = gameBoost.ContainsKey(pid);
                retryEco = !writeBlocked && boostFail.ContainsKey(pid) && !boostEcoGaveUp.Contains(pid);
                needTweak = !tweakApplied.Contains(pid);
                ulong placed; bool placedStrict;
                needPlacement = !writeBlocked && !placementGaveUp.Contains(pid)
                    && (!gamePlacement.TryGetValue(pid, out placed) || placed != pass.DesiredMask
                        || !gamePlacementStrict.TryGetValue(pid, out placedStrict) || placedStrict != pass.UseStrict);
                long nextAudit;
                auditDue = !known || retryEco || needTweak || needPlacement
                    || !gameBoostNextAudit.TryGetValue(pid, out nextAudit)
                    || DateTime.UtcNow.Ticks >= nextAudit;
            }
            return auditDue;
        }

        private bool AuditWouldMutateRenderer(
            IntPtr h, int pid, long creation,
            BoostPass pass, bool known, bool needTweak)
        {
            if (!known || needTweak) return true;
            if (pass.WriteDenied) return false;
            if (Native.GetPriorityClass(h) != pass.PriorityTarget
                || Native.QueryIoPriority(h) != 3) return true;
            int gpuPriority;
            int gpuQuery = Native.D3DKMTGetProcessSchedulingPriorityClass(
                h, out gpuPriority);
            if (gpuQuery == 0)
            {
                if (gpuPriority != Native.GpuPriorityHigh) return true;
            }
            else
            {
                // 已跟踪 GPU 原值时，后续 CaptureAndTrack 会再查并可能
                // 写回 High。预查失败不能 fail-open，否则该写入会落入
                // 已开始的 IRQ epoch。
                lock (sync)
                    if (gameGpu.ContainsKey(pid)) return true;
            }
            bool ecoGaveUp;
            lock (sync) ecoGaveUp = boostEcoGaveUp.Contains(pid);
            if (!ecoGaveUp && !HighQoSVerified(h)) return true;
            if (EffLane)
            {
                LaneState lane = RenderLane.StateFor(pid, creation);
                if (IrqLaneNeedsInitialization(lane)) return true;
            }
            return false;
        }

        internal static bool IrqLaneNeedsInitialization(LaneState state)
        {
            // Trying 包含只读识别和每批之间一分钟的等待，不代表正在写。
            // 真正的线程 setter 已由 Begin/EndExternalMutation 与起采共用门锁。
            return state != LaneState.Trying && state != LaneState.Engaged
                && state != LaneState.Unavailable;
        }

        private IntPtr OpenBoostHandle(int pid, BoostPass pass)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                bool noSuchProcess = Native.LastOpenProcessFailureWasNoSuchProcess();
                // 后续保护名单/IFEO 处理可能写注册表；已有 IRQ capture 必须先停。
                if (irqProbe.IsPlacementCapturing)
                {
                    // 已确认进程不存在是正常收口，不能把整局废弃；
                    // 拒绝访问/身份不明才必须作废。
                    if (noSuchProcess) SealIrqObservation();
                    else irqProbe.InvalidateGameMask();
                }
                bool firstDeny;
                lock (sync) firstDeny = boostDenied.Add(pid);
                if (firstDeny) Logger.Log(Lang.T("log.gamemodeboost.3") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.4"));
                if (!noSuchProcess)
                {
                    ProtectedGameRoster.Remember(pass.RendererName);
                    if (EffIfeo && EffBoost) { IfeoBoost.Arm(pass.RendererName); IfeoBoost.EnsureForGame(pass.RendererName); }
                }
            }
            return h;
        }

        private bool VerifyRendererIdentity(IntPtr h, int pid, BoostPass pass, out long currentCreation)
        {
            string img = Native.ImageName(h);
            long currentCpu; ulong currentDisk;
            if (!Native.QueryProcessSample(h, out currentCreation, out currentCpu, out currentDisk))
            {
                Logger.Log(Lang.T("log.gamemodeboost.5") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.6"));
                return false;
            }
            if (!RendererIdentityMatches(
                    pass.RendererPid, pass.RendererCreation,
                    pass.RendererName, pid,
                    currentCreation, img))
            {
                Logger.Log(Lang.T("log.gamemodeboost.7")
                    + pid + Lang.T("log.gamemodeboost.8"));
                return false;
            }
            return true;
        }

        private void HandlePidReuse(int pid, long currentCreation, ref bool known, ref bool needPlacement)
        {
            if (known)
            {
                Snap tracked;
                bool reused = false;
                lock (sync)
                    if (gameBoost.TryGetValue(pid, out tracked) && tracked.Creation > 0
                        && tracked.Creation != currentCreation)
                    {
                        gameBoost.Remove(pid); gameGpu.Remove(pid); gamePlacement.Remove(pid);
                        gamePlacementStrict.Remove(pid); boostFail.Remove(pid);
                        ForgetIrqProofHardPin(pid);
                        boostStateWarned.Remove(pid); boostStateVerified.Remove(pid);
                        gameBoostNextAudit.Remove(pid); boostStateFail.Remove(pid);
                        boostHandleStripped.Remove(pid); boostEcoGaveUp.Remove(pid);
                        placementFail.Remove(pid); placementGaveUp.Remove(pid);
                        tweakApplied.Remove(pid); reused = true; known = false;
                    }
                if (reused)
                {
                    needPlacement = true;
                    CrashGuard.ReleaseBoostProcess(pid, tracked.Creation);
                }
            }
        }

        private bool CaptureAndTrack(IntPtr h, int pid, long currentCreation, BoostPass pass,
            bool known, out bool newlyTracked, out bool gpuOk)
        {
            newlyTracked = false;
            gpuOk = false;
            if (!known)
            {
                uint pri = Native.GetPriorityClass(h);
                if (pri == 0) pri = Native.NORMAL_PRIORITY_CLASS;
                ulong oaff = Native.QueryAffinity(h);
                uint[] ocpuSets = Native.QueryCpuSets(h);
                if (ocpuSets == null)
                {
                    Logger.Log(Lang.T("log.gamemodeboost.9") + pass.RendererName + " pid " + pid);
                    return false;
                }
                int oio = Native.QueryIoPriority(h);
                int opg = Native.QueryPagePriority(h);
                int gpuOld;
                bool gpuKnown = Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuOld) == 0;
                if (!gpuKnown) gpuOld = -1;

                int oqc, oqs;
                if (!Native.TryQueryPowerThrottling(h, out oqc, out oqs)) { oqc = -1; oqs = -1; }
                CrashGuard.OriginalBoostState recovered;
                if (!CrashGuard.MarkBoostProcess(pid, currentCreation, pass.RendererName, pri, oaff,
                    oio, opg, gpuOld, ocpuSets, oqc, oqs, out recovered))
                {
                    Logger.Log(Lang.T("log.gamemodeboost.10") + pass.RendererName + " pid " + pid);
                    return false;
                }
                if (recovered != null)
                {
                    pri = recovered.Priority;
                    oaff = recovered.Affinity;
                    oio = recovered.Io;
                    opg = recovered.Page;
                    gpuOld = recovered.Gpu;
                    gpuKnown = gpuOld >= 0;
                    ocpuSets = recovered.CpuSets;
                    oqc = recovered.QoSControl;
                    oqs = recovered.QoSState;
                }
                var snap = new Snap { Pri = pri, Aff = oaff, Io = oio, Pg = opg,
                    Name = pass.RendererName, Creation = currentCreation, CpuSets = ocpuSets,
                    QoSControl = oqc, QoSState = oqs };
                lock (sync) gameBoost[pid] = snap;
                newlyTracked = true;
                // 渲染身份确认/游戏库替换在调度前独立提交，不依赖提优句柄是否可写。
                gpuOk = gpuKnown && !pass.WriteDenied && ApplyAndVerifyGpuBoost(h);
                lock (sync) { if (gpuKnown && !pass.WriteDenied) gameGpu[pid] = gpuOld; }
            }
            else
            {
                int ignoredGpu;
                lock (sync) gpuOk = gameGpu.TryGetValue(pid, out ignoredGpu);
                if (gpuOk && !pass.WriteDenied) gpuOk = ApplyAndVerifyGpuBoost(h);
            }
            return true;
        }

        private bool ApplyBoostStateStage(IntPtr h, int pid, BoostPass pass, bool needTweak,
            out bool stateOk, out bool firstVerified)
        {
            firstVerified = false;
            if (pass.WriteDenied)
            {
                stateOk = false;
                lock (sync)
                {
                    boostStateVerified.Remove(pid);
                    placementGaveUp.Add(pid);
                    boostEcoGaveUp.Add(pid);
                    gameBoostNextAudit[pid] = DateTime.UtcNow.AddSeconds(20 + Math.Abs(pid % 11)).Ticks;
                }
                return true;
            }
            uint actualPriority;
            int actualIo, writeError;
            stateOk = ApplyAndVerifyBoostState(h, pass.PriorityTarget, out actualPriority, out actualIo, out writeError);

            uint grantedAccess = 0;
            bool handleStripped = !stateOk
                && Native.HandleWriteAccessStripped(h, out grantedAccess);
            if (handleStripped)
            {
                bool firstStrip;
                lock (sync)
                {
                    firstStrip = boostHandleStripped.Add(pid);
                    boostStateVerified.Remove(pid);
                    boostFail.Remove(pid);
                    placementFail.Remove(pid);
                    placementGaveUp.Add(pid);
                    boostEcoGaveUp.Add(pid);
                    gamePlacement.Remove(pid);
                    gamePlacementStrict.Remove(pid);
                }
                if (firstStrip) OnGameHandleStripped(pid, pass.RendererName, grantedAccess);
                if (!needTweak) return false;
            }

            bool firstStateWarning = false, stateNowGaveUp = false;
            lock (sync)
            {
                if (stateOk)
                {
                    firstVerified = boostStateVerified.Add(pid);
                    boostStateWarned.Remove(pid);
                    boostStateFail.Remove(pid);
                    int jitter = Math.Abs(pid % 11);
                    gameBoostNextAudit[pid] =
                        DateTime.UtcNow.AddSeconds(20 + jitter).Ticks;
                }
                else
                {
                    boostStateVerified.Remove(pid);
                    firstStateWarning = boostStateWarned.Add(pid);
                    int stateTries;
                    boostStateFail.TryGetValue(pid, out stateTries); stateTries++;
                    if (stateTries >= StateRetryMax)
                    {
                        boostStateFail.Remove(pid);
                        stateNowGaveUp = true;
                        gameBoostNextAudit[pid] =
                            DateTime.UtcNow.AddMinutes(5).Ticks;
                    }
                    else
                    {
                        boostStateFail[pid] = stateTries;
                        gameBoostNextAudit[pid] =
                            DateTime.UtcNow.AddSeconds(4).Ticks;
                    }
                }
            }
            if (!stateOk && firstStateWarning && !handleStripped)
                Logger.Log(Lang.T("log.gamemodeboost.11") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.12")
                    + actualPriority.ToString("X") + " / IO " + actualIo + Lang.T("log.gamemodeboost.13") + writeError + Lang.T("log.gamemodeboost.14"));
            if (stateNowGaveUp && !handleStripped)
                Logger.Log(Lang.T("log.gamemodeboost.11") + pass.RendererName + " pid " + pid
                    + Lang.T("log.gamemodeboost.55") + StateRetryMax + Lang.T("log.gamemodeboost.56"));
            return true;
        }

        private bool AuditActiveIrqCapture(ProcessSnapshot all, BoostPass pass)
        {
            if (all == null || pass == null || pass.RendererPid <= 0)
            {
                irqProbe.InvalidateGameMask();
                return true;
            }
            ProcEntry renderer = all.Find(pass.RendererPid);
            if (renderer == null)
            {
                SealIrqObservation();
                return true;
            }

            IntPtr h = Native.OpenProcess(
                Native.PROCESS_SET_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION,
                false, pass.RendererPid);
            if (h == IntPtr.Zero)
            {
                if (Native.LastOpenProcessFailureWasNoSuchProcess())
                    SealIrqObservation();
                else
                    irqProbe.InvalidateGameMask();
                return true;
            }
            try
            {
                long creation;
                if (!VerifyRendererIdentity(
                        h, pass.RendererPid, pass, out creation))
                {
                    irqProbe.InvalidateGameMask();
                    return true;
                }
                if (!AttributionPlacementMatches(h, pass))
                {
                    irqProbe.RestartCurrentEpoch();
                    // 线程级归因 proof 比普通落核读回更严格。线程瞬时增删、
                    // 查询被拒或显式 thread CPU Sets 都只应停掉 IRQ epoch；
                    // 普通 placement 仍稳定时不能清缓存并每 500ms 重写设置。
                    if (!PlacementMatches(h, pass))
                    {
                        lock (sync)
                        {
                            gamePlacement.Remove(pass.RendererPid);
                            gamePlacementStrict.Remove(pass.RendererPid);
                            gameBoostNextAudit.Remove(pass.RendererPid);
                        }
                    }
                    return false;
                }
                bool known, needTweak, needPlacement;
                ComputeAuditDue(pass.RendererPid, pass,
                    out known, out needTweak, out needPlacement);
                bool wouldMutate = CfgOffTweak.NeedsApply(pass.RendererName)
                    || AuditWouldMutateRenderer(
                        h, pass.RendererPid, creation,
                        pass, known, needTweak);
                if (wouldMutate)
                {
                    irqProbe.RestartCurrentEpoch();
                    lock (sync)
                        gameBoostNextAudit.Remove(pass.RendererPid);
                    return false;
                }
                ConfirmIrqCapture(
                    h, pass, pass.RendererPid, creation);
                return true;
            }
            catch { irqProbe.InvalidateGameMask(); return true; }
            finally { Native.CloseHandle(h); }
        }

        // 这个读回只验证 Pavise 的软/硬落核是否生效，不作为中断归因证据。
        // 线程显式 CPU Sets 可以覆盖进程默认 CPU Sets，因此后者不能证明每个
        // 渲染线程都在 desiredMask 内。
        private bool PlacementMatches(IntPtr h, BoostPass pass)
        {
            if (h == IntPtr.Zero || pass == null
                || !IrqSessionProbe.CanConfirmMask(
                    pass.DesiredMask, allMask,
                    pass.RendererPid, pass.RendererCreation))
                return false;
            uint[] ids = CpuTopology.CustomCpuSetIds()
                ?? CpuTopology.AdaptiveGameCpuSetIds(pass.UseStrict);
            bool cpuSetsMatch = ids != null && ids.Length > 0
                && Native.CpuSetsMatch(h, ids);
            bool cpuSetsUnconstrained = Native.CpuSetsMatch(h, new uint[0]);
            ulong affinity = CpuTopology.MultiGroup ? 0 : Native.QueryAffinity(h);
            return PlacementProofMatches(
                pass.DesiredMask, affinity,
                CpuTopology.MultiGroup, cpuSetsMatch,
                cpuSetsUnconstrained);
        }

        internal static bool PlacementProofMatches(
            ulong desiredMask, ulong hardAffinity,
            bool multiGroup, bool cpuSetsMatch,
            bool cpuSetsUnconstrained)
        {
            if (desiredMask == 0) return false;
            if (multiGroup) return cpuSetsMatch;
            // 有目标 CPU Sets 时 hard affinity 至少要覆盖目标；否则有效集合是
            // 两者交集。没有 CPU Sets 时则只接受精确 hard affinity。
            if (cpuSetsMatch)
                return hardAffinity != 0
                    && (desiredMask & ~hardAffinity) == 0;
            return cpuSetsUnconstrained
                && hardAffinity == desiredMask;
        }

        // 中断归因必须证明 renderer 的每一个当前线程实际可运行集合。
        // 进程默认 CPU Sets 会被线程显式 CPU Sets 覆盖，只有进程硬亲和性
        // 不能证明 desired 里的每一颗核确实仍属于 renderer。多组机器的 ulong
        // 无法完整表示全部组，所以宁可不采样，也不做不完整的证明。
        private bool AttributionPlacementMatches(IntPtr h, BoostPass pass)
        {
            if (h == IntPtr.Zero || pass == null
                || !IrqSessionProbe.CanConfirmMask(
                    pass.DesiredMask, allMask,
                    pass.RendererPid, pass.RendererCreation))
                return false;
            return AttributionThreadPlacementMatches(
                h, pass.RendererPid, pass.RendererCreation,
                pass.DesiredMask, CpuTopology.MultiGroup);
        }

        internal static bool AttributionPlacementProofMatches(
            ulong desiredMask, ulong hardAffinity, bool multiGroup,
            bool threadProofComplete, ulong threadUnion)
        {
            return !multiGroup && threadProofComplete
                && desiredMask != 0 && hardAffinity == desiredMask
                && threadUnion == desiredMask;
        }

        internal static ulong EffectiveAttributionThreadMask(
            ulong processHardAffinity, ulong threadGroupAffinity,
            ulong assignedCpuSetMask, bool hasCpuSetAssignment)
        {
            ulong threadHard = processHardAffinity & threadGroupAffinity;
            if (threadHard == 0 || !hasCpuSetAssignment) return threadHard;
            ulong intersection = threadHard & assignedCpuSetMask;
            // Windows 在 CPU Set 分配与限制性硬亲和性完全冲突时以后者为准。
            return intersection != 0 ? intersection : threadHard;
        }

        private struct AttributionCpuSetAssignment
        {
            public bool Assigned;
            public ulong Mask;
        }

        private static bool TryQueryProcessAttributionCpuSets(
            IntPtr process, out AttributionCpuSetAssignment assignment)
        {
            assignment = new AttributionCpuSetAssignment();
            bool assigned;
            ulong mask;
            Native.CpuSetMaskQueryResult result =
                Native.QueryProcessDefaultCpuSetMasks(
                    process, out assigned, out mask);
            if (result == Native.CpuSetMaskQueryResult.Success)
            {
                assignment.Assigned = assigned;
                assignment.Mask = mask;
                return !assigned || mask != 0;
            }
            // Win11 mask getter 能同时看到 Masks 与 IDs 两条设置路径；只在
            // 老 Win10 确实没有导出时才允许回退旧 ID getter。
            if (result != Native.CpuSetMaskQueryResult.ApiUnavailable)
                return false;
            uint[] ids = Native.QueryCpuSets(process);
            if (ids == null) return false;
            assignment.Assigned = ids.Length > 0;
            return !assignment.Assigned
                || CpuTopology.TryCpuSetIdsToMask(ids, out assignment.Mask);
        }

        private static bool TryQueryThreadAttributionCpuSets(
            IntPtr thread, out AttributionCpuSetAssignment assignment)
        {
            assignment = new AttributionCpuSetAssignment();
            bool assigned;
            ulong mask;
            Native.CpuSetMaskQueryResult result =
                Native.QueryThreadSelectedCpuSetMasks(
                    thread, out assigned, out mask);
            if (result == Native.CpuSetMaskQueryResult.Success)
            {
                assignment.Assigned = assigned;
                assignment.Mask = mask;
                return !assigned || mask != 0;
            }
            if (result != Native.CpuSetMaskQueryResult.ApiUnavailable)
                return false;
            uint[] ids = Native.QueryThreadSelectedCpuSets(thread);
            if (ids == null) return false;
            assignment.Assigned = ids.Length > 0;
            return !assignment.Assigned
                || CpuTopology.TryCpuSetIdsToMask(ids, out assignment.Mask);
        }

        internal static bool AttributionThreadPlacementMatches(
            IntPtr processHandle, int pid, long expectedCreation,
            ulong desiredMask, bool multiGroup)
        {
            if (processHandle == IntPtr.Zero || pid <= 0
                || expectedCreation <= 0 || desiredMask == 0
                || multiGroup)
                return false;

            long creation, cpu;
            ulong io;
            if (!Native.QueryProcessSample(
                    processHandle, out creation, out cpu, out io)
                || creation != expectedCreation)
                return false;

            ulong processHard = Native.QueryAffinity(processHandle);
            if (processHard != desiredMask) return false;

            AttributionCpuSetAssignment processDefault;
            if (!TryQueryProcessAttributionCpuSets(
                    processHandle, out processDefault))
                return false;

            int[] firstThreads = SnapshotRendererThreadIds(
                pid, expectedCreation);
            if (firstThreads == null || firstThreads.Length == 0)
                return false;

            var handles = new List<IntPtr>(firstThreads.Length);
            var initialGroups = new ushort[firstThreads.Length];
            var initialGroupAffinities = new ulong[firstThreads.Length];
            var initialSelected =
                new AttributionCpuSetAssignment[firstThreads.Length];
            ulong threadUnion = 0;
            try
            {
                for (int i = 0; i < firstThreads.Length; i++)
                {
                    IntPtr thread = Native.OpenThread(
                        Native.THREAD_QUERY_LIMITED_INFORMATION
                            | Native.SYNCHRONIZE,
                        false, firstThreads[i]);
                    if (thread == IntPtr.Zero) return false;
                    handles.Add(thread);

                    if (Native.QueryThreadOwnerPid(thread) != pid)
                        return false;
                    bool active;
                    if (!Native.TryQueryThreadActive(thread, out active)
                        || !active)
                        return false;
                    ushort group;
                    ulong groupAffinity;
                    if (!Native.TryQueryThreadGroupAffinity(
                            thread, out group, out groupAffinity)
                        || group != 0)
                        return false;

                    AttributionCpuSetAssignment selected;
                    if (!TryQueryThreadAttributionCpuSets(
                            thread, out selected))
                        return false;
                    initialGroups[i] = group;
                    initialGroupAffinities[i] = groupAffinity;
                    initialSelected[i] = selected;
                    AttributionCpuSetAssignment effectiveAssignment =
                        selected.Assigned ? selected : processDefault;
                    ulong effective = EffectiveAttributionThreadMask(
                        processHard, groupAffinity,
                        effectiveAssignment.Mask,
                        effectiveAssignment.Assigned);
                    if (effective == 0) return false;
                    threadUnion |= effective;
                }

                // 首尾必须从同一批持有句柄复查 owner、存活与线程级策略。
                // 仅比较 TID 集不足以排除线程在证明窗口内退出或改绑。
                for (int i = 0; i < handles.Count; i++)
                {
                    bool active;
                    int ownerPid = Native.QueryThreadOwnerPid(handles[i]);
                    if (!Native.TryQueryThreadActive(
                            handles[i], out active))
                        return false;
                    ushort group;
                    ulong groupAffinity;
                    if (!Native.TryQueryThreadGroupAffinity(
                            handles[i], out group, out groupAffinity))
                        return false;
                    AttributionCpuSetAssignment selected;
                    if (!TryQueryThreadAttributionCpuSets(
                            handles[i], out selected))
                        return false;
                    if (!AttributionThreadStateMatches(
                            pid, ownerPid, active,
                            initialGroups[i], initialGroupAffinities[i],
                            initialSelected[i].Assigned,
                            initialSelected[i].Mask,
                            group, groupAffinity,
                            selected.Assigned, selected.Mask))
                        return false;
                }

                AttributionCpuSetAssignment finalDefault;
                if (!TryQueryProcessAttributionCpuSets(
                        processHandle, out finalDefault)
                    || finalDefault.Assigned != processDefault.Assigned
                    || finalDefault.Mask != processDefault.Mask
                    || Native.QueryAffinity(processHandle) != processHard)
                    return false;
                if (!Native.QueryProcessSample(
                        processHandle, out creation, out cpu, out io)
                    || creation != expectedCreation)
                    return false;
                int[] finalThreads = SnapshotRendererThreadIds(
                    pid, expectedCreation);
                if (!SameAttributionThreadSnapshot(
                        firstThreads, finalThreads))
                    return false;

                return AttributionPlacementProofMatches(
                    desiredMask, processHard, multiGroup,
                    true, threadUnion);
            }
            finally
            {
                for (int i = 0; i < handles.Count; i++)
                    Native.CloseHandle(handles[i]);
            }
        }

        private static int[] SnapshotRendererThreadIds(
            int pid, long expectedCreation)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.StartTime.ToFileTimeUtc()
                        != expectedCreation)
                        return null;
                    ProcessThreadCollection threads = process.Threads;
                    if (threads == null || threads.Count == 0) return null;
                    var ids = new int[threads.Count];
                    for (int i = 0; i < threads.Count; i++)
                    {
                        ids[i] = threads[i].Id;
                        if (ids[i] <= 0) return null;
                    }
                    Array.Sort(ids);
                    for (int i = 1; i < ids.Length; i++)
                        if (ids[i] == ids[i - 1]) return null;
                    return ids;
                }
            }
            catch { return null; }
        }

        internal static bool SameAttributionThreadSnapshot(
            int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        internal static bool AttributionThreadStateMatches(
            int expectedPid, int ownerPid, bool active,
            ushort initialGroup, ulong initialGroupAffinity,
            bool initialCpuSetAssigned, ulong initialCpuSetMask,
            ushort finalGroup, ulong finalGroupAffinity,
            bool finalCpuSetAssigned, ulong finalCpuSetMask)
        {
            return expectedPid > 0 && ownerPid == expectedPid && active
                && initialGroup == 0 && finalGroup == initialGroup
                && initialGroupAffinity != 0
                && finalGroupAffinity == initialGroupAffinity
                && finalCpuSetAssigned == initialCpuSetAssigned
                && finalCpuSetMask == initialCpuSetMask
                && (initialCpuSetAssigned
                    ? initialCpuSetMask != 0
                    : initialCpuSetMask == 0);
        }

        private bool ConfirmIrqCapture(
            IntPtr h, BoostPass pass, int pid, long creation)
        {
            bool accepted = irqProbe.ConfirmGameMask(
                pass.DesiredMask, pid, creation);
            if (!accepted) RestoreIrqProofHardPin(h, pid);
            return accepted;
        }

        private enum IrqProofHandleState
        {
            Match = 0,
            Gone = 1,
            Mismatch = 2,
            Unknown = 3
        }

        private const uint DuplicateSameAccess = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(
            IntPtr sourceProcess, IntPtr sourceHandle,
            IntPtr targetProcess, out IntPtr targetHandle,
            uint desiredAccess, bool inheritHandle, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessId(IntPtr process);

        private static IntPtr DuplicateIrqProofRestoreHandle(IntPtr source)
        {
            if (source == IntPtr.Zero) return IntPtr.Zero;
            IntPtr duplicate;
            IntPtr self = Native.GetCurrentProcess();
            return DuplicateHandle(self, source, self, out duplicate,
                0, false, DuplicateSameAccess)
                ? duplicate : IntPtr.Zero;
        }

        private static IrqProofHandleState IrqProofHandleStateOf(
            IrqProofHardPin pin)
        {
            if (pin == null || pin.RestoreHandle == IntPtr.Zero)
                return IrqProofHandleState.Unknown;
            uint actualPid = GetProcessId(pin.RestoreHandle);
            if (actualPid != 0 && actualPid != (uint)pin.Pid)
                return IrqProofHandleState.Mismatch;
            if (!Native.StillActive(pin.RestoreHandle))
                return IrqProofHandleState.Gone;
            long creation, cpu;
            ulong disk;
            if (!Native.QueryProcessSample(
                    pin.RestoreHandle, out creation, out cpu, out disk))
                return IrqProofHandleState.Unknown;
            if (actualPid == 0 || creation != pin.Creation)
                return IrqProofHandleState.Mismatch;
            return IrqProofHandleState.Match;
        }

        private void RememberIrqProofHardPin(
            int pid, long creation, ulong originalAffinity,
            IntPtr restoreHandle)
        {
            if (pid <= 0 || creation <= 0 || originalAffinity == 0
                || restoreHandle == IntPtr.Zero)
            {
                if (restoreHandle != IntPtr.Zero)
                    Native.CloseHandle(restoreHandle);
                return;
            }
            lock (sync)
            {
                IrqProofHardPin old;
                if (irqProofHardPins.TryGetValue(pid, out old))
                {
                    if (old.Creation == creation
                        && old.RestoreHandle != IntPtr.Zero)
                    {
                        Native.CloseHandle(restoreHandle);
                        return;
                    }
                    irqProofHardPins.Remove(pid);
                    if (old.RestoreHandle != IntPtr.Zero)
                        Native.CloseHandle(old.RestoreHandle);
                }
                irqProofHardPins[pid] = new IrqProofHardPin
                {
                    Pid = pid,
                    Creation = creation,
                    OriginalAffinity = originalAffinity,
                    RestoreHandle = restoreHandle
                };
            }
        }

        // 仅用于进程已确认退出/PID 换代，或其它恢复路径已经精确读回原值后。
        // 活进程的普通失败路径必须保留 handle 继续重试，不能只删 marker。
        private void ForgetIrqProofHardPin(int pid)
        {
            lock (sync)
            {
                IrqProofHardPin pin;
                if (!irqProofHardPins.TryGetValue(pid, out pin)) return;
                irqProofHardPins.Remove(pid);
                if (pin.RestoreHandle != IntPtr.Zero)
                    Native.CloseHandle(pin.RestoreHandle);
            }
        }

        private bool RestoreIrqProofHardPin(IntPtr ignored, int pid)
        {
            lock (sync)
            {
                IrqProofHardPin pin;
                if (!irqProofHardPins.TryGetValue(pid, out pin)) return true;
                IrqProofHandleState state = IrqProofHandleStateOf(pin);
                if (state == IrqProofHandleState.Gone
                    || state == IrqProofHandleState.Mismatch)
                {
                    irqProofHardPins.Remove(pid);
                    // 进程已退出或绑定身份不再一致时，同 PID 下的旧 proof
                    // 缓存也必须失效；否则 PID 复用后可能误认旧 desired 已生效。
                    gamePlacement.Remove(pid);
                    gamePlacementStrict.Remove(pid);
                    if (pin.RestoreHandle != IntPtr.Zero)
                        Native.CloseHandle(pin.RestoreHandle);
                    return true;
                }
                if (state != IrqProofHandleState.Match) return false;
                if (!Native.SetProcessAffinityMask(
                        pin.RestoreHandle, (UIntPtr)pin.OriginalAffinity)
                    || Native.QueryAffinity(pin.RestoreHandle)
                        != pin.OriginalAffinity)
                    return false;
                irqProofHardPins.Remove(pid);
                // 与句柄移除保持在同一个锁域：Stop 超时后仍可能有 worker
                // 正在收尾，不能让它刚写入的新 placement 缓存被旧恢复动作误删。
                gamePlacement.Remove(pid);
                gamePlacementStrict.Remove(pid);
                Native.CloseHandle(pin.RestoreHandle);
            }
            // hard affinity 已撤回原值后，普通 placement 缓存也已同步清除，
            // 不能继续假称 desired 仍成立。proof-gap 会在下一轮重施加；
            // 已 disarm 则只保留实际仍在进程上的软 CPU Sets。
            return true;
        }

        private void RestoreOrphanedIrqProofHardPin(BoostPass pass)
        {
            RestoreAllIrqProofHardPins();
        }

        private bool RestoreAllIrqProofHardPins()
        {
            List<int> pids;
            lock (sync)
                pids = new List<int>(irqProofHardPins.Keys);
            bool ok = true;
            foreach (int pid in pids)
                if (!RestoreIrqProofHardPin(IntPtr.Zero, pid)) ok = false;
            return ok;
        }

#if PAVISE_SELFTEST
        internal static IntPtr DuplicateIrqProofRestoreHandleForTest(IntPtr source)
        {
            return DuplicateIrqProofRestoreHandle(source);
        }

        internal static bool IrqProofRestoreHandleForTest(
            IntPtr handle, int pid, long creation, ulong affinity)
        {
            var pin = new IrqProofHardPin
            {
                Pid = pid,
                Creation = creation,
                OriginalAffinity = affinity,
                RestoreHandle = handle
            };
            return IrqProofHandleStateOf(pin) == IrqProofHandleState.Match
                && Native.SetProcessAffinityMask(handle, (UIntPtr)affinity)
                && Native.QueryAffinity(handle) == affinity;
        }
#endif

        private bool ApplyPlacementStage(IntPtr h, int pid, BoostPass pass, bool needPlacement,
            bool newlyTracked, out string placementText, out bool placementVerified)
        {
            placementText = "";
            placementVerified = false;
            if (!needPlacement)
                placementVerified = PlacementMatches(h, pass);
            if (needPlacement)
            {
                Snap original;
                lock (sync) { if (!gameBoost.TryGetValue(pid, out original)) return false; }

                bool placementOk = Native.RestoreCpuSetsVerified(h, original.CpuSets);
                if (!CpuTopology.MultiGroup)
                {
                    ulong restoredMask = original.Aff != 0 ? original.Aff : allMask;
                    bool hardRestored = Native.SetProcessAffinityMask(
                        h, (UIntPtr)restoredMask)
                        && Native.QueryAffinity(h) == restoredMask;
                    placementOk &= hardRestored;
                    if (hardRestored)
                        ForgetIrqProofHardPin(pid);
                }
                uint[] ids = CpuTopology.CustomCpuSetIds()
                    ?? CpuTopology.AdaptiveGameCpuSetIds(pass.UseStrict);
                bool soft = false;
                bool placementUnavailable = false;
                if (pass.UseStrict || pass.DesiredMask != allMask)
                    soft = Native.TrySetCpuSetsVerified(h, ids);
                // 默认 CPU Sets 会被线程显式选择覆盖，无法作为 IRQ 归因
                // proof。用户明确开启对局观测时，在单 group 机器上再
                // 叠加一层精确的临时进程硬亲和。写入前先复制当前可写句柄，
                // 并与 pid+creation+原 affinity 绑定；即使反作弊随后拒绝新句柄，
                // 仍可用这份 retained handle 恢复并读回。原值未知时绝不强写。
                bool proofHardWritten = false;
                IntPtr proofRestoreHandle = IntPtr.Zero;
                if (soft && irqProbe.RequiresPlacementAudit
                    && !CpuTopology.MultiGroup
                    && pass.DesiredMask != allMask
                    && original.Aff != 0)
                    proofRestoreHandle = DuplicateIrqProofRestoreHandle(h);
                if (proofRestoreHandle != IntPtr.Zero)
                {
                    // Stop 可能在 worker.Join 超时后并发清理。hard write 与
                    // retained handle 登记必须处于同一锁域：要么 Stop 先令
                    // stopping 可见，本轮完全不写；要么先登记，Stop 随后必能恢复。
                    lock (sync)
                    {
                        if (!stopping && Native.SetProcessAffinityMask(
                                h, (UIntPtr)pass.DesiredMask))
                        {
                            RememberIrqProofHardPin(
                                pid, pass.RendererCreation,
                                original.Aff, proofRestoreHandle);
                            proofRestoreHandle = IntPtr.Zero;
                            proofHardWritten = Native.QueryAffinity(h)
                                == pass.DesiredMask;
                        }
                    }
                    if (!proofHardWritten && proofRestoreHandle == IntPtr.Zero)
                        RestoreIrqProofHardPin(IntPtr.Zero, pid);
                }
                if (proofRestoreHandle != IntPtr.Zero)
                    Native.CloseHandle(proofRestoreHandle);
                if (soft)
                {
                    placementVerified = pass.DesiredMask != allMask;
                    placementText = pass.UseStrict
                        ? Lang.T("t.gamemodeboost.15") + CpuTopology.CountSetBits(pass.DesiredMask) + Lang.T("t.gamemodeboost.16")
                        : Lang.T("t.gamemodeboost.17");
                }
                else if (pass.DesiredMask != allMask && !CpuTopology.MultiGroup)
                {
                    Native.RestoreCpuSets(h, original.CpuSets);
                    placementOk = Native.SetProcessAffinityMask(h, (UIntPtr)pass.DesiredMask)
                        && Native.QueryAffinity(h) == pass.DesiredMask;
                    placementVerified = placementOk;
                    placementText = Lang.T("t.gamemodeboost.18") + CpuTopology.CountSetBits(pass.DesiredMask) + Lang.T("t.gamemodeboost.16");
                }
                else
                {
                    placementText = Lang.T("t.gamemodeboost.17");
                    if (pass.UseStrict) placementUnavailable = true;
                }
                if (soft) placementOk = true;
                if (placementUnavailable) placementOk = true;
                // 写入 API 返回成功仍不足以入账，最后再从进程句柄读回一次。
                placementVerified = PlacementMatches(h, pass);
                if (pass.DesiredMask != allMask
                    && !placementUnavailable && !placementVerified)
                    placementOk = false;
                int placeTries = 0;
                bool placementNowGaveUp = false, firstPlacementWarning = false;
                lock (sync)
                {
                    if (placementOk && !placementUnavailable)
                    {
                        gamePlacement[pid] = pass.DesiredMask; gamePlacementStrict[pid] = pass.UseStrict;
                        placementFail.Remove(pid); placementGaveUp.Remove(pid);
                    }
                    else if (placementUnavailable)
                    {
                        gamePlacement.Remove(pid); gamePlacementStrict.Remove(pid);
                        placementFail.Remove(pid); placementGaveUp.Add(pid);
                    }
                    else
                    {
                        gamePlacement.Remove(pid); gamePlacementStrict.Remove(pid);
                        placementFail.TryGetValue(pid, out placeTries); placeTries++;
                        if (placeTries >= PlacementRetryMax)
                        {
                            placementFail.Remove(pid);
                            placementNowGaveUp = placementGaveUp.Add(pid);
                        }
                        else { placementFail[pid] = placeTries; firstPlacementWarning = placeTries == 1; }
                    }
                }
                if (placementUnavailable)
                    Logger.Log(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid
                        + Lang.T("log.gamemodeboost.20"));
                else if (placementNowGaveUp)
                    Logger.Log(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.21")
                        + PlacementRetryMax + Lang.T("log.gamemodeboost.22"));
                else if (!placementOk && firstPlacementWarning)
                    Logger.Log(Lang.T("log.gamemodeboost.23") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.24"));

                if (!newlyTracked && placementOk)
                    Logger.Log(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid + placementText);
            }
            return true;
        }

        private bool ClearEfficiencyMode(IntPtr h, int pid, BoostPass pass)
        {
            bool ecoGaveUp;
            lock (sync) ecoGaveUp = boostEcoGaveUp.Contains(pid);
            bool ecoCleared = ecoGaveUp || HighQoSVerified(h);
            if (!ecoCleared)
            {
                bool timerExemptDropped;
                Native.ApplyHighQoS(h, Native.TimerExemptWanted, out timerExemptDropped);
                if (timerExemptDropped) Logger.Log(Lang.T("log.gamemodeboost.59"));
                ecoCleared = HighQoSVerified(h);
                if (ecoCleared) { lock (sync) { boostFail.Remove(pid); boostEcoGaveUp.Remove(pid); } }
                else
                {
                    int tries;
                    bool nowGaveUp = false;
                    lock (sync)
                    {
                        boostFail.TryGetValue(pid, out tries); tries++;
                        if (tries >= BoostRetryMax)
                        {
                            boostFail.Remove(pid);
                            nowGaveUp = boostEcoGaveUp.Add(pid);
                        }
                        else boostFail[pid] = tries;
                    }
                    if (nowGaveUp)
                        Logger.Log(Lang.T("log.gamemodeboost.3") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.25") + tries + Lang.T("log.gamemodeboost.26"));
                }
            }
            return ecoCleared;
        }

        private void EngageLaneAndReport(IntPtr h, ProcessSnapshot all, int pid, long currentCreation,
            BoostPass pass, bool stateOk, bool firstVerified, bool gpuOk, bool ecoCleared, string placementText)
        {
            if (EffLane && !pass.WriteDenied && !RenderLane.IsActiveFor(pid, currentCreation))
                RenderLane.EnsureForGame(pid, currentCreation, pass.RendererName);

            if (stateOk && firstVerified)
            {
                long stamp = Interlocked.Exchange(ref boostFirstStampTicks, 0);
                if (stamp != 0)
                    Logger.Log(Lang.T("log.gamemodeboost.60")
                        + ((DateTime.UtcNow.Ticks - stamp) / TimeSpan.TicksPerMillisecond)
                        + Lang.T("log.gamemodeboost.61"));
                WarnIfPartitionHurtsWideGame(pass.RendererName, all, pid, pass.DesiredMask);
                Logger.Log(Lang.T("log.gamemodeboost.27") + pass.RendererName + "(pid " + pid + ") "
                    + (pass.PriorityTarget == Native.HIGH_PRIORITY_CLASS ? Lang.T("log.gamemodeboost.28") : Lang.T("log.gamemodeboost.29"))
                    + placementText + Lang.T("log.gamemodeboost.30")
                    + (gpuOk ? Lang.T("log.gamemodeboost.31") : "")
                    + (!Native.PowerThrottlingSupported ? ""
                        : ecoCleared ? Lang.T("t.gamemodeboost.32") : EcoStateText(h)));
            }
        }

        private long nvTweakRetryAtTicks;

        private void ApplyGameTweaks(IntPtr h, int pid, BoostPass pass, bool needTweak)
        {
            if (needTweak)
            {
                if (DateTime.UtcNow.Ticks < Interlocked.Read(ref nvTweakRetryAtTicks)) return;
                string imagePath = Native.ImagePath(h);
                var nvPlan = new NvGamePlan
                {
                    MaxPerf = pass.NvMaxPerf,
                    LowLatMode = pass.NvLowLat,
                    SmoothMotion = pass.NvSmooth,
                    ShaderCacheMax = pass.NvShader,
                    Rebar = pass.NvRebar,
                    DlssMode = pass.NvDlss
                };
                bool nvRetry;
                List<string> nvFailed = NvDrsTweaks.ApplyForGame(imagePath, nvPlan, out nvRetry);
                if (!nvPlan.Empty) HandleNvTweakOutcome(nvFailed, nvPlan);
                if (!nvRetry) lock (sync) tweakApplied.Add(pid);
                else Interlocked.Exchange(ref nvTweakRetryAtTicks,
                    DateTime.UtcNow.AddSeconds(30).Ticks);
            }
        }

        private enum BoostTarget { Alive = 0, Gone = 1, Unopenable = 2 }

        private static BoostTarget ProbeBoostTarget(int pid, long creation)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 87
                    ? BoostTarget.Gone : BoostTarget.Unopenable;
            try
            {
                long cur, cpu;
                ulong io;
                if (!Native.QueryProcessSample(h, out cur, out cpu, out io)) return BoostTarget.Unopenable;
                if (creation > 0 && cur != creation) return BoostTarget.Gone;
                return Native.StillActive(h) ? BoostTarget.Alive : BoostTarget.Gone;
            }
            finally { Native.CloseHandle(h); }
        }

        private const int VanishGiveUpTries = 20;
        private readonly Dictionary<int, int> boostVanishTries = new Dictionary<int, int>();

        internal int DropVanishedBoosts()
        {
            var pending = new List<KeyValuePair<int, Snap>>();
            lock (sync)
                foreach (KeyValuePair<int, Snap> kv in gameBoost) pending.Add(kv);
            if (pending.Count == 0) return 0;

            int dropped = 0, abandoned = 0;
            foreach (KeyValuePair<int, Snap> kv in pending)
            {
                BoostTarget state = ProbeBoostTarget(kv.Key, kv.Value.Creation);
                if (state == BoostTarget.Alive)
                {
                    lock (sync) boostVanishTries.Remove(kv.Key);
                    continue;
                }
                bool unopenable = state == BoostTarget.Unopenable;
                if (unopenable)
                {
                    int tries;
                    lock (sync)
                    {
                        boostVanishTries.TryGetValue(kv.Key, out tries);
                        tries++;
                        boostVanishTries[kv.Key] = tries;
                    }
                    if (tries < VanishGiveUpTries) continue;
                    abandoned++;
                }
                // 新 OpenProcess 即使已被反作弊拒绝，首次 hard pin 前保留的
                // handle 仍须先恢复并读回；活进程恢复失败时不能丢掉唯一恢复句柄。
                if (!RestoreIrqProofHardPin(IntPtr.Zero, kv.Key)) continue;
                if (!unopenable) CrashGuard.ReleaseBoostProcess(kv.Key, kv.Value.Creation);
                lock (sync)
                {
                    boostVanishTries.Remove(kv.Key);
                    gameBoost.Remove(kv.Key); gameGpu.Remove(kv.Key);
                    gamePlacement.Remove(kv.Key); gamePlacementStrict.Remove(kv.Key);
                    boostFail.Remove(kv.Key); boostStateWarned.Remove(kv.Key);
                    boostStateVerified.Remove(kv.Key); boostHandleStripped.Remove(kv.Key);
                    boostEcoGaveUp.Remove(kv.Key); placementGaveUp.Remove(kv.Key);
                    placementFail.Remove(kv.Key); boostStateFail.Remove(kv.Key);
                    gameBoostNextAudit.Remove(kv.Key); boostDenied.Remove(kv.Key);
                    tweakApplied.Remove(kv.Key);
                }
                dropped++;
            }
            if (dropped > 0)
                Logger.Log((abandoned > 0 ? Lang.T("log.gamemodeboost.58") : Lang.T("log.gamemodeboost.57")) + dropped);
            return dropped;
        }

        private void PruneDeadBoosts(HashSet<int> live)
        {
            lock (sync)
            {
                boostDenied.RemoveWhere(x => !live.Contains(x));
                boostStateWarned.RemoveWhere(x => !live.Contains(x));
                boostStateVerified.RemoveWhere(x => !live.Contains(x));
                boostHandleStripped.RemoveWhere(x => !live.Contains(x));
                boostEcoGaveUp.RemoveWhere(x => !live.Contains(x));
                placementGaveUp.RemoveWhere(x => !live.Contains(x));
                tweakApplied.RemoveWhere(x => !live.Contains(x));
                List<int> dead = null;
                foreach (int k in gameBoost.Keys)
                    if (!live.Contains(k)) { if (dead == null) dead = new List<int>(); dead.Add(k); }
                if (dead != null)
                    foreach (int k in dead)
                    {
                        if (!RestoreIrqProofHardPin(IntPtr.Zero, k)) continue;
                        Snap old = gameBoost[k];
                        CrashGuard.ReleaseBoostProcess(k, old.Creation);
                        gameBoost.Remove(k); gameGpu.Remove(k); gamePlacement.Remove(k); gamePlacementStrict.Remove(k);
                        boostFail.Remove(k); boostStateWarned.Remove(k); boostStateVerified.Remove(k);
                        gameBoostNextAudit.Remove(k); placementFail.Remove(k); boostStateFail.Remove(k);
                    }
            }
        }

        internal static bool RendererIdentityMatches(
            int expectedPid, long expectedCreation,
            string expectedName, int actualPid,
            long actualCreation, string actualName)
        {
            return expectedPid > 0
                && expectedPid == actualPid
                && expectedCreation > 0
                && expectedCreation == actualCreation
                && !string.IsNullOrEmpty(expectedName)
                && !string.IsNullOrEmpty(actualName)
                && string.Equals(
                    expectedName, actualName,
                    StringComparison.OrdinalIgnoreCase);
        }

        private void OnGameHandleStripped(int pid, string rendererName, uint granted)
        {
            string ac = KernelAntiCheat.Describe(rendererName);
            Logger.Log(Lang.T("log.gamemodeboost.3") + rendererName + " pid " + pid + Lang.T("log.gamemodeboost.34")
                + (ac == null ? Lang.T("nav.tame") : ac) + Lang.T("log.gamemodeboost.35") + granted.ToString("X")
                + Lang.T("log.gamemodeboost.36"));

            ProtectedGameRoster.Remember(rendererName);
            if (EffIfeo && EffBoost)
            {
                IfeoBoost.Arm(rendererName);
                IfeoBoost.EnsureForGame(rendererName);
            }
        }

        internal static bool ApplyAndVerifyBoostState(IntPtr process, out uint actualPriority, out int actualIo, out int error)
        {
            return ApplyAndVerifyBoostState(process, Native.HIGH_PRIORITY_CLASS, out actualPriority, out actualIo, out error);
        }

        internal static bool ApplyAndVerifyBoostState(IntPtr process, uint priorityTarget, out uint actualPriority, out int actualIo, out int error)
        {
            error = 0;
            actualIo = Native.QueryIoPriority(process);
            if (actualIo != 3)
            {
            if (!Native.EnsureBoostPrivilege()) error = 1314;
                else
                {
                    int status;
                    if (!Native.TrySetIoPriority(process, 3, out status)) error = status;
                }
            }

            actualPriority = Native.GetPriorityClass(process);
            if (actualPriority != priorityTarget && !Native.SetPriorityClass(process, priorityTarget))
                error = Marshal.GetLastWin32Error();

            actualPriority = Native.GetPriorityClass(process);
            actualIo = Native.QueryIoPriority(process);
            return actualPriority == priorityTarget && actualIo == 3;
        }

        private static string EcoStateText(IntPtr process)
        {
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state))
                return Lang.T("t.gamemodeboost.33") + QoSDump(process);
            return (Native.EcoClearedMasksOk(control, state)
                ? Lang.T("t.gamemodeboost.38") : Lang.T("t.gamemodeboost.33"))
                + QoSDump(process);
        }

        internal static string QoSDump(IntPtr process)
        {
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state)) return Lang.T("t.gamemodeboost.37");
            return "(control=0x" + control.ToString("X") + " state=0x" + state.ToString("X") + ")";
        }

        internal static bool HighQoSVerified(IntPtr process)
        {
            if (!Native.PowerThrottlingSupported) return true;
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state)) return false;
            return Native.HighQoSMasksOk(control, state);
        }

        private static bool ApplyAndVerifyGpuBoost(IntPtr process)
        {
            int current;
            if (Native.D3DKMTGetProcessSchedulingPriorityClass(process, out current) != 0) return false;
            if (current != Native.GpuPriorityHigh
                && Native.D3DKMTSetProcessSchedulingPriorityClass(process, Native.GpuPriorityHigh) != 0) return false;
            return Native.D3DKMTGetProcessSchedulingPriorityClass(process, out current) == 0
                && current == Native.GpuPriorityHigh;
        }

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
            List<KeyValuePair<int, Snap>> boosts;
            Dictionary<int, int> gpus;
            lock (sync)
            {
                if (gameBoost.Count == 0 && gameGpu.Count == 0) return true;
                boosts = new List<KeyValuePair<int, Snap>>();
                foreach (KeyValuePair<int, Snap> boosted in gameBoost)
                    if (!IsKeptBoost(boosted, keepPid, keepCreation, keepName))
                        boosts.Add(boosted);
                gpus = new Dictionary<int, int>();
                foreach (KeyValuePair<int, int> gpu in gameGpu)
                    if (keepPid <= 0 || gpu.Key != keepPid)
                        gpus[gpu.Key] = gpu.Value;
                if (boosts.Count == 0 && gpus.Count == 0) return true;
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
            // 新 OpenProcess 可能已被反作弊剥权；优先使用首次 hard pin 前
            // 留下的 pid+creation 绑定句柄恢复 affinity 并精确读回。失败时
            // 保留句柄，后面的普通恢复仍可尝试，不能提前丢失唯一恢复能力。
            foreach (var kv in boosts)
                RestoreIrqProofHardPin(IntPtr.Zero, kv.Key);
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
                            done = false;
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
                    // RestoreValues 已精确还原该 identity 的 affinity；关闭 retained
                    // handle 之前再移除其绑定，避免退出路径泄漏。
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
            lock (sync) return gameBoost.Count == 0;
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
            int cleared = SelfProtectedRoster.Clear();
            if (cleared > 0)
                Logger.Log(Lang.T("log.gamemodeboost.46") + cleared + Lang.T("log.gamemodeboost.47"));
            int unarmed = IfeoBoost.ClearArmed();
            if (unarmed > 0)
                Logger.Log(Lang.T("log.gamemodeboost.48") + unarmed + Lang.T("log.gamemodeboost.49"));
            bool ifeoOk = IrqMutationBoundary.Run<bool>(IfeoBoost.RestoreAll);
            int fusesCleared;
            lock (sync) { fusesCleared = envFused.Count; envFused.Clear(); }
            foreach (string envKey in EnvKeys)
                if (Settings.Load("EnvFuse_" + envKey, false)) Settings.Save("EnvFuse_" + envKey, false);
            SaveCounter(PowerFailStreakKey, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPState, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPreRender, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyAnsel, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyRebarFeat, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyDlssOvr, 0);
            if (fusesCleared > 0)
                Logger.Log(Lang.T("log.gamemodeboost.50") + fusesCleared + Lang.T("log.gamemodeboost.51"));
            lock (panicCallGate)
            {
                int mine = Interlocked.Increment(ref panicSeq);
                panicDone.Reset();
                panicResult = false;
                lock (sync)
                {
                    panicReq = true;
                    InvalidateRendererHandoff();
                }
                kick.Set();

                long deadline = DateTime.UtcNow.Ticks + 12000L * TimeSpan.TicksPerMillisecond;
                while (true)
                {
                    long left = (deadline - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
                    if (left <= 0) return false;
                    if (!panicDone.WaitOne((int)left)) return false;
                    if (Volatile.Read(ref panicServed) == mine) return panicResult && ifeoOk;
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
            InvalidateRendererHandoff();
            // 先封账再做任何恢复，避免把 Pavise 自己撤电源/核心/网络设置产生的 DPC
            // 记到刚结束的游戏里。ReportFinish 内部按 Present→DPC 收口。
            Exception reportFailure = null;
            try { ReportFinish(); }
            catch (Exception ex) { reportFailure = ex; }
            finally
            {
                // ReportFinish 任何中途异常都不能把两个 ETW 探针留到下一局。
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
            Interlocked.Exchange(ref boostFirstStampTicks, 0);
            Interlocked.Exchange(ref nvTweakRetryAtTicks, 0);
            preStagedNvPath = null;
            try { cpuLimit.Stop(); } catch { }
            Interlocked.Exchange(ref sessionStartTicks, 0);
            overlayScanned = false;
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
            // 维持旧语义：恢复动作已经完成后，再把原本会由 ReportFinish 抛出的异常交给上层。
            if (reportFailure != null) throw reportFailure;
            return clean && envClean && backgroundClean;
        }

    }
}
