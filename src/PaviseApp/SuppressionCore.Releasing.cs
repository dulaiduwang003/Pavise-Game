using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {

        public bool Release(int pid, SuppressReason reason)
        {
            bool had;
            ReleaseOne(pid, reason, out had);
            return had;
        }

        public bool ReleaseIfCreation(int pid, SuppressReason reason, long expectedCreation)
        {
            if (expectedCreation <= 0)
            {
                return false;
            }
            bool had;
            ReleaseOne(pid, reason, expectedCreation, true, out had);
            return had;
        }

        internal BackgroundReleaseState ReleaseBackgroundForRenderer(int pid, long expectedCreation, string expectedName)
        {
            if (pid <= 0 || expectedCreation <= 0 || string.IsNullOrWhiteSpace(expectedName))
            {
                return BackgroundReleaseState.IdentityMismatch;
            }
            lock (sync)
            {
                Entry entry;
                if (!map.TryGetValue(pid, out entry))
                {
                    return BackgroundReleaseState.Ready;
                }
                if (entry.Creation != expectedCreation || !SameName(entry.Name, expectedName))
                {
                    return BackgroundReleaseState.IdentityMismatch;
                }
                if ((entry.Reasons & SuppressReason.Background) == 0)
                {
                    return RendererReleaseStateOf(entry);
                }
            }
            bool had;
            RestoreResult? restoreResult;
            ReleaseOne(pid, SuppressReason.Background, expectedCreation, true, expectedName, out had, out restoreResult);
            lock (sync)
            {
                Entry currentEntry;
                if (!map.TryGetValue(pid, out currentEntry))
                {
                    return (restoreResult == RestoreResult.Gone) ? BackgroundReleaseState.Gone : BackgroundReleaseState.Ready;
                }
                if (currentEntry.Creation != expectedCreation || !SameName(currentEntry.Name, expectedName))
                {
                    return BackgroundReleaseState.IdentityMismatch;
                }
                return RendererReleaseStateOf(currentEntry);
            }
        }

        private static BackgroundReleaseState RendererReleaseStateOf(Entry entry)
        {
            if ((entry.Reasons & SuppressReason.Background) != 0 || entry.BackgroundRestorePending || (entry.BackgroundPlacementOwned && (entry.Reasons & SuppressReason.AntiCheat) != 0))
            {
                return BackgroundReleaseState.Pending;
            }
            if (((uint)entry.Reasons & 0xFFFFFFFDu) != 0)
            {
                return BackgroundReleaseState.OtherReasonActive;
            }
            return BackgroundReleaseState.Pending;
        }

        internal bool DiscardReusedRendererTracking(int pid, long expectedCreation, string expectedName)
        {
            if (pid <= 0 || expectedCreation <= 0 || string.IsNullOrWhiteSpace(expectedName))
            {
                return false;
            }
            Entry entry;
            long creation;
            lock (sync)
            {
                map.TryGetValue(pid, out entry);
                creation = ((entry == null) ? 0 : entry.Creation);
            }
            if (!RendererDiscardIdentityMatches(pid, expectedCreation, expectedName))
            {
                return false;
            }
            lock (sync)
            {
                Entry currentEntry;
                if (!map.TryGetValue(pid, out currentEntry))
                {
                    return true;
                }
                if (!object.ReferenceEquals(currentEntry, entry) || currentEntry.Creation != creation)
                {
                    return false;
                }
                bool processReused = currentEntry.Creation > 0 && currentEntry.Creation != expectedCreation;
                bool unownedPlaceholder = currentEntry.Creation == 0 && currentEntry.OrigPri == uint.MaxValue && !currentEntry.Applied && !currentEntry.Journaled;
                if (!processReused && !unownedPlaceholder)
                {
                    return false;
                }
                RemoveEntryLocked(pid);
                batchApply.Remove(pid);
                batchApplyResults.Remove(pid);
                batchApplyErrors.Remove(pid);
                TryClearMarkLocked();
                RefreshThrottledCacheLocked();
                RefreshGroupCountsLocked();
                return true;
            }
        }

        private bool RendererDiscardIdentityMatches(int pid, long expectedCreation, string expectedName)
        {
            IntPtr processHandle = Native.OpenProcess(4096, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                string a = Native.ImageName(processHandle);
                long creation;
                long cpu;
                ulong io;
                return SameName(a, expectedName) && Native.QueryProcessSample(processHandle, out creation, out cpu, out io) && creation == expectedCreation && Native.StillActive(processHandle);
            }
            catch
            {
                return false;
            }
            finally
            {
                Native.CloseHandle(processHandle);
            }
        }

        public int ReleaseReason(SuppressReason reason)
        {
            int releasedCount = 0;
            List<int> pids = PidsWith(reason);
            BeginJournalDefer();
            try
            {
                foreach (int pid in pids)
                {
                    bool had;
                    releasedCount += ReleaseOne(pid, reason, out had);
                }
                return releasedCount;
            }
            finally
            {
                EndJournalDefer();
            }
        }

        private int ReleaseOne(int pid, SuppressReason reason, out bool had)
        {
            return ReleaseOne(pid, reason, 0L, false, out had);
        }

        private int ReleaseOne(int pid, SuppressReason reason, long expectedCreation, bool requireCreation, out bool had)
        {
            RestoreResult? restoreResult;
            return ReleaseOne(pid, reason, expectedCreation, requireCreation, null, out had, out restoreResult);
        }

        private int ReleaseOne(int pid, SuppressReason reason, long expectedCreation, bool requireCreation, string expectedName, out bool had, out RestoreResult? restoreResult)
        {
            restoreResult = null;
            bool needsReapply = false;
            bool hasRemainingReasons = false;
            Entry entry;
            lock (sync)
            {
                had = map.TryGetValue(pid, out entry) && (entry.Reasons & reason) != 0;
                if (had && requireCreation)
                {
                    had = entry.Creation > 0 && entry.Creation == expectedCreation;
                }
                if (had && expectedName != null)
                {
                    had = SameName(entry.Name, expectedName);
                }
                if (!had)
                {
                    return 0;
                }
                SuppressionLevel level = entry.Level;
                bool hasSqueezedAffinity = entry.SqueezeAff != 0;
                entry.SetReasons(entry.Reasons & ~reason);
                if ((reason & SuppressReason.AntiCheat) != 0)
                {
                    entry.AntiCheatLevel = SuppressionLevel.None;
                }
                if ((reason & SuppressReason.Background) != 0)
                {
                    entry.BackgroundReleaseDebt |= entry.OrigPri != uint.MaxValue && (entry.Journaled || entry.Applied);
                    entry.BackgroundLevel = SuppressionLevel.None;
                    if ((entry.Reasons & ~reason & SuppressReason.AntiCheat) == 0)
                    {
                        entry.SqueezeAff = 0uL;
                    }
                }
                entry.Level = EffectiveLevel(entry);
                RefreshThrottledCacheLocked();
                if (entry.Reasons != 0)
                {
                    hasRemainingReasons = true;
                    needsReapply = entry.OrigPri != uint.MaxValue && entry.Journaled && (level != entry.Level || !entry.Applied || hasSqueezedAffinity || (reason & SuppressReason.AntiCheat) != 0);
                    if (!needsReapply) entry.BackgroundReleaseDebt = false;
                    PersistJournalLocked();
                }
                else
                {
                    if (entry.OrigPri == uint.MaxValue)
                    {
                        RemoveEntryLocked(pid);
                        PersistJournalLocked();
                        return 0;
                    }
                    if (!entry.Journaled && !entry.Applied)
                    {
                        RemoveEntryLocked(pid);
                        batchApply.Remove(pid);
                        PersistJournalLocked();
                        return 0;
                    }
                }
            }
            if (needsReapply)
            {
                IntPtr processHandle = Native.OpenProcess(12800, false, pid);
                bool applied = false;
                if (processHandle != IntPtr.Zero)
                {
                    try
                    {
                        applied = ReapplyReleasedEntry(processHandle, entry, pid);
                    }
                    finally
                    {
                        Native.CloseHandle(processHandle);
                    }
                }
                lock (sync)
                {
                    Entry currentEntry;
                    if (map.TryGetValue(pid, out currentEntry) && currentEntry == entry)
                    {
                        currentEntry.SetApplied(applied);
                        ScheduleAfterApply(currentEntry, applied, pid);
                        RefreshThrottledCacheLocked();
                    }
                }
                return 0;
            }
            if (hasRemainingReasons)
            {
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                return 0;
            }
            RestoreResult result;
            bool tryRestore = TryRestore(pid, entry, out result);
            restoreResult = result;
            if (!tryRestore)
            {
                return 0;
            }
            return 1;
        }

    }
}
