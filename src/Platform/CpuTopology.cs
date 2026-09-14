// @author bdth 2074055628@qq.com
// File purpose CPU topology probing, mask derivation and safety validation
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
        // The lowest class in a three-class hybrid layout, Core Ultra's LP-E cores are it, on the SoC tile with the lowest clocks
        //   still stays inside EffMask, pushing background suppression there is right
        //   but interrupt placement must exclude it, 1.8.1.0 pulled USB and disk interrupt affinity precisely because interrupts landed on low-clock efficiency cores
        //   only set with three or more classes, with two the lowest class is EffMask itself and excluding it would empty the placement pool
        public static ulong LowPowerEffMask;
        // The scheduler's per-logical-core rating, the highest tier in SchedulingClass, Intel Turbo Boost Max 3.0 favored cores
        //   the OS pushes the heaviest threads onto these, 0 when all ratings are equal
        public static ulong FavoredMask;
        public static string FavoredDetail = "";

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

        // ProcessorCount is the process's view, enumerated cores are the machine's view, normally they agree
        //   on mismatch keep only bits both sides agree on, extras on one side cannot be written, missing ones would put non-existent cores in the mask
        //   narrow in both directions, better to manage a few cores less than let AllMask describe a machine that does not exist
        internal static ulong ReconcileAllMask(ulong fromCount, ulong fromEnum, out bool mismatch)
        {
            mismatch = fromEnum != 0 && fromEnum != fromCount;
            if (!mismatch) return fromCount;
            ulong both = fromCount & fromEnum;
            return both != 0 ? both : fromCount;
        }

        // Set when AllMask disagrees with enumeration, UI and log use it to warn that the saved core plan may be off
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
            InterruptMask = DeriveInterruptMask(Hybrid, PerfMask, ThrottleMask,
                LowPowerEffMask, ParsedPhysicalIn(PerfMask));
        }

        // With enough P-cores put interrupts on P-cores, close to the render thread, otherwise yield, do not fight the game for its few P-cores
        //   the threshold must count physical cores, the old code counted logical ones, written when P-cores always had hyper-threading
        //   Arrow Lake dropped hyper-threading, 6 physical P-cores leave just 6 logical bits, the same machine would flip the decision
        //   measured comparison: Raptor 6P with HT is 12 logical bits and goes to P-cores, Arrow-H 6P without HT is 6 and falls straight to efficiency cores
        internal const int MinPerfPhysicalForInterrupts = 4;

        internal static ulong DeriveInterruptMask(bool hybrid, ulong perfMask, ulong throttle,
            ulong lowPower, int perfPhysicalCores)
        {
            if (!hybrid || perfMask == 0) return throttle;
            // Even when yielding never land on the lowest efficiency class, those are the lowest-clocked cores in the machine
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
    }
}
