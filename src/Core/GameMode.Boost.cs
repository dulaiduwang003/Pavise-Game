// @author bdth 2074055628@qq.com
// 文件用途 负责游戏提优 环境调整和退出恢复

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
            { "notif", "do", "hz", "svc", "dvr", "fx", "wu", "nvbg", "alag", "chill", "esync", "ris",
              "pqos", "awake", "overlay" };

        private static string EnvLabel(string key)
        {
            switch (key)
            {
                case "notif": return "通知免打扰";
                case "do": return "后台下载暂停";
                case "hz": return "刷新率守护";
                case "svc": return "服务暂停";
                case "dvr": return "Game DVR 关闭";
                case "fx": return "视觉效果降级";
                case "wu": return "Windows 更新暂停";
                case "nvbg": return "后台硬限帧";
                case "alag": return "AMD Anti-Lag";
                case "chill": return "AMD Chill 限帧";
                case "esync": return "AMD Enhanced Sync";
                case "ris": return "AMD 锐化";
                case "pqos": return "无输入降级关闭";
                case "awake": return "息屏防护";
                case "overlay": return "电源滑块最佳性能";
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
                        Logger.Log("环境项「" + EnvLabel(key) + "」连续 " + failures
                            + " 次写入失败，已自动关闭对应开关并停用；重新打开该开关即恢复尝试");
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
                case "notif": notifQuiet = false; Settings.Save("NotifQuiet", false); break;
                case "do": pauseDlOn = false; Settings.Save("GmPauseDl", false); break;
                case "hz": hzGuard = false; Settings.Save("HzGuardOn", false); break;
                case "svc": svcPauseOn = false; Settings.Save("GmSvcPause", false); break;
                case "dvr": killGameDvr = false; Settings.Save("GameDvrOff", false); break;
                case "fx": visualFxOn = false; Settings.Save("GmVisualFx", false); break;
                case "wu": pauseUpdateOn = false; Settings.Save("GmPauseUpdate", false); break;
                case "nvbg": nvBgFrlOn = false; Settings.Save("NvBgFrl", false); break;
                case "alag": amdAntiLagOn = false; Settings.Save("AmdAntiLag", false); break;
                case "chill": amdChillMode = "off"; Settings.SaveStr("AmdChill", "off"); break;
                case "esync": amdEnhSyncOn = false; Settings.Save("AmdEnhSync", false); break;
                case "ris": amdRisOn = false; Settings.Save("AmdRis", false); break;
                case "pqos": presenceQosOn = false; Settings.Save("GmPresenceQos", false); break;
                case "awake": awakeOn = false; Settings.Save("GmAwake", false); break;
                case "overlay": powerOverlayOn = false; Settings.Save("GmPowerOverlay", false); break;
            }
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
            if (wasFused) Logger.Log("环境项「" + EnvLabel(key) + "」开关重新打开，恢复写入尝试");
        }

        private void ApplyEnv()
        {
            PerformancePreset mode = ActivePreset;
            bool competitive = mode == PerformancePreset.Competitive;
            bool custom = mode == PerformancePreset.Custom;
            bool usePauseDl = custom ? pauseDlOn : competitive;
            bool useSvc = custom ? svcPauseOn : false;
            bool useDvr = custom ? killGameDvr : competitive;
            notifActive = EnvStep("notif", notifQuiet, notifActive, Notif.Quiet, Notif.Restore);
            doActive = EnvStep("do", usePauseDl, doActive, DoTweak.Activate, DoTweak.Restore);
            hzActive = EnvStep("hz", hzGuard, hzActive, DisplayGuard.Activate, DisplayGuard.Restore);
            svcActive = EnvStep("svc", useSvc, svcActive, SvcPause.Activate, SvcPause.Restore);
            dvrActive = EnvStep("dvr", useDvr, dvrActive, GameDvr.Activate, GameDvr.Restore);
            fxActive = EnvStep("fx", visualFxOn, fxActive, VisualFx.Activate, VisualFx.Restore);
            wuActive = EnvStep("wu", pauseUpdateOn, wuActive, UpdatePause.Activate, UpdatePause.Restore);
            nvbgActive = EnvStep("nvbg", nvBgFrlOn, nvbgActive, NvGlobalTweaks.Activate, NvGlobalTweaks.Restore);
            alagActive = EnvStep("alag", amdAntiLagOn, alagActive, AdlxTweaks.ActivateAntiLag, AdlxTweaks.RestoreAntiLag);
            chillActive = EnvStep("chill", ResolveFrlFps(amdChillMode) > 0, chillActive,
                delegate { return AdlxTweaks.ActivateChill(ResolveFrlFps(amdChillMode)); }, AdlxTweaks.RestoreChill);
            esyncActive = EnvStep("esync", amdEnhSyncOn, esyncActive, AdlxTweaks.ActivateEnhancedSync, AdlxTweaks.RestoreEnhancedSync);
            risActive = EnvStep("ris", amdRisOn, risActive, AdlxTweaks.ActivateRis, AdlxTweaks.RestoreRis);
            pqosActive = EnvStep("pqos", presenceQosOn, pqosActive, PresenceQos.Activate, PresenceQos.Restore);
            awakeActive = EnvStep("awake", awakeOn, awakeActive, DisplayAwake.Activate, DisplayAwake.Restore);
            overlayActive = EnvStep("overlay", powerOverlayOn, overlayActive, PowerOverlay.Activate, PowerOverlay.Restore);
            if (standbySweepOn && !standbyPurged)
            {
                standbyPurged = true;
                StandbySweep.PurgeOnce();
            }
            bool aggressivePower = IsAggressive(mode, aggressiveOn);
            int powerKey = (aggressivePower ? 1 : 0)
                | (idleDisableOn ? 2 : 0) | (planSwitch ? 4 : 0);
            long nowTicks = DateTime.UtcNow.Ticks;
            if (planSwitch)
            {
                if (!planActive || powerKey != lastPowerPolicyKey
                    || nowTicks >= nextPowerAuditTicks)
                {
                    bool planOk = PowerPlan.Enforce(aggressivePower, idleDisableOn);
                    planActive = true;
                    lastPowerPolicyKey = powerKey;
                    if (planOk)
                    {
                        planFailStreak = 0;
                        if (LoadCounter(PowerFailStreakKey) != 0) SaveCounter(PowerFailStreakKey, 0);
                        nextPowerAuditTicks = DateTime.UtcNow.AddSeconds(30).Ticks;
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
                            Logger.Log("电源计划累计连续 " + persistedStreak
                                + " 次切换失败（多半被其他电源/优化类软件接管），已自动关闭「电源计划切换」开关，不再重试；"
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

            if (!timerRaised)
            {
                if (Native.OsBuild() > 0 && Native.OsBuild() < 19041)
                {
                    try { Native.timeBeginPeriod(1); } catch { }
                    timerRaised = true;
                }
                else if (!timerSkipLogged)
                {
                    Logger.Log("计时器精度：Win10 2004+ 按进程隔离，跨进程提升无效，已跳过");
                    timerSkipLogged = true;
                }
            }
        }

        private bool fxActive;
        private bool wuActive;
        private bool standbyPurged;
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
            NoteNvKey(NvDrsTweaks.KeyPreRender, plan.LowLatency, failed.Contains(NvDrsTweaks.KeyPreRender));
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
            if (key == NvDrsTweaks.KeyPState) { nvMaxPerf = false; Settings.Save("NvMaxPerf", false); label = "NVIDIA 电源最高性能"; }
            else if (key == NvDrsTweaks.KeyFrl) { nvFrlMode = "off"; Settings.SaveStr("NvFrl", "off"); label = "NVIDIA 帧率上限"; }
            else if (key == NvDrsTweaks.KeyAnsel) { nvAnselOff = false; Settings.Save("NvAnselOff", false); label = "NVIDIA Ansel 关闭"; }
            else if (key == NvDrsTweaks.KeyRebarFeat) { nvRebarOn = false; Settings.Save("NvRebar", false); label = "NVIDIA ReBAR 强开"; }
            else if (key == NvDrsTweaks.KeyDlssOvr) { nvDlssMode = "off"; Settings.SaveStr("NvDlss", "off"); label = "NVIDIA DLSS 覆写"; }
            else if (key == NvDrsTweaks.KeyBattFps) { nvBattFull = false; Settings.Save("NvBattFull", false); label = "NVIDIA 电池满血"; }
            else { nvLowLatency = false; Settings.Save("NvLowLatency", false); label = "NVIDIA 低延迟"; }
            Logger.Log("「" + label + "」连续 " + EnvFuseAttempts
                + " 次写入失败，已自动关闭该开关；重新打开即恢复尝试");
        }

        internal static int ResolveFrlFps(string mode)
        {
            if (mode == "60") return 60;
            if (mode == "120") return 120;
            if (mode == "240") return 240;
            if (mode == "screen")
            {
                int hz = DisplayGuard.CurrentRefreshRate();
                if (hz >= 48) return hz - 3;
            }
            return 0;
        }

        private bool EnvActive()
        {
            return notifActive || doActive || hzActive || svcActive || dvrActive || fxActive || wuActive || nvbgActive || alagActive || chillActive || esyncActive || risActive || pqosActive || awakeActive || overlayActive || planActive || timerRaised;
        }

        private bool RestoreEnv()
        {
            bool ok = true;
            standbyPurged = false;
            if (Notif.Restore()) notifActive = false; else ok = false;
            if (DoTweak.Restore()) doActive = false; else ok = false;
            if (DisplayGuard.Restore()) hzActive = false; else ok = false;
            if (SvcPause.Restore()) svcActive = false; else ok = false;
            if (GameDvr.Restore()) dvrActive = false; else ok = false;
            if (VisualFx.Restore()) fxActive = false; else ok = false;
            if (UpdatePause.Restore()) wuActive = false; else ok = false;
            if (NvGlobalTweaks.Restore()) nvbgActive = false; else ok = false;
            if (AdlxTweaks.RestoreAntiLag()) alagActive = false; else ok = false;
            if (AdlxTweaks.RestoreChill()) chillActive = false; else ok = false;
            if (AdlxTweaks.RestoreEnhancedSync()) esyncActive = false; else ok = false;
            if (AdlxTweaks.RestoreRis()) risActive = false; else ok = false;
            if (PresenceQos.Restore()) pqosActive = false; else ok = false;
            if (DisplayAwake.Restore()) awakeActive = false; else ok = false;
            if (PowerOverlay.Restore()) overlayActive = false; else ok = false;
            if (PowerPlan.Restore())
            {
                planActive = false;
                lastPowerPolicyKey = -1;
                nextPowerAuditTicks = 0;
            }
            else ok = false;
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
            freezeDwell.Clear();
            if (!core.AnyWith(SuppressReason.Background)) return 0;
            int n = 0;
            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (core.Release(pid, SuppressReason.Background)) { ReportUntrack(pid); n++; }
            if (n > 0) Logger.Log(reasonPrefix + "：解除 " + n + " 个进程的压制（个别被句柄保护的会自动补还原）");
            return n;
        }

        private void Boost(Process[] all)
        {
            var live = new HashSet<int>();
            PerformancePreset mode = ActivePreset;
            bool useStrict = strictCoreOn && CpuTopology.HasSafeBackgroundPartition();
            ulong desiredMask = useStrict ? strictMask : gameMask;
            int rendererPid = -1;
            long rendererCreation = 0;
            string rendererName = null;
            string rendererPath = null;
            string rendererProfileId = null;
            bool rendererLearnable = false;
            lock (sync)
                if (activeDetection != null && activeDetection.RendererCandidateSelected)
                {
                    rendererPid = activeDetection.RendererPid;
                    rendererCreation =
                        activeDetection.RendererCreation;
                    rendererName =
                        activeDetection.RendererName;
                    rendererPath =
                        activeDetection.RendererPath;
                    rendererProfileId =
                        activeDetection.Profile != null
                            ? activeDetection.Profile.Id : null;
                    rendererLearnable =
                        activeDetection.RendererLearnable;
                }
            bool staleBoost = false;
            lock (sync)
                foreach (KeyValuePair<int, Snap> boosted
                    in gameBoost)
                    if (boosted.Key != rendererPid
                        || boosted.Value.Creation
                            != rendererCreation
                        || !string.Equals(
                            boosted.Value.Name,
                            rendererName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        staleBoost = true;
                        break;
                    }
            if (staleBoost) UnboostGames(rendererPid, rendererCreation, rendererName);
            foreach (Process p in all)
            {
                try
                {
                    int pid = p.Id;
                    live.Add(pid);
                    if (rendererPid <= 0 || pid != rendererPid) continue;
                    bool known, retryEco, needTweak, needPlacement, auditDue, stripped;
                    lock (sync)
                    {
                        stripped = boostHandleStripped.Contains(pid);
                        known = gameBoost.ContainsKey(pid);
                        retryEco = boostFail.ContainsKey(pid) && !boostEcoGaveUp.Contains(pid);
                        needTweak = (gpuHighPerf || disableFso || nvMaxPerf || nvLowLatency || nvFrlMode != "off")
                            && !tweakApplied.Contains(pid);
                        ulong placed; bool placedStrict;
                        needPlacement = !placementGaveUp.Contains(pid)
                            && (!gamePlacement.TryGetValue(pid, out placed) || placed != desiredMask
                                || !gamePlacementStrict.TryGetValue(pid, out placedStrict) || placedStrict != useStrict);
                        long nextAudit;
                        auditDue = !known || retryEco || needTweak || needPlacement
                            || !boostStateVerified.Contains(pid)
                            || !gameBoostNextAudit.TryGetValue(pid, out nextAudit)
                            || DateTime.UtcNow.Ticks >= nextAudit;
                        if (stripped) auditDue = needTweak;
                    }
                    if (!auditDue) continue;
                    IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        bool firstDeny;
                        lock (sync) firstDeny = boostDenied.Add(pid);
                        if (firstDeny) Logger.Log("游戏提优：" + rendererName + " (pid " + pid + ") 打不开句柄，本体提优跳过，后台压制不受影响");
                        if (ifeoOn && boostOn) IfeoBoost.EnsureForGame(rendererName);
                        continue;
                    }
                    try
                    {
                        string img = Native.ImageName(h);
                        long currentCreation, currentCpu; ulong currentDisk;
                        if (!Native.QueryProcessSample(h, out currentCreation, out currentCpu, out currentDisk))
                        {
                            Logger.Log("游戏提优：无法读取 " + rendererName + " (pid " + pid + ") 的创建时间，已按安全边界跳过");
                            continue;
                        }
                        if (!RendererIdentityMatches(
                                rendererPid, rendererCreation,
                                rendererName, pid,
                                currentCreation, img))
                        {
                            Logger.Log("游戏提优：renderer 身份已变化，跳过 pid "
                                + pid + " 的全部写入");
                            continue;
                        }
                        if (known)
                        {
                            Snap tracked;
                            bool reused = false;
                            lock (sync)
                                if (gameBoost.TryGetValue(pid, out tracked) && tracked.Creation > 0
                                    && tracked.Creation != currentCreation)
                                {
                                    gameBoost.Remove(pid); gameGpu.Remove(pid); gamePlacement.Remove(pid);
                                    gamePlacementStrict.Remove(pid); boostFail.Remove(pid);
                                    boostStateWarned.Remove(pid); boostStateVerified.Remove(pid);
                                    gameBoostNextAudit.Remove(pid);
                                    boostHandleStripped.Remove(pid); boostEcoGaveUp.Remove(pid);
                                    placementFail.Remove(pid); placementGaveUp.Remove(pid);
                                    tweakApplied.Remove(pid); reused = true; known = false;
                                }
                            if (reused)
                            {
                                needPlacement = true;
                                CrashGuard.ReleaseBoostProcess(pid, tracked.Creation);
                            }
                        }
                        bool newlyTracked = false;
                        bool gpuOk = false;
                        if (!known)
                        {
                            uint pri = Native.GetPriorityClass(h);
                            if (pri == 0) pri = Native.NORMAL_PRIORITY_CLASS;
                            ulong oaff = Native.QueryAffinity(h);
                            uint[] ocpuSets = Native.QueryCpuSets(h);
                            if (ocpuSets == null)
                            {
                                Logger.Log("游戏提优：无法读取原 CPU Sets，已按安全边界跳过 " + rendererName + " (pid " + pid + ")");
                                continue;
                            }
                            int oio = Native.QueryIoPriority(h);
                            int opg = Native.QueryPagePriority(h);
                            int gpuOld;
                            bool gpuKnown = Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuOld) == 0;
                            if (!gpuKnown) gpuOld = -1;

                            int oqc, oqs;
                            if (!Native.TryQueryPowerThrottling(h, out oqc, out oqs)) { oqc = -1; oqs = -1; }
                            CrashGuard.OriginalBoostState recovered;
                            if (!CrashGuard.MarkBoostProcess(pid, currentCreation, rendererName, pri, oaff,
                                oio, opg, gpuOld, ocpuSets, oqc, oqs, out recovered))
                            {
                                Logger.Log("游戏提优：崩溃恢复快照无法持久化，已取消修改 " + rendererName + " (pid " + pid + ")");
                                continue;
                            }
                            if (recovered != null)
                            {
                                pri = recovered.Priority;
                                oaff = recovered.Affinity;
                                oio = recovered.Io;
                                opg = recovered.Page;
                                gpuOld = recovered.Gpu;
                                gpuKnown = gpuOld >= 0;
                                ocpuSets = recovered.CpuSets;
                                oqc = recovered.QoSControl;
                                oqs = recovered.QoSState;
                            }
                            var snap = new Snap { Pri = pri, Aff = oaff, Io = oio, Pg = opg,
                                Name = rendererName, Creation = currentCreation, CpuSets = ocpuSets,
                                QoSControl = oqc, QoSState = oqs };
                            lock (sync) gameBoost[pid] = snap;
                            newlyTracked = true;
                            if (rendererLearnable)
                                TryLearnRenderer(rendererProfileId, rendererPath, rendererName);
                            gpuOk = gpuKnown && ApplyAndVerifyGpuBoost(h);
                            lock (sync) { if (gpuKnown) gameGpu[pid] = gpuOld; }
                        }
                        else
                        {
                            int ignoredGpu;
                            lock (sync) gpuOk = gameGpu.TryGetValue(pid, out ignoredGpu);
                            if (gpuOk) gpuOk = ApplyAndVerifyGpuBoost(h);
                        }

                        uint actualPriority;
                        int actualIo, writeError;
                        bool stateOk = ApplyAndVerifyBoostState(h, out actualPriority, out actualIo, out writeError);

                        uint grantedAccess = 0;
                        bool handleStripped = !stateOk
                            && Native.HandleWriteAccessStripped(h, out grantedAccess);
                        if (handleStripped)
                        {
                            bool firstStrip;
                            lock (sync)
                            {
                                firstStrip = boostHandleStripped.Add(pid);
                                boostStateVerified.Remove(pid);
                                boostFail.Remove(pid);
                                placementFail.Remove(pid);
                                placementGaveUp.Add(pid);
                                boostEcoGaveUp.Add(pid);
                                gamePlacement.Remove(pid);
                                gamePlacementStrict.Remove(pid);
                            }
                            if (firstStrip) OnGameHandleStripped(pid, rendererName, grantedAccess);
                            if (!needTweak) continue;
                        }

                        bool firstVerified = false, firstStateWarning = false;
                        lock (sync)
                        {
                            if (stateOk)
                            {
                                firstVerified = boostStateVerified.Add(pid);
                                boostStateWarned.Remove(pid);
                                int jitter = Math.Abs(pid % 11);
                                gameBoostNextAudit[pid] =
                                    DateTime.UtcNow.AddSeconds(20 + jitter).Ticks;
                            }
                            else
                            {
                                boostStateVerified.Remove(pid);
                                firstStateWarning = boostStateWarned.Add(pid);
                                gameBoostNextAudit[pid] =
                                    DateTime.UtcNow.AddSeconds(4).Ticks;
                            }
                        }
                        if (!stateOk && firstStateWarning && !handleStripped)
                            Logger.Log("游戏提优失败：" + rendererName + " (pid " + pid + ") 回读仍为优先级 0x"
                                + actualPriority.ToString("X") + " / IO " + actualIo + "，错误 " + writeError + "；下一轮继续纠偏");

                        string placementText = "";
                        if (needPlacement)
                        {
                            Snap original;
                            lock (sync) { if (!gameBoost.TryGetValue(pid, out original)) continue; }

                            bool placementOk = Native.RestoreCpuSetsVerified(h, original.CpuSets);
                            if (!CpuTopology.MultiGroup)
                                placementOk &= Native.SetProcessAffinityMask(h, (UIntPtr)(original.Aff != 0 ? original.Aff : allMask));
                            uint[] ids = CpuTopology.AdaptiveGameCpuSetIds(useStrict);
                            bool soft = false;
                            bool placementUnavailable = false;
                            if (useStrict || desiredMask != allMask)
                                soft = Native.TrySetCpuSetsVerified(h, ids);
                            if (soft)
                            {
                                placementText = useStrict
                                    ? " + 严格核心分区(CPU Sets)"
                                    : " + 优先大缓存CCD";
                            }
                            else if (desiredMask != allMask && !CpuTopology.MultiGroup)
                            {
                                Native.RestoreCpuSets(h, original.CpuSets);
                                placementOk = Native.SetProcessAffinityMask(h, (UIntPtr)desiredMask)
                                    && Native.QueryAffinity(h) == desiredMask;
                                placementText = useStrict
                                    ? " + 严格绑核 0x" + desiredMask.ToString("X")
                                    : " + 绑核 0x" + desiredMask.ToString("X");
                            }
                            else
                            {
                                placementText = " + 不限核";
                                if (useStrict) placementUnavailable = true;
                            }
                            if (soft) placementOk = true;
                            if (placementUnavailable) placementOk = true;
                            int placeTries = 0;
                            bool placementNowGaveUp = false, firstPlacementWarning = false;
                            lock (sync)
                            {
                                if (placementOk)
                                {
                                    gamePlacement[pid] = desiredMask; gamePlacementStrict[pid] = useStrict;
                                    placementFail.Remove(pid); placementGaveUp.Remove(pid);
                                }
                                else
                                {
                                    gamePlacement.Remove(pid); gamePlacementStrict.Remove(pid);
                                    placementFail.TryGetValue(pid, out placeTries); placeTries++;
                                    if (placeTries >= PlacementRetryMax)
                                    {
                                        placementFail.Remove(pid);
                                        placementNowGaveUp = placementGaveUp.Add(pid);
                                    }
                                    else { placementFail[pid] = placeTries; firstPlacementWarning = placeTries == 1; }
                                }
                            }
                            if (placementUnavailable)
                                Logger.Log("游戏核心策略：" + rendererName + " (pid " + pid
                                    + ") 本机无可用核心分区手段，按不限核处理");
                            else if (placementNowGaveUp)
                                Logger.Log("游戏核心策略：" + rendererName + " (pid " + pid + ") 重试 "
                                    + PlacementRetryMax + " 次仍未生效，已放弃");
                            else if (!placementOk && firstPlacementWarning)
                                Logger.Log("游戏核心策略未完整生效：" + rendererName + " (pid " + pid + ")，下一轮重试");

                            if (!newlyTracked && placementOk)
                                Logger.Log("游戏核心策略：" + rendererName + " (pid " + pid + ")" + placementText);
                        }

                        bool ecoGaveUp;
                        lock (sync) ecoGaveUp = boostEcoGaveUp.Contains(pid);
                        bool ecoCleared = ecoGaveUp || HighQoSVerified(h);
                        if (!ecoCleared)
                        {
                            Native.ApplyHighQoS(h, Native.OsBuild() >= 22000);
                            ecoCleared = HighQoSVerified(h);
                            if (ecoCleared) { lock (sync) { boostFail.Remove(pid); boostEcoGaveUp.Remove(pid); } }
                            else
                            {
                                int tries;
                                bool nowGaveUp = false;
                                lock (sync)
                                {
                                    boostFail.TryGetValue(pid, out tries); tries++;
                                    if (tries >= BoostRetryMax)
                                    {
                                        boostFail.Remove(pid);
                                        nowGaveUp = boostEcoGaveUp.Add(pid);
                                    }
                                    else boostFail[pid] = tries;
                                }
                                if (nowGaveUp)
                                    Logger.Log("游戏提优：" + rendererName + " (pid " + pid + ") 效率模式清不掉，重试 " + tries + " 次后放弃");
                            }
                        }

                        if (renderLaneOn && stateOk && !RenderLane.IsActiveFor(pid, currentCreation))
                            RenderLane.EnsureForGame(pid, currentCreation, rendererName);

                        if (stateOk && firstVerified)
                        {
                            Logger.Log("游戏提优已验证：" + rendererName + " (pid " + pid + ") → 高优先级(回读 0x"
                                + actualPriority.ToString("X") + ")" + placementText + " + 高IO(回读 " + actualIo + ")"
                                + (gpuOk ? " + GPU高" : "")
                                + (!Native.PowerThrottlingSupported ? ""
                                    : ecoCleared ? " + 已退出效率模式" : " + 效率模式未清除" + QoSDump(h)));
                        }

                        if (needTweak)
                        {
                            string imagePath = Native.ImagePath(h);
                            GameExeTweaks.ApplyForGame(imagePath, gpuHighPerf, disableFso);
                            var nvPlan = new NvGamePlan
                            {
                                MaxPerf = nvMaxPerf,
                                FrlFps = ResolveFrlFps(nvFrlMode),
                                LowLatency = nvLowLatency,
                                AnselOff = nvAnselOff,
                                Rebar = nvRebarOn,
                                DlssMode = nvDlssMode,
                                BattFull = nvBattFull
                            };
                            if (!nvPlan.Empty)
                            {
                                List<string> nvFailed = NvDrsTweaks.ApplyForGame(imagePath, nvPlan);
                                HandleNvTweakOutcome(nvFailed, nvPlan);
                            }
                            lock (sync) tweakApplied.Add(pid);
                        }

                    }
                    finally { Native.CloseHandle(h); }
                }
                catch { }
            }

            lock (sync)
            {
                boostDenied.RemoveWhere(x => !live.Contains(x));
                boostStateWarned.RemoveWhere(x => !live.Contains(x));
                boostStateVerified.RemoveWhere(x => !live.Contains(x));
                boostHandleStripped.RemoveWhere(x => !live.Contains(x));
                boostEcoGaveUp.RemoveWhere(x => !live.Contains(x));
                placementGaveUp.RemoveWhere(x => !live.Contains(x));
                tweakApplied.RemoveWhere(x => !live.Contains(x));
                List<int> dead = null;
                foreach (int k in gameBoost.Keys)
                    if (!live.Contains(k)) { if (dead == null) dead = new List<int>(); dead.Add(k); }
                if (dead != null)
                    foreach (int k in dead)
                    {
                        Snap old = gameBoost[k];
                        CrashGuard.ReleaseBoostProcess(k, old.Creation);
                        gameBoost.Remove(k); gameGpu.Remove(k); gamePlacement.Remove(k); gamePlacementStrict.Remove(k);
                        boostFail.Remove(k); boostStateWarned.Remove(k); boostStateVerified.Remove(k);
                        gameBoostNextAudit.Remove(k); placementFail.Remove(k);
                    }
            }
        }

        internal static bool RendererIdentityMatches(
            int expectedPid, long expectedCreation,
            string expectedName, int actualPid,
            long actualCreation, string actualName)
        {
            return expectedPid > 0
                && expectedPid == actualPid
                && expectedCreation > 0
                && expectedCreation == actualCreation
                && !string.IsNullOrEmpty(expectedName)
                && !string.IsNullOrEmpty(actualName)
                && string.Equals(
                    expectedName, actualName,
                    StringComparison.OrdinalIgnoreCase);
        }

        private void OnGameHandleStripped(int pid, string rendererName, uint granted)
        {
            string ac = KernelAntiCheat.Describe(rendererName);
            Logger.Log("游戏提优：" + rendererName + " (pid " + pid + ") 句柄写入权限被"
                + (ac == null ? "反作弊" : ac) + "剥离（授予 0x" + granted.ToString("X")
                + "），本体提优已停止，后台压制不受影响");

            if (ifeoOn && boostOn)
            {
                IfeoBoost.Arm(rendererName);
                IfeoBoost.EnsureForGame(rendererName);
            }
        }

        internal static bool ApplyAndVerifyBoostState(IntPtr process, out uint actualPriority, out int actualIo, out int error)
        {
            error = 0;
            actualIo = Native.QueryIoPriority(process);
            if (actualIo != 3)
            {
            if (!Native.EnsureBoostPrivilege()) error = 1314;
                else
                {
                    int status;
                    if (!Native.TrySetIoPriority(process, 3, out status)) error = status;
                }
            }

            actualPriority = Native.GetPriorityClass(process);
            if (actualPriority != Native.HIGH_PRIORITY_CLASS && !Native.SetPriorityClass(process, Native.HIGH_PRIORITY_CLASS))
                error = Marshal.GetLastWin32Error();

            actualPriority = Native.GetPriorityClass(process);
            actualIo = Native.QueryIoPriority(process);
            return actualPriority == Native.HIGH_PRIORITY_CLASS && actualIo == 3;
        }

        internal static string QoSDump(IntPtr process)
        {
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state)) return "(读取失败)";
            return "(control=0x" + control.ToString("X") + " state=0x" + state.ToString("X") + ")";
        }

        internal static bool InEfficiencyMode(IntPtr process)
        {
            if (!Native.PowerThrottlingSupported) return false;
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state)) return false;
            return (state & 1) != 0;
        }

        internal static bool HighQoSVerified(IntPtr process)
        {
            if (!Native.PowerThrottlingSupported) return true;
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state)) return false;
            return (control & 1) != 0 && (state & 1) == 0;
        }

        private static bool ApplyAndVerifyGpuBoost(IntPtr process)
        {
            int current;
            if (Native.D3DKMTGetProcessSchedulingPriorityClass(process, out current) != 0) return false;
            if (current != Native.GpuPriorityHigh
                && Native.D3DKMTSetProcessSchedulingPriorityClass(process, Native.GpuPriorityHigh) != 0) return false;
            return Native.D3DKMTGetProcessSchedulingPriorityClass(process, out current) == 0
                && current == Native.GpuPriorityHigh;
        }

        private bool UnboostGames()
        {
            return UnboostGames(0, 0, null);
        }

        private static bool IsKeptBoost(
            KeyValuePair<int, Snap> boosted, int keepPid, long keepCreation, string keepName)
        {
            return keepPid > 0
                && boosted.Key == keepPid
                && boosted.Value.Creation == keepCreation
                && string.Equals(boosted.Value.Name, keepName, StringComparison.OrdinalIgnoreCase);
        }

        private bool UnboostGames(int keepPid, long keepCreation, string keepName)
        {
            List<KeyValuePair<int, Snap>> boosts;
            Dictionary<int, int> gpus;
            lock (sync)
            {
                if (gameBoost.Count == 0 && gameGpu.Count == 0) return true;
                boosts = new List<KeyValuePair<int, Snap>>();
                foreach (KeyValuePair<int, Snap> boosted in gameBoost)
                    if (!IsKeptBoost(boosted, keepPid, keepCreation, keepName))
                        boosts.Add(boosted);
                gpus = new Dictionary<int, int>();
                foreach (KeyValuePair<int, int> gpu in gameGpu)
                    if (keepPid <= 0 || gpu.Key != keepPid)
                        gpus[gpu.Key] = gpu.Value;
                if (boosts.Count == 0 && gpus.Count == 0) return true;
                if (keepPid <= 0)
                {
                    boostFail.Clear(); boostDenied.Clear(); boostStateWarned.Clear();
                    boostStateVerified.Clear(); gameBoostNextAudit.Clear();
                    tweakApplied.Clear(); boostHandleStripped.Clear(); boostEcoGaveUp.Clear();
                    placementFail.Clear(); placementGaveUp.Clear();
                }
                else
                    foreach (KeyValuePair<int, Snap> stale in boosts)
                    {
                        boostFail.Remove(stale.Key); boostDenied.Remove(stale.Key);
                        boostStateWarned.Remove(stale.Key); boostStateVerified.Remove(stale.Key);
                        gameBoostNextAudit.Remove(stale.Key); tweakApplied.Remove(stale.Key);
                        boostHandleStripped.Remove(stale.Key); boostEcoGaveUp.Remove(stale.Key);
                        placementFail.Remove(stale.Key); placementGaveUp.Remove(stale.Key);
                    }
            }
            foreach (var kv in boosts)
                if (RenderLane.IsActiveFor(kv.Key, kv.Value.Creation)) RenderLane.Release();
            foreach (var kv in boosts)
            {
                int pid = kv.Key;
                bool done;
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                {
                    IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (hq == IntPtr.Zero)
                    {
                        done = Native.LastOpenProcessFailureWasNoSuchProcess();
                    }
                    else
                    {
                        try
                        {
                            string name = Native.ImageName(hq);
                            long creation, cpu; ulong disk;
                            bool sampled = Native.QueryProcessSample(
                                hq, out creation, out cpu, out disk);
                            bool identityKnown = name != null && sampled;
                            bool same = identityKnown
                                && string.Equals(
                                    name, kv.Value.Name,
                                    StringComparison.OrdinalIgnoreCase)
                                && creation == kv.Value.Creation;
                            done = identityKnown && !same;
                            if (same) Logger.Log("提优还原：" + kv.Value.Name + " (pid " + pid + ") 句柄被保护，身份快照保留待重试");
                        }
                        finally { Native.CloseHandle(hq); }
                    }
                }
                else
                {
                    try
                    {
                        string cur = Native.ImageName(h);
                        long creation, cpu; ulong disk;
                        bool sampled = Native.QueryProcessSample(
                            h, out creation, out cpu, out disk);
                        bool identityKnown = cur != null && sampled;
                        bool identity = identityKnown
                            && string.Equals(
                                cur, kv.Value.Name,
                                StringComparison.OrdinalIgnoreCase)
                            && creation == kv.Value.Creation;
                        if (!identityKnown)
                        {
                            done = false;
                        }
                        else if (identity)
                        {
                            done = SuppressionCore.RestoreValues(h, kv.Value.Pri, kv.Value.Aff, kv.Value.Io,
                                kv.Value.Pg, allMask, kv.Value.CpuSets, kv.Value.QoSControl, kv.Value.QoSState);
                            int gpuOld;
                            if (done && gpus.TryGetValue(pid, out gpuOld))
                                done = Native.D3DKMTSetProcessSchedulingPriorityClass(h, gpuOld) == 0;
                        }
                        else done = true;
                    }
                    finally { Native.CloseHandle(h); }
                }
                if (done)
                {
                    CrashGuard.ReleaseBoostProcess(pid, kv.Value.Creation);
                    lock (sync)
                    {
                        gameBoost.Remove(pid); gameGpu.Remove(pid);
                        gamePlacement.Remove(pid); gamePlacementStrict.Remove(pid);
                        gameBoostNextAudit.Remove(pid);
                    }
                }
            }
            lock (sync) return gameBoost.Count == 0;
        }

        public bool PanicRestore()
        {
            int cleared = SelfProtectedRoster.Clear();
            if (cleared > 0)
                Logger.Log("免压制名单已清空（" + cleared + " 项），下次对局重新探测这些进程");
            int unarmed = IfeoBoost.ClearArmed();
            if (unarmed > 0)
                Logger.Log("内核反作弊预置名单已清空（" + unarmed + " 项），下次对局重新探测这些游戏");
            int fusesCleared;
            lock (sync) { fusesCleared = envFused.Count; envFused.Clear(); }
            foreach (string envKey in EnvKeys)
                if (Settings.Load("EnvFuse_" + envKey, false)) Settings.Save("EnvFuse_" + envKey, false);
            SaveCounter(PowerFailStreakKey, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPState, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyFrl, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPreRender, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyAnsel, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyRebarFeat, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyDlssOvr, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyBattFps, 0);
            if (fusesCleared > 0)
                Logger.Log("已重置 " + fusesCleared + " 个因写入失败自动停用的环境项（对应开关仍为关，需要请手动打开）");
            int mine = Interlocked.Increment(ref panicSeq);
            panicDone.Reset();
            panicResult = false;
            panicReq = true;
            kick.Set();

            long deadline = DateTime.UtcNow.Ticks + 12000L * TimeSpan.TicksPerMillisecond;
            while (true)
            {
                long left = (deadline - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
                if (left <= 0) return false;
                if (!panicDone.WaitOne((int)left)) return false;
                if (Volatile.Read(ref panicServed) == mine) return panicResult;
                panicDone.Reset();
            }
        }

        private bool Deactivate(string reason)
        {
            lock (sync)
            {
                active = false;
                activeGame = null;
                firstSweep = true;
            }
            gameGoneSinceTicks = 0;

            bool clean = UnboostGames();
            List<int> background = core.PidsWith(SuppressReason.Background);
            int ok = core.ReleaseReason(SuppressReason.Background);
            bool backgroundClean = true;
            foreach (int pid in background) if (core.IsThrottled(pid)) { backgroundClean = false; break; }
            bool envClean = RestoreEnv();
            ClearEnvRetryState();
            pressure.Clear();
            freezeDwell.Clear();
            if (clean) CrashGuard.ClearBoost();
            int restoredTotal = ok + gracePreReleased;
            gracePreReleased = 0;
            Logger.Log("游戏模式解除（" + reason + "）：恢复 " + restoredTotal
                + " 个后台进程（本局累计，含中途新增与宽限期先行还原）");
            ReportFinish();
            lock (sync)
            {
                activeDetection = null;
                transitionProbeRendererPid = 0;
                transitionProbeRendererCreation = 0;
            }
            ClearSticky();
            return clean && envClean && backgroundClean;
        }

    }
}
