using System;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class LolOptimizationService
    {

        private void WorkerLoop(int generation)
        {
            WaitHandle[] waitHandles = new WaitHandle[2] { stopEvent, pokeEvent };
            try
            {
                while (Volatile.Read(ref runGeneration) == generation && !stopEvent.WaitOne(0))
                {
                    try
                    {
                        RunCycle();
                    }
                    catch
                    {
                        SetError(Lang.T("lol.err.cycle"));
                    }
                    if (Volatile.Read(ref runGeneration) != generation)
                    {
                        break;
                    }
                    bool serviceEnabled;
                    bool cleanupEnabledSnapshot;
                    bool headlessEnabledSnapshot;
                    bool clientRunningSnapshot;
                    bool gameRunningSnapshot;
                    bool headlessActiveSnapshot;
                    bool recoveryMarked;
                    lock (stateLock)
                    {
                        serviceEnabled = enabled;
                        cleanupEnabledSnapshot = cleanupEnabled;
                        headlessEnabledSnapshot = headlessEnabled;
                        clientRunningSnapshot = clientRunning;
                        gameRunningSnapshot = gameRunning;
                        headlessActiveSnapshot = headlessActive;
                        recoveryMarked = !string.IsNullOrEmpty(headlessLeaseRoot);
                    }
                    int millisecondsTimeout = WorkerDelayMs(serviceEnabled, cleanupEnabledSnapshot, headlessEnabledSnapshot, clientRunningSnapshot, gameRunningSnapshot, headlessActiveSnapshot, recoveryMarked);
                    if (WaitHandle.WaitAny(waitHandles, millisecondsTimeout) == 0)
                    {
                        break;
                    }
                }
            }
            finally
            {
                bool disposedSnapshot;
                lock (stateLock)
                {
                    if (object.ReferenceEquals(worker, Thread.CurrentThread))
                    {
                        worker = null;
                    }
                    disposedSnapshot = disposed;
                }
                if (disposedSnapshot)
                {
                    DisposeWaitHandles();
                }
            }
        }

        internal static int WorkerDelayMs(bool serviceEnabled, bool cleanupEnabled, bool headlessEnabled, bool clientRunning, bool gameRunning, bool headlessActive, bool recoveryMarked)
        {
            if (headlessActive || recoveryMarked)
            {
                return 20000;
            }
            if (!serviceEnabled)
            {
                return 30000;
            }
            if (!clientRunning && !gameRunning)
            {
                return 15000;
            }
            if (headlessEnabled)
            {
                if (!gameRunning)
                {
                    return 6000;
                }
                return 5000;
            }
            if (cleanupEnabled)
            {
                return 10000;
            }
            return 30000;
        }

        private void RunCycle()
        {
            lock (actionLock)
            {
                string root;
                string weGame;
                if (CycleGate() && LocateInstallation(out root, out weGame) && !RuntimeStopped())
                {
                    LolProcessSnapshot processes = ScanProcesses(root, weGame);
                    string currentPhase;
                    bool ready;
                    LolLcuCredentials credentials = ProbeLcu(root, processes.ClientRunning, out currentPhase, out ready);
                    bool inProgress = string.Equals(currentPhase, "InProgress", StringComparison.OrdinalIgnoreCase);
                    bool sameMatch = IsSameMatchPhase(currentPhase);
                    bool serviceEnabled;
                    bool cleanEnabled;
                    bool noUxEnabled;
                    bool manualBypass;
                    bool clientExitConfirmed;
                    bool matchExitConfirmed;
                    bool cleanupMatch;
                    bool active;
                    AdoptRuntimeState(processes, currentPhase, ready, sameMatch, out serviceEnabled, out cleanEnabled, out noUxEnabled, out active, out manualBypass, out clientExitConfirmed, out matchExitConfirmed, out cleanupMatch);
                    active = AdoptHeadlessLease(root, processes, active, sameMatch);
                    active = ReleaseOnClientExit(active, clientExitConfirmed);
                    HandleUxRespawn(credentials, root, weGame, serviceEnabled, noUxEnabled, inProgress, ref active, ref manualBypass, ref processes);
                    RunRestoreStage(credentials, root, weGame, serviceEnabled, noUxEnabled, manualBypass, active, ready, matchExitConfirmed, ref processes);
                    RunCleanupStage(credentials, root, weGame, serviceEnabled, cleanEnabled, currentPhase, cleanupMatch, inProgress, ref ready, ref processes);
                    RunHeadlessKillStage(credentials, root, weGame, serviceEnabled, noUxEnabled, ready, inProgress, ref processes);
                    UpdateProcessState(processes);
                    RaiseChanged();
                }
            }
        }

        private bool CycleGate()
        {
            if (Interlocked.Exchange(ref credentialGenerationChanged, 0) != 0)
            {
                InvalidateCredentials();
            }
            bool workEnabled;
            bool headlessActiveSnapshot;
            lock (stateLock)
            {
                workEnabled = enabled && (cleanupEnabled || headlessEnabled);
                headlessActiveSnapshot = headlessActive;
            }
            bool hasHeadlessMark = HasHeadlessMark();
            if (!hasHeadlessMark)
            {
                ForgetRememberedHeadlessLease();
            }
            if (!workEnabled && !headlessActiveSnapshot && !hasHeadlessMark)
            {
                GoIdle();
                return false;
            }
            return true;
        }

        private bool LocateInstallation(out string root, out string weGame)
        {
            Discover(out root, out weGame, false, true);
            if (RuntimeStopped())
            {
                return false;
            }
            if (root == null)
            {
                lock (stateLock)
                {
                    clientRunning = false;
                    gameRunning = false;
                    lcuReady = false;
                    phase = "";
                    weGameProcessCount = 0;
                    weGameMainRunning = false;
                    crossProcessCount = 0;
                    uxProcessCount = 0;
                    lastAction = Lang.T("lol.act.waitinstall");
                    updatedUtc = DateTime.UtcNow;
                }
                RaiseChanged();
                return false;
            }
            RememberHeadlessLease(root);
            bool headlessLeaseReleasePendingSnapshot;
            lock (stateLock)
            {
                headlessLeaseReleasePendingSnapshot = headlessLeaseReleasePending;
            }
            if (headlessLeaseReleasePendingSnapshot)
            {
                ClearOwnedHeadlessMark();
            }
            return true;
        }

        private LolProcessSnapshot ScanProcesses(string root, string weGame)
        {
            bool useCachedProcesses;
            lock (stateLock)
            {
                useCachedProcesses = DateTime.UtcNow < nextFullProcessScanUtc;
                if (!useCachedProcesses)
                {
                    nextFullProcessScanUtc = DateTime.UtcNow.AddSeconds(60.0);
                }
            }
            return LolRuntimeProcesses.Scan(root, weGame, useCachedProcesses);
        }

        private void AdoptRuntimeState(LolProcessSnapshot processes, string currentPhase, bool ready, bool sameMatch, out bool serviceEnabled, out bool cleanEnabled, out bool noUxEnabled, out bool active, out bool manualBypass, out bool clientExitConfirmed, out bool matchExitConfirmed, out bool cleanupMatch)
        {
            lock (stateLock)
            {
                clientExitConfirmed = ConfirmClientExit(processes.ScanSucceeded && !processes.CoreIdentityIndeterminate, processes.ClientRunning, processes.GameRunning, ref clientAbsentSamples);
            }
            matchExitConfirmed = clientExitConfirmed || (!processes.GameRunning && IsConfirmedMatchExitPhase(currentPhase));
            lock (stateLock)
            {
                if (ShouldResetMatchSafetyState(clientExitConfirmed, matchExitConfirmed, cleanupMatchActive))
                {
                    ResetMatchSafetyStateLocked();
                }
                else if (sameMatch && processes.GameRunning && !cleanupMatchActive)
                {
                    cleanupMatchActive = true;
                    cleanupDirty = true;
                    nextCleanupUtc = DateTime.MinValue;
                }
                serviceEnabled = enabled;
                cleanEnabled = cleanupEnabled;
                noUxEnabled = headlessEnabled;
                active = headlessActive;
                manualBypass = manualUxBypassGame;
                clientRunning = processes.ClientRunning;
                gameRunning = processes.GameRunning;
                lcuReady = ready;
                phase = currentPhase ?? "";
                weGameProcessCount = processes.WeGameProcessCount;
                weGameMainRunning = processes.WeGameMainRunning;
                crossProcessCount = processes.CrossProcessCount;
                uxProcessCount = processes.UxProcessCount;
                cleanupMatch = cleanupMatchActive;
                updatedUtc = DateTime.UtcNow;
            }
        }

        private void HandleUxRespawn(LolLcuCredentials credentials, string root, string weGame, bool serviceEnabled, bool noUxEnabled, bool inProgress, ref bool active, ref bool manualBypass, ref LolProcessSnapshot processes)
        {
            bool respawnLimitReached = false;
            if (active && serviceEnabled && noUxEnabled && !manualBypass && inProgress && processes.MainUxProcessCount > 0)
            {
                lock (stateLock)
                {
                    headlessActive = false;
                    headlessExitSamples = 0;
                    nextUxKillUtc = DateTime.UtcNow.AddSeconds(10.0);
                    respawnLimitReached = RegisterUxRespawn(DateTime.UtcNow, uxRespawns);
                    if (respawnLimitReached)
                    {
                        manualUxBypassGame = true;
                    }
                }
                active = false;
            }
            if (respawnLimitReached)
            {
                bool restoreCoreSucceeded = RestoreCore(credentials, root, true);
                lock (stateLock)
                {
                    headlessActive = !restoreCoreSucceeded;
                    headlessExitSamples = 0;
                    manualUxBypassGame = true;
                    active = !restoreCoreSucceeded;
                    manualBypass = true;
                    lastAction = Lang.T("lol.act.uxcircuit");
                    lastError = (restoreCoreSucceeded ? "" : Lang.T("lol.err.uxcircuitrestore"));
                    lastActionUtc = DateTime.UtcNow;
                    updatedUtc = DateTime.UtcNow;
                }
                processes = LolRuntimeProcesses.Scan(root, weGame, true);
            }
        }

        private void RunRestoreStage(LolLcuCredentials credentials, string root, string weGame, bool serviceEnabled, bool noUxEnabled, bool manualBypass, bool active, bool ready, bool matchExitConfirmed, ref LolProcessSnapshot processes)
        {
            if (matchExitConfirmed) ObserveHeadlessOwnerExit();
            bool exitConfirmed = false;
            if (active)
            {
                lock (stateLock)
                {
                    if (!serviceEnabled || !noUxEnabled || manualBypass)
                    {
                        headlessExitSamples = 0;
                        exitConfirmed = true;
                    }
                    else if (ready && matchExitConfirmed)
                    {
                        if (headlessExitSamples < int.MaxValue)
                        {
                            headlessExitSamples++;
                        }
                        exitConfirmed = headlessExitSamples >= 2;
                    }
                    else
                    {
                        headlessExitSamples = 0;
                    }
                }
            }
            if (exitConfirmed)
            {
                RestoreCore(credentials, root);
                processes = LolRuntimeProcesses.Scan(root, weGame, true);
            }
            else if (!active && processes.ClientRunning && processes.MainUxProcessCount == 0 && credentials != null && HasHeadlessMark(root) && (!serviceEnabled || !noUxEnabled || matchExitConfirmed))
            {
                RestoreCore(credentials, root);
                processes = LolRuntimeProcesses.Scan(root, weGame, true);
            }
            else if (processes.MainUxProcessCount > 0 && !active && matchExitConfirmed)
            {
                ClearOwnedHeadlessMark();
            }
        }

        private void RunCleanupStage(LolLcuCredentials credentials, string root, string weGame, bool serviceEnabled, bool cleanEnabled, string currentPhase, bool cleanupMatch, bool inProgress, ref bool ready, ref LolProcessSnapshot processes)
        {
            bool isVerifiedCleanupSession = IsVerifiedCleanupSession(processes.ClientRunning, ready, currentPhase, processes.GameRunning, cleanupMatch);
            DateTime utcNow = DateTime.UtcNow;
            bool cleanupDue;
            bool cleanupCircuitOpenSnapshot;
            lock (stateLock)
            {
                cleanupDue = utcNow >= nextCleanupUtc || (cleanupDirty && !cleanupBurstActive);
                cleanupCircuitOpenSnapshot = cleanupCircuitOpen;
            }
            if (!serviceEnabled || !cleanEnabled || !isVerifiedCleanupSession || cleanupCircuitOpenSnapshot || !cleanupDue)
            {
                return;
            }
            lock (stateLock)
            {
                cleanupDirty = false;
                if (!cleanupBurstActive)
                {
                    cleanupBurstActive = true;
                    cleanupFollowUpsRemaining = 2;
                }
                else if (cleanupFollowUpsRemaining > 0)
                {
                    cleanupFollowUpsRemaining--;
                }
            }
            LolCleanupResult lolCleanupResult = LolRuntimeProcesses.Clean(root, weGame);
            bool sessionAlive = lolCleanupResult.Count == 0 || LolLcuClient.IsReady(credentials);
            DateTime utcNow2 = DateTime.UtcNow;
            bool cleanupLimitReached = false;
            lock (stateLock)
            {
                cleanedProcessCount += lolCleanupResult.Count;
                releasedWorkingSetBytes += lolCleanupResult.WorkingSetBytes;
                lcuReady = sessionAlive;
                if (lolCleanupResult.Count > 0)
                {
                    cleanupLimitReached = RegisterCleanupKillCycle(utcNow2, cleanupKillCycles) || !sessionAlive;
                    if (cleanupLimitReached)
                    {
                        cleanupCircuitOpen = true;
                    }
                }
                if (cleanupLimitReached)
                {
                    cleanupDirty = false;
                    cleanupBurstActive = false;
                    cleanupFollowUpsRemaining = 0;
                    nextCleanupUtc = DateTime.MaxValue;
                    lastAction = Lang.T("lol.act.cleanupcircuit");
                    lastError = (sessionAlive ? "" : Lang.T("lol.err.sessionlost"));
                    lastActionUtc = utcNow2;
                }
                else if (!sessionAlive)
                {
                    lastAction = Lang.T("lol.act.recovering");
                    lastError = Lang.T("lol.err.sessionlost");
                    lastActionUtc = utcNow2;
                }
                else if (lolCleanupResult.Count > 0)
                {
                    lastAction = Lang.F("lol.act.cleaned", lolCleanupResult.Count.ToString());
                    lastError = "";
                    lastActionUtc = utcNow2;
                    Logger.Log("英雄联盟专栏：自动精准净化 " + lolCleanupResult.Count + " 个附加进程");
                }
                if (!cleanupLimitReached)
                {
                    if ((lolCleanupResult.Count > 0 && cleanupFollowUpsRemaining > 0) || cleanupDirty)
                    {
                        cleanupBurstActive = true;
                        nextCleanupUtc = utcNow2.AddSeconds(10.0);
                    }
                    else
                    {
                        cleanupBurstActive = false;
                        cleanupFollowUpsRemaining = 0;
                        nextCleanupUtc = utcNow2.AddSeconds(120.0);
                    }
                }
                updatedUtc = utcNow2;
            }
            ready = sessionAlive;
            if (lolCleanupResult.Count > 0)
            {
                processes = LolRuntimeProcesses.Scan(root, weGame, true);
            }
            if (!sessionAlive && lolCleanupResult.Count > 0)
            {
                InvalidateCredentials(false);
                ScheduleCredentialRetry(root);
                TryRecoverLoginChain(weGame, processes, inProgress, true);
            }
        }

    }
}
