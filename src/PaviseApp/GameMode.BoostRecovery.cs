using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class BoostRecoveryAttempt
        {
            internal int Failures;
            internal long Due, ExitProbeDue;
            internal bool Running, ExitPhase, CheckingPhase;
            internal List<Func<bool>> ChildObservers;
        }

        private readonly Dictionary<string, BoostRecoveryAttempt> boostRecoveries = new Dictionary<string, BoostRecoveryAttempt>(StringComparer.Ordinal);
        private sealed class BoostRecoveryContext
        {
            internal GameMode Owner;
            internal string Phase;
        }
        [ThreadStatic] private static BoostRecoveryContext boostRecoveryContext;

        private static bool BoostRecoveryTargetExited(int pid, long birth)
        {
            IntPtr handle = Native.OpenProcess(0x100000 | 4096, false, pid);
            if (handle == IntPtr.Zero) return Native.LastOpenProcessFailureWasNoSuchProcess();
            try
            {
                if (Native.WaitForSingleObject(handle, 0) == 0) return true;
                long creation, cpu; ulong io;
                return Native.QueryProcessSample(handle, out creation, out cpu, out io) && creation != birth;
            }
            finally { Native.CloseHandle(handle); }
        }

        // Share one throttled owner-exit observer; observations do not consume mutation/read failure attempts.
        private bool ObserveBoostRecoveryPhase(BoostRecoveryAttempt attempt, int pid, long birth)
        {
            List<Func<bool>> children;
            bool observeTarget;
            lock (sync)
            {
                long now = DateTime.UtcNow.Ticks;
                if (attempt.Running || attempt.CheckingPhase || now < attempt.ExitProbeDue) return false;
                observeTarget = attempt.Failures >= 3 && !attempt.ExitPhase;
                children = attempt.ChildObservers;
                if (!observeTarget && (children == null || children.Count == 0)) return false;
                attempt.CheckingPhase = true;
                attempt.ExitProbeDue = now + TimeSpan.FromSeconds(30).Ticks;
            }
            bool targetExited = false, childChanged = false;
            try
            {
                if (observeTarget) try { targetExited = BoostRecoveryTargetExited(pid, birth); } catch { }
                if (children != null) foreach (Func<bool> child in children)
                    try { childChanged |= child(); } catch { }
            }
            finally
            {
                lock (sync)
                {
                    attempt.CheckingPhase = false;
                    if (targetExited)
                    {
                        attempt.ExitPhase = true;
                        attempt.Failures = 0;
                    }
                    if (targetExited || childChanged) { attempt.Due = 0; attempt.ChildObservers = null; }
                }
            }
            return targetExited || childChanged;
        }

        private void ReportBoostRecoveryWait(BoostRecoveryAttempt attempt, int pid, long birth)
        {
            if (attempt.Due != long.MaxValue && attempt.Failures < 3)
                RecoveryAttemptTrace.DeferredUntil(attempt.Due);
            else
                RecoveryAttemptTrace.Exhausted(attempt.Failures >= 3 && attempt.ExitPhase
                    && (attempt.ChildObservers == null || attempt.ChildObservers.Count == 0)
                    ? (Func<bool>)null : () => ObserveBoostRecoveryPhase(attempt, pid, birth));
        }

        private bool RunBoostRecovery(int pid, long birth, string stage, Func<bool> work)
        {
            BoostRecoveryContext previous = boostRecoveryContext;
            string phase = previous != null && object.ReferenceEquals(previous.Owner, this) ? previous.Phase : AutomaticPhaseKey("");
            string key = phase + "boost-recovery:" + pid + ":" + birth + ":" + stage;
            BoostRecoveryAttempt attempt;
            lock (sync)
            {
                if (!boostRecoveries.TryGetValue(key, out attempt)) boostRecoveries[key] = attempt = new BoostRecoveryAttempt();
                if (attempt.Running || attempt.CheckingPhase) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false; }
            }
            if (attempt.Due == long.MaxValue || attempt.Failures >= 3) ObserveBoostRecoveryPhase(attempt, pid, birth);
            lock (sync)
            {
                if (attempt.Running || attempt.CheckingPhase) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false; }
                if (attempt.Failures >= 3 || attempt.Due == long.MaxValue || DateTime.UtcNow.Ticks < attempt.Due)
                { ReportBoostRecoveryWait(attempt, pid, birth); return false; }
                attempt.Running = true;
            }
            bool restored = false;
            var childTrace = new RecoveryAttemptTrace();
            boostRecoveryContext = new BoostRecoveryContext { Owner = this, Phase = phase };
            try { restored = work(); return restored; }
            catch { return false; }
            finally
            {
                childTrace.Dispose();
                boostRecoveryContext = previous;
                lock (sync)
                {
                    attempt.Running = false;
                    if (restored) boostRecoveries.Remove(key);
                    else if (childTrace.Reported)
                    {
                        // Reuse a child owner's finite schedule before another raw process/identity query.
                        attempt.Due = childTrace.Next;
                        attempt.ChildObservers = childTrace.PhaseObservers;
                        attempt.ExitProbeDue = DateTime.UtcNow.AddSeconds(30).Ticks;
                        ReportBoostRecoveryWait(attempt, pid, birth);
                    }
                    else
                    {
                        attempt.Failures++;
                        attempt.Due = attempt.Failures >= 3 ? long.MaxValue : DateTime.UtcNow.AddSeconds(30).Ticks;
                        attempt.ExitProbeDue = DateTime.UtcNow.AddSeconds(30).Ticks;
                        ReportBoostRecoveryWait(attempt, pid, birth);
                        Logger.Warn("Game recovery: target=" + pid + "/" + birth + " stage=" + stage
                            + " failure=" + attempt.Failures + "/3; originals and ownership retained");
                    }
                }
            }
        }

        private bool RestoreBoostProcess(int pid, Snap snapshot)
        {
            IntPtr handle = IntPtr.Zero;
            bool gone = false;
            try
            {
                bool readable = RunBoostRecovery(pid, snapshot.Creation, "identity", delegate
                {
                    handle = Native.OpenProcess(12800, false, pid);
                    if (handle == IntPtr.Zero)
                    {
                        IntPtr query = Native.OpenProcess(4096, false, pid);
                        if (query == IntPtr.Zero) { gone = Native.LastOpenProcessFailureWasNoSuchProcess(); return gone; }
                        try
                        {
                            string name = Native.ImageName(query);
                            long birth, cpu; ulong io;
                            bool known = Native.QueryProcessSample(query, out birth, out cpu, out io) && name != null;
                            gone = known && (birth != snapshot.Creation || !string.Equals(name, snapshot.Name, StringComparison.OrdinalIgnoreCase));
                            return gone;
                        }
                        finally { Native.CloseHandle(query); }
                    }
                    string actual = Native.ImageName(handle);
                    long creation, usedCpu; ulong usedIo;
                    bool identified = Native.QueryProcessSample(handle, out creation, out usedCpu, out usedIo) && actual != null;
                    if (!identified) { gone = !Native.StillActive(handle); return gone; }
                    gone = creation != snapshot.Creation || !string.Equals(actual, snapshot.Name, StringComparison.OrdinalIgnoreCase);
                    return true;
                });
                if (!readable) return false;
                if (gone) return true;
                bool pin = RestoreHardPin(pid);
                return RestoreBoostSnapshot(handle, pid, snapshot) && pin;
            }
            finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
        }
    }
}
