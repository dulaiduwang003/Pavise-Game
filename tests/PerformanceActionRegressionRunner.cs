#if PAVISE_SELFTEST && PAVISE_PERFORMANCE_ACTION_RUNNER
using System;
using System.Collections.Generic;
namespace PaviseApp
{
    internal static partial class SelfTests
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try {
                if (args.Length == 1 && args[0] == "--visual-effects") {
                    RunMatchVisualEffectsTests();
                    Console.WriteLine("PASS match visual effects assertions=" + PerformanceActionAssertions + "; lifecycle and recovery passed; system_writes=mocked");
                } else if (args.Length == 1 && args[0] == "--environment-quantum") {
                    RunEnvironmentQuantumTests();
                    Console.WriteLine("PASS environment bundle + independent quantum assertions=" + PerformanceActionAssertions + "; migration and layout passed; system_writes=mocked");
                } else {
                    RunPerformanceActionTests(); RunAutomaticFullscreenOptimizationTests();
                    Console.WriteLine("PASS performance actions assertions=" + PerformanceActionAssertions + "; FSO regression passed; system_writes=mocked");
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        private static void Eq<T>(T expected, T actual)
        { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Expected " + expected + ", actual " + actual); }
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application runtime is unavailable in this isolated runner"); }
    }
}
#endif
