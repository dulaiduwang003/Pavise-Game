// @author bdth 2074055628@qq.com
// File purpose Placement stage of the boost, efficiency mode clearing, render lane engagement and driver tweaks
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
            // Manual placement read-back mismatch while readable means another process changed the affinity
            //   Guard on: rewrite on the spot this pass, relapsed records that it was changed back and keeps the pre-change read-back value
            //   Guard off: keep its change, no more placement this match, withdraw exclusive only if the game can no longer reach the exclusive cores after the change
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
                // Default CPU Sets get overridden by explicit thread choices and can't serve as IRQ attribution
                // proof, when the user explicitly enables match observation, on single-group machines
                // stack an extra exact temporary process hard affinity, duplicating the current writable handle first
                // and binding it to pid+creation+original affinity, so even if anti-cheat later denies new handles
                // this retained handle can still restore and read back, never force-write when the original is unknown
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
                    // Stop may clean up concurrently after worker.Join times out, the hard write and
                    // retained handle registration must share one lock scope: either Stop makes
                    // stopping visible first and this pass writes nothing, or registration lands first and Stop can then always restore
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
                // A successful write API return still isn't enough to book it, read back from the process handle one last time
                placementVerified = PlacementMatches(h, pass, out placementUnreadable, out observedAfter);
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
                    Logger.Warn(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid
                        + Lang.T("log.gamemodeboost.20"));
                else if (placementNowGaveUp || !placementOk && firstPlacementWarning)
                    Logger.Log(PlacementWarningLine(pass.RendererName, pid, pass.ManualPlacement,
                        placementUnreadable, placementNowGaveUp));

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
                // Changed back and rewritten on the spot doesn't count as lost, no cap, just count, log the first time and report the total at match end
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
            // Only a read-back still wrong after the rewrite counts as lost, once consecutive misses hit the cap, if the game's affinity still covers the exclusive cores just stop correcting and keep isolation
            //   Withdraw isolation only when it doesn't cover them, cores taken away that the game can't use is worse than no isolation
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

        // Whether the game's current affinity still covers the whole exclusive range, if so the game can reach the exclusive cores and isolation still makes sense
        internal static bool CoversIsolation(ulong observed, ulong isolationMask)
        {
            return isolationMask != 0 && observed != 0 && (observed & isolationMask) == isolationMask;
        }

        // Handling after a manual placement write fails to take effect, pulled into a pure function to pin the boundary in self-tests, unreadable passes don't come here
        //   Passes changed back and successfully rewritten on the spot don't come here either, that doesn't count as lost
        //   Below the cap record a retry, at the cap stop correcting if the exclusive range is covered, withdraw only if not
        internal static IsolationVerdict IsolationVerdictOf(bool coversIsolation, int consecutiveMisses, int max)
        {
            if (consecutiveMisses < max) return IsolationVerdict.Retry;
            return coversIsolation ? IsolationVerdict.StopCorrecting : IsolationVerdict.Withdraw;
        }

        // Guard off: if another program changed the game's affinity keep it, no more placement this match, log once
        //   If the game can't reach the exclusive cores after the change, exclusivity is pointless, withdraw it
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

        // With the guard on, read affinity once per second via the handle retained at the first hard pin, no new handle opened
        //   Later anti-cheat privilege stripping doesn't matter, a read-back mismatch sends this pass straight into the placement stage to rewrite
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
            BoostPass pass, bool stateOk, bool firstVerified, bool gpuOk, bool ecoCleared,
            string placementText, bool placementVerified)
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
                Logger.Log(BoostSuccessLine(pass.RendererName, pid,
                    pass.PriorityTarget == Native.HIGH_PRIORITY_CLASS ? Lang.T("log.gamemodeboost.28") : Lang.T("log.gamemodeboost.29"),
                    placementText, placementVerified, Lang.T("log.gamemodeboost.30")
                    + (gpuOk ? Lang.T("log.gamemodeboost.31") : "")
                    + (!Native.PowerThrottlingSupported ? ""
                        : ecoCleared ? Lang.T("t.gamemodeboost.32") : EcoStateText(h))));
            }
        }

        internal static string BoostSuccessLine(string renderer, int pid, string priority,
            string placement, bool placementVerified, string otherStates)
        {
            return Logger.SuccessTag + Lang.T("log.gamemodeboost.27") + renderer + "(pid " + pid + ") "
                + priority + (placementVerified ? placement : "") + otherStates;
        }

        internal static string PlacementWarningLine(string renderer, int pid, bool manual,
            bool unreadable, bool gaveUp)
        {
            return Logger.WarnTag + Lang.T(manual ? "schedule.placement.unconfirmed" : "log.gamemodeboost.23")
                + renderer + " pid " + pid + " "
                + Lang.T(unreadable ? "log.placement.unreadable" : "log.placement.unconfirmed")
                + Lang.T(gaveUp ? "log.placement.stopped" : "log.gamemodeboost.24");
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
