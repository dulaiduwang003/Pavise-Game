using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal static class GameAffinityJobGuard
    {
        internal const string Argument = "--game-affinity-job-guard";
        internal enum RecoveryOutcome { Settled, OrderingWait, Failed }

        internal static bool TryHandleArgs(string[] args)
        {
            if (args == null || args.Length == 0 || args[0] != Argument) return false;
            Environment.ExitCode = 1;
            int ownerPid, pid; long ownerBirth, birth; ulong original, desired;
            if (args.Length != 8 || !int.TryParse(args[1], out ownerPid) || ownerPid <= 4
                || !long.TryParse(args[2], out ownerBirth) || ownerBirth <= 0
                || !int.TryParse(args[3], out pid) || pid <= 4
                || !long.TryParse(args[4], out birth) || birth <= 0
                || !ulong.TryParse(args[5], out original) || original == 0
                || !ulong.TryParse(args[6], out desired) || desired == 0
                || !GameAffinityJobRecord.ValidName(args[7], pid, birth)) return true;
            IntPtr parent = IntPtr.Zero, target = IntPtr.Zero, job = IntPtr.Zero;
            try
            {
                GameAffinityJobRecord record;
                if (!Find(args[7], ownerPid, ownerBirth, pid, birth, out record)
                    || record.Original != original || record.Desired != desired) return true;
                parent = GameAffinityJobNative.OpenIdentity(ownerPid, ownerBirth);
                target = GameAffinityJobNative.OpenTarget(pid, birth);
                job = GameAffinityJobNative.Open(record.Name);
                string reason; uint flags; ulong mask;
                if (parent == IntPtr.Zero || target == IntPtr.Zero || job == IntPtr.Zero
                    || !GameAffinityJobNative.Inspect(job, out flags, out mask, out reason)) return true;
                Environment.ExitCode = Run(parent, target, job, record) ? 0 : 1;
            }
            catch { Environment.ExitCode = 1; }
            finally
            {
                if (job != IntPtr.Zero) Native.CloseHandle(job);
                if (target != IntPtr.Zero) Native.CloseHandle(target);
                if (parent != IntPtr.Zero) Native.CloseHandle(parent);
            }
            return true;
        }

        private static bool Find(string name, int ownerPid, long ownerBirth, int pid, long birth, out GameAffinityJobRecord record)
        {
            record = null;
            List<GameAffinityJobRecord> records;
            if (!GameAffinityJobReceiptStore.Read(out records)) return false;
            foreach (GameAffinityJobRecord current in records)
                if (current.Name == name && current.OwnerPid == ownerPid && current.OwnerBirth == ownerBirth
                    && current.Pid == pid && current.Birth == birth) { record = current; return true; }
            return false;
        }

        private static bool Settle(IntPtr target, IntPtr job, GameAffinityJobRecord record, out string reason)
        {
            string failure = null;
            bool contended;
            bool settled = GameAffinityJobOperation.Run(record, delegate { return SettleLocked(target, job, record, out failure); }, out contended);
            reason = failure ?? (settled ? null : (contended ? "recovery-operation-pending" : "recovery-operation-unconfirmed"));
            return settled;
        }

        private static bool SettleLocked(IntPtr target, IntPtr job, GameAffinityJobRecord record, out string reason)
        {
            GameAffinityJobRecord current;
            if (!GameAffinityJobReceiptStore.Find(record.Name, out current)) { reason = "receipt-unreadable"; return false; }
            if (current == null)
                return Native.WaitForSingleObject(target, 0) == 0 ? GameAffinityJobNative.ClearForGoneTarget(job, out reason)
                    : GameAffinityJobNative.VerifyNeutral(job, out reason);
            if (!record.SameScope(current)) { reason = "receipt-scope-changed"; return false; }
            record = current;
            uint targetState = Native.WaitForSingleObject(target, 0);
            if (targetState != 0)
            {
                if (targetState != 258) { reason = "restore-process-unreadable"; return false; }
                bool debt;
                if (!record.TryThreadRecoveryPending(out debt)) { reason = "thread-recovery-unconfirmed"; return false; }
                if (debt) { reason = "thread-recovery-pending"; return false; }
            }
            bool restored = record.Phase != "armed" ? GameAffinityJobNative.VerifyNeutral(job, out reason)
                : GameAffinityJobNative.Restore(job, target, record.Birth, record.Original, record.Desired, out reason);
            if (!restored) return false;
            record.Phase = "neutral";
            if (!GameAffinityJobReceiptStore.Save(record)) { reason = "receipt-neutral"; return false; }
            // Persist neutral before relinquishing the last accessible job name.
            // A live process cannot detach from its now harmless neutral job.
            if (Native.WaitForSingleObject(target, 0) == 0 && !GameAffinityJobReceiptStore.Remove(record.Name))
            { reason = "receipt-remove"; return false; }
            return true;
        }

        private static void Reply(string text)
        { Console.WriteLine(text); Console.Out.Flush(); }

        internal static bool RetainRecoveryUntilSettled(Func<bool> settle, Action<int> wait)
        {
            int delay = 500;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                bool settled = false;
                try { settled = settle(); } catch { }
                if (settled) return true;
                if (attempt == 3) return false;
                try { wait(delay); }
                catch { Thread.Sleep(delay); }
                delay = Math.Min(30000, delay * 2);
            }
            return false;
        }

        internal static bool RetainRecoveryAcrossTargetExit(Func<bool> settle, Action<int> wait, Func<bool> targetGone, Func<bool> waitForTargetExit, int initialDelay)
        {
            // An untyped false or exception counts as a failure, not an ordering wait.
            return RetainRecoveryAcrossTargetExitTyped(
                () => settle() ? RecoveryOutcome.Settled : RecoveryOutcome.Failed,
                wait, targetGone, waitForTargetExit, initialDelay);
        }

        internal static bool RetainRecoveryAcrossTargetExitTyped(Func<RecoveryOutcome> settle, Action<int> wait, Func<bool> targetGone, Func<bool> waitForTargetExit, int initialDelay)
        {
            bool exitPhase = false;
            try { exitPhase = targetGone(); } catch { }
            int attempts = 0, orderingObservations = 0, delay = initialDelay;
            while (true)
            {
                bool gone = exitPhase;
                if (!gone) { try { gone = targetGone(); } catch { } }
                if (!exitPhase && gone)
                {
                    exitPhase = true; attempts = 0; orderingObservations = 0; delay = initialDelay;
                }
                RecoveryOutcome outcome = RecoveryOutcome.Failed;
                try { outcome = settle(); } catch { }
                if (outcome == RecoveryOutcome.Settled) return true;
                if (outcome == RecoveryOutcome.OrderingWait)
                {
                    // Journal ordering debt and mutex timeouts are observations, not native restores.
                    // Bound those observations separately without refunding earlier failures.
                    orderingObservations++;
                    if (orderingObservations < GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                    {
                        try { wait(initialDelay); } catch { Thread.Sleep(initialDelay); }
                        continue;
                    }
                    Logger.Warn("Guardian recovery ordering exhausted: observations=" + orderingObservations
                        + "; original ownership retained; active attempts stopped");
                }
                else
                {
                    attempts++;
                    Logger.Warn("Guardian recovery: phase=" + (exitPhase ? "target-exit" : "live") + " attempt=" + attempts + "/3; original ownership retained");
                }
                if (attempts >= 3 || orderingObservations >= GameAffinityJobRecord.RecoveryOrderingObservationLimit)
                {
                    if (exitPhase) return false;
                    bool exited = false;
                    try { exited = waitForTargetExit(); } catch { }
                    if (!exited) return false;
                    exitPhase = true; attempts = 0; orderingObservations = 0; delay = initialDelay;
                    continue;
                }
                try { wait(delay); } catch { Thread.Sleep(delay); }
                delay *= 2;
            }
        }

        private static void WaitForRecovery(IntPtr target, int delay)
        {
            // A live target exiting can wake one retry early. Once signaled (or
            // unreadable), use a timed sleep so a bad receipt cannot busy-loop.
            if (!GameAffinityJobNative.Alive(target)) { Thread.Sleep(delay); return; }
            uint result = Native.WaitForSingleObject(target, (uint)delay);
            if (result != 0 && result != 258) Thread.Sleep(delay);
        }

        private static bool Run(IntPtr parent, IntPtr target, IntPtr job, GameAffinityJobRecord record)
        {
            var commands = new BlockingCollection<string>(8);
            var reader = new Thread(delegate()
            {
                try
                {
                    string line;
                    while ((line = Console.ReadLine()) != null)
                        if (line.Length > 128 || !commands.TryAdd(line)) break;
                }
                catch { }
                finally { commands.CompleteAdding(); }
            });
            reader.IsBackground = true;
            reader.Start();
            bool pending = false;
            try
            {
                Reply("READY");
                while (GameAffinityJobNative.Alive(parent) && GameAffinityJobNative.Alive(target))
                {
                    // EOF revokes forward authority even when commands were buffered.
                    if (commands.IsAddingCompleted) break;
                    string command;
                    if (!commands.TryTake(out command, 250)) continue;
                    if (!GameAffinityJobNative.Alive(parent) || commands.IsAddingCompleted) break;
                    string reason;
                    if (command == "CLEAR")
                    {
                        pending = !Settle(target, job, record, out reason);
                        Reply(pending ? "ERR " + reason : "OK");
                        continue;
                    }
                    string[] fields = command.Split(' ');
                    ulong desired, original;
                    if (fields.Length != 3 || fields[0] != "APPLY" || pending
                        || !ulong.TryParse(fields[1], out desired) || desired == 0
                        || !ulong.TryParse(fields[2], out original) || original == 0)
                    { Reply("ERR invalid-command-or-recovery-pending"); break; }
                    GameAffinityJobRecord armed;
                    if (!Find(record.Name, record.OwnerPid, record.OwnerBirth, record.Pid, record.Birth, out armed)
                        || armed.Phase != "armed" || armed.Desired != desired || armed.Original != original)
                    { Reply("ERR receipt-not-armed"); break; }
                    record = armed;
                    string applyFailure = null;
                    bool applied = GameAffinityJobOperation.Run(record, delegate
                    {
                        // Recheck forward authority under the startup-recovery gate to reject buffered APPLY after EOF.
                        if (!GameAffinityJobNative.Alive(parent) || commands.IsAddingCompleted)
                        { applyFailure = "forward-owner-lost"; return false; }
                        return GameAffinityJobNative.Apply(job, target, record.Birth, desired, out applyFailure);
                    });
                    reason = applyFailure ?? (applied ? null : "apply-operation-pending");
                    if (applied) Reply("OK");
                    else
                    {
                        string failure = reason;
                        pending = !Settle(target, job, record, out reason);
                        Reply("ERR " + failure + (pending ? "; recovery=" + reason : ""));
                    }
                }
            }
            catch { }
            finally
            {
                // Keep both original handles alive until restoration and its neutral
                // receipt are confirmed. Closing handles is never a recovery action.
                Func<RecoveryOutcome> settle = delegate
                {
                    string reason;
                    if (Settle(target, job, record, out reason)) return RecoveryOutcome.Settled;
                    return reason == "thread-recovery-pending" || reason == "recovery-operation-pending"
                        ? RecoveryOutcome.OrderingWait : RecoveryOutcome.Failed;
                };
                Action<int> wait = delegate(int delay) { WaitForRecovery(target, delay); };
                if (!RetainRecoveryAcrossTargetExitTyped(settle, wait,
                    () => Native.WaitForSingleObject(target, 0) == 0,
                    () => Native.WaitForSingleObject(target, uint.MaxValue) == 0, 500))
                {
                    Logger.Warn("Game affinity guardian recovery exhausted: ownership retained, no further active attempts");
                    Thread.Sleep(Timeout.Infinite);
                }
            }
            return true;
        }
    }
}
