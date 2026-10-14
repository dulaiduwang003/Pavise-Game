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

        private static void DieMasksStayEmptyOnSingleL3()
        {
            const ulong all = 0xFFFFFFFFUL;
            CpuTopology.InjectTopologyForTest(all, SixteenCores(all), new ulong[0],
                0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(L3(0xFFFFFFFFUL));
            Eq(0, CpuTopology.DieMasks().Length);
            CpuTopology.InjectCacheDomainsForTest(L3());
            Eq(0, CpuTopology.DieMasks().Length);
        }

        private static void DieMasksAreSortedAndDeduplicated()
        {
            const ulong all = 0x0000FFFFUL;
            CpuTopology.InjectTopologyForTest(all, SixteenCores(all), new ulong[0],
                0, 0, 0, 0, false, false);
            CpuTopology.InjectCacheDomainsForTest(L3(0xFF00UL, 0x00FFUL, 0xFF00UL,
                0xFFFF0000UL));
            ulong[] dies = CpuTopology.DieMasks();
            Eq(2, dies.Length);
            Eq(0x00FFUL, dies[0]);
            Eq(0xFF00UL, dies[1]);
        }
    }
}
#endif
