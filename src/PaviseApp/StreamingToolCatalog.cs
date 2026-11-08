using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
	internal static class StreamingToolCatalog
	{
		// Streaming roots use publisher-verified executable names.
		// Generic encoders and browser hosts do not establish stream ownership.
		private static readonly HashSet<string> ProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"obs32", "obs64", "obs-browser-page", "obs-ffmpeg-mux",
			"livehime", "直播伴侣", "直播伴侣 Launcher", "kwaiLive", "DYTool", "DYToolEx", "dyClientLancher", "DYffmpeg-mux", "HuyaPresenter",
			"Streamlabs Desktop", "Streamlabs OBS", "XSplit.Core", "PRISMLiveStudio",
			"TikTok LIVE Studio", "MeldStudio", "vMix64", "Wirecast"
		};

		private static readonly string[] Products = new string[]
		{
			"OBS Studio", "哔哩哔哩直播姬", "抖音直播伴侣", "快手直播伴侣", "斗鱼直播伴侣", "虎牙开播端",
			"Streamlabs Desktop", "XSplit Broadcaster", "PRISM Live Studio", "TikTok LIVE Studio", "Meld Studio", "vMix", "Wirecast"
		};

		internal static bool IsStreamingToolProcess(string name, string imagePath)
		{
			string normalizedName = WhitelistRule.NormalizeName(name);
			if (!ProcessNames.Contains(normalizedName)) return false;
			string path = WhitelistRule.NormalizeImagePath(imagePath);
			// Require a verified local absolute executable to establish a new streaming family.
			if (path.Length < 4 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
			try
			{
				return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
					&& string.Equals(normalizedName, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		internal static string[] ProcessNamesForDisplay()
		{
			string[] names = new string[ProcessNames.Count];
			ProcessNames.CopyTo(names);
			Array.Sort(names, StringComparer.OrdinalIgnoreCase);
			return names;
		}

		internal static string[] ProductsForDisplay()
		{
			return (string[])Products.Clone();
		}
	}
}
