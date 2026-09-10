// 文件用途 档位取值解析与可见档位回归 纯判定 只读写隔离的自测存储
//   原先这些断言在 SelfTests.ExtremeMode.cs 里 极限档下架后那个文件整体删掉
//   PresetValue 一度没有任何覆盖 这里补回来 并加上两条读档路径必须一致的守卫
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

        // 3 是 1.x 那个极限 5 是 2.2.2 砍掉的极限 都不许有枚举成员认领
        private static void PresetGravestonesStayRejected()
        {
            Eq(false, PresetValue.IsValid(3));
            Eq(false, PresetValue.IsValid(5));
            Eq(true, PresetValue.IsValid(0) && PresetValue.IsValid(1)
                && PresetValue.IsValid(2) && PresetValue.IsValid(4));
            // 5 落电竞不落智能 极限的压制口径与电竞逐字节相同 落智能会放宽压制范围
            Eq(PerformancePreset.Competitive, PresetValue.From(5));
            // 3 那一档比电竞更激进 跟掌机方向相反 接手它等于静默换档 所以落智能
            Eq(PerformancePreset.Standard, PresetValue.From(3));
            Eq(PerformancePreset.Standard, PresetValue.From(-1));
            Eq(PerformancePreset.Standard, PresetValue.From(99));
        }

        // GameMode 读原始设置走 PresetValue.From 快照层走 PolicyCatalog.Canonical
        //   两条路一分叉就会出现界面显示一个档而 Sweep 按另一个档压制
        //   Canonical 的 Enum 分支原本对不认识的值一律回 Fallback 也就是智能
        private static void PresetBothReadPathsAgreeOnGravestones()
        {
            foreach (int raw in new[] { 0, 1, 2, 3, 4, 5, -1, 99 })
            {
                PerformancePreset direct = PresetValue.From(raw);
                string canonical = PolicyCatalog.Canonical(PolicyCatalog.KeyPreset,
                    raw.ToString(CultureInfo.InvariantCulture));
                Eq(PresetText(direct), canonical);
            }
            // 全局设置层同样要一致 存量的极限用户界面与压制必须是同一档
            Settings.SaveStr(PolicyCatalog.KeyPreset, "5");
            Eq(PresetText(PerformancePreset.Competitive),
                PolicyResolver.GlobalValue(PolicyCatalog.KeyPreset));
            Eq(PerformancePreset.Competitive, PolicyResolver.Global().Preset);
            Settings.SaveStr(PolicyCatalog.KeyPreset, "3");
            Eq(PerformancePreset.Standard, PolicyResolver.Global().Preset);
            // 不可解析的垃圾值回智能 与 From 一致
            Settings.SaveStr(PolicyCatalog.KeyPreset, "abc");
            Eq(PerformancePreset.Standard, PolicyResolver.Global().Preset);
        }

        // 掌机只在带电池的机器上列出 但当前就停在这一档时照样列出 否则换不出去
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
            // 极限已下架 任何情况下都不列 存量停在它上面也不列
            Settings.SaveStr(PolicyCatalog.KeyPreset, "5");
            Eq(false, Array.IndexOf(PresetValue.VisibleChoices(), "5") >= 0);
            // 智能 电竞 自定义 三档任何机器都有
            string[] choices = PresetValue.VisibleChoices();
            foreach (PerformancePreset always in new[] { PerformancePreset.Standard,
                PerformancePreset.Competitive, PerformancePreset.Custom })
                Eq(true, Array.IndexOf(choices, PresetText(always)) >= 0);
        }

        // 模式条按下标回填 顺序与取值错位就会选错档
        private static void PresetOrderAndChoicesShareOneSource()
        {
            PerformancePreset[] order = PresetValue.VisibleOrder();
            string[] choices = PresetValue.VisibleChoices();
            Eq(order.Length, choices.Length);
            for (int i = 0; i < order.Length; i++) Eq(PresetText(order[i]), choices[i]);
            // 每一个列出来的取值都必须是合法值 否则回填时会被 Index 的上界挡掉
            foreach (string choice in choices)
            {
                int parsed;
                Eq(true, int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed));
                Eq(true, PresetValue.IsValid(parsed));
            }
            // 自定义排在最后 界面顺序靠它收尾
            Eq(PerformancePreset.Custom, order[order.Length - 1]);
            Eq(PerformancePreset.Standard, order[0]);
        }
    }
}
#endif
