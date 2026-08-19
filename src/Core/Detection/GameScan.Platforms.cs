// @author bdth 2074055628@qq.com
// 文件用途 游戏扫描平台清单分部 Steam Epic GOG 育碧 Riot WeGame 战网 Xbox 商店
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class GameScan
    {
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

        private static bool HitsAny(string s, string[] words)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string w in words)
                if (s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
