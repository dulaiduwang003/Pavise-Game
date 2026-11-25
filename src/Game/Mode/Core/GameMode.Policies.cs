using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        internal List<string> ApplyNvGamePlan(string path, NvGamePlan plan, out bool retry, long now = -1L, bool preStage = false)
        {
            SyncNvAutomaticPhase(path, plan);
            List<string> list = NvDrsTweaks.ApplyForGame(path, plan, out retry);
            if (list == null && retry && plan != null)
            {
                if (plan.MaxPerf)
                {
                    RecordNvAutomation("NvMaxPerf", "failed", preStage);
                }
                if (plan.ShaderCacheMax)
                {
                    RecordNvAutomation("NvShaderCache", "failed", preStage);
                }
                if (plan.LowLatency)
                {
                    RecordNvAutomation(PolicyCatalog.KeyGameLowLatency, "failed", preStage);
                }
            }
            return list;
        }
        internal void ResetNvApplyAttempts()
        {
        }
        internal NvGamePlan RefreshAutomaticNvPlan(bool maxPerf, bool cache, bool onAc, bool rebar = false, bool lowLatency = false)
        {
            NvGamePlan nvGamePlan = NvGamePlan.ForAutomatic(maxPerf, cache, onAc, rebar, lowLatency);
            int automaticKey = nvGamePlan.AutomaticKey;
            lock (sync)
            {
                if (automaticNvPlanKey != automaticKey)
                {
                    automaticNvPlanKey = automaticKey;
                    ForgetNvTweaksLocked();
                    Interlocked.Exchange(ref nvTweakRetryAtTicks, 0L);
                }
            }
            return nvGamePlan;
        }
        internal bool SetEnvironmentPreference(string key, bool value)
        {
            if (PerformancePolicy.IsRetired(key)) return false;
            if (!PerformancePolicy.IsEnvironment(key))
            {
                return false;
            }
            if (key == "GmStandbyCleanerV2")
            {
                return TrySetStandbyCleanerEnabled(value);
            }
            lock (timerMode.Gate)
            {
                lock (sync)
                {
                    if (stopping || !Settings.Save(key, value))
                    {
                        return false;
                    }
                }
            }
            RequestPolicyApply("SetEnvironmentPreference");
            return true;
        }
        private void ForgetNvTweaksLocked()
        {
            tweakApplied.Clear();
            nvTweakGeneration++;
        }
        private void ForgetNvTweakLocked(int pid)
        {
            tweakApplied.Remove(pid);
            nvTweakGeneration++;
        }
        private bool NvTweakTargetCurrentLocked(int pid, long creation, long generation)
        {
            if (active && enabled && !stopping && !panicReq && generation == nvTweakGeneration && activeDetection != null && activeDetection.RendererPid == pid)
            {
                return activeDetection.RendererCreation == creation;
            }
            return false;
        }
        private static string EnvLabel(string key)
        {
            switch (key)
            {
            case "do":
                return Lang.T("t.gamemodeenv.1");
            case "wu":
                return Lang.T("t.gamemodeenv.3");
            case "services":
                return Lang.T("gm.pausesvc");
            case "standby":
                return Lang.T("gm.standbycleaner");
            case "pqos":
                return Lang.T("t.gamemodeenv.4");
            case "awake":
                return Lang.T("t.gamemodeenv.5");
            case "dwmboost":
                return Lang.T("gm.dwmboost");
            case "rsr":
                return Lang.T("set.rsr");
            case "maint":
                return Lang.T("gm.pausemaint");
            case "laptopperf":
                return Lang.T("gm.laptopperf");
            case "amdafmf":
                return Lang.T("t.gamemodeenv.8");
            case "GameDvrOff":
                return Lang.T("set.dvr");
            case "gamedvr":
                return Lang.T("set.dvr");
            default:
                return key;
            }
        }
        private bool EnvStep(string key, bool want, bool active, Func<bool> activate, Func<bool> restore)
        {
            long session = Interlocked.Read(ref gameSessionStarts);
            bool originalActive = active;
            string action = EnvFailureAction(key);
            bool syncEnvFailureIsolationSucceeded;
            lock (sync)
            {
                if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
                syncEnvFailureIsolationSucceeded = SyncEnvFailureIsolation(key);
                if (envRestorePending.Contains(key))
                {
                    want = false;
                    active = true;
                }
            }
            if (want && syncEnvFailureIsolationSucceeded)
            {
                want = false;
            }
            if (want == active)
            {
                return active;
            }
            long ticks = DateTime.UtcNow.Ticks;
            lock (sync)
            {
                long value;
                if (envNextAttempt.TryGetValue(key, out value) && ticks < value)
                {
                    if (!want) { if (value == long.MaxValue) RecoveryAttemptTrace.Exhausted(); else RecoveryAttemptTrace.DeferredUntil(value); }
                    return active;
                }
            }
            string reason;
            if (want && (activate == null || !EnvironmentApplyReady(key))) return active;
            lock (sync)
            {
                if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
                if (want && !ActionFailureStore.TryBeginApply(action, "system", out reason)) return active;
            }
            bool skipped;
            bool runEnvironmentActionSucceeded = want ? RunEnvironmentAction(activate, out skipped, out reason) : RunEnvStepRestore(session, key, restore, out skipped, out reason);
            if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
            if (want && skipped)
            {
                bool skipped2;
                string reason2;
                bool runEnvStepRestoreSucceeded = RunEnvStepRestore(session, key, restore, out skipped2, out reason2);
                lock (sync)
                {
                    if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
                    if (runEnvStepRestoreSucceeded)
                    {
                        envRestorePending.Remove(key);
                    }
                    else
                    {
                        envRestorePending.Add(key);
                    }
                    if (runEnvStepRestoreSucceeded)
                    {
                        ActionFailureStore.CancelApply(action, "system");
                    }
                    else
                    {
                        ActionFailureStore.Trip(action, "system", PerformancePolicy.Text("取消或跳过后的恢复失败：", "Recovery after cancellation or skip failed: ") + reason2);
                    }
                }
                return !runEnvStepRestoreSucceeded;
            }
            lock (sync)
            {
                if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
                RecordAutomation(key, (!runEnvironmentActionSucceeded) ? (want ? "failed" : "restore-failed") : ((!want) ? "restored" : ((key == "services" && OptionalServicePause.PendingStopCount > 0) ? "unverified" : "applied")));
            }
            if (want)
            {
                if (runEnvironmentActionSucceeded)
                {
                    bool completeApplySucceeded;
                    lock (sync)
                    {
                        if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
                        completeApplySucceeded = ActionFailureStore.CompleteApply(action, "system");
                    }
                    if (!completeApplySucceeded)
                    {
                        bool skipped3;
                        string reason3;
                        bool runEnvStepRestoreSucceeded2 = RunEnvStepRestore(session, key, restore, out skipped3, out reason3);
                        lock (sync)
                        {
                            if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
                            RecordAutomation(key, runEnvStepRestoreSucceeded2 ? "restored" : "restore-failed");
                            if (runEnvStepRestoreSucceeded2)
                            {
                                envRestorePending.Remove(key);
                            }
                            else
                            {
                                envRestorePending.Add(key);
                            }
                            if (runEnvStepRestoreSucceeded2)
                            {
                                ActionFailureStore.CancelApply(action, "system");
                            }
                            else
                            {
                                ActionFailureStore.Trip(action, "system", PerformancePolicy.Text("完成记录失败且回退失败：", "Completion persistence and rollback failed: ") + reason3);
                            }
                            SyncEnvFailureIsolation(key);
                        }
                        return !runEnvStepRestoreSucceeded2;
                    }
                    lock (sync)
                    {
                        if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
                        envNextAttempt.Remove(key);
                        envFailures.Remove(key);
                        SyncEnvFailureIsolation(key);
                        if (completeApplySucceeded && Settings.Load("EnvFuse_" + key, false))
                        {
                            Settings.Save("EnvFuse_" + key, false);
                        }
                    }
                    return true;
                }
                lock (sync)
                {
                    if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
                    ActionFailureStore.Trip(action, "system", reason);
                    envFused.Add(key);
                }
                Logger.Warn(EnvLabel(key) + Logger.Text("：执行失败，本阶段停止应用并核验恢复。", ": apply failed; this phase stopped applying and is verifying the restore."));
                bool skipped4;
                string reason4;
                bool runEnvStepRestoreSucceeded3 = RunEnvStepRestore(session, key, restore, out skipped4, out reason4);
                lock (sync)
                {
                    if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
                    RecordAutomation(key, runEnvStepRestoreSucceeded3 ? "restored" : "restore-failed");
                    if (runEnvStepRestoreSucceeded3)
                    {
                        envRestorePending.Remove(key);
                        envNextAttempt.Remove(key);
                        envFailures.Remove(key);
                    }
                    else
                    {
                        envRestorePending.Add(key);
                    }
                    if (runEnvStepRestoreSucceeded3)
                    {
                        ActionFailureStore.CancelApply(action, "system");
                    }
                    else
                    {
                        ActionFailureStore.Trip(action, "system", reason + PerformancePolicy.Text("；回退失败：", "; rollback failed: ") + reason4);
                    }
                }
                return !runEnvStepRestoreSucceeded3;
            }
            lock (sync)
            {
                if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
                if (runEnvironmentActionSucceeded)
                {
                    ActionFailureStore.CancelApply(action, "system");
                    envRestorePending.Remove(key);
                    envNextAttempt.Remove(key);
                    envFailures.Remove(key);
                }
                else
                {
                    envRestorePending.Add(key);
                    if (!skipped)
                    {
                        ActionFailureStore.Trip(action, "system", PerformancePolicy.Text("恢复失败：", "Recovery failed: ") + reason);
                    }
                }
            }
            if (!runEnvironmentActionSucceeded)
            {
                return active;
            }
            return false;
        }
        private static string EnvFailureAction(string key)
        {
            string envPolicyKeyResult = EnvPolicyKey(key);
            if (envPolicyKeyResult != null)
            {
                return envPolicyKeyResult;
            }
            switch (key)
            {
            case "rsr":
                return "GmRsr";
            case "pqos":
                return "PresenceQos";
            default:
                return key;
            }
        }
        private bool SyncEnvFailureIsolation(string key)
        {
            bool isBlocked = ActionFailureStore.IsBlocked(EnvFailureAction(key), "system");
            lock (sync)
            {
                if (isBlocked)
                {
                    envFused.Add(key);
                }
                else
                {
                    envFused.Remove(key);
                }
            }
            return isBlocked;
        }
