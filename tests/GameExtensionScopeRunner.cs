#if PAVISE_SELFTEST && PAVISE_GAME_EXTENSION_SCOPE_RUNNER
using System;
using System.Collections.Generic;
namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int Main()
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                RunGameExtensionScopeRegressionTests(); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        private static void Eq<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException("Expected " + expected + ", actual " + actual);
        }
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Extension scope regressions cannot start the application runtime"); }
    }
}
#endif
