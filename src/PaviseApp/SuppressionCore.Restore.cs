using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {

        private bool RestoreEntryValues(IntPtr handle, Entry entry)
        {
            if (RestoreBudgetBlocked(entry, "restore-values", false)) return false;
            bool restored = RestoreEntryValuesCore(handle, entry);
            RecordFailureBudget(entry, "restore-values", restored, 0);
            return restored;
        }

        private bool RestoreEntryValuesCore(IntPtr handle, Entry entry)
        {
            try
            {
                if (EntryIdentity(handle, entry) != 0)
                {
                    return false;
                }
                return RestoreBackgroundPlacement(handle, entry) && (HasAntiCheatResources(entry) ? RestoreRecordedAntiCheatResources(handle, entry)
                    : RestoreOrdinaryEntryValues(handle, entry));
            }
            catch (Exception ex)
            {
                LastApplyError = ex.Message;
                return false;
            }
        }

        private static void StopEntry(Entry entry, bool restored, bool gone = false)
        {
            if (gone) ObserveBackgroundAccountingExit(entry);
            entry.GaveUp = true;
            entry.GaveUpRestored = restored;
            entry.SetApplied(false);
            entry.NextReconcileTicks = long.MaxValue;
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, new uint[0]);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, -1, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState, int gpu)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, boost, true);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost, bool restorePlacement)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, boost, restorePlacement, true);
        }

        internal static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost, bool restorePlacement, bool restorePriority)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, boost, restorePlacement, restorePriority, true);
        }

        internal static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost, bool restorePlacement, bool restorePriority, bool restorePage)
        {
            bool placementRestored = !restorePlacement || Native.RestoreCpuSetsVerified(h, cpuSets);
            uint priSnapshot = ((pri == 0 || pri == uint.MaxValue) ? 32u : pri);
            if (restorePriority) placementRestored &= Native.SetPriorityClass(h, priSnapshot);
            ulong affSnapshot = ((aff != 0) ? aff : allMask);
            if (restorePlacement && !CpuTopology.MultiGroup && Native.QueryAffinity(h) != affSnapshot)
            {
                placementRestored &= Native.SetProcessAffinityMask(h, (UIntPtr)affSnapshot);
            }
            int ioSnapshot = ((io >= 0) ? io : 2);
            placementRestored &= Native.TrySetIoPriority(h, ioSnapshot);
            int pgSnapshot = ((pg >= 0) ? pg : 5);
            if (restorePage)
            {
                placementRestored &= Native.TrySetPagePriority(h, pgSnapshot);
            }
            if (Native.PowerThrottlingSupported)
            {
                placementRestored &= Native.RestorePowerThrottling(h, qosControl, qosState);
            }
            if (boost >= 0)
            {
                if (Native.QueryBoostDisabled(h) != boost)
                {
                    placementRestored &= Native.TrySetBoostDisabled(h, boost == 1);
                }
                placementRestored &= Native.QueryBoostDisabled(h) == boost;
            }
            if (restorePriority) placementRestored &= Native.GetPriorityClass(h) == priSnapshot;
            placementRestored &= Native.QueryIoPriority(h) == ioSnapshot;
            if (restorePage) placementRestored &= Native.QueryPagePriority(h) == pgSnapshot;
            if (restorePlacement && !CpuTopology.MultiGroup)
            {
                placementRestored &= Native.QueryAffinity(h) == affSnapshot;
            }
            // 旧调用只有原值，不能据此推定当前不同值的所有者。未知读数也不能算恢复完成。
            if (gpu >= 0) placementRestored &= GpuPriorityLease.Legacy(gpu).Restore(h, () => 0, () => true);
            return placementRestored;
        }

        private bool SaveGpuJournal()
        {
            lock (sync) return SaveJournalLocked(true, true);
        }

        private bool RestoreOrdinaryEntryValues(IntPtr handle, Entry entry)
        { return RestoreOrdinaryEntryValues(handle, entry, allMask, () => SaveGpuJournal()); }

        private static bool RestoreOrdinaryEntryValues(IntPtr handle, Entry entry, ulong mask, Func<bool> checkpoint)
        {
            bool restored = RestoreValues(handle, entry.OrigPri, entry.OrigAff, entry.OrigIo, entry.OrigPg, mask,
                entry.OrigCpuSets, entry.OrigQoSControl, entry.OrigQoSState, -1, entry.OrigBoost, RestorePlacement(entry));
            bool gpuRestored = entry.GpuLease.Restore(handle, () => Native.QueryProcessIdentityState(handle, entry.Creation), checkpoint);
            return restored && gpuRestored;
        }

        private RestoreResult RestoreOne(int pid, Entry e)
        {
            if (!ReleaseBackgroundJob(e)) return RestoreResult.Protected;
            IntPtr restoreHandle = Native.OpenProcess(12800, false, pid);
            if (restoreHandle == IntPtr.Zero)
            {
                IntPtr queryHandle = Native.OpenProcess(4096, false, pid);
                if (queryHandle == IntPtr.Zero)
                {
                    if (!Native.LastOpenProcessFailureWasNoSuchProcess())
                    {
                        return RestoreResult.Protected;
                    }
                    return RestoreResult.Gone;
                }
                try
                {
                    if (!Native.StillActive(queryHandle))
                    {
                        return RestoreResult.Gone;
                    }
                    string observedName = Native.ImageName(queryHandle);
                    if (observedName != null && !SameName(observedName, e.Name))
                    {
                        return RestoreResult.Gone;
                    }
                    long creation;
                    long cpu;
                    ulong io;
                    if (e.Creation > 0 && Native.QueryProcessSample(queryHandle, out creation, out cpu, out io) && creation != e.Creation)
                    {
                        return RestoreResult.Gone;
                    }
                    return RestoreResult.Protected;
                }
                finally
                {
                    Native.CloseHandle(queryHandle);
                }
            }
            try
            {
                if (!Native.StillActive(restoreHandle))
                {
                    return RestoreResult.Gone;
                }
                if (e.Creation <= 0)
                {
                    return RestoreResult.Protected;
                }
                if (e.Name != null)
                {
                    string currentName = Native.ImageName(restoreHandle);
                    if (currentName == null)
                    {
                        return RestoreResult.Protected;
                    }
                    if (!SameName(currentName, e.Name))
                    {
                        return RestoreResult.Gone;
                    }
                }
                if (e.Creation > 0)
                {
                    long creation2;
                    long cpu2;
                    ulong io2;
                    if (!Native.QueryProcessSample(restoreHandle, out creation2, out cpu2, out io2))
                    {
                        return RestoreResult.Protected;
                    }
                    if (creation2 != e.Creation)
                    {
                        return RestoreResult.Gone;
                    }
                }
                if (RestoreBackgroundPlacement(restoreHandle, e) && (HasAntiCheatResources(e) ? RestoreRecordedAntiCheatResources(restoreHandle, e)
                    : RestoreOrdinaryEntryValues(restoreHandle, e)))
                {
                    return RestoreResult.Restored;
                }
                return (!Native.StillActive(restoreHandle)) ? RestoreResult.Gone : RestoreResult.Protected;
            }
            finally
            {
                Native.CloseHandle(restoreHandle);
            }
        }

        private static bool SameProcess(IntPtr h, Entry e)
        {
            if (e.Creation <= 0)
            {
                return false;
            }
            if (e.Name != null)
            {
                string processName = Native.ImageName(h);
                if (processName == null || !SameName(processName, e.Name))
                {
                    return false;
                }
            }
            if (e.Creation > 0)
            {
                long creation;
                long cpu;
                ulong io;
                if (!Native.QueryProcessSample(h, out creation, out cpu, out io))
                {
                    return false;
                }
                if (creation != e.Creation)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SameName(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool FullyBlockedDetail(string detail)
        {
            if (detail != null && detail.Contains("priority-write") && detail.Contains("io-write"))
            {
                return detail.Contains("page-write");
            }
            return false;
        }

        internal static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost)
        {
            return SnapshotMatchesCurrent(h, pri, aff, io, pg, cpuSets, qosControl, qosState, gpu, boost, true);
        }

        private static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost, bool placement)
        {
            if (!SnapshotMatchesCurrent(h, pri, aff, io, pg, cpuSets, qosControl, qosState, gpu, placement))
            {
                return false;
            }
            if (boost >= 0)
            {
                int boostDisabled = Native.QueryBoostDisabled(h);
                if (boostDisabled != boost)
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg, uint[] cpuSets, int qosControl, int qosState, int gpu)
        {
            return SnapshotMatchesCurrent(h, pri, aff, io, pg, cpuSets, qosControl, qosState, gpu, true);
        }

        private static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg, uint[] cpuSets, int qosControl, int qosState, int gpu, bool placement)
        {
            if (pri == 0 || pri == uint.MaxValue || io < 0 || pg < 0)
            {
                return false;
            }
            if (Native.GetPriorityClass(h) != pri)
            {
                return false;
            }
            if (Native.QueryIoPriority(h) != io)
            {
                return false;
            }
            if (Native.QueryPagePriority(h) != pg)
            {
                return false;
            }
            if (placement && !CpuTopology.MultiGroup && Native.QueryAffinity(h) != aff)
            {
                return false;
            }
            if (placement && !Native.CpuSetsMatch(h, cpuSets ?? new uint[0]))
            {
                return false;
            }
            int controlMask;
            int stateMask;
            if (qosControl >= 0 && Native.PowerThrottlingSupported && (!Native.TryQueryPowerThrottling(h, out controlMask, out stateMask) || controlMask != qosControl || stateMask != qosState))
            {
                return false;
            }
            int cls;
            if (gpu >= 0 && (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) != 0 || cls != gpu))
            {
                return false;
            }
            return true;
        }

        private bool TryNeutralizeUnwritableLocked(IntPtr h, int pid, Entry e)
        {
            if (HasAntiCheatResources(e) && AntiCheatThrottled(e)) return false;
            if (e == null || e.OrigPri == uint.MaxValue)
            {
                return false;
            }
            if (!FullyBlockedDetail(LastApplyError))
            {
                return false;
            }
            if (Native.PowerThrottlingSupported)
            {
                Native.RestorePowerThrottling(h, e.OrigQoSControl, e.OrigQoSState);
            }
            if (e.OrigBoost == 0)
            {
                Native.TrySetBoostDisabled(h, false);
            }
            if (!e.GpuLease.Restore(h, () => EntryIdentity(h, e), () => SaveGpuJournal())) return false;
            if (!EntrySnapshotMatches(h, e))
            {
                return false;
            }
            CompleteBackgroundRestoration(e.BackgroundRecoveryTicket, RestoreResult.Restored);
            e.SetOriginalPriority(uint.MaxValue);
            e.SetApplied(false);
            e.NextRetryTicks = 0L;
            PersistJournalLocked();
            bool markSucceeded = SelfProtectedRoster.Mark(e.Name);
            if (markSucceeded || ShouldLogProtected(e.Name + "-unwritable"))
            {
                Logger.Log(Lang.T("log.suppressioncoreapply.1", 0) + e.Name + " 进程编号 " + pid + Lang.T("log.suppressioncoreapply.2", 0) + (markSucceeded ? Lang.T("log.suppressioncoreapply.3", 0) : ""));
            }
            return true;
        }

        private bool TryRestore(int pid, Entry e, bool respectBackoff)
        {
            RestoreResult result;
            return TryRestore(pid, e, respectBackoff, out result);
        }

        private bool TryRestore(int pid, Entry e, out RestoreResult result)
        {
            return TryRestore(pid, e, false, out result);
        }

        private bool TryRestore(int pid, Entry e, bool respectBackoff, out RestoreResult result)
        {
            result = RestoreResult.Protected;
            BackgroundRecoveryLedger.Ticket recoveryTicket;
            lock (sync)
            {
                Entry currentEntry;
                if (RestoreBudgetBlocked(e, "restore", false)) return false;
                if (!map.TryGetValue(pid, out currentEntry) || !object.ReferenceEquals(currentEntry, e) || e.Reasons != 0 || e.RestoreInFlight)
                {
                    RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                    return false;
                }
                if (respectBackoff && DateTime.UtcNow.Ticks < e.NextRetryTicks)
                {
                    RecoveryAttemptTrace.DeferredUntil(e.NextRetryTicks);
                    return false;
                }
                e.RestoreInFlight = true;
                recoveryTicket = e.BackgroundRecoveryTicket;
            }
            try
            {
                return TryRestoreOwned(pid, e, recoveryTicket, out result);
            }
            finally
            {
                lock (sync)
                {
                    e.RestoreInFlight = false;
                }
            }
        }

        private bool TryRestoreOwned(int pid, Entry e, BackgroundRecoveryLedger.Ticket recoveryTicket, out RestoreResult result)
        {
            RestoreResult restoreOutcome = (result = ((!e.GaveUpRestored) ? RestoreOne(pid, e) : RestoreResult.Restored));
            bool restored = false;
            lock (sync)
            {
                Entry currentEntry;
                if (map.TryGetValue(pid, out currentEntry) && currentEntry == e)
                {
                    if (e.Reasons == SuppressReason.None)
                    {
                        if (restoreOutcome != RestoreResult.Protected)
                        {
                            RemoveEntryLocked(pid);
                            if (HasAntiCheatResources(e) && !SaveJournalLocked(true))
                            {
                                SetEntryLocked(pid, e); e.AntiCheatResourceRemovalPending = true;
                                restoreOutcome = result = RestoreResult.Protected;
                            }
                        }
                    }
                    else if (restoreOutcome == RestoreResult.Restored)
                    {
                        restored = true;
                    }
                }
                TryClearMarkLocked();
                RefreshThrottledCacheLocked();
                if (!restored) CompleteBackgroundRestoration(recoveryTicket, restoreOutcome);
            }
            if (restored)
            {
                IntPtr processHandle = Native.OpenProcess(12800, false, pid);
                bool applied = false;
                if (processHandle != IntPtr.Zero)
                {
                    try
                    {
                        applied = ReapplyReleasedEntry(processHandle, e, pid);
                    }
                    finally
                    {
                        Native.CloseHandle(processHandle);
                    }
                }
                lock (sync)
                {
                    Entry entryToRestore;
                    if (map.TryGetValue(pid, out entryToRestore) && entryToRestore == e)
                    {
                        entryToRestore.SetApplied(applied);
                        ScheduleAfterApply(entryToRestore, applied, pid);
                        RefreshThrottledCacheLocked();
                    }
                }
                return false;
            }
            switch (restoreOutcome)
            {
            case RestoreResult.Protected:
                lock (sync)
                {
                    Entry restoredEntry;
                    if (!map.TryGetValue(pid, out restoredEntry) || restoredEntry != e)
                    {
                        break;
                    }
                    if (e.ProtectedRetries == 0 && ShouldLogProtected(e.Name))
                    {
                        Logger.Log(Lang.T("log.suppressioncore.1", 0) + e.Name + " 进程编号 " + pid + Lang.T("log.suppressioncore.2", 0));
                    }
                    if (e.ProtectedRetries < 8)
                    {
                        e.ProtectedRetries++;
                    }
                    if (e.ProtectedRetries >= 8)
                    {
                        e.NextRetryTicks = DateTime.MaxValue.Ticks;
                        if (ShouldLogProtected(e.Name + "-parked"))
                        {
                            Logger.Log(Lang.T("log.suppressioncore.1", 0) + e.Name + " 进程编号 " + pid + Lang.T("log.suppressioncore.3", 0));
                        }
                        break;
                    }
                    int restoreOwnedValue = 8;
                    for (int i = 1; i < e.ProtectedRetries; i++)
                    {
                        restoreOwnedValue *= 2;
                        if (restoreOwnedValue >= 300)
                        {
                            break;
                        }
                    }
                    if (restoreOwnedValue > 300)
                    {
                        restoreOwnedValue = 300;
                    }
                    e.NextRetryTicks = DateTime.UtcNow.AddSeconds(restoreOwnedValue).Ticks;
                }
                break;
            case RestoreResult.Restored:
                if (e.ProtectedRetries > 0)
                {
                    SuccessLogBatch.Record("restored", e.Name, pid, e.Creation,
                        "previousFailures=" + e.ProtectedRetries + " 处理器组=" + e.Group);
                }
                break;
            }
            lock (sync) RecordFailureBudget(e, "restore", restoreOutcome != RestoreResult.Protected, e.NextRetryTicks);
            return restoreOutcome == RestoreResult.Restored;
        }

        public void RetryPending()
        {
            using (SuccessLogBatch.Begin("suppression-recovery cycle=" + PerformanceTuning.RecoveryCycleSerial))
                RetryPendingCore();
        }

        private void RetryPendingCore()
        {
            List<KeyValuePair<int, Entry>> pendingRestores = null;
            long ticks = DateTime.UtcNow.Ticks;
            lock (sync)
            {
                foreach (KeyValuePair<int, Entry> item in map)
                {
                    if (item.Value.Reasons == SuppressReason.None && !RestoreBudgetBlocked(item.Value, "restore", true) && ticks >= item.Value.NextRetryTicks)
                    {
                        if (pendingRestores == null)
                        {
                            pendingRestores = new List<KeyValuePair<int, Entry>>();
                        }
                        pendingRestores.Add(item);
                    }
                }
            }
            if (pendingRestores == null)
            {
                return;
            }
            foreach (KeyValuePair<int, Entry> pending in pendingRestores)
            {
                if (TryRestore(pending.Key, pending.Value, true) && pending.Value.ProtectedRetries == 0)
                {
                    SuccessLogBatch.Record("restored", pending.Value.Name, pending.Key, pending.Value.Creation,
                        "previousFailures=0 group=" + pending.Value.Group);
                }
            }
        }

        private void TryClearMarkLocked()
        {
            PersistJournalLocked();
            if (!marked)
            {
                return;
            }
            foreach (KeyValuePair<int, Entry> item in map)
            {
                if (item.Value.OrigPri != uint.MaxValue)
                {
                    return;
                }
            }
            marked = false;
            CrashGuard.ReleaseThrottle(throttleMask);
        }

    }
}
