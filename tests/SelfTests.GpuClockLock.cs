// Pure decision and receipt checks; no NVML calls, no driver writes, no windows.
// 显卡频率锁定回归 只验证档位门 钉频决策和收据编解码 不碰 NVML
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int gpuClockChecks;

        internal static int RunGpuClockLockRegressionTests()
        {
            Action[] tests =
            {
                GpuClockTierForcesOnlyEsportsAndExtremeOnDesktops,
                GpuClockPlanPinsBothBoundsAtMax,
                GpuClockReceiptRoundTrips,
                LaptopPerfTierForcesOnlyEsportsAndExtremeOnLaptops,
                SessionGatesAreDecidedByEvidence,
                DpiLayerTokensMergeWithoutClobbering
            };
            gpuClockChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS gpu-clock-lock assertions=" + gpuClockChecks
                + " nvml=untouched driver=untouched windows_shown=false");
            return tests.Length;
        }

        private static void GpuClockCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("GPU clock lock regression: " + message);
            gpuClockChecks++;
        }

        private static void GpuClockTierForcesOnlyEsportsAndExtremeOnDesktops()
        {
            GpuClockCheck(GpuClockLock.ForcedByTier(PerformancePreset.Competitive, false)
                && GpuClockLock.ForcedByTier(PerformancePreset.Extreme, false),
                "esports and extreme lock the switch on desktops");
            GpuClockCheck(!GpuClockLock.ForcedByTier(PerformancePreset.Standard, false)
                && !GpuClockLock.ForcedByTier(PerformancePreset.Handheld, false)
                && !GpuClockLock.ForcedByTier(PerformancePreset.Custom, false),
                "smart, handheld and custom leave it to the user");
            GpuClockCheck(!GpuClockLock.ForcedByTier(PerformancePreset.Competitive, true)
                && !GpuClockLock.ForcedByTier(PerformancePreset.Extreme, true),
                "a battery means a shared power budget; no tier forces the lock there");
        }

        private static void GpuClockPlanPinsBothBoundsAtMax()
        {
            uint lo, hi;
            GpuClockCheck(GpuClockLock.TryPlanClocks(2520, out lo, out hi) && lo == 2520 && hi == 2520,
                "the lock pins the lower and upper bound at the machine maximum");
            GpuClockCheck(!GpuClockLock.TryPlanClocks(0, out lo, out hi),
                "an unreadable maximum never produces a plan");
        }

        private static void LaptopPerfTierForcesOnlyEsportsAndExtremeOnLaptops()
        {
            GpuClockCheck(LaptopPerfMode.ForcedByTier(PerformancePreset.Competitive, true)
                && LaptopPerfMode.ForcedByTier(PerformancePreset.Extreme, true),
                "esports and extreme lock the vendor performance mode on laptops");
            GpuClockCheck(!LaptopPerfMode.ForcedByTier(PerformancePreset.Handheld, true)
                && !LaptopPerfMode.ForcedByTier(PerformancePreset.Standard, true),
                "handhelds and the smart tier leave the vendor mode alone");
            GpuClockCheck(!LaptopPerfMode.ForcedByTier(PerformancePreset.Competitive, false),
                "a desktop has no vendor performance mode to switch");
        }

        private static void SessionGatesAreDecidedByEvidence()
        {
            GpuClockCheck(LinkMetricTweak.Decide(true, true), "wired gateway up with a wireless default route needs the repair");
            GpuClockCheck(!LinkMetricTweak.Decide(true, false) && !LinkMetricTweak.Decide(false, true),
                "a wired default route or no wired gateway means nothing to repair");
            GpuClockCheck(NvVrrWindowed.ShouldExpand(1) && !NvVrrWindowed.ShouldExpand(0) && !NvVrrWindowed.ShouldExpand(2),
                "only fullscreen-only G-SYNC is expanded to windowed");
            GpuClockCheck(IntelEndurance.Wanted(1) && IntelEndurance.Wanted(2) && !IntelEndurance.Wanted(0),
                "endurance gaming is only turned off when it is on or automatic");
            var range = new AdlxIntRange { Min = 500, Max = 2400 };
            GpuClockCheck(AdlxTweaks.GfxMinTarget(2500, range) == 2400 && AdlxTweaks.GfxMinTarget(2000, range) == 2000
                && AdlxTweaks.GfxMinTarget(100, range) == 500,
                "the AMD minimum clock target is the current maximum clamped into the legal range");
        }

        private static void DpiLayerTokensMergeWithoutClobbering()
        {
            string merged = DpiTweak.AddToken("~ DISABLEDXMAXIMIZEDWINDOWEDMODE");
            GpuClockCheck(merged == "~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE" && DpiTweak.HasToken(merged),
                "adding the DPI token keeps the FSO token and the leading marker");
            GpuClockCheck(DpiTweak.RemoveToken(merged) == "~ DISABLEDXMAXIMIZEDWINDOWEDMODE",
                "removing the DPI token leaves the other token intact");
            GpuClockCheck(DpiTweak.RemoveToken("~ HIGHDPIAWARE") == "",
                "removing the last token clears the value so the registry entry is deleted");
            GpuClockCheck(DpiTweak.AddToken(null) == "~ HIGHDPIAWARE", "an absent value gains the marker and the token");
        }

        private static void GpuClockReceiptRoundTrips()
        {
            var locked = new List<KeyValuePair<string, uint>>
            {
                new KeyValuePair<string, uint>("GPU-1111", 2520),
                new KeyValuePair<string, uint>("GPU-2222", 1980),
            };
            string raw = GpuClockLock.EncodeReceipt(locked);
            List<KeyValuePair<string, uint>> back = GpuClockLock.DecodeReceipt(raw);
            GpuClockCheck(back.Count == 2 && back[0].Key == "GPU-1111" && back[0].Value == 2520
                && back[1].Key == "GPU-2222" && back[1].Value == 1980,
                "two locked devices survive the round trip in order");
            GpuClockCheck(GpuClockLock.DecodeReceipt("").Count == 0
                && GpuClockLock.DecodeReceipt("junk").Count == 0
                && GpuClockLock.DecodeReceipt("GPU-1|abc").Count == 0,
                "empty and malformed receipts decode to nothing instead of guessing");
        }
    }
}
#endif
