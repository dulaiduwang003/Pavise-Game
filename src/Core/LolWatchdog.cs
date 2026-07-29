// @author bdth 2074055628@qq.com

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal static class LolWatchdog
    {
        private const int Synchronize = 0x00100000;
        private const int ReconnectBindWindowSeconds = 20;
        private const int UnknownPhaseExitGraceSeconds = 12;
        private const int CredentialGenerationCheckSeconds = 3;
        private const int SameMatchSlowPollSeconds = 120;
        private const int SameMatchWatchLimitMinutes = 30;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;
        private const uint HandlePollMs = 5000;
        private const int ReadyTimeoutMs = 5000;
        private const int ReadyTokenBytes = 32;
        private const string ReadyEventPrefix = "Aegis_LolWatchdogReady_";
        private const string GuardMutexPrefix = "Aegis_LolWatchdog_";
        private const string AliveEventPrefix = "Aegis_LolWatchdogAlive_";

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        public static bool TryHandle(string[] args)
        {
            if (args == null || args.Length == 0
                || !string.Equals(args[0], "--lol-watchdog", StringComparison.OrdinalIgnoreCase))
                return false;
            if (args.Length != 2 && args.Length != 4 && args.Length != 5)
                return true;
            string root;
            try { root = Path.GetFullPath(args[1]).TrimEnd('\\'); }
            catch { return true; }
            if (!LolInstallDiscovery.IsValidLolRoot(root)) return true;
            int pid = 0;
            long creation = 0;
            if (args.Length >= 4
                && (!int.TryParse(args[2], out pid) || pid <= 0
                    || !long.TryParse(args[3], out creation) || creation <= 0))
                return true;
            string readyToken = args.Length == 5 ? args[4] : null;
            if (readyToken != null && !IsReadyToken(readyToken))
                return true;
            Run(root, pid, creation, readyToken);
            return true;
        }

        public static bool StartDetached(string lolRoot)
        {
            int pid;
            long creation;
            return LolRuntimeProcesses.TryGetGameIdentity(
                lolRoot, 0, out pid, out creation)
                && StartDetached(lolRoot, pid, creation);
        }

        public static bool StartDetached(string lolRoot, int pid, long creation)
        {
            try { lolRoot = Path.GetFullPath(lolRoot).TrimEnd('\\'); }
            catch { return false; }
            if (!LolInstallDiscovery.IsValidLolRoot(lolRoot)) return false;
            IntPtr verifiedHandle;
            if (!TryOpenGameWaitHandle(
                lolRoot, pid, creation, out verifiedHandle)) return false;
            Native.CloseHandle(verifiedHandle);

            // 守护对象的名字由 lolRoot|pid|creation 哈希而来，这三项对同用户进程都是公开信息，
            // 对象本身也没有独占 DACL，所以"对象存在且被持有"并不能证明真有看门狗在跑。
            // 一旦误信，客户端会被关成无头却没有任何恢复者。这里额外要求确实存在一个跑着本程序
            // 映像的其它进程；判不出来就当作没有看门狗，重新拉一个——多起一个进程是安全的方向。
            if (ExistingGuardIsReady(lolRoot, pid, creation)
                && SiblingAegisProcessAlive()) return true;
            EventWaitHandle readyEvent = null;
            Process process = null;
            try
            {
                string readyToken = CreateReadyToken();
                string readyEventName = ReadyEventName(
                    lolRoot, pid, creation, readyToken);
                bool created;
                readyEvent = new EventWaitHandle(
                    false,
                    EventResetMode.ManualReset,
                    readyEventName,
                    out created);
                if (!created) return false;
                string executable = Application.ExecutablePath;
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = executable;
                startInfo.Arguments = "--lol-watchdog " + Quote(lolRoot)
                    + " " + pid + " " + creation + " " + readyToken;
                startInfo.WorkingDirectory = Path.GetDirectoryName(executable);
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process = Process.Start(startInfo);
                if (process == null) return false;
                if (!readyEvent.WaitOne(ReadyTimeoutMs)) return false;
                try { if (process.HasExited) return false; }
                catch { return false; }
                return LolHeadlessLease.Matches(lolRoot, pid, creation);
            }
            catch { return false; }
            finally
            {
                if (process != null) try { process.Dispose(); } catch { }
                if (readyEvent != null) try { readyEvent.Close(); } catch { }
            }
        }

        private static void Run(
            string lolRoot, int pid, long creation, string readyToken)
        {
            Mutex mutex = null;
            EventWaitHandle aliveEvent = null;
            bool created = false;
            bool globalNamespace = false;
            IntPtr preparedGameHandle = IntPtr.Zero;
            try
            {
                try
                {
                    mutex = new Mutex(
                        true, GuardMutexName(
                            lolRoot, pid, creation, true), out created);
                    globalNamespace = true;
                }
                catch
                {
                    mutex = new Mutex(
                        true, GuardMutexName(
                            lolRoot, pid, creation, false), out created);
                }
                if (!created) return;
                if (!LeaseStillOwned(lolRoot, pid, creation)) return;
                if (pid > 0 && creation > 0
                    && !TryOpenGameWaitHandle(
                        lolRoot, pid, creation, out preparedGameHandle))
                    return;
                if (preparedGameHandle != IntPtr.Zero
                    && WaitForSingleObject(preparedGameHandle, 0) != WaitTimeout)
                    return;
                bool aliveCreated;
                aliveEvent = new EventWaitHandle(
                    false,
                    EventResetMode.ManualReset,
                    AliveEventName(
                        lolRoot, pid, creation, globalNamespace),
                    out aliveCreated);
                if (!aliveCreated || !aliveEvent.Set()) return;
                if (readyToken != null
                    && (preparedGameHandle == IntPtr.Zero
                        || !SignalReady(
                            lolRoot, pid, creation, readyToken)))
                    return;

                IntPtr initialGameHandle = preparedGameHandle;
                preparedGameHandle = IntPtr.Zero;
                if (WaitForGameExit(
                        lolRoot, pid, creation, initialGameHandle))
                    TryRestore(lolRoot, pid, creation);
            }
            finally
            {
                if (preparedGameHandle != IntPtr.Zero)
                    Native.CloseHandle(preparedGameHandle);
                if (aliveEvent != null)
                {
                    try { aliveEvent.Reset(); } catch { }
                    try { aliveEvent.Close(); } catch { }
                }
                if (mutex != null)
                {
                    if (created)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                    try { mutex.Close(); } catch { }
                }
            }
        }

        internal static bool TryOpenGameWaitHandle(
            string lolRoot, int pid, long creation, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            if (pid <= 0 || creation <= 0
                || !LolInstallDiscovery.IsValidLolRoot(lolRoot))
                return false;
            IntPtr candidate = IntPtr.Zero;
            try
            {
                candidate = Native.OpenProcess(
                    Native.PROCESS_QUERY_LIMITED_INFORMATION | Synchronize, false, pid);
                if (candidate == IntPtr.Zero) return false;
                string path = Native.ImagePath(candidate);
                if (!LolRuntimeProcesses.IsGameProcess(
                    path, Path.GetFileName(path), lolRoot))
                    return false;
                long actualCreation;
                long cpu;
                ulong io;
                if (!Native.QueryProcessSample(
                    candidate, out actualCreation, out cpu, out io)
                    || actualCreation != creation)
                    return false;
                handle = candidate;
                candidate = IntPtr.Zero;
                return true;
            }
            catch { return false; }
            finally
            {
                if (candidate != IntPtr.Zero) Native.CloseHandle(candidate);
            }
        }

        private static bool WaitForGameExit(
            string lolRoot,
            int initialPid,
            long initialCreation,
            IntPtr preparedGameHandle)
        {
            DateTime nextCredentialRefresh = DateTime.MinValue;
            LolLcuCredentials credentials = null;
            int absentSamples = 0;
            int failedProbes = 0;
            int credentialLookupFailures = 0;
            int pid = initialPid;
            long creation = initialCreation;
            long credentialGeneration =
                LolLcuCredentialSource.CredentialGenerationStamp(lolRoot);
            DateTime nextGenerationCheck = DateTime.MinValue;
            IntPtr gameHandle = preparedGameHandle;
            bool legacyBind = pid <= 0 || creation <= 0;
            int leasePid = initialPid;
            long leaseCreation = initialCreation;
            bool initialVerified = false;
            try
            {
                if (!LeaseStillOwned(
                    lolRoot, leasePid, leaseCreation)) return false;
                if (legacyBind)
                {
                    if (!LolRuntimeProcesses.TryGetGameIdentity(
                        lolRoot, 0, out pid, out creation))
                    {
                        pid = 0;
                        creation = 0;
                    }
                }

                if (gameHandle != IntPtr.Zero
                    || (pid > 0 && creation > 0
                        && TryOpenGameWaitHandle(
                            lolRoot, pid, creation, out gameHandle)))
                {
                    initialVerified = true;
                    uint wait = WaitForOwnedGameExit(
                        gameHandle, lolRoot, leasePid, leaseCreation);
                    Native.CloseHandle(gameHandle);
                    gameHandle = IntPtr.Zero;
                    if (wait != WaitObject0) return false;
                }
                else if (!legacyBind)
                {
                    if (!LeaseStillOwned(
                            lolRoot, leasePid, leaseCreation)
                        || !OriginalGameIdentityGone(
                            lolRoot, pid, creation))
                        return false;

                    initialVerified = true;
                }

                DateTime gameExitUtc = DateTime.UtcNow;
                DateTime clientMissingSinceUtc = DateTime.MinValue;
                DateTime sameMatchMissingSinceUtc = DateTime.MinValue;
                DateTime reconnectDeadline = gameExitUtc.AddSeconds(
                    ReconnectBindWindowSeconds);
                bool rebound = false;
                while (true)
                {
                    if (!LeaseStillOwned(
                            lolRoot, leasePid, leaseCreation))
                        return false;
                    if (!LolRuntimeProcesses.IsClientRunning(lolRoot))
                    {
                        if (clientMissingSinceUtc == DateTime.MinValue)
                            clientMissingSinceUtc = DateTime.UtcNow;
                        if ((DateTime.UtcNow - clientMissingSinceUtc).TotalMinutes
                            >= 2)
                            return false;
                        Thread.Sleep(1500);
                        continue;
                    }
                    clientMissingSinceUtc = DateTime.MinValue;
                    if (DateTime.UtcNow >= nextGenerationCheck)
                    {
                        long currentGeneration =
                            LolLcuCredentialSource.CredentialGenerationStamp(
                                lolRoot);
                        if (currentGeneration != credentialGeneration)
                        {
                            credentialGeneration = currentGeneration;
                            credentials = null;
                            failedProbes = 0;
                            credentialLookupFailures = 0;
                            nextCredentialRefresh = DateTime.MinValue;
                        }
                        nextGenerationCheck = DateTime.UtcNow.AddSeconds(
                            CredentialGenerationCheckSeconds);
                    }
                    if (credentials == null && DateTime.UtcNow >= nextCredentialRefresh)
                    {
                        credentials = LolLcuCredentialSource.Find(lolRoot);
                        if (credentials == null)
                        {
                            nextCredentialRefresh = NextCredentialLookup(
                                ref credentialLookupFailures);
                        }
                        else
                        {
                            credentialLookupFailures = 0;
                            failedProbes = 0;
                        }
                    }
                    string phase = credentials == null
                        ? null : LolLcuClient.GetGameflowPhase(credentials);
                    if (credentials != null)
                    {
                        if (phase == null)
                        {
                            if (++failedProbes >= 3)
                            {
                                credentials = null;
                                failedProbes = 0;
                                nextCredentialRefresh = NextCredentialLookup(
                                    ref credentialLookupFailures);
                            }
                        }
                        else failedProbes = 0;
                    }
                    bool phaseKnown = !string.IsNullOrEmpty(phase);
                    bool sameMatch =
                        LolOptimizationService.IsSameMatchPhase(phase);

                    if (initialVerified && !rebound && sameMatch
                        && DateTime.UtcNow < reconnectDeadline)
                    {
                        int replacementPid;
                        long replacementCreation;
                        if (LolRuntimeProcesses.TryGetGameIdentity(
                            lolRoot, 0, out replacementPid, out replacementCreation)
                            && replacementCreation > creation
                            && TryOpenGameWaitHandle(
                                lolRoot, replacementPid, replacementCreation, out gameHandle))
                        {
                            rebound = true;
                            uint wait = WaitForOwnedGameExit(
                                gameHandle, lolRoot, leasePid, leaseCreation);
                            Native.CloseHandle(gameHandle);
                            gameHandle = IntPtr.Zero;
                            if (wait != WaitObject0) return false;
                            gameExitUtc = DateTime.UtcNow;
                            absentSamples = 0;
                            continue;
                        }
                    }

                    bool gameRunning = LolRuntimeProcesses.IsGameRunning(lolRoot);
                    if (sameMatch && !gameRunning)
                    {
                        if (sameMatchMissingSinceUtc == DateTime.MinValue)
                            sameMatchMissingSinceUtc = DateTime.UtcNow;
                        if ((DateTime.UtcNow - sameMatchMissingSinceUtc).TotalMinutes
                            >= SameMatchWatchLimitMinutes)
                            return false;
                    }
                    else
                    {
                        sameMatchMissingSinceUtc = DateTime.MinValue;
                    }
                    if (!gameRunning && phaseKnown
                        && LolOptimizationService.IsConfirmedMatchExitPhase(phase))
                    {
                        if (++absentSamples >= 2) return true;
                    }
                    else
                        absentSamples = 0;
                    if (!gameRunning
                        && (DateTime.UtcNow - gameExitUtc).TotalSeconds
                            >= UnknownPhaseExitGraceSeconds
                        && !phaseKnown)
                        return true;
                    int waitMs = DateTime.UtcNow < reconnectDeadline
                        ? 1500
                        : (sameMatchMissingSinceUtc != DateTime.MinValue
                            && (DateTime.UtcNow - sameMatchMissingSinceUtc)
                                .TotalSeconds >= SameMatchSlowPollSeconds
                            ? 30000 : 6000);
                    Thread.Sleep(waitMs);
                }
            }
            finally
            {
                if (gameHandle != IntPtr.Zero) Native.CloseHandle(gameHandle);
            }
        }

        private static uint WaitForOwnedGameExit(
            IntPtr handle, string lolRoot, int leasePid, long leaseCreation)
        {
            while (true)
            {
                uint wait = WaitForSingleObject(handle, HandlePollMs);
                if (wait == WaitObject0) return wait;
                if (wait != WaitTimeout) return wait;
                if (!LeaseStillOwned(
                    lolRoot, leasePid, leaseCreation)) return uint.MaxValue;
            }
        }

        private static bool OriginalGameIdentityGone(
            string lolRoot, int pid, long creation)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = Native.OpenProcess(
                    Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (handle == IntPtr.Zero)
                    return Marshal.GetLastWin32Error() == 87;
                long actualCreation;
                long cpu;
                ulong io;
                if (!Native.QueryProcessSample(
                        handle, out actualCreation, out cpu, out io))
                    return false;
                if (actualCreation != creation) return true;
                string path = Native.ImagePath(handle);
                return !LolRuntimeProcesses.IsGameProcess(
                    path, Path.GetFileName(path), lolRoot);
            }
            catch { return false; }
            finally
            {
                if (handle != IntPtr.Zero) Native.CloseHandle(handle);
            }
        }

        private static bool TryRestore(
            string lolRoot, int leasePid, long leaseCreation)
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(2);
            DateTime nextCredentialRefresh = DateTime.MinValue;
            DateTime nextRestoreAttempt = DateTime.MinValue;
            LolLcuCredentials credentials = null;
            int credentialLookupFailures = 0;
            int restoreFailures = 0;
            bool uxRestored = false;
            long credentialGeneration =
                LolLcuCredentialSource.CredentialGenerationStamp(lolRoot);
            DateTime nextGenerationCheck = DateTime.MinValue;
            while (DateTime.UtcNow < deadline)
            {
                if (!LeaseStillOwned(
                    lolRoot, leasePid, leaseCreation)) return false;
                if (!LolRuntimeProcesses.IsClientRunning(lolRoot))
                {
                    Thread.Sleep(1500);
                    continue;
                }
                if (DateTime.UtcNow >= nextGenerationCheck)
                {
                    long currentGeneration =
                        LolLcuCredentialSource.CredentialGenerationStamp(
                            lolRoot);
                    if (currentGeneration != credentialGeneration)
                    {
                        credentialGeneration = currentGeneration;
                        credentials = null;
                        credentialLookupFailures = 0;
                        restoreFailures = 0;
                        nextCredentialRefresh = DateTime.MinValue;
                        nextRestoreAttempt = DateTime.MinValue;
                    }
                    nextGenerationCheck = DateTime.UtcNow.AddSeconds(
                        CredentialGenerationCheckSeconds);
                }
                if (credentials == null
                    && DateTime.UtcNow >= nextCredentialRefresh)
                {
                    credentials = LolLcuCredentialSource.Find(lolRoot);
                    if (credentials == null)
                    {
                        nextCredentialRefresh = NextCredentialLookup(
                            ref credentialLookupFailures);
                    }
                    else
                    {
                        credentialLookupFailures = 0;
                        nextCredentialRefresh = DateTime.MaxValue;
                    }
                }
                string phase = credentials == null
                    ? null : LolLcuClient.GetGameflowPhase(credentials);
                int newerPid;
                long newerCreation;
                if (leaseCreation > 0
                    && LolRuntimeProcesses.TryGetGameIdentity(
                        lolRoot, 0, out newerPid, out newerCreation)
                    && newerCreation > leaseCreation)
                    return false;
                if (credentials != null && phase == null)
                {
                    credentials = null;
                    nextCredentialRefresh = NextCredentialLookup(
                        ref credentialLookupFailures);
                    Thread.Sleep(1500);
                    continue;
                }
                if (LolOptimizationService.IsSameMatchPhase(phase))
                {
                    Thread.Sleep(6000);
                    continue;
                }
                bool restoreAttempted = !uxRestored && credentials != null
                    && DateTime.UtcNow >= nextRestoreAttempt;
                if (restoreAttempted
                    && LolLcuClient.RestoreUx(credentials, lolRoot))
                {
                    uxRestored = true;
                }
                if (uxRestored)
                {
                    bool cleared = leasePid > 0 && leaseCreation > 0
                        ? LolHeadlessLease.ClearIfMatches(
                            lolRoot, leasePid, leaseCreation)
                        : LolHeadlessLease.ClearLegacyIfMatches(lolRoot);
                    if (cleared || !LeaseStillOwned(
                            lolRoot, leasePid, leaseCreation))
                        return true;
                    Thread.Sleep(1500);
                    continue;
                }
                if (restoreAttempted)
                {
                    if (restoreFailures < int.MaxValue) restoreFailures++;
                    nextRestoreAttempt = DateTime.UtcNow.AddSeconds(
                        LolOptimizationService.RestoreCredentialRetrySeconds(
                            restoreFailures));
                }
                Thread.Sleep(1500);
            }
            return false;
        }

        private static DateTime NextCredentialLookup(ref int failures)
        {
            if (failures < int.MaxValue) failures++;
            return DateTime.UtcNow.AddSeconds(
                LolOptimizationService.CredentialRetrySeconds(failures));
        }

        private static bool LeaseStillOwned(
            string lolRoot, int leasePid, long leaseCreation)
        {
            return leasePid > 0 && leaseCreation > 0
                ? LolHeadlessLease.Matches(
                    lolRoot, leasePid, leaseCreation)
                : LolHeadlessLease.MatchesLegacyRoot(lolRoot);
        }

        // 是否存在另一个运行着本程序映像的进程。这不是不可伪造的证明（同用户的攻击者
        // 可以复制一份 exe 再跑），但足以挡住陈旧对象和无关进程抢占命名对象的情况。
        private static bool SiblingAegisProcessAlive()
        {
            string self;
            int selfPid;
            string baseName;
            try
            {
                self = Path.GetFullPath(Application.ExecutablePath);
                baseName = Path.GetFileNameWithoutExtension(self);
                using (Process me = Process.GetCurrentProcess()) selfPid = me.Id;
            }
            catch { return false; }
            if (string.IsNullOrEmpty(baseName)) return false;

            Process[] all;
            try { all = Process.GetProcessesByName(baseName); }
            catch { return false; }
            try
            {
                foreach (Process other in all)
                {
                    try
                    {
                        if (other.Id == selfPid) continue;
                        IntPtr h = Native.OpenProcess(
                            Native.PROCESS_QUERY_LIMITED_INFORMATION, false, other.Id);
                        if (h == IntPtr.Zero) continue;
                        try
                        {
                            string image = Native.ImagePath(h);
                            if (image != null
                                && string.Equals(
                                    Path.GetFullPath(image), self,
                                    StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        finally { Native.CloseHandle(h); }
                    }
                    catch { }
                }
            }
            finally
            {
                foreach (Process other in all) try { other.Dispose(); } catch { }
            }
            return false;
        }

        internal static bool ExistingGuardIsReady(
            string lolRoot, int pid, long creation)
        {
            if (pid <= 0 || creation <= 0
                || !LolHeadlessLease.Matches(
                    lolRoot, pid, creation))
                return false;
            return GuardObjectsReady(
                    GuardMutexName(lolRoot, pid, creation, true),
                    AliveEventName(lolRoot, pid, creation, true))
                || GuardObjectsReady(
                    GuardMutexName(lolRoot, pid, creation, false),
                    AliveEventName(lolRoot, pid, creation, false));
        }

        internal static bool GuardObjectsReady(
            string mutexName, string aliveEventName)
        {
            EventWaitHandle alive = null;
            Mutex mutex = null;
            bool acquired = false;
            try
            {
                alive = EventWaitHandle.OpenExisting(aliveEventName);
                if (!alive.WaitOne(0)) return false;
                mutex = Mutex.OpenExisting(mutexName);
                try { acquired = mutex.WaitOne(0, false); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return true;
                try { mutex.ReleaseMutex(); } catch { }
                acquired = false;
                return false;
            }
            catch { return false; }
            finally
            {
                if (acquired && mutex != null)
                    try { mutex.ReleaseMutex(); } catch { }
                if (mutex != null) try { mutex.Close(); } catch { }
                if (alive != null) try { alive.Close(); } catch { }
            }
        }

        internal static string GuardMutexName(
            string lolRoot, int pid, long creation, bool globalNamespace)
        {
            return (globalNamespace ? "Global\\" : "")
                + GuardMutexPrefix + GuardIdentity(lolRoot, pid, creation);
        }

        internal static string AliveEventName(
            string lolRoot, int pid, long creation, bool globalNamespace)
        {
            return (globalNamespace ? "Global\\" : "")
                + AliveEventPrefix + GuardIdentity(lolRoot, pid, creation);
        }

        private static string GuardIdentity(
            string lolRoot, int pid, long creation)
        {
            return LolHeadlessLease.StableHash(
                lolRoot + "|" + pid + "|" + creation);
        }

        internal static string CreateReadyToken()
        {
            byte[] bytes = new byte[ReadyTokenBytes];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            var text = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                text.Append(bytes[i].ToString("x2"));
            return text.ToString();
        }

        internal static bool IsReadyToken(string token)
        {
            if (token == null || token.Length != ReadyTokenBytes * 2)
                return false;
            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (!((c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        internal static string ReadyEventName(
            string lolRoot, int pid, long creation, string readyToken)
        {
            if (!IsReadyToken(readyToken) || pid <= 0 || creation <= 0)
                return null;
            return ReadyEventPrefix
                + LolHeadlessLease.StableHash(lolRoot)
                + "_" + pid + "_" + creation + "_" + readyToken;
        }

        internal static bool SignalReady(
            string lolRoot, int pid, long creation, string readyToken)
        {
            string name = ReadyEventName(
                lolRoot, pid, creation, readyToken);
            if (name == null) return false;
            EventWaitHandle readyEvent = null;
            try
            {
                readyEvent = EventWaitHandle.OpenExisting(name);
                return readyEvent.Set();
            }
            catch { return false; }
            finally
            {
                if (readyEvent != null) try { readyEvent.Close(); } catch { }
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "") + "\"";
        }
    }
}
