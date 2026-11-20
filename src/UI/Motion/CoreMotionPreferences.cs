using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class CoreMotionCatalog
    {
        internal const int SmartNative = 0, CompetitiveNative = 15, ExtremeNative = 16;
        internal const int AstralWhale = 7, EventHorizon = 11;

        private sealed class Motion
        {
            internal readonly int Id;
            internal readonly string Key, Chinese, English, DescriptionZh, DescriptionEn;

            internal Motion(int id, string key, string chinese, string english, string descriptionZh, string descriptionEn)
            {
                Id = id;
                Key = key;
                Chinese = chinese;
                English = english;
                DescriptionZh = descriptionZh;
                DescriptionEn = descriptionEn;
            }
        }

        // 列表按界面顺序排列 编号和存储键各自保持稳定
        private static readonly Motion[] Choices =
        {
            new Motion(SmartNative, "default-smart", "原生智能", "Original Smart",
                "原智能默认动效 · 两种模式均可选择", "Original Smart artwork / available in either mode"),
            new Motion(CompetitiveNative, "default-competitive", "原生竞技", "Original Esports",
                "原竞技默认动效 · 两种模式均可选择", "Original Esports artwork / available in either mode"),
            new Motion(ExtremeNative, "default-extreme", "原生极限", "Original Extreme",
                "原极限模式动效 · 晶核与三重环轨", "Original Extreme artwork / crystal core and triple orbits"),
            new Motion(AstralWhale, "thunder", "星海游鲸", "Astral Whale",
                "鲸尾摆动 · 星海漫游", "Sweeping flukes / drifting through starlight"),
            new Motion(EventHorizon, "event-horizon", "黑洞星空", "Event Horizon",
                "引力弯曲 · 吞噬星流", "Bent starlight / falling star streams")
        };

        private static Motion Find(int id)
        {
            foreach (Motion motion in Choices)
            {
                if (motion.Id == id) return motion;
            }
            return null;
        }

        internal static int Count { get { return Choices.Length; } }
        internal static bool Valid(int id) { return Find(id) != null; }
        internal static string Key(int id) { return (Find(id) ?? Choices[0]).Key; }
        internal static int Parse(string key) { return Resolve(key, PerformancePreset.Standard); }
        internal static int DefaultFor(PerformancePreset mode) { return mode == PerformancePreset.Competitive ? CompetitiveNative : SmartNative; }
        internal static int Resolve(string key, PerformancePreset mode)
        {
            foreach (Motion motion in Choices)
            {
                if (motion.Key == key) return motion.Id;
            }
            return DefaultFor(mode);
        }

        internal static bool IsNative(int id) { return id == SmartNative || id == CompetitiveNative || id == ExtremeNative; }
        internal static int DisplayId(int index) { return Choices[index].Id; }
        internal static string Name(int id)
        {
            Motion motion = Find(id) ?? Choices[0];
            return PerformancePolicy.Text(motion.Chinese, motion.English);
        }
        internal static string Description(int id)
        {
            Motion motion = Find(id) ?? Choices[0];
            return PerformancePolicy.Text(motion.DescriptionZh, motion.DescriptionEn);
        }
        internal static string ChineseChoices { get { return string.Join("/", Array.ConvertAll(Choices, motion => motion.Chinese)); } }
    }

    internal static class CoreMotionPreferences
    {
        internal const string KeyPrefix = "CoreMotionV1_";
        private static readonly object gate = new object();
        private static readonly int[] selected = new int[2];
        private static int generation = -1;
        internal static string PreferenceKey(PerformancePreset mode) { return KeyPrefix + ((int)mode).ToString(CultureInfo.InvariantCulture); }
        internal static int Get(PerformancePreset mode)
        {
            if (!PresetValue.IsValid((int)mode)) return 0;
            lock (gate)
            {
                int current = Settings.MutationGeneration;
                if (generation != current)
                {
                    // 旧版 default 沿用原模式 显式原生标识可在模式间共用
                    for (int i = 0; i < selected.Length; i++) selected[i] = CoreMotionCatalog.Resolve(Settings.LoadStr(PreferenceKey((PerformancePreset)i), "default"), (PerformancePreset)i);
                    generation = current;
                }
                return selected[(int)mode];
            }
        }
        internal static bool TrySave(PerformancePreset mode, int id)
        {
            if (!PresetValue.IsValid((int)mode) || !CoreMotionCatalog.Valid(id)) return false;
            lock (gate)
            {
                if (!Settings.SaveStr(PreferenceKey(mode), CoreMotionCatalog.Key(id))) return false;
                generation = -1;
                return Get(mode) == id;
            }
        }
    }
}
