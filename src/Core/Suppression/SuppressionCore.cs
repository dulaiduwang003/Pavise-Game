// @author bdth 2074055628@qq.com
// 文件用途 统一管理进程压制 快照 回读和恢复

using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    [Flags]
    internal enum SuppressReason
    {
        None = 0,
        AntiCheat = 1,
        Background = 2
    }

    internal enum AcquireResult
    {
        AlreadyThrottled,
        NewlyThrottled,
        NewlyProtected,
        AlreadyProtected,
        ApplyFailed
    }

    internal sealed partial class SuppressionCore
    {
        public const string StateFileName = "Pavise.suppression.state";
        public static volatile bool GpuDemoteEnabled;
        private sealed class Entry
        {
            public string Name;
            public string Group;
            public uint OrigPri;
            public ulong OrigAff;
            public int OrigIo = -1;
            public int OrigPg = -1;
            public uint[] OrigCpuSets;
            public int OrigGpu = -1;

            public int OrigQoSControl = -1;
            public int OrigQoSState = -1;
            public long Creation;
            public SuppressionLevel Level;
            public SuppressionLevel AntiCheatLevel;
            public SuppressionLevel BackgroundLevel;
            public bool Applied;
            public SuppressReason Reasons;

            public int ProtectedRetries;
            public long NextRetryTicks;

            public long NextReconcileTicks;
            public int ReconcileFailures;
            public int FastReconcileRemaining;

            public bool Journaled;

        }

        private enum RestoreResult { Restored, Gone, Protected }

        private const int ProtectedBackoffBaseSeconds = 8;
        private const int ProtectedBackoffCapSeconds = 300;
        private const int ProtectedBackoffMax = 8;
        private const int ReconcileFastSeconds = 4;
        private const int ReconcileStableBaseSeconds = 20;
        private const int ReconcileStableJitterSeconds = 11;
        private const int ReconcileFailureCapSeconds = 60;

        private readonly object sync = new object();
        private readonly object batchGate = new object();
        private readonly Dictionary<int, Entry> map = new Dictionary<int, Entry>();
        private ulong throttleMask;
        private readonly ulong allMask;
        private readonly string journalPath;
        private bool marked;
        private int batchDepth;
        private int journalDefer;
        private bool batchJournalDirty;
        private readonly Dictionary<int, string> batchApply = new Dictionary<int, string>();
        private readonly Dictionary<int, bool> batchApplyResults = new Dictionary<int, bool>();
        private readonly Dictionary<int, string> batchApplyErrors = new Dictionary<int, string>();
        private long applyOperations;
        public string LastApplyError { get; private set; }

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
                return applied.TryGetValue(pid, out value) && value;
            }

            public string FailureOf(int pid)
            {
                string value;
                return errors.TryGetValue(pid, out value) ? value : null;
            }
        }

        public SuppressionCore() : this(null) { }

        public SuppressionCore(string statePath)
        {
            throttleMask = CpuTopology.ThrottleMask;
            allMask = CpuTopology.AllMask;
            journalPath = statePath;
            LoadJournal();
        }

        public ulong ThrottleMask { get { return throttleMask; } }

        public static volatile bool SqueezeBackground;

        public void RefreshTopologyMasks() { throttleMask = CpuTopology.ThrottleMask; }
        internal long ApplyOperations { get { return Interlocked.Read(ref applyOperations); } }

        public void BeginBatch()
        {
            Monitor.Enter(batchGate);
            try { Monitor.Enter(sync); }
            catch
            {
                Monitor.Exit(batchGate);
                throw;
            }
            if (batchDepth == 0) { batchApplyResults.Clear(); batchApplyErrors.Clear(); }
            batchDepth++;
        }

        public BatchResult EndBatch()
        {
            if (!Monitor.IsEntered(batchGate) || !Monitor.IsEntered(sync))
                throw new InvalidOperationException("EndBatch requires a matching BeginBatch on the same thread.");

            List<KeyValuePair<int, string>> pending = null;
            bool journalOk = true;
            bool outermost = false;
            try
            {
                try
                {
                    if (batchDepth <= 0)
                        throw new InvalidOperationException("Suppression batch depth is invalid.");
                    batchDepth--;
                    if (batchDepth == 0)
                    {
                        outermost = true;
                        if (batchJournalDirty) journalOk = SaveJournalLocked();
                        batchJournalDirty = false;
                        if (batchApply.Count > 0)
                        {
                            pending = new List<KeyValuePair<int, string>>(batchApply);
                            batchApply.Clear();
                        }
                    }
                }
                finally { Monitor.Exit(sync); }

                if (pending != null)
                    foreach (KeyValuePair<int, string> item in pending)
                    {
                        bool ok;
                        string error = null;
                        try
                        {
                            if (journalOk) ok = ApplyQueued(item.Key, item.Value, out error);
                            else { ok = false; error = "journal-write"; }
                        }
                        catch (Exception ex) { ok = false; error = "apply-exception:" + ex.GetType().Name; }
                        lock (sync)
                        {
                            batchApplyResults[item.Key] = ok;
                            if (ok) batchApplyErrors.Remove(item.Key);
                            else batchApplyErrors[item.Key] = error ?? "unknown";
                        }
                    }

                if (!outermost) return new BatchResult(null);
                lock (sync)
                {
                    var snapshot = new Dictionary<int, bool>(
                        batchApplyResults);
                    var errorSnapshot = new Dictionary<int, string>(batchApplyErrors);
                    batchApplyResults.Clear();
                    batchApplyErrors.Clear();
                    RefreshThrottledCacheLocked();
                    RefreshGroupCountsLocked();
                    return new BatchResult(snapshot, errorSnapshot);
                }
            }
            finally { Monitor.Exit(batchGate); }
        }

        public AcquireResult Acquire(int pid, string name, SuppressReason reason, string group)
        {
            return Acquire(pid, name, reason, group, SuppressionLevel.Isolated);
        }

        public AcquireResult Acquire(int pid, string name, SuppressReason reason, string group, SuppressionLevel level)
        {
            if (level == SuppressionLevel.None) level = SuppressionLevel.Eco;
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                lock (sync)
                {
                    Entry e0;
                    if (map.TryGetValue(pid, out e0) && SameName(e0.Name, name))
                    {
                        e0.Reasons |= reason;
                        SetReasonLevel(e0, reason, level);
                        if (group != null && e0.Group == null) e0.Group = group;
                        return AcquireResult.AlreadyProtected;
                    }
                    var protectedEntry = new Entry { Name = name, Group = group, OrigPri = uint.MaxValue, Reasons = reason };
                    SetReasonLevel(protectedEntry, reason, level);
                    map[pid] = protectedEntry;
                    return AcquireResult.NewlyProtected;
                }
            }
            try
            {

                string img = Native.ImageName(h);
                if (img == null || !SameName(img, name)) return AcquireResult.AlreadyProtected;
                long currentCreation = 0, sampleCpu; ulong sampleIo;
                bool identityKnown = Native.QueryProcessSample(h, out currentCreation, out sampleCpu, out sampleIo);
                if (!identityKnown || currentCreation <= 0)
                    return AcquireResult.ApplyFailed;
                lock (sync)
                {
                    Entry e;
                    bool known = map.TryGetValue(pid, out e);
                    if (known && !SameName(e.Name, name)) { map.Remove(pid); known = false; e = null; }

                    if (known && e.OrigPri != uint.MaxValue && e.Creation <= 0)
                    {
                        map.Remove(pid);
                        batchApply.Remove(pid);
                        known = false;
                        e = null;
                    }
                    if (known && e.Creation > 0)
                    {
                        if (!identityKnown) return AcquireResult.AlreadyProtected;
                        if (e.Creation != currentCreation)
                        {
                            map.Remove(pid);
                            known = false;
                            e = null;
                        }
                    }

                    if (known && e.OrigPri != uint.MaxValue)
                    {
                        SuppressionLevel previousLevel = e.Level;
                        SuppressReason previousReasons = e.Reasons;
                        string previousGroup = e.Group;
                        e.Reasons |= reason;
                        if (group != null && e.Group == null) e.Group = group;
                        SetReasonLevel(e, reason, level);
                        bool metadataChanged = previousReasons != e.Reasons
                            || previousLevel != e.Level || !SameName(previousGroup, e.Group);
                        long now = DateTime.UtcNow.Ticks;
                        bool levelChanged = previousLevel != e.Level;
                        bool mustWrite = !e.Journaled || levelChanged
                            || !e.Applied && now >= e.NextReconcileTicks;
                        if ((metadataChanged || !e.Journaled)
                            && !PersistJournalLocked())
                            return AcquireResult.ApplyFailed;
                        if (mustWrite)
                        {
                            if (QueueApplyLocked(pid, name)) return AcquireResult.AlreadyThrottled;
                            e.Applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e));
                            ScheduleAfterApply(e, e.Applied, pid);
                            if (!e.Applied && TryNeutralizeUnwritableLocked(h, pid, e))
                                return AcquireResult.AlreadyProtected;
                            return e.Applied ? AcquireResult.AlreadyThrottled : AcquireResult.ApplyFailed;
                        }

                        if (!e.Applied)
                        {
                            RecordBatchApplyResultLocked(pid, false, "apply-pending-backoff");
                            return AcquireResult.AlreadyThrottled;
                        }
                        if (now < e.NextReconcileTicks)
                        {
                            RecordBatchApplyResultLocked(pid, true, null);
                            return AcquireResult.AlreadyThrottled;
                        }
                        bool matches = ThrottleMatches(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e))
