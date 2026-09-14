// File purpose One chance per game run, not per foreground event; this state machine
// has no timer and no worker thread; once a terminal verdict is made it stops looking at input
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal sealed class EnglishInputOnce
    {
        internal const int ForegroundWaitMs = 15000;
        private const int MaximumHistory = 128;
        private readonly object stateGate = new object(), operationGate = new object();
        private readonly IGameInputLanguage input;
        private readonly Func<long> clock;
        private readonly List<Session> history = new List<Session>();
        private Session current;
        private int cancellationVersion;
        private bool historyExhausted;

        private sealed class Session
        {
            internal string ProfileId;
            internal long Deadline;
            internal bool Consumed, Revoked, HistoryFull;
            internal Func<bool> OriginalAdmission;
            internal readonly List<GameInputProcess> Processes = new List<GameInputProcess>();
        }

        internal EnglishInputOnce(IGameInputLanguage value) : this(value, MonotonicMilliseconds) { }
        internal EnglishInputOnce(IGameInputLanguage value, Func<long> milliseconds)
        {
            if (value == null || milliseconds == null) throw new ArgumentNullException();
            input = value; clock = milliseconds;
        }

        private static long MonotonicMilliseconds()
        { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }
        private static bool SameProfile(string first, string second)
        { return !string.IsNullOrEmpty(first) && string.Equals(first, second, StringComparison.OrdinalIgnoreCase); }
        private static void Remember(Session session, GameInputProcess process)
        { if (process.IsValid && !session.Processes.Contains(process)) session.Processes.Add(process); }
        private static bool Evaluate(Func<bool> admission)
        {
            try { return admission != null && admission(); }
            catch { return false; }
        }

        internal void Begin(string profileId, GameInputProcess process, bool enabledAtEntry, Func<bool> admission)
        {
            long deadline = clock() + ForegroundWaitMs;
            // Begin and Step run outside GameMode.sync; Cancel and End only take
            // the short state lock; no native calls or callbacks inside the lock
            lock (operationGate)
            {
                Session[] previous;
                int beginVersion;
                lock (stateGate)
                {
                    beginVersion = cancellationVersion;
                    if (current != null && SameProfile(current.ProfileId, profileId))
                    { Remember(current, process); return; }
                    if (current != null) { current.Consumed = true; current.Revoked = true; }
                    current = null;
                    previous = history.ToArray();
                }
                Session reuse = null;
                var gone = new List<Session>();
                foreach (Session old in previous)
                {
                    // Once a reusable session is found, probing the remaining sessions only serves pruning; defer it to the next
                    //   Begin and clear then; still reselect by exact identity, that is a pure compare with no kernel call
                    if (reuse != null)
                    {
                        foreach (GameInputProcess identity in old.Processes)
                            if (identity.Equals(process)) { reuse = old; break; }
                        continue;
                    }
                    bool liveOrUnknown = false;
                    foreach (GameInputProcess identity in old.Processes)
                    {
                        if (identity.Equals(process)) { reuse = old; liveOrUnknown = true; break; }
                        // Liveness needs just one non-Gone result; after that only match identity, no more probing
                        if (liveOrUnknown) continue;
                        GameInputProcessState state;
                        try { state = input.Probe(identity); }
                        catch { state = GameInputProcessState.Unknown; }
                        if (state != GameInputProcessState.Gone) liveOrUnknown = true;
                    }
                    // Old render process unreachable; can't prove this is a brand-new run
                    // Never alias the launcher's or family processes' PID; they may outlive the game
                    if (!liveOrUnknown) gone.Add(old);
                    else if (reuse == null && SameProfile(old.ProfileId, profileId)) reuse = old;
                }
                bool permitted = enabledAtEntry && Evaluate(admission);
                lock (stateGate)
                {
                    permitted = permitted && beginVersion == cancellationVersion;
                    foreach (Session old in gone) history.Remove(old);
                    if (reuse != null)
                    {
                        reuse.ProfileId = profileId;
                        reuse.Consumed = true; reuse.Revoked = true;
                        Remember(reuse, process); current = reuse;
                        return;
                    }
                    // Once an identity can't be held, don't later forget this skip and re-arm
                    // just because another history entry expired
                    if (history.Count >= MaximumHistory) historyExhausted = true;
                    Session fresh = new Session {
                        ProfileId = profileId, Deadline = deadline, Consumed = !permitted,
                        Revoked = !permitted, OriginalAdmission = admission,
                        HistoryFull = historyExhausted
                    };
                    Remember(fresh, process);
                    if (!fresh.HistoryFull) history.Add(fresh);
                    current = fresh;
                }
            }
        }

        private bool Allowed(Session session, Func<bool> admission)
        {
            bool stateAllows;
            lock (stateGate) stateAllows = ReferenceEquals(current, session) && !session.Revoked;
            // Never call back while holding stateGate; Invalidate may hold sync
            return stateAllows && clock() <= session.Deadline
                && Evaluate(session.OriginalAdmission) && Evaluate(admission);
        }

        internal EnglishInputOutcome Step(string profileId, GameInputProcess process, Func<bool> admission)
        {
            if (!Monitor.TryEnter(operationGate)) return null; // No queued catch-up
            try
            {
                Session session;
                lock (stateGate)
                {
                    session = current;
                    if (session == null || !SameProfile(session.ProfileId, profileId)) return null;
                    Remember(session, process); // Record renderer handoffs even after the one attempt
                    if (session.Consumed) return null;
                }
                EnglishInputOutcome result;
                if (session.HistoryFull) result = new EnglishInputOutcome(EnglishInputResult.HistoryFull);
                else if (clock() > session.Deadline) result = new EnglishInputOutcome(EnglishInputResult.Expired);
                else if (!process.IsValid) return null; // Await a verified renderer with the original deadline
                else if (!Allowed(session, admission)) result = new EnglishInputOutcome(EnglishInputResult.Canceled);
                else
                {
                    Func<bool> allowed = delegate { return Allowed(session, admission); };
                    Func<bool> claim = delegate
                    {
                        if (!allowed()) return false;
                        lock (stateGate)
                        {
                            if (!ReferenceEquals(current, session) || session.Revoked || session.Consumed) return false;
                            session.Consumed = true;
                            return true;
                        }
                    };
                    try { result = input.TryOnce(process, claim, allowed); }
                    catch { result = new EnglishInputOutcome(EnglishInputResult.Unavailable); }
                    if (result == null) result = new EnglishInputOutcome(EnglishInputResult.Unavailable);
                }
                if (result.Result == EnglishInputResult.WaitingForGameWindow)
                {
                    if (Allowed(session, admission)) return null;
                    result = new EnglishInputOutcome(clock() > session.Deadline
                        ? EnglishInputResult.Expired : EnglishInputResult.Canceled);
                }
                lock (stateGate) session.Consumed = true;
                return result;
            }
            finally { Monitor.Exit(operationGate); }
        }

        internal void Cancel()
        {
            lock (stateGate)
            {
                cancellationVersion++;
                if (current != null) { current.Revoked = true; current.Consumed = true; }
            }
        }

        internal void End()
        {
            lock (stateGate)
            {
                cancellationVersion++;
                if (current != null) { current.Revoked = true; current.Consumed = true; }
                current = null; // Retain deduplication while an earlier runtime may still be alive
            }
        }

        internal bool Drain(int timeoutMs)
        {
            if (Monitor.IsEntered(operationGate)) return false;
            if (!Monitor.TryEnter(operationGate, Math.Max(0, timeoutMs))) return false;
            Monitor.Exit(operationGate);
            return true;
        }
    }
}
