// Pure decision and receipt checks; no NVML calls, no driver writes, no windows.
// 显卡锁频已下架 这里只剩收据编解码与同批落地的会话门与 DPI 令牌回归 不碰 NVML
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
                LaptopPerfDefaultsOffAndPresetsRespectTheSwitch,
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

        private static void LaptopPerfDefaultsOffAndPresetsRespectTheSwitch()
        {
            Settings.UseTransientStoreForCurrentProcess();
            PolicyItem item = PolicyCatalog.ItemOf(PolicyCatalog.KeyLaptopPerf);
            GpuClockCheck(!PolicyCatalog.LaptopPerfDefault && item != null
                && item.Fallback == "0" && !PolicyResolver.Global().LaptopPerf,
                "vendor performance mode defaults off in both the catalog and the global snapshot");
            GpuClockCheck(LaptopPerfMode.ShouldActivate(true, true)
                && !LaptopPerfMode.ShouldActivate(false, true)
                && !LaptopPerfMode.ShouldActivate(true, false),
                "the runtime gate did not depend only on the configured value and hardware support");

            Settings.Save(PolicyCatalog.KeyLaptopPerf, false);
            foreach (PerformancePreset preset in new[]
            {
                PerformancePreset.Standard, PerformancePreset.Competitive,
                PerformancePreset.Handheld, PerformancePreset.Custom
            })
            {
                var profile = new GameProfile();
                profile.Overrides[PolicyCatalog.KeyPreset] = ((int)preset).ToString();
                PolicySnapshot snapshot = PolicyResolver.For(profile);
                GpuClockCheck(!snapshot.LaptopPerf,
                    preset + " overrode the user's disabled vendor performance switch");
            }

            Settings.Save("ExtremeUnlocked", true);
            Settings.SaveStr("ExtremeUnlockTicks", "1");
            var extreme = new GameProfile();
            extreme.Overrides[PolicyCatalog.KeyPreset] = ((int)PerformancePreset.Extreme).ToString();
            PolicySnapshot extremeSnapshot = PolicyResolver.For(extreme);
            GpuClockCheck(extremeSnapshot.Preset == PerformancePreset.Extreme && !extremeSnapshot.LaptopPerf,
                "extreme overrode the user's disabled vendor performance switch");
            MethodInfo uiRule = typeof(PanelForm).GetMethod("CfgPresetForces",
                BindingFlags.Static | BindingFlags.NonPublic);
            object[] competitiveArgs =
                { PolicyCatalog.KeyLaptopPerf, PerformancePreset.Competitive, false };
            object[] extremeArgs =
                { PolicyCatalog.KeyLaptopPerf, PerformancePreset.Extreme, false };
            GpuClockCheck(uiRule != null
                && !(bool)uiRule.Invoke(null, competitiveArgs)
                && !(bool)uiRule.Invoke(null, extremeArgs),
                "the per-game UI still locked vendor performance mode in esports or extreme");
            Settings.UseTransientStoreForCurrentProcess();
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
