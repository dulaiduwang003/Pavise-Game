// @author bdth 2074055628@qq.com
// File purpose CPU Sets record parsing, background partition identification and core domain switching
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class CpuTopology
    {
        private sealed class CpuSetRec
        {
            public uint Id;
            public short Group;
            public byte Logical;
            public byte Core;
            public byte Efficiency;
            public byte Scheduling;
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

        // Physical core masks recorded at Parse time, each RelationProcessorCore record is one physical core
        //   physicalCoreMasks is not filled until BuildCpuSetPolicies, while DeriveMasks runs before it
        //   the interrupt placement threshold must count physical P-cores, only this copy works, otherwise the count is always 0
        private static List<ulong> parsedCoreMasks = new List<ulong>();

        internal static ulong[] ParsedCoreMasksForTest() { return parsedCoreMasks.ToArray(); }

        internal static void SetParsedCoreMasksForTest(ulong[] cores)
        {
            parsedCoreMasks = new List<ulong>(cores ?? new ulong[0]);
        }

        // Union of all logical cores reported by GLPIE, DeriveMasks reconciles it against ProcessorCount
        internal static ulong ParsedUnion
        {
            get { ulong m = 0; foreach (ulong c in parsedCoreMasks) m |= c; return m; }
        }

        private static ulong UnionOf(List<ulong> masks)
        {
            ulong m = 0; foreach (ulong c in masks) m |= c; return m;
        }

        // Physical cores have two sources, masks and hybrid detection come from GLPIE, the plan stamp comes from CPU Set enumeration
        //   both must describe the same machine, otherwise the saved core selection plan gets invalidated by a stamp mismatch
        //   equal core counts are not enough, same count at different positions is still two machines, so the unions must match too
        public static bool TopologySourcesAgree
        {
            get
            {
                return parsedCoreMasks.Count == physicalCoreMasks.Count
                    && UnionOf(parsedCoreMasks) == UnionOf(physicalCoreMasks);
            }
        }

        public static string TopologySourceCounts
        {
            get { return parsedCoreMasks.Count + " / " + physicalCoreMasks.Count; }
        }

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
    }
}
