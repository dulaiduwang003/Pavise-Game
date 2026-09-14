// @author bdth 2074055628@qq.com
// File purpose Main game process boost flow, handle acquisition, identity verification and state writes
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
            public bool ManualPlacement;
            public ulong DesiredMask;
            public int RendererPid;
            public long RendererCreation;
            public string RendererName;
            public bool WriteDenied;
            public bool LaneAllowed;
            public uint PriorityTarget = Native.NORMAL_PRIORITY_CLASS;
            public bool CpuSaturated;
            public long IdentityGeneration;
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
            // An epoch already capturing must bind to the same proof before the old renderer is restored
            // When the renderer changes generation or config changes the mask, DropStale's restore actions must not mix into the old match
            if (irqProbe.IsPlacementCapturing
                && !irqProbe.ProofMatches(
                    pass.DesiredMask,
                    pass.RendererPid, pass.RendererCreation))
                irqProbe.InvalidateGameMask();
            // Restoring the old renderer also triggers scheduling/GPU writes, once any stale state
            // is seen this pass no new IRQ epoch may start capturing, it starts only after the next pass confirms
            // no stale remains, giving the restore writes a complete sampling boundary
            bool staleRestoreThisPass = DropStaleBoosts(pass);
            if (!irqProbe.RequiresPlacementAudit)
                RestoreOrphanedIrqProofHardPin(pass);
            if (irqProbe.IsPlacementCapturing)
            {
                // During capture only audit identity, hard affinity and tuning state first, if a write is really needed
                // AuditActiveIrqCapture first calls RestartCurrentEpoch and synchronously stops the old ETW
                // only then may this pass fall through to the priority/IO/GPU/QoS/lane setters
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
                    // Manual placement reads affinity once per second via the retained handle, if changed back it goes straight into this pass's rewrite without waiting for the audit period
                    if (!auditDue && ManualPlacementDrifted(pid, pass)) auditDue = true;
                    bool placementAudit = irqProbe.RequiresPlacementAudit;
                    if (!auditDue && !placementAudit) continue;
                    IntPtr h = OpenBoostHandle(pid, pass);
                    if (h == IntPtr.Zero)
                    {
                        // Before capture starts there's no data to pollute, stay armed and wait for the next pass, only losing read-back
                        // ability after capture started forces permanent abandonment of this match's epoch
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

                        // Core placement no longer decides boost eligibility, but a pass from the previous match or an old renderer must not keep writing
                        if (!RefreshBoostPriority(pass)) continue;

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
                                // An epoch already capturing is permanently abandoned on the first observed drift, before capture starts
                                // clear the cache and let this pass's normal placement flow reapply and verify by read-back
                                if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                                // Soft CPU Sets can be valid ordinary placement but aren't enough to support
                                // IRQ attribution, clear the cache and rewrite only when the ordinary read-back fails too
                                // otherwise stay armed and wait, avoiding a CPU Sets rewrite every 500ms
                                if (!normalPlacement)
                                    lock (sync)
                                    {
                                        gamePlacement.Remove(pid);
                                        gamePlacementStrict.Remove(pid);
                                        needPlacement = !pass.WriteDenied
                                            && !placementGaveUp.Contains(pid);
                                    }
                                else if (!auditDue)
                                    // Ordinary soft placement is stable, it just doesn't meet the strict IRQ attribution
                                    // proof, stay armed but don't let placementAudit rerun the whole set of
                                    // boost reads and writes on every process scan
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
                        EngageLaneAndReport(h, all, pid, currentCreation, pass, stateOk, firstVerified, gpuOk, ecoCleared, placementText, placementVerified);
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

                        // The first ETW must come after all of this pass's Pavise tuning writes, then read back the same
                        // renderer identity and placement, so DPCs from driver/scheduler initialization aren't counted as game evidence
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
                    // If this pass's identity/placement re-check can't complete, rather discard the whole match's interrupt samples
                    if (irqProbe.IsPlacementCapturing) irqProbe.InvalidateGameMask();
                }
            }
            if (!rendererSeen && irqProbe.IsPlacementCapturing)
                // A renderer vanishing from the snapshot is the normal exit/brief missed-detection boundary
                // Seal first without persisting, commit only once exit is confirmed, if it comes back drop the prefix and restart
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
            pass.ManualPlacement = sp != null ? sp.ManualPlacement : CoreScheduling.HasGlobalRecord();
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
            if (sp != null && sp.ManualPlacement)
            {
                ulong selected = sp.CoreMask;
                // A saved plan whose mask can't be resolved most likely has a topology stamp mismatch, this machine's topology changed
                //   It then silently falls back to no core restriction and the user's core selection is wasted, must say so once
                if (selected == 0 && !placementVoidLogged)
                {
                    placementVoidLogged = true;
                    Logger.Warn(Lang.T("log.placement.void"));
                }
                useStrict = selected != 0 && selected != allMask;
                return selected != 0 ? selected : allMask;
            }
            ulong customMask = CpuTopology.CustomMask;
            useStrict = customMask != 0
                || ShouldUseCorePartition(sp != null ? sp.StrictCores : corePartitionOn,
                    CpuTopology.HasSafeBackgroundPartition());
            return customMask != 0 ? customMask : useStrict ? strictMask : gameMask;
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
            // Restores the old rule: keep High when candidate thread boost is enabled, otherwise fall back under sustained saturation
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
            // A direct match switch with the same PID and creation time counts too, the previous match's audit cache can't be reused
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
            // Only verify match generation and process identity, affinity / CPU Sets readings are not a boost gate
            if (pass.IdentityGeneration != boostIdentityGeneration || pass.RendererPid <= 0
                || pass.RendererCreation <= 0 || pass.RendererPid != boostIdentityPid
                || pass.RendererCreation != boostIdentityCreation) return false;
            pass.LaneAllowed = EffLane && LaneEligible && !pass.WriteDenied;
            // After switching to Handheld tier or turning the switch off, a leftover lane must not exempt the saturation fallback even if restore temporarily failed
            bool laneActive = pass.LaneAllowed
                && RenderLane.IsActiveFor(pass.RendererPid, pass.RendererCreation);
            SetBoostPriorityTarget(pass, BoostPriorityTarget(pass.CpuSaturated, laneActive));
            return true;
        }

        private void SetBoostPriorityTarget(BoostPass pass, uint priorityTarget)
        {
            if (priorityTarget == Native.NORMAL_PRIORITY_CLASS || !pass.LaneAllowed)
            {
                if (priorityTarget == Native.NORMAL_PRIORITY_CLASS
                    && irqProbe.IsPlacementCapturing && priorityTarget != boostPriorityTarget)
                    irqProbe.InvalidateGameMask();
                // Under Normal or when policy disallows, the lane never starts again, after success stop re-reading the restore ledger every time
                // Failure must not be cached as restored, later scans and match end still need their restore chance
                if (!boostLaneReleased)
                    boostLaneReleased = RenderLane.Release();
            }
            else boostLaneReleased = false;
            // The initial decision for a new match or new renderer doesn't count as a raise/lower
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
                // When the GPU original is already tracked, a later CaptureAndTrack re-checks and may
                // write High back, a failed pre-check can't fail-open or that write would land inside
                // an already started IRQ epoch
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
            // Trying covers read-only identification and the one-minute wait between batches, not an active write
            // The real thread setter already shares the gate lock with capture start via Begin/EndExternalMutation
            return state != LaneState.Trying && state != LaneState.Engaged
                && state != LaneState.Unavailable;
        }

        private IntPtr OpenBoostHandle(int pid, BoostPass pass)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                bool noSuchProcess = Native.LastOpenProcessFailureWasNoSuchProcess();
                // Later protected-list handling may write the registry, an existing IRQ capture must stop first
                if (irqProbe.IsPlacementCapturing)
                {
                    // A confirmed nonexistent process is a normal close-out, don't abandon the whole match
                    // Only access denied/unknown identity forces abandonment
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
                // Renderer identity confirmation/game library replacement commit independently before scheduling, regardless of whether the boost handle is writable
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
            // stateOk requires both scheduling priority and disk IO in place, but the two differ in nature
            //   Scheduling priority is the substance of the boost, failing to get IO priority doesn't mean the boost didn't apply
            //   A refused write is the expected result of anti-cheat protecting the game, not a fault, log it separately from a real write failure
            //   Otherwise the log shows a boost failed line followed by 0x80, which is the target value, and it looks like something broke
            bool prioOk = actualPriority == pass.PriorityTarget;
            bool writeRefused = writeError == 5 || writeError == unchecked((int)0xC0000022);
            if (!stateOk && firstStateWarning && !handleStripped)
            {
                if (writeRefused && !prioOk)
                {
                    string guard = KernelAntiCheat.DescribeForLog(pass.RendererName);
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
            // The two cases already explained get no extra failure line: scheduling priority was already there, or the write was refused
            if (stateNowGaveUp && !handleStripped && !prioOk && !writeRefused)
                Logger.Log(Lang.T("log.gamemodeboost.11") + pass.RendererName + " pid " + pid
                    + Lang.T("log.gamemodeboost.55") + StateRetryMax + Lang.T("log.gamemodeboost.56"));
            return true;
        }
    }
}
