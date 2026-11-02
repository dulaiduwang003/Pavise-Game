using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class CoreMotionCatalog
    {
        internal const int Count = 16;
        internal const int SmartNative = 0, CompetitiveNative = 15;
        private static readonly string[] Keys = { "default-smart", "prism", "turbine", "rift", "pyro", "voxel", "gravity", "thunder", "mechanical", "data", "resonance", "event-horizon", "supernova", "megastructure", "hourglass", "default-competitive" };
        private static readonly string[] Chinese = { "原生智能", "天使圣翼", "等离子风暴", "时空震荡", "星火凤凰", "虚空巨龙", "引力引擎", "恶魔降临", "反应堆阵列", "虚空脉冲", "光子织域", "黑洞星空", "超新星爆发", "星环巨构", "引力潮汐", "原生竞技" };
        private static readonly string[] English = { "Original Smart", "Seraph Wings", "Plasma Storm", "Spacetime Shock", "Starfire Phoenix", "Void Dragon", "Gravity Engine", "Demon Ascendant", "Reactor Array", "Void Pulse", "Photon Weave", "Event Horizon", "Supernova", "Orbital Megastructure", "Gravitational Tide", "Original Esports" };
        private static readonly string[] DescriptionsZh = { "原智能默认动效 · 两种模式均可选择", "分层圣翼开合 · 晶枪升降", "涡轮旋转变距 · 能量粒簇", "舱门分离错位 · 断层呼吸", "独立翼尾摆动 · 焰屑喷发", "盘龙错位摆动 · 蓄能吐息", "星流环绕 · 极轴喷流", "角冠开合 · 下颌展开蓄能", "反应芯公转 · 汇能脉冲", "四瓣闸门开合 · 六边冲击", "棱片交错旋转 · 光流穿梭", "引力弯曲 · 吞噬星流", "星核蓄能 · 裂甲冲击", "三层星环反转 · 浮晶自转", "双极装甲离合 · 碎晶绕行", "原竞技默认动效 · 两种模式均可选择" };
        private static readonly string[] DescriptionsEn = { "Original Smart artwork / available in either mode", "Articulated wing tiers / rising crystal lance", "Variable-pitch rotor / energy packets", "Separating bulkheads / breathing spatial fracture", "Independent wing and tail sweep / ember ejection", "Articulated coiled armor / charged breath", "Orbiting stars / polar jets", "Articulated horns and jaw / power buildup", "Orbiting reactor cells / inward energy feed", "Opening iris shutters / hexagonal shock fronts", "Counter-turning optical facets / traveling light", "Bent starlight / falling star streams", "Stellar charging / armored burst", "Counter-rotating ring tiers / spinning crystal", "Separating tidal poles / orbiting crystal fragments", "Original Esports artwork / available in either mode" };
        internal static bool Valid(int id) { return id >= 0 && id < Count; }
        internal static string Key(int id) { return Keys[Valid(id) ? id : 0]; }
        internal static int Parse(string key) { int id = Array.IndexOf(Keys, key); return id < 0 ? SmartNative : id; }
        internal static int DefaultFor(PerformancePreset mode) { return mode == PerformancePreset.Competitive ? CompetitiveNative : SmartNative; }
        internal static int Resolve(string key, PerformancePreset mode)
        { int id = Array.IndexOf(Keys, key); return id < 0 ? DefaultFor(mode) : id; }
        internal static bool IsNative(int id) { return id == SmartNative || id == CompetitiveNative; }
        internal static int DisplayId(int index) { return index == 0 ? SmartNative : index == 1 ? CompetitiveNative : index - 1; }
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
                    // Legacy "default" follows its former mode; explicit native tokens are freely shared.
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
