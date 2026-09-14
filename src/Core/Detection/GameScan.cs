// @author bdth 2074055628@qq.com
// File purpose Scans local games and maintains the game library catalog
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
    internal class ScanHit
    {
        public string Name;
        public string Proc;
        public string Root;
        public string Exe;
        public bool NeedsChoice;
    }

    internal static partial class GameScan
    {
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windows", "programdata", "$recycle.bin", "system volume information",
            "recovery", "perflogs", "onedrivetemp",
            "node_modules", ".git", "temp", "tmp", "cache", "__pycache__",
            "easyanticheat", "easyanticheat_eos", "battleye", "tenprotect"
        };

        private static bool IsSystemWideDirName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.EndsWith(":", StringComparison.Ordinal)) return true;
            return name.Equals("program files", StringComparison.OrdinalIgnoreCase)
                || name.Equals("program files (x86)", StringComparison.OrdinalIgnoreCase)
                || name.Equals("common files", StringComparison.OrdinalIgnoreCase)
                || name.Equals("users", StringComparison.OrdinalIgnoreCase)
                || name.Equals("desktop", StringComparison.OrdinalIgnoreCase)
                || name.Equals("downloads", StringComparison.OrdinalIgnoreCase)
                || name.Equals("documents", StringComparison.OrdinalIgnoreCase);
        }

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
            if (Stop(canceled)) return;
            try { FromShortcuts(root, hits, roots, canceled); } catch { }
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

        internal static void AddManifestHit(string root, List<ScanHit> hits, HashSet<string> roots,
            string name, string dir, string exePath)
        {
            dir = CleanDir(dir);
            if (dir == null) return;
            if (!UnderRoot(dir, root) || !Directory.Exists(dir)) return;
            if (roots.Contains(dir)) return;
            if (string.IsNullOrEmpty(name)) name = Path.GetFileName(dir);
            string exe = null, error;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                try
                {
                    string candidate = Environment.ExpandEnvironmentVariables(exePath.Trim().Trim('"')).Replace('/', '\\');
                    if (!Path.IsPathRooted(candidate)) candidate = Path.Combine(dir, candidate);
                    candidate = Path.GetFullPath(candidate);
                    if (UnderRoot(candidate, dir)
                        && string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase)
                        && !IsJunkName(candidate))
                        GameExecutableResolver.TryResolve(candidate, out exe, out error);
                }
                catch { }
            }
            List<ExecutableCandidateFacts> candidates = null;
            if (exe == null)
                candidates = ExecutableCandidateProbe.ListCandidates(dir, 24, out exe);
            bool found = exe != null;
            if (exe != null)
                AddUniqueManifestEntry(hits, name, dir, exe, false);
            else
            {
                // Install record found but entry not unique: keep the candidates for the user to choose so the game does not vanish
                foreach (ExecutableCandidateFacts candidate in candidates)
                    if (ExecutableCandidateProbe.Rank(candidate) > 0)
                    {
                        found = true;
                        AddUniqueManifestEntry(hits, name, dir, candidate.Path, true);
                    }
            }
            // The same EXE may be found via both a platform Content root and an uninstall record's parent directory; keep the earlier source's metadata
            // A duplicate entry still means this directory is recognized; do not go on to try the record's install source
            if (found) roots.Add(dir);
        }

        private static void AddUniqueManifestEntry(List<ScanHit> hits, string name, string dir,
            string exe, bool needsChoice)
        {
            foreach (ScanHit hit in hits)
                if (string.Equals(hit.Exe, exe, StringComparison.OrdinalIgnoreCase)) return;
            hits.Add(new ScanHit { Name = name, Proc = Path.GetFileNameWithoutExtension(exe),
                Root = dir, Exe = exe, NeedsChoice = needsChoice });
        }

        private static void FromInstalled(string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RegistryKey[] hives = { Registry.LocalMachine, Registry.LocalMachine, Registry.CurrentUser };
            string[] paths =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            for (int i = 0; i < hives.Length; i++)
            {
                if (Stop(canceled)) return;
                try { ScanUninstallHive(hives[i], paths[i], root, hits, roots, seen, canceled); }
                catch { }
            }
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

                            string[] candidates = InstalledDirectoryCandidates(
                                g.GetValue("InstallLocation") as string,
                                g.GetValue("DisplayIcon") as string,
                                g.GetValue("UninstallString") as string,
                                g.GetValue("InstallSource") as string);
                            ScanInstalledRecord(g.GetValue("DisplayName") as string, candidates,
                                root, hits, roots, seen, canceled);
                        }
                    }
                    catch { }
                }
            }
        }

        internal static string[] InstalledDirectoryCandidates(string installLocation, string displayIcon,
            string uninstallString, string installSource)
        {
            var candidates = new List<string>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddInstalledDirectoryCandidate(installLocation, candidates, unique);
            AddInstalledCommandDirectories(displayIcon, candidates, unique);
            AddInstalledCommandDirectories(uninstallString, candidates, unique);
            AddInstalledDirectoryCandidate(installSource, candidates, unique);
            return candidates.ToArray();
        }

        private static void AddInstalledDirectoryCandidate(string value,
            List<string> candidates, HashSet<string> unique)
        {
            string dir = CleanDir(value);
            if (dir != null && unique.Add(dir)) candidates.Add(dir);
        }

        private static void AddInstalledCommandDirectories(string command,
            List<string> candidates, HashSet<string> unique)
        {
            string target = InstalledCommandTarget(command);
            if (target == null) return;
            // The icon or uninstaller may sit in a subdirectory like Bin or Binaries; recover the install root first, keep the original directory as fallback
            AddInstalledDirectoryCandidate(InferGameRoot(target), candidates, unique);
            AddInstalledDirectoryCandidate(Path.GetDirectoryName(target), candidates, unique);
        }

        internal static void ScanInstalledRecord(string name, string[] candidates, string root,
            List<ScanHit> hits, HashSet<string> roots, HashSet<string> seen, Func<bool> canceled)
        {
            if (candidates == null) return;
            foreach (string value in candidates)
            {
                if (Stop(canceled)) return;
                try
                {
                    string dir = CleanDir(value);
                    if (dir == null || !UnderRoot(dir, root) || !Directory.Exists(dir)) continue;
                    if (IsSystemOrTooBroad(dir) || IsSystemWideDirName(Path.GetFileName(dir))) continue;
                    // When the platform manifest already gives the install root, the inner directory derived from the icon does not override its name and root
                    foreach (ScanHit hit in hits)
                        if (!string.IsNullOrEmpty(hit.Root) && UnderRoot(dir, hit.Root)) return;
                    if (roots.Contains(dir))
                    {
                        foreach (ScanHit hit in hits)
                            if (!string.IsNullOrEmpty(hit.Exe) && UnderRoot(hit.Exe, dir)) return;
                        continue;
                    }
                    if (!seen.Add(dir)) continue;
                    // Publisher, product name and client wording in the directory are not evidence of game identity
                    if (!LooksLikeGameDir(dir, 3)) continue;
                    AddManifestHit(root, hits, roots, name, dir, null);
                    // Non-empty, existing or engine signals do not mean an entry was found; on failure continue with the same record's fallback paths
                    if (roots.Contains(dir)) return;
                }
                catch { }
            }
        }

        private static string CleanDir(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try
            {
                string dir = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Replace('/', '\\');
                if (!Path.IsPathRooted(dir)) return null;
                dir = Path.GetFullPath(dir).TrimEnd('\\');
                return dir.Length >= 4 ? dir : null;
            }
            catch { return null; }
        }

        private static string InstalledCommandTarget(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            try
            {
                string s = Environment.ExpandEnvironmentVariables(command.Trim());
                if (s.StartsWith("\""))
                {
                    int end = s.IndexOf('"', 1);
                    if (end <= 1) return null;
                    s = s.Substring(1, end - 1);
                }
                else
                {
                    for (int start = 0; start < s.Length;)
                    {
                        int exe = s.IndexOf(".exe", start, StringComparison.OrdinalIgnoreCase);
                        if (exe < 0) break;
                        int end = exe + 4;
                        if (end == s.Length || char.IsWhiteSpace(s[end]) || s[end] == ',')
                        { s = s.Substring(0, end); break; }
                        start = end;
                    }
                    int comma = s.LastIndexOf(',');
                    int iconIndex;
                    if (comma > 0 && int.TryParse(s.Substring(comma + 1).Trim(), out iconIndex))
                        s = s.Substring(0, comma);
                }
                return CleanDir(s);
            }
            catch { return null; }
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

        internal static bool LooksLikeGameDir(string dir, int depth)
        {
            string[] files, subs;
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
                files = Directory.GetFiles(dir); subs = Directory.GetDirectories(dir);
            }
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
            return cur;
        }

        private static bool IsJunkName(string name)
        {
            return GameSessionDetector.ElectionVetoed(
                Path.GetFileNameWithoutExtension(name ?? ""), null);
        }

        // The scan only recommends an entry with unique static evidence; when several graphics programs cannot be told apart, the user chooses
        // No tie-breaking by game name, client role, EXE size or launcher and client wording in the directory
        internal static string PickMainExe(string dir)
        {
            return ExecutableCandidateProbe.PickMainExecutable(dir);
        }

    }
}
