// File purpose CCD grouping source regression, pure decisions, only injects topology snapshots, no real hardware reads
//   The UI's CCD bands and shortcuts both come from CpuTopology.DieMasks()
//   Windows basically never reports RelationProcessorDie, both measured machines returned 0 entries
//     Win10 19045 / i9-13900K   cores=24 dies=0 L3groups=1
//     Win11 26200 / Ryzen 9 8940HX cores=16 dies=0 L3groups=2 two 32MiB blocks
//   So with fewer than two dies fall back to L3 grouping, otherwise no band is drawn at all on dual-CCD AMD
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunDieMaskRegressionTests()
        {
            Action[] tests =
            {
                DieMasksFallBackToL3WhenWindowsReportsNoDie,
                DieMasksPreferRealDiesWhenReported,
                DieMasksStayEmptyOnSingleL3,
                DieMasksAreSortedAndDeduplicated
            };
            CpuTopology.TopologySnapshot saved = CpuTopology.CaptureTopologyForTest();
            try
            {
                foreach (Action test in tests)
                {
                    test();
                    Console.WriteLine("PASS " + test.Method.Name);
                }
            }
            finally { CpuTopology.RestoreTopologyForTest(saved); }
            return tests.Length;
        }

        private static KeyValuePair<uint, ulong>[] L3(params ulong[] masks)
        {
            var list = new List<KeyValuePair<uint, ulong>>();
            foreach (ulong m in masks) list.Add(new KeyValuePair<uint, ulong>(32u * 1024 * 1024, m));
            return list.ToArray();
        }

        private static ulong[] SixteenCores(ulong all)
        {
            var cores = new List<ulong>();
            for (int i = 0; i < 32; i += 2) cores.Add((3UL << i) & all);
            return cores.ToArray();
        }

        // The real shape of the 8940HX: 32 logical cores, two equal-size L3 blocks each holding half, no die records
        private static void DieMasksFallBackToL3WhenWindowsReportsNoDie()
        {
            const ulong all = 0xFFFFFFFFUL;
            CpuTopology.InjectTopologyForTest(all, SixteenCores(all), new ulong[0],
                0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(L3(0x0000FFFFUL, 0xFFFF0000UL));
            ulong[] dies = CpuTopology.DieMasks();
            Eq(2, dies.Length);
            Eq(0x0000FFFFUL, dies[0]);
            Eq(0xFFFF0000UL, dies[1]);
        }

        // When dies are really reported use them, do not let L3 override; machines with multiple dies and multiple L3 blocks per die must band by die
        private static void DieMasksPreferRealDiesWhenReported()
        {
            const ulong all = 0xFFFFFFFFUL;
            CpuTopology.InjectTopologyForTest(all, SixteenCores(all),
                new[] { 0x0000FFFFUL, 0xFFFF0000UL }, 0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(L3(0x000000FFUL, 0x0000FF00UL,
                0x00FF0000UL, 0xFF000000UL));
            ulong[] dies = CpuTopology.DieMasks();
            Eq(2, dies.Length);
            Eq(0x0000FFFFUL, dies[0]);
        }

        // Consumer Intel shares one L3 across the whole chip, still empty after the fallback, no band may appear out of nowhere
        private static void DieMasksStayEmptyOnSingleL3()
        {
            const ulong all = 0xFFFFFFFFUL;
            CpuTopology.InjectTopologyForTest(all, SixteenCores(all), new ulong[0],
                0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(L3(0xFFFFFFFFUL));
            Eq(0, CpuTopology.DieMasks().Length);
            // A machine with no entries at all is empty too
            CpuTopology.InjectCacheDomainsForTest(L3());
            Eq(0, CpuTopology.DieMasks().Length);
        }

        // Enumeration order is not stable, CCD 0 must always be the lowest-numbered group or button numbers swap across launches
        //   Masks are also intersected with AllMask first, out-of-range bits and duplicates must not get in
        private static void DieMasksAreSortedAndDeduplicated()
        {
            const ulong all = 0x0000FFFFUL;
            CpuTopology.InjectTopologyForTest(all, SixteenCores(all), new ulong[0],
                0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(L3(0xFF00UL, 0x00FFUL, 0xFF00UL,
                0xFFFF0000UL));
            ulong[] dies = CpuTopology.DieMasks();
            // The high group lies entirely outside AllMask, intersects to zero and is dropped; the duplicate 0xFF00 is kept once
            Eq(2, dies.Length);
            Eq(0x00FFUL, dies[0]);
            Eq(0xFF00UL, dies[1]);
        }
    }
}
#endif
