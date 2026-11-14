using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void RevokeCriticalThreadIntent([CallerMemberName] string reason = null)
        {
            Interlocked.Increment(ref criticalIntentEpoch);
            CriticalThreadTargetLease criticalThreadTargetLease = criticalOriginalTarget;
            if (criticalThreadTargetLease != null)
            {
                if (!criticalThreadTargetLease.Revoked)
                {
                    LogCriticalThreadState("revoked", reason);
                }
                criticalThreadTargetLease.Revoke();
            }
            CriticalThreadUiState criticalThreadUiState = criticalThreadUi;
            if (criticalThreadUiState.Applied || criticalThreadUiState.Pending)
            {
                criticalThreadUi = new CriticalThreadUiState(criticalThreadUiState.ProfileId, "recovery-pending", PerformancePolicy.Text("已停止新增绑定，正在核验原设置恢复", "Further bindings stopped; verifying restoration"), criticalThreadUiState.Enabled, false, true, criticalThreadUiState.Workers, criticalThreadUiState.SnapshotTicks, criticalThreadUiState.WorkerMask, criticalThreadUiState.CandidateMasks, criticalThreadUiState.CandidateLabels, criticalThreadUiState.SessionActive, criticalThreadUiState.RequestedWorkerAvoid, criticalThreadUiState.Coverage, criticalThreadUiState.SessionToken, criticalThreadUiState.GameSerial, criticalThreadUiState.IntentEpoch, criticalThreadUiState.TargetPid, criticalThreadUiState.TargetBirth, criticalThreadUiState.RuleSignature);
            }
        }
        // 依赖组的零掩码表示仅授权优先级，不授权亲和性写入。
        private CriticalThreadTargetLease CreateCriticalThreadTarget(BoostPass pass, IntPtr process, CriticalThreadRule expectedRule, string expectedProfile, string expectedRawRule, ulong currentProcessMask, long expectedEpoch, long expectedSerial, bool priorityOnlySession = false)
        {
            bool identityOnly = expectedRule != null && expectedRule.IsDependencyBoost;
            bool priorityOnly = identityOnly || priorityOnlySession && expectedRule != null && expectedRule.IsRenderStrategy && !expectedRule.IsTimeCriticalRender;
            if (!Monitor.IsEntered(criticalProcessGate) || pass == null || process == IntPtr.Zero || expectedRule == null || expectedRule.IsOff || expectedRule.IsComparison
                || !identityOnly && currentProcessMask == 0 || GetProcessId(process) != (uint)pass.RendererPid
                || (!priorityOnly && (CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)))
            {
                return null;
            }
            long criticalIntentEpochSnapshot = Interlocked.Read(ref criticalIntentEpoch);
            if (criticalIntentEpochSnapshot != expectedEpoch || Interlocked.Read(ref gameSessionStarts) != expectedSerial)
            {
                return null;
            }
            long currentCreation;
            if (!VerifyRendererIdentity(process, pass.RendererPid, pass, out currentCreation) || currentCreation != pass.RendererCreation)
            {
                return null;
            }
            string imagePath = Native.ImagePath(process);
            ulong mask;
            if (string.IsNullOrEmpty(imagePath) || !GamePerformanceTargetPolicy.AllowsGameTarget(imagePath)
                || !identityOnly && (!Native.TryQueryAffinity(process, out mask) || mask != currentProcessMask))
            {
                return null;
            }
            lock (sync)
            {
                GameDetection gameDetection = activeDetection;
                GameProfile gameProfile = CriticalPreferenceProfileLocked(expectedProfile);
                bool admits = expectedRule.IsUnified && expectedRule.Automatic && !expectedRule.ManualTarget
                    ? expectedRule.Encode() == (PolicyResolver.GameSchedulingRule(gameProfile) == null ? null : PolicyResolver.GameSchedulingRule(gameProfile).Encode())
                    : StoredRuleAdmits(expectedRawRule, expectedRule);
                if (!enabled || stopping || panicReq || !active || smartSwitchedOut || ProfileStoreSaveFailed || gameDetection == null || gameDetection.Profile == null || gameProfile == null || gameDetection.Profile.Id != gameProfile.Id || !gameDetection.RendererCandidateSelected || gameDetection.RendererSafetyOnly || gameDetection.RequiresGpuConfirm || gameDetection.RendererPid != pass.RendererPid || gameDetection.RendererCreation != currentCreation || !string.Equals(gameDetection.RendererPath, imagePath, StringComparison.OrdinalIgnoreCase) || EffectiveGameSchedulingRaw(gameProfile) != expectedRawRule || !admits || Interlocked.Read(ref criticalIntentEpoch) != expectedEpoch || gameSessionStarts != expectedSerial)
                {
                    return null;
                }
                CriticalThreadAllowedRange criticalThreadAllowedRange = priorityOnly ? null : CriticalThreadAllowedRange.For(gameProfile);
                if (!priorityOnly && criticalThreadAllowedRange.Error != null)
                {
                    return null;
                }
                // 依赖组的零范围是没有亲和性授权，不是猜测进程可用核心；其他策略保留真实范围。
                ulong allowedThreadMask = identityOnly ? 0 : priorityOnly ? currentProcessMask : currentProcessMask & criticalThreadAllowedRange.Mask;
                ulong reservedCores = priorityOnly ? 0 : AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, CpuTopology.PhysicalCoreMasks());
                string validationError = priorityOnly ? null
                    : expectedRule.Validate(allMask, CpuTopology.PhysicalCoreMasks(), allowedThreadMask, reservedCores, true);
                if (validationError != null)
                {
                    return null;
                }
                criticalMintInFlight = new CriticalThreadMint(pass.RendererPid, currentCreation, imagePath, allowedThreadMask & ~reservedCores, criticalIntentEpochSnapshot, gameSessionStarts, expectedRawRule);
                try
                {
                    return criticalOriginalTarget = CriticalThreadTargetLease.Issue(this, criticalMintInFlight);
                }
                finally
                {
                    criticalMintInFlight = null;
                }
            }
        }
        private bool CriticalRolesWanted(int pid, long birth)
        {
            if (pid > 0 && birth > 0 && Volatile.Read(ref criticalRolesPid) == pid)
            {
                return Interlocked.Read(ref criticalRolesBirth) == birth;
            }
            return false;
        }
        private void WantCriticalRoles(CriticalRuntimeSource source, ThreadTimes snapshot)
        {
            if (source.Rule.Layout != CriticalThreadLayout.Compatible) EnsureThreadLayoutCapture(source);
            if (FrameCoordination.ReadCriticalRoles(source.Pid, source.Birth, snapshot, AutoCores.NowTicks)
                .Any(fact => fact.Role == "presentation-associated") && !ThreadLayoutObservationWanted(source)) return;
            criticalRolesTouched = true;
            int pid = source.Pid;
            long birth = source.Birth;
            Interlocked.Exchange(ref criticalRolesBirth, birth);
            Volatile.Write(ref criticalRolesPid, pid);
            Func<bool> admitted = delegate
            {
                GameDetection gameDetection = Volatile.Read(ref activeDetection);
                return enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed && CriticalRolesWanted(pid, birth) && gameDetection != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && gameDetection.RendererPid == pid && gameDetection.RendererCreation == birth && RendererForegroundPid() == pid;
            };
            FrameCoordination.EnsureObserver(pid, birth, source.Name, admitted);
        }
        private void SettleCriticalRoles()
        {
            if (criticalRolesTouched)
            {
                criticalRolesTouched = false;
            }
            else if (Volatile.Read(ref criticalRolesPid) != 0)
            {
                Volatile.Write(ref criticalRolesPid, 0);
                Interlocked.Exchange(ref criticalRolesBirth, 0L);
                FrameCoordination.ReleaseObserver();
            }
        }
        private void ResetCriticalAuto()
        {
            criticalRolesTouched = false;
            SettleCriticalRoles();
        }
        private static bool ReadCriticalProcessMask(int pid, long birth, out ulong mask)
        {
            mask = 0uL;
            IntPtr intPtr = Native.OpenProcess(4096, false, pid);
            if (intPtr == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return Native.QueryProcessIdentityState(intPtr, birth) == 0 && Native.TryQueryAffinity(intPtr, out mask) && mask != 0;
            }
            finally
            {
                Native.CloseHandle(intPtr);
            }
        }
        private bool DrainHistoricalCriticalThreads(int timeoutMs)
        {
            if (Monitor.IsEntered(sync) || Monitor.IsEntered(criticalProcessGate))
            {
                return false;
            }
            return criticalHistoricalIdle.WaitOne(Math.Max(0, timeoutMs));
        }
        internal CriticalThreadUiState CriticalThreadView(string profileId)
        {
            CriticalThreadUiState criticalThreadUiState = criticalThreadUi;
            if (criticalThreadUiState.Pending && (criticalThreadResidue || criticalHistoricalDebt))
            {
                return criticalThreadUiState;
            }
            GameProfile profile;
            lock (sync)
            {
                if (!criticalThreadUiState.Pending && CriticalThreadSnapshotCurrent(criticalThreadUiState, profileId))
                {
                    return criticalThreadUiState;
                }
                GameProfile gameProfile = FindProfileLocked(profileId);
                profile = ((gameProfile == null) ? null : gameProfile.Clone());
            }
            CriticalThreadRule criticalThreadRule = PolicyResolver.GameSchedulingRule(profile);
            if (criticalThreadRule == null || criticalThreadRule.IsOff)
            {
                return new CriticalThreadUiState(profileId, "off", PerformancePolicy.Text("未启用线程硬绑定", "Thread hard affinity is off"), false, false, false, 0, 0L, 0uL, null, null, false, false, CriticalThreadCoverageState.None, null, 0L, 0L, 0, 0L);
            }
            string nextSessionDetail = criticalThreadRule.Layout != CriticalThreadLayout.Compatible
                ? ThreadLayoutDescriptions.StrategyName(criticalThreadRule) + " · " + ThreadLayoutDescriptions.StrategyHint(criticalThreadRule)
                : criticalThreadRule.Automatic
                ? PerformancePolicy.Text("下次游戏运行时自动识别关键线程并绑定独立物理核心，其他接近满载的线程设为优先核", "The next game session binds the critical thread to its own physical core and gives other near-saturated threads a preferred core")
                : PerformancePolicy.Text("保存的规则在下次有效游戏会话中核验", "The saved rule is checked in the next valid game session");
            return new CriticalThreadUiState(profileId, "waiting-game", nextSessionDetail, true, false, false, 0, 0L, 0uL, null, null, false, criticalThreadRule.WorkerAvoidance, CriticalThreadCoverageState.None, null, 0L, 0L, 0, 0L);
        }
        internal string CriticalThreadDiagnosticLine()
        {
            CriticalThreadUiState view = criticalThreadUi;
            if (view == null) return "unavailable";
            long now = AutoCores.NowTicks;
            long age = view.SnapshotTicks > 0 && now >= view.SnapshotTicks ? (now - view.SnapshotTicks) / 10000L : -1;
            return "cached=" + view.State + " applied=" + view.Applied + " pending=" + view.Pending
                + " pid=" + view.TargetPid + " birth=" + view.TargetBirth + " serial=" + view.GameSerial
                + " rule=" + view.RuleSignature + " candidates=" + string.Join(",", view.CandidateMasks.Select(mask => "0x" + mask.ToString("X")))
                + " workers=" + view.Workers + " workerAvoid=" + view.RequestedWorkerAvoid + " workerMask=0x" + view.WorkerMask.ToString("X")
                + " corrections=" + Volatile.Read(ref criticalReassertions) + " ageMs=" + age + " coupling=" + view.Coupling
                + (view.Detail.Length == 0 ? "" : " detail=" + view.Detail);
        }
        internal string CriticalThreadRuntimeDiagnosticLine()
        {
            CriticalThreadRuntimeTelemetry telemetry = Volatile.Read(ref criticalRuntimeTelemetry);
            return (telemetry == null ? "no samples" : telemetry.Describe())
                + " | lastCandidate=" + Volatile.Read(ref criticalLastAuditDetail);
        }
        private void TraceCriticalRuntime(string stage, string result, int pid, long started, bool forwardAttempt = false)
        {
            if (criticalRuntimeTelemetry == null) criticalRuntimeTelemetry = new CriticalThreadRuntimeTelemetry();
            criticalRuntimeTelemetry.Record(stage, result, pid, started, forwardAttempt);
        }
        internal string SaveCriticalThreadRule(string profileId, string expected, CriticalThreadRule rule)
        {
            return SaveRenderSchedulingConfiguration(profileId, expected, null, false, rule, null);
        }

        // 未改动资源草案时不制造逐游戏覆盖；有改动时与策略共用同一份持久快照。
        internal string SaveRenderSchedulingConfiguration(string profileId, string expected, string expectedResourceOverride,
            bool expectedResourceEnabled, CriticalThreadRule rule, bool? resourceEnabled)
        {
            if (rule == null || rule.IsOff || rule.IsComparison)
            {
                if (resourceEnabled.HasValue)
                    return PerformancePolicy.Text("请先选择正式调度方案", "Choose a scheduling strategy first");
                string expectedEnabled;
                lock (sync)
                {
                    GameProfile current = FindProfileLocked(profileId);
                    if (current == null) return PerformancePolicy.Text("游戏规则已变化，请重新打开", "The game rule changed; reopen the editor");
                    current.Overrides.TryGetValue(PolicyCatalog.KeyGameScheduling, out expectedEnabled);
                }
                return SaveCriticalThreadEnabled(profileId, expected, expectedEnabled, false);
            }
            // 旧格式只读兼容；保存时统一编码并保留策略与清核选择。
            if (rule.Automatic && !rule.ManualTarget)
                rule = rule.IsUnified ? CriticalThreadRule.UnifiedStrategy(rule.Strategy, rule.ClearCore) : CriticalThreadRule.Auto(true, rule.Layout);
            if (rule.IsUnified && rule.ManualTarget) rule = CriticalThreadRule.ParseForRuntime(rule.Encode());
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed)
                {
                    return PerformancePolicy.Text("游戏库暂时无法保存", "The game library cannot be saved right now");
                }
                GameProfile gameProfile = FindProfileLocked(profileId);
                if (gameProfile == null || CriticalThreadRule.Raw(gameProfile) != expected)
                {
                    return PerformancePolicy.Text("游戏规则已变化，请重新打开", "The game rule changed; reopen the editor");
                }
                string resourceError = ResourcePriorityDraftError(gameProfile, expectedResourceOverride, expectedResourceEnabled, resourceEnabled);
                if (resourceError != null) return resourceError;
                CriticalThreadRule savedRule = CriticalThreadRule.ParseForRuntime(CriticalThreadRule.Raw(gameProfile));
                if (rule != null && rule.ClearCore)
                {
                    CriticalThreadAllowedRange clearRange = CriticalThreadAllowedRange.For(gameProfile);
                    string clearError = clearRange.Error ?? rule.ClearCoreCapacityError(allMask, CpuTopology.PhysicalCoreMasks(), clearRange.Mask,
                        AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask,
                        IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled);
                    if (clearError != null) return clearError;
                }
                if (rule != null && !rule.IsOff && (!rule.IsUnified || rule.ManualTarget))
                {
                    CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(gameProfile);
                    if (criticalThreadAllowedRange.Error != null)
                    {
                        return criticalThreadAllowedRange.Error;
                    }
                    string validationError = rule.Validate(allMask, CpuTopology.PhysicalCoreMasks(), criticalThreadAllowedRange.Mask, AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled);
                    if (validationError != null)
                    {
                        return validationError;
                    }
                }
                GameProfile gameProfile2 = gameProfile.Clone();
                gameProfile2.Overrides[CriticalThreadRule.Key] = rule.Encode();
                if (resourceEnabled.HasValue) gameProfile2.Overrides[PolicyCatalog.KeyBoost] = resourceEnabled.Value ? "1" : "0";
                // 从旧关闭规则另存正式策略时，同笔保留关闭状态。
                if (savedRule != null && savedRule.IsOff)
                    gameProfile2.Overrides[PolicyCatalog.KeyGameScheduling] = "0";
                string beforeEnabled, afterEnabled;
                gameProfile.Overrides.TryGetValue(PolicyCatalog.KeyGameScheduling, out beforeEnabled);
                gameProfile2.Overrides.TryGetValue(PolicyCatalog.KeyGameScheduling, out afterEnabled);
                string beforeResource, afterResource;
                gameProfile.Overrides.TryGetValue(PolicyCatalog.KeyBoost, out beforeResource);
                gameProfile2.Overrides.TryGetValue(PolicyCatalog.KeyBoost, out afterResource);
                if (CriticalThreadRule.Raw(gameProfile2) == CriticalThreadRule.Raw(gameProfile) && beforeEnabled == afterEnabled && beforeResource == afterResource) return null;
                List<GameProfile> list = new List<GameProfile>(profiles);
                list[list.IndexOf(gameProfile)] = gameProfile2;
                if (!SaveProfileSnapshotLocked(list))
                {
                    return PerformancePolicy.Text("保存失败，原方案和资源设置保留", "Save failed; the original strategy and resource settings were retained");
                }
                profiles[profiles.IndexOf(gameProfile)] = gameProfile2;
                if (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId)
                {
                    activeDetection.Profile = gameProfile2.Clone();
                }
                if (stickyDetection != null && stickyDetection.Profile != null && stickyDetection.Profile.Id == profileId)
                {
                    stickyDetection.Profile = gameProfile2.Clone();
                }
            }
            RaiseLibraryChanged();
            return null;
        }

        private static string ResourcePriorityDraftError(GameProfile current, string expectedOverride, bool expectedEnabled, bool? desired)
        {
            if (!desired.HasValue) return null;
            string currentOverride;
            current.Overrides.TryGetValue(PolicyCatalog.KeyBoost, out currentOverride);
            return currentOverride != expectedOverride || PolicyResolver.For(current).BoostGame != expectedEnabled
                ? PerformancePolicy.Text("资源优先级已变化，请重新打开", "Resource priority changed; reopen the editor") : null;
        }

        // 外层开关只改逐游戏启用值；已选策略和清核原样保留，显式开启没有策略的旧关闭规则时才建立默认策略。
        internal string SaveCriticalThreadEnabled(string profileId, string expectedRule, string expectedEnabledOverride, bool enabledValue)
        {
            bool deferred;
            return SaveCriticalThreadEnabled(profileId, expectedRule, expectedEnabledOverride, enabledValue, out deferred);
        }

        internal string SaveCriticalThreadEnabled(string profileId, string expectedRule, string expectedEnabledOverride, bool enabledValue, out bool deferredUntilNextSession)
        {
            deferredUntilNextSession = false;
            bool affectsSession = false;
            bool changed = false;
            // 开启需按运行期的门禁顺序读取旧采集阶段；关闭只撤销资格，不等进程门禁，以免被在途核验挡住。
            if (enabledValue && !Monitor.TryEnter(criticalProcessGate, 1000))
                return PerformancePolicy.Text("线程计划仍在核验，请稍后重试", "The thread plan is still being checked; retry shortly");
            try
            {
                lock (sync)
                {
                    if (stopping || ProfileStoreSaveFailed)
                        return PerformancePolicy.Text("游戏库暂时无法保存", "The game library cannot be saved right now");
                    GameProfile current = FindProfileLocked(profileId);
                    string currentEnabled;
                    if (current == null || CriticalThreadRule.Raw(current) != expectedRule)
                        return PerformancePolicy.Text("游戏规则已变化，请重新打开", "The game rule changed; reopen the editor");
                    current.Overrides.TryGetValue(PolicyCatalog.KeyGameScheduling, out currentEnabled);
                    if (currentEnabled != expectedEnabledOverride)
                        return PerformancePolicy.Text("游戏开关已变化，请重新打开", "The game's scheduling switch changed; reopen the editor");
                    CriticalThreadRule stored = CriticalThreadRule.Parse(expectedRule);
                    if (enabledValue && stored != null && stored.IsComparison)
                        return PerformancePolicy.Text("优先级对照已移除，请先选择一种正式策略", "Priority comparison was removed; choose a scheduling strategy first");
                    GameProfile next = current.Clone();
                    if (enabledValue)
                    {
                        CriticalThreadRule runtime = CriticalThreadRule.ParseForRuntime(expectedRule);
                        if (runtime == null || runtime.IsOff)
                            next.Overrides[CriticalThreadRule.Key] = CriticalThreadRule.UnifiedDefault().Encode();
                    }
                    string desired = enabledValue ? "1" : "0";
                    next.Overrides[PolicyCatalog.KeyGameScheduling] = desired;
                    changed = currentEnabled != desired || CriticalThreadRule.Raw(next) != expectedRule;
                    if (changed)
                    {
                        List<GameProfile> snapshot = new List<GameProfile>(profiles);
                        snapshot[snapshot.IndexOf(current)] = next;
                        if (!SaveProfileSnapshotLocked(snapshot))
                            return PerformancePolicy.Text("保存失败，原开关和策略保留", "Save failed; the original switch and strategy were retained");
                        profiles[profiles.IndexOf(current)] = next;
                        if (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId)
                            activeDetection.Profile = next.Clone();
                        if (stickyDetection != null && stickyDetection.Profile != null && stickyDetection.Profile.Id == profileId)
                            stickyDetection.Profile = next.Clone();
                    }
                    if (!enabledValue)
                    {
                        CriticalRuntimeSource source = Volatile.Read(ref criticalSource);
                        affectsSession = source != null && source.Profile == profileId
                            || activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId;
                        DisableSessionCriticalPreferenceLocked(profileId);
                        if (affectsSession) RevokeCriticalThreadIntent("SaveCriticalThreadEnabled");
                    }
                    else if (active && criticalSessionPreference != null && criticalPreferenceSerial == gameSessionStarts
                        && string.Equals(criticalSessionPreference.Id, profileId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (CriticalThreadRule.Raw(criticalSessionPreference) != CriticalThreadRule.Raw(next)
                            || PolicyResolver.For(criticalSessionPreference).ValueOf(PolicyCatalog.KeyGameScheduling) != "1")
                        {
                            CriticalThreadRule desiredRuntime = CriticalThreadRule.ParseForRuntime(CriticalThreadRule.Raw(next));
                            string identity = activeDetection == null ? null : gameSessionStarts + "|" + profileId + "|"
                                + activeDetection.RendererPid + "|" + activeDetection.RendererCreation + "|" + desiredRuntime.Encode();
                            // 关闭后的采集请求不可重新授权。同一身份已经有采集阶段时，保留次数和取消状态，明确等待下局。
                            deferredUntilNextSession = identity != null && threadLayoutCapturePhases.ContainsKey(identity);
                            if (deferredUntilNextSession)
                            {
                                DisableSessionCriticalPreferenceLocked(profileId);
                                Logger.Info("游戏调度 开关已开启；游戏=" + profileId + "；本局原识别阶段已关闭，保留采集预算，下局应用已保存策略");
                            }
                            else
                            {
                                GameProfile sessionNext = criticalSessionPreference.Clone();
                                sessionNext.Overrides[CriticalThreadRule.Key] = CriticalThreadRule.Raw(next);
                                sessionNext.Overrides[PolicyCatalog.KeyGameScheduling] = "1";
                                criticalSessionPreference = sessionNext;
                                // 只替换调度字段，沿用本局其余冻结偏好；不重建阶段或补充首次接管次数。
                                if (sessionPolicy != null && sessionPolicy.ProfileId == profileId)
                                    sessionPolicy = PolicyResolver.For(sessionNext);
                            }
                            affectsSession = true;
                            RevokeCriticalThreadIntent("SaveCriticalThreadEnabled");
                        }
                    }
                }
            }
            finally { if (enabledValue) Monitor.Exit(criticalProcessGate); }
            if (affectsSession) RequestPolicyApply("SaveCriticalThreadEnabled");
            if (changed) RaiseLibraryChanged();
            return null;
        }
        private bool CriticalThreadSnapshotCurrent(CriticalThreadUiState view, string profileId)
        {
            if (!Monitor.IsEntered(sync) || view.ProfileId != profileId || !view.SessionActive || view.SessionToken == Guid.Empty || view.TargetPid <= 0 || view.TargetBirth <= 0 || view.GameSerial != gameSessionStarts || view.IntentEpoch != Interlocked.Read(ref criticalIntentEpoch) || !active || stopping || !enabled || activeDetection == null || activeDetection.Profile == null || activeDetection.Profile.Id != profileId || activeDetection.RendererPid != view.TargetPid || activeDetection.RendererCreation != view.TargetBirth)
            {
                return false;
            }
            GameProfile gameProfile = CriticalPreferenceProfileLocked(profileId);
            if (gameProfile != null)
            {
                return EffectiveGameSchedulingRaw(gameProfile) == view.RuleSignature;
            }
            return false;
        }
        private CriticalRuntimeSource CaptureCriticalThreadSource(out string missing)
        {
            lock (sync)
            {
                GameDetection gameDetection = activeDetection;
                missing = ((!enabled || stopping || panicReq) ? "game mode stopped" : ((!active) ? "session inactive" : (ProfileStoreSaveFailed ? "profile store unavailable" : ((gameDetection == null || gameDetection.Profile == null) ? "no game detection" : ((!gameDetection.RendererCandidateSelected) ? "renderer candidate not selected" : (gameDetection.RendererSafetyOnly ? "renderer is safety-only" : (gameDetection.RequiresGpuConfirm ? "renderer awaits GPU confirmation" : ((gameDetection.RendererPid <= 0 || gameDetection.RendererCreation <= 0 || string.IsNullOrEmpty(gameDetection.RendererPath)) ? "renderer identity incomplete" : null))))))));
                if (missing != null)
                {
                    return null;
                }
                GameProfile gameProfile = CriticalPreferenceProfileLocked(gameDetection.Profile.Id);
                if (gameProfile == null) { missing = "game removed"; return null; }
                string rawRule = EffectiveGameSchedulingRaw(gameProfile);
                CriticalRuntimeSource criticalRuntimeSource = criticalSource;
                if (criticalRuntimeSource != null && criticalRuntimeSource.RuleText == rawRule && criticalRuntimeSource.Profile == gameProfile.Id && criticalRuntimeSource.Pid == gameDetection.RendererPid && criticalRuntimeSource.Birth == gameDetection.RendererCreation && criticalRuntimeSource.Serial == gameSessionStarts && criticalRuntimeSource.Epoch == Interlocked.Read(ref criticalIntentEpoch) && string.Equals(criticalRuntimeSource.Path, gameDetection.RendererPath, StringComparison.OrdinalIgnoreCase))
                {
                    return criticalRuntimeSource;
                }
                if (CriticalThreadRule.Parse(rawRule) == null)
                {
                    missing = "rule unreadable";
                    return null;
                }
                return new CriticalRuntimeSource(gameDetection, rawRule, gameSessionStarts, Interlocked.Read(ref criticalIntentEpoch));
            }
        }

    }
}
