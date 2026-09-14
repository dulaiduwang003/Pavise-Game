// @author bdth 2074055628@qq.com
// File purpose Session environment orchestration, circuit breaker retry and restore
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
        private const int EnvFuseAttempts = 2;
        private readonly Dictionary<string, long> envNextAttempt =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> envFailures =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> envFused =
            new HashSet<string>(StringComparer.Ordinal);

        internal static readonly string[] EnvKeys =
            { "do", "wu", "services", "cpuidle", "standby",
              "pqos", "awake", "audiolat", "dwmboost",
              "rsr", "gpupower", "amdalag", "amdafmf", "intelll", "maint",
              "nvvrr", "intelend" };

        private static string EnvLabel(string key)
        {
            switch (key)
            {
                case "do": return Lang.T("t.gamemodeenv.1");
                case "wu": return Lang.T("t.gamemodeenv.3");
                case "services": return Lang.T("gm.pausesvc");
                case "cpuidle": return Lang.T("gm.disablecpuidle");
                case "standby": return Lang.T("gm.standbycleaner");
                case "pqos": return Lang.T("t.gamemodeenv.4");
                case "awake": return Lang.T("t.gamemodeenv.5");
                case "audiolat": return Lang.T("gm.audiolat");
                case "dwmboost": return Lang.T("gm.dwmboost");
                case "rsr": return Lang.T("set.rsr");
                case "gpupower": return Lang.T("t.gamemodeenv.6");
                case "maint": return Lang.T("gm.pausemaint");
                case "nvvrr": return Lang.T("set.nvvrr");
                case "intelend": return Lang.T("set.intel.endurance");
                case "amdalag": return "AMD Anti-Lag";
                case "amdafmf": return Lang.T("t.gamemodeenv.8");
                case "intelll": return Lang.T("set.intel.lowlatency");
                default: return key;
            }
        }

        private bool EnvStep(
            string key, bool want, bool active, Func<bool> activate, Func<bool> restore)
        {
            if (want) lock (sync) { if (envFused.Contains(key)) want = false; }
            if (want == active) return active;
            long now = DateTime.UtcNow.Ticks;
            lock (sync)
            {
                long next;
                if (envNextAttempt.TryGetValue(key, out next) && now < next) return active;
            }

            bool ok = RunIrqIsolatedMutation(want ? activate : restore);

            lock (sync)
            {
                if (ok)
                {
                    envNextAttempt.Remove(key);
                    envFailures.Remove(key);
                }
                else
                {
                    int failures;
                    envFailures.TryGetValue(key, out failures);
                    if (failures < EnvFuseAttempts) failures++;
                    envFailures[key] = failures;
                    int seconds = EnvRetryBaseSeconds;
                    int backoffSteps = Math.Min(failures, EnvRetryMaxSteps);
                    for (int i = 1; i < backoffSteps && seconds < EnvRetryCapSeconds; i++)
                        seconds = Math.Min(EnvRetryCapSeconds, seconds * 2);
                    envNextAttempt[key] = DateTime.UtcNow.AddSeconds(seconds).Ticks;
                    if (want && failures >= EnvFuseAttempts && envFused.Add(key))
                    {
                        Settings.Save("EnvFuse_" + key, true);
                        RunIrqIsolatedMutation(restore);
                        DisableEnvSwitch(key);
                        Logger.Warn(Lang.T("log.gamemodeenv.9") + EnvLabel(key) + Lang.T("log.gamemodeenv.10") + failures
                            + Lang.T("log.gamemodeenv.11"));
                    }
                }
            }
            return want ? ok : (ok ? false : active);
        }

        // The IRQ match epoch only accepts interrupts from the game running naturally, every Pavise-initiated
        // change to system, driver or process policy must first stop the old epoch, and until the action completes
        // externalMutations stays positive so Confirm can't reopen early either
        private bool RunIrqIsolatedMutation(Func<bool> action)
        {
            irqProbe.BeginExternalMutation();
            try { return action != null && action(); }
            catch { return false; }
            finally { irqProbe.EndExternalMutation(); }
        }

        private void RunIrqIsolatedMutation(Action action)
        {
            irqProbe.BeginExternalMutation();
            try { if (action != null) action(); }
            catch { }
            finally { irqProbe.EndExternalMutation(); }
        }

