// @author bdth 2074055628@qq.com

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace AegisApp
{
    internal sealed class LolLcuCredentials
    {
        public readonly int Port;
        public readonly string Token;

        public LolLcuCredentials(int port, string token)
        {
            Port = port;
            Token = token;
        }
    }

    internal sealed class LolCleanupResult
    {
        public int Count;
        public long WorkingSetBytes;
    }

    internal sealed class LolProcessSnapshot
    {
        public bool ClientRunning;
        public bool GameRunning;
        public int WeGameProcessCount;
        public int CrossProcessCount;
        public int UxProcessCount;
    }

    internal static class LolCredentialParser
    {
        private static readonly Regex PortPattern = new Regex(
            @"(?:^|\s)--app-port(?:=|\s+)(?:\"")?(\d{1,5})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TokenPattern = new Regex(
            @"(?:^|\s)--remoting-auth-token(?:=|\s+)(?:\""([^\""]+)\""|([^\s\""]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool TryParseCommandLine(string value, out int port, out string token)
        {
            port = 0;
            token = null;
            if (string.IsNullOrEmpty(value)) return false;
            int end = value.Length;
            while (end > 0)
            {
                int newline = value.LastIndexOfAny(new[] { '\r', '\n' }, end - 1);
                int start = newline + 1;
                if (TryParseSegment(value, start, end - start, out port, out token)) return true;
                end = newline;
                while (end > 0 && (value[end - 1] == '\r' || value[end - 1] == '\n')) end--;
            }
            return false;
        }

        private static bool TryParseSegment(
            string value, int start, int length, out int port, out string token)
        {
            port = 0;
            token = null;
            string segment = start == 0 && length == value.Length
                ? value : value.Substring(start, length);
            MatchCollection portMatches = PortPattern.Matches(segment);
            MatchCollection tokenMatches = TokenPattern.Matches(segment);
            if (portMatches.Count == 0 || tokenMatches.Count == 0) return false;
            Match portMatch = portMatches[portMatches.Count - 1];
            Match tokenMatch = tokenMatches[tokenMatches.Count - 1];
            if (!int.TryParse(portMatch.Groups[1].Value, out port) || !ValidPort(port)) return false;
            token = tokenMatch.Groups[1].Success
                ? tokenMatch.Groups[1].Value
                : tokenMatch.Groups[2].Value;
            if (!ValidToken(token))
            {
                port = 0;
                token = null;
                return false;
            }
            return true;
        }

        public static bool TryParseLockfile(string value, out int port, out string token)
        {
            port = 0;
            token = null;
            if (string.IsNullOrEmpty(value)) return false;
            string line = value.Trim();
            int end = line.IndexOfAny(new[] { '\r', '\n' });
            if (end >= 0) line = line.Substring(0, end);
            string[] parts = line.Split(':');
            if (parts.Length != 5) return false;
            if (!int.TryParse(parts[2], out port) || !ValidPort(port)) return false;
            token = parts[3];
            if (!ValidToken(token))
            {
                port = 0;
                token = null;
                return false;
            }
            return string.Equals(parts[4], "https", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ValidPort(int port)
        {
            return port > 0 && port <= 65535;
        }

        private static bool ValidToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 1024) return false;
            for (int i = 0; i < token.Length; i++)
                if (char.IsWhiteSpace(token[i]) || char.IsControl(token[i])) return false;
            return true;
        }
    }

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
            string root = NormalizeLolRoot(preferred);
            if (root != null) return root;
            root = FindLolFromProcesses();
            if (root != null) return root;
            for (int i = 0; i < LolRegistryKeys.Length; i++)
            {
                root = FindRegistryLolRoot(Registry.CurrentUser, LolRegistryKeys[i]);
                if (root != null) return root;
                root = FindRegistryLolRoot(Registry.LocalMachine, LolRegistryKeys[i]);
                if (root != null) return root;
            }
            return FindCommonLolRoot();
        }

        public static string FindWeGameRoot(string preferred, string lolRoot)
        {
            string root = NormalizeWeGameRoot(preferred);
            if (root != null) return root;
            root = FindWeGameFromProcesses();
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
            return root ?? FindCommonWeGameRoot();
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
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return null; }
            foreach (Process process in all)
            {
                try
                {
                    string name = process.ProcessName;
                    if (!string.Equals(name, "LeagueClient", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "LeagueClientUx", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "League of Legends", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string root = NormalizeLolRoot(LolRuntimeProcesses.GetImagePath(process));
                    if (root != null) return root;
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
            return null;
        }

        private static string FindWeGameFromProcesses()
        {
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return null; }
            foreach (Process process in all)
            {
                try
                {
                    string name = process.ProcessName;
                    if (!string.Equals(name, "wegame", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "wegame_env", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "wegameclient", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string path = LolRuntimeProcesses.GetImagePath(process);
                    string root = NormalizeWeGameRoot(path);
                    if (root != null) return root;
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
            return null;
        }

        private static string FindCommonLolRoot()
        {
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
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
                        string root = NormalizeLolRoot(candidates[i]);
                        if (root != null) return root;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FindCommonWeGameRoot()
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
                        return current.FullName.TrimEnd('\\');
            }
            catch { }
            return null;
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

    internal static class LolRuntimeProcesses
    {
        private const int ProcessTerminate = 0x0001;
        private const int Synchronize = 0x00100000;
        private const uint WaitObject0 = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        private static readonly string[] WeGameNames =
        {
            "wegame.exe", "wegame_env.exe", "wegameclient.exe", "pallas.exe",
            "rail.exe", "tcls_core.exe", "browser.exe", "crashpad_handler.exe"
        };

        private static readonly string[] DownloaderNames =
        {
            "teniodl.exe", "wegameupdate.exe"
        };

        private static readonly string[] TencentSessionNames =
        {
            "wegame_env.exe", "pallas.exe", "rail.exe", "tcls_core.exe"
        };

        public static LolProcessSnapshot Scan(string lolRoot, string weGameRoot)
        {
            var result = new LolProcessSnapshot();
            if (!LolInstallDiscovery.IsValidLolRoot(lolRoot)) lolRoot = null;
            if (!LolInstallDiscovery.IsValidWeGameRoot(weGameRoot)) weGameRoot = null;
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return result; }
            foreach (Process process in all)
            {
                try
                {
                    string path = GetImagePath(process);
                    if (string.IsNullOrEmpty(path)) continue;
                    string file = System.IO.Path.GetFileName(path);
                    if (IsLeagueClientProcess(path, file, lolRoot)) result.ClientRunning = true;
                    if (IsGameProcess(path, file, lolRoot)) result.GameRunning = true;
                    if (IsUxProcess(path, file, lolRoot)) result.UxProcessCount++;
                    if (IsWeGameProcess(path, file, weGameRoot)) result.WeGameProcessCount++;
                    if (IsUnder(path, CombineSafe(lolRoot, "Cross"))) result.CrossProcessCount++;
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
            return result;
        }

        public static LolCleanupResult Clean(string lolRoot, string weGameRoot)
        {
            return Clean(lolRoot, weGameRoot, false);
        }

        public static LolCleanupResult Clean(
            string lolRoot, string weGameRoot, bool includeDownloaders)
        {
            var result = new LolCleanupResult();
            if (!LolInstallDiscovery.IsValidLolRoot(lolRoot)) lolRoot = null;
            if (!LolInstallDiscovery.IsValidWeGameRoot(weGameRoot)) weGameRoot = null;
            if (lolRoot == null && weGameRoot == null) return result;
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return result; }
            int currentPid;
            using (Process current = Process.GetCurrentProcess()) currentPid = current.Id;

            var targets = new List<int>();
            var workingSets = new List<long>();
            foreach (Process process in all)
            {
                IntPtr probe = IntPtr.Zero;
                try
                {
                    if (process.Id == currentPid) continue;
                    probe = Native.OpenProcess(
                        Native.PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
                    if (probe == IntPtr.Zero) continue;
                    string path = Native.ImagePath(probe);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!IsCleanupTarget(path, System.IO.Path.GetFileName(path),
                        lolRoot, weGameRoot, includeDownloaders)) continue;
                    long workingSet = 0;
                    try { workingSet = process.WorkingSet64; } catch { }
                    targets.Add(process.Id);
                    workingSets.Add(workingSet);
                }
                catch { }
                finally
                {
                    if (probe != IntPtr.Zero) Native.CloseHandle(probe);
                    try { process.Dispose(); } catch { }
                }
            }
            if (targets.Count == 0) return result;

            for (int i = 0; i < targets.Count; i++)
            {
                IntPtr handle = IntPtr.Zero;
                try
                {
                    handle = Native.OpenProcess(
                        Native.PROCESS_QUERY_LIMITED_INFORMATION | ProcessTerminate | Synchronize,
                        false,
                        targets[i]);
                    if (handle == IntPtr.Zero) continue;
                    string path = Native.ImagePath(handle);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!IsCleanupTarget(path, System.IO.Path.GetFileName(path),
                        lolRoot, weGameRoot, includeDownloaders)) continue;
                    if (!TerminateProcess(handle, 0)) continue;
                    if (WaitForSingleObject(handle, 1500) != WaitObject0) continue;
                    result.Count++;
                    if (workingSets[i] > 0) result.WorkingSetBytes += workingSets[i];
                }
                catch { }
                finally
                {
                    if (handle != IntPtr.Zero) Native.CloseHandle(handle);
                }
            }
            return result;
        }

        public static bool IsGameRunning(string lolRoot)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName("League of Legends"); }
            catch { return false; }
            bool found = false;
            foreach (Process process in processes)
            {
                try
                {
                    string path = GetImagePath(process);
                    if (IsGameProcess(path, System.IO.Path.GetFileName(path), lolRoot))
                        found = true;
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
            return found;
        }

        public static bool IsWeGameRunning(string weGameRoot)
        {
            if (!LolInstallDiscovery.IsValidWeGameRoot(weGameRoot)) return false;
            for (int i = 0; i < WeGameNames.Length; i++)
            {
                string bare = WeGameNames[i];
                if (bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    bare = bare.Substring(0, bare.Length - 4);
                Process[] processes;
                try { processes = Process.GetProcessesByName(bare); }
                catch { continue; }
                foreach (Process process in processes)
                {
                    try
                    {
                        string path = GetImagePath(process);
                        if (IsWeGameProcess(path, System.IO.Path.GetFileName(path), weGameRoot))
                            return true;
                    }
                    catch { }
                    finally { try { process.Dispose(); } catch { } }
                }
            }
            return false;
        }

        public static bool IsClientRunning(string lolRoot)
        {
            if (string.IsNullOrEmpty(lolRoot)) return false;
            Process[] processes;
            try { processes = Process.GetProcessesByName("LeagueClient"); }
            catch { return false; }
            bool found = false;
            foreach (Process process in processes)
            {
                try
                {
                    string path = GetImagePath(process);
                    if (IsLeagueClientProcess(path, System.IO.Path.GetFileName(path), lolRoot))
                        found = true;
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
            return found;
        }

        public static bool IsUxRunning(string lolRoot)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName("LeagueClientUx"); }
            catch { return false; }
            bool found = false;
            foreach (Process process in processes)
            {
                try
                {
                    string path = GetImagePath(process);
                    if (IsUxProcess(path, System.IO.Path.GetFileName(path), lolRoot))
                        found = true;
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
            return found;
        }

        public static string GetImagePath(Process process)
        {
            if (process == null) return null;
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = Native.OpenProcess(
                    Native.PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
                return handle == IntPtr.Zero ? null : Native.ImagePath(handle);
            }
            catch { return null; }
            finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
        }

        public static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            try
            {
                string fullPath = System.IO.Path.GetFullPath(path);
                string fullRoot = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        internal static bool IsCleanupTarget(
            string path, string file, string lolRoot, string weGameRoot)
        {
            return IsCleanupTarget(path, file, lolRoot, weGameRoot, false);
        }

        internal static bool IsCleanupTarget(
            string path, string file, string lolRoot, string weGameRoot, bool includeDownloaders)
        {
            if (IsWeGameProcess(path, file, weGameRoot)) return true;
            if (includeDownloaders && IsDownloaderProcess(path, file, lolRoot, weGameRoot))
                return true;
            if (string.IsNullOrEmpty(lolRoot)) return false;
            string[] roots =
            {
                System.IO.Path.Combine(lolRoot, "Cross"),
                System.IO.Path.Combine(lolRoot, "LeagueClient", "FeedBack"),
                System.IO.Path.Combine(lolRoot, "LeagueClient", "NetworkAssist"),
                System.IO.Path.Combine(lolRoot, "LeagueClient", "TQM"),
                System.IO.Path.Combine(lolRoot, "LeagueClient", "DiagnosticAssistant"),
                System.IO.Path.Combine(lolRoot, "Launcher", "qbblinktrial")
            };
            for (int i = 0; i < roots.Length; i++)
                if (IsUnder(path, roots[i])) return true;
            if (IsUnder(path, System.IO.Path.Combine(lolRoot, "TCLS")))
            {
                for (int i = 0; i < TencentSessionNames.Length; i++)
                    if (string.Equals(
                        file, TencentSessionNames[i], StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }

        internal static bool IsDownloaderProcess(
            string path, string file, string lolRoot, string weGameRoot)
        {
            if (!string.IsNullOrEmpty(lolRoot)
                && IsUnder(path, System.IO.Path.Combine(lolRoot, "WeGameLauncher", "TenioDL")))
                return true;
            if (string.IsNullOrEmpty(weGameRoot) || !IsUnder(path, weGameRoot)) return false;
            for (int i = 0; i < DownloaderNames.Length; i++)
                if (string.Equals(file, DownloaderNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool IsLeagueClientProcess(string path, string file, string lolRoot)
        {
            if (string.IsNullOrEmpty(lolRoot) || !IsUnder(path, lolRoot)) return false;
            return string.Equals(file, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("LeagueClientUx", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGameProcess(string path, string file, string lolRoot)
        {
            return !string.IsNullOrEmpty(lolRoot)
                && string.Equals(file, "League of Legends.exe", StringComparison.OrdinalIgnoreCase)
                && IsUnder(path, System.IO.Path.Combine(lolRoot, "Game"));
        }

        private static bool IsUxProcess(string path, string file, string lolRoot)
        {
            return !string.IsNullOrEmpty(lolRoot)
                && file.StartsWith("LeagueClientUx", StringComparison.OrdinalIgnoreCase)
                && IsUnder(path, lolRoot);
        }

        private static bool IsWeGameProcess(string path, string file, string weGameRoot)
        {
            if (string.IsNullOrEmpty(weGameRoot) || !IsUnder(path, weGameRoot)) return false;
            for (int i = 0; i < WeGameNames.Length; i++)
                if (string.Equals(file, WeGameNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static string CombineSafe(string root, string child)
        {
            if (string.IsNullOrEmpty(root)) return null;
            try { return System.IO.Path.Combine(root, child); }
            catch { return null; }
        }
    }

    internal static class LolLcuCredentialSource
    {
        public static LolLcuCredentials Find(string lolRoot)
        {
            if (!LolInstallDiscovery.IsValidLolRoot(lolRoot)) return null;
            LolLcuCredentials credentials = FindFromProcesses(lolRoot);
            if (credentials != null) return credentials;
            credentials = FindFromLockfile(lolRoot);
            return credentials ?? FindFromLogs(lolRoot);
        }

        private static LolLcuCredentials FindFromProcesses(string lolRoot)
        {
            const string query =
                "SELECT ProcessId, ExecutablePath, CommandLine, Name FROM Win32_Process "
                + "WHERE Name='LeagueClient.exe' OR Name='LeagueClientUx.exe'";
            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                    using (ManagementObjectCollection rows = searcher.Get())
                    {
                        var ordered = new List<ManagementObject>();
                        foreach (ManagementObject row in rows) ordered.Add(row);
                        ordered.Sort(delegate(ManagementObject left, ManagementObject right)
                        {
                            string a = Convert.ToString(left["Name"]);
                            string b = Convert.ToString(right["Name"]);
                            bool ac = string.Equals(a, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase);
                            bool bc = string.Equals(b, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase);
                            return ac == bc ? 0 : (ac ? -1 : 1);
                        });
                        for (int i = 0; i < ordered.Count; i++)
                        {
                            using (ManagementObject row = ordered[i])
                            {
                                string executablePath = Convert.ToString(row["ExecutablePath"]);
                                if (string.IsNullOrEmpty(executablePath))
                                {
                                    int pid;
                                    if (int.TryParse(Convert.ToString(row["ProcessId"]), out pid))
                                        executablePath = ImagePath(pid);
                                }
                                if (!LolRuntimeProcesses.IsUnder(executablePath, lolRoot)) continue;
                                int port;
                                string token;
                                if (LolCredentialParser.TryParseCommandLine(
                                    Convert.ToString(row["CommandLine"]), out port, out token))
                                    return new LolLcuCredentials(port, token);
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static LolLcuCredentials FindFromLockfile(string lolRoot)
        {
            string[] files =
            {
                Path.Combine(lolRoot, "lockfile"),
                Path.Combine(lolRoot, "LeagueClient", "lockfile"),
                Path.Combine(lolRoot, "Riot Client", "lockfile")
            };
            for (int i = 0; i < files.Length; i++)
            {
                string content;
                if (!TryReadTail(files[i], 64 * 1024, out content)) continue;
                int port;
                string token;
                if (LolCredentialParser.TryParseLockfile(content, out port, out token))
                    return new LolLcuCredentials(port, token);
            }
            return null;
        }

        private static LolLcuCredentials FindFromLogs(string lolRoot)
        {
            var logs = new List<FileInfo>();
            AddLogs(logs, Path.Combine(lolRoot, "LeagueClient"));
            AddLogs(logs, Path.Combine(lolRoot, "LeagueClient", "Logs"));
            logs.Sort(delegate(FileInfo a, FileInfo b)
            {
                return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
            });
            int limit = Math.Min(logs.Count, 16);
            for (int i = 0; i < limit; i++)
            {
                string content;
                if (!TryReadTail(logs[i].FullName, 2 * 1024 * 1024, out content)) continue;
                int port;
                string token;
                if (LolCredentialParser.TryParseCommandLine(content, out port, out token))
                    return new LolLcuCredentials(port, token);
            }
            return null;
        }

        private static void AddLogs(List<FileInfo> output, string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                string[] files = Directory.GetFiles(directory, "*.log", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    string name = System.IO.Path.GetFileName(files[i]);
                    if (name.IndexOf("LeagueClient", StringComparison.OrdinalIgnoreCase) >= 0)
                        output.Add(new FileInfo(files[i]));
                }
            }
            catch { }
        }

        private static string ImagePath(int pid)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                return handle == IntPtr.Zero ? null : Native.ImagePath(handle);
            }
            catch { return null; }
            finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
        }

        private static bool TryReadTail(string path, int maximumBytes, out string content)
        {
            content = null;
            try
            {
                if (!File.Exists(path)) return false;
                using (var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, stream.Length - maximumBytes);
                    stream.Seek(start, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        if (start > 0) reader.ReadLine();
                        content = reader.ReadToEnd();
                    }
                }
                return true;
            }
            catch { return false; }
        }
    }

    internal sealed class LolHttpResult
    {
        public bool Reached;
        public int Status;
        public string Body;

        public bool Success
        {
            get { return Reached && Status >= 200 && Status < 300; }
        }
    }

    internal static class LolLcuClient
    {
        private static readonly Regex LoginSucceededPattern = new Regex(
            @"\""state\""\s*:\s*\""SUCCEEDED\""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsReady(LolLcuCredentials credentials)
        {
            LolHttpResult login = Send(credentials, "GET", "/lol-login/v1/session");
            if (!login.Success || !LoginSucceededPattern.IsMatch(login.Body ?? "")) return false;
            LolHttpResult summoner = Send(credentials, "GET", "/lol-summoner/v1/current-summoner");
            return summoner.Success;
        }

        public static string GetGameflowPhase(LolLcuCredentials credentials)
        {
            LolHttpResult result = Send(credentials, "GET", "/lol-gameflow/v1/gameflow-phase");
            if (!result.Success || string.IsNullOrEmpty(result.Body)) return null;
            string phase = result.Body.Trim();
            if (phase.Length >= 2 && phase[0] == '"' && phase[phase.Length - 1] == '"')
                phase = phase.Substring(1, phase.Length - 2);
            return phase.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        public static bool KillUx(LolLcuCredentials credentials, string lolRoot)
        {
            if (!Send(credentials, "POST", "/riotclient/kill-ux").Success) return false;
            return WaitForUxExit(lolRoot, 8000);
        }

        public static bool RestoreUx(LolLcuCredentials credentials, string lolRoot)
        {
            if (LolRuntimeProcesses.IsUxRunning(lolRoot))
            {
                LolHttpResult showExisting = Send(credentials, "POST", "/riotclient/ux-show");
                if (showExisting.Success && WaitForUx(lolRoot, 3000)) return true;
            }
            LolHttpResult launch = Send(credentials, "POST", "/riotclient/launch-ux");
            if (launch.Success)
            {
                WaitForUx(lolRoot, 6000);
                LolHttpResult show = Send(credentials, "POST", "/riotclient/ux-show");
                if (show.Success && WaitForUx(lolRoot, 3000)) return true;
            }
            LolHttpResult restart = Send(
                credentials, "POST", "/riotclient/kill-and-restart-ux");
            if (!restart.Success) return false;
            WaitForUx(lolRoot, 10000);
            Send(credentials, "POST", "/riotclient/ux-show");
            return WaitForUx(lolRoot, 3000);
        }

        private static bool WaitForUx(string lolRoot, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (LolRuntimeProcesses.IsUxRunning(lolRoot)) return true;
                Thread.Sleep(250);
                waited += 250;
            }
            return LolRuntimeProcesses.IsUxRunning(lolRoot);
        }

        private static bool WaitForUxExit(string lolRoot, int timeoutMs)
        {
            int waited = 0;
            int absent = 0;
            while (waited < timeoutMs)
            {
                if (LolRuntimeProcesses.IsUxRunning(lolRoot))
                    absent = 0;
                else if (++absent >= 2)
                    return true;
                Thread.Sleep(250);
                waited += 250;
            }
            return !LolRuntimeProcesses.IsUxRunning(lolRoot);
        }

        private static LolHttpResult Send(
            LolLcuCredentials credentials, string method, string relativePath)
        {
            var result = new LolHttpResult();
            if (credentials == null || credentials.Port <= 0 || credentials.Port > 65535)
                return result;
            Uri uri;
            if (!Uri.TryCreate(
                "https://127.0.0.1:" + credentials.Port + relativePath,
                UriKind.Absolute, out uri) || !uri.IsLoopback)
                return result;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = method;
                request.Proxy = null;
                request.AllowAutoRedirect = false;
                request.KeepAlive = false;
                request.Timeout = 3000;
                request.ReadWriteTimeout = 3000;
                request.Accept = "application/json";
                request.UserAgent = "Aegis-LolRuntime";
                request.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes("riot:" + credentials.Token));
                request.ServerCertificateValidationCallback = ValidateLoopbackCertificate;
                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    request.ContentType = "application/json";
                    request.ContentLength = 0;
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    result.Reached = true;
                    result.Status = (int)response.StatusCode;
                    result.Body = ReadBody(response);
                }
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                    {
                        result.Reached = true;
                        result.Status = (int)response.StatusCode;
                        result.Body = ReadBody(response);
                    }
                }
            }
            catch { }
            return result;
        }

        private static bool ValidateLoopbackCertificate(
            object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            var request = sender as HttpWebRequest;
            return request != null && request.RequestUri != null && request.RequestUri.IsLoopback;
        }

        private static string ReadBody(HttpWebResponse response)
        {
            try
            {
                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null) return "";
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch { return ""; }
        }
    }
}
