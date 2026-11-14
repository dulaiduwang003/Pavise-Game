using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void Boost(ProcessSnapshot all)
        {
            if (FrameCoordinationEnabled || EffLane) RefreshThreadBoostRecoveryAdmission();
            BoostPass boostPass = PrepareBoostPass();
            SyncFrameCoordination(boostPass);
            if (boostPass.Exclusive)
            {
                boostPass.DesiredMask = coreGameMask;
            }
            DropStaleBoosts(boostPass);
            RunIndependentCorePlacement(boostPass);
            if (all.ByPid.Count == all.Entries.Length)
            {
                ProcEntry renderer;
                if (all.ByPid.TryGetValue(boostPass.RendererPid, out renderer))
                    BoostEntry(renderer, boostPass, null);
                PruneDeadBoosts(all.ByPid.Keys);
                return;
            }
            // 异常快照包含重复 PID 时，仍保留条目的原顺序。
            HashSet<int> hashSet = new HashSet<int>();
            ProcEntry[] entries = all.Entries;
            foreach (ProcEntry procEntry in entries)
                BoostEntry(procEntry, boostPass, hashSet);
            PruneDeadBoosts(hashSet);
        }
        private void BoostEntry(ProcEntry procEntry, BoostPass boostPass, HashSet<int> live)
        {
            try
            {
                int pid = procEntry.Pid;
                if (live != null) live.Add(pid);
                bool known;
                bool needTweak;
                bool needPlacement;
                if (boostPass.RendererPid <= 0 || pid != boostPass.RendererPid || !ComputeAuditDue(pid, boostPass, out known, out needTweak, out needPlacement))
                {
                    return;
                }
                IntPtr intPtr = OpenBoostHandle(pid, boostPass);
                if (intPtr == IntPtr.Zero)
                {
                    return;
                }
                try
                {
                    long currentCreation;
                    if (VerifyRendererIdentity(intPtr, pid, boostPass, out currentCreation) && RefreshBoostPriority(boostPass))
                    {
                        HandlePidReuse(pid, currentCreation, ref known, ref needPlacement);
                        bool newlyTracked;
                        bool gpuOk;
                        bool stateOk;
                        bool firstVerified;
                        if (CaptureAndTrack(intPtr, pid, currentCreation, boostPass, known, out newlyTracked, out gpuOk) && ApplyBoostStateStage(intPtr, pid, boostPass, needTweak, out stateOk, out firstVerified))
                        {
                            bool isolationVerified = boostPass.Exclusive && coreEngaged && coreAffinityVerified;
                            string placementText = (isolationVerified ? (Lang.T("t.gamemodeboost.18") + CpuTopology.CountSetBits(boostPass.DesiredMask) + Lang.T("t.gamemodeboost.16")) : Lang.T("t.gamemodeboost.17"));
                            bool ecoCleared = ClearEfficiencyMode(intPtr, pid, boostPass);
                            EngageLaneAndReport(intPtr, pid, currentCreation, boostPass, stateOk, firstVerified, gpuOk, ecoCleared, placementText, isolationVerified);
                            ApplyGameTweaks(intPtr, pid, boostPass, needTweak);
                        }
                    }
                }
                finally
                {
                    Native.CloseHandle(intPtr);
                }
            }
            catch (Exception error)
            {
                // 单个动作失败不打断本轮，重复异常由日志限频。
                Logger.RepeatedError("game-boost-pass", "游戏提优本轮未完成： " + error.GetType().Name + " " + error.Message);
            }
        }
        private BoostPass PrepareBoostPass()
        {
            BoostPass boostPass = new BoostPass();
            PolicySnapshot policySnapshot = sessionPolicy;
            PrepareResourcePriorities(boostPass, policySnapshot);
            boostPass.NvMaxPerf = ((policySnapshot != null) ? policySnapshot.NvMaxPerf : nvMaxPerf);
            boostPass.NvLowLat = ((policySnapshot != null) ? policySnapshot.NvLowLatMode : nvLowLatMode);
            boostPass.NvSmooth = ((policySnapshot != null) ? policySnapshot.NvSmoothMotion : nvSmoothMotion);
            boostPass.NvShader = ((policySnapshot != null) ? policySnapshot.NvShaderCacheMax : nvShaderCacheMax);
            boostPass.NvRebar = ((policySnapshot != null) ? policySnapshot.NvRebar : nvRebarOn);
            boostPass.NvDlss = ((policySnapshot != null) ? policySnapshot.NvDlssMode : nvDlssMode);
            boostPass.NvVrr = nvVrrWindowedOn;
            NvGamePlan nvGamePlan = RefreshAutomaticNvPlan(boostPass.NvMaxPerf, boostPass.NvShader, Native.OnAcPower(), boostPass.NvLowLat, boostPass.NvVrr, boostPass.NvRebar);
            boostPass.NvMaxPerf = nvGamePlan.MaxPerf;
            boostPass.RendererPid = -1;
            boostPass.RendererCreation = 0L;
            boostPass.RendererName = null;
            lock (sync)
            {
                if (activeDetection != null && activeDetection.RendererCandidateSelected)
                {
                    boostPass.RendererPid = activeDetection.RendererPid;
                    boostPass.RendererCreation = activeDetection.RendererCreation;
                    boostPass.RendererName = activeDetection.RendererName;
                }
            }
            // 名单只保留历史观测；本次进程能力由当前句柄核验，各动作仍使用原有有限失败预算。
            boostPass.WriteDenied = false;
            RenderLane.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), boostPass.RendererPid, boostPass.RendererCreation, "lane:" + EffLane);
            StepAutoCores(boostPass);
            AutoCorePlan autoCorePlan = corePlan;
            AutoCoreGovernor autoCoreGovernor = coreGovernor;
            boostPass.Exclusive = !coreObservationOnly && !CorePending && autoCoreGovernor != null && autoCoreGovernor.Exclusive && autoCorePlan != null && autoCorePlan.Available;
            boostPass.DesiredMask = (boostPass.Exclusive ? coreGameMask : allMask);
            ResolvePriorityTarget(boostPass);
            return boostPass;
        }
        internal static uint BoostPriorityTarget()
        {
            return Native.HIGH_PRIORITY_CLASS;
        }
        private void ResolvePriorityTarget(BoostPass pass)
        {
            if (pass.RendererPid != boostIdentityPid || pass.RendererCreation != boostIdentityCreation)
            {
                ResetBoostIdentity();
            }
            boostIdentityPid = pass.RendererPid;
            boostIdentityCreation = pass.RendererCreation;
            pass.IdentityGeneration = boostIdentityGeneration;
            if (boostIdentityNeedsAudit && pass.RendererPid > 0)
            {
                lock (sync)
                {
                    boostStateVerified.Remove(pass.RendererPid);
                    gameBoostNextAudit.Remove(pass.RendererPid);
                    boostIdentityNeedsAudit = false;
                }
            }
            RefreshBoostPriority(pass);
        }
        private void ResetBoostIdentity()
        {
            boostIdentityPid = 0;
            boostIdentityCreation = 0L;
            boostIdentityGeneration++;
            boostIdentityNeedsAudit = true;
            boostLaneReleased = false;
        }
        private bool RefreshBoostPriority(BoostPass pass)
        {
            if (pass.IdentityGeneration != boostIdentityGeneration || pass.RendererPid <= 0 || pass.RendererCreation <= 0 || pass.RendererPid != boostIdentityPid || pass.RendererCreation != boostIdentityCreation)
            {
                return false;
            }
            // 旧备用提优只保留恢复入口，角色提优统一由当前游戏调度计划决定。
            pass.LaneAllowed = false;
            if (!pass.LaneAllowed)
            {
                if (!boostLaneReleased)
                {
                    boostLaneReleased = RenderLane.Release();
                }
            }
            else
            {
                boostLaneReleased = false;
            }
            pass.PriorityTarget = BoostPriorityTarget();
            return true;
        }
        private void DropStaleBoosts(BoostPass pass)
        {
            bool boostsRemoved = false;
            lock (sync)
            {
                foreach (KeyValuePair<int, Snap> item in gameBoost)
                {
                    if (item.Key != pass.RendererPid || item.Value.Creation != pass.RendererCreation || !string.Equals(item.Value.Name, pass.RendererName, StringComparison.OrdinalIgnoreCase))
                    {
                        boostsRemoved = true;
                        break;
                    }
                }
            }
            if (boostsRemoved)
            {
                UnboostGames(pass.RendererPid, pass.RendererCreation, pass.RendererName);
            }
        }
        private IntPtr OpenBoostHandle(int pid, BoostPass pass)
        {
            IntPtr intPtr = Native.OpenProcess(12800, false, pid);
            if (intPtr == IntPtr.Zero)
            {
                int openError = Marshal.GetLastWin32Error();
                bool wasAdded;
                lock (sync)
                {
                    wasAdded = boostDenied.Add(pid);
                }
                if (wasAdded)
                {
                    Logger.Log(Lang.T("log.gamemodeboost.3", 0) + pass.RendererName + " 进程编号 " + pid + Lang.T("log.gamemodeboost.4", 0)
                        + "；阶段=OpenProcess requested=0x3200 win32=" + openError);
                }
                if (openError == 5)
                {
                    ProtectedGameRoster.Remember(pass.RendererName);
                }
            }
            return intPtr;
        }
        private bool ApplyBoostStateStage(IntPtr h, int pid, BoostPass pass, bool needTweak, out bool stateOk, out bool firstVerified)
        {
            firstVerified = false;
            if (pass.WriteDenied)
            {
                stateOk = false;
                lock (sync)
                {
                    boostStateVerified.Remove(pid);
                    gameBoostNextAudit[pid] = DateTime.UtcNow.AddSeconds(20 + Math.Abs(pid % 11)).Ticks;
                }
                return true;
            }
            // 游戏自己已是 Realtime 时保留原类别，不往下改成 High。
            bool applySelectedBoostActionSucceeded = ApplySelectedBoostAction(h, pid, pass, "GamePriority", pass.CpuBoost, (long value) => value == pass.PriorityTarget || value == 0x100, () => WriteBoostAction("GamePriority", h, pass.PriorityTarget));
            bool applySelectedBoostActionSucceeded2 = ApplySelectedBoostAction(h, pid, pass, "GameIoPriority", pass.IoBoost, (long value) => value == 3, () => WriteBoostAction("GameIoPriority", h, 3L));
            stateOk = applySelectedBoostActionSucceeded && applySelectedBoostActionSucceeded2;
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
                }
                boostStateFail.Remove(pid);
                gameBoostNextAudit[pid] = DateTime.UtcNow.AddSeconds(20 + Math.Abs(pid % 11)).Ticks;
            }
            return true;
        }
        private bool UnboostGames()
        {
            return UnboostGames(0, 0L, null);
        }
        private static bool IsKeptBoost(KeyValuePair<int, Snap> boosted, int keepPid, long keepCreation, string keepName)
        {
            if (keepPid > 0 && boosted.Key == keepPid && boosted.Value.Creation == keepCreation)
            {
                return string.Equals(boosted.Value.Name, keepName, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        private bool UnboostGames(int keepPid, long keepCreation, string keepName)
        {
            bool releaseExceptSucceeded = RenderLane.ReleaseExcept(keepPid, keepCreation);
            releaseExceptSucceeded &= FrameCoordination.ReleaseExcept(keepPid, keepCreation);
            bool refinementSettled = (keepPid == coreWatchPid && keepCreation == coreWatchCreation) || StopCoreRefinement();
            bool isolationSettled = keepPid > 0 || StopCoreIsolation();
            bool placementOwned = coreEngaged && !corePinPending;
            bool restoreHardPinsExceptSucceeded = RestoreHardPinsExcept(placementOwned ? keepPid : 0, placementOwned ? keepCreation : 0);
            corePinPending = !restoreHardPinsExceptSucceeded;
            isolationSettled = isolationSettled && refinementSettled && releaseExceptSucceeded && restoreHardPinsExceptSucceeded;
            List<KeyValuePair<int, Snap>> list;
            lock (sync)
            {
                if (gameBoost.Count == 0 && gameGpu.Count == 0)
                {
                    return isolationSettled;
                }
                list = new List<KeyValuePair<int, Snap>>();
                foreach (KeyValuePair<int, Snap> item in gameBoost)
                {
                    if (!IsKeptBoost(item, keepPid, keepCreation, keepName))
                    {
                        list.Add(item);
                    }
                }
                Dictionary<int, int> dictionary = new Dictionary<int, int>();
                foreach (KeyValuePair<int, int> item2 in gameGpu)
                {
                    if (keepPid <= 0 || item2.Key != keepPid)
                    {
                        dictionary[item2.Key] = item2.Value;
                    }
                }
                if (list.Count == 0 && dictionary.Count == 0)
                {
                    return isolationSettled;
                }
                if (keepPid <= 0)
                {
                    boostFail.Clear();
                    boostDenied.Clear();
                    boostStateWarned.Clear();
                    boostStateVerified.Clear();
                    gameBoostNextAudit.Clear();
                    ForgetNvTweaksLocked();
                    boostHandleStripped.Clear();
                    boostEcoGaveUp.Clear();
                    placementFail.Clear();
                    placementGaveUp.Clear();
                    boostStateFail.Clear();
                }
                else
                {
                    foreach (KeyValuePair<int, Snap> item3 in list)
                    {
                        boostFail.Remove(item3.Key);
                        boostDenied.Remove(item3.Key);
                        boostStateWarned.Remove(item3.Key);
                        boostStateVerified.Remove(item3.Key);
                        gameBoostNextAudit.Remove(item3.Key);
                        ForgetNvTweakLocked(item3.Key);
                        boostHandleStripped.Remove(item3.Key);
                        boostEcoGaveUp.Remove(item3.Key);
                        placementFail.Remove(item3.Key);
                        placementGaveUp.Remove(item3.Key);
                        boostStateFail.Remove(item3.Key);
                    }
                }
            }
            foreach (KeyValuePair<int, Snap> item4 in list)
            {
                RestoreHardPin(item4.Key);
            }
            foreach (KeyValuePair<int, Snap> item5 in list)
            {
                int key = item5.Key;
                bool runBoostRecoverySucceeded = RunBoostRecovery(key, item5.Value.Creation, "snapshot", () => RestoreBoostProcess(key, item5.Value));
                if (runBoostRecoverySucceeded)
                {
                    lock (coreIsolationGate)
                    if ((criticalBorrowsCoreIsolation && criticalCoreIsolationPid == key && criticalCoreIsolationBirth == item5.Value.Creation)
                        || (!criticalOwnsCoreIsolation && coreIsolation != null && !coreIsolation.Drop(key, item5.Value.Creation)))
                    {
                        isolationSettled = false;
                    }
                    ForgetHardPin(key);
                    CrashGuard.ReleaseBoostProcess(key, item5.Value.Creation);
                    lock (sync)
                    {
                        gameBoost.Remove(key);
                        gameGpu.Remove(key);
                        gamePlacement.Remove(key);
                        gameBoostNextAudit.Remove(key);
                        ForgetBoostActionTarget(key);
                    }
                }
            }
            lock (sync)
            {
                return isolationSettled && gameBoost.Count == 0;
            }
        }
        internal static void CurrentPlacement(IntPtr h, ref ulong affinity, ref uint[] cpuSets)
        {
            ulong mask;
            if (!CpuTopology.MultiGroup && Native.TryQueryAffinity(h, out mask) && mask != 0)
            {
                affinity = mask;
            }
            uint[] cpuSets2 = Native.QueryCpuSets(h);
            if (cpuSets2 != null)
            {
                cpuSets = cpuSets2;
            }
        }
        private static BoostTarget ProbeBoostTarget(int pid, long creation)
        {
            IntPtr intPtr = Native.OpenProcess(4096, false, pid);
            if (intPtr == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() != 87)
                {
                    return BoostTarget.Unopenable;
                }
                return BoostTarget.Gone;
            }
            try
            {
                long creation2;
                long cpu;
                ulong io;
                if (!Native.QueryProcessSample(intPtr, out creation2, out cpu, out io))
                {
                    return BoostTarget.Unopenable;
                }
                if (creation > 0 && creation2 != creation)
                {
                    return BoostTarget.Gone;
                }
                return (!Native.StillActive(intPtr)) ? BoostTarget.Gone : BoostTarget.Alive;
            }
            finally
            {
                Native.CloseHandle(intPtr);
            }
        }
        internal int DropVanishedBoosts()
        {
            List<KeyValuePair<int, Snap>> list = new List<KeyValuePair<int, Snap>>();
            lock (sync)
            {
                foreach (KeyValuePair<int, Snap> item in gameBoost)
                {
                    list.Add(item);
                }
            }
            if (list.Count == 0)
            {
                return 0;
            }
            int processedCount = 0;
            int processedCount2 = 0;
            foreach (KeyValuePair<int, Snap> item2 in list)
            {
                BoostTarget boostTarget = ProbeBoostTarget(item2.Key, item2.Value.Creation);
                if (boostTarget == BoostTarget.Alive)
                {
                    lock (sync)
                    {
                        boostVanishTries.Remove(item2.Key);
                    }
                    continue;
                }
                bool unopenable = boostTarget == BoostTarget.Unopenable;
                if (unopenable)
                {
                    int value;
                    lock (sync)
                    {
                        boostVanishTries.TryGetValue(item2.Key, out value);
                        value++;
                        boostVanishTries[item2.Key] = value;
                    }
                    if (value < 20)
                    {
                        continue;
                    }
                    processedCount2++;
                }
                if (RestoreHardPin(item2.Key))
                {
                    if (!unopenable)
                    {
                        CrashGuard.ReleaseBoostProcess(item2.Key, item2.Value.Creation);
                    }
                    lock (sync)
                    {
                        boostVanishTries.Remove(item2.Key);
                        gameBoost.Remove(item2.Key);
                        gameGpu.Remove(item2.Key);
                        gamePlacement.Remove(item2.Key);
                        boostFail.Remove(item2.Key);
                        boostStateWarned.Remove(item2.Key);
                        boostStateVerified.Remove(item2.Key);
                        boostHandleStripped.Remove(item2.Key);
                        boostEcoGaveUp.Remove(item2.Key);
                        placementGaveUp.Remove(item2.Key);
                        placementFail.Remove(item2.Key);
                        boostStateFail.Remove(item2.Key);
                        gameBoostNextAudit.Remove(item2.Key);
                        boostDenied.Remove(item2.Key);
                        ForgetNvTweakLocked(item2.Key);
                        ForgetBoostActionTarget(item2.Key);
                    }
                    processedCount++;
                }
            }
            if (processedCount2 > 0)
            {
                Logger.Warn(Lang.T("log.gamemodeboost.58", 0) + processedCount);
            }
            else if (processedCount > 0)
            {
                Logger.Log(Lang.T("log.gamemodeboost.57", 0) + processedCount);
            }
            return processedCount;
        }
        private void PruneDeadBoosts(ICollection<int> live)
        {
            lock (sync)
            {
                boostDenied.RemoveWhere((int x) => !live.Contains(x));
                boostStateWarned.RemoveWhere((int x) => !live.Contains(x));
                boostStateVerified.RemoveWhere((int x) => !live.Contains(x));
                boostHandleStripped.RemoveWhere((int x) => !live.Contains(x));
                boostEcoGaveUp.RemoveWhere((int x) => !live.Contains(x));
                placementGaveUp.RemoveWhere((int x) => !live.Contains(x));
                tweakApplied.RemoveWhere((int x) => !live.Contains(x));
                List<int> list = null;
                foreach (int key in gameBoost.Keys)
                {
                    if (!live.Contains(key))
                    {
                        if (list == null)
                        {
                            list = new List<int>();
                        }
                        list.Add(key);
                    }
                }
                if (list == null)
                {
                    return;
                }
                foreach (int item in list)
                {
                    // 复用或不完整的进程快照不能证明目标已退出。
                    if (ProbeBoostTarget(item, gameBoost[item].Creation) != BoostTarget.Gone) continue;
                    if (RestoreHardPin(item))
                    {
                        CrashGuard.ReleaseBoostProcess(item, gameBoost[item].Creation);
                        gameBoost.Remove(item);
                        gameGpu.Remove(item);
                        gamePlacement.Remove(item);
                        boostFail.Remove(item);
                        boostStateWarned.Remove(item);
                        boostStateVerified.Remove(item);
                        gameBoostNextAudit.Remove(item);
                        placementFail.Remove(item);
                        boostStateFail.Remove(item);
                        ForgetBoostActionTarget(item);
                    }
                }
            }
        }
        internal static string QoSDump(IntPtr process)
        {
            int controlMask;
            int stateMask;
            if (!Native.TryQueryPowerThrottling(process, out controlMask, out stateMask))
            {
                return Lang.T("t.gamemodeboost.37");
            }
            return "(control=0x" + controlMask.ToString("X") + " state=0x" + stateMask.ToString("X") + ")";
        }
        internal static bool HighQoSVerified(IntPtr process)
        {
            if (!Native.PowerThrottlingSupported)
            {
                return true;
            }
            int controlMask;
            int stateMask;
            if (!Native.TryQueryPowerThrottling(process, out controlMask, out stateMask))
            {
                return false;
            }
            return Native.HighQoSMasksOk(controlMask, stateMask);
        }
        private static bool CpuTicks(int pid, out long ticks, out long creation)
        {
            ticks = 0L;
            creation = 0L;
            IntPtr intPtr = Native.OpenProcess(4096, false, pid);
            if (intPtr == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                long exit;
                long kernel;
                long user;
                if (!GetProcessTimes(intPtr, out creation, out exit, out kernel, out user))
                {
                    return false;
                }
                ticks = kernel + user;
                return true;
            }
            finally
            {
                Native.CloseHandle(intPtr);
            }
        }
        private static long CurrentProcessCpuTicks()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    return process.TotalProcessorTime.Ticks;
                }
            }
            catch
            {
                return 0L;
            }
        }
        internal static double AverageCpuPercent(long cpuTicks, TimeSpan duration)
        {
            if (cpuTicks <= 0 || duration.Ticks <= 0)
            {
                return 0.0;
            }
            int processorCountLimit = Math.Max(1, Environment.ProcessorCount);
            double cpuPercent = (double)cpuTicks * 100.0 / ((double)duration.Ticks * (double)processorCountLimit);
            return Math.Max(0.0, Math.Min(100.0, cpuPercent));
        }

    }
}
