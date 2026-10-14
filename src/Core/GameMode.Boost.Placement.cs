// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void RunIndependentCorePlacement(BoostPass pass)
        {
            long started = AutoCores.NowTicks;
            try { RunIndependentCorePlacementImpl(pass); }
            finally { coreBudget.Record(started); }
        }

        private void RunIndependentCorePlacementImpl(BoostPass pass)
        {
            if (coreManual == null || pass == null || !pass.Exclusive || CorePending || stopping) return;
            IntPtr handle = IntPtr.Zero;
            try
            {
                if (coreManualPlacementYielded && coreEngaged)
                { AuditManualIsolationCoverage(pass); return; }
                ulong observed;
                if (ExclusiveDrifted(pass.RendererPid, pass, out observed))
                {
                    YieldExclusive(pass.RendererPid, observed);
                    return;
                }
                if (coreEngaged) return; // 不自动扩大手动选择的核心范围。
                if (RendererForegroundPid() != pass.RendererPid)
                {
                    if (coreGovernor != null) ApplyAutoCoreStep(coreGovernor.Pause("schedule.auto.foreground", AutoCores.NowTicks));
                    return;
                }
                handle =
#if PAVISE_SELFTEST
                    CorePlacementOpenForTest != null ? CorePlacementOpenForTest(pass.RendererPid) :
#endif
                    Native.OpenAffinityProcess(pass.RendererPid);
                int openError = handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                if (handle == IntPtr.Zero)
                {
                    LogCorePlacement(pass, "open", openError, handle);
                    StartBackgroundOnly();
                    return;
                }
                long creation;
                if (!VerifyRendererIdentity(handle, pass.RendererPid, pass, out creation))
                {
                    LogCorePlacement(pass, "identity", 0, handle);
                    BlockAutoCores("schedule.auto.identity");
                    return;
                }
                uint granted;
                if (Native.TryQueryGrantedAccess(handle, out granted) && (granted & Native.PROCESS_SET_INFORMATION) == 0)
                {
                    LogCorePlacement(pass, "access", 5, handle);
                    StartBackgroundOnly();
                    return;
                }
                bool known, needPlacement = true;
                lock (sync) known = gameBoost.ContainsKey(pass.RendererPid);
                HandlePidReuse(pass.RendererPid, creation, ref known, ref needPlacement);
                bool newlyTracked, gpuOk;
                if (!CaptureAndTrack(handle, pass.RendererPid, creation, pass, known, out newlyTracked, out gpuOk, false))
                {
                    LogCorePlacement(pass, "snapshot", 0, handle);
                    BlockAutoCores("schedule.auto.capture");
                    return;
                }
                string text; bool verified;
                if (!ApplyPlacementStage(handle, pass.RendererPid, pass, true, newlyTracked, out text, out verified))
                    BlockAutoCores("schedule.auto.capture");
                if (verified) LogCorePlacement(pass, "verified", 0, handle);
            }
            catch (Exception error)
            {
                Logger.Warn("AutoCore placement exception pid=" + pass.RendererPid + " type=" + error.GetType().Name);
                BlockAutoCores("schedule.auto.placement");
            }
            finally
            {
                if (handle != IntPtr.Zero) Native.CloseHandle(handle);
                pass.Exclusive = coreGovernor != null && coreGovernor.Exclusive && !CorePending;
                if (!pass.Exclusive) pass.DesiredMask = allMask;
                PublishAutoCores(pass.RendererName);
            }
        }

        private void LogCorePlacement(BoostPass pass, string stage, int error, IntPtr handle)
        {
            pass.PlacementStage = stage;
            pass.PlacementError = error;
            uint granted;
            string rights = Native.TryQueryGrantedAccess(handle, out granted) ? "0x" + granted.ToString("X") : "unknown";
            ulong current;
            string actual = handle != IntPtr.Zero && Native.TryQueryAffinity(handle, out current) ? "0x" + current.ToString("X") : "unknown";
            ulong original = 0;
            lock (sync) { Snap snap; if (gameBoost.TryGetValue(pass.RendererPid, out snap)) original = snap.Aff; }
            corePlacementEvidence = "pid=" + pass.RendererPid + " creation=" + pass.RendererCreation
                + " stage=" + stage + " win32=" + error + " granted=" + rights
                + " original=0x" + original.ToString("X") + " actual=" + actual
                + " target=0x" + pass.DesiredMask.ToString("X") + " cachedBoostRestriction=" + pass.WriteDenied;
        }

        private static string PlacementFailureReason(BoostPass pass)
        {
            switch (pass.PlacementStage)
            {
                case "affinity-range": return "schedule.auto.affinityrange";
                case "journal": return "schedule.auto.capture";
                case "restore-handle": return "schedule.auto.restorehandle";
                case "read-original": case "read-placement": case "readback": return "schedule.auto.affinityread";
                case "set-affinity": return "schedule.auto.affinitywrite";
                default: return "schedule.auto.placement";
            }
        }

        private bool ApplyPlacementStage(IntPtr h, int pid, BoostPass pass, bool needPlacement,
            bool newlyTracked, out string placementText, out bool placementVerified)
        {
            placementText = "";
            placementVerified = false;
            bool placementUnreadable = false;
            ulong observed = 0;
            if (!needPlacement)
            {
                placementVerified = PlacementMatches(h, pass, out placementUnreadable, out observed);
                if (pass.Exclusive && !placementVerified && !placementUnreadable)
                {
                    bool tracked;
                    lock (sync) tracked = !placementGaveUp.Contains(pid) && gameBoost.ContainsKey(pid);
                    if (tracked) YieldExclusive(pid, observed);
                }
                return true;
            }
            if (pass.Exclusive && RendererForegroundPid() != pid)
            {
                if (coreGovernor != null)
                    ApplyAutoCoreStep(coreGovernor.Pause("schedule.auto.foreground", AutoCores.NowTicks));
                PublishAutoCores(pass.RendererName);
                return true;
            }
            Snap original;
            lock (sync) { if (!gameBoost.TryGetValue(pid, out original)) return false; }
            if (pass.Exclusive)
            {
                bool unreadable;
                bool changedOutside = PlacementChangedOutside(h, original, out observed, out unreadable);
                if (unreadable)
                {
                    LogCorePlacement(pass, "read-placement", 0, h);
                    BlockAutoCores("schedule.auto.affinityread");
                    return true;
                }
                if (changedOutside) { YieldExclusive(pid, observed); return true; }
            }

            bool placementOk = true;
            if (pass.Exclusive)
            {
                // 核心准入被拒绝时保持游戏亲和性不变。
                if (coreManual != null && coreManual.Isolation && !EngageIsolation(pid, pass))
                {
                    BlockAutoCores("schedule.auto.failed");
                    return true;
                }
                bool pinned = placementOk && PinExclusive(h, pid, pass, original);
                if (!pinned && pass.PlacementStage == "set-affinity" && pass.PlacementError == 5)
                {
                    if (!Native.TryQueryAffinity(h, out observed) || observed != original.Aff)
                        BlockAutoCores("schedule.auto.affinityread");
                    else if (!CrashGuard.TrackBoostAffinity(pid, original.Creation, 0))
                        BlockAutoCores("schedule.auto.capture");
                    else StartBackgroundOnly();
                    return true;
                }
                placementVerified = pinned && PlacementMatches(h, pass, out placementUnreadable, out observed);
                string failure = !placementVerified ? PlacementFailureReason(pass)
                    : !EngageIsolation(pid, pass) ? "schedule.auto.failed" : null;
                if (failure != null)
                {
                    RestoreHardPin(pid);
                    lock (sync) gamePlacement.Remove(pid);
                    placementVerified = false;
                    BlockAutoCores(failure);
                    return true;
                }
                placementText = Lang.T("t.gamemodeboost.18") + CpuTopology.CountSetBits(pass.DesiredMask)
                    + Lang.T("t.gamemodeboost.16");
            }
            else
            {
                placementText = Lang.T("t.gamemodeboost.17");
                placementVerified = PlacementMatches(h, pass, out placementUnreadable, out observed);
                if (!placementVerified) placementOk = false;
            }
            int placeTries = 0;
            bool placementNowGaveUp = false, firstPlacementWarning = false;
            lock (sync)
            {
                if (placementOk)
                {
                    gamePlacement[pid] = pass.DesiredMask;
                    placementFail.Remove(pid); placementGaveUp.Remove(pid);
                }
                else
                {
                    gamePlacement.Remove(pid);
                    placementFail.TryGetValue(pid, out placeTries); placeTries++;
                    if (placeTries >= PlacementRetryMax)
                    {
                        placementFail.Remove(pid);
                        placementNowGaveUp = placementGaveUp.Add(pid);
                    }
                    else { placementFail[pid] = placeTries; firstPlacementWarning = placeTries == 1; }
                }
            }
            if (placementNowGaveUp || !placementOk && firstPlacementWarning)
                Logger.Log(PlacementWarningLine(pass.RendererName, pid, placementUnreadable, placementNowGaveUp));
            if (!newlyTracked && placementOk)
                Logger.Log(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid + placementText);
            return true;
        }

        private bool ClearEfficiencyMode(IntPtr h, int pid, BoostPass pass)
        {
            if (!Native.PowerThrottlingSupported) return true;
            return ApplyIsolatedBoostAction(h, pid, pass, GameEcoAction,
                delegate(long value) { return Native.HighQoSMasksOk((int)(value >> 32), (int)value); },
                delegate { return WriteHighQoSBoost(h); });
        }

        private void EngageLaneAndReport(IntPtr h, int pid, long currentCreation,
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

        internal static string PlacementWarningLine(string renderer, int pid, bool unreadable, bool gaveUp)
        {
            return Logger.WarnTag + Lang.T("log.gamemodeboost.23")
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
                if (AutomaticPolicy.Enabled)
                    nvPlan = NvGamePlan.ForAutomatic(pass.NvMaxPerf, pass.NvShader, Native.OnAcPower(), pass.NvLowLat, pass.NvVrr, pass.NvRebar);
                bool nvRetry;
                List<string> nvFailed = ApplyNvGamePlan(imagePath, nvPlan, out nvRetry);
                if (!nvPlan.Empty) HandleNvTweakOutcome(nvFailed, nvPlan);
                nvRetry |= nvFailed != null && nvFailed.Count > 0;
                if (!nvRetry) lock (sync) tweakApplied.Add(pid);
                else Interlocked.Exchange(ref nvTweakRetryAtTicks,
                    DateTime.UtcNow.AddSeconds(30).Ticks);
            }
        }
    }
}
