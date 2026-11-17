using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {

        private sealed class FailureBudget
        {
            internal int Failures;
            internal long NotBefore;
        }

        private static string FailurePhaseIdentity(Entry entry, string action)
        {
            return action + "/" + PerformanceTuning.RecoveryCycleSerial + "/" + entry.Level + "/"
                + entry.BackgroundLevel + "/" + GpuDemoteEnabled
                + "/" + entry.RecoveryExitObserved;
        }

        private static FailureBudget FailureBudgetFor(Entry entry, string action)
        {
            string identity = FailurePhaseIdentity(entry, action);
            FailureBudget budget;
            if (!entry.FailureBudgets.TryGetValue(identity, out budget)) entry.FailureBudgets[identity] = budget = new FailureBudget();
            string prior;
            if (!entry.SelectedFailureBudgets.TryGetValue(action, out prior) || prior != identity)
            {
                entry.SelectedFailureBudgets[action] = identity;
                if (action == "restore") { entry.ProtectedRetries = budget.Failures; entry.NextRetryTicks = budget.NotBefore; }
                if (action == "layout") { entry.AutomaticLayoutFailures = budget.Failures; entry.AutomaticLayoutRetryAt = budget.NotBefore; }
                if (action == "reconcile") { entry.ReconcileFailures = budget.Failures; entry.NextReconcileTicks = budget.NotBefore; }
            }
            return budget;
        }

        private static void ReportFailureBudget(Entry entry, string action, FailureBudget budget)
        {
            if (budget.Failures < 8) { RecoveryAttemptTrace.DeferredUntil(budget.NotBefore); return; }
            string phase = FailurePhaseIdentity(entry, action);
            RecoveryAttemptTrace.Exhausted(() => budget.Failures < 8 || FailurePhaseIdentity(entry, action) != phase);
        }

        private static bool RestoreBudgetBlocked(Entry entry, string action, bool respectDeadline)
        {
            FailureBudget budget = FailureBudgetFor(entry, action);
            if (budget.Failures < 8 && (!respectDeadline || DateTime.UtcNow.Ticks >= budget.NotBefore)) return false;
            ReportFailureBudget(entry, action, budget);
            return true;
        }

        private static void RecordFailureBudget(Entry entry, string action, bool completed, long notBefore)
        {
            FailureBudget budget = FailureBudgetFor(entry, action);
            if (completed) { budget.Failures = 0; budget.NotBefore = 0; return; }
            budget.Failures = Math.Min(8, budget.Failures + 1);
            budget.NotBefore = budget.Failures >= 8 ? DateTime.MaxValue.Ticks : notBefore;
            ReportFailureBudget(entry, action, budget);
            SuccessLogBatch.FlushPending();
            Logger.Log("后台压制 " + action + "：目标=" + entry.Name + "/" + entry.Creation
                + " 阶段=" + PerformanceTuning.RecoveryCycleSerial + " 尝试=" + budget.Failures + "/8；保留所有权");
        }

        internal void ObserveTargetExit(int pid, long birth)
        {
            lock (sync)
            {
                Entry entry;
                if (!map.TryGetValue(pid, out entry) || entry.Creation != birth || entry.RecoveryExitObserved) return;
                entry.RecoveryExitObserved = true;
                FailureBudgetFor(entry, "restore");
                FailureBudgetFor(entry, "layout");
            }
        }

        private sealed class Entry
        {
            internal readonly Dictionary<string, FailureBudget> FailureBudgets = new Dictionary<string, FailureBudget>();
            internal readonly Dictionary<string, string> SelectedFailureBudgets = new Dictionary<string, string>();
            internal bool RecoveryExitObserved;
            public string Name;

            public string FailureTarget;

            public string Group;

            internal SuppressionCore CountOwner;
            internal int AccountingPid;
            internal long AccountingExitedBirth;
            internal bool AccountingForwardEffects;
            internal bool AccountingForwardEffectsUnknown;
            internal BackgroundRecoveryLedger.Ticket BackgroundRecoveryTicket;

            internal void SetGroup(string value)
            {
                Group = value;
            }

            internal void SetOriginalPriority(uint value)
            {
                if (OrigPri == uint.MaxValue && value != uint.MaxValue)
                {
                    BackgroundRecoveryTicket = null;
                    AccountingForwardEffects = false;
                    AccountingForwardEffectsUnknown = false;
                }
                OrigPri = value;
            }

            internal void SetApplied(bool value)
            {
                Applied = value;
                if (CountOwner != null) CountOwner.EnsureBackgroundAccountingLocked(AccountingPid, this);
            }

            internal void SetReasons(SuppressReason value)
            {
                SuppressReason previous = Reasons;
                Reasons = value;
                if (CountOwner != null) CountOwner.AccountBackgroundReasonsLocked(this, previous);
            }

            public uint OrigPri;

            public ulong OrigAff;

            public int OrigIo = -1;

            public int OrigPg = -1;

            public uint[] OrigCpuSets;

            public uint[] AutomaticLayoutSets;

            public bool AutomaticLayoutYielded;

            public bool AutomaticLayoutRestoring;

            public int AutomaticLayoutFailures;

            public long AutomaticLayoutRetryAt;

            public ulong SqueezeAff;

            public bool BackgroundPlacementOwned;

            // 后台 Job 回执继续独占亲和性恢复所有权，中性化后也不提前转交。
            public bool BackgroundJobPlacement, BackgroundJobActive;
            public int BackgroundPid;
            public GameAffinityJobLease BackgroundJobs;
            public string BackgroundAffinityDetail;

            public bool LegacyPlacementDebt;

            public bool BackgroundPlacementVerified;

            public bool BackgroundRestorePending;

            // 仅保留到已释放的后台所有权完成恢复或重新应用。
            public bool BackgroundReleaseDebt;

            public ulong BackgroundApplied;

            public ulong BackgroundPrevious;

            public bool SqueezeRefused;

            public int SqueezeFailures;

            public long SqueezeRetryAt;

            public bool GaveUp;

            public bool GaveUpRestored;

            public int OrigGpu = -1;

            public GpuPriorityLease GpuLease = new GpuPriorityLease();

            public int OrigQoSControl = -1;

            public int OrigQoSState = -1;

            public int OrigBoost = -1;

            public long Creation;

            public SuppressionLevel Level;

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

        internal enum RestoreResult
        {
            Restored,
            Gone,
            Protected
        }

        internal const string SelfProtectedDetail = "self-protected";

        public const string StateFileName = "Pavise.suppression.state";

        internal const string FailureAction = "BackgroundSuppression";

        private const int ProtectedBackoffBaseSeconds = 8;

        private const int ProtectedBackoffCapSeconds = 300;

        private const int ProtectedBackoffMax = 8;

        private const int ReconcileFastSeconds = 4;

        private const int ReconcileStableBaseSeconds = 20;

        private const int ReconcileStableJitterSeconds = 11;

        private const int ReconcileFailureCapSeconds = 60;

        private static readonly IAutomaticLayoutPlatform automaticLayoutPlatform = new AutomaticLayoutPlatform();

        public static volatile bool GpuDemoteEnabled;

        private readonly object sync = new object();

        private readonly object batchGate = new object();

        private readonly Dictionary<int, Entry> map = new Dictionary<int, Entry>();

        private readonly GameAffinityJobLease backgroundAffinityJobs = new GameAffinityJobLease();
        private bool backgroundJobJournalPending;
        private bool backgroundJobsStopCompleted;
        private readonly Dictionary<string, FailureBudget> backgroundJobJournalFailures = new Dictionary<string, FailureBudget>();

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

        [ThreadStatic]
        private static string lastApplyError;

        private int lastThrottledCount;

        private void SetEntryLocked(int pid, Entry entry)
        {
            Entry previous;
            map.TryGetValue(pid, out previous);
            if (previous != null) previous.CountOwner = null;
            map[pid] = entry;
            entry.CountOwner = this;
            entry.AccountingPid = pid;
            EnsureBackgroundAccountingLocked(pid, entry);
        }

        private void RemoveEntryLocked(int pid)
        {
            Entry previous;
            if (!map.TryGetValue(pid, out previous)) return;
            map.Remove(pid);
            previous.CountOwner = null;
        }

        private static readonly Dictionary<string, long> protectedLogTimes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        internal bool EnforceBackgroundAffinity;

        // 默认使用进程亲和性与恢复日志，避免为每个普通后台目标启动独立守护。
        internal bool BackgroundJobBackend;

        internal bool BackgroundReleasePending
        {
            get
            {
                lock (sync)
                {
                    if (backgroundJobJournalPending || backgroundAffinityJobs.HasPending) return true;
                    foreach (Entry entry in map.Values)
                        if ((entry.Reasons & SuppressReason.Background) != 0 || entry.BackgroundReleaseDebt || entry.BackgroundRestorePending
                            || (entry.Reasons == SuppressReason.None && entry.BackgroundPlacementOwned)) return true;
                    return false;
                }
            }
        }

        public string LastApplyError
        {
            get
            {
                return lastApplyError;
            }
            private set
            {
                lastApplyError = value;
            }
        }

        public ulong ThrottleMask
        {
            get
            {
                return throttleMask;
            }
        }

        internal long ApplyOperations
        {
            get
            {
                return Interlocked.Read(ref applyOperations);
            }
        }

        // 普通后台的临时提优限制使用本会话 Aggressive 标志，反作弊策略单独管理。
        internal static volatile bool BackgroundBoostLimit;

        public SuppressionCore()
            : this(null)
        {
        }

        public SuppressionCore(string statePath)
        {
            throttleMask = CpuTopology.ThrottleMask;
            allMask = CpuTopology.AllMask;
            journalPath = statePath;
            LoadJournal();
        }

        public bool HasReason(int pid, SuppressReason reason)
        {
            lock (sync)
            {
                Entry entry;
                return map.TryGetValue(pid, out entry) && (entry.Reasons & reason) != 0;
            }
        }

        public bool IsThrottled(int pid)
        {
            lock (sync)
            {
                Entry entry;
                return map.TryGetValue(pid, out entry) && entry.OrigPri != uint.MaxValue && entry.Applied;
            }
        }

        public SuppressionLevel LevelOf(int pid)
        {
            lock (sync)
            {
                Entry entry;
                return map.TryGetValue(pid, out entry) ? entry.Level : SuppressionLevel.None;
            }
        }

        public SuppressionLevel LevelOf(int pid, SuppressReason reason)
        {
            lock (sync)
            {
                Entry entry;
                if (!map.TryGetValue(pid, out entry))
                {
                    return SuppressionLevel.None;
                }
                return (reason & SuppressReason.Background) != 0 ? entry.BackgroundLevel : SuppressionLevel.None;
            }
        }

        public bool AnyWith(SuppressReason reason)
        {
            lock (sync)
            {
                foreach (KeyValuePair<int, Entry> item in map)
                {
                    if ((item.Value.Reasons & reason) != 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public string NameOf(int pid)
        {
            lock (sync)
            {
                Entry entry;
                return map.TryGetValue(pid, out entry) ? entry.Name : null;
            }
        }

        public long CreationOf(int pid)
        {
            lock (sync)
            {
                Entry entry;
                return map.TryGetValue(pid, out entry) ? entry.Creation : 0;
            }
        }

        public List<int> PidsWith(SuppressReason reason)
        {
            List<int> pids = new List<int>();
            lock (sync)
            {
                foreach (KeyValuePair<int, Entry> item in map)
                {
                    if ((item.Value.Reasons & reason) != 0)
                    {
                        pids.Add(item.Key);
                    }
                }
                return pids;
            }
        }

        internal KeyValuePair<int, long>[] CaptureAppliedActivity()
        {
            lock (sync)
            {
                var result = new List<KeyValuePair<int, long>>();
                foreach (KeyValuePair<int, Entry> item in map)
                    if (item.Value.Reasons != SuppressReason.None && item.Value.OrigPri != uint.MaxValue
                        && item.Value.Applied && !item.Value.RestoreInFlight)
                        result.Add(new KeyValuePair<int, long>(item.Key, item.Value.Creation));
                return result.ToArray();
            }
        }

        public int CountThrottled(SuppressReason reason)
        {
            if (!Monitor.TryEnter(sync, 15))
            {
                return Volatile.Read(ref lastThrottledCount);
            }
            try
            {
                int count = CountThrottledLocked(reason);
                if (reason == SuppressReason.Background) Volatile.Write(ref lastThrottledCount, count);
                return count;
            }
            finally
            {
                Monitor.Exit(sync);
            }
        }

        public int ThrottledCountCached()
        {
            return Volatile.Read(ref lastThrottledCount);
        }

        private int CountThrottledLocked(SuppressReason reason)
        {
            int processedCount = 0;
            foreach (KeyValuePair<int, Entry> item in map)
            {
                if ((item.Value.Reasons & reason) != 0 && item.Value.OrigPri != uint.MaxValue && item.Value.Applied)
                {
                    processedCount++;
                }
            }
            return processedCount;
        }

        private void RefreshThrottledCacheLocked()
        {
            Volatile.Write(ref lastThrottledCount, CountThrottledLocked(SuppressReason.Background));
        }

        private static bool ShouldLogProtected(string name)
        {
            long ticks = DateTime.UtcNow.Ticks;
            lock (protectedLogTimes)
            {
                string key = name ?? "";
                long lastLoggedAt;
                if (protectedLogTimes.TryGetValue(key, out lastLoggedAt) && ticks - lastLoggedAt < TimeSpan.FromMinutes(10.0).Ticks)
                {
                    return false;
                }
                protectedLogTimes[key] = ticks;
                return true;
            }
        }
    }
}
