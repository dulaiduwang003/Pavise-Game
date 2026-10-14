// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private const int EnvRetryBaseSeconds = 4;
        private const int EnvRetryCapSeconds = 60;
        private const int EnvRetryMaxSteps = 8;
        private readonly Dictionary<string, long> envNextAttempt =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> envFailures =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> envFused =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> envRestorePending = new HashSet<string>(StringComparer.Ordinal);

        internal static readonly string[] EnvKeys =
            { "do", "wu", "services", "standby",
              "pqos", "awake", "dwmboost",
              "rsr", "gpupower", "amdalag", "amdafmf", "maint",
              "nvvrr", "intelend", "laptopperf", "gamedvr", "mmcss" };

        private static string EnvLabel(string key)
        {
            switch (key)
            {
                case "do": return Lang.T("t.gamemodeenv.1");
                case "wu": return Lang.T("t.gamemodeenv.3");
                case "services": return Lang.T("gm.pausesvc");
                case "standby": return Lang.T("gm.standbycleaner");
                case "pqos": return Lang.T("t.gamemodeenv.4");
                case "awake": return Lang.T("t.gamemodeenv.5");
                case "dwmboost": return Lang.T("gm.dwmboost");
                case "rsr": return Lang.T("set.rsr");
                case "gpupower": return Lang.T("t.gamemodeenv.6");
                case "maint": return Lang.T("gm.pausemaint");
                case "nvvrr": return Lang.T("set.nvvrr");
                case "intelend": return Lang.T("set.intel.endurance");
                case "laptopperf": return Lang.T("gm.laptopperf");
                case "amdalag": return "AMD Anti-Lag";
                case "amdafmf": return Lang.T("t.gamemodeenv.8");
                case "GameDvrOff": return Lang.T("set.dvr");
                case "GmMmcss": return Lang.T("auto.mmcss");
                case "gamedvr": return Lang.T("set.dvr");
                case "mmcss": return Lang.T("auto.mmcss");
                default: return key;
            }
        }

        private bool EnvStep(
            string key, bool want, bool active, Func<bool> activate, Func<bool> restore)
        {
            string action = EnvFailureAction(key);
            bool blocked = SyncEnvFailureIsolation(key);
            // 隔离只拦截新应用，部分写入和已应用项目仍须恢复。
            lock (sync)
                if (envRestorePending.Contains(key)) { want = false; active = true; }
            if (want && blocked) want = false;
            if (want == active) return active;
            long now = DateTime.UtcNow.Ticks;
            lock (sync)
            {
                long next;
                if (envNextAttempt.TryGetValue(key, out next) && now < next
                    && !ActionFailureStore.IsRetryPending(action, "system")) return active;
            }

            string reason;
            if (want && (activate == null || !EnvironmentApplyReady(key)
                || !ActionFailureStore.TryBeginApply(action, "system", out reason)))
                return active;
            bool skipped;
            bool ok = RunEnvironmentAction(want ? activate : restore, out skipped, out reason);
            if (want && skipped)
            {
                bool restoreSkipped;
                string restoreReason;
                bool restored = RunEnvironmentAction(restore, out restoreSkipped, out restoreReason);
                lock (sync)
                {
                    if (restored) envRestorePending.Remove(key);
                    else { envRestorePending.Add(key); ScheduleEnvRestoreRetryLocked(key); }
                }
                if (restored) ActionFailureStore.CancelApply(action, "system");
                else ActionFailureStore.Trip(action, "system", AutomaticPolicy.Text("取消或跳过后的恢复失败：", "Recovery after cancellation or skip failed: ") + restoreReason);
                return !restored;
            }
            RecordAutomation(key, ok ? (want ? key == "services" && OptionalServicePause.PendingStopCount > 0 ? "unverified" : "applied" : "restored") : (want ? "failed" : "restore-failed"));
            if (want)
            {
                if (ok)
                {
                    bool completed = ActionFailureStore.CompleteApply(action, "system");
                    if (!completed)
                    {
                        bool completionRestoreSkipped;
                        string completionRestoreReason;
                        bool completionRestored = RunEnvironmentAction(restore, out completionRestoreSkipped, out completionRestoreReason);
                        RecordAutomation(key, completionRestored ? "restored" : "restore-failed");
                        lock (sync)
                        {
                            if (completionRestored) envRestorePending.Remove(key);
                            else { envRestorePending.Add(key); ScheduleEnvRestoreRetryLocked(key); }
                        }
                        if (completionRestored) ActionFailureStore.CancelApply(action, "system");
                        else ActionFailureStore.Trip(action, "system", AutomaticPolicy.Text("完成记录失败且回退失败：", "Completion persistence and rollback failed: ") + completionRestoreReason);
                        SyncEnvFailureIsolation(key);
                        return !completionRestored;
                    }
                    lock (sync)
                    {
                        envNextAttempt.Remove(key);
                        envFailures.Remove(key);
                    }
                    SyncEnvFailureIsolation(key);
                    if (completed && Settings.Load("EnvFuse_" + key, false)) Settings.Save("EnvFuse_" + key, false);
                    return true;
                }

                ActionFailureStore.Trip(action, "system", reason);
                lock (sync) envFused.Add(key);
                Logger.Warn(EnvLabel(key) + AutomaticPolicy.Text("：执行失败，已隔离；可在记录中重试。", ": apply failed and isolated; retry from records."));
                // 首次失败就撤销已写入部分。恢复失败仅退避重试，不能被隔离阻断。
                bool restoreSkipped;
                string restoreReason;
                bool restored = RunEnvironmentAction(restore, out restoreSkipped, out restoreReason);
                RecordAutomation(key, restored ? "restored" : "restore-failed");
                lock (sync)
                {
                    if (restored)
                    {
                        envRestorePending.Remove(key);
                        envNextAttempt.Remove(key);
                        envFailures.Remove(key);
                    }
                    else { envRestorePending.Add(key); ScheduleEnvRestoreRetryLocked(key); }
                }
                if (restored) ActionFailureStore.CancelApply(action, "system");
                else ActionFailureStore.Trip(action, "system", reason + AutomaticPolicy.Text("；回退失败：", "; rollback failed: ") + restoreReason);
                return !restored;
            }

            lock (sync)
            {
                if (ok)
                {
                    ActionFailureStore.CancelApply(action, "system");
                    envRestorePending.Remove(key);
                    envNextAttempt.Remove(key);
                    envFailures.Remove(key);
                }
                else
                {
                    envRestorePending.Add(key); ScheduleEnvRestoreRetryLocked(key);
                    if (!skipped)
                        ActionFailureStore.Trip(action, "system", AutomaticPolicy.Text("恢复失败：", "Recovery failed: ") + reason);
                    else if (ActionFailureStore.IsRetryPending(action, "system"))
                        ActionFailureStore.Trip(action, "system", AutomaticPolicy.Text("重试前恢复尚未完成", "Recovery before retry is incomplete"));
                }
            }
            return ok ? false : active;
        }

        private static string EnvFailureAction(string key)
        {
            string policy = EnvPolicyKey(key);
            if (policy != null) return policy;
            switch (key)
            {
                case "rsr": return "GmRsr";
                case "nvvrr": return "NvVrrWindowed";
                case "pqos": return "PresenceQos";
                default: return key;
            }
        }

        private bool SyncEnvFailureIsolation(string key)
        {
            bool blocked = ActionFailureStore.IsBlocked(EnvFailureAction(key), "system");
            lock (sync)
            {
                if (blocked) envFused.Add(key);
                else envFused.Remove(key);
            }
            return blocked;
        }

        private bool EnvironmentApplyReady(string key)
        {
#if PAVISE_SELFTEST
            if (EnvironmentReadyForTest != null) return EnvironmentReadyForTest(key);
#else
            if ((key == "maint" || key == "pqos" || key == "do" || key == "wu" || key == "services")
                && !Native.IsElevated()) return false;
            if (key == "do" && Native.IsDomainJoined() && SvcState.Query("DoSvc") == 1 && !DoTweak.HasResidue)
                return false;
#endif
            if (key == "maint") return Native.IsElevated();
            if (key == "rsr") return AdlxTweaks.RsrSupported();
            if (key == "amdafmf") return AdlxTweaks.AfmfSupported();
            if (key == "laptopperf")
                return LaptopPerformanceAdmitted() && LaptopPerfMode.CanActivate && !LaptopPerfMode.HasResidue;
            return true;
        }

        private void ScheduleEnvRestoreRetryLocked(string key)
        {
            int failures;
            envFailures.TryGetValue(key, out failures);
            failures = Math.Min(failures + 1, EnvRetryMaxSteps);
            envFailures[key] = failures;
            int seconds = EnvRetryBaseSeconds;
            for (int i = 1; i < failures && seconds < EnvRetryCapSeconds; i++)
                seconds = Math.Min(EnvRetryCapSeconds, seconds * 2);
            envNextAttempt[key] = DateTime.UtcNow.AddSeconds(seconds).Ticks;
        }

        private static bool RunEnvironmentAction(Func<bool> action, out bool skipped, out string reason)
        {
            skipped = action == null;
            reason = "";
            if (skipped) return false;
            try
            {
                if (action()) return true;
                reason = AutomaticPolicy.Text("执行返回失败", "The action returned failure");
            }
            catch (NotSupportedException) { skipped = true; }
            catch (OperationCanceledException) { skipped = true; }
            catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; }
            return false;
        }

        private static bool RunGuarded(Func<bool> action)
        {
            try { return action != null && action(); }
            catch { return false; }
        }

