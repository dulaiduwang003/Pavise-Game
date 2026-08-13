// @author bdth 2074055628@qq.com
// 文件用途 各大游戏平台客户端家族的内置豁免 进程名加安装目录双重校验

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
            public readonly string Note;

            public readonly string[] ShellNames;

            public readonly string[] LocalNames;

            public readonly string[] RegistryRoots;

            public readonly string[] FolderRoots;

            public readonly string[] UninstallTags;
            public List<string> Roots;
            public bool Logged;

            public Platform(string id, string note, string[] shellNames, string[] localNames,
                string[] registryRoots, string[] folderRoots, string[] uninstallTags)
            {
                Id = id;
                Note = note;
                ShellNames = shellNames ?? new string[0];
                LocalNames = localNames ?? new string[0];
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
                "Valve 官方警告 压制 Steam 客户端会引发优先级反转掉帧 steamservice 还承载 VAC",
                new[]{ "steam", "steamservice", "steamwebhelper",
                    "gameoverlayui", "gameoverlayui64" },
                new[]{ "steamerrorreporter", "steamerrorreporter64" },
                new[]
                {
                    "U|Software\\Valve\\Steam|SteamPath",
                    "M|SOFTWARE\\WOW6432Node\\Valve\\Steam|InstallPath",
                    "M|SOFTWARE\\Valve\\Steam|InstallPath"
                },
                new[]{ Common86 + "|Steam", Common + "|Steam", Pf86 + "|Steam", Pf + "|Steam" },
                null),

            new Platform("Epic Games",
                "EpicWebHelper 是商店与好友界面的渲染进程 压下去会连带卡住游戏内覆盖层",
                new[]{ "epicgameslauncher", "epicwebhelper" },
                null,
                null,
                new[]{ Pf86 + "|Epic Games\\Launcher", Pf + "|Epic Games\\Launcher" },
                new[]{ "Epic Games Launcher" }),

            new Platform("EA app",
                "EABackgroundService 承载正版校验与云存档 被压会掉线",
                new[]
                {
                    "eadesktop", "eabackgroundservice", "ealauncher", "ealocalhostsvc",
                    "easteamproxy", "originwebhelperservice", "originclientservice"
                },
                new[]{ "origin" },
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
                "UplayWebCore 掉帧会拖住育碧游戏的在线校验",
                new[]{ "ubisoftconnect", "ubisoftgamelauncher", "uplay", "uplaywebcore" },
                new[]{ "upc" },
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
                "Agent 承载补丁与完整性校验 Battle.net Helper 是暴雪游戏的内嵌界面",
                new[]{ "battle.net", "battle.net helper", "blizzarderror", "blizzardbrowser" },
                new[]{ "agent" },
                null,
                new[]
                {
                    Pf86 + "|Battle.net", Pf + "|Battle.net",
                    Data + "|Battle.net", Data + "|Blizzard Entertainment"
                },
                new[]{ "Battle.net" }),

            new Platform("GOG Galaxy",
                "GalaxyCommunication 断了会让 GOG 游戏的成就与云存档失败",
                new[]
                {
                    "galaxyclient", "galaxyclient helper", "galaxycommunication",
                    "galaxyclientservice", "galaxyservice"
                },
                null,
                new[]
                {
                    "M|SOFTWARE\\WOW6432Node\\GOG.com\\GalaxyClient\\paths|client",
                    "M|SOFTWARE\\GOG.com\\GalaxyClient\\paths|client"
                },
                new[]{ Pf86 + "|GOG Galaxy", Pf + "|GOG Galaxy", Data + "|GOG.com\\Galaxy" },
                new[]{ "GOG GALAXY" }),

            new Platform("Rockstar Games",
                "R 星启动器主进程就叫 Launcher 只在自己的安装目录里认",
                new[]
                {
                    "rockstarservice", "rockstarerrorhandler", "socialclubhelper",
                    "rockstar-games-launcher", "launcherpatcher"
                },
                new[]{ "launcher" },
                new[]{ "M|SOFTWARE\\WOW6432Node\\Rockstar Games\\Launcher|InstallFolder" },
                new[]
                {
                    Pf + "|Rockstar Games\\Launcher", Pf86 + "|Rockstar Games\\Launcher",
                    Pf + "|Rockstar Games\\Social Club", Pf86 + "|Rockstar Games\\Social Club"
                },
                new[]{ "Rockstar Games Launcher" }),

            new Platform("Riot Client",
                "英雄联盟 无畏契约都靠它做登录与补丁 Vanguard 另由反作弊名录豁免",
                new[]
                {
                    "riotclientservices", "riotclientux", "riotclientuxrender",
                    "riotclientcrashhandler"
                },
                null,
                null,
                new[]
                {
                    Drive + "|Riot Games\\Riot Client",
                    Pf + "|Riot Games\\Riot Client", Pf86 + "|Riot Games\\Riot Client",
                    Data + "|Riot Games"
                },
                new[]{ "Riot Client" }),

            new Platform("WeGame",
                "腾讯平台的登录与反外挂通道走 wegame 本体",
                new[]{ "wegame", "wegame_env", "wegameclient" },
                null,
                null,
                new[]
                {
                    Pf86 + "|WeGame", Pf + "|WeGame",
                    Pf86 + "|Tencent\\WeGame", Pf + "|Tencent\\WeGame"
                },
                new[]{ "WeGame", "腾讯游戏平台" }),

            new Platform("Xbox / 微软商店",
                "Gaming Services 停摆会让 Game Pass 游戏直接启动失败",
                new[]
                {
                    "xboxpcapp", "xboxpcappft", "xboxappservices",
                    "gamingservices", "gamingservicesnet", "gamebarftserver"
                },
                new[]{ "xbox", "gamebar" },
                null,
                new[]{ Pf + "|WindowsApps" },
                null),

            new Platform("HoYoPlay",
                "米哈游启动器兼做补丁与反作弊分发 旧版主进程也叫 launcher",
                new[]{ "hyp", "hoyoplay", "hyupdater" },
                new[]{ "launcher" },
                null,
                new[]
                {
                    Pf + "|HoYoPlay", Pf86 + "|HoYoPlay",
                    Pf + "|miHoYo Launcher", Pf86 + "|miHoYo Launcher",
                    Pf + "|miHoYo", Pf86 + "|miHoYo"
                },
                new[]{ "HoYoPlay", "miHoYo Launcher", "米哈游启动器" }),

            new Platform("Amazon Games",
                "自带的 SDK 服务掉线会让亚马逊发行的游戏卡在登录",
                new[]{ "amazon games ui", "amazon games services", "amazongamessdkservice" },
                new[]{ "amazon games" },
                null,
                new[]{ Local + "|Amazon Games", Pf + "|Amazon Games" },
                new[]{ "Amazon Games" }),

            new Platform("itch.io",
                "itch 客户端负责游戏进程的启动与在线状态",
                null,
                new[]{ "itch", "itch-setup", "butler" },
                null,
                new[]{ Local + "|itch", Roaming + "|itch" },
                null),

            new Platform("Garena",
                "东南亚区的登录与对战平台",
                new[]{ "garena", "garenamsg" },
                null,
                null,
                new[]{ Pf86 + "|Garena", Pf + "|Garena", Local + "|Garena" },
                new[]{ "Garena" }),

            new Platform("Nexon",
                "韩系网游的补丁与启动通道",
                new[]{ "nexon_runtime", "nexonlauncher", "nexonplug" },
                new[]{ "ngm" },
                null,
                new[]
                {
                    Pf86 + "|Nexon", Pf + "|Nexon",
                    Data + "|NexonUS\\NGM", Data + "|NexonEU\\NGM", Data + "|Nexon"
                },
                new[]{ "Nexon Launcher", "Nexon Game Manager" }),

            new Platform("NCSOFT PURPLE",
                "天堂 剑灵等 NCSOFT 游戏的统一启动器",
                null,
                new[]{ "purple", "ncsoft" },
                null,
                new[]
                {
                    Pf + "|NCSOFT\\Purple", Pf86 + "|NCSOFT\\Purple",
                    Pf + "|NCSOFT", Pf86 + "|NCSOFT"
                },
                new[]{ "NCSOFT" }),

            new Platform("DMM GAME PLAYER",
                "日区 DMM 游戏的启动与授权通道",
                new[]{ "dmmgameplayer", "dmmgameplayerfastlauncher" },
                null,
                null,
                new[]
                {
                    Pf86 + "|DMMGamePlayer", Pf + "|DMMGamePlayer",
                    Local + "|DMMGamePlayer", Roaming + "|DMMGamePlayer",
                    Roaming + "|DMMGamePlayerFastLauncher"
                },
                new[]{ "DMM Game" }),

            new Platform("网易游戏",
                "网易各平台的启动器命名不统一 只在网易的安装目录里认",
                new[]{ "gest_launcher", "neteasegamecenter" },
                new[]{ "gamecenter", "launcher" },
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
        private static readonly Dictionary<string, List<Platform>> ByName = BuildNameIndex();
        private static readonly HashSet<string> ShellNames = BuildShellNames();
        private static bool rootsResolved;
        private static long lastResolveTicks;

        internal static readonly string[] GameAreaSegments =
        {
            "steamapps", "games", "gamelibrary", "library", "downloads"
        };

        internal static bool InGameArea(string image, string root)
        {
            if (string.IsNullOrEmpty(image) || string.IsNullOrEmpty(root)) return false;
            string prefix = root.TrimEnd('\\') + "\\";
            if (!image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = image.Substring(prefix.Length);
            foreach (string seg in GameAreaSegments)
                if (rest.StartsWith(seg + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsPlatformProcess(string name, string path)
        {
            string image = NormalizePath(path);
            if (image.Length == 0) return false;

            List<Platform> owners = OwnersOf(name);
            if (owners != null)
            {
                Platform byName = MatchRoot(owners, image);
                if (byName == null && RefreshRootsIfStale()) byName = MatchRoot(owners, image);
                if (byName != null) { LogOnce(byName); return true; }
            }

            Platform byPath = MatchAnyRootOutsideGames(image);
            if (byPath == null && RefreshRootsIfStale()) byPath = MatchAnyRootOutsideGames(image);
            if (byPath == null) return false;
            LogOnce(byPath);
            return true;
        }

        private static Platform MatchAnyRootOutsideGames(string image)
        {
            EnsureRoots();
            lock (sync)
                foreach (Platform platform in Platforms)
                {
                    if (platform.Roots == null) continue;
                    foreach (string root in platform.Roots)
                    {
                        if (string.IsNullOrEmpty(root)) continue;
                        string prefix = root.TrimEnd('\\') + "\\";
                        if (!image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (InGameArea(image, root)) return null;
                        return platform;
                    }
                }
            return null;
        }

        internal static bool IsPlatformShellName(string name)
        {
            return !string.IsNullOrEmpty(name) && ShellNames.Contains(name.Trim());
        }

        internal static IEnumerable<string> PlatformShellNames()
        {
            return ShellNames;
        }

        internal static bool OwnsName(string platformId, string name)
        {
            List<Platform> owners = OwnersOf(name);
            if (owners == null) return false;
            foreach (Platform platform in owners)
                if (string.Equals(platform.Id, platformId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static bool MatchesWithRoots(string platformId, string name, string path, IList<string> roots)
        {
            return OwnsName(platformId, name) && UnderAnyRoot(NormalizePath(path), roots);
        }

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

        private static Platform MatchRoot(List<Platform> owners, string image)
        {
            EnsureRoots();
            lock (sync)
                foreach (Platform platform in owners)
                    if (UnderAnyRoot(image, platform.Roots)) return platform;
            return null;
        }

        private static List<Platform> OwnersOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            List<Platform> owners;
            return ByName.TryGetValue(name.Trim(), out owners) ? owners : null;
        }

        private static void LogOnce(Platform platform)
        {
            lock (sync)
            {
                if (platform.Logged) return;
                platform.Logged = true;
            }
            Logger.Log(platform.Id + " 客户端已豁免 不压制");
        }

        private static bool UnderAnyRoot(string path, IList<string> roots)
        {
            if (string.IsNullOrEmpty(path) || roots == null) return false;
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string prefix = root.TrimEnd('\\') + "\\";
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Trim().Trim('"').Replace('/', '\\');
        }

        private static void EnsureRoots()
        {
            lock (sync)
            {
                if (rootsResolved) return;
                ResolveRootsLocked();
            }
        }

        private static bool RefreshRootsIfStale()
        {
            lock (sync)
            {
                long now = DateTime.UtcNow.Ticks;
                if (lastResolveTicks > 0 && now >= lastResolveTicks
                    && now - lastResolveTicks < RootRefreshMs * TimeSpan.TicksPerMillisecond)
                    return false;
                ResolveRootsLocked();
                return true;
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

        private static Dictionary<string, List<Platform>> BuildNameIndex()
        {
            var index = new Dictionary<string, List<Platform>>(StringComparer.OrdinalIgnoreCase);
            foreach (Platform platform in Platforms)
            {
                foreach (string name in platform.ShellNames) Index(index, name, platform);
                foreach (string name in platform.LocalNames) Index(index, name, platform);
            }
            return index;
        }

        private static void Index(Dictionary<string, List<Platform>> index, string name, Platform platform)
        {
            if (string.IsNullOrEmpty(name)) return;
            List<Platform> owners;
            if (!index.TryGetValue(name, out owners))
            {
                owners = new List<Platform>();
                index[name] = owners;
            }
            if (!owners.Contains(platform)) owners.Add(platform);
        }

        private static HashSet<string> BuildShellNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Platform platform in Platforms)
                foreach (string name in platform.ShellNames)
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
            return names;
        }
    }
}
