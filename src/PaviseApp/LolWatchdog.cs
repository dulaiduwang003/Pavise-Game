using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class LolWatchdog
    {
        private sealed class LaunchOwner { internal Process Process; internal EventWaitHandle Ready; }
        private static readonly object launchGate = new object();
        private static readonly Dictionary<string, LaunchOwner> launchOwners = new Dictionary<string, LaunchOwner>(StringComparer.OrdinalIgnoreCase);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        public static bool TryHandle(string[] args)
        {
            if (args == null || args.Length == 0 || !string.Equals(args[0], "--lol-watchdog", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (args.Length != 2 && args.Length != 4 && args.Length != 5)
            {
                return true;
            }
            string getFullPathTrimmed;
            try
            {
                getFullPathTrimmed = Path.GetFullPath(args[1]).TrimEnd('\\');
            }
            catch
            {
                return true;
            }
            if (!LolInstallDiscovery.IsValidLolRoot(getFullPathTrimmed))
            {
                return true;
            }
            int result = 0;
            long result2 = 0L;
            if (args.Length >= 4 && (!int.TryParse(args[2], out result) || result <= 0 || !long.TryParse(args[3], out result2) || result2 <= 0))
            {
                return true;
            }
            string message = ((args.Length == 5) ? args[4] : null);
            if (message != null && !IsReadyToken(message))
            {
                return true;
            }
            Run(getFullPathTrimmed, result, result2, message);
            return true;
        }

        public static bool StartDetached(string lolRoot)
        {
            int pid;
            long creation;
            if (LolRuntimeProcesses.TryGetGameIdentity(lolRoot, 0, out pid, out creation))
            {
                return StartDetached(lolRoot, pid, creation);
            }
            return false;
        }

        public static bool StartDetached(string lolRoot, int pid, long creation)
        {
            lock (launchGate) return StartDetachedCore(lolRoot, pid, creation);
        }

        private static bool StartDetachedCore(string lolRoot, int pid, long creation)
        {
            try
            {
                lolRoot = Path.GetFullPath(lolRoot).TrimEnd('\\');
            }
            catch
            {
                return false;
            }
            if (!LolInstallDiscovery.IsValidLolRoot(lolRoot))
            {
                return false;
            }
            IntPtr handle;
            if (!TryOpenGameWaitHandle(lolRoot, pid, creation, out handle))
            {
                return false;
            }
            Native.CloseHandle(handle);
            string launchKey = lolRoot + "|" + pid + "|" + creation;
            LaunchOwner owner;
            if (launchOwners.TryGetValue(launchKey, out owner))
            {
                try
                {
                    if (!owner.Process.HasExited)
                        return owner.Ready.WaitOne(0) && LolHeadlessLease.Matches(lolRoot, pid, creation);
                }
                catch { return false; }
                owner.Ready.Dispose(); owner.Process.Dispose(); launchOwners.Remove(launchKey);
            }
            if (ExistingGuardIsReady(lolRoot, pid, creation) && SiblingPaviseProcessAlive())
            {
                return true;
            }
            EventWaitHandle eventWaitHandle = null;
            Process process = null;
            try
            {
                string readyToken = CreateReadyToken();
                string name = ReadyEventName(lolRoot, pid, creation, readyToken);
                bool createdNew;
                eventWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, name, out createdNew);
                if (!createdNew)
                {
                    return false;
                }
                string executablePath = Application.ExecutablePath;
                ProcessStartInfo processStartInfo = new ProcessStartInfo();
                processStartInfo.FileName = executablePath;
                processStartInfo.Arguments = "--lol-watchdog " + Quote(lolRoot) + " " + pid + " " + creation + " " + readyToken;
                processStartInfo.WorkingDirectory = Path.GetDirectoryName(executablePath);
                processStartInfo.UseShellExecute = false;
                processStartInfo.CreateNoWindow = true;
                processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                process = Process.Start(processStartInfo);
                if (process == null)
                {
                    return false;
                }
                launchOwners[launchKey] = new LaunchOwner { Process = process, Ready = eventWaitHandle };
                if (!eventWaitHandle.WaitOne(5000))
                {
                    return false;
                }
                try
                {
                    if (process.HasExited)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
                return LolHeadlessLease.Matches(lolRoot, pid, creation);
            }
            catch
            {
                return false;
            }
            finally
            {
                // 就绪等待超时时保留原辅助进程和事件，后续调用继续观察同一所有者。
                bool exited = process == null;
                try { if (process != null) exited = process.HasExited; } catch { }
                if (exited && process != null)
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
                if (exited && eventWaitHandle != null)
                {
                    try
                    {
                        eventWaitHandle.Close();
                    }
                    catch
                    {
                    }
                }
                if (exited) launchOwners.Remove(launchKey);
            }
        }

        private static void Run(string lolRoot, int pid, long creation, string readyToken)
        {
            Mutex mutex = null;
            EventWaitHandle eventWaitHandle = null;
            bool createdNew = false;
            bool globalNamespace = false;
            IntPtr handle = IntPtr.Zero;
            try
            {
                try
                {
                    mutex = new Mutex(true, GuardMutexName(lolRoot, pid, creation, true), out createdNew);
                    globalNamespace = true;
                }
                catch
                {
                    mutex = new Mutex(true, GuardMutexName(lolRoot, pid, creation, false), out createdNew);
                }
                if (!createdNew || !LeaseStillOwned(lolRoot, pid, creation) || (pid > 0 && creation > 0 && !TryOpenGameWaitHandle(lolRoot, pid, creation, out handle)) || (handle != IntPtr.Zero && WaitForSingleObject(handle, 0u) != 258))
                {
                    return;
                }
                bool createdNew2;
                eventWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, AliveEventName(lolRoot, pid, creation, globalNamespace), out createdNew2);
                if (createdNew2 && eventWaitHandle.Set() && (readyToken == null || (handle != IntPtr.Zero && SignalReady(lolRoot, pid, creation, readyToken))))
                {
                    IntPtr preparedGameHandle = handle;
                    handle = IntPtr.Zero;
                    if (WaitForGameExit(lolRoot, pid, creation, preparedGameHandle))
                    {
                        TryRestore(lolRoot, pid, creation);
                    }
                }
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    Native.CloseHandle(handle);
                }
                if (eventWaitHandle != null)
                {
                    try
                    {
                        eventWaitHandle.Reset();
                    }
                    catch
                    {
                    }
                    try
                    {
                        eventWaitHandle.Close();
                    }
                    catch
                    {
                    }
                }
                if (mutex != null)
                {
                    if (createdNew)
                    {
                        try
                        {
                            mutex.ReleaseMutex();
                        }
                        catch
                        {
                        }
                    }
                    try
                    {
                        mutex.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal static bool TryOpenGameWaitHandle(string lolRoot, int pid, long creation, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            if (pid <= 0 || creation <= 0 || !LolInstallDiscovery.IsValidLolRoot(lolRoot))
            {
                return false;
            }
            IntPtr processHandle = IntPtr.Zero;
            try
            {
                processHandle = Native.OpenProcess(1052672, false, pid);
                if (processHandle == IntPtr.Zero)
                {
                    return false;
                }
                string path = Native.ImagePath(processHandle);
                if (!LolRuntimeProcesses.IsGameProcess(path, Path.GetFileName(path), lolRoot))
                {
                    return false;
                }
                long creation2;
                long cpu;
                ulong io;
                if (!Native.QueryProcessSample(processHandle, out creation2, out cpu, out io) || creation2 != creation)
                {
                    return false;
                }
                handle = processHandle;
                processHandle = IntPtr.Zero;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (processHandle != IntPtr.Zero)
                {
                    Native.CloseHandle(processHandle);
                }
            }
        }

        private static bool WaitForGameExit(string lolRoot, int initialPid, long initialCreation, IntPtr preparedGameHandle)
        {
            DateTime dateTime = DateTime.MinValue;
            LolLcuCredentials lolLcuCredentials = null;
            int gameExitObservations = 0;
            int missingPhaseObservations = 0;
            int failures = 0;
            long credentialGenerationStampResult = LolLcuCredentialSource.CredentialGenerationStamp(lolRoot);
            DateTime minValue = DateTime.MinValue;
            IntPtr handle = preparedGameHandle;
            if (initialPid <= 0 || initialCreation <= 0)
            {
                return false;
            }
            bool originalExitConfirmed = false;
            try
            {
                if (!LeaseStillOwned(lolRoot, initialPid, initialCreation))
                {
                    return false;
                }
                if (handle != IntPtr.Zero || TryOpenGameWaitHandle(lolRoot, initialPid, initialCreation, out handle))
                {
                    originalExitConfirmed = true;
                    uint waitForOwnedGameExitResult = WaitForOwnedGameExit(handle, lolRoot, initialPid, initialCreation);
                    Native.CloseHandle(handle);
                    handle = IntPtr.Zero;
                    if (waitForOwnedGameExitResult != 0)
                    {
                        return false;
                    }
                }
                else
                {
                    if (!LeaseStillOwned(lolRoot, initialPid, initialCreation) || !OriginalGameIdentityGone(lolRoot, initialPid, initialCreation))
                    {
                        return false;
                    }
                    originalExitConfirmed = true;
                }
                DateTime utcNow = DateTime.UtcNow;
                DateTime currentMinValue = DateTime.MinValue;
                DateTime candidateMinValue = DateTime.MinValue;
                DateTime addSeconds = utcNow.AddSeconds(20.0);
                bool handoffAttempted = false;
                while (true)
                {
                    if (!LeaseStillOwned(lolRoot, initialPid, initialCreation))
                    {
                        return false;
                    }
                    if (!LolRuntimeProcesses.IsClientRunning(lolRoot))
                    {
                        if (currentMinValue == DateTime.MinValue)
                        {
                            currentMinValue = DateTime.UtcNow;
                        }
                        if ((DateTime.UtcNow - currentMinValue).TotalMinutes >= 2.0)
                        {
                            return false;
                        }
                        Thread.Sleep(1500);
                        continue;
                    }
                    currentMinValue = DateTime.MinValue;
                    if (DateTime.UtcNow >= minValue)
                    {
                        long credentialGenerationStampResult2 = LolLcuCredentialSource.CredentialGenerationStamp(lolRoot);
                        if (credentialGenerationStampResult2 != credentialGenerationStampResult)
                        {
                            credentialGenerationStampResult = credentialGenerationStampResult2;
                            lolLcuCredentials = null;
                            missingPhaseObservations = 0;
                            failures = 0;
                            dateTime = DateTime.MinValue;
                        }
                        minValue = DateTime.UtcNow.AddSeconds(3.0);
                    }
                    if (lolLcuCredentials == null && DateTime.UtcNow >= dateTime)
                    {
                        lolLcuCredentials = LolLcuCredentialSource.Find(lolRoot);
                        if (lolLcuCredentials == null)
                        {
                            dateTime = NextCredentialLookup(ref failures);
                        }
                        else
                        {
                            failures = 0;
                            missingPhaseObservations = 0;
                        }
                    }
                    string gameflowPhase = ((lolLcuCredentials == null) ? null : LolLcuClient.GetGameflowPhase(lolLcuCredentials));
                    if (lolLcuCredentials != null)
                    {
                        if (gameflowPhase == null)
                        {
                            if (++missingPhaseObservations >= 3)
                            {
                                lolLcuCredentials = null;
                                missingPhaseObservations = 0;
                                dateTime = NextCredentialLookup(ref failures);
                            }
                        }
                        else
                        {
                            missingPhaseObservations = 0;
                        }
                    }
                    bool phaseKnown = !string.IsNullOrEmpty(gameflowPhase);
                    bool isSameMatchPhase = LolOptimizationService.IsSameMatchPhase(gameflowPhase);
                    int pid;
                    long creation;
                    if (originalExitConfirmed && !handoffAttempted && isSameMatchPhase && DateTime.UtcNow < addSeconds && LolRuntimeProcesses.TryGetGameIdentity(lolRoot, 0, out pid, out creation) && creation > initialCreation && TryOpenGameWaitHandle(lolRoot, pid, creation, out handle))
                    {
                        handoffAttempted = true;
                        uint waitForOwnedGameExitResult2 = WaitForOwnedGameExit(handle, lolRoot, initialPid, initialCreation);
                        Native.CloseHandle(handle);
                        handle = IntPtr.Zero;
                        if (waitForOwnedGameExitResult2 != 0)
                        {
                            return false;
                        }
                        utcNow = DateTime.UtcNow;
                        gameExitObservations = 0;
                        continue;
                    }
                    bool isGameRunning = LolRuntimeProcesses.IsGameRunning(lolRoot);
                    if (isSameMatchPhase && !isGameRunning)
                    {
                        if (candidateMinValue == DateTime.MinValue)
                        {
                            candidateMinValue = DateTime.UtcNow;
                        }
                        if ((DateTime.UtcNow - candidateMinValue).TotalMinutes >= 30.0)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        candidateMinValue = DateTime.MinValue;
                    }
                    if (!isGameRunning && phaseKnown && LolOptimizationService.IsConfirmedMatchExitPhase(gameflowPhase))
                    {
                        if (++gameExitObservations >= 2)
                        {
                            return true;
                        }
                    }
                    else
                    {
                        gameExitObservations = 0;
                    }
                    if (!isGameRunning && (DateTime.UtcNow - utcNow).TotalSeconds >= 12.0 && !phaseKnown)
                    {
                        break;
                    }
                    int millisecondsTimeout = ((DateTime.UtcNow < addSeconds) ? 1500 : ((candidateMinValue != DateTime.MinValue && (DateTime.UtcNow - candidateMinValue).TotalSeconds >= 120.0) ? 30000 : 6000));
                    Thread.Sleep(millisecondsTimeout);
                }
                return true;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    Native.CloseHandle(handle);
                }
            }
        }

        private static uint WaitForOwnedGameExit(IntPtr handle, string lolRoot, int leasePid, long leaseCreation)
        {
            do
            {
                uint waitForSingleObjectResult = WaitForSingleObject(handle, 5000u);
                switch (waitForSingleObjectResult)
                {
                case 0u:
                    return waitForSingleObjectResult;
                default:
                    return waitForSingleObjectResult;
                case 258u:
                    break;
                }
            }
            while (LeaseStillOwned(lolRoot, leasePid, leaseCreation));
            return uint.MaxValue;
        }

        internal static bool OriginalGameIdentityGone(string lolRoot, int pid, long creation)
        {
            IntPtr processHandle = IntPtr.Zero;
            try
            {
                processHandle = Native.OpenProcess(4096, false, pid);
                if (processHandle == IntPtr.Zero)
                {
                    return Marshal.GetLastWin32Error() == 87;
                }
                long creation2;
                long cpu;
                ulong io;
                if (!Native.QueryProcessSample(processHandle, out creation2, out cpu, out io))
                {
                    return false;
                }
                if (creation2 != creation)
                {
                    return true;
                }
                string path = Native.ImagePath(processHandle);
                return !LolRuntimeProcesses.IsGameProcess(path, Path.GetFileName(path), lolRoot);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (processHandle != IntPtr.Zero)
                {
                    Native.CloseHandle(processHandle);
                }
            }
        }

        private static bool TryRestore(string lolRoot, int leasePid, long leaseCreation)
        {
            int restoreAttempts = 0, clearAttempts = 0;
            DateTime dateTime = DateTime.UtcNow.AddMinutes(2.0);
            DateTime addMinutes = DateTime.UtcNow.AddMinutes(30.0);
            DateTime minValue = DateTime.MinValue;
            DateTime currentMinValue = DateTime.MinValue;
            LolLcuCredentials lolLcuCredentials = null;
            int failures = 0;
            int processedCount = 0;
            bool uxRestored = false;
            long credentialGenerationStampResult = LolLcuCredentialSource.CredentialGenerationStamp(lolRoot);
            DateTime candidateMinValue = DateTime.MinValue;
            while (DateTime.UtcNow < dateTime && DateTime.UtcNow < addMinutes)
            {
                if (!LeaseStillOwned(lolRoot, leasePid, leaseCreation))
                {
                    return false;
                }
                if (!LolRuntimeProcesses.IsClientRunning(lolRoot))
                {
                    dateTime = DateTime.UtcNow.AddMinutes(2.0);
                    Thread.Sleep(1500);
                    continue;
                }
                if (DateTime.UtcNow >= candidateMinValue)
                {
                    long credentialGenerationStampResult2 = LolLcuCredentialSource.CredentialGenerationStamp(lolRoot);
                    if (credentialGenerationStampResult2 != credentialGenerationStampResult)
                    {
                        credentialGenerationStampResult = credentialGenerationStampResult2;
                        lolLcuCredentials = null;
                        failures = 0;
                        processedCount = 0;
                        minValue = DateTime.MinValue;
                        currentMinValue = DateTime.MinValue;
                    }
                    candidateMinValue = DateTime.UtcNow.AddSeconds(3.0);
                }
                if (lolLcuCredentials == null && DateTime.UtcNow >= minValue)
                {
                    lolLcuCredentials = LolLcuCredentialSource.Find(lolRoot);
                    if (lolLcuCredentials == null)
                    {
                        minValue = NextCredentialLookup(ref failures);
                    }
                    else
                    {
                        failures = 0;
                        minValue = DateTime.MaxValue;
                    }
                }
                string gameflowPhase = ((lolLcuCredentials == null) ? null : LolLcuClient.GetGameflowPhase(lolLcuCredentials));
                int pid;
                long creation;
                if (leaseCreation > 0 && LolRuntimeProcesses.TryGetGameIdentity(lolRoot, 0, out pid, out creation) && creation > leaseCreation)
                {
                    return false;
                }
                if (lolLcuCredentials != null && gameflowPhase == null)
                {
                    lolLcuCredentials = null;
                    minValue = NextCredentialLookup(ref failures);
                    dateTime = DateTime.UtcNow.AddMinutes(2.0);
                    Thread.Sleep(1500);
                    continue;
                }
                if (LolOptimizationService.IsSameMatchPhase(gameflowPhase))
                {
                    dateTime = DateTime.UtcNow.AddMinutes(2.0);
                    Thread.Sleep(6000);
                    continue;
                }
                bool uxRestoreDue = !uxRestored && lolLcuCredentials != null && DateTime.UtcNow >= currentMinValue;
                if (uxRestoreDue && ++restoreAttempts <= 3 && LolLcuClient.RestoreUx(lolLcuCredentials, lolRoot))
                {
                    uxRestored = true;
                }
                if (uxRestored)
                {
                    if (++clearAttempts > 3) return false;
                    if (LolHeadlessLease.ClearIfMatches(lolRoot, leasePid, leaseCreation) || !LeaseStillOwned(lolRoot, leasePid, leaseCreation))
                    {
                        return true;
                    }
                    Thread.Sleep(1500);
                    continue;
                }
                if (uxRestoreDue)
                {
                    if (restoreAttempts >= 3) return false;
                    if (processedCount < int.MaxValue)
                    {
                        processedCount++;
                    }
                    currentMinValue = DateTime.UtcNow.AddSeconds(LolOptimizationService.RestoreCredentialRetrySeconds(processedCount));
                }
                if (lolLcuCredentials == null)
                {
                    dateTime = DateTime.UtcNow.AddMinutes(2.0);
                }
                Thread.Sleep(1500);
            }
            return false;
        }

        private static DateTime NextCredentialLookup(ref int failures)
        {
            if (failures < int.MaxValue)
            {
                failures++;
            }
            return DateTime.UtcNow.AddSeconds(LolOptimizationService.CredentialRetrySeconds(failures));
        }

        private static bool LeaseStillOwned(string lolRoot, int leasePid, long leaseCreation)
        {
            return LolHeadlessLease.Matches(lolRoot, leasePid, leaseCreation);
        }

        private static bool SiblingPaviseProcessAlive()
        {
            string fullPath;
            string fileNameWithoutExtension;
            int id;
            try
            {
                fullPath = Path.GetFullPath(Application.ExecutablePath);
                fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
                using (Process process = Process.GetCurrentProcess())
                {
                    id = process.Id;
                }
            }
            catch
            {
                return false;
            }
            if (string.IsNullOrEmpty(fileNameWithoutExtension))
            {
                return false;
            }
            Process[] processesByName;
            try
            {
                processesByName = Process.GetProcessesByName(fileNameWithoutExtension);
            }
            catch
            {
                return false;
            }
            try
            {
                foreach (Process processe in processesByName)
                {
                    try
                    {
                        if (processe.Id == id)
                        {
                            continue;
                        }
                        IntPtr processHandle = Native.OpenProcess(4096, false, processe.Id);
                        if (processHandle == IntPtr.Zero)
                        {
                            continue;
                        }
                        try
                        {
                            string imagePathResult = Native.ImagePath(processHandle);
                            if (imagePathResult != null && string.Equals(Path.GetFullPath(imagePathResult), fullPath, StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                        finally
                        {
                            Native.CloseHandle(processHandle);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                foreach (Process currentProcess in processesByName)
                {
                    try
                    {
                        currentProcess.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
            return false;
        }

        internal static bool ExistingGuardIsReady(string lolRoot, int pid, long creation)
        {
            if (pid <= 0 || creation <= 0 || !LolHeadlessLease.Matches(lolRoot, pid, creation))
            {
                return false;
            }
            if (!GuardObjectsReady(GuardMutexName(lolRoot, pid, creation, true), AliveEventName(lolRoot, pid, creation, true)))
            {
                return GuardObjectsReady(GuardMutexName(lolRoot, pid, creation, false), AliveEventName(lolRoot, pid, creation, false));
            }
            return true;
        }

        internal static bool GuardObjectsReady(string mutexName, string aliveEventName)
        {
            EventWaitHandle eventWaitHandle = null;
            Mutex mutex = null;
            bool mutexAcquired = false;
            try
            {
                eventWaitHandle = EventWaitHandle.OpenExisting(aliveEventName);
                if (!eventWaitHandle.WaitOne(0))
                {
                    return false;
                }
                mutex = Mutex.OpenExisting(mutexName);
                try
                {
                    mutexAcquired = mutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    mutexAcquired = true;
                }
                if (!mutexAcquired)
                {
                    return true;
                }
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                }
                mutexAcquired = false;
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (mutexAcquired && mutex != null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                    }
                }
                if (mutex != null)
                {
                    try
                    {
                        mutex.Close();
                    }
                    catch
                    {
                    }
                }
                if (eventWaitHandle != null)
                {
                    try
                    {
                        eventWaitHandle.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal static string GuardMutexName(string lolRoot, int pid, long creation, bool globalNamespace)
        {
            return (globalNamespace ? "Global\\" : "") + "Pavise_LolWatchdog_" + GuardIdentity(lolRoot, pid, creation);
        }

        internal static string AliveEventName(string lolRoot, int pid, long creation, bool globalNamespace)
        {
            return (globalNamespace ? "Global\\" : "") + "Pavise_LolWatchdogAlive_" + GuardIdentity(lolRoot, pid, creation);
        }

        private static string GuardIdentity(string lolRoot, int pid, long creation)
        {
            return LolHeadlessLease.StableHash(lolRoot + "|" + pid + "|" + creation);
        }

        internal static string CreateReadyToken()
        {
            byte[] rawBytes = new byte[32];
            using (RandomNumberGenerator randomNumberGenerator = RandomNumberGenerator.Create())
            {
                randomNumberGenerator.GetBytes(rawBytes);
            }
            StringBuilder stringBuilder = new StringBuilder(rawBytes.Length * 2);
            for (int i = 0; i < rawBytes.Length; i++)
            {
                stringBuilder.Append(rawBytes[i].ToString("x2"));
            }
            return stringBuilder.ToString();
        }

        internal static bool IsReadyToken(string token)
        {
            if (token == null || token.Length != 64)
            {
                return false;
            }
            foreach (char c in token)
            {
                if ((c < '0' || c > '9') && (c < 'a' || c > 'f') && (c < 'A' || c > 'F'))
                {
                    return false;
                }
            }
            return true;
        }

        internal static string ReadyEventName(string lolRoot, int pid, long creation, string readyToken)
        {
            if (!IsReadyToken(readyToken) || pid <= 0 || creation <= 0)
            {
                return null;
            }
            return "Pavise_LolWatchdogReady_" + LolHeadlessLease.StableHash(lolRoot) + "_" + pid + "_" + creation + "_" + readyToken;
        }

        internal static bool SignalReady(string lolRoot, int pid, long creation, string readyToken)
        {
            string yEventName = ReadyEventName(lolRoot, pid, creation, readyToken);
            if (yEventName == null)
            {
                return false;
            }
            EventWaitHandle eventWaitHandle = null;
            try
            {
                eventWaitHandle = EventWaitHandle.OpenExisting(yEventName);
                return eventWaitHandle.Set();
            }
            catch
            {
                return false;
            }
            finally
            {
                if (eventWaitHandle != null)
                {
                    try
                    {
                        eventWaitHandle.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "") + "\"";
        }
    }
}
