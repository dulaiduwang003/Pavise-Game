using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal sealed class FreezeTarget
    {
        internal int Pid;
        internal long Creation;
        internal string Path;
        internal bool Same(FreezeTarget other)
        { return other != null && Pid == other.Pid && Creation == other.Creation
            && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase); }
    }

    internal sealed class FreezeTrace
    {
        private readonly List<string> lines = new List<string>();
        private int dropped;
        internal const int Capacity = 8192;
        internal static string Identity(FreezeTarget target)
        {
            string name;
            try { name = Path.GetFileName(target.Path ?? ""); } catch { name = "?"; }
            name = name.Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');
            if (name.Length > 160) name = name.Substring(0, 160);
            return "pid=" + target.Pid + " born=" + target.Creation + " exe=" + name;
        }
        internal void Add(string message)
        { if (lines.Count < Capacity) lines.Add(message); else if (dropped < int.MaxValue) dropped++; }
        internal string Drain()
        {
            if (dropped > 0) lines.Add("trace-truncated dropped=" + dropped);
            string result = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", lines.ToArray())));
            lines.Clear(); dropped = 0; return "TRACE " + result;
        }
    }

    internal static class BackgroundFreezePolicy
    {
        internal static bool Eligible(PerformancePreset mode, int pid, long creation, string path,
            int session, int ownerSession, bool protectedFamily, bool service, string windows, bool approved = false)
        {
            return approved && mode == PerformancePreset.Extreme && pid > 4 && creation > 0
                && ownerSession > 0 && session == ownerSession
                && !protectedFamily && !service && !string.IsNullOrEmpty(path)
                && !string.IsNullOrEmpty(windows) && !FamilyBoundary.UnderRoot(path, windows);
        }
    }

    internal sealed class FreezeLeaseSet : IDisposable
    {
        private sealed class Entry { internal FreezeTarget Target; internal IDisposable Lease; }
        private readonly Dictionary<int, Entry> active = new Dictionary<int, Entry>();
        private readonly Dictionary<int, FreezeTarget> refused = new Dictionary<int, FreezeTarget>();
        internal Action<string> Trace;
        private void Note(string message) { try { if (Trace != null) Trace(message); } catch { } }
        internal int Count { get { return active.Count; } }
        internal int[] Pids { get { var result = new int[active.Count]; active.Keys.CopyTo(result, 0); return result; } }
        internal void Release(int pid, string reason = "explicit-release")
        {
            Entry entry;
            if (!active.TryGetValue(pid, out entry)) return;
            entry.Lease.Dispose(); active.Remove(pid);
            Note("released " + FreezeTrace.Identity(entry.Target) + " reason=" + reason);
        }
        internal void Release(int pid, long creation)
        {
            Entry entry;
            if (active.TryGetValue(pid, out entry) && entry.Target.Creation == creation) Release(pid);
        }
        internal void Sync(Dictionary<int, FreezeTarget> wanted, Func<bool> allowed, Func<FreezeTarget, IDisposable> acquire)
        {
            try
            {
                if (!allowed()) { Dispose(); return; }
                foreach (int pid in Pids)
                {
                    FreezeTarget next;
                    if (!wanted.TryGetValue(pid, out next) || !active[pid].Target.Same(next)) Release(pid, "ineligible-or-identity-changed");
                }
                foreach (int pid in new List<int>(refused.Keys))
                {
                    FreezeTarget next;
                    if (!wanted.TryGetValue(pid, out next) || !refused[pid].Same(next)) refused.Remove(pid);
                }
                foreach (var pair in wanted)
                {
                    if (!allowed()) { Dispose(); return; }
                    if (active.ContainsKey(pair.Key) || refused.ContainsKey(pair.Key)) continue;
                    IDisposable lease = acquire(pair.Value);
                    if (lease == null) refused[pair.Key] = pair.Value;
                    else active.Add(pair.Key, new Entry { Target = pair.Value, Lease = lease });
                    if (!allowed()) { Dispose(); return; }
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (int pid in Pids) Release(pid, "session-gate-or-cleanup");
            refused.Clear();
        }
    }
}
