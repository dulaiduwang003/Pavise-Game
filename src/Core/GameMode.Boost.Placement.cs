// @author bdth 2074055628@qq.com
// 文件用途 提优的放置阶段 能效模式解除 渲染车道接入与驱动微调
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
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
                // 默认 CPU Sets 会被线程显式选择覆盖 无法作为 IRQ 归因
                // proof 用户明确开启对局观测时 在单 group 机器上再
                // 叠加一层精确的临时进程硬亲和 写入前先复制当前可写句柄
                // 并与 pid+creation+原 affinity 绑定 即使反作弊随后拒绝新句柄
                // 仍可用这份 retained handle 恢复并读回 原值未知时绝不强写
                bool proofHardWritten = false;
                IntPtr proofRestoreHandle = IntPtr.Zero;
                if (soft && irqProbe.RequiresPlacementAudit
                    && !CpuTopology.MultiGroup
                    && pass.DesiredMask != allMask
                    && original.Aff != 0)
                    proofRestoreHandle = DuplicateIrqProofRestoreHandle(h);
                if (proofRestoreHandle != IntPtr.Zero)
                {
                    // Stop 可能在 worker.Join 超时后并发清理 hard write 与
                    // retained handle 登记必须处于同一锁域 要么 Stop 先令
                    // stopping 可见 本轮完全不写 要么先登记 Stop 随后必能恢复
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
                // 写入 API 返回成功仍不足以入账 最后再从进程句柄读回一次
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
                        Logger.Warn(Lang.T("log.gamemodeboost.3") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.25") + tries + Lang.T("log.gamemodeboost.26"));
                }
            }
            return ecoCleared;
        }

        private void EngageLaneAndReport(IntPtr h, ProcessSnapshot all, int pid, long currentCreation,
            BoostPass pass, bool stateOk, bool firstVerified, bool gpuOk, bool ecoCleared, string placementText)
        {
            if (EffLane && LaneEligible && pass.PriorityTarget == Native.HIGH_PRIORITY_CLASS
                && !pass.WriteDenied && !RenderLane.IsActiveFor(pid, currentCreation))
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
                    DlssMode = pass.NvDlss,
                    WindowedVrr = pass.NvVrr
                };
                bool nvRetry;
                List<string> nvFailed = NvDrsTweaks.ApplyForGame(imagePath, nvPlan, out nvRetry);
                if (!nvPlan.Empty) HandleNvTweakOutcome(nvFailed, nvPlan);
                if (!nvRetry) lock (sync) tweakApplied.Add(pid);
                else Interlocked.Exchange(ref nvTweakRetryAtTicks,
                    DateTime.UtcNow.AddSeconds(30).Ticks);
            }
        }
    }
}
