// File purpose Pure decisions and receipt checks, no NVML calls, no driver writes, no windows
// GPU clock lock has been retired, only receipt codec plus the session gate and DPI token regression that landed in the same batch remain, no NVML
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int gpuClockChecks;

        internal static int RunGpuClockLockRegressionTests()
        {
            Action[] tests =
            {
                GpuClockReceiptRoundTrips,
                LaptopPerfIsRetiredButStillRecoverable,
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

        // Vendor performance mode has been retired, the catalog no longer has this item, only the old-receipt cleanup path remains
        //   Retirement reason: it dialed the power tier through vendor APIs, which conflicts with Handheld tier ceding the power side to vendor tools
        //   And its criteria never included the tier, it dialed even with Handheld tier selected
        private static void LaptopPerfIsRetiredButStillRecoverable()
        {
            Settings.UseTransientStoreForCurrentProcess();
            GpuClockCheck(PolicyCatalog.ItemOf(PolicyCatalog.KeyLaptopPerf) == null,
                "vendor performance mode is still in the policy catalog after retirement");

            Type mode = typeof(LaptopPerfMode);
            foreach (string gone in new[] { "Activate", "ShouldActivate", "SupportedCached" })
                GpuClockCheck(mode.GetMethod(gone,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) == null,
                    "retired vendor performance mode still exposes " + gone);

            // The restore path must stay, an old user's machine may still hold a receipt
            foreach (string kept in new[] { "Restore", "HealFromCrash" })
                GpuClockCheck(mode.GetMethod(kept,
                        BindingFlags.Public | BindingFlags.Static) != null,
                    "vendor performance mode lost its recovery entry " + kept);
            GpuClockCheck(mode.GetProperty("HasResidue",
                    BindingFlags.Public | BindingFlags.Static) != null,
                "vendor performance mode lost its residue probe");

            // The key name is reserved for old-receipt cleanup, don't delete it in passing
            GpuClockCheck(PolicyCatalog.KeyLaptopPerf == "GmLaptopPerfV1",
                "the retired vendor performance key changed and old receipts would be orphaned");
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
