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
            bool placementUnreadable = false;
            if (pass.ManualPlacement && !EnsureManualIsolation(pid, pass.RendererCreation, pass.DesiredMask))
            {
                placementText = Lang.T("schedule.isolation.failed");
                return false;
            }
            // 手动落核读回不符且读得出 说明有别的进程改了亲和性
            //   守护开着 本轮当场重写 relapsed 记住这是一次被改回 保留改回前的读回值
            //   守护关着 保留它的改动 本局不再落核 改动后游戏够不着独占核才撤独占
            bool relapsed = false;
            ulong observedBefore = 0;
            if (!needPlacement)
            {
                placementVerified = PlacementMatches(h, pass, out placementUnreadable, out observedBefore);
                if (pass.ManualPlacement && pass.DesiredMask != allMask
                    && !placementVerified && !placementUnreadable && !pass.WriteDenied)
                {
                    bool tracked;
                    lock (sync)
                        tracked = !placementGaveUp.Contains(pid) && gameBoost.ContainsKey(pid);
                    if (tracked && !affinityGuardOn)
                    {
                        AcceptExternalPlacement(pid, observedBefore);
                        return true;
                    }
                    relapsed = tracked;
                    needPlacement = relapsed;
                }
            }
            ulong observedAfter = 0;
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
                if (!pass.ManualPlacement && (pass.UseStrict || pass.DesiredMask != allMask))
                    soft = Native.TrySetCpuSetsVerified(h, ids);
                // 默认 CPU Sets 会被线程显式选择覆盖 无法作为 IRQ 归因
                // proof 用户明确开启对局观测时 在单 group 机器上再
                // 叠加一层精确的临时进程硬亲和 写入前先复制当前可写句柄
                // 并与 pid+creation+原 affinity 绑定 即使反作弊随后拒绝新句柄
                // 仍可用这份 retained handle 恢复并读回 原值未知时绝不强写
                bool proofHardWritten = false;
                IntPtr proofRestoreHandle = IntPtr.Zero;
                if ((soft && irqProbe.RequiresPlacementAudit || pass.ManualPlacement && placementOk)
                    && !CpuTopology.MultiGroup
                    && pass.DesiredMask != allMask
                    && original.Aff != 0
                    && (!pass.ManualPlacement || (pass.DesiredMask & ~original.Aff) == 0))
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
                                pid, original.Creation,
                                original.Aff, proofRestoreHandle, pass.ManualPlacement);
                            proofRestoreHandle = IntPtr.Zero;
                            proofHardWritten = Native.QueryAffinity(h)
                                == pass.DesiredMask;
                        }
                    }
                    if (!proofHardWritten && proofRestoreHandle == IntPtr.Zero)
                        RestoreIrqProofHardPin(IntPtr.Zero, pid, true);
                }
                if (proofRestoreHandle != IntPtr.Zero)
                    Native.CloseHandle(proofRestoreHandle);
                if (pass.ManualPlacement && pass.DesiredMask != allMask)
                {
                    placementOk = placementOk && proofHardWritten;
                    placementText = Lang.T("t.gamemodeboost.18")
                        + CpuTopology.CountSetBits(pass.DesiredMask) + Lang.T("t.gamemodeboost.16");
                }
                else if (soft)
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
                placementVerified = PlacementMatches(h, pass, out placementUnreadable, out observedAfter);
                if (pass.DesiredMask != allMask
                    && !placementUnavailable && !placementVerified)
                    placementOk = false;
                if (pass.ManualPlacement && pass.DesiredMask != allMask && !placementVerified)
                    placementText = Lang.T("schedule.placement.failed");
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

                if (!newlyTracked && placementOk && !relapsed)
                    Logger.Log(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid + placementText);
            }
            if (!pass.ManualPlacement) return true;
            if (placementVerified && !relapsed)
            {
                lock (sync) isolationUnconfirmed.Remove(pid);
                return true;
            }
            if (placementUnreadable)
            {
                Logger.Log(Lang.T("log.isolation.placementunreadable"));
                return true;
            }
            if (relapsed && placementVerified)
            {
                // 被改回且已当场写回 不算失守 不设上限 只记次数 首次记日志 退局报总数
                int count;
                lock (sync)
                {
                    isolationRelapses.TryGetValue(pid, out count);
                    count++;
                    isolationRelapses[pid] = count;
                    isolationUnconfirmed.Remove(pid);
                }
                if (count == 1)
                    Logger.Log(Lang.F("log.isolation.placementcorrected", observedBefore.ToString("X")));
                return true;
            }
            // 重写后读回仍不对才算失守 连续到上限 游戏的亲和性仍盖住独占核就只停手 隔离保留
            //   盖不住才撤隔离 核被收走而游戏用不上比不隔离更糟
            int misses;
            lock (sync)
            {
                isolationUnconfirmed.TryGetValue(pid, out misses);
                misses++;
                isolationUnconfirmed[pid] = misses;
            }
            ulong external = relapsed ? observedBefore : observedAfter;
            PolicySnapshot snapshot = sessionPolicy;
            bool isolationOn = snapshot != null && snapshot.CorePlan.IsolationOn;
            bool covers = !isolationOn || CoversIsolation(external, snapshot.CorePlan.IsolationMask);
            switch (IsolationVerdictOf(covers, misses, PlacementRetryMax))
            {
                case IsolationVerdict.Retry:
                    Logger.Log(Lang.F("log.isolation.placementretry", misses, PlacementRetryMax));
                    break;
                case IsolationVerdict.StopCorrecting:
                    lock (sync)
                    {
                        placementGaveUp.Add(pid);
                        gamePlacement.Remove(pid); gamePlacementStrict.Remove(pid);
                        isolationUnconfirmed.Remove(pid);
                    }
                    Logger.Log(Lang.F("log.isolation.placementyield", external.ToString("X")));
                    break;
                case IsolationVerdict.Withdraw:
                    lock (sync) { gamePlacement.Remove(pid); gamePlacementStrict.Remove(pid); }
                    Logger.Log(Lang.F("log.isolation.placementlost", external.ToString("X")));
                    RollBackUnconfirmedIsolation();
                    break;
            }
            return true;
        }

        internal enum IsolationVerdict { Retry, StopCorrecting, Withdraw }

        // 游戏当前的亲和性是否还盖住整个独占区 盖住就说明独占核它够得着 隔离仍有意义
        internal static bool CoversIsolation(ulong observed, ulong isolationMask)
        {
            return isolationMask != 0 && observed != 0 && (observed & isolationMask) == isolationMask;
        }

        // 手动落核写入未生效后的处置 抽成纯函数是为了把边界钉进自测 读不出的轮次不进这里
        //   被改回且当场写回成功的轮次不进这里 那不算失守
        //   未到上限记重试 到上限 盖住独占区只停手 盖不住才撤
        internal static IsolationVerdict IsolationVerdictOf(bool coversIsolation, int consecutiveMisses, int max)
        {
            if (consecutiveMisses < max) return IsolationVerdict.Retry;
            return coversIsolation ? IsolationVerdict.StopCorrecting : IsolationVerdict.Withdraw;
        }

        // 守护关着 其他程序改了游戏亲和性就保留 本局不再落核 只记一条
        //   改动后游戏够不着独占核 独占就没意义 撤回
        private void AcceptExternalPlacement(int pid, ulong observed)
        {
            lock (sync)
            {
                placementGaveUp.Add(pid);
                gamePlacement.Remove(pid); gamePlacementStrict.Remove(pid);
                isolationUnconfirmed.Remove(pid);
            }
            PolicySnapshot snapshot = sessionPolicy;
            bool isolationOn = snapshot != null && snapshot.CorePlan.IsolationOn;
            if (!isolationOn || CoversIsolation(observed, snapshot.CorePlan.IsolationMask))
            {
                Logger.Log(Lang.F("log.isolation.placementaccepted", observed.ToString("X")));
                return;
            }
            Logger.Log(Lang.F("log.isolation.placementlost", observed.ToString("X")));
            RollBackUnconfirmedIsolation();
        }

        // 守护开着时每秒用首次硬钉时留存的句柄读一次亲和性 不新开句柄
        //   反作弊后来剥权也不影响 读到和期望不符就让本轮立刻进落核阶段写回
        private long affinityGuardCheckTicks;

        private bool ManualPlacementDrifted(int pid, BoostPass pass)
        {
            if (!affinityGuardOn || !pass.ManualPlacement || pass.DesiredMask == allMask || pass.WriteDenied
                || CpuTopology.MultiGroup || stopping) return false;
            long now = DateTime.UtcNow.Ticks;
            if (now - affinityGuardCheckTicks < TimeSpan.TicksPerSecond) return false;
            affinityGuardCheckTicks = now;
            lock (sync)
            {
                if (placementGaveUp.Contains(pid) || !gamePlacement.ContainsKey(pid)) return false;
                IrqProofHardPin pin;
                if (!irqProofHardPins.TryGetValue(pid, out pin) || !pin.Manual
                    || pin.Pid != pid || pin.RestoreHandle == IntPtr.Zero) return false;
                ulong affinity;
                return Native.TryQueryAffinity(pin.RestoreHandle, out affinity) && affinity != pass.DesiredMask;
            }
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
            if (pass.LaneAllowed && EffLane && LaneEligible && pass.PriorityTarget == Native.HIGH_PRIORITY_CLASS
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
