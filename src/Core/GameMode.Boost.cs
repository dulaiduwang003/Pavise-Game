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
        private sealed class BoostPass
        {
            public bool NvMaxPerf;
            public string NvLowLat;
            public bool NvSmooth;
            public bool NvShader;
            public bool NvRebar;
            public string NvDlss;
            public bool NvVrr;
            public bool Exclusive;
            public ulong DesiredMask;
            public int RendererPid;
            public long RendererCreation;
            public string RendererName;
            public string ActionTarget;
            public bool WriteDenied;
            public bool LaneAllowed;
            public uint PriorityTarget = Native.NORMAL_PRIORITY_CLASS;
            public bool CpuSaturated;
            public long IdentityGeneration;
            public string PlacementStage;
            public int PlacementError;
        }

        private void Boost(ProcessSnapshot all)
        {
            var live = new HashSet<int>();
            BoostPass pass = PrepareBoostPass();
            SyncFrameCoordination(pass);
            if (pass.Exclusive) pass.DesiredMask = coreGameMask;
            DropStaleBoosts(pass);
            RunIndependentCorePlacement(pass);
            foreach (ProcEntry p in all.Entries)
            {
                try
                {
                    int pid = p.Pid;
                    live.Add(pid);
                    if (pass.RendererPid <= 0 || pid != pass.RendererPid) continue;
                    bool known, needTweak, needPlacement;
                    bool auditDue = ComputeAuditDue(pid, pass, out known, out needTweak, out needPlacement);
                    if (!auditDue) continue;
                    IntPtr h = OpenBoostHandle(pid, pass);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        long currentCreation;
                        if (!VerifyRendererIdentity(h, pid, pass, out currentCreation)) continue;

                        if (!RefreshBoostPriority(pass)) continue;

                        HandlePidReuse(pid, currentCreation, ref known, ref needPlacement);
                        bool newlyTracked, gpuOk;
                        if (!CaptureAndTrack(h, pid, currentCreation, pass, known, out newlyTracked, out gpuOk)) continue;
                        bool stateOk, firstVerified;
                        if (!ApplyBoostStateStage(h, pid, pass, needTweak, out stateOk, out firstVerified)) continue;
                        bool placementVerified = pass.Exclusive && coreEngaged && !coreBackgroundOnly;
                        string placementText = placementVerified
                            ? Lang.T("t.gamemodeboost.18") + CpuTopology.CountSetBits(pass.DesiredMask) + Lang.T("t.gamemodeboost.16")
                            : Lang.T("t.gamemodeboost.17");
                        bool ecoCleared = ClearEfficiencyMode(h, pid, pass);
                        EngageLaneAndReport(h, pid, currentCreation, pass, stateOk, firstVerified, gpuOk, ecoCleared, placementText, placementVerified);
                        ApplyGameTweaks(h, pid, pass, needTweak);
                    }
                    finally { Native.CloseHandle(h); }
                }
                catch { }
            }
            PruneDeadBoosts(live);
        }

        private BoostPass PrepareBoostPass()
        {
            SyncNvFailureRetryGeneration();
            var pass = new BoostPass();
            PolicySnapshot sp = sessionPolicy;
            pass.NvMaxPerf = sp != null ? sp.NvMaxPerf : nvMaxPerf;
            pass.NvLowLat = sp != null ? sp.NvLowLatMode : nvLowLatMode;
            pass.NvSmooth = sp != null ? sp.NvSmoothMotion : nvSmoothMotion;
            pass.NvShader = sp != null ? sp.NvShaderCacheMax : nvShaderCacheMax;
            pass.NvRebar = sp != null ? sp.NvRebar : nvRebarOn;
            pass.NvDlss = sp != null ? sp.NvDlssMode : nvDlssMode;
            pass.NvVrr = nvVrrWindowedOn;
            if (AutomaticPolicy.Enabled)
            {
                NvGamePlan automatic = RefreshAutomaticNvPlan(pass.NvMaxPerf, pass.NvShader, Native.OnAcPower(), pass.NvLowLat, pass.NvVrr, pass.NvRebar);
                pass.NvMaxPerf = automatic.MaxPerf;
            }
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
            StepAutoCores(pass);
            AutoCorePlan plan = corePlan;
            AutoCoreGovernor governor = coreGovernor;
            pass.Exclusive = !coreObservationOnly && !CorePending && governor != null && governor.Exclusive && plan != null && plan.Available;
            pass.DesiredMask = pass.Exclusive ? coreGameMask : allMask;
            ResolvePriorityTarget(pass);
            return pass;
        }

        private readonly CpuSaturation cpuSaturation = new CpuSaturation();
        private uint boostPriorityTarget = Native.HIGH_PRIORITY_CLASS;
        private long boostFirstStampTicks;
        private int boostIdentityPid;
        private long boostIdentityCreation;
        private long boostIdentityGeneration;
        private bool boostIdentityNeedsAudit = true;
        private bool boostLaneReleased;
        private bool boostPriorityDecided;

        internal static uint BoostPriorityTarget(bool saturated, bool laneActive)
        {
            return saturated && !laneActive
                ? Native.NORMAL_PRIORITY_CLASS : Native.HIGH_PRIORITY_CLASS;
        }

        private void ResolvePriorityTarget(BoostPass pass)
        {
            pass.CpuSaturated = cpuSaturation.Update(cpuSaturation.Sample(), DateTime.UtcNow.Ticks);
            if (pass.RendererPid != boostIdentityPid || pass.RendererCreation != boostIdentityCreation)
                ResetBoostIdentity();
            boostIdentityPid = pass.RendererPid;
            boostIdentityCreation = pass.RendererCreation;
            pass.IdentityGeneration = boostIdentityGeneration;
            // 同 PID 的直接换局也不能复用旧局审计缓存。
            if (boostIdentityNeedsAudit && pass.RendererPid > 0)
                lock (sync)
                {
                    boostStateVerified.Remove(pass.RendererPid);
                    gameBoostNextAudit.Remove(pass.RendererPid);
                    boostIdentityNeedsAudit = false;
                }
            RefreshBoostPriority(pass);
        }

        private void ResetBoostIdentity()
        {
            boostIdentityPid = 0;
            boostIdentityCreation = 0;
            boostIdentityGeneration++;
            boostIdentityNeedsAudit = true;
            boostLaneReleased = false;
            boostPriorityDecided = false;
        }

        private bool RefreshBoostPriority(BoostPass pass)
        {
            if (pass.IdentityGeneration != boostIdentityGeneration || pass.RendererPid <= 0
                || pass.RendererCreation <= 0 || pass.RendererPid != boostIdentityPid
                || pass.RendererCreation != boostIdentityCreation) return false;
            pass.LaneAllowed = EffLane && LaneEligible && !pass.WriteDenied;
            bool laneActive = pass.LaneAllowed
                && RenderLane.IsActiveFor(pass.RendererPid, pass.RendererCreation);
            SetBoostPriorityTarget(pass, BoostPriorityTarget(pass.CpuSaturated, laneActive));
            return true;
        }

        private void SetBoostPriorityTarget(BoostPass pass, uint priorityTarget)
        {
            if (priorityTarget == Native.NORMAL_PRIORITY_CLASS || !pass.LaneAllowed)
            {
                // 恢复失败不能缓存为已恢复。
                if (!boostLaneReleased)
                    boostLaneReleased = RenderLane.Release();
            }
            else boostLaneReleased = false;
            bool previousDecided = boostPriorityDecided;
            boostPriorityDecided = true;
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
                    if (previousDecided)
                        Logger.Log(priorityTarget == Native.NORMAL_PRIORITY_CLASS
                            ? Lang.T("log.boostsat.1") : Lang.T("log.boostsat.2"));
                }
            }
            pass.PriorityTarget = priorityTarget;
        }

        private void DropStaleBoosts(BoostPass pass)
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
            if (!staleBoost) return;
            UnboostGames(pass.RendererPid, pass.RendererCreation, pass.RendererName);
        }

        private bool ComputeAuditDue(int pid, BoostPass pass,
            out bool known, out bool needTweak, out bool needPlacement)
        {
            bool explicitRetry = HasBoostActionRetry(pid);
            bool auditDue;
            lock (sync)
            {
                known = gameBoost.ContainsKey(pid);
                needTweak = !tweakApplied.Contains(pid);
                ulong placed;
                needPlacement = pass.Exclusive && !placementGaveUp.Contains(pid)
                    && (!gamePlacement.TryGetValue(pid, out placed) || placed != pass.DesiredMask);
                long nextAudit;
                auditDue = !known || explicitRetry || needTweak || needPlacement
                    || !gameBoostNextAudit.TryGetValue(pid, out nextAudit)
                    || DateTime.UtcNow.Ticks >= nextAudit;
            }
            return auditDue;
        }

        private IntPtr OpenBoostHandle(int pid, BoostPass pass)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                bool noSuchProcess = Native.LastOpenProcessFailureWasNoSuchProcess();
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
                        boostFail.Remove(pid);
                        ForgetHardPin(pid);
                        boostStateWarned.Remove(pid); boostStateVerified.Remove(pid);
                        gameBoostNextAudit.Remove(pid); boostStateFail.Remove(pid);
                        boostHandleStripped.Remove(pid); boostEcoGaveUp.Remove(pid);
                        placementFail.Remove(pid); placementGaveUp.Remove(pid);
                        tweakApplied.Remove(pid); reused = true; known = false;
                        ForgetBoostActionTarget(pid);
                    }
                if (reused)
                {
                    needPlacement = true;
                    CrashGuard.ReleaseBoostProcess(pid, tracked.Creation);
                }
            }
        }

        private bool CaptureAndTrack(IntPtr h, int pid, long currentCreation, BoostPass pass,
            bool known, out bool newlyTracked, out bool gpuOk, bool applyGpu = true)
        {
            newlyTracked = false;
            gpuOk = false;
            pass.ActionTarget = BoostActionTarget(h);
            if (!known)
            {
                uint pri = Native.GetPriorityClass(h);
                if (pri == 0) return false;
                ulong oaff = Native.QueryAffinity(h);
                uint[] ocpuSets = Native.QueryCpuSets(h);
                if (ocpuSets == null)
                {
                    Logger.Warn(Lang.T("log.gamemodeboost.9") + pass.RendererName + " pid " + pid);
                    return false;
                }
                int oio = Native.QueryIoPriority(h);
                int opg = Native.QueryPagePriority(h);
                if (oio < 0 || opg < 0) return false;
                int gpuOld;
                bool gpuKnown = Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuOld) == 0;
                if (!gpuKnown) gpuOld = -1;

                int oqc, oqs;
                if (!Native.TryQueryPowerThrottling(h, out oqc, out oqs))
                {
                    if (Native.PowerThrottlingSupported) return false;
                    oqc = -1; oqs = -1;
                }
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
                lock (sync)
                {
                    gameBoost[pid] = snap;
                    if (!string.IsNullOrEmpty(pass.ActionTarget)) boostActionTargets[pid] = pass.ActionTarget;
                    if (gpuKnown) gameGpu[pid] = gpuOld;
                }
                newlyTracked = true;
            }
            else
            {
                lock (sync)
                    if (!string.IsNullOrEmpty(pass.ActionTarget)) boostActionTargets[pid] = pass.ActionTarget;
            }
            uint grantedAccess;
            if (Native.HandleWriteAccessStripped(h, out grantedAccess))
            {
                bool first;
                lock (sync) first = boostHandleStripped.Add(pid);
                pass.WriteDenied = true;
                if (first) OnGameHandleStripped(pid, pass.RendererName, grantedAccess);
            }
            if (applyGpu)
                gpuOk = ApplyIsolatedBoostAction(h, pid, pass, GameGpuAction,
                    delegate(long value) { return value == Native.GpuPriorityHigh; },
                    delegate { return WriteBoostAction(GameGpuAction, h, Native.GpuPriorityHigh); });
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
                    gameBoostNextAudit[pid] = DateTime.UtcNow.AddSeconds(20 + Math.Abs(pid % 11)).Ticks;
                }
                return true;
            }
            bool priorityOk = ApplyIsolatedBoostAction(h, pid, pass, GamePriorityAction,
                delegate(long value) { return value == pass.PriorityTarget; },
                delegate { return WriteBoostAction(GamePriorityAction, h, pass.PriorityTarget); });
            bool ioOk = ApplyIsolatedBoostAction(h, pid, pass, GameIoAction,
                delegate(long value) { return value == 3; },
                delegate { return WriteBoostAction(GameIoAction, h, 3); });
            stateOk = priorityOk && ioOk;
            lock (sync)
            {
                if (stateOk)
                {
                    firstVerified = boostStateVerified.Add(pid);
                    boostStateWarned.Remove(pid);
                }
                else boostStateVerified.Remove(pid);
                boostStateFail.Remove(pid);
                gameBoostNextAudit[pid] = DateTime.UtcNow.AddSeconds(20 + Math.Abs(pid % 11)).Ticks;
            }
            return true;
        }
    }
}
