using System;
using System.Collections.Generic;
namespace PaviseApp
{
    // Async owners report waiting at admission; ThreadStatic recovery state does not flow to workers.
    internal sealed class RecoveryAttemptTrace : IDisposable
    {
        [ThreadStatic] private static RecoveryAttemptTrace current;
        private readonly RecoveryAttemptTrace previous;
        internal bool Reported;
        internal long Next = long.MaxValue;
        internal readonly List<Func<bool>> PhaseObservers = new List<Func<bool>>();
        internal RecoveryAttemptTrace() { previous = current; current = this; }
        internal static void DeferredUntil(long utcTicks)
        { if (current != null) { current.Reported = true; current.Next = Math.Min(current.Next, utcTicks); } }
        internal static void DeferredFor(TimeSpan delay) { DeferredUntil(DateTime.UtcNow.Ticks + Math.Max(1, delay.Ticks)); }
        internal static void Exhausted(Func<bool> phaseChanged = null)
        {
            if (current == null) return;
            current.Reported = true;
            if (phaseChanged != null) current.PhaseObservers.Add(phaseChanged);
        }
        internal static void FailureSchedule(int failures, int limit, long utcDue)
        { if (failures >= limit) Exhausted(); else DeferredUntil(utcDue); }
        internal void Propagate()
        {
            if (previous == null || !Reported) return;
            previous.Reported = true; previous.Next = Math.Min(previous.Next, Next);
            previous.PhaseObservers.AddRange(PhaseObservers);
        }
        public void Dispose() { current = previous; }
    }

    internal sealed class RecoveryRouteSet
    {
        private sealed class Route
        {
            internal Func<bool> Work;
            internal string[] Dependencies;
            internal bool Neutral, Running;
            internal bool? Intent;
            internal long IntentGeneration;
            internal int Failures;
            internal long Due;
            internal long ProbeDue;
            internal List<Func<bool>> PhaseObservers;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Route> routes = new Dictionary<string, Route>(StringComparer.Ordinal);
        private readonly List<string> order = new List<string>();
        private readonly Func<long> clock;
        internal readonly long Session;
        internal RecoveryRouteSet(long session, Func<long> utcClock) { Session = session; clock = utcClock; }
        internal bool Complete { get { lock (gate) { foreach (Route r in routes.Values) if (!r.Neutral) return false; return true; } } }
        // Exhausted routes (Due == MaxValue) wake only on a genuine phase change.
        internal bool RetryScheduled
        {
            get
            {
                lock (gate)
                {
                    foreach (Route route in routes.Values)
                        if (!route.Neutral && (route.Running || route.Due != long.MaxValue && DependenciesReady(route))) return true;
                    return false;
                }
            }
        }
        internal long Next
        {
            get
            {
                lock (gate)
                {
                    long next = long.MaxValue;
                    foreach (Route route in routes.Values)
                        if (!route.Neutral && !route.Running && DependenciesReady(route))
                            next = Math.Min(next, Math.Min(route.Due, route.PhaseObservers == null || route.PhaseObservers.Count == 0 ? long.MaxValue : route.ProbeDue));
                    return next;
                }
            }
        }
        private bool DependenciesReady(Route route)
        {
            if (route.Dependencies == null) return true;
            foreach (string name in route.Dependencies)
            {
                if (name == "*")
                { foreach (Route other in routes.Values) if (!object.ReferenceEquals(other, route) && !other.Neutral) return false; }
                else { Route dependency; if (!routes.TryGetValue(name, out dependency) || !dependency.Neutral) return false; }
            }
            return true;
        }
        internal bool Capture(string key, Func<bool> work, string[] dependencies = null, bool? intent = null)
        {
            lock (gate)
            {
                Route route;
                if (!routes.TryGetValue(key, out route))
                { routes[key] = route = new Route { Intent = intent }; order.Add(key); }
                if (!route.Neutral && !route.Running) { route.Work = work; route.Dependencies = dependencies; }
            }
            return Run(key);
        }
        internal bool InvalidateNeutral(string key)
        {
            lock (gate)
            {
                Route route;
                if (!routes.TryGetValue(key, out route) || !route.Neutral) return false;
                route.Neutral = false; route.Due = 0; return true;
            }
        }
        internal bool UpdateIntent(string key, bool intent)
        {
            lock (gate)
            {
                Route route;
                if (!routes.TryGetValue(key, out route) || !route.Intent.HasValue || route.Intent.Value == intent) return false;
                route.Intent = intent;
                route.IntentGeneration++;
                // A policy that previously allowed retained ownership may now
                // require restoration. Never refill a pending/exhausted owner.
                InvalidateNeutral(key);
                return true;
            }
        }
        private bool Run(string key)
        {
            Route route;
            long intentGeneration;
            List<Func<bool>> observers = null;
            lock (gate)
            {
                route = routes[key];
                if (route.Neutral) return true;
                if (!route.Running && route.Due == long.MaxValue && route.PhaseObservers != null && clock() >= route.ProbeDue)
                { observers = new List<Func<bool>>(route.PhaseObservers); route.ProbeDue = clock() + TimeSpan.FromSeconds(30).Ticks; }
            }
            if (observers != null)
            {
                foreach (Func<bool> observe in observers)
                {
                    bool changed = false; try { changed = observe(); } catch { }
                    if (changed) { lock (gate) route.Due = 0; break; }
                }
            }
            lock (gate)
            {
                if (route.Neutral) return true;
                if (route.Running || route.Due == long.MaxValue || clock() < route.Due || !DependenciesReady(route)) return false;
                route.Running = true;
                intentGeneration = route.IntentGeneration;
            }
            bool neutral = false, accepted = false;
            using (var attempt = new RecoveryAttemptTrace())
            {
                try { neutral = route.Work(); }
                catch (Exception error) { Logger.Warn("Session recovery route " + key + " failed: " + error.GetType().Name); }
                finally
                {
                    lock (gate)
                    {
                        route.Running = false;
                        if (neutral)
                        {
                            accepted = intentGeneration == route.IntentGeneration;
                            route.Neutral = accepted;
                            if (accepted) route.Failures = 0;
                            // A result obtained for an older intent cannot settle
                            // the current one. Its real failure history is retained.
                            route.Due = accepted || route.Failures >= 3 ? long.MaxValue : 0;
                        }
                        else if (attempt.Reported)
                        {
                            route.Due = attempt.Next;
                            route.PhaseObservers = attempt.PhaseObservers;
                            route.ProbeDue = clock() + TimeSpan.FromSeconds(30).Ticks;
                        }
                        else
                        {
                            route.Failures++;
                            route.Due = route.Failures >= 3 ? long.MaxValue : clock() + TimeSpan.FromSeconds(30).Ticks;
                            Logger.Warn("Session recovery route " + key + " actual failure=" + route.Failures + "/3; ownership retained");
                        }
                    }
                }
            }
            return accepted;
        }
        internal bool RetryDue()
        {
            string[] snapshot;
            lock (gate) snapshot = order.ToArray();
            foreach (string name in snapshot) Run(name);
            return Complete;
        }
    }
}
