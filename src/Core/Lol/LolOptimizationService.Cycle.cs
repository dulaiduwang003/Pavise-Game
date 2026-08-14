// @author bdth 2074055628@qq.com
// 文件用途 英雄联盟优化服务的工作循环与恢复核心

using System;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class LolOptimizationService
    {
        private void WorkerLoop(int generation)
        {
            WaitHandle[] waitHandles = new WaitHandle[] { stopEvent, pokeEvent };
            try
            {
                while (Volatile.Read(ref runGeneration) == generation && !stopEvent.WaitOne(0))
                {
                    try { RunCycle(); }
                    catch
                    {
                        SetError(Lang.T("lol.err.cycle"));
                    }
                    if (Volatile.Read(ref runGeneration) != generation) break;
                    int delay;
                    bool enabled1;
                    bool cleanup1;
                    bool headless1;
                    bool client1;
                    bool game1;
                    bool active1;
                    bool marked;
                    lock (stateLock)
                    {
                        enabled1 = enabled;
                        cleanup1 = cleanupEnabled;
                        headless1 = headlessEnabled;
                        client1 = clientRunning;
                        game1 = gameRunning;
                        active1 = headlessActive;
                        marked = !string.IsNullOrEmpty(headlessLeaseRoot);
                    }
                    delay = WorkerDelayMs(
                        enabled1,
                        cleanup1,
                        headless1,
                        client1,
                        game1,
                        active1,
                        marked);
                    int signaled = WaitHandle.WaitAny(waitHandles, delay);
                    if (signaled == 0) break;
                }
            }
            finally
            {
                bool disposeHandles;
                lock (stateLock)
                {
                    if (ReferenceEquals(worker, Thread.CurrentThread)) worker = null;
                    disposeHandles = disposed;
                }
                if (disposeHandles) DisposeWaitHandles();
            }
        }

        internal static int WorkerDelayMs(
            bool serviceEnabled,
            bool cleanupEnabled,
            bool headlessEnabled,
            bool clientRunning,
            bool gameRunning,
            bool headlessActive,
            bool recoveryMarked)
        {
            if (headlessActive || recoveryMarked)
                return HeadlessStableCycleMs;
            if (!serviceEnabled) return DisabledCycleMs;
            if (!clientRunning && !gameRunning) return DormantCycleMs;
            if (headlessEnabled)
                return gameRunning ? TransitionCycleMs : ClientCycleMs;
            if (cleanupEnabled) return CleanupOnlyCycleMs;
            return DisabledCycleMs;
        }

        private void RunCycle()
        {
            lock (actionLock)
            {
                if (!CycleGate()) return;
                string root;
                string weGame;
                if (!LocateInstallation(out root, out weGame)) return;
                LolProcessSnapshot processes = ScanProcesses(root, weGame);
                string currentPhase;
                bool ready;
                LolLcuCredentials credentials = ProbeLcu(
                    root, processes.ClientRunning, out currentPhase, out ready);
                bool inProgress = string.Equals(
                    currentPhase, "InProgress", StringComparison.OrdinalIgnoreCase);
                bool sameMatch = IsSameMatchPhase(currentPhase);
                bool serviceEnabled;
                bool cleanEnabled;
                bool noUxEnabled;
                bool active;
                bool manualBypass;
                bool clientExitConfirmed;
                bool matchExitConfirmed;
                bool cleanupMatch;
                AdoptRuntimeState(
                    processes,
                    currentPhase,
                    ready,
                    sameMatch,
                    out serviceEnabled,
                    out cleanEnabled,
                    out noUxEnabled,
                    out active,
                    out manualBypass,
                    out clientExitConfirmed,
                    out matchExitConfirmed,
                    out cleanupMatch);
                active = AdoptHeadlessLease(root, processes, active, sameMatch);
                active = ReleaseOnClientExit(active, clientExitConfirmed);
                HandleUxRespawn(
                    credentials, root, weGame,
                    serviceEnabled, noUxEnabled, inProgress,
                    ref active, ref manualBypass, ref processes);
                RunRestoreStage(
                    credentials, root, weGame,
                    serviceEnabled, noUxEnabled, manualBypass,
                    active, ready, matchExitConfirmed, ref processes);
                RunCleanupStage(
                    credentials, root, weGame,
                    serviceEnabled, cleanEnabled,
                    currentPhase, cleanupMatch, inProgress,
                    ref ready, ref processes);
                RunHeadlessKillStage(
                    credentials, root, weGame,
                    serviceEnabled, noUxEnabled, ready, inProgress,
                    ref processes);
                UpdateProcessState(processes);
                RaiseChanged();
            }
        }

        private bool CycleGate()
        {
            if (Interlocked.Exchange(
                    ref credentialGenerationChanged, 0) != 0)
                InvalidateCredentials();
            bool active0;
            bool enabled0;
            lock (stateLock)
            {
                enabled0 = enabled && (cleanupEnabled || headlessEnabled);
                active0 = headlessActive;
            }
            bool recoveryMarked = HasHeadlessMark();
            if (!recoveryMarked) ForgetRememberedHeadlessLease();
            if (!enabled0 && !active0 && !recoveryMarked)
            {
                GoIdle();
                return false;
            }
            return true;
        }

        private bool LocateInstallation(out string root, out string weGame)
        {
            Discover(out root, out weGame, false);
            if (root == null)
            {
                lock (stateLock)
                {
                    clientRunning = false;
                    gameRunning = false;
                    lcuReady = false;
                    phase = "";
                    weGameProcessCount = 0;
                    crossProcessCount = 0;
                    uxProcessCount = 0;
                    lastAction = Lang.T("lol.act.waitinstall");
                    updatedUtc = DateTime.UtcNow;
                }
                RaiseChanged();
                return false;
            }
            RememberHeadlessLease(root);
            bool releasePending;
            lock (stateLock)
                releasePending = headlessLeaseReleasePending;
            if (releasePending) ClearOwnedHeadlessMark();
            return true;
        }

        private LolProcessSnapshot ScanProcesses(string root, string weGame)
        {
            bool lightScan;
            lock (stateLock)
            {
                lightScan = DateTime.UtcNow < nextFullProcessScanUtc;
                if (!lightScan)
                    nextFullProcessScanUtc = DateTime.UtcNow.AddSeconds(
                        FullProcessScanSeconds);
            }
            return LolRuntimeProcesses.Scan(root, weGame, lightScan);
        }

        private void AdoptRuntimeState(
            LolProcessSnapshot processes,
            string currentPhase,
            bool ready,
            bool sameMatch,
            out bool serviceEnabled,
            out bool cleanEnabled,
            out bool noUxEnabled,
            out bool active,
            out bool manualBypass,
            out bool clientExitConfirmed,
            out bool matchExitConfirmed,
            out bool cleanupMatch)
        {
            lock (stateLock)
            {
                clientExitConfirmed = ConfirmClientExit(
                    processes.ScanSucceeded
                        && !processes.CoreIdentityIndeterminate,
                    processes.ClientRunning,
                    processes.GameRunning,
                    ref clientAbsentSamples);
            }
            matchExitConfirmed = clientExitConfirmed
                || (!processes.GameRunning
                    && IsConfirmedMatchExitPhase(currentPhase));
            lock (stateLock)
            {
                if (ShouldResetMatchSafetyState(
                        clientExitConfirmed,
                        matchExitConfirmed,
                        cleanupMatchActive))
                {
                    ResetMatchSafetyStateLocked();
                }
                else if (sameMatch && processes.GameRunning
                    && !cleanupMatchActive)
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
                crossProcessCount = processes.CrossProcessCount;
                uxProcessCount = processes.UxProcessCount;
                cleanupMatch = cleanupMatchActive;
                updatedUtc = DateTime.UtcNow;
            }
        }

        private bool AdoptHeadlessLease(
            string root,
            LolProcessSnapshot processes,
            bool active,
            bool sameMatch)
        {
            if (!active && sameMatch && processes.GameRunning)
            {
                int leasedGamePid;
                long leasedGameCreation;
                if (LolRuntimeProcesses.TryGetGameIdentity(
                        root,
                        processes.GameProcessId,
                        out leasedGamePid,
                        out leasedGameCreation)
                    && LolHeadlessLease.Matches(
                        root, leasedGamePid, leasedGameCreation))
                {
                    lock (stateLock)
                    {
                        headlessActive = true;
                        active = true;
                    }
                }
            }
            return active;
        }

        private bool ReleaseOnClientExit(bool active, bool clientExitConfirmed)
        {
            if (active && clientExitConfirmed)
            {
                lock (stateLock)
                {
                    headlessActive = false;
                    headlessExitSamples = 0;
                    ResetMatchSafetyStateLocked();
                }
                ClearOwnedHeadlessMark();
                active = false;
            }
            else if (clientExitConfirmed)
            {
                lock (stateLock)
                {
                    ResetMatchSafetyStateLocked();
                }
                ClearOwnedHeadlessMark();
            }
            return active;
        }

        private void HandleUxRespawn(
            LolLcuCredentials credentials,
            string root,
            string weGame,
            bool serviceEnabled,
            bool noUxEnabled,
            bool inProgress,
            ref bool active,
            ref bool manualBypass,
            ref LolProcessSnapshot processes)
        {
            bool uxRespawnCircuitTripped = false;
            if (active && serviceEnabled && noUxEnabled && !manualBypass
                && inProgress && processes.MainUxProcessCount > 0)
            {
                lock (stateLock)
                {
                    headlessActive = false;
                    headlessExitSamples = 0;
                    nextUxKillUtc = DateTime.UtcNow.AddSeconds(UxRespawnBackoffSeconds);
                    uxRespawnCircuitTripped = RegisterUxRespawn(
                        DateTime.UtcNow, uxRespawns);
                    if (uxRespawnCircuitTripped)
                        manualUxBypassGame = true;
                }
                active = false;
            }

            if (uxRespawnCircuitTripped)
            {
                bool restoredAfterRespawns = RestoreCore(
                    credentials, root, true);
                lock (stateLock)
                {
                    headlessActive = !restoredAfterRespawns;
                    headlessExitSamples = 0;
                    manualUxBypassGame = true;
                    active = !restoredAfterRespawns;
                    manualBypass = true;
                    lastAction = Lang.T("lol.act.uxcircuit");
                    lastError = restoredAfterRespawns
                        ? "" : Lang.T("lol.err.uxcircuitrestore");
                    lastActionUtc = DateTime.UtcNow;
                    updatedUtc = DateTime.UtcNow;
                }
                processes = LolRuntimeProcesses.Scan(root, weGame, true);
            }
        }

        private void RunRestoreStage(
            LolLcuCredentials credentials,
            string root,
            string weGame,
            bool serviceEnabled,
            bool noUxEnabled,
            bool manualBypass,
            bool active,
            bool ready,
            bool matchExitConfirmed,
            ref LolProcessSnapshot processes)
        {
            bool restoreActive = false;
            if (active)
            {
                lock (stateLock)
                {
                    if (!serviceEnabled || !noUxEnabled || manualBypass)
                    {
                        headlessExitSamples = 0;
                        restoreActive = true;
                    }
                    else if (ready && matchExitConfirmed)
                    {
                        if (headlessExitSamples < int.MaxValue) headlessExitSamples++;
                        restoreActive = headlessExitSamples >= 2;
                    }
                    else
                    {
                        headlessExitSamples = 0;
                    }
                }
            }

            if (restoreActive)
            {
                RestoreCore(credentials, root);
                processes = LolRuntimeProcesses.Scan(root, weGame, true);
            }
            else if (!active && processes.ClientRunning && processes.MainUxProcessCount == 0
                && credentials != null && HasHeadlessMark(root)
                && (!serviceEnabled || !noUxEnabled
                    || matchExitConfirmed))
            {
                RestoreCore(credentials, root);
                processes = LolRuntimeProcesses.Scan(root, weGame, true);
            }
            else if (processes.MainUxProcessCount > 0 && !active
                && matchExitConfirmed)
            {
                ClearOwnedHeadlessMark();
            }
        }

        private void RunCleanupStage(
            LolLcuCredentials credentials,
            string root,
            string weGame,
            bool serviceEnabled,
            bool cleanEnabled,
            string currentPhase,
            bool cleanupMatch,
            bool inProgress,
            ref bool ready,
            ref LolProcessSnapshot processes)
        {
            bool cleanupSessionActive = IsVerifiedCleanupSession(
                processes.ClientRunning,
                ready,
                currentPhase,
                processes.GameRunning,
                cleanupMatch);
            DateTime cleanupNow = DateTime.UtcNow;
            bool cleanupPending;
            bool cleanupSuspended;
            lock (stateLock)
            {
                cleanupPending = cleanupNow >= nextCleanupUtc
                    || (cleanupDirty && !cleanupBurstActive);
                cleanupSuspended = cleanupCircuitOpen;
            }
            if (serviceEnabled && cleanEnabled && cleanupSessionActive
                && !cleanupSuspended && cleanupPending)
            {
                lock (stateLock)
                {
                    cleanupDirty = false;
                    if (!cleanupBurstActive)
                    {
                        cleanupBurstActive = true;
                        cleanupFollowUpsRemaining = CleanupFollowUpLimit;
                    }
                    else if (cleanupFollowUpsRemaining > 0)
                    {
                        cleanupFollowUpsRemaining--;
                    }
                }

                LolCleanupResult clean =
                    LolRuntimeProcesses.Clean(root, weGame);
                bool healthy = clean.Count == 0
                    || LolLcuClient.IsReady(credentials);
                DateTime cleanupCompletedUtc = DateTime.UtcNow;
                bool circuitTripped = false;
                lock (stateLock)
                {
                    cleanedProcessCount += clean.Count;
                    releasedWorkingSetBytes += clean.WorkingSetBytes;
                    lcuReady = healthy;
                    if (clean.Count > 0)
                    {
                        circuitTripped = RegisterCleanupKillCycle(
                            cleanupCompletedUtc, cleanupKillCycles)
                            || !healthy;
                        if (circuitTripped) cleanupCircuitOpen = true;
                    }
                    if (circuitTripped)
                    {
                        cleanupDirty = false;
                        cleanupBurstActive = false;
                        cleanupFollowUpsRemaining = 0;
                        nextCleanupUtc = DateTime.MaxValue;
                        lastAction = Lang.T("lol.act.cleanupcircuit");
                        lastError = healthy
                            ? "" : Lang.T("lol.err.sessionlost");
                        lastActionUtc = cleanupCompletedUtc;
                    }
                    else if (!healthy)
                    {
                        lastAction = Lang.T("lol.act.recovering");
                        lastError = Lang.T("lol.err.sessionlost");
                        lastActionUtc = cleanupCompletedUtc;
                    }
                    else if (clean.Count > 0)
                    {
                        lastAction = Lang.F("lol.act.cleaned", clean.Count.ToString());
                        lastError = "";
                        lastActionUtc = cleanupCompletedUtc;
                        Logger.Log("英雄联盟专栏：自动精准净化 " + clean.Count + " 个附加进程");
                    }
                    if (!circuitTripped)
                    {
                        if ((clean.Count > 0
                                && cleanupFollowUpsRemaining > 0)
                            || cleanupDirty)
                        {
                            cleanupBurstActive = true;
                            nextCleanupUtc = cleanupCompletedUtc.AddSeconds(
                                CleanupFollowUpSeconds);
                        }
                        else
                        {
                            cleanupBurstActive = false;
                            cleanupFollowUpsRemaining = 0;
                            nextCleanupUtc = cleanupCompletedUtc.AddSeconds(
                                CleanupFallbackSeconds);
                        }
                    }
                    updatedUtc = cleanupCompletedUtc;
                }
                ready = healthy;
                if (clean.Count > 0)
                    processes = LolRuntimeProcesses.Scan(root, weGame, true);
                if (!healthy && clean.Count > 0)
                {
                    InvalidateCredentials(false);
                    ScheduleCredentialRetry(root);
                    TryRecoverLoginChain(
                        weGame, processes, inProgress, true);
                }
            }
        }

        private void RunHeadlessKillStage(
            LolLcuCredentials credentials,
            string root,
            string weGame,
            bool serviceEnabled,
            bool noUxEnabled,
            bool ready,
            bool inProgress,
            ref LolProcessSnapshot processes)
        {
            if (serviceEnabled && noUxEnabled && ready
                && inProgress)
            {
                bool shouldKill;
                lock (stateLock) shouldKill = !headlessActive && !manualUxBypassGame
                    && processes.MainUxProcessCount > 0
                    && DateTime.UtcNow >= nextUxKillUtc;
                if (shouldKill)
                {
                    lock (stateLock)
                        nextUxKillUtc = DateTime.UtcNow.AddSeconds(UxRespawnBackoffSeconds);
                    string freshPhase = LolLcuClient.GetGameflowPhase(credentials);
                    if (!string.Equals(freshPhase, "InProgress", StringComparison.OrdinalIgnoreCase))
                        return;
                    int gamePid;
                    long gameCreation;
                    bool identityFound = LolRuntimeProcesses.TryGetGameIdentity(
                        root, processes.GameProcessId, out gamePid, out gameCreation);
                    bool leaseWritten = identityFound
                        && SetHeadlessMark(root, gamePid, gameCreation);
                    bool watchdogReady = leaseWritten
                        && LolWatchdog.StartDetached(
                            root, gamePid, gameCreation);
                    bool killed = watchdogReady
                        && LolLcuClient.KillUx(credentials, root);
                    if (leaseWritten && !killed)
                        ClearOwnedHeadlessMark();
                    lock (stateLock)
                    {
                        if (killed)
                        {
                            headlessActive = true;
                            headlessExitSamples = 0;
                            lastAction = Lang.T("lol.act.headless");
                            lastError = "";
                            lastActionUtc = DateTime.UtcNow;
                        }
                        else
                        {
                            lastError = !identityFound
                                ? Lang.T("lol.err.gameidentity")
                                : (!leaseWritten
                                    ? Lang.T("lol.err.lease")
                                    : (!watchdogReady
                                        ? Lang.T("lol.err.watchdog")
                                        : Lang.T("lol.err.killux")));
                            lastActionUtc = DateTime.UtcNow;
                        }
                        updatedUtc = DateTime.UtcNow;
                    }
                    if (killed)
                    {
                        Logger.Log("英雄联盟专栏：独立恢复器已确认守护，对局真无头已启用");
                        Thread.Sleep(400);
                        processes = LolRuntimeProcesses.Scan(root, weGame, true);
                    }
                }
            }
        }

        private bool RestoreCore(
            LolLcuCredentials credentials, string root)
        {
            return RestoreCore(credentials, root, false);
        }

        private bool RestoreCore(
            LolLcuCredentials credentials, string root, bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && now < nextRestoreCredentialLookupUtc)
                return false;
            if (credentials == null)
                credentials = GetCredentialsForRestore(root, force);
            bool restored = credentials != null && LolLcuClient.RestoreUx(credentials, root);
            lock (stateLock)
            {
                string previousAction = lastAction;
                string previousError = lastError;
                if (restored)
                {
                    headlessActive = false;
                    headlessExitSamples = 0;
                    lastAction = Lang.T("lol.act.restored");
                    lastError = "";
                    credentialLookupFailures = 0;
                    restoreCredentialLookupFailures = 0;
                    nextRestoreCredentialLookupUtc = DateTime.MinValue;
                }
                else
                {
                    lastError = Lang.T("lol.err.restorepending");
                    if (credentials != null)
                        ScheduleRestoreCredentialRetry();
                }
                if (!string.Equals(previousAction, lastAction, StringComparison.Ordinal)
                    || !string.Equals(previousError, lastError, StringComparison.Ordinal))
                    lastActionUtc = DateTime.UtcNow;
                updatedUtc = DateTime.UtcNow;
            }
            if (restored)
            {
                ClearOwnedHeadlessMark();
                Logger.Log("英雄联盟专栏：大厅界面已自动恢复");
            }
            return restored;
        }
    }
}
