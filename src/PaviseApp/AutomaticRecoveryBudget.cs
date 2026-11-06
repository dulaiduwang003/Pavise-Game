using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    // A failed restoration keeps its owner after the active work budget is spent.
    // Only a genuine session/configuration phase can select another budget.
    internal sealed class AutomaticRecoveryBudget
    {
        internal const int MaximumFailures = 3;
        private readonly Dictionary<string, int> failures = new Dictionary<string, int>(StringComparer.Ordinal);
        private string cycle = "";
        private long session = -1;
        private readonly int maximumFailures;
        internal AutomaticRecoveryBudget(int maximumFailures = MaximumFailures)
        {
            if (maximumFailures < 1) throw new ArgumentOutOfRangeException("maximumFailures");
            this.maximumFailures = maximumFailures;
        }
        internal int FailureCount { get { int count; return failures.TryGetValue(cycle, out count) ? count : 0; } }
        internal bool CanAttempt
        {
            get { return FailureCount < maximumFailures; }
        }
        internal bool BeginCycle(string identity)
        {
            if (string.IsNullOrEmpty(identity) || identity == cycle) return false;
            long incoming;
            if (!long.TryParse(SessionPart(identity), NumberStyles.None, CultureInfo.InvariantCulture, out incoming) || incoming < session) return false;
            if (incoming > session) failures.Clear();
            session = incoming;
            cycle = identity;
            return true;
        }
        internal void Record(bool restored)
        {
            if (restored) return;
            int count; failures.TryGetValue(cycle, out count);
            failures[cycle] = Math.Min(maximumFailures, count + 1);
        }
        // Called only after an existing owned receipt has been verified neutral.
        internal void CompleteRecovery() { failures.Remove(cycle); }
        private static string SessionPart(string identity)
        { int split = identity.IndexOf('\n'); return split < 0 ? "" : identity.Substring(0, split); }
    }
}
