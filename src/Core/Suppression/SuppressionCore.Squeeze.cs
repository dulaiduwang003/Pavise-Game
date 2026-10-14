// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        internal bool EnforceBackgroundAffinity;
#if PAVISE_SELFTEST
        internal bool SqueezeReadbackFailsForTest;
#endif
        public bool SetSqueeze(int pid, long creation, string name, ulong squeezeMask,
            SuppressReason reason, out bool changed)
        {
            changed = false;
            if (squeezeMask != 0 && reason == SuppressReason.Background && (WeGameRuntimeProtection.IsProtectedRuntimeName(name)
                || PeripheralCatalog.IsInputAudioLike(name))) return false;
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
                if (squeezeMask != 0 && target == 0) return false;
                // 手动分配不能覆盖自动 CPU Set 租约。
                if (target != 0 && e.AutomaticLayoutSets != null
                    && !RestoreAutomaticLayoutLocked(pid, e)) return false;
                if (reason == SuppressReason.Background)
                {
                    if (target == 0 && e.SqueezeAff != 0)
                    { e.BackgroundRestorePending = true; e.BackgroundPlacementVerified = false; }
                    if (target != 0 && e.BackgroundRestorePending) return false;
                }
                if (e.SqueezeAff == target && (reason != SuppressReason.Background
                    || !e.BackgroundRestorePending && (e.BackgroundPlacementVerified || target == 0))) return true;
                if (target != 0 && (e.SqueezeRefused || e.GaveUp)) return false;
                if (target != 0 && DateTime.UtcNow.Ticks < e.SqueezeRetryAt) return false;
            }
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) { NoteSqueezeAttempt(pid, e, target, false); return false; }
            bool squeezeOk = false;
            try
            {
                if (!SameProcess(h, e)) return false;
                lock (sync)
                {
                    Entry cur;
                    if (!map.TryGetValue(pid, out cur) || cur != e || !SqueezeOwnedBy(cur, reason)
                        || cur.OrigPri == uint.MaxValue || cur.Creation != creation) return false;
                    ulong previous = cur.SqueezeAff;
                    if (reason == SuppressReason.Background)
                    {
                        ulong current;
                        if (!Native.TryQueryAffinity(h, out current)) return false;
                        ulong expected = previous != 0 ? previous : cur.OrigAff;
                        if (!EnforceBackgroundAffinity && (current != expected || !Native.CpuSetsMatch(h, cur.OrigCpuSets ?? new uint[0])))
                        {
                            if (previous != 0) { cur.SqueezeAff = 0; cur.SqueezeRefused = true; cur.BackgroundPlacementVerified = false; }
                            cur.BackgroundRestorePending = false;
                            return false;
                        }
                        ulong desired = target != 0 ? target : cur.OrigAff;
                        if (desired == 0) return false;
                        bool wasOwned = cur.BackgroundPlacementOwned;
                        ulong oldApplied = cur.BackgroundApplied, oldPrevious = cur.BackgroundPrevious;
                        cur.BackgroundPlacementOwned = true;
                        cur.BackgroundPrevious = previous != 0 ? previous : oldPrevious;
                        cur.BackgroundApplied = desired;
                        if (!SaveJournalLocked())
                        {
                            cur.BackgroundPlacementOwned = wasOwned;
                            cur.BackgroundApplied = oldApplied; cur.BackgroundPrevious = oldPrevious;
                            return false;
                        }
                        bool written = Native.SetProcessAffinityMask(h, (UIntPtr)desired);
                        bool read = Native.TryQueryAffinity(h, out current);
#if PAVISE_SELFTEST
                        if (SqueezeReadbackFailsForTest) read = false;
#endif
                        cur.BackgroundPlacementVerified = written && read && current == desired;
                        if (written && read && current == desired)
                        {
                            cur.SqueezeAff = target;
                            cur.BackgroundRestorePending = false;
                            if (EnforceBackgroundAffinity) ScheduleAfterApply(cur, true, pid);
                            cur.BackgroundPrevious = 0;
                            if (!SaveJournalLocked()) cur.BackgroundPrevious = previous;
                            changed = previous != target;
                            squeezeOk = true;
                            return true;
                        }
                        if (target != 0 && (!read || current == desired)) cur.SqueezeAff = target;
                        // 释放需回读确认，待恢复期间巡检使用原掩码。
                        return false;
                    }
                    if (cur.BackgroundPlacementOwned)
                    {
                        cur.BackgroundPlacementOwned = false;
                        if (!SaveJournalLocked()) { cur.BackgroundPlacementOwned = true; return false; }
                        cur.BackgroundRestorePending = false;
                    }
                    cur.SqueezeAff = target;
                    bool applied = ApplyThrottle(h, cur.Level, cur.OrigPri, cur.OrigAff, cur.OrigCpuSets,
                        DesiredGpu(cur), cur.OrigBoost, AntiCheatThrottled(cur), DesiredAffinityOf(cur));
                    if (!applied && target != 0)
                    {
                        cur.SqueezeAff = 0;
                        ulong original = cur.OrigAff != 0 ? cur.OrigAff : allMask;
                        if (!CpuTopology.MultiGroup && Native.QueryAffinity(h) != original)
                            Native.SetProcessAffinityMask(h, (UIntPtr)original);
                        return false;
                    }
                    cur.Applied = applied;
                    ScheduleAfterApply(cur, applied, pid);
                    changed = previous != cur.SqueezeAff;
                    squeezeOk = applied;
                    return applied;
                }
            }
            finally { Native.CloseHandle(h); NoteSqueezeAttempt(pid, e, target, squeezeOk); }
        }

        private void NoteSqueezeAttempt(int pid, Entry entry, ulong target, bool ok)
        {
            // 恢复不消耗应用预算。
            if (target == 0) return;
            lock (sync)
            {
                Entry current;
                if (!map.TryGetValue(pid, out current) || current != entry) return;
                if (ok) { current.SqueezeFailures = 0; current.SqueezeRetryAt = 0; return; }
                current.SqueezeFailures++;
                current.SqueezeRetryAt = DateTime.UtcNow.AddSeconds(30).Ticks;
                if (current.SqueezeFailures >= 2) current.SqueezeRefused = true;
            }
        }

        private static bool RestoreBackgroundPlacement(IntPtr handle, Entry e)
        {
            return RestoreAutomaticLayoutValues(handle, e) && (!e.BackgroundPlacementOwned
                || (CrashGuard.RestoreOwnedAffinity(handle, e.OrigAff, e.BackgroundApplied)
                    && CrashGuard.RestoreOwnedAffinity(handle, e.OrigAff, e.BackgroundPrevious)));
        }

        private void DetectBackgroundPlacementChange(IntPtr handle, Entry e)
        {
            if (EnforceBackgroundAffinity || e.BackgroundRestorePending) return;
            if (!e.BackgroundPlacementOwned || e.SqueezeRefused || (e.Reasons & SuppressReason.AntiCheat) != 0) return;
            ulong current;
            if (!Native.TryQueryAffinity(handle, out current) || current != (e.SqueezeAff == 0 ? e.OrigAff : e.SqueezeAff)
                || !Native.CpuSetsMatch(handle, e.OrigCpuSets ?? new uint[0]))
            { e.SqueezeRefused = true; e.SqueezeAff = 0; e.BackgroundPlacementVerified = false; }
        }

        private static bool SqueezeOwnedBy(Entry e, SuppressReason reason)
        {
            bool antiCheat = (e.Reasons & SuppressReason.AntiCheat) != 0;
            if (reason == SuppressReason.AntiCheat) return antiCheat;
            return !antiCheat && (e.Reasons & SuppressReason.Background) != 0;
        }

        public int ClearSqueezes(SuppressReason reason)
        {
            var squeezed = new List<KeyValuePair<int, Entry>>();
            lock (sync)
                foreach (var kv in map)
                    if ((kv.Value.SqueezeAff != 0 || reason == SuppressReason.Background && kv.Value.BackgroundRestorePending)
                        && SqueezeOwnedBy(kv.Value, reason)) squeezed.Add(kv);
            int released = 0;
            foreach (var kv in squeezed)
            {
                bool changed;
                SetSqueeze(kv.Key, kv.Value.Creation, kv.Value.Name, 0, reason, out changed);
                if (changed) released++;
            }
            return released;
        }

        public int SqueezedCount(SuppressReason reason, bool verifiedOnly = true)
        {
            int n = 0;
            lock (sync)
                foreach (var kv in map)
                    if ((kv.Value.SqueezeAff != 0 || reason == SuppressReason.Background && kv.Value.BackgroundRestorePending)
                        && SqueezeOwnedBy(kv.Value, reason)
                        && (!verifiedOnly || reason != SuppressReason.Background
                            || kv.Value.BackgroundPlacementVerified && !kv.Value.BackgroundRestorePending)) n++;
            return n;
        }

        public bool IsSqueezed(int pid)
        {
            lock (sync)
            {
                Entry e;
                return map.TryGetValue(pid, out e) && e.SqueezeAff != 0
                    && !e.BackgroundRestorePending
                    && (!e.BackgroundPlacementOwned || e.BackgroundPlacementVerified);
            }
        }
    }
}
