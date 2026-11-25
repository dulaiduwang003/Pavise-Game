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
            if (DualWriteRefused(pid, name, reason)) return AcquireResult.AlreadyProtected;
            if ((reason & SuppressReason.AntiCheat) != 0 && !RegisterAntiCheatRequest(pid, expectedCreation, name, group, level)) return AcquireResult.AlreadyProtected;
            AntiCheatSnapshotAttempt snapshot = null;
            if ((reason & SuppressReason.AntiCheat) != 0 && !BeginAntiCheatSnapshot(pid, expectedCreation, name, level, out snapshot)) return AcquireResult.AlreadyProtected;
            try { return AcquireCore(pid, name, reason, group, level, expectedCreation, snapshot); }
            finally { CompleteAntiCheatSnapshot(snapshot); }
        }

        // 同一个进程只能由一方写入 普通后台不碰反作弊进程 反作弊引擎也不接手普通后台还在管的进程
        private bool DualWriteRefused(int pid, string name, SuppressReason reason)
        {
            bool antiCheat = (reason & SuppressReason.AntiCheat) != 0;
            bool background = (reason & SuppressReason.Background) != 0;
            if (antiCheat && background) return true;
            if (background && AntiCheatCatalog.IsAntiCheatLikeName(name)) return true;
            if (!antiCheat && !background) return false;
            lock (sync)
            {
                Entry entry;
                if (!map.TryGetValue(pid, out entry)) return false;
                return antiCheat ? (entry.Reasons & SuppressReason.Background) != 0 : (entry.Reasons & SuppressReason.AntiCheat) != 0;
            }
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
                    if (map.TryGetValue(pid, out protectedEntry) && HasAntiCheatResources(protectedEntry)
                        && !SameName(protectedEntry.Name, name)) return AcquireResult.AlreadyProtected;
                    if (map.TryGetValue(pid, out protectedEntry) && SameName(protectedEntry.Name, name))
                    {
                        if (protectedEntry.ResourceJobOnlyIo || protectedEntry.ResourceJobRestoringProcess
                            || (protectedEntry.OrdinaryValues && protectedEntry.Reasons == SuppressReason.None)
                            || (protectedEntry.ResourceJobName != null && protectedEntry.RestoreInFlight))
                            return AcquireResult.AlreadyProtected;
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
                        if (alreadyTracked && HasAntiCheatResources(trackedEntry)
                            && (trackedEntry.Creation != creation || !SameName(trackedEntry.Name, name)))
                        {
                            if (trackedEntry.Creation <= 0 || trackedEntry.Creation == creation
                                || !ClearAntiCheatJob(trackedEntry, () => SaveAntiCheatResourceJournal(trackedEntry)))
                                return AcquireResult.AlreadyProtected;
                        }
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
                        if (alreadyTracked && (trackedEntry.ResourceJobOnlyIo || trackedEntry.ResourceJobRestoringProcess
                            || (trackedEntry.OrdinaryValues && trackedEntry.Reasons == SuppressReason.None)
                            || (trackedEntry.ResourceJobName != null && trackedEntry.RestoreInFlight)))
                            return AcquireResult.AlreadyProtected;
                        if (alreadyTracked && trackedEntry.OrigPri != uint.MaxValue && (reason & SuppressReason.AntiCheat) != 0
                            && !HasAntiCheatResources(trackedEntry) && !trackedEntry.GaveUpRestored
                            && (trackedEntry.LegacyPlacementDebt || ((trackedEntry.Reasons & SuppressReason.Background) != 0 && (trackedEntry.Journaled || trackedEntry.Applied))))
                        {
                            // 整档失败也可能已经写入部分字段 旧日志债务先沿稳定恢复预算结清
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
                            if (!(HasAntiCheatResources(trackedEntry) && AntiCheatThrottled(trackedEntry)) && !GpuPolicyNeedsRelease(trackedEntry) && VerifyBackgroundJob(trackedEntry) && ResourceJobMatches(trackedEntry, pid) && ThrottleMatches(processHandle, trackedEntry.Level, trackedEntry.OrigPri, trackedEntry.OrigAff, trackedEntry.OrigCpuSets, AntiCheatThrottled(trackedEntry), DesiredAffinityOf(trackedEntry), PlacementIsProtected(trackedEntry)))
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
                        bool ordinary = reason == SuppressReason.Background;
                        uint priorityClass = ordinary && (OrdinaryAttemptsOf(pid, creation) & OrdinaryFields.Priority) != 0
                            ? 0u : Native.GetPriorityClass(processHandle);
                        if (priorityClass == 0 && !ordinary)
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
                        AdoptInheritedOriginalsLocked(processHandle, pid, creation, ref priorityClass, ref origIoValue, ref origPgValue);
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
                        int origBoostValue = ordinary || reason == SuppressReason.AntiCheat ? -1 : Native.QueryBoostDisabled(processHandle);
                        if (staleThrottle && origBoostValue == 1)
                        {
                            origBoostValue = 0;
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
                            trackedEntry.OrigQoSControl = controlMask;
                            trackedEntry.OrigQoSState = stateMask;
                            trackedEntry.OrigBoost = origBoostValue;
                            trackedEntry.SetReasons(trackedEntry.Reasons | reason);
                            SetReasonLevel(trackedEntry, reason, level);
                            trackedEntry.Creation = creation2;
                            trackedEntry.PressedSince = DateTime.UtcNow.ToFileTimeUtc();
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
                            snapshotEntry.OrigQoSControl = controlMask;
                            snapshotEntry.OrigQoSState = stateMask;
                            snapshotEntry.OrigBoost = origBoostValue;
                            snapshotEntry.SetReasons(reason);
                            snapshotEntry.Creation = creation2;
                            snapshotEntry.PressedSince = DateTime.UtcNow.ToFileTimeUtc();
                            Entry newTrackedEntry = snapshotEntry;
                            SetReasonLevel(newTrackedEntry, reason, level);
                            SetEntryLocked(pid, newTrackedEntry);
                            e = newTrackedEntry;
                        }
                        e.OrdinaryValues = ordinary;
                        if (AntiCheatThrottled(e) && !AttachAntiCheatResourceHistory(pid, e)) return AcquireResult.ApplyFailed;
                        if (!PersistJournalLocked())
                        {
                            if (HasAntiCheatResources(e)) e.AntiCheatResourceJournalPending = true;
                            return AcquireResult.ApplyFailed;
                        }
                        bool queueApplyLockedSucceeded = QueueApplyLocked(pid, name);
                        bool applied = queueApplyLockedSucceeded || (AntiCheatThrottled(e) ? ApplyAntiCheatResources(processHandle, e)
                            : ApplyThrottle(processHandle, level, priorityClassSnapshot, origAffValue, origCpuSetsValue, false, DesiredAffinityOf(e), PlacementIsProtected(e), e));
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
            if (entry.OrdinaryValues) return false;
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
            return ApplyThrottle(handle, entry.Level, entry.OrigPri, entry.OrigAff, entry.OrigCpuSets, AntiCheatThrottled(entry), DesiredAffinityOf(entry), PlacementIsProtected(entry), entry);
        }

        private bool BeginSuppressionCycle(IntPtr handle, Entry entry, int pid)
        {
            string signature = PerformanceTuning.RecoveryCycleSerial + "\n" + pid + "/" + entry.Creation + "/"
                + entry.Level + "/" + entry.BackgroundLevel + "/" + entry.AntiCheatLevel + "/" + EnforceBackgroundAffinity;
            return ActionFailureStore.BeginAutomaticCycle("BackgroundSuppression", FailureTargetOf(handle, entry), signature);
        }

        private bool ApplyEntryLocked(IntPtr h, Entry e, int pid)
        {
            if (e.ResourceJobOnlyIo || e.ResourceJobRestoringProcess) return false;
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
            Logger.Log(() => Logger.Format("反作弊压制 {0} 进程编号 {1} 写入被拒 本次运行不再尝试 失败环节 {2}{3}", "Anti-cheat suppression {0} pid {1} write refused, no more attempts this run, failed step {2}{3}", e.Name, pid, (string.IsNullOrEmpty(lastApplyErrorSnapshot) ? "" : lastApplyErrorSnapshot), (gaveUpRestoredValue ? Logger.Text(" 已写入的项已退回原值", " written items reverted to original values") : " rollback=pending exitRetry=scheduled")));
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

        private static void ScheduleAfterMatch(Entry e, int pid)
        {
            if (HasAntiCheatResources(e) && AntiCheatThrottled(e))
            {
                e.AntiCheatResources.MaintenanceFailures = 0;
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(HasAntiCheatResources(e) && e.AntiCheatResources.JobActive ? 30 : AntiCheatAuditSeconds(e.Level)).Ticks;
                return;
            }
            RecordFailureBudget(e, "reconcile", true, 0);
            e.BackgroundReleaseDebt = false;
            e.ReconcileFailures = 0;
            if (AntiCheatThrottled(e))
            {
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(HasAntiCheatResources(e) && e.AntiCheatResources.JobActive ? 30 : AntiCheatAuditSeconds(e.Level)).Ticks;
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
                if (applied) e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(HasAntiCheatResources(e) && e.AntiCheatResources.JobActive ? 30 : AntiCheatAuditSeconds(e.Level)).Ticks;
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

        private bool ApplyThrottle(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity, uint[] originalCpuSets, bool antiCheat, ulong desiredAffinity, bool affinityOwned = false, Entry accountingEntry = null)
        {
            affinityOwned |= antiCheat;
            Interlocked.Increment(ref applyOperations);
            List<string> failures = new List<string>();
            if (accountingEntry != null && !PrepareResourceJob(accountingEntry, accountingEntry.AccountingPid))
            {
                failures.Add("resource-job-release");
                return StopFailedApply(failures);
            }
            uint desiredPriority = DesiredPriority(level, originalPriority, antiCheat);
            if (antiCheat && Native.GetPriorityClass(h) != desiredPriority
                && !AccountBackgroundWrite(accountingEntry, Native.SetPriorityClass(h, desiredPriority)))
            {
                failures.Add("priority-write");
            }
            if (failures.Count != 0)
            {
                return StopFailedApply(failures);
            }
            if (!antiCheat && accountingEntry != null) ApplyFirstOrdinaryValues(h, accountingEntry);
            if (accountingEntry != null && GpuPolicyNeedsRelease(accountingEntry)
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
            if (Native.PowerThrottlingSupported && !antiCheat)
            {
                int control, state;
                if (!Native.TryQueryPowerThrottling(h, out control, out state))
                    failures.Add("eco-readback");
                else if (!BackgroundEcoMatches(control, state)
                    && !AccountBackgroundWrite(accountingEntry, Native.RestorePowerThrottling(h, control | 1, state | 1)))
                    failures.Add("eco-write");
            }
            if (Native.PowerThrottlingSupported && antiCheat)
            {
                bool desiredTimerSealSucceeded = DesiredTimerSeal(level, true);
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
            if (antiCheat && Native.GetPriorityClass(h) != desiredPriority)
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
            if (Native.PowerThrottlingSupported && !EcoStateVisible(h, Native.EcoQoSWantMask(DesiredTimerSeal(level, antiCheat)), antiCheat))
            {
                failures.Add("eco-readback");
            }
            // 逐进程写入都成功后再加资源 Job 加不进去只记原因 照旧算压制成功
            if (failures.Count == 0 && accountingEntry != null) EnsureResourceJob(accountingEntry, accountingEntry.AccountingPid);
            LastApplyError = string.Join(",", failures.ToArray());
            return failures.Count == 0;
        }

        [Flags]
        private enum OrdinaryFields
        {
            None = 0,
            Priority = 1,
            Timer = 2,
            Both = Priority | Timer
        }

        private readonly Dictionary<int, KeyValuePair<long, OrdinaryFields>> ordinaryAttempts =
            new Dictionary<int, KeyValuePair<long, OrdinaryFields>>();

        internal void ResetOrdinaryBackgroundAttempts()
        {
            lock (sync) ordinaryAttempts.Clear();
        }

        private OrdinaryFields OrdinaryAttemptsOf(int pid, long creation)
        {
            KeyValuePair<long, OrdinaryFields> previous;
            return ordinaryAttempts.TryGetValue(pid, out previous) && previous.Key == creation
                ? previous.Value : OrdinaryFields.None;
        }

        private void RearmRestoredOrdinaryFields(Entry entry, OrdinaryFields restored)
        {
            if (restored == OrdinaryFields.None) return;
            lock (sync)
            {
                int pid = entry.AccountingPid;
                Entry current;
                if (!map.TryGetValue(pid, out current) || current != entry) return;
                KeyValuePair<long, OrdinaryFields> previous;
                if (!ordinaryAttempts.TryGetValue(pid, out previous) || previous.Key != entry.Creation) return;
                OrdinaryFields remaining = previous.Value & ~restored;
                entry.OrdinaryResnapshot |= previous.Value & restored;
                if (remaining == OrdinaryFields.None) ordinaryAttempts.Remove(pid);
                else ordinaryAttempts[pid] = new KeyValuePair<long, OrdinaryFields>(entry.Creation, remaining);
            }
        }

        private void ApplyFirstOrdinaryValues(IntPtr handle, Entry entry)
        {
            int pid = entry.AccountingPid;
            OrdinaryFields attempted = OrdinaryAttemptsOf(pid, entry.Creation);
            if (attempted != OrdinaryFields.Both)
            {
                entry.OrdinaryValues = true;
                ordinaryAttempts[pid] = new KeyValuePair<long, OrdinaryFields>(entry.Creation, OrdinaryFields.Both);
                OrdinaryFields resnapshot = entry.OrdinaryResnapshot;
                entry.OrdinaryResnapshot = OrdinaryFields.None;
                if ((attempted & OrdinaryFields.Priority) == 0)
                {
                    if ((resnapshot & OrdinaryFields.Priority) != 0)
                        entry.SetOriginalPriority(Native.GetPriorityClass(handle));
                    if (entry.OrigPri == 0)
                        LogSkippedOrdinaryField(entry, "priority-snapshot", 0);
                    else if (!AccountBackgroundWrite(entry, Native.SetPriorityClass(handle, IdlePriorityClass)))
                        LogSkippedOrdinaryField(entry, "priority-write", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    else
                        entry.OrdinaryPriorityOwned = entry.OrigPri != IdlePriorityClass;
                }

                if ((attempted & OrdinaryFields.Timer) == 0 && Native.PowerThrottlingSupported)
                {
                    if ((resnapshot & OrdinaryFields.Timer) != 0)
                    {
                        int control, state;
                        bool sampled = Native.TryQueryPowerThrottling(handle, out control, out state);
                        entry.OrigQoSControl = sampled ? control : -1;
                        entry.OrigQoSState = sampled ? state : -1;
                    }
                    if (AccountBackgroundWrite(entry, Native.ApplyEcoQoS(handle, true)))
                        entry.OrdinaryTimerOwned = Native.EcoQoSWantMask(true) == 5;
                    else
                        LogSkippedOrdinaryField(entry, "timer-write", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                }
                entry.OrdinaryOwnershipJournalPending |= entry.OrdinaryPriorityOwned || entry.OrdinaryTimerOwned;
                SavePendingOrdinaryOwnershipJournal(entry);
            }
        }

        private static void LogSkippedOrdinaryField(Entry entry, string field, int error)
        {
            Logger.Warn(Logger.Format("后台治理跳过单项 进程={0} 编号={1} 原因={2} 错误={3}",
                "Background governance skipped a field: process={0} pid={1} reason={2} error={3}",
                entry.Name, entry.AccountingPid, field, error));
        }

        private static bool BackgroundEcoMatches(int control, int state)
        { return (control & 1) != 0 && (state & 1) != 0; }

        private bool StopFailedApply(List<string> failed)
        {
            LastApplyError = string.Join(",", failed.ToArray());
            return false;
        }

        private static bool EcoStateVisible(IntPtr h, uint required, bool antiCheat)
        {
            int controlMask;
            int stateMask;
            return ConfirmEcoState(() => Native.TryQueryPowerThrottling(h, out controlMask, out stateMask)
                && (antiCheat ? PowerStateMatches(controlMask, stateMask, required) : BackgroundEcoMatches(controlMask, stateMask)), delegate
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
                if (reason == SuppressReason.Background && ActiveBackgroundJournalRound == null)
                    SavePendingOrdinaryOwnershipJournal(entry);
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
                    if (!(HasAntiCheatResources(appliedEntry) && AntiCheatThrottled(appliedEntry)) && !GpuPolicyNeedsRelease(appliedEntry) && VerifyBackgroundJob(appliedEntry) && ResourceJobMatches(appliedEntry, pid) && ThrottleMatches(processHandle, level, origPri, origAff, appliedEntry.OrigCpuSets, AntiCheatThrottled(appliedEntry), DesiredAffinityOf(appliedEntry), PlacementIsProtected(appliedEntry)))
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
                            Logger.Log(() => Logger.Format("{0}{1}（进程编号 {2}) {3}{4}", "{0}{1}(pid {2}) {3}{4}", Lang.T("log.gamemodesweep.1"), expectedName, pid, ApplyFailureText.Of(LastApplyError), Lang.T("log.suppressioncore.11")));
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
