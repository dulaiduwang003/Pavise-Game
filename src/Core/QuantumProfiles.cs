using System;

namespace PaviseApp
{
    internal static class QuantumProfiles
    {
        internal const string SettingKey = "EnvQuantumSelectedProfileV1";
        internal static readonly int[] Values = { -1, 0x26, 0x25, 0x24, 0x28, 0x16, 0x15, 0x14, 0x18 };
        internal static int Selected
        {
            get {
                if (!Settings.LoadCached(PerformanceActionCatalog.Quantum, false)) return 0;
                int value;
                if (int.TryParse(Settings.LoadStr(SettingKey, "38"), out value))
                    for (int i = 1; i < Values.Length; i++) if (Values[i] == value) return i;
                return 1;
            }
        }
        internal static int Target { get { return Values[Math.Max(1, Selected)]; } }
        internal static string Title(int index)
        {
            if (index == 0) return AutomaticPolicy.Text("恢复原配置", "Restore original configuration");
            if (index == 1) return AutomaticPolicy.Text("游戏推荐 · 短可变 3:1", "Gaming pick · Short variable 3:1");
            if (index == 8) return AutomaticPolicy.Text("后台计算推荐 · 长固定 1:1", "Background pick · Long fixed 1:1");
            QuantumShape shape = QuantumDecoder.Decode(Values[index]);
            return AutomaticPolicy.Text(shape.ShortInterval ? "短" : "长", shape.ShortInterval ? "Short " : "Long ")
                + AutomaticPolicy.Text(shape.VariableLength ? "可变" : "固定", shape.VariableLength ? "variable" : "fixed")
                + " · " + shape.Ratio + ":1";
        }
        internal static string Description(int index)
        {
            if (index == 0) return AutomaticPolicy.Text("撤销这里的修改，回到修改前的设置。", "Undo these changes and return to your previous settings.");
            if (index == 1) return AutomaticPolicy.Text("适合玩游戏、日常操作；优先照顾当前窗口。", "For games and everyday use; favors the active window.");
            if (index == 8) return AutomaticPolicy.Text("适合后台渲染、批量计算；前后台每轮时间相同。", "For background rendering or batch jobs; equal turns.");
            QuantumShape shape = QuantumDecoder.Decode(Values[index]);
            return AutomaticPolicy.Text(shape.ShortInterval ? "更频繁轮流运行；" : "每轮连续运行更久；", shape.ShortInterval ? "Shorter turns; " : "Longer turns; ")
                + (shape.VariableLength
                    ? (shape.Ratio == 1
                        ? AutomaticPolicy.Text("前台游戏与后台浏览器每轮一样长。", "foreground game and background browser get equal turns.")
                        : AutomaticPolicy.Text("前台游戏每轮时间为后台浏览器的 " + shape.Ratio + " 倍。", "foreground game gets " + shape.Ratio + "x the background browser's turn."))
                    : AutomaticPolicy.Text("时间固定，前台游戏与后台浏览器一样长。", "fixed, equal turns for the foreground game and background browser."));
        }
    }
}
