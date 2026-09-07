// @author bdth 2074055628@qq.com
// 文件用途 英雄联盟相关进程的扫描 识别与清理

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseApp
{
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
        public bool WeGameMainRunning;
        public int CrossProcessCount;
        public int UxProcessCount;
        public int MainUxProcessCount;
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

        internal static bool IsOwnedLcuListenerPort(string lolRoot, int port)
        {
            if (port <= 0) return false;
            int currentSession;
            if (!TryGetCurrentSessionId(out currentSession)) return false;
            int listenerPid;
            if (!LolNative.TryGetTcpListenerOwner(port, out listenerPid)) return false;
            return IsOwnedCredentialSourceProcess(lolRoot, listenerPid, currentSession);
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
            return ScanCore(lolRoot, weGameRoot, namesOnly, false);
        }

        // 通用 WeGame 游戏用这个入口 根目录只要求存在 不要求是英雄联盟的布局
        //   客户端 游戏 大厅几项自然全假 只有 WeGame 与 Cross 的计数有意义
        public static LolProcessSnapshot ScanShell(string gameRoot, string weGameRoot, bool namesOnly)
        {
            return ScanCore(gameRoot, weGameRoot, namesOnly, true);
        }

        private static string ValidatedRoot(string root, bool genericRoot)
        {
            if (genericRoot)
            {
                try { return !string.IsNullOrEmpty(root) && System.IO.Directory.Exists(root) ? root : null; }
                catch { return null; }
            }
            return LolInstallDiscovery.IsValidLolRoot(root) ? root : null;
        }

        private static LolProcessSnapshot ScanCore(string lolRoot, string weGameRoot, bool namesOnly, bool genericRoot)
        {
            var result = new LolProcessSnapshot();
            lolRoot = ValidatedRoot(lolRoot, genericRoot);
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
                    if (IsWeGameMainProcess(path, file, weGameRoot)) result.WeGameMainRunning = true;
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
            return CleanCore(lolRoot, weGameRoot, includeDownloaders, false);
        }

        // 通用 WeGame 游戏的脱壳 结束的只有 WeGame 目录下的壳进程 游戏目录下的 Cross 与 TCLS 会话进程
        //   游戏本体 TCLS\Client.exe 反作弊一个都不在名单里 名单见 IsCleanupTarget
        public static LolCleanupResult CleanShell(
            string gameRoot, string weGameRoot, bool includeDownloaders)
        {
            return CleanCore(gameRoot, weGameRoot, includeDownloaders, true);
        }

        private static LolCleanupResult CleanCore(
            string lolRoot, string weGameRoot, bool includeDownloaders, bool genericRoot)
        {
            var result = new LolCleanupResult();
            lolRoot = ValidatedRoot(lolRoot, genericRoot);
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
            return AnyProcessMatches(
                lolRoot, new[] { "League of Legends" }, IsGameProcess);
        }

        private static bool AnyProcessMatches(
            string root, string[] names,
            Func<string, string, string, bool> predicate)
        {
            if (string.IsNullOrEmpty(root)) return false;
            int currentSession;
            if (!TryGetCurrentSessionId(out currentSession)) return false;
            for (int i = 0; i < names.Length; i++)
            {
                string bare = names[i];
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
                            if (predicate(
                                path, System.IO.Path.GetFileName(path), root))
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
            return LolInstallDiscovery.IsValidWeGameRoot(weGameRoot)
                && AnyProcessMatches(weGameRoot, new[] { "wegame.exe" }, IsWeGameMainProcess);
        }

        public static bool IsClientRunning(string lolRoot)
        {
            return AnyProcessMatches(
                lolRoot, new[] { "LeagueClient" }, IsLeagueClientProcess);
        }

        public static bool IsUxRunning(string lolRoot)
        {
            return AnyProcessMatches(
                lolRoot, new[] { "LeagueClientUx" }, IsUxProcess);
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
                if (fullRoot.Length <= 3) return false;
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

        private static bool IsWeGameMainProcess(string path, string file, string weGameRoot)
        {
            return !string.IsNullOrEmpty(weGameRoot) && IsUnder(path, weGameRoot)
                && string.Equals(file, "wegame.exe", StringComparison.OrdinalIgnoreCase);
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
}