#if PAVISE_SELFTEST
        internal static Func<string, bool> EnvironmentReadyForTest;
        internal int EnvAttemptCountForTest(
            string key, bool want, bool active, Func<bool> activate, Func<bool> restore, int rounds)
        {
            int attempts = 0;
            Func<bool> countedActivate = delegate { attempts++; return activate(); };
            Func<bool> countedRestore = delegate { attempts++; return restore(); };
            for (int i = 0; i < rounds; i++)
                active = EnvStep(key, want, active, countedActivate, countedRestore);
            return attempts;
        }

        internal void ClearEnvRetryStateForTest() { ClearEnvRetryState(); }
#endif

        private void ClearEnvRetryState()
        {
            lock (sync)
            {
                envNextAttempt.Clear();
                envFailures.Clear();
            }
        }

        private void BeginRetrySession()
        {
            foreach (string key in EnvKeys) SyncEnvFailureIsolation(key);
            PowerPlanStopped(lastPowerPolicyKey);
        }

        private void DisableEnvSwitch(string key)
        {
            ActionFailureStore.Trip(EnvFailureAction(key), "system",
                AutomaticPolicy.Text("执行失败，已停止后续应用", "Apply failed; later attempts isolated"));
            lock (sync) envFused.Add(key);
        }

        private static string EnvPolicyKey(string key)
        {
            switch (key)
            {
                case "do": return PolicyCatalog.KeyPauseDl;
                case "wu": return PolicyCatalog.KeyPauseUpdate;
                case "dwmboost": return PolicyCatalog.KeyDwmBoost;
                case "gpupower": return PolicyCatalog.KeyGpuPowerMax;
                case "services": return PolicyCatalog.KeyPauseServices;
                case "standby": return PolicyCatalog.KeyStandbyCleaner;
                case "awake": return PolicyCatalog.KeyAwake;
                case "maint": return PolicyCatalog.KeyPauseMaintenance;
                case "intelend": return PolicyCatalog.KeyIntelEndurance;
                case "laptopperf": return LaptopPerfMode.PreferenceKey;
                case "amdalag": return PolicyCatalog.KeyAmdAntiLag;
                case "amdafmf": return PolicyCatalog.KeyAmdAfmf;
                case "gamedvr": return "GameDvrOff";
                case "mmcss": return "GmMmcss";
                default: return null;
            }
        }

        private void ClearEnvFuse(string key)
        {
            // 设置和模式切换不是重试授权；只有记录中的显式重试可放行一次。
            SyncEnvFailureIsolation(key);
        }

        private void ApplyEnv()
        {
            bool wantTimer = AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyGlobalTimer) && timerMode.GlobalReady;
            if (wantTimer)
                RunGuarded(delegate { return timerLease.Update(true, DateTime.UtcNow.Ticks); });
            else timerLease.Update(false, DateTime.UtcNow.Ticks);
            RecordAutomation(PolicyCatalog.KeyGlobalTimer, AutomaticExecutionState(PolicyCatalog.KeyGlobalTimer));
            SyncAutomationPolicy();
            if (AutomaticPolicy.Enabled) SyncGameDvr();
            PolicySnapshot sp = sessionPolicy;
            PerformancePreset mode = sp != null ? sp.Preset : ActivePreset;
            bool pPauseDl = sp != null ? sp.PauseDownloads : pauseDlOn;
            bool pWu = sp != null ? sp.PauseUpdate : pauseUpdateOn;
            bool pMaint = sp != null ? sp.PauseMaintenance : pauseMaintOn;
            bool pAwake = sp != null ? sp.Awake : awakeOn;
            bool pPlan = sp != null ? sp.PowerPlanOn : planSwitch;
            bool pAggr = sp != null ? sp.Aggressive : aggressiveOn;
            bool pAmdAfmf = sp != null ? sp.AmdAfmf : amdAfmf;
            bool competitive = PresetValue.IsCompetitive(mode);
            bool idlePolicy = sp != null ? sp.IdlePolicy : PolicyResolver.Global().IdlePolicy;
            bool handheld = IsHandheld(mode);
            bool usePauseDl = competitive || handheld;
            bool slowReady = slowEnvAtTicks == 0 || DateTime.UtcNow.Ticks >= slowEnvAtTicks;
            usePauseDl = usePauseDl && slowReady;
            bool usePlan = ResolvePowerPlanEnabled(mode, pPlan);
            SuppressionCore.GpuDemoteEnabled = sp != null ? sp.GpuDemote : gpuDemoteOn;
            WsTrim.Enabled = sp != null ? sp.WsTrim : wsTrimOn;
            doActive = EnvStep("do", usePauseDl, doActive, DoTweak.Activate, DoTweak.Restore);
            wuActive = EnvStep("wu", pWu && slowReady, wuActive, UpdatePause.Activate, UpdatePause.Restore);
            maintActive = EnvStep("maint", pMaint && slowReady, maintActive,
                MaintenancePause.Activate, MaintenancePause.Restore);
            ApplyOptionalServices(slowReady);
            pqosActive = EnvStep("pqos", true, pqosActive, PresenceQos.Activate, PresenceQos.Restore);
            if (AutomaticPolicy.Enabled)
            {
                GameDetection foregroundGame = activeDetection;
                pAwake = pAwake && foregroundGame != null && foregroundGame.RendererPid == RendererForegroundPid();
            }
            awakeActive = EnvStep("awake", pAwake, awakeActive, DisplayAwake.Activate, DisplayAwake.Restore);
            dwmBoostActive = EnvStep("dwmboost", dwmBoostOn, dwmBoostActive,
                DwmBoost.Activate, DwmBoost.Restore);
            rsrActive = EnvStep("rsr", rsrOn, rsrActive, AdlxTweaks.ActivateRsr, AdlxTweaks.RestoreRsr);
            gpwActive = EnvStep("gpupower", false,
                gpwActive || GpuPowerMax.HasResidue(), null, GpuPowerMax.Restore);
            nvVrrActive = EnvStep("nvvrr", nvVrrWindowedOn && NvApi.Available,
                nvVrrActive || (!nvVrrWindowedOn && NvVrrWindowed.HasResidue), NvVrrWindowed.Activate, NvVrrWindowed.Restore);
            intelEndActive = EnvStep("intelend",
                EffIntelEnduranceOff && IntelGraphicsApi.HasAvailable && IntelEndurance.Supported(),
                intelEndActive, IntelEndurance.Activate, IntelEndurance.Restore);
            amdAlagActive = EnvStep("amdalag", false,
                amdAlagActive || AdlxTweaks.HasAntiLagResidue(), null, RestoreAmdAntiLagEnv);
            amdAfmfActive = EnvStep("amdafmf", pAmdAfmf && AdlxTweaks.AfmfSupported(), amdAfmfActive,
                AdlxTweaks.ActivateAfmf, AdlxTweaks.RestoreAfmf);
            bool aggressivePower = IsAggressive(mode, pAggr)
                || (adaptiveEscalated && mode == PerformancePreset.Standard);
            int powerKey = (aggressivePower ? 1 : 0) | (usePlan ? 2 : 0)
                | (handheld ? 8 : 0) | (idlePolicy ? 16 : 0);
            long nowTicks = DateTime.UtcNow.Ticks;
            bool powerRestoreReady = RestoreFailedPowerPlan(nowTicks);
            if (usePlan && !stopping && powerRestoreReady)
            {
                if (!PowerPlanStopped(powerKey) && (!planActive || powerKey != lastPowerPolicyKey
                    || nowTicks >= Interlocked.Read(ref nextPowerAuditTicks)))
                {
                    if (Interlocked.CompareExchange(ref powerApplyInFlight, 1, 0) == 0)
                    {
                        int keyShot = powerKey;
                        bool aggrShot = aggressivePower;
                        bool handheldShot = handheld;
                        bool idleShot = idlePolicy;
                        int genShot = Volatile.Read(ref powerSessionGen);
                        planActive = true;
                        lastPowerPolicyKey = keyShot;
                        Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
                        bool queued = false;
                        try
                        {
                            queued = ThreadPool.QueueUserWorkItem(delegate
                            {
                                RunPowerPlanApply(genShot,
                                    delegate { return PowerPlan.Enforce(aggrShot, handheldShot, idleShot); });
                            });
                        }
                        catch { }
                        if (!queued)
                        {
                            planActive = false;
                            lastPowerPolicyKey = -1;
                            Interlocked.Exchange(ref nextPowerAuditTicks, 0);
                            Interlocked.Exchange(ref powerApplyInFlight, 0);
                        }
                    }
                }
            }
            else if (planActive && powerRestoreReady)
            {
                Interlocked.Increment(ref powerSessionGen);
                lock (powerApplyGate)
                {
                    if (RunGuarded(
                            delegate { return PowerPlan.Restore(); }))
                    {
                        planActive = false;
                        lastPowerPolicyKey = -1;
                        Interlocked.Exchange(ref nextPowerAuditTicks, 0);
                    }
                }
            }

            // 先改 Windows 方案再改厂商模式，恢复按相反顺序。
            SyncLaptopPerformance(!usePlan || (PowerPlanStopped(powerKey) && !PowerPlan.HasResidue)
                || (planActive && Volatile.Read(ref powerApplyInFlight) == 0));

        }

        private bool wuActive;
        private bool optionalServicesActive;
        private bool optionalServicesWanted;
        private int optionalServiceGeneration;
        private bool amdAlagActive;
        private bool amdAfmfActive;
        private bool rsrActive;

        private bool EffPauseServices
        {
            get
            {
                PolicySnapshot sp = sessionPolicy;
                if (AutomaticPolicy.Enabled) return (sp ?? PolicyResolver.Global()).PauseServices;
                if (sp == null || string.IsNullOrEmpty(sp.ProfileId)) return pauseServicesOn;
                lock (sync)
                    foreach (GameProfile profile in profiles)
                        if (string.Equals(profile.Id, sp.ProfileId, StringComparison.OrdinalIgnoreCase))
                        {
                            string value;
                            return profile.Overrides.TryGetValue(PolicyCatalog.KeyPauseServices, out value)
                                ? value == "1" : pauseServicesOn;
                        }
                return false;
            }
        }

        private void ApplyOptionalServices(bool slowReady)
        {
            Func<bool> mayContinue = CaptureOptionalServicesAdmission();
            if (OptionalServicePause.Active && !OptionalServicePause.AuditStops(mayContinue))
                IsolateOptionalServiceAudit(OptionalServicePause.LastFailure);
            optionalServicesActive = StepOptionalServices(slowReady && mayContinue(), optionalServicesActive,
                delegate { return OptionalServicePause.HasResidue; },
                delegate { return OptionalServicePause.MatchesSelection(EffPauseServices); },
                delegate { return OptionalServicePause.Activate(mayContinue, EffPauseServices); }, OptionalServicePause.Restore);
        }

        private void IsolateOptionalServiceAudit(string reason)
        {
            ActionFailureStore.Trip(EnvFailureAction("services"), "system", reason ?? "Service pause verification failed");
            lock (sync) envRestorePending.Add("services");
        }

        private Func<bool> CaptureOptionalServicesAdmission()
        {
            int generation = Volatile.Read(ref optionalServiceGeneration);
            return delegate
            {
                return generation == Volatile.Read(ref optionalServiceGeneration)
                    && !stopping && !panicReq && enabled && !ProfileStoreSaveFailed && OptionalServicesRequested;
            };
        }

        private bool OptionalServicesRequested
        { get { return EffPauseServices || Settings.Load(OptionalServicePauseEngine.PcaPreferenceKey, false); } }

        private bool StepOptionalServices(bool want, bool applied,
            Func<bool> hasResidue, Func<bool> isApplied, Func<bool> activate, Func<bool> restore)
        {
            lock (sync)
            {
                if (optionalServicesWanted != want)
                {
                    optionalServicesWanted = want;
                    if (!envRestorePending.Contains("services"))
                    {
                        envNextAttempt.Remove("services");
                        envFailures.Remove("services");
                    }
                }
            }
            // 部分激活失败仍可能留下真实改动，不能跳过恢复。
            if (want) applied = isApplied();
            else if (hasResidue()) applied = true;
            bool result = EnvStep("services", want, applied, activate, restore);
            return want ? result && isApplied() : result;
        }

