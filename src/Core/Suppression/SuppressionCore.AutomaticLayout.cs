using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        internal interface IAutomaticLayoutPlatform
        {
            IntPtr Open(int pid, long creation, string name);
            uint[] ReadSets(IntPtr handle);
            bool ReadAffinity(IntPtr handle, out ulong mask);
            bool WriteSets(IntPtr handle, uint[] sets);
            void Close(IntPtr handle);
        }
        private sealed class AutomaticLayoutPlatform : IAutomaticLayoutPlatform
        {
            public IntPtr Open(int pid, long creation, string name)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_SET_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return h;
                long born, cpu; ulong io;
                if (Native.QueryProcessSample(h, out born, out cpu, out io) && born == creation
                    && SameName(Native.ImageName(h), name) && Native.StillActive(h)) return h;
                Native.CloseHandle(h); return IntPtr.Zero;
            }
            public uint[] ReadSets(IntPtr h) { return Native.QueryCpuSets(h); }
            public bool ReadAffinity(IntPtr h, out ulong mask) { return Native.TryQueryAffinity(h, out mask); }
            public bool WriteSets(IntPtr h, uint[] ids) { return Native.RestoreCpuSetsVerified(h, ids); }
            public void Close(IntPtr h) { Native.CloseHandle(h); }
        }
#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static IAutomaticLayoutPlatform AutomaticLayoutPlatformForTest;
        internal Func<bool> AutomaticLayoutSaveForTest;
#endif
        private static readonly IAutomaticLayoutPlatform automaticLayoutPlatform = new AutomaticLayoutPlatform();
        private static IAutomaticLayoutPlatform LayoutPlatform
        {
            get
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return AutomaticLayoutPlatformForTest;
#else
                return automaticLayoutPlatform;
#endif
            }
        }
        private bool SaveLayoutLocked()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (AutomaticLayoutSaveForTest != null && !AutomaticLayoutSaveForTest()) return false;
