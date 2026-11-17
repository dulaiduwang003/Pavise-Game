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
                IntPtr processHandle = Native.OpenProcess(12800, false, pid);
                if (processHandle == IntPtr.Zero)
                {
                    return processHandle;
                }
                long creation2;
                long cpu;
                ulong io;
                if (Native.QueryProcessSample(processHandle, out creation2, out cpu, out io) && creation2 == creation && SameName(Native.ImageName(processHandle), name) && Native.StillActive(processHandle))
                {
                    return processHandle;
                }
                Native.CloseHandle(processHandle);
                return IntPtr.Zero;
            }

            public uint[] ReadSets(IntPtr h)
            {
                return Native.QueryCpuSets(h);
            }

            public bool ReadAffinity(IntPtr h, out ulong mask)
            {
                return Native.TryQueryAffinity(h, out mask);
            }

            public bool WriteSets(IntPtr h, uint[] ids)
            {
                return Native.RestoreCpuSetsVerified(h, ids);
            }

            public void Close(IntPtr h)
            {
                Native.CloseHandle(h);
            }
        }

        private static IAutomaticLayoutPlatform LayoutPlatform
        {
            get
            {
                return automaticLayoutPlatform;
            }
        }

        internal bool AutomaticLayoutPending
        {
            get
            {
                lock (sync)
                {
                    foreach (Entry value in map.Values)
                    {
                        if (value.AutomaticLayoutSets != null)
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }
        }

        private bool SaveLayoutLocked()
        {
            return SaveJournalLocked(true);
        }

        private static void LayoutFailed(Entry e)
        {
            FailureBudgetFor(e, "layout");
            e.AutomaticLayoutFailures = Math.Min(8, e.AutomaticLayoutFailures + 1);
            e.AutomaticLayoutRetryAt = e.AutomaticLayoutFailures >= 8 ? DateTime.MaxValue.Ticks : DateTime.UtcNow.AddSeconds(Math.Min(60, 4 * (1 << Math.Min(4, e.AutomaticLayoutFailures - 1)))).Ticks;
            RecordFailureBudget(e, "layout", false, e.AutomaticLayoutRetryAt);
        }

        private static bool RestoreAutomaticLayoutValues(IntPtr h, Entry e)
        {
            if (e.AutomaticLayoutSets == null || e.AutomaticLayoutYielded)
            {
                return true;
            }
            IAutomaticLayoutPlatform layoutPlatform = LayoutPlatform;
            if (layoutPlatform == null)
            {
                return false;
            }
            uint[] sets = layoutPlatform.ReadSets(h);
            ulong mask;
            if (sets == null || !layoutPlatform.ReadAffinity(h, out mask))
            {
                return false;
            }
            if (CoreThreadPlacement.Same(sets, e.OrigCpuSets ?? new uint[0]) && mask == e.OrigAff)
            {
                return true;
            }
            if (!CoreThreadPlacement.Same(sets, e.AutomaticLayoutSets) || mask != e.OrigAff)
            {
                e.AutomaticLayoutYielded = true;
                return true;
            }
            if (layoutPlatform.WriteSets(h, e.OrigCpuSets ?? new uint[0]))
            {
                return CoreThreadPlacement.Same(layoutPlatform.ReadSets(h), e.OrigCpuSets ?? new uint[0]);
            }
            return false;
        }

        private bool RestoreAutomaticLayoutLocked(int pid, Entry e)
        {
            if (e.AutomaticLayoutSets == null)
            {
                return true;
            }
            if (RestoreBudgetBlocked(e, "layout", false)) return false;
            if (e.RestoreInFlight)
            {
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                return false;
            }
            e.AutomaticLayoutRestoring = true;
            IAutomaticLayoutPlatform layoutPlatform = LayoutPlatform;
            if (layoutPlatform == null)
            {
                RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
                return false;
            }
            IntPtr processHandle = layoutPlatform.Open(pid, e.Creation, e.Name);
            if (processHandle == IntPtr.Zero)
            {
                LayoutFailed(e);
                return false;
            }
            try
            {
                if (!RestoreAutomaticLayoutValues(processHandle, e))
                {
                    LayoutFailed(e);
                    return false;
                }
                uint[] automaticLayoutSets = e.AutomaticLayoutSets;
                e.AutomaticLayoutSets = null;
                if (!SaveLayoutLocked())
                {
                    e.AutomaticLayoutSets = automaticLayoutSets;
                    LayoutFailed(e);
                    return false;
                }
                e.AutomaticLayoutRestoring = false;
                RecordFailureBudget(e, "layout", true, 0);
                return true;
            }
            finally
            {
                layoutPlatform.Close(processHandle);
            }
        }

        internal bool RestoreAutomaticLayouts()
        {
            lock (sync)
            {
                bool result = true;
                foreach (KeyValuePair<int, Entry> item in map)
                {
                    if (!RestoreAutomaticLayoutLocked(item.Key, item.Value))
                    {
                        result = false;
                    }
                }
                return result;
            }
        }

        internal void RetryRetiredAutomaticLayouts()
        {
            lock (sync)
            {
                long ticks = DateTime.UtcNow.Ticks;
                foreach (KeyValuePair<int, Entry> item in map)
                {
                    if (item.Value.AutomaticLayoutSets != null && !RestoreBudgetBlocked(item.Value, "layout", true) && ticks >= item.Value.AutomaticLayoutRetryAt)
                    {
                        RestoreAutomaticLayoutLocked(item.Key, item.Value);
                    }
                }
            }
        }

        private static bool PlacementIsProtected(Entry e)
        {
            if (e.BackgroundJobPlacement)
            {
                return true;
            }
            if (!e.SqueezeRefused && e.AutomaticLayoutSets == null && !e.AutomaticLayoutYielded)
            {
                return e.AutomaticLayoutRestoring;
            }
            return true;
        }

        private static bool RestorePlacement(Entry e)
        {
            return !e.BackgroundJobPlacement && !e.BackgroundPlacementOwned && !e.AutomaticLayoutYielded;
        }

    }
}
