// @author bdth 2074055628@qq.com
// 文件用途 解析 Steam 桌面快捷方式（.url）定位游戏安装目录与主程序

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class SteamShortcut
    {
        private const string RunGamePrefix = "steam://rungameid/";

        public static bool TryResolve(string urlPath, out string executablePath, out string error)
        {
            string ignored;
            return TryResolve(urlPath, out executablePath, out error, out ignored);
        }

        public static bool TryResolve(string urlPath, out string executablePath, out string error,
            out string suggestedName)
        {
            executablePath = null;
            error = null;
            suggestedName = null;
            string content;
            try { content = File.ReadAllText(urlPath); }
            catch { error = "无法读取快捷方式文件"; return false; }
            long appId;
            if (!TryParseUrlFile(content, out appId))
            {
                error = "不是 Steam 游戏快捷方式（未找到 steam://rungameid 链接）";
                return false;
            }
            string steamRoot = FindSteamRoot();
            if (steamRoot == null)
            {
                error = "未找到 Steam 安装（注册表无 SteamPath）";
                return false;
            }
            string gameRoot = null;
            foreach (string library in LibraryRoots(steamRoot))
            {
                string manifest = Path.Combine(library, @"steamapps\appmanifest_" + appId + ".acf");
                try
                {
                    if (!File.Exists(manifest)) continue;
                    string installDir = ParseVdfValue(File.ReadAllText(manifest), "installdir");
                    if (string.IsNullOrEmpty(installDir)) continue;
                    string candidate = Path.Combine(library, @"steamapps\common\" + installDir);
                    if (Directory.Exists(candidate)) { gameRoot = candidate; break; }
                }
                catch { }
            }
            if (gameRoot == null)
            {
                error = "Steam 清单中找不到该游戏（appid " + appId + "），可能是非 Steam 游戏的快捷方式或游戏未安装";
                return false;
            }
            string exe = PickMainExecutable(gameRoot, Path.GetFileName(gameRoot));
            if (exe == null)
            {
                error = "在游戏目录里没有找到合适的主程序：" + gameRoot;
                return false;
            }
            executablePath = exe;
            suggestedName = Path.GetFileName(gameRoot);
            return true;
        }

        internal static bool TryParseUrlFile(string content, out long appId)
        {
            appId = 0;
            if (string.IsNullOrEmpty(content)) return false;
            foreach (string raw in content.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                string url = line.Substring(4).Trim();
                if (!url.StartsWith(RunGamePrefix, StringComparison.OrdinalIgnoreCase)) return false;
                string digits = url.Substring(RunGamePrefix.Length);
                int end = 0;
                while (end < digits.Length && char.IsDigit(digits[end])) end++;
                if (end == 0) return false;
                return long.TryParse(digits.Substring(0, end), out appId) && appId > 0;
            }
            return false;
        }

        private static string FindSteamRoot()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string path = k == null ? null : k.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(path))
                    {
                        path = path.Replace('/', '\\');
                        if (Directory.Exists(path)) return path;
                    }
                }
            }
            catch { }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    string path = k == null ? null : k.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
                }
            }
            catch { }
            return null;
        }

        private static IEnumerable<string> LibraryRoots(string steamRoot)
        {
            var roots = new List<string> { steamRoot };
            foreach (string vdf in new[]
            {
                Path.Combine(steamRoot, @"steamapps\libraryfolders.vdf"),
                Path.Combine(steamRoot, @"config\libraryfolders.vdf")
            })
            {
                try
                {
                    if (!File.Exists(vdf)) continue;
                    foreach (string path in ParseLibraryPaths(File.ReadAllText(vdf)))
                    {
                        bool known = false;
                        foreach (string existing in roots)
                            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                            { known = true; break; }
                        if (!known && Directory.Exists(path)) roots.Add(path);
                    }
                }
                catch { }
            }
            return roots;
        }

        internal static List<string> ParseLibraryPaths(string vdfContent)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(vdfContent)) return result;
            foreach (Match m in Regex.Matches(vdfContent, "\"path\"\\s+\"((?:\\\\.|[^\"\\\\])*)\""))
            {
                string path = m.Groups[1].Value.Replace(@"\\", @"\");
                if (path.Length > 0) result.Add(path);
            }
            return result;
        }

        internal static string ParseVdfValue(string content, string key)
        {
            if (string.IsNullOrEmpty(content)) return null;
            Match m = Regex.Match(content,
                "\"" + Regex.Escape(key) + "\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace(@"\\", @"\") : null;
        }

        private static readonly string[] RejectNameTokens =
        {
            "launcher", "updater", "update", "setup", "install", "unins", "crash", "report",
            "helper", "service", "redist", "vc_redist", "vcredist", "dxsetup", "prereq",
            "easyanticheat", "battleye", "eac", "cleanup", "benchmark", "dotnet", "activation"
        };

        internal static string PickMainExecutable(string root, string installDirName)
        {
            var candidates = new List<string>();
            CollectExecutables(root, 0, candidates);
            string best = null;
            long bestScore = long.MinValue;
            string normalizedInstall = NormalizeName(installDirName);
            foreach (string path in candidates)
            {
                string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                bool rejected = false;
                foreach (string token in RejectNameTokens)
                    if (name.Contains(token)) { rejected = true; break; }
                if (rejected) continue;
                long size;
                try { size = new FileInfo(path).Length; } catch { continue; }
                string low = path.ToLowerInvariant();
                long score = 0;
                if (low.Contains(@"\bin\")) score += 30;
                if (low.Contains("win64") || low.Contains("x64") || low.Contains("shipping")) score += 25;
                if (size >= 50L * 1024 * 1024) score += 20;
                else if (size >= 5L * 1024 * 1024) score += 10;
                string normalizedName = NormalizeName(name);
                if (normalizedInstall.Length >= 3
                    && (normalizedInstall.Contains(normalizedName) || normalizedName.Contains(normalizedInstall)))
                    score += 25;
                score = score * 1000000000L + Math.Min(size, 999999999L);
                if (score > bestScore) { bestScore = score; best = path; }
            }
            return best;
        }

        private static void CollectExecutables(string dir, int depth, List<string> result)
        {
            if (depth > 4 || result.Count > 400) return;
            try
            {
                foreach (string file in Directory.GetFiles(dir, "*.exe")) result.Add(file);
                foreach (string sub in Directory.GetDirectories(dir))
                    CollectExecutables(sub, depth + 1, result);
            }
            catch { }
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }
}
