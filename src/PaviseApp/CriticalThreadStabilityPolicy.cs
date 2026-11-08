using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal sealed class CriticalThreadStabilityPolicy
    {
        private const int MaximumReassertions = 3;
        private const int MaximumRediscoveries = 3;
        private sealed class Scope
        {
            internal int Reassertions, Rediscoveries;
            internal bool Blocked;
            internal string Reason;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Scope> scopes = new Dictionary<string, Scope>(StringComparer.Ordinal);
        private Scope current;
        private long highestSerial;

        private Scope Select(string profile, int pid, long birth, long serial, string rawRule)
        {
            if (string.IsNullOrWhiteSpace(profile) || pid <= 0 || birth <= 0 || serial <= 0
                || serial < highestSerial || string.IsNullOrWhiteSpace(rawRule)) return null;
            if (serial > highestSerial) { highestSerial = serial; scopes.Clear(); }
            CriticalThreadRule runtimeRule = CriticalThreadRule.ParseForRuntime(rawRule);
            string identity = runtimeRule == null ? rawRule : runtimeRule.Encode();
            string key = profile + "\n" + pid.ToString(CultureInfo.InvariantCulture) + ":" + birth.ToString(CultureInfo.InvariantCulture) + "\n" + identity;
            Scope scope;
            if (!scopes.TryGetValue(key, out scope)) scopes[key] = scope = new Scope();
            current = scope;
            return scope;
        }
        internal bool IsBlocked(string profile, int pid, long birth, long serial, string rawRule)
        {
            lock (gate) { Scope scope = Select(profile, pid, birth, serial, rawRule); return scope == null || scope.Blocked; }
        }
        internal bool RecordReassertion(string profile, int pid, long birth, long serial, string rawRule)
        {
            lock (gate)
            {
                Scope scope = Select(profile, pid, birth, serial, rawRule);
                if (scope == null || scope.Blocked) return true;
                scope.Reassertions++;
                scope.Blocked = scope.Reassertions >= MaximumReassertions;
                if (scope.Blocked) scope.Reason = "reassertion-limit";
                return scope.Blocked;
            }
        }
        internal bool RecordRediscovery(string profile, int pid, long birth, long serial, string rawRule)
        {
            lock (gate)
            {
                Scope scope = Select(profile, pid, birth, serial, rawRule);
                if (scope == null || scope.Blocked) return true;
                scope.Rediscoveries++;
                scope.Blocked = scope.Rediscoveries >= MaximumRediscoveries;
                if (scope.Blocked) scope.Reason = "candidate-lifecycle";
                return scope.Blocked;
            }
        }
        internal void Block(string profile, int pid, long birth, long serial, string rawRule)
        {
            lock (gate)
            {
                Scope scope = Select(profile, pid, birth, serial, rawRule);
                if (scope == null) return;
                scope.Blocked = true; scope.Reason = "session-blocked";
            }
        }
        internal string BlockReason { get { lock (gate) return current != null && current.Blocked ? current.Reason : ""; } }
    }
}
