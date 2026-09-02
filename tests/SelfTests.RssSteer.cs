// Pure range/parse checks; no PowerShell execution, no adapter access, no writes.
// RSS 引导回归 只验证目标区间推导和收据解析 不碰网卡
#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int rssSteerChecks;

        internal static int RunRssSteerRegressionTests()
        {
            Action[] tests =
            {
                RssSteerRangeComesFromThrottleMask,
                RssSteerRangeRefusesUnfitTopologies,
                RssSteerReceiptParsing
            };
            rssSteerChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS rss-steer assertions=" + rssSteerChecks
                + " powershell=untouched adapters=untouched windows_shown=false");
            return tests.Length;
        }

        private static void RssCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("RSS steer regression: " + message);
            rssSteerChecks++;
        }

        private static void RssSteerRangeComesFromThrottleMask()
        {
            int baseCpu, maxCpu;
            // 13900K 形状 压制核是逻辑 16-31 的能效核
            RssCheck(RssSteer.TryTargetRange(0xFFFF0000UL, 0xFFFFFFFFUL, false, out baseCpu, out maxCpu)
                && baseCpu == 16 && maxCpu == 31,
                "the steering range is the throttle mask's lowest and highest logical cpu");
            RssCheck(RssSteer.TryTargetRange(0x0F0UL, 0xFFFUL, false, out baseCpu, out maxCpu)
                && baseCpu == 4 && maxCpu == 7,
                "a mid-mask throttle range maps to its own bounds");
        }

        private static void RssSteerRangeRefusesUnfitTopologies()
        {
            int baseCpu, maxCpu;
            RssCheck(!RssSteer.TryTargetRange(0, 0xFFUL, false, out baseCpu, out maxCpu),
                "no throttle cores means nowhere to steer");
            RssCheck(!RssSteer.TryTargetRange(0xFFUL, 0xFFUL, false, out baseCpu, out maxCpu),
                "a throttle mask covering every core steers nothing away");
            RssCheck(!RssSteer.TryTargetRange(0x10UL, 0xFFUL, false, out baseCpu, out maxCpu),
                "a single-core range cannot host RSS queues");
            RssCheck(!RssSteer.TryTargetRange(0xF0UL, 0xFFUL, true, out baseCpu, out maxCpu),
                "multi-group machines are out of scope");
        }

        private static void RssSteerReceiptParsing()
        {
            RssCheck(RssSteer.ExtractReceipt("RECEIPT:Ethernet|0|63") == "Ethernet|0|63",
                "the receipt line carries the adapter records verbatim");
            RssCheck(RssSteer.ExtractReceipt("junk\r\nRECEIPT:A|0|63;B|2|31\r\n") == "A|0|63;B|2|31",
                "the receipt is found among other output lines");
            RssCheck(RssSteer.ExtractReceipt("NONE") == "",
                "no eligible adapter yields an empty receipt, not a failure");
            RssCheck(RssSteer.ExtractReceipt("garbage only") == null,
                "output without a marker means the run failed");
            RssCheck(RssSteer.ExtractReceipt(null) == null,
                "missing output means the run failed");
        }
    }
}
#endif
