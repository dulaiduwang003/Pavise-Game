// 作者：bdth 2074055628@qq.com
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
            public ulong All, Perf, Eff, BigL3, SmallL3;
            public bool Hybrid, Asym;
            public ulong[] Cores, Dies, Parsed;
            public bool Reconciled;
            public KeyValuePair<uint, ulong>[] L3;
        }

        internal static TopologySnapshot CaptureTopologyForTest()
        {
            return new TopologySnapshot
            {
                All = AllMask, Perf = PerfMask, Eff = EffMask,
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
            SetParsedCoreMasksForTest(s.Parsed);
            AllMaskReconciled = s.Reconciled;
        }

        internal static void InjectTopologyForTest(ulong all, ulong[] cores, ulong[] dies,
            ulong perf, ulong eff, ulong bigL3, ulong smallL3, bool hybrid, bool asym)
        {
            AllMask = all;
            physicalCoreMasks.Clear();
            if (cores != null) physicalCoreMasks.AddRange(cores);
            SetParsedCoreMasksForTest(cores);
            AllMaskReconciled = false;
            processorDieDomains = new List<ulong>(dies ?? new ulong[0]);
            PerfMask = perf; EffMask = eff;
            BigL3Mask = bigL3; SmallL3Mask = smallL3;
            Hybrid = hybrid; AsymCache = asym;
            squeezeCache = null;
        }

        internal static void InjectCacheDomainsForTest(KeyValuePair<uint, ulong>[] domains)
        {
            cacheDomains = new List<KeyValuePair<uint, ulong>>(
                domains ?? new KeyValuePair<uint, ulong>[0]);
        }
#endif

        // 系统缺少 Die 记录时，用 L3 分组识别 AMD CCD。
        public static ulong[] DieMasks()
        {
            ulong[] fromDies = DomainMasks(processorDieDomains);
            if (fromDies.Length >= 2) return fromDies;
            var l3 = new List<ulong>();
            foreach (KeyValuePair<uint, ulong> kv in cacheDomains) l3.Add(kv.Value);
            return DomainMasks(l3);
        }

        // 按最低位排序，保持 CCD 编号稳定。
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
    }
}
