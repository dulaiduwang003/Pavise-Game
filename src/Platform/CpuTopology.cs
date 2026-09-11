// @author bdth 2074055628@qq.com
// 文件用途 CPU 拓扑探测 掩码推导与安全校验
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
        // 三档混合架构里最低的那一档 Core Ultra 的 LP-E 核就是它 SoC tile 上 时钟最低
        //   仍然留在 EffMask 里 后台压制往那儿赶是对的
        //   但中断落点必须排除它 1.8.1.0 下架 USB 与硬盘中断亲和就是栽在把中断投到低频能效核
        //   只有三档以上才有值 两档时最低档就是 EffMask 本身 整体排除会把落点池清空
        public static ulong LowPowerEffMask;
        // 调度器给每个逻辑核打的评级 SchedulingClass 里最高的那一档 Intel Turbo Boost Max 3.0 的优选核
        //   系统会把最重的线程往这几颗上赶 评级全相同时为 0
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

        // ProcessorCount 是进程视角的数字 枚举出来的核是机器视角 正常两者一致
        //   不一致时只取两边都认的位 多出来的一边写不进去 少掉的一边会让掩码带上不存在的核
        //   两个方向都往窄里收 宁可少管几个核 也不让 AllMask 描述一台不存在的机器
        internal static ulong ReconcileAllMask(ulong fromCount, ulong fromEnum, out bool mismatch)
        {
            mismatch = fromEnum != 0 && fromEnum != fromCount;
            if (!mismatch) return fromCount;
            ulong both = fromCount & fromEnum;
            return both != 0 ? both : fromCount;
        }

        // AllMask 与枚举对不上时置位 界面与日志据此提示保存的核心方案可能失真
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
    }
}
