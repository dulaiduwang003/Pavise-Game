using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private AutoCoreGovernor NewCoreGovernor()
        {
            if (coreRangeUnrestricted || corePlan == null || !corePlan.Available)
            {
                return null;
            }
            return new AutoCoreGovernor(corePlan.Cores, corePlan.Logical);
        }
        private void ResetCoreWatch()
        {
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
            // 全选且不隔离不新增范围限制 旧归属仍须恢复结清 未完成时保持 Pending
            coreRangeUnrestricted = !coreObservationOnly && corePlan.Available && coreManual.IsUnrestricted(allMask);
            coreGovernor = NewCoreGovernor();
            coreGameMask = ((coreManual != null) ? coreManual.GameMask : corePlan.Exclusive);
            if (coreGovernor != null && (!isolationClean || !restoreAllHardPinsSucceeded))
            {
                coreGovernor.Block("schedule.auto.pending");
            }
            if (corePlan.Available)
            {
                // 范围未启用时这一行只是配置记录 游戏进程不受限制 已保存的范围仅供反作弊等后台选核时避让
                // 已保存为全部核心时没有可避让的余核 不写“供避让参考”
                string planKey = !coreObservationOnly ? "schedule.manual.plan"
                    : coreManual.IsUnrestricted(allMask) ? "schedule.manual.plan.unused" : "schedule.manual.plan.reference";
                Logger.Log(() => string.Format(Lang.T(planKey), CpuTopology.DescribeMask(coreManual.GameMask)));
            }
            else
            {
                Logger.Log(() => string.Format(Lang.T("log.autocore.unavailable"), Lang.T(corePlan.Blocked)));
            }
            if (corePlan.Allocated != 0)
            {
                Logger.Log(() => string.Format(Lang.T("log.autocore.allocated"), CpuTopology.DescribeMask(corePlan.Allocated)));
            }
            PublishAutoCores(null);
        }
        private void EndAutoCores()
        {
            coreGovernor = null;
            corePlan = null;
            coreManual = null;
            coreRangeUnrestricted = false;
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
            StepAutoCoresImpl(pass);
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
            if (coreRangeUnrestricted)
            {
                // 无新增限制时 旧恢复待办仍沿原预算处理
                if (corePinPending) corePinPending = !RestoreAllHardPins();
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
            backgroundCoreProtected = new HashSet<int>(protectedPids);
            backgroundCoreProtectionEpoch = FamilyPolicyEpoch;
            SyncCoreBackgroundImpl(protectedPids, foreground);
        }
        private ulong CoreBackgroundTarget()
        {
            ulong manual = ManualCoreBackgroundTarget();
            ulong reserved = GameBackgroundReservedCores();
            // 手动核心隔离优先 没有隔离时 激进档按分区把后台收到后台核心上
            ulong aggressive = manual == 0 ? AggressiveCoreBackgroundTarget() : 0;
            if (manual == 0 && reserved == 0 && aggressive == 0) return 0;
            ulong allowed = CpuTopology.BackgroundAllowedMask();
            if (manual != 0) allowed &= manual;
            else if (aggressive != 0) allowed &= aggressive;
            return allowed & ~reserved;
        }
        // 上次记进日志的激进档后台核心 变化时才再记一行
        private ulong loggedAggressiveCoreTarget;
        private ulong AggressiveCoreBackgroundTarget()
        {
            PolicySnapshot policy = sessionPolicy ?? PolicyResolver.Global();
            bool safePartition = CpuTopology.HasSafeBackgroundPartition();
            ulong target = BackgroundGovernancePolicy.AggressiveCoreTarget(policy.SuppressBackground, policy.BackgroundGovernanceMode, CpuTopology.MultiGroup,
                safePartition, CpuTopology.ThrottleMask, safePartition ? 0 : CpuTopology.BackgroundSqueezeMask(), CpuTopology.AllMask);
            if (target != loggedAggressiveCoreTarget)
            {
                loggedAggressiveCoreTarget = target;
                if (target != 0)
                    Logger.Info(Logger.Format("后台治理激进档：普通后台收到 CPU{0}（{1}），只在各进程原范围内收紧", "Aggressive background level: ordinary background narrowed to CPU{0} ({1}), only tightened inside each process's original range", CpuTopology.DescribeRanges(target), (safePartition ? Logger.Text("分区 ", "partition ") + CpuTopology.PartitionTag : Logger.Text("无分区 收核", "no partition, cores narrowed"))));
            }
            return target;
        }
        private ulong ManualCoreBackgroundTarget()
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
            ulong reserved = GameBackgroundReservedCores();
            ulong backgroundMask = CoreBackgroundTarget();
            if (backgroundMask == 0)
            {
                ClearBackgroundSqueezes();
                return;
            }
            core.EnforceBackgroundAffinity = true;
            foreach (int item in core.PidsWith(SuppressReason.Background))
            {
                if (GameBackgroundReservedCores() != reserved) break;
                long creation = core.CreationOf(item);
                string processName = core.NameOf(item);
                if (creation > 0 && !string.IsNullOrEmpty(processName))
                {
                    if (TryProtectHardwareControlBackground(item, creation, processName)) continue;
                    bool changed;
                    if (item == foreground || item == coreWatchPid || protectedPids.Contains(item) || WeGameRuntimeProtection.IsProtectedRuntimeName(processName) || PeripheralCatalog.IsInputAudioLike(processName))
                    {
                        core.SetSqueeze(item, creation, processName, 0uL, SuppressReason.Background, out changed);
                    }
                    else
                    {
                        core.SetSqueeze(item, creation, processName, backgroundMask, SuppressReason.Background, out changed);
                    }
                }
            }
            // 计数结果不用 这一遍按 Job 核验每个后台条目 核验不过的转入待恢复
            core.SqueezedCount(SuppressReason.Background);
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
                // 撤回可能更新现场 先保留阻止落核时的证据 沿用本局首次停止的日志门 避免轮询刷屏
                string evidence = corePlacementEvidence;
                ApplyAutoCoreStep(autoCoreGovernor.Block(reason));
                if (allowed)
                {
                    Logger.Log(() => string.Format(Lang.T("log.autocore.blocked"), Lang.T(reason)) + (string.IsNullOrEmpty(evidence) ? "" : Logger.Text(" | 绑定证据：", " | binding evidence: ") + evidence));
                }
            }
        }
        private void PublishAutoCores(string game)
        {
            AutoCoreGovernor autoCoreGovernor = coreGovernor;
            ulong appliedAffinity = coreGameMask;
            bool jobBound = false;
            lock (sync)
            {
                HardPin value;
                if (coreEngaged && hardPins.TryGetValue(coreWatchPid, out value))
                {
                    appliedAffinity = value.AppliedAffinity;
                    jobBound = value.JobBound;
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
            autoCoreStatus.BackgroundAffinityDetail = core == null ? null : core.BackgroundAffinityDetail();
            autoCoreStatus.GameMask = appliedAffinity;
            autoCoreStatus.ExpectedMask = ((coreManual != null) ? coreManual.GameMask : coreGameMask);
            autoCoreStatus.ObservedMask = coreAffinityObserved;
            autoCoreStatus.AffinityRead = coreAffinityRead;
            autoCoreStatus.AffinityVerified = coreEngaged && coreAffinityVerified && !CorePending;
            autoCoreStatus.JobAffinity = jobBound && autoCoreStatus.AffinityVerified;
            autoCoreStatus.Pending = CorePending;
            autoCoreStatus.Blocked = ((autoCoreGovernor != null) ? autoCoreGovernor.Blocked : ((coreManual != null && coreAllowed && corePlan != null) ? corePlan.Blocked : null));
            autoCoreStatus.ObservationOnly = coreObservationOnly;
            autoCoreStatus.Unrestricted = coreRangeUnrestricted;
            AutoCores.Publish(autoCoreStatus);
        }
        private void EngageLaneAndReport(IntPtr h, int pid, long currentCreation, BoostPass pass, bool stateOk, bool firstVerified, bool gpuOk, bool ecoCleared, string placementText, bool placementVerified)
        {
            if (pass.LaneAllowed && pass.LaneRestorationReady && pass.ClassicThreadBoost && LaneEligible && pass.PriorityTarget == 128 && !pass.WriteDenied && !RenderLane.IsActiveFor(pid, currentCreation))
            {
                RenderLane.EnsureForGame(pid, currentCreation, pass.RendererName,
                    () => ThreadBoostRecoveryAllowed(pid, currentCreation));
            }
            if (stateOk && firstVerified)
            {
                // 本局没有开启任何提优 没有解除省电限制 也没有核验到核心范围时 这里只是完成了一次状态核验 不能记成“已生效”
                bool applied = pass.CpuBoost || pass.IoBoost || (gpuOk && pass.GpuBoost) || ecoCleared || placementVerified;
                long firstBoostStamp = Interlocked.Exchange(ref boostFirstStampTicks, 0L);
                if (firstBoostStamp != 0)
                {
                    Logger.Log(() => Lang.T(applied ? "log.gamemodeboost.60" : "log.gamemodeboost.62") + (DateTime.UtcNow.Ticks - firstBoostStamp) / 10000 + Lang.T("log.gamemodeboost.61"));
                }
                if (!applied)
                {
                    Logger.Info(BoostIdleLine(pass.RendererName, pid, Native.PowerThrottlingSupported ? EcoStateText(h) : ""));
                    return;
                }
                Logger.Log(() => BoostSuccessLine(pass.RendererName, pid, (!pass.CpuBoost) ? "" : ((pass.PriorityTarget == 128) ? Lang.T("log.gamemodeboost.28") : Lang.T("log.gamemodeboost.29")), placementText, placementVerified, (pass.IoBoost ? Lang.T("log.gamemodeboost.30") : "") + ((gpuOk && pass.GpuBoost) ? Lang.T("log.gamemodeboost.31") : "") + ((!Native.PowerThrottlingSupported) ? "" : (ecoCleared ? Lang.T("t.gamemodeboost.32") : EcoStateText(h)))));
            }
        }
        internal static string BoostIdleLine(string renderer, int pid, string otherStates)
        {
            return Lang.T("log.gamemodeboost.63") + renderer + "(pid " + pid + ")" + Lang.T("log.gamemodeboost.64") + otherStates;
        }
        internal static string BoostSuccessLine(string renderer, int pid, string priority, string placement, bool placementVerified, string otherStates)
        {
            return "PASS " + Lang.T("log.gamemodeboost.27") + renderer + "(pid " + pid + ") " + priority + (placementVerified ? placement : "") + otherStates;
        }
        internal static string PlacementWarningLine(string renderer, int pid, bool unreadable, bool gaveUp)
        {
            return "WARN " + Lang.T("log.gamemodeboost.23") + renderer + " pid " + pid + " " + Lang.T(unreadable ? "log.placement.unreadable" : "log.placement.unconfirmed") + Lang.T(gaveUp ? "log.placement.stopped" : "log.gamemodeboost.24");
        }
        private bool StopCoreRefinement()
        {
            if (CoreThreadPlacement.UnownedPending)
            {
                return CoreThreadPlacement.Recover();
            }
            return true;
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
            bool renderIdentificationExhausted = Volatile.Read(ref classicThreadBoostOn) && FrameCoordination.IdentificationExhausted(pid, birth, name);
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
            string[] textEntries = new string[2] { renderLaneState, frameCoordinationState };
            foreach (string entryText in textEntries)
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
                if (!StageGameExecutable(name, executablePath, preferredRoot, next, nextIgnore, out error, true) || !CommitLibraryLocked(next, nextIgnore))
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
#if PAVISE_SELFTEST
        internal static Func<PerformancePreset, bool> LaneSupportedForTest;
#endif
        internal static bool LaneSupported(PerformancePreset mode)
        {
#if PAVISE_SELFTEST
            if (LaneSupportedForTest != null) return LaneSupportedForTest(mode);
#endif
            return !IsHandheld(mode);
        }
    }
}
