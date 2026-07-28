// @author bdth 2074055628@qq.com
// 文件用途 负责游戏提优 环境调整和退出恢复

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AegisApp
{
    internal partial class GameMode
    {
        private void ApplyEnv()
        {
            PerformancePreset mode = ActivePreset;
            bool competitive = mode == PerformancePreset.Competitive;
            bool custom = mode == PerformancePreset.Custom;
            bool usePauseDl = custom ? pauseDlOn : competitive;
            bool useNet = custom ? netOn : competitive;
            bool useFg = custom ? fgBoostOn : true;
            bool useSvc = custom ? svcPauseOn : false;
            bool useMmcss = custom ? mmcssOn : competitive;
            bool useDvr = custom ? killGameDvr : competitive;
            if (notifQuiet != notifActive) { if (notifQuiet) notifActive = Notif.Quiet(); else if (Notif.Restore()) notifActive = false; }
            if (usePauseDl != doActive) { if (usePauseDl) doActive = DoTweak.Activate(); else if (DoTweak.Restore()) doActive = false; }
            if (hzGuard != hzActive) { if (hzGuard) hzActive = DisplayGuard.Activate(); else if (DisplayGuard.Restore()) hzActive = false; }
            if (useNet != netActive) { if (useNet) netActive = NetTweak.Activate(); else if (NetTweak.Restore()) netActive = false; }
            if (useFg != fgActive) { if (useFg) fgActive = FgBoost.Activate(); else if (FgBoost.Restore()) fgActive = false; }
            if (useSvc != svcActive) { if (useSvc) svcActive = SvcPause.Activate(); else if (SvcPause.Restore()) svcActive = false; }
            if (useMmcss != mmcssActive) { if (useMmcss) mmcssActive = Mmcss.Activate(); else if (Mmcss.Restore()) mmcssActive = false; }
            if (useDvr != dvrActive) { if (useDvr) dvrActive = GameDvr.Activate(); else if (GameDvr.Restore()) dvrActive = false; }
            if (visualFxOn != fxActive) { if (visualFxOn) fxActive = VisualFx.Activate(); else if (VisualFx.Restore()) fxActive = false; }
            bool aggressivePower = IsAggressive(mode, aggressiveOn);
            if (planSwitch) { PowerPlan.Enforce(aggressivePower, idleDisableOn); planActive = true; }
            else if (PowerPlan.Restore()) planActive = false;

            ApplyStandbyClean();

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

        // 待机内存清理不是"设置"，是一次性动作，所以不走 EnvActive/RestoreEnv 那套还原流程。
        // 全量清理实测清 2.3GB 要 ~530ms，游戏跑起来之后做必然引入卡顿，因此：
        //   - 只在会话刚建立时做一次，此时游戏还在加载，且开在后台线程上不阻塞压制循环
        //   - 会话中途一律只做低优先级清理（实测 5ms），且要同时满足空闲见底 + 确有低优先级可清
        // 抽成静态纯函数便于单测：全量清理每局只能发生一次，之后无论轮询多少次
        // 都只可能走到低优先级那条路，且必须等冷却过去。
        // sessionFresh 表示"这是本局会话建立后的第一次调用"，由游戏进程本身（渲染 PID）
        // 是否换过来判定：中途急救恢复或关开总开关会把 active 清掉再置上，可游戏根本没退，
        // 此时若重新武装全量清理，实测清 2.3GB 要 ~530ms，且会把游戏刚建立的缓存全丢掉，
        // 正是本功能承诺绝不做的事。
        internal static StandbyAction NextStandbyAction(bool cleanOn, bool midOn, ref bool purged,
            bool cooldownElapsed, bool sessionFresh)
        {
            if (!cleanOn) { purged = true; return StandbyAction.None; }
            if (!purged)
            {
                purged = true;
                // 错过了会话建立那一刻就不再补做全量，只允许走保守的中途清理
                if (sessionFresh) return StandbyAction.PurgeFull;
            }
            if (!midOn || !cooldownElapsed) return StandbyAction.None;
            return StandbyAction.PurgeLowPriority;
        }

        private void ApplyStandbyClean()
        {
            long now = DateTime.UtcNow.Ticks;
            int renderer;
            lock (sync) renderer = activeDetection != null ? activeDetection.RendererPid : 0;
            bool sessionFresh = renderer != standbySessionPid;
            if (sessionFresh)
            {
                standbySessionPid = renderer;
                standbyPurged = false;
                standbyMidLastTicks = 0;
            }
            bool cooldownElapsed = now - standbyMidLastTicks >= MidPurgeIntervalTicks;
            StandbyAction action = NextStandbyAction(standbyCleanOn, standbyMidOn, ref standbyPurged,
                cooldownElapsed, sessionFresh);
            if (action == StandbyAction.None) return;
            if (action == StandbyAction.PurgeLowPriority) standbyMidLastTicks = now;

            // 一律丢到后台线程：全量清理实测数百毫秒，绝不能卡住压制循环
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (action == StandbyAction.PurgeFull) { StandbyMemory.PurgeAtSessionStart(); return; }
                    StandbyStats s;
                    if (!StandbyMemory.TryQuery(out s)) return;
                    if (!StandbyMemory.ShouldPurgeMidSession(s, MidPurgeMinMemoryLoad, MidPurgeMinLowBytes)) return;
                    StandbyMemory.PurgeLowPriority();
                }
                catch { }
            });
        }

        private bool fxActive;
        private bool planActive;
        private bool standbyPurged;
        private int standbySessionPid = -1;
        private long standbyMidLastTicks;
        private const long MidPurgeIntervalTicks = 60L * 1000 * 10000;
        private const int MidPurgeMinMemoryLoad = 80;
        private const long MidPurgeMinLowBytes = 256L * 1024 * 1024;

        private bool EnvActive()
        {
            return notifActive || doActive || hzActive || netActive || fgActive || svcActive || mmcssActive || dvrActive || fxActive || planActive || timerRaised;
        }

        private bool RestoreEnv()
        {
            bool ok = true;
            if (Notif.Restore()) notifActive = false; else ok = false;
            if (DoTweak.Restore()) doActive = false; else ok = false;
            if (DisplayGuard.Restore()) hzActive = false; else ok = false;
            if (NetTweak.Restore()) netActive = false; else ok = false;
            if (FgBoost.Restore()) fgActive = false; else ok = false;
            if (SvcPause.Restore()) svcActive = false; else ok = false;
            if (Mmcss.Restore()) mmcssActive = false; else ok = false;
            if (GameDvr.Restore()) dvrActive = false; else ok = false;
            if (VisualFx.Restore()) fxActive = false; else ok = false;
            if (PowerPlan.Restore()) planActive = false; else ok = false;
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
            pressure.Clear();
            if (!core.AnyWith(SuppressReason.Background)) return;
            int n = 0;
            foreach (int pid in core.PidsWith(SuppressReason.Background))
                if (core.Release(pid, SuppressReason.Background)) { ReportUntrack(pid); n++; }
            if (n > 0) Logger.Log("后台压制已关闭：解除 " + n + " 个进程的压制（个别被句柄保护的会自动补还原）");
        }

        private void Boost(Process[] all)
        {
            var live = new HashSet<int>();
            PerformancePreset mode = ActivePreset;
            bool aggressive = IsAggressive(mode, aggressiveOn);
            bool useStrict = (strictCoreOn || mode == PerformancePreset.Competitive)
                && CpuTopology.HasSafeBackgroundPartition();
            dpcSampler.Sample();
            ulong avoidMask = useStrict && aggressive ? dpcSampler.NoisyPhysicalMask : 0;
            ulong desiredMask = (useStrict ? strictMask : gameMask) & ~avoidMask;
            if (desiredMask == 0) desiredMask = useStrict ? strictMask : gameMask;
            int rendererPid = -1;
            lock (sync)
                if (activeDetection != null && activeDetection.RendererCandidateSelected)
                    rendererPid = activeDetection.RendererPid;
            bool staleBoost = false;
            lock (sync)
                foreach (int boostedPid in gameBoost.Keys)
                    if (boostedPid != rendererPid) { staleBoost = true; break; }
            if (staleBoost) UnboostGames();
            foreach (Process p in all)
            {
                try
                {
                    int pid = p.Id;
                    live.Add(pid);
                    if (rendererPid <= 0 || pid != rendererPid) continue;
                    bool known, retryEco, needTweak, needPlacement;
                    lock (sync)
                    {
                        known = gameBoost.ContainsKey(pid);
                        retryEco = boostFail.ContainsKey(pid);
                        needTweak = (gpuHighPerf || disableFso) && !tweakApplied.Contains(pid);
                        ulong placed; bool placedStrict;
                        needPlacement = !gamePlacement.TryGetValue(pid, out placed) || placed != desiredMask
                            || !gamePlacementStrict.TryGetValue(pid, out placedStrict) || placedStrict != useStrict;
                    }
                    IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        bool firstDeny;
                        lock (sync) firstDeny = boostDenied.Add(pid);
                        if (firstDeny) Logger.Log("游戏提优：" + p.ProcessName + " (pid " + pid + ") 打不开句柄（多半被反作弊保护），本体提优跳过；后台压制不受影响");
                        continue;
                    }
                    try
                    {
                        string img = Native.ImageName(h);
                        if (img != null && !string.Equals(img, p.ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                        long currentCreation, currentCpu; ulong currentDisk;
                        if (!Native.QueryProcessSample(h, out currentCreation, out currentCpu, out currentDisk))
                        {
                            Logger.Log("游戏提优：无法读取 " + p.ProcessName + " (pid " + pid + ") 的创建时间，已按安全边界跳过");
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
                                Logger.Log("游戏提优：无法读取原 CPU Sets，已按安全边界跳过 " + p.ProcessName + " (pid " + pid + ")");
                                continue;
                            }
                            int oio = Native.QueryIoPriority(h);
                            int opg = Native.QueryPagePriority(h);
                            int gpuOld;
                            bool gpuKnown = Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuOld) == 0;
                            if (!gpuKnown) gpuOld = -1;
                            // 游戏进程也可能自带 PowerThrottling 设置，提优会覆盖它，还原必须写回原值
                            int oqc, oqs;
                            if (!Native.TryQueryPowerThrottling(h, out oqc, out oqs)) { oqc = -1; oqs = -1; }
                            var snap = new Snap { Pri = pri, Aff = oaff, Io = oio, Pg = opg,
                                Name = p.ProcessName, Creation = currentCreation, CpuSets = ocpuSets,
                                QoSControl = oqc, QoSState = oqs };
                            lock (sync) gameBoost[pid] = snap;
                            if (!CrashGuard.MarkBoostProcess(pid, currentCreation, p.ProcessName, pri, oaff,
                                oio, opg, gpuOld, ocpuSets, oqc, oqs))
                            {
                                lock (sync) gameBoost.Remove(pid);
                                Logger.Log("游戏提优：崩溃恢复快照无法持久化，已取消修改 " + p.ProcessName + " (pid " + pid + ")");
                                continue;
                            }
                            newlyTracked = true;
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
                        bool firstVerified = false, firstStateWarning = false;
                        lock (sync)
                        {
                            if (stateOk)
                            {
                                firstVerified = boostStateVerified.Add(pid);
                                boostStateWarned.Remove(pid);
                            }
                            else
                            {
                                boostStateVerified.Remove(pid);
                                firstStateWarning = boostStateWarned.Add(pid);
                            }
                        }
                        if (!stateOk && firstStateWarning)
                            Logger.Log("游戏提优失败：" + p.ProcessName + " (pid " + pid + ") 回读仍为优先级 0x"
                                + actualPriority.ToString("X") + " / IO " + actualIo + "，错误 " + writeError + "；下一轮继续纠偏");

                        string placementText = "";
                        if (needPlacement)
                        {
                            Snap original;
                            lock (sync) { if (!gameBoost.TryGetValue(pid, out original)) continue; }

                            bool placementOk = Native.RestoreCpuSetsVerified(h, original.CpuSets);
                            if (!CpuTopology.MultiGroup)
                                placementOk &= Native.SetProcessAffinityMask(h, (UIntPtr)(original.Aff != 0 ? original.Aff : allMask));
                            uint[] ids = CpuTopology.AdaptiveGameCpuSetIds(useStrict, avoidMask);
                            bool soft = false;
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
                            else { placementText = " + 不限核"; placementOk = placementOk && !useStrict; }
                            if (soft) placementOk = true;
                            lock (sync)
                            {
                                if (placementOk) { gamePlacement[pid] = desiredMask; gamePlacementStrict[pid] = useStrict; }
                                else { gamePlacement.Remove(pid); gamePlacementStrict.Remove(pid); }
                            }
                            if (!placementOk) Logger.Log("游戏核心策略未完整生效：" + p.ProcessName + " (pid " + pid + ")，下一轮重试");

                            if (!newlyTracked)
                                Logger.Log("游戏核心策略：" + p.ProcessName + " (pid " + pid + ")" + placementText);
                        }

                        if (stateOk && firstVerified)
                        {
                            ReportBoostVerified();
                            Logger.Log("游戏提优已验证：" + p.ProcessName + " (pid " + pid + ") → 高优先级(回读 0x"
                                + actualPriority.ToString("X") + ")" + placementText + " + 高IO(回读 " + actualIo + ")"
                                + (gpuOk ? " + GPU高" : ""));
                        }

                        if (needTweak)
                        {
                            GameExeTweaks.ApplyForGame(Native.ImagePath(h), gpuHighPerf, disableFso);
                            lock (sync) tweakApplied.Add(pid);
                        }

                        if (newlyTracked || retryEco)
                        {
                            bool okEco = Native.ApplyHighQoS(h, Native.OsBuild() >= 22000);
                            if (okEco) { lock (sync) boostFail.Remove(pid); }
                            else
                            {
                                int tries;
                                lock (sync)
                                {
                                    boostFail.TryGetValue(pid, out tries); tries++;
                                    if (tries >= BoostRetryMax) boostFail.Remove(pid); else boostFail[pid] = tries;
                                }
                                if (tries >= BoostRetryMax)
                                    Logger.Log("游戏提优：" + p.ProcessName + " (pid " + pid + ") 效率模式清不掉，重试 " + tries + " 次后放弃（多半被反作弊句柄保护，压不动）");
                            }
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
                    }
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
            List<KeyValuePair<int, Snap>> boosts;
            Dictionary<int, int> gpus;
            lock (sync)
            {
                if (gameBoost.Count == 0 && gameGpu.Count == 0) return true;
                boosts = new List<KeyValuePair<int, Snap>>(gameBoost);
                gpus = new Dictionary<int, int>(gameGpu);
                boostFail.Clear(); boostDenied.Clear(); boostStateWarned.Clear(); boostStateVerified.Clear(); tweakApplied.Clear();
            }
            foreach (var kv in boosts)
            {
                int pid = kv.Key;
                bool done;
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                {
                    IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    done = hq == IntPtr.Zero;
                    if (!done)
                    {
                        try
                        {
                            string name = Native.ImageName(hq);
                            long creation, cpu; ulong disk;
                            bool same = string.Equals(name, kv.Value.Name, StringComparison.OrdinalIgnoreCase)
                                && Native.QueryProcessSample(hq, out creation, out cpu, out disk)
                                && creation == kv.Value.Creation;
                            done = !same;
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
                        bool identity = cur != null && string.Equals(cur, kv.Value.Name, StringComparison.OrdinalIgnoreCase)
                            && Native.QueryProcessSample(h, out creation, out cpu, out disk)
                            && creation == kv.Value.Creation;
                        if (identity)
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
                    lock (sync) { gameBoost.Remove(pid); gameGpu.Remove(pid); gamePlacement.Remove(pid); gamePlacementStrict.Remove(pid); }
                }
            }
            lock (sync) return gameBoost.Count == 0;
        }

        // 带请求序号：上一次超时后工作线程可能还在跑，它完成时不能去满足新的请求，
        // 否则界面会为一次根本没执行的恢复报告成功。
        public bool PanicRestore()
        {
            int mine = Interlocked.Increment(ref panicSeq);
            panicDone.Reset();
            panicResult = false;
            panicReq = true;
            kick.Set();
            // 必须等到 worker 明确报告"我服务的就是这一次请求"。若被上一次超时请求的
            // 完成信号提前唤醒，就继续等，直到轮到自己或真正超时。
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

            bool clean = UnboostGames();
            List<int> background = core.PidsWith(SuppressReason.Background);
            int ok = core.ReleaseReason(SuppressReason.Background);
            int thawed = freezer.RestoreAll();
            bool backgroundClean = true;
            foreach (int pid in background) if (core.IsThrottled(pid)) { backgroundClean = false; break; }
            bool freezeClean = freezer.Count == 0;
            bool envClean = RestoreEnv();
            interference.Clear();
            pressure.Clear();
            if (clean) CrashGuard.ClearBoost();
            Logger.Log("游戏模式解除（" + reason + "）：恢复 " + ok + " 个进程" + (thawed > 0 ? "，解冻 " + thawed + " 个高干扰后台" : ""));
            ReportFinish();
            lock (sync) activeDetection = null;
            ClearSticky();
            return clean && envClean && backgroundClean && freezeClean;
        }

    }
}
