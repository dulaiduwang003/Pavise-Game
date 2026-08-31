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

    internal enum BackgroundReleaseState
    {
        Ready,
        Pending,
        Gone,
        IdentityMismatch,
        OtherReasonActive
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
            public int OrigBoost = -1;
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
            public bool RestoreInFlight;

        }

        internal enum RestoreResult { Restored, Gone, Protected }

#if PAVISE_SELFTEST
        private readonly Func<int, long, string, RestoreResult> restoreForTest;
        internal Func<int, long, string, bool> RendererDiscardIdentityForTest;

        // 渲染进程释放的回归测试只用内存台账和假的还原结果
        // 不要初始化拓扑 恢复流程或者任何系统状态
        internal SuppressionCore(
            Func<int, long, string, RestoreResult> restoreForTest, bool inMemoryOnly)
        {
            if (!inMemoryOnly || restoreForTest == null)
                throw new ArgumentException("A fake restore is required for the in-memory core.");
            this.restoreForTest = restoreForTest;
            throttleMask = 0;
            allMask = 0;
            journalPath = null;
        }
#endif

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
        private Action mutationBegin;
        private Action mutationEnd;
        [ThreadStatic] private static string lastApplyError;
        public string LastApplyError
        {
            get { return lastApplyError; }
            private set { lastApplyError = value; }
        }

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

        public void ConfigureMutationBoundary(Action begin, Action end)
        {
            lock (sync)
            {
                mutationBegin = begin;
                mutationEnd = end;
            }
        }

        private void BeginMutation()
        {
            Action callback;
            lock (sync) callback = mutationBegin;
            if (callback != null) try { callback(); } catch { }
        }

        private void EndMutation()
        {
            Action callback;
            lock (sync) callback = mutationEnd;
            if (callback != null) try { callback(); } catch { }
        }

        private bool RunMutation(Func<bool> action)
        {
            BeginMutation();
            try { return action != null && action(); }
            finally { EndMutation(); }
        }


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
                            e.Applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e), e.OrigBoost, AntiCheatThrottled(e));
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
                        bool matches = ThrottleMatches(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e), AntiCheatThrottled(e));
                        if (matches)
                        {
                            ScheduleAfterMatch(e, pid);
                            RecordBatchApplyResultLocked(pid, true, null);
                            return AcquireResult.AlreadyThrottled;
                        }
                        if (QueueApplyLocked(pid, name)) return AcquireResult.AlreadyThrottled;
                        e.Applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e), e.OrigBoost, AntiCheatThrottled(e));
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
                    uint[] yieldIds = CpuTopology.BackgroundYieldCpuSetIds();
                    bool cpuSetsLookPavise = SameCpuSets(ocpuSets, CpuTopology.BackgroundCpuSetIds())
                        || SameCpuSets(ocpuSets, CpuTopology.InactiveBackgroundCpuSetIds())
                        || yieldIds != null && yieldIds.Length > 0 && SameCpuSets(ocpuSets, yieldIds);
                    ulong squeezeMask = CpuTopology.MultiGroup ? 0 : CpuTopology.BackgroundSqueezeMask();
                    bool affinityLooksPavise = !CpuTopology.MultiGroup && (oaff == throttleMask
                        || CpuTopology.InactiveThrottleMask != 0 && oaff == CpuTopology.InactiveThrottleMask
                        || squeezeMask != 0 && oaff == squeezeMask);
                    bool residue = CrashGuard.UncleanThrottleAtLaunch
                        && rawPri == Native.IDLE_PRIORITY_CLASS && oio == 0 && opg == 1
                        && (cpuSetsLookPavise || affinityLooksPavise);
                    uint orig = residue ? Native.NORMAL_PRIORITY_CLASS : rawPri;
                    if (residue && affinityLooksPavise) oaff = 0;
                    if (residue && cpuSetsLookPavise) ocpuSets = new uint[0];
                    if (residue && oio == 0) oio = -1;
                    if (residue && opg == 1) opg = -1;
                    int oqc, oqs;
                    if (!Native.TryQueryPowerThrottling(h, out oqc, out oqs)) { oqc = -1; oqs = -1; }
                    else if (residue && (oqc == 1 || oqc == 5) && oqs == oqc) { oqc = -1; oqs = -1; }
                    int oboost = Native.QueryBoostDisabled(h);
                    if (residue && oboost == 1) oboost = 0;
                    int ogpu;
                    if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out ogpu) != 0) ogpu = -1;
                    else if (residue && ogpu == Native.GpuPriorityIdle) ogpu = Native.GpuPriorityNormal;
                    long creation = currentCreation;

                    Entry active;
                    if (known)
                    {
                        e.OrigPri = orig; e.OrigAff = oaff; e.OrigIo = oio; e.OrigPg = opg; e.OrigCpuSets = ocpuSets;
                        e.OrigGpu = ogpu;
                        e.OrigQoSControl = oqc; e.OrigQoSState = oqs; e.OrigBoost = oboost;
                        e.Reasons |= reason; SetReasonLevel(e, reason, level); e.Creation = creation; e.Applied = false;
                        e.Journaled = false;
                        if (group != null && e.Group == null) e.Group = group;
                        active = e;
                    }
                    else
                    {
                        var created = new Entry { Name = name, Group = group, OrigPri = orig, OrigAff = oaff,
                            OrigIo = oio, OrigPg = opg, OrigCpuSets = ocpuSets, OrigGpu = ogpu,
                            OrigQoSControl = oqc, OrigQoSState = oqs, OrigBoost = oboost,
                            Reasons = reason, Creation = creation };
                        SetReasonLevel(created, reason, level);
                        map[pid] = created;
                        active = created;
                    }
                    if (!PersistJournalLocked()) return AcquireResult.ApplyFailed;
                    bool queued = QueueApplyLocked(pid, name);
                    bool applied = queued || ApplyThrottle(h, level, orig, oaff, ocpuSets, DesiredGpu(active), oboost,
                        (reason & SuppressReason.AntiCheat) != 0);
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

        // 调用方必须先把这个身份从新的后台 Acquire/Reconcile 里排除
        // 并在 Ready 之后重新校验它的原生身份
        // 清掉一个 reason 不等于原始值已经还原完
        // Reasons=None 但条目还活着 那仍然是一笔恢复欠账
        internal BackgroundReleaseState ReleaseBackgroundForRenderer(
            int pid, long expectedCreation, string expectedName)
        {
            if (pid <= 0 || expectedCreation <= 0 || string.IsNullOrWhiteSpace(expectedName))
                return BackgroundReleaseState.IdentityMismatch;

            lock (sync)
            {
                Entry current;
                if (!map.TryGetValue(pid, out current)) return BackgroundReleaseState.Ready;
                if (current.Creation != expectedCreation || !SameName(current.Name, expectedName))
                    return BackgroundReleaseState.IdentityMismatch;
                if ((current.Reasons & SuppressReason.Background) == 0)
                    return RendererReleaseStateOf(current);
            }

            bool had;
            RestoreResult? restoreResult;
            ReleaseOne(pid, SuppressReason.Background, expectedCreation, true,
                expectedName, out had, out restoreResult);

            lock (sync)
            {
                Entry current;
                if (!map.TryGetValue(pid, out current))
                    return restoreResult == RestoreResult.Gone
                        ? BackgroundReleaseState.Gone : BackgroundReleaseState.Ready;
                if (current.Creation != expectedCreation || !SameName(current.Name, expectedName))
                    return BackgroundReleaseState.IdentityMismatch;
                return RendererReleaseStateOf(current);
            }
        }

        private static BackgroundReleaseState RendererReleaseStateOf(Entry entry)
        {
            if ((entry.Reasons & ~SuppressReason.Background) != SuppressReason.None)
                return BackgroundReleaseState.OtherReasonActive;
            // 这里不要调 TryRestore 只有第一次释放才启动恢复
            // 后续轮询只观察 RetryPending 和它受保护的退避
            return BackgroundReleaseState.Pending;
        }

        // 合法的渲染进程可能复用一个 PID 而 map 里那个进程已经死了
        // 只忘掉这条过期账 绝不能为了删旧身份就对新进程做
        // Acquire 或 Restore 调用方还需要 ReleaseBackgroundForRenderer
        // 以及最后一次原生身份检查
        internal bool DiscardReusedRendererTracking(int pid, long expectedCreation, string expectedName)
        {
            if (pid <= 0 || expectedCreation <= 0 || string.IsNullOrWhiteSpace(expectedName)) return false;
            Entry observed;
            long observedCreation;
            lock (sync)
            {
                map.TryGetValue(pid, out observed);
                observedCreation = observed == null ? 0 : observed.Creation;
            }
            if (!RendererDiscardIdentityMatches(pid, expectedCreation, expectedName)) return false;
            lock (sync)
            {
                Entry current;
                if (!map.TryGetValue(pid, out current)) return true;
                // 只读查询还在飞的时候 Acquire 可能替换掉一个条目
                // 或者就地填上一个 creation=0 的占位
                if (!ReferenceEquals(current, observed) || current.Creation != observedCreation) return false;
                bool reused = current.Creation > 0 && current.Creation != expectedCreation;
                bool untouched = current.Creation == 0 && current.OrigPri == uint.MaxValue
                    && !current.Applied && !current.Journaled;
                if (!reused && !untouched) return false;

                map.Remove(pid);
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
#if PAVISE_SELFTEST
            if (RendererDiscardIdentityForTest != null)
                return RendererDiscardIdentityForTest(pid, expectedCreation, expectedName);
            // 没有身份接缝的内存夹具 必须一直留在内存里
            if (restoreForTest != null) return false;
#endif
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                string name = Native.ImageName(handle);
                long creation, cpu;
                ulong io;
                return SameName(name, expectedName)
                    && Native.QueryProcessSample(handle, out creation, out cpu, out io)
                    && creation == expectedCreation && Native.StillActive(handle);
            }
            catch { return false; }
            finally { Native.CloseHandle(handle); }
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
            RestoreResult? ignored;
            return ReleaseOne(pid, reason, expectedCreation, requireCreation, null, out had, out ignored);
        }

        private int ReleaseOne(
            int pid, SuppressReason reason,
            long expectedCreation, bool requireCreation, string expectedName,
            out bool had, out RestoreResult? restoreResult)
        {
            restoreResult = null;
            Entry e;
            bool adjust = false;
            bool remaining = false;
            lock (sync)
            {
                had = map.TryGetValue(pid, out e) && (e.Reasons & reason) != 0;
                if (had && requireCreation)
                    had = e.Creation > 0
                        && e.Creation == expectedCreation;
                if (had && expectedName != null)
                    had = SameName(e.Name, expectedName);
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
                if (h != IntPtr.Zero) { try { if (SameProcess(h, e)) applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e), e.OrigBoost, AntiCheatThrottled(e)); } finally { Native.CloseHandle(h); } }
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
            RestoreResult result;
            bool restored = TryRestore(pid, e, out result);
            restoreResult = result;
            return restored ? 1 : 0;
        }

        private bool TryRestore(int pid, Entry e, bool respectBackoff)
        {
            RestoreResult ignored;
            return TryRestore(pid, e, respectBackoff, out ignored);
        }

        private bool TryRestore(int pid, Entry e, out RestoreResult result)
        {
            return TryRestore(pid, e, false, out result);
        }

        private bool TryRestore(int pid, Entry e, bool respectBackoff, out RestoreResult result)
        {
            result = RestoreResult.Protected;
            lock (sync)
            {
                Entry current;
                // RetryPending 基于快照工作 这个条目被移除 替换或者重新获取之后
                // 它不能再去启动一次旧的还原
                // 也不能和同一条目上第一次 ReleaseOne 的还原叠在一起
                if (!map.TryGetValue(pid, out current) || !ReferenceEquals(current, e)
                    || e.Reasons != SuppressReason.None || e.RestoreInFlight
                    || (respectBackoff && DateTime.UtcNow.Ticks < e.NextRetryTicks)) return false;
                e.RestoreInFlight = true;
            }
            try { return TryRestoreOwned(pid, e, out result); }
            finally { lock (sync) e.RestoreInFlight = false; }
        }

        private bool TryRestoreOwned(int pid, Entry e, out RestoreResult result)
        {
            RestoreResult r;
#if PAVISE_SELFTEST
            if (restoreForTest != null) r = restoreForTest(pid, e.Creation, e.Name);
            else
#endif
                r = RestoreOne(pid, e);
            result = r;
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
                if (h != IntPtr.Zero) { try { if (SameProcess(h, e)) applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e), e.OrigBoost, AntiCheatThrottled(e)); } finally { Native.CloseHandle(h); } }
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
                            Logger.Log(Lang.T("log.suppressioncore.1") + e.Name + " pid " + pid + Lang.T("log.suppressioncore.2"));
                        if (e.ProtectedRetries < ProtectedBackoffMax) e.ProtectedRetries++;
                        if (e.ProtectedRetries >= ProtectedBackoffMax)
                        {
                            e.NextRetryTicks = DateTime.MaxValue.Ticks;
                            if (ShouldLogProtected(e.Name + "-parked"))
                                Logger.Log(Lang.T("log.suppressioncore.1") + e.Name + " pid " + pid
                                    + Lang.T("log.suppressioncore.3"));
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
                Logger.Log(Lang.T("log.suppressioncore.4") + e.Name + " pid " + pid + Lang.T("log.suppressioncore.5") + e.ProtectedRetries + Lang.T("t.gputhrottleprobe.5"));
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
                if (TryRestore(kv.Key, kv.Value, true) && kv.Value.ProtectedRetries == 0)
                    Logger.Log(Lang.T("log.suppressioncore.4") + kv.Value.Name + " pid " + kv.Key);
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
                            else Logger.Log(Lang.T("log.suppressioncore.6") + expectedName + " pid " + pid
                                + Lang.T("log.suppressioncore.7"));
                        }
                    }
                    int desiredGpu = DesiredGpu(currentEntry);
                    if (ThrottleMatches(h, level, pri, aff, cpuSets, desiredGpu, AntiCheatThrottled(currentEntry)))
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
                    currentEntry.Applied = ApplyThrottle(h, level, pri, aff, cpuSets, desiredGpu,
                        currentEntry.OrigBoost, AntiCheatThrottled(currentEntry));
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
                    applied = ApplyThrottle(h, level, pri, aff, cpuSets, DesiredGpu(currentEntry),
                        currentEntry.OrigBoost, AntiCheatThrottled(currentEntry));
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
