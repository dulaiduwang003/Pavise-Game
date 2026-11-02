#if PAVISE_SELFTEST && PAVISE_SUPPRESSION_STRENGTH_CHECKS
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int Main()
        {
            try
            {
                Logger.WritesEnabled = false;
                Settings.UseTransientStoreForCurrentProcess();
                int groups = RunAntiCheatThrottleRegressionTests();
                Console.WriteLine("PASS 2223 suppression strength groups=" + groups
                    + "; original dynamic boost recovered; ordinary background unchanged; native writes=none");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Suppression strength checks cannot start Pavise."); }
    }
}
#endif
