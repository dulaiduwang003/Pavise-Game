// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class CpuTopology
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
        public static ulong FavoredMask;
        public static string FavoredDetail = "";

        public static ulong AllMask, ThrottleMask, BoostMask, StrictBoostMask;
        public static ulong AltThrottleMask;
        public static string PartitionTag = "";
        private static List<KeyValuePair<uint, ulong>> cacheDomains = new List<KeyValuePair<uint, ulong>>();
        private static List<ulong> processorDieDomains = new List<ulong>();

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

        // 核心掩码取系统枚举与进程视图的交集。
        internal static ulong ReconcileAllMask(ulong fromCount, ulong fromEnum, out bool mismatch)
        {
            mismatch = fromEnum != 0 && fromEnum != fromCount;
            if (!mismatch) return fromCount;
            ulong both = fromCount & fromEnum;
            return both != 0 ? both : fromCount;
        }

        public static bool AllMaskReconciled;

        private static void DeriveMasks()
        {
            int nc = Environment.ProcessorCount;
            ulong fromCount = nc >= 64 ? ulong.MaxValue : (1UL << nc) - 1UL;
            AllMask = ReconcileAllMask(fromCount, ParsedUnion, out AllMaskReconciled);
            if (Hybrid) { ThrottleMask = EffMask; BoostMask = AllMask; }
            else if (AsymCache) { ThrottleMask = SmallL3Mask; BoostMask = AllMask; }

            else { ThrottleMask = nc >= 2 && nc <= 64 ? 3UL << (nc - 2) : (nc >= 2 ? 0UL : 1UL); BoostMask = AllMask; }
            StrictBoostMask = CpuPartitionPolicy.StrictMask(AllMask, ThrottleMask,
                Hybrid ? PerfMask : 0, AsymCache ? BigL3Mask : 0);
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
    }
}
