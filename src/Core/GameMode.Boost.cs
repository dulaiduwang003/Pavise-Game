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
            public bool NvSmooth;
            public bool NvShader;
            public bool NvRebar;
            public string NvDlss;
            public bool UseStrict;
            public ulong DesiredMask;
            public int RendererPid;
            public long RendererCreation;
            public string RendererName;
            public string RendererPath;
            public string RendererProfileId;
            public bool RendererLearnable;
            public bool WriteDenied;
            public uint PriorityTarget;
        }

        private void Boost(ProcessSnapshot all)
        {
            var live = new HashSet<int>();
            BoostPass pass = PrepareBoostPass();
            DropStaleBoosts(pass);
            foreach (ProcEntry p in all.Entries)
            {
                try
                {
                    int pid = p.Pid;
                    live.Add(pid);
                    if (pass.RendererPid <= 0 || pid != pass.RendererPid) continue;
                    if (CfgOffTweak.Enabled) CfgOffTweak.EnsureForGame(pass.RendererName);
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
            pass.NvSmooth = sp != null ? sp.NvSmoothMotion : nvSmoothMotion;
            pass.NvShader = sp != null ? sp.NvShaderCacheMax : nvShaderCacheMax;
            pass.NvRebar = sp != null ? sp.NvRebar : nvRebarOn;
            pass.NvDlss = sp != null ? sp.NvDlssMode : nvDlssMode;
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
            pass.WriteDenied = pass.RendererPid > 0 && ProtectedGameRoster.Contains(pass.RendererName);
            ResolvePriorityTarget(pass);
            return pass;
        }

        private readonly CpuSaturation cpuSaturation = new CpuSaturation();
        private uint boostPriorityTarget = Native.HIGH_PRIORITY_CLASS;
        private long boostFirstStampTicks;

        internal static uint BoostPriorityTarget(bool saturated, bool laneActive)
        {
            return saturated && !laneActive
                ? Native.NORMAL_PRIORITY_CLASS : Native.HIGH_PRIORITY_CLASS;
        }

        private void ResolvePriorityTarget(BoostPass pass)
        {
            bool saturated = cpuSaturation.Update(cpuSaturation.Sample(), DateTime.UtcNow.Ticks);
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
                        ? Lang.T("log.boostsat.1") : Lang.T("log.boostsat.2"));
                }
            }
            pass.PriorityTarget = priorityTarget;
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

        private bool ComputeAuditDue(int pid, BoostPass pass,
            out bool known, out bool needTweak, out bool needPlacement)
        {
            bool retryEco, auditDue, writeBlocked;
            lock (sync)
            {
                writeBlocked = pass.WriteDenied || boostHandleStripped.Contains(pid);
                known = gameBoost.ContainsKey(pid);
                retryEco = !writeBlocked && boostFail.ContainsKey(pid) && !boostEcoGaveUp.Contains(pid);
                needTweak = !tweakApplied.Contains(pid);
                ulong placed; bool placedStrict;
                needPlacement = !writeBlocked && !placementGaveUp.Contains(pid)
                    && (!gamePlacement.TryGetValue(pid, out placed) || placed != pass.DesiredMask
                        || !gamePlacementStrict.TryGetValue(pid, out placedStrict) || placedStrict != pass.UseStrict);
                long nextAudit;
                auditDue = !known || retryEco || needTweak || needPlacement
                    || !gameBoostNextAudit.TryGetValue(pid, out nextAudit)
                    || DateTime.UtcNow.Ticks >= nextAudit;
            }
            return auditDue;
        }

        private IntPtr OpenBoostHandle(int pid, BoostPass pass)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                bool noSuchProcess = Native.LastOpenProcessFailureWasNoSuchProcess();
                bool firstDeny;
                lock (sync) firstDeny = boostDenied.Add(pid);
                if (firstDeny) Logger.Log(Lang.T("log.gamemodeboost.3") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.4"));
                if (!noSuchProcess)
                {
                    ProtectedGameRoster.Remember(pass.RendererName);
                    if (EffIfeo && EffBoost) { IfeoBoost.Arm(pass.RendererName); IfeoBoost.EnsureForGame(pass.RendererName); }
                }
            }
            return h;
        }

        private bool VerifyRendererIdentity(IntPtr h, int pid, BoostPass pass, out long currentCreation)
        {
            string img = Native.ImageName(h);
            long currentCpu; ulong currentDisk;
            if (!Native.QueryProcessSample(h, out currentCreation, out currentCpu, out currentDisk))
            {
                Logger.Log(Lang.T("log.gamemodeboost.5") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.6"));
                return false;
            }
            if (!RendererIdentityMatches(
                    pass.RendererPid, pass.RendererCreation,
                    pass.RendererName, pid,
                    currentCreation, img))
            {
                Logger.Log(Lang.T("log.gamemodeboost.7")
                    + pid + Lang.T("log.gamemodeboost.8"));
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
                        gameBoostNextAudit.Remove(pid); boostStateFail.Remove(pid);
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
                    Logger.Log(Lang.T("log.gamemodeboost.9") + pass.RendererName + " pid " + pid);
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
                    Logger.Log(Lang.T("log.gamemodeboost.10") + pass.RendererName + " pid " + pid);
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
                gpuOk = gpuKnown && !pass.WriteDenied && ApplyAndVerifyGpuBoost(h);
                lock (sync) { if (gpuKnown && !pass.WriteDenied) gameGpu[pid] = gpuOld; }
            }
            else
            {
                int ignoredGpu;
                lock (sync) gpuOk = gameGpu.TryGetValue(pid, out ignoredGpu);
                if (gpuOk && !pass.WriteDenied) gpuOk = ApplyAndVerifyGpuBoost(h);
            }
            return true;
        }

        private bool ApplyBoostStateStage(IntPtr h, int pid, BoostPass pass, bool needTweak,
            out bool stateOk, out bool firstVerified)
        {
            firstVerified = false;
            if (pass.WriteDenied)
            {
                stateOk = false;
                lock (sync)
                {
                    boostStateVerified.Remove(pid);
                    placementGaveUp.Add(pid);
                    boostEcoGaveUp.Add(pid);
                    gameBoostNextAudit[pid] = DateTime.UtcNow.AddSeconds(20 + Math.Abs(pid % 11)).Ticks;
                }
                return true;
            }
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

            bool firstStateWarning = false, stateNowGaveUp = false;
            lock (sync)
            {
                if (stateOk)
                {
                    firstVerified = boostStateVerified.Add(pid);
                    boostStateWarned.Remove(pid);
                    boostStateFail.Remove(pid);
                    int jitter = Math.Abs(pid % 11);
                    gameBoostNextAudit[pid] =
                        DateTime.UtcNow.AddSeconds(20 + jitter).Ticks;
                }
                else
                {
                    boostStateVerified.Remove(pid);
                    firstStateWarning = boostStateWarned.Add(pid);
                    int stateTries;
                    boostStateFail.TryGetValue(pid, out stateTries); stateTries++;
                    if (stateTries >= StateRetryMax)
                    {
                        boostStateFail.Remove(pid);
                        stateNowGaveUp = true;
                        gameBoostNextAudit[pid] =
                            DateTime.UtcNow.AddMinutes(5).Ticks;
                    }
                    else
                    {
                        boostStateFail[pid] = stateTries;
                        gameBoostNextAudit[pid] =
                            DateTime.UtcNow.AddSeconds(4).Ticks;
                    }
                }
            }
            if (!stateOk && firstStateWarning && !handleStripped)
                Logger.Log(Lang.T("log.gamemodeboost.11") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.12")
                    + actualPriority.ToString("X") + " / IO " + actualIo + Lang.T("log.gamemodeboost.13") + writeError + Lang.T("log.gamemodeboost.14"));
            if (stateNowGaveUp && !handleStripped)
                Logger.Log(Lang.T("log.gamemodeboost.11") + pass.RendererName + " pid " + pid
                    + Lang.T("log.gamemodeboost.55") + StateRetryMax + Lang.T("log.gamemodeboost.56"));
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
                        ? Lang.T("t.gamemodeboost.15") + CpuTopology.CountSetBits(pass.DesiredMask) + Lang.T("t.gamemodeboost.16")
                        : Lang.T("t.gamemodeboost.17");
                }
                else if (pass.DesiredMask != allMask && !CpuTopology.MultiGroup)
                {
                    Native.RestoreCpuSets(h, original.CpuSets);
                    placementOk = Native.SetProcessAffinityMask(h, (UIntPtr)pass.DesiredMask)
                        && Native.QueryAffinity(h) == pass.DesiredMask;
                    placementText = Lang.T("t.gamemodeboost.18") + CpuTopology.CountSetBits(pass.DesiredMask) + Lang.T("t.gamemodeboost.16");
                }
                else
                {
                    placementText = Lang.T("t.gamemodeboost.17");
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
                    Logger.Log(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid
                        + Lang.T("log.gamemodeboost.20"));
                else if (placementNowGaveUp)
                    Logger.Log(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.21")
                        + PlacementRetryMax + Lang.T("log.gamemodeboost.22"));
                else if (!placementOk && firstPlacementWarning)
                    Logger.Log(Lang.T("log.gamemodeboost.23") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.24"));

                if (!newlyTracked && placementOk)
                    Logger.Log(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid + placementText);
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
                bool timerExemptDropped;
                Native.ApplyHighQoS(h, Native.TimerExemptWanted, out timerExemptDropped);
                if (timerExemptDropped) Logger.Log(Lang.T("log.gamemodeboost.59"));
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
                        Logger.Log(Lang.T("log.gamemodeboost.3") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.25") + tries + Lang.T("log.gamemodeboost.26"));
                }
            }
            return ecoCleared;
        }

        private void EngageLaneAndReport(IntPtr h, ProcessSnapshot all, int pid, long currentCreation,
            BoostPass pass, bool stateOk, bool firstVerified, bool gpuOk, bool ecoCleared, string placementText)
        {
            if (EffLane && !pass.WriteDenied && !RenderLane.IsActiveFor(pid, currentCreation))
                RenderLane.EnsureForGame(pid, currentCreation, pass.RendererName);

            if (stateOk && firstVerified)
            {
                long stamp = Interlocked.Exchange(ref boostFirstStampTicks, 0);
                if (stamp != 0)
                    Logger.Log(Lang.T("log.gamemodeboost.60")
                        + ((DateTime.UtcNow.Ticks - stamp) / TimeSpan.TicksPerMillisecond)
                        + Lang.T("log.gamemodeboost.61"));
                WarnIfPartitionHurtsWideGame(pass.RendererName, all, pid, pass.DesiredMask);
                Logger.Log(Lang.T("log.gamemodeboost.27") + pass.RendererName + "(pid " + pid + ") "
                    + (pass.PriorityTarget == Native.HIGH_PRIORITY_CLASS ? Lang.T("log.gamemodeboost.28") : Lang.T("log.gamemodeboost.29"))
                    + placementText + Lang.T("log.gamemodeboost.30")
                    + (gpuOk ? Lang.T("log.gamemodeboost.31") : "")
                    + (!Native.PowerThrottlingSupported ? ""
                        : ecoCleared ? Lang.T("t.gamemodeboost.32") : EcoStateText(h)));
            }
        }

        private long nvTweakRetryAtTicks;

        private void ApplyGameTweaks(IntPtr h, int pid, BoostPass pass, bool needTweak)
        {
            if (needTweak)
            {
                if (DateTime.UtcNow.Ticks < Interlocked.Read(ref nvTweakRetryAtTicks)) return;
                string imagePath = Native.ImagePath(h);
                var nvPlan = new NvGamePlan
                {
                    MaxPerf = pass.NvMaxPerf,
                    LowLatMode = pass.NvLowLat,
                    SmoothMotion = pass.NvSmooth,
                    ShaderCacheMax = pass.NvShader,
                    Rebar = pass.NvRebar,
                    DlssMode = pass.NvDlss
                };
                bool nvRetry;
                List<string> nvFailed = NvDrsTweaks.ApplyForGame(imagePath, nvPlan, out nvRetry);
                if (!nvPlan.Empty) HandleNvTweakOutcome(nvFailed, nvPlan);
                if (!nvRetry) lock (sync) tweakApplied.Add(pid);
                else Interlocked.Exchange(ref nvTweakRetryAtTicks,
                    DateTime.UtcNow.AddSeconds(30).Ticks);
            }
        }

        private enum BoostTarget { Alive = 0, Gone = 1, Unopenable = 2 }

        private static BoostTarget ProbeBoostTarget(int pid, long creation)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 87
                    ? BoostTarget.Gone : BoostTarget.Unopenable;
            try
            {
                long cur, cpu;
                ulong io;
                if (!Native.QueryProcessSample(h, out cur, out cpu, out io)) return BoostTarget.Unopenable;
                if (creation > 0 && cur != creation) return BoostTarget.Gone;
                return Native.StillActive(h) ? BoostTarget.Alive : BoostTarget.Gone;
            }
            finally { Native.CloseHandle(h); }
        }

        private const int VanishGiveUpTries = 20;
        private readonly Dictionary<int, int> boostVanishTries = new Dictionary<int, int>();

        internal int DropVanishedBoosts()
        {
            var pending = new List<KeyValuePair<int, Snap>>();
            lock (sync)
                foreach (KeyValuePair<int, Snap> kv in gameBoost) pending.Add(kv);
            if (pending.Count == 0) return 0;

            int dropped = 0, abandoned = 0;
            foreach (KeyValuePair<int, Snap> kv in pending)
            {
                BoostTarget state = ProbeBoostTarget(kv.Key, kv.Value.Creation);
                if (state == BoostTarget.Alive)
                {
                    lock (sync) boostVanishTries.Remove(kv.Key);
                    continue;
                }
                bool unopenable = state == BoostTarget.Unopenable;
                if (unopenable)
                {
                    int tries;
                    lock (sync)
                    {
                        boostVanishTries.TryGetValue(kv.Key, out tries);
                        tries++;
                        boostVanishTries[kv.Key] = tries;
                    }
                    if (tries < VanishGiveUpTries) continue;
                    abandoned++;
                }
                if (!unopenable) CrashGuard.ReleaseBoostProcess(kv.Key, kv.Value.Creation);
                lock (sync)
                {
                    boostVanishTries.Remove(kv.Key);
                    gameBoost.Remove(kv.Key); gameGpu.Remove(kv.Key);
                    gamePlacement.Remove(kv.Key); gamePlacementStrict.Remove(kv.Key);
                    boostFail.Remove(kv.Key); boostStateWarned.Remove(kv.Key);
                    boostStateVerified.Remove(kv.Key); boostHandleStripped.Remove(kv.Key);
                    boostEcoGaveUp.Remove(kv.Key); placementGaveUp.Remove(kv.Key);
                    placementFail.Remove(kv.Key); boostStateFail.Remove(kv.Key);
                    gameBoostNextAudit.Remove(kv.Key); boostDenied.Remove(kv.Key);
                    tweakApplied.Remove(kv.Key);
                }
                dropped++;
            }
            if (dropped > 0)
                Logger.Log((abandoned > 0 ? Lang.T("log.gamemodeboost.58") : Lang.T("log.gamemodeboost.57")) + dropped);
            return dropped;
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
                        gameBoostNextAudit.Remove(k); placementFail.Remove(k); boostStateFail.Remove(k);
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
            Logger.Log(Lang.T("log.gamemodeboost.3") + rendererName + " pid " + pid + Lang.T("log.gamemodeboost.34")
                + (ac == null ? Lang.T("nav.tame") : ac) + Lang.T("log.gamemodeboost.35") + granted.ToString("X")
                + Lang.T("log.gamemodeboost.36"));

            ProtectedGameRoster.Remember(rendererName);
            if (EffIfeo && EffBoost)
            {
                IfeoBoost.Arm(rendererName);
                IfeoBoost.EnsureForGame(rendererName);
            }
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

        private static string EcoStateText(IntPtr process)
        {
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state))
                return Lang.T("t.gamemodeboost.33") + QoSDump(process);
            return (Native.EcoClearedMasksOk(control, state)
                ? Lang.T("t.gamemodeboost.38") : Lang.T("t.gamemodeboost.33"))
                + QoSDump(process);
        }

        internal static string QoSDump(IntPtr process)
        {
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state)) return Lang.T("t.gamemodeboost.37");
            return "(control=0x" + control.ToString("X") + " state=0x" + state.ToString("X") + ")";
        }

        internal static bool HighQoSVerified(IntPtr process)
        {
            if (!Native.PowerThrottlingSupported) return true;
            int control, state;
            if (!Native.TryQueryPowerThrottling(process, out control, out state)) return false;
            return Native.HighQoSMasksOk(control, state);
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
                    placementFail.Clear(); placementGaveUp.Clear(); boostStateFail.Clear();
                }
                else
                    foreach (KeyValuePair<int, Snap> stale in boosts)
                    {
                        boostFail.Remove(stale.Key); boostDenied.Remove(stale.Key);
                        boostStateWarned.Remove(stale.Key); boostStateVerified.Remove(stale.Key);
                        gameBoostNextAudit.Remove(stale.Key); tweakApplied.Remove(stale.Key);
                        boostHandleStripped.Remove(stale.Key); boostEcoGaveUp.Remove(stale.Key);
                        placementFail.Remove(stale.Key); placementGaveUp.Remove(stale.Key);
                        boostStateFail.Remove(stale.Key);
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
                            if (same) Logger.Log(Lang.T("log.gamemodeboost.38") + kv.Value.Name + " pid " + pid + Lang.T("log.gamemodeboost.39"));
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
            Logger.Log(Lang.T("log.gamemodeboost.40") + (name ?? "?") + Lang.T("log.gamemodeboost.41") + entry.Threads
                + Lang.T("log.gamemodeboost.42") + given + Lang.T("log.gamemodeboost.43") + total
                + Lang.T("log.gamemodeboost.44") + (100 - given * 100 / total)
                + Lang.T("log.gamemodeboost.45"));
        }

        public bool PanicRestore()
        {
            int cleared = SelfProtectedRoster.Clear();
            if (cleared > 0)
                Logger.Log(Lang.T("log.gamemodeboost.46") + cleared + Lang.T("log.gamemodeboost.47"));
            int unarmed = IfeoBoost.ClearArmed();
            if (unarmed > 0)
                Logger.Log(Lang.T("log.gamemodeboost.48") + unarmed + Lang.T("log.gamemodeboost.49"));
            bool ifeoOk = IfeoBoost.RestoreAll();
            int fusesCleared;
            lock (sync) { fusesCleared = envFused.Count; envFused.Clear(); }
            foreach (string envKey in EnvKeys)
                if (Settings.Load("EnvFuse_" + envKey, false)) Settings.Save("EnvFuse_" + envKey, false);
            SaveCounter(PowerFailStreakKey, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPState, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPreRender, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyAnsel, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyRebarFeat, 0);
            SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyDlssOvr, 0);
            if (fusesCleared > 0)
                Logger.Log(Lang.T("log.gamemodeboost.50") + fusesCleared + Lang.T("log.gamemodeboost.51"));
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
            try { irqProbe.CompleteIfRunning(); } catch { }
            PowerBudgetYieldRunner.Stop();
            RestorePowerOverlay();
            SelfYield.Release();
            lock (sync)
            {
                active = false;
                activeGame = null;
                firstSweep = true;
            }
            sessionPolicy = null;
            RestoreGlobalCoreMask();
            SuppressionCore.GpuDemoteEnabled = gpuDemoteOn;
            gameGoneSinceTicks = 0;
            cpuSaturation.Reset();
            boostPriorityTarget = Native.HIGH_PRIORITY_CLASS;
            Interlocked.Exchange(ref boostFirstStampTicks, 0);
            Interlocked.Exchange(ref nvTweakRetryAtTicks, 0);
            preStagedNvPath = null;
            try { cpuLimit.Stop(); } catch { }
            Interlocked.Exchange(ref sessionStartTicks, 0);
            overlayScanned = false;
            partitionHintLogged = false;

            bool clean = UnboostGames();
            slowEnvAtTicks = 0;
            List<int> background = core.PidsWith(SuppressReason.Background);
            int ok = core.ReleaseReason(SuppressReason.Background);
            bool backgroundClean = true;
            foreach (int pid in background) if (core.IsThrottled(pid)) { backgroundClean = false; break; }
            bool envClean = RestoreEnv();
            ClearEnvRetryState();
            if (clean) CrashGuard.ClearBoost();
            int restoredTotal = ok + gracePreReleased;
            gracePreReleased = 0;
            if (!quiet || restoredTotal > 0)
                Logger.Log(Lang.T("log.gamemodeboost.52") + reason + Lang.T("log.gamemodeboost.53") + restoredTotal
                    + Lang.T("log.gamemodeboost.54"));
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