#if PAVISE_SELFTEST
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

        private void DisableEnvSwitch(string key)
        {
            switch (key)
            {
                case "do": pauseDlOn = false; Settings.Save("GmPauseDl", false); break;
                case "wu": pauseUpdateOn = false; Settings.Save("GmPauseUpdate", false); break;
                case "services": pauseServicesOn = false; Settings.Save(PolicyCatalog.KeyPauseServices, false); break;
                case "cpuidle":
                    disableCpuIdleOn = false;
                    Interlocked.Increment(ref cpuIdleGeneration);
                    Settings.Save(PolicyCatalog.KeyDisableCpuIdle, false);
                    break;
                case "standby":
                    standbyCleanerOn = false;
                    InvalidateStandbyCleanerWork();
                    Settings.Save(PolicyCatalog.KeyStandbyCleaner, false);
                    break;
                case "pqos": break;
                case "awake": awakeOn = false; Settings.Save("GmAwake", false); break;
                // These two got their own switches in 2.2.2, a trip must land on both sides
                //   Without turning off the switch it keeps retrying against that still-on value and the UI still shows it on
                case "audiolat":
                    audioLatOn = false;
                    Settings.Save(PolicyCatalog.KeyAudioLowLat, false);
                    break;
                case "dwmboost":
                    dwmBoostOn = false;
                    Settings.Save("GmDwmBoost", false);
                    break;
                case "rsr": rsrOn = false; Settings.Save("GmRsr", false); break;
                case "gpupower":
                    gpuPowerMaxOn = false;
                    Settings.Save("GmGpuPowerMax", false);
                    break;
                case "maint": pauseMaintOn = false; Settings.Save(PolicyCatalog.KeyPauseMaintenance, false); break;
                case "nvvrr": nvVrrWindowedOn = false; Settings.Save("NvVrrWindowed", false); break;
                case "intelend": intelEnduranceOn = false; Settings.Save(PolicyCatalog.KeyIntelEndurance, false); break;
                case "amdalag": amdAntiLag = false; Settings.Save("AmdAntiLag", false); break;
                case "amdafmf": amdAfmf = false; Settings.Save("AmdAfmf", false); break;
                case "intelll":
                    intelLowLatencyOn = false;
                    InvalidateIntelGraphicsWork();
                    Settings.Save(PolicyCatalog.KeyIntelLowLatency, false);
                    break;
                case "overlay": break;
            }
            string policyKey = EnvPolicyKey(key);
            if (policyKey != null) ClearActiveSessionOverride(policyKey, EnvLabel(key));
        }

        private static string EnvPolicyKey(string key)
        {
            switch (key)
            {
                case "do": return PolicyCatalog.KeyPauseDl;
                case "wu": return PolicyCatalog.KeyPauseUpdate;
                case "services": return PolicyCatalog.KeyPauseServices;
                case "cpuidle": return PolicyCatalog.KeyDisableCpuIdle;
                case "standby": return PolicyCatalog.KeyStandbyCleaner;
                case "awake": return PolicyCatalog.KeyAwake;
                case "audiolat": return PolicyCatalog.KeyAudioLowLat;
                case "maint": return PolicyCatalog.KeyPauseMaintenance;
                case "intelend": return PolicyCatalog.KeyIntelEndurance;
                case "amdalag": return PolicyCatalog.KeyAmdAntiLag;
                case "amdafmf": return PolicyCatalog.KeyAmdAfmf;
                case "intelll": return PolicyCatalog.KeyIntelLowLatency;
                default: return null;
            }
        }

        private void ClearActiveSessionOverride(string policyKey, string label)
        {
            PolicySnapshot snap = sessionPolicy;
            if (snap == null || snap.ProfileId == null
                || (policyKey != PolicyCatalog.KeyPauseServices && policyKey != PolicyCatalog.KeyDisableCpuIdle
                    && policyKey != PolicyCatalog.KeyStandbyCleaner
                    && policyKey != PolicyCatalog.KeyIntelLowLatency
                    && !snap.HasOverride(policyKey))) return;
            bool cleared = false;
            lock (sync)
                foreach (GameProfile p in profiles)
                    if (string.Equals(p.Id, snap.ProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        string before;
                        if (p.Overrides.TryGetValue(policyKey, out before))
                        {
                            p.Overrides.Remove(policyKey);
                            if (SaveProfilesLocked()) cleared = true;
                            else p.Overrides[policyKey] = before;
                        }
                        break;
                    }
            if (cleared)
                Logger.Warn(Lang.T("log.gamemodeenv.12") + snap.ProfileName + Lang.T("log.gamemodeenv.13") + label
                    + Lang.T("log.gamemodeenv.14"));
        }

        private void ClearEnvFuse(string key)
        {
            bool wasFused;
            lock (sync)
            {
                wasFused = envFused.Remove(key);
                envFailures.Remove(key);
                envNextAttempt.Remove(key);
            }
            if (Settings.Load("EnvFuse_" + key, false)) Settings.Save("EnvFuse_" + key, false);
            if (wasFused) Logger.Log(Lang.T("log.gamemodeenv.9") + EnvLabel(key) + Lang.T("log.gamemodeenv.15"));
        }

        private void ApplyEnv()
        {
            PolicySnapshot sp = sessionPolicy;
            PerformancePreset mode = sp != null ? sp.Preset : ActivePreset;
            bool pPauseDl = sp != null ? sp.PauseDownloads : pauseDlOn;
            bool pWu = sp != null ? sp.PauseUpdate : pauseUpdateOn;
            bool pMaint = sp != null ? sp.PauseMaintenance : pauseMaintOn;
            bool pAwake = sp != null ? sp.Awake : awakeOn;
            bool pPlan = sp != null ? sp.PowerPlanOn : planSwitch;
            bool pAggr = sp != null ? sp.Aggressive : aggressiveOn;
            bool pAmdAlag = sp != null ? sp.AmdAntiLag : amdAntiLag;
            bool pAmdAfmf = sp != null ? sp.AmdAfmf : amdAfmf;
            bool competitive = mode == PerformancePreset.Competitive;
            bool custom = mode == PerformancePreset.Custom;
            bool handheld = IsHandheld(mode);
            bool pIdlePolicy = sp != null ? sp.IdlePolicy : idlePolicyOn;
            bool usePauseDl = custom ? pPauseDl : (competitive || handheld);
            bool slowReady = slowEnvAtTicks == 0 || DateTime.UtcNow.Ticks >= slowEnvAtTicks;
            usePauseDl = usePauseDl && slowReady;
            bool usePlan = ResolvePowerPlanEnabled(mode, pPlan);
            SuppressionCore.GpuDemoteEnabled = sp != null ? sp.GpuDemote : gpuDemoteOn;
            // These items have their own switches since 2.2.2, no longer tier-driven, read the session snapshot when there is one, else global
            WsTrim.Enabled = sp != null ? sp.WsTrim : wsTrimOn;
            doActive = EnvStep("do", usePauseDl, doActive, DoTweak.Activate, DoTweak.Restore);
            wuActive = EnvStep("wu", pWu && slowReady, wuActive, UpdatePause.Activate, UpdatePause.Restore);
            // Automatic maintenance starts on the idle verdict, AFK and cutscenes count as idle, turn it off during the match and write back at match end
            maintActive = EnvStep("maint", pMaint && slowReady, maintActive,
                MaintenancePause.Activate, MaintenancePause.Restore);
            ApplyOptionalServices(slowReady);
            pqosActive = EnvStep("pqos", true, pqosActive, PresenceQos.Activate, PresenceQos.Restore);
            awakeActive = EnvStep("awake", pAwake, awakeActive, DisplayAwake.Activate, DisplayAwake.Restore);
            bool pAudioLat = sp != null ? sp.AudioLowLat : audioLatOn;
            // After the default device switches the old stream can't pin the new engine, treat as not applied and redo, the off direction skips drift checks and restores directly
            bool audioApplied = audioLatActive;
            if (pAudioLat && audioLatActive && AudioLowLatency.DeviceDrifted) audioApplied = false;
            audioLatActive = EnvStep("audiolat", pAudioLat, audioApplied,
                AudioLowLatency.Activate, AudioLowLatency.Restore);
            // Global switch with no per-game override, same family as gpupower autoecogpu gpuprefstage
            dwmBoostActive = EnvStep("dwmboost", dwmBoostOn, dwmBoostActive,
                DwmBoost.Activate, DwmBoost.Restore);
            rsrActive = EnvStep("rsr", rsrOn, rsrActive, AdlxTweaks.ActivateRsr, AdlxTweaks.RestoreRsr);
            // After the user explicitly enables it, a failure trip is deserved feedback, no extra eligibility gate added here
            gpwActive = EnvStep("gpupower", gpuPowerMaxOn,
                gpwActive, GpuPowerMax.Activate, GpuPowerMax.Restore);
            // NVIDIA windowed G-SYNC is only patched in when the user already has G-SYNC on and fullscreen-only, never forced by tier
            nvVrrActive = EnvStep("nvvrr", nvVrrWindowedOn && NvApi.Available,
                nvVrrActive, NvVrrWindowed.Activate, NvVrrWindowed.Restore);
            // Endurance Gaming only has something to turn off on Intel GPU machines with a battery, on AC power it does nothing anyway
            intelEndActive = EnvStep("intelend",
                EffIntelEnduranceOff && Native.HasSystemBattery() && IntelGraphicsTweaks.HasAvailable,
                intelEndActive, IntelEndurance.Activate, IntelEndurance.Restore);
            amdAlagActive = EnvStep("amdalag", pAmdAlag && AdlxTweaks.AntiLagSupported(),
                amdAlagActive, AdlxTweaks.ActivateAntiLag, RestoreAmdAntiLagEnv);
            amdAfmfActive = EnvStep("amdafmf", pAmdAfmf && AdlxTweaks.AfmfSupported(), amdAfmfActive,
                AdlxTweaks.ActivateAfmf, AdlxTweaks.RestoreAfmf);
            // Adaptive suppression escalation borrows the aggressive column, only happens on Smart tier, state machine lives in AdaptiveGuard
            //   When the preset is switched away mid-match the flag isn't cleared by StepAdaptiveGuard until the end of this pass
            //   Gate it here by the current tier again, an old escalation must not sit on the new tier even for one cycle
            bool aggressivePower = IsAggressive(mode, pAggr)
                || (adaptiveEscalated && mode == PerformancePreset.Standard);
            // Handheld tier must enter this key too, otherwise switching from Esports to Handheld has aggressive true on both sides and gets treated as unchanged without a rewrite
            int powerKey = (aggressivePower ? 1 : 0) | (usePlan ? 2 : 0)
                | (handheld ? 8 : 0) | (pIdlePolicy ? 16 : 0);
            long nowTicks = DateTime.UtcNow.Ticks;
            if (usePlan && !stopping)
            {
                if (!planActive || powerKey != lastPowerPolicyKey
                    || nowTicks >= Interlocked.Read(ref nextPowerAuditTicks))
                {
                    if (Interlocked.CompareExchange(ref powerApplyInFlight, 1, 0) == 0)
                    {
                        int keyShot = powerKey;
                        bool aggrShot = aggressivePower;
                        bool handheldShot = handheld;
                        bool idleShot = pIdlePolicy;
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
            else if (planActive)
            {
                Interlocked.Increment(ref powerSessionGen);
                lock (powerApplyGate)
                {
                    if (RunIrqIsolatedMutation(
                            delegate { return PowerPlan.Restore(); }))
                    {
                        planActive = false;
                        lastPowerPolicyKey = -1;
                        Interlocked.Exchange(ref nextPowerAuditTicks, 0);
                    }
                }
            }

            ApplyCpuIdlePolicy(slowReady);

            if (!timerRaised)
            {
                bool globalRes = GlobalTimerResTweak.EnabledByPavise;
                if ((Native.OsBuild() > 0 && Native.OsBuild() < 19041) || globalRes)
                {
                    RunIrqIsolatedMutation(delegate
                    {
                        // The request only takes effect machine-wide when the global resolution item is on, then use 0.5ms instead of 1ms
                        //   Measured under load, wait jitter goes from 1 to 2ms tri-state to converging around 1.5ms
                        //   On old systems that path's 1ms is already a global side effect, don't pile on
                        bool half = false;
                        if (globalRes)
                        {
                            uint actual;
                            try { half = Native.NtSetTimerResolution(HalfMsUnits, true, out actual) == 0; }
                            catch { half = false; }
                        }
                        if (!half) try { Native.timeBeginPeriod(1); } catch { }
                        timerHalfMs = half;
                        if (globalRes && Native.TimerExemptWanted)
                            try { Native.ApplyHighQoS(new IntPtr(-1), true); } catch { }
                    });
                    timerRaised = true;
                }
                else if (!timerSkipLogged)
                {
                    Logger.Log(Lang.T("log.gamemodeenv.20"));
                    timerSkipLogged = true;
                }
            }
        }

        private bool wuActive;
        private bool optionalServicesActive;
        private bool optionalServicesWanted;
        private int optionalServiceGeneration;
        private bool amdAlagActive;
        private bool amdAfmfActive;
        private bool rsrActive;

        // Unlike the frozen tuning snapshot, this restorable service switch must respond
        // to the user turning it off mid-match, an explicit per-game override still
        // takes precedence over the global default
        private bool EffPauseServices
        {
            get
            {
                PolicySnapshot sp = sessionPolicy;
                // IsGlobal means there was no override at the moment the snapshot was taken
                // A recognized game can still pick up its first override now
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
            if (OptionalServicePause.Active) OptionalServicePause.ObserveStops();
            Func<bool> mayContinue = CaptureOptionalServicesAdmission();
            optionalServicesActive = StepOptionalServices(slowReady && mayContinue(), optionalServicesActive,
                delegate { return OptionalServicePause.HasResidue; },
                delegate { return OptionalServicePause.Active; },
                delegate { return OptionalServicePause.Activate(mayContinue); }, OptionalServicePause.Restore);
        }

        private Func<bool> CaptureOptionalServicesAdmission()
        {
            int generation = Volatile.Read(ref optionalServiceGeneration);
            return delegate
            {
                return generation == Volatile.Read(ref optionalServiceGeneration)
                    && !stopping && !panicReq && enabled && !ProfileStoreSaveFailed && EffPauseServices;
            };
        }

        private bool StepOptionalServices(bool want, bool applied,
            Func<bool> hasResidue, Func<bool> isApplied, Func<bool> activate, Func<bool> restore)
        {
            lock (sync)
            {
                if (envFused.Contains("services")) want = false;
                if (optionalServicesWanted != want)
                {
                    optionalServicesWanted = want;
                    // A user-initiated cancel shouldn't wait out the activation backoff
                    // Repeatedly failing restores still keep their own backoff
                    envNextAttempt.Remove("services");
                    envFailures.Remove("services");
                }
            }
            // A partially failed activation leaves real restore debt even when EnvStep returned false
            // Don't let want==active mask it
            if (want) applied = isApplied();
            else if (hasResidue()) applied = true;
            bool result = EnvStep("services", want, applied, activate, restore);
            // A cancelled Activate may also roll back successfully and return true
            // That doesn't count as the policy taking effect and must not suppress later retries
            return want ? result && isApplied() : result;
        }

#if PAVISE_SELFTEST
        internal bool ProbeEffPauseServices { get { return EffPauseServices; } }
        internal Func<bool> CaptureOptionalServicesAdmissionForTest() { return CaptureOptionalServicesAdmission(); }
        internal bool StepOptionalServicesForTest(bool want, bool applied, OptionalServicePauseEngine engine)
        {
            return StepOptionalServices(want, applied, delegate { return engine.HasResidue; },
                delegate { return engine.Active; },
                delegate { return engine.Activate(); }, engine.Restore);
        }
#endif

        // When Anti-Lag is enabled the driver also pauses Chill, both snapshots must be restored together
        private static bool RestoreAmdAntiLagEnv()
        {
            bool ok = AdlxTweaks.RestoreAntiLag();
            ok &= AdlxTweaks.RestoreChill();
            return ok;
        }

        private volatile bool planActive;
        private volatile int lastPowerPolicyKey = -1;
        // Final ownership of the power scheme during a match belongs to Pavise
        // If ThrottleStop, G-Helper or anything else switches the scheme away, the system's scheme change notification pulls it right back
        private long nextPowerAuditTicks;
        private int powerApplyInFlight;
        private int powerPlanNotificationPending;
        private int powerSessionGen;
        private readonly object powerApplyGate = new object();

        // When the UI window receives GUID_ACTIVE_POWERSCHEME it queues only one lightweight power scheme task
        // without touching the process snapshot, game detection or the full environment policy refresh
        internal void NotifyPowerSchemeChanged()
        {
            bool shouldAudit;
            lock (sync)
                shouldAudit = enabled && active && planActive && !stopping;
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
                if (!enabled || !active || !planActive || stopping
                    || keyShot < 0 || (keyShot & 2) == 0)
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
            lock (sync)
                return enabled && active && planActive && !stopping
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
                    bool ok = RunIrqIsolatedMutation(delegate
                    {
                        if (!PowerPlanNotificationStillCurrent(genShot, keyShot)) return false;
                        return PowerPlan.Enforce((keyShot & 1) != 0,
                            (keyShot & 8) != 0, (keyShot & 16) != 0);
                    });
                    if (!PowerPlanNotificationStillCurrent(genShot, keyShot)) return;
                    OnPowerPlanApplied(ok);
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
                    // A queued task must not apply the scheme after the session has already been restored
                    // even if it hasn't started running yet
                    if (stopping || Volatile.Read(ref powerSessionGen) != genShot) return false;
                    bool planOk = RunIrqIsolatedMutation(delegate
                    {
                        if (stopping || Volatile.Read(ref powerSessionGen) != genShot) return false;
                        return apply != null && apply();
                    });
                    // RestoreEnv and the disable-scheme path both invalidate first, then restore under this gate
                    // Stale worker threads may not publish results
                    if (stopping || Volatile.Read(ref powerSessionGen) != genShot) return false;
                    OnPowerPlanApplied(planOk);
                    return planOk;
                }
            }
            catch { return false; }
            finally
            {
                Interlocked.Exchange(ref powerApplyInFlight, 0);
                // The first apply itself also fires a scheme change notification
                // If the notification arrives before the apply finishes, run one lightweight check afterwards, the event must not be lost
                if (Interlocked.CompareExchange(
                    ref powerPlanNotificationPending, 0, 0) != 0)
                    QueuePowerPlanNotificationAudit();
            }
        }

        private const string PowerFailStreakKey = "PowerPlanFailStreak";
        private const int PowerPlanAutoOffThreshold = EnvFuseAttempts;
        private int planFailStreak;

        private void OnPowerPlanApplied(bool planOk)
        {
            if (planOk)
            {
                planFailStreak = 0;
                if (LoadCounter(PowerFailStreakKey) != 0) SaveCounter(PowerFailStreakKey, 0);
                // No periodic audit after success, re-check only on system notification or when the policy itself changes
                Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
                return;
            }
            planFailStreak++;
            int persistedStreak = LoadCounter(PowerFailStreakKey) + 1;
            SaveCounter(PowerFailStreakKey, persistedStreak);
            if (persistedStreak >= PowerPlanAutoOffThreshold)
            {
                planSwitch = false;
                Settings.Save("PowerPlanOn", false);
                SaveCounter(PowerFailStreakKey, 0);
                ClearActiveSessionOverride(PolicyCatalog.KeyPowerPlan, Lang.T("t.gamemodeenv.17"));
                Logger.Warn(Lang.T("log.gamemodeenv.18") + persistedStreak
                    + Lang.T("log.gamemodeenv.19"));
                return;
            }
            int delay = 30;
            for (int i = 1; i < planFailStreak && delay < 300; i++) delay *= 2;
            if (delay > 300) delay = 300;
            Interlocked.Exchange(ref nextPowerAuditTicks,
                DateTime.UtcNow.AddSeconds(delay).Ticks);
            planActive = false;
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

        private void HandleNvTweakOutcome(List<string> failed, NvGamePlan plan)
        {
            if (failed == null || plan == null) return;
            NoteNvKey(NvDrsTweaks.KeyPState, plan.MaxPerf, failed.Contains(NvDrsTweaks.KeyPState));
            bool lowLatWanted = plan.LowLatMode == "on" || plan.LowLatMode == "ultra";
            NoteNvKey(NvDrsTweaks.KeyPreRender, lowLatWanted,
                plan.LowLatMode == "ultra"
                    ? NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.UltraKeys)
                    : failed.Contains(NvDrsTweaks.KeyPreRender));
            NoteNvKey(NvDrsTweaks.KeySmooth, plan.SmoothMotion && NvDrsTweaks.SmoothMotionSupported(),
                failed.Contains(NvDrsTweaks.KeySmooth));
            NoteNvKey(NvDrsTweaks.KeyShaderCache, plan.ShaderCacheMax,
                failed.Contains(NvDrsTweaks.KeyShaderCache));
            NoteNvKey(NvDrsTweaks.KeyRebarFeat, plan.Rebar,
                NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.RebarKeys));
            bool dlssWanted = (plan.DlssMode == "latest" || plan.DlssMode == "j" || plan.DlssMode == "k")
                && NvDrsTweaks.DlssOverrideSupported();
            NoteNvKey(NvDrsTweaks.KeyDlssOvr, dlssWanted,
                NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.DlssKeys));
        }

        private void NoteNvKey(string key, bool wanted, bool didFail)
        {
            if (!wanted) return;
            string counterKey = "NvFailStreak_" + key;
            if (!didFail)
            {
                if (LoadCounter(counterKey) != 0) SaveCounter(counterKey, 0);
                return;
            }
            int streak = LoadCounter(counterKey) + 1;
            if (streak < EnvFuseAttempts) { SaveCounter(counterKey, streak); return; }
            SaveCounter(counterKey, 0);
            string label;
            string policyKey;
            if (key == NvDrsTweaks.KeyPState) { nvMaxPerf = false; Settings.Save("NvMaxPerf", false); label = Lang.T("t.gamemodeenv.21"); policyKey = PolicyCatalog.KeyNvMaxPerf; }
            else if (key == NvDrsTweaks.KeyRebarFeat) { nvRebarOn = false; Settings.Save("NvRebar", false); label = Lang.T("t.gamemodeenv.24"); policyKey = PolicyCatalog.KeyNvRebar; }
            else if (key == NvDrsTweaks.KeyDlssOvr) { nvDlssMode = "off"; Settings.SaveStr("NvDlss", "off"); label = Lang.T("t.gamemodeenv.25"); policyKey = PolicyCatalog.KeyNvDlss; }
            else if (key == NvDrsTweaks.KeySmooth) { nvSmoothMotion = false; Settings.Save("NvSmoothMotion", false); label = Lang.T("t.gamemodeenv.27"); policyKey = PolicyCatalog.KeyNvSmoothMotion; }
            else if (key == NvDrsTweaks.KeyShaderCache) { nvShaderCacheMax = false; Settings.Save("NvShaderCache", false); label = Lang.T("set.nvshader"); policyKey = PolicyCatalog.KeyNvShaderCache; }
            else { nvLowLatMode = "off"; Settings.SaveStr("NvLowLat", "off"); label = Lang.T("set.nvll"); policyKey = PolicyCatalog.KeyNvLowLat; }
            Logger.Warn(" " + label + Lang.T("log.gamemodeenv.10") + EnvFuseAttempts
                + Lang.T("log.gamemodeenv.28"));
            ClearActiveSessionOverride(policyKey, label);
        }

        private int envResidueGeneration = -1;
        private bool envResidueOther, envResidueNvList, envResidueGpuPref;

        // The leftover ledger is all persisted via Settings and only this process writes it, reading the registry every pass while idle
        //   is pure waste, the ledger verdict is cached by Settings write generation and any config write invalidates it immediately
        //   The result matches a real read every pass exactly, in-memory active flags and field gating are still evaluated live
        private bool EnvActive()
        {
            if (doActive || wuActive || maintActive || optionalServicesActive
                || pqosActive || awakeActive || audioLatActive || dwmBoostActive
                || gpwActive || nvVrrActive || intelEndActive
                || planActive || timerRaised
                || rsrActive || amdAlagActive || amdAfmfActive
                || IntelGraphicsTweaks.Active
                || (standbyCleaner != null && standbyCleaner.HasInFlight)) return true;
            int generation = Settings.MutationGeneration;
            if (generation != envResidueGeneration)
            {
                envResidueOther = DoTweak.HasResidue || UpdatePause.HasResidue || MaintenancePause.HasResidue
                    || OptionalServicePause.HasResidue || PresenceQos.HasResidue
                    || GpuPowerMax.HasResidue() || GpuClockLock.HasResidue || AdlxTweaks.HasResidue()
                    || NvVrrWindowed.HasResidue || IntelEndurance.HasResidue || LaptopPerfMode.HasResidue
                    || PowerPlan.HasResidue || IntelGraphicsTweaks.HasResidue
                    || DisplaySolo.HasResidue();
                envResidueNvList = NvDrsTweaks.HasGameResidue;
                envResidueGpuPref = GpuPrefStage.HasResidue;
                envResidueGeneration = generation;
            }
            return envResidueOther
                || NvGameResidueGate(envResidueNvList)
                || GpuPrefResidueGate(envResidueGpuPref);
        }

        // Standby pre-staging deliberately leaves the driver key in the driver waiting for game launch, that isn't residue
        //   Both share the same ListKey and can't be told apart, so residue cleanup restores the pre-staged write as garbage
        //   And Deactivate clears preStagedNvPath to null along the way, killing the pre-staging dedup guard
        //   The next scan writes it again, measured at one pass every 4 to 8 seconds, pre-staging never lasted more than one pass
        //   No exemption when game mode is off, pre-staging should be cleaned up along with everything else then
        private bool NvGameResidueGate(bool hasResidue)
        {
            return hasResidue && !(enabled && preStagedNvPath != null);
        }

        // NVIDIA and the GPU preference pre-stage share the same pending path
        // but turning off only the GPU pre-stage must still release its own receipt
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
            if (cpuIdleActive || PowerPlan.CpuIdleHasResidue) parts.Add(Lang.T("gm.disablecpuidle"));
            if (standbyCleaner != null && standbyCleaner.HasInFlight) parts.Add(Lang.T("gm.standbycleaner"));
            if (IntelGraphicsTweaks.Active || IntelGraphicsTweaks.HasResidue) parts.Add(Lang.T("set.intel.lowlatency"));
            if (pqosActive || PresenceQos.HasResidue) parts.Add(Lang.T("t.gamemodeenv.33"));
            if (awakeActive) parts.Add(Lang.T("t.gamemodeenv.34"));
            if (audioLatActive) parts.Add(Lang.T("gm.audiolat"));
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

        private bool RestoreEnv()
        {
            Interlocked.Increment(ref cpuIdleGeneration);
            bool ok = true;
            if (!RestoreIntelGraphics()) ok = false;
            if (DoTweak.Restore()) doActive = false; else ok = false;
            if (UpdatePause.Restore()) wuActive = false; else ok = false;
            if (MaintenancePause.Restore()) maintActive = false; else ok = false;
            if (OptionalServicePause.Restore()) optionalServicesActive = false; else ok = false;
            if (PresenceQos.Restore()) pqosActive = false; else ok = false;
            if (DisplayAwake.Restore()) awakeActive = false; else ok = false;
            if (AudioLowLatency.Restore()) audioLatActive = false; else ok = false;
            if (DwmBoost.Restore()) dwmBoostActive = false; else ok = false;
            if (!RssSteer.Restore()) ok = false;
            if (AdlxTweaks.RestoreRsr()) rsrActive = false; else ok = false;
            if (GpuPowerMax.Restore()) gpwActive = false; else ok = false;
            if (!GpuClockLock.Restore()) ok = false;
            if (NvVrrWindowed.Restore()) nvVrrActive = false; else ok = false;
            if (IntelEndurance.Restore()) intelEndActive = false; else ok = false;
            if (!LaptopPerfMode.Restore()) ok = false;
            if (RestoreAmdAntiLagEnv()) amdAlagActive = false; else ok = false;
            if (AdlxTweaks.RestoreAfmf()) amdAfmfActive = false; else ok = false;
            // Retired controls still leave session change records after a startup restore failure
            // Those snapshots are ours and must be retried too
            if (!AdlxTweaks.RestoreEnhancedSync()) ok = false;
            if (!AdlxTweaks.RestoreRis()) ok = false;
            if (!AdlxTweaks.RestoreFrtc()) ok = false;
            lock (driverStageGate)
            {
                if (!NvDrsTweaks.RestoreAllGames()) ok = false;
                if (!GpuPrefStage.Restore()) ok = false;
            }
            Interlocked.Increment(ref powerSessionGen);
            lock (powerApplyGate)
            {
                if (PowerPlan.Restore())
                {
                    planActive = false;
                    cpuIdleActive = false;
                    lastPowerPolicyKey = -1;
                    Interlocked.Exchange(ref nextPowerAuditTicks, 0);
                }
                else ok = false;
                PowerPlan.RestoreParkState();
            }
            if (timerRaised)
            {
                try
                {
                    if (timerHalfMs)
                    {
                        uint actual;
                        if (Native.NtSetTimerResolution(HalfMsUnits, false, out actual) == 0)
                        { timerRaised = false; timerHalfMs = false; }
                        else ok = false;
                    }
                    else if (Native.timeEndPeriod(1) == 0) timerRaised = false;
                    else ok = false;
                }
                catch { ok = false; }
                try { Native.RestorePowerThrottling(new IntPtr(-1), -1, -1); } catch { }
            }
            return EnvRestoreCompleted(ok);
        }

        private bool EnvRestoreCompleted(bool operationsSucceeded)
        {
            if (!operationsSucceeded || PowerPlan.HasResidue || OptionalServicePause.HasResidue
                || IntelGraphicsTweaks.HasResidue) return false;
            // Native restore may succeed while saving the cleared receipt fails
            // Before resetting the retry backoff require the session journal to be readable and empty
            // The user's persistent preferences don't count
            string[] keys = { DoTweak.BandwidthJournalKey, DoTweak.StopFlag, UpdatePause.Flag,
                PresenceQos.JournalKey, GpuPowerMax.SnapKey, AdlxTweaks.SnapKey, NvDrsTweaks.ListKey,
                GpuPrefStage.JournalKey, PowerPlan.PlanJournalKey,
                PowerPlan.CpuIdleLedgerKey, OptionalServicePause.LedgerKey, IntelGraphicsSettingsLedger.Key };
            foreach (string key in keys)
            {
                string record;
                if (!Settings.TryLoadStr(key, out record) || !string.IsNullOrEmpty(record)) return false;
            }
            return true;
        }

        private void ReleaseBackground()
        {
            ReleaseBackground(Lang.T("t.gamemodeenv.41"));
        }

        private int ReleaseBackground(string reasonPrefix)
        {
            if (!core.AnyWith(SuppressReason.Background)) return 0;
            int n = 0;
            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (core.Release(pid, SuppressReason.Background)) { ReportSeal(pid); n++; }
            if (n > 0) Logger.Log(reasonPrefix + Lang.T("log.gamemodeenv.42") + n + Lang.T("log.gamemodeenv.43"));
            return n;
        }
    }
}