#if PAVISE_SELFTEST
        internal bool ProbeEffPauseServices { get { return EffPauseServices; } }
        internal bool ProbeOptionalServicesRequested { get { return OptionalServicesRequested; } }
        internal bool StepConfiguredOptionalServicesForTest(bool applied, OptionalServicePauseEngine engine)
        {
            bool want = OptionalServicesRequested;
            if (engine.Active && !engine.AuditStops(delegate { return want; })) IsolateOptionalServiceAudit(engine.LastFailure);
            return StepOptionalServices(want, applied, delegate { return engine.HasResidue; },
                delegate { return engine.MatchesSelection(Settings.Load(OptionalServicePauseEngine.PcaPreferenceKey, false), EffPauseServices); },
                delegate { return engine.Activate(null, Settings.Load(OptionalServicePauseEngine.PcaPreferenceKey, false), EffPauseServices); }, engine.Restore);
        }
        internal Func<bool> CaptureOptionalServicesAdmissionForTest() { return CaptureOptionalServicesAdmission(); }
        internal bool StepOptionalServicesForTest(bool want, bool applied, OptionalServicePauseEngine engine)
        {
            if (engine.Active && !engine.AuditStops(delegate { return want; }))
                IsolateOptionalServiceAudit(engine.LastFailure);
            return StepOptionalServices(want, applied, delegate { return engine.HasResidue; },
                delegate { return engine.Active; },
                delegate { return engine.Activate(); }, engine.Restore);
        }
