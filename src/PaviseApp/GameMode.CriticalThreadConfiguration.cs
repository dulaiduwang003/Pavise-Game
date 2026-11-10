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
        private CriticalThreadTargetLease CreateCriticalThreadTarget(BoostPass pass, IntPtr process, CriticalThreadRule expectedRule, string expectedProfile, string expectedRawRule, ulong currentProcessMask, long expectedEpoch, long expectedSerial)
        {
            if (!Monitor.IsEntered(criticalProcessGate) || pass == null || process == IntPtr.Zero || expectedRule == null || currentProcessMask == 0 || GetProcessId(process) != (uint)pass.RendererPid || CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
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
            if (string.IsNullOrEmpty(imagePath) || !GamePerformanceTargetPolicy.AllowsGameTarget(imagePath) || !Native.TryQueryAffinity(process, out mask) || mask != currentProcessMask)
            {
                return null;
            }
            lock (sync)
            {
                GameDetection gameDetection = activeDetection;
                GameProfile gameProfile = CriticalPreferenceProfileLocked(expectedProfile);
                if (!enabled || stopping || panicReq || !active || smartSwitchedOut || ProfileStoreSaveFailed || gameDetection == null || gameDetection.Profile == null || gameProfile == null || gameDetection.Profile.Id != gameProfile.Id || !gameDetection.RendererCandidateSelected || gameDetection.RendererSafetyOnly || gameDetection.RequiresGpuConfirm || gameDetection.RendererPid != pass.RendererPid || gameDetection.RendererCreation != currentCreation || !string.Equals(gameDetection.RendererPath, imagePath, StringComparison.OrdinalIgnoreCase) || CriticalThreadRule.Raw(gameProfile) != expectedRawRule || !StoredRuleAdmits(expectedRawRule, expectedRule) || Interlocked.Read(ref criticalIntentEpoch) != expectedEpoch || gameSessionStarts != expectedSerial)
                {
                    return null;
                }
                CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(gameProfile);
                if (criticalThreadAllowedRange.Error != null)
                {
                    return null;
                }
                ulong allowedThreadMask = currentProcessMask & criticalThreadAllowedRange.Mask;
                ulong reservedCores = AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, CpuTopology.PhysicalCoreMasks());
                string validationError = expectedRule.Validate(allMask, CpuTopology.PhysicalCoreMasks(), allowedThreadMask, reservedCores, true);
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
            if (FrameCoordination.ReadCriticalRoles(source.Pid, source.Birth, snapshot, AutoCores.NowTicks)
                .Any(fact => fact.Role == "presentation-associated")) return;
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
            FrameCoordination.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), pid, birth, FrameAutomaticConfiguration());
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
        private void BeginAutomaticCriticalThreadSession(CriticalRuntimeSource source, ThreadTimes snapshot)
        {
            CriticalThreadRoleFact[] roles = FrameCoordination.ReadCriticalRoles(source.Pid, source.Birth, snapshot, AutoCores.NowTicks);
            CriticalAutoTopology topology;
            string refusal;
            if (!ReadCriticalAutoTopology(source, out topology, out refusal))
            {
                criticalAutoSelectionKey = "";
                criticalAutoSelectionStable = 0;
                PublishCriticalThreadState(source, "unsupported", refusal, false, false, 0, 0L);
                return;
            }
            int capacity = CriticalThreadAutoPolicy.Capacity(topology.Physical, topology.Allowed, topology.Reserved, topology.Performance);
            if (capacity < 1)
            {
                criticalAutoSelectionKey = "";
                criticalAutoSelectionStable = 0;
                PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text("可用物理核心不足：至少需要三个不同物理核，其中两个留给其他线程", "Not enough available physical cores: at least three distinct physical cores are needed, two of them left for other threads"), false, false, 0, 0L);
                return;
            }
            CriticalThreadHotspot[] hotspots = criticalObservation.Hotspots;
            bool pooled;
            HardThreadIdentity primary = CriticalThreadAutoPolicy.SelectPrimary(hotspots, roles, out pooled);
            if (primary == null)
            {
                criticalAutoSelectionKey = "";
                criticalAutoSelectionStable = 0;
                string waiting = (hotspots.Length == 0)
                    ? PerformancePolicy.Text("游戏线程尚未持续占用至少 30% 单核，暂不绑定", "No game thread keeps at least 30% of one core busy yet; nothing is bound")
                    : (pooled
                        ? PerformancePolicy.Text("最忙的几个线程负载接近，像可互换的工作线程，交给系统调度，不硬绑定", "The busiest threads carry similar loads like an interchangeable worker pool; they are left to the scheduler and not bound")
                        : ((roles.Length == 0)
                            ? PerformancePolicy.Text("正在识别渲染线程；识别不到时，最忙的线程持续 30 秒后再绑定", "Identifying the render thread; without it, the busiest thread is bound after 30 seconds of sustained load")
                            : PerformancePolicy.Text("等待线程持续占用至少 30% 单核", "Waiting for a thread to keep at least 30% of one core busy")));
                PublishCriticalThreadState(source, "observing", waiting, false, false, 0, 0L);
                return;
            }
            HardThreadIdentity[] secondary = CriticalThreadAutoPolicy.SelectSecondary(hotspots, primary, capacity - 1);
            HardThreadIdentity[] threads = new HardThreadIdentity[1] { primary }.Concat(secondary).ToArray();
            string key = source.Pid + ":" + source.Birth + "|" + primary.Key + "|" + string.Join(",", secondary.Select((HardThreadIdentity x) => x.Key).OrderBy((string x) => x, StringComparer.Ordinal).ToArray());
            if (key == criticalAutoSelectionKey)
            {
                criticalAutoSelectionStable++;
            }
            else
            {
                criticalAutoSelectionKey = key;
                criticalAutoSelectionStable = 1;
            }
            if (criticalAutoSelectionStable < 2)
            {
                PublishCriticalThreadState(source, "observing", (secondary.Length == 0)
                    ? PerformancePolicy.Text("已选定主线程，确认稳定后硬绑定", "Main thread chosen; it is bound once the choice is stable")
                    : PerformancePolicy.Text("已选定主线程，另有 " + secondary.Length + " 个接近跑满的线程将设为优先核（本机最多 " + capacity + " 个），确认稳定后一并施加", "Main thread chosen, plus " + secondary.Length + " near-saturated thread(s) for preferred cores (up to " + capacity + " on this PC); applied together once stable"), false, false, 0, 0L);
                return;
            }
            criticalAutoSelectionKey = "";
            criticalAutoSelectionStable = 0;
            CriticalThreadRule rule;
            HardThreadChoice[] choices;
            HardThreadChoice[] hints;
            if (!ResolveCriticalAutoRule(source, threads, topology, out rule, out choices, out hints, out refusal))
            {
                PublishCriticalThreadState(source, "unsupported", refusal, false, false, 0, 0L);
                return;
            }
            BindCriticalThreads(source, snapshot, rule, choices, roles);
            if (criticalSession != null && criticalPlan != null && criticalThreadUi.Applied)
            {
                string soft = (hints.Length == 0) ? "" : criticalIdealHints.Apply(source.Pid, source.Birth, hints);
                Logger.Info("线程 " + (source.Rule.ManualTarget
                    ? PerformancePolicy.Text("关键线程已手动选核：", "关键线程已放置到手动选择的处理器： ")
                    : PerformancePolicy.Text("关键线程已自动选核：", "关键线程已放置到自动选择的核心： ")) + source.Name
                    + " 硬绑定 " + string.Join(", ", choices.Select((HardThreadChoice x) => "线程编号 " + x.Thread.Tid + " -> " + CpuTopology.DescribeMask(x.Mask)).ToArray())
                    + (hints.Length == 0 ? "" : " | 软提示 " + string.Join(", ", hints.Select((HardThreadChoice x) => "线程编号 " + x.Thread.Tid + " -> " + CpuTopology.DescribeMask(x.Mask)).ToArray()) + " [" + soft + "]")
                    + (source.Rule.WorkerAvoidance ? " 工作线程避让" : " 仅候选线程；清核=关闭") + " 硬绑定=" + choices.Length + " 软提示=" + criticalIdealHints.Count + "/" + hints.Length + " 容量=" + capacity);
            }
        }
        private bool ReadCriticalAutoTopology(CriticalRuntimeSource source, out CriticalAutoTopology topology, out string refusal)
        {
            topology = null;
            refusal = null;
            if (IntPtr.Size != 8 || CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
            {
                refusal = PerformancePolicy.Text("处理器拓扑或处理器组暂不支持", "CPU topology or processor groups are unsupported");
                return false;
            }
            GameProfile profile;
            lock (sync)
            {
                profile = CriticalPreferenceProfileLocked(source.Profile);
                if (profile != null) profile = profile.Clone();
            }
            CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(profile);
            if (criticalThreadAllowedRange.Error != null)
            {
                refusal = criticalThreadAllowedRange.Error;
                return false;
            }
            ulong mask;
            if (!ReadCriticalProcessMask(source.Pid, source.Birth, out mask))
            {
                refusal = PerformancePolicy.Text("无法读取游戏进程的处理器亲和性；未施加线程硬绑定", "The game's processor affinity cannot be read; no hard thread binding is applied");
                return false;
            }
            topology = new CriticalAutoTopology
            {
                Physical = CpuTopology.PhysicalCoreMasks(),
                Domains = CpuTopology.L3Masks(),
                Allowed = mask & criticalThreadAllowedRange.Mask,
                Reserved = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask,
                Performance = (CpuTopology.Hybrid ? CpuTopology.PerfMask : 0),
                PreferredDomain = (CpuTopology.AsymCache ? CpuTopology.BigL3Mask : 0)
            };
            return true;
        }
        private bool ResolveCriticalAutoRule(CriticalRuntimeSource source, HardThreadIdentity[] threads, CriticalAutoTopology topology, out CriticalThreadRule rule, out HardThreadChoice[] choices, out HardThreadChoice[] hints, out string refusal)
        {
            rule = null;
            choices = null;
            hints = null;
            refusal = null;
            refusal = source.Rule.Validate(allMask, topology.Physical, topology.Allowed, topology.Reserved, true);
            if (refusal != null) return false;
            // 核心不足时保留线程优先顺序，先舍弃优先级较低的软提示。
            ulong[] destinations = null;
            for (int count = Math.Min(threads.Length, CriticalThreadAutoPolicy.MaximumIsolated); count >= 1 && destinations == null; count--)
            {
                destinations = CriticalThreadAutoPolicy.Destinations(source.Rule, count, topology.Physical, topology.Allowed, topology.Reserved, CpuTopology.FavoredMask, topology.Performance, topology.Domains, topology.PreferredDomain, CpuTopology.ThrottleMask);
            }
            if (destinations == null)
            {
                refusal = PerformancePolicy.Text("可用物理核心不足：至少选中三个不同物理核，每核可只选一个逻辑线程", "Not enough available physical cores: select at least three distinct physical cores; one logical processor per core is enough");
                return false;
            }
            CriticalThreadRule criticalThreadRule = new CriticalThreadRule(ManualCorePlan.Stamp(allMask, topology.Physical), source.Rule.WorkerAvoidance, new ulong[1] { destinations[0] });
            refusal = criticalThreadRule.Validate(allMask, topology.Physical, topology.Allowed, topology.Reserved, true);
            if (refusal != null)
            {
                return false;
            }
            HardThreadIdentity[] chosen = threads;
            rule = criticalThreadRule;
            choices = new HardThreadChoice[1] { new HardThreadChoice(chosen[0], destinations[0]) };
            hints = destinations.Skip(1).Select((ulong mask, int index) => new HardThreadChoice(chosen[index + 1], mask)).ToArray();
            return true;
        }
        private static bool ReadCriticalProcessMask(int pid, long birth, out ulong mask)
        {
            mask = 0uL;
            IntPtr processHandle = Native.OpenProcess(4096, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return Native.QueryProcessIdentityState(processHandle, birth) == 0 && Native.TryQueryAffinity(processHandle, out mask) && mask != 0;
            }
            finally
            {
                Native.CloseHandle(processHandle);
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
            CriticalThreadRule criticalThreadRule = CriticalThreadRule.ParseForRuntime(CriticalThreadRule.Raw(profile));
            if (criticalThreadRule == null)
            {
                return new CriticalThreadUiState(profileId, "off", PerformancePolicy.Text("未启用线程硬绑定", "Thread hard affinity is off"), false, false, false, 0, 0L, 0uL, null, null, false, false, CriticalThreadCoverageState.None, null, 0L, 0L, 0, 0L);
            }
            return new CriticalThreadUiState(profileId, "waiting-game", criticalThreadRule.Automatic
                ? PerformancePolicy.Text("下次游戏运行时自动识别关键线程并绑定独立物理核心，其他接近满载的线程设为优先核", "The next game session binds the critical thread to its own physical core and gives other near-saturated threads a preferred core")
                : PerformancePolicy.Text("保存的规则在下次有效游戏会话中核验", "The saved rule is checked in the next valid game session"), true, false, false, 0, 0L, 0uL, null, null, false, criticalThreadRule.WorkerAvoidance, CriticalThreadCoverageState.None, null, 0L, 0L, 0, 0L);
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
            if (rule != null && rule.Automatic && !rule.ManualTarget) rule = CriticalThreadRule.Auto(true);
            bool affectsSession = false;
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
                CriticalThreadRule savedRule = CriticalThreadRule.ParseForRuntime(CriticalThreadRule.Raw(gameProfile));
                if (rule != null && savedRule != null && rule.Encode() == savedRule.Encode()) return null;
                if (rule != null)
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
                GameProfile profile = gameProfile.Clone();
                if (rule == null)
                {
                    profile.Overrides.Remove("GameCriticalThreadHardRuleV1");
                }
                else
                {
                    profile.Overrides["GameCriticalThreadHardRuleV1"] = rule.Encode();
                }
                if (CriticalThreadRule.Raw(profile) == CriticalThreadRule.Raw(gameProfile)) return null;
                List<GameProfile> profilesSnapshot = new List<GameProfile>(profiles);
                profilesSnapshot[profilesSnapshot.IndexOf(gameProfile)] = profile;
                if (!SaveProfileSnapshotLocked(profilesSnapshot))
                {
                    return PerformancePolicy.Text("保存失败，原规则保留", "Save failed; the original rule was retained");
                }
                profiles[profiles.IndexOf(gameProfile)] = profile;
                if (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId)
                {
                    activeDetection.Profile = profile.Clone();
                }
                if (stickyDetection != null && stickyDetection.Profile != null && stickyDetection.Profile.Id == profileId)
                {
                    stickyDetection.Profile = profile.Clone();
                }
                CriticalRuntimeSource source = Volatile.Read(ref criticalSource);
                affectsSession = rule == null && ((source != null && source.Profile == profileId)
                    || (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId));
                if (rule == null) DisableSessionCriticalPreferenceLocked(profileId);
                if (affectsSession) RevokeCriticalThreadIntent("SaveCriticalThreadRule");
            }
            if (affectsSession) RequestPolicyApply("SaveCriticalThreadRule");
            RaiseLibraryChanged();
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
                return CriticalThreadRule.Raw(gameProfile) == view.RuleSignature;
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
                string rawRule = CriticalThreadRule.Raw(gameProfile);
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
