// 文件用途 反作弊目录和现有压制回归的专用入口
#if PAVISE_ANTICHEAT_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static class AntiCheatChecks
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest();
                Logger.LogPath = null;
                Lang.Init();
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
                typeof(PeripheralVendorProbe).GetField("tokens", flags).SetValue(null, new string[0]);
                typeof(PeripheralVendorProbe).GetField("stamp", flags).SetValue(null, Environment.TickCount);
                typeof(PeripheralVendorProbe).GetField("scanned", flags).SetValue(null, true);
                Console.WriteLine("ISOLATION settings=transient process_writes=none application_started=false");
                int catalog = SelfTests.RunAntiCheatCatalogRegressionTests();
                int throttle = SelfTests.RunAntiCheatThrottleRegressionTests();
                SelfTests.RunSuppressionAffinityTests();
                int release = SelfTests.RunRendererReleaseRegressionTests();
                Console.WriteLine("PASS catalog=" + catalog + " throttle=" + throttle
                    + " SuppressionAffinity=passed RendererRelease=" + release);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL " + error);
                return 1;
            }
        }
    }

    internal static partial class SelfTests
    {
        private static void Eq<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException("Expected " + expected + ", actual " + actual);
        }

        public static bool TryHandleRuntimeMode(string[] args)
        {
            throw new InvalidOperationException("The anti-cheat checks cannot start the tuning runtime.");
        }
    }
}
#endif
