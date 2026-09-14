// File purpose Anti-cheat three-tier strength and classification regression, pure decisions, no process writes, no real settings changes
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
            // Unreadable or unknown values fall back to the default, no guessing which tier the user wanted
            Eq(AntiCheatModes.Default, AntiCheatModes.Parse(null));
            Eq(AntiCheatModes.Default, AntiCheatModes.Parse(""));
            Eq(AntiCheatModes.Default, AntiCheatModes.Parse("whatever"));
            // Default must be Isolation, that is the behavior this feature always had, upgrading does not change settings already in effect
            Eq(AntiCheatMode.Isolated, AntiCheatModes.Default);
        }

        private static void AcModeMapsToExistingLevels()
        {
            Eq(SuppressionLevel.Eco, AntiCheatModes.LevelOf(AntiCheatMode.Gentle));
            Eq(SuppressionLevel.Restrained, AntiCheatModes.LevelOf(AntiCheatMode.Balanced));
            Eq(SuppressionLevel.Isolated, AntiCheatModes.LevelOf(AntiCheatMode.Isolated));
        }

        // The effective ingredients are EcoQoS, E-core frequency cap and disk IO downgrade, none of the three tiers may drop any
        //   Only the depth of intervention escalates: Gentle leaves scheduling priority alone, Balanced drops it below normal
        private static void AcModeKeepsEffectiveIngredientsInEveryTier()
        {
            const uint original = Native.NORMAL_PRIORITY_CLASS;
            foreach (AntiCheatMode mode in new[] { AntiCheatMode.Gentle, AntiCheatMode.Balanced, AntiCheatMode.Isolated })
            {
                SuppressionLevel level = AntiCheatModes.LevelOf(mode);
                // Disk IO is lowered in every tier, disk scanning is the main damage
                Eq(true, SuppressionCore.DesiredIoPriority(level) < 2);
            }
            // Gentle leaves scheduling priority alone, Balanced and Isolation drop it below normal
            Eq(original, SuppressionCore.DesiredPriority(
                AntiCheatModes.LevelOf(AntiCheatMode.Gentle), original, true));
            Eq(Native.BELOW_NORMAL_PRIORITY_CLASS, SuppressionCore.DesiredPriority(
                AntiCheatModes.LevelOf(AntiCheatMode.Balanced), original, true));
            Eq(Native.BELOW_NORMAL_PRIORITY_CLASS, SuppressionCore.DesiredPriority(
                AntiCheatModes.LevelOf(AntiCheatMode.Isolated), original, true));
            // Only Isolation pushes disk IO down to very low
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

        // A scanning anti-cheat that suspends game threads while starved of time slices stretches the suspend window from hundreds of ms to seconds
        //   So no tier may feed the anti-cheat the three starvation parts, see the comment at the top of SuppressionCore.Apply
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
            // Ordinary background gets no such protection, the Isolation tier still feeds it all; the difference in these three is the antiCheat flag
            Eq(Native.IDLE_PRIORITY_CLASS, SuppressionCore.DesiredPriority(
                SuppressionLevel.Isolated, Native.NORMAL_PRIORITY_CLASS, false));
            Eq(1, SuppressionCore.DesiredPagePriority(SuppressionLevel.Isolated, false));
            Eq(true, SuppressionCore.DesiredTimerSeal(SuppressionLevel.Isolated, false));
        }

        // Lowering the tier only stops adding new pins, existing pins must be actively released
        //   Unless SqueezeAff is cleared, DesiredAffinity keeps returning the placement and every reconcile round writes it back
        //   The anti-cheat would stay pinned to the tail cores until the process exits, while the UI says that tier does no pinning
        //   This tests the decision itself, Tamer uses it to decide whether to call ClearSqueezes
        //   A real placement needs Acquire first, which would modify this process, the isolated test skips that
        private static void AcModeLoweringReleasesExistingPins()
        {
            // Dropping from a pinning tier to a non-pinning tier must release
            Eq(true, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Isolated, AntiCheatMode.Gentle));
            Eq(true, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Isolated, AntiCheatMode.Balanced));
            // Switching between tiers that never pin has no placement to release
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Gentle, AntiCheatMode.Balanced));
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Balanced, AntiCheatMode.Gentle));
            // Raising the tier only allows re-pinning, needs no release and must not pin on its own
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Gentle, AntiCheatMode.Isolated));
            Eq(false, AntiCheatModes.ShouldReleasePins(AntiCheatMode.Balanced, AntiCheatMode.Isolated));
            // Same tier, no change
            foreach (AntiCheatMode mode in new[] { AntiCheatMode.Gentle, AntiCheatMode.Balanced, AntiCheatMode.Isolated })
                Eq(false, AntiCheatModes.ShouldReleasePins(mode, mode));
        }

        // Vanguard blocks file loading by third-party programs that touch low-level system functions; the failure mode of suppressing it is the game not launching
        //   The criterion is not whether a kernel driver exists, BattlEye and EAC both have .sys but their user-mode services are safe to suppress
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
            // Kernel driver present but user-mode service suppressible stays suppressible, this is a measured result, not an inference
            Eq(true, battleye.Suppressible);
            Eq(true, eac.Suppressible);
            Eq(true, ace.Suppressible);
        }

        // Reclassifying as protect-only only removes it from suppression targets; the process names still go on the exemption list and background suppression still skips them
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
