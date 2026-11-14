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
        internal bool OwnThreadAffinity;
        internal bool StrictThreadAdmission;
        internal int Pid, OwnerPid;
        internal long Birth, OwnerBirth;
        internal ulong Original, Desired;
        internal WindowsBackgroundLimits Resources;

        internal string Encode()
        {
            if (OwnThreadAffinity && (Resources != null || !string.IsNullOrEmpty(ThreadRecoveryRoot)))
                throw new InvalidOperationException("sguard-thread-owner-scope-conflict");
            string row = string.Join("|", new[] { Name, Pid.ToString(CultureInfo.InvariantCulture), Birth.ToString(CultureInfo.InvariantCulture),
                Original.ToString(CultureInfo.InvariantCulture), Desired.ToString(CultureInfo.InvariantCulture), Phase,
                OwnerPid.ToString(CultureInfo.InvariantCulture), OwnerBirth.ToString(CultureInfo.InvariantCulture),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(ThreadRecoveryRoot ?? "")) });
            if (StrictThreadAdmission && !OwnThreadAffinity) throw new InvalidOperationException("thread-admission-owner-missing");
            if (OwnThreadAffinity) return row + (StrictThreadAdmission ? "||AntiCheatThreads1" : "||SGuardThreads1");
            return Resources == null ? row : row + "|" + Resources.Encode();
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
            if ((fields.Length != 8 && fields.Length != 9 && fields.Length != 10 && fields.Length != 11) || !int.TryParse(fields[1], out value.Pid) || value.Pid <= 4
                || !long.TryParse(fields[2], out value.Birth) || value.Birth <= 0
                || !ulong.TryParse(fields[3], out value.Original) || value.Original == 0
                || !ulong.TryParse(fields[4], out value.Desired) || value.Desired == 0
                || !int.TryParse(fields[6], out value.OwnerPid) || value.OwnerPid <= 4
                || !long.TryParse(fields[7], out value.OwnerBirth) || value.OwnerBirth <= 0
                || !ValidName(fields[0], value.Pid, value.Birth)
                || (fields[5] != "prepared" && fields[5] != "armed" && fields[5] != "neutral")) return null;
            value.Name = fields[0]; value.Phase = fields[5];
            if (fields.Length == 10)
            {
                value.Resources = WindowsBackgroundLimits.Parse(fields[9]);
                if (value.Resources == null) return null;
            }
            if (fields.Length == 11)
            {
                if (fields[9] != "" || (fields[10] != "SGuardThreads1" && fields[10] != "AntiCheatThreads1")) return null;
                value.OwnThreadAffinity = true;
                value.StrictThreadAdmission = fields[10] == "AntiCheatThreads1";
            }
            try
            {
                value.ThreadRecoveryRoot = fields.Length >= 9 ? Encoding.UTF8.GetString(Convert.FromBase64String(fields[8])) : "";
                if (value.ThreadRecoveryRoot.Length > 0 && (!Path.IsPathRooted(value.ThreadRecoveryRoot)
                    || Path.GetFullPath(value.ThreadRecoveryRoot) != value.ThreadRecoveryRoot)) return null;
            }
            catch { return null; }
            if (value.OwnThreadAffinity && !string.IsNullOrEmpty(value.ThreadRecoveryRoot)) return null;
            return value;
        }

        internal bool ThreadRecoveryPending()
        {
            bool pending;
            return !TryThreadRecoveryPending(out pending) || pending;
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
                && OwnerPid == other.OwnerPid && OwnerBirth == other.OwnerBirth
                && (Resources == null) == (other.Resources == null) && OwnThreadAffinity == other.OwnThreadAffinity
                && StrictThreadAdmission == other.StrictThreadAdmission;
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
            internal Process Guard;
            internal GameAffinityJobProtocol Protocol;
            internal int GuardPid;
            internal long GuardBirth;
            internal bool Active, Pending, ProtocolLost;
            internal long NextRecovery;
            internal int RecoveryDelay;
            internal int RecoveryAttempts;
            internal int RecoveryOrderingObservations;
            internal bool RecoveryExitPhase;
            internal bool RecoveryGuardAlive, RecoveryTargetGone;
            internal GameAffinityJobRecord Record;
            internal EventWaitHandle ForwardRevoked;
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
            internal readonly bool OwnThreads;
            internal EntryView(Entry entry)
            { Pid = entry.Pid; Birth = entry.Birth; Active = entry.Active && !entry.Pending; Pending = entry.Pending; Name = entry.Name; OwnThreads = entry.Record != null && entry.Record.OwnThreadAffinity; }
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
        private static readonly object guardianGate = new object();
        private static readonly Dictionary<int, long> guardians = new Dictionary<int, long>();
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
        private readonly Dictionary<string, int> threadGuardStarts = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool recoveryPending;
        private int operationDepth;
        private long forwardEpoch;
        internal Func<bool> ForwardAdmission;
        private static readonly LeaseView UnknownView = new LeaseView(new EntryView[0], true, true, true, true);
        private volatile LeaseView view = new LeaseView(new EntryView[0], false, false, false, false);
        internal string ThreadRecoveryRoot { get; set; }
        // 撤权不等父进程的操作锁；守护会在每个后续原生写入前观察同一事件。
        internal void RevokeForward(int pid = 0, long birth = 0)
        {
            Interlocked.Increment(ref forwardEpoch);
            foreach (EntryView entry in view.Entries)
            {
                if (!entry.OwnThreads || (pid != 0 && (entry.Pid != pid || entry.Birth != birth))) continue;
                try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting(entry.Name + ".ForwardRevoked")) signal.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
            }
        }

        private bool ForwardCurrent(Entry entry, long epoch, out string reason)
        {
            reason = null;
            if (!entry.Record.OwnThreadAffinity) return true;
            if (epoch == Interlocked.Read(ref forwardEpoch) && (ForwardAdmission == null || ForwardAdmission())) return true;
            // 全局代号也会因别组撤权变化，只拒绝旧轮次；持久撤权事件由明确匹配的 RevokeForward 设置。
            reason = "sguard-forward-revoked"; return false;
        }

        private Operation BeginOperation(bool wait = true)
        {
            // 准入前先分配视图，避免分配失败后留下未上报的所有权。
            var operation = new Operation(this);
            if (wait) Monitor.Enter(gate);
            else if (!Monitor.TryEnter(gate)) return null;
            operationDepth++;
            try { PublishView(); return operation; }
            catch
            {
                operationDepth--;
                view = UnknownView; // 保留所有权，直到后续成功发布。
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

        internal bool HasPendingFor(int pid, long birth)
        {
            LeaseView snapshot = view;
            // 操作尚未发布或孤立回执未确认范围时保守暂停，已知另一目标欠债不扩大到当前目标。
            if (snapshot.Busy || snapshot.UnscopedPending || pid <= 0 || birth <= 0) return true;
            foreach (EntryView entry in snapshot.Entries)
                if (entry.Pid == pid && entry.Birth == birth) return entry.Pending;
            return false;
        }

        internal bool HasLease(int pid, long birth)
        {
            LeaseView snapshot = view;
            // 所有权不确定时，必须保留调用方的恢复句柄。
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

        internal static bool IsGuardian(int pid, long birth)
        {
            lock (guardianGate)
            {
                long heldBirth;
                return pid > 0 && birth > 0 && guardians.TryGetValue(pid, out heldBirth) && heldBirth == birth;
            }
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

        private static bool GuardAlive(Entry entry)
        {
            if (entry.Guard == null) return false;
            try
            {
                if (!entry.Guard.HasExited) return true;
                lock (guardianGate)
                {
                    long heldBirth;
                    if (guardians.TryGetValue(entry.GuardPid, out heldBirth) && heldBirth == entry.GuardBirth)
                        guardians.Remove(entry.GuardPid);
                }
                entry.GuardPid = 0; entry.GuardBirth = 0;
                return false;
            }
            catch { return true; } // 退出状态不确定时，保留恢复权限。
        }

        private static string ReadReply(Entry entry)
        {
            if (entry.Protocol == null) throw new InvalidOperationException("guardian-reader-missing");
            return entry.Protocol.ReadReply(3000);
        }

        private static bool Command(Entry entry, string command, out string reason)
        {
            reason = null;
            try
            {
                if (!GuardAlive(entry) || entry.ProtocolLost) { reason = "guardian-lost"; return false; }
                entry.Guard.StandardInput.WriteLine(command);
                entry.Guard.StandardInput.Flush();
                string reply = ReadReply(entry);
                if (reply == "OK") return true;
                if (reply == null) entry.ProtocolLost = true;
                reason = reply == null ? "guardian-ended" : reply;
                return false;
            }
            catch (Exception error)
            {
                entry.ProtocolLost = true;
                if (entry.Protocol != null) entry.Protocol.Revoke();
                reason = "guardian:" + error.GetType().Name; return false;
            }
        }

        private bool StartGuard(Entry entry, out string reason)
        {
            reason = null;
            try
            {
                if (GuardAlive(entry)) { reason = "previous-guardian-live"; return false; }
                if (entry.Record.OwnThreadAffinity)
                {
                    string key = entry.Pid + ":" + entry.Birth + ":" + entry.Original + ":" + entry.Desired;
                    int attempts;
                    threadGuardStarts.TryGetValue(key, out attempts);
                    if (attempts >= 3) { reason = "sguard-guardian-start-budget-exhausted"; return false; }
                    threadGuardStarts[key] = attempts + 1;
                }
                if (entry.Protocol != null) { entry.Protocol.Dispose(); entry.Protocol = null; }
                if (entry.Guard != null) entry.Guard.Dispose();
                entry.ProtocolLost = false;
                var start = new ProcessStartInfo(typeof(GameAffinityJobLease).Assembly.Location,
                    GameAffinityJobGuard.Argument + " " + entry.Record.OwnerPid + " " + entry.Record.OwnerBirth
                    + " " + entry.Pid + " " + entry.Birth + " " + entry.Original + " " + entry.Desired + " " + entry.Name);
                start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
                start.RedirectStandardInput = true; start.RedirectStandardOutput = true;
                start.StandardOutputEncoding = new UTF8Encoding(false);
                entry.Guard = System.Diagnostics.Process.Start(start);
                entry.Protocol = new GameAffinityJobProtocol(entry.Guard);
                long guardBirth, cpu; ulong io;
                if (!Native.QueryProcessSample(entry.Guard.Handle, out guardBirth, out cpu, out io) || guardBirth <= 0)
                { reason = "guardian-identity"; return false; }
                entry.GuardPid = entry.Guard.Id; entry.GuardBirth = guardBirth;
                lock (guardianGate) guardians[entry.GuardPid] = guardBirth;
                if (ReadReply(entry) == "READY") return true;
                reason = "guardian-not-ready";
            }
            catch (Exception error) { entry.ProtocolLost = true; reason = "guardian-start:" + error.GetType().Name; }
            return false;
        }

        private static void RequestGuardExit(Entry entry, bool wait = true)
        {
            if (entry.Guard == null) return;
            entry.ProtocolLost = true;
            if (entry.Protocol != null) entry.Protocol.Revoke();
            try { entry.Guard.StandardInput.Close(); } catch { }
            if (wait) { try { entry.Guard.WaitForExit(3000); } catch { } }
            GuardAlive(entry);
        }

        private static void CloseSettled(Entry entry)
        {
            if (entry.Protocol != null) { entry.Protocol.Dispose(); entry.Protocol = null; }
            if (entry.Guard != null)
            {
                try { entry.Guard.Dispose(); } catch { }
                entry.Guard = null;
            }
            if (entry.Process != IntPtr.Zero) { Native.CloseHandle(entry.Process); entry.Process = IntPtr.Zero; }
            if (entry.Job != IntPtr.Zero) { Native.CloseHandle(entry.Job); entry.Job = IntPtr.Zero; }
            if (entry.ForwardRevoked != null) { entry.ForwardRevoked.Dispose(); entry.ForwardRevoked = null; }
        }

        // 仅游戏核心范围的所有者置为 true。后台、反作弊和微软后台的所有者保持严格准入。
        internal bool OverrideThreadPlacement;
        // SGuard 使用专用逐线程日志和两阶段恢复，不能借用游戏的无日志覆盖分支。
        internal bool OwnThreadAffinity;
        internal bool StrictThreadAdmission;
        internal bool Ensure(int pid, long birth, string name, ulong originalMask, ulong desiredMask, out string reason)
        { return EnsureCore(pid, birth, name, originalMask, desiredMask, null, out reason); }

        internal bool EnsureWindowsBackground(int pid, long birth, string name, ulong originalMask, ulong desiredMask, int mode, out string reason)
        { return EnsureCore(pid, birth, name, originalMask, desiredMask, BackgroundGovernancePolicy.Normalize(mode), out reason); }

        private bool EnsureCore(int pid, long birth, string name, ulong originalMask, ulong desiredMask, int? windowsMode, out string reason)
        {
            long admittedEpoch = Interlocked.Read(ref forwardEpoch);
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
                if (OwnThreadAffinity && !windowsMode.HasValue && threadRoot.Length != 0)
                { reason = "sguard-thread-owner-scope-conflict"; return false; }
                Entry entry;
                if (leases.TryGetValue(pid, out entry) && entry.Birth != birth)
                {
                    if (!ReleaseLocked(entry, out reason)) return false;
                    RequestGuardExit(entry);
                    if (GuardAlive(entry)) { entry.Pending = true; reason = "guardian-exit-pending"; return false; }
                    CloseSettled(entry); leases.Remove(pid);
                    if (!GameAffinityJobReceiptStore.Remove(entry.Name)) { recoveryPending = true; reason = "receipt-remove"; return false; }
                    entry = null;
                }
                if (entry == null)
                {
                    if (!Recover(out reason)) { recoveryPending = true; return false; }
                    recoveryPending = false;
                    if (windowsMode.HasValue) Native.TryEnableDebugPrivilege();
                    IntPtr process = windowsMode.HasValue ? Native.OpenProcess(0x101701, false, pid) : GameAffinityJobNative.OpenTarget(pid, birth);
                    if (process == IntPtr.Zero) { reason = "process-access"; return false; }
                    if (!GameAffinityJobNative.Matches(process, birth))
                    { Native.CloseHandle(process); reason = "target-identity"; return false; }
                    // Windows 在分配时核验已有 Job 层级的嵌套关系，不能直接拒绝启动器持有的 Job。

                    if (!string.IsNullOrEmpty(name) && !string.Equals(Native.ImageName(process), name, StringComparison.OrdinalIgnoreCase))
                    { Native.CloseHandle(process); reason = "target-name"; return false; }
                    ulong current;
                    if (!Native.TryQueryAffinity(process, out current) || current != originalMask)
                    { Native.CloseHandle(process); reason = "original-affinity-changed"; return false; }
                    WindowsBackgroundLimits resources = null;
                    if (windowsMode.HasValue && !WindowsBackgroundJobNative.Capture(process, windowsMode.Value, out resources, out reason))
                    { Native.CloseHandle(process); return false; }
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
                        ThreadRecoveryRoot = threadRoot, Resources = resources, OwnThreadAffinity = OwnThreadAffinity && !windowsMode.HasValue,
                        StrictThreadAdmission = OwnThreadAffinity && StrictThreadAdmission && !windowsMode.HasValue };
                    leases.Add(pid, entry);
                    if (entry.Record.OwnThreadAffinity)
                    {
                        try { entry.ForwardRevoked = new EventWaitHandle(false, EventResetMode.ManualReset, entry.Name + ".ForwardRevoked"); }
                        catch (Exception error) { entry.Pending = true; reason = "sguard-forward-signal:" + error.GetType().Name; return false; }
                        PublishView();
                        if (!ForwardCurrent(entry, admittedEpoch, out reason)) { entry.Pending = true; return false; }
                    }
                    if (!GameAffinityJobReceiptStore.Save(entry.Record))
                    { entry.Pending = true; reason = "receipt-write"; return false; }
                    if (!StartGuard(entry, out reason))
                    {
                        string cleanup;
                        ReleaseLocked(entry, out cleanup);
                        return false;
                    }
                }
                if (entry.Pending) { reason = "recovery-pending"; return false; }
                if ((entry.Record.Resources != null) != windowsMode.HasValue)
                { reason = "job-resource-owner-conflict"; return false; }
                if (entry.Record.OwnThreadAffinity != (OwnThreadAffinity && !windowsMode.HasValue))
                { reason = "job-thread-owner-conflict"; return false; }
                if (entry.Record.StrictThreadAdmission != (OwnThreadAffinity && StrictThreadAdmission && !windowsMode.HasValue))
                { reason = "job-thread-admission-mode-conflict"; return false; }
                if (entry.Active && entry.Desired == desiredMask
                    && (!windowsMode.HasValue || entry.Record.Resources.MatchesMode(windowsMode.Value)))
                {
                    if (!entry.Record.OwnThreadAffinity) return VerifyLocked(entry, desiredMask, out reason);
                    if (!GuardAlive(entry)) { entry.Pending = true; reason = "guardian-lost"; return false; }
                    if (!ForwardCurrent(entry, admittedEpoch, out reason)) return false;
                    return Command(entry, "MAINTAIN " + desiredMask, out reason) && VerifyLocked(entry, desiredMask, out reason);
                }
                // 改变活动范围或记录前，先拒绝游戏自行设置的线程放置；原生写入前再次核对。
                ulong currentRange;
                bool overrideThreads = (OverrideThreadPlacement || entry.Record.OwnThreadAffinity) && !windowsMode.HasValue;
                if (!Native.TryQueryAffinity(entry.Process, out currentRange)
                    || !(overrideThreads ? GameThreadAffinityAdmission.CheckProcessOnly(entry.Process, pid, birth, currentRange, out reason)
                        : GameThreadAffinityAdmission.Check(entry.Process, pid, birth, currentRange, out reason)))
                {
                    if (reason == null) reason = GameThreadAffinityAdmission.RefusalPrefix + "process-mask-unreadable";
                    return false;
                }
                if (entry.Active)
                {
                    // Desired 保存已应用范围；变更前先持久保存中性状态，防止崩溃后遗留旧限制。

                    if (!ReleaseLocked(entry, out reason)) return false;
                }
                if (!GuardAlive(entry))
                {
                    if (!ReleaseLocked(entry, out reason)) return false;
                    if (!StartGuard(entry, out reason)) return false;
                }
                if (!Native.TryQueryAffinity(entry.Process, out currentRange) || currentRange != originalMask)
                { reason = "original-affinity-changed"; return false; }
                entry.Original = originalMask;
                entry.Desired = desiredMask;
                if (windowsMode.HasValue)
                {
                    WindowsBackgroundLimits resources;
                    if (!WindowsBackgroundJobNative.Capture(entry.Process, windowsMode.Value, out resources, out reason)) return false;
                    entry.Record.Resources = resources;
                }
                entry.Record.Original = entry.Original; entry.Record.Desired = desiredMask; entry.Record.Phase = "armed";
                entry.Record.ThreadRecoveryRoot = threadRoot;
                if (entry.Record.OwnThreadAffinity)
                {
                    if (!ForwardCurrent(entry, admittedEpoch, out reason)) return false;
                    entry.ForwardRevoked.Reset();
                    if (!ForwardCurrent(entry, admittedEpoch, out reason)) return false;
                }
                if (!GameAffinityJobReceiptStore.Save(entry.Record)) { entry.Pending = true; reason = "receipt-arm"; return false; }
                // 守护已持有 Job 和目标句柄，且是唯一写入方；父进程退出不会使迟到的应用失去恢复所有者。

                if (!Command(entry, "APPLY " + desiredMask + " " + entry.Original + (entry.Record.OwnThreadAffinity ? " owned-threads" : overrideThreads ? " override" : ""), out reason)
                    || !SGuardAffinityOperations.Verify(entry.Record, entry.Job, entry.Process, out reason))
                {
                    string failure = reason, cleanup;
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
            if (!GuardAlive(entry) || entry.ProtocolLost) { entry.Pending = true; reason = "guardian-lost"; return false; }
            return SGuardAffinityOperations.Verify(entry.Record, entry.Job, entry.Process, out reason);
        }

        // Job 接管被拒后改用进程范围限制，回执同步报告实际后端。
        internal bool IsDirectRange(int pid, long birth)
        {
            using (BeginOperation())
            {
                Entry entry;
                return leases.TryGetValue(pid, out entry) && entry.Birth == birth && entry.Active && !entry.Pending
                    && SGuardAffinityOperations.DirectRange(entry.Record, entry.Job, entry.Process);
            }
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
            if (entry.ForwardRevoked != null) entry.ForwardRevoked.Set();
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
            if (entry.Pending && now < entry.NextRecovery)
            {
                bool guardAlive = GuardAlive(entry);
                if (guardAlive == entry.RecoveryGuardAlive && targetGone == entry.RecoveryTargetGone)
                { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(entry.NextRecovery - now)); reason = "recovery-backoff"; return false; }
            }
            bool restored = ReleaseCore(entry, out reason);
            if (restored) { entry.NextRecovery = 0; entry.RecoveryDelay = 0; entry.RecoveryAttempts = 0; entry.RecoveryOrderingObservations = 0; }
            else if (reason == "thread-recovery-pending" || reason == "recovery-operation-pending"
                || reason == "guardian-forward-revocation-pending")
            {
                // 进程恢复先等待线程日志，使用独立有限观察预算，不执行原生恢复或退还失败次数。

                entry.Pending = true;
                entry.RecoveryOrderingObservations++;
                if (entry.RecoveryOrderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                    RecoveryAttemptTrace.Exhausted(entry.RecoveryExitPhase ? (Func<bool>)null : () => Native.WaitForSingleObject(entry.Process, 0) == 0);
                else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                if (entry.RecoveryOrderingObservations == 1 || entry.RecoveryOrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                    Logger.Warn("游戏亲和性恢复顺序：目标=" + entry.Pid + "/" + entry.Birth
                        + " 观测=" + entry.RecoveryOrderingObservations + "/" + GameAffinityJobRecord.RecoveryOrderingObservationLimit
                        + " 原因=" + reason + (entry.RecoveryOrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit
                            ? "；已停止主动尝试，保留回执和句柄" : ""));
            }
            else
            {
                entry.RecoveryAttempts++;
                entry.Pending = true;
                entry.RecoveryDelay = entry.RecoveryDelay == 0 ? 500 : Math.Min(30000, entry.RecoveryDelay * 2);
                entry.NextRecovery = AutoCores.NowTicks + TimeSpan.FromMilliseconds(entry.RecoveryDelay).Ticks;
                entry.RecoveryGuardAlive = GuardAlive(entry);
                entry.RecoveryTargetGone = Native.WaitForSingleObject(entry.Process, 0) == 0;
                if (entry.RecoveryAttempts >= 3)
                    RecoveryAttemptTrace.Exhausted(entry.RecoveryExitPhase ? (Func<bool>)null : () => Native.WaitForSingleObject(entry.Process, 0) == 0);
                else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(entry.NextRecovery - AutoCores.NowTicks));
                Logger.Warn("游戏亲和性恢复：目标=" + entry.Pid + "/" + entry.Birth
                    + " 尝试=" + entry.RecoveryAttempts + "/3 原因=" + reason
                    + (entry.RecoveryAttempts == 3 ? "；已停止主动重试，保留回执和句柄" : "；恢复尚未完成"));
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
            if (GuardAlive(entry) && Command(entry, "CLEAR", out reason))
            { entry.Active = false; entry.Pending = false; entry.Record.Phase = "neutral"; return true; }
            // EOF 撤销写入权限；后续恢复轮询只观察退出，不再额外花三秒等待子进程。

            RequestGuardExit(entry, false);
            if (GuardAlive(entry))
            { entry.Pending = true; reason = "guardian-forward-revocation-pending"; return false; }
            string failure = null;
            bool contended;
            bool restored = GameAffinityJobOperation.Run(entry.Record, delegate
            {
                GameAffinityJobRecord current;
                if (!GameAffinityJobReceiptStore.Find(entry.Name, out current)) { failure = "receipt-unreadable"; return false; }
                bool gone = Native.WaitForSingleObject(entry.Process, 0) == 0;
                if (current == null && entry.Record.OwnThreadAffinity) { failure = "sguard-receipt-missing-retained"; return false; }
                if (current == null)
                    return gone ? WindowsBackgroundJobNative.ClearGone(entry.Record, entry.Job, out failure)
                        : WindowsBackgroundJobNative.Neutral(entry.Record, entry.Job, out failure);
                if (!entry.Record.SameScope(current)) { failure = "receipt-scope-changed"; return false; }
                if (!gone)
                {
                    bool threadPending;
                    if (!current.TryThreadRecoveryPending(out threadPending)) { failure = "thread-recovery-unconfirmed"; return false; }
                    if (threadPending) { failure = "thread-recovery-pending"; return false; }
                }
                bool clean = SGuardAffinityOperations.Restore(current, entry.Job, entry.Process, out failure);
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
            RevokeForward(pid, birth);
            using (BeginOperation())
            {
                Entry entry;
                if (!leases.TryGetValue(pid, out entry) || entry.Birth != birth) { reason = null; return true; }
                return ReleaseLocked(entry, out reason);
            }
        }

        internal bool ReleaseExcept(int keepPid, long keepBirth, out string reason)
        {
            foreach (EntryView entry in view.Entries)
                if (entry.OwnThreads && (entry.Pid != keepPid || entry.Birth != keepBirth)) RevokeForward(entry.Pid, entry.Birth);
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
                    RequestGuardExit(entry);
                    if (GuardAlive(entry)) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); entry.Pending = true; ok = false; reason = "guardian-exit-pending"; continue; }
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
            RevokeForward();
            using (BeginOperation())
            {
                if (!ReleaseAll(out reason)) return false;
                foreach (Entry entry in new List<Entry>(leases.Values))
                {
                    RequestGuardExit(entry);
                    if (GuardAlive(entry)) { entry.Pending = true; reason = "guardian-exit-pending"; return false; }
                    CloseSettled(entry);
                    leases.Remove(entry.Pid);
                }
                // 最后一个 Job 句柄关闭后，中性记录仍然安全；保留到 Recover 确认它并非遗留限制。

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
                    Logger.Warn("游戏亲和性遗留回执读取失败=" + orphanReadFailures + "/3；保留恢复状态");
                    reason = "receipt-unreadable"; return false;
                }
                int self;
                using (Process current = System.Diagnostics.Process.GetCurrentProcess()) self = current.Id;
                bool all = true;
                foreach (GameAffinityJobRecord record in records)
                {
                    // 观测身份或退出不算另一次恢复尝试。
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
                                Logger.Warn("游戏亲和性遗留所有者顺序：回执=" + record.Name + " 观测=" + budget.OrderingObservations
                                    + "/" + GameAffinityJobRecord.RecoveryOrderingObservationLimit + " 原因=" + reason
                                    + (budget.OrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit
                                        ? "；已停止主动尝试，保留回执和句柄" : ""));
                        }
                        else
                        {
                            budget.Failures++;
                            budget.Next = AutoCores.NowTicks + TimeSpan.FromSeconds(4 * budget.Failures).Ticks;
                            if (budget.Failures >= 3) ReportOrphanExhausted(record, budget);
                            else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(budget.Next - AutoCores.NowTicks));
                            Logger.Warn("游戏亲和性遗留所有者身份：回执=" + record.Name + " 失败详情=" + budget.Failures + "/3 原因=" + reason
                                + (budget.Failures >= 3 ? "；已停止主动尝试，保留回执和句柄" : ""));
                        }
                        continue;
                    }
                    string failure;
                    bool restored;
                    try { restored = RecoverRecord(record, target, budget, out failure); }
                    catch (Exception ex) { restored = false; failure = "recovery-exception:" + ex.GetType().Name; }
                    if (!restored && (failure == "thread-recovery-pending" || failure == "recovery-operation-pending"))
                    {
                        // 未执行原生恢复时不退还失败次数，上游债务观察也有次数上限。

                        all = false; reason = failure;
                        budget.OrderingObservations++;
                        if (budget.OrderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                            ReportOrphanExhausted(record, budget);
                        else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                        if (budget.OrderingObservations == 1 || budget.OrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                            Logger.Warn("游戏亲和性遗留顺序检查：回执=" + record.Name + " 观测=" + budget.OrderingObservations
                                + "/" + GameAffinityJobRecord.RecoveryOrderingObservationLimit + " 原因=" + reason
                                + (budget.OrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit
                                    ? "；已停止主动尝试，保留回执和句柄" : ""));
                    }
                    else if (!restored)
                    {
                        all = false; reason = failure;
                        budget.Failures++;
                        budget.Next = AutoCores.NowTicks + TimeSpan.FromSeconds(4 * budget.Failures).Ticks;
                        if (budget.Failures >= 3) ReportOrphanExhausted(record, budget);
                        else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(budget.Next - AutoCores.NowTicks));
                        Logger.Warn("游戏亲和性遗留恢复：回执=" + record.Name + " 失败详情=" + budget.Failures + "/3 原因=" + reason
                            + (budget.Failures >= 3 ? "；已停止主动尝试，保留回执" : ""));
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
                // 未进入 Job 的 SGuard 目标不依赖 Job 句柄，可按进程范围和线程日志直接结清。
                if (openError == 2 && record.Phase == "armed" && target != 1 && record.OwnThreadAffinity && !record.StrictThreadAdmission)
                    return RecoverWithoutJob(record, budget, out reason);
                if (openError != 2 || (record.Phase == "armed" && target != 1))
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
                    if (process == IntPtr.Zero) process = budget.Process = WindowsBackgroundJobNative.OpenRestore(record);
                    if (process == IntPtr.Zero) { reason = "recovery-process-access"; return false; }
                }
                string failure = null;
                bool contended;
                bool restored = GameAffinityJobOperation.Run(record, delegate
                {
                    GameAffinityJobRecord current;
                    if (!GameAffinityJobReceiptStore.Find(record.Name, out current)) { failure = "receipt-unreadable"; return false; }
                    if (current == null && record.OwnThreadAffinity) { failure = "sguard-receipt-missing-retained"; return false; }
                    if (current == null)
                        return target == 1 ? WindowsBackgroundJobNative.ClearGone(record, job, out failure)
                            : WindowsBackgroundJobNative.Neutral(record, job, out failure);
                    if (!record.SameScope(current)) { failure = "receipt-scope-changed"; return false; }
                    if (target != 1)
                    {
                        bool threadPending;
                        if (!current.TryThreadRecoveryPending(out threadPending)) { failure = "thread-recovery-unconfirmed"; return false; }
                        if (threadPending) { failure = "thread-recovery-pending"; return false; }
                    }
                    bool cleared = SGuardAffinityOperations.Restore(current, job, target == 1 ? IntPtr.Zero : process, out failure);
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
                // 原始记录尚未结清时，保留最后可访问的句柄，预算耗尽后也不释放。

                if (settled) CloseRecoveredOrphan(budget);
            }
        }

        private static bool RecoverWithoutJob(GameAffinityJobRecord record, OrphanRecoveryBudget budget, out string reason)
        {
            reason = null;
            if (budget.Process == IntPtr.Zero) budget.Process = WindowsBackgroundJobNative.OpenRestore(record);
            IntPtr process = budget.Process;
            if (process == IntPtr.Zero) { reason = "recovery-process-access"; return false; }
            bool settled = false;
            try
            {
                string failure = null;
                bool contended;
                settled = GameAffinityJobOperation.Run(record, delegate
                {
                    GameAffinityJobRecord current;
                    if (!GameAffinityJobReceiptStore.Find(record.Name, out current)) { failure = "receipt-unreadable"; return false; }
                    if (current == null) { failure = "sguard-receipt-missing-retained"; return false; }
                    if (!record.SameScope(current)) { failure = "receipt-scope-changed"; return false; }
                    if (!SGuardAffinityOperations.RestoreWithoutJob(current, process, out failure)) return false;
                    current.Phase = "neutral";
                    if (!GameAffinityJobReceiptStore.Save(current)) { failure = "receipt-neutral"; return false; }
                    return true;
                }, out contended);
                reason = failure ?? (settled ? null : (contended ? "recovery-operation-pending" : "recovery-operation-unconfirmed"));
                return settled;
            }
            finally
            {
                // 未结清时保留进程句柄，后续尝试沿用同一身份。
                if (settled) CloseRecoveredOrphan(budget);
            }
        }

        private static void CloseRecoveredOrphan(OrphanRecoveryBudget budget)
        {
            if (budget.Process != IntPtr.Zero) { Native.CloseHandle(budget.Process); budget.Process = IntPtr.Zero; }
            if (budget.Job != IntPtr.Zero) { Native.CloseHandle(budget.Job); budget.Job = IntPtr.Zero; }
        }

    }
}
