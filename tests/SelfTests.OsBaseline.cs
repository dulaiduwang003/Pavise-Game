// 文件用途 拿真实上报的内部版本做纯阈值检查 不启动 不还原 不建窗口
// 系统门槛回归 只验证比较规则 不触发拦截也不还原任何东西
#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int osBaselineChecks;

        internal static int RunOsBaselineRegressionTests()
        {
            Action[] tests =
            {
                OsBaselineIsTheSchedulingApiFloor,
                OsBaselineJudgesByBuildNumber
            };
            osBaselineChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS os-baseline assertions=" + osBaselineChecks
                + " startup=untouched restore=none windows_shown=false");
            return tests.Length;
        }

        private static void OsCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("OS baseline regression: " + message);
            osBaselineChecks++;
        }

        private static void OsBaselineIsTheSchedulingApiFloor()
        {
            OsCheck(Program.OsBuildBaseline == 19041,
                "the floor is Windows 10 2004, where per-process timer isolation arrives");
            OsCheck(Program.OsBuildBest > Program.OsBuildBaseline,
                "the preferred build must sit above the floor");
            // 本机跑得起自测就说明它在门槛之上 拦截不会误伤开发机
            OsCheck(!Program.OsBelowBaseline(),
                "a machine running these tests must be at or above the baseline");
        }

        private static void OsBaselineJudgesByBuildNumber()
        {
            int build = Native.OsBuild();
            OsCheck(build > 0, "RtlGetVersion must report a build number on a real system");
            OsCheck(build >= Program.OsBuildBaseline == !Program.OsBelowBaseline(),
                "the verdict follows the build number and nothing else");
        }
    }
}
#endif
