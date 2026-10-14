#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunAntiCheatModeRegressionTests()
        {
            Action[] tests =
            {
                AcModeRoundTripsAndFallsBack,
                AcModeMapsToExistingLevels,
                AcModeKeepsEffectiveIngredientsInEveryTier,
                AcModePinsCoresOnlyAtTheTop,
                AcModeNeverStarvesAnAntiCheat,
                AcModeLoweringReleasesExistingPins,
                AcCategorySplitsVanguardOut,
                AcCategoryKeepsProtectOnlyGroupsExempt
            };
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            return tests.Length;
        }

        private static void AcModeRoundTripsAndFallsBack()
        {
            foreach (AntiCheatMode mode in new[] { AntiCheatMode.Gentle, AntiCheatMode.Balanced, AntiCheatMode.Isolated })
                Eq(mode, AntiCheatModes.Parse(AntiCheatModes.Token(mode)));
            Eq(AntiCheatModes.Default, AntiCheatModes.Parse(null));
            Eq(AntiCheatModes.Default, AntiCheatModes.Parse(""));
            Eq(AntiCheatModes.Default, AntiCheatModes.Parse("whatever"));
            Eq(AntiCheatMode.Isolated, AntiCheatModes.Default);
        }

        private static void AcModeMapsToExistingLevels()
        {
            Eq(SuppressionLevel.Eco, AntiCheatModes.LevelOf(AntiCheatMode.Gentle));
            Eq(SuppressionLevel.Restrained, AntiCheatModes.LevelOf(AntiCheatMode.Balanced));
            Eq(SuppressionLevel.Isolated, AntiCheatModes.LevelOf(AntiCheatMode.Isolated));
        }

        private static void AcModeKeepsEffectiveIngredientsInEveryTier()
        {
            const uint original = Native.NORMAL_PRIORITY_CLASS;
            foreach (AntiCheatMode mode in new[] { AntiCheatMode.Gentle, AntiCheatMode.Balanced, AntiCheatMode.Isolated })
            {
                SuppressionLevel level = AntiCheatModes.LevelOf(mode);
                Eq(true, SuppressionCore.DesiredIoPriority(level) < 2);
            }
            Eq(original, SuppressionCore.DesiredPriority(
                AntiCheatModes.LevelOf(AntiCheatMode.Gentle), original, true));
            Eq(Native.BELOW_NORMAL_PRIORITY_CLASS, SuppressionCore.DesiredPriority(
                AntiCheatModes.LevelOf(AntiCheatMode.Balanced), original, true));
            Eq(Native.BELOW_NORMAL_PRIORITY_CLASS, SuppressionCore.DesiredPriority(
                AntiCheatModes.LevelOf(AntiCheatMode.Isolated), original, true));
            Eq(1, SuppressionCore.DesiredIoPriority(AntiCheatModes.LevelOf(AntiCheatMode.Gentle)));
            Eq(1, SuppressionCore.DesiredIoPriority(AntiCheatModes.LevelOf(AntiCheatMode.Balanced)));
            Eq(0, SuppressionCore.DesiredIoPriority(AntiCheatModes.LevelOf(AntiCheatMode.Isolated)));
        }

        private static void AcModePinsCoresOnlyAtTheTop()
        {
            Eq(false, AntiCheatModes.PinsCores(AntiCheatMode.Gentle));
            Eq(false, AntiCheatModes.PinsCores(AntiCheatMode.Balanced));
            Eq(true, AntiCheatModes.PinsCores(AntiCheatMode.Isolated));
        }

        private static void AcModeNeverStarvesAnAntiCheat()
        {
            foreach (AntiCheatMode mode in new[] { AntiCheatMode.Gentle, AntiCheatMode.Balanced, AntiCheatMode.Isolated })
            {
                SuppressionLevel level = AntiCheatModes.LevelOf(mode);
                Eq(false, SuppressionCore.DesiredPriority(level, Native.NORMAL_PRIORITY_CLASS, true)
                    == Native.IDLE_PRIORITY_CLASS);
                Eq(3, SuppressionCore.DesiredPagePriority(level, true));
                Eq(false, SuppressionCore.DesiredTimerSeal(level, true));
            }
            Eq(Native.IDLE_PRIORITY_CLASS, SuppressionCore.DesiredPriority(
                SuppressionLevel.Isolated, Native.NORMAL_PRIORITY_CLASS, false));
            Eq(1, SuppressionCore.DesiredPagePriority(SuppressionLevel.Isolated, false));
            Eq(true, SuppressionCore.DesiredTimerSeal(SuppressionLevel.Isolated, false));
        }

        private static void AcModeLoweringReleasesExistingPins()
        {
            Eq(true, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Isolated, AntiCheatMode.Gentle));
            Eq(true, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Isolated, AntiCheatMode.Balanced));
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Gentle, AntiCheatMode.Balanced));
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Balanced, AntiCheatMode.Gentle));
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Gentle, AntiCheatMode.Isolated));
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Balanced, AntiCheatMode.Isolated));
            foreach (AntiCheatMode mode in new[] { AntiCheatMode.Gentle, AntiCheatMode.Balanced, AntiCheatMode.Isolated })
                Eq(false, AntiCheatModes.ShouldReleasePins(mode, mode));
        }

        private static void AcCategorySplitsVanguardOut()
        {
            AcGroup vanguard = null, battleye = null, eac = null, ace = null;
            foreach (AcGroup g in AntiCheatCatalog.Groups)
            {
                if (g.Key == "vanguard") vanguard = g;
                if (g.Key == "battleye") battleye = g;
                if (g.Key == "eac") eac = g;
                if (g.Key == "ace") ace = g;
            }
            Eq(true, vanguard != null && battleye != null && eac != null && ace != null);
            Eq(AcCategory.ProtectOnly, vanguard.Category);
            Eq(false, vanguard.Suppressible);
            Eq(true, battleye.Suppressible);
            Eq(true, eac.Suppressible);
            Eq(true, ace.Suppressible);
        }

        private static void AcCategoryKeepsProtectOnlyGroupsExempt()
        {
            foreach (AcGroup g in AntiCheatCatalog.Groups)
            {
                if (g.Suppressible) continue;
                foreach (string name in g.Procs)
                {
                    Eq(true, AntiCheatCatalog.IsKnownProcess(name));
                    Eq(true, AntiCheatCatalog.IsAntiCheatLikeName(name + ".exe"));
                }
            }
        }
    }
}
#endif
