#if !PAVISE_IRQ_BENCH || !PAVISE_SELFTEST
#error Isolated IRQ load bench only.
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class IrqLoadBench
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length != 1) return 2;
            string output = Path.GetFullPath(args[0]);
            if (!string.Equals(output.TrimEnd('\\'), AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)) return 2;
            Settings.UseTransientStoreForCurrentProcess();
            Logger.LogPath = Path.Combine(output, "isolated.log");
            Lang.Init(); Dpi.Init();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int passed = 0;
            try
            {
                string[] cases = {
                    "TestIrqEnhancedCapturePath", "TestIrqEnhancedCoreEvidence", "TestIrqEnhancedTransactions", "TestIrqEnhancedPersistence", "TestIrqEnhancedComparison",
                    "TestIrqAdjustmentHistory", "TestIrqRestoreResults", "TestIrqCorePlan",
                    "TestIrqCoreLoadWeighted", "TestIrqCoreLoadMissingAndFailure", "TestIrqCoreLoadLifecycle",
                    "TestIrqCoreLoadDiscardAndRearm", "TestIrqCoreLoadLedger", "TestIrqPinSessionSources",
                    "TestIrqObservationLifecycle", "TestIrqEveryMatchFallback",
                    "TestIrqShortMatchMeasurements", "TestIrqSessionFailureSummary",
                    "TestIrqObservationVerdictProvenance", "TestIrqShortObservationDisplay",
                    "TestIrqDisplayVersionAndValidity", "TestIrqObservationStatusReasons",
                    "TestIrqSessionExclusionReasons", "TestIrqLedgerReadStatus", "TestIrqSessionLedgerRoundtrip" };
                for (int repeat = 0; repeat < 3; repeat++)
                foreach (string name in cases)
                {
                    MethodInfo test = typeof(SelfTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
                    if (test == null) throw new Exception("Missing test " + name);
                    try { test.Invoke(null, test.GetParameters().Length == 0 ? null : new object[] { output }); }
                    catch (TargetInvocationException error) { throw new Exception(name, error.InnerException); }
                    Console.WriteLine("PASS " + (repeat + 1) + " " + name); passed++;
                }
                int ui = IrqCoreLoadUiChecks.Run(output);
                Console.WriteLine("PASS UI assertions=" + ui);
                Console.WriteLine("PASS groups=" + passed + " assertions=" + SelfTests.IrqAssertionCount
                    + " UI=" + ui + " actual_ETW=false actual_PDH=false application_run=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }

    internal static partial class SelfTests
    {
        internal static int IrqAssertionCount;
        private static void Eq<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception("Expected " + expected + ", actual " + actual);
            IrqAssertionCount++;
        }
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Isolated IRQ bench cannot start the application"); }
    }
}
