using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {

        public sealed class BatchResult
        {
            private readonly Dictionary<int, bool> applied;

            private readonly Dictionary<int, string> errors;

            internal BatchResult(Dictionary<int, bool> values)
                : this(values, null)
            {
            }

            internal BatchResult(Dictionary<int, bool> values, Dictionary<int, string> errorValues)
            {
                applied = values ?? new Dictionary<int, bool>();
                errors = errorValues ?? new Dictionary<int, string>();
            }

            public bool WasApplied(int pid)
            {
                bool value;
                if (applied.TryGetValue(pid, out value))
                {
                    return value;
                }
                return false;
            }

            public string FailureOf(int pid)
            {
                string value;
                if (!errors.TryGetValue(pid, out value))
                {
                    return null;
                }
                return value;
            }
        }

        private bool ReapplyReleasedEntry(IntPtr h, Entry e, int pid)
        {
            CaptureAntiCheatAdmission();
            lock (sync)
            {
                Entry trackedEntry;
                if (!map.TryGetValue(pid, out trackedEntry) || trackedEntry != e || e.Reasons == SuppressReason.None || !SameProcess(h, e))
                {
                    return false;
                }
                if ((e.Reasons & SuppressReason.Background) == 0 && !ReleaseBackgroundJob(e)) return false;
                if (HasAntiCheatResources(e) && !RestoreOwnedAntiCheatResources(h, e)) return false;
                if (AntiCheatThrottled(e)) return ApplyAntiCheatResources(h, e);
                if (HasAntiCheatResources(e))
                {
                    e.AntiCheatResources.Active = false;
                    if (!SaveAntiCheatResourceJournal(e)) { e.AntiCheatResources.Active = true; return false; }
                }
                return VerifyBackgroundJob(e) && ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e), e.OrigBoost, false, DesiredAffinityOf(e), PlacementIsProtected(e), e);
            }
        }

        public void BeginBatch()
        {
            Monitor.Enter(batchGate);
            try
            {
                Monitor.Enter(sync);
            }
            catch
            {
                Monitor.Exit(batchGate);
                throw;
            }
            if (batchDepth == 0)
            {
                batchApplyResults.Clear();
                batchApplyErrors.Clear();
            }
            batchDepth++;
        }

        public BatchResult EndBatch()
        {
            if (!Monitor.IsEntered(batchGate) || !Monitor.IsEntered(sync))
            {
                throw new InvalidOperationException("EndBatch requires a matching BeginBatch on the same thread.");
            }
            List<KeyValuePair<int, string>> pendingApplies = null;
            bool journalSaved = true;
            bool outermostBatch = false;
            try
            {
                try
                {
                    if (batchDepth <= 0)
                    {
                        throw new InvalidOperationException("Suppression batch depth is invalid.");
                    }
                    batchDepth--;
                    if (batchDepth == 0)
                    {
                        outermostBatch = true;
                        if (batchJournalDirty)
                        {
                            journalSaved = SaveJournalLocked();
                        }
                        batchJournalDirty = false;
                        if (batchApply.Count > 0)
                        {
                            pendingApplies = new List<KeyValuePair<int, string>>(batchApply);
                            batchApply.Clear();
                        }
                    }
                }
                finally
                {
                    Monitor.Exit(sync);
                }
                if (pendingApplies != null)
                {
                    foreach (KeyValuePair<int, string> item in pendingApplies)
                    {
                        string error = null;
                        bool applyQueuedSucceeded;
                        try
                        {
                            if (journalSaved)
                            {
                                applyQueuedSucceeded = ApplyQueued(item.Key, item.Value, out error);
                            }
                            else
                            {
                                applyQueuedSucceeded = false;
                                error = "journal-write";
                            }
                        }
                        catch (Exception ex)
                        {
                            applyQueuedSucceeded = false;
                            error = "apply-exception:" + ex.GetType().Name;
                        }
                        lock (sync)
                        {
                            batchApplyResults[item.Key] = applyQueuedSucceeded;
                            if (applyQueuedSucceeded)
                            {
                                batchApplyErrors.Remove(item.Key);
                            }
                            else
                            {
                                batchApplyErrors[item.Key] = error ?? "unknown";
                            }
                        }
                    }
                }
                if (!outermostBatch)
                {
                    return new BatchResult(null);
                }
                lock (sync)
                {
                    Dictionary<int, bool> values = new Dictionary<int, bool>(batchApplyResults);
                    Dictionary<int, string> errorValues = new Dictionary<int, string>(batchApplyErrors);
                    batchApplyResults.Clear();
                    batchApplyErrors.Clear();
                    RefreshThrottledCacheLocked();
                    RefreshGroupCountsLocked();
                    return new BatchResult(values, errorValues);
                }
            }
            finally
            {
                Monitor.Exit(batchGate);
            }
        }

        private bool QueueApplyLocked(int pid, string name)
        {
            if (batchDepth <= 0)
            {
                return false;
            }
            batchApply[pid] = name;
            return true;
        }

        private void RecordBatchApplyResultLocked(int pid, bool applied, string error)
        {
            if (batchDepth > 0 && !batchApply.ContainsKey(pid))
            {
                batchApplyResults[pid] = applied;
                if (applied)
                {
                    batchApplyErrors.Remove(pid);
                }
                else
                {
                    batchApplyErrors[pid] = error ?? "unknown";
                }
            }
        }

        private bool ApplyQueued(int pid, string expectedName, out string error)
        {
            CaptureAntiCheatAdmission();
            error = null;
            Entry entry;
            uint origPri;
            ulong origAff;
            SuppressionLevel level;
            long creation;
            uint[] origCpuSets;
            lock (sync)
            {
                Entry pendingEntry;
                if (!map.TryGetValue(pid, out pendingEntry) || pendingEntry.Reasons == SuppressReason.None || pendingEntry.OrigPri == uint.MaxValue || !pendingEntry.Journaled || !SameName(pendingEntry.Name, expectedName))
                {
                    error = "entry-state";
                    return false;
                }
                entry = pendingEntry;
                origPri = pendingEntry.OrigPri;
                origAff = pendingEntry.OrigAff;
                level = pendingEntry.Level;
                creation = pendingEntry.Creation;
                origCpuSets = pendingEntry.OrigCpuSets;
            }
            IntPtr processHandle = Native.OpenProcess(12800, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                error = "open-process";
                return false;
            }
            try
            {
                string processName = Native.ImageName(processHandle);
                if (processName == null || !SameName(processName, expectedName))
                {
                    error = "identity-image";
                    return false;
                }
                long creation2;
                long cpu;
                ulong io;
                if (creation > 0 && (!Native.QueryProcessSample(processHandle, out creation2, out cpu, out io) || creation2 != creation))
                {
                    error = "identity-creation";
                    return false;
                }
                bool applyEntryLockedSucceeded;
                lock (sync)
                {
                    Entry currentEntry;
                    if (!map.TryGetValue(pid, out currentEntry) || currentEntry != entry || currentEntry.Reasons == SuppressReason.None || !SameName(currentEntry.Name, expectedName) || currentEntry.Creation != creation || currentEntry.Level != level || currentEntry.OrigPri != origPri || currentEntry.OrigAff != origAff || !object.ReferenceEquals(currentEntry.OrigCpuSets, origCpuSets))
                    {
                        error = "entry-state";
                        return false;
                    }
                    applyEntryLockedSucceeded = ApplyEntryLocked(processHandle, currentEntry, pid);
                    if (!applyEntryLockedSucceeded && TryNeutralizeUnwritableLocked(processHandle, pid, currentEntry))
                    {
                        error = "self-protected";
                        return false;
                    }
                    if (!applyEntryLockedSucceeded && GiveUpAntiCheatLocked(processHandle, pid, currentEntry))
                    {
                        error = "self-protected";
                        return false;
                    }
                    if (!applyEntryLockedSucceeded)
                    {
                        error = (string.IsNullOrEmpty(LastApplyError) ? "apply" : LastApplyError);
                    }
                    currentEntry.SetApplied(applyEntryLockedSucceeded);
                    ScheduleAfterApply(currentEntry, applyEntryLockedSucceeded, pid);
                }
                return applyEntryLockedSucceeded;
            }
            finally
            {
                Native.CloseHandle(processHandle);
            }
        }

    }
}
