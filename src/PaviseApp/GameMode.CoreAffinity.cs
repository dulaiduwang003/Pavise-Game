using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool ReleaseExclusive()
        {
            bool coreEngagedSnapshot = coreEngaged;
            exclusiveAuditRetry.Reset();
            exclusiveAuditDeferred = false;
            coreAffinityVerified = false;
            ClearBackgroundSqueezes();
            coreEngaged = false;
            bool stopCoreIsolationSucceeded = StopCoreIsolation();
            bool restoreAllHardPinsSucceeded = RestoreAllHardPins();
            corePinPending = !restoreAllHardPinsSucceeded;
            if ((!stopCoreIsolationSucceeded || !restoreAllHardPinsSucceeded) && coreGovernor != null && coreGovernor.Blocked == null)
            {
                coreGovernor.Block("schedule.auto.pending");
            }
            if (coreEngagedSnapshot)
            {
                Logger.Log((stopCoreIsolationSucceeded && restoreAllHardPinsSucceeded) ? Lang.T("log.autocore.released", 0) : Lang.T("log.autocore.releasepending", 0));
            }
            if (stopCoreIsolationSucceeded)
            {
                return restoreAllHardPinsSucceeded;
            }
            return false;
        }
        private bool ExclusiveDrifted(int pid, BoostPass pass, out ulong observed, out bool unreadable)
        {
            observed = 0uL;
            unreadable = false;
            if (!pass.Exclusive || !coreEngaged || CpuTopology.MultiGroup || stopping)
            {
                return false;
            }
            long nowTicks = AutoCores.NowTicks;
            if (nowTicks - exclusiveCheckTicks < 10000000)
            {
                return false;
            }
            exclusiveCheckTicks = nowTicks;
            exclusiveAuditDeferred = false;
            lock (sync)
            {
                if (placementGaveUp.Contains(pid))
                {
                    return false;
                }
                HardPin hardPin = null;
                bool affinityReadable;
                switch ((gamePlacement.ContainsKey(pid) && hardPins.TryGetValue(pid, out hardPin) && hardPin.Pid == pid && hardPin.Creation == pass.RendererCreation) ? HardPinHandleStateOf(hardPin) : HardPinHandleState.Unknown)
                {
                case HardPinHandleState.Gone:
                    return false;
                case HardPinHandleState.Match:
                    string jobReason;
                    if (hardPin.JobBound && !coreAffinityJobs.Verify(pid, hardPin.Creation, pass.DesiredMask, out jobReason))
                    {
                        corePlacementEvidence = "pid=" + pid + " stage=job-audit reason=" + jobReason;
                        affinityReadable = false;
                        break;
                    }
                    affinityReadable = ReadCoreAffinity(hardPin.RestoreHandle, out observed);
                    if (!affinityReadable)
                    {
                        int error = Marshal.GetLastWin32Error();
                        exclusiveAuditDeferred = DeferExclusiveAuditFailure(pass, "audit-read", error);
                    }
                    break;
                default:
                    corePlacementEvidence = "pid=" + pid + " creation=" + pass.RendererCreation + " stage=audit-identity-or-ownership-unavailable";
                    affinityReadable = false;
                    break;
                }
                RecordCoreAffinity(pass.DesiredMask, observed, affinityReadable);
                if (coreAffinityVerified) exclusiveAuditRetry.Reset();
                unreadable = !affinityReadable;
                return !affinityReadable || !coreAffinityVerified;
            }
        }
        private bool ReadCoreAffinity(IntPtr handle, out ulong observed)
        {
            return Native.TryQueryAffinity(handle, out observed);
        }
        private bool DeferExclusiveAuditFailure(BoostPass pass, string stage, int error)
        {
            coreAffinityVerified = false;
            bool retry = exclusiveAuditRetry.ShouldRetry(error, AutoCores.NowTicks);
            corePlacementEvidence = "pid=" + pass.RendererPid + " creation=" + pass.RendererCreation
                + " 步骤=" + stage + " win32=" + error + " target=0x" + pass.DesiredMask.ToString("X")
                + " 尝试=" + exclusiveAuditRetry.Failures + "/3 action=" + (retry ? "retry" : "stop");
            Logger.Warn("游戏亲和性维护 " + corePlacementEvidence);
            return retry;
        }
        private void RecordCoreAffinity(ulong expected, ulong observed, bool readable)
        {
            coreAffinityObserved = (readable ? observed : 0);
            coreAffinityRead = readable;
            coreAffinityVerified = readable && ManualAffinityMatches(observed, expected);
        }
        internal static bool ManualAffinityMatches(ulong observed, ulong expected)
        {
            if (expected != 0)
            {
                return observed == expected;
            }
            return false;
        }
        private bool PlacementChangedOutside(IntPtr h, Snap original, out ulong observed, out bool unreadable)
        {
            observed = 0uL;
            unreadable = false;
            if (CpuTopology.MultiGroup)
            {
                return false;
            }
            ulong aff = ((original.Aff != 0) ? original.Aff : allMask);
            if (!Native.TryQueryAffinity(h, out observed))
            {
                unreadable = true;
                return false;
            }
            if (observed != aff)
            {
                return true;
            }
            if (coreManual != null)
            {
                return false;
            }
            uint[] cpuSets = Native.QueryCpuSets(h);
            if (cpuSets == null)
            {
                unreadable = true;
                return false;
            }
            return !CoreThreadPlacement.Same(cpuSets, original.CpuSets ?? new uint[0]);
        }
        private bool HoldExclusive(int pid, BoostPass pass, ulong observed)
        {
            switch (WriteBackExclusive(pid, pass, true))
            {
            case ExclusiveWriteBack.Busy:
            case ExclusiveWriteBack.Deferred:
                return false;
            case ExclusiveWriteBack.Failed:
                YieldExclusive(pid, observed);
                return false;
            default:
            {
                int exclusiveWriteBacksSnapshot = ++exclusiveWriteBacks;
                if (exclusiveWriteBacksSnapshot <= 3 || exclusiveWriteBacksSnapshot % 20 == 0)
                {
                    Logger.Log(PerformancePolicy.Text("其他程序改动了游戏亲和性 读回 0x", "其他程序修改了游戏亲和性（读回 0x") + observed.ToString("X") + PerformancePolicy.Text(" 已改回 0x", "）；已写回 0x") + pass.DesiredMask.ToString("X") + PerformancePolicy.Text(" 本局第 ", " 本会话： ") + exclusiveWriteBacksSnapshot + PerformancePolicy.Text(" 次", ""));
                }
                return true;
            }
            }
        }
        private ExclusiveWriteBack WriteBackExclusive(int pid, BoostPass pass, bool retryMaintenance = false)
        {
            if (stopping || pass == null || !pass.Exclusive || CpuTopology.MultiGroup || pass.DesiredMask == 0)
            {
                return ExclusiveWriteBack.Failed;
            }
            if (!Monitor.TryEnter(criticalProcessGate, 0))
            {
                return ExclusiveWriteBack.Busy;
            }
            try
            {
                lock (sync)
                {
                    HardPin hardPin;
                    if (CorePending || placementGaveUp.Contains(pid) || !hardPins.TryGetValue(pid, out hardPin)
                        || hardPin.RestoreRequested || hardPin.RestoreInFlight || hardPin.Creation != pass.RendererCreation
                        || hardPin.AppliedAffinity != pass.DesiredMask || HardPinHandleStateOf(hardPin) != 0)
                    {
                        return ExclusiveWriteBack.Failed;
                    }
                    if (hardPin.JobBound)
                    {
                        string jobReason;
                        bool verified = coreAffinityJobs.Verify(pid, hardPin.Creation, pass.DesiredMask, out jobReason);
                        RecordCoreAffinity(pass.DesiredMask, verified ? pass.DesiredMask : 0, verified);
                        if (!verified) corePlacementEvidence = "pid=" + pid + " stage=job-audit reason=" + jobReason;
                        return verified ? ExclusiveWriteBack.Written : ExclusiveWriteBack.Failed;
                    }
                    ulong currentMask;
                    if (!Native.TryQueryAffinity(hardPin.RestoreHandle, out currentMask)) return ExclusiveWriteBack.Failed;
                    if (currentMask == pass.DesiredMask)
                    {
                        RecordCoreAffinity(pass.DesiredMask, currentMask, true);
                        return ExclusiveWriteBack.Written;
                    }
                    string threadReason;
                    if (!GameThreadAffinityAdmission.Check(hardPin.RestoreHandle, pid, hardPin.Creation, currentMask, out threadReason))
                    {
                        corePlacementEvidence = "pid=" + pid + " stage=thread-affinity-refused reason=" + threadReason;
                        return ExclusiveWriteBack.Failed;
                    }
                    bool setProcessAffinityMaskSucceeded = Native.SetProcessAffinityMask(hardPin.RestoreHandle, (UIntPtr)pass.DesiredMask);
                    int writeError = setProcessAffinityMaskSucceeded ? 0 : Marshal.GetLastWin32Error();
                    if (setProcessAffinityMaskSucceeded) criticalNextSample = 0L;
                    ulong mask;
                    bool tryQueryAffinity = Native.TryQueryAffinity(hardPin.RestoreHandle, out mask);
                    int readError = tryQueryAffinity ? 0 : Marshal.GetLastWin32Error();
                    RecordCoreAffinity(pass.DesiredMask, mask, tryQueryAffinity);
                    if (!setProcessAffinityMaskSucceeded || !tryQueryAffinity || mask != pass.DesiredMask)
                    {
                        if (!retryMaintenance) return ExclusiveWriteBack.Failed;
                        return DeferExclusiveAuditFailure(pass, !setProcessAffinityMaskSucceeded ? "audit-write" : (!tryQueryAffinity ? "audit-write-readback" : "audit-write-mismatch"), !setProcessAffinityMaskSucceeded ? writeError : readError)
                            ? ExclusiveWriteBack.Deferred : ExclusiveWriteBack.Failed;
                    }
                    exclusiveAuditRetry.Reset();
                    criticalNextSample = 0L;
                    return ExclusiveWriteBack.Written;
                }
            }
            finally
            {
                Monitor.Exit(criticalProcessGate);
            }
        }
        private void YieldExclusive(int pid, ulong observed)
        {
            long processCreation = 0L;
            bool jobOwned = false;
            lock (sync)
            {
                HardPin pin;
                jobOwned = hardPins.TryGetValue(pid, out pin) && coreAffinityJobs.HasLease(pid, pin.Creation);
            }
            // 进程恢复必须在对应锁内，等待关键线程恢复之后进行。
            if (jobOwned && !RestoreHardPin(pid))
            {
                BlockAutoCores("schedule.auto.pending");
                return;
            }
            lock (sync)
            {
                placementGaveUp.Add(pid);
                gamePlacement.Remove(pid);
                Snap currentGameBoost;
                if (gameBoost.TryGetValue(pid, out currentGameBoost))
                {
                    currentGameBoost.PlacementYielded = true;
                    gameBoost[pid] = currentGameBoost;
                    processCreation = currentGameBoost.Creation;
                }
            }
            if (processCreation > 0)
            {
                CrashGuard.YieldBoostPlacement(pid, processCreation);
            }
            ForgetHardPin(pid);
            coreAffinityVerified = false;
            if (coreManual != null)
            {
                corePlacementStopReason = "schedule.auto.external";
            }
            coreEngaged = false;
            AutoCoreGovernor autoCoreGovernor = coreGovernor;
            if (autoCoreGovernor != null)
            {
                autoCoreGovernor.Block("schedule.auto.external");
            }
            if (!StopCoreIsolation() && autoCoreGovernor != null)
            {
                autoCoreGovernor.Block("schedule.auto.pending");
            }
            Logger.Log(string.Format(Lang.T("log.autocore.external", 0), observed.ToString("X")));
        }
        private bool PinExclusive(IntPtr h, int pid, BoostPass pass, Snap original)
        {
            if (!Monitor.TryEnter(criticalProcessGate, 0))
            {
                return false;
            }
            try
            {
                if (!PrepareCriticalThreadProcessMaskMutation(pid, original.Creation))
                {
                    return false;
                }
                return PinExclusiveLocked(h, pid, pass, original);
            }
            finally
            {
                Monitor.Exit(criticalProcessGate);
            }
        }
        private bool PinExclusiveLocked(IntPtr h, int pid, BoostPass pass, Snap original)
        {
            if (CpuTopology.MultiGroup || original.Aff == 0 || pass.DesiredMask == 0 || (pass.DesiredMask & ~allMask) != 0 || (coreManual == null && (pass.DesiredMask & ~original.Aff) != 0))
            {
                LogCorePlacement(pass, "affinity-range", 0, h);
                return false;
            }
            string threadAdmissionReason;
            if (!GameThreadAffinityAdmission.Check(h, pid, original.Creation, original.Aff, out threadAdmissionReason))
            {
                LogCorePlacement(pass, threadAdmissionReason, 0, h);
                return false;
            }
            IntPtr processHandle = DuplicateRestoreHandle(h);
            if (processHandle == IntPtr.Zero)
            {
                LogCorePlacement(pass, "restore-handle", Marshal.GetLastWin32Error(), h);
                return false;
            }
            bool retained = false;
            bool verified = false;
            try
            {
                ulong mask;
                lock (sync)
                {
                    if (stopping) return false;
                    if (!Native.TryQueryAffinity(h, out mask))
                    {
                        LogCorePlacement(pass, "read-original", Marshal.GetLastWin32Error(), h);
                        return false;
                    }
                    if (mask != original.Aff)
                    {
                        LogCorePlacement(pass, "original-changed", 0, h);
                        return false;
                    }
                    // 改变亲和性前，将恢复所有权从旧声明移交给 Job 回执。
                    if (!CrashGuard.TrackBoostAffinity(pid, original.Creation, 0uL))
                    {
                        LogCorePlacement(pass, "journal", 0, h);
                        return false;
                    }
                    // Job 后端可能改变目标前，先保留句柄。
                    if (!RememberHardPin(pid, original.Creation, original.Aff, pass.DesiredMask, processHandle)) return false;
                    hardPins[pid].JobRecoveryOnly = true;
                    retained = true;
                }
                string jobReason;
                coreAffinityJobs.ThreadRecoveryRoot = CriticalThreadStoreRoot;
                bool jobBound = coreAffinityJobs.Ensure(pid, original.Creation, pass.RendererName, original.Aff, pass.DesiredMask, out jobReason);
                lock (sync)
                {
                    HardPin pin;
                    if (hardPins.TryGetValue(pid, out pin) && pin.Creation == original.Creation)
                    {
                        pin.JobBound = jobBound;
                        pin.JobFallbackReason = jobBound ? null : jobReason;
                        if (pin.RestoreRequested || pin.RestoreInFlight) return false;
                    }
                }
                if (stopping || coreAffinityJobs.HasPending || (!jobBound && coreAffinityJobs.IsActive(pid, original.Creation)))
                {
                    LogCorePlacement(pass, "job-recovery", 0, h);
                    return false;
                }
                if (!jobBound)
                {
                    // 游戏自定义线程放置被拒绝时，也禁止退回进程亲和性方案。
                    if (GameThreadAffinityAdmission.IsRefusal(jobReason))
                    {
                        LogCorePlacement(pass, jobReason, 0, h);
                        return false;
                    }
                    ulong fallbackMask;
                    if (!Native.TryQueryAffinity(h, out fallbackMask)
                        || !GameThreadAffinityAdmission.Check(h, pid, original.Creation, fallbackMask, out threadAdmissionReason))
                    {
                        LogCorePlacement(pass, threadAdmissionReason ?? GameThreadAffinityAdmission.RefusalPrefix + "process-mask-unreadable", 0, h);
                        return false;
                    }
                    Logger.Warn("游戏亲和性 Job 保护不可用，进程编号=" + pid + " 原因=" + jobReason + "；使用进程亲和性维护");
                    // Job 结清后，先移交恢复所有权，再执行备用进程亲和性写入。
                    lock (sync)
                    {
                        HardPin fallbackPin;
                        if (stopping || !hardPins.TryGetValue(pid, out fallbackPin) || fallbackPin.Creation != original.Creation
                            || fallbackPin.RestoreRequested || fallbackPin.RestoreInFlight
                            || !CrashGuard.TrackBoostAffinity(pid, original.Creation, pass.DesiredMask))
                        {
                            LogCorePlacement(pass, "fallback-journal", 0, h);
                            return false;
                        }
                        fallbackPin.JobRecoveryOnly = false;
                        if (fallbackMask != pass.DesiredMask && !Native.SetProcessAffinityMask(h, (UIntPtr)pass.DesiredMask))
                        {
                            LogCorePlacement(pass, "set-affinity", Marshal.GetLastWin32Error(), h);
                            return false;
                        }
                    }
                }
                exclusiveWriteBacks = 0;
                exclusiveAuditRetry.Reset();
                bool readable = Native.TryQueryAffinity(h, out mask);
                int readError = readable ? 0 : Marshal.GetLastWin32Error();
                verified = readable && mask == pass.DesiredMask;
                if (verified && jobBound) verified = coreAffinityJobs.Verify(pid, original.Creation, pass.DesiredMask, out jobReason);
                RecordCoreAffinity(pass.DesiredMask, mask, readable);
                coreAffinityVerified = verified;
                if (!verified) LogCorePlacement(pass, readable ? "readback-mismatch" : "readback", readError, h);
                return verified;
            }
            finally
            {
                if (!retained) Native.CloseHandle(processHandle);
                else if (!verified) RestoreHardPin(pid);
            }
        }
        private bool EngageIsolation(int pid, BoostPass pass)
        {
            if (stopping || coreManual == null)
            {
                return false;
            }
            if (!coreManual.Isolation)
            {
                coreEngaged = true;
                return true;
            }
            if (CriticalCoreIsolationRecoveryOwner != null && !StopCriticalThreads(0)) return false;
            lock (coreIsolationGate)
            {
            if (criticalOwnsCoreIsolation) return false;
            if (coreEngaged && CoreIsolationClient.State == "schedule.isolation.active")
            {
                return true;
            }
            if (coreIsolation == null)
            {
                coreIsolation = new CoreIsolationClient();
            }
            if (coreIsolation.Ensure((corePlan != null) ? corePlan.Exclusive : pass.DesiredMask, pid, pass.RendererCreation, pass.DesiredMask, true))
            {
                coreEngaged = true;
                Logger.Log(string.Format(Lang.T("log.autocore.engaged", 0), CpuTopology.DescribeMask((corePlan != null) ? corePlan.Exclusive : pass.DesiredMask)));
                return true;
            }
            }
            StopCoreIsolation();
            return false;
        }
        private bool StopCoreIsolation()
        {
            if (HasCriticalCoreIsolationLease && CriticalCoreIsolationRecoveryOwner == null && !StopCriticalThreads(0)) return false;
            coreAffinityVerified = false;
            bool result = StopCoreRefinement();
            ClearBackgroundSqueezes();
            coreEngaged = false;
            lock (coreIsolationGate)
            {
            if (criticalOwnsCoreIsolation) return result && !coreBackgroundPending;
            if (criticalBorrowsCoreIsolation) return false;
            bool isolationStopped = coreIsolation == null || coreIsolation.Stop();
            if (isolationStopped)
            {
                coreIsolation = null;
            }
            if (isolationStopped && !coreBackgroundPending)
            {
                return result;
            }
            return false;
            }
        }
        private bool PlacementMatches(IntPtr h, BoostPass pass, out bool unreadable, out ulong observed)
        {
            unreadable = false;
            observed = 0uL;
            if (h == IntPtr.Zero || pass == null)
            {
                return false;
            }
            if (!pass.Exclusive && pass.DesiredMask == allMask)
            {
                return true;
            }
            if (pass.DesiredMask == 0 || (pass.DesiredMask & ~allMask) != 0 || CpuTopology.MultiGroup || pass.RendererPid <= 0 || pass.RendererCreation <= 0)
            {
                return false;
            }
            if (!Native.TryQueryAffinity(h, out observed))
            {
                observed = 0uL;
                unreadable = true;
                return false;
            }
            return observed == pass.DesiredMask;
        }
        private static HardPinHandleState HardPinHandleStateOf(HardPin pin)
        {
            if (pin == null || pin.RestoreHandle == IntPtr.Zero)
            {
                return HardPinHandleState.Unknown;
            }
            uint processId = GetProcessId(pin.RestoreHandle);
            if (processId != 0 && processId != (uint)pin.Pid)
            {
                return HardPinHandleState.Mismatch;
            }
            if (!Native.StillActive(pin.RestoreHandle))
            {
                return HardPinHandleState.Gone;
            }
            long creation;
            long cpu;
            ulong io;
            if (!Native.QueryProcessSample(pin.RestoreHandle, out creation, out cpu, out io))
            {
                return HardPinHandleState.Unknown;
            }
            if (processId == 0 || creation != pin.Creation)
            {
                return HardPinHandleState.Mismatch;
            }
            return HardPinHandleState.Match;
        }
        private bool RememberHardPin(int pid, long creation, ulong originalAffinity, ulong appliedAffinity, IntPtr restoreHandle)
        {
            if (pid <= 0 || creation <= 0 || originalAffinity == 0 || restoreHandle == IntPtr.Zero)
            {
                return false;
            }
            lock (sync)
            {
                HardPin hardPin;
                if (hardPins.TryGetValue(pid, out hardPin))
                {
                    if (hardPin.RestoreRequested || hardPin.RestoreInFlight || (hardPin.Creation != creation && coreAffinityJobs.HasLease(pid, hardPin.Creation)))
                    {
                        RequestHardPinRestoreLocked(hardPin);
                        return false;
                    }
                    if (hardPin.Creation == creation && hardPin.RestoreHandle != IntPtr.Zero)
                    {
                        Native.CloseHandle(restoreHandle);
                        return true;
                    }
                    hardPins.Remove(pid);
                    if (hardPin.RestoreHandle != IntPtr.Zero)
                    {
                        Native.CloseHandle(hardPin.RestoreHandle);
                    }
                }
                hardPins[pid] = new HardPin
                {
                    Pid = pid,
                    Creation = creation,
                    OriginalAffinity = originalAffinity,
                    AppliedAffinity = appliedAffinity,
                    RestoreHandle = restoreHandle
                };
                return true;
            }
        }
        private void ForgetHardPin(int pid)
        {
            bool restore = false;
            lock (sync)
            {
                HardPin hardPin;
                if (hardPins.TryGetValue(pid, out hardPin))
                {
                    if (hardPin.RestoreInFlight || hardPin.RestoreRequested || coreAffinityJobs.HasLease(pid, hardPin.Creation))
                    {
                        RequestHardPinRestoreLocked(hardPin);
                        restore = true;
                    }
                    else
                    {
                        hardPins.Remove(pid);
                        if (hardPin.RestoreHandle != IntPtr.Zero) Native.CloseHandle(hardPin.RestoreHandle);
                    }
                }
            }
            if (restore && !Monitor.IsEntered(sync)) RestoreHardPin(pid);
        }
        private void RunIndependentCorePlacement(BoostPass pass)
        {
            long nowTicks = AutoCores.NowTicks;
            try
            {
                RunIndependentCorePlacementImpl(pass);
            }
            finally
            {
                coreBudget.Record(nowTicks);
            }
        }
        private void RunIndependentCorePlacementImpl(BoostPass pass)
        {
            if (coreManual == null || pass == null || !pass.Exclusive || CorePending || stopping)
            {
                return;
            }
            IntPtr openAffinityProcessHandle = IntPtr.Zero;
            try
            {
                ulong observed;
                bool unreadable;
                if (ExclusiveDrifted(pass.RendererPid, pass, out observed, out unreadable))
                {
                    if (!unreadable)
                    {
                        corePlacementEvidence = "pid=" + pass.RendererPid + " creation=" + pass.RendererCreation + " stage=audit-mismatch actual=0x" + observed.ToString("X") + " target=0x" + pass.DesiredMask.ToString("X");
                    }
                    if (unreadable)
                    {
                        if (!exclusiveAuditDeferred) BlockAutoCores("schedule.auto.affinityread");
                    }
                    else
                    {
                        HoldExclusive(pass.RendererPid, pass, observed);
                    }
                }
                else
                {
                    if (coreEngaged)
                    {
                        return;
                    }
                    if (RendererForegroundPid() != pass.RendererPid)
                    {
                        if (coreGovernor != null)
                        {
                            ApplyAutoCoreStep(coreGovernor.Pause());
                        }
                        return;
                    }
                    openAffinityProcessHandle = Native.OpenAffinityProcess(pass.RendererPid);
                    int error = ((openAffinityProcessHandle == IntPtr.Zero) ? Marshal.GetLastWin32Error() : 0);
                    if (openAffinityProcessHandle == IntPtr.Zero)
                    {
                        LogCorePlacement(pass, "open", error, openAffinityProcessHandle);
                        BlockAutoCores("schedule.auto.placementunavailable");
                        return;
                    }
                    long currentCreation;
                    if (!VerifyRendererIdentity(openAffinityProcessHandle, pass.RendererPid, pass, out currentCreation))
                    {
                        LogCorePlacement(pass, "identity", 0, openAffinityProcessHandle);
                        BlockAutoCores("schedule.auto.identity");
                        return;
                    }
                    uint granted;
                    if (Native.TryQueryGrantedAccess(openAffinityProcessHandle, out granted) && (granted & 0x200) == 0)
                    {
                        LogCorePlacement(pass, "access", 5, openAffinityProcessHandle);
                        BlockAutoCores("schedule.auto.placementunavailable");
                        return;
                    }
                    bool needPlacement = true;
                    bool known;
                    lock (sync)
                    {
                        known = gameBoost.ContainsKey(pass.RendererPid);
                    }
                    HandlePidReuse(pass.RendererPid, currentCreation, ref known, ref needPlacement);
                    bool newlyTracked;
                    bool gpuOk;
                    if (!CaptureAndTrack(openAffinityProcessHandle, pass.RendererPid, currentCreation, pass, known, out newlyTracked, out gpuOk, false))
                    {
                        LogCorePlacement(pass, "snapshot", 0, openAffinityProcessHandle);
                        BlockAutoCores("schedule.auto.capture");
                        return;
                    }
                    string placementText;
                    bool placementVerified;
                    if (!ApplyPlacementStage(openAffinityProcessHandle, pass.RendererPid, pass, true, newlyTracked, out placementText, out placementVerified))
                    {
                        BlockAutoCores("schedule.auto.capture");
                    }
                    if (placementVerified)
                    {
                        LogCorePlacement(pass, "verified", 0, openAffinityProcessHandle);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("自动选核放置异常，进程编号=" + pass.RendererPid + " 类型=" + ex.GetType().Name);
                BlockAutoCores("schedule.auto.placement");
            }
            finally
            {
                if (openAffinityProcessHandle != IntPtr.Zero)
                {
                    Native.CloseHandle(openAffinityProcessHandle);
                }
                pass.Exclusive = coreGovernor != null && coreGovernor.Exclusive && !CorePending;
                if (!pass.Exclusive)
                {
                    pass.DesiredMask = allMask;
                }
                PublishAutoCores(pass.RendererName);
            }
        }
        private void LogCorePlacement(BoostPass pass, string stage, int error, IntPtr handle)
        {
            pass.PlacementStage = stage;
            pass.PlacementError = error;
            uint granted;
            string toStringOffset = (Native.TryQueryGrantedAccess(handle, out granted) ? ("0x" + granted.ToString("X")) : "unknown");
            ulong mask;
            string toStringOffset2 = ((handle != IntPtr.Zero && Native.TryQueryAffinity(handle, out mask)) ? ("0x" + mask.ToString("X")) : "unknown");
            ulong originalAffinityMask = 0uL;
            lock (sync)
            {
                Snap currentGameBoost;
                if (gameBoost.TryGetValue(pass.RendererPid, out currentGameBoost))
                {
                    originalAffinityMask = currentGameBoost.Aff;
                }
            }
            corePlacementEvidence = "pid=" + pass.RendererPid + " creation=" + pass.RendererCreation + " 步骤=" + stage + " win32=" + error + " granted=" + toStringOffset + " original=0x" + originalAffinityMask.ToString("X") + " actual=" + toStringOffset2 + " target=0x" + pass.DesiredMask.ToString("X") + " cachedBoostRestriction=" + pass.WriteDenied;
        }
        private static string PlacementFailureReason(BoostPass pass)
        {
            switch (pass.PlacementStage)
            {
            case "affinity-range":
                return "schedule.auto.affinityrange";
            case "journal":
                return "schedule.auto.capture";
            case "restore-handle":
                return "schedule.auto.restorehandle";
            case "read-original":
            case "read-placement":
            case "readback":
                return "schedule.auto.affinityread";
            case "set-affinity":
                return "schedule.auto.affinitywrite";
            default:
                return "schedule.auto.placement";
            }
        }
        private bool ApplyPlacementStage(IntPtr h, int pid, BoostPass pass, bool needPlacement, bool newlyTracked, out string placementText, out bool placementVerified)
        {
            placementText = "";
            placementVerified = false;
            bool unreadable = false;
            ulong observed = 0uL;
            if (!needPlacement)
            {
                placementVerified = PlacementMatches(h, pass, out unreadable, out observed);
                if (pass.Exclusive && !placementVerified && !unreadable)
                {
                    bool canReassert;
                    lock (sync)
                    {
                        canReassert = !placementGaveUp.Contains(pid) && gameBoost.ContainsKey(pid);
                    }
                    if (canReassert)
                    {
                        placementVerified = HoldExclusive(pid, pass, observed);
                    }
                }
                return true;
            }
            if (pass.Exclusive && RendererForegroundPid() != pid)
            {
                if (coreGovernor != null)
                {
                    ApplyAutoCoreStep(coreGovernor.Pause());
                }
                PublishAutoCores(pass.RendererName);
                return true;
            }
            Snap currentGameBoost;
            lock (sync)
            {
                if (!gameBoost.TryGetValue(pid, out currentGameBoost))
                {
                    return false;
                }
            }
            if (pass.Exclusive)
            {
                bool unreadable2;
                bool placementChangedOutsideSucceeded = PlacementChangedOutside(h, currentGameBoost, out observed, out unreadable2);
                if (unreadable2)
                {
                    LogCorePlacement(pass, "read-placement", 0, h);
                    BlockAutoCores("schedule.auto.affinityread");
                    return true;
                }
                if (placementChangedOutsideSucceeded)
                {
                    YieldExclusive(pid, observed);
                    return true;
                }
            }
            bool mayApplyPlacement = true;
            if (pass.Exclusive)
            {
                bool affinitySet = mayApplyPlacement && PinExclusive(h, pid, pass, currentGameBoost);
                if (!affinitySet && pass.PlacementStage == "set-affinity" && pass.PlacementError == 5)
                {
                    if (!Native.TryQueryAffinity(h, out observed) || observed != currentGameBoost.Aff)
                    {
                        BlockAutoCores("schedule.auto.affinityread");
                    }
                    else if (!CrashGuard.TrackBoostAffinity(pid, currentGameBoost.Creation, 0uL))
                    {
                        BlockAutoCores("schedule.auto.capture");
                    }
                    else
                    {
                        BlockAutoCores("schedule.auto.placementunavailable");
                    }
                    return true;
                }
                placementVerified = affinitySet && PlacementMatches(h, pass, out unreadable, out observed);
                string failureReason = ((!placementVerified) ? PlacementFailureReason(pass) : ((!EngageIsolation(pid, pass)) ? "schedule.auto.failed" : null));
                if (failureReason != null)
                {
                    placementVerified = false;
                    BlockAutoCores(failureReason);
                    if (coreGovernor == null)
                    {
                        ReleaseExclusive();
                    }
                    return true;
                }
                placementVerified = PlacementMatches(h, pass, out unreadable, out observed);
                RecordCoreAffinity(pass.DesiredMask, observed, !unreadable);
                if (!placementVerified && !unreadable && WriteBackExclusive(pid, pass) == ExclusiveWriteBack.Written)
                {
                    placementVerified = true;
                }
                if (!placementVerified)
                {
                    if (unreadable)
                    {
                        BlockAutoCores("schedule.auto.affinityread");
                    }
                    else
                    {
                        YieldExclusive(pid, observed);
                    }
                    return true;
                }
                placementText = Lang.T("t.gamemodeboost.18") + CpuTopology.CountSetBits(pass.DesiredMask) + Lang.T("t.gamemodeboost.16");
            }
            else
            {
                placementText = Lang.T("t.gamemodeboost.17");
                placementVerified = PlacementMatches(h, pass, out unreadable, out observed);
                if (!placementVerified)
                {
                    mayApplyPlacement = false;
                }
            }
            int failureCount = 0;
            bool gaveUpNow = false;
            bool firstFailure = false;
            lock (sync)
            {
                if (mayApplyPlacement)
                {
                    gamePlacement[pid] = pass.DesiredMask;
                    placementFail.Remove(pid);
                    placementGaveUp.Remove(pid);
                }
                else
                {
                    gamePlacement.Remove(pid);
                    placementFail.TryGetValue(pid, out failureCount);
                    failureCount++;
                    if (failureCount >= 3)
                    {
                        placementFail.Remove(pid);
                        gaveUpNow = placementGaveUp.Add(pid);
                    }
                    else
                    {
                        placementFail[pid] = failureCount;
                        firstFailure = failureCount == 1;
                    }
                }
            }
            if (gaveUpNow || (!mayApplyPlacement && firstFailure))
            {
                Logger.Log(PlacementWarningLine(pass.RendererName, pid, unreadable, gaveUpNow));
            }
            if (!newlyTracked && mayApplyPlacement)
            {
                Logger.Log(Lang.T("log.gamemodeboost.19", 0) + pass.RendererName + " 进程编号 " + pid + placementText);
            }
            if (pass.Exclusive && placementVerified && mayApplyPlacement)
            {
                Logger.Info("游戏落核已核验，进程编号=" + pid + " 创建时间=" + pass.RendererCreation
                    + " 掩码=0x" + pass.DesiredMask.ToString("X")
                    + " 来源=" + (coreManual != null ? "manual" : "auto") + " 名称=" + pass.RendererName);
            }
            return true;
        }

    }
}
