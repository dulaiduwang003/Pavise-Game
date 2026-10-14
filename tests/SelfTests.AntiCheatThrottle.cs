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
            AcThrottleCheck(SuppressionCore.DesiredPriority(
                    SuppressionLevel.Isolated, Native.NORMAL_PRIORITY_CLASS, true)
                == Native.BELOW_NORMAL_PRIORITY_CLASS,
                "anti-cheat Strong must stop at below-normal priority, never idle");
            AcThrottleCheck(!SuppressionCore.DesiredTimerSeal(SuppressionLevel.Isolated, true),
                "anti-cheat Strong must not seal timer resolution");
            AcThrottleCheck(SuppressionCore.DesiredPagePriority(SuppressionLevel.Isolated, true) == 3,
                "anti-cheat Strong must not lower page priority");
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
