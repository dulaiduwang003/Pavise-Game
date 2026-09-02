// Pure mask-computation checks; no registry access, no topology mutation, no writes.
// 内核保留核回归 只验证掩码推导 不碰注册表
#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int resCoreChecks;

        internal static int RunReservedCoresRegressionTests()
        {
            Action[] tests =
            {
                ResCoresHybridPicksPerfCoresAfterCpuZero,
                ResCoresRefusesUnfitTopologies,
                ResCoresBytesRoundTrip
            };
            resCoreChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS reserved-cores assertions=" + resCoreChecks
                + " registry=untouched topology=untouched windows_shown=false");
            return tests.Length;
        }

        private static void ResCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Reserved cores regression: " + message);
            resCoreChecks++;
        }

        // 13900K 形状 8 个 SMT 性能物理核加 16 个单线程能效核
        private static ulong[] HybridCores()
        {
            var cores = new ulong[24];
            for (int i = 0; i < 8; i++) cores[i] = 3UL << (i * 2);
            for (int i = 0; i < 16; i++) cores[8 + i] = 1UL << (16 + i);
            return cores;
        }

        private static void ResCoresHybridPicksPerfCoresAfterCpuZero()
        {
            ulong all = 0xFFFFFFFFUL;
            ulong perf = 0xFFFFUL;
            ulong mask = ReservedCoresTweak.ComputeMask(HybridCores(), perf, true, all, perf);
            ResCheck(mask == 0x3CUL,
                "the two physical P-cores after the CPU0 core are logical 2-5");
            ResCheck((mask & 1UL) == 0, "the core holding CPU0 must never be reserved");
            ulong flat = ReservedCoresTweak.ComputeMask(HybridCores(), 0, false, all, 0);
            ResCheck(flat == 0x3CUL, "without a strict mask the pick stands on its own");
        }

        private static void ResCoresRefusesUnfitTopologies()
        {
            ulong all = 0xFFFFFFFFUL;
            ulong perf = 0xFFFFUL;
            var small = new ulong[6];
            for (int i = 0; i < 6; i++) small[i] = 3UL << (i * 2);
            ResCheck(ReservedCoresTweak.ComputeMask(small, 0, false, 0xFFFUL, 0) == 0,
                "fewer than eight physical cores must refuse");
            var eight = new ulong[8];
            for (int i = 0; i < 8; i++) eight[i] = 3UL << (i * 2);
            ResCheck(ReservedCoresTweak.ComputeMask(eight, 0, false, 0xFFFFUL, 0) == 0x3CUL,
                "eight physical cores is the first shape that reserves");
            ResCheck(ReservedCoresTweak.ComputeMask(HybridCores(), perf, true, all, 0xFFFF0000UL) == 0,
                "reserved cores outside the partitioned game cores must refuse");
            ResCheck(ReservedCoresTweak.ComputeMask(null, perf, true, all, perf) == 0,
                "a missing topology must refuse");
            // 前两个可选物理核大得反常时 保留会吃掉过半逻辑核 必须拒绝
            var lopsided = new ulong[12];
            lopsided[0] = 0x3UL; lopsided[1] = 0x0FFCUL; lopsided[2] = 0x3000UL;
            for (int i = 3; i < 12; i++) lopsided[i] = 1UL << (11 + i);
            ResCheck(ReservedCoresTweak.ComputeMask(lopsided, 0, false, 0x7FFFFFUL, 0) == 0,
                "a pick that swallows over half the logical cores must refuse");
        }

        private static void ResCoresBytesRoundTrip()
        {
            ulong mask = 0x3CUL;
            byte[] bytes = ReservedCoresTweak.MaskBytes(mask);
            ResCheck(bytes.Length == 8 && bytes[0] == 0x3C,
                "the registry value is eight little-endian bytes");
            ResCheck(ReservedCoresTweak.BytesMask(bytes) == mask,
                "bytes must decode back to the same mask");
            ResCheck(ReservedCoresTweak.BytesMask(null) == 0,
                "an absent value decodes to an empty mask");
        }
    }
}
#endif
