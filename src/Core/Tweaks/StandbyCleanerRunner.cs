// Session-scoped scheduling for standby-list cleaning. No overlapping or catch-up polls.
using System;
using System.Threading;

namespace PaviseApp
{
    internal sealed class StandbyCleanerRunner
    {
        private sealed class Request
        {
            internal readonly int Generation;
            internal readonly StandbyCleanerOptions Options;
            internal readonly Func<bool> MayContinue;
            internal int Failures;

            internal Request(int generation, StandbyCleanerOptions options, Func<bool> mayContinue)
            {
                Generation = generation;
                Options = options;
                MayContinue = mayContinue;
            }
        }

        private readonly object gate = new object();
        private readonly object operationGate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly StandbyCleanerEngine engine;
        private readonly Action<int, StandbyCleanerResult, int> onFault;
        private volatile Request current;
        private volatile bool closing;
        private bool closed;
        private bool hasFaultedGeneration;
        private int faultedGeneration;
        private int inFlight;
        private Thread worker;

        internal StandbyCleanerRunner(StandbyCleanerEngine engine,
            Action<int, StandbyCleanerResult, int> onFault)
        {
            if (engine == null) throw new ArgumentNullException("engine");
            this.engine = engine;
            this.onFault = onFault;
        }

        // Callers supply a new generation for every intent/configuration change.
        // Repeated game scans must not restart the countdown or revive a fault.
        internal bool Update(int generation, StandbyCleanerOptions options, Func<bool> mayContinue)
        {
            lock (gate)
            {
                if (closing) return false;
                if (options == null || !options.IsValid)
                {
                    current = null;
                    wake.Set();
                    return false;
                }
                if (hasFaultedGeneration && faultedGeneration == generation) return false;
                Request previous = current;
                if (previous != null && previous.Generation == generation
                    && SameOptions(previous.Options, options)) return true;
                hasFaultedGeneration = false;
                current = new Request(generation, options, mayContinue);
                if (worker == null)
                {
                    var next = new Thread(Loop);
                    next.IsBackground = true;
                    next.Name = "Pavise.StandbyCleaner";
                    worker = next;
                    try { next.Start(); }
                    catch
                    {
                        worker = null;
                        current = null;
                        return false;
                    }
                }
                wake.Set();
                return true;
            }
        }

        private static bool SameOptions(StandbyCleanerOptions left, StandbyCleanerOptions right)
        {
            return left.ListMegabytes == right.ListMegabytes
                && left.FreeMegabytes == right.FreeMegabytes
                && left.PollingMilliseconds == right.PollingMilliseconds;
        }

        internal void Pause()
        {
            lock (gate)
            {
                current = null;
                if (!closed) wake.Set();
            }
        }

        private bool Admitted(Request request)
        {
            return !closing && ReferenceEquals(current, request)
                && StandbyCleanerEngine.MayContinue(request.MayContinue);
        }

        private void Loop()
        {
            while (!closing)
            {
                Request request = current;
                if (request == null)
                {
                    wake.WaitOne();
                    continue;
                }
                // Wait a full interval after each poll/change. A slow native call
                // never queues missed ticks and never runs concurrently with another.
                if (wake.WaitOne(request.Options.PollingMilliseconds)) continue;
                if (!Admitted(request))
                {
                    lock (gate)
                        if (ReferenceEquals(current, request)) current = null;
                    continue;
                }

                StandbyCleanerResult result;
                int status;
                lock (operationGate)
                {
                    Interlocked.Exchange(ref inFlight, 1);
                    try
                    {
                        StandbyMemorySnapshot snapshot;
                        result = engine.Poll(request.Options,
                            delegate { return Admitted(request); }, out snapshot, out status);
                    }
                    catch
                    {
                        result = StandbyCleanerResult.QueryFailed;
                        status = StandbyCleanerEngine.StatusUnsuccessful;
                    }
                    finally { Interlocked.Exchange(ref inFlight, 0); }
                }

                bool fault = false;
                lock (gate)
                {
                    if (closing || !ReferenceEquals(current, request)) continue;
                    if (result == StandbyCleanerResult.Cancelled) current = null;
                    else if (result == StandbyCleanerResult.QueryFailed
                        || result == StandbyCleanerResult.PurgeFailed
                        || result == StandbyCleanerResult.InvalidOptions
                        || (result == StandbyCleanerResult.Purged && status != 0))
                    {
                        request.Failures++;
                        if (request.Failures >= 2 || result == StandbyCleanerResult.InvalidOptions
                            || result == StandbyCleanerResult.Purged)
                        {
                            current = null;
                            hasFaultedGeneration = true;
                            faultedGeneration = request.Generation;
                            fault = true;
                        }
                    }
                    else request.Failures = 0;
                }
                // Never hold either gate across a callback into GameMode/settings.
                if (fault && onFault != null)
                    try { onFault(request.Generation, result, status); } catch { }
            }
        }

        internal bool HasInFlight { get { return Volatile.Read(ref inFlight) != 0; } }

        // Pause revokes pending admission; this drains a call that already entered.
        // Windows does not offer cancellation for an in-progress standby purge.
        internal bool Drain(int timeoutMs)
        {
            if (timeoutMs < 0 || Monitor.IsEntered(operationGate)
                || !Monitor.TryEnter(operationGate, timeoutMs)) return false;
            Monitor.Exit(operationGate);
            return true;
        }

        internal bool Close(int timeoutMs)
        {
            Thread pending;
            lock (gate)
            {
                if (closed) return true;
                closing = true;
                current = null;
                wake.Set();
                pending = worker;
            }
            // Keep the handle and event on timeout/self-join; a later Close must
            // still observe/drain the same worker before reset can remove data.
            if (timeoutMs < 0 || pending == Thread.CurrentThread
                || (pending != null && !pending.Join(timeoutMs))) return false;
            lock (gate)
            {
                if (!closed)
                {
                    closed = true;
                    worker = null;
                    wake.Close();
                }
            }
            return true;
        }

#if PAVISE_SELFTEST
        internal bool HasWorkerForTest { get { lock (gate) return worker != null; } }
#endif
    }
}
