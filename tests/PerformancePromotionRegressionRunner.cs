#if PAVISE_SELFTEST && PAVISE_PROMOTION_REGRESSION
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal static class PerformancePromotionRegressionRunner
    {
        private static int Main()
        {
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 45000, Timeout.Infinite))
            {
                try
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.WritesEnabled = false;
                    Lang.Init();
                    SelfTests.RunPerformancePromotionTests();
                    SelfTests.RunFrameCoordinationTests();
                    SelfTests.RunLogSeverityRegressionTests();
                    Settings.UseTransientStoreForCurrentProcess();
                    SelfTests.RunPromotionPolicyCatalogueTests();
                    Settings.UseTransientStoreForCurrentProcess();
                    SelfTests.RunRenderLaneEligibilityTests();
                    Console.WriteLine("PASS Assertions=" + SelfTests.PromotionAssertions);
                    return 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
                finally { RenderLane.Release(); }
            }
        }
    }
    internal static partial class SelfTests
    {
        internal static int PromotionAssertions;
        private static void Eq<T>(T expected, T actual)
        {
            PromotionAssertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException("Expected " + expected + ", actual " + actual);
        }
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("The regression runner cannot start the application runtime"); }
    }
}
#endif
