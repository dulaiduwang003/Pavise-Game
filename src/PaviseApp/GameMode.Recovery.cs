using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // 驱动配置按游戏整体恢复；本局仅为启用过的项目记录结果，冷启动归属未知时记录全部三项。
        internal static string[] NvRestoreRecordKeys(IDictionary<string, string> evidence)
        {
            string[] keys = { "NvLowLat", "NvMaxPerf", "NvShaderCache" };
            var touched = new List<string>();
            foreach (string key in keys)
            {
                string state;
                if (evidence.TryGetValue(key, out state) && !string.IsNullOrEmpty(state) && state != "unchanged") touched.Add(key);
            }
            return touched.Count == 0 ? keys : touched.ToArray();
        }

        private static IntPtr DuplicateRestoreHandle(IntPtr source)
        {
            if (source == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            IntPtr currentProcess = Native.GetCurrentProcess();
            IntPtr targetHandle;
            if (!DuplicateHandle(currentProcess, source, currentProcess, out targetHandle, 0u, false, 2u))
            {
                return IntPtr.Zero;
            }
            return targetHandle;
        }
        private bool RestoreHardPin(int pid, HardPin expected = null)
        {
            // 持有 sync 的调用方将清理交给现有工作线程，不能带着 sync 进入守护进程通信。
            if (Monitor.IsEntered(sync))
            {
                HardPin queued;
                if (!hardPins.TryGetValue(pid, out queued)) return true;
                if (expected != null && !object.ReferenceEquals(queued, expected)) return true;
                RequestHardPinRestoreLocked(queued);
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                return false;
            }
            if (!Monitor.TryEnter(criticalProcessGate, 0))
            {
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                return false;
            }
            try
            {
                HardPin value;
                lock (sync)
                {
                    if (!hardPins.TryGetValue(pid, out value))
                    {
                        return true;
                    }
                    if (expected != null && !object.ReferenceEquals(value, expected)) return true;
                    RequestHardPinRestoreLocked(value, false);
                    bool targetGone = Native.WaitForSingleObject(value.RestoreHandle, 0) == 0;
                    if (targetGone && !value.RestoreExitPhase)
                    {
                        value.RestoreExitPhase = true;
                        value.RestoreAttempts = 0;
                        value.NextRestore = 0;
                    }
                    if (value.RestoreAttempts >= 3)
                    {
                        RecoveryAttemptTrace.Exhausted(value.RestoreExitPhase ? (Func<bool>)null : () => Native.WaitForSingleObject(value.RestoreHandle, 0) == 0);
                        return false;
                    }
                    if (AutoCores.NowTicks < value.NextRestore)
                    {
                        RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(value.NextRestore - AutoCores.NowTicks));
                        return false;
                    }
                    value.RestoreAttempts++;
                    value.RestoreInFlight = true;
                }
                bool restored = false;
                try
                {
                    if (!PrepareCriticalThreadProcessMaskMutation(pid, value.Creation)) return false;
                    restored = RestoreHardPinLocked(pid);
                    return restored;
                }
                finally
                {
                    lock (sync)
                    {
                        HardPin current;
                        if (hardPins.TryGetValue(pid, out current) && object.ReferenceEquals(value, current))
                        {
                            current.RestoreInFlight = false;
                            if (!restored)
                            {
                                current.RestoreDelay = current.RestoreDelay == 0 ? 500 : Math.Min(30000, current.RestoreDelay * 2);
                                current.NextRestore = AutoCores.NowTicks + TimeSpan.FromMilliseconds(current.RestoreDelay).Ticks;
                                current.RestoreTargetGone = Native.WaitForSingleObject(current.RestoreHandle, 0) == 0;
                                if (current.RestoreAttempts >= 3)
                                    RecoveryAttemptTrace.Exhausted(current.RestoreExitPhase ? (Func<bool>)null : () => Native.WaitForSingleObject(current.RestoreHandle, 0) == 0);
                                else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(current.NextRestore - AutoCores.NowTicks));
                                Logger.Warn("硬亲和性恢复：目标=" + pid + "/" + current.Creation
                                    + " 尝试=" + current.RestoreAttempts + "/3; "
                                    + (current.RestoreAttempts >= 3 ? "已停止主动重试，保留回执和句柄" : "等待还原"));
                            }
                        }
                    }
                }
            }
            finally
            {
                Monitor.Exit(criticalProcessGate);
            }
        }
        private bool RestoreHardPinLocked(int pid)
        {
            if (!Monitor.IsEntered(criticalProcessGate) || Monitor.IsEntered(sync)) return false;
            HardPin value;
            lock (sync)
            {
                if (!hardPins.TryGetValue(pid, out value))
                {
                    return true;
                }
                if (!value.RestoreInFlight) return false;
            }
            string jobReason;
            if (!coreAffinityJobs.Release(pid, value.Creation, out jobReason))
            {
                lock (sync) corePlacementEvidence = "pid=" + pid + " stage=job-release reason=" + jobReason;
                return false;
            }
            HardPinHandleState state = HardPinHandleStateOf(value);
            if (state == HardPinHandleState.Match)
            {
                if (!value.JobRecoveryOnly && !CrashGuard.RestoreOwnedAffinity(value.RestoreHandle, value.OriginalAffinity, value.AppliedAffinity)) return false;
                if (!CrashGuard.TrackBoostAffinity(pid, value.Creation, 0uL)) return false;
            }
            else if (state != HardPinHandleState.Gone && state != HardPinHandleState.Mismatch) return false;
            lock (sync)
            {
                HardPin current;
                if (!hardPins.TryGetValue(pid, out current) || !object.ReferenceEquals(value, current)) return false;
                hardPins.Remove(pid);
                gamePlacement.Remove(pid);
                if (value.RestoreHandle != IntPtr.Zero) Native.CloseHandle(value.RestoreHandle);
            }
            return true;
        }
        private bool RestoreAllHardPins()
        {
            return RestoreHardPinsExcept(0, 0L);
        }
        private bool RestoreHardPinsExcept(int keepPid, long keepCreation)
        {
            List<int> list = new List<int>();
            lock (sync)
            {
                foreach (KeyValuePair<int, HardPin> hardPin in hardPins)
                {
                    if (keepPid <= 0 || hardPin.Key != keepPid || hardPin.Value.Creation != keepCreation)
                    {
                        list.Add(hardPin.Key);
                    }
                }
            }
            bool result = true;
            foreach (int item in list)
            {
                if (!RestoreHardPin(item))
                {
                    result = false;
                }
            }
            string jobReason;
            if (!result || Monitor.IsEntered(sync) || !Monitor.TryEnter(criticalProcessGate, 0)) return false;
            try { return coreAffinityJobs.ReleaseExcept(keepPid, keepCreation, out jobReason); }
            finally { Monitor.Exit(criticalProcessGate); }
        }
        private void SetBoostActionRecovery(int pid, string action, bool pending)
        {
            lock (sync)
            {
                HashSet<string> value;
                if (!boostActionRestorePending.TryGetValue(pid, out value))
                {
                    if (!pending)
                    {
                        return;
                    }
                    value = (boostActionRestorePending[pid] = new HashSet<string>(StringComparer.Ordinal));
                }
                if (pending)
                {
                    value.Add(action);
                    return;
                }
                value.Remove(action);
                if (value.Count == 0)
                {
                    boostActionRestorePending.Remove(pid);
                }
            }
        }
        private bool RestoreBoostAction(IntPtr handle, int pid, long creation, string action)
        {
            return RunBoostRecovery(pid, creation, action, () => RestoreBoostActionCore(handle, pid, creation, action));
        }
        private bool RestoreBoostActionCore(IntPtr handle, int pid, long creation, string action)
        {
            try
            {
                switch (BoostActionIdentity(handle, creation))
                {
                case 1:
                    SetBoostActionRecovery(pid, action, false);
                    return true;
                default:
                    return false;
                case 0:
                {
                    if (action == "GamePriority")
                    {
                        bool restored = CrashGuard.RestoreBoostPriority(handle, pid, creation);
                        if (restored) SetBoostActionRecovery(pid, action, false);
                        return restored;
                    }
                    if (action == "GameGpuPriority")
                    {
                        bool restored = CrashGuard.RestoreBoostGpu(handle, pid, creation);
                        if (restored) SetBoostActionRecovery(pid, action, false);
                        return restored;
                    }
                    long original;
                    if (!TryOriginalBoostAction(pid, action, out original))
                    {
                        return false;
                    }
                    long? boostAction = ReadBoostAction(action, handle);
                    if (!boostAction.HasValue)
                    {
                        return false;
                    }
                    bool writeSucceeded = boostAction.Value == original || WriteBoostAction(action, handle, original) == 0;
                    long? boostAction2 = ReadBoostAction(action, handle);
                    int boostActionIdentityResult = BoostActionIdentity(handle, creation);
                    bool restoreVerified = boostActionIdentityResult == 1 || (boostActionIdentityResult == 0 && writeSucceeded && boostAction2.HasValue && boostAction2.Value == original);
                    if (restoreVerified)
                    {
                        SetBoostActionRecovery(pid, action, false);
                    }
                    return restoreVerified;
                }
                }
            }
            catch
            {
                return false;
            }
        }
        private bool RestoreBoostActionTarget(IntPtr currentHandle, int currentPid, string target, string action)
        {
            List<KeyValuePair<int, Snap>> list = new List<KeyValuePair<int, Snap>>();
            lock (sync)
            {
                foreach (KeyValuePair<int, Snap> item in gameBoost)
                {
                    string value;
                    if (boostActionTargets.TryGetValue(item.Key, out value) && string.Equals(value, target, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(item);
                    }
                }
            }
            foreach (KeyValuePair<int, Snap> item2 in list)
            {
                bool restored = RunBoostRecovery(item2.Key, item2.Value.Creation, "target:" + action, delegate
                {
                IntPtr intPtr = ((item2.Key == currentPid) ? currentHandle : Native.OpenProcess(12800, false, item2.Key));
                if (intPtr == IntPtr.Zero)
                {
                    if (ProbeBoostTarget(item2.Key, item2.Value.Creation) != BoostTarget.Gone)
                    {
                        return false;
                    }
                    return true;
                }
                try
                {
                    if (BoostActionIdentity(intPtr, item2.Value.Creation) == 0 && !string.Equals(BoostActionTarget(intPtr), target, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    if (!RestoreBoostAction(intPtr, item2.Key, item2.Value.Creation, action))
                    {
                        return false;
                    }
                }
                finally
                {
                    if (item2.Key != currentPid)
                    {
                        Native.CloseHandle(intPtr);
                    }
                }
                return true;
                });
                if (!restored) return false;
            }
            return true;
        }
        private bool RestoreBoostSnapshot(IntPtr handle, int pid, Snap snapshot)
        {
            bool snapshotValid = snapshot.Pri != 0 && snapshot.Pri != uint.MaxValue && snapshot.Io >= 0 && (!Native.PowerThrottlingSupported || (snapshot.QoSControl >= 0 && snapshot.QoSState >= 0));
            string[] boostActions = BoostActions;
            foreach (string entryText in boostActions)
            {
                long original;
                if (TryOriginalBoostAction(pid, entryText, out original))
                {
                    bool restoreBoostActionSucceeded = RestoreBoostAction(handle, pid, snapshot.Creation, entryText);
                    snapshotValid = snapshotValid && restoreBoostActionSucceeded;
                    if (entryText != "GameEcoQoS")
                    {
                        RecordAutomation(ResourcePriorityKey(entryText), restoreBoostActionSucceeded ? "restored" : "restore-failed");
                    }
                    string value;
                    lock (sync)
                    {
                        boostActionTargets.TryGetValue(pid, out value);
                    }
                    if (restoreBoostActionSucceeded && !string.IsNullOrEmpty(value))
                    {
                        ActionFailureStore.CancelApply(entryText, value);
                    }
                }
            }
            return snapshotValid;
        }
        public bool PanicRestore()
        {
            Interlocked.Increment(ref standbyCleanerRestorePending);
            InvalidateCacheWarm();
            InvalidateStandbyCleanerWork();
            InvalidateEnglishInputWork();
            try
            {
                return PanicRestoreCore();
            }
            finally
            {
                Interlocked.Decrement(ref standbyCleanerRestorePending);
            }
        }
        private bool PanicRestoreCore()
        {
            int clearResult = SelfProtectedRoster.Clear();
            if (clearResult > 0)
            {
                Logger.Log(Lang.T("log.gamemodeboost.46", 0) + clearResult + Lang.T("log.gamemodeboost.47", 0));
            }
            bool persistentTweaksRestored = true;
            if (IfeoBoost.HasResidue())
            {
                persistentTweaksRestored &= IfeoBoost.RestoreAll();
            }
            if (CfgOffTweak.HasResidue())
            {
                persistentTweaksRestored &= CfgOffTweak.RestoreAll();
            }
            try
            {
                ActionFailureStore.Records();
            }
            catch (Exception ex)
            {
                Logger.Warn("紧急恢复期间保留失败隔离： " + ex.Message);
            }
            lock (panicCallGate)
            {
                int incrementResult = Interlocked.Increment(ref panicSeq);
                panicDone.Reset();
                panicResult = false;
                lock (sync)
                {
                    panicReq = true;
                    InvalidatePowerPlanSession();
                    InvalidateStandbyCleanerWork();
                    InvalidateEnglishInputWork();
                    InvalidateRendererHandoff();
                }
                kick.Set();
                long ticksOffset = DateTime.UtcNow.Ticks + 120000000;
                while (true)
                {
                    long ratio = (ticksOffset - DateTime.UtcNow.Ticks) / 10000;
                    if (ratio <= 0)
                    {
                        return false;
                    }
                    if (!panicDone.WaitOne((int)ratio))
                    {
                        return false;
                    }
                    if (Volatile.Read(ref panicServed) == incrementResult)
                    {
                        break;
                    }
                    panicDone.Reset();
                }
                return panicResult && persistentTweaksRestored;
            }
        }
        private bool Deactivate(string reason)
        {
            return Deactivate(reason, false);
        }
        private void MaybeRecoverHistoricalCriticalThreads()
        {
            if (stopping || GameSessionToken < 0 || criticalThreadResidue || (!CriticalThreadHasRecoveryDebt(false) && !CriticalThreadGuardian.SettledLeftoversPending))
            {
                return;
            }
            lock (criticalHistoricalQueueGate)
            {
                if (stopping || criticalHistoricalWorker || AutoCores.NowTicks < criticalHistoricalRetry)
                {
                    return;
                }
                criticalHistoricalWorker = true;
                criticalHistoricalIdle.Reset();
                try
                {
                    GameNvWorkerQueue.TryQueue((WaitCallback callback) => ThreadPool.QueueUserWorkItem(callback), delegate
                    {
                        try
                        {
                            if (stopping || GameSessionToken < 0 || !Monitor.TryEnter(criticalProcessGate, 0))
                            {
                                return;
                            }
                            try
                            {
                                if (criticalSession == null && !criticalRestorationConfirmed && !stopping)
                                {
                                    if (CriticalThreadHasRecoveryDebt(false))
                                    {
                                        CriticalThreadGuardian.TrySettleHistoricalGone(dataDir);
                                        criticalDebtNextRead = 0L;
                                        CriticalThreadHasRecoveryDebt(true);
                                        ScheduleThreadBoostRecoveryRefresh();
                                    }
                                    if (CriticalThreadGuardian.SettledLeftoversPending)
                                    {
                                        CriticalThreadGuardian.SweepSettledGuards(dataDir);
                                    }
                                }
                            }
                            finally
                            {
                                Monitor.Exit(criticalProcessGate);
                            }
                        }
                        catch (Exception ex2)
                        {
                            Logger.RepeatedError("critical-history-recovery", "历史线程恢复仍未确认： " + ex2.Message, -1L);
                        }
                    }, delegate
                    {
                        lock (criticalHistoricalQueueGate)
                        {
                            criticalHistoricalWorker = false;
                            criticalHistoricalRetry = AutoCores.NowTicks + 300000000;
                            criticalHistoricalIdle.Set();
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.RepeatedError("critical-history-queue", "线程恢复无法入队： " + ex.Message, -1L);
                }
            }
        }
        private bool RestoreCriticalThreadsCore()
        {
            if (!Monitor.IsEntered(criticalProcessGate))
            {
                throw new InvalidOperationException("Original placement gate required");
            }
            if (criticalOriginalTarget != null) criticalOriginalTarget.Revoke();
            if (unifiedThreadControl != null)
            {
                unifiedThreadControl.RequestStop();
                if (!unifiedThreadControl.StopRecovery())
                {
                    criticalThreadResidue = true;
                    PublishUnifiedState(criticalSource, "recovery-pending", unifiedThreadControl.Detail, false, true);
                    return false;
                }
                Logger.Info("游戏调度 线程属性恢复已核验： " + unifiedThreadControl.Describe());
                unifiedThreadControl.Dispose();
                unifiedThreadControl = null;
            }
            if (criticalSession == null)
            {
                object reservationOwner = criticalPriorityReservation;
                bool priorityClean = ReleaseCriticalThreadPriorityReservation();
                bool isolationClean = StopCriticalCoreIsolation();
                if (priorityClean && isolationClean)
                {
                    if (reservationOwner != null) criticalRecoveryPhases.Remove(reservationOwner);
                    criticalOriginalTarget = null;
                }
                // 本地预留债务不能阻塞自身恢复，也不能阻塞独立的历史恢复线程。
                criticalThreadResidue = !priorityClean || !isolationClean;
                return priorityClean && isolationClean && !CriticalThreadHistoricalRecoveryDebt(false);
            }
            if (criticalOriginalTarget != null)
            {
                criticalOriginalTarget.Revoke();
            }
            if (!criticalRestorationConfirmed && !criticalSession.Restore())
            {
                criticalThreadResidue = true;
                PublishCriticalThreadState(criticalSource, "recovery-pending", criticalSession.Detail, false, true, 0, 0L);
                return false;
            }
            criticalRestorationConfirmed = true;
            if (!StopCriticalCoreIsolation())
            {
                criticalThreadResidue = true;
                PublishCriticalThreadState(criticalSource, "recovery-pending", "Thread placement restored; core isolation restoration is pending: " + criticalCoreIsolationError, false, true, 0, 0L);
                return false;
            }
            if (criticalFailureAdmitted)
            {
                ActionFailureStore.CancelApply("game-critical-thread-hard-affinity", criticalFailureTarget);
                criticalFailureAdmitted = false;
            }
            bool closureObserved = criticalClosureObserveOnly && criticalSession.RefreshGuardianClosure();
            if (!closureObserved)
            {
                try
                {
                    criticalSession.Dispose();
                }
                catch (Exception ex)
                {
                    criticalHelperClosureWaiting = false;
                    criticalThreadResidue = true;
                    PublishCriticalThreadState(criticalSource, "recovery-pending", PerformancePolicy.Text("线程已恢复，辅助资源关闭仍待确认：", "Threads restored; helper resource closure is pending: ") + ex.Message, false, true, 0, 0L);
                    return false;
                }
            }
            if (!criticalSession.ResourcesClosed)
            {
                criticalHelperClosureWaiting = true;
                criticalThreadResidue = true;
                PublishCriticalThreadState(criticalSource, "recovery-pending", PerformancePolicy.Text("线程已恢复，等待原辅助进程退出与资源关闭", "Threads restored; waiting for the original helper to exit and resources to close"), false, true, 0, 0L);
                return false;
            }
            if (!ReleaseCriticalThreadPriorityReservation())
            {
                criticalThreadResidue = true;
                PublishCriticalThreadState(criticalSource, "recovery-pending", "Thread priority restoration is unconfirmed", false, true, 0, 0L);
                return false;
            }
            criticalSession = null;
            criticalHelperClosureWaiting = false;
            InvalidateThreadBoostRecoveryAdmission();
            criticalPlan = null;
            criticalRestorationConfirmed = false;
            criticalChoiceFrameAssociated = false;
            criticalGuardianActor = null;
            criticalOriginalTarget = null;
            criticalThreadResidue = false;
            criticalHistoricalDebt = false;
            criticalDebtNextRead = 0L;
            if (criticalSource != null)
            {
                PublishCriticalThreadState(criticalSource, "observing", PerformancePolicy.Text("原线程设置已恢复；未继续绑定", "Original thread settings restored; no binding remains active"), false, false, 0, 0L);
            }
            return true;
        }
        private bool CriticalThreadHasRecoveryDebt(bool force)
        {
            if (criticalThreadResidue)
            {
                return true;
            }
            return CriticalThreadHistoricalRecoveryDebt(force);
        }
        private bool RestoreGuardScheduling()
        {
            lock (sync)
            {
                return SetGuardScheduling(false, false);
            }
        }
        private bool RunEnvStepRestore(long session, string key, Func<bool> restore, out bool skipped, out string reason)
        {
            // 恢复预算记在准入会话名下；发布边界由调用方单独控制。
            if (session != Interlocked.Read(ref gameSessionStarts))
            { skipped = true; reason = "superseded environment step"; return false; }
            if (CapturingSessionRecovery) return RunAutomaticRestore(key, restore, out skipped, out reason);
            return RunAutomaticRestoreCore(key, restore, out skipped, out reason, session);
        }
        private void ClearEnvRetryState()
        {
            lock (sync)
            {
                foreach (string key in new List<string>(envNextAttempt.Keys))
                    if (!envRestorePending.Contains(key)) envNextAttempt.Remove(key);
            }
        }
        private void BeginRetrySession()
        {
            SyncAutomaticRetrySession();
            CoreIsolationClient.SyncAutomaticSession(Interlocked.Read(ref gameSessionStarts));
            SelfYield.BeginSession(Interlocked.Read(ref gameSessionStarts));
            GameDetection retryDetection = Volatile.Read(ref activeDetection);
            RenderLane.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), retryDetection == null ? 0 : retryDetection.RendererPid,
                retryDetection == null ? 0 : retryDetection.RendererCreation, "lane:" + EffLane);
            PerformanceTuning.BeginRecoveryCycle(Interlocked.Read(ref gameSessionStarts));
            string[] envKeys = EnvKeys;
            foreach (string key in envKeys)
            {
                ActionFailureStore.BeginAutomaticCycle(EnvFailureAction(key), "system", AutomaticPhaseKey(key));
                SyncEnvFailureIsolation(key);
            }
            PowerPlanStopped(lastPowerPolicyKey);
        }
        private void RestoreRetiredOptionalServices()
        {
            optionalServicesActive = RestoreRetiredServiceDebt(optionalServicesActive, () => OptionalServicePause.HasResidue, OptionalServicePause.Restore);
        }
        private bool RestoreRetiredServiceDebt(bool pending, Func<bool> hasResidue, Func<bool> restore)
        {
            return EnvStep("services", false, pending || hasResidue(), null, restore);
        }
        private static bool RestoreAmdAntiLagEnv()
        {
            bool restoreAntiLagSucceeded = AdlxTweaks.RestoreAntiLag();
            return restoreAntiLagSucceeded & AdlxTweaks.RestoreChill();
        }
        private static bool RestorePowerPlan()
        {
            return PowerPlan.Restore();
        }
        private bool RestoreFailedPowerPlan(long now)
        {
            bool isInEnvRestorePending;
            lock (sync)
            {
                isInEnvRestorePending = envRestorePending.Contains("powerplan");
                long value;
                if (isInEnvRestorePending && envNextAttempt.TryGetValue("powerplan", out value) && now < value)
                {
                    return false;
                }
            }
            if (isInEnvRestorePending || (!planActive && PowerPlan.HasResidue))
            {
                lock (powerApplyGate)
                {
                    bool runAutomaticRestoreSucceeded = RunAutomaticRestore("powerplan", () => RestoreLaptopPerformanceForPowerPlan() && RestorePowerPlan());
                    lock (sync)
                    {
                        if (runAutomaticRestoreSucceeded)
                        {
                            ActionFailureStore.CancelApply("PowerPlanOn", "system");
                            envRestorePending.Remove("powerplan");
                            envNextAttempt.Remove("powerplan");
                            envFailures.Remove("powerplan");
                        }
                        else
                        {
                            envRestorePending.Add("powerplan");
                        }
                    }
                    planActive = !runAutomaticRestoreSucceeded;
                    if (runAutomaticRestoreSucceeded)
                    {
                        lastPowerPolicyKey = -1;
                        Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
                    }
                    return runAutomaticRestoreSucceeded;
                }
            }
            return true;
        }
        private bool RestoreDisplayAwake()
        {
            bool valuesMatch;
            lock (sync)
            {
                int restoreDisplayAwakeValue;
                if (!awakeActive)
                {
                    string value;
                    if (automationEvidence.TryGetValue("awake", out value))
                    {
                        switch (value)
                        {
                        default:
                            restoreDisplayAwakeValue = ((value == "restore-failed") ? 1 : 0);
                            break;
                        case "applied":
                        case "restored":
                        case "failed":
                            restoreDisplayAwakeValue = 1;
                            break;
                        }
                    }
                    else
                    {
                        restoreDisplayAwakeValue = 0;
                    }
                }
                else
                {
                    restoreDisplayAwakeValue = 1;
                }
                valuesMatch = (byte)restoreDisplayAwakeValue != 0;
            }
            bool runAutomaticRestoreSucceeded = RunAutomaticRestore("awake", valuesMatch ? (Func<bool>)DisplayAwake.RestoreForSession : DisplayAwake.Restore);
            if (runAutomaticRestoreSucceeded)
            {
                awakeActive = false;
            }
            if (valuesMatch)
            {
                RecordAutomation("awake", runAutomaticRestoreSucceeded ? "restored" : "restore-failed");
            }
            return runAutomaticRestoreSucceeded;
        }
        private bool RestoreEnv()
        {
            InvalidatePowerPlanSession();
            RunSessionRecoveryRoute("selfyield", delegate { SelfYield.Release(); return !SelfYield.HasResidue; });
            bool labClean = RunSessionRecoveryRoute("laboratory", () => RestoreLaboratory());
            bool operationsSucceeded = labClean && !SelfYield.HasResidue;
            if (!RunSessionRecoveryRoute("windows-background", () => windowsBackground.Restore())) operationsSucceeded = false;
            if (!RunSessionRecoveryRoute("critical", () => StopCriticalThreads(0, "RestoreEnv")))
            {
                operationsSucceeded = false;
            }
            if (!RunSessionRecoveryRoute("legacy-nv", () => RestoreGameNvDelivery()))
            {
                operationsSucceeded = false;
            }
            if (!RunSessionRecoveryRoute("timer", () => timerLease.Update(false, DateTime.UtcNow.Ticks)))
            {
                operationsSucceeded = false;
            }
            RecordAutomation("AutoGlobalTimer", AutomaticExecutionState("AutoGlobalTimer"));
            if (!RunSessionRecoveryRoute("layouts", () => core.RestoreAutomaticLayouts()))
            {
                operationsSucceeded = false;
            }
            if (RunAutomaticRestore("do", () => DoTweak.Restore()))
            {
                doActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            if (RunAutomaticRestore("wu", () => UpdatePause.Restore()))
            {
                wuActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            if (RunAutomaticRestore("maint", () => MaintenancePause.Restore()))
            {
                maintActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            if (RunAutomaticRestore("services", () => OptionalServicePause.Restore()))
            {
                optionalServicesActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            if (RunAutomaticRestore("pqos", () => PresenceQos.Restore()))
            {
                pqosActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            if (!RestoreDisplayAwake())
            {
                operationsSucceeded = false;
            }
            if (RunAutomaticRestore("dwmboost", () => DwmBoost.Restore()))
            {
                dwmBoostActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            if (!RunAutomaticRestore("rss", () => RssSteer.Restore()))
            {
                operationsSucceeded = false;
            }
            if (RunAutomaticRestore("rsr", () => AdlxTweaks.RestoreRsr()))
            {
                rsrActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            {
            if (RunAutomaticRestore("gpupower", () => GpuPowerMax.Restore()))
            {
                gpwActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            if (!RunAutomaticRestore("gpuclock", () => GpuClockLock.Restore()))
            {
                operationsSucceeded = false;
            }
            }
            if (RunAutomaticRestore("nvvrr", () => NvVrrWindowed.Restore()))
            {
                nvVrrActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            if (RunAutomaticRestore("intelend", () => IntelEndurance.Restore()))
            {
                intelEndActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            bool laptopClean = RunSessionRecoveryRoute("laptop", () => RestoreLaptopPerformanceForPowerPlan());
            if (!laptopClean)
            {
                operationsSucceeded = false;
            }
            if (RunAutomaticRestore("amdalag", () => RestoreAmdAntiLagEnv()))
            {
                amdAlagActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            if (RunAutomaticRestore("amdafmf", () => AdlxTweaks.RestoreAfmf()))
            {
                amdAfmfActive = false;
            }
            else
            {
                operationsSucceeded = false;
            }
            if (!RunAutomaticRestore("amdsync", () => AdlxTweaks.RestoreEnhancedSync()))
            {
                operationsSucceeded = false;
            }
            if (!RunAutomaticRestore("amdris", () => AdlxTweaks.RestoreRis()))
            {
                operationsSucceeded = false;
            }
            if (!RunAutomaticRestore("amdfrtc", () => AdlxTweaks.RestoreFrtc()))
            {
                operationsSucceeded = false;
            }
            if (!RunSessionRecoveryRoute("nv-profiles", delegate
            {
                lock (driverStageGate)
                {
                    if (!NvGameResidueGate(true)) return true;
                    preStagedNvPath = null;
                    bool hadResidue = NvDrsTweaks.HasGameResidue;
                    bool clean = NvDrsTweaks.RestoreAllGames();
                    if (hadResidue)
                    {
                        string[] keys;
                        lock (sync) keys = NvRestoreRecordKeys(automationEvidence);
                        foreach (string key in keys) RecordAutomation(key, clean ? "restored" : "restore-failed");
                    }
                    return clean;
                }
            })) operationsSucceeded = false;
            Interlocked.Increment(ref powerSessionGen);
            if (CapturingSessionRecovery || (labClean && laptopClean))
            {
                if (RunAutomaticRestore("powerplan", delegate { lock (powerApplyGate) return PowerPlan.Restore(); }))
                {
                    planActive = false;
                    lastPowerPolicyKey = -1;
                    Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
                }
                else
                {
                    operationsSucceeded = false;
                }
                if (!RunAutomaticRestore("powerpark", delegate { lock (powerApplyGate) return PowerPlan.RestoreParkState(); }))
                {
                    operationsSucceeded = false;
                }
            }
            return CapturingSessionRecovery ? operationsSucceeded : EnvRestoreCompleted(operationsSucceeded);
        }
        private bool EnvRestoreCompleted(bool operationsSucceeded)
        {
            if (windowsBackground.HasRecovery) return false;
            if (Volatile.Read(ref laptopPerfWorker) != 0) return false;
            if (LaboratoryPower.HasResidue || LaboratoryGpu.HasResidue || (laboratory != null && !laboratory.IsRestored)) return false;
            if (!operationsSucceeded || PowerPlan.HasResidue || PowerPlan.HasParkResidue() || PowerOverlay.HasResidue || OptionalServicePause.HasResidue)
            {
                return false;
            }
            string[] textEntries = new string[15]
            {
                "PrevDoBgBw", "PrevDoSvcStopped", "PrevUpdatePaused", "PrevPresenceQos", "GpuPowerSnap", "AmdSnap", "NvDrsList", "PrevPowerPlan", "PowerOverlaySnap",
                "CpuIdleStateV1", "PrevOptionalServicesPausedV1", "LaptopPerfSnap", "FrameCoordinationJournalV1", "FrameMemoryProtectionJournalV1", "FrameCoordinationTraceV1"
            };
            string[] textEntries2 = textEntries;
            foreach (string name in textEntries2)
            {
                string value;
                if (!Settings.TryLoadStr(name, out value) || !string.IsNullOrEmpty(value))
                {
                    return false;
                }
            }
            lock (sync)
            {
                envRestorePending.RemoveWhere((string key) => key != "gamedvr" && key != "mmcss");
                return envRestorePending.Count == 0;
            }
        }
        private void ReleaseBackground()
        {
            ReleaseBackground(Lang.T("t.gamemodeenv.41"));
        }
        private int ReleaseBackground(string reasonPrefix)
        {
            if (!core.AnyWith(SuppressReason.Background))
            {
                return 0;
            }
            int processedCount = 0;
            foreach (int item in core.PidsWith(SuppressReason.Background))
            {
                if (core.Release(item, SuppressReason.Background))
                {
                    ReportSeal(item);
                    processedCount++;
                }
            }
            if (processedCount > 0)
            {
                Logger.Log(reasonPrefix + Lang.T("log.gamemodeenv.42", 0) + processedCount + Lang.T("log.gamemodeenv.43", 0));
            }
            return processedCount;
        }
        private void RestoreLegacyGameNvCore()
        {
            if (gameNvStore == null && !GameNvCanonicalStore.RootConfirmedAbsent(dataDir))
            {
                gameNvStore = GameNvCanonicalStore.Open(dataDir);
            }
            if (gameNvStore != null)
            {
                // 原始日志路径是不可变的恢复身份，不是驱动状态缓存。
                foreach (string journal in gameNvStore.PendingJournals())
                    legacyNvOwners.Add(gameNvStore.ReadOriginal(journal).Executable);
                LegacyNvOwnersExited();
                GameNvPersistentRecovery.RequireClear(gameNvStore);
            }
            lock (gameNvGate)
            {
                gameNvRecoveryChecked = true;
                gameNvRootClear = true;
                PublishLegacyNvPending();
            }
        }
        private void ResumeGameNvAfterRecovery()
        {
            lock (gameNvGate)
            {
                gameNvRecoveryStopped = stopping;
            }
        }
        private bool RestoreGameNvDelivery()
        {
            bool stopGameNvDeliverySucceeded = StopGameNvDelivery(5000);
            if (stopGameNvDeliverySucceeded)
            {
                ResumeGameNvAfterRecovery();
            }
            return stopGameNvDeliverySucceeded;
        }
        internal bool FinishShutdownRecovery(Func<bool> restoreLayouts, Func<bool> stopIsolation, Func<bool> restorePins, Func<bool> restoreGuard)
        {
            shutdownWritersStopped = false;
            bool clean = true;
            bool writersStopped = true;
            ShutdownRecoveryStep(restoreLayouts, false, ref clean, ref writersStopped);
            ShutdownRecoveryStep(stopIsolation, true, ref clean, ref writersStopped);
            ShutdownRecoveryStep(restorePins, false, ref clean, ref writersStopped);
            ShutdownRecoveryStep(restoreGuard, false, ref clean, ref writersStopped);
            shutdownWritersStopped = writersStopped;
            return clean;
        }
        private static void ShutdownRecoveryStep(Func<bool> work, bool requiresWriterExit, ref bool clean, ref bool writersStopped)
        {
            try
            {
                if (work != null && work())
                {
                    return;
                }
            }
            catch
            {
            }
            clean = false;
            if (requiresWriterExit)
            {
                writersStopped = false;
            }
        }
        public bool RestorePlatformProtection()
        {
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (stopping)
                    {
                        return false;
                    }
                    if (!PlatformRuntimePolicy.TryRestore())
                    {
                        whitelistLastError = Lang.T("white.save.failed");
                        return false;
                    }
                    whiteRevision++;
                    whitelistLastError = "";
                }
                int matched;
                ReleaseCurrentWhitelistMatches(out matched);
            }
            RequestPolicyApply("RestorePlatformProtection");
            return true;
        }
        private void ReleaseBackgroundExemption(int pid, string name, string reason)
        {
            if (core.Release(pid, SuppressReason.Background))
            {
                ReportUntrack(pid);
                if (!string.IsNullOrEmpty(reason))
                {
                    Logger.Log(reason + Lang.T("log.gamemodesweep.12", 0) + name + " 进程编号 " + pid);
                }
            }
        }
    }
}
