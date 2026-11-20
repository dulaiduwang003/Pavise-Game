using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool ComputeAuditDue(int pid, BoostPass pass, out bool known, out bool needTweak, out bool needPlacement)
        {
            lock (sync)
            {
                known = gameBoost.ContainsKey(pid);
                needTweak = !tweakApplied.Contains(pid);
                ulong value;
                needPlacement = pass.Exclusive && !placementGaveUp.Contains(pid) && (!gamePlacement.TryGetValue(pid, out value) || value != pass.DesiredMask);
                long value2;
                return !known || needTweak || needPlacement || !gameBoostNextAudit.TryGetValue(pid, out value2) || DateTime.UtcNow.Ticks >= value2;
            }
        }
        private void HandlePidReuse(int pid, long currentCreation, ref bool known, ref bool needPlacement)
        {
            if (!known)
            {
                return;
            }
            bool staleBoostRemoved = false;
            Snap value;
            lock (sync)
            {
                if (gameBoost.TryGetValue(pid, out value) && value.Creation > 0 && value.Creation != currentCreation)
                {
                    gameBoost.Remove(pid);
                    gameGpu.Remove(pid);
                    gamePlacement.Remove(pid);
                    boostFail.Remove(pid);
                    ForgetHardPin(pid);
                    boostStateWarned.Remove(pid);
                    boostStateVerified.Remove(pid);
                    gameBoostNextAudit.Remove(pid);
                    boostStateFail.Remove(pid);
                    boostHandleStripped.Remove(pid);
                    boostEcoGaveUp.Remove(pid);
                    placementFail.Remove(pid);
                    placementGaveUp.Remove(pid);
                    ForgetNvTweakLocked(pid);
                    staleBoostRemoved = true;
                    known = false;
                    ForgetBoostActionTarget(pid);
                }
            }
            if (staleBoostRemoved)
            {
                needPlacement = true;
                CrashGuard.ReleaseBoostProcess(pid, value.Creation);
            }
        }
        private bool CaptureAndTrack(IntPtr h, int pid, long currentCreation, BoostPass pass, bool known, out bool newlyTracked, out bool gpuOk, bool applyGpu = true)
        {
            newlyTracked = false;
            gpuOk = false;
            pass.ActionTarget = BoostActionTarget(h);
            if (known)
            {
                lock (sync)
                {
                    if (!string.IsNullOrEmpty(pass.ActionTarget))
                    {
                        boostActionTargets[pid] = pass.ActionTarget;
                    }
                }
            }
            else
            {
                uint priValue = Native.GetPriorityClass(h);
                if (priValue == 0)
                {
                    return false;
                }
                ulong affValue = Native.QueryAffinity(h);
                uint[] cpuSetsValue = Native.QueryCpuSets(h);
                if (cpuSetsValue == null)
                {
                    Logger.Warn(Logger.Format("{0}{1} 进程编号 {2}", "{0}{1} pid {2}", Lang.T("log.gamemodeboost.9"), pass.RendererName, pid));
                    return false;
                }
                int ioValue = Native.QueryIoPriority(h);
                if (ioValue < 0)
                {
                    return false;
                }
                int cls;
                bool gpuPriorityRead = Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) == 0;
                if (!gpuPriorityRead)
                {
                    cls = -1;
                }
                int controlMask;
                int stateMask;
                if (!Native.TryQueryPowerThrottling(h, out controlMask, out stateMask))
                {
                    if (Native.PowerThrottlingSupported)
                    {
                        return false;
                    }
                    controlMask = -1;
                    stateMask = -1;
                }
                CrashGuard.OriginalBoostState recovered;
                if (!CrashGuard.MarkBoostProcess(pid, currentCreation, pass.RendererName, priValue, affValue, ioValue, cls, cpuSetsValue, controlMask, stateMask, out recovered))
                {
                    Logger.Log(() => Logger.Format("{0}{1} 进程编号 {2}", "{0}{1} pid {2}", Lang.T("log.gamemodeboost.10"), pass.RendererName, pid));
                    return false;
                }
                if (recovered != null)
                {
                    priValue = recovered.Priority;
                    affValue = recovered.Affinity;
                    ioValue = recovered.Io;
                    cls = recovered.Gpu;
                    gpuPriorityRead = cls >= 0;
                    cpuSetsValue = recovered.CpuSets;
                    controlMask = recovered.QoSControl;
                    stateMask = recovered.QoSState;
                }
                Snap snap = default(Snap);
                snap.Pri = priValue;
                snap.Aff = affValue;
                snap.Io = ioValue;
                snap.Name = pass.RendererName;
                snap.Creation = currentCreation;
                snap.CpuSets = cpuSetsValue;
                snap.QoSControl = controlMask;
                snap.QoSState = stateMask;
                Snap value2 = snap;
                lock (sync)
                {
                    gameBoost[pid] = value2;
                    if (!string.IsNullOrEmpty(pass.ActionTarget))
                    {
                        boostActionTargets[pid] = pass.ActionTarget;
                    }
                    if (gpuPriorityRead)
                    {
                        gameGpu[pid] = cls;
                    }
                }
                newlyTracked = true;
            }
            uint granted;
            if (Native.HandleWriteAccessStripped(h, out granted))
            {
                bool wasAdded;
                lock (sync)
                {
                    wasAdded = boostHandleStripped.Add(pid);
                }
                pass.WriteDenied = true;
                if (wasAdded)
                {
                    OnGameHandleStripped(pid, pass.RendererName, granted);
                }
            }
            if (applyGpu)
            {
                gpuOk = ApplySelectedBoostAction(h, pid, pass, "GameGpuPriority", pass.GpuBoost, (long value) => value == 4, () => WriteBoostAction("GameGpuPriority", h, 4L));
            }
            return true;
        }
        // 对局中锁定游戏的高性能 QoS 关闭执行速度节流 Win11 上保留它的计时器精度请求 避免失焦或被遮挡时降到能效核 限帧计时变粗
        // 原状态记入回执 关闭调度 换游戏或退局时按回执恢复 不属于资源优先级开关 不能走它们的入口 那里不认识这个名字会抛异常
        private bool SyncGameQos(IntPtr h, int pid, BoostPass pass)
        {
            if (!Native.PowerThrottlingSupported)
            {
                return true;
            }
            if (pass.QosBoost)
            {
                bool applied = ApplyIsolatedBoostAction(h, pid, pass, GameEcoAction,
                    (long value) => Native.HighQoSMasksOk((int)(value >> 32), (int)value), () => WriteHighQoSBoost(h));
                if (applied)
                {
                    lock (sync)
                    {
                        HashSet<string> selected;
                        if (!boostResourceSelections.TryGetValue(pid, out selected))
                        {
                            selected = boostResourceSelections[pid] = new HashSet<string>(StringComparer.Ordinal);
                        }
                        selected.Add(GameEcoAction);
                    }
                }
                return applied;
            }
            bool owned;
            lock (sync)
            {
                HashSet<string> selected, pending;
                owned = (boostResourceSelections.TryGetValue(pid, out selected) && selected.Contains(GameEcoAction))
                    || (boostActionRestorePending.TryGetValue(pid, out pending) && pending.Contains(GameEcoAction));
            }
            if (owned && RestoreBoostAction(h, pid, pass.RendererCreation, GameEcoAction))
            {
                lock (sync)
                {
                    HashSet<string> selected;
                    if (boostResourceSelections.TryGetValue(pid, out selected)) selected.Remove(GameEcoAction);
                }
                if (!string.IsNullOrEmpty(pass.ActionTarget)) ActionFailureStore.CancelApply(GameEcoAction, pass.ActionTarget);
            }
            return false;
        }
        private void OnGameHandleStripped(int pid, string rendererName, uint granted)
        {
            Logger.Log(() => Logger.Format("{0}{1} 进程编号 {2}{3}{4}{5}{6}", "{0}{1} pid {2}{3}{4}{5}{6}", Lang.T("log.gamemodeboost.3"), rendererName, pid, Lang.T("log.gamemodeboost.34"), Lang.T("log.gamemodeboost.35"), granted.ToString("X"), Lang.T("log.gamemodeboost.36")));
            ProtectedGameRoster.Remember(rendererName);
        }
            // 省电状态仅读取 不以未修改判为失败
        private static string EcoStateText(IntPtr process)
        {
            return Lang.T("t.gamemodeboost.39") + QoSDump(process);
        }
        private static bool StoredRuleAdmits(string storedRaw, CriticalThreadRule bound)
        {
            CriticalThreadRule criticalThreadRule = CriticalThreadRule.ParseForRuntime(storedRaw);
            if (criticalThreadRule != null)
            {
                return criticalThreadRule.Admits(bound);
            }
            return false;
        }
        private static void PurgeRetiredIrqObservation(string dir)
        {
            Settings.Remove("IrqSessionProbe");
            Settings.Remove("IrqObserveSkipsV1");
            Settings.Remove("IrqLastCaptureResultV1");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }
            try
            {
                if (!Directory.Exists(dir))
                {
                    return;
                }
                string[] retiredIrqObservationFiles = RetiredIrqObservationFiles;
                foreach (string searchPattern in retiredIrqObservationFiles)
                {
                    string[] files = Directory.GetFiles(dir, searchPattern);
                    foreach (string path in files)
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }
        private static string StripExe(string s)
        {
            if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
            return s.Substring(0, s.Length - 4);
        }
        internal static bool ForegroundConsumesService(string foregroundName, string envKey)
        {
            if (string.IsNullOrEmpty(foregroundName) || string.IsNullOrEmpty(envKey))
            {
                return false;
            }
            string toLowerInvariantResult = WhitelistRule.NormalizeName(foregroundName).ToLowerInvariant();
            string[][] foregroundServiceConsumers = ForegroundServiceConsumers;
            foreach (string[] textEntriesEntry in foregroundServiceConsumers)
            {
                if (textEntriesEntry[0] == toLowerInvariantResult && textEntriesEntry[1] == envKey)
                {
                    return true;
                }
            }
            return false;
        }
        private bool SwitchOutDebounceElapsed(long nowTicks)
        {
            if (smartSwitchedOut)
            {
                return sessionForeground.DebounceElapsed(nowTicks, SwitchOutDebounceTicks);
            }
            return false;
        }
        // 本轮资源准备得出的游戏线程提优结论 线程协同工作线程的准入委托只读它 不拿会话锁
        private bool classicThreadBoostOn;

        // 与资源准备同一判定 本局有游戏 且这款游戏没有在深度调优里关掉游戏线程提优
        // 供会话起点这类尚未跑过资源准备的入口使用
        private bool ClassicThreadBoostWanted()
        {
            lock (sync)
            {
                if (activeDetection == null || activeDetection.Profile == null) return false;
                return PolicyResolver.For(CriticalPreferenceProfileLocked(activeDetection.Profile.Id)).ThreadBoostOn;
            }
        }

        // 线程协同与亲和性的角色采集共用 FrameCoordination 唯一的采集线程 线程协同生效后会一直占着它做租约核验
        // 亲和性要先采集角色才能固定核心 所以它还在等采集时线程协同先不启动 等待有上限
        // 角色已选定 本阶段转为只观察 首次接管额度或采集额度用尽 或从本局首次让出起超过上限 都会放行线程协同
        // 只在本局会话循环线程上调用 与统一调度的步进同一线程 不另加锁
        internal static readonly long PlacementCaptureWaitTicks = TimeSpan.FromSeconds(90).Ticks;
        private long placementWaitSerial, placementWaitBirth, placementWaitStarted;
        private int placementWaitPid;
        // 统一调度最近一次步进时仍在等角色采集的亲和性来源 不在等时为 null
        private CriticalRuntimeSource placementCaptureSource;

        private bool PlacementCaptureFirst(BoostPass pass)
        {
            if (!pass.ClassicThreadBoost || !pass.PlacementConfigured || pass.RendererPid <= 0 || pass.RendererCreation <= 0) return false;
            long serial = Interlocked.Read(ref gameSessionStarts);
            long now = AutoCores.NowTicks;
            if (placementWaitSerial != serial || placementWaitPid != pass.RendererPid || placementWaitBirth != pass.RendererCreation)
            {
                placementWaitSerial = serial; placementWaitPid = pass.RendererPid; placementWaitBirth = pass.RendererCreation;
                placementWaitStarted = now;
            }
            if (now - placementWaitStarted > PlacementCaptureWaitTicks) return false;
            CriticalRuntimeSource source = criticalSource;
            // 统一调度还没评估这个渲染进程 先让一轮 等它决定是否要采集
            if (source == null || source.Pid != pass.RendererPid || source.Birth != pass.RendererCreation) return true;
            return object.ReferenceEquals(placementCaptureSource, source);
        }

        // 游戏线程提优（线程协同） 渲染进程确认后立即采样 呈现线程设为 TIME_CRITICAL 配合线程 +1
        // 不固定核心 也不等前台 识别额度用尽后才改由备用提优选最忙线程 两者互斥
        // 关闭时这两条写入器只归还自己的旧记录 亲和性的角色观察走独立的只读入口
        private void SyncFrameCoordination(BoostPass pass)
        {
            if (!pass.ClassicThreadBoost)
            {
                bool laneClean = RenderLane.Release();
                bool frameClean = FrameCoordination.ReleaseBoost(pass.RendererPid, pass.RendererCreation);
                pass.LaneRestorationReady = laneClean && frameClean;
                return;
            }
            // 亲和性还在等角色采集时 先把唯一的采集通道让给它 有界等待 见 PlacementCaptureFirst
            // 这期间不启动线程协同和备用提优 进程 磁盘与显卡提优照常
            if (PlacementCaptureFirst(pass))
            {
                pass.LaneRestorationReady = false;
                return;
            }
            bool criticalRolesWanted = CriticalRolesWanted(pass.RendererPid, pass.RendererCreation);
            UpdateLaneFallback(pass.RendererPid, pass.RendererCreation, pass.RendererName);
            bool frameOn = !LaneFallbackFor(pass.RendererPid, pass.RendererCreation);
            if (frameOn && (!RenderLane.Release() || RenderLane.HasResidue()))
            {
                // 备用提优的旧写入尚未归还前不启动呈现线程提优 避免两条写入器同时持有线程
                pass.LaneRestorationReady = false;
                if (criticalRolesWanted)
                {
                    FrameCoordination.ReleaseBoost(pass.RendererPid, pass.RendererCreation);
                }
                else
                {
                    FrameCoordination.ReleaseExcept(0, 0L);
                }
                return;
            }
            int pid = pass.RendererPid;
            long birth = pass.RendererCreation;
            int epoch = Volatile.Read(ref rendererHandoffEpoch);
            Func<bool> admitted = delegate
            {
                GameDetection gameDetection = Volatile.Read(ref activeDetection);
                return Volatile.Read(ref classicThreadBoostOn) && enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed && !pass.WriteDenied
                    && epoch == Volatile.Read(ref rendererHandoffEpoch) && gameDetection != null && gameDetection.RendererCandidateSelected
                    && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && gameDetection.RendererPid == pid && gameDetection.RendererCreation == birth
                    && ThreadBoostRecoveryAllowed(pid, birth);
            };
            if (frameOn && pid > 0 && birth > 0 && admitted())
            {
                FrameCoordination.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), pid, birth, FrameAutomaticConfiguration());
                FrameCoordination.Ensure(pid, birth, pass.RendererName, admitted);
            }
            else
            {
                pass.LaneRestorationReady = criticalRolesWanted ? FrameCoordination.ReleaseBoost(pid, birth) : FrameCoordination.ReleaseExcept(0, 0L);
            }
        }
        private void InvalidateOverrideWorkLocked(string key)
        {
            if (key == "GmStandbyCleanerV2")
            {
                InvalidateStandbyCleanerWork();
            }
        }
        private void NotifyOverridePolicyChanged(string key, bool effectiveOn)
        {
            if (key == PolicyCatalog.KeyGameScheduling && !effectiveOn && criticalOriginalTarget != null && criticalOriginalTarget.Revoked)
                StopCriticalThreads(1000);
            if (key == "GmAutoCoresV2" || key == "GmManualCorePlanV2")
            {
                RaiseLibraryChanged();
                return;
            }
            if (key == "GameDisableFullscreenOptimizationsV1")
            {
                AutomaticFso.Invalidate();
                AutomaticFso.Sync(true, -1L);
                RaiseLibraryChanged();
                return;
            }
            if (key == "GameNvRebarV1")
            {
                lock (sync)
                {
                    if (sessionPolicy != null)
                    {
                        sessionPolicy = PolicyResolver.For(FindProfileLocked(sessionPolicy.ProfileId));
                    }
                }
            }
            switch (key)
            {
            case "GmPauseServices":
                PauseServicesPolicyChanged(effectiveOn);
                break;
            case "GmStandbyCleanerV2":
                StandbyCleanerPolicyChanged(effectiveOn);
                break;
            case "PowerPlanOn":
            case "GameNvRebarV1":
            case "GmAutoCoresV2":
            case "GmManualCorePlanV2":
            case "PerformancePreset":
                RequestPolicyApply("NotifyOverridePolicyChanged");
                break;
            }
        }
        internal static WhitelistRuleKind ResolveAutoKind(string executablePath)
        {
            if (!WhitelistRule.IsUnsafeFamilyAnchor(executablePath))
            {
                return WhitelistRuleKind.ApplicationFamily;
            }
            return WhitelistRuleKind.ExactPath;
        }
        private static string DisplayName(string executablePath, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback.Trim();
            }
            try
            {
                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                string fileDescription = ((!string.IsNullOrWhiteSpace(versionInfo.FileDescription)) ? versionInfo.FileDescription : versionInfo.ProductName);
                if (!string.IsNullOrWhiteSpace(fileDescription))
                {
                    return fileDescription.Trim();
                }
            }
            catch
            {
            }
            return Path.GetFileNameWithoutExtension(executablePath);
        }
        private static string NormalizeGameRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }
            try
            {
                string getFullPathTrimmed = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
                return FamilyBoundary.SafeFamilyDir(getFullPathTrimmed) ? getFullPathTrimmed : null;
            }
            catch
            {
                return null;
            }
        }
        private Dictionary<int, PlatformProcessOwner> CollectPlatformMembers(ProcessSnapshot snapshot)
        {
            HashSet<int> hashSet = new HashSet<int>();
            HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (sync)
            {
                if (activeDetection != null && activeDetection.RendererPid > 4)
                {
                    hashSet.Add(activeDetection.RendererPid);
                }
                foreach (GameProfile profile in profiles)
                {
                    if (!string.IsNullOrEmpty(profile.ExecutablePath))
                    {
                        hashSet2.Add(profile.ExecutablePath);
                    }
                    if (!string.IsNullOrEmpty(profile.LearnedExecutablePath))
                    {
                        hashSet2.Add(profile.LearnedExecutablePath);
                    }
                }
            }
            if (snapshot != null)
            {
                ProcEntry[] entries = snapshot.Entries;
                foreach (ProcEntry procEntry in entries)
                {
                    if (procEntry != null && (hashSet2.Contains(procEntry.Path) || (PlatformRuntimePolicy.RuntimePlatform(procEntry.Name) == null && FamilyBoundary.CatalogProtected(procEntry.Pid, procEntry.Creation, procEntry.Name, procEntry.Path))))
                    {
                        hashSet.Add(procEntry.Pid);
                    }
                }
            }
            platformMembers = PlatformRuntimePolicy.Collect(snapshot, selfPid, selfSession, hashSet, platformMembers);
            return platformMembers;
        }
        private static bool PlatformRuleEnabled(PlatformProcessOwner owner)
        {
            if (owner.Platform == "wegame")
            {
                return PlatformRuntimePolicy.Enabled(owner.Platform);
            }
            return false;
        }
        private void AppendPlatformRuleViews(List<WhitelistRuleView> views, Dictionary<int, PlatformProcessOwner> members)
        {
            string[] ids = PlatformRuntimePolicy.Ids;
            foreach (string entryText in ids)
            {
                if (!PlatformRuntimePolicy.Enabled(entryText))
                {
                    continue;
                }
                int processedCount = ((members == null) ? (-1) : 0);
                if (members != null)
                {
                    foreach (PlatformProcessOwner value in members.Values)
                    {
                        if (value.Platform == entryText)
                        {
                            processedCount++;
                        }
                    }
                }
                WhitelistRule rule;
                if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, entryText, out rule))
                {
                    views.Add(new WhitelistRuleView(rule, processedCount, false, entryText));
                }
            }
        }
        private bool RemovePlatformProtection(string id)
        {
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (stopping)
                    {
                        return false;
                    }
                    if (!PlatformRuntimePolicy.TryRemove(id))
                    {
                        whitelistLastError = Lang.T("white.save.failed");
                        return false;
                    }
                    whiteRevision++;
                    whitelistLastError = "";
                }
            }
            RequestPolicyApply("RemovePlatformProtection");
            return true;
        }
        private void UpdateArmedStatus(string name, string via, bool engaged)
        {
            armedGameName = name;
            if (!string.Equals(name, lastArmedLogged, StringComparison.OrdinalIgnoreCase))
            {
                if (name != null)
                {
                    Logger.Log(() => Lang.T("log.gamemode.54") + name + Lang.T("log.gamemode.55") + (string.IsNullOrEmpty(via) ? "" : (Lang.T("log.gamemode.62") + via)));
                }
                else if (lastArmedLogged != null && !engaged)
                {
                    Logger.Log(() => Lang.T("log.gamemode.56") + lastArmedLogged + Lang.T("log.gamemode.57"));
                    ReleasePreStagedTuning();
                }
                lastArmedLogged = name;
            }
        }
        public bool NeedsLauncherChildParentIdentity(int eventParentPid, string eventName, int session)
        {
            if (session != selfSession || eventParentPid <= 0)
            {
                return false;
            }
            lock (sync)
            {
                return ShouldCaptureLauncherParentIdentity(activeDetection, eventParentPid);
            }
        }
        internal static bool ShouldCaptureLauncherParentIdentity(GameDetection detection, int eventParentPid)
        {
            if (detection != null && eventParentPid > 0 && detection.RendererPid == eventParentPid)
            {
                return detection.RendererCreation > 0;
            }
            return false;
        }
        private static long ElapsedMsOrMax(long last, long elapsedTicks)
        {
            if (last <= 0 || elapsedTicks < 0)
            {
                return long.MaxValue;
            }
            return elapsedTicks / 10000;
        }
        private static int TicksToWaitMilliseconds(long ticks)
        {
            if (ticks <= 0)
            {
                return 1;
            }
            long waitMilliseconds = (ticks + 10000 - 1) / 10000;
            return (int)Math.Min(2147483647L, Math.Max(1L, waitMilliseconds));
        }
        internal static string SeverityTagFor(string reason)
        {
            switch (reason)
            {
            case "Error":
            case "QueueFailed":
            case "SaveFailed":
                return "FAIL ";
            case "GpuUnavailable":
            case "FileUnavailable":
            case "InvalidEvidence":
            case "FileChanged":
                return "WARN ";
            default:
                return "INFO ";
            }
        }
        private void PrepareResourcePriorities(BoostPass pass, PolicySnapshot policy)
        {
            // 进程 CPU 类 磁盘 显卡优先级和高性能 QoS 归性能优先级 线程提优归游戏线程提优 两项都只有逐游戏开关
            // 渲染线程亲和性不参与这里的写入
            // 这款游戏关掉时不写 已有的本方收据在本轮按原归属恢复
            pass.CpuBoost = pass.IoBoost = pass.GpuBoost = pass.QosBoost = false;
            pass.ClassicThreadBoost = false;
            pass.PlacementConfigured = false;
            lock (sync)
            {
                if (activeDetection != null && activeDetection.Profile != null)
                {
                    GameProfile sessionProfile = CriticalPreferenceProfileLocked(activeDetection.Profile.Id);
                    PolicySnapshot game = PolicyResolver.For(sessionProfile);
                    bool performance = game.BoostGame;
                    pass.CpuBoost = pass.IoBoost = pass.GpuBoost = performance;
                    // 高性能 QoS 跟随性能优先级 游戏没焦点或被遮挡时不被系统降到能效核 Win11 上它的计时器请求照常生效
                    pass.QosBoost = performance;
                    pass.ClassicThreadBoost = game.ThreadBoostOn;
                    CriticalThreadRule sessionRule = PolicyResolver.GameSchedulingRule(sessionProfile);
                    pass.PlacementConfigured = sessionRule != null && !sessionRule.IsOff;
                }
            }
            // 线程协同的工作线程每 200 毫秒复核一次准入 这里只发布本轮结论 不让它去拿会话锁
            Volatile.Write(ref classicThreadBoostOn, pass.ClassicThreadBoost);
            int combinedMask = (pass.CpuBoost ? 1 : 0) | (pass.ClassicThreadBoost ? 2 : 0) | (pass.IoBoost ? 4 : 0) | (pass.GpuBoost ? 8 : 0) | (pass.QosBoost ? 16 : 0);
            lock (sync)
            {
                if (combinedMask != boostResourceSelection)
                {
                    boostResourceSelection = combinedMask;
                    gameBoostNextAudit.Clear();
                    boostStateVerified.Clear();
                }
            }
        }
        private void RecordRuntimeProtection(ProcEntry process, BackgroundReleaseState backgroundState)
        {
            string source = "platform-runtime-" + process.Pid + "-" + process.Creation;
            if (!RuntimeBackgroundExempt(backgroundState))
            {
                string getFileNameOffset = "pid=" + process.Pid + " born=" + process.Creation + " exe=" + Path.GetFileName(process.Path ?? "");
                string backgroundStateOffset = " background=" + backgroundState;
                Logger.RepeatedError(source, Logger.Format("自动 [{0}] [平台] 等待恢复 {1}{2}", "AUTO [{0}] [PLATFORM] restore-pending {1}{2}", AutomationSession, getFileNameOffset, backgroundStateOffset), -1L);
            }
            else
            {
                Logger.RepeatedRecovered(source);
            }
        }
        private static string FmtDur(TimeSpan t)
        {
            if (t.TotalHours >= 1.0)
            {
                return (int)t.TotalHours + "h" + t.Minutes.ToString("00") + "m";
            }
            if (t.TotalMinutes >= 1.0)
            {
                return t.Minutes + "m" + t.Seconds.ToString("00") + "s";
            }
            return t.Seconds + "s";
        }
        private static string FmtCpu(long ticks)
        {
            TimeSpan timeSpan = TimeSpan.FromTicks(ticks);
            if (timeSpan.TotalSeconds < 1.0)
            {
                return "<1s";
            }
            if (timeSpan.TotalMinutes >= 1.0)
            {
                return (int)timeSpan.TotalMinutes + "m" + timeSpan.Seconds.ToString("00") + "s";
            }
            return timeSpan.TotalSeconds.ToString("0.0") + "s";
        }
        private void PauseServicesPolicyChanged(bool enabledValue)
        {
            Interlocked.Increment(ref optionalServiceGeneration);
            if (!enabledValue)
            {
                lock (sync)
                {
                    envNextAttempt.Remove("services");
                    envFailures.Remove("services");
                }
            }
            else
            {
                ClearEnvFuse("services");
            }
            RequestPolicyApply("PauseServicesPolicyChanged");
        }
        private GameDetection ApplyStickiness(GameDetection hit)
        {
            if (hit != null)
            {
                stickyGraceOnly = false;
                stickyMiss = 0;
                if (stickyDetection != null && stickyDetection.Profile != null && hit.Profile != null && string.Equals(stickyDetection.Profile.Id, hit.Profile.Id, StringComparison.OrdinalIgnoreCase) && stickyDetection.RendererPid > 0 && AliveWithIdentity(stickyDetection.RendererPid))
                {
                    bool canReplaceStickyRenderer = false;
                    GameId value;
                    string name;
                    long creation;
                    if (hit.RendererPid > 0 && hit.RendererPid != stickyDetection.RendererPid && stickyIds.TryGetValue(stickyDetection.RendererPid, out value) && TryStickyIdentity(hit.RendererPid, out name, out creation) && FreshRendererMayReplaceSticky(hit, name, creation, value.Creation, RendererForegroundHeldMs(hit.RendererPid)))
                    {
                        canReplaceStickyRenderer = true;
                    }
                    if (hit.RendererPid != stickyDetection.RendererPid && !canReplaceStickyRenderer)
                    {
                        List<int> verifiedStickyPids = LiveStickyFamily();
                        if (!ReanchorToStickyInstance(hit, stickyDetection, verifiedStickyPids))
                        {
                            ClearSticky();
                        }
                    }
                }
                if (!RememberSticky(hit))
                {
                    return ApplyStickiness(null);
                }
                {
                    foreach (int item in LiveStickyFamily())
                    {
                        hit.FamilyPids.Add(item);
                    }
                    return hit;
                }
            }
            if (stickyDetection != null && stickyDetection.RendererPid > 0 && AliveWithIdentity(stickyDetection.RendererPid))
            {
                stickyGraceOnly = false;
                stickyMiss = 0;
                GameDetection gameDetection = new GameDetection();
                gameDetection.Profile = stickyDetection.Profile;
                gameDetection.RendererPid = stickyDetection.RendererPid;
                gameDetection.RendererCreation = stickyDetection.RendererCreation;
                gameDetection.RendererName = stickyDetection.RendererName;
                gameDetection.RendererPath = stickyDetection.RendererPath;
                gameDetection.RendererForeground = stickyDetection.RendererForeground;
                gameDetection.RendererCandidateSelected = true;
                gameDetection.RendererUserSelected = stickyDetection.RendererUserSelected;
                gameDetection.RendererLearnable = stickyDetection.RendererLearnable;
                gameDetection.Evidence = stickyDetection.Evidence;
                GameDetection gameDetection2 = gameDetection;
                gameDetection2.FamilyPids.Add(stickyDetection.RendererPid);
                foreach (int item2 in LiveStickyFamily())
                {
                    gameDetection2.FamilyPids.Add(item2);
                }
                foreach (string familyName in stickyDetection.FamilyNames)
                {
                    gameDetection2.FamilyNames.Add(familyName);
                }
                stickyDetection = gameDetection2;
                return gameDetection2;
            }
            if (stickyDetection != null && stickyMiss < 1 && !AnyStickyReused())
            {
                stickyMiss++;
                stickyGraceOnly = true;
                RequestFullGameDetection();
                try
                {
                    kick.Set();
                }
                catch
                {
                }
                return CloneWithAnchoredFamily(stickyDetection);
            }
            ClearSticky();
            return null;
        }
        private bool AliveWithIdentity(int pid)
        {
            GameId value;
            if (!stickyIds.TryGetValue(pid, out value))
            {
                return false;
            }
            string name;
            long creation;
            if (TryStickyIdentity(pid, out name, out creation) && creation == value.Creation)
            {
                return string.Equals(name, value.Name, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        private static bool TryIdentity(int pid, out string name, out long creation)
        {
            name = null;
            creation = 0L;
            if (pid <= 0)
            {
                return false;
            }
            IntPtr intPtr = Native.OpenProcess(4096, false, pid);
            if (intPtr == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                name = Native.ImageName(intPtr);
                long cpu;
                ulong io;
                return Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && !string.IsNullOrEmpty(name);
            }
            finally
            {
                Native.CloseHandle(intPtr);
            }
        }
        internal static bool IsHandheld(PerformancePreset mode)
        {
            return false;
        }
        private bool TryProtectOverlayHost(int pid, long creation, string name, string imagePath, bool familyExempt)
        {
            if (pid <= 4 || !OverlayHostCatalog.ShouldProtectProcess(name, imagePath, familyExempt))
            {
                return false;
            }
            if (creation > 0 && core.ReleaseIfCreation(pid, SuppressReason.Background, creation))
            {
                ReportUntrack(pid);
            }
            return true;
        }
    }
}
