// File purpose Pure receipt parsing checks, no PowerShell, no NICs, no writes at all
// Energy Efficient Ethernet regression, only verifies receipt parsing and counting, no NICs
#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int eeeChecks;

        internal static int RunEeeRegressionTests()
        {
            Action[] tests =
            {
                EeeReceiptParsingRecognizesNoneAndReceipt,
                EeeVrrOsGateIsBuildBased
            };
            eeeChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS eee assertions=" + eeeChecks
                + " powershell=untouched adapters=untouched windows_shown=false");
            return tests.Length;
        }

        private static void EeeCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("EEE regression: " + message);
            eeeChecks++;
        }

        private static void EeeReceiptParsingRecognizesNoneAndReceipt()
        {
            EeeCheck(EeeTweak.ExtractReceipt("noise\r\nRECEIPT:以太网|*EEE|1;以太网|EEELinkAdvertisement|1\r\n")
                == "以太网|*EEE|1;以太网|EEELinkAdvertisement|1",
                "the receipt line is returned verbatim without the prefix");
            EeeCheck(EeeTweak.ExtractReceipt("NONE") == "",
                "NONE means nothing was changed and must decode to an empty receipt");
            EeeCheck(EeeTweak.ExtractReceipt("") == null && EeeTweak.ExtractReceipt("garbage") == null,
                "output without a verdict line is a failure, not an empty receipt");
        }

        private static void EeeVrrOsGateIsBuildBased()
        {
            EeeCheck(VrrOptTweak.MinOsBuild == 18362,
                "OS variable refresh rate arrived with Windows 10 1903 and the gate must say so");
        }
    }
}
#endif
