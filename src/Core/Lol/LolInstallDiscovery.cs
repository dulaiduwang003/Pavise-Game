// @author bdth 2074055628@qq.com
// 文件用途 发现英雄联盟与 WeGame 安装目录

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class LolInstallDiscovery
    {
        private static readonly string[] LolRegistryKeys =
        {
            @"Software\Tencent\LOL",
            @"Software\WOW6432Node\Tencent\LOL",
            @"Software\Tencent\WeGame\LOL"
        };

        private static readonly string[] WeGameRegistryKeys =
        {
            @"Software\Tencent\WeGame",
            @"Software\WOW6432Node\Tencent\WeGame",
            @"Software\Tencent\GameAssistant",
            @"Software\WOW6432Node\Tencent\GameAssistant"
        };

        private static readonly string[] RegistryValues =
        {
            "InstallPath", "InstallDir", "Path", "InstallLocation"
        };

        public static string FindLolRoot(string preferred)
        {
            return FindLolRoot(preferred, true, null);
        }

        public static string FindLolRoot(string preferred, bool deep, Func<bool> cancelled)
        {
            string root = FindLolFromProcesses();
            if (root != null) return root;
            root = NormalizeLolRoot(preferred);
            if (root != null) return root;
            for (int i = 0; i < LolRegistryKeys.Length; i++)
            {
                root = FindRegistryLolRoot(Registry.CurrentUser, LolRegistryKeys[i]);
                if (root != null) return root;
                root = FindRegistryLolRoot(Registry.LocalMachine, LolRegistryKeys[i]);
                if (root != null) return root;
            }
            if (!deep || (cancelled != null && cancelled())) return null;
            return FindCommonLolRoot(cancelled);
        }

        public static string FindWeGameRoot(string preferred, string lolRoot)
        {
            return FindWeGameRoot(preferred, lolRoot, true, null);
        }

        public static string FindWeGameRoot(string preferred, string lolRoot, bool deep, Func<bool> cancelled)
        {
            string root = FindWeGameFromProcesses();
            if (root != null) return root;
            root = NormalizeWeGameRoot(preferred);
            if (root != null) return root;
            root = FindWeGameFromAppPaths();
            if (root != null) return root;
            for (int i = 0; i < WeGameRegistryKeys.Length; i++)
            {
                root = FindRegistryWeGameRoot(Registry.CurrentUser, WeGameRegistryKeys[i]);
                if (root != null) return root;
                root = FindRegistryWeGameRoot(Registry.LocalMachine, WeGameRegistryKeys[i]);
                if (root != null) return root;
            }
            root = FindWeGameFromLaunchFile(lolRoot);
            if (root != null) return root;
            if (!deep || (cancelled != null && cancelled())) return null;
            return FindCommonWeGameRoot(cancelled);
        }

        public static string FindWeGameExecutable(string root)
        {
            root = NormalizeWeGameRoot(root);
            if (root == null) return null;
            string[] candidates =
            {
                Path.Combine(root, "wegame.exe"),
                Path.Combine(root, "WeGame.exe"),
                Path.Combine(root, "WeGameLauncher.exe"),
                Path.Combine(root, "apps", "wegame.exe")
            };
            for (int i = 0; i < candidates.Length; i++)
                if (File.Exists(candidates[i])) return candidates[i];
            try
            {
                string[] files = Directory.GetFiles(root, "wegame.exe", SearchOption.TopDirectoryOnly);
                if (files.Length > 0) return files[0];
            }
            catch { }
            return null;
        }

        public static bool IsValidLolRoot(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
                bool client = File.Exists(Path.Combine(root, "LeagueClient", "LeagueClient.exe"))
                    || File.Exists(Path.Combine(root, "LeagueClient.exe"));
                return client && Directory.Exists(Path.Combine(root, "Game"));
            }
            catch { return false; }
        }

        public static bool IsValidWeGameRoot(string root)
        {
            return FindWeGameExecutableUnchecked(root) != null;
        }

        private static string FindRegistryLolRoot(RegistryKey hive, string subKey)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(subKey))
                {
                    if (key == null) return null;
                    for (int i = 0; i < RegistryValues.Length; i++)
                    {
                        string root = NormalizeLolRoot(Convert.ToString(key.GetValue(RegistryValues[i])));
                        if (root != null) return root;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FindRegistryWeGameRoot(RegistryKey hive, string subKey)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(subKey))
                {
                    if (key == null) return null;
                    for (int i = 0; i < RegistryValues.Length; i++)
                    {
                        string root = NormalizeWeGameRoot(Convert.ToString(key.GetValue(RegistryValues[i])));
                        if (root != null) return root;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FindWeGameFromAppPaths()
        {
            string[] keys =
            {
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\wegame.exe",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\wegame.exe"
            };
            RegistryKey[] hives = { Registry.CurrentUser, Registry.LocalMachine };
            for (int h = 0; h < hives.Length; h++)
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    try
                    {
                        using (RegistryKey key = hives[h].OpenSubKey(keys[i]))
                        {
                            if (key == null) continue;
                            string root = NormalizeWeGameRoot(Convert.ToString(key.GetValue(null)));
                            if (root != null) return root;
                            root = NormalizeWeGameRoot(Convert.ToString(key.GetValue("Path")));
                            if (root != null) return root;
                            root = NormalizeWeGameRoot(Convert.ToString(key.GetValue("ExeFile")));
                            if (root != null) return root;
                            root = NormalizeWeGameRoot(Convert.ToString(key.GetValue("InstallPath")));
                            if (root != null) return root;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string FindLolFromProcesses()
        {
            return FindRootFromProcesses(
                new[]
                {
                    "LeagueClient",
                    "LeagueClientUx",
                    "League of Legends"
                },
                LolRuntimeProcesses.IsCoreIdentityCandidateName,
                NormalizeLolRoot);
        }

        private static string FindWeGameFromProcesses()
        {
            return FindRootFromProcesses(
                new[]
                {
                    "wegame",
                    "wegame_env",
                    "wegameclient"
                },
                LolRuntimeProcesses.IsWeGameDiscoveryCandidateName,
                NormalizeWeGameRoot);
        }

        private static string FindRootFromProcesses(
            string[] names,
            Func<string, bool> candidateName,
            Func<string, string> normalize)
        {
            int currentSession;
            if (!LolRuntimeProcesses.TryGetCurrentSessionId(
                    out currentSession))
                return null;
            for (int i = 0; i < names.Length; i++)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(names[i]); }
                catch { continue; }
                try
                {
                    foreach (Process process in processes)
                    {
                        string path;
                        if (!LolRuntimeProcesses.TryGetOwnedImagePath(
                                process, currentSession, out path))
                            continue;
                        if (!candidateName(Path.GetFileName(path)))
                            continue;
                        string root = normalize(path);
                        if (root != null) return root;
                    }
                }
                finally
                {
                    foreach (Process process in processes)
                        if (process != null)
                            try { process.Dispose(); } catch { }
                }
            }
            return null;
        }

        private static string FindCommonLolRoot(Func<bool> cancelled)
        {
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (cancelled != null && cancelled()) return null;
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                    string basePath = drive.RootDirectory.FullName;
                    string[] candidates =
                    {
                        Path.Combine(basePath, "WeGameApps", "英雄联盟"),
                        Path.Combine(basePath, "Program Files", "WeGameApps", "英雄联盟"),
                        Path.Combine(basePath, "Program Files (x86)", "WeGameApps", "英雄联盟"),
                        Path.Combine(basePath, "英雄联盟")
                    };
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        if (cancelled != null && cancelled()) return null;
                        string root = NormalizeLolRoot(candidates[i]);
                        if (root != null) return root;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FindCommonWeGameRoot(Func<bool> cancelled)
        {
            var candidates = new List<string>();
            try
            {
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WeGame"));
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WeGame"));
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                    candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "WeGame"));
                    candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files", "WeGame"));
                    candidates.Add(Path.Combine(drive.RootDirectory.FullName, "WeGame"));
                }
            }
            catch { }
            for (int i = 0; i < candidates.Count; i++)
            {
                if (cancelled != null && cancelled()) return null;
                string root = NormalizeWeGameRoot(candidates[i]);
                if (root != null) return root;
            }
            return null;
        }

        private static string FindWeGameFromLaunchFile(string lolRoot)
        {
            if (!IsValidLolRoot(lolRoot)) return null;
            string[] candidates =
            {
                Path.Combine(lolRoot, "TCLS", "wegame_launch.ini"),
                Path.Combine(lolRoot, "TCLS", "wegame_launch.tmp")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                string content;
                if (!TryReadSmall(candidates[i], out content)) continue;
                MatchCollection matches = Regex.Matches(
                    content,
                    @"[A-Za-z]:\\[^\""\r\n]*?\\(?:wegame\.exe|WeGameLauncher\.exe)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                foreach (Match match in matches)
                {
                    string root = NormalizeWeGameRoot(match.Value);
                    if (root != null) return root;
                }
            }
            return null;
        }

        private static string NormalizeLolRoot(string value)
        {
            string path = NormalizePath(value);
            if (path == null) return null;
            if (File.Exists(path)) path = Path.GetDirectoryName(path);
            DirectoryInfo current;
            try { current = new DirectoryInfo(path); }
            catch { return null; }
            for (int i = 0; current != null && i < 9; i++, current = current.Parent)
                if (IsValidLolRoot(current.FullName)) return current.FullName.TrimEnd('\\');
            return null;
        }

        private static string NormalizeWeGameRoot(string value)
        {
            string path = NormalizePath(value);
            if (path == null) return null;
            try
            {
                if (File.Exists(path)) path = Path.GetDirectoryName(path);
                DirectoryInfo current = new DirectoryInfo(path);
                for (int i = 0; current != null && i < 6; i++, current = current.Parent)
                    if (FindWeGameExecutableUnchecked(current.FullName) != null)
                    {
                        if (IsVolumeRoot(current)) return null;
                        return current.FullName.TrimEnd('\\');
                    }
            }
            catch { }
            return null;
        }

        private static bool IsVolumeRoot(DirectoryInfo directory)
        {
            if (directory == null) return false;
            try { return directory.Parent == null; }
            catch { return true; }
        }

        private static string FindWeGameExecutableUnchecked(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            try
            {
                string direct = Path.Combine(root, "wegame.exe");
                if (File.Exists(direct)) return direct;
                direct = Path.Combine(root, "WeGame.exe");
                if (File.Exists(direct)) return direct;
                direct = Path.Combine(root, "WeGameLauncher.exe");
                if (File.Exists(direct)) return direct;
                direct = Path.Combine(root, "apps", "wegame.exe");
                return File.Exists(direct) ? direct : null;
            }
            catch { return null; }
        }

        private static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try
            {
                string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                return Path.GetFullPath(path);
            }
            catch { return null; }
        }

        private static bool TryReadSmall(string path, out string content)
        {
            content = null;
            try
            {
                if (!File.Exists(path)) return false;
                using (var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > 1024 * 1024) return false;
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        content = reader.ReadToEnd();
                }
                return true;
            }
            catch { return false; }
        }
    }
}
