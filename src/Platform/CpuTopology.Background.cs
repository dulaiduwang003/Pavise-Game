// @author bdth 2074055628@qq.com
// File purpose Background core-yield mask, L3 grouping and CPU Sets policy
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class CpuTopology
    {
        public static uint[] CustomCpuSetIds()
        {
            CustomCoreSet c = customSet;
            return c != null ? c.Ids : null;
        }

        public static uint[] EffectiveBackgroundCpuSetIds()
        {
            CustomCoreSet c = customSet;
            return c != null && c.BackgroundIds != null ? c.BackgroundIds : backgroundIds;
        }

        public static ulong BackgroundAllowedMask()
        {
            CustomCoreSet c = customSet;
            if (c != null) return c.BackgroundMask;
            if (HasSafeBackgroundPartition()) return ThrottleMask;
            return AllMask;
        }

        public static ulong BackgroundSqueezeMask()
        {
            CustomCoreSet c = customSet;
            return CpuPartitionPolicy.SqueezeMask(
                physicalCoreMasks.ToArray(), BackgroundAllowedMask(), EffMask, Hybrid,
                L3Masks(), c != null ? c.Mask : StrictBoostMask);
        }


        public static int L3CacheMb()
        {
            try
            {
                uint max = 0;
                foreach (KeyValuePair<uint, ulong> kv in cacheDomains)
                    if (kv.Key > max) max = kv.Key;
                return (int)(max / (1024 * 1024));
            }
            catch { return 0; }
        }

        public static ulong[] L3Masks()
        {
            var list = new List<ulong>();
            foreach (KeyValuePair<uint, ulong> kv in cacheDomains)
            {
                ulong m = kv.Value & AllMask;
                if (m != 0) list.Add(m);
            }
            return list.ToArray();
        }

        public static bool HasEffectiveBackgroundPartition()
        {
            CustomCoreSet c = customSet;
            if (c != null) return c.BackgroundIds != null && c.BackgroundIds.Length > 0;
            return HasSafeBackgroundPartition();
        }

        private sealed class SqueezeCache
        {
            public uint[] Ids;
        }

        private static volatile SqueezeCache squeezeCache;

        public static uint[] BackgroundYieldCpuSetIds()
        {
            if (HasEffectiveBackgroundPartition()) return EffectiveBackgroundCpuSetIds();
            SqueezeCache cache = squeezeCache;
            if (cache == null)
            {
                cache = new SqueezeCache();
                if (!MultiGroup)
                {
                    ulong squeeze = BackgroundSqueezeMask();
                    if (squeeze != 0)
                    {
                        try { cache.Ids = CpuSetIdsFor(squeeze); }
                        catch { cache.Ids = null; }
                    }
                }
                squeezeCache = cache;
            }
            return cache.Ids;
        }

        public static bool HasBackgroundYieldTarget()
        {
            uint[] ids = BackgroundYieldCpuSetIds();
            return ids != null && ids.Length > 0;
        }

        internal static ulong DefaultBoostMaskForPartition(string tag, ulong partition, ulong all)
        {
            return all;
        }

        public static ulong ExpandPhysicalCoreMask(ulong logicalMask)
        {
            foreach (ulong core in physicalCoreMasks) if ((core & logicalMask) != 0) return core;
            return logicalMask;
        }

        public static int PhysicalCoreCount { get { return physicalCoreMasks.Count; } }

        public static ulong PrimaryCoreMask
        {
            get { return physicalCoreMasks.Count > 0 ? ExpandPhysicalCoreMask(1UL) : 0; }
        }

        public static ulong[] PhysicalCoreMasks() { return physicalCoreMasks.ToArray(); }

        public static string TopologyStamp()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Environment.ProcessorCount).Append(':');
            sb.Append(physicalCoreMasks.Count).Append(':');
            sb.Append(Hybrid ? '1' : '0').Append(AsymCache ? '1' : '0').Append(':');
            sb.Append(MultiGroup ? '1' : '0').Append(':');
            sb.Append(PartitionTag ?? "").Append(':');
            var sorted = new List<ulong>(physicalCoreMasks);
            sorted.Sort();
            foreach (ulong m in sorted) sb.Append(m.ToString("X")).Append(',');
            unchecked
            {
                ulong h = 1469598103934665603UL;
                string s = sb.ToString();
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 1099511628211UL;
                }
                return h.ToString("X16");
            }
        }

        internal static bool TryCpuSetIdsToMask(uint[] ids, out ulong mask)
        {
            mask = 0;
            if (ids == null) return false;
            for (int i = 0; i < ids.Length; i++)
            {
                ulong bit;
                if (!cpuSetMaskById.TryGetValue(ids[i], out bit)
                    || bit == 0)
                {
                    mask = 0;
                    return false;
                }
                mask |= bit;
            }
            return true;
        }


        private static ulong ComputeFavoredMask(List<CpuSetRec> rows)
        {
            var logical = new List<int>(rows.Count);
            var efficiency = new List<byte>(rows.Count);
            var scheduling = new List<byte>(rows.Count);
            foreach (CpuSetRec r in rows)
            {
                if (r.Group != 0 || r.Logical >= 64) continue;
                logical.Add(r.Logical); efficiency.Add(r.Efficiency); scheduling.Add(r.Scheduling);
            }
            FavoredDetail = DescribeCoreClasses(logical.ToArray(), efficiency.ToArray(), scheduling.ToArray());
            return FavoredMaskOf(logical.ToArray(), efficiency.ToArray(), scheduling.ToArray());
        }

        // Efficiency class and rating per core, adjacent equal values merged into segments, like 0-7:1/1 8-11:1/2 12-15:0/0
        internal static string DescribeCoreClasses(int[] logical, byte[] efficiency, byte[] scheduling)
        {
            if (logical == null || efficiency == null || scheduling == null || logical.Length == 0
                || efficiency.Length != logical.Length || scheduling.Length != logical.Length) return "";
            int[] order = new int[logical.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, delegate(int a, int b) { return logical[a].CompareTo(logical[b]); });
            var parts = new List<string>();
            int start = 0;
            for (int k = 1; k <= order.Length; k++)
            {
                bool split = k == order.Length
                    || efficiency[order[k]] != efficiency[order[start]]
                    || scheduling[order[k]] != scheduling[order[start]]
                    || logical[order[k]] != logical[order[k - 1]] + 1;
                if (!split) continue;
                int from = logical[order[start]], to = logical[order[k - 1]];
                parts.Add((from == to ? from.ToString() : from + "-" + to)
                    + ":" + efficiency[order[start]] + "/" + scheduling[order[start]]);
                start = k;
            }
            return string.Join(" ", parts.ToArray());
        }

        // Favored cores compare rating only within the highest efficiency class, P-cores rating above E-cores is an architecture difference, not favoring
        //   identical ratings within the class mean this machine has no favored cores, return 0
        internal static ulong FavoredMaskOf(int[] logical, byte[] efficiency, byte[] scheduling)
        {
            if (logical == null || efficiency == null || scheduling == null
                || logical.Length == 0 || efficiency.Length != logical.Length || scheduling.Length != logical.Length)
                return 0;
            byte topEfficiency = byte.MinValue;
            foreach (byte e in efficiency) if (e > topEfficiency) topEfficiency = e;
            byte min = byte.MaxValue, max = byte.MinValue;
            for (int i = 0; i < logical.Length; i++)
            {
                if (efficiency[i] != topEfficiency) continue;
                if (scheduling[i] < min) min = scheduling[i];
                if (scheduling[i] > max) max = scheduling[i];
            }
            if (max <= min) return 0;
            ulong mask = 0;
            for (int i = 0; i < logical.Length; i++)
                if (efficiency[i] == topEfficiency && scheduling[i] == max && logical[i] >= 0 && logical[i] < 64)
                    mask |= 1UL << logical[i];
            return mask;
        }

        private static void BuildCpuSetPolicies()
        {
            try { MultiGroup = GetActiveProcessorGroupCount() > 1; }
            catch { MultiGroup = Environment.ProcessorCount > 64; }
            int len;
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out len, IntPtr.Zero, 0);
            if (len <= 0) return;
            int capacity = len;
            IntPtr buf = Marshal.AllocHGlobal(capacity);
            try
            {
                if (!GetSystemCpuSetInformation(buf, capacity, out len, IntPtr.Zero, 0)
                    || len <= 0 || len > capacity) return;
                var rows = new List<CpuSetRec>();
                long pos = 0;
                while (pos + 20 <= len)
                {
                    IntPtr rec = (IntPtr)((long)buf + pos);
                    int size = Marshal.ReadInt32(rec, 0);
                    int type = Marshal.ReadInt32(rec, 4);
                    if (size <= 0 || pos + size > len) break;
                    if (type == 0 && size >= 20)
                    {
                        rows.Add(new CpuSetRec
                        {
                            Id = (uint)Marshal.ReadInt32(rec, 8),
                            Group = Marshal.ReadInt16(rec, 12),
                            Logical = Marshal.ReadByte(rec, 14),
                            Core = Marshal.ReadByte(rec, 15),
                            Efficiency = Marshal.ReadByte(rec, 18),
                            Scheduling = size >= 21 ? Marshal.ReadByte(rec, 20) : (byte)0
                        });
                    }
                    pos += size;
                }
                if (rows.Count == 0) return;
                cpuSetMaskById.Clear();
                bool cpuSetMapValid = true;
                foreach (CpuSetRec r in rows)
                {
                    if (r.Group != 0 || r.Logical >= 64) continue;
                    ulong bit = 1UL << r.Logical;
                    ulong old;
                    if (cpuSetMaskById.TryGetValue(r.Id, out old)
                        && old != bit)
                    {
                        cpuSetMapValid = false;
                        break;
                    }
                    cpuSetMaskById[r.Id] = bit;
                }
                if (!cpuSetMapValid) cpuSetMaskById.Clear();
                var all = new List<uint>();
                byte min = byte.MaxValue, max = byte.MinValue;
                foreach (CpuSetRec r in rows) { all.Add(r.Id); if (r.Efficiency < min) min = r.Efficiency; if (r.Efficiency > max) max = r.Efficiency; }
                allIds = all.ToArray();
                FavoredMask = ComputeFavoredMask(rows);

                var firstByCore = new Dictionary<string, CpuSetRec>();
                var maskByCore = new Dictionary<string, ulong>();
                foreach (CpuSetRec r in rows)
                {
                    string key = r.Group + ":" + r.Core;
                    CpuSetRec old;
                    if (!firstByCore.TryGetValue(key, out old) || r.Logical < old.Logical) firstByCore[key] = r;
                    if (r.Group == 0 && r.Logical < 64)
                    {
                        ulong mask;
                        maskByCore.TryGetValue(key, out mask);
                        maskByCore[key] = mask | (1UL << r.Logical);
                    }
                }
                foreach (ulong mask in maskByCore.Values) if (mask != 0) physicalCoreMasks.Add(mask);
                var bg = new List<uint>();
                var gamePartition = new List<uint>();
                var chosenBackgroundCores = new HashSet<string>(StringComparer.Ordinal);
                if (max > min)
                {
                    int perfLogical = 0;
                    foreach (CpuSetRec r in rows) if (r.Efficiency == max) perfLogical++;
                    if (perfLogical >= 8)
                        foreach (CpuSetRec r in firstByCore.Values)
                            if (r.Efficiency != max) chosenBackgroundCores.Add(r.Group + ":" + r.Core);
                }
                else if (!AsymCache && !MultiGroup)
                {
                    var descs = new List<CpuPartitionPolicy.CoreDesc>();
                    foreach (ulong coreMask in maskByCore.Values)
                    {
                        int dom = -1, die = -1; uint sz = 0;
                        for (int d = 0; d < cacheDomains.Count; d++)
                            if ((cacheDomains[d].Value & coreMask) != 0) { dom = d; sz = cacheDomains[d].Key; break; }
                        for (int d = 0; d < processorDieDomains.Count; d++)
                            if ((processorDieDomains[d] & coreMask) != 0) { die = d; break; }
                        descs.Add(new CpuPartitionPolicy.CoreDesc { Mask = coreMask, L3 = dom, Die = die, Eff = 0, L3Size = sz });
                    }
                    CpuPartitionPolicy.CorePlan plan = CpuPartitionPolicy.Decide(descs.ToArray(), AllMask);
                    if (plan.Partitioned)
                    {
                        uint[] planBg = CpuSetIdsFor(plan.Background);
                        uint[] planGame = CpuSetIdsFor(plan.Game);
                        if (planBg != null && planBg.Length > 0 && planGame != null && planGame.Length > 0)
                        {
                            backgroundIds = planBg;
                            partitionGameIds = planGame;
                            ThrottleMask = plan.Background;
                            StrictBoostMask = plan.Game;
                            InterruptMask = plan.Interrupt != 0 ? plan.Interrupt : plan.Background;
                            BoostMask = DefaultBoostMaskForPartition(plan.Tag, plan.Game, AllMask);
                            PartitionTag = plan.Tag;
                            GameDomainIndex = plan.GameDomain;
                            if (plan.AltGame != 0 && plan.AltBackground != 0)
                            {
                                uint[] planAltBg = CpuSetIdsFor(plan.AltBackground);
                                uint[] planAltGame = CpuSetIdsFor(plan.AltGame);
                                if (planAltBg != null && planAltBg.Length > 0
                                    && planAltGame != null && planAltGame.Length > 0)
                                {
                                    altBackgroundIds = planAltBg;
                                    altPartitionGameIds = planAltGame;
                                    AltThrottleMask = plan.AltBackground;
                                    AltStrictBoostMask = plan.AltGame;
                                    AltInterruptMask = plan.AltInterrupt != 0
                                        ? plan.AltInterrupt : plan.AltBackground;
                                    AltDomainIndex = plan.AltDomain;
                                }
                            }
                        }
                    }
                }
                else if (!AsymCache)
                {
                    var cores = new List<CpuSetRec>(firstByCore.Values);
                    cores.Sort(delegate(CpuSetRec a, CpuSetRec b)
                    {
                        int groupCompare = a.Group.CompareTo(b.Group);
                        return groupCompare != 0 ? groupCompare : a.Core.CompareTo(b.Core);
                    });
                    int reserve = CpuPartitionPolicy.BackgroundCoreCount(cores.Count);
                    for (int i = Math.Max(0, cores.Count - reserve); i < cores.Count; i++)
                        chosenBackgroundCores.Add(cores[i].Group + ":" + cores[i].Core);
                }

                ulong backgroundMask = 0, gamePartitionMask = 0;
                foreach (CpuSetRec r in rows)
                {
                    string key = r.Group + ":" + r.Core;
                    bool backgroundCore = chosenBackgroundCores.Contains(key);
                    if (backgroundCore) bg.Add(r.Id);
                    else if (chosenBackgroundCores.Count > 0 && (max == min || r.Efficiency == max))
                        gamePartition.Add(r.Id);
                    if (r.Group == 0 && r.Logical < 64)
                    {
                        if (backgroundCore) backgroundMask |= 1UL << r.Logical;
                        else if (chosenBackgroundCores.Count > 0 && (max == min || r.Efficiency == max))
                            gamePartitionMask |= 1UL << r.Logical;
                    }
                }

                if (AsymCache && !MultiGroup)
                {
                    uint[] cacheBg = CpuSetIdsFor(ThrottleMask);
                    uint[] cacheGame = CpuSetIdsFor(BigL3Mask);
                    if (cacheBg != null && cacheBg.Length > 0) backgroundIds = cacheBg;
                    if (cacheGame != null && cacheGame.Length > 0) partitionGameIds = cacheGame;
                    uint[] freqGame = CpuSetIdsFor(SmallL3Mask);
                    uint[] freqBg = CpuSetIdsFor(BigL3Mask);
                    if (freqGame != null && freqGame.Length > 0 && freqBg != null && freqBg.Length > 0)
                    {
                        altPartitionGameIds = freqGame;
                        altBackgroundIds = freqBg;
                        AltStrictBoostMask = SmallL3Mask;
                        AltThrottleMask = BigL3Mask;
                        AltInterruptMask = BigL3Mask;
                    }
                }
                else if (bg.Count > 0 && gamePartition.Count > 0)
                {
                    backgroundIds = bg.ToArray();
                    partitionGameIds = gamePartition.ToArray();
                }

                if (!MultiGroup && !AsymCache && backgroundMask != 0 && gamePartitionMask != 0)
                {
                    if (PartitionAgreesWithEfficiency(gamePartitionMask, backgroundMask))
                    {
                        ThrottleMask = backgroundMask;
                        StrictBoostMask = gamePartitionMask;
                    }
                    else
                    {
                        CpuSetPartitionRejected = true;
                        backgroundIds = null;
                        partitionGameIds = null;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static void Parse()
        {
            if (IntPtr.Size != 8) return;
            int len = 0;
            GetLogicalProcessorInformationEx(0xFFFF, IntPtr.Zero, ref len);
            if (len <= 0) return;
            int capacity = len;
            IntPtr buf = Marshal.AllocHGlobal(capacity);
            try
            {
                if (!GetLogicalProcessorInformationEx(0xFFFF, buf, ref len)
                    || len <= 0 || len > capacity) return;

                var classes = new Dictionary<int, ulong>();
                var coreMasks = new List<ulong>();
                var l3 = new List<KeyValuePair<uint, ulong>>();
                var dies = new List<ulong>();
                bool multiGroup = false;

                long pos = 0;
                while (pos + 8 <= len)
                {
                    IntPtr rec = (IntPtr)((long)buf + pos);
                    int rel = Marshal.ReadInt32(rec, 0);
                    int size = Marshal.ReadInt32(rec, 4);
                    if (size <= 0 || pos + size > len) break;
                    IntPtr u = (IntPtr)((long)rec + 8);

                    if (rel == 0)
                    {
                        if (!RecordFits(size, 30, 2)) { pos += size; continue; }
                        int cls = Marshal.ReadByte(u, 1);
                        int gc = Marshal.ReadInt16(u, 22);
                        if (!RecordArrayFits(size, 32, gc, 16)) { pos += size; continue; }
                        ulong coreMask = 0;
                        for (int i = 0; i < gc; i++)
                        {
                            IntPtr ga = (IntPtr)((long)u + 24 + i * 16);
                            if (Marshal.ReadInt16(ga, 8) != 0) { multiGroup = true; continue; }
                            ulong bits = (ulong)Marshal.ReadInt64(ga, 0);
                            ulong cur;
                            classes.TryGetValue(cls, out cur);
                            classes[cls] = cur | bits;
                            coreMask |= bits;
                        }
                        if (coreMask != 0) coreMasks.Add(coreMask);
                    }
                    else if (rel == 5)
                    {
                        if (!RecordFits(size, 30, 2)) { pos += size; continue; }
                        int gc = Marshal.ReadInt16(u, 22);
                        if (!RecordArrayFits(size, 32, gc, 16)) { pos += size; continue; }
                        ulong m = 0;
                        for (int i = 0; i < gc; i++)
                        {
                            IntPtr ga = (IntPtr)((long)u + 24 + i * 16);
                            if (Marshal.ReadInt16(ga, 8) != 0) { multiGroup = true; continue; }
                            m |= (ulong)Marshal.ReadInt64(ga, 0);
                        }
                        if (m != 0) dies.Add(m);
                    }
                    else if (rel == 2)
                    {
                        if (!RecordFits(size, 8, 1)) { pos += size; continue; }
                        if (Marshal.ReadByte(u, 0) == 3)
                        {
                            if (!RecordFits(size, 38, 2)) { pos += size; continue; }
                            uint csize = (uint)Marshal.ReadInt32(u, 4);
                            int gc = Marshal.ReadInt16(u, 30);
                            ulong m = 0;
                            if (gc == 0)
                            {
                                if (!RecordFits(size, 40, 16)) { pos += size; continue; }
                                IntPtr ga = (IntPtr)((long)u + 32);
                                if (Marshal.ReadInt16(ga, 8) != 0) multiGroup = true;
                                else m = (ulong)Marshal.ReadInt64(ga, 0);
                            }
                            else
                            {
                                if (!RecordArrayFits(size, 40, gc, 16)) { pos += size; continue; }
                                for (int i = 0; i < gc; i++)
                                {
                                    IntPtr ga = (IntPtr)((long)u + 32 + i * 16);
                                    if (Marshal.ReadInt16(ga, 8) != 0) { multiGroup = true; continue; }
                                    m |= (ulong)Marshal.ReadInt64(ga, 0);
                                }
                            }
                            if (m != 0) l3.Add(new KeyValuePair<uint, ulong>(csize, m));
                        }
                    }
                    pos += size;
                }

                if (multiGroup) return;
                parsedCoreMasks = coreMasks;
                cacheDomains = l3;
                processorDieDomains = dies;

                if (classes.Count >= 2)
                {
                    int max = int.MinValue, min = int.MaxValue;
                    foreach (var kv in classes)
                    {
                        if (kv.Key > max) max = kv.Key;
                        if (kv.Key < min) min = kv.Key;
                    }
                    ulong perf = 0, eff = 0, lowest = 0;
                    foreach (var kv in classes)
                    {
                        if (kv.Key == max) perf |= kv.Value;
                        else { eff |= kv.Value; if (kv.Key == min) lowest |= kv.Value; }
                    }
                    if (perf != 0 && eff != 0)
                    {
                        PerfMask = perf; EffMask = eff; Hybrid = true;
                        // With two classes lowest equals eff, excluding it would empty the placement pool, only honored with three or more classes
                        if (classes.Count >= 3) LowPowerEffMask = lowest;
                    }
                }

                if (!Hybrid && l3.Count >= 2)
                {
                    uint maxSz = 0, minSz = uint.MaxValue;
                    foreach (var kv in l3) { if (kv.Key > maxSz) maxSz = kv.Key; if (kv.Key < minSz) minSz = kv.Key; }
                    if (maxSz > minSz)
                    {
                        ulong big = 0, small = 0;
                        foreach (var kv in l3) { if (kv.Key == maxSz) big |= kv.Value; else small |= kv.Value; }
                        if (big != 0 && small != 0) { BigL3Mask = big; SmallL3Mask = small; AsymCache = true; }
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