#endif
            return SaveJournalLocked(true);
        }
        internal bool AutomaticLayoutPending
        {
            get { lock (sync) { foreach (Entry e in map.Values) if (e.AutomaticLayoutSets != null) return true; return false; } }
        }
        private static void LayoutFailed(Entry e)
        {
            e.AutomaticLayoutFailures = Math.Min(8, e.AutomaticLayoutFailures + 1);
            e.AutomaticLayoutRetryAt = DateTime.UtcNow.AddSeconds(Math.Min(60,
                4 * (1 << Math.Min(4, e.AutomaticLayoutFailures - 1)))).Ticks;
            if (e.AutomaticLayoutFailures >= 2) e.AutomaticLayoutBlocked = true;
        }

        private static bool RestoreAutomaticLayoutValues(IntPtr h, Entry e)
        {
            if (e.AutomaticLayoutSets == null || e.AutomaticLayoutYielded) return true;
            IAutomaticLayoutPlatform api = LayoutPlatform;
            if (api == null) return false;
            uint[] current = api.ReadSets(h);
            ulong affinity;
            if (current == null || !api.ReadAffinity(h, out affinity)) return false;
            if (CoreThreadPlacement.Same(current, e.OrigCpuSets ?? new uint[0]) && affinity == e.OrigAff) return true;
            if (!CoreThreadPlacement.Same(current, e.AutomaticLayoutSets) || affinity != e.OrigAff)
            { e.AutomaticLayoutYielded = true; return true; }
            return api.WriteSets(h, e.OrigCpuSets ?? new uint[0])
                && CoreThreadPlacement.Same(api.ReadSets(h), e.OrigCpuSets ?? new uint[0]);
        }

        private bool RestoreAutomaticLayoutLocked(int pid, Entry e)
        {
            if (e.AutomaticLayoutSets == null) return true;
            if (e.RestoreInFlight) return false;
            e.AutomaticLayoutRestoring = true;
            IAutomaticLayoutPlatform api = LayoutPlatform;
            if (api == null) return false;
            IntPtr h = api.Open(pid, e.Creation, e.Name);
            if (h == IntPtr.Zero) { LayoutFailed(e); return false; }
            try
            {
                if (!RestoreAutomaticLayoutValues(h, e)) { LayoutFailed(e); return false; }
                uint[] previous = e.AutomaticLayoutSets;
                e.AutomaticLayoutSets = null;
                if (!SaveLayoutLocked()) { e.AutomaticLayoutSets = previous; LayoutFailed(e); return false; }
                e.AutomaticLayoutRestoring = false;
                return true;
            }
            finally { api.Close(h); }
        }

        internal bool RestoreAutomaticLayouts()
        {
            lock (sync)
            {
                bool ok = true;
                foreach (var item in map) if (!RestoreAutomaticLayoutLocked(item.Key, item.Value)) ok = false;
                return ok;
            }
        }

        private uint[] AutomaticLayoutDesired(IntPtr h, Entry e)
        {
            if (e.AutomaticLayoutSets == null || e.AutomaticLayoutYielded || e.AutomaticLayoutRestoring) return e.OrigCpuSets;
            IAutomaticLayoutPlatform api = LayoutPlatform;
            if (api == null) { e.AutomaticLayoutRestoring = true; return e.OrigCpuSets; }
            uint[] current = api.ReadSets(h); ulong affinity;
            if (current == null || !api.ReadAffinity(h, out affinity))
            { e.AutomaticLayoutRestoring = true; return e.OrigCpuSets; }
            if (!CoreThreadPlacement.Same(current, e.AutomaticLayoutSets) || affinity != e.OrigAff)
            { e.AutomaticLayoutYielded = true; SaveLayoutLocked(); return e.OrigCpuSets; }
            return e.AutomaticLayoutSets;
        }
        private static bool PlacementIsProtected(Entry e)
        { return e.SqueezeRefused || e.AutomaticLayoutYielded || e.AutomaticLayoutRestoring; }

        internal void SyncAutomaticLayout(ulong mask, uint[] ids, HashSet<int> protectedPids,
            int foreground, int renderer, Func<bool> admitted)
        {
            lock (sync)
            {
                IAutomaticLayoutPlatform api = LayoutPlatform;
                bool wanted = api != null && mask != 0 && (mask & ~allMask) == 0
                    && ids != null && ids.Length == AutoCores.CountBits(mask)
                    && new HashSet<uint>(ids).Count == ids.Length
                    && admitted != null && admitted() && !string.IsNullOrEmpty(journalPath);
                int attempts = 0, owned = 0;
                foreach (Entry entry in map.Values) if (entry.AutomaticLayoutSets != null) owned++;
                long now = DateTime.UtcNow.Ticks;
                foreach (var item in map)
                {
                    int pid = item.Key; Entry e = item.Value;
                    if (e.RestoreInFlight) continue;
                    bool eligible = wanted && pid != foreground && pid != renderer
                        && protectedPids != null && !protectedPids.Contains(pid)
                        && e.Reasons == SuppressReason.Background && e.Applied && !e.RestoreInFlight
                        && e.Creation > 0 && e.OrigPri != uint.MaxValue && !e.GaveUp && !e.SqueezeRefused
                        && !e.BackgroundPlacementOwned && e.SqueezeAff == 0 && e.OrigAff == allMask
                        && e.OrigCpuSets != null && e.OrigCpuSets.Length == 0
                        && !WeGameRuntimeProtection.IsProtectedRuntimeName(e.Name) && !PeripheralCatalog.IsInputAudioLike(e.Name);
                    if (e.AutomaticLayoutSets != null)
                    {
                        if (!eligible || e.AutomaticLayoutRestoring
                            || !CoreThreadPlacement.Same(e.AutomaticLayoutSets, ids))
                            if (now >= e.AutomaticLayoutRetryAt && RestoreAutomaticLayoutLocked(pid, e)) owned--;
                        continue;
                    }
                    if (!eligible || e.AutomaticLayoutYielded || e.AutomaticLayoutBlocked
                        || now < e.AutomaticLayoutRetryAt || owned >= 32 || ++attempts > 8 || !admitted()) continue;
                    IntPtr h = api.Open(pid, e.Creation, e.Name);
                    if (h == IntPtr.Zero) { LayoutFailed(e); continue; }
                    try
                    {
                        ulong affinity; uint[] original = api.ReadSets(h);
                        if (original == null || original.Length != 0 || !api.ReadAffinity(h, out affinity)
                            || affinity != e.OrigAff) { e.AutomaticLayoutBlocked = true; continue; }
                        e.AutomaticLayoutSets = (uint[])ids.Clone();
                        if (!SaveLayoutLocked()) { e.AutomaticLayoutSets = null; LayoutFailed(e); continue; }
                        if (!admitted() || !api.WriteSets(h, ids) || !CoreThreadPlacement.Same(api.ReadSets(h), ids))
                        { LayoutFailed(e); RestoreAutomaticLayoutLocked(pid, e); }
                        if (e.AutomaticLayoutSets != null) owned++;
                    }
                    finally { api.Close(h); }
                }
            }
        }

        private bool ReapplyReleasedEntry(IntPtr h, Entry e, int pid)
        {
            lock (sync)
            {
                Entry current;
                if (!map.TryGetValue(pid, out current) || current != e
                    || e.Reasons == SuppressReason.None || !SameProcess(h, e)) return false;
                return ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, AutomaticLayoutDesired(h, e),
                    DesiredGpu(e), e.OrigBoost, AntiCheatThrottled(e), DesiredAffinityOf(e), PlacementIsProtected(e));
            }
        }
    }
}
