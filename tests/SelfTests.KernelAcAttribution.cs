// File purpose Kernel anti-cheat attribution regression, pure verdicts, reads nothing beyond the registry, no process launch, no settings changes
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

        // Names only games it recognizes, the EA family is this round's addition, Battlefield 6 used to fall to the fallback and report whichever anti-cheat is installed locally
        private static void KernelAcMatchesGameByExecutable()
        {
            Eq("EA Javelin", KernelAntiCheat.MatchByExe("bf6"));
            Eq("EA Javelin", KernelAntiCheat.MatchByExe("bf6.exe"));
            Eq("EA Javelin", KernelAntiCheat.MatchByExe(
                @"C:\Program Files (x86)\Steam\steamapps\common\Battlefield 6\bf6.exe"));
            Eq("EA Javelin", KernelAntiCheat.MatchByExe("bf2042.exe"));
            // cod.exe is the real main executable of Call of Duty HQ, matched on full-name equality
            Eq("Ricochet", KernelAntiCheat.MatchByExe("cod.exe"));
            Eq("Ricochet", KernelAntiCheat.MatchByExe("cod"));
            Eq("Ricochet", KernelAntiCheat.MatchByExe("ModernWarfare.exe"));
            // A three-letter prefix would hit unrelated games, so this entry accepts the full name only
            Eq(null, KernelAntiCheat.MatchByExe("CodeVein.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("codex.exe"));
            // The prefix can't be so short it mismatches unrelated programs, naming the wrong one and blaming a bystander are the same class of error
            Eq(null, KernelAntiCheat.MatchByExe("nfsclient.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("fc2launcher.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("maddenhelper.exe"));
            Eq(null, KernelAntiCheat.MatchByExe("bfgminer.exe"));
            Eq("HoYoKProtect", KernelAntiCheat.MatchByExe("YuanShen.exe"));
        }

        // When the game isn't recognized, the local install list can't be named as the culprit, that list has nothing to do with what's running now
        //   A misnamed log line sends the user to turn off an anti-cheat group that wasn't even involved
        private static void KernelAcDoesNotNameABystander()
        {
            string log = KernelAntiCheat.DescribeForLog("SomeUnknownGame.exe");
            if (log == null) return; // No kernel anti-cheat installed locally, not naming one is correct
            string installed = KernelAntiCheat.InstalledName();
            Eq(true, installed != null);
            // The fallback text must state this is the local install list, not hand the name over as the culprit
            Eq(Lang.F("log.kernelac.installed", installed), log);
            Eq(false, log == installed);
        }

        // Recognized games take the naming branch, both entries must agree for the same game
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
