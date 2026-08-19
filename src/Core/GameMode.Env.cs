// @author bdth 2074055628@qq.com
// 文件用途 会话环境编排 熔断重试与恢复
using System;
using System.Collections.Generic;
using System.Globalization;

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
            { "do", "wlanscan", "wu",
              "pqos", "awake", "rsr", "gpupower", "amdalag", "amdafmf" };

        private static string EnvLabel(string key)
        {
            switch (key)
            {
                case "do": return Lang.T("t.gamemodeenv.1");
                case "wlanscan": return Lang.T("t.gamemodeenv.2");
                case "wu": return Lang.T("t.gamemodeenv.3");
                case "pqos": return Lang.T("t.gamemodeenv.4");
                case "awake": return Lang.T("t.gamemodeenv.5");
                case "rsr": return Lang.T("set.rsr");
                case "gpupower": return Lang.T("t.gamemodeenv.6");
                case "amdalag": return "AMD Anti-Lag";
                case "amdafmf": return Lang.T("t.gamemodeenv.8");
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

            bool ok;
            try { ok = want ? activate() : restore(); }
            catch { ok = false; }

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
                        DisableEnvSwitch(key);
                        Logger.Log(Lang.T("log.gamemodeenv.9") + EnvLabel(key) + Lang.T("log.gamemodeenv.10") + failures
                            + Lang.T("log.gamemodeenv.11"));
                    }
                }
            }
            return want ? ok : (ok ? false : active);
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
                case "pqos": break;
                case "awake": awakeOn = false; Settings.Save("GmAwake", false); break;
                case "rsr": rsrOn = false; Settings.Save("GmRsr", false); break;
                case "gpupower": gpuPowerMaxOn = false; Settings.Save("GmGpuPowerMax", false); break;
                case "amdalag": amdAntiLag = false; Settings.Save("AmdAntiLag", false); break;
                case "amdafmf": amdAfmf = false; Settings.Save("AmdAfmf", false); break;
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
                case "awake": return PolicyCatalog.KeyAwake;
                case "amdalag": return PolicyCatalog.KeyAmdAntiLag;
                case "amdafmf": return PolicyCatalog.KeyAmdAfmf;
                default: return null;
            }
        }

        private void ClearActiveSessionOverride(string policyKey, string label)
        {
            PolicySnapshot snap = sessionPolicy;
            if (snap == null || snap.ProfileId == null || !snap.HasOverride(policyKey)) return;
            bool cleared = false;
            lock (sync)
                foreach (GameProfile p in profiles)
                    if (string.Equals(p.Id, snap.ProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (p.Overrides.Remove(policyKey))
                        {
                            profileStore.Save(profiles);
                            cleared = true;
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
            bool usePauseDl = custom ? pPauseDl : competitive;
            bool slowReady = slowEnvAtTicks == 0 || DateTime.UtcNow.Ticks >= slowEnvAtTicks;
            usePauseDl = usePauseDl && slowReady;
            bool usePlan = ResolvePowerPlanEnabled(mode, pPlan);
            SuppressionCore.GpuDemoteEnabled = sp != null ? sp.GpuDemote : gpuDemoteOn;
            SuppressionCore.SqueezeBackground = CpuTopology.SqueezeSupported
                && (sp != null ? sp.SqueezeBackground : squeezeBgOn);
            doActive = EnvStep("do", usePauseDl, doActive, DoTweak.Activate, DoTweak.Restore);
            wlanActive = EnvStep("wlanscan", pWlan, wlanActive, WlanGuard.Activate, WlanGuard.Restore);
            wuActive = EnvStep("wu", pWu && slowReady, wuActive, UpdatePause.Activate, UpdatePause.Restore);
            pqosActive = EnvStep("pqos", true, pqosActive, PresenceQos.Activate, PresenceQos.Restore);
            awakeActive = EnvStep("awake", pAwake, awakeActive, DisplayAwake.Activate, DisplayAwake.Restore);
            rsrActive = EnvStep("rsr", rsrOn, rsrActive, AdlxTweaks.ActivateRsr, AdlxTweaks.RestoreRsr);
            gpwActive = EnvStep("gpupower", gpuPowerMaxOn, gpwActive, GpuPowerMax.Activate, GpuPowerMax.Restore);
            amdAlagActive = EnvStep("amdalag", pAmdAlag && AdlxTweaks.AntiLagSupported(),
                amdAlagActive, AdlxTweaks.ActivateAntiLag, RestoreAmdAntiLagEnv);
            amdAfmfActive = EnvStep("amdafmf", pAmdAfmf && AdlxTweaks.AfmfSupported(), amdAfmfActive,
                AdlxTweaks.ActivateAfmf, AdlxTweaks.RestoreAfmf);
            bool pStandby = sp != null ? sp.StandbySweep : standbySweepOn;
            if (pStandby) StandbySweep.MaybePurge();
            bool aggressivePower = IsAggressive(mode, pAggr);
            int powerKey = (aggressivePower ? 1 : 0) | (usePlan ? 2 : 0);
            long nowTicks = DateTime.UtcNow.Ticks;
            if (usePlan)
            {
                if (!planActive || powerKey != lastPowerPolicyKey
                    || nowTicks >= nextPowerAuditTicks)
                {
                    bool planOk = PowerPlan.Enforce(aggressivePower);
                    planActive = true;
                    lastPowerPolicyKey = powerKey;
                    if (planOk)
                    {
                        planFailStreak = 0;
                        if (LoadCounter(PowerFailStreakKey) != 0) SaveCounter(PowerFailStreakKey, 0);
                        nextPowerAuditTicks = long.MaxValue;
                    }
                    else
                    {
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
                        }
                        else
                        {
                            int delay = 30;
                            for (int i = 1; i < planFailStreak && delay < 300; i++) delay *= 2;
                            if (delay > 300) delay = 300;
                            nextPowerAuditTicks = DateTime.UtcNow.AddSeconds(delay).Ticks;
                        }
                    }
                }
            }
            else if (planActive && PowerPlan.Restore())
            {
                planActive = false;
                lastPowerPolicyKey = -1;
                nextPowerAuditTicks = 0;
            }

            if (!timerRaised)
            {
                if (Native.OsBuild() > 0 && Native.OsBuild() < 19041)
                {
                    try { Native.timeBeginPeriod(1); } catch { }
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
        private bool amdAlagActive;
        private bool amdAfmfActive;

        private static bool RestoreAmdAntiLagEnv()
        {
            bool ok = AdlxTweaks.RestoreAntiLag();
            ok &= AdlxTweaks.RestoreChill();
            return ok;
        }
        private bool planActive;
        private int lastPowerPolicyKey = -1;
        private long nextPowerAuditTicks;

        private const string PowerFailStreakKey = "PowerPlanFailStreak";
        private const int PowerPlanAutoOffThreshold = EnvFuseAttempts;
        private int planFailStreak;

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
            NoteNvKey(NvDrsTweaks.KeyAnsel, plan.AnselOff, failed.Contains(NvDrsTweaks.KeyAnsel));
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
            else if (key == NvDrsTweaks.KeyAnsel) { nvAnselOff = false; Settings.Save("NvAnselOff", false); label = Lang.T("t.gamemodeenv.23"); policyKey = PolicyCatalog.KeyNvAnselOff; }
            else if (key == NvDrsTweaks.KeyRebarFeat) { nvRebarOn = false; Settings.Save("NvRebar", false); label = Lang.T("t.gamemodeenv.24"); policyKey = PolicyCatalog.KeyNvRebar; }
            else if (key == NvDrsTweaks.KeyDlssOvr) { nvDlssMode = "off"; Settings.SaveStr("NvDlss", "off"); label = Lang.T("t.gamemodeenv.25"); policyKey = PolicyCatalog.KeyNvDlss; }
            else if (key == NvDrsTweaks.KeySmooth) { nvSmoothMotion = false; Settings.Save("NvSmoothMotion", false); label = Lang.T("t.gamemodeenv.27"); policyKey = PolicyCatalog.KeyNvSmoothMotion; }
            else if (key == NvDrsTweaks.KeyShaderCache) { nvShaderCacheMax = false; Settings.Save("NvShaderCache", false); label = Lang.T("set.nvshader"); policyKey = PolicyCatalog.KeyNvShaderCache; }
            else { nvLowLatMode = "off"; Settings.SaveStr("NvLowLat", "off"); label = Lang.T("set.nvll"); policyKey = PolicyCatalog.KeyNvLowLat; }
            Logger.Log(" " + label + Lang.T("log.gamemodeenv.10") + EnvFuseAttempts
                + Lang.T("log.gamemodeenv.28"));
            ClearActiveSessionOverride(policyKey, label);
        }

        private bool EnvActive()
        {
            bool remedy = false;
            try { remedy = FrameRemedy.HasResidue; } catch { }
            return doActive || wlanActive || wuActive || pqosActive || awakeActive || rsrActive || gpwActive || planActive || timerRaised
                || amdAlagActive || amdAfmfActive || remedy;
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
            if (doActive) parts.Add(Lang.T("t.gamemodeenv.31"));
            if (wlanActive) parts.Add(Lang.T("t.gamemodeenv.2"));
            if (wuActive) parts.Add(Lang.T("t.gamemodeenv.32"));
            if (pqosActive) parts.Add(Lang.T("t.gamemodeenv.33"));
            if (awakeActive) parts.Add(Lang.T("t.gamemodeenv.34"));
            if (planActive) parts.Add(Lang.T("t.gamemodeenv.35"));
            try { if (FrameRemedy.HasResidue) parts.Add(Lang.T("remedy.residue")); } catch { }
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
            bool ok = true;
            if (DoTweak.Restore()) doActive = false; else ok = false;
            if (WlanGuard.Restore()) wlanActive = false; else ok = false;
            if (UpdatePause.Restore()) wuActive = false; else ok = false;
            if (PresenceQos.Restore()) pqosActive = false; else ok = false;
            if (DisplayAwake.Restore()) awakeActive = false; else ok = false;
            if (AdlxTweaks.RestoreRsr()) rsrActive = false; else ok = false;
            if (GpuPowerMax.Restore()) gpwActive = false; else ok = false;
            if (RestoreAmdAntiLagEnv()) amdAlagActive = false; else ok = false;
            if (AdlxTweaks.RestoreAfmf()) amdAfmfActive = false; else ok = false;
            if (PowerPlan.Restore())
            {
                planActive = false;
                lastPowerPolicyKey = -1;
                nextPowerAuditTicks = 0;
            }
            else ok = false;
            PowerPlan.RestoreParkState();
            if (FrameRemedy.HasResidue && !FrameRemedy.Revert()) ok = false;
            if (timerRaised)
            {
                try
                {
                    if (Native.timeEndPeriod(1) == 0) timerRaised = false;
                    else ok = false;
                }
                catch { ok = false; }
            }
            return ok;
        }

        private void ReleaseBackground()
        {
            ReleaseBackground(Lang.T("t.gamemodeenv.41"));
        }

        private int ReleaseBackground(string reasonPrefix)
        {
            pressure.Clear();
            if (!core.AnyWith(SuppressReason.Background)) return 0;
            int n = 0;
            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (core.Release(pid, SuppressReason.Background)) { ReportSeal(pid); n++; }
            if (n > 0) Logger.Log(reasonPrefix + Lang.T("log.gamemodeenv.42") + n + Lang.T("log.gamemodeenv.43"));
            return n;
        }
    }
}
