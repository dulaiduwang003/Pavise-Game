using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
	internal static class PresetValue
	{
		public static bool HandheldSupported
		{
			get
			{
				return false;
			}
		}

		public static bool IsCompetitive(PerformancePreset mode)
		{
			if (mode != PerformancePreset.Competitive)
			{
				return mode == PerformancePreset.Extreme;
			}
			return true;
		}

		public static bool IsRetired(PerformancePreset mode)
		{
			if (mode != PerformancePreset.Handheld)
			{
				return mode == PerformancePreset.Extreme;
			}
			return true;
		}

		public static bool SupportsCoreScheduling(PerformancePreset mode)
		{
			return IsValid((int)mode);
		}

		public static bool IsValid(int raw)
		{
			if (raw != 0)
			{
				return raw == 1;
			}
			return true;
		}

		public static bool IsLegacyRaw(int raw)
		{
			if (raw != 2 && raw != 3 && raw != 4 && raw != 5)
			{
				return raw == 6;
			}
			return true;
		}

		public static PerformancePreset From(int raw)
		{
			if (raw == 1 || raw == 5 || raw == 6)
			{
				return PerformancePreset.Competitive;
			}
			return PerformancePreset.Standard;
		}

		public static string MigrationNote(int raw)
		{
			switch (raw)
			{
			case 6:
				return PerformancePolicy.Text("旧极限场景已并入竞技（不继承冻结与 CPU 硬上限）", "Legacy Extreme mapped to Esports (freeze and CPU caps are not inherited)");
			case 4:
				return PerformancePolicy.Text("旧掌机场景已并入智能", "Legacy Handheld mapped to Smart");
			default:
				if (IsLegacyRaw(raw))
				{
					return PerformancePolicy.Text("旧场景值已映射为当前场景", "Legacy scene value mapped to a current scene");
				}
				return null;
			}
		}

		private static PerformancePreset[] Order()
		{
			List<PerformancePreset> list = new List<PerformancePreset>(2);
			list.Add(PerformancePreset.Standard);
			list.Add(PerformancePreset.Competitive);
			return list.ToArray();
		}

		public static string[] VisibleChoices()
		{
			PerformancePreset[] orderResult = Order();
			string[] textEntries = new string[orderResult.Length];
			for (int i = 0; i < orderResult.Length; i++)
			{
				int iSnapshot = i;
				int visibleChoicesValue = (int)orderResult[i];
				textEntries[iSnapshot] = visibleChoicesValue.ToString(CultureInfo.InvariantCulture);
			}
			return textEntries;
		}

		public static PerformancePreset[] VisibleOrder()
		{
			return Order();
		}
	}
}
