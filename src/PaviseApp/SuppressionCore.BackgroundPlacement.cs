using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {

        internal bool BackgroundAffinityPending
        {
            get
            {
                lock (sync)
                {
                    if (backgroundJobJournalPending || backgroundAffinityJobs.HasPending) return true;
                    foreach (Entry entry in map.Values) if (entry.BackgroundRestorePending) return true;
                    return false;
                }
            }
        }

        internal string BackgroundAffinityDetail()
        {
            lock (sync)
            {
                int jobs = 0, fallback = 0;
                HashSet<string> reasons = new HashSet<string>(StringComparer.Ordinal);
                foreach (Entry entry in map.Values)
                {
                    if (entry.SqueezeAff == 0 || !SqueezeOwnedBy(entry, SuppressReason.Background)) continue;
                    if (entry.BackgroundJobActive && VerifyBackgroundJob(entry)) jobs++;
                    else if (!entry.BackgroundJobPlacement && entry.BackgroundPlacementVerified) fallback++;
                    if (!string.IsNullOrEmpty(entry.BackgroundAffinityDetail)) reasons.Add(entry.BackgroundAffinityDetail);
                }
                return jobs == 0 && fallback == 0 && !BackgroundAffinityPending ? "" : "Job=" + jobs + "; process-fallback=" + fallback + (reasons.Count == 0 ? "" : "; " + string.Join("; ", new List<string>(reasons).ToArray()));
            }
        }

        internal bool StopBackgroundAffinityJobs()
        {
            lock (sync)
            {
                if (backgroundJobsStopCompleted) return FlushBackgroundJobJournalLocked(false);
                string reason;
                bool ok = backgroundAffinityJobs.Stop(out reason);
                backgroundJobsStopCompleted = ok;
                bool changed = false;
                foreach (Entry entry in map.Values)
                {
                    if (!entry.BackgroundJobPlacement) continue;
                    changed |= entry.BackgroundPlacementVerified || (ok ? entry.BackgroundJobActive || entry.BackgroundRestorePending || entry.SqueezeAff != 0 : !entry.BackgroundRestorePending);
                    entry.BackgroundPlacementVerified = false;
                    if (ok) { entry.BackgroundJobActive = false; entry.BackgroundRestorePending = false; entry.SqueezeAff = 0; }
                    else entry.BackgroundRestorePending = true;
                }
                return FlushBackgroundJobJournalLocked(changed) && ok;
            }
        }

        private bool FlushBackgroundJobJournalLocked(bool changed)
        {
            backgroundJobJournalPending |= changed;
            if (!backgroundJobJournalPending) return true;
            string phase = PerformanceTuning.RecoveryCycleSerial + "/" + EnforceBackgroundAffinity;
            FailureBudget budget;
            if (!backgroundJobJournalFailures.TryGetValue(phase, out budget))
                backgroundJobJournalFailures[phase] = budget = new FailureBudget();
            long now = DateTime.UtcNow.Ticks;
            if (budget.Failures >= 8 || now < budget.NotBefore)
            {
                ReportBackgroundJournalBudget(phase, budget);
                return false;
            }
            if (SaveJournalLocked(true)) return true;
            budget.Failures++;
            budget.NotBefore = budget.Failures >= 8 ? DateTime.MaxValue.Ticks : DateTime.UtcNow.AddSeconds(30).Ticks;
            ReportBackgroundJournalBudget(phase, budget);
            Logger.Log("后台压制日志：阶段=" + phase + " 尝试=" + budget.Failures + "/8；保留日志债务");
            return false;
        }

        private void ReportBackgroundJournalBudget(string phase, FailureBudget budget)
        {
            if (budget.Failures < 8) RecoveryAttemptTrace.DeferredUntil(budget.NotBefore);
            else RecoveryAttemptTrace.Exhausted(() => !backgroundJobJournalPending
                || PerformanceTuning.RecoveryCycleSerial + "/" + EnforceBackgroundAffinity != phase);
        }

        public bool SetSqueeze(int pid, long creation, string name, ulong squeezeMask, SuppressReason reason, out bool changed)
        {
            changed = false;
            if (squeezeMask != 0 && GameAffinityJobLease.IsGuardian(pid, creation)) return false;
            if (squeezeMask != 0 && reason == SuppressReason.AntiCheat)
            {
                return false;
            }
            if (squeezeMask != 0 && reason == SuppressReason.Background && (WeGameRuntimeProtection.IsProtectedRuntimeName(name) || PeripheralCatalog.IsInputAudioLike(name)))
            {
                return false;
            }
            if (reason != SuppressReason.AntiCheat && reason != SuppressReason.Background && squeezeMask != 0)
            {
                return false;
            }
            Entry entry;
            ulong squeezeAffValue;
            lock (sync)
            {
                if (!map.TryGetValue(pid, out entry) || !SqueezeOwnedBy(entry, reason) || entry.OrigPri == uint.MaxValue || !entry.Journaled || entry.Creation <= 0 || entry.Creation != creation || !SameName(entry.Name, name))
                {
                    return false;
                }
                squeezeAffValue = ((squeezeMask == 0) ? 0 : SuppressionAffinityPolicy.SqueezeTarget(squeezeMask, entry.OrigAff, entry.OrigCpuSets, allMask, CpuTopology.MultiGroup));
                if (squeezeMask != 0 && squeezeAffValue == 0)
                {
                    return false;
                }
                if (squeezeAffValue != 0 && entry.AutomaticLayoutSets != null && !RestoreAutomaticLayoutLocked(pid, entry))
                {
                    return false;
                }
                if (reason == SuppressReason.Background)
                {
                    if (squeezeAffValue == 0 && entry.SqueezeAff != 0)
                    {
                        entry.BackgroundRestorePending = true;
                        entry.BackgroundPlacementVerified = false;
                    }
                    if (squeezeAffValue != 0 && entry.BackgroundRestorePending)
                    {
                        return false;
                    }
                }
                if (entry.SqueezeAff == squeezeAffValue && VerifyBackgroundJob(entry) && (reason != SuppressReason.Background || (!entry.BackgroundRestorePending && (entry.BackgroundPlacementVerified || squeezeAffValue == 0))))
                {
                    return true;
                }
                if (squeezeAffValue != 0 && (entry.SqueezeRefused || entry.GaveUp))
                {
                    return false;
                }
                if (squeezeAffValue != 0 && DateTime.UtcNow.Ticks < entry.SqueezeRetryAt)
                {
                    return false;
                }
                if (squeezeAffValue == 0)
                {
                    if (RestoreBudgetBlocked(entry, "squeeze-restore", true)) return false;
                }
            }
            IntPtr processHandle = Native.OpenProcess(12800, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                NoteSqueezeAttempt(pid, entry, squeezeAffValue, false);
                return false;
            }
            bool ok = false;
            try
            {
                if (!SameProcess(processHandle, entry))
                {
                    return false;
                }
                lock (sync)
                {
                    Entry currentEntry;
                    if (!map.TryGetValue(pid, out currentEntry) || currentEntry != entry || !SqueezeOwnedBy(currentEntry, reason) || currentEntry.OrigPri == uint.MaxValue || currentEntry.Creation != creation)
                    {
                        return false;
                    }
                    ulong squeezeAff = currentEntry.SqueezeAff;
                    if (squeezeAffValue == 0)
                    {
                        if (RestoreBudgetBlocked(currentEntry, "squeeze-restore", true)) return false;
                    }
                    if (reason == SuppressReason.Background)
                    {
                        if (squeezeAffValue == 0 && !ReleaseBackgroundJob(currentEntry)) return false;
                        ulong mask;
                        if (!Native.TryQueryAffinity(processHandle, out mask))
                        {
                            return false;
                        }
                        ulong squeezeAffSnapshot = ((squeezeAff != 0) ? squeezeAff : currentEntry.OrigAff);
                        if (!currentEntry.BackgroundJobPlacement && !EnforceBackgroundAffinity && (mask != squeezeAffSnapshot || !Native.CpuSetsMatch(processHandle, currentEntry.OrigCpuSets ?? new uint[0])))
                        {
                            if (squeezeAff != 0)
                            {
                                currentEntry.SqueezeAff = 0uL;
                                currentEntry.SqueezeRefused = true;
                                currentEntry.BackgroundPlacementVerified = false;
                            }
                            currentEntry.BackgroundRestorePending = false;
                            return false;
                        }
                        ulong backgroundAppliedValue = ((squeezeAffValue != 0) ? squeezeAffValue : currentEntry.OrigAff);
                        if (backgroundAppliedValue == 0)
                        {
                            return false;
                        }
                        bool backgroundPlacementOwned = currentEntry.BackgroundPlacementOwned;
                        bool priorJobPlacement = currentEntry.BackgroundJobPlacement;
                        // 将恢复回执移交 Job 前，先恢复旧亲和性所有权。
                        bool keepProcessBackend = !priorJobPlacement && squeezeAff != 0;
                        if (squeezeAffValue != 0 && (!keepProcessBackend || mask != backgroundAppliedValue))
                        {
                            string admissionReason;
                            if (!GameThreadAffinityAdmission.Check(processHandle, pid, creation, mask, out admissionReason))
                            {
                                SetBackgroundBackend(currentEntry, "Forward affinity refused pid=" + pid + ": " + admissionReason);
                                return false;
                            }
                        }
                        ulong backgroundApplied = currentEntry.BackgroundApplied;
                        ulong backgroundPrevious = currentEntry.BackgroundPrevious;
                        currentEntry.BackgroundPlacementOwned = true;
                        currentEntry.BackgroundPrevious = ((squeezeAff != 0) ? squeezeAff : backgroundPrevious);
                        currentEntry.BackgroundApplied = backgroundAppliedValue;
                        if (squeezeAffValue != 0 && !keepProcessBackend)
                        {
                            currentEntry.BackgroundJobPlacement = true;
                            currentEntry.BackgroundJobs = backgroundAffinityJobs; currentEntry.BackgroundPid = pid;
                        }
                        if (!SaveJournalLocked(true))
                        {
                            currentEntry.BackgroundPlacementOwned = backgroundPlacementOwned;
                            currentEntry.BackgroundJobPlacement = priorJobPlacement;
                            currentEntry.BackgroundApplied = backgroundApplied;
                            currentEntry.BackgroundPrevious = backgroundPrevious;
                            return false;
                        }
                        bool affinitySet;
                        if (squeezeAffValue == 0 && currentEntry.BackgroundJobPlacement) affinitySet = true; // Job 释放过程已恢复本方值或保留外部状态。
                        else if (squeezeAffValue != 0 && !keepProcessBackend)
                        {
                            string jobReason;
                            backgroundJobsStopCompleted = false;
                            affinitySet = backgroundAffinityJobs.Ensure(pid, creation, name, currentEntry.OrigAff, backgroundAppliedValue, out jobReason);
                            if (affinitySet)
                            {
                                currentEntry.BackgroundJobActive = true;
                                SetBackgroundBackend(currentEntry, "Job affinity verified", true);
                            }
                            else if (GameThreadAffinityAdmission.IsRefusal(jobReason))
                            {
                                // 游戏自行设置放置而被拒绝时，阻止全部亲和性后端，并保留已有 Job 所有权。
                                currentEntry.BackgroundJobActive = backgroundAffinityJobs.IsActive(pid, creation);
                                currentEntry.BackgroundRestorePending = backgroundAffinityJobs.HasPending;
                                currentEntry.BackgroundPlacementVerified = false;
                                SetBackgroundBackend(currentEntry, "Job affinity refused: " + jobReason);
                                return false;
                            }
                            else if (backgroundAffinityJobs.HasPending || backgroundAffinityJobs.IsActive(pid, creation))
                            {
                                currentEntry.BackgroundRestorePending = true; currentEntry.BackgroundPlacementVerified = false;
                                SetBackgroundBackend(currentEntry, "Job recovery pending: " + jobReason);
                                return false;
                            }
                            else
                            {
                                // 唯一一次备用进程亲和性写入前，先持久保存所有权移交。
                                ulong currentMask;
                                if (!SameProcess(processHandle, currentEntry) || !Native.TryQueryAffinity(processHandle, out currentMask) || currentMask != mask)
                                { currentEntry.BackgroundRestorePending = true; currentEntry.BackgroundPlacementVerified = false; return false; }
                                string admissionReason;
                                if (!AdmitBackgroundProcessWrite(processHandle, pid, currentEntry, mask, backgroundAppliedValue, out currentMask, out admissionReason))
                                {
                                    SetBackgroundBackend(currentEntry, "Process affinity refused: " + admissionReason);
                                    return false;
                                }
                                currentEntry.BackgroundJobPlacement = currentEntry.BackgroundJobActive = false;
                                if (!SaveJournalLocked(true)) { currentEntry.BackgroundJobPlacement = true; currentEntry.BackgroundRestorePending = true; return false; }
                                SetBackgroundBackend(currentEntry, "Job unavailable (" + jobReason + "); process affinity fallback");
                                affinitySet = currentMask == backgroundAppliedValue || Native.SetProcessAffinityMask(processHandle, (UIntPtr)backgroundAppliedValue);
                            }
                        }
                        else if (squeezeAffValue != 0)
                        {
                            ulong currentMask; string admissionReason;
                            if (!AdmitBackgroundProcessWrite(processHandle, pid, currentEntry, mask, backgroundAppliedValue, out currentMask, out admissionReason))
                            {
                                SetBackgroundBackend(currentEntry, "Process affinity refused: " + admissionReason);
                                return false;
                            }
                            affinitySet = currentMask == backgroundAppliedValue || Native.SetProcessAffinityMask(processHandle, (UIntPtr)backgroundAppliedValue);
                        }
                        else affinitySet = Native.SetProcessAffinityMask(processHandle, (UIntPtr)backgroundAppliedValue);
                        bool tryQueryAffinity = Native.TryQueryAffinity(processHandle, out mask);
                        bool verified = affinitySet && ((squeezeAffValue == 0 && currentEntry.BackgroundJobPlacement) || (tryQueryAffinity && mask == backgroundAppliedValue));
                        currentEntry.BackgroundPlacementVerified = verified;
                        if (verified)
                        {
                            currentEntry.SqueezeAff = squeezeAffValue;
                            currentEntry.BackgroundRestorePending = false;
                            if (EnforceBackgroundAffinity)
                            {
                                ScheduleAfterApply(currentEntry, true, pid);
                            }
                            currentEntry.BackgroundPrevious = 0uL;
                            if (!SaveJournalLocked())
                            {
                                currentEntry.BackgroundPrevious = squeezeAff;
                            }
                            changed = squeezeAff != squeezeAffValue;
                            ok = true;
                            return true;
                        }
                        if (squeezeAffValue != 0 && (!tryQueryAffinity || mask == backgroundAppliedValue))
                        {
                            currentEntry.SqueezeAff = squeezeAffValue;
                        }
                        return false;
                    }
                    if (currentEntry.BackgroundPlacementOwned)
                    {
                        currentEntry.BackgroundPlacementOwned = false;
                        if (!SaveJournalLocked())
                        {
                            currentEntry.BackgroundPlacementOwned = true;
                            return false;
                        }
                        currentEntry.BackgroundRestorePending = false;
                    }
                    currentEntry.SqueezeAff = squeezeAffValue;
                    bool applyThrottleSucceeded = ApplyThrottle(processHandle, currentEntry.Level, currentEntry.OrigPri, currentEntry.OrigAff, currentEntry.OrigCpuSets, DesiredGpu(currentEntry), currentEntry.OrigBoost, AntiCheatThrottled(currentEntry), DesiredAffinityOf(currentEntry));
                    if (!applyThrottleSucceeded && squeezeAffValue != 0)
                    {
                        currentEntry.SqueezeAff = 0uL;
                        ulong origAff = ((currentEntry.OrigAff != 0) ? currentEntry.OrigAff : allMask);
                        if (!CpuTopology.MultiGroup && Native.QueryAffinity(processHandle) != origAff)
                        {
                            Native.SetProcessAffinityMask(processHandle, (UIntPtr)origAff);
                        }
                        return false;
                    }
                    currentEntry.SetApplied(applyThrottleSucceeded);
                    ScheduleAfterApply(currentEntry, applyThrottleSucceeded, pid);
                    changed = squeezeAff != currentEntry.SqueezeAff;
                    ok = applyThrottleSucceeded;
                    return applyThrottleSucceeded;
                }
            }
            finally
            {
                Native.CloseHandle(processHandle);
                NoteSqueezeAttempt(pid, entry, squeezeAffValue, ok);
            }
        }

        private void NoteSqueezeAttempt(int pid, Entry entry, ulong target, bool ok)
        {
            if (target == 0)
            {
                lock (sync) RecordFailureBudget(entry, "squeeze-restore", ok, DateTime.UtcNow.AddSeconds(30).Ticks);
                return;
            }
            lock (sync)
            {
                Entry trackedEntry;
                if (!map.TryGetValue(pid, out trackedEntry) || trackedEntry != entry)
                {
                    return;
                }
                if (ok)
                {
                    trackedEntry.SqueezeFailures = 0;
                    trackedEntry.SqueezeRetryAt = 0L;
                    return;
                }
                trackedEntry.SqueezeFailures++;
                trackedEntry.SqueezeRetryAt = DateTime.UtcNow.AddSeconds(30.0).Ticks;
                if (trackedEntry.SqueezeFailures >= 2)
                {
                    trackedEntry.SqueezeRefused = true;
                }
            }
        }

        private static bool RestoreBackgroundPlacement(IntPtr handle, Entry e)
        {
            if (e.BackgroundJobPlacement) return ReleaseBackgroundJob(e);
            if (e.AntiCheatPlacementExternal)
            {
                return true;
            }
            if (RestoreAutomaticLayoutValues(handle, e))
            {
                if (e.BackgroundPlacementOwned)
                {
                    if (CrashGuard.RestoreOwnedAffinity(handle, e.OrigAff, e.BackgroundApplied))
                    {
                        return CrashGuard.RestoreOwnedAffinity(handle, e.OrigAff, e.BackgroundPrevious);
                    }
                    return false;
                }
                return true;
            }
            return false;
        }

        private static bool VerifyBackgroundJob(Entry entry)
        {
            if (!entry.BackgroundJobPlacement) return true;
            if (!entry.BackgroundJobActive) return !entry.BackgroundRestorePending;
            string reason = "job-owner-unavailable";
            if (entry.BackgroundJobs != null && entry.BackgroundJobs.Verify(entry.BackgroundPid, entry.Creation, entry.SqueezeAff, out reason)) return true;
            entry.BackgroundPlacementVerified = false; entry.BackgroundRestorePending = true;
            SetBackgroundBackend(entry, "Job recovery pending: " + reason);
            return false;
        }

        private static bool ReleaseBackgroundJob(Entry entry)
        {
            if (!entry.BackgroundJobPlacement) return true;
            string reason;
            bool released = entry.BackgroundJobs != null
                ? entry.BackgroundJobs.Release(entry.BackgroundPid, entry.Creation, out reason)
                : GameAffinityJobLease.Recover(out reason) && GameAffinityJobLease.TargetRecoverySettled(entry.BackgroundPid, entry.Creation, out reason);
            if (!released)
            {
                entry.BackgroundRestorePending = true; entry.BackgroundPlacementVerified = false;
                SetBackgroundBackend(entry, "Job recovery pending: " + reason);
                return false;
            }
            entry.BackgroundJobActive = false; entry.BackgroundRestorePending = false; entry.BackgroundPlacementVerified = false;
            return true;
        }

        private static bool AdmitBackgroundProcessWrite(IntPtr process, int pid, Entry entry, ulong expected, ulong target, out ulong current, out string reason)
        {
            current = 0; reason = null;
            if (!SameProcess(process, entry) || !Native.TryQueryAffinity(process, out current) || current != expected)
            { reason = GameThreadAffinityAdmission.RefusalPrefix + "process-changed"; return false; }
            return current == target || GameThreadAffinityAdmission.Check(process, pid, entry.Creation, current, out reason);
        }

        private static void SetBackgroundBackend(Entry entry, string detail, bool verifiedSuccess = false)
        {
            if (entry.BackgroundAffinityDetail == detail) return;
            entry.BackgroundAffinityDetail = detail;
            if (verifiedSuccess)
            {
                SuccessLogBatch.Record("background-affinity-verified", entry.Name, entry.BackgroundPid, entry.Creation, detail);
                return;
            }
            SuccessLogBatch.FlushPending();
            Logger.Info("后台亲和性 " + entry.Name + " 进程编号=" + entry.BackgroundPid + " " + detail);
        }

        internal bool ReleaseAllBackgroundAffinityJobs()
        {
            lock (sync)
            {
                bool needed = backgroundAffinityJobs.HasActive || backgroundAffinityJobs.HasPending;
                foreach (Entry entry in map.Values)
                    if (entry.BackgroundJobPlacement && (entry.BackgroundJobActive || entry.BackgroundRestorePending || entry.SqueezeAff != 0)) needed = true;
                if (!needed) return FlushBackgroundJobJournalLocked(false);
                string reason;
                bool ok = backgroundAffinityJobs.ReleaseAll(out reason);
                bool changed = false;
                foreach (Entry entry in map.Values)
                {
                    if (!entry.BackgroundJobPlacement) continue;
                    bool active = entry.BackgroundJobActive, pending = entry.BackgroundRestorePending, verified = entry.BackgroundPlacementVerified;
                    ulong squeeze = entry.SqueezeAff;
                    if (!ReleaseBackgroundJob(entry)) { ok = false; continue; }
                    entry.SqueezeAff = 0;
                    changed |= active != entry.BackgroundJobActive || pending != entry.BackgroundRestorePending || verified != entry.BackgroundPlacementVerified || squeeze != entry.SqueezeAff;
                }
                return FlushBackgroundJobJournalLocked(changed) && ok;
            }
        }

        private void DetectBackgroundPlacementChange(IntPtr handle, Entry e)
        {
            ulong mask;
            if (!EnforceBackgroundAffinity && !e.BackgroundRestorePending && e.BackgroundPlacementOwned && !e.SqueezeRefused && (e.Reasons & SuppressReason.AntiCheat) == 0 && (!Native.TryQueryAffinity(handle, out mask) || mask != ((e.SqueezeAff == 0) ? e.OrigAff : e.SqueezeAff) || !Native.CpuSetsMatch(handle, e.OrigCpuSets ?? new uint[0])))
            {
                e.SqueezeRefused = true;
                e.SqueezeAff = 0uL;
                e.BackgroundPlacementVerified = false;
            }
        }

        private static bool SqueezeOwnedBy(Entry e, SuppressReason reason)
        {
            if (e.AntiCheatPlacementExternal || reason == SuppressReason.AntiCheat)
            {
                return false;
            }
            bool valuesMatch = (e.Reasons & SuppressReason.AntiCheat) != 0;
            if (!valuesMatch)
            {
                return (e.Reasons & SuppressReason.Background) != 0;
            }
            return false;
        }

        public int ClearSqueezes(SuppressReason reason)
        {
            List<KeyValuePair<int, Entry>> entriesToClear = new List<KeyValuePair<int, Entry>>();
            lock (sync)
            {
                foreach (KeyValuePair<int, Entry> mapEntry in map)
                {
                    if ((mapEntry.Value.SqueezeAff != 0 || (reason == SuppressReason.Background && mapEntry.Value.BackgroundRestorePending)) && SqueezeOwnedBy(mapEntry.Value, reason))
                    {
                        entriesToClear.Add(mapEntry);
                    }
                }
            }
            int processedCount = 0;
            foreach (KeyValuePair<int, Entry> pending in entriesToClear)
            {
                bool changed;
                SetSqueeze(pending.Key, pending.Value.Creation, pending.Value.Name, 0uL, reason, out changed);
                if (changed)
                {
                    processedCount++;
                }
            }
            return processedCount;
        }

        public int SqueezedCount(SuppressReason reason, bool verifiedOnly = true)
        {
            int processedCount = 0;
            lock (sync)
            {
                foreach (KeyValuePair<int, Entry> item in map)
                {
                    if (verifiedOnly && reason == SuppressReason.Background) VerifyBackgroundJob(item.Value);
                    if ((item.Value.SqueezeAff != 0 || (reason == SuppressReason.Background && item.Value.BackgroundRestorePending)) && SqueezeOwnedBy(item.Value, reason) && (!verifiedOnly || reason != SuppressReason.Background || (item.Value.BackgroundPlacementVerified && !item.Value.BackgroundRestorePending)))
                    {
                        processedCount++;
                    }
                }
                return processedCount;
            }
        }

        public bool IsSqueezed(int pid)
        {
            lock (sync)
            {
                Entry entry;
                return map.TryGetValue(pid, out entry) && VerifyBackgroundJob(entry) && entry.SqueezeAff != 0 && !entry.BackgroundRestorePending && (!entry.BackgroundPlacementOwned || entry.BackgroundPlacementVerified);
            }
        }

    }
}
