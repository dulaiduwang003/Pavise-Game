using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {

        public AcquireResult Acquire(int pid, string name, SuppressReason reason, string group)
        {
            return Acquire(pid, name, reason, group, SuppressionLevel.Isolated, 0L);
        }

        public AcquireResult Acquire(int pid, string name, SuppressReason reason, string group, SuppressionLevel level, long expectedCreation = 0L)
        {
            CaptureAntiCheatAdmission();
            if (level == SuppressionLevel.None)
            {
                level = SuppressionLevel.Eco;
            }
            if ((reason & SuppressReason.AntiCheat) != 0 && !RegisterAntiCheatRequest(pid, expectedCreation, name, group, level)) return AcquireResult.AlreadyProtected;
            AntiCheatSnapshotAttempt snapshot = null;
            if ((reason & SuppressReason.AntiCheat) != 0 && !BeginAntiCheatSnapshot(pid, expectedCreation, name, level, out snapshot)) return AcquireResult.AlreadyProtected;
            try { return AcquireCore(pid, name, reason, group, level, expectedCreation, snapshot); }
            finally { CompleteAntiCheatSnapshot(snapshot); }
        }

        private AcquireResult AcquireCore(int pid, string name, SuppressReason reason, string group, SuppressionLevel level, long expectedCreation, AntiCheatSnapshotAttempt snapshot)
        {
            IntPtr processHandle = Native.OpenProcess(12800, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                lock (sync)
                {
                    if ((reason & SuppressReason.AntiCheat) != 0 && antiCheatAdmissionEpoch != Interlocked.Read(ref antiCheatForwardEpoch)) return AcquireResult.AlreadyProtected;
                    Entry protectedEntry;
                    if (map.TryGetValue(pid, out protectedEntry) && SameName(protectedEntry.Name, name))
                    {
                        protectedEntry.SetReasons(protectedEntry.Reasons | reason);
                        SetReasonLevel(protectedEntry, reason, level);
                        if (group != null && protectedEntry.Group == null)
                        {
                            protectedEntry.SetGroup(group);
                        }
                        return AcquireResult.AlreadyProtected;
                    }
                    Entry entry = new Entry();
                    entry.Name = name;
                    entry.SetGroup(group);
                    entry.SetOriginalPriority(uint.MaxValue);
                    entry.SetReasons(reason);
                    Entry newProtectedEntry = entry;
                    SetReasonLevel(newProtectedEntry, reason, level);
                    SetEntryLocked(pid, newProtectedEntry);
                    return AcquireResult.NewlyProtected;
                }
            }
            try
            {
                string processName = Native.ImageName(processHandle);
                if (processName == null || !SameName(processName, name))
                {
                    return AcquireResult.AlreadyProtected;
                }
                if (reason == SuppressReason.Background && (WeGameRuntimeProtection.IsProtectedRuntimeName(processName) || PeripheralCatalog.IsInputAudioLike(processName)))
                {
                    return AcquireResult.AlreadyProtected;
                }
                long creation = 0L;
                long cpu;
                ulong io;
                bool processSample = Native.QueryProcessSample(processHandle, out creation, out cpu, out io);
                if (processSample && creation > 0 && (expectedCreation <= 0 || expectedCreation == creation))
                {
                    if (reason == SuppressReason.Background
                        && PeripheralCatalog.IsAmdNoiseSuppressionName(processName)
                        && PeripheralCatalog.IsAmdNoiseSuppressionProcess(processName, Native.ImagePath(processHandle)))
                    {
                        ReleaseBackgroundForRenderer(pid, creation, processName);
                        return AcquireResult.AlreadyProtected;
                    }
                    if ((reason & SuppressReason.AntiCheat) != 0 && !BindAntiCheatRequest(pid, creation, name, group, level)) return AcquireResult.AlreadyProtected;
                    lock (sync)
                    {
                        if ((reason & SuppressReason.AntiCheat) != 0 && antiCheatAdmissionEpoch != Interlocked.Read(ref antiCheatForwardEpoch)) return AcquireResult.AlreadyProtected;
                        Entry trackedEntry;
                        bool alreadyTracked = map.TryGetValue(pid, out trackedEntry);
                        if (alreadyTracked && DiscardChangedTrackedNameLocked(pid, name, creation, trackedEntry))
                        {
                            alreadyTracked = false;
                            trackedEntry = null;
                        }
                        if (alreadyTracked && trackedEntry.OrigPri != uint.MaxValue && trackedEntry.Creation <= 0)
                        {
                            RemoveEntryLocked(pid);
                            batchApply.Remove(pid);
                            alreadyTracked = false;
                            trackedEntry = null;
                        }
                        if (alreadyTracked && trackedEntry.Creation > 0)
                        {
                            if (!processSample)
                            {
                                return AcquireResult.AlreadyProtected;
                            }
                            if (trackedEntry.Creation != creation)
                            {
                                ObserveBackgroundAccountingExit(trackedEntry);
                                RemoveEntryLocked(pid);
                                alreadyTracked = false;
                                trackedEntry = null;
                            }
                        }
                        if (alreadyTracked && trackedEntry.OrigPri != uint.MaxValue && (reason & SuppressReason.AntiCheat) != 0
                            && !HasAntiCheatResources(trackedEntry) && !trackedEntry.GaveUpRestored
                            && (trackedEntry.LegacyPlacementDebt || ((trackedEntry.Reasons & SuppressReason.Background) != 0 && (trackedEntry.Journaled || trackedEntry.Applied))))
                        {
                            // 整档失败也可能已经写入部分字段，旧日志债务先沿稳定恢复预算结清。
                            if (!AttachAntiCheatResourceHistory(pid, trackedEntry)) return AcquireResult.AlreadyProtected;
                            trackedEntry.AntiCheatResources.Active = false;
                        }
                        if (alreadyTracked && trackedEntry.OrigPri != uint.MaxValue && trackedEntry.GaveUp)
                        {
                            trackedEntry.SetReasons(trackedEntry.Reasons | reason);
                            SetReasonLevel(trackedEntry, reason, level);
                            if (group != null && trackedEntry.Group == null)
                            {
                                trackedEntry.SetGroup(group);
                            }
                            if (!trackedEntry.GaveUpRestored)
                                trackedEntry.GaveUpRestored = RestoreEntryValues(processHandle, trackedEntry);
                            if (!trackedEntry.GaveUpRestored) return AcquireResult.AlreadyProtected;
                            ActionFailureStore.CancelApply("BackgroundSuppression", FailureTargetOf(processHandle, trackedEntry));
                            if (!BeginSuppressionCycle(processHandle, trackedEntry, pid)) return AcquireResult.AlreadyProtected;
                            trackedEntry.GaveUp = false;
                            trackedEntry.NextReconcileTicks = 0;
                        }
                        if (alreadyTracked && trackedEntry.OrigPri != uint.MaxValue)
                        {
                            if (snapshot != null) snapshot.Completed = true;
                            if (trackedEntry.AntiCheatResourceRemovalPending) return AcquireResult.AlreadyProtected;
                            SuppressionLevel level2 = trackedEntry.Level;
                            SuppressReason reasons = trackedEntry.Reasons;
                            string group2 = trackedEntry.Group;
                            trackedEntry.SetReasons(trackedEntry.Reasons | reason);
                            if (group != null && trackedEntry.Group == null)
                            {
                                trackedEntry.SetGroup(group);
                            }
                            SetReasonLevel(trackedEntry, reason, level);
                            if ((reason & SuppressReason.AntiCheat) != 0)
                            {
                                if (!AttachAntiCheatResourceHistory(pid, trackedEntry)) return AcquireResult.AlreadyProtected;
                            }
                            bool requestChanged = reasons != trackedEntry.Reasons || level2 != trackedEntry.Level || !SameName(group2, trackedEntry.Group);
                            long ticks = DateTime.UtcNow.Ticks;
                            bool levelChanged = level2 != trackedEntry.Level;
                            bool applyRequired = !trackedEntry.Journaled || levelChanged || reasons != trackedEntry.Reasons || (!trackedEntry.Applied && ticks >= trackedEntry.NextReconcileTicks);
                            if ((requestChanged || !trackedEntry.Journaled) && !PersistJournalLocked())
                            {
                                return AcquireResult.ApplyFailed;
                            }
                            if (applyRequired)
                            {
                                if (QueueApplyLocked(pid, name))
                                {
                                    return AcquireResult.AlreadyThrottled;
                                }
                                trackedEntry.SetApplied(ApplyEntryLocked(processHandle, trackedEntry, pid));
                                ScheduleAfterApply(trackedEntry, trackedEntry.Applied, pid);
                                if (!trackedEntry.Applied && TryNeutralizeUnwritableLocked(processHandle, pid, trackedEntry))
                                {
                                    return AcquireResult.AlreadyProtected;
                                }
                                if (!trackedEntry.Applied && GiveUpAntiCheatLocked(processHandle, pid, trackedEntry))
                                {
                                    return AcquireResult.AlreadyProtected;
                                }
                                return (!trackedEntry.Applied) ? AcquireResult.ApplyFailed : AcquireResult.AlreadyThrottled;
                            }
                            if (!trackedEntry.Applied)
                            {
                                RecordBatchApplyResultLocked(pid, false, "apply-pending-backoff");
                                return AcquireResult.AlreadyThrottled;
                            }
                            if (ticks < trackedEntry.NextReconcileTicks)
                            {
                                RecordBatchApplyResultLocked(pid, true, null);
                                return AcquireResult.AlreadyThrottled;
                            }
                            if (!(HasAntiCheatResources(trackedEntry) && AntiCheatThrottled(trackedEntry)) && !GpuPolicyNeedsRelease(trackedEntry) && VerifyBackgroundJob(trackedEntry) && ThrottleMatches(processHandle, trackedEntry.Level, trackedEntry.OrigPri, trackedEntry.OrigAff, trackedEntry.OrigCpuSets, DesiredGpu(trackedEntry), AntiCheatThrottled(trackedEntry), DesiredAffinityOf(trackedEntry), PlacementIsProtected(trackedEntry), trackedEntry.OrigBoost))
                            {
                                ScheduleAfterMatch(trackedEntry, pid);
                                RecordBatchApplyResultLocked(pid, true, null);
                                return AcquireResult.AlreadyThrottled;
                            }
                            if (!AllowAntiCheatCorrection(processHandle, pid, trackedEntry))
                            {
                                RecordBatchApplyResultLocked(pid, false, "anti-cheat-policy-conflict");
                                return AcquireResult.AlreadyProtected;
                            }
                            if (QueueApplyLocked(pid, name))
                            {
                                return AcquireResult.AlreadyThrottled;
                            }
                            trackedEntry.SetApplied(ApplyEntryLocked(processHandle, trackedEntry, pid));
                            ScheduleAfterApply(trackedEntry, trackedEntry.Applied, pid);
                            if (!trackedEntry.Applied && TryNeutralizeUnwritableLocked(processHandle, pid, trackedEntry))
                            {
                                return AcquireResult.AlreadyProtected;
                            }
                            if (!trackedEntry.Applied && GiveUpAntiCheatLocked(processHandle, pid, trackedEntry))
                            {
                                return AcquireResult.AlreadyProtected;
                            }
                            return AcquireResult.AlreadyThrottled;
                        }
                        uint priorityClass = Native.GetPriorityClass(processHandle);
                        if (priorityClass == 0)
                        {
                            return AcquireResult.ApplyFailed;
                        }
                        ulong origAffValue = Native.QueryAffinity(processHandle);
                        uint[] origCpuSetsValue = Native.QueryCpuSets(processHandle);
                        if (origCpuSetsValue == null)
                        {
                            return AcquireResult.ApplyFailed;
                        }
                        int origIoValue = Native.QueryIoPriority(processHandle);
                        int origPgValue = Native.QueryPagePriority(processHandle);
                        if ((!CpuTopology.MultiGroup && origAffValue == 0) || origIoValue < 0 || origPgValue < 0)
                        {
                            return AcquireResult.ApplyFailed;
                        }
                        if (snapshot != null) snapshot.Completed = true;
                        uint[] yieldCpuSets = CpuTopology.BackgroundYieldCpuSetIds();
                        bool inheritedCpuSets = SameCpuSets(origCpuSetsValue, CpuTopology.BackgroundCpuSetIds()) || SameCpuSets(origCpuSetsValue, CpuTopology.InactiveBackgroundCpuSetIds()) || (yieldCpuSets != null && yieldCpuSets.Length > 0 && SameCpuSets(origCpuSetsValue, yieldCpuSets));
                        ulong squeezeMask = (CpuTopology.MultiGroup ? 0 : CpuTopology.BackgroundSqueezeMask());
                        bool inheritedAffinity = !CpuTopology.MultiGroup && (origAffValue == throttleMask || (CpuTopology.InactiveThrottleMask != 0 && origAffValue == CpuTopology.InactiveThrottleMask) || (squeezeMask != 0 && origAffValue == squeezeMask));
                        bool staleThrottle = CrashGuard.UncleanThrottleAtLaunch && priorityClass == 64 && origIoValue == 0 && origPgValue == 1 && (inheritedCpuSets || inheritedAffinity);
                        uint priorityClassSnapshot = (staleThrottle ? 32u : priorityClass);
                        if (staleThrottle && inheritedAffinity)
                        {
                            origAffValue = 0uL;
                        }
                        if (staleThrottle && inheritedCpuSets)
                        {
                            origCpuSetsValue = new uint[0];
                        }
                        if (staleThrottle && origIoValue == 0)
                        {
                            origIoValue = -1;
                        }
                        if (staleThrottle && origPgValue == 1)
                        {
                            origPgValue = -1;
                        }
                        int controlMask;
                        int stateMask;
                        if (!Native.TryQueryPowerThrottling(processHandle, out controlMask, out stateMask))
                        {
                            controlMask = -1;
                            stateMask = -1;
                        }
                        else if (staleThrottle && (controlMask == 1 || controlMask == 5) && stateMask == controlMask)
                        {
                            controlMask = -1;
                            stateMask = -1;
                        }
                        int origBoostValue = Native.QueryBoostDisabled(processHandle);
                        if (staleThrottle && origBoostValue == 1)
                        {
                            origBoostValue = 0;
                        }
                        int cls;
                        if (Native.D3DKMTGetProcessSchedulingPriorityClass(processHandle, out cls) != 0)
                        {
                            cls = -1;
                            if (GpuDemoteEnabled && (reason & SuppressReason.Background) != 0)
                                Logger.Log("后台 GPU 优先级：未取得原值，保持未接管；目标=" + pid + "/" + creation);
                        }
                        else if (staleThrottle && cls == 0)
                        {
                            cls = 2;
                        }
                        long creation2 = creation;
                        Entry e;
                        if (alreadyTracked)
                        {
                            trackedEntry.SetOriginalPriority(priorityClassSnapshot);
                            trackedEntry.OrigAff = origAffValue;
                            trackedEntry.OrigIo = origIoValue;
                            trackedEntry.OrigPg = origPgValue;
                            trackedEntry.OrigCpuSets = origCpuSetsValue;
                            trackedEntry.OrigGpu = cls;
                            trackedEntry.OrigQoSControl = controlMask;
                            trackedEntry.OrigQoSState = stateMask;
                            trackedEntry.OrigBoost = origBoostValue;
                            trackedEntry.SetReasons(trackedEntry.Reasons | reason);
                            SetReasonLevel(trackedEntry, reason, level);
                            trackedEntry.Creation = creation2;
                            trackedEntry.SetApplied(false);
                            trackedEntry.Journaled = false;
                            if (group != null && trackedEntry.Group == null)
                            {
                                trackedEntry.SetGroup(group);
                            }
                            e = trackedEntry;
                        }
                        else
                        {
                            Entry snapshotEntry = new Entry();
                            snapshotEntry.Name = name;
                            snapshotEntry.SetGroup(group);
                            snapshotEntry.SetOriginalPriority(priorityClassSnapshot);
                            snapshotEntry.OrigAff = origAffValue;
                            snapshotEntry.OrigIo = origIoValue;
                            snapshotEntry.OrigPg = origPgValue;
                            snapshotEntry.OrigCpuSets = origCpuSetsValue;
                            snapshotEntry.OrigGpu = cls;
                            snapshotEntry.OrigQoSControl = controlMask;
                            snapshotEntry.OrigQoSState = stateMask;
                            snapshotEntry.OrigBoost = origBoostValue;
                            snapshotEntry.SetReasons(reason);
                            snapshotEntry.Creation = creation2;
                            Entry newTrackedEntry = snapshotEntry;
                            SetReasonLevel(newTrackedEntry, reason, level);
                            SetEntryLocked(pid, newTrackedEntry);
                            e = newTrackedEntry;
                        }
                        if (AntiCheatThrottled(e) && !AttachAntiCheatResourceHistory(pid, e)) return AcquireResult.ApplyFailed;
                        if (!PersistJournalLocked())
                        {
                            if (HasAntiCheatResources(e)) e.AntiCheatResourceJournalPending = true;
                            return AcquireResult.ApplyFailed;
                        }
                        bool queueApplyLockedSucceeded = QueueApplyLocked(pid, name);
                        bool applied = queueApplyLockedSucceeded || (AntiCheatThrottled(e) ? ApplyAntiCheatResources(processHandle, e)
                            : ApplyThrottle(processHandle, level, priorityClassSnapshot, origAffValue, origCpuSetsValue, DesiredGpu(e), origBoostValue, false, DesiredAffinityOf(e), PlacementIsProtected(e), e));
                        Entry appliedEntry;
                        if (map.TryGetValue(pid, out appliedEntry) && !queueApplyLockedSucceeded)
                        {
                            appliedEntry.SetApplied(applied);
                            ScheduleAfterApply(appliedEntry, applied, pid);
                            if (!applied && TryNeutralizeUnwritableLocked(processHandle, pid, appliedEntry))
                            {
                                return AcquireResult.NewlyProtected;
                            }
                            if (!applied && GiveUpAntiCheatLocked(processHandle, pid, appliedEntry))
                            {
                                return AcquireResult.AlreadyProtected;
                            }
                        }
                        if (!marked)
                        {
                            marked = true;
                            CrashGuard.MarkThrottle(throttleMask);
                        }
                        return applied ? AcquireResult.NewlyThrottled : AcquireResult.ApplyFailed;
                    }
                }
                return AcquireResult.ApplyFailed;
            }
            finally
            {
                Native.CloseHandle(processHandle);
            }
        }

        private int EntryIdentity(IntPtr handle, Entry entry)
        {
            int processIdentityState = Native.QueryProcessIdentityState(handle, entry.Creation);
            if (processIdentityState >= 0)
            {
                return processIdentityState;
            }
            return 2;
        }

        private static bool EntrySnapshotMatches(IntPtr handle, Entry entry)
        {
            if (entry.BackgroundJobPlacement && (entry.BackgroundJobActive || entry.BackgroundRestorePending)) return false;
            if (entry.GpuLease.PendingCheckpoint || entry.GpuLease.Phase == 'P' || entry.GpuLease.Phase == 'A') return false;
            int gpu = entry.GpuLease.Phase == 'L' ? entry.OrigGpu : -1;
            return SnapshotMatchesCurrent(handle, entry.OrigPri, entry.OrigAff, entry.OrigIo, entry.OrigPg, entry.OrigCpuSets, entry.OrigQoSControl, entry.OrigQoSState, gpu, entry.OrigBoost, !entry.AntiCheatPlacementExternal && !entry.BackgroundJobPlacement);
        }

        private bool ApplyEntryValues(IntPtr handle, Entry entry)
        {
            if (AntiCheatThrottled(entry)) return ApplyAntiCheatResources(handle, entry);
            if (!VerifyBackgroundJob(entry)) return false;
            DetectBackgroundPlacementChange(handle, entry);
            return ApplyThrottle(handle, entry.Level, entry.OrigPri, entry.OrigAff, entry.OrigCpuSets, DesiredGpu(entry), entry.OrigBoost, AntiCheatThrottled(entry), DesiredAffinityOf(entry), PlacementIsProtected(entry), entry);
        }

        private bool BeginSuppressionCycle(IntPtr handle, Entry entry, int pid)
        {
            string signature = PerformanceTuning.RecoveryCycleSerial + "\n" + pid + "/" + entry.Creation + "/"
                + entry.Level + "/" + entry.BackgroundLevel + "/" + entry.AntiCheatLevel + "/" + GpuDemoteEnabled + "/" + EnforceBackgroundAffinity;
            return ActionFailureStore.BeginAutomaticCycle("BackgroundSuppression", FailureTargetOf(handle, entry), signature);
        }

        private bool ApplyEntryLocked(IntPtr h, Entry e, int pid)
        {
            if (AntiCheatThrottled(e)) return ApplyAntiCheatResources(h, e);
            if (FailureBudgetFor(e, "reconcile").Failures >= 8) return false;
            string target = FailureTargetOf(h, e);
            BeginSuppressionCycle(h, e, pid);
            int identityState = EntryIdentity(h, e);
            if (identityState != 0)
            {
                StopEntry(e, identityState == 1, identityState == 1);
                return false;
            }
            if (ActionFailureStore.IsIsolated("BackgroundSuppression", target))
            {
                if (!RestoreEntryValues(h, e))
                {
                    if (EntryIdentity(h, e) == 1)
                    {
                        StopEntry(e, true, true);
                        return false;
                    }
                    ActionFailureStore.Trip("BackgroundSuppression", target, "Background recovery incomplete before apply");
                    StopEntry(e, false);
                    return false;
                }
                e.GaveUp = false;
                ActionFailureStore.CancelApply("BackgroundSuppression", target);
                BeginSuppressionCycle(h, e, pid);
            }
            string reason;
            if (!ActionFailureStore.TryBeginApply("BackgroundSuppression", target, out reason))
            {
                StopEntry(e, EntrySnapshotMatches(h, e));
                return false;
            }
            LastApplyError = "";
            bool applyEntryValuesSucceeded;
            try
            {
                identityState = EntryIdentity(h, e);
                if (identityState != 0)
                {
                    ActionFailureStore.CancelApply("BackgroundSuppression", target);
                    StopEntry(e, identityState == 1, identityState == 1);
                    return false;
                }
                applyEntryValuesSucceeded = ApplyEntryValues(h, e);
            }
            catch (NotSupportedException)
            {
                return CancelSkippedEntry(h, e, target);
            }
            catch (OperationCanceledException)
            {
                return CancelSkippedEntry(h, e, target);
            }
            catch (Exception ex3)
            {
                LastApplyError = ex3.Message;
                applyEntryValuesSucceeded = false;
            }
            switch (EntryIdentity(h, e))
            {
            case 1:
                ActionFailureStore.CancelApply("BackgroundSuppression", target);
                StopEntry(e, true, true);
                return false;
            case 2:
                LastApplyError = "Background identity unreadable after apply; recovery retained";
                applyEntryValuesSucceeded = false;
                break;
            }
            if (applyEntryValuesSucceeded)
            {
                if (ActionFailureStore.CompleteApply("BackgroundSuppression", target))
                {
                    e.GaveUp = false;
                    e.GaveUpRestored = false;
                    return true;
                }
                LastApplyError = "Background completion record could not be persisted";
            }
            ActionFailureStore.Trip("BackgroundSuppression", target, string.IsNullOrEmpty(LastApplyError) ? "Background process apply or verification failed" : LastApplyError);
            StopEntry(e, RestoreEntryValues(h, e));
            return false;
        }

        private bool CancelSkippedEntry(IntPtr handle, Entry entry, string target)
        {
            bool gone = EntryIdentity(handle, entry) == 1;
            bool restoredOrGone = gone || RestoreEntryValues(handle, entry);
            if (restoredOrGone)
            {
                ActionFailureStore.CancelApply("BackgroundSuppression", target);
            }
            else
            {
                ActionFailureStore.Trip("BackgroundSuppression", target, "Background recovery incomplete after skipped apply");
            }
            StopEntry(entry, restoredOrGone, gone);
            return false;
        }

        private static string FailureTargetOf(IntPtr h, Entry entry)
        {
            if (string.IsNullOrEmpty(entry.FailureTarget))
            {
                entry.FailureTarget = Native.ImagePath(h) ?? entry.Name ?? "unknown-process";
            }
            return entry.FailureTarget;
        }

        private bool GiveUpAntiCheatLocked(IntPtr h, int pid, Entry e)
        {
            if (HasAntiCheatResources(e) && AntiCheatThrottled(e)) return false;
            if (e == null || e.GaveUp || e.OrigPri == uint.MaxValue)
            {
                return false;
            }
            if ((e.Reasons & SuppressReason.AntiCheat) == 0 || (e.Reasons & SuppressReason.Background) != 0)
            {
                return false;
            }
            string lastApplyErrorSnapshot = LastApplyError;
            bool gaveUpRestoredValue = RestoreBackgroundPlacement(h, e) && RestoreOrdinaryEntryValues(h, e);
            e.GaveUp = true;
            e.GaveUpRestored = gaveUpRestoredValue;
            e.SetApplied(false);
            e.SqueezeAff = 0uL;
            e.NextReconcileTicks = long.MaxValue;
            Logger.Log(Lang.T("log.suppressioncore.giveup.1", 0) + e.Name + " 进程编号 " + pid + Lang.T("log.suppressioncore.giveup.2", 0) + (string.IsNullOrEmpty(lastApplyErrorSnapshot) ? "" : lastApplyErrorSnapshot) + Lang.T(gaveUpRestoredValue ? "log.suppressioncore.giveup.3" : "log.suppressioncore.giveup.4", 0));
            return true;
        }

        private bool AllowAntiCheatCorrection(IntPtr handle, int pid, Entry entry)
        {
            if (HasAntiCheatResources(entry) && AntiCheatThrottled(entry)) return true;
            if (!entry.Applied || !AntiCheatThrottled(entry)
                || (entry.Reasons & SuppressReason.Background) != 0) return true;
            long now = DateTime.UtcNow.Ticks;
            if (now < entry.AntiCheatCorrectionWindow || now - entry.AntiCheatCorrectionWindow >= TimeSpan.FromMinutes(1).Ticks)
            {
                entry.AntiCheatCorrectionWindow = now;
                entry.AntiCheatCorrections = 0;
            }
            if (entry.AntiCheatCorrections < 3) { entry.AntiCheatCorrections++; return true; }
            LastApplyError = "anti-cheat-policy-conflict";
            GiveUpAntiCheatLocked(handle, pid, entry);
            return false;
        }

        internal static bool AffinityOnlyFailure(string detail)
        {
            if (string.IsNullOrEmpty(detail))
            {
                return false;
            }
            string[] detailParts = detail.Split(',');
            foreach (string entryText in detailParts)
            {
                if (entryText != "affinity-write" && entryText != "affinity-readback")
                {
                    return false;
                }
            }
            return true;
        }

        private static void ScheduleAfterMatch(Entry e, int pid)
        {
            if (HasAntiCheatResources(e) && AntiCheatThrottled(e))
            {
                e.AntiCheatResources.MaintenanceFailures = 0;
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(AntiCheatAuditSeconds(e.Level)).Ticks;
                return;
            }
            RecordFailureBudget(e, "reconcile", true, 0);
            e.BackgroundReleaseDebt = false;
            e.ReconcileFailures = 0;
            if (AntiCheatThrottled(e))
            {
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(AntiCheatAuditSeconds(e.Level)).Ticks;
                return;
            }
            if (e.FastReconcileRemaining > 0)
            {
                e.FastReconcileRemaining--;
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds((e.FastReconcileRemaining > 0) ? 4 : (20 + PositiveMod(pid, 11))).Ticks;
            }
            else
            {
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(20 + PositiveMod(pid, 11)).Ticks;
            }
        }

        private static void ScheduleAfterApply(Entry e, bool applied, int pid)
        {
            if (HasAntiCheatResources(e) && AntiCheatThrottled(e))
            {
                e.SetApplied(applied);
                if (applied) e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(AntiCheatAuditSeconds(e.Level)).Ticks;
                return;
            }
            FailureBudgetFor(e, "reconcile");
            if (!applied) e.SetApplied(false);
            if (applied)
            {
                RecordFailureBudget(e, "reconcile", true, 0);
                e.BackgroundReleaseDebt = false;
                e.ReconcileFailures = 0;
                e.FastReconcileRemaining = 2;
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(AntiCheatThrottled(e) ? AntiCheatAuditSeconds(e.Level) : 4).Ticks;
                return;
            }
            if (e.ReconcileFailures < 8)
            {
                e.ReconcileFailures++;
            }
            int scheduleAfterApplyValue = 4;
            for (int i = 1; i < e.ReconcileFailures; i++)
            {
                if (scheduleAfterApplyValue >= 60)
                {
                    break;
                }
                scheduleAfterApplyValue = Math.Min(60, scheduleAfterApplyValue * 2);
            }
            e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(scheduleAfterApplyValue).Ticks;
            RecordFailureBudget(e, "reconcile", false, e.NextReconcileTicks);
            if (e.ReconcileFailures >= 8) e.NextReconcileTicks = DateTime.MaxValue.Ticks;
        }

        private bool ApplyThrottle(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity, uint[] originalCpuSets, int desiredGpu, int origBoost, bool antiCheat, ulong desiredAffinity, bool affinityOwned = false, Entry accountingEntry = null)
        {
            affinityOwned |= antiCheat;
            Interlocked.Increment(ref applyOperations);
            List<string> failures = new List<string>();
            if (DisableDynamicBoost(level, antiCheat) && DesiredBoostDisabled(level, antiCheat, origBoost) < 0)
            {
                failures.Add("boost-snapshot");
                return StopFailedApply(failures);
            }
            uint desiredPriority = DesiredPriority(level, originalPriority, antiCheat);
            if (Native.GetPriorityClass(h) != desiredPriority && !AccountBackgroundWrite(accountingEntry, Native.SetPriorityClass(h, desiredPriority)))
            {
                failures.Add("priority-write");
            }
            if (failures.Count != 0)
            {
                return StopFailedApply(failures);
            }
            if (desiredGpu >= 0)
            {
                bool gpuWrote;
                if (accountingEntry == null)
                {
                    failures.Add("gpu-owner-missing");
                }
                else
                {
                    bool gpuApplied = accountingEntry.GpuLease.Apply(h, desiredGpu,
                        () => EntryIdentity(h, accountingEntry), () => SaveGpuJournal(), out gpuWrote);
                    AccountBackgroundWrite(accountingEntry, gpuWrote);
                    if (!gpuApplied) failures.Add("gpu-ownership-or-readback");
                }
            }
            else if (accountingEntry != null && GpuPolicyNeedsRelease(accountingEntry)
                && !accountingEntry.GpuLease.Restore(h, () => EntryIdentity(h, accountingEntry), () => SaveGpuJournal()))
            {
                failures.Add("gpu-restore-pending");
            }
            if (failures.Count != 0)
            {
                return StopFailedApply(failures);
            }
            if (!affinityOwned && !Native.CpuSetsMatch(h, originalCpuSets) && !RestoreCpuSetsDuringApply(h, originalCpuSets, accountingEntry))
            {
                failures.Add("cpu-sets-restore");
            }
            if (failures.Count != 0)
            {
                return StopFailedApply(failures);
            }
            if (!CpuTopology.MultiGroup && !affinityOwned)
            {
                ulong originalAffinitySnapshot = ((originalAffinity != 0) ? originalAffinity : allMask);
                bool affinityChanged = desiredAffinity != originalAffinitySnapshot;
                ulong currentAffinity = Native.QueryAffinity(h);
                if (affinityChanged && currentAffinity != desiredAffinity)
                {
                    long birth, cpu; ulong io; string admissionReason = null;
                    if (!Native.QueryProcessSample(h, out birth, out cpu, out io)
                        || !GameThreadAffinityAdmission.Check(h, birth, currentAffinity, out admissionReason))
                    {
                        failures.Add(admissionReason ?? GameThreadAffinityAdmission.RefusalPrefix + "process-identity-unconfirmed");
                        return StopFailedApply(failures);
                    }
                }
                if (currentAffinity != desiredAffinity && !AccountBackgroundWrite(accountingEntry, Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity)))
                {
                    failures.Add(affinityChanged ? "affinity-write" : "affinity-restore");
                }
                if (Native.QueryAffinity(h) != desiredAffinity)
                {
                    failures.Add(affinityChanged ? "affinity-readback" : "affinity-restore-readback");
                }
            }
            if (failures.Count != 0)
            {
                return StopFailedApply(failures);
            }
            int desiredIoPriority = DesiredIoPriority(level, antiCheat);
            if (Native.QueryIoPriority(h) != desiredIoPriority && !AccountBackgroundWrite(accountingEntry, Native.TrySetIoPriority(h, desiredIoPriority)))
            {
                failures.Add("io-write");
            }
            if (failures.Count != 0)
            {
                return StopFailedApply(failures);
            }
            int desiredPagePriority = DesiredPagePriority(level, antiCheat);
            if (Native.QueryPagePriority(h) != desiredPagePriority && !AccountBackgroundWrite(accountingEntry, Native.TrySetPagePriority(h, desiredPagePriority)))
            {
                failures.Add("page-write");
            }
            if (failures.Count != 0)
            {
                return StopFailedApply(failures);
            }
            if (Native.PowerThrottlingSupported)
            {
                bool desiredTimerSealSucceeded = DesiredTimerSeal(level, antiCheat);
                uint requiredQosMask = Native.EcoQoSWantMask(desiredTimerSealSucceeded);
                int controlMask;
                int stateMask;
                bool tryQueryPowerThrottling = Native.TryQueryPowerThrottling(h, out controlMask, out stateMask);
                bool preserveTimerSeal = tryQueryPowerThrottling && !desiredTimerSealSucceeded && (controlMask & 4) != 0;
                if ((!tryQueryPowerThrottling || ((uint)controlMask & requiredQosMask) != requiredQosMask || ((uint)stateMask & requiredQosMask) != requiredQosMask || preserveTimerSeal) && !AccountBackgroundWrite(accountingEntry, Native.ApplyEcoQoS(h, desiredTimerSealSucceeded)))
                {
                    failures.Add("eco-write");
                }
            }
            if (failures.Count != 0)
            {
                return StopFailedApply(failures);
            }
            string boostFailure = ApplyBoostPolicy(level, antiCheat, origBoost, () => Native.QueryBoostDisabled(h), (bool disabled) => AccountBackgroundWrite(accountingEntry, Native.TrySetBoostDisabled(h, disabled)));
            if (boostFailure.Length > 0)
            {
                failures.Add(boostFailure);
            }
            if (failures.Count != 0)
            {
                return StopFailedApply(failures);
            }
            if (Native.GetPriorityClass(h) != desiredPriority)
            {
                failures.Add("priority-readback");
            }
            if (Native.QueryIoPriority(h) != desiredIoPriority)
            {
                failures.Add("io-readback");
            }
            if (Native.QueryPagePriority(h) != desiredPagePriority)
            {
                failures.Add("page-readback");
            }
            if (Native.PowerThrottlingSupported && !EcoStateVisible(h, Native.EcoQoSWantMask(DesiredTimerSeal(level, antiCheat))))
            {
                failures.Add("eco-readback");
            }
            LastApplyError = string.Join(",", failures.ToArray());
            return failures.Count == 0;
        }

        private bool StopFailedApply(List<string> failed)
        {
            LastApplyError = string.Join(",", failed.ToArray());
            return false;
        }

        private static bool EcoStateVisible(IntPtr h, uint required)
        {
            int controlMask;
            int stateMask;
            return ConfirmEcoState(() => Native.TryQueryPowerThrottling(h, out controlMask, out stateMask) && PowerStateMatches(controlMask, stateMask, required), delegate
            {
                Thread.Sleep(1);
            });
        }

        internal static bool ConfirmEcoState(Func<bool> query, Action wait)
        {
            for (int i = 0; i < 4; i++)
            {
                if (query())
                {
                    return true;
                }
                if (i < 3)
                {
                    wait();
                }
            }
            return false;
        }

        public bool Reconcile(int pid, string expectedName, SuppressReason reason)
        {
            return Reconcile(pid, expectedName, reason, false);
        }

        internal bool Reconcile(int pid, string expectedName, SuppressReason reason, bool forceAudit)
        {
            CaptureAntiCheatAdmission();
            long ticks = DateTime.UtcNow.Ticks;
            Entry entry;
            uint origPri;
            ulong origAff;
            uint[] origCpuSets;
            SuppressionLevel level;
            lock (sync)
            {
                if (!map.TryGetValue(pid, out entry) || (entry.Reasons & reason) == 0 || entry.OrigPri == uint.MaxValue || !entry.Journaled || !SameName(entry.Name, expectedName))
                {
                    return false;
                }
                if (entry.GaveUp)
                {
                    return true;
                }
                if (!(HasAntiCheatResources(entry) && AntiCheatThrottled(entry)) && FailureBudgetFor(entry, "reconcile").Failures >= 8) return true;
                if ((!forceAudit || HasAntiCheatResources(entry) && AntiCheatThrottled(entry) && entry.AntiCheatResources.MaintenanceFailures > 0) && ticks < entry.NextReconcileTicks)
                {
                    return true;
                }
                origPri = entry.OrigPri;
                origAff = entry.OrigAff;
                origCpuSets = entry.OrigCpuSets;
                level = entry.Level;
            }
            IntPtr processHandle = Native.OpenProcess(12800, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                lock (sync)
                {
                    Entry currentEntry;
                    if (map.TryGetValue(pid, out currentEntry) && currentEntry == entry)
                    {
                        if (HasAntiCheatResources(currentEntry) && AntiCheatThrottled(currentEntry)) PauseAntiCheatResources(currentEntry, "OpenProcess:" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                        else ScheduleAfterApply(currentEntry, false, pid);
                    }
                }
                return true;
            }
            try
            {
                string processName = Native.ImageName(processHandle);
                if (processName == null || !SameName(processName, expectedName))
                {
                    if (processName == null) { lock (sync) { if (HasAntiCheatResources(entry) && AntiCheatThrottled(entry)) PauseAntiCheatResources(entry, "process-image-unknown"); else ScheduleAfterApply(entry, false, pid); } }
                    return false;
                }
                long creation;
                long cpu;
                ulong io;
                if (!Native.QueryProcessSample(processHandle, out creation, out cpu, out io))
                {
                    lock (sync) { if (HasAntiCheatResources(entry) && AntiCheatThrottled(entry)) PauseAntiCheatResources(entry, "process-creation-unknown"); else ScheduleAfterApply(entry, false, pid); }
                    return false;
                }
                lock (sync)
                {
                    Entry appliedEntry;
                    if (!map.TryGetValue(pid, out appliedEntry) || appliedEntry != entry || (appliedEntry.Reasons & reason) == 0 || (appliedEntry.Creation > 0 && appliedEntry.Creation != creation))
                    {
                        return false;
                    }
                    if (appliedEntry.Level != level || appliedEntry.OrigPri != origPri || appliedEntry.OrigAff != origAff || !object.ReferenceEquals(appliedEntry.OrigCpuSets, origCpuSets))
                    {
                        return true;
                    }
                    int cls;
                    if (appliedEntry.OrigGpu < 0 && GpuDemoteEnabled && (appliedEntry.Reasons & SuppressReason.Background) != 0 && appliedEntry.BackgroundLevel >= SuppressionLevel.Restrained && Native.D3DKMTGetProcessSchedulingPriorityClass(processHandle, out cls) == 0)
                    {
                        appliedEntry.OrigGpu = cls;
                        if (!PersistJournalLocked())
                        {
                            appliedEntry.OrigGpu = -1;
                        }
                        else
                        {
                            Logger.Log(Lang.T("log.suppressioncore.6", 0) + expectedName + " 进程编号 " + pid + Lang.T("log.suppressioncore.7", 0));
                        }
                    }
                    int desiredGpu = DesiredGpu(appliedEntry);
                    if (!(HasAntiCheatResources(appliedEntry) && AntiCheatThrottled(appliedEntry)) && !GpuPolicyNeedsRelease(appliedEntry) && VerifyBackgroundJob(appliedEntry) && ThrottleMatches(processHandle, level, origPri, origAff, appliedEntry.OrigCpuSets, desiredGpu, AntiCheatThrottled(appliedEntry), DesiredAffinityOf(appliedEntry), PlacementIsProtected(appliedEntry), appliedEntry.OrigBoost))
                    {
                        if (!appliedEntry.Applied && appliedEntry.ReconcileFailures > 0)
                        {
                            SuccessLogBatch.Record("reconcile-verified", expectedName, pid, appliedEntry.Creation,
                                "reason=" + reason + " previousFailures=" + appliedEntry.ReconcileFailures);
                        }
                        appliedEntry.SetApplied(true);
                        ScheduleAfterMatch(appliedEntry, pid);
                        return true;
                    }
                    bool applied = appliedEntry.Applied;
                    if (!AllowAntiCheatCorrection(processHandle, pid, appliedEntry)) return true;
                    int reconcileFailures = appliedEntry.ReconcileFailures;
                    appliedEntry.SetApplied(ApplyEntryLocked(processHandle, appliedEntry, pid));
                    ScheduleAfterApply(appliedEntry, appliedEntry.Applied, pid);
                    if (appliedEntry.Applied)
                    {
                        if (!applied && reconcileFailures > 0)
                        {
                            SuccessLogBatch.Record("reconcile-recovered", expectedName, pid, appliedEntry.Creation,
                                "reason=" + reason + " previousFailures=" + reconcileFailures);
                        }
                    }
                    else
                    {
                        if (TryNeutralizeUnwritableLocked(processHandle, pid, appliedEntry))
                        {
                            return true;
                        }
                        if (reconcileFailures < 3)
                        {
                            SuccessLogBatch.FlushPending();
                            Logger.Log(Lang.T("log.gamemodesweep.1", 0) + expectedName + "（进程编号 " + pid + ") " + ApplyFailureText.Of(LastApplyError) + Lang.T("log.suppressioncore.11", 0));
                        }
                    }
                    return true;
                }
            }
            finally
            {
                Native.CloseHandle(processHandle);
                if ((reason & SuppressReason.AntiCheat) != 0)
                {
                    lock (sync) RefreshGroupCountsLocked();
                }
            }
        }

    }
}
