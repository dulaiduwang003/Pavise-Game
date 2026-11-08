using System.Collections.Generic;

namespace PaviseApp
{
	internal static class ThreadBoostSelection
	{
		internal const string Key = "GmThreadBoostStrategyV1";

		internal const string Off = "off";

		internal const string On = "on";

		internal const string Candidate = "candidate";

		internal const string Frame = "frame";

		internal static readonly string[] Choices = new string[2] { "off", "on" };

		internal static string RawGlobal
		{
			get
			{
				string selected;
				if (!TryReadGlobal(out selected))
				{
					return "off";
				}
				return selected;
			}
		}

		internal static bool IsLegacy(string key)
		{
			if (!(key == "GmRenderLane"))
			{
				return key == "FrameCoordination";
			}
			return true;
		}

		internal static bool Valid(string value)
		{
			switch (value)
			{
			default:
				return value == "frame";
			case "off":
			case "on":
			case "candidate":
				return true;
			}
		}

		internal static string Normalize(string value)
		{
			switch (value)
			{
			default:
				return "off";
			case "on":
			case "candidate":
			case "frame":
				return "on";
			}
		}

		internal static bool IsOn(string value)
		{
			return Normalize(value) == "on";
		}

		internal static bool TryReadGlobal(out string selected)
		{
			selected = "off";
			string value;
			if (!Settings.TryLoadStr("GmThreadBoostStrategyV1", out value))
			{
				return false;
			}
			if (!string.IsNullOrEmpty(value))
			{
				if (!Valid(value))
				{
					return false;
				}
				selected = Normalize(value);
				return true;
			}
			bool value2;
			bool value3;
			if (!TryLegacyFlag("GmRenderLane", true, out value2) || !TryLegacyFlag("FrameCoordination", false, out value3))
			{
				return false;
			}
			selected = LegacyValue(value2, value3);
			return true;
		}

		private static bool TryLegacyFlag(string key, bool fallback, out bool value)
		{
			return Settings.TryLoadBool(key, fallback, out value);
		}

		internal static string LegacyValue(bool candidate, bool frame)
		{
			if (!candidate && !frame)
			{
				return "off";
			}
			return "on";
		}

		internal static bool Save(string value)
		{
			if (Valid(value))
			{
				return Settings.SaveStr("GmThreadBoostStrategyV1", Normalize(value));
			}
			return false;
		}

		internal static bool MigrateOverrides(IDictionary<string, string> values)
		{
			string value;
			bool value6 = values.TryGetValue("GmRenderLane", out value);
			string value2;
			bool value7 = values.TryGetValue("FrameCoordination", out value2);
			if (!value6 && !value7)
			{
				return true;
			}
			string value3;
			if (!values.TryGetValue("GmThreadBoostStrategyV1", out value3))
			{
				bool value4 = value == "1";
				bool value5 = value2 == "1";
				if (!value6 && !TryLegacyFlag("GmRenderLane", true, out value4))
				{
					return false;
				}
				if (!value7 && !TryLegacyFlag("FrameCoordination", false, out value5))
				{
					return false;
				}
				values["GmThreadBoostStrategyV1"] = LegacyValue(value4, value5);
			}
			values.Remove("GmRenderLane");
			values.Remove("FrameCoordination");
			return true;
		}

		internal static string Title(int index)
		{
			if (index != 0)
			{
				return PerformancePolicy.Text("开启", "On");
			}
			return PerformancePolicy.Text("关闭", "Off");
		}
	}
}