;
                        if (matches)
                        {
                            ScheduleAfterMatch(e, pid);
                            RecordBatchApplyResultLocked(pid, true, null);
                            return AcquireResult.AlreadyThrottled;
                        }
                        if (QueueApplyLocked(pid, name)) return AcquireResult.AlreadyThrottled;
                        e.Applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e));
                        ScheduleAfterApply(e, e.Applied, pid);
                        if (!e.Applied && TryNeutralizeUnwritableLocked(h, pid, e))
                            return AcquireResult.AlreadyProtected;

                        return AcquireResult.AlreadyThrottled;
                    }

                    uint rawPri = Native.GetPriorityClass(h);

                    if (rawPri == 0) return AcquireResult.ApplyFailed;
                    ulong oaff = Native.QueryAffinity(h);
                    uint[] ocpuSets = Native.QueryCpuSets(h);
                    if (ocpuSets == null) return AcquireResult.ApplyFailed;
                    int oio = Native.QueryIoPriority(h);
                    int opg = Native.QueryPagePriority(h);

                    if ((!CpuTopology.MultiGroup && oaff == 0)
                        || oio < 0 || opg < 0)
                        return AcquireResult.ApplyFailed;
                    bool cpuSetsLookPavise = SameCpuSets(ocpuSets, CpuTopology.BackgroundCpuSetIds())
                        || SameCpuSets(ocpuSets, CpuTopology.InactiveBackgroundCpuSetIds());
                    bool affinityLooksPavise = !CpuTopology.MultiGroup && (oaff == throttleMask
                        || CpuTopology.InactiveThrottleMask != 0 && oaff == CpuTopology.InactiveThrottleMask);
                    bool residue = rawPri == Native.IDLE_PRIORITY_CLASS && oio == 0 && opg == 1
                        && (cpuSetsLookPavise || affinityLooksPavise);
                    uint orig = residue ? Native.NORMAL_PRIORITY_CLASS : rawPri;
                    if (residue && affinityLooksPavise) oaff = 0;
                    if (residue && cpuSetsLookPavise) ocpuSets = new uint[0];
                    if (residue && oio == 0) oio = -1;
                    if (residue && opg == 1) opg = -1;
                    int oqc, oqs;
                    if (!Native.TryQueryPowerThrottling(h, out oqc, out oqs)) { oqc = -1; oqs = -1; }
                    else if (residue && oqc == 1 && oqs == 1) { oqc = -1; oqs = -1; }
                    int ogpu;
                    if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out ogpu) != 0) ogpu = -1;
                    else if (residue && ogpu == Native.GpuPriorityIdle) ogpu = Native.GpuPriorityNormal;
                    long creation = currentCreation;

                    Entry active;
                    if (known)
                    {
                        e.OrigPri = orig; e.OrigAff = oaff; e.OrigIo = oio; e.OrigPg = opg; e.OrigCpuSets = ocpuSets;
                        e.OrigGpu = ogpu;
                        e.OrigQoSControl = oqc; e.OrigQoSState = oqs;
                        e.Reasons |= reason; SetReasonLevel(e, reason, level); e.Creation = creation; e.Applied = false;
                        e.Journaled = false;
                        if (group != null && e.Group == null) e.Group = group;
                        active = e;
                    }
                    else
                    {
                        var created = new Entry { Name = name, Group = group, OrigPri = orig, OrigAff = oaff,
                            OrigIo = oio, OrigPg = opg, OrigCpuSets = ocpuSets, OrigGpu = ogpu,
                            OrigQoSControl = oqc, OrigQoSState = oqs, Reasons = reason, Creation = creation };
                        SetReasonLevel(created, reason, level);
                        map[pid] = created;
                        active = created;
                    }
                    if (!PersistJournalLocked()) return AcquireResult.ApplyFailed;
                    bool queued = QueueApplyLocked(pid, name);
                    bool applied = queued || ApplyThrottle(h, level, orig, oaff, ocpuSets, DesiredGpu(active));
                    Entry appliedEntry;
                    if (map.TryGetValue(pid, out appliedEntry) && !queued)
                    {
                        appliedEntry.Applied = applied;
                        ScheduleAfterApply(appliedEntry, applied, pid);
                        if (!applied && TryNeutralizeUnwritableLocked(h, pid, appliedEntry))
                            return AcquireResult.NewlyProtected;
                    }
                    if (!marked) { marked = true; CrashGuard.MarkThrottle(throttleMask); }
                    return applied ? AcquireResult.NewlyThrottled : AcquireResult.ApplyFailed;
                }
            }
            finally { Native.CloseHandle(h); }
        }

        public bool Release(int pid, SuppressReason reason)
        {
            bool had;
            ReleaseOne(pid, reason, out had);
            return had;
        }

        public bool ReleaseIfCreation(
            int pid, SuppressReason reason, long expectedCreation)
        {
            if (expectedCreation <= 0) return false;
            bool had;
            ReleaseOne(
                pid, reason, expectedCreation, true, out had);
            return had;
        }

        public int ReleaseReason(SuppressReason reason)
        {
            int restored = 0; bool had;
            List<int> pids = PidsWith(reason);
            BeginJournalDefer();
            try
            {
                foreach (int pid in pids) restored += ReleaseOne(pid, reason, out had);
            }
            finally { EndJournalDefer(); }
            return restored;
        }

        private int ReleaseOne(int pid, SuppressReason reason, out bool had)
        {
            return ReleaseOne(pid, reason, 0, false, out had);
        }

        private int ReleaseOne(
            int pid, SuppressReason reason,
            long expectedCreation, bool requireCreation, out bool had)
        {
            Entry e;
            bool adjust = false;
            bool remaining = false;
            lock (sync)
            {
                had = map.TryGetValue(pid, out e) && (e.Reasons & reason) != 0;
                if (had && requireCreation)
                    had = e.Creation > 0
                        && e.Creation == expectedCreation;
                if (!had) return 0;
                SuppressionLevel previousLevel = e.Level;
                e.Reasons &= ~reason;
                if ((reason & SuppressReason.AntiCheat) != 0) e.AntiCheatLevel = SuppressionLevel.None;
                if ((reason & SuppressReason.Background) != 0) e.BackgroundLevel = SuppressionLevel.None;
                e.Level = EffectiveLevel(e);
                if (e.Reasons != SuppressReason.None)
                {
                    remaining = true;
                    adjust = e.OrigPri != uint.MaxValue && e.Journaled
                        && (previousLevel != e.Level || !e.Applied);
                    PersistJournalLocked();
                }
                else if (e.OrigPri == uint.MaxValue) { map.Remove(pid); PersistJournalLocked(); return 0; }
                else if (!e.Journaled && !e.Applied)
                {

                    map.Remove(pid);
                    batchApply.Remove(pid);
                    PersistJournalLocked();
                    return 0;
                }
            }
            if (adjust)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                bool applied = false;
                if (h != IntPtr.Zero) { try { if (SameProcess(h, e)) applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e)); } finally { Native.CloseHandle(h); } }
                lock (sync)
                {
                    Entry cur;
                    if (map.TryGetValue(pid, out cur) && cur == e)
                    {
                        cur.Applied = applied;
                        ScheduleAfterApply(cur, applied, pid);
                    }
                }
                return 0;
            }
            if (remaining) return 0;
            return TryRestore(pid, e) ? 1 : 0;
        }

        private bool TryRestore(int pid, Entry e)
        {
            RestoreResult r = RestoreOne(pid, e);
            bool reThrottle = false;
            lock (sync)
            {
                Entry cur;
                if (map.TryGetValue(pid, out cur) && cur == e)
                {
                    if (e.Reasons == SuppressReason.None)
                    {
                        if (r != RestoreResult.Protected) map.Remove(pid);
                    }
                    else if (r == RestoreResult.Restored) reThrottle = true;
                }
                TryClearMarkLocked();
            }
            if (reThrottle)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                bool applied = false;
                if (h != IntPtr.Zero) { try { if (SameProcess(h, e)) applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e)); } finally { Native.CloseHandle(h); } }
                lock (sync)
                {
                    Entry cur;
                    if (map.TryGetValue(pid, out cur) && cur == e)
                    {
                        cur.Applied = applied;
                        ScheduleAfterApply(cur, applied, pid);
                    }
                }
                return false;
            }
            if (r == RestoreResult.Protected)
            {
                lock (sync)
                {
                    Entry cur;
                    if (map.TryGetValue(pid, out cur) && cur == e)
                    {
                        if (e.ProtectedRetries == 0 && ShouldLogProtected(e.Name))
                            Logger.Log("还原 " + e.Name + " pid " + pid + " 暂被句柄保护挡住 快照保留待重试");
                        if (e.ProtectedRetries < ProtectedBackoffMax) e.ProtectedRetries++;
                        if (e.ProtectedRetries >= ProtectedBackoffMax)
                        {
                            e.NextRetryTicks = DateTime.MaxValue.Ticks;
                            if (ShouldLogProtected(e.Name + "-parked"))
                                Logger.Log("还原 " + e.Name + " pid " + pid
                                    + " 多次被句柄保护挡住 停止周期重试 快照与恢复日志已保留 下次启动自动恢复");
                        }
                        else
                        {
                            int delay = ProtectedBackoffBaseSeconds;
                            for (int i = 1; i < e.ProtectedRetries; i++)
                            {
                                delay *= 2;
                                if (delay >= ProtectedBackoffCapSeconds) break;
                            }
                            if (delay > ProtectedBackoffCapSeconds) delay = ProtectedBackoffCapSeconds;
                            e.NextRetryTicks = DateTime.UtcNow.AddSeconds(delay).Ticks;
                        }
                    }
                }
            }
            else if (r == RestoreResult.Restored && e.ProtectedRetries > 0)
                Logger.Log("补还原成功 " + e.Name + " pid " + pid + " 此前被句柄保护挡住 " + e.ProtectedRetries + " 次");
            return r == RestoreResult.Restored;
        }

        public void RetryPending()
        {
            List<KeyValuePair<int, Entry>> pending = null;
            long now = DateTime.UtcNow.Ticks;
            lock (sync)
                foreach (var kv in map)
                    if (kv.Value.Reasons == SuppressReason.None && now >= kv.Value.NextRetryTicks)
                    {
                        if (pending == null) pending = new List<KeyValuePair<int, Entry>>();
                        pending.Add(kv);
                    }
            if (pending == null) return;
            foreach (var kv in pending)
                if (TryRestore(kv.Key, kv.Value) && kv.Value.ProtectedRetries == 0)
                    Logger.Log("补还原成功 " + kv.Value.Name + " pid " + kv.Key);
        }

        private void TryClearMarkLocked()
        {
            PersistJournalLocked();
            if (!marked) return;
            foreach (var kv in map) if (kv.Value.OrigPri != uint.MaxValue) return;
            marked = false;
            CrashGuard.ReleaseThrottle(throttleMask);
        }

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
                            else Logger.Log("后台策略 " + expectedName + " pid " + pid
                                + " 检测到新建 GPU 上下文 纳入 GPU 调度让位");
                        }
                    }
                    int desiredGpu = DesiredGpu(currentEntry);
                    if (ThrottleMatches(h, level, pri, aff, cpuSets, desiredGpu))
                    {
                        if (!currentEntry.Applied && currentEntry.ReconcileFailures > 0)
                            Logger.Log("后台策略核验已生效 " + expectedName + " pid " + pid
                                + " 此前写入未完全生效 " + currentEntry.ReconcileFailures + " 次");
                        currentEntry.Applied = true;
                        ScheduleAfterMatch(currentEntry, pid);
                        return true;
                    }
                    bool previouslyApplied = currentEntry.Applied;
                    int previousFailures = currentEntry.ReconcileFailures;
                    currentEntry.Applied = ApplyThrottle(h, level, pri, aff, cpuSets, desiredGpu);
                    ScheduleAfterApply(currentEntry, currentEntry.Applied, pid);
                    if (currentEntry.Applied)
                    {
                        if (!previouslyApplied && previousFailures > 0)
                            Logger.Log("后台策略重试已生效 " + expectedName + " pid " + pid
                                + " 此前写入未完全生效 " + previousFailures + " 次");
                    }
                    else if (TryNeutralizeUnwritableLocked(h, pid, currentEntry)) return true;
                    else if (previousFailures < 3)
                        Logger.Log("后台压制 " + expectedName + "(pid " + pid + ") "
                            + ApplyFailureText.Of(LastApplyError) + " 按退避重试");
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

        private bool ThrottleMatches(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity,
            uint[] originalCpuSets, int desiredGpu)
        {
            uint desiredPriority = DesiredPriority(level, originalPriority);
            if (Native.GetPriorityClass(h) != desiredPriority) return false;
            if (desiredGpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0
                    && gpuCur != desiredGpu) return false;
            }
            int desiredIo = level >= SuppressionLevel.Isolated ? 0 : 1;
            int desiredPage = level >= SuppressionLevel.Isolated ? 1 : 3;
            if (Native.QueryIoPriority(h) != desiredIo || Native.QueryPagePriority(h) != desiredPage) return false;

            if (Native.PowerThrottlingSupported)
            {
                int qosControl, qosState;
                if (!Native.TryQueryPowerThrottling(h, out qosControl, out qosState)
                    || (qosControl & 1) == 0 || (qosState & 1) == 0) return false;
            }

            if (level >= SuppressionLevel.Isolated)
            {
                if (CpuTopology.HasEffectiveBackgroundPartition())
                {
                    uint[] backgroundCpuSets = CpuTopology.EffectiveBackgroundCpuSetIds();
                    bool cpuSetsMatch = backgroundCpuSets != null && backgroundCpuSets.Length > 0
                        && Native.CpuSetsMatch(h, backgroundCpuSets);
                    bool affinityFallback = !CpuTopology.MultiGroup && Native.QueryAffinity(h) == throttleMask;
                    if (!cpuSetsMatch && !affinityFallback) return false;
                }
                if (SqueezeBackground && !CpuTopology.MultiGroup)
                {
                    ulong squeeze = CpuTopology.BackgroundSqueezeMask();
                    if (squeeze != 0 && Native.QueryAffinity(h) != squeeze) return false;
                }
            }
            else
            {
                if (!Native.CpuSetsMatch(h, originalCpuSets ?? new uint[0])) return false;
                if (!CpuTopology.MultiGroup)
                {
                    ulong desiredAffinity = originalAffinity != 0 ? originalAffinity : allMask;
                    if (Native.QueryAffinity(h) != desiredAffinity) return false;
                }
            }
            return true;
        }

        internal static uint DesiredPriority(SuppressionLevel level, uint originalPriority)
        {
            uint desired = originalPriority == 0 || originalPriority == uint.MaxValue
                ? Native.NORMAL_PRIORITY_CLASS : originalPriority;
            if (level >= SuppressionLevel.Restrained)
                desired = level >= SuppressionLevel.Isolated
                    ? Native.IDLE_PRIORITY_CLASS : Native.BELOW_NORMAL_PRIORITY_CLASS;
            return desired;
        }

        internal static int DesiredGpuClass(bool demoteEnabled, SuppressReason reasons,
            SuppressionLevel backgroundLevel, int origGpu)
        {
            if (origGpu < 0) return -1;
            if (!demoteEnabled || (reasons & SuppressReason.Background) == 0
                || backgroundLevel < SuppressionLevel.Restrained) return origGpu;
            return backgroundLevel >= SuppressionLevel.Isolated
                ? Native.GpuPriorityIdle : Native.GpuPriorityBelowNormal;
        }

        private static int DesiredGpu(Entry e)
        {
            return DesiredGpuClass(GpuDemoteEnabled, e.Reasons, e.BackgroundLevel, e.OrigGpu);
        }

        private static void ScheduleAfterMatch(Entry e, int pid)
        {
            e.ReconcileFailures = 0;
            if (e.FastReconcileRemaining > 0)
            {
                e.FastReconcileRemaining--;
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(
                    e.FastReconcileRemaining > 0 ? ReconcileFastSeconds
                    : ReconcileStableBaseSeconds + PositiveMod(pid, ReconcileStableJitterSeconds)).Ticks;
                return;
            }
            e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(
                ReconcileStableBaseSeconds + PositiveMod(pid, ReconcileStableJitterSeconds)).Ticks;
        }

        private static void ScheduleAfterApply(Entry e, bool applied, int pid)
        {
            if (applied)
            {
                e.ReconcileFailures = 0;
                e.FastReconcileRemaining = 2;
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(ReconcileFastSeconds).Ticks;
                return;
            }
            if (e.ReconcileFailures < 8) e.ReconcileFailures++;
            int seconds = ReconcileFastSeconds;
            for (int i = 1; i < e.ReconcileFailures && seconds < ReconcileFailureCapSeconds; i++)
                seconds = Math.Min(ReconcileFailureCapSeconds, seconds * 2);
            e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(seconds).Ticks;
        }

        private static int PositiveMod(int value, int modulo)
        {
            int result = value % modulo;
            return result < 0 ? result + modulo : result;
        }

        private bool ApplyThrottle(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity,
            uint[] originalCpuSets, int desiredGpu)
        {
            Interlocked.Increment(ref applyOperations);
            var failed = new List<string>();
            uint desiredPriority = DesiredPriority(level, originalPriority);
            if (Native.GetPriorityClass(h) != desiredPriority)
                if (!Native.SetPriorityClass(h, desiredPriority)) failed.Add("priority-write");
            if (desiredGpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur != desiredGpu)
                {
                    if (Native.D3DKMTSetProcessSchedulingPriorityClass(h, desiredGpu) != 0) failed.Add("gpu-write");
                    else if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) != 0
                        || gpuCur != desiredGpu) failed.Add("gpu-readback");
                }
            }

            if (level >= SuppressionLevel.Isolated)
            {
                if (CpuTopology.HasEffectiveBackgroundPartition())
                {
                    uint[] backgroundCpuSets = CpuTopology.EffectiveBackgroundCpuSetIds();
                    if (!Native.CpuSetsMatch(h, backgroundCpuSets))
                    {
                        bool soft = Native.TrySetCpuSets(h, backgroundCpuSets);
                        if (soft && !Native.CpuSetsMatch(h, backgroundCpuSets))
                            failed.Add("cpu-sets-readback");
                        if (!soft && !CpuTopology.MultiGroup)
                        {
                            if (Native.QueryAffinity(h) != throttleMask
                                && !Native.SetProcessAffinityMask(h, (UIntPtr)throttleMask))
                                failed.Add("affinity-write");
                            if (Native.QueryAffinity(h) != throttleMask)
                                failed.Add("affinity-readback");
                        }
                        else if (!soft)
                            failed.Add("cpu-sets-write");
                    }
                }

                if (SqueezeBackground && !CpuTopology.MultiGroup)
                {
                    ulong squeeze = CpuTopology.BackgroundSqueezeMask();
                    if (squeeze != 0)
                    {
                        if (Native.QueryAffinity(h) != squeeze
                            && !Native.SetProcessAffinityMask(h, (UIntPtr)squeeze))
                            failed.Add("affinity-write");
                        if (Native.QueryAffinity(h) != squeeze)
                            failed.Add("affinity-readback");
                    }
                }
            }
            else
            {
                if (!Native.CpuSetsMatch(h, originalCpuSets)
                    && !Native.RestoreCpuSetsVerified(h, originalCpuSets))
                    failed.Add("cpu-sets-restore");
                if (!CpuTopology.MultiGroup)
                {
                    ulong desiredAffinity = originalAffinity != 0 ? originalAffinity : allMask;
                    if (Native.QueryAffinity(h) != desiredAffinity
                        && !Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity))
                        failed.Add("affinity-restore");
                    if (Native.QueryAffinity(h) != desiredAffinity)
                        failed.Add("affinity-restore-readback");
                }
            }
            int io = level >= SuppressionLevel.Isolated ? 0 : 1;
            if (Native.QueryIoPriority(h) != io
                && !Native.TrySetIoPriority(h, io))
                failed.Add("io-write");
            int pg = level >= SuppressionLevel.Isolated ? 1 : 3;
            if (Native.QueryPagePriority(h) != pg
                && !Native.TrySetPagePriority(h, pg))
                failed.Add("page-write");
            if (Native.PowerThrottlingSupported)
            {
                int qosControl;
                int qosState;
                if ((!Native.TryQueryPowerThrottling(h, out qosControl, out qosState)
                        || (qosControl & 1) == 0 || (qosState & 1) == 0)
                    && !Native.ApplyEcoQoS(h))
                    failed.Add("eco-write");
            }
            if (Native.GetPriorityClass(h) != desiredPriority) failed.Add("priority-readback");
            if (Native.QueryIoPriority(h) != io) failed.Add("io-readback");
            if (Native.QueryPagePriority(h) != pg) failed.Add("page-readback");
            if (Native.PowerThrottlingSupported && !EcoStateVisible(h)) failed.Add("eco-readback");
            LastApplyError = string.Join(",", failed.ToArray());
            return failed.Count == 0;
        }

        private static bool EcoStateVisible(IntPtr h)
        {
            for (int attempt = 0; ; attempt++)
            {
                int qosControl, qosState;
                if (Native.TryQueryPowerThrottling(h, out qosControl, out qosState)
                    && (qosControl & 1) != 0 && (qosState & 1) != 0) return true;
                if (attempt >= 80) return false;
                if (attempt < 77) Thread.SpinWait(1000);
                else Thread.Sleep(1);
            }
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, new uint[0]);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets)
        {

            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, -1, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState, int gpu)
        {
            bool ok = Native.RestoreCpuSetsVerified(h, cpuSets);
            uint desiredPriority = pri == 0 || pri == uint.MaxValue ? Native.NORMAL_PRIORITY_CLASS : pri;
            ok &= Native.SetPriorityClass(h, desiredPriority);
            ulong desiredAffinity = aff != 0 ? aff : allMask;
            if (!CpuTopology.MultiGroup) ok &= Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity);
            int rio = io >= 0 ? io : 2; ok &= Native.TrySetIoPriority(h, rio);
            int rpg = pg >= 0 ? pg : 5; ok &= Native.TrySetPagePriority(h, rpg);
            if (Native.PowerThrottlingSupported)
                ok &= Native.RestorePowerThrottling(h, qosControl, qosState);
            ok &= Native.GetPriorityClass(h) == desiredPriority;
            ok &= Native.QueryIoPriority(h) == rio;
            ok &= Native.QueryPagePriority(h) == rpg;
            if (!CpuTopology.MultiGroup) ok &= Native.QueryAffinity(h) == desiredAffinity;
            if (gpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur != gpu)
                {
                    Native.D3DKMTSetProcessSchedulingPriorityClass(h, gpu);
                    ok &= Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur == gpu;
                }
            }
            return ok;
        }

        private RestoreResult RestoreOne(int pid, Entry e)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hq == IntPtr.Zero)
                    return Native.LastOpenProcessFailureWasNoSuchProcess()
                        ? RestoreResult.Gone
                        : RestoreResult.Protected;
                try
                {
                    if (!Native.StillActive(hq)) return RestoreResult.Gone;
                    string nm = Native.ImageName(hq);
                    if (nm != null && !SameName(nm, e.Name)) return RestoreResult.Gone;
                    long creation, cpu; ulong io;
                    if (e.Creation > 0
                        && Native.QueryProcessSample(hq, out creation, out cpu, out io)
                        && creation != e.Creation)
                        return RestoreResult.Gone;
                    return RestoreResult.Protected;
                }
                finally { Native.CloseHandle(hq); }
            }
            try
            {
                if (!Native.StillActive(h)) return RestoreResult.Gone;
                if (e.Creation <= 0) return RestoreResult.Protected;
                if (e.Name != null)
                {
                    string cur = Native.ImageName(h);
                    if (cur == null) return RestoreResult.Protected;
                    if (!SameName(cur, e.Name)) return RestoreResult.Gone;
                }
                long creation, cpu; ulong io;
                if (e.Creation > 0)
                {
                    if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return RestoreResult.Protected;
                    if (creation != e.Creation) return RestoreResult.Gone;
                }
                if (RestoreValues(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg, allMask, e.OrigCpuSets,
                        e.OrigQoSControl, e.OrigQoSState, e.OrigGpu))
                    return RestoreResult.Restored;
                return Native.StillActive(h) ? RestoreResult.Protected : RestoreResult.Gone;
            }
            finally { Native.CloseHandle(h); }
        }

        private static bool SameProcess(IntPtr h, Entry e)
        {
            if (e.Creation <= 0) return false;
            if (e.Name != null)
            {
                string cur = Native.ImageName(h);
                if (cur == null || !SameName(cur, e.Name)) return false;
            }
            if (e.Creation > 0)
            {
                long creation, cpu; ulong io;
                if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return false;
                if (creation != e.Creation) return false;
            }
            return true;
        }

        private static bool SameName(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        internal const string SelfProtectedDetail = "self-protected";

        internal static bool FullyBlockedDetail(string detail)
        {
            return detail != null
                && detail.Contains("priority-write")
                && detail.Contains("io-write")
                && detail.Contains("page-write");
        }

        internal static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg,
            uint[] cpuSets, int qosControl, int qosState, int gpu)
        {
            if (pri == 0 || pri == uint.MaxValue || io < 0 || pg < 0) return false;
            if (Native.GetPriorityClass(h) != pri) return false;
            if (Native.QueryIoPriority(h) != io) return false;
            if (Native.QueryPagePriority(h) != pg) return false;
            if (!CpuTopology.MultiGroup && Native.QueryAffinity(h) != aff) return false;
            if (!Native.CpuSetsMatch(h, cpuSets ?? new uint[0])) return false;
            if (qosControl >= 0 && Native.PowerThrottlingSupported)
            {
                int qc, qs;
                if (!Native.TryQueryPowerThrottling(h, out qc, out qs)
                    || qc != qosControl || qs != qosState) return false;
            }
            if (gpu >= 0)
            {
                int g;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out g) != 0 || g != gpu) return false;
            }
            return true;
        }

        private bool TryNeutralizeUnwritableLocked(IntPtr h, int pid, Entry e)
        {
            if (e == null || e.OrigPri == uint.MaxValue) return false;
            if (!FullyBlockedDetail(LastApplyError)) return false;

            if (Native.PowerThrottlingSupported)
                Native.RestorePowerThrottling(h, e.OrigQoSControl, e.OrigQoSState);
            if (e.OrigGpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur != e.OrigGpu)
                    Native.D3DKMTSetProcessSchedulingPriorityClass(h, e.OrigGpu);
            }
            if (!SnapshotMatchesCurrent(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg,
                    e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState, e.OrigGpu))
                return false;

            e.OrigPri = uint.MaxValue;
            e.Applied = false;
            e.NextRetryTicks = 0;
            PersistJournalLocked();
            bool newlyListed = SelfProtectedRoster.Mark(e.Name);
            if (newlyListed || ShouldLogProtected(e.Name + "-unwritable"))
                Logger.Log("进程 " + e.Name + " pid " + pid
                    + " 拒绝全部策略写入且状态未被改动 自保护驱动 按句柄受保护处理"
                    + (newlyListed ? " 已记入免压制名单 后续对局直接跳过" : ""));
            return true;
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
            if (e.AntiCheatLevel >= SuppressionLevel.Frozen)
                e.AntiCheatLevel = SuppressionLevel.Isolated;
            if (e.BackgroundLevel >= SuppressionLevel.Frozen)
                e.BackgroundLevel = SuppressionLevel.Isolated;
            if (e.Level >= SuppressionLevel.Frozen)
                e.Level = SuppressionLevel.Isolated;
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
                    applied = ApplyThrottle(h, level, pri, aff, cpuSets, DesiredGpu(currentEntry));
                    if (!applied && TryNeutralizeUnwritableLocked(h, pid, currentEntry))
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
