using System;
using System.Globalization;

namespace PaviseApp
{
	internal sealed class HardThreadOriginContext
	{
		internal readonly string Machine;

		internal readonly string Name;

		internal readonly string Boot;

		internal HardThreadOriginContext(string machine, string name, string boot)
		{
			string[] parts = (boot ?? "").Split(':');
			Guid result;
			Guid result2;
			uint result3;
			if (!Guid.TryParseExact(machine, "N", out result) || result == Guid.Empty || string.IsNullOrEmpty(name) || name.Length > 255 || parts.Length != 3 || parts[0] != "boot2" || !Guid.TryParseExact(parts[1], "N", out result2) || result2 == Guid.Empty || !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out result3) || result3 == 0)
			{
				throw new ArgumentException("original Windows protection and kernel domains are unknown");
			}
			Machine = result.ToString("N");
			Name = name.ToLowerInvariant();
			Boot = boot;
		}

		internal bool SameMachine(HardThreadOriginContext other)
		{
			if (other != null && Machine == other.Machine)
			{
				return Name == other.Name;
			}
			return false;
		}
	}
}
