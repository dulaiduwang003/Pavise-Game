using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // Publish topology only from complete Windows snapshots; do not infer core type from model/SMT.
    internal sealed class CpuTopologyEvidence
    {
        internal bool Valid, MultiGroup;
        internal string Reason = "unavailable";
        internal ulong LogicalMask;
        internal ulong[] CoreMasks = new ulong[0];
        internal int[] Classes;

        private static int[] UnknownClasses()
        {
            int[] values = new int[64];
            for (int i = 0; i < values.Length; i++) values[i] = -1;
            return values;
        }

        private static CpuTopologyEvidence Invalid(string reason, bool multiGroup)
        {
            return new CpuTopologyEvidence { Reason = reason, MultiGroup = multiGroup };
        }

        internal static CpuTopologyEvidence FromTopology(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return Invalid("topology-unavailable", false);
            List<ulong> cores = new List<ulong>();
            int[] classes = UnknownClasses();
            ulong union = 0, active = 0;
            bool groupsSeen = false, multi = false;
            for (int at = 0; at < bytes.Length; )
            {
                if (bytes.Length - at < 8) return Invalid("truncated-topology", multi);
                int type = BitConverter.ToInt32(bytes, at);
                uint sizeValue = BitConverter.ToUInt32(bytes, at + 4);
                if (sizeValue < 8 || sizeValue > bytes.Length - at) return Invalid("invalid-topology-record", multi);
                int size = (int)sizeValue;
                if (type == 0)
                {
                    if (size < 48 || BitConverter.ToUInt16(bytes, at + 30) != 1)
                        return Invalid("invalid-core-record", multi);
                    ushort group = BitConverter.ToUInt16(bytes, at + 40);
                    ulong mask = BitConverter.ToUInt64(bytes, at + 32);
                    if (mask == 0) return Invalid("empty-core", multi);
                    if (group != 0) multi = true;
                    else
                    {
                        if ((mask & union) != 0) return Invalid("overlapping-cores", multi);
                        union |= mask;
                        cores.Add(mask);
                        for (int logical = 0; logical < 64; logical++)
                            if ((mask & (1UL << logical)) != 0) classes[logical] = bytes[at + 9];
                    }
                }
                else if (type == 4)
                {
                    if (groupsSeen || size < 80) return Invalid("invalid-processor-groups", multi);
                    groupsSeen = true;
                    int count = BitConverter.ToUInt16(bytes, at + 10);
                    int maximum = BitConverter.ToUInt16(bytes, at + 8);
                    if (count < 1 || count > maximum || count > (size - 32) / 48)
                        return Invalid("invalid-group-count", multi);
                    if (count > 1) multi = true;
                    for (int group = 0; group < count; group++)
                    {
                        int offset = at + 32 + group * 48;
                        ulong mask = BitConverter.ToUInt64(bytes, offset + 40);
                        int bits = 0;
                        for (ulong remaining = mask; remaining != 0; remaining &= remaining - 1) bits++;
                        if (bits == 0 || bits != bytes[offset + 1] || bits > bytes[offset] || bytes[offset] > 64)
                            return Invalid("invalid-active-mask", multi);
                        if (group == 0) active = mask;
                    }
                }
                at += size;
            }
            if (multi) return Invalid("multiple-processor-groups", true);
            if (!groupsSeen || active == 0 || union != active) return Invalid("incomplete-core-coverage", false);
            return new CpuTopologyEvidence { Valid = true, Reason = "ok", LogicalMask = union, CoreMasks = cores.ToArray(), Classes = classes };
        }

        internal static CpuTopologyEvidence FromCpuSets(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return Invalid("cpu-sets-unavailable", false);
            Dictionary<byte, ulong> masks = new Dictionary<byte, ulong>();
            HashSet<uint> ids = new HashSet<uint>();
            HashSet<int> processors = new HashSet<int>();
            int[] classes = UnknownClasses();
            ulong union = 0;
            bool multi = false;
            for (int at = 0; at < bytes.Length; )
            {
                if (bytes.Length - at < 8) return Invalid("truncated-cpu-sets", multi);
                uint sizeValue = BitConverter.ToUInt32(bytes, at);
                if (sizeValue < 8 || sizeValue > bytes.Length - at) return Invalid("invalid-cpu-set-record", multi);
                int size = (int)sizeValue;
                if (BitConverter.ToInt32(bytes, at + 4) == 0)
                {
                    if (size < 32 || bytes[at + 14] >= 64) return Invalid("invalid-cpu-set", multi);
                    ushort group = BitConverter.ToUInt16(bytes, at + 12);
                    int logical = bytes[at + 14];
                    if (!ids.Add(BitConverter.ToUInt32(bytes, at + 8)) || !processors.Add(group * 64 + logical))
                        return Invalid("duplicate-cpu-set", multi);
                    if (group != 0) multi = true;
                    else
                    {
                        byte core = bytes[at + 15];
                        ulong mask;
                        masks.TryGetValue(core, out mask);
                        masks[core] = mask | (1UL << logical);
                        union |= 1UL << logical;
                        classes[logical] = bytes[at + 18];
                    }
                }
                at += size;
            }
            if (multi) return Invalid("multiple-processor-groups", true);
            if (union == 0) return Invalid("empty-cpu-sets", false);
            return new CpuTopologyEvidence { Valid = true, Reason = "ok", LogicalMask = union,
                CoreMasks = new List<ulong>(masks.Values).ToArray(), Classes = classes };
        }
    }
}
