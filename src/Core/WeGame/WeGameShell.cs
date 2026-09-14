// @author bdth 2074055628@qq.com
// File purpose Pure rules for WeGame shell removal, identifies WeGame game directories, decides when to remove the shell and when to trip the breaker, touches no process and reads no registry
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class WeGameShell
    {
        // Act only after the match has been confirmed running this long, launcher handoff, anti-cheat init and first-screen loading are all over before the shell is taken down
        public const int StabilizeSeconds = 30;
        // If the game exits within this window after shell removal, the shell is deemed a dependency of this game, auto shell removal for it is disabled on this machine
        public const int ExitFuseSeconds = 20;
        // The match-end notification arrives a grace period after process exit, it also counts if the notification arrives within this many seconds of the last shell removal
        public const int ExitFuseNotifySeconds = 45;
        // After shell removal first re-check at this moment whether the game is still there, then watch for shell respawn every RespawnCheckSeconds
        public const int RespawnCheckSeconds = 60;
        // Shell process keeps respawning, three rounds ending within five minutes means stand down for this match, do not fight WeGame's self-relaunch
        public const int RespawnWindowSeconds = 300;
        public const int RespawnLimit = 3;

        private static readonly string[] RootMarkers = { "TCLS", "rail_files", "WeGameLauncher", "Cross" };
        private const string AppsFolderName = "WeGameApps";
        private const int MaxParentWalk = 4;

        // Directory carries the WeGame launch chain marker, or it is a direct child of WeGameApps
        public static bool IsWeGameGameRoot(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            try
            {
                string full = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
                if (!Directory.Exists(full)) return false;
                return HasRootMarker(full) || DirectChildOfAppsFolder(full);
            }
            catch { return false; }
        }

        internal static bool HasRootMarker(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            foreach (string marker in RootMarkers)
                if (Directory.Exists(Path.Combine(full, marker))) return true;
            return false;
        }

        // Only direct children of WeGameApps are game roots, deeper directories belong to the game's internals
        internal static bool DirectChildOfAppsFolder(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            string parent;
            try { parent = Path.GetDirectoryName(full); } catch { return false; }
            return parent != null
                && string.Equals(Path.GetFileName(parent), AppsFolderName, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool UnderAppsFolder(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            string[] parts = full.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
                if (string.Equals(parts[i], AppsFolderName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Trust the profile's Root first, then walk up from the executable, at most four levels
        //   a directory with the marker counts as root directly, without the marker climb until a direct child of WeGameApps
        public static string ResolveGameRoot(string profileRoot, string executablePath)
        {
            if (IsWeGameGameRoot(profileRoot)) return NormalizeDir(profileRoot);
            if (string.IsNullOrEmpty(executablePath)) return null;
            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(executablePath)); }
            catch { return null; }
            for (int depth = 0; depth <= MaxParentWalk && !string.IsNullOrEmpty(dir); depth++)
            {
                if (HasRootMarker(dir) || DirectChildOfAppsFolder(dir)) return NormalizeDir(dir);
                string parent;
                try { parent = Path.GetDirectoryName(dir); } catch { break; }
                if (parent == null) break;
                dir = parent;
            }
            return null;
        }

        private static string NormalizeDir(string dir)
        {
            try { return Path.GetFullPath(dir.Trim().Trim('"')).TrimEnd('\\'); }
            catch { return null; }
        }

        // When action is allowed: match running, past the stabilize period, not removed this match, not tripped, not stood down
        public static bool ShouldClean(long sessionStartTicks, long nowTicks, bool cleanedThisSession,
            bool fused, bool circuitOpen, bool autoEnabled)
        {
            if (!autoEnabled || fused || circuitOpen || cleanedThisSession) return false;
            if (sessionStartTicks <= 0 || nowTicks < sessionStartTicks) return false;
            return nowTicks - sessionStartTicks >= StabilizeSeconds * TimeSpan.TicksPerSecond;
        }

        // Game vanished this soon after shell removal, count the shell as a dependency, timer re-check uses the short window, end notification uses the long window with grace
        public static bool ExitBlamesCleanup(long cleanedTicks, long exitTicks, bool fromNotification)
        {
            if (cleanedTicks <= 0 || exitTicks < cleanedTicks) return false;
            long window = (fromNotification ? ExitFuseNotifySeconds : ExitFuseSeconds) * TimeSpan.TicksPerSecond;
            return exitTicks - cleanedTicks <= window;
        }

        // Record one shell removal round that actually ended the process, returns true at three rounds within five minutes, meaning the shell keeps respawning
        public static bool RegisterKillCycle(Queue<long> cycles, long nowTicks)
        {
            if (cycles == null) return false;
            long window = RespawnWindowSeconds * TimeSpan.TicksPerSecond;
            while (cycles.Count > 0 && nowTicks - cycles.Peek() > window) cycles.Dequeue();
            cycles.Enqueue(nowTicks);
            return cycles.Count >= RespawnLimit;
        }

        // When the next re-check is due, right after removal check the game is still alive first, then follow the respawn period
        public static int NextCheckDelayMs(bool justCleaned)
        {
            return (justCleaned ? ExitFuseSeconds : RespawnCheckSeconds) * 1000;
        }
    }
}
