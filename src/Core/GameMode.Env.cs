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
              "pqos", "awake", "rsr", "gpupower", "amdalag", "amdafmf", "amdfrtc", "corepark" };

        private static string EnvLabel(string key)
        {
            switch (key)
            {
                case "do": return "后台下载暂停";
                case "wlanscan": return "无线扫描抑制";
                case "wu": return "Windows 更新暂停";
                case "pqos": return "无输入降级关闭";
                case "awake": return "息屏防护";
                case "rsr": return "RSR 驱动级升格";
                case "gpupower": return "显卡功耗墙";
                case "corepark": return "核心停泊解除";
                case "amdalag": return "AMD Anti-Lag";
                case "amdafmf": return "AMD 流体运动帧";
                case "amdfrtc": return "AMD 帧率上限";
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
                        Logger.Log("环境项 " + EnvLabel(key) + " 连续 " + failures
                            + " 次写入失败 已自动关闭对应开关并停用 重新打开该开关即恢复尝试");
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
                case "amdfrtc": amdFrlMode = "off"; Settings.SaveStr("AmdFrl", "off"); break;
                case "overlay": break;
                case "corepark": break;
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
                case "amdfrtc": return PolicyCatalog.KeyAmdFrl;
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
                Logger.Log("独立配置 " + snap.ProfileName + " 的 " + label
                    + " 覆盖因写入失败一并清除 不再逐局重试");
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
            if (wasFused) Logger.Log("环境项 " + EnvLabel(key) + " 开关重新打开 恢复写入尝试");
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
            string pAmdFrl = sp != null ? sp.AmdFrlMode : amdFrlMode;
            bool competitive = mode == PerformancePreset.Competitive;
            bool custom = mode == PerformancePreset.Custom;
            bool usePauseDl = custom ? pPauseDl : competitive;
            bool slowReady = slowEnvAtTicks == 0 || DateTime.UtcNow.Ticks >= slowEnvAtTicks;
            usePauseDl = usePauseDl && slowReady;
            bool usePlan = ResolvePowerPlanEnabled(mode, pPlan);
            SuppressionCore.GpuDemoteEnabled = sp != null ? sp.GpuDemote : gpuDemoteOn;
            SuppressionCore.SqueezeBackground = sp != null ? sp.SqueezeBackground : squeezeBgOn;
            doActive = EnvStep("do", usePauseDl, doActive, DoTweak.Activate, DoTweak.Restore);
            wlanActive = EnvStep("wlanscan", pWlan, wlanActive, WlanGuard.Activate, WlanGuard.Restore);
            // Game DVR 关闭已移出对局环境步:该值由系统在游戏启动那一刻读取 对局中才写对本局无效
            // 改由游戏模式生命周期在会话级应用(SyncGameDvr)游戏启动前就已关闭 退出 Pavise 才还原
            wuActive = EnvStep("wu", pWu && slowReady, wuActive, UpdatePause.Activate, UpdatePause.Restore);
            // 无输入降级关闭(PresenceQos):属对游戏有益的 PowerThrottling 家族项 成本极低(一次 HKLM 写)恢复启用
            pqosActive = EnvStep("pqos", true, pqosActive, PresenceQos.Activate, PresenceQos.Restore);
            awakeActive = EnvStep("awake", pAwake, awakeActive, DisplayAwake.Activate, DisplayAwake.Restore);
            rsrActive = EnvStep("rsr", rsrOn, rsrActive, AdlxTweaks.ActivateRsr, AdlxTweaks.RestoreRsr);
            gpwActive = EnvStep("gpupower", gpuPowerMaxOn, gpwActive, GpuPowerMax.Activate, GpuPowerMax.Restore);
            int frlTarget = pAmdFrl == "off" ? 0 : ResolveFrlFps(pAmdFrl);
            // Chill 与 Anti-Lag 驱动互斥 限帧走 Chill 时 Anti-Lag 让位 用户显式设定的数值优先
            bool limitViaChill = frlTarget > 0 && AdlxTweaks.ChillSupported();
            if (pAmdAlag && limitViaChill && !amdFrlMutexLogged)
            {
                amdFrlMutexLogged = true;
                Logger.Log("AMD 帧率上限与 Anti-Lag 驱动互斥，帧率上限优先，Anti-Lag 本轮不启用");
            }
            if (!limitViaChill) amdFrlMutexLogged = false;
            amdAlagActive = EnvStep("amdalag", pAmdAlag && !limitViaChill && AdlxTweaks.AntiLagSupported(),
                amdAlagActive, AdlxTweaks.ActivateAntiLag, RestoreAmdAntiLagEnv);
            amdAfmfActive = EnvStep("amdafmf", pAmdAfmf && AdlxTweaks.AfmfSupported(), amdAfmfActive,
                AdlxTweaks.ActivateAfmf, AdlxTweaks.RestoreAfmf);
            if (amdFrtcActive && frlTarget > 0 && frlTarget != amdFrtcFps
                && AdlxTweaks.ActivateFrameLimit(frlTarget))
                amdFrtcFps = frlTarget;
            amdFrtcActive = EnvStep("amdfrtc", frlTarget > 0 && AdlxTweaks.FrameLimitSupported(), amdFrtcActive,
                delegate
                {
                    bool applied = AdlxTweaks.ActivateFrameLimit(frlTarget);
                    if (applied) amdFrtcFps = frlTarget;
                    return applied;
                },
                AdlxTweaks.RestoreFrameLimit);
            if (!amdFrtcActive) amdFrtcFps = 0;
            // 待机内存清理:仅在内存吃紧(可用<15%)且过了冷却期时清一次低优先级待机页 自带阈值+45秒冷却
            // 高内存/无压力机器 MaybePurge 直接返回不动作;低内存吃紧时缓解帧时间抖动 见 StandbySweep
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
                        // 设一次即可 不再每30秒定时强制拉回 那会与其它电源/厂商软件反复抢方案造成周期性顿挫
                        // 仅在方案项(档位/开关)变化或写入失败重试时才重新应用 平时不再触碰电源方案
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
                            ClearActiveSessionOverride(PolicyCatalog.KeyPowerPlan, "电源计划切换");
                            Logger.Log("电源计划累计连续 " + persistedStreak
                                + " 次切换失败 多半被其他电源或优化类软件接管 已自动关闭 电源计划切换 开关 不再重试 "
                                + "排除冲突软件后可在策略页重新开启");
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

            coreParkActive = EnvStep("corepark", usePlan, coreParkActive,
                PowerPlan.UnparkForSession, PowerPlan.RestoreParkState);

            if (!timerRaised)
            {
                if (Native.OsBuild() > 0 && Native.OsBuild() < 19041)
                {
                    try { Native.timeBeginPeriod(1); } catch { }
                    timerRaised = true;
                }
                else if (!timerSkipLogged)
                {
                    Logger.Log("计时器精度 本系统按进程隔离 提升无效 已跳过");
                    timerSkipLogged = true;
                }
            }
        }

        private bool wuActive;
        private bool coreParkActive;
        private bool amdAlagActive;
        private bool amdAfmfActive;
        private bool amdFrtcActive;
        private int amdFrtcFps;
        private bool amdFrlMutexLogged;

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
            NoteNvKey(NvDrsTweaks.KeyFrl, plan.FrlFps > 0, failed.Contains(NvDrsTweaks.KeyFrl));
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
            NoteNvKey(NvDrsTweaks.KeyBattFps, plan.BattFull, failed.Contains(NvDrsTweaks.KeyBattFps));
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
            if (key == NvDrsTweaks.KeyPState) { nvMaxPerf = false; Settings.Save("NvMaxPerf", false); label = "NVIDIA 电源最高性能"; policyKey = PolicyCatalog.KeyNvMaxPerf; }
            else if (key == NvDrsTweaks.KeyFrl) { nvFrlMode = "off"; Settings.SaveStr("NvFrl", "off"); label = "NVIDIA 帧率上限"; policyKey = PolicyCatalog.KeyNvFrl; }
            else if (key == NvDrsTweaks.KeyAnsel) { nvAnselOff = false; Settings.Save("NvAnselOff", false); label = "NVIDIA Ansel 关闭"; policyKey = PolicyCatalog.KeyNvAnselOff; }
            else if (key == NvDrsTweaks.KeyRebarFeat) { nvRebarOn = false; Settings.Save("NvRebar", false); label = "NVIDIA ReBAR 强开"; policyKey = PolicyCatalog.KeyNvRebar; }
            else if (key == NvDrsTweaks.KeyDlssOvr) { nvDlssMode = "off"; Settings.SaveStr("NvDlss", "off"); label = "NVIDIA DLSS 覆写"; policyKey = PolicyCatalog.KeyNvDlss; }
            else if (key == NvDrsTweaks.KeyBattFps) { nvBattFull = false; Settings.Save("NvBattFull", false); label = "NVIDIA 电池满血"; policyKey = PolicyCatalog.KeyNvBattFull; }
            else if (key == NvDrsTweaks.KeySmooth) { nvSmoothMotion = false; Settings.Save("NvSmoothMotion", false); label = "NVIDIA Smooth Motion 插帧"; policyKey = PolicyCatalog.KeyNvSmoothMotion; }
            else if (key == NvDrsTweaks.KeyShaderCache) { nvShaderCacheMax = false; Settings.Save("NvShaderCache", false); label = "NVIDIA 着色器缓存无上限"; policyKey = PolicyCatalog.KeyNvShaderCache; }
            else { nvLowLatMode = "off"; Settings.SaveStr("NvLowLat", "off"); label = "NVIDIA 低延迟"; policyKey = PolicyCatalog.KeyNvLowLat; }
            Logger.Log(" " + label + " 连续 " + EnvFuseAttempts
                + " 次写入失败 已自动关闭该开关 重新打开即恢复尝试");
            ClearActiveSessionOverride(policyKey, label);
        }

        internal static int ResolveFrlFps(string mode)
        {
            if (string.IsNullOrEmpty(mode) || mode == "off") return 0;
            if (mode == "screen")
            {
                int hz = DisplayGuard.MaxRefreshRate();
                return hz >= 48 ? hz - 3 : 0;
            }
            int fps;
            if (!int.TryParse(mode, NumberStyles.Integer, CultureInfo.InvariantCulture, out fps) || fps <= 0)
                return 0;
            if (fps < PolicyCatalog.FrlMin) fps = PolicyCatalog.FrlMin;
            if (fps > PolicyCatalog.FrlMax) fps = PolicyCatalog.FrlMax;
            return fps;
        }

        private bool EnvActive()
        {
            return doActive || wlanActive || wuActive || pqosActive || awakeActive || rsrActive || gpwActive || planActive || timerRaised
                || amdAlagActive || amdAfmfActive || amdFrtcActive || coreParkActive;
        }

        private string lastResidueLogged;
        private long residueLogTicks;

        private string ResidueDetail()
        {
            var parts = new List<string>();
            bool sessionActive;
            int boostCount;
            lock (sync) { sessionActive = active; boostCount = gameBoost.Count; }
            if (sessionActive) parts.Add("会话未关");
            if (boostCount > 0) parts.Add("游戏提优 " + boostCount + " 项");
            if (core.AnyWith(SuppressReason.Background)) parts.Add("后台压制");
            if (doActive) parts.Add("下载暂停");
            if (wlanActive) parts.Add("无线扫描抑制");
            if (wuActive) parts.Add("更新暂停");
            if (pqosActive) parts.Add("降级豁免");
            if (awakeActive) parts.Add("防熄屏");
            if (planActive) parts.Add("电源计划");
            if (coreParkActive) parts.Add("核心停泊解除");
            if (timerRaised) parts.Add("计时器精度");
            return parts.Count > 0 ? string.Join(" ", parts.ToArray()) : "状态位残留";
        }

        private bool RetryDeactivate(string reason)
        {
            string detail = ResidueDetail();
            long now = DateTime.UtcNow.Ticks;
            if (detail != lastResidueLogged
                || now - residueLogTicks >= TimeSpan.TicksPerMinute * 10)
            {
                Logger.Log("游戏模式残留待恢复 " + reason + " " + detail + " 静默重试中");
                lastResidueLogged = detail;
                residueLogTicks = now;
            }
            bool clean = Deactivate(reason, true);
            if (clean)
            {
                Logger.Log("游戏模式残留已全部恢复");
                lastResidueLogged = null;
                residueLogTicks = 0;
            }
            return clean;
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
            if (AdlxTweaks.RestoreFrameLimit()) { amdFrtcActive = false; amdFrtcFps = 0; } else ok = false;
            if (PowerPlan.Restore())
            {
                planActive = false;
                lastPowerPolicyKey = -1;
                nextPowerAuditTicks = 0;
            }
            else ok = false;
            if (PowerPlan.RestoreParkState()) coreParkActive = false; else ok = false;
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
            ReleaseBackground("后台压制已关闭");
        }

        private int ReleaseBackground(string reasonPrefix)
        {
            pressure.Clear();
            if (!core.AnyWith(SuppressReason.Background)) return 0;
            int n = 0;
            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (core.Release(pid, SuppressReason.Background)) { ReportSeal(pid); n++; }
            if (n > 0) Logger.Log(reasonPrefix + " 解除 " + n + " 个进程的压制 个别被句柄保护的会自动补还原");
            return n;
        }
    }
}
