#if PAVISE_SELFTEST && PAVISE_TIMER_RESOLUTION_RUNNER
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int timerResolutionAssertions;
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                Logger.LogPath = Path.Combine(Path.GetFullPath(args[0]), "isolated.log");
                Lang.Init();
                RunExtremeTimerTests();
                Console.WriteLine("RESULT assertions=" + timerResolutionAssertions
                    + " settings=transient application_started=false timer_native_calls=mocked registry_writes=false bcd_writes=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { Logger.WritesEnabled = false; Settings.UseTransientStoreForCurrentProcess(); }
        }
        private static void Eq<T>(T expected, T actual)
        {
            timerResolutionAssertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException("Expected " + expected + ", actual " + actual);
        }
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Timer resolution regressions cannot start the application runtime"); }
    }
}
#endif
