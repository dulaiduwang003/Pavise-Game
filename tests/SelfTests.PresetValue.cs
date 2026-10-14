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

        private static void PresetGravestonesStayRejected()
        {
            Eq(false, PresetValue.IsValid(3));
            Eq(false, PresetValue.IsValid(5));
            Eq(true, PresetValue.IsValid(0) && PresetValue.IsValid(1)
                && !PresetValue.IsValid(2) && PresetValue.IsValid(4));
            Eq(PerformancePreset.Competitive, PresetValue.From(5));
            Eq(PerformancePreset.Standard, PresetValue.From(3));
            Eq(PerformancePreset.Standard, PresetValue.From(-1));
            Eq(PerformancePreset.Standard, PresetValue.From(99));
        }

        private static void PresetBothReadPathsAgreeOnGravestones()
        {
            foreach (int raw in new[] { 0, 1, 2, 3, 4, 5, 6, -1, 99 })
            {
                PerformancePreset direct = PresetValue.From(raw);
                string canonical = PolicyCatalog.Canonical(PolicyCatalog.KeyPreset,
                    raw.ToString(CultureInfo.InvariantCulture));
                Eq(raw == 2 ? "2" : PresetText(direct), canonical);
            }
            Settings.SaveStr(PolicyCatalog.KeyPreset, "5");
            Eq(PresetText(PerformancePreset.Competitive),
                PolicyResolver.GlobalValue(PolicyCatalog.KeyPreset));
            Eq(PerformancePreset.Competitive, PolicyResolver.Global().Preset);
            Settings.SaveStr(PolicyCatalog.KeyPreset, "3");
            Eq(PerformancePreset.Standard, PolicyResolver.Global().Preset);
            Settings.SaveStr(PolicyCatalog.KeyPreset, "abc");
            Eq(PerformancePreset.Standard, PolicyResolver.Global().Preset);
        }

        private static void PresetUnsupportedTierIsNotListed()
        {
            string[] choices = PresetValue.VisibleChoices();
            Eq(4, choices.Length);
            Eq("0", choices[0]); Eq("1", choices[1]); Eq("6", choices[2]); Eq("4", choices[3]);
            foreach (string retired in new[] { "2", "3", "5" })
            {
                Settings.SaveStr(PolicyCatalog.KeyPreset, retired);
                Eq(false, Array.IndexOf(PresetValue.VisibleChoices(), retired) >= 0);
            }
        }

        private static void PresetOrderAndChoicesShareOneSource()
        {
            PerformancePreset[] order = PresetValue.VisibleOrder();
            string[] choices = PresetValue.VisibleChoices();
            Eq(order.Length, choices.Length);
            for (int i = 0; i < order.Length; i++) Eq(PresetText(order[i]), choices[i]);
            foreach (string choice in choices)
            {
                int parsed;
                Eq(true, int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed));
                Eq(true, PresetValue.IsValid(parsed));
            }
            Eq(PerformancePreset.Handheld, order[order.Length - 1]);
            Eq(PerformancePreset.Standard, order[0]);
        }
    }
}
#endif
