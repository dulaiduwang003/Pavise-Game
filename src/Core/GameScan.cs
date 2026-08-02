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

        private static readonly string[] JunkManifest =
        {
            "redistributable", "steamworks common", "proton", "steam linux runtime", "steamvr"
        };

        public static List<ScanHit> Run(string root, Func<bool> canceled, Action<int, int> progress)
        {
            var hits = new List<ScanHit>();
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { FromSteam(root, hits, roots); } catch { }
            try { FromEpic(root, hits, roots); } catch { }
            try { FromGog(root, hits, roots); } catch { }
            try { FromUbisoft(root, hits, roots); } catch { }
            try { FromWeGame(root, hits, roots); } catch { }
            if (progress != null) progress(0, hits.Count);

            int[] dirs = { 0 };
            try { Visit(root, root, 8, hits, roots, dirs, canceled, progress); }
            catch { }
            return hits;
        }

        private static bool UnderRoot(string dir, string root)
        {
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
            hits.Add(new ScanHit { Name = name, Proc = Path.GetFileNameWithoutExtension(exe), Root = dir });
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
            steam = steam.Replace('/', '\\');

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

        private static readonly string[] TencentJunk =
        {
            "wegame", "wechat", "微信", "腾讯会议", "tencent meeting", "腾讯文档",
            "电脑管家", "pc manager", "输入法", "sogou", "搜狗", "企业微信", "wework",
            "腾讯视频", "腾讯课堂", "浏览器", "qqbrowser", "腾讯qq", "qqmusic", "qq音乐"
        };

        private static bool IsTencentJunk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string j in TencentJunk)
                if (s.IndexOf(j, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static void FromWeGame(string root, List<ScanHit> hits, HashSet<string> roots)
        {
            string[] unKeys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (string kp in unKeys)
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
                                string dir = g.GetValue("InstallLocation") as string;
                                if (string.IsNullOrEmpty(dir)) continue;
                                dir = dir.Trim().Trim('"');

                                string name = g.GetValue("DisplayName") as string;
                                string pub = g.GetValue("Publisher") as string ?? "";
                                string un = g.GetValue("UninstallString") as string ?? "";

                                bool tencent = pub.IndexOf("Tencent", StringComparison.OrdinalIgnoreCase) >= 0
                                            || pub.IndexOf("腾讯", StringComparison.Ordinal) >= 0
                                            || dir.IndexOf("WeGame", StringComparison.OrdinalIgnoreCase) >= 0
                                            || un.IndexOf("WeGame", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (!tencent) continue;
                                if (IsTencentJunk(name) || IsTencentJunk(dir)) continue;

                                AddManifestHit(root, hits, roots, name, dir, null);
                            }
                        }
                        catch { }
                    }
                }
            }
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
                            Root = gameRoot
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
                if (name.Length == 0 || !GenericDirs.Contains(name)) break;
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
                if (string.IsNullOrEmpty(name) || !GenericDirs.Contains(name)) break;
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
            bool clientLike = low.Contains("client") || low.Contains("launcher")
                           || low.Contains("客户端") || low.Contains("启动器");
            if (!clientLike) return false;

            try
            {
                foreach (string dir in Directory.GetDirectories(candidate))
                {
                    if (string.Equals(dir.TrimEnd('\\'), selectedDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    string n = Path.GetFileName(dir.TrimEnd('\\'));
                    if (string.Equals(n, "game", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n, "binaries", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n, "engine", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n, "content", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsJunkName(string lower)
        {
            foreach (string j in JunkExe)
                if (lower.Contains(j)) return true;
            return Regex.IsMatch(lower, "\\d+\\.\\d+");
        }

        private static string PickMainExe(string dir)
        {
            var exes = new List<FileInfo>();
            CollectExes(dir, 4, exes);
            if (exes.Count == 0) return null;

            string want = Norm(Path.GetFileName(dir.TrimEnd('\\')));
            FileInfo best = null, bestAny = null, named = null;
            foreach (FileInfo f in exes)
            {
                string low = f.Name.ToLowerInvariant();
                if (low.EndsWith("-win64-shipping.exe") || low.EndsWith("-win32-shipping.exe")) return f.FullName;
                if (bestAny == null || f.Length > bestAny.Length) bestAny = f;
                if (IsJunkName(low)) continue;
                if (named == null && want.Length > 2 && Norm(Path.GetFileNameWithoutExtension(f.Name)) == want) named = f;
                if (best == null || f.Length > best.Length) best = f;
            }
            FileInfo pick = named != null ? named : (best != null ? best : bestAny);
            return pick != null && pick.Length >= 128 * 1024 ? pick.FullName : null;
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
