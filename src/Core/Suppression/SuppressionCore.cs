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
        // 硬亲和开着才认后台落点 关着时旧账本里的后台落点只还原不重写
        public static volatile bool BackgroundPinsAllowed;
        private sealed class Entry
        {
            public string Name;
            public string Group;
            public uint OrigPri;
            public ulong OrigAff;
            public int OrigIo = -1;
            public int OrigPg = -1;
            public uint[] OrigCpuSets;
            // 反作弊的当前落点，兼容恢复旧后台绑核；0 表示未绑，还原回 OrigAff
            public ulong SqueezeAff;
            // 落点被进程或其驱动拒绝过 本条目寿命内不再给落点 巡检也不再重写亲和
            public bool SqueezeRefused;
            // 反作弊条目压制写入失败后放弃 本条目寿命内不再写也不再巡检 原值仍在 退局照常还原
            public bool GaveUp;
            public bool GaveUpRestored;
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

    }
}
