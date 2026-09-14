// @author bdth 2074055628@qq.com
// File purpose Discover game platform install directories, for install scanning and display only, no part in renderer identity or process exemption decisions
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{

    internal static class GamePlatformCatalog
    {

        private sealed class Platform
        {
            public readonly string Id;

            public readonly string[] RegistryRoots;

            public readonly string[] FolderRoots;

            public readonly string[] UninstallTags;
            public List<string> Roots;

            public Platform(string id, string[] registryRoots,
                string[] folderRoots, string[] uninstallTags)
            {
                Id = id;
                RegistryRoots = registryRoots ?? new string[0];
                FolderRoots = folderRoots ?? new string[0];
                UninstallTags = uninstallTags ?? new string[0];
            }
        }

        private const string Pf = "pf";
        private const string Pf86 = "pf86";
        private const string Common = "common";
        private const string Common86 = "common86";
        private const string Data = "data";
        private const string Local = "local";
        private const string Roaming = "roaming";
        private const string Drive = "drive";

        private static readonly Platform[] Platforms =
        {

            new Platform("Steam",
                new[]
                {
                    "U|Software\\Valve\\Steam|SteamPath",
                    "M|SOFTWARE\\WOW6432Node\\Valve\\Steam|InstallPath",
                    "M|SOFTWARE\\Valve\\Steam|InstallPath"
                },
                new[]{ Common86 + "|Steam", Common + "|Steam", Pf86 + "|Steam", Pf + "|Steam" },
                null),

            new Platform("Epic Games",
                null,
                new[]{ Pf86 + "|Epic Games\\Launcher", Pf + "|Epic Games\\Launcher" },
                new[]{ "Epic Games Launcher" }),

            new Platform("EA app",
                null,
                new[]
                {
                    Pf + "|Electronic Arts\\EA Desktop", Pf86 + "|Electronic Arts\\EA Desktop",
                    Common + "|Electronic Arts", Common86 + "|Electronic Arts",
                    Data + "|Electronic Arts",
                    Pf + "|Origin", Pf86 + "|Origin"
                },
                new[]{ "EA app", "EA Desktop" }),

            new Platform("Ubisoft Connect",
                new[]
                {
                    "M|SOFTWARE\\WOW6432Node\\Ubisoft\\Launcher|InstallDir",
                    "M|SOFTWARE\\Ubisoft\\Launcher|InstallDir"
                },
                new[]
                {
                    Pf86 + "|Ubisoft\\Ubisoft Game Launcher",
                    Pf + "|Ubisoft\\Ubisoft Game Launcher"
                },
                new[]{ "Ubisoft Connect" }),

            new Platform("Battle.net",
                null,
                new[]
                {
                    Pf86 + "|Battle.net", Pf + "|Battle.net",
                    Data + "|Battle.net", Data + "|Blizzard Entertainment"
                },
                new[]{ "Battle.net" }),

            new Platform("GOG Galaxy",
                new[]
                {
                    "M|SOFTWARE\\WOW6432Node\\GOG.com\\GalaxyClient\\paths|client",
                    "M|SOFTWARE\\GOG.com\\GalaxyClient\\paths|client"
                },
                new[]{ Pf86 + "|GOG Galaxy", Pf + "|GOG Galaxy", Data + "|GOG.com\\Galaxy" },
                new[]{ "GOG GALAXY" }),

            new Platform("Rockstar Games",
                new[]{ "M|SOFTWARE\\WOW6432Node\\Rockstar Games\\Launcher|InstallFolder" },
                new[]
                {
                    Pf + "|Rockstar Games\\Launcher", Pf86 + "|Rockstar Games\\Launcher",
                    Pf + "|Rockstar Games\\Social Club", Pf86 + "|Rockstar Games\\Social Club"
                },
                new[]{ "Rockstar Games Launcher" }),

            new Platform("Riot Client",
                null,
                new[]
                {
                    Drive + "|Riot Games\\Riot Client",
                    Pf + "|Riot Games\\Riot Client", Pf86 + "|Riot Games\\Riot Client",
                    Data + "|Riot Games"
                },
                new[]{ "Riot Client" }),

            new Platform("WeGame",
                null,
                new[]
                {
                    Pf86 + "|WeGame", Pf + "|WeGame",
                    Pf86 + "|Tencent\\WeGame", Pf + "|Tencent\\WeGame"
                },
                new[]{ "WeGame", "腾讯游戏平台" }),

            new Platform("Xbox",
                null,
                new[]{ Pf + "|WindowsApps" },
                null),

            new Platform("HoYoPlay",
                null,
                new[]
                {
                    Pf + "|HoYoPlay", Pf86 + "|HoYoPlay",
                    Pf + "|miHoYo Launcher", Pf86 + "|miHoYo Launcher",
                    Pf + "|miHoYo", Pf86 + "|miHoYo"
                },
                new[]{ "HoYoPlay", "miHoYo Launcher", "米哈游启动器" }),

            new Platform("Amazon Games",
                null,
                new[]{ Local + "|Amazon Games", Pf + "|Amazon Games" },
                new[]{ "Amazon Games" }),

            new Platform("itch.io",
                null,
                new[]{ Local + "|itch", Roaming + "|itch" },
                null),

            new Platform("Garena",
                null,
                new[]{ Pf86 + "|Garena", Pf + "|Garena", Local + "|Garena" },
                new[]{ "Garena" }),

            new Platform("Nexon",
                null,
                new[]
                {
                    Pf86 + "|Nexon", Pf + "|Nexon",
                    Data + "|NexonUS\\NGM", Data + "|NexonEU\\NGM", Data + "|Nexon"
                },
                new[]{ "Nexon Launcher", "Nexon Game Manager" }),

            new Platform("NCSOFT PURPLE",
                null,
                new[]
                {
                    Pf + "|NCSOFT\\Purple", Pf86 + "|NCSOFT\\Purple",
                    Pf + "|NCSOFT", Pf86 + "|NCSOFT"
                },
                new[]{ "NCSOFT" }),

            new Platform("DMM GAME PLAYER",
                null,
                new[]
                {
                    Pf86 + "|DMMGamePlayer", Pf + "|DMMGamePlayer",
                    Local + "|DMMGamePlayer", Roaming + "|DMMGamePlayer",
                    Roaming + "|DMMGamePlayerFastLauncher"
                },
                new[]{ "DMM Game" }),

            new Platform("NetEase",
                null,
                new[]
                {
                    Pf + "|Netease", Pf86 + "|Netease",
                    Pf + "|NetEase Games", Pf86 + "|NetEase Games",
                    Data + "|Netease"
                },
                new[]{ "网易游戏", "NetEase Games" }),
        };

        private const int RootRefreshMs = 600000;

        private static readonly object sync = new object();
        private static bool rootsResolved;
        private static long lastResolveTicks;

        internal static List<string> ResolvedRoots(string platformId)
        {
            EnsureRoots();
            var result = new List<string>();
            lock (sync)
                foreach (Platform platform in Platforms)
                    if (string.Equals(platform.Id, platformId, StringComparison.OrdinalIgnoreCase)
                        && platform.Roots != null)
                        result.AddRange(platform.Roots);
            return result;
        }

        internal static List<string> DetectedPlatforms()
        {
            EnsureRoots();
            var result = new List<string>();
            lock (sync)
                foreach (Platform platform in Platforms)
                    if (platform.Roots != null && platform.Roots.Count > 0) result.Add(platform.Id);
            return result;
        }

        private static void EnsureRoots()
        {
            lock (sync)
            {
                long now = DateTime.UtcNow.Ticks;
                if (rootsResolved && lastResolveTicks > 0 && now >= lastResolveTicks
                    && now - lastResolveTicks < RootRefreshMs * TimeSpan.TicksPerMillisecond) return;
                ResolveRootsLocked();
            }
        }

        private static void ResolveRootsLocked()
        {
            Dictionary<string, List<string>> byTag = ScanUninstallLocations();
            foreach (Platform platform in Platforms)
            {
                var roots = new List<string>();
                foreach (string spec in platform.RegistryRoots) AddRegistryRoot(roots, spec);
                foreach (string spec in platform.FolderRoots) AddFolderRoot(roots, spec);
                foreach (string tag in platform.UninstallTags)
                {
                    List<string> found;
                    if (!byTag.TryGetValue(tag, out found)) continue;
                    foreach (string dir in found) AddRoot(roots, dir);
                }
                platform.Roots = roots;
            }
            rootsResolved = true;
            lastResolveTicks = DateTime.UtcNow.Ticks;
        }

        private static void AddRegistryRoot(List<string> into, string spec)
        {
            try
            {
                string[] parts = spec.Split('|');
                if (parts.Length != 3) return;
                RegistryKey hive = parts[0] == "U" ? Registry.CurrentUser : Registry.LocalMachine;
                using (RegistryKey key = hive.OpenSubKey(parts[1]))
                {
                    if (key == null) return;
                    AddRoot(into, key.GetValue(parts[2]) as string);
                }
            }
            catch { }
        }

        private static void AddFolderRoot(List<string> into, string spec)
        {
            try
            {
                int split = spec.IndexOf('|');
                if (split <= 0) return;
                string root = BaseFolder(spec.Substring(0, split));
                if (string.IsNullOrEmpty(root)) return;
                AddRoot(into, Path.Combine(root, spec.Substring(split + 1)));
            }
            catch { }
        }

        private static string BaseFolder(string token)
        {
            try
            {
                switch (token)
                {
                    case Pf: return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    case Pf86: return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                    case Common: return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
                    case Common86: return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
                    case Data: return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    case Local: return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    case Roaming: return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    case Drive: return SystemDrive();
                }
            }
            catch { }
            return null;
        }

        private static string SystemDrive()
        {
            string drive = null;
            try { drive = Environment.GetEnvironmentVariable("SystemDrive"); }
            catch { }
            if (string.IsNullOrEmpty(drive))
            {
                try { drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)); }
                catch { }
            }
            if (string.IsNullOrEmpty(drive)) return null;
            return drive.TrimEnd('\\') + "\\";
        }

        private static void AddRoot(List<string> into, string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            string dir;
            try { dir = Path.GetFullPath(raw.Trim().Trim('"').Replace('/', '\\')).TrimEnd('\\'); }
            catch { return; }
            if (dir.Length <= 3 || IsTooBroad(dir)) return;
            try { if (!Directory.Exists(dir)) return; }
            catch { return; }
            foreach (string existing in into)
                if (string.Equals(existing, dir, StringComparison.OrdinalIgnoreCase)) return;
            into.Add(dir);
        }

        private static readonly Environment.SpecialFolder[] BroadFolders =
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.UserProfile
        };

        private static bool IsTooBroad(string dir)
        {
            foreach (Environment.SpecialFolder folder in BroadFolders)
            {
                string known;
                try { known = Environment.GetFolderPath(folder); }
                catch { continue; }
                if (string.IsNullOrEmpty(known)) continue;
                if (string.Equals(dir, known.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            }
            string windows;
            try { windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
            catch { return false; }
            if (string.IsNullOrEmpty(windows)) return false;
            windows = windows.TrimEnd('\\');
            return string.Equals(dir, windows, StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, List<string>> ScanUninstallLocations()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var tags = new List<string>();
            foreach (Platform platform in Platforms)
                foreach (string tag in platform.UninstallTags)
                    if (!tags.Contains(tag)) tags.Add(tag);
            if (tags.Count == 0) return result;

            ScanUninstallHive(Registry.LocalMachine,
                "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", tags, result);
            ScanUninstallHive(Registry.LocalMachine,
                "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall", tags, result);
            ScanUninstallHive(Registry.CurrentUser,
                "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", tags, result);
            return result;
        }

        private static void ScanUninstallHive(RegistryKey hive, string path,
            List<string> tags, Dictionary<string, List<string>> into)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(path))
                {
                    if (key == null) return;
                    foreach (string sub in key.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey entry = key.OpenSubKey(sub))
                            {
                                if (entry == null) continue;
                                string display = entry.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(display)) continue;
                                string location = entry.GetValue("InstallLocation") as string;
                                if (string.IsNullOrEmpty(location))
                                    location = DirFromCommand(entry.GetValue("UninstallString") as string);
                                if (string.IsNullOrEmpty(location))
                                    location = DirFromCommand(entry.GetValue("DisplayIcon") as string);
                                if (string.IsNullOrEmpty(location)) continue;
                                foreach (string tag in tags)
                                {
                                    if (display.IndexOf(tag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    List<string> dirs;
                                    if (!into.TryGetValue(tag, out dirs))
                                    {
                                        dirs = new List<string>();
                                        into[tag] = dirs;
                                    }
                                    dirs.Add(location);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        internal static string DirFromCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            string s = command.Trim();
            string exe = null;
            if (s.Length > 1 && s[0] == '"')
            {
                int end = s.IndexOf('"', 1);
                if (end > 1) exe = s.Substring(1, end - 1);
            }
            else
            {
                int i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (i > 0) exe = s.Substring(0, i + 4);
            }
            exe = exe == null ? null : exe.Trim();
            if (string.IsNullOrEmpty(exe)) return null;
            try
            {
                string dir = Path.GetDirectoryName(exe);
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch { return null; }
        }

    }
}