#endif

        private static bool RestoreAmdAntiLagEnv()
        {
            bool ok = AdlxTweaks.RestoreAntiLag();
            ok &= AdlxTweaks.RestoreChill();
            return ok;
        }

        private volatile bool planActive;
        private volatile int lastPowerPolicyKey = -1;
        private long nextPowerAuditTicks;
        private int powerApplyInFlight;
        private int powerPlanNotificationPending;
        private int powerSessionGen;
        private readonly object powerApplyGate = new object();
        private readonly HashSet<int> powerStoppedKeys = new HashSet<int>();
#if PAVISE_SELFTEST
        internal static Func<bool> PowerRestoreForTest;
#endif
        private static bool RestorePowerPlan()
        {
#if PAVISE_SELFTEST
            if (PowerRestoreForTest != null) return PowerRestoreForTest();
#endif
            return PowerPlan.Restore();
        }

        private void RollbackPowerPlan(string reason)
        {
            bool restored = RunGuarded(RestorePowerPlan);
            lock (sync)
            {
                if (restored)
                {
                    envRestorePending.Remove("powerplan");
                    envNextAttempt.Remove("powerplan");
                    envFailures.Remove("powerplan");
                }
                else { envRestorePending.Add("powerplan"); ScheduleEnvRestoreRetryLocked("powerplan"); }
            }
            if (restored) ActionFailureStore.CancelApply(PolicyCatalog.KeyPowerPlan, "system");
            else ActionFailureStore.Trip(PolicyCatalog.KeyPowerPlan, "system", reason
                + AutomaticPolicy.Text("；电源计划回退失败", "; power-plan rollback failed"));
            planActive = !restored;
            if (restored) lastPowerPolicyKey = -1;
        }
        private bool PowerPlanStopped(int key)
        {
            bool blocked = ActionFailureStore.IsBlocked(PolicyCatalog.KeyPowerPlan, "system");
            lock (sync)
            {
                if (blocked) powerStoppedKeys.Add(key);
                else powerStoppedKeys.Remove(key);
            }
            return blocked;
        }

        private bool RestoreFailedPowerPlan(long now)
        {
            bool pending;
            lock (sync)
            {
                pending = envRestorePending.Contains("powerplan");
                long next;
                if (pending && envNextAttempt.TryGetValue("powerplan", out next) && now < next
                    && !ActionFailureStore.IsRetryPending(PolicyCatalog.KeyPowerPlan, "system")) return false;
            }
            if (!pending && (!ActionFailureStore.IsRetryPending(PolicyCatalog.KeyPowerPlan, "system")
                || (!planActive && !PowerPlan.HasResidue))) return true;
            lock (powerApplyGate)
            {
                bool restored = RunGuarded(RestorePowerPlan);
                lock (sync)
                {
                    if (restored)
                    {
                        ActionFailureStore.CancelApply(PolicyCatalog.KeyPowerPlan, "system");
                        envRestorePending.Remove("powerplan");
                        envNextAttempt.Remove("powerplan");
                        envFailures.Remove("powerplan");
                    }
                    else
                    {
                        envRestorePending.Add("powerplan"); ScheduleEnvRestoreRetryLocked("powerplan");
                        if (ActionFailureStore.IsRetryPending(PolicyCatalog.KeyPowerPlan, "system"))
                            ActionFailureStore.Trip(PolicyCatalog.KeyPowerPlan, "system",
                                AutomaticPolicy.Text("重试前电源计划恢复失败", "Power-plan recovery before retry failed"));
                    }
                }
                planActive = !restored;
                if (restored)
                {
                    lastPowerPolicyKey = -1;
                    Interlocked.Exchange(ref nextPowerAuditTicks, 0);
                }
                return restored;
            }
        }

        internal void NotifyPowerSchemeChanged()
        {
            bool shouldAudit;
            lock (sync)
                shouldAudit = enabled && active && planActive && !stopping && !PowerPlan.NoSwitchSelected;
            if (!shouldAudit) return;

            Interlocked.Exchange(ref powerPlanNotificationPending, 1);
            QueuePowerPlanNotificationAudit();
        }

        private void QueuePowerPlanNotificationAudit()
        {
            if (Interlocked.CompareExchange(ref powerApplyInFlight, 1, 0) != 0) return;

            int keyShot;
            int genShot;
            lock (sync)
            {
                keyShot = lastPowerPolicyKey;
                genShot = Volatile.Read(ref powerSessionGen);
                if (!enabled || !active || !planActive || stopping || PowerPlan.NoSwitchSelected
                    || keyShot < 0 || (keyShot & 2) == 0 || PowerPlanStopped(keyShot)
                    || (planFailStreak > 0 && DateTime.UtcNow.Ticks < Interlocked.Read(ref nextPowerAuditTicks)))
                {
                    Interlocked.Exchange(ref powerPlanNotificationPending, 0);
                    Interlocked.Exchange(ref powerApplyInFlight, 0);
                    return;
                }
            }

            Interlocked.Exchange(ref powerPlanNotificationPending, 0);
            bool queued = false;
            try
            {
                queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    RunPowerPlanNotificationAudit(genShot, keyShot);
                });
            }
            catch { }
            if (!queued)
            {
                Interlocked.Exchange(ref powerPlanNotificationPending, 1);
                Interlocked.Exchange(ref powerApplyInFlight, 0);
            }
        }

        private bool PowerPlanNotificationStillCurrent(int genShot, int keyShot)
        {
            return PowerPlanNotificationSessionCurrent(genShot, keyShot) && !PowerPlanStopped(keyShot);
        }

        private bool PowerPlanNotificationSessionCurrent(int genShot, int keyShot)
        {
            lock (sync)
                return enabled && active && planActive && !stopping && !PowerPlan.NoSwitchSelected
                    && Volatile.Read(ref powerSessionGen) == genShot
                    && lastPowerPolicyKey == keyShot;
        }

        private void RunPowerPlanNotificationAudit(int genShot, int keyShot)
        {
            try
            {
                lock (powerApplyGate)
                {
                    if (!PowerPlanNotificationStillCurrent(genShot, keyShot)) return;
                    string reason;
                    if (!ActionFailureStore.TryBeginApply(PolicyCatalog.KeyPowerPlan, "system", out reason)) return;
                    bool skipped;
                    bool ok = RunEnvironmentAction(delegate
                    {
                        if (!PowerPlanNotificationSessionCurrent(genShot, keyShot)) throw new OperationCanceledException();
                        return PowerPlan.Enforce((keyShot & 1) != 0, (keyShot & 8) != 0, (keyShot & 16) != 0);
                    }, out skipped, out reason);
                    if (skipped) { RollbackPowerPlan(AutomaticPolicy.Text("电源计划应用已跳过", "Power-plan apply was skipped")); return; }
                    if (!PowerPlanNotificationSessionCurrent(genShot, keyShot))
                    {
                        if (!ok) ActionFailureStore.Trip(PolicyCatalog.KeyPowerPlan, "system", reason);
                        RollbackPowerPlan(AutomaticPolicy.Text("电源计划任务已取消", "Power-plan task was cancelled"));
                        return;
                    }
                    OnPowerPlanAppliedWithReason(ok, reason);
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref powerApplyInFlight, 0);
                if (Interlocked.CompareExchange(
                    ref powerPlanNotificationPending, 0, 0) != 0)
                    QueuePowerPlanNotificationAudit();
            }
        }

        private bool RunPowerPlanApply(int genShot, Func<bool> apply)
        {
            try
            {
                lock (powerApplyGate)
                {
                    // 对局已恢复后，排队任务不能再次应用旧方案。
                    if (stopping || PowerPlan.NoSwitchSelected || Volatile.Read(ref powerSessionGen) != genShot || PowerPlanStopped(lastPowerPolicyKey)) return false;
                    if (apply == null) return false;
                    string reason;
                    if (!ActionFailureStore.TryBeginApply(PolicyCatalog.KeyPowerPlan, "system", out reason)) return false;
                    bool skipped;
                    bool planOk = RunEnvironmentAction(delegate
                    {
                        if (stopping || PowerPlan.NoSwitchSelected || Volatile.Read(ref powerSessionGen) != genShot)
                            throw new OperationCanceledException();
                        return apply();
                    }, out skipped, out reason);
                    if (skipped) { RollbackPowerPlan(AutomaticPolicy.Text("电源计划应用已跳过", "Power-plan apply was skipped")); return false; }
                    if (stopping || PowerPlan.NoSwitchSelected || Volatile.Read(ref powerSessionGen) != genShot)
                    {
                        if (!planOk) ActionFailureStore.Trip(PolicyCatalog.KeyPowerPlan, "system", reason);
                        RollbackPowerPlan(AutomaticPolicy.Text("电源计划任务已取消", "Power-plan task was cancelled"));
                        return false;
                    }
                    OnPowerPlanAppliedWithReason(planOk, reason);
                    return planOk && !ActionFailureStore.IsBlocked(PolicyCatalog.KeyPowerPlan, "system");
                }
            }
            catch { return false; }
            finally
            {
                Interlocked.Exchange(ref powerApplyInFlight, 0);
                if (Interlocked.CompareExchange(
                    ref powerPlanNotificationPending, 0, 0) != 0)
                    QueuePowerPlanNotificationAudit();
            }
        }

        private const string PowerFailStreakKey = "PowerPlanFailStreak";
        private int planFailStreak;

        private void OnPowerPlanApplied(bool planOk)
        {
            OnPowerPlanAppliedWithReason(planOk, AutomaticPolicy.Text("电源计划执行返回失败", "The power-plan action returned failure"));
        }

        private void OnPowerPlanAppliedWithReason(bool planOk, string reason)
        {
            if (planOk)
            {
                if (!ActionFailureStore.CompleteApply(PolicyCatalog.KeyPowerPlan, "system"))
                {
                    RollbackPowerPlan(AutomaticPolicy.Text("电源计划完成记录失败", "Power-plan completion could not be persisted"));
                    return;
                }
                planFailStreak = 0;
                if (LoadCounter(PowerFailStreakKey) != 0) SaveCounter(PowerFailStreakKey, 0);
                lock (sync) powerStoppedKeys.Clear();
                // 成功后仅按系统通知或策略变更复查。
                Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
                return;
            }
            planFailStreak = 1;
            ActionFailureStore.Trip(PolicyCatalog.KeyPowerPlan, "system", reason);
            Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
            lock (sync) powerStoppedKeys.Add(lastPowerPolicyKey);
            Logger.Warn(Lang.T("log.power.retry.stopped"));
            RollbackPowerPlan(reason);
        }
        private static int LoadCounter(string key)
        {
            int value;
            return int.TryParse(Settings.LoadStr(key, "0"), out value) && value > 0 ? value : 0;
        }

        private static void SaveCounter(string key, int value)
        {
            Settings.SaveStr(key, value.ToString());
        }

        private void RecordNvAutomation(string key, string state, bool preStage)
        {
            if (!preStage) { RecordAutomation(key, state); return; }
            if (!AutomaticPolicy.Enabled) return;
            string line = "PRESTAGE " + Lang.T("auto.log.prestage") + " | "
                + AutomationActionLabel(key) + " | " + AutomationStateLabel(state);
            if (state == "failed") Logger.Warn(line); else Logger.Info(line);
        }

        private void HandleNvTweakOutcome(List<string> failed, NvGamePlan plan, bool preStage = false)
        {
            if (failed == null || plan == null) return;
            if (plan.Automatic)
            {
                if (plan.LowLatMode == "on") RecordNvAutomation(PolicyCatalog.KeyNvLowLat,
                    failed.Contains(NvDrsTweaks.KeyPreRender) ? "failed" : "configured", preStage);
                if (plan.MaxPerf) RecordNvAutomation(PolicyCatalog.KeyNvMaxPerf,
                    failed.Contains(NvDrsTweaks.KeyPState) ? "failed" : "configured", preStage);
                if (plan.ShaderCacheMax) RecordNvAutomation(PolicyCatalog.KeyNvShaderCache,
                    failed.Contains(NvDrsTweaks.KeyShaderCache) || failed.Contains(NvDrsTweaks.KeyShaderOn) ? "failed" : "configured", preStage);
            }
            NoteNvKey(NvDrsTweaks.KeyPState, plan.MaxPerf, failed.Contains(NvDrsTweaks.KeyPState));
            bool lowLatWanted = plan.LowLatMode == "on" || plan.LowLatMode == "ultra";
            NoteNvKey(NvDrsTweaks.KeyPreRender, lowLatWanted,
                plan.LowLatMode == "ultra"
                    ? NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.UltraKeys)
                    : failed.Contains(NvDrsTweaks.KeyPreRender));
            NoteNvKey(NvDrsTweaks.KeySmooth, plan.SmoothMotion && NvDrsTweaks.SmoothMotionSupported(),
                failed.Contains(NvDrsTweaks.KeySmooth));
            NoteNvKey(NvDrsTweaks.KeyShaderCache, plan.ShaderCacheMax,
                failed.Contains(NvDrsTweaks.KeyShaderCache) || failed.Contains(NvDrsTweaks.KeyShaderOn));
            NoteNvKey(NvDrsTweaks.KeyRebarFeat, plan.Rebar,
                NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.RebarKeys));
            bool dlssWanted = (plan.DlssMode == "latest" || plan.DlssMode == "j" || plan.DlssMode == "k")
                && NvDrsTweaks.DlssOverrideSupported();
            NoteNvKey(NvDrsTweaks.KeyDlssOvr, dlssWanted,
                NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.DlssKeys));
        }

        private void NoteNvKey(string key, bool wanted, bool didFail)
        {
            // 失败记录及准入在 NvDrsTweaks 的每游戏动作边界处理，用户偏好保持不变。
        }

        private int envResidueGeneration = -1;
        private bool envResidueOther, envResidueNvList, envResidueGpuPref;

        private bool EnvActive()
        {
            if (FrameCoordination.HasResidue) return true;
            if (core.AutomaticLayoutPending) return true;
            if (GuardSchedulingNeedsRestore) return true;
            if (backgroundFreeze != null && backgroundFreeze.HasPending) return true;
            lock (sync) if (envRestorePending.Count > 0) return true;
            if (coreThreadPlacement != null && (coreThreadPlacement.Pending || coreThreadPlacement.UnownedPending)) return true;
            if (doActive || wuActive || maintActive || optionalServicesActive
                || pqosActive || awakeActive || dwmBoostActive
                || gpwActive || nvVrrActive || intelEndActive || laptopPerfActive
                || planActive || timerRaised
                || rsrActive || amdAlagActive || amdAfmfActive
                || (standbyCleaner != null && standbyCleaner.HasInFlight)) return true;
            int generation = Settings.MutationGeneration;
            if (generation != envResidueGeneration)
            {
                envResidueOther = DoTweak.HasResidue || UpdatePause.HasResidue || MaintenancePause.HasResidue
                    || OptionalServicePause.HasResidue || PresenceQos.HasResidue
                    || GpuPowerMax.HasResidue() || GpuClockLock.HasResidue || AdlxTweaks.HasResidue()
                    || NvVrrWindowed.HasResidue || IntelEndurance.HasResidue || LaptopPerfMode.HasResidue
                    || PowerPlan.HasResidue || PowerOverlay.HasResidue
                    || DisplaySolo.HasResidue();
                envResidueNvList = NvDrsTweaks.HasGameResidue;
                envResidueGpuPref = GpuPrefStage.HasResidue;
                envResidueGeneration = generation;
            }
            return envResidueOther
                || NvGameResidueGate(envResidueNvList)
                || GpuPrefResidueGate(envResidueGpuPref);
        }

        private bool NvGameResidueGate(bool hasResidue)
        {
            return hasResidue && !(enabled && preStagedNvPath != null);
        }

        private bool GpuPrefResidueGate(bool hasResidue)
        {
            return hasResidue && !(enabled && gpuPrefStageOn && preStagedNvPath != null);
        }

        private bool NvGameResidueNeedsRestore
        {
            get { return NvGameResidueGate(NvDrsTweaks.HasGameResidue); }
        }

        private bool GpuPrefStageResidueNeedsRestore
        {
            get { return GpuPrefResidueGate(GpuPrefStage.HasResidue); }
        }

        private string lastResidueLogged;
        private long residueLogTicks;
        private int residueRetries;
        private long residueNextTryTicks;

        private string ResidueDetail()
        {
            var parts = new List<string>();
            if (FrameCoordination.HasResidue) parts.Add(AutomaticPolicy.Text("线程协同恢复", "Thread coordination recovery"));
            if (GuardSchedulingNeedsRestore)
            {
                if (GameDvr.HasResidue) parts.Add(Lang.T("set.dvr"));
                if (Mmcss.HasResidue()) parts.Add(Lang.T("auto.mmcss"));
            }
            bool sessionActive;
            int boostCount;
            lock (sync) { sessionActive = active; boostCount = gameBoost.Count; }
            if (sessionActive) parts.Add(Lang.T("t.gamemodeenv.29"));
            if (boostCount > 0) parts.Add(Lang.T("log.gamemodeboost.3") + boostCount + Lang.T("t.gamemodeenv.30"));
            if (core.AnyWith(SuppressReason.Background)) parts.Add(Lang.T("cfg.group.bg"));
            if (doActive || DoTweak.HasResidue) parts.Add(Lang.T("t.gamemodeenv.31"));
            if (wuActive || UpdatePause.HasResidue) parts.Add(Lang.T("t.gamemodeenv.32"));
            if (maintActive || MaintenancePause.HasResidue) parts.Add(Lang.T("gm.pausemaint"));
            if (optionalServicesActive || OptionalServicePause.HasResidue) parts.Add(Lang.T("gm.pausesvc"));
            if (standbyCleaner != null && standbyCleaner.HasInFlight) parts.Add(Lang.T("gm.standbycleaner"));
            if (pqosActive || PresenceQos.HasResidue) parts.Add(Lang.T("t.gamemodeenv.33"));
            if (awakeActive) parts.Add(Lang.T("t.gamemodeenv.34"));
            if (dwmBoostActive) parts.Add(Lang.T("gm.dwmboost"));
            if (RssSteer.HasResidue) parts.Add(Lang.T("gm.rsssteer"));
            if (gpwActive || GpuPowerMax.HasResidue()) parts.Add(EnvLabel("gpupower"));
            if (GpuClockLock.HasResidue) parts.Add(Lang.T("set.gpuclock"));
            if (nvVrrActive || NvVrrWindowed.HasResidue) parts.Add(EnvLabel("nvvrr"));
            if (intelEndActive || IntelEndurance.HasResidue) parts.Add(EnvLabel("intelend"));
            if (LaptopPerfMode.HasResidue) parts.Add(Lang.T("gm.laptopperf"));
            if (DisplaySolo.HasResidue()) parts.Add(Lang.T("gm.solo"));
            if (rsrActive || amdAlagActive || amdAfmfActive || AdlxTweaks.HasResidue()) parts.Add("AMD");
            if (planActive) parts.Add(Lang.T("t.gamemodeenv.35"));
            if (NvGameResidueNeedsRestore) parts.Add("NVIDIA Profile");
            if (GpuPrefStageResidueNeedsRestore) parts.Add(Lang.T("set.gpupref"));
            if (timerRaised) parts.Add(Lang.T("t.gamemodeenv.36"));
            return parts.Count > 0 ? string.Join(" ", parts.ToArray()) : Lang.T("t.gamemodeenv.37");
        }

        private static long ResidueBackoffTicks(int tries)
        {
            long ms = 4000L << Math.Min(Math.Max(tries - 1, 0), 7);
            const long Cap = 5L * 60 * 1000;
            if (ms > Cap) ms = Cap;
            return ms * TimeSpan.TicksPerMillisecond;
        }

        private bool RetryDeactivate(string reason)
        {
            long now = DateTime.UtcNow.Ticks;
            if (residueNextTryTicks != 0 && now < residueNextTryTicks) return false;

            residueRetries++;
            string detail = ResidueDetail();
            if (detail != lastResidueLogged
                || now - residueLogTicks >= TimeSpan.TicksPerMinute * 10)
            {
                Logger.Log(Lang.T("log.gamemodeenv.38") + reason + " " + detail
                    + Lang.T("log.gamemodeenv.39") + Lang.T("log.gamemodeenv.41") + residueRetries);
                lastResidueLogged = detail;
                residueLogTicks = now;
            }
            bool clean = Deactivate(reason, true);
            if (clean)
            {
                Logger.Log(Lang.T("log.gamemodeenv.40") + Lang.T("log.gamemodeenv.41") + residueRetries);
                lastResidueLogged = null;
                residueLogTicks = 0;
                residueRetries = 0;
                residueNextTryTicks = 0;
                return true;
            }
            residueNextTryTicks = now + ResidueBackoffTicks(residueRetries);
            return false;
        }

        private bool RestoreDisplayAwake()
        {
            bool tracked;
            lock (sync)
            {
                string state;
                tracked = awakeActive || (automationEvidence.TryGetValue("awake", out state)
                    && (state == "applied" || state == "restored" || state == "failed" || state == "restore-failed"));
            }
            bool restored = DisplayAwake.Restore();
            if (restored) awakeActive = false;
            if (tracked) RecordAutomation("awake", restored ? "restored" : "restore-failed");
            return restored;
        }

        private bool RestoreEnv()
        {
            bool ok = true;
            // 先释放本局计时器请求，其他恢复失败不影响此步骤。
            if (!timerLease.Update(false, DateTime.UtcNow.Ticks)) ok = false;
            RecordAutomation(PolicyCatalog.KeyGlobalTimer, AutomaticExecutionState(PolicyCatalog.KeyGlobalTimer));
            if (!core.RestoreAutomaticLayouts()) ok = false;
            if (DoTweak.Restore()) doActive = false; else ok = false;
            if (UpdatePause.Restore()) wuActive = false; else ok = false;
            if (MaintenancePause.Restore()) maintActive = false; else ok = false;
            if (OptionalServicePause.Restore()) optionalServicesActive = false; else ok = false;
            if (PresenceQos.Restore()) pqosActive = false; else ok = false;
            if (!RestoreDisplayAwake()) ok = false;
            if (DwmBoost.Restore()) dwmBoostActive = false; else ok = false;
            if (!RssSteer.Restore()) ok = false;
            if (AdlxTweaks.RestoreRsr()) rsrActive = false; else ok = false;
            if (GpuPowerMax.Restore()) gpwActive = false; else ok = false;
            if (!GpuClockLock.Restore()) ok = false;
            if (NvVrrWindowed.Restore()) nvVrrActive = false; else ok = false;
            if (IntelEndurance.Restore()) intelEndActive = false; else ok = false;
            if (LaptopPerfMode.Restore()) laptopPerfActive = false; else ok = false;
            if (RestoreAmdAntiLagEnv()) amdAlagActive = false; else ok = false;
            if (AdlxTweaks.RestoreAfmf()) amdAfmfActive = false; else ok = false;
            if (!AdlxTweaks.RestoreEnhancedSync()) ok = false;
            if (!AdlxTweaks.RestoreRis()) ok = false;
            if (!AdlxTweaks.RestoreFrtc()) ok = false;
            lock (driverStageGate)
            {
                bool hadNv = NvDrsTweaks.HasGameResidue;
                bool nvRestored = NvDrsTweaks.RestoreAllGames();
                if (!nvRestored) ok = false;
                if (hadNv)
                {
                    RecordAutomation(PolicyCatalog.KeyNvMaxPerf, nvRestored ? "restored" : "restore-failed");
                    RecordAutomation(PolicyCatalog.KeyNvShaderCache, nvRestored ? "restored" : "restore-failed");
                }
                if (!GpuPrefStage.Restore()) ok = false;
            }
            Interlocked.Increment(ref powerSessionGen);
            lock (powerApplyGate)
            {
                if (PowerPlan.Restore())
                {
                    planActive = false;
                    lastPowerPolicyKey = -1;
                    Interlocked.Exchange(ref nextPowerAuditTicks, 0);
                }
                else ok = false;
                PowerPlan.RestoreParkState();
            }
            return EnvRestoreCompleted(ok);
        }

        private bool EnvRestoreCompleted(bool operationsSucceeded)
        {
            if (!operationsSucceeded || PowerPlan.HasResidue || PowerOverlay.HasResidue || OptionalServicePause.HasResidue) return false;
            // 恢复成功还需确认收据已清除，再重置退避。
            string[] keys = { DoTweak.BandwidthJournalKey, DoTweak.StopFlag, UpdatePause.Flag,
                PresenceQos.JournalKey, GpuPowerMax.SnapKey, AdlxTweaks.SnapKey, NvDrsTweaks.ListKey,
                GpuPrefStage.JournalKey, PowerPlan.PlanJournalKey, PowerOverlay.SnapKey,
                PowerPlan.LegacyCpuIdleLedgerKey, OptionalServicePause.LedgerKey, LaptopPerfMode.SnapKey,
                FrameCoordination.JournalKey, FrameMemoryProtection.JournalKey, FrameCoordinationTrace.TraceKey };
            foreach (string key in keys)
            {
                string record;
                if (!Settings.TryLoadStr(key, out record) || !string.IsNullOrEmpty(record)) return false;
            }
            lock (sync)
            {
                envRestorePending.RemoveWhere(delegate(string key) { return key != "gamedvr" && key != "mmcss"; });
                return envRestorePending.Count == 0;
            }
        }

        private void ReleaseBackground()
        {
            ReleaseBackground(Lang.T("t.gamemodeenv.41"));
        }

        private int ReleaseBackground(string reasonPrefix)
        {
            backgroundFreeze.Clear("background-release");
            backgroundCpuCap.Clear();
            if (!core.AnyWith(SuppressReason.Background)) return 0;
            int n = 0;
            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (core.Release(pid, SuppressReason.Background)) { ReportSeal(pid); n++; }
            if (n > 0) Logger.Log(reasonPrefix + Lang.T("log.gamemodeenv.42") + n + Lang.T("log.gamemodeenv.43"));
            return n;
        }
    }
}
