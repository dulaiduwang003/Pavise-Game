// @author bdth 2074055628@qq.com
// 文件用途 识别处理器拓扑并计算游戏和后台核心分区
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
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
        // 三档混合架构里最低的那一档 Core Ultra 的 LP-E 核就是它 SoC tile 上 时钟最低
        //   仍然留在 EffMask 里 后台压制往那儿赶是对的
        //   但中断落点必须排除它 1.8.1.0 下架 USB 与硬盘中断亲和就是栽在把中断投到低频能效核
        //   只有三档以上才有值 两档时最低档就是 EffMask 本身 整体排除会把落点池清空
        public static ulong LowPowerEffMask;

        public static ulong AllMask, ThrottleMask, BoostMask, StrictBoostMask, InterruptMask;
        public static ulong AltStrictBoostMask, AltThrottleMask, AltInterruptMask;
        public static int GameDomainIndex = -1, AltDomainIndex = -1;
        public static bool AltDomainActive;
        public static string PartitionTag = "";
        private static List<KeyValuePair<uint, ulong>> cacheDomains = new List<KeyValuePair<uint, ulong>>();
        private static List<ulong> processorDieDomains = new List<ulong>();
        private static readonly Dictionary<uint, ulong> cpuSetMaskById =
            new Dictionary<uint, ulong>();

        static CpuTopology()
        {
            try { Parse(); }
            catch { Hybrid = false; AsymCache = false; }
            DeriveMasks();
            try { BuildCpuSetPolicies(); } catch { }
            ValidateMasks();
        }

        internal static bool PartitionAgreesWithEfficiency(ulong gamePartition, ulong background)
        {
            if (!Hybrid || PerfMask == 0 || EffMask == 0) return true;
            if ((gamePartition & EffMask) != 0) return false;
            if ((background & PerfMask) != 0) return false;
            return true;
        }

        public static bool CpuSetPartitionRejected;
        public static bool StrictMaskUnsafe;

        private static void DeriveMasks()
        {
            int nc = Environment.ProcessorCount;
            AllMask = nc >= 64 ? ulong.MaxValue : (1UL << nc) - 1UL;
            if (Hybrid) { ThrottleMask = EffMask; BoostMask = AllMask; }
            else if (AsymCache) { ThrottleMask = SmallL3Mask; BoostMask = AllMask; }

            else { ThrottleMask = nc >= 2 && nc <= 64 ? 3UL << (nc - 2) : (nc >= 2 ? 0UL : 1UL); BoostMask = AllMask; }
            StrictBoostMask = CpuPartitionPolicy.StrictMask(AllMask, ThrottleMask,
                Hybrid ? PerfMask : 0, AsymCache ? BigL3Mask : 0);
            InterruptMask = DeriveInterruptMask(Hybrid, PerfMask, ThrottleMask,
                LowPowerEffMask, ParsedPhysicalIn(PerfMask));
        }

        // P 核够多就把中断放 P 核 靠近渲染线程 不够就让开 别抢游戏仅有的那几个 P 核
        //   阈值必须数物理核 老写法数的是逻辑核 那是 P 核必然带超线程的年代写的
        //   Arrow Lake 取消超线程后 6 个物理 P 核只剩 6 个逻辑位 同样的机器判定会翻面
        //   实测过的对照 Raptor 6P 带 HT 是 12 个逻辑位走 P 核 Arrow-H 6P 无 HT 是 6 个直接退到能效核
        internal const int MinPerfPhysicalForInterrupts = 4;

        internal static ulong DeriveInterruptMask(bool hybrid, ulong perfMask, ulong throttle,
            ulong lowPower, int perfPhysicalCores)
        {
            if (!hybrid || perfMask == 0) return throttle;
            // 退让时也不许落到最低一档能效核 那是全机器时钟最低的核
            ulong fallback = throttle & ~lowPower;
            if (fallback == 0) fallback = throttle;
            ulong pool = perfPhysicalCores >= MinPerfPhysicalForInterrupts ? perfMask
                : fallback != 0 ? fallback : perfMask;
            ulong top = TopBits(pool, 2);
            return top != 0 ? top : throttle;
        }

        internal static ulong TopBits(ulong pool, int n)
        {
            ulong top = 0;
            int taken = 0;
            for (int i = 63; i >= 0 && taken < n; i--)
            {
                ulong bit = 1UL << i;
                if ((pool & bit) == 0) continue;
                top |= bit;
                taken++;
            }
            return top;
        }

        internal static ulong SafeStrictMask(ulong strict, ulong throttle, ulong all, ulong eff, bool hybrid)
        {
            if (strict == 0 || (strict & all) == 0) return all;
            if ((strict & throttle) != 0) return all;
            if (hybrid && eff != 0 && (strict & eff) != 0) return all;
            return strict;
        }

        private static void ValidateMasks()
        {
            ulong safe = SafeStrictMask(StrictBoostMask, ThrottleMask, AllMask, EffMask, Hybrid);
            if (safe != StrictBoostMask)
            {
                StrictMaskUnsafe = true;
                StrictBoostMask = safe;
                partitionGameIds = null;
                backgroundIds = null;
            }
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
        private static uint[] altBackgroundIds;
        private static uint[] altPartitionGameIds;
        private static bool domainPreferenceApplied;
        private static readonly List<ulong> physicalCoreMasks = new List<ulong>();

        // Parse 阶段就记下的物理核掩码 每条 RelationProcessorCore 记录就是一个物理核
        //   physicalCoreMasks 要等 BuildCpuSetPolicies 才填 而 DeriveMasks 跑在它前面
        //   中断落点的阈值要数物理 P 核 只能用这份 否则数到的永远是 0
        private static List<ulong> parsedCoreMasks = new List<ulong>();

        internal static int PhysicalCountIn(List<ulong> cores, ulong logicalMask)
        {
            int n = 0;
            if (cores == null) return 0;
            foreach (ulong core in cores) if ((core & logicalMask) != 0) n++;
            return n;
        }

        internal static int ParsedPhysicalIn(ulong logicalMask)
        {
            return PhysicalCountIn(parsedCoreMasks, logicalMask);
        }

        public static uint[] BackgroundCpuSetIds()
        {
            return backgroundIds;
        }

        public static bool HasSafeBackgroundPartition()
        {
            return backgroundIds != null && backgroundIds.Length > 0
                && partitionGameIds != null && partitionGameIds.Length > 0;
        }

        public static bool HasAltPartition()
        {
            return AltStrictBoostMask != 0 && AltThrottleMask != 0
                && altBackgroundIds != null && altBackgroundIds.Length > 0
                && altPartitionGameIds != null && altPartitionGameIds.Length > 0
                && HasSafeBackgroundPartition();
        }

        public static uint[] InactiveBackgroundCpuSetIds()
        {
            return altBackgroundIds;
        }

        public static ulong InactiveThrottleMask
        {
            get { return AltThrottleMask; }
        }

        public static bool DomainPreferenceApplied
        {
            get { return domainPreferenceApplied; }
        }

        public static void ApplyDomainPreference(bool alt)
        {
            if (domainPreferenceApplied) return;
            domainPreferenceApplied = true;
            if (alt) SwapDomains();
        }

        public static bool SwapDomains()
        {
            if (!HasAltPartition()) return false;
            ulong m;
            m = ThrottleMask; ThrottleMask = AltThrottleMask; AltThrottleMask = m;
            m = StrictBoostMask; StrictBoostMask = AltStrictBoostMask; AltStrictBoostMask = m;
            m = InterruptMask; InterruptMask = AltInterruptMask; AltInterruptMask = m;
            int d = GameDomainIndex; GameDomainIndex = AltDomainIndex; AltDomainIndex = d;
            uint[] ids = backgroundIds; backgroundIds = altBackgroundIds; altBackgroundIds = ids;
            ids = partitionGameIds; partitionGameIds = altPartitionGameIds; altPartitionGameIds = ids;
            AltDomainActive = !AltDomainActive;
            squeezeCache = null;
            ValidateMasks();
            return true;
        }

        public static uint[] AdaptiveGameCpuSetIds(bool competitive)
        {
            if (!competitive) return MultiGroup ? allIds : BoostCpuSetIds();
            return partitionGameIds != null && partitionGameIds.Length > 0
                ? partitionGameIds
                : (MultiGroup ? allIds : BoostCpuSetIds());
        }

        public const int MinCustomCores = 2;

        public static int PhysicalCoresIn(ulong logicalMask)
        {
            int n = 0;
            foreach (ulong core in physicalCoreMasks) if ((core & logicalMask) != 0) n++;
            return n;
        }

        public static ulong OnePerPhysicalIn(ulong logicalMask)
        {
            ulong m = 0;
            foreach (ulong core in physicalCoreMasks)
            {
                ulong hit = core & logicalMask;
                if (hit != 0) m |= hit & (ulong)(-(long)hit);
            }
            return m & AllMask;
        }

        public static ulong PhysicalOnlyMask()
        {
            ulong m = 0;
            foreach (ulong core in physicalCoreMasks) m |= core & (ulong)(-(long)core);
            return m & AllMask;
        }

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
                        // 两档时 lowest 就等于 eff 排除它等于清空落点池 只有三档以上才认
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
