// File purpose Identify recording and overlay hosts that must be exempt from background suppression, without reading game modules or product files
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class OverlayHostCatalog
    {
        // This is a compatibility list, not signature verification, and does not mean the overlay is running in the current game
        // Match specific EXEs only, no exemption propagation by install directory, child process tree or product name prefix
        // Steam's comms, overlay UI and browser rendering form one group, protecting only one of them does not guarantee function
        // That group is governed by the caller's existing family policy and does not affect the protection of the standalone tools below
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

                // Recording/overlay hosts of the new NVIDIA App and the old GeForce Experience
                "NVIDIA Share", "NVIDIA Overlay", "nvsphelper", "nvsphelper64",

                // 32-bit games are injected by the 32-bit loader rtsshooks; EncoderServer is
                // the encoder host of the Afterburner recording chain, suppressing any of them drags down the render path
                "RTSS", "RTSSHooksLoader", "RTSSHooksLoader64",
                "MSIAfterburner", "EncoderServer", "EncoderServer64",

                // Browser sources and mux output are part of the recording chain too, protecting only the main window is not enough
                "obs32", "obs64", "obs-browser-page", "obs-ffmpeg-mux",

                "Overwolf", "OverwolfBrowser", "OverwolfHelper", "OverwolfHelper64",

                // nahimicosd/a-volute from the old module scan belong to the same audio/OSD product chain
                "NahimicService", "NahimicSvc32", "NahimicSvc64",

                "fraps"
            };

        // The caller must still verify session and PID/Creation and release only the Background reason
        // Existing protection from HardwareControlCatalog / PeripheralCatalog is kept independently
        // Unknown or renamed hosts can be added via the exact whitelist, not loosened to generic processes like chrome/updater
        internal static bool ShouldProtectProcess(
            string name, string imagePath, bool protectGamePlatformHosts)
        {
            string normalizedName = WhitelistRule.NormalizeName(name);
            if (!ProcessNames.Contains(normalizedName)
                && !(protectGamePlatformHosts && GamePlatformProcessNames.Contains(normalizedName)))
                return false;

            // This normalizer requires an absolute path before calling GetFullPath, a relative path is never taken as identity
            string path = WhitelistRule.NormalizeImagePath(imagePath);
            if (path.Length == 0) return false;
            try
            {
                // \\server\obs64.exe is just a UNC share root, not an executable inside a shared directory
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
