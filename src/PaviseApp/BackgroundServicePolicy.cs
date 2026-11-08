using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
	internal static class BackgroundServicePolicy
	{
		internal static bool CandidateName(string name)
		{
			if (!string.Equals(name, "SearchIndexer", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(name, "spoolsv", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		internal static bool SupportedMode(PerformancePreset mode)
		{
			return mode == PerformancePreset.Extreme;
		}

		internal static bool GameRuntimeService(string name)
		{
			if (!string.Equals(name, "GamingServices", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(name, "GamingServicesNet", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		internal static bool Eligible(PerformancePreset mode, ProcEntry process, string imagePath, IList<KeyValuePair<string, int>> services, string windowsRoot)
		{
			if (!SupportedMode(mode) || process == null || process.Pid <= 4 || process.Session != 0 || process.Creation <= 0 || string.IsNullOrEmpty(windowsRoot))
			{
				return false;
			}
			if (GameRuntimeService(process.Name))
			{
				return false;
			}
			if (ThirdPartyImage(imagePath, windowsRoot))
			{
				return true;
			}
			if (services == null || !CandidateName(process.Name))
			{
				return false;
			}
			string b = (string.Equals(process.Name, "SearchIndexer", StringComparison.OrdinalIgnoreCase) ? "WSearch" : "Spooler");
			string right = Path.Combine(windowsRoot, "System32", process.Name + ".exe");
			if (!WhitelistRule.PathEquals(process.Path, right))
			{
				return false;
			}
			bool result = false;
			foreach (KeyValuePair<string, int> service in services)
			{
				if (service.Value == process.Pid)
				{
					if (!string.Equals(service.Key, b, StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					result = true;
				}
			}
			return result;
		}

		internal static bool ThirdPartyImage(string path, string windowsRoot)
		{
			if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(windowsRoot) || path.Length < 3 || path[1] != ':' || path[2] != '\\')
			{
				return false;
			}
			string value = windowsRoot.TrimEnd('\\', '/') + "\\";
			return !path.StartsWith(value, StringComparison.OrdinalIgnoreCase);
		}
	}
}
