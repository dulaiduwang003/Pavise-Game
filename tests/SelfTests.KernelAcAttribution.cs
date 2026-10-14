#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunKernelAcAttributionRegressionTests()
        {
            Action[] tests =
            {
                KernelAcMatchesGameByExecutable,
                KernelAcDoesNotNameABystander,
                KernelAcUnknownGameStaysUnnamed
            };
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            return tests.Length;
        }

        private static void KernelAcMatchesGameByExecutable()
        {
            Eq("EA Javelin", KernelAntiCheat.MatchByExe("bf6"));
            Eq("EA Javelin", KernelAntiCheat.MatchByExe("bf6.exe"));
            Eq("EA Javelin", KernelAntiCheat.MatchByExe(
                @"C:\Program Files (x86)\Steam\steamapps\common\Battlefield 6\bf6.exe"));
            Eq("EA Javelin", KernelAntiCheat.MatchByExe("bf2042.exe"));
            Eq("Ricochet", KernelAntiCheat.MatchByExe("cod.exe"));
            Eq("Ricochet", KernelAntiCheat.MatchByExe("cod"));
            Eq("Ricochet", KernelAntiCheat.MatchByExe("ModernWarfare.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("CodeVein.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("codex.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("nfsclient.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("fc2launcher.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("maddenhelper.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("bfgminer.exe"));
            Eq("HoYoKProtect", KernelAntiCheat.MatchByExe("YuanShen.exe"));
        }

        private static void KernelAcDoesNotNameABystander()
        {
            string log = KernelAntiCheat.DescribeForLog("SomeUnknownGame.exe");
            if (log == null) return;
            string installed = KernelAntiCheat.InstalledName();
            Eq(true, installed != null);
            Eq(Lang.F("log.kernelac.installed", installed), log);
            Eq(false, log == installed);
        }

        private static void KernelAcUnknownGameStaysUnnamed()
        {
            Eq("EA Javelin", KernelAntiCheat.DescribeForLog("bf6.exe"));
            Eq(KernelAntiCheat.MatchByExe("bf6.exe"), KernelAntiCheat.DescribeForLog("bf6.exe"));
            Eq(null, KernelAntiCheat.MatchByExe(null));
            Eq(null, KernelAntiCheat.MatchByExe(""));
        }
    }
}
#endif
