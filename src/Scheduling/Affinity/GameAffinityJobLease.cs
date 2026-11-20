using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class GameAffinityJobRecord
    {
        internal const int RecoveryOrderingObservationLimit = 120;
        internal string Name, Phase, ThreadRecoveryRoot;
        internal int Pid, OwnerPid;
        internal long Birth, OwnerBirth;
        internal ulong Original, Desired;

        internal string Encode()
        {
            return string.Join("|", new[] { Name, Pid.ToString(CultureInfo.InvariantCulture), Birth.ToString(CultureInfo.InvariantCulture),
                Original.ToString(CultureInfo.InvariantCulture), Desired.ToString(CultureInfo.InvariantCulture), Phase,
                OwnerPid.ToString(CultureInfo.InvariantCulture), OwnerBirth.ToString(CultureInfo.InvariantCulture),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(ThreadRecoveryRoot ?? "")) });
        }

        // 旧版反作弊和微软后台治理的回执带第 10 和第 11 列 这类回执不恢复 读取时丢弃 不能阻塞游戏核心范围的回执
        internal static bool RetiredFormat(string line)
        {
            return line != null && line.Split('|').Length >= 10;
        }

        internal static bool ValidName(string name, int pid, long birth)
        {
            if (name == null || !name.StartsWith(GameAffinityJobNative.Prefix, StringComparison.Ordinal)) return false;
            string tail = name.Substring(GameAffinityJobNative.Prefix.Length);
            string suffix = "." + pid.ToString(CultureInfo.InvariantCulture) + "." + birth.ToString(CultureInfo.InvariantCulture);
            Guid nonce;
            return tail.Length == 32 + suffix.Length && tail.EndsWith(suffix, StringComparison.Ordinal)
                && Guid.TryParseExact(tail.Substring(0, 32), "N", out nonce) && nonce != Guid.Empty;
        }

        internal static GameAffinityJobRecord Parse(string line)
        {
            string[] fields = line.Split('|');
            var value = new GameAffinityJobRecord();
            if ((fields.Length != 8 && fields.Length != 9) || !int.TryParse(fields[1], out value.Pid) || value.Pid <= 4
                || !long.TryParse(fields[2], out value.Birth) || value.Birth <= 0
                || !ulong.TryParse(fields[3], out value.Original) || value.Original == 0
                || !ulong.TryParse(fields[4], out value.Desired) || value.Desired == 0
                || !int.TryParse(fields[6], out value.OwnerPid) || value.OwnerPid <= 4
                || !long.TryParse(fields[7], out value.OwnerBirth) || value.OwnerBirth <= 0
                || !ValidName(fields[0], value.Pid, value.Birth)
                || (fields[5] != "prepared" && fields[5] != "armed" && fields[5] != "neutral")) return null;
            value.Name = fields[0]; value.Phase = fields[5];
            try
            {
                value.ThreadRecoveryRoot = fields.Length >= 9 ? Encoding.UTF8.GetString(Convert.FromBase64String(fields[8])) : "";
                if (value.ThreadRecoveryRoot.Length > 0 && (!Path.IsPathRooted(value.ThreadRecoveryRoot)
                    || Path.GetFullPath(value.ThreadRecoveryRoot) != value.ThreadRecoveryRoot)) return null;
            }
            catch { return null; }
            return value;
        }

        internal bool TryThreadRecoveryPending(out bool pending)
        {
            pending = false;
            return Phase != "armed" || string.IsNullOrEmpty(ThreadRecoveryRoot)
                || HardThreadFileStore.TryCanonicalRootHasRecoveryDebt(ThreadRecoveryRoot, out pending);
        }

        internal bool SameScope(GameAffinityJobRecord other)
        {
            return other != null && Name == other.Name && Pid == other.Pid && Birth == other.Birth
                && OwnerPid == other.OwnerPid && OwnerBirth == other.OwnerBirth;
        }
    }

    internal static class GameAffinityJobReceiptStore
    {
        private const string MutexName = "Local\\Pavise.GameAffinity.ReceiptWriter.V1";

        private static bool Access(Func<List<GameAffinityJobRecord>, bool> action, bool write)
        {
            bool held = false;
            try
            {
                using (var mutex = new Mutex(false, MutexName))
                {
                    try { held = mutex.WaitOne(3000); } catch (AbandonedMutexException) { held = true; }
                    if (!held) return false;
                    try
                    {
                        string raw;
                        if (!Settings.TryLoadStr(GameAffinityJobLease.ReceiptKey, out raw)) return false;
                        var records = new List<GameAffinityJobRecord>();
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        foreach (string line in (raw ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (GameAffinityJobRecord.RetiredFormat(line.TrimEnd('\r'))) continue;
                            GameAffinityJobRecord record = GameAffinityJobRecord.Parse(line.TrimEnd('\r'));
                            if (record == null || !names.Add(record.Name) || records.Count >= 256) return false;
                            records.Add(record);
                        }
                        if (!action(records)) return false;
                        if (!write) return true;
                        string[] rows = new string[records.Count];
                        for (int i = 0; i < rows.Length; i++) rows[i] = records[i].Encode();
                        string joinResult = string.Join("\n", rows), verify;
                        return Settings.SaveStrDurable(GameAffinityJobLease.ReceiptKey, joinResult)
                            && Settings.TryLoadStr(GameAffinityJobLease.ReceiptKey, out verify) && verify == joinResult;
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch { return false; }
        }

        internal static bool Read(out List<GameAffinityJobRecord> records)
        {
            List<GameAffinityJobRecord> result = null;
            bool ok = Access(delegate(List<GameAffinityJobRecord> rows) { result = rows; return true; }, false);
            records = result;
            return ok;
        }

        internal static bool Save(GameAffinityJobRecord record)
        {
            return Access(delegate(List<GameAffinityJobRecord> rows)
            {
                rows.RemoveAll(delegate(GameAffinityJobRecord row) { return row.Name == record.Name; });
                rows.Add(record);
                return rows.Count <= 256;
            }, true);
        }

        internal static bool Find(string name, out GameAffinityJobRecord record)
        {
            record = null;
            List<GameAffinityJobRecord> records;
            if (!Read(out records)) return false;
            foreach (GameAffinityJobRecord row in records)
                if (row.Name == name) { record = row; break; }
            return true;
        }

        internal static bool Remove(string name)
        {
            return Access(delegate(List<GameAffinityJobRecord> rows)
            { rows.RemoveAll(delegate(GameAffinityJobRecord row) { return row.Name == name; }); return true; }, true);
        }
    }

    internal static class GameAffinityJobOperation
    {
        internal static bool Run(GameAffinityJobRecord record, Func<bool> action)
        {
            bool contended;
            return Run(record, action, out contended);
        }

        internal static bool Run(GameAffinityJobRecord record, Func<bool> action, out bool contended)
        {
            contended = false;
            try
            {
                using (var mutex = new Mutex(false, record.Name + ".operation"))
                {
                    bool held;
                    try { held = mutex.WaitOne(3000); } catch (AbandonedMutexException) { held = true; }
                    if (!held) { contended = true; return false; }
                    try { return action(); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch { return false; }
        }
    }

    internal sealed class GameAffinityJobLease
    {
        internal const string ReceiptKey = "GameAffinityJobReceiptsV1";

        private sealed class Entry
        {
            internal int Pid;
            internal long Birth;
            internal ulong Original, Desired;
            internal string Name;
            internal IntPtr Process, Job;
            // 锚点放进目标后就一直在 同一个 Job 不再重复放
            internal bool Anchored;
            internal bool Active, Pending;
            internal long NextRecovery;
            internal int RecoveryDelay;
            internal int RecoveryAttempts;
            internal int RecoveryOrderingObservations;
            internal bool RecoveryExitPhase;
            internal bool RecoveryTargetGone;
            internal GameAffinityJobRecord Record;
        }

        private sealed class LeaseView
        {
            internal readonly EntryView[] Entries;
            internal readonly bool Pending, Active, Busy, UnscopedPending;
            internal LeaseView(EntryView[] entries, bool pending, bool active, bool busy, bool unscopedPending)
            { Entries = entries; Pending = pending; Active = active; Busy = busy; UnscopedPending = unscopedPending; }
        }
        private sealed class EntryView
        {
            internal readonly int Pid;
            internal readonly long Birth;
            internal readonly bool Active, Pending;
            internal readonly string Name;
            internal EntryView(Entry entry)
            { Pid = entry.Pid; Birth = entry.Birth; Active = entry.Active && !entry.Pending; Pending = entry.Pending; Name = entry.Name; }
        }
        private sealed class Operation : IDisposable
        {
            private GameAffinityJobLease owner;
            internal Operation(GameAffinityJobLease owner)
            {
                this.owner = owner;
            }
            public void Dispose()
            {
                GameAffinityJobLease value = owner;
                if (value == null) return;
                owner = null;
                try { value.operationDepth--; value.PublishView(); }
                catch { value.view = UnknownView; throw; }
                finally { Monitor.Exit(value.gate); }
            }
        }

        private readonly object gate = new object();
        private sealed class OrphanRecoveryBudget
        {
            internal int Failures;
            internal int OrderingObservations;
            internal long Next;
            internal bool ExitPhase;
            internal bool OwnerExitObserved;
            internal IntPtr Job, Process;
        }
        private static readonly object orphanRecoveryGate = new object();
        private static readonly Dictionary<string, OrphanRecoveryBudget> orphanRecovery = new Dictionary<string, OrphanRecoveryBudget>(StringComparer.Ordinal);
        private static int orphanReadFailures;
        private static long orphanReadNext;
        private readonly Dictionary<int, Entry> leases = new Dictionary<int, Entry>();
        private bool recoveryPending;
        private int operationDepth;
        private static readonly LeaseView UnknownView = new LeaseView(new EntryView[0], true, true, true, true);
        private volatile LeaseView view = new LeaseView(new EntryView[0], false, false, false, false);
        internal string ThreadRecoveryRoot { get; set; }

        private Operation BeginOperation(bool wait = true)
        {
            // 准入前先分配视图 避免分配失败后留下未上报的所有权
            var operation = new Operation(this);
            if (wait) Monitor.Enter(gate);
            else if (!Monitor.TryEnter(gate)) return null;
            operationDepth++;
            try { PublishView(); return operation; }
            catch
            {
                operationDepth--;
                view = UnknownView; // 保留所有权 直到后续成功发布
                Monitor.Exit(gate);
                throw;
            }
        }

        private void PublishView()
        {
            var entries = new EntryView[leases.Count];
            int index = 0; bool pending = recoveryPending, active = false;
            foreach (Entry entry in leases.Values)
            { entries[index++] = new EntryView(entry); pending |= entry.Pending; active |= entry.Active; }
            view = new LeaseView(entries, pending, active, operationDepth != 0, recoveryPending);
        }

        internal bool HasPending
        {
            get
            {
                LeaseView snapshot = view;
                return snapshot.Busy || snapshot.Pending;
            }
        }

        internal bool HasActive
        { get { return view.Active; } }

        // 只看本目标的恢复债务 别的目标待恢复不阻塞这一个 所有权不确定时按待恢复处理
        internal bool HasPendingFor(int pid, long birth)
        {
            LeaseView snapshot = view;
            if (snapshot.Busy || snapshot.UnscopedPending) return true;
            foreach (EntryView entry in snapshot.Entries) if (entry.Pid == pid && entry.Birth == birth) return entry.Pending;
            return false;
        }

        internal bool HasLease(int pid, long birth)
        {
            LeaseView snapshot = view;
            // 所有权不确定时 必须保留调用方的恢复句柄
            if (snapshot.Busy) return true;
            foreach (EntryView entry in snapshot.Entries) if (entry.Pid == pid && entry.Birth == birth) return true;
            return false;
        }

        internal bool IsActive(int pid, long birth)
        {
            LeaseView snapshot = view;
            if (snapshot.Busy) return false;
            foreach (EntryView entry in snapshot.Entries) if (entry.Pid == pid && entry.Birth == birth) return entry.Active;
            return false;
        }

        internal static bool TargetRecoverySettled(int pid, long birth, out string reason)
        {
            reason = null;
            List<GameAffinityJobRecord> records;
            if (!GameAffinityJobReceiptStore.Read(out records)) { reason = "receipt-unreadable"; return false; }
            foreach (GameAffinityJobRecord record in records)
            {
                if (record.Pid == pid && record.Birth == birth && record.Phase != "neutral")
                { reason = "job-recovery-pending"; return false; }
            }
            return true;
        }

        private static void CloseSettled(Entry entry)
        {
            if (entry.Process != IntPtr.Zero) { Native.CloseHandle(entry.Process); entry.Process = IntPtr.Zero; }
            if (entry.Job != IntPtr.Zero) { Native.CloseHandle(entry.Job); entry.Job = IntPtr.Zero; }
        }

        // 仅游戏核心范围的所有者置为 true 后台 反作弊和微软后台的所有者保持严格准入
        internal bool OverrideThreadPlacement;
        internal bool Ensure(int pid, long birth, string name, ulong originalMask, ulong desiredMask, out string reason)
        {
            using (BeginOperation())
            {
                reason = null;
                if (pid <= 4 || birth <= 0 || originalMask == 0 || desiredMask == 0 || IntPtr.Size != 8)
                { reason = "invalid-target"; return false; }
                string threadRoot;
                try
                {
                    if (!string.IsNullOrEmpty(ThreadRecoveryRoot) && !Path.IsPathRooted(ThreadRecoveryRoot))
                    { reason = "invalid-thread-recovery-root"; return false; }
                    threadRoot = string.IsNullOrEmpty(ThreadRecoveryRoot) ? "" : Path.GetFullPath(ThreadRecoveryRoot);
                }
                catch { reason = "invalid-thread-recovery-root"; return false; }
                Entry entry;
                if (leases.TryGetValue(pid, out entry) && entry.Birth != birth)
                {
                    if (!ReleaseLocked(entry, out reason)) return false;
                    CloseSettled(entry); leases.Remove(pid);
                    if (!GameAffinityJobReceiptStore.Remove(entry.Name)) { recoveryPending = true; reason = "receipt-remove"; return false; }
                    entry = null;
                }
                if (entry == null)
                {
                    if (!Recover(out reason)) { recoveryPending = true; return false; }
                    recoveryPending = false;
                    IntPtr process = GameAffinityJobNative.OpenTarget(pid, birth);
                    if (process == IntPtr.Zero) { reason = "process-access"; return false; }
                    if (!GameAffinityJobNative.Matches(process, birth))
                    { Native.CloseHandle(process); reason = "target-identity"; return false; }
                    // Windows 在分配时核验已有 Job 层级的嵌套关系 不能直接拒绝启动器持有的 Job

                    if (!string.IsNullOrEmpty(name) && !string.Equals(Native.ImageName(process), name, StringComparison.OrdinalIgnoreCase))
                    { Native.CloseHandle(process); reason = "target-name"; return false; }
                    ulong current;
                    if (!Native.TryQueryAffinity(process, out current) || current != originalMask)
                    { Native.CloseHandle(process); reason = "original-affinity-changed"; return false; }
                    string jobName = GameAffinityJobNative.Prefix + Guid.NewGuid().ToString("N") + "." + pid + "." + birth;
                    IntPtr job = GameAffinityJobNative.Create(jobName, out reason);
                    if (job == IntPtr.Zero) { Native.CloseHandle(process); return false; }
                    long ownerBirth, cpu; ulong io; int ownerPid;
                    using (Process owner = System.Diagnostics.Process.GetCurrentProcess()) ownerPid = owner.Id;
                    if (!Native.QueryProcessSample(new IntPtr(-1), out ownerBirth, out cpu, out io))
                    { Native.CloseHandle(process); Native.CloseHandle(job); reason = "owner-identity"; return false; }
                    entry = new Entry { Pid = pid, Birth = birth, Original = originalMask, Desired = desiredMask,
                        Name = jobName, Process = process, Job = job };
                    entry.Record = new GameAffinityJobRecord { Name = jobName, Pid = pid, Birth = birth, Original = originalMask,
                        Desired = desiredMask, OwnerPid = ownerPid, OwnerBirth = ownerBirth, Phase = "prepared",
                        ThreadRecoveryRoot = threadRoot };
                    leases.Add(pid, entry);
                    if (!GameAffinityJobReceiptStore.Save(entry.Record))
                    { entry.Pending = true; reason = "receipt-write"; return false; }
                }
                if (entry.Pending) { reason = "recovery-pending"; return false; }
                if (entry.Active && entry.Desired == desiredMask) return VerifyLocked(entry, desiredMask, out reason);
                // 改变活动范围或记录前 先拒绝游戏自行设置的线程放置 原生写入前再次核对
                ulong currentRange;
                bool overrideThreads = OverrideThreadPlacement;
                if (!Native.TryQueryAffinity(entry.Process, out currentRange)
                    || !(overrideThreads ? GameThreadAffinityAdmission.CheckProcessOnly(entry.Process, pid, birth, currentRange, out reason)
                        : GameThreadAffinityAdmission.Check(entry.Process, pid, birth, currentRange, out reason)))
                {
                    if (reason == null) reason = GameThreadAffinityAdmission.RefusalPrefix + "process-mask-unreadable";
                    return false;
                }
                if (entry.Active)
                {
                    // Desired 保存已应用范围 变更前先持久保存中性状态 防止崩溃后遗留旧限制

                    if (!ReleaseLocked(entry, out reason)) return false;
                }
                if (!Native.TryQueryAffinity(entry.Process, out currentRange) || currentRange != originalMask)
                { reason = "original-affinity-changed"; return false; }
                entry.Original = originalMask;
                entry.Desired = desiredMask;
                entry.Record.Original = entry.Original; entry.Record.Desired = desiredMask; entry.Record.Phase = "armed";
                entry.Record.ThreadRecoveryRoot = threadRoot;
                if (!GameAffinityJobReceiptStore.Save(entry.Record)) { entry.Pending = true; reason = "receipt-arm"; return false; }
                // 写入前先往游戏进程里放锚点 Pavise 中途退出后下次启动能按回执里的名字重开 Job 解除限制并写回原范围
                // 锚点放不进去就不加限制 绝不持有崩溃后无法解除的限制
                string failure = null;
                if (!entry.Anchored)
                {
                    string anchorReason;
                    entry.Anchored = GameAffinityJobNative.Anchor(entry.Job, entry.Process, out anchorReason);
                    if (!entry.Anchored) failure = anchorReason;
                }
                // 与启动恢复共用同一把操作锁 恢复不会和这次写入交错
                bool applied = entry.Anchored && GameAffinityJobOperation.Run(entry.Record,
                    () => GameAffinityJobNative.Apply(entry.Job, entry.Process, entry.Record.Birth, desiredMask, overrideThreads, out failure));
                if (!applied || !GameAffinityJobNative.Verify(entry.Job, entry.Process, entry.Record.Birth, entry.Record.Desired, out failure))
                {
                    string cleanup;
                    if (failure == null) failure = "apply-operation-pending";
                    if (!ReleaseLocked(entry, out cleanup)) failure += "; recovery=" + cleanup;
                    reason = failure;
                    return false;
                }
                entry.Active = true; entry.Pending = false;
                return true;
            }
        }

        private bool VerifyLocked(Entry entry, ulong desiredMask, out string reason)
        {
            reason = null;
            if (entry.Desired != desiredMask) { reason = "job-requested-mask-changed"; return false; }
            if (!entry.Active || entry.Pending) { reason = "job-not-active"; return false; }
            return GameAffinityJobNative.Verify(entry.Job, entry.Process, entry.Record.Birth, entry.Record.Desired, out reason);
        }

        internal bool Verify(int pid, long birth, ulong desiredMask, out string reason)
        {
            Operation operation = BeginOperation(false);
            if (operation == null) { reason = "job-operation-pending"; return false; }
            using (operation)
            {
                Entry entry;
                if (!leases.TryGetValue(pid, out entry) || entry.Birth != birth) { reason = "job-not-owned"; return false; }
                return VerifyLocked(entry, desiredMask, out reason);
            }
        }

        private bool ReleaseLocked(Entry entry, out string reason)
        {
            if (!entry.Active && !entry.Pending && entry.Record.Phase == "neutral") { reason = null; return true; }
            long now = AutoCores.NowTicks;
            bool targetGone = Native.WaitForSingleObject(entry.Process, 0) == 0;
            if (targetGone && !entry.RecoveryExitPhase)
            {
                entry.RecoveryExitPhase = true;
                entry.RecoveryAttempts = 0;
                entry.RecoveryOrderingObservations = 0;
                entry.NextRecovery = 0;
            }
            if (entry.RecoveryAttempts >= 3 || entry.RecoveryOrderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit)
            {
                RecoveryAttemptTrace.Exhausted(entry.RecoveryExitPhase ? (Func<bool>)null : () => Native.WaitForSingleObject(entry.Process, 0) == 0);
                reason = "recovery-budget-exhausted; receipt-and-handles-retained"; return false;
            }
            if (entry.Pending && now < entry.NextRecovery && targetGone == entry.RecoveryTargetGone)
            { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(entry.NextRecovery - now)); reason = "recovery-backoff"; return false; }
            bool restored = ReleaseCore(entry, out reason);
            if (restored) { entry.NextRecovery = 0; entry.RecoveryDelay = 0; entry.RecoveryAttempts = 0; entry.RecoveryOrderingObservations = 0; }
            else if (reason == "thread-recovery-pending" || reason == "recovery-operation-pending")
            {
                // 进程恢复先等待线程日志 使用独立有限观察预算 不执行原生恢复或退还失败次数

                entry.Pending = true;
                entry.RecoveryOrderingObservations++;
                if (entry.RecoveryOrderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                    RecoveryAttemptTrace.Exhausted(entry.RecoveryExitPhase ? (Func<bool>)null : () => Native.WaitForSingleObject(entry.Process, 0) == 0);
                else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                if (entry.RecoveryOrderingObservations == 1 || entry.RecoveryOrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                    Logger.Warn(Logger.Format("游戏亲和性恢复顺序：目标={0}/{1} 观测={2}/{3} 原因={4}{5}", "Game affinity recovery ordering: target={0}/{1} observation={2}/{3} reason={4}{5}", entry.Pid, entry.Birth, entry.RecoveryOrderingObservations, GameAffinityJobRecord.RecoveryOrderingObservationLimit, reason, (entry.RecoveryOrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit
                            ? Logger.Text("；已停止主动尝试，保留回执和句柄", "; active attempts stopped; receipt and handles retained") : "")));
            }
            else
            {
                entry.RecoveryAttempts++;
                entry.Pending = true;
                entry.RecoveryDelay = entry.RecoveryDelay == 0 ? 500 : Math.Min(30000, entry.RecoveryDelay * 2);
                entry.NextRecovery = AutoCores.NowTicks + TimeSpan.FromMilliseconds(entry.RecoveryDelay).Ticks;
                entry.RecoveryTargetGone = Native.WaitForSingleObject(entry.Process, 0) == 0;
                if (entry.RecoveryAttempts >= 3)
                    RecoveryAttemptTrace.Exhausted(entry.RecoveryExitPhase ? (Func<bool>)null : () => Native.WaitForSingleObject(entry.Process, 0) == 0);
                else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(entry.NextRecovery - AutoCores.NowTicks));
                Logger.Warn(Logger.Format("游戏亲和性恢复：目标={0}/{1} 尝试={2}/3 原因={3}{4}", "Game affinity recovery: target={0}/{1} attempt={2}/3 reason={3}{4}", entry.Pid, entry.Birth, entry.RecoveryAttempts, reason, (entry.RecoveryAttempts == 3 ? Logger.Text("；已停止主动重试，保留回执和句柄", "; active retries stopped; receipt and handles retained") : Logger.Text("；恢复尚未完成", "; restoration pending"))));
            }
            return restored;
        }

        private bool ReleaseCore(Entry entry, out string reason)
        {
            reason = null;
            if (!entry.Active && !entry.Pending && entry.Record.Phase == "neutral") return true;
            if (Native.WaitForSingleObject(entry.Process, 0) != 0)
            {
                bool threadPending;
                if (!entry.Record.TryThreadRecoveryPending(out threadPending))
                { entry.Pending = true; reason = "thread-recovery-unconfirmed"; return false; }
                if (threadPending) { entry.Pending = true; reason = "thread-recovery-pending"; return false; }
            }
            string failure = null;
            bool contended;
            bool restored = GameAffinityJobOperation.Run(entry.Record, delegate
            {
                GameAffinityJobRecord current;
                if (!GameAffinityJobReceiptStore.Find(entry.Name, out current)) { failure = "receipt-unreadable"; return false; }
                bool gone = Native.WaitForSingleObject(entry.Process, 0) == 0;
                if (current == null)
                    return gone ? GameAffinityJobNative.ClearForGoneTarget(entry.Job, out failure)
                        : GameAffinityJobNative.VerifyNeutral(entry.Job, out failure);
                if (!entry.Record.SameScope(current)) { failure = "receipt-scope-changed"; return false; }
                if (!gone)
                {
                    bool threadPending;
                    if (!current.TryThreadRecoveryPending(out threadPending)) { failure = "thread-recovery-unconfirmed"; return false; }
                    if (threadPending) { failure = "thread-recovery-pending"; return false; }
                }
                bool clean = GameAffinityJobNative.RestoreRecord(current, entry.Job, entry.Process, out failure);
                if (!clean) return false;
                current.Phase = "neutral";
                if (!GameAffinityJobReceiptStore.Save(current)) { failure = "receipt-neutral"; return false; }
                entry.Record = current;
                return true;
            }, out contended);
            if (!restored) { entry.Pending = true; reason = failure ?? (contended ? "recovery-operation-pending" : "recovery-operation-unconfirmed"); return false; }
            entry.Record.Phase = "neutral";
            entry.Active = false; entry.Pending = false;
            return true;
        }

        internal bool Release(int pid, long birth, out string reason)
        {
            using (BeginOperation())
            {
                Entry entry;
                if (!leases.TryGetValue(pid, out entry) || entry.Birth != birth) { reason = null; return true; }
                return ReleaseLocked(entry, out reason);
            }
        }

        internal bool ReleaseExcept(int keepPid, long keepBirth, out string reason)
        {
            using (BeginOperation())
            {
                reason = null; bool ok = true;
                var gone = new List<Entry>();
                foreach (Entry entry in leases.Values)
                {
                    if (entry.Pid == keepPid && entry.Birth == keepBirth) continue;
                    string failure;
                    if (!ReleaseLocked(entry, out failure)) { ok = false; reason = failure; }
                    else if (Native.WaitForSingleObject(entry.Process, 0) == 0) gone.Add(entry);
                }
                foreach (Entry entry in gone)
                {
                    CloseSettled(entry); leases.Remove(entry.Pid);
                    if (!GameAffinityJobReceiptStore.Remove(entry.Name)) { recoveryPending = true; ok = false; reason = "receipt-remove"; }
                }
                if (recoveryPending) recoveryPending = !Recover(out reason);
                return ok && !recoveryPending;
            }
        }

        internal bool ReleaseAll(out string reason) { return ReleaseExcept(0, 0, out reason); }
        internal bool ReleaseAll() { string reason; return ReleaseAll(out reason); }
        internal bool Stop() { string reason; return Stop(out reason); }

        internal bool Stop(out string reason)
        {
            using (BeginOperation())
            {
                if (!ReleaseAll(out reason)) return false;
                foreach (Entry entry in new List<Entry>(leases.Values))
                {
                    CloseSettled(entry);
                    leases.Remove(entry.Pid);
                }
                // 最后一个 Job 句柄关闭后 中性记录仍然安全 保留到 Recover 确认它并非遗留限制

                return true;
            }
        }

        internal static bool Recover(out string reason)
        {
            lock (orphanRecoveryGate)
            {
                reason = null;
                long now = AutoCores.NowTicks;
                if (orphanReadFailures >= 3 || now < orphanReadNext)
                {
                    if (orphanReadFailures >= 3) RecoveryAttemptTrace.Exhausted();
                    else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(orphanReadNext - now));
                    reason = "receipt-read-budget-or-backoff; recovery retained"; return false;
                }
                List<GameAffinityJobRecord> records;
                if (!GameAffinityJobReceiptStore.Read(out records))
                {
                    orphanReadFailures++;
                    orphanReadNext = now + TimeSpan.FromSeconds(4 * orphanReadFailures).Ticks;
                    RecoveryAttemptTrace.FailureSchedule(orphanReadFailures, 3, DateTime.UtcNow.Ticks + orphanReadNext - now);
                    Logger.Warn(Logger.Format("游戏亲和性遗留回执读取失败={0}/3；保留恢复状态", "Game affinity orphan receipt read failure={0}/3; recovery retained", orphanReadFailures));
                    reason = "receipt-unreadable"; return false;
                }
                int self;
                using (Process current = System.Diagnostics.Process.GetCurrentProcess()) self = current.Id;
                bool all = true;
                foreach (GameAffinityJobRecord record in records)
                {
                    // 观测身份或退出不算另一次恢复尝试
                    int target = GameAffinityJobNative.Probe(record.Pid, record.Birth);
                    int owner = GameAffinityJobNative.Probe(record.OwnerPid, record.OwnerBirth);
                    bool foreignOwnerPending = !(record.Phase == "neutral" && target == 1) && owner != 1;
                    if (foreignOwnerPending && (record.Phase == "neutral" || (record.OwnerPid == self && owner == 0))) continue;
                    string key = record.Name;
                    OrphanRecoveryBudget budget;
                    if (!orphanRecovery.TryGetValue(key, out budget))
                        orphanRecovery[key] = budget = new OrphanRecoveryBudget { OwnerExitObserved = owner == 1 };
                    if (owner == 1 && !budget.OwnerExitObserved)
                    {
                        budget.OwnerExitObserved = true;
                        budget.Failures = 0;
                        budget.OrderingObservations = 0;
                        budget.Next = 0;
                    }
                    if (target == 1 && !budget.ExitPhase)
                    {
                        budget.ExitPhase = true;
                        budget.Failures = 0;
                        budget.OrderingObservations = 0;
                        budget.Next = 0;
                    }
                    if (budget.Failures >= 3 || budget.OrderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit || now < budget.Next)
                    {
                        if (budget.Failures >= 3 || budget.OrderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit) ReportOrphanExhausted(record, budget);
                        else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(budget.Next - now));
                        all = false; reason = "orphan-recovery-budget-or-backoff; receipt retained"; continue;
                    }
                    if (foreignOwnerPending)
                    {
                        all = false;
                        reason = owner == 0 ? "previous-owner-live" : "previous-owner-unreadable";
                        if (owner == 0)
                        {
                            budget.OrderingObservations++;
                            budget.Next = AutoCores.NowTicks + TimeSpan.FromSeconds(30).Ticks;
                            if (budget.OrderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit) ReportOrphanExhausted(record, budget);
                            else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(budget.Next - AutoCores.NowTicks));
                            if (budget.OrderingObservations == 1 || budget.OrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                                Logger.Warn(Logger.Format("游戏亲和性遗留所有者顺序：回执={0} 观测={1}/{2} 原因={3}{4}", "Game affinity orphan owner ordering: receipt={0} observation={1}/{2} reason={3}{4}", record.Name, budget.OrderingObservations, GameAffinityJobRecord.RecoveryOrderingObservationLimit, reason, (budget.OrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit
                                        ? Logger.Text("；已停止主动尝试，保留回执和句柄", "; active attempts stopped; receipt and handles retained") : "")));
                        }
                        else
                        {
                            budget.Failures++;
                            budget.Next = AutoCores.NowTicks + TimeSpan.FromSeconds(4 * budget.Failures).Ticks;
                            if (budget.Failures >= 3) ReportOrphanExhausted(record, budget);
                            else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(budget.Next - AutoCores.NowTicks));
                            Logger.Warn(Logger.Format("游戏亲和性遗留所有者身份：回执={0} 失败详情={1}/3 原因={2}{3}", "Game affinity orphan owner identity: receipt={0} failure={1}/3 reason={2}{3}", record.Name, budget.Failures, reason, (budget.Failures >= 3 ? Logger.Text("；已停止主动尝试，保留回执和句柄", "; active attempts stopped; receipt and handles retained") : "")));
                        }
                        continue;
                    }
                    string failure;
                    bool restored;
                    try { restored = RecoverRecord(record, target, budget, out failure); }
                    catch (Exception ex) { restored = false; failure = "recovery-exception:" + ex.GetType().Name; }
                    if (!restored && (failure == "thread-recovery-pending" || failure == "recovery-operation-pending"))
                    {
                        // 未执行原生恢复时不退还失败次数 上游债务观察也有次数上限

                        all = false; reason = failure;
                        budget.OrderingObservations++;
                        if (budget.OrderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                            ReportOrphanExhausted(record, budget);
                        else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                        if (budget.OrderingObservations == 1 || budget.OrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                            Logger.Warn(Logger.Format("游戏亲和性遗留顺序检查：回执={0} 观测={1}/{2} 原因={3}{4}", "Game affinity orphan ordering: receipt={0} observation={1}/{2} reason={3}{4}", record.Name, budget.OrderingObservations, GameAffinityJobRecord.RecoveryOrderingObservationLimit, reason, (budget.OrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit
                                    ? Logger.Text("；已停止主动尝试，保留回执和句柄", "; active attempts stopped; receipt and handles retained") : "")));
                    }
                    else if (!restored)
                    {
                        all = false; reason = failure;
                        budget.Failures++;
                        budget.Next = AutoCores.NowTicks + TimeSpan.FromSeconds(4 * budget.Failures).Ticks;
                        if (budget.Failures >= 3) ReportOrphanExhausted(record, budget);
                        else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(budget.Next - AutoCores.NowTicks));
                        Logger.Warn(Logger.Format("游戏亲和性遗留恢复：回执={0} 失败详情={1}/3 原因={2}{3}", "Game affinity orphan recovery: receipt={0} failure={1}/3 reason={2}{3}", record.Name, budget.Failures, reason, (budget.Failures >= 3 ? Logger.Text("；已停止主动尝试，保留回执", "; active attempts stopped; receipt retained") : "")));
                    }
                }
                return all;
            }
        }

        private static void ReportOrphanExhausted(GameAffinityJobRecord record, OrphanRecoveryBudget budget)
        {
            Func<bool> observe = null;
            if (!budget.ExitPhase || !budget.OwnerExitObserved)
                observe = () => (!budget.ExitPhase && GameAffinityJobNative.Probe(record.Pid, record.Birth) == 1)
                    || (!budget.OwnerExitObserved && GameAffinityJobNative.Probe(record.OwnerPid, record.OwnerBirth) == 1);
            RecoveryAttemptTrace.Exhausted(observe);
        }

        private static bool RecoverRecord(GameAffinityJobRecord record, int target, OrphanRecoveryBudget budget, out string reason)
        {
            reason = null;
            if (record.Phase == "neutral" && target == 1)
            {
                if (GameAffinityJobReceiptStore.Remove(record.Name)) { CloseRecoveredOrphan(budget); return true; }
                reason = "receipt-remove"; return false;
            }
            if (target != 1)
            {
                bool threadPending;
                if (!record.TryThreadRecoveryPending(out threadPending)) { reason = "thread-recovery-unconfirmed"; return false; }
                if (threadPending) { reason = "thread-recovery-pending"; return false; }
            }
            if (budget.Job == IntPtr.Zero) budget.Job = GameAffinityJobNative.Open(record.Name);
            IntPtr job = budget.Job;
            int openError = job == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (job == IntPtr.Zero)
            {
                if (openError != 2 || (record.Phase == "armed" && target != 1 && !RestoredWithoutJob(record)))
                { reason = "job-handle-lost; game-exit-required"; return false; }
                if (GameAffinityJobReceiptStore.Remove(record.Name)) { CloseRecoveredOrphan(budget); return true; }
                reason = "receipt-remove"; return false;
            }
            IntPtr process = budget.Process;
            bool settled = false;
            try
            {
                if (target != 1)
                {
                    if (process == IntPtr.Zero) process = budget.Process = GameAffinityJobNative.OpenRestore(record.Pid, record.Birth);
                    if (process == IntPtr.Zero) { reason = "recovery-process-access"; return false; }
                }
                string failure = null;
                bool contended;
                bool restored = GameAffinityJobOperation.Run(record, delegate
                {
                    GameAffinityJobRecord current;
                    if (!GameAffinityJobReceiptStore.Find(record.Name, out current)) { failure = "receipt-unreadable"; return false; }
                    if (current == null)
                        return target == 1 ? GameAffinityJobNative.ClearForGoneTarget(job, out failure)
                            : GameAffinityJobNative.VerifyNeutral(job, out failure);
                    if (!record.SameScope(current)) { failure = "receipt-scope-changed"; return false; }
                    if (target != 1)
                    {
                        bool threadPending;
                        if (!current.TryThreadRecoveryPending(out threadPending)) { failure = "thread-recovery-unconfirmed"; return false; }
                        if (threadPending) { failure = "thread-recovery-pending"; return false; }
                    }
                    bool cleared = GameAffinityJobNative.RestoreRecord(current, job, target == 1 ? IntPtr.Zero : process, out failure);
                    if (!cleared) return false;
                    current.Phase = "neutral";
                    if (!GameAffinityJobReceiptStore.Save(current)) { failure = "receipt-neutral"; return false; }
                    if (target == 1 && !GameAffinityJobReceiptStore.Remove(current.Name)) { failure = "receipt-remove"; return false; }
                    return true;
                }, out contended);
                reason = failure ?? (restored ? null : (contended ? "recovery-operation-pending" : "recovery-operation-unconfirmed"));
                settled = restored;
                return restored;
            }
            finally
            {
                // 原始记录尚未结清时 保留最后可访问的句柄 预算耗尽后也不释放

                if (settled) CloseRecoveredOrphan(budget);
            }
        }

        // 旧版本由守护持有 Job 句柄 守护恢复后名字随之消失 目标仍是原范围说明已经恢复 回执可以结清
        private static bool RestoredWithoutJob(GameAffinityJobRecord record)
        {
            IntPtr process = GameAffinityJobNative.OpenIdentity(record.Pid, record.Birth);
            if (process == IntPtr.Zero) return false;
            try
            {
                ulong mask;
                return Native.TryQueryAffinity(process, out mask) && mask == record.Original;
            }
            finally { Native.CloseHandle(process); }
        }

        private static void CloseRecoveredOrphan(OrphanRecoveryBudget budget)
        {
            if (budget.Process != IntPtr.Zero) { Native.CloseHandle(budget.Process); budget.Process = IntPtr.Zero; }
            if (budget.Job != IntPtr.Zero) { Native.CloseHandle(budget.Job); budget.Job = IntPtr.Zero; }
        }

    }
}
