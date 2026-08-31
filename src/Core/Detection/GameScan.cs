// @author bdth 2074055628@qq.com
// 文件用途 扫描本机游戏并维护游戏库目录
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
            if (roots.Contains(dir)) return;
            string exe = exePath != null && File.Exists(exePath) ? exePath : PickMainExe(dir);
            if (exe == null) return;
            roots.Add(dir);
            if (string.IsNullOrEmpty(name)) name = Path.GetFileName(dir);
            hits.Add(new ScanHit { Name = name, Proc = Path.GetFileNameWithoutExtension(exe), Root = dir, Exe = exe });
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
                            if (derived && SameNameAlreadyHit(hits, name)) continue;
                            // 发布商 产品名和目录里的客户端字样都不是游戏身份依据
                            if (!LooksLikeGameDir(dir, 3)) continue;

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

        // 扫描只推荐有唯一静态证据的入口 无法区分多个图形程序时交给用户选择
        // 不按游戏名 客户端角色 EXE 大小或目录里的 launcher/client 字样决胜
        internal static string PickMainExe(string dir)
        {
            return ExecutableCandidateProbe.PickMainExecutable(dir);
        }

    }
}
