using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class AutomaticCoreLayout
    {
        internal const string PreferenceKey = "AutoBackgroundCoreLayoutV1";
        internal static ulong Plan(ulong all, ulong[] physical, bool hybrid, ulong efficiency,
            bool asymmetricCache, bool multiGroup, bool sourcesAgree, ulong allocated, ulong favored)
        {
            if (all == 0 || physical == null || multiGroup || !sourcesAgree || asymmetricCache
                || (efficiency & ~all) != 0) return 0;
            ulong covered = 0;
            var available = new List<ulong>();
            foreach (ulong core in physical)
            {
                if (core == 0 || (core & ~all) != 0 || (covered & core) != 0) return 0;
                if (hybrid && (core & efficiency) != 0 && (core & efficiency) != core) return 0;
                covered |= core;
                if ((core & allocated) == 0) available.Add(core);
            }
            if (covered != all) return 0;
            if (hybrid)
            {
                int performance = 0, efficient = 0;
                ulong result = 0;
                foreach (ulong core in available)
                {
                    ulong part = core & efficiency;
                    if (part != 0 && part != core) return 0; // 同一物理核的 SMT 线程不能拆分。
                    if (part == core) { result |= core; efficient++; }
                    else performance++;
                }
                return performance >= 4 && efficient >= 2 ? result : 0;
            }
            if (available.Count < 8) return 0;
            available.Sort(delegate(ulong a, ulong b)
            {
                int rank = ((a & favored) != 0 ? 1 : 0).CompareTo((b & favored) != 0 ? 1 : 0);
                return rank != 0 ? rank : b.CompareTo(a);
            });
            int count = Math.Min(2, available.Count / 8);
            ulong background = 0;
            for (int i = 0; i < count; i++) background |= available[i];
            return background;
        }

        internal static ulong Current()
        {
            if (CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree) return 0;
            IsolationCpuState live = new CoreIsolationNative().ReadSystem(IntPtr.Zero);
            if (live == null || live.All != CpuTopology.AllMask) return 0;
            ulong[] physical = CpuTopology.PhysicalCoreMasks();
            if (live.Physical == null || live.Physical.Length != physical.Length
                || !new HashSet<ulong>(live.Physical).SetEquals(physical)) return 0;
            return Plan(CpuTopology.AllMask, physical, CpuTopology.Hybrid,
                CpuTopology.EffMask, CpuTopology.AsymCache, CpuTopology.MultiGroup,
                CpuTopology.TopologySourcesAgree, live.Allocated, CpuTopology.FavoredMask);
        }
    }
}
