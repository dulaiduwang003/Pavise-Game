using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class OverlayHostCatalog
    {
        private static readonly HashSet<string> GamePlatformProcessNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "steam", "gameoverlayui", "steamwebhelper"
            };

        private static readonly HashSet<string> ProcessNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment",
                "DiscordHookHelper", "DiscordHookHelper64",

                "NVIDIA Share", "NVIDIA Overlay", "nvsphelper", "nvsphelper64",

                // 录制链的注入器和编码器都需保护。
                "RTSS", "RTSSHooksLoader", "RTSSHooksLoader64",
                "MSIAfterburner", "EncoderServer", "EncoderServer64",

                "obs32", "obs64", "obs-browser-page", "obs-ffmpeg-mux",

                "GameBar", "GameBarElevatedFT", "GameBarFTServer", "GameBarPresenceWriter",

                "Overwolf", "OverwolfBrowser", "OverwolfHelper", "OverwolfHelper64",

                "NahimicService", "NahimicSvc32", "NahimicSvc64",

                "fraps"
            };

        // 释放时仅移除后台原因，保留独立的硬件保护。
        internal static bool ShouldProtectProcess(
            string name, string imagePath, bool protectGamePlatformHosts)
        {
            string normalizedName = WhitelistRule.NormalizeName(name);
            if (!ProcessNames.Contains(normalizedName)
                && !(protectGamePlatformHosts && PlatformRuntimePolicy.Enabled("steam") && GamePlatformProcessNames.Contains(normalizedName)))
                return false;

            string path = WhitelistRule.NormalizeImagePath(imagePath);
            if (path.Length == 0) return false;
            try
            {
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || path.Length <= root.Length) return false;
                string leaf = Path.GetFileName(path);
                if (!leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
                return string.Equals(normalizedName, leaf.Substring(0, leaf.Length - 4),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
