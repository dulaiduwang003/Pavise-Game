using System;

namespace PaviseApp
{
	internal static class QuantumProfiles
	{
		internal const string SettingKey = "EnvQuantumSelectedProfileV1";

		internal static readonly int[] Values = new int[9] { -1, 38, 37, 36, 40, 22, 21, 20, 24 };

		internal static int Selected
		{
			get
			{
				if (!Settings.LoadCached("EnvQuantumProfileV1", false))
				{
					return 0;
				}
				int result;
				if (int.TryParse(Settings.LoadStr("EnvQuantumSelectedProfileV1", "38"), out result))
				{
					for (int i = 1; i < Values.Length; i++)
					{
						if (Values[i] == result)
						{
							return i;
						}
					}
				}
				return 1;
			}
		}

		internal static int Target
		{
			get
			{
				return Values[Math.Max(1, Selected)];
			}
		}

		internal static string Title(int index)
		{
			switch (index)
			{
			case 0:
				return PerformancePolicy.Text("恢复原配置", "Restore original configuration");
			case 1:
				return PerformancePolicy.Text("游戏推荐 · 短可变 3:1", "Gaming pick · Short variable 3:1");
			case 8:
				return PerformancePolicy.Text("后台计算推荐 · 长固定 1:1", "Background pick · Long fixed 1:1");
			default:
			{
				QuantumShape quantumShape = QuantumDecoder.Decode(Values[index]);
				return PerformancePolicy.Text(quantumShape.ShortInterval ? "短" : "长", quantumShape.ShortInterval ? "Short " : "Long ") + PerformancePolicy.Text(quantumShape.VariableLength ? "可变" : "固定", quantumShape.VariableLength ? "variable" : "fixed") + " · " + quantumShape.Ratio + ":1";
			}
			}
		}

		internal static string Description(int index)
		{
			switch (index)
			{
			case 0:
				return PerformancePolicy.Text("撤销这里的修改，回到修改前的设置。", "Undo these changes and return to your previous settings.");
			case 1:
				return PerformancePolicy.Text("适合玩游戏、日常操作；优先照顾当前窗口。", "For games and everyday use; favors the active window.");
			case 8:
				return PerformancePolicy.Text("适合后台渲染、批量计算；前后台每轮时间相同。", "For background rendering or batch jobs; equal turns.");
			default:
			{
				QuantumShape quantumShape = QuantumDecoder.Decode(Values[index]);
				return PerformancePolicy.Text(quantumShape.ShortInterval ? "更频繁轮流运行；" : "每轮连续运行更久；", quantumShape.ShortInterval ? "Shorter turns; " : "Longer turns; ") + ((!quantumShape.VariableLength) ? PerformancePolicy.Text("时间固定，前台游戏与后台浏览器一样长。", "fixed, equal turns for the foreground game and background browser.") : ((quantumShape.Ratio == 1) ? PerformancePolicy.Text("前台游戏与后台浏览器每轮一样长。", "foreground game and background browser get equal turns.") : PerformancePolicy.Text("前台游戏每轮时间为后台浏览器的 " + quantumShape.Ratio + " 倍。", "foreground game gets " + quantumShape.Ratio + "x the background browser's turn.")));
			}
			}
		}
	}
}
