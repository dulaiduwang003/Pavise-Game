// @author bdth 2074055628@qq.com
// File purpose Anti-cheat affinity limits and old background core-pinning restore
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        // Target mask 0 means release; placement validation lives in SuppressionAffinityPolicy.SqueezeTarget
        //   Only ledgered entries whose identity fully matches; protected and unledgered entries are untouched
        //   reason says who is placing; the background path takes only pure background entries, the anti-cheat path only entries with the anti-cheat reason
        //   Returns true when the entry is now in the desired state; changed means this call actually altered affinity
        public bool SetSqueeze(int pid, long creation, string name, ulong squeezeMask,
            SuppressReason reason, out bool changed)
        {
            changed = false;
            // The old heat-based heavy-load pinning is retired; background placement serves only exclusive-core hard affinity and must be given as a whole block
            //   Caller is responsible for checking the switch and whether exclusive is really in effect; this only rejects unknown reasons
            if (reason != SuppressReason.AntiCheat && reason != SuppressReason.Background
                && squeezeMask != 0) return false;
            Entry e;
            ulong target;
            lock (sync)
            {
                if (!map.TryGetValue(pid, out e) || !SqueezeOwnedBy(e, reason)
                    || e.OrigPri == uint.MaxValue || !e.Journaled || e.Creation <= 0
                    || e.Creation != creation || !SameName(e.Name, name)) return false;
                target = squeezeMask == 0 ? 0 : SuppressionAffinityPolicy.SqueezeTarget(
                    squeezeMask, e.OrigAff, e.OrigCpuSets, allMask, CpuTopology.MultiGroup);
                if (e.SqueezeAff == target) return true;
                if (target != 0 && (e.SqueezeRefused || e.GaveUp)) return false;
            }
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                if (!SameProcess(h, e)) return false;
                lock (sync)
                {
                    Entry cur;
                    if (!map.TryGetValue(pid, out cur) || cur != e || !SqueezeOwnedBy(cur, reason)
                        || cur.OrigPri == uint.MaxValue || cur.Creation != creation) return false;
                    ulong previous = cur.SqueezeAff;
                    cur.SqueezeAff = target;
                    bool applied = ApplyThrottle(h, cur.Level, cur.OrigPri, cur.OrigAff, cur.OrigCpuSets,
                        DesiredGpu(cur), cur.OrigBoost, AntiCheatThrottled(cur), DesiredAffinityOf(cur));
                    if (!applied && target != 0)
                    {
                        // If the pin fails, don't ledger it; write affinity back to original, Applied and patrol cadence untouched
                        //   Otherwise one refused affinity write marks the whole suppression failed and every retry round spams the log
                        cur.SqueezeAff = 0;
                        ulong original = cur.OrigAff != 0 ? cur.OrigAff : allMask;
                        if (!CpuTopology.MultiGroup && Native.QueryAffinity(h) != original)
                            RunMutation(delegate { return Native.SetProcessAffinityMask(h, (UIntPtr)original); });
                        return false;
                    }
                    cur.Applied = applied;
                    ScheduleAfterApply(cur, applied, pid);
                    changed = previous != cur.SqueezeAff;
                    return applied;
                }
            }
            finally { Native.CloseHandle(h); }
        }

        // Entries with the anti-cheat reason belong to the anti-cheat path; the rest with a background reason belong to the background path
        private static bool SqueezeOwnedBy(Entry e, SuppressReason reason)
        {
            bool antiCheat = (e.Reasons & SuppressReason.AntiCheat) != 0;
            if (reason == SuppressReason.AntiCheat) return antiCheat;
            return !antiCheat && (e.Reasons & SuppressReason.Background) != 0;
        }

        // When the switch is turned off mid-match, release everything this path pinned; returns the count actually released
        public int ClearSqueezes(SuppressReason reason)
        {
            var squeezed = new List<KeyValuePair<int, Entry>>();
            lock (sync)
                foreach (var kv in map)
                    if (kv.Value.SqueezeAff != 0 && SqueezeOwnedBy(kv.Value, reason)) squeezed.Add(kv);
            int released = 0;
            foreach (var kv in squeezed)
            {
                bool changed;
                SetSqueeze(kv.Key, kv.Value.Creation, kv.Value.Name, 0, reason, out changed);
                if (changed) released++;
            }
            return released;
        }

        public int SqueezedCount(SuppressReason reason)
        {
            int n = 0;
            lock (sync)
                foreach (var kv in map)
                    if (kv.Value.SqueezeAff != 0 && SqueezeOwnedBy(kv.Value, reason)) n++;
            return n;
        }

        public bool IsSqueezed(int pid)
        {
            lock (sync)
            {
                Entry e;
                return map.TryGetValue(pid, out e) && e.SqueezeAff != 0;
            }
        }
    }
}
