using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 这些记录来自 Windows 拓扑，不根据型号猜测。L3 域不直接称作 CCD，因为共享缓存不能证明芯片边界。

    internal sealed class AntiCheatAffinityCore
    {
        internal ushort Group;
        internal ulong Mask;
        internal byte Efficiency;
        internal byte Scheduling;
        internal bool Parked;
        internal bool Allocated;
        internal bool Favored { get; set; }
        internal int L3Id = -1;
        internal uint L3Bytes;
    }

    internal sealed class AntiCheatAffinitySnapshot
    {
        internal bool Valid;
        internal string Reason = "topology-unavailable";
        internal bool CpuSetMetadata;
        internal AntiCheatAffinityCore[] Cores = new AntiCheatAffinityCore[0];
    }

    internal sealed class AntiCheatAffinityChoice
    {
        internal ulong Mask;
        internal string Reason = "no-safe-core-pool";
        internal int PhysicalCount;
        internal string Kind = "none";
        internal bool Applicable { get { return Mask != 0 && PhysicalCount >= 1; } }
    }

    internal static class AntiCheatAffinityTopology
    {
        private sealed class CacheDomain
        {
            internal uint Bytes;
            internal readonly SortedDictionary<ushort, ulong> Masks = new SortedDictionary<ushort, ulong>();
        }

        private sealed class CpuState
        {
            internal byte Efficiency, Scheduling, Core;
            internal bool Parked, Allocated;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationship, [Out] byte[] buffer, ref int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation([Out] byte[] buffer, int length, out int returned, IntPtr process, uint flags);

        internal static AntiCheatAffinitySnapshot Capture()
        {
            if (IntPtr.Size != 8) return Invalid("64-bit-topology-required");
            try
            {
                byte[] topology = ReadTopology();
                byte[] cpuSets = ReadCpuState();
                return Decode(topology, cpuSets);
            }
            catch (Exception ex)
            {
                if (!(ex is EntryPointNotFoundException) && !(ex is DllNotFoundException)
                    && !(ex is InvalidOperationException) && !(ex is ArgumentException)
                    && !(ex is OverflowException)) throw;
                return Invalid("topology-read-failed");
            }
        }

        private static byte[] ReadTopology()
        {
            int length = 0;
            GetLogicalProcessorInformationEx(65535, null, ref length);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (length < 48 || length > 4194304) return null;
                byte[] data = new byte[length];
                int returned = data.Length;
                if (GetLogicalProcessorInformationEx(65535, data, ref returned))
                {
                    if (returned <= 0 || returned > data.Length) return null;
                    if (returned != data.Length) Array.Resize(ref data, returned);
                    return data;
                }
                length = returned;
            }
            return null;
        }

        private static byte[] ReadCpuState()
        {
            int length;
            GetSystemCpuSetInformation(null, 0, out length, IntPtr.Zero, 0);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (length < 32 || length > 4194304) return null;
                byte[] data = new byte[length];
                int returned;
                if (GetSystemCpuSetInformation(data, data.Length, out returned, IntPtr.Zero, 0))
                {
                    if (returned <= 0 || returned > data.Length) return null;
                    if (returned != data.Length) Array.Resize(ref data, returned);
                    return data;
                }
                length = returned;
            }
            return null;
        }

        private static AntiCheatAffinitySnapshot Invalid(string reason)
        {
            return new AntiCheatAffinitySnapshot { Reason = reason };
        }

        private static string CpuKey(ushort group, int logical)
        {
            return group.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
                + logical.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static AntiCheatAffinitySnapshot Decode(byte[] topology, byte[] cpuSets)
        {
            if (topology == null || topology.Length < 48) return Invalid("topology-unavailable");
            if (cpuSets == null || cpuSets.Length < 32) return Invalid("cpu-state-unavailable");
            List<AntiCheatAffinityCore> cores = new List<AntiCheatAffinityCore>();
            List<CacheDomain> caches = new List<CacheDomain>();
            SortedDictionary<ushort, ulong> active = new SortedDictionary<ushort, ulong>();
            bool groupRecord = false;
            for (int at = 0; at < topology.Length; )
            {
                if (topology.Length - at < 8) return Invalid("truncated-topology");
                int type = BitConverter.ToInt32(topology, at);
                uint sizeValue = BitConverter.ToUInt32(topology, at + 4);
                if (sizeValue < 8 || sizeValue > topology.Length - at) return Invalid("invalid-topology-record");
                int size = (int)sizeValue;
                if (type == 0)
                {
                    if (size < 48 || BitConverter.ToUInt16(topology, at + 30) != 1)
                        return Invalid("invalid-core-record");
                    ulong mask = BitConverter.ToUInt64(topology, at + 32);
                    if (mask == 0) return Invalid("empty-core");
                    cores.Add(new AntiCheatAffinityCore {
                        Group = BitConverter.ToUInt16(topology, at + 40),
                        Mask = mask, Efficiency = topology[at + 9]
                    });
                }
                else if (type == 2)
                {
                    if (size < 56) return Invalid("invalid-cache-record");
                    if (topology[at + 8] == 3 && BitConverter.ToInt32(topology, at + 16) == 0)
                    {
                        int count = BitConverter.ToUInt16(topology, at + 38);
                        // 旧版 Windows 的多组缓存记录尚未定义此 WORD，当时它保留为零，只有一个 GroupMask。

                        if (count == 0) count = 1;
                        if (count > (size - 40) / 16) return Invalid("invalid-cache-groups");
                        CacheDomain cache = new CacheDomain { Bytes = BitConverter.ToUInt32(topology, at + 12) };
                        if (cache.Bytes == 0) return Invalid("empty-cache-size");
                        for (int n = 0; n < count; n++)
                        {
                            ulong mask = BitConverter.ToUInt64(topology, at + 40 + n * 16);
                            ushort group = BitConverter.ToUInt16(topology, at + 48 + n * 16);
                            if (mask == 0 || cache.Masks.ContainsKey(group)) return Invalid("invalid-cache-mask");
                            cache.Masks.Add(group, mask);
                        }
                        caches.Add(cache);
                    }
                }
                else if (type == 4)
                {
                    if (groupRecord || size < 80) return Invalid("invalid-group-record");
                    groupRecord = true;
                    int count = BitConverter.ToUInt16(topology, at + 10);
                    int max = BitConverter.ToUInt16(topology, at + 8);
                    if (count == 0 || count > max || count > (size - 32) / 48)
                        return Invalid("invalid-group-count");
                    for (int n = 0; n < count; n++)
                    {
                        int offset = at + 32 + n * 48;
                        ulong mask = BitConverter.ToUInt64(topology, offset + 40);
                        if (mask == 0 || CountBits(mask) != topology[offset + 1]
                            || topology[offset + 1] > topology[offset] || topology[offset] > 64)
                            return Invalid("invalid-active-group");
                        active.Add((ushort)n, mask);
                    }
                }
                at += size;
            }
            if (!groupRecord || cores.Count == 0) return Invalid("incomplete-topology");
            cores.Sort(CompareCores);
            SortedDictionary<ushort, ulong> covered = new SortedDictionary<ushort, ulong>();
            foreach (AntiCheatAffinityCore core in cores)
            {
                ulong full, seen;
                if (!active.TryGetValue(core.Group, out full) || (core.Mask & ~full) != 0)
                    return Invalid("core-outside-active-group");
                covered.TryGetValue(core.Group, out seen);
                if ((seen & core.Mask) != 0) return Invalid("overlapping-physical-cores");
                covered[core.Group] = seen | core.Mask;
            }
            foreach (KeyValuePair<ushort, ulong> group in active)
            {
                ulong mask;
                if (!covered.TryGetValue(group.Key, out mask) || mask != group.Value)
                    return Invalid("incomplete-core-coverage");
            }
            caches.Sort(CompareCaches);
            for (int i = 0; i < caches.Count; i++)
            {
                foreach (KeyValuePair<ushort, ulong> part in caches[i].Masks)
                {
                    ulong full;
                    if (!active.TryGetValue(part.Key, out full) || (part.Value & ~full) != 0)
                        return Invalid("cache-outside-active-group");
                }
                foreach (AntiCheatAffinityCore core in cores)
                {
                    ulong mask;
                    if (!caches[i].Masks.TryGetValue(core.Group, out mask) || (core.Mask & mask) == 0) continue;
                    if ((core.Mask & mask) != core.Mask || core.L3Id != -1)
                        return Invalid("contradictory-l3-domains");
                    core.L3Id = i;
                    core.L3Bytes = caches[i].Bytes;
                }
            }
            Dictionary<string, CpuState> states = new Dictionary<string, CpuState>(StringComparer.Ordinal);
            HashSet<uint> ids = new HashSet<uint>();
            for (int at = 0; at < cpuSets.Length; )
            {
                if (cpuSets.Length - at < 8) return Invalid("truncated-cpu-state");
                uint sizeValue = BitConverter.ToUInt32(cpuSets, at);
                if (sizeValue < 8 || sizeValue > cpuSets.Length - at) return Invalid("invalid-cpu-state-record");
                int size = (int)sizeValue;
                if (BitConverter.ToInt32(cpuSets, at + 4) == 0)
                {
                    if (size < 32 || cpuSets[at + 14] >= 64) return Invalid("invalid-cpu-state");
                    ushort group = BitConverter.ToUInt16(cpuSets, at + 12);
                    byte logical = cpuSets[at + 14];
                    ulong full;
                    string key = CpuKey(group, logical);
                    if (!active.TryGetValue(group, out full) || (full & (1UL << logical)) == 0
                        || states.ContainsKey(key) || !ids.Add(BitConverter.ToUInt32(cpuSets, at + 8)))
                        return Invalid("contradictory-cpu-state");
                    states.Add(key, new CpuState {
                        Core = cpuSets[at + 15], Efficiency = cpuSets[at + 18], Scheduling = cpuSets[at + 20],
                        Parked = (cpuSets[at + 19] & 1) != 0,
                        Allocated = (cpuSets[at + 19] & 10) != 0
                    });
                }
                at += size;
            }
            Dictionary<string, ulong> coreIds = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (AntiCheatAffinityCore core in cores)
            {
                int coreId = -1;
                for (int logical = 0; logical < 64; logical++)
                {
                    if ((core.Mask & (1UL << logical)) == 0) continue;
                    CpuState state;
                    if (!states.TryGetValue(CpuKey(core.Group, logical), out state)) return Invalid("incomplete-cpu-state");
                    if (state.Efficiency != core.Efficiency || (coreId >= 0 && coreId != state.Core))
                        return Invalid("contradictory-core-metadata");
                    coreId = state.Core;
                    core.Parked |= state.Parked;
                    core.Allocated |= state.Allocated;
                    // 部分系统只给主要 SMT 线程提供等级，不能据此把同一物理核拆成两个性能层级。

                    core.Scheduling = Math.Max(core.Scheduling, state.Scheduling);
                }
                string identity = CpuKey(core.Group, coreId);
                if (coreIds.ContainsKey(identity)) return Invalid("contradictory-core-index");
                coreIds.Add(identity, core.Mask);
            }
            return new AntiCheatAffinitySnapshot { Valid = true, Reason = "ok", CpuSetMetadata = true, Cores = cores.ToArray() };
        }

        private static int CompareCores(AntiCheatAffinityCore a, AntiCheatAffinityCore b)
        {
            int order = a.Group.CompareTo(b.Group);
            return order != 0 ? order : a.Mask.CompareTo(b.Mask);
        }

        private static int CompareCaches(CacheDomain a, CacheDomain b)
        {
            // 接口不提供持久缓存编号；按固定顺序排列，使 Windows 改变枚举顺序时快照仍保持稳定。

            IEnumerator<KeyValuePair<ushort, ulong>> x = a.Masks.GetEnumerator();
            IEnumerator<KeyValuePair<ushort, ulong>> y = b.Masks.GetEnumerator();
            while (true)
            {
                bool nx = x.MoveNext(), ny = y.MoveNext();
                if (!nx || !ny) return nx == ny ? a.Bytes.CompareTo(b.Bytes) : nx ? 1 : -1;
                int order = x.Current.Key.CompareTo(y.Current.Key);
                if (order == 0) order = x.Current.Value.CompareTo(y.Current.Value);
                if (order != 0) return order;
            }
        }

        private static int CountBits(ulong value)
        {
            int count = 0;
            while (value != 0) { value &= value - 1; count++; }
            return count;
        }

        private static AntiCheatAffinityChoice Skip(string reason)
        {
            return new AntiCheatAffinityChoice { Reason = reason };
        }

        internal static AntiCheatAffinityChoice Select(AntiCheatAffinitySnapshot snapshot, ushort group,
            ulong originalAllowedMask, ulong protectedMask, ulong gameDomainMask, int maximumPhysicalCores = int.MaxValue)
        {
            if (maximumPhysicalCores < 1) return Skip("invalid-core-limit");
            if (snapshot == null || !snapshot.Valid || snapshot.Cores == null)
                return Skip(snapshot == null ? "topology-unavailable" : snapshot.Reason);
            if (!snapshot.CpuSetMetadata) return Skip("cpu-state-unavailable");
            if (originalAllowedMask == 0) return Skip("empty-original-affinity");
            List<AntiCheatAffinityCore> local = new List<AntiCheatAffinityCore>();
            ulong full = 0;
            SortedSet<byte> tiers = new SortedSet<byte>();
            foreach (AntiCheatAffinityCore core in snapshot.Cores)
            {
                if (core == null || core.Mask == 0) return Skip("invalid-core-topology");
                if (core.Group != group) continue;
                if ((full & core.Mask) != 0) return Skip("overlapping-physical-cores");
                full |= core.Mask;
                local.Add(core);
                tiers.Add(core.Efficiency);
            }
            if (local.Count == 0 || (originalAllowedMask & ~full) != 0 || ((protectedMask | gameDomainMask) & ~full) != 0)
                return Skip("affinity-outside-group");
            List<AntiCheatAffinityCore> candidates = new List<AntiCheatAffinityCore>();
            foreach (AntiCheatAffinityCore core in local)
            {
                // 允许或受保护掩码只覆盖部分 SMT 线程时，不代表可以使用其同核线程；仅接纳完整且未受保护的物理核。

                if ((core.Mask & originalAllowedMask) != core.Mask || (core.Mask & protectedMask) != 0
                    || core.Parked || core.Allocated || core.Favored) continue;
                candidates.Add(core);
            }
            int minimumCores = maximumPhysicalCores == 1 ? 1 : 2;
            if (candidates.Count < minimumCores) return Skip("insufficient-unreserved-cores");
            if (tiers.Count > 1)
            {
                List<byte> orderedTiers = new List<byte>(tiers);
                int performanceCores = 0;
                foreach (AntiCheatAffinityCore core in local)
                    if (core.Efficiency == orderedTiers[orderedTiers.Count - 1])
                        performanceCores++;
                if (performanceCores < 2) return Skip("insufficient-game-core-capacity");
                // 优先选择较高的次级类别；最低逻辑处理器类别只有能提供两个完整物理核时才作为备用。

                for (int tier = orderedTiers.Count - 2; tier >= 0; tier--)
                {
                    List<AntiCheatAffinityCore> pool = new List<AntiCheatAffinityCore>();
                    foreach (AntiCheatAffinityCore core in candidates)
                        if (core.Efficiency == orderedTiers[tier]) pool.Add(core);
                    // EfficiencyClass 相同时，使用 SchedulingClass 区分 E 核与低功耗 E 核。
                    SortedSet<byte> secondaryClasses = new SortedSet<byte>();
                    foreach (AntiCheatAffinityCore core in pool) secondaryClasses.Add(core.Scheduling);
                    List<byte> schedulingClasses = new List<byte>(secondaryClasses);
                    for (int rank = schedulingClasses.Count - 1; rank >= 0; rank--)
                    {
                        List<AntiCheatAffinityCore> classPool = new List<AntiCheatAffinityCore>();
                        foreach (AntiCheatAffinityCore core in pool)
                            if (core.Scheduling == schedulingClasses[rank]) classPool.Add(core);
                        if (classPool.Count >= minimumCores && local.Count - Math.Min(maximumPhysicalCores, classPool.Count) >= 2)
                            return Pick(classPool, "efficiency", "secondary-efficiency-tier", maximumPhysicalCores);
                    }
                }
                return Skip("insufficient-efficiency-core-capacity");
            }
            // 候选核心必须未停泊；已停泊的游戏核心仍计入游戏容量。
            if (local.Count < 4 + minimumCores) return Skip("insufficient-game-core-capacity");
            int domain = -1;
            bool validDomain = gameDomainMask != 0;
            foreach (AntiCheatAffinityCore core in local)
            {
                if ((core.Mask & gameDomainMask) == 0) continue;
                if (core.L3Id < 0 || (domain >= 0 && domain != core.L3Id)) validDomain = false;
                domain = core.L3Id;
            }
            if (validDomain && domain >= 0)
            {
                bool alternateExists = false;
                foreach (AntiCheatAffinityCore core in local)
                    if (core.L3Id >= 0 && core.L3Id != domain) alternateExists = true;
                SortedDictionary<int, List<AntiCheatAffinityCore>> alternatives = new SortedDictionary<int, List<AntiCheatAffinityCore>>();
                foreach (AntiCheatAffinityCore core in candidates)
                {
                    if (core.L3Id < 0 || core.L3Id == domain) continue;
                    List<AntiCheatAffinityCore> pool;
                    if (!alternatives.TryGetValue(core.L3Id, out pool))
                    { pool = new List<AntiCheatAffinityCore>(); alternatives.Add(core.L3Id, pool); }
                    pool.Add(core);
                }
                List<AntiCheatAffinityCore> best = null;
                foreach (List<AntiCheatAffinityCore> pool in alternatives.Values)
                    if (pool.Count >= minimumCores && local.Count - Math.Min(maximumPhysicalCores, pool.Count) >= 4
                        && (best == null || pool.Count > best.Count)) best = pool;
                if (best != null) return Pick(best, "alternate-cache-domain", "explicit-game-cache-domain-preserved", maximumPhysicalCores);
                if (alternateExists) return Skip("alternate-cache-domain-unavailable");
            }
            // 依据缓存域证据选择，并为游戏和系统保留四个物理核。
            candidates.Sort(delegate(AntiCheatAffinityCore a, AntiCheatAffinityCore b) {
                int order = a.Scheduling.CompareTo(b.Scheduling);
                return order != 0 ? order : b.Mask.CompareTo(a.Mask);
            });
            List<AntiCheatAffinityCore> auxiliary = new List<AntiCheatAffinityCore>();
            for (int i = 0; i < minimumCores; i++) auxiliary.Add(candidates[i]);
            return Pick(auxiliary, "auxiliary", minimumCores == 1 ? "one-complete-auxiliary-core" : "two-complete-auxiliary-cores", maximumPhysicalCores);
        }

        private static AntiCheatAffinityChoice Pick(List<AntiCheatAffinityCore> cores, string kind, string reason, int maximumPhysicalCores)
        {
            cores.Sort(delegate(AntiCheatAffinityCore a, AntiCheatAffinityCore b) {
                int order = a.Scheduling.CompareTo(b.Scheduling);
                return order != 0 ? order : b.Mask.CompareTo(a.Mask);
            });
            int count = Math.Min(cores.Count, maximumPhysicalCores);
            ulong mask = 0;
            for (int i = 0; i < count; i++) mask |= cores[i].Mask;
            return new AntiCheatAffinityChoice { Mask = mask, PhysicalCount = count, Kind = count == 1 ? "single-" + kind : kind, Reason = reason };
        }
    }
}
