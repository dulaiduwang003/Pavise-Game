// @author bdth 2074055628@qq.com
// 文件用途 扫描本机游戏并维护游戏库目录

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    internal class ScanHit
    {
        public string Name;
        public string Proc;
        public string Root;
        public string Exe;
    }

    internal static class GameScan
    {
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windows", "programdata", "$recycle.bin", "system volume information",
            "recovery", "perflogs", "onedrivetemp",
            "node_modules", ".git", "temp", "tmp", "cache", "__pycache__"
        };

        private static readonly string[] JunkExe =
        {
            "unins", "setup", "install", "crash", "report", "redist", "dxsetup",
            "dotnet", "easyanticheat", "battleye", "prereq", "helper", "handler",
            "cef", "cleanup", "diagnostic", "activation", "touchup"
        };

        private static readonly HashSet<string> GenericDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "bin64", "binaries", "win64", "win32", "x64", "x86",
            "game", "games", "retail", "shipping", "engine", "content", "data", "app"
        };

        private static readonly HashSet<string> GenericDirTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "bin64", "binaries", "win64", "win32", "x64", "x86",
            "game", "games", "retail", "shipping", "engine", "content", "data", "app", "client"
        };

        internal static bool IsGenericDirName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (GenericDirs.Contains(name)) return true;
            string[] parts = name.Split(new[] { '_', '-', ' ', '.' });
            if (parts.Length < 2) return false;
            int tokens = 0;
            foreach (string part in parts)
            {
                if (part.Length == 0) continue;
                if (!GenericDirTokens.Contains(part)) return false;
                tokens++;
            }
            return tokens >= 2;
        }

        private static readonly string[] JunkManifest =
        {
            "redistributable", "steamworks common", "proton", "steam linux runtime", "steamvr"
        };

        public static List<ScanHit> Run(string root, Func<bool> canceled, Action<int, int> progress)
        {
            var hits = new List<ScanHit>();
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectManifests(root, hits, roots, canceled);
            if (progress != null) progress(0, hits.Count);

            int[] dirs = { 0 };
            try { Visit(root, root, 8, hits, roots, dirs, canceled, progress); }
            catch { }
            return hits;
        }

        public static List<ScanHit> RunManifests(Func<bool> canceled)
        {
            var hits = new List<ScanHit>();
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectManifests(null, hits, roots, canceled);
            return hits;
        }

        private static void CollectManifests(string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
        {
            try { FromSteam(root, hits, roots); } catch { }
            if (Stop(canceled)) return;
            try { FromEpic(root, hits, roots); } catch { }
            if (Stop(canceled)) return;
            try { FromGog(root, hits, roots); } catch { }
            if (Stop(canceled)) return;
            try { FromUbisoft(root, hits, roots); } catch { }
            if (Stop(canceled)) return;
            try { FromRiot(root, hits, roots); } catch { }
            if (Stop(canceled)) return;
            try { FromWeGameApps(root, hits, roots); } catch { }
            if (Stop(canceled)) return;
            try { FromBattleNet(root, hits, roots); } catch { }
            if (Stop(canceled)) return;
            try { FromXbox(root, hits, roots); } catch { }
            if (Stop(canceled)) return;
            try { FromMicrosoftStore(root, hits, roots); } catch { }
            if (Stop(canceled)) return;
            try { FromInstalled(root, hits, roots, canceled); } catch { }
        }

        private static bool Stop(Func<bool> canceled)
        {
            return canceled != null && canceled();
        }

        private static bool UnderRoot(string dir, string root)
        {
            if (string.IsNullOrEmpty(root)) return dir != null;
            string r = root.TrimEnd('\\') + "\\";
            return dir != null && (dir.TrimEnd('\\') + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddManifestHit(string root, List<ScanHit> hits, HashSet<string> roots,
            string name, string dir, string exePath)
        {
            if (dir == null) return;
            dir = dir.Replace('/', '\\').TrimEnd('\\');
            if (!UnderRoot(dir, root) || !Directory.Exists(dir)) return;
            if (!roots.Add(dir)) return;
            string exe = exePath != null && File.Exists(exePath) ? exePath : PickMainExe(dir);
            if (exe == null) return;
            if (string.IsNullOrEmpty(name)) name = Path.GetFileName(dir);
            hits.Add(new ScanHit { Name = name, Proc = Path.GetFileNameWithoutExtension(exe), Root = dir, Exe = exe });
        }

        private static bool JunkManifestName(string name)
        {
            if (name == null) return false;
            foreach (string j in JunkManifest)
                if (name.IndexOf(j, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static void FromSteam(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string steam = null;
            try { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
            if (string.IsNullOrEmpty(steam))
                try { steam = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string; } catch { }
            if (string.IsNullOrEmpty(steam)) return;
            FromSteamLibraries(steam.Replace('/', '\\'), root, hits, roots);
        }

        internal static void FromSteamLibraries(string steam, string root, List<ScanHit> hits, HashSet<string> roots)
        {
            var libs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            libs.Add(steam);
            string vdf = Path.Combine(steam, "steamapps\\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));

            foreach (string lib in libs)
            {
                string sa = Path.Combine(lib, "steamapps");
                string[] acfs;
                try { acfs = Directory.GetFiles(sa, "appmanifest_*.acf"); } catch { continue; }
                foreach (string acf in acfs)
                {
                    try
                    {
                        string txt = File.ReadAllText(acf);
                        Match mn = Regex.Match(txt, "\"name\"\\s+\"([^\"]+)\"");
                        Match md = Regex.Match(txt, "\"installdir\"\\s+\"([^\"]+)\"");
                        if (!md.Success) continue;
                        string name = mn.Success ? mn.Groups[1].Value : null;
                        if (JunkManifestName(name)) continue;
                        AddManifestHit(root, hits, roots, name, Path.Combine(sa, "common\\" + md.Groups[1].Value), null);
                    }
                    catch { }
                }
            }
        }

        private static string JsonStr(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\/", "/");
        }

        private static void FromEpic(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string mdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic\\EpicGamesLauncher\\Data\\Manifests");
            string[] items;
            try { items = Directory.GetFiles(mdir, "*.item"); } catch { return; }
            foreach (string f in items)
            {
                try
                {
                    string txt = File.ReadAllText(f);
                    string loc = JsonStr(txt, "InstallLocation");
                    if (loc == null) continue;
                    string exe = JsonStr(txt, "LaunchExecutable");
                    string exePath = exe != null && exe.Length > 0 ? Path.Combine(loc, exe.Replace('/', '\\')) : null;
                    AddManifestHit(root, hits, roots, JsonStr(txt, "DisplayName"), loc, exePath);
                }
                catch { }
            }
        }

        private static void FromGog(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string[] keys = { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" };
            foreach (string kp in keys)
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(kp))
                {
                    if (k == null) continue;
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey g = k.OpenSubKey(sub))
                            {
                                if (g == null) continue;
                                string dir = g.GetValue("path") as string;
                                string exe = g.GetValue("exe") as string;
                                AddManifestHit(root, hits, roots, g.GetValue("gameName") as string, dir, exe);
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private static void FromUbisoft(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs"))
            {
                if (k == null) return;
                foreach (string sub in k.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey g = k.OpenSubKey(sub))
                        {
                            if (g == null) continue;
                            AddManifestHit(root, hits, roots, null, g.GetValue("InstallDir") as string, null);
                        }
                    }
                    catch { }
                }
            }
        }

        private static string[] FixedDriveRoots()
        {
            var result = new List<string>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try { if (drive.IsReady) result.Add(drive.RootDirectory.FullName); }
                    catch { }
                }
            }
            catch { }
            return result.ToArray();
        }

        private static void FromXbox(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            foreach (string drive in FixedDriveRoots())
            {
                string[] games;
                try { games = Directory.GetDirectories(Path.Combine(drive, "XboxGames")); } catch { continue; }
                foreach (string g in games)
                {
                    try
                    {
                        string name = Path.GetFileName(g.TrimEnd('\\'));
                        string content = Path.Combine(g, "Content");
                        string dir = Directory.Exists(content) ? content : g;

                        string exe = null;
                        string cfg = Path.Combine(dir, "MicrosoftGame.config");
                        if (File.Exists(cfg))
                        {
                            string txt = File.ReadAllText(cfg);
                            Match mx = Regex.Match(txt, "<Executable[^>]*\\bName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (mx.Success)
                            {
                                string candidate = Path.Combine(dir, mx.Groups[1].Value.Replace('/', '\\'));
                                if (File.Exists(candidate)) exe = candidate;
                            }
                            Match mn = Regex.Match(txt, "DefaultDisplayName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (mn.Success && mn.Groups[1].Value.Trim().Length > 0
                                && !mn.Groups[1].Value.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                                name = mn.Groups[1].Value.Trim();
                        }
                        AddManifestHit(root, hits, roots, name, dir, exe);
                    }
                    catch { }
                }
            }
        }

        private static void FromMicrosoftStore(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            FromPackageRepository(root,
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages",
                hits, roots);
        }

        internal static void FromPackageRepository(string root, string repoKey, List<ScanHit> hits, HashSet<string> roots)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(repoKey))
            {
                if (k == null) return;
                foreach (string sub in k.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey g = k.OpenSubKey(sub))
                        {
                            if (g == null) continue;
                            string dir = g.GetValue("PackageRootFolder") as string;
                            if (string.IsNullOrEmpty(dir)) continue;
                            dir = dir.Trim().TrimEnd('\\');
                            if (dir.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) < 0) continue;

                            bool game = File.Exists(Path.Combine(dir, "MicrosoftGame.config"))
                                     || File.Exists(Path.Combine(dir, "xboxservices.config"));
                            if (!game) continue;

                            string exe = null;
                            string cfg = Path.Combine(dir, "MicrosoftGame.config");
                            if (File.Exists(cfg))
                            {
                                try
                                {
                                    Match m = Regex.Match(File.ReadAllText(cfg),
                                        "<Executable[^>]*\\bName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                                    if (m.Success)
                                    {
                                        string candidate = Path.Combine(dir, m.Groups[1].Value.Replace('/', '\\'));
                                        if (File.Exists(candidate)) exe = candidate;
                                    }
                                }
                                catch { }
                            }
                            if (exe == null)
                            {
                                string manifest = Path.Combine(dir, "AppxManifest.xml");
                                if (File.Exists(manifest))
                                {
                                    try
                                    {
                                        Match m = Regex.Match(File.ReadAllText(manifest),
                                            "<Application\\b[^>]*\\bExecutable\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                                        if (m.Success)
                                        {
                                            string candidate = Path.Combine(dir, m.Groups[1].Value.Replace('/', '\\'));
                                            if (File.Exists(candidate)) exe = candidate;
                                        }
                                    }
                                    catch { }
                                }
                            }

                            string name = g.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(name) || name.StartsWith("@")) name = PackageBaseName(sub);
                            AddManifestHit(root, hits, roots, name, dir, exe);
                        }
                    }
                    catch { }
                }
            }
        }

        private static string PackageBaseName(string packageFullName)
        {
            string s = packageFullName ?? "";
            int us = s.IndexOf('_');
            if (us > 0) s = s.Substring(0, us);
            int dot = s.IndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
            return s;
        }

        private static void FromRiot(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string meta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Riot Games\\Metadata");
            string[] dirs;
            try { dirs = Directory.GetDirectories(meta); } catch { return; }
            foreach (string d in dirs)
            {
                try
                {
                    if (Path.GetFileName(d).StartsWith("riot_client", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string yaml in Directory.GetFiles(d, "*.yaml"))
                    {
                        Match m = Regex.Match(File.ReadAllText(yaml),
                            "product_install_full_path:\\s*\"?([^\"\\r\\n]+?)\"?\\s*$", RegexOptions.Multiline);
                        if (!m.Success) continue;
                        string dir = m.Groups[1].Value.Trim().Replace('/', '\\');
                        AddManifestHit(root, hits, roots, Path.GetFileName(dir.TrimEnd('\\')), dir, null);
                    }
                }
                catch { }
            }
        }

        private static void FromWeGameApps(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            foreach (string drive in FixedDriveRoots())
            {
                string[] containers =
                {
                    Path.Combine(drive, "WeGameApps"),
                    Path.Combine(drive, "Program Files\\WeGameApps"),
                    Path.Combine(drive, "Program Files (x86)\\WeGameApps")
                };
                foreach (string container in containers)
                {
                    string[] games;
                    try { games = Directory.GetDirectories(container); } catch { continue; }
                    foreach (string g in games)
                    {
                        try
                        {
                            string name = Path.GetFileName(g.TrimEnd('\\'));
                            if (name.Length == 0 || name[0] == '.' || HitsAny(name, InstalledJunk)) continue;
                            AddManifestHit(root, hits, roots, name, g, null);
                        }
                        catch { }
                    }
                }
            }
        }

        private static void FromBattleNet(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Battle.net\\Agent\\product.db");
            if (!File.Exists(db)) return;
            string text;
            try { text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(db)); } catch { return; }
            foreach (Match m in Regex.Matches(text, @"[A-Za-z]:[/\\][^\x00-\x1f""|?*<>]{2,200}"))
            {
                try
                {
                    string dir = m.Value.Replace('/', '\\').TrimEnd('\\');
                    if (dir.IndexOf("battle.net", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (!Directory.Exists(dir)) continue;
                    if (!File.Exists(Path.Combine(dir, ".build.info"))) continue;
                    AddManifestHit(root, hits, roots, Path.GetFileName(dir), dir, null);
                }
                catch { }
            }
        }

        private static readonly string[] InstalledJunk =
        {
            "wegame", "wechat", "微信", "腾讯会议", "tencent meeting", "腾讯文档",
            "电脑管家", "pc manager", "输入法", "sogou", "搜狗", "企业微信", "wework",
            "腾讯视频", "腾讯课堂", "浏览器", "browser", "腾讯qq", "qqmusic", "qq音乐",
            "运行库", "redistributable", "runtime", "driver", "驱动", "sdk", "toolkit",
            "update", "更新", "补丁", "安全", "杀毒", "antivirus", "defender",
            "office", "wps", "acrobat", "reader", "python", "java", "dotnet",
            "visual studio", "vc++", "directx", "nvidia", "amd software", "realtek",
            "网易云音乐", "cloudmusic", "迅雷", "thunder", "百度", "钉钉", "dingtalk"
        };

        private static readonly string[] GamePublishers =
        {
            "tencent", "腾讯", "riot games", "blizzard", "暴雪", "netease", "网易",
            "mihoyo", "米哈游", "hoyoverse", "ubisoft", "育碧", "electronic arts",
            "square enix", "capcom", "sega", "bandai", "2k games", "rockstar",
            "bethesda", "cd projekt", "paradox", "perfect world", "完美世界",
            "kingsoft", "西山居", "巨人网络", "盛趣", "shanda", "hypergryph", "鹰角",
            "kuro game", "库洛", "叠纸", "papergames", "游戏", "game studio"
        };

        private static bool HitsAny(string s, string[] words)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string w in words)
                if (s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static void FromInstalled(string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ScanUninstallHive(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", root, hits, roots, seen, canceled);
            ScanUninstallHive(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", root, hits, roots, seen, canceled);
            ScanUninstallHive(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", root, hits, roots, seen, canceled);
        }

        internal static void ScanUninstallHive(RegistryKey hive, string path, string root,
            List<ScanHit> hits, HashSet<string> roots, HashSet<string> seen, Func<bool> canceled)
        {
            using (RegistryKey k = hive.OpenSubKey(path))
            {
                if (k == null) return;
                foreach (string sub in k.GetSubKeyNames())
                {
                    if (Stop(canceled)) return;
                    try
                    {
                        using (RegistryKey g = k.OpenSubKey(sub))
                        {
                            if (g == null) continue;
                            if (g.GetValue("SystemComponent") is int && (int)g.GetValue("SystemComponent") != 0) continue;
                            if (g.GetValue("ParentKeyName") != null) continue;

                            string name = g.GetValue("DisplayName") as string;
                            string pub = g.GetValue("Publisher") as string ?? "";
                            if (HitsAny(name, InstalledJunk)) continue;
                            if (NetAcceleratorCatalog.IsAcceleratorLikeName(name)) continue;

                            string dir = CleanDir(g.GetValue("InstallLocation") as string);
                            bool derived = false;
                            if (dir == null)
                            {
                                derived = true;
                                dir = CleanDir(ExeDir(g.GetValue("DisplayIcon") as string));
                                if (dir == null) dir = CleanDir(ExeDir(g.GetValue("UninstallString") as string));
                                if (dir == null) dir = CleanDir(g.GetValue("InstallSource") as string);
                            }
                            if (dir == null || dir.Length < 4 || !seen.Add(dir)) continue;
                            if (roots.Contains(dir) || !Directory.Exists(dir)) continue;
                            if (IsSystemOrTooBroad(dir)) continue;
                            if (HitsAny(dir, InstalledJunk)) continue;
                            if (derived && SameNameAlreadyHit(hits, name)) continue;

                            bool trusted = !derived
                                && (HitsAny(pub, GamePublishers)
                                    || dir.IndexOf("WeGameApps", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (!trusted && !LooksLikeGameDir(dir, 3)) continue;

                            AddManifestHit(root, hits, roots, name, dir, null);
                        }
                    }
                    catch { }
                }
            }
        }

        private static string CleanDir(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string dir = value.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
            return dir.Length >= 4 ? dir : null;
        }

        private static string ExeDir(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            string s = command.Trim();
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                if (end > 1) s = s.Substring(1, end - 1);
            }
            else
            {
                int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe > 0) s = s.Substring(0, exe + 4);
            }
            try { return Path.GetDirectoryName(s.Trim()); }
            catch { return null; }
        }

        private static bool SameNameAlreadyHit(List<ScanHit> hits, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (ScanHit h in hits)
                if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsSystemOrTooBroad(string dir)
        {
            string win = null, pf = null, pf86 = null, common = null, profile = null;
            try
            {
                win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch { }

            if (!string.IsNullOrEmpty(win) && (Same(dir, win) || UnderRoot(dir, win))) return true;
            if (Same(dir, pf) || Same(dir, pf86) || Same(dir, common) || Same(dir, profile)) return true;

            try
            {
                string parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
                if (string.IsNullOrEmpty(parent)) return true;
            }
            catch { return true; }
            return false;
        }

        private static bool Same(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeGameDir(string dir, int depth)
        {
            string[] files, subs;
            try { files = Directory.GetFiles(dir); subs = Directory.GetDirectories(dir); }
            catch { return false; }
            if (HasGameSignals(files, subs)) return true;
            if (depth <= 1) return false;

            int visited = 0;
            foreach (string d in subs)
            {
                if (++visited > 24) break;
                string n = Path.GetFileName(d);
                if (n.Length == 0 || n[0] == '.' || SkipDirs.Contains(n)) continue;
                if (LooksLikeGameDir(d, depth - 1)) return true;
            }
            return false;
        }

        private static void Visit(string dir, string scanRoot, int depth,
            List<ScanHit> hits, HashSet<string> roots, int[] dirs, Func<bool> canceled, Action<int, int> progress)
        {
            if (depth <= 0 || (canceled != null && canceled())) return;
            dirs[0]++;
            if (progress != null && (dirs[0] & 63) == 0) progress(dirs[0], hits.Count);

            string[] files, subs;
            try { files = Directory.GetFiles(dir); subs = Directory.GetDirectories(dir); }
            catch { return; }

            if (HasGameSignals(files, subs))
            {
                string gameRoot = FindGameRoot(dir, scanRoot);
                if (roots.Add(gameRoot))
                {
                    string exe = PickMainExe(gameRoot);
                    if (exe != null)
                    {
                        hits.Add(new ScanHit
                        {
                            Name = Path.GetFileName(gameRoot.TrimEnd('\\')),
                            Proc = Path.GetFileNameWithoutExtension(exe),
                            Root = gameRoot,
                            Exe = exe
                        });
                        if (progress != null) progress(dirs[0], hits.Count);
                    }
                }
                return;
            }

            foreach (string d in subs)
            {
                if (canceled != null && canceled()) return;
                string n = Path.GetFileName(d);
                if (n.Length == 0 || n[0] == '.' || SkipDirs.Contains(n)) continue;
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                Visit(d, scanRoot, depth - 1, hits, roots, dirs, canceled, progress);
            }
        }

        private static bool HasGameSignals(string[] files, string[] subs)
        {
            bool electron = false, hasNw = false, hasWww = false;
            foreach (string f in files)
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (n == "unityplayer.dll" || n == "gameassembly.dll") return true;
                if (n == "steam_api.dll" || n == "steam_api64.dll" || n == "steam_appid.txt") return true;
                if (n == "eossdk-win64-shipping.dll") return true;
                if (n == "data.win") return true;
                if (n == "data.pck") return true;
                if (n.EndsWith(".rpa")) return true;
                if (n.EndsWith(".vpk")) return true;
                if (n.StartsWith("pakchunk") && n.EndsWith(".pak")) return true;
                if (n == "fna.dll" || n == "monogame.framework.dll") return true;
                if (n.EndsWith("-win64-shipping.exe") || n.EndsWith("-win32-shipping.exe")) return true;
                if (n.EndsWith(".dll") && (n.StartsWith("bink") || n.StartsWith("fmod") || n.StartsWith("crysystem"))) return true;
                if (n == "mss32.dll" || n == "mss64.dll") return true;
                if (n.StartsWith("goggame-")) return true;
                if (n == "steam_emu.ini" || n == "onlinefix.ini" || n == "cream_api.ini") return true;
                if (n.StartsWith("tersafe")) return true;
                if (n == ".build.info") return true;
                if (n == "nw.dll") hasNw = true;
                if (n == "icudtl.dat" || n == "chrome_100_percent.pak" || n == "v8_context_snapshot.bin" || n == "app.asar")
                    electron = true;
            }
            foreach (string d in subs)
            {
                string n = Path.GetFileName(d).ToLowerInvariant();
                if (n == "easyanticheat" || n == "easyanticheat_eos" || n == "battleye" || n == "tenprotect") return true;
                if (n == "renpy") return true;
                if (n == "www") hasWww = true;
            }
            if (hasNw && hasWww) return true;
            if (electron) return false;

            foreach (string f in files)
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (!n.EndsWith(".exe") || IsJunkName(n)) continue;
                try { if (new FileInfo(f).Length >= 200L * 1024 * 1024) return true; }
                catch { }
            }
            return false;
        }

        private static string FindGameRoot(string dir, string scanRoot)
        {
            string cur = dir;
            for (int i = 0; i < 4; i++)
            {
                string name = Path.GetFileName(cur.TrimEnd('\\'));
                if (name.Length == 0 || !IsGenericDirName(name)) break;
                string parent = null;
                try { parent = Path.GetDirectoryName(cur.TrimEnd('\\')); } catch { }
                if (parent == null || parent.Length <= scanRoot.TrimEnd('\\').Length) break;
                cur = parent;
            }
            return cur;
        }

        internal static string InferGameRoot(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath)) return null;
            string cur;
            try
            {
                string full = Path.GetFullPath(executablePath.Trim().Trim('"'));
                cur = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            }
            catch { return null; }
            if (string.IsNullOrEmpty(cur)) return null;

            for (int i = 0; i < 4; i++)
            {
                string name;
                try { name = Path.GetFileName(cur.TrimEnd('\\')); }
                catch { break; }
                if (string.IsNullOrEmpty(name) || !IsGenericDirName(name)) break;
                string parent;
                try { parent = Path.GetDirectoryName(cur.TrimEnd('\\')); }
                catch { break; }
                if (string.IsNullOrEmpty(parent)) break;
                cur = parent;
            }
            string candidate;
            try { candidate = Path.GetDirectoryName(cur.TrimEnd('\\')); }
            catch { candidate = null; }
            if (LooksLikeMultiFolderGameRoot(candidate, cur)) cur = candidate;
            return cur;
        }

        private static bool LooksLikeMultiFolderGameRoot(string candidate, string selectedDir)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(selectedDir)) return false;
            string selectedName;
            try { selectedName = Path.GetFileName(selectedDir.TrimEnd('\\')); }
            catch { return false; }
            string low = (selectedName ?? "").ToLowerInvariant();
            bool clientLike = IsClientComponentDirName(low);
            bool gameLike = string.Equals(low, "game", StringComparison.Ordinal)
                || string.Equals(low, "binaries", StringComparison.Ordinal);
            if (!clientLike && !gameLike) return false;

            try
            {
                foreach (string dir in Directory.GetDirectories(candidate))
                {
                    if (string.Equals(dir.TrimEnd('\\'), selectedDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    string n = (Path.GetFileName(dir.TrimEnd('\\')) ?? "").ToLowerInvariant();
                    if (clientLike && (string.Equals(n, "game", StringComparison.Ordinal)
                        || string.Equals(n, "binaries", StringComparison.Ordinal)
                        || string.Equals(n, "engine", StringComparison.Ordinal)
                        || string.Equals(n, "content", StringComparison.Ordinal))) return true;
                    if (gameLike && IsClientComponentDirName(n)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsClientComponentDirName(string lower)
        {
            if (string.IsNullOrEmpty(lower)) return false;
            return lower.Contains("client") || lower.Contains("launcher")
                || lower.Contains("tcls")
                || lower.Contains("客户端") || lower.Contains("启动器");
        }

        private static bool IsJunkName(string lower)
        {
            foreach (string j in JunkExe)
                if (lower.Contains(j)) return true;
            return Regex.IsMatch(lower, "\\d+\\.\\d+");
        }

        private static long SafeLen(FileInfo f)
        {
            try { return f.Length; }
            catch { return -1; }
        }

        private static readonly string[] LauncherDirTokens =
        {
            "launcher", "client", "tcls", "updater", "installer", "support", "tools", "redist"
        };

        private static readonly string[] LauncherNameTokens =
        {
            "launcher", "updater", "update", "patch", "config", "settings",
            "server", "dedicated", "benchmark", "editor", "service", "bootstrap",
            "backend", "daemon"
        };

        internal static string PickMainExe(string dir)
        {
            var exes = new List<FileInfo>();
            CollectExes(dir, 4, exes);
            if (exes.Count == 0) return null;

            string rootTrim = dir.TrimEnd('\\');
            string want = Norm(Path.GetFileName(rootTrim));

            foreach (FileInfo f in exes)
            {
                string low = f.Name.ToLowerInvariant();
                if (low.EndsWith("-win64-shipping.exe") || low.EndsWith("-win32-shipping.exe")) return f.FullName;
            }

            FileInfo unity = null; long unityLen = -1;
            foreach (FileInfo f in exes)
            {
                try
                {
                    if (IsJunkName(f.Name.ToLowerInvariant())) continue;
                    if (!Directory.Exists(Path.Combine(f.DirectoryName,
                        Path.GetFileNameWithoutExtension(f.Name) + "_Data"))) continue;
                    long len = SafeLen(f);
                    if (unity == null || len > unityLen) { unity = f; unityLen = len; }
                }
                catch { }
            }
            if (unity != null) return unity.FullName;

            FileInfo best = null, unreadable = null;
            int bestScore = int.MinValue;
            long bestLen = -1;
            foreach (FileInfo f in exes)
            {
                string low = f.Name.ToLowerInvariant();
                if (IsJunkName(low)) continue;
                long len = SafeLen(f);
                if (len < 0) { if (unreadable == null) unreadable = f; continue; }
                if (len < 128 * 1024) continue;

                int score = 0;
                string rel = RelDir(rootTrim, f.DirectoryName);
                if (rel.Length == 0) score += 15;
                else
                {
                    string[] segs = rel.Split('\\');
                    score -= 3 * segs.Length;
                    foreach (string seg in segs)
                    {
                        if (seg == "game" || seg == "games") { score += 40; continue; }
                        if (seg == "binaries") { score += 25; continue; }
                        if (seg == "bin" || seg == "bin64" || seg == "win64" || seg == "x64"
                            || seg == "retail" || seg == "shipping") { score += 10; continue; }
                        foreach (string t in LauncherDirTokens)
                            if (seg.Contains(t)) { score -= 45; break; }
                    }
                }
                string bare = Norm(Path.GetFileNameWithoutExtension(f.Name));
                if (want.Length > 2 && bare == want) score += 60;
                foreach (string t in LauncherNameTokens)
                    if (low.Contains(t)) { score -= 50; break; }
                if (len >= 100L * 1024 * 1024) score += 15;
                else if (len >= 20L * 1024 * 1024) score += 8;
                else if (len >= 1024 * 1024) score += 3;

                if (score > bestScore || (score == bestScore && len > bestLen))
                {
                    best = f; bestScore = score; bestLen = len;
                }
            }
            if (best != null) return best.FullName;
            return unreadable != null ? unreadable.FullName : null;
        }

        private static string RelDir(string root, string dir)
        {
            if (string.IsNullOrEmpty(dir) || dir.Length <= root.Length) return "";
            string prefix = root + "\\";
            if (!dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
            return dir.Substring(prefix.Length).ToLowerInvariant();
        }

        private static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static void CollectExes(string dir, int depth, List<FileInfo> outList)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.exe"))
                {
                    try { outList.Add(new FileInfo(f)); } catch { }
                }
                if (depth <= 1) return;
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string n = Path.GetFileName(d).ToLowerInvariant();
                    if (n.Contains("redist") || n == "directx" || n == "dotnet" || n == "support") continue;
                    CollectExes(d, depth - 1, outList);
                }
            }
            catch { }
        }
    }
}
