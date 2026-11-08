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
                value.ThreadRecoveryRoot = fields.Length == 9 ? Encoding.UTF8.GetString(Convert.FromBase64String(fields[8])) : "";
                if (value.ThreadRecoveryRoot.Length > 0 && (!Path.IsPathRooted(value.ThreadRecoveryRoot)
                    || Path.GetFullPath(value.ThreadRecoveryRoot) != value.ThreadRecoveryRoot)) return null;
            }
            catch { return null; }
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
                            GameAffinityJobRecord record = GameAffinityJobRecord.Parse(line.TrimEnd('\r'));
                            if (record == null || !names.Add(record.Name) || records.Count >= 256) return false;
                            records.Add(record);
                        }
                        if (!action(records)) return false;
                        if (!write) return true;
                        string[] rows = new string[records.Count];
                        for (int i = 0; i < rows.Length; i++) rows[i] = records[i].Encode();
                        string text = string.Join("\n", rows), verify;
                        return Settings.SaveStrDurable(GameAffinityJobLease.ReceiptKey, text)
                            && Settings.TryLoadStr(GameAffinityJobLease.ReceiptKey, out verify) && verify == text;
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
        }

        private sealed class LeaseView
        {
            internal readonly EntryView[] Entries;
            internal readonly bool Pending, Active, Busy;
            internal LeaseView(EntryView[] entries, bool pending, bool active, bool busy)
            { Entries = entries; Pending = pending; Active = active; Busy = busy; }
        }
        private sealed class EntryView
        {
            internal readonly int Pid;
            internal readonly long Birth;
            internal readonly bool Active;
            internal EntryView(Entry entry)
            { Pid = entry.Pid; Birth = entry.Birth; Active = entry.Active && !entry.Pending; }
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
        private bool recoveryPending;
        private int operationDepth;
        private static readonly LeaseView UnknownView = new LeaseView(new EntryView[0], true, true, true);
        private volatile LeaseView view = new LeaseView(new EntryView[0], false, false, false);
        internal string ThreadRecoveryRoot { get; set; }

        private Operation BeginOperation(bool wait = true)
        {
            // Allocate the view before admission so allocation failure cannot leave unreported ownership.
            var operation = new Operation(this);
            if (wait) Monitor.Enter(gate);
            else if (!Monitor.TryEnter(gate)) return null;
            operationDepth++;
            try { PublishView(); return operation; }
            catch
            {
                operationDepth--;
                view = UnknownView; // Retain ownership until a later successful publication.
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
            view = new LeaseView(entries, pending, active, operationDepth != 0);
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

        internal bool HasLease(int pid, long birth)
        {
            LeaseView snapshot = view;
            // Uncertain ownership must retain the caller's recovery handle.
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
            catch { return true; } // Unknown exit state must retain recovery authority.
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

        private static bool StartGuard(Entry entry, out string reason)
        {
            reason = null;
            try
            {
                if (GuardAlive(entry)) { reason = "previous-guardian-live"; return false; }
                if (entry.Protocol != null) { entry.Protocol.Dispose(); entry.Protocol = null; }
                if (entry.Guard != null) entry.Guard.Dispose();
                entry.ProtocolLost = false;
                var start = new ProcessStartInfo(typeof(GameAffinityJobLease).Assembly.Location,
                    GameAffinityJobGuard.Argument + " " + entry.Record.OwnerPid + " " + entry.Record.OwnerBirth
                    + " " + entry.Pid + " " + entry.Birth + " " + entry.Original + " " + entry.Desired + " " + entry.Name);
                start.UseShellExecute = false; start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
                start.RedirectStandardInput = true; start.RedirectStandardOutput = true;
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
        }

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
                    IntPtr process = GameAffinityJobNative.OpenTarget(pid, birth);
                    if (process == IntPtr.Zero) { reason = "process-access"; return false; }
                    // Windows validates nesting against an existing job hierarchy
                    // during assignment. Do not reject launcher-owned jobs outright.
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
                    if (!StartGuard(entry, out reason))
                    {
                        string cleanup;
                        ReleaseLocked(entry, out cleanup);
                        return false;
                    }
                }
                if (entry.Pending) { reason = "recovery-pending"; return false; }
                if (entry.Active && entry.Desired == desiredMask) return VerifyLocked(entry, desiredMask, out reason);
                // Reject game-owned thread placement before changing the active range or receipt; recheck before the native write.
                ulong currentRange;
                if (!Native.TryQueryAffinity(entry.Process, out currentRange)
                    || !GameThreadAffinityAdmission.Check(entry.Process, pid, birth, currentRange, out reason))
                {
                    if (reason == null) reason = GameThreadAffinityAdmission.RefusalPrefix + "process-mask-unreadable";
                    return false;
                }
                if (entry.Active)
                {
                    // Desired records the applied range, not the next request.
                    // Persist neutral before changing range so a crash cannot orphan the previous limit.
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
                entry.Record.Original = entry.Original; entry.Record.Desired = desiredMask; entry.Record.Phase = "armed";
                entry.Record.ThreadRecoveryRoot = threadRoot;
                if (!GameAffinityJobReceiptStore.Save(entry.Record)) { entry.Pending = true; reason = "receipt-arm"; return false; }
                // The guardian already holds the job and target handles. It is the
                // sole forward writer, so parent death cannot orphan a late apply.
                if (!Command(entry, "APPLY " + desiredMask + " " + entry.Original, out reason)
                    || !GameAffinityJobNative.Verify(entry.Job, entry.Process, birth, desiredMask, out reason))
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
            if (!entry.Active || entry.Pending) { reason = "job-not-active"; return false; }
            if (!GuardAlive(entry)) { entry.Pending = true; reason = "guardian-lost"; return false; }
            return GameAffinityJobNative.Verify(entry.Job, entry.Process, entry.Birth, desiredMask, out reason);
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
                // Wait for the thread journal before process recovery; no native restore is attempted here.
                // Use a separate finite observation budget without refunding failures.
                entry.Pending = true;
                entry.RecoveryOrderingObservations++;
                if (entry.RecoveryOrderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                    RecoveryAttemptTrace.Exhausted(entry.RecoveryExitPhase ? (Func<bool>)null : () => Native.WaitForSingleObject(entry.Process, 0) == 0);
                else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                if (entry.RecoveryOrderingObservations == 1 || entry.RecoveryOrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                    Logger.Warn("Game affinity recovery ordering: target=" + entry.Pid + "/" + entry.Birth
                        + " observation=" + entry.RecoveryOrderingObservations + "/" + GameAffinityJobRecord.RecoveryOrderingObservationLimit
                        + " reason=" + reason + (entry.RecoveryOrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit
                            ? "; active attempts stopped; receipt and handles retained" : ""));
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
                Logger.Warn("Game affinity recovery: target=" + entry.Pid + "/" + entry.Birth
                    + " attempt=" + entry.RecoveryAttempts + "/3 reason=" + reason
                    + (entry.RecoveryAttempts == 3 ? "; active retries stopped; receipt and handles retained" : "; restoration pending"));
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
            // EOF revokes forward authority. Subsequent recovery polls observe
            // exit without spending another three seconds waiting on this child.
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
                bool clean = current.Phase != "armed" ? GameAffinityJobNative.VerifyNeutral(entry.Job, out failure)
                    : GameAffinityJobNative.Restore(entry.Job, entry.Process, current.Birth, current.Original, current.Desired, out failure);
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
                // Neutral receipts are safe even after the last job handle closes;
                // keep them until Recover distinguishes this from an orphaned limit.
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
                    Logger.Warn("Game affinity orphan receipt read failure=" + orphanReadFailures + "/3; recovery retained");
                    reason = "receipt-unreadable"; return false;
                }
                int self;
                using (Process current = System.Diagnostics.Process.GetCurrentProcess()) self = current.Id;
                bool all = true;
                foreach (GameAffinityJobRecord record in records)
                {
                    // Observing identity/exit is not another restoration attempt.
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
                                Logger.Warn("Game affinity orphan owner ordering: receipt=" + record.Name + " observation=" + budget.OrderingObservations
                                    + "/" + GameAffinityJobRecord.RecoveryOrderingObservationLimit + " reason=" + reason
                                    + (budget.OrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit
                                        ? "; active attempts stopped; receipt and handles retained" : ""));
                        }
                        else
                        {
                            budget.Failures++;
                            budget.Next = AutoCores.NowTicks + TimeSpan.FromSeconds(4 * budget.Failures).Ticks;
                            if (budget.Failures >= 3) ReportOrphanExhausted(record, budget);
                            else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(budget.Next - AutoCores.NowTicks));
                            Logger.Warn("Game affinity orphan owner identity: receipt=" + record.Name + " failure=" + budget.Failures + "/3 reason=" + reason
                                + (budget.Failures >= 3 ? "; active attempts stopped; receipt and handles retained" : ""));
                        }
                        continue;
                    }
                    string failure;
                    bool restored;
                    try { restored = RecoverRecord(record, target, budget, out failure); }
                    catch (Exception ex) { restored = false; failure = "recovery-exception:" + ex.GetType().Name; }
                    if (!restored && (failure == "thread-recovery-pending" || failure == "recovery-operation-pending"))
                    {
                        // No native restoration was attempted. Preserve failures,
                        // but also cap observations if the upstream debt never settles.
                        all = false; reason = failure;
                        budget.OrderingObservations++;
                        if (budget.OrderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                            ReportOrphanExhausted(record, budget);
                        else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                        if (budget.OrderingObservations == 1 || budget.OrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                            Logger.Warn("Game affinity orphan ordering: receipt=" + record.Name + " observation=" + budget.OrderingObservations
                                + "/" + GameAffinityJobRecord.RecoveryOrderingObservationLimit + " reason=" + reason
                                + (budget.OrderingObservations == GameAffinityJobRecord.RecoveryOrderingObservationLimit
                                    ? "; active attempts stopped; receipt and handles retained" : ""));
                    }
                    else if (!restored)
                    {
                        all = false; reason = failure;
                        budget.Failures++;
                        budget.Next = AutoCores.NowTicks + TimeSpan.FromSeconds(4 * budget.Failures).Ticks;
                        if (budget.Failures >= 3) ReportOrphanExhausted(record, budget);
                        else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(budget.Next - AutoCores.NowTicks));
                        Logger.Warn("Game affinity orphan recovery: receipt=" + record.Name + " failure=" + budget.Failures + "/3 reason=" + reason
                            + (budget.Failures >= 3 ? "; active attempts stopped; receipt retained" : ""));
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
                    bool cleared = current.Phase != "armed" ? GameAffinityJobNative.VerifyNeutral(job, out failure)
                        : (target == 1 ? GameAffinityJobNative.ClearForGoneTarget(job, out failure)
                        : GameAffinityJobNative.Restore(job, process, current.Birth, current.Original, current.Desired, out failure));
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
                // Keep the last accessible handles while the original receipt
                // is unresolved, including after this phase spends its budget.
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
