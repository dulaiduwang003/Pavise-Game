// File purpose Pure threshold checks against the real reported build number, no startup, no restore, no windows
// OS baseline regression, only verifies the comparison rules, triggers no blocking and restores nothing
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
            // If the self-test runs on this machine it is above the baseline, the block will not hit the dev machine
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
