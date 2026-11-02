#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunAntiCheatThrottleRegressionTests()
        {
            Action[] tests =
            {
                AntiCheatStrongTierDropsStarvationParts,
                AntiCheatStrongTierKeepsEffectParts,
                BackgroundTiersAreUnchanged
            };
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            return tests.Length;
        }

        private static void AcThrottleCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Anti-cheat throttle regression: " + message);
        }

        private static void AntiCheatStrongTierDropsStarvationParts()
        {
            // Match 2223: allow the anti-cheat to finish work that the game may be waiting for.
            AcThrottleCheck(!SuppressionCore.SevereAntiCheat(SuppressionLevel.Isolated, true),
                "anti-cheat Strong must not use the starvation policy");
            AcThrottleCheck(SuppressionCore.DesiredPriority(
                    SuppressionLevel.Isolated, Native.NORMAL_PRIORITY_CLASS, true)
                == Native.BELOW_NORMAL_PRIORITY_CLASS,
                "anti-cheat Strong must retain 2223 BelowNormal priority");
            AcThrottleCheck(!SuppressionCore.DesiredTimerSeal(SuppressionLevel.Isolated, true),
                "anti-cheat Strong must preserve timer requests");
            AcThrottleCheck(SuppressionCore.DesiredPagePriority(SuppressionLevel.Isolated, true) == 3,
                "anti-cheat Strong must retain page priority 3");
            foreach (int original in new[] { -1, 0, 1 })
                AcThrottleCheck(SuppressionCore.DesiredBoostDisabled(SuppressionLevel.Isolated, true, original) == original,
                    "anti-cheat Strong must retain the original dynamic boost policy");
            int current = 1, writes = 0;
            AcThrottleCheck(SuppressionCore.ApplyBoostPolicy(SuppressionLevel.Isolated, true, 0,
                () => current, disabled => { writes++; current = disabled ? 1 : 0; return true; }) == ""
                && current == 0 && writes == 1, "a previously forced boost-disable must return to its captured original");
        }

        private static void AntiCheatStrongTierKeepsEffectParts()
        {
            AcThrottleCheck(SuppressionCore.DesiredIoPriority(SuppressionLevel.Isolated) == 0,
                "Strong must keep very-low disk I/O — that is the anti-scan payoff");
            AcThrottleCheck(SuppressionCore.DesiredPriority(
                    SuppressionLevel.Restrained, Native.NORMAL_PRIORITY_CLASS, true)
                == Native.BELOW_NORMAL_PRIORITY_CLASS,
                "Balanced keeps below-normal priority for anti-cheat");
            AcThrottleCheck(SuppressionCore.DesiredPriority(
                    SuppressionLevel.Eco, Native.NORMAL_PRIORITY_CLASS, true)
                == Native.NORMAL_PRIORITY_CLASS,
                "Gentle leaves the priority untouched for anti-cheat");
        }

        private static void BackgroundTiersAreUnchanged()
        {
            AcThrottleCheck(SuppressionCore.DesiredPriority(
                    SuppressionLevel.Isolated, Native.NORMAL_PRIORITY_CLASS, false)
                == Native.IDLE_PRIORITY_CLASS,
                "background Isolated must still use idle priority");
            AcThrottleCheck(SuppressionCore.DesiredTimerSeal(SuppressionLevel.Isolated, false),
                "background Isolated must still seal timer resolution");
            AcThrottleCheck(SuppressionCore.DesiredPagePriority(SuppressionLevel.Isolated, false) == 1,
                "background Isolated must still lower page priority");
            AcThrottleCheck(SuppressionCore.DesiredIoPriority(SuppressionLevel.Restrained) == 1
                && SuppressionCore.DesiredPagePriority(SuppressionLevel.Restrained, false) == 3
                && !SuppressionCore.DesiredTimerSeal(SuppressionLevel.Restrained, false),
                "background Balanced components must be unchanged");
        }
    }
}
#endif
