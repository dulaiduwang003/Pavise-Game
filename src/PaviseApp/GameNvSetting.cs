using System.Globalization;
using System.IO;

namespace PaviseApp
{
	internal sealed class GameNvSetting
	{
		internal readonly bool Resolved;

		internal readonly uint Value;

		internal readonly uint Location;

		internal readonly uint CurrentPredefined;

		internal readonly uint PredefinedValid;

		internal readonly uint Predefined;

		internal bool UserOverride
		{
			get
			{
				if (Resolved && Location == 0)
				{
					return CurrentPredefined == 0;
				}
				return false;
			}
		}

		internal string Text
		{
			get
			{
				return string.Join(",", Resolved ? "1" : "0", Value.ToString(CultureInfo.InvariantCulture), Location.ToString(CultureInfo.InvariantCulture), CurrentPredefined.ToString(CultureInfo.InvariantCulture), PredefinedValid.ToString(CultureInfo.InvariantCulture), Predefined.ToString(CultureInfo.InvariantCulture));
			}
		}

		internal GameNvSetting(bool resolved, uint value, uint location, uint currentPredefined, uint predefinedValid, uint predefined)
		{
			if (location > 3 || currentPredefined > 1 || predefinedValid > 1)
			{
				throw new InvalidDataException("Unknown NVIDIA setting provenance");
			}
			Resolved = resolved;
			Value = value;
			Location = location;
			CurrentPredefined = currentPredefined;
			PredefinedValid = predefinedValid;
			Predefined = predefined;
		}

		internal static GameNvSetting Parse(string value)
		{
			string[] valueParts = value.Split(',');
			if (valueParts.Length != 6 || (valueParts[0] != "0" && valueParts[0] != "1"))
			{
				throw new InvalidDataException("Invalid game NVIDIA original setting");
			}
			GameNvSetting gameNvSetting = new GameNvSetting(valueParts[0] == "1", uint.Parse(valueParts[1], CultureInfo.InvariantCulture), uint.Parse(valueParts[2], CultureInfo.InvariantCulture), uint.Parse(valueParts[3], CultureInfo.InvariantCulture), uint.Parse(valueParts[4], CultureInfo.InvariantCulture), uint.Parse(valueParts[5], CultureInfo.InvariantCulture));
			if (gameNvSetting.Text != value)
			{
				throw new InvalidDataException("Noncanonical game NVIDIA original setting");
			}
			return gameNvSetting;
		}
	}
}
