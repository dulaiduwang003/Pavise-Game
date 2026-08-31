// @author bdth 2074055628@qq.com
// 文件用途 会话环境编排 熔断重试与恢复
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
            { "do", "wlanscan", "wu", "services", "cpuidle", "standby",
              "pqos", "awake", "rsr", "gpupower", "amdalag", "amdafmf", "solo", "intelll" };

        private static string EnvLabel(string key)
        {
            switch (key)
            {
                case "do": return Lang.T("t.gamemodeenv.1");
                case "wlanscan": return Lang.T("t.gamemodeenv.2");
                case "wu": return Lang.T("t.gamemodeenv.3");
                case "services": return Lang.T("gm.pausesvc");
                case "cpuidle": return Lang.T("gm.disablecpuidle");
                case "standby": return Lang.T("gm.standbycleaner");
                case "pqos": return Lang.T("t.gamemodeenv.4");
                case "awake": return Lang.T("t.gamemodeenv.5");
                case "rsr": return Lang.T("set.rsr");
                case "gpupower": return Lang.T("t.gamemodeenv.6");
                case "amdalag": return "AMD Anti-Lag";
                case "amdafmf": return Lang.T("t.gamemodeenv.8");
                case "solo": return Lang.T("gm.solo");
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
                        Logger.Log(Lang.T("log.gamemodeenv.9") + EnvLabel(key) + Lang.T("log.gamemodeenv.10") + failures
                            + Lang.T("log.gamemodeenv.11"));
                    }
                }
            }
            return want ? ok : (ok ? false : active);
        }

        // IRQ 对局 epoch 只接纳游戏自然运行产生的中断 所有 Pavise 主动
        // 改系统 驱动或进程策略的动作 都必须先停掉旧 epoch 动作完成前
        // externalMutations 保持为正 Confirm 也不能抢先重开
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
                case "wlanscan": wlanGuardOn = false; Settings.Save("GmWlanGuard", false); break;
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
                case "rsr": rsrOn = false; Settings.Save("GmRsr", false); break;
                case "gpupower": gpuPowerMaxOn = false; Settings.Save("GmGpuPowerMax", false); break;
                case "amdalag": amdAntiLag = false; Settings.Save("AmdAntiLag", false); break;
                case "amdafmf": amdAfmf = false; Settings.Save("AmdAfmf", false); break;
                case "solo": displaySoloOn = false; Settings.Save(PolicyCatalog.KeyDisplaySolo, false); break;
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
                case "wlanscan": return PolicyCatalog.KeyWlanGuard;
                case "wu": return PolicyCatalog.KeyPauseUpdate;
                case "services": return PolicyCatalog.KeyPauseServices;
                case "cpuidle": return PolicyCatalog.KeyDisableCpuIdle;
                case "standby": return PolicyCatalog.KeyStandbyCleaner;
                case "awake": return PolicyCatalog.KeyAwake;
                case "amdalag": return PolicyCatalog.KeyAmdAntiLag;
                case "amdafmf": return PolicyCatalog.KeyAmdAfmf;
                case "solo": return PolicyCatalog.KeyDisplaySolo;
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
                        if (p.Overrides.Remove(policyKey))
                        {
                            if (SaveProfilesLocked()) cleared = true;
                        }
                        break;
                    }
            if (cleared)
                Logger.Log(Lang.T("log.gamemodeenv.12") + snap.ProfileName + Lang.T("log.gamemodeenv.13") + label
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
            bool pWlan = sp != null ? sp.WlanGuard : wlanGuardOn;
            bool pWu = sp != null ? sp.PauseUpdate : pauseUpdateOn;
            bool pAwake = sp != null ? sp.Awake : awakeOn;
            bool pPlan = sp != null ? sp.PowerPlanOn : planSwitch;
            bool pAggr = sp != null ? sp.Aggressive : aggressiveOn;
            bool pAmdAlag = sp != null ? sp.AmdAntiLag : amdAntiLag;
            bool pAmdAfmf = sp != null ? sp.AmdAfmf : amdAfmf;
            bool competitive = mode == PerformancePreset.Competitive;
            bool custom = mode == PerformancePreset.Custom;
            bool handheld = IsHandheld(mode);
            bool usePauseDl = custom ? pPauseDl : (competitive || handheld);
            bool slowReady = slowEnvAtTicks == 0 || DateTime.UtcNow.Ticks >= slowEnvAtTicks;
            usePauseDl = usePauseDl && slowReady;
            bool usePlan = ResolvePowerPlanEnabled(mode, pPlan);
            SuppressionCore.GpuDemoteEnabled = sp != null ? sp.GpuDemote : gpuDemoteOn;
            doActive = EnvStep("do", usePauseDl, doActive, DoTweak.Activate, DoTweak.Restore);
            wlanActive = EnvStep("wlanscan", pWlan, wlanActive, WlanGuard.Activate, WlanGuard.Restore);
            wuActive = EnvStep("wu", pWu && slowReady, wuActive, UpdatePause.Activate, UpdatePause.Restore);
            ApplyOptionalServices(slowReady);
            pqosActive = EnvStep("pqos", true, pqosActive, PresenceQos.Activate, PresenceQos.Restore);
            awakeActive = EnvStep("awake", pAwake, awakeActive, DisplayAwake.Activate, DisplayAwake.Restore);
            bool pSolo = sp != null ? sp.DisplaySolo : displaySoloOn;
            soloActive = EnvStep("solo", pSolo, soloActive, DisplaySolo.Activate, DisplaySolo.Restore);
            rsrActive = EnvStep("rsr", rsrOn, rsrActive, AdlxTweaks.ActivateRsr, AdlxTweaks.RestoreRsr);
            gpwActive = EnvStep("gpupower", gpuPowerMaxOn, gpwActive, GpuPowerMax.Activate, GpuPowerMax.Restore);
            amdAlagActive = EnvStep("amdalag", pAmdAlag && AdlxTweaks.AntiLagSupported(),
                amdAlagActive, AdlxTweaks.ActivateAntiLag, RestoreAmdAntiLagEnv);
            amdAfmfActive = EnvStep("amdafmf", pAmdAfmf && AdlxTweaks.AfmfSupported(), amdAfmfActive,
                AdlxTweaks.ActivateAfmf, AdlxTweaks.RestoreAfmf);
            // 自适应压制升档时借用激进列 只在智能档发生 状态机在 AdaptiveGuard
            //   局中切走预设时标志要到本轮末尾才被 StepAdaptiveGuard 清掉
            //   这里再按当前档位卡一道 旧升档不许套在新档位上哪怕一个周期
            bool aggressivePower = IsAggressive(mode, pAggr)
                || (adaptiveEscalated && mode == PerformancePreset.Standard);
            // 掌机档也要进这个键 否则从专注切到掌机时 aggressive 两边都是真 会被当成没变过而不重写
            int powerKey = (aggressivePower ? 1 : 0) | (usePlan ? 2 : 0)
                | (handheld ? 8 : 0);
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
                                    delegate { return PowerPlan.Enforce(aggrShot, handheldShot); });
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

            ApplyCpuIdlePolicy(usePlan && slowReady);

            if (!timerRaised)
            {
                bool globalRes = GlobalTimerResTweak.EnabledByPavise;
                if ((Native.OsBuild() > 0 && Native.OsBuild() < 19041) || globalRes)
                {
                    RunIrqIsolatedMutation(delegate
                    {
                        try { Native.timeBeginPeriod(1); } catch { }
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
        private bool soloActive;
        private bool rsrActive;

        // 和冻结的调优快照不一样 这个可还原的服务开关必须响应
        // 用户在对局中途关掉它 逐游戏的显式覆盖仍然
        // 优先于全局默认
        private bool EffPauseServices
        {
            get
            {
                PolicySnapshot sp = sessionPolicy;
                // IsGlobal 的意思是 做快照的那一刻还没有任何覆盖
                // 已识别的游戏现在照样可以拿到它的第一条覆盖
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
                    // 用户主动取消不该去等激活的退避
                    // 反复还原失败的仍然保留它们自己的退避
                    envNextAttempt.Remove("services");
                    envFailures.Remove("services");
                }
            }
            // 部分失败的激活会留下真实的还原欠账 哪怕 EnvStep 返回了 false
            // 别让 want==active 把它掩盖过去
            if (want) applied = isApplied();
            else if (hasResidue()) applied = true;
            bool result = EnvStep("services", want, applied, activate, restore);
            // 被取消的 Activate 也可能回滚成功并返回 true
            // 那不算策略生效 不能压掉后面的重试
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

        // Anti-Lag 开启时驱动会把 Chill 一并暂关 两份快照要一起还原
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
        private int powerSessionGen;
        private readonly object powerApplyGate = new object();

        private bool RunPowerPlanApply(int genShot, Func<bool> apply)
        {
            try
            {
                lock (powerApplyGate)
                {
                    // 排队中的任务不能在会话已经还原之后再去应用方案
                    // 哪怕它还没开始执行
                    if (stopping || Volatile.Read(ref powerSessionGen) != genShot) return false;
                    bool planOk = RunIrqIsolatedMutation(delegate
                    {
                        if (stopping || Volatile.Read(ref powerSessionGen) != genShot) return false;
                        return apply != null && apply();
                    });
                    // RestoreEnv 和禁用方案那条路都是先失效再在这道闸下还原
                    // 过期的工作线程不许发布结果
                    if (stopping || Volatile.Read(ref powerSessionGen) != genShot) return false;
                    OnPowerPlanApplied(planOk);
                    return planOk;
                }
            }
            catch { return false; }
            finally { Interlocked.Exchange(ref powerApplyInFlight, 0); }
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
                Logger.Log(Lang.T("log.gamemodeenv.18") + persistedStreak
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
            Logger.Log(" " + label + Lang.T("log.gamemodeenv.10") + EnvFuseAttempts
                + Lang.T("log.gamemodeenv.28"));
            ClearActiveSessionOverride(policyKey, label);
        }

        private int envResidueGeneration = -1;
        private bool envResidueOther, envResidueNvList, envResidueGpuPref;

        // 残留账本全部经由 Settings 落盘 只有本进程会写 空闲时逐轮读注册表
        //   纯属浪费 账本判定按 Settings 写代数缓存 任何配置写入立即失效
        //   结果与逐轮实读完全一致 内存活动标志与字段门控仍然实时求值
        private bool EnvActive()
        {
            if (doActive || wlanActive || wuActive || optionalServicesActive
                || pqosActive || awakeActive || gpwActive || planActive || timerRaised
                || rsrActive || amdAlagActive || amdAfmfActive || soloActive
                || IntelGraphicsTweaks.Active
                || (standbyCleaner != null && standbyCleaner.HasInFlight)) return true;
            int generation = Settings.MutationGeneration;
            if (generation != envResidueGeneration)
            {
                envResidueOther = DoTweak.HasResidue || UpdatePause.HasResidue
                    || OptionalServicePause.HasResidue || PresenceQos.HasResidue
                    || GpuPowerMax.HasResidue() || AdlxTweaks.HasResidue()
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

        // 待命预写入故意把驱动键留在驱动里等游戏启动 那不是残留
        //   两者共用同一份 ListKey 分不出来 于是残留清理会把预写入当垃圾还原
        //   而 Deactivate 顺手把 preStagedNvPath 清成 null 预写入的去重守卫失效
        //   下一轮扫描又写一遍 实测每 4 到 8 秒一轮 预写入从来没生效超过一轮
        //   游戏模式关掉时不豁免 那时候预写入也该跟着一起收干净
        private bool NvGameResidueGate(bool hasResidue)
        {
            return hasResidue && !(enabled && preStagedNvPath != null);
        }

        // NVIDIA 和显卡偏好预置共用同一条 pending 路径
        // 但只关掉显卡预置时 仍然要释放它自己的收据
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
            if (wlanActive) parts.Add(Lang.T("t.gamemodeenv.2"));
            if (wuActive || UpdatePause.HasResidue) parts.Add(Lang.T("t.gamemodeenv.32"));
            if (optionalServicesActive || OptionalServicePause.HasResidue) parts.Add(Lang.T("gm.pausesvc"));
            if (cpuIdleActive || PowerPlan.CpuIdleHasResidue) parts.Add(Lang.T("gm.disablecpuidle"));
            if (standbyCleaner != null && standbyCleaner.HasInFlight) parts.Add(Lang.T("gm.standbycleaner"));
            if (IntelGraphicsTweaks.Active || IntelGraphicsTweaks.HasResidue) parts.Add(Lang.T("set.intel.lowlatency"));
            if (pqosActive || PresenceQos.HasResidue) parts.Add(Lang.T("t.gamemodeenv.33"));
            if (awakeActive) parts.Add(Lang.T("t.gamemodeenv.34"));
            if (gpwActive || GpuPowerMax.HasResidue()) parts.Add(EnvLabel("gpupower"));
            if (soloActive || DisplaySolo.HasResidue()) parts.Add(EnvLabel("solo"));
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
            if (WlanGuard.Restore()) wlanActive = false; else ok = false;
            if (UpdatePause.Restore()) wuActive = false; else ok = false;
            if (OptionalServicePause.Restore()) optionalServicesActive = false; else ok = false;
            if (PresenceQos.Restore()) pqosActive = false; else ok = false;
            if (DisplayAwake.Restore()) awakeActive = false; else ok = false;
            if (DisplaySolo.Restore()) soloActive = false; else ok = false;
            if (AdlxTweaks.RestoreRsr()) rsrActive = false; else ok = false;
            if (GpuPowerMax.Restore()) gpwActive = false; else ok = false;
            if (RestoreAmdAntiLagEnv()) amdAlagActive = false; else ok = false;
            if (AdlxTweaks.RestoreAfmf()) amdAfmfActive = false; else ok = false;
            // 已下架的控制项 在启动还原失败之后照样会留下会话改动记录
            // 这些属于我们的快照也要一起重试
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
                    if (Native.timeEndPeriod(1) == 0) timerRaised = false;
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
            // 可能出现原生还原成功 但清空收据保存失败的情况
            // 重置重试退避之前 要求会话日志可读且为空
            // 用户的持久偏好不算在内
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
