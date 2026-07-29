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
        public bool ScanSucceeded;
        public bool CoreIdentityIndeterminate;
        public bool ClientRunning;
        public bool GameRunning;
        public int GameProcessId;
        public int WeGameProcessCount;
        public int CrossProcessCount;
        public int UxProcessCount;
        public int MainUxProcessCount;
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
            int pid;
            return TryParseLockfile(
                value, out pid, out port, out token);
        }

        internal static bool TryParseLockfile(
            string value, out int pid, out int port, out string token)
        {
            pid = 0;
            port = 0;
            token = null;
            if (string.IsNullOrEmpty(value)) return false;
            string line = value.Trim();
            int end = line.IndexOfAny(new[] { '\r', '\n' });
            if (end >= 0) line = line.Substring(0, end);
            string[] parts = line.Split(':');
            if (parts.Length != 5) return false;
            if (!int.TryParse(parts[1], out pid) || pid <= 0)
                return false;
            if (!int.TryParse(parts[2], out port) || !ValidPort(port)) return false;
            token = parts[3];
            if (!ValidToken(token))
            {
                port = 0;
                pid = 0;
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
            return FindCommonLolRoot();
        }

        public static string FindWeGameRoot(string preferred, string lolRoot)
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
            int currentSession;
            if (!LolRuntimeProcesses.TryGetCurrentSessionId(
                    out currentSession))
                return null;
            string[] names =
            {
                "LeagueClient",
                "LeagueClientUx",
                "League of Legends"
            };
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
                        if (!LolRuntimeProcesses
                            .IsCoreIdentityCandidateName(
                                Path.GetFileName(path)))
                            continue;
                        string root = NormalizeLolRoot(path);
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

        private static string FindWeGameFromProcesses()
        {
            int currentSession;
            if (!LolRuntimeProcesses.TryGetCurrentSessionId(
                    out currentSession))
                return null;
            string[] names =
            {
                "wegame",
                "wegame_env",
                "wegameclient"
            };
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
                        if (!LolRuntimeProcesses
                            .IsWeGameDiscoveryCandidateName(
                                Path.GetFileName(path)))
                            continue;
                        string root = NormalizeWeGameRoot(path);
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

        private static readonly string[] ScanCandidateNames =
        {
            "leagueclient", "leagueclientux", "leagueclientuxrender", "league of legends",
            "wegame", "wegame_env", "wegameclient", "pallas", "rail", "tcls_core",
            "browser", "crashpad_handler", "teniodl", "wegameupdate",
            "crossproxy", "lolaicoach", "aicoachapp", "icreatelol", "tqmcenter", "yxqxunyou"
        };

        private static readonly string[] CleanupCandidateNames =
        {
            "wegame", "wegame_env", "wegameclient", "pallas", "rail",
            "tcls_core", "browser", "crashpad_handler", "teniodl",
            "wegameupdate", "crossproxy", "lolaicoach", "aicoachapp",
            "icreatelol", "tqmcenter", "yxqxunyou"
        };

        private enum SessionRelation
        {
            Unknown,
            Foreign,
            Owned
        }

        internal static bool IsScanCandidateName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < ScanCandidateNames.Length; i++)
                if (string.Equals(name, ScanCandidateNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        internal static bool IsCoreIdentityCandidateName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string bare = name.EndsWith(
                ".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4) : name;
            return string.Equals(
                    bare, "LeagueClient", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    bare, "LeagueClientUx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    bare, "League of Legends",
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsCredentialSourceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string bare = name.EndsWith(
                ".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4) : name;
            return string.Equals(
                    bare, "LeagueClient", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    bare, "LeagueClientUx", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsWeGameDiscoveryCandidateName(
            string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string bare = name.EndsWith(
                ".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4) : name;
            return string.Equals(
                    bare, "wegame", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    bare, "wegame_env",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    bare, "wegameclient",
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsCleanupCandidateName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string bare = name.EndsWith(
                ".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4) : name;
            for (int i = 0; i < CleanupCandidateNames.Length; i++)
                if (string.Equals(
                    bare,
                    CleanupCandidateNames[i],
                    StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        internal static bool IsExpectedSession(
            int expectedSession, int candidateSession)
        {
            return expectedSession >= 0
                && candidateSession >= 0
                && expectedSession == candidateSession;
        }

        internal static bool TryGetCurrentSessionId(out int sessionId)
        {
            sessionId = -1;
            try
            {
                using (Process current = Process.GetCurrentProcess())
                    sessionId = current.SessionId;
                return sessionId >= 0;
            }
            catch { return false; }
        }

        private static bool TryGetVerifiedImagePath(
            Process process,
            int expectedSession,
            out string path,
            out SessionRelation sessionRelation)
        {
            path = null;
            sessionRelation = SessionRelation.Unknown;
            if (process == null || expectedSession < 0) return false;
            IntPtr handle = IntPtr.Zero;
            try
            {
                int pid = process.Id;
                handle = Native.OpenProcess(
                    Native.PROCESS_QUERY_LIMITED_INFORMATION | Synchronize,
                    false, pid);
                if (handle == IntPtr.Zero)
                {
                    int fallbackSession;
                    try { fallbackSession = process.SessionId; }
                    catch { fallbackSession = -1; }
                    if (fallbackSession >= 0)
                        sessionRelation = IsExpectedSession(
                            expectedSession, fallbackSession)
                            ? SessionRelation.Owned
                            : SessionRelation.Foreign;
                    return false;
                }
                int candidateSession;
                if (!Native.TryGetLiveProcessSessionId(
                        handle, pid, out candidateSession))
                    return false;
                sessionRelation = IsExpectedSession(
                    expectedSession, candidateSession)
                    ? SessionRelation.Owned
                    : SessionRelation.Foreign;
                if (sessionRelation != SessionRelation.Owned)
                    return false;
                path = Native.ImagePath(handle);
                return !string.IsNullOrEmpty(path);
            }
            catch { return false; }
            finally
            {
                if (handle != IntPtr.Zero) Native.CloseHandle(handle);
            }
        }

        internal static bool TryGetOwnedImagePath(
            Process process, int expectedSession, out string path)
        {
            SessionRelation relation;
            return TryGetVerifiedImagePath(
                process, expectedSession, out path, out relation);
        }

        internal static bool IsOwnedCredentialSourceProcess(
            string lolRoot, int pid, int expectedSession)
        {
            return IsOwnedCredentialSourceProcess(
                lolRoot, pid, expectedSession, 0);
        }

        internal static bool IsOwnedCredentialSourceProcess(
            string lolRoot, int pid, int expectedSession,
            long expectedCreation)
        {
            if (pid <= 0 || expectedSession < 0
                || !LolInstallDiscovery.IsValidLolRoot(lolRoot))
                return false;
            IntPtr handle = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION | Synchronize,
                false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                int liveSession;
                if (!Native.TryGetLiveProcessSessionId(
                        handle, pid, out liveSession)
                    || !IsExpectedSession(
                        expectedSession, liveSession))
                    return false;
                if (expectedCreation > 0)
                {
                    long actualCreation;
                    long cpu;
                    ulong io;
                    if (!Native.QueryProcessSample(
                            handle, out actualCreation,
                            out cpu, out io)
                        || !CredentialCreationMatches(
                            expectedCreation, actualCreation))
                        return false;
                }
                string path = Native.ImagePath(handle);
                return IsUnder(path, lolRoot)
                    && IsCredentialSourceName(
                        System.IO.Path.GetFileName(path));
            }
            finally { Native.CloseHandle(handle); }
        }

        internal static bool CredentialCreationMatches(
            long expectedCreation, long actualCreation)
        {
            if (expectedCreation <= 0 || actualCreation <= 0)
                return false;
            long delta = expectedCreation >= actualCreation
                ? expectedCreation - actualCreation
                : actualCreation - expectedCreation;
            // WMI CreationDate is serialized at microsecond precision while
            // GetProcessTimes uses 100 ns FILETIME ticks.
            return delta <= TimeSpan.TicksPerMillisecond;
        }

        internal static bool HasExclusiveCredentialSourceSession(
            string lolRoot)
        {
            if (!LolInstallDiscovery.IsValidLolRoot(lolRoot))
                return false;
            int currentSession;
            if (!TryGetCurrentSessionId(out currentSession))
                return false;
            bool owned = false;
            string[] names = { "LeagueClient", "LeagueClientUx" };
            for (int i = 0; i < names.Length; i++)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(names[i]); }
                catch { return false; }
                try
                {
                    foreach (Process process in processes)
                    {
                        string path;
                        SessionRelation relation;
                        if (!TryGetVerifiedImagePath(
                                process, currentSession,
                                out path, out relation))
                            return false;
                        if (relation != SessionRelation.Owned)
                            return false;
                        if (IsUnder(path, lolRoot)
                            && IsCredentialSourceName(
                                System.IO.Path.GetFileName(path)))
                            owned = true;
                    }
                }
                finally { DisposeProcesses(processes); }
            }
            return owned;
        }

        internal static bool IsRelevantProcessChange(
            string name, string path, string lolRoot, string weGameRoot)
        {
            if (!IsScanCandidateName(name)) return false;
            if (!string.IsNullOrEmpty(path))
                return IsUnder(path, lolRoot) || IsUnder(path, weGameRoot);
            return !string.Equals(
                    name, "browser", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    name, "crashpad_handler", StringComparison.OrdinalIgnoreCase);
        }

        public static LolProcessSnapshot Scan(string lolRoot, string weGameRoot)
        {
            return Scan(lolRoot, weGameRoot, false);
        }

        public static LolProcessSnapshot Scan(string lolRoot, string weGameRoot, bool namesOnly)
        {
            var result = new LolProcessSnapshot();
            if (!LolInstallDiscovery.IsValidLolRoot(lolRoot)) lolRoot = null;
            if (!LolInstallDiscovery.IsValidWeGameRoot(weGameRoot)) weGameRoot = null;
            int currentSession;
            if (!TryGetCurrentSessionId(out currentSession)) return result;
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return result; }
            result.ScanSucceeded = true;
            foreach (Process process in all)
            {
                string processName = null;
                try
                {
                    processName = process.ProcessName;
                    if (namesOnly && !IsScanCandidateName(processName)) continue;
                    string path;
                    SessionRelation sessionRelation;
                    if (!TryGetVerifiedImagePath(
                            process, currentSession,
                            out path, out sessionRelation))
                    {
                        if (sessionRelation != SessionRelation.Foreign
                            && IsCoreIdentityCandidateName(processName))
                            result.CoreIdentityIndeterminate = true;
                        continue;
                    }
                    if (string.IsNullOrEmpty(path))
                    {
                        if (IsCoreIdentityCandidateName(processName))
                            result.CoreIdentityIndeterminate = true;
                        continue;
                    }
                    string file = System.IO.Path.GetFileName(path);
                    if (IsLeagueClientProcess(path, file, lolRoot)) result.ClientRunning = true;
                    if (IsGameProcess(path, file, lolRoot))
                    {
                        result.GameRunning = true;
                        result.GameProcessId = process.Id;
                    }
                    if (IsUxProcess(path, file, lolRoot)) result.UxProcessCount++;
                    if (IsMainUxProcess(path, file, lolRoot)) result.MainUxProcessCount++;
                    if (IsWeGameProcess(path, file, weGameRoot)) result.WeGameProcessCount++;
                    if (IsUnder(path, CombineSafe(lolRoot, "Cross"))) result.CrossProcessCount++;
                }
                catch
                {
                    if (IsCoreIdentityCandidateName(processName))
                        result.CoreIdentityIndeterminate = true;
                }
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
            int currentSession;
            if (!TryGetCurrentSessionId(out currentSession)) return result;
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return result; }
            int currentPid;
            using (Process current = Process.GetCurrentProcess()) currentPid = current.Id;

            var targets = new List<int>();
            var workingSets = new List<long>();
            var creations = new List<long>();
            var paths = new List<string>();
            foreach (Process process in all)
            {
                IntPtr probe = IntPtr.Zero;
                try
                {
                    if (process.Id == currentPid) continue;
                    string candidateName;
                    try { candidateName = process.ProcessName; }
                    catch { continue; }
                    if (!IsCleanupCandidateName(candidateName)) continue;
                    probe = Native.OpenProcess(
                        Native.PROCESS_QUERY_LIMITED_INFORMATION | Synchronize,
                        false, process.Id);
                    if (probe == IntPtr.Zero) continue;
                    int candidateSession;
                    if (!Native.TryGetLiveProcessSessionId(
                            probe, process.Id, out candidateSession)
                        || !IsExpectedSession(
                            currentSession, candidateSession))
                        continue;
                    string path = Native.ImagePath(probe);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!IsCleanupTarget(path, System.IO.Path.GetFileName(path),
                        lolRoot, weGameRoot, includeDownloaders)) continue;
                    long creation;
                    long cpu;
                    ulong io;
                    if (!Native.QueryProcessSample(
                            probe, out creation, out cpu, out io)
                        || creation <= 0)
                        continue;
                    long workingSet = 0;
                    try { workingSet = process.WorkingSet64; } catch { }
                    targets.Add(process.Id);
                    workingSets.Add(workingSet);
                    creations.Add(creation);
                    paths.Add(path);
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
                    int candidateSession;
                    if (!Native.TryGetLiveProcessSessionId(
                            handle, targets[i], out candidateSession)
                        || !IsExpectedSession(
                            currentSession, candidateSession))
                        continue;
                    string path = Native.ImagePath(handle);
                    if (string.IsNullOrEmpty(path)) continue;
                    long creation;
                    long cpu;
                    ulong io;
                    if (!Native.QueryProcessSample(
                            handle, out creation, out cpu, out io)
                        || creation != creations[i]
                        || !string.Equals(
                            path, paths[i],
                            StringComparison.OrdinalIgnoreCase))
                        continue;
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
            int currentSession;
            if (!TryGetCurrentSessionId(out currentSession)) return false;
            Process[] processes;
            try { processes = Process.GetProcessesByName("League of Legends"); }
            catch { return false; }
            bool found = false;
            foreach (Process process in processes)
            {
                try
                {
                    string path;
                    SessionRelation sessionRelation;
                    if (!TryGetVerifiedImagePath(
                            process, currentSession,
                            out path, out sessionRelation))
                        continue;
                    if (IsGameProcess(path, System.IO.Path.GetFileName(path), lolRoot))
                        found = true;
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
            return found;
        }

        internal static bool TryGetGameIdentity(
            string lolRoot, int preferredPid, out int pid, out long creation)
        {
            pid = 0;
            creation = 0;
            if (!LolInstallDiscovery.IsValidLolRoot(lolRoot)) return false;
            int currentSession;
            if (!TryGetCurrentSessionId(out currentSession)) return false;
            if (preferredPid > 0
                && TryGetGameIdentityCore(
                    lolRoot, preferredPid, currentSession, out creation))
            {
                pid = preferredPid;
                return true;
            }
            Process[] processes;
            try { processes = Process.GetProcessesByName("League of Legends"); }
            catch { return false; }
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        long candidateCreation;
                        if (!TryGetGameIdentityCore(
                            lolRoot, process.Id, currentSession,
                            out candidateCreation)) continue;
                        pid = process.Id;
                        creation = candidateCreation;
                        return true;
                    }
                    catch { }
                }
            }
            finally { DisposeProcesses(processes); }
            return false;
        }

        private static bool TryGetGameIdentityCore(
            string lolRoot, int pid, int expectedSession,
            out long creation)
        {
            creation = 0;
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = Native.OpenProcess(
                    Native.PROCESS_QUERY_LIMITED_INFORMATION | Synchronize,
                    false, pid);
                if (handle == IntPtr.Zero) return false;
                int candidateSession;
                if (!Native.TryGetLiveProcessSessionId(
                        handle, pid, out candidateSession)
                    || !IsExpectedSession(
                        expectedSession, candidateSession))
                    return false;
                string path = Native.ImagePath(handle);
                if (!IsGameProcess(path, System.IO.Path.GetFileName(path), lolRoot))
                    return false;
                long cpu;
                ulong io;
                return Native.QueryProcessSample(handle, out creation, out cpu, out io)
                    && creation != 0;
            }
            catch { return false; }
            finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
        }

        public static bool IsWeGameRunning(string weGameRoot)
        {
            if (!LolInstallDiscovery.IsValidWeGameRoot(weGameRoot)) return false;
            int currentSession;
            if (!TryGetCurrentSessionId(out currentSession)) return false;
            for (int i = 0; i < WeGameNames.Length; i++)
            {
                string bare = WeGameNames[i];
                if (bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    bare = bare.Substring(0, bare.Length - 4);
                Process[] processes;
                try { processes = Process.GetProcessesByName(bare); }
                catch { continue; }
                bool found = false;
                try
                {
                    foreach (Process process in processes)
                    {
                        try
                        {
                            string path;
                            SessionRelation sessionRelation;
                            if (!TryGetVerifiedImagePath(
                                    process, currentSession,
                                    out path, out sessionRelation))
                                continue;
                            if (IsWeGameProcess(path, System.IO.Path.GetFileName(path), weGameRoot))
                            {
                                found = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                finally { DisposeProcesses(processes); }
                if (found) return true;
            }
            return false;
        }

        public static bool IsClientRunning(string lolRoot)
        {
            if (string.IsNullOrEmpty(lolRoot)) return false;
            int currentSession;
            if (!TryGetCurrentSessionId(out currentSession)) return false;
            Process[] processes;
            try { processes = Process.GetProcessesByName("LeagueClient"); }
            catch { return false; }
            bool found = false;
            foreach (Process process in processes)
            {
                try
                {
                    string path;
                    SessionRelation sessionRelation;
                    if (!TryGetVerifiedImagePath(
                            process, currentSession,
                            out path, out sessionRelation))
                        continue;
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
            int currentSession;
            if (!TryGetCurrentSessionId(out currentSession)) return false;
            Process[] processes;
            try { processes = Process.GetProcessesByName("LeagueClientUx"); }
            catch { return false; }
            bool found = false;
            foreach (Process process in processes)
            {
                try
                {
                    string path;
                    SessionRelation sessionRelation;
                    if (!TryGetVerifiedImagePath(
                            process, currentSession,
                            out path, out sessionRelation))
                        continue;
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

        private static void DisposeProcesses(Process[] processes)
        {
            if (processes == null) return;
            foreach (Process process in processes)
                if (process != null) try { process.Dispose(); } catch { }
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

        internal static bool IsLeagueClientProcess(string path, string file, string lolRoot)
        {
            if (string.IsNullOrEmpty(lolRoot) || !IsUnder(path, lolRoot)) return false;
            // ClientRunning means the LeagueClient backend, not a surviving
            // Chromium renderer. UX processes are counted independently below.
            return string.Equals(file, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsGameProcess(string path, string file, string lolRoot)
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

        internal static bool IsMainUxProcess(string path, string file, string lolRoot)
        {
            return !string.IsNullOrEmpty(lolRoot)
                && string.Equals(file, "LeagueClientUx.exe", StringComparison.OrdinalIgnoreCase)
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
        private const int RecentLogLimit = 8;
        private const int CredentialProbeLimit = 4;
        private const int LockfileTailBytes = 4 * 1024;
        private const int LogHeadBytes = 64 * 1024;
        private const int LogTailBytes = 448 * 1024;
        private static readonly object LogCacheLock = new object();
        private static string cachedLogRoot;
        private static string[] cachedLogPaths;
        private static DateTime cachedLogPathsUntilUtc;

        internal static long CredentialGenerationStamp(string lolRoot)
        {
            if (string.IsNullOrEmpty(lolRoot)) return 0;
            unchecked
            {
                long stamp = 17;
                string[] files =
                {
                    Path.Combine(lolRoot, "lockfile"),
                    Path.Combine(lolRoot, "LeagueClient", "lockfile"),
                    Path.Combine(lolRoot, "Riot Client", "lockfile")
                };
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        var info = new FileInfo(files[i]);
                        if (!info.Exists) continue;
                        stamp = (stamp * 31) ^ info.LastWriteTimeUtc.Ticks;
                        stamp = (stamp * 31) ^ info.Length;
                    }
                    catch { }
                }
                stamp = (stamp * 31) ^ CredentialProcessStamp(
                    lolRoot, "LeagueClient");
                stamp = (stamp * 31) ^ CredentialProcessStamp(
                    lolRoot, "LeagueClientUx");
                return stamp;
            }
        }

        private static long CredentialProcessStamp(
            string lolRoot, string processName)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { return 0; }
            long newestCreation = 0;
            int newestPid = 0;
            try
            {
                foreach (Process process in processes)
                {
                    IntPtr handle = IntPtr.Zero;
                    try
                    {
                        handle = Native.OpenProcess(
                            Native.PROCESS_QUERY_LIMITED_INFORMATION,
                            false, process.Id);
                        if (handle == IntPtr.Zero) continue;
                        string path = Native.ImagePath(handle);
                        string file = Path.GetFileName(path);
                        if (!LolRuntimeProcesses.IsUnder(path, lolRoot)
                            || !string.Equals(
                                file, processName + ".exe",
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        long creation;
                        long cpu;
                        ulong io;
                        if (!Native.QueryProcessSample(
                                handle, out creation, out cpu, out io)
                            || creation <= newestCreation)
                            continue;
                        newestCreation = creation;
                        newestPid = process.Id;
                    }
                    catch { }
                    finally
                    {
                        if (handle != IntPtr.Zero) Native.CloseHandle(handle);
                    }
                }
            }
            finally
            {
                foreach (Process process in processes)
                    if (process != null) try { process.Dispose(); } catch { }
            }
            unchecked
            {
                return newestCreation ^ ((long)newestPid << 32);
            }
        }

        public static LolLcuCredentials Find(string lolRoot)
        {
            if (!LolInstallDiscovery.IsValidLolRoot(lolRoot)) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int probed;
            LolLcuCredentials credentials = SelectReachable(
                ReadProcessCandidates(lolRoot),
                LolLcuClient.IsCredentialReachable,
                Math.Min(2, CredentialProbeLimit),
                seen,
                out probed);
            if (credentials != null || probed >= CredentialProbeLimit)
                return credentials;

            int lockfileProbed;
            credentials = SelectReachable(
                ReadLockfileCandidates(lolRoot),
                LolLcuClient.IsCredentialReachable,
                CredentialProbeLimit - probed,
                seen,
                out lockfileProbed);
            probed += lockfileProbed;
            if (credentials != null || probed >= CredentialProbeLimit)
                return credentials;
            if (!LolRuntimeProcesses
                .HasExclusiveCredentialSourceSession(lolRoot))
                return null;

            int logProbed;
            return SelectReachableLogCandidate(
                lolRoot,
                LolLcuClient.IsCredentialReachable,
                CredentialProbeLimit - probed,
                seen,
                out logProbed);
        }

        private static IList<LolLcuCredentials> ReadProcessCandidates(
            string lolRoot)
        {
            var candidates = new List<LolLcuCredentials>();
            int currentSession;
            if (!LolRuntimeProcesses.TryGetCurrentSessionId(
                    out currentSession))
                return candidates;
            const string query =
                "SELECT ProcessId, SessionId, CreationDate, ExecutablePath, CommandLine, Name FROM Win32_Process "
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
                            if (ac != bc) return ac ? -1 : 1;
                            long apid;
                            long bpid;
                            long.TryParse(
                                Convert.ToString(left["ProcessId"]), out apid);
                            long.TryParse(
                                Convert.ToString(right["ProcessId"]), out bpid);
                            return bpid.CompareTo(apid);
                        });
                        try
                        {
                            for (int i = 0; i < ordered.Count; i++)
                            {
                                ManagementObject row = ordered[i];
                                int pid;
                                int sessionId;
                                long creation;
                                if (!int.TryParse(
                                        Convert.ToString(row["ProcessId"]),
                                        out pid)
                                    || !int.TryParse(
                                        Convert.ToString(row["SessionId"]),
                                        out sessionId)
                                    || !TryParseWmiCreation(
                                        row["CreationDate"], out creation)
                                    || !LolRuntimeProcesses.IsExpectedSession(
                                        currentSession, sessionId))
                                    continue;
                                if (!LolRuntimeProcesses
                                    .IsOwnedCredentialSourceProcess(
                                        lolRoot, pid, currentSession,
                                        creation))
                                    continue;
                                int port;
                                string token;
                                if (LolCredentialParser.TryParseCommandLine(
                                    Convert.ToString(row["CommandLine"]),
                                    out port, out token))
                                    candidates.Add(
                                        new LolLcuCredentials(
                                            port, token));
                            }
                        }
                        finally
                        {
                            foreach (ManagementObject row in ordered)
                                if (row != null) try { row.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch { }
            return candidates;
        }

        private static bool TryParseWmiCreation(
            object value, out long fileTimeUtc)
        {
            fileTimeUtc = 0;
            try
            {
                string text = Convert.ToString(value);
                if (string.IsNullOrWhiteSpace(text)) return false;
                DateTime created =
                    ManagementDateTimeConverter.ToDateTime(text);
                fileTimeUtc = created.ToUniversalTime().ToFileTimeUtc();
                return fileTimeUtc > 0;
            }
            catch { return false; }
        }

        private static IList<LolLcuCredentials> ReadLockfileCandidates(
            string lolRoot)
        {
            int currentSession;
            if (!LolRuntimeProcesses.TryGetCurrentSessionId(
                    out currentSession))
                return new List<LolLcuCredentials>();
            string[] files =
            {
                Path.Combine(lolRoot, "lockfile"),
                Path.Combine(lolRoot, "LeagueClient", "lockfile"),
                Path.Combine(lolRoot, "Riot Client", "lockfile")
            };
            files = OrderExistingCredentialFiles(files);
            var candidates = new List<LolLcuCredentials>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                string content;
                if (!TryReadTail(
                    files[i], LockfileTailBytes, out content)) continue;
                int pid;
                int port;
                string token;
                if (LolCredentialParser.TryParseLockfile(
                        content, out pid, out port, out token)
                    && LolRuntimeProcesses.IsOwnedCredentialSourceProcess(
                        lolRoot, pid, currentSession))
                    candidates.Add(new LolLcuCredentials(port, token));
            }
            return candidates;
        }

        private static LolLcuCredentials SelectReachableLogCandidate(
            string lolRoot,
            Func<LolLcuCredentials, bool> probe,
            int maximumProbes,
            HashSet<string> seen,
            out int probed)
        {
            probed = 0;
            if (probe == null || maximumProbes <= 0) return null;
            if (seen == null) seen = new HashSet<string>(StringComparer.Ordinal);
            string[] logs;
            lock (LogCacheLock)
            {
                if (cachedLogPaths != null
                    && DateTime.UtcNow < cachedLogPathsUntilUtc
                    && string.Equals(
                        cachedLogRoot, lolRoot, StringComparison.OrdinalIgnoreCase))
                {
                    logs = cachedLogPaths;
                }
                else
                {
                    logs = SelectRecentLogPaths(lolRoot, RecentLogLimit);
                    cachedLogRoot = lolRoot;
                    cachedLogPaths = logs;
                    cachedLogPathsUntilUtc = DateTime.UtcNow.AddSeconds(10);
                }
            }
            for (int i = 0; i < logs.Length; i++)
            {
                if (probed >= maximumProbes) break;
                string content;
                if (!TryReadHeadAndTail(
                        logs[i], LogHeadBytes, LogTailBytes, out content))
                    continue;
                int port;
                string token;
                if (!LolCredentialParser.TryParseCommandLine(
                        content, out port, out token))
                    continue;
                string key = port + "\n" + token;
                if (!seen.Add(key)) continue;
                var candidate = new LolLcuCredentials(port, token);
                probed++;
                try
                {
                    if (probe(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        internal static string[] OrderExistingCredentialFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return new string[0];
            var files = new List<FileInfo>(paths.Length);
            for (int i = 0; i < paths.Length; i++)
            {
                try
                {
                    var file = new FileInfo(paths[i]);
                    if (file.Exists) files.Add(file);
                }
                catch { }
            }
            files.Sort(delegate(FileInfo left, FileInfo right)
            {
                int byTime = right.LastWriteTimeUtc.CompareTo(
                    left.LastWriteTimeUtc);
                return byTime != 0
                    ? byTime
                    : StringComparer.OrdinalIgnoreCase.Compare(
                        left.FullName, right.FullName);
            });
            string[] result = new string[files.Count];
            for (int i = 0; i < files.Count; i++)
                result[i] = files[i].FullName;
            return result;
        }

        internal static LolLcuCredentials SelectReachable(
            IList<LolLcuCredentials> candidates,
            Func<LolLcuCredentials, bool> probe,
            int maximumProbes,
            out int probed)
        {
            return SelectReachable(
                candidates, probe, maximumProbes,
                new HashSet<string>(StringComparer.Ordinal), out probed);
        }

        internal static LolLcuCredentials SelectReachable(
            IList<LolLcuCredentials> candidates,
            Func<LolLcuCredentials, bool> probe,
            int maximumProbes,
            HashSet<string> seen,
            out int probed)
        {
            probed = 0;
            if (candidates == null || probe == null || maximumProbes <= 0)
                return null;
            if (seen == null) seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < candidates.Count; i++)
            {
                LolLcuCredentials candidate = candidates[i];
                if (candidate == null || candidate.Port <= 0
                    || string.IsNullOrEmpty(candidate.Token))
                    continue;
                if (probed >= maximumProbes) break;
                string key = candidate.Port + "\n" + candidate.Token;
                if (!seen.Add(key)) continue;
                probed++;
                try
                {
                    if (probe(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        internal static string[] SelectRecentLogPaths(string lolRoot, int limit)
        {
            if (string.IsNullOrEmpty(lolRoot) || limit <= 0) return new string[0];
            var logs = new List<FileInfo>(limit);
            AddRecentLogs(logs, Path.Combine(lolRoot, "LeagueClient"), limit);
            AddRecentLogs(logs, Path.Combine(lolRoot, "LeagueClient", "Logs"), limit);
            string[] paths = new string[logs.Count];
            for (int i = 0; i < logs.Count; i++) paths[i] = logs[i].FullName;
            return paths;
        }

        private static void AddRecentLogs(
            List<FileInfo> output, string directory, int limit)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                foreach (string path in Directory.EnumerateFiles(
                    directory, "*.log", SearchOption.TopDirectoryOnly))
                {
                    string name = System.IO.Path.GetFileName(path);
                    if (name.IndexOf(
                        "LeagueClient", StringComparison.OrdinalIgnoreCase) < 0
                        || name.IndexOf(
                            "LeagueClientUxHelper", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    var candidate = new FileInfo(path);
                    int insert = 0;
                    while (insert < output.Count
                        && output[insert].LastWriteTimeUtc >= candidate.LastWriteTimeUtc)
                        insert++;
                    if (insert >= limit) continue;
                    output.Insert(insert, candidate);
                    if (output.Count > limit) output.RemoveAt(output.Count - 1);
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

        internal static bool TryReadHeadAndTail(
            string path, int headBytes, int tailBytes, out string content)
        {
            content = null;
            if (headBytes <= 0 || tailBytes < 0) return false;
            try
            {
                using (var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    long total = (long)headBytes + tailBytes;
                    if (stream.Length <= total)
                    {
                        using (var reader = new StreamReader(
                            stream, Encoding.UTF8, true))
                            content = reader.ReadToEnd();
                        return true;
                    }

                    byte[] head = new byte[headBytes];
                    int headRead = ReadAtMost(stream, head, head.Length);
                    stream.Seek(-tailBytes, SeekOrigin.End);
                    byte[] tail = new byte[tailBytes];
                    int tailRead = ReadAtMost(stream, tail, tail.Length);
                    string headText =
                        Encoding.UTF8.GetString(head, 0, headRead);
                    if (headText.Length > 0 && headText[0] == '\uFEFF')
                        headText = headText.Substring(1);
                    content = headText
                        + Environment.NewLine
                        + Encoding.UTF8.GetString(tail, 0, tailRead);
                    return true;
                }
            }
            catch { return false; }
        }

        private static int ReadAtMost(
            Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int current = stream.Read(buffer, read, count - read);
                if (current <= 0) break;
                read += current;
            }
            return read;
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
        private const int MaximumResponseChars = 32 * 1024;
        private static readonly string[] GameflowPhases =
        {
            "None", "Lobby", "Matchmaking", "CheckedIntoTournament",
            "ReadyCheck", "ChampSelect",
            "GameStart", "FailedToLaunch", "InProgress", "Reconnect",
            "WaitingForStats", "PreEndOfGame", "EndOfGame",
            "TerminatedInError"
        };
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
            string phase;
            return result.Success
                && TryParseGameflowPhaseBody(result.Body, out phase)
                ? phase : null;
        }

        internal static bool IsCredentialReachable(
            LolLcuCredentials credentials)
        {
            LolHttpResult result = Send(
                credentials,
                "GET",
                "/lol-gameflow/v1/gameflow-phase",
                1000);
            string phase;
            return result.Success
                && TryParseGameflowPhaseBody(result.Body, out phase);
        }

        internal static bool TryParseGameflowPhaseBody(
            string body, out string phase)
        {
            phase = null;
            if (string.IsNullOrWhiteSpace(body)) return false;
            string text = body.Trim();
            if (text.Length < 3 || text[0] != '"'
                || text[text.Length - 1] != '"')
                return false;
            string candidate = text.Substring(1, text.Length - 2);
            for (int i = 0; i < GameflowPhases.Length; i++)
            {
                if (!string.Equals(
                        candidate, GameflowPhases[i],
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                phase = GameflowPhases[i];
                return true;
            }
            return false;
        }

        public static bool KillUx(LolLcuCredentials credentials, string lolRoot)
        {
            if (!Send(credentials, "POST", "/riotclient/kill-ux").Success) return false;
            return WaitForUxExit(lolRoot, 8000);
        }

        public static bool RestoreUx(LolLcuCredentials credentials, string lolRoot)
        {
            Mutex restoreMutex = null;
            bool held = false;
            try
            {
                string suffix = LolHeadlessLease.StableHash(lolRoot);
                try
                {
                    restoreMutex = new Mutex(
                        false, "Global\\Aegis_LolUxRestore_" + suffix);
                }
                catch
                {
                    restoreMutex = new Mutex(
                        false, "Aegis_LolUxRestore_" + suffix);
                }
                try { held = restoreMutex.WaitOne(15000); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return false;
                return RestoreUxSingleWriter(credentials, lolRoot);
            }
            catch { return false; }
            finally
            {
                if (held && restoreMutex != null)
                    try { restoreMutex.ReleaseMutex(); } catch { }
                if (restoreMutex != null)
                    try { restoreMutex.Close(); } catch { }
            }
        }

        private static bool RestoreUxSingleWriter(
            LolLcuCredentials credentials, string lolRoot)
        {
            if (LolRuntimeProcesses.IsUxRunning(lolRoot))
                return TryShowExistingUx(credentials, lolRoot);

            LolHttpResult launch = Send(
                credentials, "POST", "/riotclient/launch-ux");
            if (launch.Success && WaitForUx(lolRoot, 6000))
                return TryShowExistingUx(credentials, lolRoot);

            // launch 请求返回失败时 UX 仍可能已由客户端自行拉起。健康进程
            // 绝不升级为 kill-and-restart，只在确认主 UX 仍不存在时兜底。
            if (LolRuntimeProcesses.IsUxRunning(lolRoot))
                return TryShowExistingUx(credentials, lolRoot);
            LolHttpResult restart = Send(
                credentials, "POST", "/riotclient/kill-and-restart-ux");
            if (!restart.Success) return false;
            WaitForUx(lolRoot, 10000);
            return TryShowExistingUx(credentials, lolRoot);
        }

        private static bool TryShowExistingUx(
            LolLcuCredentials credentials, string lolRoot)
        {
            if (!LolRuntimeProcesses.IsUxRunning(lolRoot)) return false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                LolHttpResult show = Send(
                    credentials, "POST", "/riotclient/ux-show");
                if (show.Success && WaitForUx(lolRoot, 1000)) return true;
                if (!LolRuntimeProcesses.IsUxRunning(lolRoot)) return false;
                Thread.Sleep(350);
            }
            return false;
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
            return Send(credentials, method, relativePath, 3000);
        }

        private static LolHttpResult Send(
            LolLcuCredentials credentials,
            string method,
            string relativePath,
            int timeoutMs)
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
                request.KeepAlive = true;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
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
                    {
                        var text = new StringBuilder(Math.Min(
                            MaximumResponseChars,
                            response.ContentLength > 0
                                ? (int)Math.Min(response.ContentLength, MaximumResponseChars)
                                : 1024));
                        var buffer = new char[2048];
                        while (text.Length < MaximumResponseChars)
                        {
                            int count = reader.Read(
                                buffer, 0,
                                Math.Min(buffer.Length, MaximumResponseChars - text.Length));
                            if (count <= 0) break;
                            text.Append(buffer, 0, count);
                        }
                        return text.ToString();
                    }
                }
            }
            catch { return ""; }
        }
    }
}
