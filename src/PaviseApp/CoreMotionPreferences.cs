using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class CoreMotionCatalog
    {
        internal const int Count = 17;
        internal const int SmartNative = 0, CompetitiveNative = 15, ExtremeNative = 16;
        // 新动效追加存储编号，显示位置单独排列，避免改变已保存的选择。
        private static readonly string[] Keys = { "default-smart", "prism", "turbine", "rift", "pyro", "voxel", "gravity", "thunder", "mechanical", "data", "resonance", "event-horizon", "supernova", "megastructure", "hourglass", "default-competitive", "default-extreme" };
        private static readonly string[] Chinese = { "原生智能", "日冕之蚀", "磁暴之心", "折跃星门", "不死焰羽", "深海之灵", "量子双星", "星海游鲸", "星舰引擎", "奇点之花", "光子魔方", "黑洞星空", "超新星爆发", "浑天星仪", "时之沙漏", "原生竞技", "原生极限" };
        private static readonly string[] English = { "Original Smart", "Solar Eclipse", "Plasma Heart", "Warp Gate", "Firebird", "Abyssal Jellyfish", "Quantum Binary", "Astral Whale", "Starship Engine", "Singularity Bloom", "Photon Cube", "Event Horizon", "Supernova", "Celestial Orrery", "Chronoglass", "Original Esports", "Original Extreme" };
        private static readonly string[] DescriptionsZh = { "原智能默认动效 · 两种模式均可选择", "月轮掠影 · 日冕流动", "等离子张弛 · 磁弧游走", "多层透视门廊 · 光流跃迁", "羽翼舒展 · 流焰长尾", "透明伞盖呼吸 · 触须漂游", "双星绕行 · 量子星流", "鲸尾摆动 · 星海漫游", "双翼引擎 · 脉冲喷流", "层叠花瓣开合 · 星核绽放", "立方体自转 · 光子沿边穿行", "引力弯曲 · 吞噬星流", "星核呼吸 · 扩散冲击波", "立体星轨交错 · 卫星公转", "玻璃舱轻摆 · 星砂流转", "原竞技默认动效 · 两种模式均可选择", "原极限模式动效 · 晶核与三重环轨" };
        private static readonly string[] DescriptionsEn = { "Original Smart artwork / available in either mode", "Drifting lunar silhouette / flowing corona", "Breathing plasma / wandering magnetic arcs", "Layered perspective gates / traveling light", "Sweeping wings / flowing flame tails", "Breathing translucent bell / drifting tentacles", "Orbiting binary stars / quantum streams", "Sweeping flukes / drifting through starlight", "Twin engine wings / pulsing exhaust", "Layered petals / blooming stellar core", "Rotating cube / traveling edge photons", "Bent starlight / falling star streams", "Breathing stellar core / expanding shock waves", "Crossing spatial orbits / revolving satellites", "Swaying glass chambers / flowing stardust", "Original Esports artwork / available in either mode", "Original Extreme artwork / crystal core and triple orbits" };
        internal static bool Valid(int id) { return id >= 0 && id < Count; }
        internal static string Key(int id) { return Keys[Valid(id) ? id : 0]; }
        internal static int Parse(string key) { int id = Array.IndexOf(Keys, key); return id < 0 ? SmartNative : id; }
        internal static int DefaultFor(PerformancePreset mode) { return mode == PerformancePreset.Competitive ? CompetitiveNative : SmartNative; }
        internal static int Resolve(string key, PerformancePreset mode)
        { int id = Array.IndexOf(Keys, key); return id < 0 ? DefaultFor(mode) : id; }
        internal static bool IsNative(int id) { return id == SmartNative || id == CompetitiveNative || id == ExtremeNative; }
        internal static int DisplayId(int index) { return index == 0 ? SmartNative : index == 1 ? CompetitiveNative : index == 2 ? ExtremeNative : index - 2; }
        internal static string Name(int id) { id = Valid(id) ? id : 0; return PerformancePolicy.Text(Chinese[id], English[id]); }
        internal static string Description(int id) { id = Valid(id) ? id : 0; return PerformancePolicy.Text(DescriptionsZh[id], DescriptionsEn[id]); }
        internal static string ChineseChoices { get { return string.Join("/", Chinese); } }
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
                    // 旧版 default 沿用原模式；显式原生标识可在模式间共用。
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
