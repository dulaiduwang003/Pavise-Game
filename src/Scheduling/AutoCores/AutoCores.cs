using System;
using System.Diagnostics;

namespace PaviseApp
{
    internal static class AutoCores
    {
        internal const int MinSpareCores = 2;

        internal const int MinGameLogical = 2;

        internal const int WindowMs = 5000;

        internal const int MaxWindowMs = 60000;

        internal static readonly string[] RetiredSettings = new string[8] { "GmManualCorePlanV1", "GmCoreMask", "GmStrictCores", "GmCoreDomainAlt", "GmHeavySqueezeV2", "GmHeavyCoreMaskV1", "GmHardAffinityV1", "GmAffinityGuardV1" };

        private static volatile AutoCoreStatus status = new AutoCoreStatus();

        internal static long NowTicks
        {
            get
            {
                long timestamp = Stopwatch.GetTimestamp();
                long frequency = Stopwatch.Frequency;
                return timestamp / frequency * 10000000 + timestamp % frequency * 10000000 / frequency;
            }
        }

        internal static AutoCoreStatus Status
        {
            get
            {
                return status;
            }
        }

#if PAVISE_SELFTEST
        internal static bool? SupportedForTest;
        internal static Func<ulong> AllocatedForTest;
#endif
        internal static bool Supported
        {
            get
            {
#if PAVISE_SELFTEST
                if (SupportedForTest.HasValue) return SupportedForTest.Value;
#endif
                if (IntPtr.Size == 8 && Native.OsBuild() >= 19041)
                {
                    return !CpuTopology.MultiGroup;
                }
                return false;
            }
        }

        internal static void Publish(AutoCoreStatus next)
        {
            status = next ?? new AutoCoreStatus();
        }

        internal static ulong WholeCores(ulong mask, ulong[] cores)
        {
            ulong wholeCoresResult = 0uL;
            if (cores == null)
            {
                return 0uL;
            }
            foreach (ulong numericValueEntry in cores)
            {
                if ((numericValueEntry & mask) != 0)
                {
                    wholeCoresResult |= numericValueEntry;
                }
            }
            return wholeCoresResult;
        }

        internal static int CountBits(ulong mask)
        {
            int processedCount = 0;
            while (mask != 0)
            {
                mask &= mask - 1;
                processedCount++;
            }
            return processedCount;
        }

        internal static int PhysicalCount(ulong mask, ulong[] cores)
        {
            int processedCount = 0;
            if (cores != null)
            {
                foreach (ulong numericValueEntry in cores)
                {
                    if (numericValueEntry != 0 && (mask & numericValueEntry) == numericValueEntry)
                    {
                        processedCount++;
                    }
                }
            }
            return processedCount;
        }

        internal static ulong LiveAllocated()
        {
#if PAVISE_SELFTEST
            if (AllocatedForTest != null) return AllocatedForTest();
#endif
            try
            {
                IsolationCpuState isolationCpuState = new CoreIsolationNative().ReadSystem(IntPtr.Zero);
                return (isolationCpuState == null) ? 0 : isolationCpuState.Allocated;
            }
            catch
            {
                return 0uL;
            }
        }

        internal static string AffinityProtectionText(AutoCoreStatus status)
        {
            if (status != null && status.Unrestricted)
                return PerformancePolicy.Text("全选不额外限制，保留系统原范围", "All selected; keep the system's existing range");
            return PerformancePolicy.Text("核心绑定已生效", "Core affinity active");
        }

        internal static string ValidateRange(ulong isolation, ulong game, ulong all, ulong[] cores, ulong allocated, bool manual = false)
        {
            if (all == 0 || cores == null || cores.Length == 0)
            {
                return "topology";
            }
            ulong numericValue = 0uL;
            foreach (ulong numericValueEntry in cores)
            {
                if (numericValueEntry == 0 || (numericValueEntry & ~all) != 0 || (numericValue & numericValueEntry) != 0)
                {
                    return "topology";
                }
                numericValue |= numericValueEntry;
            }
            if (numericValue != all)
            {
                return "topology";
            }
            if (game == 0 || (game & ~all) != 0 || CountBits(game) < (manual ? 1 : 2))
            {
                return "game";
            }
            if (isolation == 0 || (isolation & ~all) != 0 || (!manual && (isolation & 1) != 0) || WholeCores(isolation, cores) != isolation)
            {
                return "whole";
            }
            int processedCount = 0;
            foreach (ulong numericValueEntry2 in cores)
            {
                if ((numericValueEntry2 & isolation) == 0 && (numericValueEntry2 & ~allocated) != 0)
                {
                    processedCount++;
                }
            }
            if (processedCount >= 2)
            {
                return null;
            }
            return "spare";
        }

        internal static bool WindowUsable(ThreadTimes before, ThreadTimes after)
        {
            if (before == null || after == null || before.Creation != after.Creation)
            {
                return false;
            }
            long remainingTicks = after.Ticks - before.Ticks;
            if (remainingTicks >= 40000000)
            {
                return remainingTicks <= 600000000;
            }
            return false;
        }
    }
}
