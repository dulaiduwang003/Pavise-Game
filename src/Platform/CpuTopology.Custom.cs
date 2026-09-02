// @author bdth 2074055628@qq.com
// 文件用途 拓扑快照测试钩子 掩码描述与自定义核心选择
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
            public ulong[] Cores, Dies;
        }

        internal static TopologySnapshot CaptureTopologyForTest()
        {
            return new TopologySnapshot
            {
                All = AllMask, Perf = PerfMask, Eff = EffMask, LowPower = LowPowerEffMask,
                BigL3 = BigL3Mask, SmallL3 = SmallL3Mask,
                Hybrid = Hybrid, Asym = AsymCache,
                Cores = physicalCoreMasks.ToArray(),
                Dies = processorDieDomains.ToArray(),
            };
        }

        internal static void RestoreTopologyForTest(TopologySnapshot s)
        {
            if (s == null) return;
            InjectTopologyForTest(s.All, s.Cores, s.Dies,
                s.Perf, s.Eff, s.BigL3, s.SmallL3, s.Hybrid, s.Asym);
            LowPowerEffMask = s.LowPower;
        }

        internal static void InjectTopologyForTest(ulong all, ulong[] cores, ulong[] dies,
            ulong perf, ulong eff, ulong bigL3, ulong smallL3, bool hybrid, bool asym)
        {
            AllMask = all;
            physicalCoreMasks.Clear();
            if (cores != null) physicalCoreMasks.AddRange(cores);
            processorDieDomains = new List<ulong>(dies ?? new ulong[0]);
            PerfMask = perf; EffMask = eff; LowPowerEffMask = 0;
            BigL3Mask = bigL3; SmallL3Mask = smallL3;
            Hybrid = hybrid; AsymCache = asym;
            customSet = null;
            squeezeCache = null;
        }
#endif

        public static ulong[] DieMasks()
        {
            var list = new List<ulong>();
            foreach (ulong d in processorDieDomains)
            {
                ulong m = d & AllMask;
                if (m != 0) list.Add(m);
            }
            return list.Count >= 2 ? list.ToArray() : new ulong[0];
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
