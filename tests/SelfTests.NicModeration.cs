// Pure row-eligibility and ledger parsing only. The registry paths are
// exercised by the app; the type-tolerance below is the exact bug class
// that once made the whole feature a dead path (*IfType is REG_DWORD).
#if PAVISE_SELFTEST
using System;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int nicImChecks;

        internal static int RunNicModerationRegressionTests()
        {
            Action[] tests = { NicImRowEligibility, NicImNumericTolerance, NicImLedgerParsing };
            nicImChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS nic-moderation assertions=" + nicImChecks
                + " registry=untouched settings=untouched windows_shown=false");
            return tests.Length;
        }

        private static void NicCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("NIC moderation regression: " + message);
            Interlocked.Increment(ref nicImChecks);
        }

        private static void NicImRowEligibility()
        {
            // 真机形态 *InterruptModeration=REG_SZ *IfType=REG_DWORD Characteristics=REG_DWORD
            NicCheck(NicModerationTweak.RowEligible("1", 6, 0x84),
                "the real-machine value shapes must pass");
            // 有的 INF 把数字写成字符串 两种形态都要认
            NicCheck(NicModerationTweak.RowEligible("0", "6", "132"),
                "string-typed numeric values must pass");
            NicCheck(!NicModerationTweak.RowEligible(1, 6, 0x84),
                "a non-string moderation value must fail closed");
            NicCheck(!NicModerationTweak.RowEligible("1", 71, 0x84),
                "wireless adapters (IfType 71) must be excluded");
            NicCheck(!NicModerationTweak.RowEligible("1", null, 0x84),
                "a missing IfType must fail closed");
            NicCheck(!NicModerationTweak.RowEligible("1", 6, 0x80),
                "a non-physical adapter (no NCF_PHYSICAL) must be excluded");
            NicCheck(!NicModerationTweak.RowEligible("1", 6, null),
                "missing characteristics must fail closed");
        }

        private static void NicImNumericTolerance()
        {
            int value;
            NicCheck(NicModerationTweak.TryNumeric(6, out value) && value == 6,
                "DWORD values must parse");
            NicCheck(NicModerationTweak.TryNumeric("132", out value) && value == 132,
                "numeric strings must parse");
            NicCheck(!NicModerationTweak.TryNumeric("abc", out value)
                && !NicModerationTweak.TryNumeric(null, out value)
                && !NicModerationTweak.TryNumeric(6L, out value),
                "non-numeric and unexpected shapes must fail closed");
        }

        private static void NicImLedgerParsing()
        {
            string[] ids = NicModerationTweak.ParseList("0001;;0016;");
            NicCheck(ids.Length == 2 && ids[0] == "0001" && ids[1] == "0016",
                "the ledger must drop empty segments and keep order");
            NicCheck(NicModerationTweak.ParseList(null).Length == 0
                && NicModerationTweak.ParseList("").Length == 0,
                "an empty ledger must parse to nothing");
        }
    }
}
#endif
