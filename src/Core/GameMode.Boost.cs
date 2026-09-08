// @author bdth 2074055628@qq.com
// 文件用途 游戏进程提优主流程 句柄获取 身份校验与状态写入
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
            public bool NvVrr;
            public bool UseStrict;
            public ulong DesiredMask;
            public int RendererPid;
            public long RendererCreation;
            public string RendererName;
            public bool WriteDenied;
            public uint PriorityTarget;
            public bool CpuSaturated;
            public long DomainGeneration;
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
            // 已开采 epoch 必须在恢复旧 renderer 之前先绑定同一份 proof
            // renderer 换代或配置换 mask 时 不能让 DropStale 的恢复动作混入旧局
            if (irqProbe.IsPlacementCapturing
                && !irqProbe.ProofMatches(
                    pass.DesiredMask,
                    pass.RendererPid, pass.RendererCreation))
                irqProbe.InvalidateGameMask();
            // 旧 renderer 的恢复也会触发调度/GPU 写入 本轮只要发现过
            // stale 状态 就不允许新的 IRQ epoch 起采 下轮确认已无
            // stale 后才能开始 给恢复写入留出完整的采样边界
            bool staleRestoreThisPass = DropStaleBoosts(pass);
            if (!irqProbe.RequiresPlacementAudit)
                RestoreOrphanedIrqProofHardPin(pass);
            if (irqProbe.IsPlacementCapturing)
            {
                // 采集中先只审计身份 硬亲和与调优状态 若确实需要写
                // AuditActiveIrqCapture 会先 RestartCurrentEpoch 并同步停掉旧 ETW
                // 然后才允许本轮落入 priority/IO/GPU/QoS/lane setter
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
                    bool known, needTweak, needPlacement;
                    bool auditDue = ComputeAuditDue(pid, pass, out known, out needTweak, out needPlacement);
                    bool placementAudit = irqProbe.RequiresPlacementAudit;
                    if (!auditDue && !placementAudit) continue;
                    IntPtr h = OpenBoostHandle(pid, pass);
                    if (h == IntPtr.Zero)
                    {
                        // 尚未开采时没有数据可被污染 保留 armed 等下一轮 只有已开采
                        // 后失去读回能力 才必须永久废弃本局 epoch
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

                        // 全机平均值看不见局部核域饿着 读回失败或者已经有 CPU Sets 时
                        // 当前渲染身份保守留在 Normal 收紧要赶在任何 priority 和 lane 写入之前
                        uint[] currentSets = Native.QueryCpuSets(h);
                        RecordBoostDomain(pass, Native.QueryAffinity(h), currentSets);

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
                                // 已开采的 epoch 只要观察到一次漂移便永久废弃 尚未开采时
                                // 清掉缓存 让本轮正常落核流程重新施加并读回验证
                                if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                                // 软 CPU Sets 可以是有效的普通落核 但不足以支撑
                                // IRQ 归因 只有普通读回也失败时才清缓存重写
                                // 否则保持 armed 等待 避免每 500ms 重复写 CPU Sets
                                if (!normalPlacement)
                                    lock (sync)
                                    {
                                        gamePlacement.Remove(pid);
                                        gamePlacementStrict.Remove(pid);
                                        needPlacement = !pass.WriteDenied
                                            && !placementGaveUp.Contains(pid);
                                    }
                                else if (!auditDue)
                                    // 普通软落核稳定 只是达不到 IRQ 归因的严格
                                    // proof 保留 armed 但不要因 placementAudit 在每次
                                    // 进程扫描里重跑整套 boost 读写
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
                        // 放置阶段会恢复原始核域 有可能是从崩溃恢复账本来的 这个值一样要过准入
                        // 不能这边刚提到 High 那边又把进程塞回受限核域
                        Snap originalDomain;
                        bool originalKnown;
                        lock (sync) originalKnown = gameBoost.TryGetValue(pid, out originalDomain)
                            && originalDomain.Creation == currentCreation;
                        RecordBoostDomain(pass, originalKnown ? originalDomain.Aff : 0UL,
                            originalKnown ? originalDomain.CpuSets : null);
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

                        // 首次 ETW 必须晚于本轮所有 Pavise 调优写入 随后再读回同一
                        // renderer 身份与落核 避免把初始化驱动/调度产生的 DPC 算成游戏证据
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
                    // 无法完成本轮身份/落核复核时 宁可丢弃整局中断样本
                    if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                }
            }
            if (!rendererSeen && irqProbe.IsPlacementCapturing)
                // 快照中渲染进程消失是正常退出/短暂漏检边界
                // 先封存但不落盘 后续确认退出才提交 恢复则丢前缀重开
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
            pass.NvVrr = nvVrrWindowedOn;
            pass.DesiredMask = EffectiveGameMask(sp, out pass.UseStrict);
            pass.RendererPid = -1;
            pass.RendererCreation = 0;
            pass.RendererName = null;
            lock (sync)
                if (activeDetection != null && activeDetection.RendererCandidateSelected)
                {
                    pass.RendererPid = activeDetection.RendererPid;
                    pass.RendererCreation =
                        activeDetection.RendererCreation;
                    pass.RendererName =
                        activeDetection.RendererName;
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
        private bool boostDomainRestricted;
        private bool boostDomainKnown;
        private int boostDomainPid;
        private long boostDomainCreation;
        private long boostDomainGeneration;
        private bool boostDomainNeedsAudit = true;
        private bool boostLaneReleasedForNormal;
        private bool boostPriorityDecidedKnown;

        internal static uint BoostPriorityTarget(bool saturated, bool laneActive)
        {
            // 有个 CPU 耗时候选 不等于饱和可以当没看见
            return saturated
                ? Native.NORMAL_PRIORITY_CLASS : Native.HIGH_PRIORITY_CLASS;
        }

        internal static uint BoostPriorityTarget(bool saturated, bool laneActive,
            ulong desiredMask, ulong allMask, bool domainRestricted)
        {
            // 整机空闲不能拿来替游戏可运行核域的余量作证
            return domainRestricted || allMask == 0 || desiredMask != allMask
                ? Native.NORMAL_PRIORITY_CLASS : BoostPriorityTarget(saturated, laneActive);
        }

        private void ResolvePriorityTarget(BoostPass pass)
        {
            pass.CpuSaturated = cpuSaturation.Update(cpuSaturation.Sample(), DateTime.UtcNow.Ticks);
            if (pass.RendererPid != boostDomainPid || pass.RendererCreation != boostDomainCreation)
                ResetBoostDomainEvidence();
            boostDomainPid = pass.RendererPid;
            boostDomainCreation = pass.RendererCreation;
            pass.DomainGeneration = boostDomainGeneration;
            // 同 PID 同创建时间的直接换局也算 上一局的审计缓存不能复用
            if (boostDomainNeedsAudit && pass.RendererPid > 0)
                lock (sync)
                {
                    boostStateVerified.Remove(pass.RendererPid);
                    gameBoostNextAudit.Remove(pass.RendererPid);
                    boostDomainNeedsAudit = false;
                }
            ResolveKnownDomainPriority(pass);
        }

        private void ResetBoostDomainEvidence()
        {
            boostDomainKnown = false;
            boostDomainRestricted = false;
            boostDomainPid = 0;
            boostDomainCreation = 0;
            boostDomainGeneration++;
            boostDomainNeedsAudit = true;
            boostLaneReleasedForNormal = false;
            boostPriorityDecidedKnown = false;
        }

        private void RecordBoostDomain(BoostPass pass, ulong affinity, uint[] cpuSets)
        {
            // 旧局或者旧渲染进程的迟到读回 不能拿来重新认证当前核域
            if (pass.DomainGeneration != boostDomainGeneration || pass.RendererPid <= 0
                || pass.RendererCreation <= 0 || pass.RendererPid != boostDomainPid
                || pass.RendererCreation != boostDomainCreation) return;
            boostDomainKnown = true;
            boostDomainRestricted |= allMask == 0 || affinity != allMask
                || cpuSets == null || cpuSets.Length != 0;
            ResolveKnownDomainPriority(pass);
        }

        private void ResolveKnownDomainPriority(BoostPass pass)
        {
            bool laneActive = pass.RendererPid > 0
                && RenderLane.IsActiveFor(pass.RendererPid, pass.RendererCreation);
            uint priorityTarget = BoostPriorityTarget(pass.CpuSaturated, laneActive,
                pass.DesiredMask, allMask, !boostDomainKnown || boostDomainRestricted || CpuTopology.MultiGroup);
            SetBoostPriorityTarget(pass, priorityTarget);
        }

        private void SetBoostPriorityTarget(BoostPass pass, uint priorityTarget)
        {
            if (priorityTarget == Native.NORMAL_PRIORITY_CLASS)
            {
                if (irqProbe.IsPlacementCapturing && priorityTarget != boostPriorityTarget)
                    irqProbe.InvalidateGameMask();
                // 取消成功后这个状态里不会再起 lane 别每次扫描都去重读恢复账本
                // 失败不能缓存成已恢复 后面的扫描和退局还得留着恢复机会
                if (!boostLaneReleasedForNormal)
                    boostLaneReleasedForNormal = RenderLane.Release();
            }
            else boostLaneReleasedForNormal = false;
            // 核域读回之前的首轮判定只是保守起步 不算一次真正的升降 不记日志
            bool previousDecidedKnown = boostPriorityDecidedKnown;
            boostPriorityDecidedKnown = boostDomainKnown;
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
                    if (previousDecidedKnown)
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
                // 已跟踪 GPU 原值时 后续 CaptureAndTrack 会再查并可能
                // 写回 High 预查失败不能 fail-open 否则该写入会落入
                // 已开始的 IRQ epoch
                lock (sync)
                    if (gameGpu.ContainsKey(pid)) return true;
            }
            bool ecoGaveUp;
            lock (sync) ecoGaveUp = boostEcoGaveUp.Contains(pid);
            if (!ecoGaveUp && !HighQoSVerified(h)) return true;
            if (EffLane && LaneEligible && pass.PriorityTarget == Native.HIGH_PRIORITY_CLASS)
            {
                LaneState lane = RenderLane.StateFor(pid, creation);
                if (IrqLaneNeedsInitialization(lane)) return true;
            }
            return false;
        }

        internal static bool IrqLaneNeedsInitialization(LaneState state)
        {
            // Trying 包含只读识别和每批之间一分钟的等待 不代表正在写
            // 真正的线程 setter 已由 Begin/EndExternalMutation 与起采共用门锁
            return state != LaneState.Trying && state != LaneState.Engaged
                && state != LaneState.Unavailable;
        }

        private IntPtr OpenBoostHandle(int pid, BoostPass pass)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                bool noSuchProcess = Native.LastOpenProcessFailureWasNoSuchProcess();
                // 后续保护名单处理可能写注册表 已有 IRQ capture 必须先停
                if (irqProbe.IsPlacementCapturing)
                {
                    // 已确认进程不存在是正常收口 不能把整局废弃
                    // 拒绝访问/身份不明才必须作废
                    if (noSuchProcess) SealIrqObservation();
                    else irqProbe.InvalidateGameMask();
                }
                bool firstDeny;
                lock (sync) firstDeny = boostDenied.Add(pid);
                if (firstDeny) Logger.Log(Lang.T("log.gamemodeboost.3") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.4"));
                if (!noSuchProcess)
                {
                    ProtectedGameRoster.Remember(pass.RendererName);
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
                Logger.Warn(Lang.T("log.gamemodeboost.5") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.6"));
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
                    Logger.Warn(Lang.T("log.gamemodeboost.9") + pass.RendererName + " pid " + pid);
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
                // 渲染身份确认/游戏库替换在调度前独立提交 不依赖提优句柄是否可写
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
            // stateOk 要求调度优先级和磁盘 IO 都到位 但这两件事的性质不同
            //   调度优先级是提优的主体 IO 优先级拿不到不算提优没生效
            //   写入被拒是反作弊保护游戏的预期结果 不是故障 与真的写不进去分开记
            //   否则日志里一行"提优失败"后面跟着 0x80 就是目标值 会让人以为出了问题
            bool prioOk = actualPriority == pass.PriorityTarget;
            bool writeRefused = writeError == 5 || writeError == unchecked((int)0xC0000022);
            if (!stateOk && firstStateWarning && !handleStripped)
            {
                if (writeRefused && !prioOk)
                {
                    string guard = KernelAntiCheat.Describe(pass.RendererName);
                    Logger.Log(Lang.T("log.gamemodeboost.3") + pass.RendererName + " pid " + pid
                        + Lang.T("log.gamemodeboost.62") + (guard == null ? Lang.T("nav.tame") : guard)
                        + Lang.T("log.gamemodeboost.63"));
                }
                else if (prioOk)
                    Logger.Log(Lang.T("log.gamemodeboost.3") + pass.RendererName + " pid " + pid
                        + Lang.T("log.gamemodeboost.64") + actualIo + Lang.T("log.gamemodeboost.65"));
                else
                    Logger.Log(Lang.T("log.gamemodeboost.11") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.12")
                        + actualPriority.ToString("X") + " / IO " + actualIo + Lang.T("log.gamemodeboost.13") + writeError + Lang.T("log.gamemodeboost.14"));
            }
            // 已经解释过原因的两种情况不再补一条"失败" 调度优先级本来就在 或写入被拒
            if (stateNowGaveUp && !handleStripped && !prioOk && !writeRefused)
                Logger.Log(Lang.T("log.gamemodeboost.11") + pass.RendererName + " pid " + pid
                    + Lang.T("log.gamemodeboost.55") + StateRetryMax + Lang.T("log.gamemodeboost.56"));
            return true;
        }
    }
}
