// File purpose Tier value parsing and visible tier regression, pure verdicts, reads/writes only the isolated self-test store
//   these assertions used to live in SelfTests.ExtremeMode.cs, that file was deleted wholesale when Extreme was retired
//   PresetValue had no coverage for a while, restored here plus guards that both tier read paths must agree
#if PAVISE_SELFTEST
using System;
using System.Globalization;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunPresetValueRegressionTests()
        {
            Action[] tests =
            {
                PresetGravestonesStayRejected,
                PresetBothReadPathsAgreeOnGravestones,
                PresetUnsupportedTierIsNotListed,
                PresetOrderAndChoicesShareOneSource
            };
            foreach (Action test in tests)
            {
                Settings.UseTransientStoreForCurrentProcess();
                Settings.SaveStr(PolicyCatalog.KeyPreset, "0");
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            return tests.Length;
        }

        private static string PresetText(PerformancePreset mode)
        {
            return ((int)mode).ToString(CultureInfo.InvariantCulture);
        }

        // 3 is the 1.x Extreme, 5 is the Extreme cut in 2.2.2, no enum member may claim either
        private static void PresetGravestonesStayRejected()
        {
            Eq(false, PresetValue.IsValid(3));
            Eq(false, PresetValue.IsValid(5));
            Eq(true, PresetValue.IsValid(0) && PresetValue.IsValid(1)
                && PresetValue.IsValid(2) && PresetValue.IsValid(4));
            // 5 maps to Esports, not Smart: Extreme's suppression criteria are byte-for-byte identical to Esports, Smart would relax the suppression scope
            Eq(PerformancePreset.Competitive, PresetValue.From(5));
            // Tier 3 was more aggressive than Esports, the opposite direction from Handheld, adopting it would be a silent tier switch, so it maps to Smart
            Eq(PerformancePreset.Standard, PresetValue.From(3));
            Eq(PerformancePreset.Standard, PresetValue.From(-1));
            Eq(PerformancePreset.Standard, PresetValue.From(99));
        }

        // GameMode reads raw settings via PresetValue.From, the snapshot layer goes through PolicyCatalog.Canonical
        //   once the two paths diverge the UI shows one tier while Sweep suppresses by another
        //   the Enum branch of Canonical used to return Fallback, i.e. Smart, for any unknown value
        private static void PresetBothReadPathsAgreeOnGravestones()
        {
            foreach (int raw in new[] { 0, 1, 2, 3, 4, 5, -1, 99 })
            {
                PerformancePreset direct = PresetValue.From(raw);
                string canonical = PolicyCatalog.Canonical(PolicyCatalog.KeyPreset,
                    raw.ToString(CultureInfo.InvariantCulture));
                Eq(PresetText(direct), canonical);
            }
            // The global settings layer must agree too, existing Extreme users must see and suppress by the same tier
            Settings.SaveStr(PolicyCatalog.KeyPreset, "5");
            Eq(PresetText(PerformancePreset.Competitive),
                PolicyResolver.GlobalValue(PolicyCatalog.KeyPreset));
            Eq(PerformancePreset.Competitive, PolicyResolver.Global().Preset);
            Settings.SaveStr(PolicyCatalog.KeyPreset, "3");
            Eq(PerformancePreset.Standard, PolicyResolver.Global().Preset);
            // Unparseable garbage falls back to Smart, consistent with From
            Settings.SaveStr(PolicyCatalog.KeyPreset, "abc");
            Eq(PerformancePreset.Standard, PolicyResolver.Global().Preset);
        }

        // Handheld is listed only on machines with a battery, but still listed when it is the current tier, otherwise there is no way to switch away
        private static void PresetUnsupportedTierIsNotListed()
        {
            string handheld = PresetText(PerformancePreset.Handheld);
            if (PresetValue.HandheldSupported)
            {
                Eq(true, Array.IndexOf(PresetValue.VisibleChoices(), handheld) >= 0);
            }
            else
            {
                Eq(false, Array.IndexOf(PresetValue.VisibleChoices(), handheld) >= 0);
                Settings.SaveStr(PolicyCatalog.KeyPreset, handheld);
                Eq(true, Array.IndexOf(PresetValue.VisibleChoices(), handheld) >= 0);
                Settings.SaveStr(PolicyCatalog.KeyPreset, "0");
                Eq(false, Array.IndexOf(PresetValue.VisibleChoices(), handheld) >= 0);
            }
            // Extreme is retired, never listed under any condition, not even when an existing install is parked on it
            Settings.SaveStr(PolicyCatalog.KeyPreset, "5");
            Eq(false, Array.IndexOf(PresetValue.VisibleChoices(), "5") >= 0);
            // Smart, Esports, Custom are present on every machine
            string[] choices = PresetValue.VisibleChoices();
            foreach (PerformancePreset always in new[] { PerformancePreset.Standard,
                PerformancePreset.Competitive, PerformancePreset.Custom })
                Eq(true, Array.IndexOf(choices, PresetText(always)) >= 0);
        }

        // The mode bar writes back by index, a mismatch between order and values picks the wrong tier
        private static void PresetOrderAndChoicesShareOneSource()
        {
            PerformancePreset[] order = PresetValue.VisibleOrder();
            string[] choices = PresetValue.VisibleChoices();
            Eq(order.Length, choices.Length);
            for (int i = 0; i < order.Length; i++) Eq(PresetText(order[i]), choices[i]);
            // Every listed value must be a valid value, otherwise the write-back is blocked by the Index upper bound
            foreach (string choice in choices)
            {
                int parsed;
                Eq(true, int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed));
                Eq(true, PresetValue.IsValid(parsed));
            }
            // Custom goes last, it closes the UI order
            Eq(PerformancePreset.Custom, order[order.Length - 1]);
            Eq(PerformancePreset.Standard, order[0]);
        }
    }
}
#endif
