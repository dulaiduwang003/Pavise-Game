using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class ServiceWhitelistHost
    {
        internal readonly string Name, Reason;
        internal readonly bool Known;
        internal readonly int ProcessId;
        internal readonly long CreationTime;
        internal bool HasHost { get { return Known && ProcessId > 4 && CreationTime > 0; } }
        internal ServiceWhitelistHost(string name, bool known, int pid, long birth, string reason)
        { Name = name; Known = known; ProcessId = pid; CreationTime = birth; Reason = reason ?? ""; }
    }

    internal sealed class ServiceWhitelistObservation
    {
        internal readonly long Session, Cycle, Version;
        internal readonly int Revision;
        internal readonly bool HasRules, Known;
        internal readonly string[] Failures;
        private readonly Dictionary<string, ServiceWhitelistHost> rules;
        private readonly Dictionary<int, long> hosts = new Dictionary<int, long>();
        internal ServiceWhitelistObservation(long session, long cycle, long version, int revision,
            Dictionary<string, ServiceWhitelistHost> bindings, string[] failures)
        {
            Session = session; Cycle = cycle; Version = version; Revision = revision; rules = bindings;
            HasRules = bindings.Count != 0; Known = true; Failures = failures;
            foreach (ServiceWhitelistHost binding in rules.Values)
            {
                if (!binding.Known) Known = false;
                if (!binding.HasHost) continue;
                long previous;
                if (hosts.TryGetValue(binding.ProcessId, out previous) && previous != binding.CreationTime) Known = false;
                else hosts[binding.ProcessId] = binding.CreationTime;
            }
        }
        internal ServiceWhitelistHost Find(string name) { ServiceWhitelistHost host; return rules.TryGetValue(name, out host) ? host : null; }
        internal bool TryHost(int pid, out long birth) { return hosts.TryGetValue(pid, out birth); }
        internal bool Protects(int pid, long birth) { long expected; return birth > 0 && hosts.TryGetValue(pid, out expected) && expected == birth; }
        internal IEnumerable<KeyValuePair<int, long>> Hosts { get { return hosts; } }
        internal bool SameBindings(Dictionary<string, ServiceWhitelistHost> other)
        {
            if (rules.Count != other.Count) return false;
            foreach (KeyValuePair<string, ServiceWhitelistHost> pair in rules)
            {
                ServiceWhitelistHost current;
                if (!other.TryGetValue(pair.Key, out current) || current.Known != pair.Value.Known
                    || current.ProcessId != pair.Value.ProcessId || current.CreationTime != pair.Value.CreationTime) return false;
            }
            return true;
        }
    }

    // 每轮重新核对服务和宿主身份；只保留失败预算及语义版本，不跨轮复用原生观测结果。
    internal sealed class ServiceWhitelistObserver
    {
        private sealed class Budget { internal int Failures; internal long Next; }
        private readonly object gate = new object();
        private readonly Dictionary<string, Budget> budgets = new Dictionary<string, Budget>(StringComparer.OrdinalIgnoreCase);
        private long session = -1, version;
        private ServiceWhitelistObservation previous;

        internal ServiceWhitelistObservation Observe(long serial, long cycle, int revision, IList<string> requested,
            Func<IList<string>, Func<bool>, Dictionary<string, ServiceHostBinding>> read, Func<bool> canceled, Func<long> clock)
        {
            lock (gate)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in requested) names.Add(name);
                if (serial > session) { session = serial; budgets.Clear(); previous = null; }
                var observed = new Dictionary<string, ServiceWhitelistHost>(StringComparer.OrdinalIgnoreCase);
                var due = new List<string>();
                var failures = new List<string>();
                long now = clock();
                foreach (string name in names)
                {
                    Budget budget;
                    if (!budgets.TryGetValue(name, out budget)) budgets[name] = budget = new Budget();
                    if (serial < session || canceled() || budget.Failures >= 3 || now < budget.Next)
                    {
                        observed[name] = new ServiceWhitelistHost(name, false, 0, 0,
                            budget.Failures >= 3 ? "服务读取预算已耗尽" : "服务身份等待重新核验");
                        continue;
                    }
                    due.Add(name);
                }
                if (due.Count != 0 && !canceled())
                {
                    Dictionary<string, ServiceHostBinding> result = null;
                    string failure = null;
                    try { result = read(due, canceled); }
                    catch (Exception error) { if (error is OutOfMemoryException) throw; failure = error.GetType().Name; }
                    var hostBirths = new Dictionary<int, long>();
                    var conflictingHosts = new HashSet<int>();
                    foreach (string name in due)
                    {
                        ServiceHostBinding binding;
                        if (result == null || !result.TryGetValue(name, out binding) || binding == null || !binding.Known
                            || !string.Equals(binding.Name, name, StringComparison.OrdinalIgnoreCase)
                            || binding.ProcessId <= 4 || binding.CreationTime <= 0) continue;
                        long birth;
                        if (hostBirths.TryGetValue(binding.ProcessId, out birth) && birth != binding.CreationTime)
                            conflictingHosts.Add(binding.ProcessId);
                        else hostBirths[binding.ProcessId] = binding.CreationTime;
                    }
                    foreach (string name in due)
                    {
                        ServiceHostBinding binding = null;
                        if (result != null) result.TryGetValue(name, out binding);
                        bool conflicting = binding != null && conflictingHosts.Contains(binding.ProcessId);
                        bool valid = !canceled() && binding != null && binding.Known
                            && !conflicting
                            && string.Equals(binding.Name, name, StringComparison.OrdinalIgnoreCase)
                            && (binding.ProcessId == 0 && binding.CreationTime == 0 || binding.ProcessId > 4 && binding.CreationTime > 0);
                        if (valid)
                        {
                            observed[name] = new ServiceWhitelistHost(name, true, binding.ProcessId, binding.CreationTime, binding.Reason);
                            continue;
                        }
                        string reason = conflicting ? "同轮服务宿主创建时间冲突" : binding == null ? (failure ?? "服务查询未返回完整结果") : binding.Reason;
                        observed[name] = new ServiceWhitelistHost(name, false, 0, 0, reason);
                        if (canceled()) continue;
                        Budget budget = budgets[name];
                        budget.Failures = Math.Min(3, budget.Failures + 1);
                        budget.Next = clock() + TimeSpan.FromSeconds(Math.Min(30, 1 << (budget.Failures - 1))).Ticks;
                        failures.Add(name + " 尝试=" + budget.Failures + "/3 原因=" + reason
                            + (budget.Failures >= 3 ? "；本局停止该服务主动读取，保留后台恢复责任" : ""));
                    }
                }
                foreach (string name in due)
                    if (!observed.ContainsKey(name)) observed[name] = new ServiceWhitelistHost(name, false, 0, 0, "服务查询资格已撤销");
                if (previous == null || previous.Session != serial || !previous.SameBindings(observed)) version++;
                var current = new ServiceWhitelistObservation(serial, cycle, version, revision, observed, failures.ToArray());
                previous = current;
                return current;
            }
        }
    }
}
