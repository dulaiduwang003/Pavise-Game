// @author bdth 2074055628@qq.com
// File purpose Topology snapshot test hooks, mask description and custom core selection
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class CpuTopology
    {
#if PAVISE_SELFTEST
        internal sealed class TopologySnapshot
        {
            public ulong All, Perf, Eff, LowPower, BigL3, SmallL3;
            public bool Hybrid, Asym;
            public ulong[] Cores, Dies, Parsed;
            public bool Reconciled;
            public KeyValuePair<uint, ulong>[] L3;
        }

        internal static TopologySnapshot CaptureTopologyForTest()
        {
            return new TopologySnapshot
            {
                All = AllMask, Perf = PerfMask, Eff = EffMask, LowPower = LowPowerEffMask,
                BigL3 = BigL3Mask, SmallL3 = SmallL3Mask,
                Hybrid = Hybrid, Asym = AsymCache,
                Cores = physicalCoreMasks.ToArray(),
                Parsed = ParsedCoreMasksForTest(),
                Reconciled = AllMaskReconciled,
                Dies = processorDieDomains.ToArray(),
                L3 = cacheDomains.ToArray(),
            };
        }

        internal static void RestoreTopologyForTest(TopologySnapshot s)
        {
            if (s == null) return;
            InjectTopologyForTest(s.All, s.Cores, s.Dies,
                s.Perf, s.Eff, s.BigL3, s.SmallL3, s.Hybrid, s.Asym);
            InjectCacheDomainsForTest(s.L3);
            LowPowerEffMask = s.LowPower;
            SetParsedCoreMasksForTest(s.Parsed);
            AllMaskReconciled = s.Reconciled;
        }

        internal static void InjectTopologyForTest(ulong all, ulong[] cores, ulong[] dies,
            ulong perf, ulong eff, ulong bigL3, ulong smallL3, bool hybrid, bool asym)
        {
            AllMask = all;
            physicalCoreMasks.Clear();
            if (cores != null) physicalCoreMasks.AddRange(cores);
            // Physical cores have two sources, injecting only one path gives a false positive on the consistency check and a warning on bench screenshots
            SetParsedCoreMasksForTest(cores);
            AllMaskReconciled = false;
            processorDieDomains = new List<ulong>(dies ?? new ulong[0]);
            PerfMask = perf; EffMask = eff; LowPowerEffMask = 0;
            BigL3Mask = bigL3; SmallL3Mask = smallL3;
            Hybrid = hybrid; AsymCache = asym;
            customSet = null;
            squeezeCache = null;
        }

        // L3 grouping stays out of InjectTopologyForTest's parameter list, that signature has thirteen call sites
        //   whatever is injected must be restored by RestoreTopologyForTest, otherwise it leaks into other suites
        internal static void InjectCacheDomainsForTest(KeyValuePair<uint, ulong>[] domains)
        {
            cacheDomains = new List<KeyValuePair<uint, ulong>>(
                domains ?? new KeyValuePair<uint, ulong>[0]);
        }
#endif

        // Windows basically never reports RelationProcessorDie, measured on Win10 19045 with a 13900K and
        //   Win11 26200 with a Ryzen 9 8940HX, both give 0 die records
        //   AMD CCDs show up as one L3 per CCD, so with fewer than two dies fall back to L3 grouping
        //   Intel consumer parts share one L3 across the whole die, after fallback still 0 groups, no bands or hotkeys appear out of nowhere
        //   background placement does not go through here, it has its own path reading the L3 index from cacheDomains, this only serves the UI
        public static ulong[] DieMasks()
        {
            ulong[] fromDies = DomainMasks(processorDieDomains);
            if (fromDies.Length >= 2) return fromDies;
            var l3 = new List<ulong>();
            foreach (KeyValuePair<uint, ulong> kv in cacheDomains) l3.Add(kv.Value);
            return DomainMasks(l3);
        }

        // Masks are disjoint, ascending by value is ascending by lowest bit, CCD 0 is always the lowest-numbered group
        //   enumeration order is not guaranteed stable, without sorting button numbering could swap between launches
        private static ulong[] DomainMasks(List<ulong> domains)
        {
            var list = new List<ulong>();
            foreach (ulong d in domains)
            {
                ulong m = d & AllMask;
                if (m != 0 && !list.Contains(m)) list.Add(m);
            }
            if (list.Count < 2) return new ulong[0];
            list.Sort();
            return list.ToArray();
        }

        public static ulong CacheHeavyMask()
        {
            return AsymCache ? BigL3Mask & AllMask : 0;
        }

        internal static ulong SanitizeCustomMask(ulong wanted, ulong all)
        {
            ulong m = wanted & all;
            return CountSetBits(m) >= MinCustomCores ? m : 0;
        }

        internal static int CountSetBits(ulong v)
        {
            int n = 0;
            while (v != 0) { n += (int)(v & 1UL); v >>= 1; }
            return n;
        }

        public static string DescribeMask(ulong mask)
        {
            if (mask == 0) return Lang.T("t.cputopology.1");
            var parts = new List<string>();
            int i = 0;
            while (i < 64)
            {
                if ((mask & (1UL << i)) == 0) { i++; continue; }
                int start = i;
                while (i + 1 < 64 && (mask & (1UL << (i + 1))) != 0) i++;
                parts.Add(i > start + 1 ? start + "-" + i
                    : i == start + 1 ? start + "," + i : start.ToString());
                i++;
            }
            return CountSetBits(mask) + Lang.T("t.cputopology.2") + string.Join(",", parts.ToArray());
        }

        public const int MinCustomBackgroundCores = 2;

        private sealed class CustomCoreSet
        {
            public ulong Mask;
            public uint[] Ids;
            public ulong BackgroundMask;
            public uint[] BackgroundIds;
        }

        private static volatile CustomCoreSet customSet;

        public static ulong CustomMask
        {
            get { CustomCoreSet c = customSet; return c != null ? c.Mask : 0; }
        }

        public static ulong CustomBackgroundMask
        {
            get { CustomCoreSet c = customSet; return c != null ? c.BackgroundMask : 0; }
        }

        internal static ulong BackgroundRemainderFor(ulong game, ulong all)
        {
            ulong rest = all & ~game;
            return CountSetBits(rest) >= MinCustomBackgroundCores ? rest : 0;
        }

        public static bool SetCustomMask(ulong wanted)
        {
            ulong clean = SanitizeCustomMask(wanted, AllMask);
            CustomCoreSet prev = customSet;
            if (clean == (prev != null ? prev.Mask : 0)) return clean != 0;
            if (clean == 0)
            {
                customSet = null;
                squeezeCache = null;
                return false;
            }
            uint[] ids;
            try { ids = CpuSetIdsFor(clean); }
            catch { ids = null; }
            if (ids == null || ids.Length == 0)
            {
                customSet = null;
                squeezeCache = null;
                return false;
            }
            var next = new CustomCoreSet { Mask = clean, Ids = ids };
            ulong rest = BackgroundRemainderFor(clean, AllMask);
            if (rest != 0)
            {
                uint[] bg;
                try { bg = CpuSetIdsFor(rest); }
                catch { bg = null; }
                if (bg != null && bg.Length > 0)
                {
                    next.BackgroundIds = bg;
                    next.BackgroundMask = rest;
                }
            }
            customSet = next;
            squeezeCache = null;
            return true;
        }
    }
}
