// @author bdth 2074055628@qq.com
// File purpose Game scan platform manifest partial: Steam, Epic, GOG, Ubisoft, Riot, WeGame, Battle.net, Xbox Store
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    // Each platform reads only its own manifest files or registry; no full-disk walk
    //   roots is for dedup: a game listed by two platforms keeps one entry
    //   A parse failure on any platform affects only that platform and never aborts the whole scan
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
            // Still scan the default library while Steam is writing the library manifest; supports old manifests where numeric keys hold the path directly
            try
            {
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf),
                        "\"(?:path|[0-9]+)\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase))
                    {
                        try
                        {
                            string library = m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                            // The new-format apps node also has numeric keys; values like capacity are not library paths
                            if (Path.IsPathRooted(library)) libs.Add(library);
                        }
                        catch { }
                    }
            }
            catch { }

            foreach (string lib in libs)
            {
                string sa;
                string[] acfs;
                try
                {
                    sa = Path.Combine(lib, "steamapps");
                    acfs = Directory.GetFiles(sa, "appmanifest_*.acf");
                }
                catch { continue; }
                foreach (string acf in acfs)
                {
                    try
                    {
                        string txt = File.ReadAllText(acf);
                        Match mn = Regex.Match(txt, "\"name\"\\s+\"([^\"]+)\"");
                        Match md = Regex.Match(txt, "\"installdir\"\\s+\"([^\"]+)\"");
                        if (!md.Success) continue;
                        string name = mn.Success ? mn.Groups[1].Value : null;
                        AddManifestHit(root, hits, roots, name, Path.Combine(sa, "common\\" + md.Groups[1].Value), null);
                    }
                    catch { }
                }
            }
        }

        internal static string JsonStr(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || key == null) return null;
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] != '"') continue;
                string token;
                if (!ReadJsonString(json, ref i, out token)) return null;
                if (!string.Equals(token, key, StringComparison.Ordinal)) { i--; continue; }
                int next = i;
                while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                if (next >= json.Length || json[next] != ':') { i--; continue; }
                next++;
                while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                string value;
                return ReadJsonString(json, ref next, out value) ? value : null;
            }
            return null;
        }

        // Reads JSON strings only; an escaped quote is not a terminator and Unicode paths are not left as literals
        private static bool ReadJsonString(string json, ref int offset, out string value)
        {
            value = null;
            if (offset >= json.Length || json[offset] != '"') return false;
            offset++;
            var result = new StringBuilder();
            while (offset < json.Length)
            {
                char current = json[offset++];
                if (current == '"') { value = result.ToString(); return true; }
                if (current < 32) return false;
                if (current != '\\') { result.Append(current); continue; }
                if (offset >= json.Length) return false;
                switch (json[offset++])
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (json.Length - offset < 4) return false;
                        int decoded = 0;
                        for (int digit = 0; digit < 4; digit++)
                        {
                            char hex = json[offset++];
                            int number = hex >= '0' && hex <= '9' ? hex - '0'
                                : hex >= 'a' && hex <= 'f' ? hex - 'a' + 10
                                : hex >= 'A' && hex <= 'F' ? hex - 'A' + 10 : -1;
                            if (number < 0) return false;
                            decoded = decoded * 16 + number;
                        }
                        result.Append((char)decoded);
                        break;
                    default: return false;
                }
            }
            return false;
        }

        private static void FromEpic(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string mdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic\\EpicGamesLauncher\\Data\\Manifests");
            FromEpicManifests(mdir, root, hits, roots);
        }

        internal static void FromEpicManifests(string mdir, string root, List<ScanHit> hits, HashSet<string> roots)
        {
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
                            if (name.Length == 0 || name[0] == '.') continue;
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
