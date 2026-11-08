using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
	internal static class OverlayHostCatalog
	{
		private static readonly HashSet<string> GamePlatformProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "steam", "gameoverlayui", "steamwebhelper" };

		private static readonly HashSet<string> ProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment", "DiscordHookHelper", "DiscordHookHelper64", "NVIDIA Share", "NVIDIA Overlay", "nvsphelper", "nvsphelper64",
			"RTSS", "RTSSHooksLoader", "RTSSHooksLoader64", "MSIAfterburner", "EncoderServer", "EncoderServer64", "obs32", "obs64", "obs-browser-page", "obs-ffmpeg-mux",
			"GameBar", "GameBarElevatedFT", "GameBarFTServer", "GameBarPresenceWriter", "Overwolf", "OverwolfBrowser", "OverwolfHelper", "OverwolfHelper64", "NahimicService", "NahimicSvc32",
			"NahimicSvc64", "fraps"
		};

		internal static bool ShouldProtectProcess(string name, string imagePath, bool protectGamePlatformHosts)
		{
			if (StreamingToolCatalog.IsStreamingToolProcess(name, imagePath)) return true;
			string name2 = WhitelistRule.NormalizeName(name);
			if (!ProcessNames.Contains(name2) && (!protectGamePlatformHosts || !PlatformRuntimePolicy.Enabled("steam") || !GamePlatformProcessNames.Contains(name2)))
			{
				return false;
			}
			string imagePath2 = WhitelistRule.NormalizeImagePath(imagePath);
			if (imagePath2.Length == 0)
			{
				return false;
			}
			try
			{
				string pathRoot = Path.GetPathRoot(imagePath2);
				if (string.IsNullOrEmpty(pathRoot) || imagePath2.Length <= pathRoot.Length)
				{
					return false;
				}
				string fileName = Path.GetFileName(imagePath2);
				if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				return string.Equals(name2, fileName.Substring(0, fileName.Length - 4), StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}
	}
}
