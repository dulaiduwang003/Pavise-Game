// @author bdth 2074055628@qq.com
// 文件用途 识别处理器拓扑并计算游戏和后台核心分区

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AegisApp
{
    internal static class CpuTopology
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(IntPtr buffer, int length, out int returned, IntPtr process, uint flags);
        [DllImport("kernel32.dll")]
        private static extern ushort GetActiveProcessorGroupCount();

        public static bool Hybrid;
        public static bool AsymCache;
        public static bool MultiGroup;
        public static ulong PerfMask, EffMask, BigL3Mask, SmallL3Mask;


        public static ulong AllMask, ThrottleMask, BoostMask, StrictBoostMask;

        static CpuTopology()
        {
            try { Parse(); }
            catch { Hybrid = false; AsymCache = false; }
            DeriveMasks();
            try { BuildCpuSetPolicies(); } catch { }
        }


        private static void DeriveMasks()
        {
            int nc = Environment.ProcessorCount;
            AllMask = nc >= 64 ? ulong.MaxValue : (1UL << nc) - 1UL;
            if (Hybrid) { ThrottleMask = EffMask; BoostMask = AllMask; }
            else if (AsymCache) { ThrottleMask = SmallL3Mask; BoostMask = BigL3Mask; }
            // C# 会把 ulong 的移位量按 6 bit 取模，nc>64 时 3UL<<70 会变成 3UL<<6，
            // 把 6/7 号核当成"后台核"。其余移位点都有 <64 保护，补上这一处。
            else { ThrottleMask = nc >= 2 && nc <= 64 ? 3UL << (nc - 2) : (nc >= 2 ? 0UL : 1UL); BoostMask = AllMask; }
            StrictBoostMask = CpuPartitionPolicy.StrictMask(AllMask, ThrottleMask,
                Hybrid ? PerfMask : 0, AsymCache ? BigL3Mask : 0);
        }

        private static uint[] boostIds;
        private static bool boostIdsDone;

        public static uint[] BoostCpuSetIds()
        {
            if (boostIdsDone) return boostIds;
            try { if (BoostMask != AllMask) boostIds = CpuSetIdsFor(BoostMask); }
            catch { boostIds = null; }
            boostIdsDone = true;
            return boostIds;
        }

        public static uint[] CpuSetIdsFor(ulong mask)
        {
            int len;
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out len, IntPtr.Zero, 0);
            if (len <= 0) return null;
            int capacity = len;
            IntPtr buf = Marshal.AllocHGlobal(capacity);
            try
            {
                if (!GetSystemCpuSetInformation(buf, capacity, out len, IntPtr.Zero, 0)
                    || len <= 0 || len > capacity) return null;
                var ids = new List<uint>();
                long pos = 0;
                while (pos + 8 <= len)
                {
                    IntPtr rec = (IntPtr)((long)buf + pos);
                    int size = Marshal.ReadInt32(rec, 0);
                    int type = Marshal.ReadInt32(rec, 4);
                    if (size <= 0 || pos + size > len) break;
                    if (type == 0 && size >= 16)
                    {
                        uint id = (uint)Marshal.ReadInt32(rec, 8);
                        short group = Marshal.ReadInt16(rec, 12);
                        byte lp = Marshal.ReadByte(rec, 14);
                        if (group == 0 && lp < 64 && ((mask >> lp) & 1UL) != 0) ids.Add(id);
                    }
                    pos += size;
                }
                return ids.Count > 0 ? ids.ToArray() : null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private sealed class CpuSetRec
        {
            public uint Id;
            public short Group;
            public byte Logical;
            public byte Core;
            public byte Efficiency;
        }

        private static bool RecordFits(int recordSize, int offset, int bytes)
        {
            return recordSize >= 0 && offset >= 0 && bytes >= 0
                && offset <= recordSize && bytes <= recordSize - offset;
        }

        private static bool RecordArrayFits(int recordSize, int offset, int count, int stride)
        {
            return count > 0 && count <= 64 && stride > 0
                && count <= int.MaxValue / stride
                && RecordFits(recordSize, offset, count * stride);
        }

        private static uint[] backgroundIds;
        private static uint[] allIds;
        private static uint[] partitionGameIds;
        private static readonly Dictionary<uint, int> cpuSetLogical = new Dictionary<uint, int>();
        private static readonly List<ulong> physicalCoreMasks = new List<ulong>();

        public static uint[] BackgroundCpuSetIds()
        {
            return backgroundIds;
        }

        public static bool HasSafeBackgroundPartition()
        {
            return backgroundIds != null && backgroundIds.Length > 0
                && partitionGameIds != null && partitionGameIds.Length > 0;
        }

        public static uint[] AdaptiveGameCpuSetIds(bool competitive)
        {
            return AdaptiveGameCpuSetIds(competitive, 0);
        }

        public static uint[] AdaptiveGameCpuSetIds(bool competitive, ulong avoidMask)
        {
            uint[] ids;
            if (!competitive) ids = MultiGroup ? allIds : BoostCpuSetIds();
            else ids = partitionGameIds != null && partitionGameIds.Length > 0
                ? partitionGameIds
                : (MultiGroup ? allIds : BoostCpuSetIds());
            if (ids == null || avoidMask == 0) return ids;
            var filtered = new List<uint>();
            foreach (uint id in ids)
            {
                int logical;
                if (!cpuSetLogical.TryGetValue(id, out logical) || logical < 0 || logical >= 64 || ((avoidMask >> logical) & 1UL) == 0) filtered.Add(id);
            }
            return filtered.Count > 0 ? filtered.ToArray() : ids;
        }

        public static ulong ExpandPhysicalCoreMask(ulong logicalMask)
        {
            foreach (ulong core in physicalCoreMasks) if ((core & logicalMask) != 0) return core;
            return logicalMask;
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
                            Efficiency = Marshal.ReadByte(rec, 18)
                        });
                        short group = Marshal.ReadInt16(rec, 12);
                        byte logical = Marshal.ReadByte(rec, 14);
                        uint cpuSetId = (uint)Marshal.ReadInt32(rec, 8);
                        cpuSetLogical[cpuSetId] = group == 0 ? logical : -1;
                    }
                    pos += size;
                }
                if (rows.Count == 0) return;
                var all = new List<uint>();
                byte min = byte.MaxValue, max = byte.MinValue;
                foreach (CpuSetRec r in rows) { all.Add(r.Id); if (r.Efficiency < min) min = r.Efficiency; if (r.Efficiency > max) max = r.Efficiency; }
                allIds = all.ToArray();

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
                    foreach (CpuSetRec r in firstByCore.Values)
                        if (r.Efficiency != max) chosenBackgroundCores.Add(r.Group + ":" + r.Core);
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
                }
                else if (bg.Count > 0 && gamePartition.Count > 0)
                {
                    backgroundIds = bg.ToArray();
                    partitionGameIds = gamePartition.ToArray();
                }

                if (!MultiGroup && !AsymCache && backgroundMask != 0 && gamePartitionMask != 0)
                {
                    ThrottleMask = backgroundMask;
                    StrictBoostMask = gamePartitionMask;
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
                var l3 = new List<KeyValuePair<uint, ulong>>();
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
                        for (int i = 0; i < gc; i++)
                        {
                            IntPtr ga = (IntPtr)((long)u + 24 + i * 16);
                            if (Marshal.ReadInt16(ga, 8) != 0) { multiGroup = true; continue; }
                            ulong cur;
                            classes.TryGetValue(cls, out cur);
                            classes[cls] = cur | (ulong)Marshal.ReadInt64(ga, 0);
                        }
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

                if (classes.Count >= 2)
                {
                    int max = int.MinValue;
                    foreach (var kv in classes) if (kv.Key > max) max = kv.Key;
                    ulong perf = 0, eff = 0;
                    foreach (var kv in classes) { if (kv.Key == max) perf |= kv.Value; else eff |= kv.Value; }
                    if (perf != 0 && eff != 0) { PerfMask = perf; EffMask = eff; Hybrid = true; }
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