#if PAVISE_SELFTEST
        internal static Func<string, bool> EnvironmentReadyForTest;
#endif
        private bool EnvironmentApplyReady(string key)
        {
#if PAVISE_SELFTEST
            if (EnvironmentReadyForTest != null) return EnvironmentReadyForTest(key);
            if (key == "maint") return Native.IsElevated();
            if (key == "rsr") return AdlxTweaks.RsrSupported();
            if (key == "amdafmf") return AdlxTweaks.AfmfSupported();
            return true;
#endif
            switch (key)
            {
            case "maint":
            case "pqos":
            case "do":
            case "wu":
            case "services":
                if (!Native.IsElevated())
                {
                    return false;
                }
                break;
            }
            if (key == "do" && Native.IsDomainJoined() && SvcState.Query("DoSvc") == 1 && !DoTweak.HasResidue)
            {
                return false;
            }
            switch (key)
            {
            case "maint":
                return Native.IsElevated();
            case "rsr":
                return AdlxTweaks.RsrSupported();
            case "amdafmf":
                return AdlxTweaks.AfmfSupported();
            default:
                return true;
            }
        }
        private static bool RunEnvironmentAction(Func<bool> action, out bool skipped, out string reason)
        {
            skipped = action == null;
            reason = "";
            if (skipped)
            {
                return false;
            }
            try
            {
                if (action())
                {
                    return true;
                }
                reason = PerformancePolicy.Text("执行返回失败", "The action returned failure");
            }
            catch (NotSupportedException)
            {
                skipped = true;
            }
            catch (OperationCanceledException)
            {
                skipped = true;
            }
            catch (Exception ex3)
            {
                reason = ex3.GetType().Name + ": " + ex3.Message;
            }
            return false;
        }
        private static string EnvPolicyKey(string key)
        {
            switch (key)
            {
            case "do":
                return "GmPauseDl";
            case "wu":
                return "GmPauseUpdate";
            case "dwmboost":
                return "GmDwmBoost";
            case "services":
                return "GmPauseServices";
            case "standby":
                return "GmStandbyCleanerV2";
            case "awake":
                return "GmAwake";
            case "maint":
                return "GmPauseMaintenanceV1";
            case "laptopperf":
                return LaptopPerfMode.PreferenceKey;
            case "amdafmf":
                return "AmdAfmf";
            case "gamedvr":
                return "GameDvrOff";
            default:
                return null;
            }
        }
        private void ClearEnvFuse(string key)
        {
            SyncEnvFailureIsolation(key);
        }
        private void ApplyEnv()
        {
            bool managePowerPlan, tuneProcessor;
            using (PowerPlan.BeginSelectionReadScope())
                SyncEnvironmentPolicies(out managePowerPlan, out tuneProcessor);
            SyncEnvironmentPowerPlan(managePowerPlan, tuneProcessor);
        }
        private void SyncEnvironmentPolicies(out bool managePowerPlan, out bool tuneProcessor)
        {
            SyncTimerAutomaticCycle();
            long ticks = DateTime.UtcNow.Ticks;
            SyncAutomationPolicy();
            PolicySnapshot policySnapshot = sessionPolicy;
            PolicySnapshot policySnapshot2 = policySnapshot ?? PolicyResolver.Global();
            if (policySnapshot2.GlobalTimerLease && timerMode.LeaseAllowed)
            {
                RunGuarded(() => timerLease.Update(true, DateTime.UtcNow.Ticks));
            }
            else
            {
                timerLease.Update(false, DateTime.UtcNow.Ticks);
            }
            RecordAutomation("AutoGlobalTimer", AutomaticExecutionState("AutoGlobalTimer"));
            SyncGameDvr();
            PerformancePreset mode = policySnapshot2.Preset;
            bool pauseDownloads = BackgroundSessionPolicy.PauseDownloads(policySnapshot2, enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed, activeDetection);
            bool pauseUpdate = policySnapshot2.PauseUpdate;
            bool pauseMaintenance = policySnapshot2.PauseMaintenance;
            bool awake = BackgroundSessionPolicy.KeepAwake(enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed, activeDetection);
            bool powerPlanOn = policySnapshot2.PowerPlanOn;
            bool afmfRequested = false;
            bool slowActionsDue = slowEnvAtTicks == 0 || ticks >= slowEnvAtTicks;
            bool want = pauseDownloads && slowActionsDue && Native.IsElevated() && !SessionServiceSuspended("do", ticks)
                && (doActive || DoTweak.HasResidue || DeliveryOptimizationActivity.IsDownloading());
            bool want2 = pauseUpdate && slowActionsDue && !SessionServiceSuspended("wu", ticks);
            bool want3 = pauseMaintenance && slowActionsDue && !SessionServiceSuspended("maint", ticks);
            managePowerPlan = ResolvePowerPlanEnabled(mode, powerPlanOn);
            doActive = EnvStep("do", want, doActive, DoTweak.Activate, DoTweak.Restore);
            wuActive = EnvStep("wu", want2, wuActive, UpdatePause.Activate, UpdatePause.Restore);
            maintActive = EnvStep("maint", want3, maintActive, MaintenancePause.Activate, MaintenancePause.Restore);
            RestoreRetiredOptionalServices();
            pqosActive = EnvStep("pqos", true, pqosActive, PresenceQos.Activate, PresenceQos.Restore);
            StepDisplayAwake(awake);
            rsrActive = EnvStep("rsr", rsrOn, rsrActive, AdlxTweaks.ActivateRsr, AdlxTweaks.RestoreRsr);
            amdAfmfActive = EnvStep("amdafmf", afmfRequested && AdlxTweaks.AfmfSupported(), amdAfmfActive, AdlxTweaks.ActivateAfmf, AdlxTweaks.RestoreAfmf);
            LaptopPerformance.Step();
            PowerMaintenance.Step();
            tuneProcessor = ProcessorTuningRequested(policySnapshot2);
        }
        private void SyncEnvironmentPowerPlan(bool managePowerPlan, bool tuneProcessor)
        {
            int powerPolicyKeyResult = PowerPolicyKey(tuneProcessor, managePowerPlan);
            long ticks2 = DateTime.UtcNow.Ticks;
            bool restoreFailedPowerPlanSucceeded = RestoreFailedPowerPlan(ticks2);
            if (managePowerPlan && !stopping && restoreFailedPowerPlanSucceeded)
            {
                if (PowerPlanStopped(powerPolicyKeyResult) || !AutomaticWorkAllowed("power-queue") || (planActive && powerPolicyKeyResult == lastPowerPolicyKey && ticks2 < Interlocked.Read(ref nextPowerAuditTicks)) || Interlocked.CompareExchange(ref powerApplyInFlight, 1, 0) != 0)
                {
                    return;
                }
                int applyEnvValue = powerPolicyKeyResult;
                bool aggrShot = tuneProcessor;
                int genShot = Volatile.Read(ref powerSessionGen);
                planActive = true;
                lastPowerPolicyKey = applyEnvValue;
                Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
                bool powerWorkQueued = false;
                try
                {
                    powerWorkQueued = ThreadPool.QueueUserWorkItem(delegate
                    {
                        RunPowerPlanApply(genShot, () => PowerPlan.Enforce(aggrShot));
                    });
                }
                catch
                {
                }
                if (!powerWorkQueued)
                {
                    RecordAutomaticWorkFailure("power-queue", "queue-rejected");
                    planActive = false;
                    lastPowerPolicyKey = -1;
                    Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
                    Interlocked.Exchange(ref powerApplyInFlight, 0);
                }
            }
            else
            {
                if (!planActive || !restoreFailedPowerPlanSucceeded)
                {
                    return;
                }
                Interlocked.Increment(ref powerSessionGen);
                lock (powerApplyGate)
                {
                    if (RunAutomaticRestore("powerplan", () => LaptopPerformance.RestoreForPowerPlan() && PowerPlan.Restore()))
                    {
                        planActive = false;
                        lastPowerPolicyKey = -1;
                        Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
                    }
                }
            }
        }
        private void HandleNvTweakOutcome(List<string> failed, NvGamePlan plan, bool preStage = false)
        {
            if (failed == null || plan == null)
            {
                return;
            }
            if (plan.Automatic)
            {
                if (plan.MaxPerf)
                {
                    RecordNvAutomation("NvMaxPerf", failed.Contains("pstate") ? "failed" : "configured", preStage);
                }
                if (plan.ShaderCacheMax)
                {
                    RecordNvAutomation("NvShaderCache", (failed.Contains("shadercache") || failed.Contains("shaderon")) ? "failed" : "configured", preStage);
                }
                if (plan.Rebar)
                {
                    RecordNvAutomation("NvRebar", NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.RebarKeys) ? "failed" : "configured", preStage);
                }
                if (plan.LowLatency)
                {
                    RecordNvAutomation(PolicyCatalog.KeyGameLowLatency, failed.Contains("maxframes") ? "failed" : "configured", preStage);
                }
            }
            NoteNvKey("pstate", plan.MaxPerf, failed.Contains("pstate"));
            NoteNvKey("shadercache", plan.ShaderCacheMax, failed.Contains("shadercache") || failed.Contains("shaderon"));
            NoteNvKey("rebarfeat", plan.Rebar, NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.RebarKeys));
            bool wanted2 = (plan.DlssMode == "latest" || plan.DlssMode == "j" || plan.DlssMode == "k") && NvDrsTweaks.DlssOverrideSupported();
            NoteNvKey("dlssovr", wanted2, NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.DlssKeys));
        }
        private void NoteNvKey(string key, bool wanted, bool didFail)
        {
        }
        private bool EnvActive()
        {
            if (SessionRecoveryPending) return true;
            if (SelfYield.HasResidue) return true;
            if (Laboratory.SessionUnrestored) return true;
            if (GameNvDeliveryPending)
            {
                return true;
            }
            if (CriticalThreadHasRecoveryDebt(false))
            {
                return true;
            }
            if (FrameCoordination.HasResidue)
            {
                return true;
            }
            if (core.AutomaticLayoutPending)
            {
                return true;
            }
            if (GuardSchedulingNeedsRestore)
            {
                return true;
            }
            lock (sync)
            {
                if (envRestorePending.Count > 0)
                {
                    return true;
                }
            }
            if (CoreThreadPlacement.UnownedPending)
            {
                return true;
            }
            if (doActive || wuActive || maintActive || optionalServicesActive || pqosActive || awakeActive || LaptopPerformance.Active || LaptopPerformance.WorkerBusy || planActive || timerRaised || rsrActive || amdAfmfActive || (standbyCleaner != null && standbyCleaner.HasInFlight))
            {
                return true;
            }
            int mutationGeneration = Settings.MutationGeneration;
            if (mutationGeneration != envResidueGeneration)
            {
                envResidueOther = LaboratoryPower.HasResidue || LaboratoryGpu.HasResidue || DoTweak.HasResidue || UpdatePause.HasResidue || MaintenancePause.HasResidue || OptionalServicePause.HasResidue || PresenceQos.HasResidue || AdlxTweaks.HasResidue() || LaptopPerfMode.HasResidue || PowerPlan.HasResidue || PowerPlan.HasParkResidue() || PowerOverlay.HasResidue;
                envResidueNvList = NvDrsTweaks.HasGameResidue;
                envResidueGeneration = mutationGeneration;
            }
            return envResidueOther || NvGameResidueGate(envResidueNvList);
        }
        private bool NvGameResidueGate(bool hasResidue)
        {
            if (hasResidue)
            {
                if (enabled)
                {
                    return preStagedNvPath == null;
                }
                return true;
            }
            return false;
        }
        private void PublishLegacyNvPending()
        {
            Interlocked.Exchange(ref gameNvPending, (gameNvWorker || !gameNvRecoveryChecked || !gameNvRootClear) ? 1 : 0);
        }
        private void InitializeGameNvDeliveryMetadata()
        {
            lock (gameNvGate)
            {
                if (!gameNvRecoveryChecked)
                {
                    gameNvRootClear = GameNvCanonicalStore.RootConfirmedAbsent(dataDir);
                    gameNvRecoveryChecked = true;
                    PublishLegacyNvPending();
                }
            }
        }
        private bool StopGameNvDelivery(int timeoutMs)
        {
            if (Monitor.IsEntered(sync))
            {
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                return false;
            }
            lock (gameNvGate)
            {
                gameNvRecoveryStopped = true;
            }
            if (!gameNvIdle.WaitOne(Math.Max(0, timeoutMs)))
            {
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                return false;
            }
            try
            {
                return RestoreLegacyGameNv();
            }
            catch
            {
                lock (gameNvGate)
                {
                    gameNvRootClear = false;
                    PublishLegacyNvPending();
                }
                return false;
            }
        }
        private void InitializeStandbyCleaner()
        {
            standbyCleanerOn = Settings.Load("GmStandbyCleanerV2", false);
            string value;
            bool gmStandbyCleanerOptionsV1Enabled = Settings.TryLoadStr("GmStandbyCleanerOptionsV1", out value);
            StandbyCleanerOptions options;
            standbyCleanerOptionsValid = gmStandbyCleanerOptionsV1Enabled && (value.Length == 0 || StandbyCleanerOptions.TryParse(value, out options));
            if (gmStandbyCleanerOptionsV1Enabled && value.Length != 0 && StandbyCleanerOptions.TryParse(value, out options))
            {
                standbyCleanerOptions = options;
            }
            if (!standbyCleanerOptionsValid)
            {
                standbyCleanerOn = false;
                Logger.Log(() => Lang.T("standbycleaner.config.invalid"));
            }
            standbyCleaner = new StandbyCleanerRunner(new StandbyCleanerEngine(new StandbyMemoryControl()), OnStandbyCleanerFault);
        }
        internal bool TrySetStandbyCleanerEnabled(bool value)
        {
            bool gmStandbyCleanerV2Enabled;
            lock (sync)
            {
                if (stopping)
                {
                    return false;
                }
                InvalidateStandbyCleanerWork();
                if (value && !standbyCleanerOptionsValid)
                {
                    return false;
                }
                gmStandbyCleanerV2Enabled = Settings.Save("GmStandbyCleanerV2", value);
                standbyCleanerOn = value && gmStandbyCleanerV2Enabled;
                if (standbyCleanerOn)
                {
                    ClearEnvFuse("standby");
                }
            }
            RequestPolicyApply("TrySetStandbyCleanerEnabled");
            return gmStandbyCleanerV2Enabled;
        }
        public bool TrySetStandbyCleanerOptions(StandbyCleanerOptions value)
        {
            if (value == null || !value.IsValid)
            {
                return false;
            }
            bool gmStandbyCleanerOptionsV1Enabled;
            lock (sync)
            {
                if (stopping)
                {
                    return false;
                }
                InvalidateStandbyCleanerWork();
                if (!standbyCleanerOptionsValid)
                {
                    standbyCleanerOn = false;
                }
                gmStandbyCleanerOptionsV1Enabled = Settings.SaveStr("GmStandbyCleanerOptionsV1", value.Serialize());
                if (gmStandbyCleanerOptionsV1Enabled)
                {
                    standbyCleanerOptions = value;
                    standbyCleanerOptionsValid = true;
                }
            }
            RequestPolicyApply("TrySetStandbyCleanerOptions");
            return gmStandbyCleanerOptionsV1Enabled;
        }
        private void InvalidateStandbyCleanerWork()
        {
            Interlocked.Increment(ref standbyCleanerGeneration);
            StandbyCleanerRunner standbyCleanerRunner = standbyCleaner;
            if (standbyCleanerRunner != null)
            {
                standbyCleanerRunner.Pause();
            }
        }
        private void StandbyCleanerPolicyChanged(bool enabledValue)
        {
            if (enabledValue)
            {
                ClearEnvFuse("standby");
            }
            RequestPolicyApply("StandbyCleanerPolicyChanged");
        }
        private bool LiveStandbyCleanerPreference()
        {
            if (standbyCleanerOn)
            {
                return PerformancePolicy.EnvironmentPreference("GmStandbyCleanerV2");
            }
            return false;
        }
        private Func<bool> CaptureStandbyCleanerAdmission(out StandbyCleanerOptions options, out int generation)
        {
            int capturedGeneration;
            string profileId;
            bool wanted;
            lock (sync)
            {
                generation = (capturedGeneration = Volatile.Read(ref standbyCleanerGeneration));
                options = standbyCleanerOptions;
                if (options != null && options.IsValid)
                    ActionFailureStore.BeginAutomaticCycle("GmStandbyCleanerV2", "system", AutomaticPhaseKey(options.Serialize()));
                GameDetection gameDetection = activeDetection;
                profileId = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Id : null);
                PolicySnapshot policySnapshot = sessionPolicy;
                wanted = standbyCleanerOptionsValid && LiveStandbyCleanerPreference() && !ActionFailureStore.IsBlocked("GmStandbyCleanerV2", "system") && !string.IsNullOrEmpty(profileId) && (policySnapshot == null || string.IsNullOrEmpty(policySnapshot.ProfileId) || string.Equals(policySnapshot.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
            }
            return delegate
            {
                if (!wanted || capturedGeneration != Volatile.Read(ref standbyCleanerGeneration) || !Volatile.Read(ref active) || !enabled || stopping || panicReq || Volatile.Read(ref standbyCleanerRestorePending) != 0 || ProfileStoreSaveFailed || Volatile.Read(ref stickyGraceOnly) || Volatile.Read(ref gameGoneSinceTicks) != 0)
                {
                    return false;
                }
                GameDetection gameDetection2 = Volatile.Read(ref activeDetection);
                return gameDetection2 != null && gameDetection2.Profile != null && string.Equals(gameDetection2.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase);
            };
        }
        private void ApplyStandbyCleanerPolicy()
        {
            StandbyCleanerRunner standbyCleanerRunner = standbyCleaner;
            if (standbyCleanerRunner != null)
            {
                StandbyCleanerOptions options;
                int generation;
                Func<bool> func = CaptureStandbyCleanerAdmission(out options, out generation);
                if (func())
                {
                    standbyCleanerRunner.Update(generation, options, func);
                }
                else
                {
                    standbyCleanerRunner.Pause();
                }
            }
        }
        private void OnStandbyCleanerFault(int generation, StandbyCleanerResult result, int nativeStatus)
        {
            lock (sync)
            {
                if (generation != Volatile.Read(ref standbyCleanerGeneration) || stopping || !active || !enabled || panicReq || ProfileStoreSaveFailed || !envFused.Add("standby"))
                {
                    return;
                }
                ActionFailureStore.Trip("GmStandbyCleanerV2", "system", string.Concat(result, " | 0x", nativeStatus.ToString("X8", CultureInfo.InvariantCulture)));
            }
            Logger.Warn(string.Concat(Lang.T("log.gamemodeenv.9"), EnvLabel("standby"), Lang.T("log.gamemodeenv.10"), "1", Lang.T("log.gamemodeenv.11"), " [", result, ", 0x", nativeStatus.ToString("X8", CultureInfo.InvariantCulture), "]"));
            RequestPolicyApply("OnStandbyCleanerFault");
        }
        private bool PerformanceScopeAllows(string imagePath)
        {
            return true;
        }
    }
}
