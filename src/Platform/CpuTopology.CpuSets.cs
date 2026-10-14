// 作者：bdth 2074055628@qq.com
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
        private static readonly List<ulong> physicalCoreMasks = new List<ulong>();

        private static List<ulong> parsedCoreMasks = new List<ulong>();

        internal static ulong[] ParsedCoreMasksForTest() { return parsedCoreMasks.ToArray(); }

        internal static void SetParsedCoreMasksForTest(ulong[] cores)
        {
            parsedCoreMasks = new List<ulong>(cores ?? new ulong[0]);
        }

        internal static ulong ParsedUnion
        {
            get { ulong m = 0; foreach (ulong c in parsedCoreMasks) m |= c; return m; }
        }

        private static ulong UnionOf(List<ulong> masks)
        {
            ulong m = 0; foreach (ulong c in masks) m |= c; return m;
        }

        // 两套拓扑的核心位置必须一致，仅核数相同不足以复用方案。
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

        public static uint[] BackgroundCpuSetIds()
        {
            return backgroundIds;
        }

        public static bool HasSafeBackgroundPartition()
        {
            return backgroundIds != null && backgroundIds.Length > 0
                && partitionGameIds != null && partitionGameIds.Length > 0;
        }

        public static uint[] InactiveBackgroundCpuSetIds()
        {
            return altBackgroundIds;
        }

        public static ulong InactiveThrottleMask
        {
            get { return AltThrottleMask; }
        }

    }
}
