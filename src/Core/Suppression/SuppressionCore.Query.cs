// @author bdth 2074055628@qq.com
// 文件用途 压制状态查询 巡检回读与分组计数
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        public bool HasReason(int pid, SuppressReason reason)
        {
            lock (sync) { Entry e; return map.TryGetValue(pid, out e) && (e.Reasons & reason) != 0; }
        }

        public bool IsThrottled(int pid)
        {
            lock (sync) { Entry e; return map.TryGetValue(pid, out e) && e.OrigPri != uint.MaxValue && e.Applied; }
        }

        public SuppressionLevel LevelOf(int pid)
        {
            lock (sync) { Entry e; return map.TryGetValue(pid, out e) ? e.Level : SuppressionLevel.None; }
        }

        public SuppressionLevel LevelOf(int pid, SuppressReason reason)
        {
            lock (sync)
            {
                Entry e;
                if (!map.TryGetValue(pid, out e)) return SuppressionLevel.None;
                SuppressionLevel level = SuppressionLevel.None;
                if ((reason & SuppressReason.AntiCheat) != 0) level = e.AntiCheatLevel;
                if ((reason & SuppressReason.Background) != 0 && e.BackgroundLevel > level) level = e.BackgroundLevel;
                return level;
            }
        }

        public bool Reconcile(int pid, string expectedName, SuppressReason reason)
        {
            return Reconcile(pid, expectedName, reason, false);
        }

        internal bool Reconcile(int pid, string expectedName, SuppressReason reason, bool forceAudit)
        {
            uint pri;
            ulong aff;
            uint[] cpuSets;
            SuppressionLevel level;
            Entry entry;
            long now = DateTime.UtcNow.Ticks;
            lock (sync)
            {
                if (!map.TryGetValue(pid, out entry) || (entry.Reasons & reason) == 0
                    || entry.OrigPri == uint.MaxValue || !entry.Journaled
                    || !SameName(entry.Name, expectedName)) return false;
                if (entry.GaveUp) return true;
                if (!forceAudit && now < entry.NextReconcileTicks) return true;
                pri = entry.OrigPri;
                aff = entry.OrigAff;
                cpuSets = entry.OrigCpuSets;
                level = entry.Level;
            }

            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                lock (sync)
                {
                    Entry current;
                    if (map.TryGetValue(pid, out current) && current == entry)
                        ScheduleAfterApply(current, false, pid);
                }

                return true;
            }
            try
            {
                string current = Native.ImageName(h);
                if (current == null || !SameName(current, expectedName)) return false;
                long creation, cpu; ulong io;
                if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return false;
                lock (sync)
                {
                    Entry currentEntry;
                    if (!map.TryGetValue(pid, out currentEntry)
                        || currentEntry != entry
                        || (currentEntry.Reasons & reason) == 0
                        || currentEntry.Creation > 0 && currentEntry.Creation != creation)
                        return false;

                    if (currentEntry.Level != level
                        || currentEntry.OrigPri != pri
                        || currentEntry.OrigAff != aff
                        || !ReferenceEquals(currentEntry.OrigCpuSets, cpuSets))
                        return true;
                    if (currentEntry.OrigGpu < 0 && GpuDemoteEnabled
                        && (currentEntry.Reasons & SuppressReason.Background) != 0
                        && currentEntry.BackgroundLevel >= SuppressionLevel.Restrained)
                    {
                        int gpuNow;
                        if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuNow) == 0)
                        {
                            currentEntry.OrigGpu = gpuNow;
                            if (!PersistJournalLocked()) currentEntry.OrigGpu = -1;
                            else Logger.Log(Lang.T("log.suppressioncore.6") + expectedName + " pid " + pid
                                + Lang.T("log.suppressioncore.7"));
                        }
                    }
                    int desiredGpu = DesiredGpu(currentEntry);
                    if (ThrottleMatches(h, level, pri, aff, cpuSets, desiredGpu, AntiCheatThrottled(currentEntry), DesiredAffinityOf(currentEntry), currentEntry.SqueezeRefused))
                    {
                        if (!currentEntry.Applied && currentEntry.ReconcileFailures > 0)
                            Logger.Log(Lang.T("log.suppressioncore.8") + expectedName + " pid " + pid
                                + Lang.T("log.suppressioncore.9") + currentEntry.ReconcileFailures + Lang.T("t.gputhrottleprobe.5"));
                        currentEntry.Applied = true;
                        ScheduleAfterMatch(currentEntry, pid);
                        return true;
                    }
                    bool previouslyApplied = currentEntry.Applied;
                    int previousFailures = currentEntry.ReconcileFailures;
                    currentEntry.Applied = ApplyEntryLocked(h, currentEntry, pid);
                    ScheduleAfterApply(currentEntry, currentEntry.Applied, pid);
                    if (currentEntry.Applied)
                    {
                        if (!previouslyApplied && previousFailures > 0)
                            Logger.Log(Lang.T("log.suppressioncore.10") + expectedName + " pid " + pid
                                + Lang.T("log.suppressioncore.9") + previousFailures + Lang.T("t.gputhrottleprobe.5"));
                    }
                    else if (TryNeutralizeUnwritableLocked(h, pid, currentEntry)) return true;
                    else if (previousFailures < 3)
                        Logger.Log(Lang.T("log.gamemodesweep.1") + expectedName + "(pid " + pid + ") "
                            + ApplyFailureText.Of(LastApplyError) + Lang.T("log.suppressioncore.11"));
                    return true;
                }
            }
            finally { Native.CloseHandle(h); }
        }

        public bool AnyWith(SuppressReason reason)
        {
            lock (sync)
                foreach (var kv in map) if ((kv.Value.Reasons & reason) != 0) return true;
            return false;
        }

        public string NameOf(int pid)
        {
            lock (sync) { Entry e; return map.TryGetValue(pid, out e) ? e.Name : null; }
        }

        public long CreationOf(int pid)
        {
            lock (sync) { Entry e; return map.TryGetValue(pid, out e) ? e.Creation : 0; }
        }

        public List<int> PidsWith(SuppressReason reason)
        {
            var list = new List<int>();
            lock (sync)
                foreach (var kv in map) if ((kv.Value.Reasons & reason) != 0) list.Add(kv.Key);
            return list;
        }

        private int lastThrottledCount;

        public int CountThrottled(SuppressReason reason)
        {
            if (!Monitor.TryEnter(sync, 15)) return Volatile.Read(ref lastThrottledCount);
            try
            {
                int n = CountThrottledLocked(reason);
                Volatile.Write(ref lastThrottledCount, n);
                return n;
            }
            finally { Monitor.Exit(sync); }
        }

        public int ThrottledCountCached()
        {
            return Volatile.Read(ref lastThrottledCount);
        }

        private int CountThrottledLocked(SuppressReason reason)
        {
            int n = 0;
            foreach (var kv in map)
                if ((kv.Value.Reasons & reason) != 0 && kv.Value.OrigPri != uint.MaxValue && kv.Value.Applied) n++;
            return n;
        }

        private void RefreshThrottledCacheLocked()
        {
            Volatile.Write(ref lastThrottledCount, CountThrottledLocked(SuppressReason.Background));
        }

        private readonly Dictionary<string, int[]> lastGroupCounts =
            new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

        private void RefreshGroupCountsLocked()
        {
            var fresh = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in map)
            {
                if ((kv.Value.Reasons & SuppressReason.AntiCheat) == 0) continue;
                string key = kv.Value.Group ?? "";
                int[] slot;
                if (!fresh.TryGetValue(key, out slot)) { slot = new int[2]; fresh[key] = slot; }
                if (kv.Value.OrigPri == uint.MaxValue || !kv.Value.Applied) slot[1]++; else slot[0]++;
            }
            lock (lastGroupCounts)
            {
                foreach (KeyValuePair<string, int[]> kv in fresh) lastGroupCounts[kv.Key] = kv.Value;
                foreach (string key in new List<string>(lastGroupCounts.Keys))
                    if (!fresh.ContainsKey(key)) lastGroupCounts[key] = new int[2];
            }
        }

        public void AntiCheatGroupCountsCached(string groupKey, out int throttled, out int protectedCnt)
        {
            int t = 0, f = 0;
            lock (lastGroupCounts)
            {
                int[] last;
                if (lastGroupCounts.TryGetValue(groupKey ?? "", out last)) { t = last[0]; f = last[1]; }
            }
            throttled = t; protectedCnt = f;
        }

        public void AntiCheatGroupCounts(string groupKey, out int throttled, out int protectedCnt)
        {
            int t = 0, f = 0;
            string cacheKey = groupKey ?? "";
            if (!Monitor.TryEnter(sync, 15))
            {
                lock (lastGroupCounts)
                {
                    int[] last;
                    if (lastGroupCounts.TryGetValue(cacheKey, out last)) { t = last[0]; f = last[1]; }
                }
                throttled = t; protectedCnt = f;
                return;
            }
            try
            {
                foreach (var kv in map)
                    if ((kv.Value.Reasons & SuppressReason.AntiCheat) != 0 && SameName(kv.Value.Group, groupKey))
                    {
                        if (kv.Value.OrigPri == uint.MaxValue || !kv.Value.Applied) f++; else t++;
                    }
            }
            finally { Monitor.Exit(sync); }
            lock (lastGroupCounts) lastGroupCounts[cacheKey] = new int[] { t, f };
            throttled = t; protectedCnt = f;
        }

        private static readonly Dictionary<string, long> protectedLogTimes =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private static bool ShouldLogProtected(string name)
        {
            long now = DateTime.UtcNow.Ticks;
            lock (protectedLogTimes)
            {
                long last;
                string key = name ?? "";
                if (protectedLogTimes.TryGetValue(key, out last)
                    && now - last < TimeSpan.FromMinutes(10).Ticks) return false;
                protectedLogTimes[key] = now;
                return true;
            }
        }

        private static bool SameCpuSets(uint[] a, uint[] b)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return false;
            var set = new HashSet<uint>(b);
            foreach (uint id in a) if (!set.Contains(id)) return false;
            return true;
        }

        private static void SetReasonLevel(Entry e, SuppressReason reason, SuppressionLevel level)
        {
            if ((reason & SuppressReason.AntiCheat) != 0) e.AntiCheatLevel = level;
            if ((reason & SuppressReason.Background) != 0) e.BackgroundLevel = level;
            e.Level = EffectiveLevel(e);
        }

        // 反作弊压制走扫描安全构成 见 Apply 侧四个 Desired* 函数的说明
        //   同时挂两种原因时也按反作弊算 安全边界优先于压制力度
        private static bool AntiCheatThrottled(Entry e)
        {
            return (e.Reasons & SuppressReason.AntiCheat) != 0;
        }

        private static SuppressionLevel EffectiveLevel(Entry e)
        {
            return e.AntiCheatLevel > e.BackgroundLevel ? e.AntiCheatLevel : e.BackgroundLevel;
        }

        private bool PersistJournalLocked()
        {
            if (batchDepth > 0 || journalDefer > 0) { batchJournalDirty = true; return true; }
            return SaveJournalLocked();
        }

        private void BeginJournalDefer()
        {
            lock (sync) journalDefer++;
        }

        private void EndJournalDefer()
        {
            lock (sync)
            {
                if (journalDefer <= 0) return;
                journalDefer--;
                if (journalDefer == 0 && batchDepth == 0 && batchJournalDirty)
                {
                    batchJournalDirty = false;
                    SaveJournalLocked();
                }
            }
        }

        private bool QueueApplyLocked(int pid, string name)
        {
            if (batchDepth <= 0) return false;
            batchApply[pid] = name;
            return true;
        }

        private void RecordBatchApplyResultLocked(int pid, bool applied, string error)
        {
            if (batchDepth > 0 && !batchApply.ContainsKey(pid))
            {
                batchApplyResults[pid] = applied;
                if (applied) batchApplyErrors.Remove(pid);
                else batchApplyErrors[pid] = error ?? "unknown";
            }
        }

        private bool ApplyQueued(int pid, string expectedName, out string error)
        {
            error = null;
            uint pri;
            ulong aff;
            uint[] cpuSets;
            SuppressionLevel level;
            long expectedCreation;
            Entry queuedEntry;
            lock (sync)
            {
                Entry e;
                if (!map.TryGetValue(pid, out e) || e.Reasons == SuppressReason.None
                    || e.OrigPri == uint.MaxValue || !e.Journaled
                    || !SameName(e.Name, expectedName)) { error = "entry-state"; return false; }
                queuedEntry = e;
                pri = e.OrigPri; aff = e.OrigAff; level = e.Level; expectedCreation = e.Creation;
                cpuSets = e.OrigCpuSets;
            }
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) { error = "open-process"; return false; }
            try
            {
                string current = Native.ImageName(h);
                if (current == null || !SameName(current, expectedName)) { error = "identity-image"; return false; }
                if (expectedCreation > 0)
                {
                    long creation, cpu; ulong io;
                    if (!Native.QueryProcessSample(h, out creation, out cpu, out io) || creation != expectedCreation)
                    { error = "identity-creation"; return false; }
                }
                bool applied;
                lock (sync)
                {
                    Entry currentEntry;
                    if (!map.TryGetValue(pid, out currentEntry)
                        || currentEntry != queuedEntry
                        || currentEntry.Reasons == SuppressReason.None
                        || !SameName(currentEntry.Name, expectedName)
                        || currentEntry.Creation != expectedCreation
                        || currentEntry.Level != level
                        || currentEntry.OrigPri != pri
                        || currentEntry.OrigAff != aff
                        || !ReferenceEquals(currentEntry.OrigCpuSets, cpuSets))
                        { error = "entry-state"; return false; }
                    applied = ApplyEntryLocked(h, currentEntry, pid);
                    if (!applied && TryNeutralizeUnwritableLocked(h, pid, currentEntry))
                    {
                        error = SelfProtectedDetail;
                        return false;
                    }
                    if (!applied && GiveUpAntiCheatLocked(h, pid, currentEntry))
                    {
                        error = SelfProtectedDetail;
                        return false;
                    }
                    if (!applied) error = string.IsNullOrEmpty(LastApplyError) ? "apply" : LastApplyError;
                    currentEntry.Applied = applied;
                    ScheduleAfterApply(currentEntry, applied, pid);
                }
                return applied;
            }
            finally { Native.CloseHandle(h); }
        }
    }
}
