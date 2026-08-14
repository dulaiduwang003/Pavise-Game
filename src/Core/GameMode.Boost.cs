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
        private sealed class BoostPass
        {
            public bool NvMaxPerf;
            public string NvLowLat;
            public string NvFrl;
            public bool NvSmooth;
            public bool NvShader;
            public bool NvAnsel;
            public bool NvRebar;
            public string NvDlss;
            public bool NvBatt;
            public bool UseStrict;
            public ulong DesiredMask;
            public int RendererPid;
            public long RendererCreation;
            public string RendererName;
            public string RendererPath;
            public string RendererProfileId;
            public bool RendererLearnable;
            public uint PriorityTarget;
        }

        private void Boost(ProcessSnapshot all)
        {
            var live = new HashSet<int>();
            BoostPass pass = PrepareBoostPass();
            DropStaleBoosts(pass);
            ResolvePriorityTarget(pass);
            foreach (ProcEntry p in all.Entries)
            {
                try
                {
                    int pid = p.Pid;
                    live.Add(pid);
                    if (pass.RendererPid <= 0 || pid != pass.RendererPid) continue;
                    bool known, needTweak, needPlacement;
                    if (!ComputeAuditDue(pid, pass, out known, out needTweak, out needPlacement)) continue;
                    IntPtr h = OpenBoostHandle(pid, pass);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        long currentCreation;
                        if (!VerifyRendererIdentity(h, pid, pass, out currentCreation)) continue;
                        HandlePidReuse(pid, currentCreation, ref known, ref needPlacement);
                        bool newlyTracked, gpuOk;
                        if (!CaptureAndTrack(h, pid, currentCreation, pass, known, out newlyTracked, out gpuOk)) continue;
                        bool stateOk, firstVerified;
                        if (!ApplyBoostStateStage(h, pid, pass, needTweak, out stateOk, out firstVerified)) continue;
                        string placementText;
                        if (!ApplyPlacementStage(h, pid, pass, needPlacement, newlyTracked, out placementText)) continue;
                        bool ecoCleared = ClearEfficiencyMode(h, pid, pass);
                        EngageLaneAndReport(h, all, pid, currentCreation, pass, stateOk, firstVerified, gpuOk, ecoCleared, placementText);
                        ApplyGameTweaks(h, pid, pass, needTweak);
                    }
                    finally { Native.CloseHandle(h); }
                }
                catch { }
            }
            PruneDeadBoosts(live);
        }

        private BoostPass PrepareBoostPass()
        {
            var pass = new BoostPass();
            PolicySnapshot sp = sessionPolicy;
            pass.NvMaxPerf = sp != null ? sp.NvMaxPerf : nvMaxPerf;
            pass.NvLowLat = sp != null ? sp.NvLowLatMode : nvLowLatMode;
            pass.NvFrl = sp != null ? sp.NvFrlMode : nvFrlMode;
            pass.NvSmooth = sp != null ? sp.NvSmoothMotion : nvSmoothMotion;
            pass.NvShader = sp != null ? sp.NvShaderCacheMax : nvShaderCacheMax;
            pass.NvAnsel = sp != null ? sp.NvAnselOff : nvAnselOff;
            pass.NvRebar = sp != null ? sp.NvRebar : nvRebarOn;
            pass.NvDlss = sp != null ? sp.NvDlssMode : nvDlssMode;
            pass.NvBatt = sp != null ? sp.NvBattFull : nvBattFull;
            ulong customMask = CpuTopology.CustomMask;
            pass.UseStrict = customMask != 0
                || ShouldUseCorePartition(sp != null ? sp.StrictCores : corePartitionOn,
                    CpuTopology.HasSafeBackgroundPartition());
            pass.DesiredMask = customMask != 0 ? customMask : pass.UseStrict ? strictMask : gameMask;
            pass.RendererPid = -1;
            pass.RendererCreation = 0;
            pass.RendererName = null;
            pass.RendererPath = null;
            pass.RendererProfileId = null;
            pass.RendererLearnable = false;
            lock (sync)
                if (activeDetection != null && activeDetection.RendererCandidateSelected)
                {
                    pass.RendererPid = activeDetection.RendererPid;
                    pass.RendererCreation =
                        activeDetection.RendererCreation;
                    pass.RendererName =
                        activeDetection.RendererName;
                    pass.RendererPath =
                        activeDetection.RendererPath;
                    pass.RendererProfileId =
                        activeDetection.Profile != null
                            ? activeDetection.Profile.Id : null;
                    pass.RendererLearnable =
                        activeDetection.RendererLearnable;
                }
            return pass;
        }

        private void DropStaleBoosts(BoostPass pass)
        {
            bool staleBoost = false;
            lock (sync)
                foreach (KeyValuePair<int, Snap> boosted
                    in gameBoost)
                    if (boosted.Key != pass.RendererPid
                        || boosted.Value.Creation
                            != pass.RendererCreation
                        || !string.Equals(
                            boosted.Value.Name,
                            pass.RendererName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        staleBoost = true;
                        break;
                    }
            if (staleBoost) UnboostGames(pass.RendererPid, pass.RendererCreation, pass.RendererName);
        }

        private void ResolvePriorityTarget(BoostPass pass)
        {
            bool saturated = cpuSaturation.Update(cpuSaturation.Sample());
            bool laneActive = pass.RendererPid > 0
                && RenderLane.IsActiveFor(pass.RendererPid, pass.RendererCreation);
            uint priorityTarget = BoostPriorityTarget(saturated, laneActive);
            if (priorityTarget != boostPriorityTarget)
            {
                boostPriorityTarget = priorityTarget;
                if (pass.RendererPid > 0)
                {
                    lock (sync)
                    {
                        boostStateVerified.Remove(pass.RendererPid);
                        gameBoostNextAudit.Remove(pass.RendererPid);
                    }
                    Logger.Log(priorityTarget == Native.NORMAL_PRIORITY_CLASS
                        ? "智能保帧 CPU 吃满且帧线程未接管 游戏提优暂回普通优先级 实测该状态下整进程高优先级恶化尾部帧 "
                        : "智能保帧 恢复高优先级提优");
                }
            }
            pass.PriorityTarget = priorityTarget;
        }

        private bool ComputeAuditDue(int pid, BoostPass pass,
            out bool known, out bool needTweak, out bool needPlacement)
        {
            bool retryEco, auditDue, stripped;
            lock (sync)
            {
                stripped = boostHandleStripped.Contains(pid);
                known = gameBoost.ContainsKey(pid);
                retryEco = boostFail.ContainsKey(pid) && !boostEcoGaveUp.Contains(pid);
                needTweak = !tweakApplied.Contains(pid);
                ulong placed; bool placedStrict;
                needPlacement = !placementGaveUp.Contains(pid)
                    && (!gamePlacement.TryGetValue(pid, out placed) || placed != pass.DesiredMask
                        || !gamePlacementStrict.TryGetValue(pid, out placedStrict) || placedStrict != pass.UseStrict);
                long nextAudit;
                auditDue = !known || retryEco || needTweak || needPlacement
                    || !boostStateVerified.Contains(pid)
                    || !gameBoostNextAudit.TryGetValue(pid, out nextAudit)
                    || DateTime.UtcNow.Ticks >= nextAudit;
                if (stripped) auditDue = needTweak;
            }
            return auditDue;
        }

        private IntPtr OpenBoostHandle(int pid, BoostPass pass)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                bool firstDeny;
                lock (sync) firstDeny = boostDenied.Add(pid);
                if (firstDeny) Logger.Log("游戏提优 " + pass.RendererName + " pid " + pid + " 打不开句柄 本体提优跳过 后台压制不受影响");
                if (EffIfeo && EffBoost) IfeoBoost.EnsureForGame(pass.RendererName);
            }
            return h;
        }

        private bool VerifyRendererIdentity(IntPtr h, int pid, BoostPass pass, out long currentCreation)
        {
            string img = Native.ImageName(h);
            long currentCpu; ulong currentDisk;
            if (!Native.QueryProcessSample(h, out currentCreation, out currentCpu, out currentDisk))
            {
                Logger.Log("游戏提优 无法读取 " + pass.RendererName + " pid " + pid + " 的创建时间 已按安全边界跳过");
                return false;
            }
            if (!RendererIdentityMatches(
                    pass.RendererPid, pass.RendererCreation,
                    pass.RendererName, pid,
                    currentCreation, img))
            {
                Logger.Log("游戏提优 renderer 身份已变化 跳过 pid "
                    + pid + " 的全部写入");
                return false;
            }
            return true;
        }

        private void HandlePidReuse(int pid, long currentCreation, ref bool known, ref bool needPlacement)
        {
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
        }

        private bool CaptureAndTrack(IntPtr h, int pid, long currentCreation, BoostPass pass,
            bool known, out bool newlyTracked, out bool gpuOk)
        {
            newlyTracked = false;
            gpuOk = false;
            if (!known)
            {
                uint pri = Native.GetPriorityClass(h);
                if (pri == 0) pri = Native.NORMAL_PRIORITY_CLASS;
                ulong oaff = Native.QueryAffinity(h);
                uint[] ocpuSets = Native.QueryCpuSets(h);
                if (ocpuSets == null)
                {
                    Logger.Log("游戏提优 无法读取原 CPU Sets 已按安全边界跳过 " + pass.RendererName + " pid " + pid);
                    return false;
                }
                int oio = Native.QueryIoPriority(h);
                int opg = Native.QueryPagePriority(h);
                int gpuOld;
                bool gpuKnown = Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuOld) == 0;
                if (!gpuKnown) gpuOld = -1;

                int oqc, oqs;
                if (!Native.TryQueryPowerThrottling(h, out oqc, out oqs)) { oqc = -1; oqs = -1; }
                CrashGuard.OriginalBoostState recovered;
                if (!CrashGuard.MarkBoostProcess(pid, currentCreation, pass.RendererName, pri, oaff,
                    oio, opg, gpuOld, ocpuSets, oqc, oqs, out recovered))
                {
                    Logger.Log("游戏提优 崩溃恢复快照无法持久化 已取消修改 " + pass.RendererName + " pid " + pid);
                    return false;
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
                    Name = pass.RendererName, Creation = currentCreation, CpuSets = ocpuSets,
                    QoSControl = oqc, QoSState = oqs };
                lock (sync) gameBoost[pid] = snap;
                newlyTracked = true;
                if (pass.RendererLearnable)
                    TryLearnRenderer(pass.RendererProfileId, pass.RendererPath, pass.RendererName);
                gpuOk = gpuKnown && ApplyAndVerifyGpuBoost(h);
                lock (sync) { if (gpuKnown) gameGpu[pid] = gpuOld; }
            }
            else
            {
                int ignoredGpu;
                lock (sync) gpuOk = gameGpu.TryGetValue(pid, out ignoredGpu);
                if (gpuOk) gpuOk = ApplyAndVerifyGpuBoost(h);
            }
            return true;
        }

        private bool ApplyBoostStateStage(IntPtr h, int pid, BoostPass pass, bool needTweak,
            out bool stateOk, out bool firstVerified)
        {
            firstVerified = false;
            uint actualPriority;
            int actualIo, writeError;
            stateOk = ApplyAndVerifyBoostState(h, pass.PriorityTarget, out actualPriority, out actualIo, out writeError);

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
                if (firstStrip) OnGameHandleStripped(pid, pass.RendererName, grantedAccess);
                if (!needTweak) return false;
            }

            bool firstStateWarning = false;
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
                Logger.Log("游戏提优失败 " + pass.RendererName + " pid " + pid + " 回读仍为优先级 0x"
                    + actualPriority.ToString("X") + " / IO " + actualIo + " 错误 " + writeError + " 下一轮继续纠偏");
            return true;
        }

        private bool ApplyPlacementStage(IntPtr h, int pid, BoostPass pass, bool needPlacement,
            bool newlyTracked, out string placementText)
        {
            placementText = "";
            if (needPlacement)
            {
                Snap original;
                lock (sync) { if (!gameBoost.TryGetValue(pid, out original)) return false; }

                bool placementOk = Native.RestoreCpuSetsVerified(h, original.CpuSets);
                if (!CpuTopology.MultiGroup)
                    placementOk &= Native.SetProcessAffinityMask(h, (UIntPtr)(original.Aff != 0 ? original.Aff : allMask));
                uint[] ids = CpuTopology.CustomCpuSetIds()
                    ?? CpuTopology.AdaptiveGameCpuSetIds(pass.UseStrict);
                bool soft = false;
                bool placementUnavailable = false;
                if (pass.UseStrict || pass.DesiredMask != allMask)
                    soft = Native.TrySetCpuSetsVerified(h, ids);
                if (soft)
                {
                    placementText = pass.UseStrict
                        ? " 限定 " + CpuTopology.CountSetBits(pass.DesiredMask) + " 个核"
                        : " 不限核";
                }
                else if (pass.DesiredMask != allMask && !CpuTopology.MultiGroup)
                {
                    Native.RestoreCpuSets(h, original.CpuSets);
                    placementOk = Native.SetProcessAffinityMask(h, (UIntPtr)pass.DesiredMask)
                        && Native.QueryAffinity(h) == pass.DesiredMask;
                    placementText = " 硬绑 " + CpuTopology.CountSetBits(pass.DesiredMask) + " 个核";
                }
                else
                {
                    placementText = " 不限核";
                    if (pass.UseStrict) placementUnavailable = true;
                }
                if (soft) placementOk = true;
                if (placementUnavailable) placementOk = true;
                int placeTries = 0;
                bool placementNowGaveUp = false, firstPlacementWarning = false;
                lock (sync)
                {
                    if (placementOk)
                    {
                        gamePlacement[pid] = pass.DesiredMask; gamePlacementStrict[pid] = pass.UseStrict;
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
                    Logger.Log("游戏核心策略 " + pass.RendererName + " pid " + pid
                        + " 本机无可用核心分区手段 按不限核处理");
                else if (placementNowGaveUp)
                    Logger.Log("游戏核心策略 " + pass.RendererName + " pid " + pid + " 重试 "
                        + PlacementRetryMax + " 次仍未生效 已放弃");
                else if (!placementOk && firstPlacementWarning)
                    Logger.Log("游戏核心策略未完整生效 " + pass.RendererName + " pid " + pid + " 下一轮重试");

                if (!newlyTracked && placementOk)
                    Logger.Log("游戏核心策略 " + pass.RendererName + " pid " + pid + placementText);
            }
            return true;
        }

        private bool ClearEfficiencyMode(IntPtr h, int pid, BoostPass pass)
        {
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
                        Logger.Log("游戏提优 " + pass.RendererName + " pid " + pid + " 效率模式清不掉 重试 " + tries + " 次后放弃");
                }
            }
            return ecoCleared;
        }

        private void EngageLaneAndReport(IntPtr h, ProcessSnapshot all, int pid, long currentCreation,
            BoostPass pass, bool stateOk, bool firstVerified, bool gpuOk, bool ecoCleared, string placementText)
        {
            if (EffLane && stateOk && !RenderLane.IsActiveFor(pid, currentCreation))
                RenderLane.EnsureForGame(pid, currentCreation, pass.RendererName);

            if (stateOk && firstVerified)
            {
                WarnIfPartitionHurtsWideGame(pass.RendererName, all, pid, pass.DesiredMask);
                Logger.Log("游戏提优已生效 " + pass.RendererName + "(pid " + pid + ") "
                    + (pass.PriorityTarget == Native.HIGH_PRIORITY_CLASS ? "高优先级" : "普通优先级 智能保帧降档")
                    + placementText + " 高读写优先级"
                    + (gpuOk ? " 显卡高优先级" : "")
                    + (!Native.PowerThrottlingSupported ? ""
                        : ecoCleared ? " 已退出省电模式" : " 省电模式未清除" + QoSDump(h)));
            }
        }

        private void ApplyGameTweaks(IntPtr h, int pid, BoostPass pass, bool needTweak)
        {
            if (needTweak)
            {
                string imagePath = Native.ImagePath(h);
                GameExeTweaks.ApplyForGame(imagePath, true);
                var nvPlan = new NvGamePlan
                {
                    MaxPerf = pass.NvMaxPerf,
                    FrlFps = ResolveFrlFps(pass.NvFrl),
                    LowLatMode = pass.NvLowLat,
                    SmoothMotion = pass.NvSmooth,
                    ShaderCacheMax = pass.NvShader,
                    AnselOff = pass.NvAnsel,
                    Rebar = pass.NvRebar,
                    DlssMode = pass.NvDlss,
                    BattFull = pass.NvBatt
                };
                if (!nvPlan.Empty)
                {
                    List<string> nvFailed = NvDrsTweaks.ApplyForGame(imagePath, nvPlan);
                    HandleNvTweakOutcome(nvFailed, nvPlan);
                }
                lock (sync) tweakApplied.Add(pid);
            }
        }

        private void PruneDeadBoosts(HashSet<int> live)
        {
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
            Logger.Log("游戏提优 " + rendererName + " pid " + pid + " 句柄写入权限被"
                + (ac == null ? "反作弊" : ac) + "剥离 授予 0x" + granted.ToString("X")
                + " 本体提优已停止 后台压制不受影响");

            if (EffIfeo && EffBoost)
            {
                IfeoBoost.Arm(rendererName);
                IfeoBoost.EnsureForGame(rendererName);
            }
        }

        internal static uint BoostPriorityTarget(bool saturated, bool laneActive)
        {
            return saturated && !laneActive
                ? Native.NORMAL_PRIORITY_CLASS : Native.HIGH_PRIORITY_CLASS;
        }

        internal static bool ApplyAndVerifyBoostState(IntPtr process, out uint actualPriority, out int actualIo, out int error)
        {
            return ApplyAndVerifyBoostState(process, Native.HIGH_PRIORITY_CLASS, out actualPriority, out actualIo, out error);
        }

        internal static bool ApplyAndVerifyBoostState(IntPtr process, uint priorityTarget, out uint actualPriority, out int actualIo, out int error)
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
            if (actualPriority != priorityTarget && !Native.SetPriorityClass(process, priorityTarget))
                error = Marshal.GetLastWin32Error();

            actualPriority = Native.GetPriorityClass(process);
            actualIo = Native.QueryIoPriority(process);
            return actualPriority == priorityTarget && actualIo == 3;
        }

        internal static string QoSDump(IntPtr process)
        {
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state)) return " 读取失败";
            return "(control=0x" + control.ToString("X") + " state=0x" + state.ToString("X");
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
                            if (same) Logger.Log("提优还原 " + kv.Value.Name + " pid " + pid + " 句柄被保护 身份快照保留待重试");
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

        private bool partitionHintLogged;

        private void WarnIfPartitionHurtsWideGame(string name, ProcessSnapshot all, int pid, ulong mask)
        {
            if (partitionHintLogged || all == null) return;
            ProcEntry entry = all.Find(pid);
            if (entry == null) return;
            int given = CpuTopology.CountSetBits(mask);
            int total = CpuTopology.CountSetBits(CpuTopology.AllMask);
            if (!PartitionLikelyHurts(entry.Threads, given, total)) return;
            partitionHintLogged = true;
            Logger.Log("核心分区提示 " + (name ?? "?") + " 有 " + entry.Threads
                + " 个线程 属于负载分散型 当前只给了 " + given + " 个逻辑核 全机共 " + total
                + " 个 削掉 " + (100 - given * 100 / total)
                + "% 的核心 这类游戏很可能反而掉帧 觉得卡就把游戏核心范围调回全核");
        }

        public bool PanicRestore()
        {
            int cleared = SelfProtectedRoster.Clear();
            if (cleared > 0)
                Logger.Log("免压制名单已清空 " + cleared + " 项 下次对局重新探测这些进程");
            int unarmed = IfeoBoost.ClearArmed();
            if (unarmed > 0)
                Logger.Log("内核反作弊预置名单已清空 " + unarmed + " 项 下次对局重新探测这些游戏");
            bool ifeoOk = IfeoBoost.RestoreAll();
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
                Logger.Log("已重置 " + fusesCleared + " 个因写入失败自动停用的环境项 对应开关仍为关 需要请手动打开");
            lock (panicCallGate)
            {
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
                    if (Volatile.Read(ref panicServed) == mine) return panicResult && ifeoOk;
                    panicDone.Reset();
                }
            }
        }

        private bool Deactivate(string reason)
        {
            return Deactivate(reason, false);
        }

        private bool Deactivate(string reason, bool quiet)
        {
            lock (sync)
            {
                active = false;
                activeGame = null;
                firstSweep = true;
            }
            sessionPolicy = null;
            RestoreGlobalCoreMask();
            SuppressionCore.SqueezeBackground = squeezeBgOn;
            SuppressionCore.GpuDemoteEnabled = gpuDemoteOn;
            gameGoneSinceTicks = 0;
            cpuSaturation.Reset();
            partitionHintLogged = false;
            boostPriorityTarget = Native.HIGH_PRIORITY_CLASS;

            bool clean = UnboostGames();
            slowEnvAtTicks = 0;
            uplinkSampleTicks = 0;
            uplinkSampleBytes = 0;
            UploadYield.Clear();
            List<int> background = core.PidsWith(SuppressReason.Background);
            int ok = core.ReleaseReason(SuppressReason.Background);
            bool backgroundClean = true;
            foreach (int pid in background) if (core.IsThrottled(pid)) { backgroundClean = false; break; }
            bool envClean = RestoreEnv();
            ClearEnvRetryState();
            pressure.Clear();
            if (clean) CrashGuard.ClearBoost();
            int restoredTotal = ok + gracePreReleased;
            gracePreReleased = 0;
            if (!quiet || restoredTotal > 0)
                Logger.Log("游戏模式解除 " + reason + " 恢复 " + restoredTotal
                    + " 个后台进程 本局累计 含中途新增与宽限期先行还原");
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
