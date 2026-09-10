// @author bdth 2074055628@qq.com
// 文件用途 CPU 集合记录解析 后台分区标识与核心域切换
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

        internal static ulong[] ParsedCoreMasksForTest() { return parsedCoreMasks.ToArray(); }

        internal static void SetParsedCoreMasksForTest(ulong[] cores)
        {
            parsedCoreMasks = new List<ulong>(cores ?? new ulong[0]);
        }

        // GLPIE 报出的全部逻辑核并集 DeriveMasks 拿它跟 ProcessorCount 对账
        internal static ulong ParsedUnion
        {
            get { ulong m = 0; foreach (ulong c in parsedCoreMasks) m |= c; return m; }
        }

        private static ulong UnionOf(List<ulong> masks)
        {
            ulong m = 0; foreach (ulong c in masks) m |= c; return m;
        }

        // 物理核有两套来源 掩码与混合架构判定来自 GLPIE 方案戳记来自 CPU Set 枚举
        //   两边描述的必须是同一台机器 否则保存的选核方案会因戳记不符被判无效
        //   核数相等还不够 数目一样而位置不同照样是两台机器 所以并集也要一致
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
