using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal AntiCheatAffinityGameContext GetAntiCheatAffinityContext()
        {
            lock (sync)
            {
                GameDetection detection = activeDetection;
                if (!active || !enabled || stopping || panicReq || ProfileStoreSaveFailed || gameGoneSinceTicks != 0
                    || detection == null || !detection.RendererCandidateSelected || detection.RendererSafetyOnly
                    || detection.RequiresGpuConfirm || detection.RendererPid <= 0 || detection.RendererCreation <= 0)
                {
                    AntiCheatContextStable(null, 0);
                    return null;
                }
                if (!AntiCheatContextStable(detection, AutoCores.NowTicks)) return null;
                ulong reserved = 0, domain = 0;
                if (!CpuTopology.MultiGroup)
                {
                    if (corePlan != null) reserved |= corePlan.Allocated;
                    HardPin pin;
                    if (coreEngaged && hardPins.TryGetValue(detection.RendererPid, out pin) && pin.Creation == detection.RendererCreation)
                    {
                        if (pin.AppliedAffinity != allMask) domain = pin.AppliedAffinity;
                        if (coreManual != null && coreManual.Isolation && CoreIsolationClient.State == "schedule.isolation.active") reserved |= pin.AppliedAffinity;
                    }
                    CriticalThreadUiState threads = criticalThreadUi;
                    if (threads.TargetPid == detection.RendererPid && threads.TargetBirth == detection.RendererCreation && (threads.Applied || threads.Pending))
                        foreach (ulong mask in threads.CandidateMasks) reserved |= mask;
                }
                HashSet<int> ancestors = VerifiedAntiCheatAncestors(antiCheatProcessSnapshot, detection);
                if (ancestors == null) return null;
                return new AntiCheatAffinityGameContext(detection.RendererPid, detection.RendererCreation, gameSessionStarts,
                    CpuTopology.MultiGroup ? -1 : 0, reserved, domain, ancestors);
            }
        }
        private AutoCoreGovernor NewCoreGovernor()
        {
            if (corePlan == null || !corePlan.Available)
            {
                return null;
            }
            return new AutoCoreGovernor(corePlan.Cores, corePlan.Logical);
        }
        private void ResetCoreWatch()
        {
            ResetCoreIntelligence();
            coreWatchPid = 0;
            coreWatchCreation = 0L;
            coreEngaged = false;
            coreAffinityObserved = 0uL;
            coreAffinityRead = (coreAffinityVerified = false);
            exclusiveCheckTicks = 0L;
            exclusiveAuditRetry.Reset();
            exclusiveAuditDeferred = false;
            coreGameMask = ((coreManual != null) ? coreManual.GameMask : ((corePlan != null) ? corePlan.Exclusive : 0));
            corePhysical = CpuTopology.PhysicalCoreMasks();
            coreBackgroundMoved = 0;
            corePlacementEvidence = null;
        }
        private void BeginAutoCores(PolicySnapshot snap, bool isolationClean)
        {
            bool restoreAllHardPinsSucceeded = RestoreAllHardPins();
            corePinPending = !restoreAllHardPinsSucceeded;
            ResetCoreWatch();
            coreManual = ((coreSessionPolicy != null) ? coreSessionPolicy.ManualCores : ((snap != null) ? snap.ManualCores : null));
            corePlan = ((coreManual != null) ? coreManual.RuntimePlan() : new AutoCorePlan
            {
                Blocked = "schedule.manual.required"
            });
            corePreset = ((snap != null) ? snap.Preset : PerformancePreset.Standard);
            coreAllowed = snap != null && snap.AutoCoresAllowed;
            corePolicyCheckTicks = AutoCores.NowTicks;
            coreObservationOnly = coreManual == null || snap == null || !snap.AutoCoresAllowed || !PresetValue.SupportsCoreScheduling(snap.Preset);
            coreGovernor = NewCoreGovernor();
            coreGameMask = ((coreManual != null) ? coreManual.GameMask : corePlan.Exclusive);
            if (coreGovernor != null && (!isolationClean || !restoreAllHardPinsSucceeded))
            {
                coreGovernor.Block("schedule.auto.pending");
            }
            if (corePlan.Available)
            {
                Logger.Log(string.Format(Lang.T("schedule.manual.plan", 0), CpuTopology.DescribeMask(coreManual.GameMask)));
            }
            else
            {
                Logger.Log(string.Format(Lang.T("log.autocore.unavailable", 0), Lang.T(corePlan.Blocked, 0)));
            }
            if (corePlan.Allocated != 0)
            {
                Logger.Log(string.Format(Lang.T("log.autocore.allocated", 0), CpuTopology.DescribeMask(corePlan.Allocated)));
            }
            PublishAutoCores(null);
        }
        private void EndAutoCores()
        {
            ResetCoreIntelligence();
            coreGovernor = null;
            corePlan = null;
            coreManual = null;
            corePlacementStopReason = null;
            corePlacementOnly = false;
            ResetCoreWatch();
            lock (sync)
            {
                corePinPending = hardPins.Count > 0;
            }
            AutoCoreStatus autoCoreStatus = new AutoCoreStatus();
            autoCoreStatus.Pending = CorePending;
            AutoCores.Publish(autoCoreStatus);
        }
        private void RefreshAutoCoreMode()
        {
            if (stopping || (Interlocked.Exchange(ref corePolicyDirty, 0) == 0 && AutoCores.NowTicks - corePolicyCheckTicks < 50000000))
            {
                return;
            }
            corePolicyCheckTicks = AutoCores.NowTicks;
            GameProfile gameProfile = null;
            lock (sync)
            {
                if (activeDetection != null && activeDetection.Profile != null)
                {
                    foreach (GameProfile profile in profiles)
                    {
                        if (profile.Id == activeDetection.Profile.Id)
                        {
                            gameProfile = profile.Clone();
                            break;
                        }
                    }
                }
            }
            PolicySnapshot policySnapshot = coreSessionPolicy ?? ((gameProfile == null) ? PolicyResolver.Global() : PolicyResolver.For(gameProfile));
            if (policySnapshot.AutoCoresAllowed && coreAllowed && PresetValue.SupportsCoreScheduling(policySnapshot.Preset) && PresetValue.SupportsCoreScheduling(corePreset))
            {
                if (corePreset != policySnapshot.Preset)
                {
                    StopCoreRefinement();
                }
                corePreset = policySnapshot.Preset;
            }
            else if (policySnapshot.Preset != corePreset || policySnapshot.AutoCoresAllowed != coreAllowed)
            {
                BeginAutoCores(policySnapshot, StopCoreIsolation());
            }
        }
        private void StepAutoCores(BoostPass pass)
        {
            long nowTicks = AutoCores.NowTicks;
            try
            {
                StepAutoCoresImpl(pass);
            }
            finally
            {
                coreBudget.Record(nowTicks);
            }
        }
        private void StepAutoCoresImpl(BoostPass pass)
        {
            RefreshAutoCoreMode();
            AutoCoreGovernor autoCoreGovernor = coreGovernor;
            if (coreManual == null)
            {
                PublishAutoCores(pass.RendererName);
                return;
            }
            if (corePlacementStopReason != null)
            {
                if (CorePending)
                {
                    ReleaseExclusive();
                }
                if (autoCoreGovernor != null)
                {
                    autoCoreGovernor.Block(corePlacementStopReason);
                }
                PublishAutoCores(pass.RendererName);
                return;
            }
            if (autoCoreGovernor == null || pass.RendererPid <= 0 || pass.RendererCreation <= 0)
            {
                if (autoCoreGovernor != null)
                {
                    ApplyAutoCoreStep(autoCoreGovernor.Pause());
                }
                PublishAutoCores(pass.RendererName);
                return;
            }
            if (pass.RendererPid != coreWatchPid || pass.RendererCreation != coreWatchCreation)
            {
                if (coreWatchPid > 0)
                {
                    bool releaseExclusiveSucceeded = ReleaseExclusive();
                    autoCoreGovernor = (coreGovernor = NewCoreGovernor());
                    if (!releaseExclusiveSucceeded)
                    {
                        autoCoreGovernor.Block("schedule.auto.pending");
                    }
                }
                ResetCoreWatch();
                coreWatchPid = pass.RendererPid;
                coreWatchCreation = pass.RendererCreation;
            }
            bool isInPlacementGaveUp;
            lock (sync)
            {
                isInPlacementGaveUp = placementGaveUp.Contains(pass.RendererPid);
            }
            if (isInPlacementGaveUp && !coreEngaged)
            {
                BlockAutoCores("schedule.auto.placementunavailable");
            }
            else if (coreEngaged && coreManual.Isolation && CoreIsolationClient.State != "schedule.isolation.active")
            {
                BlockAutoCores("schedule.auto.failed");
            }
            if (coreObservationOnly)
            {
                PublishAutoCores(pass.RendererName);
            }
            else if (RendererForegroundPid() != pass.RendererPid)
            {
                if (!coreEngaged)
                {
                    ApplyAutoCoreStep(autoCoreGovernor.Pause());
                }
                PublishAutoCores(pass.RendererName);
            }
            else
            {
                autoCoreGovernor.EngageManual();
                PublishAutoCores(pass.RendererName);
            }
        }
        private void SyncCoreBackground(HashSet<int> protectedPids, int foreground)
        {
            long nowTicks = AutoCores.NowTicks;
            try
            {
                SyncCoreBackgroundImpl(protectedPids, foreground);
            }
            finally
            {
                coreBudget.Record(nowTicks);
            }
        }
        private ulong CoreBackgroundTarget()
        {
            if (coreManual == null || stopping || !coreEngaged || coreObservationOnly || corePlan == null || !corePlan.Available || CorePending)
            {
                return 0uL;
            }
            if (!coreManual.Isolation || CoreIsolationClient.State != "schedule.isolation.active" || CoreIsolationClient.ActiveMask != corePlan.Exclusive)
            {
                return 0uL;
            }
            ulong combinedMask = allMask & ~corePlan.Exclusive & ~AutoCores.WholeCores(corePlan.Allocated, corePhysical);
            if (AutoCores.CountBits(combinedMask) < 2)
            {
                return 0uL;
            }
            return combinedMask;
        }
        private void SyncCoreBackgroundImpl(HashSet<int> protectedPids, int foreground)
        {
            core.RetryRetiredAutomaticLayouts();
            if (core.BackgroundAffinityPending)
            {
                ClearBackgroundSqueezes();
                return;
            }
            ulong backgroundMask = CoreBackgroundTarget();
            if (backgroundMask == 0)
            {
                ClearBackgroundSqueezes();
                return;
            }
            core.EnforceBackgroundAffinity = true;
            foreach (int pidsWith in core.PidsWith(SuppressReason.Background))
            {
                long creation = core.CreationOf(pidsWith);
                string processName = core.NameOf(pidsWith);
                if (creation > 0 && !string.IsNullOrEmpty(processName))
                {
                    bool changed;
                    if (IsCriticalThreadGuardian(pidsWith, creation))
                    {
                        ReleaseCriticalGuardianBackground();
                    }
                    else if (GameAffinityJobLease.IsGuardian(pidsWith, creation))
                    {
                        ReleaseJobGuardianBackground(pidsWith, creation, processName);
                    }
                    else if (pidsWith == foreground || pidsWith == coreWatchPid || protectedPids.Contains(pidsWith) || WeGameRuntimeProtection.IsProtectedRuntimeName(processName) || PeripheralCatalog.IsInputAudioLike(processName))
                    {
                        core.SetSqueeze(pidsWith, creation, processName, 0uL, SuppressReason.Background, out changed);
                    }
                    else if (!core.IsSqueezed(pidsWith))
                    {
                        core.SetSqueeze(pidsWith, creation, processName, backgroundMask, SuppressReason.Background, out changed);
                    }
                }
            }
            coreBackgroundMoved = core.SqueezedCount(SuppressReason.Background);
        }
        private void ApplyAutoCoreStep(AutoCoreStep step)
        {
            if (step == AutoCoreStep.Release)
            {
                ReleaseExclusive();
            }
        }
        private void BlockAutoCores(string reason)
        {
            if (coreManual != null)
            {
                corePlacementStopReason = reason;
            }
            coreAffinityVerified = false;
            AutoCoreGovernor autoCoreGovernor = coreGovernor;
            if (autoCoreGovernor != null)
            {
                bool allowed = autoCoreGovernor.Blocked == null;
                ApplyAutoCoreStep(autoCoreGovernor.Block(reason));
                if (allowed)
                {
                    Logger.Log(string.Format(Lang.T("log.autocore.blocked", 0), Lang.T(reason, 0)));
                }
            }
        }
        private void PublishAutoCores(string game)
        {
            AutoCoreGovernor autoCoreGovernor = coreGovernor;
            ulong appliedAffinity = coreGameMask;
            bool jobBound = false;
            string jobFallback = null;
            lock (sync)
            {
                HardPin hardPin;
                if (coreEngaged && hardPins.TryGetValue(coreWatchPid, out hardPin))
                {
                    appliedAffinity = hardPin.AppliedAffinity;
                    jobBound = hardPin.JobBound;
                    jobFallback = hardPin.JobFallbackReason;
                }
            }
            AutoCoreStatus autoCoreStatus = new AutoCoreStatus();
            autoCoreStatus.Plan = corePlan;
            autoCoreStatus.Manual = coreManual != null;
            autoCoreStatus.ManualIsolation = coreManual != null && coreManual.Isolation;
            autoCoreStatus.Game = game;
            autoCoreStatus.ProfileId = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Id : null);
            autoCoreStatus.Watching = autoCoreGovernor != null && coreWatchPid > 0;
            autoCoreStatus.Exclusive = autoCoreGovernor != null && autoCoreGovernor.Exclusive;
            autoCoreStatus.Engaged = coreEngaged;
            autoCoreStatus.BackgroundMoved = coreBackgroundMoved;
            autoCoreStatus.BackgroundAffinityDetail = core == null ? null : core.BackgroundAffinityDetail();
            autoCoreStatus.GameMask = appliedAffinity;
            autoCoreStatus.ExpectedMask = ((coreManual != null) ? coreManual.GameMask : coreGameMask);
            autoCoreStatus.ObservedMask = coreAffinityObserved;
            autoCoreStatus.AffinityRead = coreAffinityRead;
            autoCoreStatus.AffinityVerified = coreEngaged && coreAffinityVerified && !CorePending;
            autoCoreStatus.JobAffinity = jobBound && autoCoreStatus.AffinityVerified;
            autoCoreStatus.JobFallbackReason = jobFallback;
            autoCoreStatus.WorkMs = coreBudget.TotalMs;
            autoCoreStatus.WorkMaxMs = coreBudget.MaxMs;
            autoCoreStatus.Pending = CorePending;
            autoCoreStatus.PlacementEvidence = corePlacementEvidence;
            autoCoreStatus.Blocked = ((autoCoreGovernor != null) ? autoCoreGovernor.Blocked : ((coreManual != null && coreAllowed && corePlan != null) ? corePlan.Blocked : null));
            autoCoreStatus.ObservationOnly = coreObservationOnly;
            AutoCores.Publish(autoCoreStatus);
        }
        private void EngageLaneAndReport(IntPtr h, int pid, long currentCreation, BoostPass pass, bool stateOk, bool firstVerified, bool gpuOk, bool ecoCleared, string placementText, bool placementVerified)
        {
            if (pass.LaneAllowed && pass.LaneRestorationReady && EffLane && LaneEligible && pass.PriorityTarget == 128 && !pass.WriteDenied && !RenderLane.IsActiveFor(pid, currentCreation))
            {
                RenderLane.EnsureForGame(pid, currentCreation, pass.RendererName,
                    () => ThreadBoostRecoveryAllowed(pid, currentCreation));
            }
            if (stateOk && firstVerified)
            {
                long firstBoostStamp = Interlocked.Exchange(ref boostFirstStampTicks, 0L);
                if (firstBoostStamp != 0)
                {
                    Logger.Log(Lang.T("log.gamemodeboost.60", 0) + (DateTime.UtcNow.Ticks - firstBoostStamp) / 10000 + Lang.T("log.gamemodeboost.61", 0));
                }
                Logger.Log(BoostSuccessLine(pass.RendererName, pid, (!pass.CpuBoost) ? "" : ((pass.PriorityTarget == 128) ? Lang.T("log.gamemodeboost.28", 0) : Lang.T("log.gamemodeboost.29", 0)), placementText, placementVerified, (pass.IoBoost ? Lang.T("log.gamemodeboost.30", 0) : "") + (pass.MemoryBoost ? (" " + Lang.T("gm.boost.memory", 0)) : "") + ((gpuOk && pass.GpuBoost) ? Lang.T("log.gamemodeboost.31", 0) : "") + ((!Native.PowerThrottlingSupported) ? "" : (ecoCleared ? Lang.T("t.gamemodeboost.32", 0) : EcoStateText(h)))));
            }
        }
        internal static string BoostSuccessLine(string renderer, int pid, string priority, string placement, bool placementVerified, string otherStates)
        {
            return "PASS " + Lang.T("log.gamemodeboost.27") + renderer + "(pid " + pid + ") " + priority + (placementVerified ? placement : "") + otherStates;
        }
        internal static string PlacementWarningLine(string renderer, int pid, bool unreadable, bool gaveUp)
        {
            return "WARN " + Lang.T("log.gamemodeboost.23") + renderer + " pid " + pid + " " + Lang.T(unreadable ? "log.placement.unreadable" : "log.placement.unconfirmed") + Lang.T(gaveUp ? "log.placement.stopped" : "log.gamemodeboost.24");
        }
        private void ResetCoreIntelligence()
        {
            coreBudget = new CoreWorkBudget();
        }
        private bool StopCoreRefinement()
        {
            if (CoreThreadPlacement.UnownedPending)
            {
                return CoreThreadPlacement.Recover();
            }
            return true;
        }
        private bool IsCriticalThreadGuardian(int pid, long birth)
        {
            HardThreadGuardianActor hardThreadGuardianActor = criticalGuardianActor;
            if (hardThreadGuardianActor != null && hardThreadGuardianActor.Pid == pid && birth > 0)
            {
                return hardThreadGuardianActor.Creation == birth;
            }
            return false;
        }
        private bool LaneFallbackFor(int pid, long birth)
        {
            LaneFallbackTarget laneFallbackTarget = laneFallback;
            if (laneFallbackTarget != null && pid > 0 && laneFallbackTarget.Pid == pid)
            {
                return laneFallbackTarget.Birth == birth;
            }
            return false;
        }
        private void UpdateLaneFallback(int pid, long birth, string name)
        {
            bool renderIdentificationExhausted = FrameCoordinationEnabled && FrameCoordination.IdentificationExhausted(pid, birth, name);
            LaneFallbackTarget laneFallbackTarget = laneFallback;
            if (!renderIdentificationExhausted)
            {
                if (laneFallbackTarget != null)
                {
                    laneFallback = null;
                }
            }
            else if (laneFallbackTarget == null || laneFallbackTarget.Pid != pid || laneFallbackTarget.Birth != birth)
            {
                laneFallback = new LaneFallbackTarget(pid, birth);
            }
        }
        internal string ThreadBoostExecutionState()
        {
            string renderLaneState = AutomaticExecutionState("GmRenderLane");
            string frameCoordinationState = AutomaticExecutionState("FrameCoordination");
            foreach (string entryText in new string[2] { renderLaneState, frameCoordinationState })
            {
                switch (entryText)
                {
                case "failed":
                case "restore-failed":
                case "stopped":
                case "cooldown":
                    return entryText;
                }
            }
            if (!AutomationPolicySnapshot.ThreadBoostOn)
            {
                return "off";
            }
            if (laneFallback == null)
            {
                return frameCoordinationState;
            }
            return renderLaneState;
        }
        private bool AddGameExecutableCore(string name, string executablePath, string preferredRoot, out string error)
        {
            error = null;
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked())
                {
                    return false;
                }
                List<GameProfile> next = GetProfiles();
                HashSet<string> nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
                if (!StageGameExecutable(name, executablePath, preferredRoot, next, nextIgnore, out error) || !CommitLibraryLocked(next, nextIgnore))
                {
                    return false;
                }
            }
            KickLibraryChanged();
            return true;
        }
        internal string SaveManualCorePlan(string profileId, string expected, ManualCorePlan plan)
        {
            string rawRule, enabled;
            lock (sync)
            {
                GameProfile current = FindProfileLocked(profileId);
                if (current == null || ManualCorePlan.Raw(current) != expected) return "schedule.manual.changed";
                rawRule = CriticalThreadRule.Raw(current);
                enabled = PolicyResolver.For(current).ValueOf("GmAutoCoresV2");
            }
            if (rawRule != null && CriticalThreadRule.Parse(rawRule) == null) return "schedule.manual.changed";
            return SaveCoreConfiguration(profileId, expected, rawRule, enabled, plan,
                plan != null && ManualCorePlan.Parse(expected) != null && enabled == "1", CriticalThreadRule.Parse(rawRule));
        }
        internal static bool LaneSupported(PerformancePreset mode)
        {
            return !IsHandheld(mode);
        }
    }
}
