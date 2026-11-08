using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // All callers of a recovery operation share this phase; polling never creates a phase.
    internal static class FrameRecoveryBudget
    {
        private sealed class Budget { internal int Failures; internal long Due; internal bool Running; }
        private sealed class Owner
        {
            internal long Serial, Birth, Episode;
            internal int Target;
            internal string Configuration;
            internal string Receipt;
            internal bool Exited;
            internal string Phase { get { return Episode + "|" + Serial + "|" + Target + "|" + Birth + "|" + Configuration + (Exited ? "|exited" : "|live"); } }
        }
        private static readonly object gate = new object();
        private static readonly Dictionary<string, Budget> budgets = new Dictionary<string, Budget>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Owner> owners = new Dictionary<string, Owner>(StringComparer.Ordinal);
        private static long serial;
        private static long nextEpisode;
        private static int target;
        private static long birth;
        private static string configuration = "default";
        private static Func<int, long, int> exitProbe;
        internal static bool Sync(long gameSerial, int pid, long created, string materialConfig)
        {
            lock (gate)
            {
                if (gameSerial < serial) return false;
                if (gameSerial > serial)
                    foreach (Owner owner in owners.Values) owner.Serial = gameSerial;
                serial = gameSerial; target = pid; birth = created; configuration = materialConfig ?? "default";
                return true;
            }
        }
        internal static string Cycle(int pid, long created)
        { lock (gate) return serial + "\n" + pid + "|" + created + "|" + configuration; }
        internal static void Own(string scope, int pid = 0, long created = 0, string receipt = null)
        {
            lock (gate)
            {
                Owner previous;
                if (!owners.TryGetValue(scope, out previous))
                    owners[scope] = new Owner { Episode = ++nextEpisode, Serial = serial, Target = pid > 0 ? pid : target,
                        Birth = created > 0 ? created : birth, Configuration = configuration, Receipt = receipt };
            }
        }
        // Only the exact verified-neutral receipt may end its recovery episode.
        internal static void Settled(string scope, string receipt)
        {
            lock (gate)
            {
                Owner owner;
                if (owners.TryGetValue(scope, out owner) && owner.Receipt == receipt)
                { owners.Remove(scope); ForgetEpisode(owner); }
            }
        }
        private static void ForgetEpisode(Owner owner)
        {
            string prefix = owner.Episode + "|";
            var completed = new List<string>();
            foreach (string key in budgets.Keys) if (key.StartsWith(prefix, StringComparison.Ordinal)) completed.Add(key);
            foreach (string key in completed) budgets.Remove(key);
        }
        internal static void ObserveExit(Func<int, long, int> processState)
        {
            List<Owner> pending;
            lock (gate) { exitProbe = processState; pending = new List<Owner>(owners.Values); }
            foreach (Owner owner in pending)
            {
                if (owner.Exited || owner.Target <= 0 || owner.Birth <= 0 || processState(owner.Target, owner.Birth) <= 0) continue;
                lock (gate) owner.Exited = true;
            }
        }
        private static long Clock() { return AutoCores.NowTicks; }
        private static void ReportSchedule(string scope, Owner owner, Budget budget)
        {
            if (budget.Running) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return; }
            if (budget.Failures < 3)
            { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(Math.Max(0, budget.Due - Clock()))); return; }
            string phase = owner.Phase;
            RecoveryAttemptTrace.Exhausted(delegate
            {
                Func<int, long, int> probe;
                lock (gate)
                {
                    Owner current;
                    if (!owners.TryGetValue(scope, out current) || !object.ReferenceEquals(current, owner) || owner.Phase != phase) return true;
                    probe = exitProbe;
                }
                if (!owner.Exited && owner.Target > 0 && owner.Birth > 0 && probe != null && probe(owner.Target, owner.Birth) > 0)
                    lock (gate) { owner.Exited = true; return true; }
                return false;
            });
        }
        internal static bool Run(string scope, Func<bool> work, Func<bool> neutral = null)
        {
            Budget budget;
            Owner admittedOwner;
            lock (gate)
            {
                Own(scope);
                admittedOwner = owners[scope];
                string key = admittedOwner.Phase + "|" + scope;
                if (!budgets.TryGetValue(key, out budget)) budgets[key] = budget = new Budget();
                if (budget.Running || budget.Failures >= 3 || Clock() < budget.Due)
                { ReportSchedule(scope, admittedOwner, budget); return false; }
                budget.Running = true;
            }
            bool completed = false, settled = false;
            try { completed = work(); settled = completed && (neutral == null || neutral()); return completed; }
            finally
            {
                lock (gate)
                {
                    budget.Running = false;
                    if (settled)
                    {
                        budget.Failures = 0; budget.Due = 0;
                        ForgetEpisode(admittedOwner);
                        Owner current;
                        if (owners.TryGetValue(scope, out current) && object.ReferenceEquals(current, admittedOwner)) owners.Remove(scope);
                    }
                    else if (!completed)
                    {
                        budget.Failures++;
                        budget.Due = Clock() + TimeSpan.FromSeconds(30).Ticks;
                        ReportSchedule(scope, admittedOwner, budget);
                        if (budget.Failures == 3) Logger.Log("FRAME recovery paused after 3 failures: " + scope + "; recovery ownership retained");
                    }
                }
            }
        }
    }
}
