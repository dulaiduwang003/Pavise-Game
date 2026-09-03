// @author bdth 2074055628@qq.com
// 文件用途 LCU 凭据的解析与来源发现

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace PaviseApp
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
                if (!LolRuntimeProcesses.IsOwnedLcuListenerPort(lolRoot, port)) continue;
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
}
