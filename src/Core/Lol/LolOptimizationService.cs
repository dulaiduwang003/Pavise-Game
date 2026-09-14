// @author bdth 2074055628@qq.com
// File purpose State fields and control entry points of the League of Legends optimization service

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal sealed class LolOptimizationSnapshot
    {
        public bool Running { get; private set; }
        public bool Enabled { get; private set; }
        public bool CleanupEnabled { get; private set; }
        public bool HeadlessEnabled { get; private set; }
        public string LolRoot { get; private set; }
        public string WeGameRoot { get; private set; }
        public bool InstallationFound { get; private set; }
        public bool WeGameFound { get; private set; }
        public bool ClientRunning { get; private set; }
        public bool GameRunning { get; private set; }
        public bool LcuReady { get; private set; }
        public string Phase { get; private set; }
        public bool HeadlessActive { get; private set; }
        public int WeGameProcessCount { get; private set; }
        public bool WeGameMainRunning { get; internal set; }
        public int CrossProcessCount { get; private set; }
        public int UxProcessCount { get; private set; }
        public int CleanedProcessCount { get; private set; }
        public long ReleasedWorkingSetBytes { get; private set; }
        public string LastAction { get; private set; }
        public string LastError { get; private set; }
        public DateTime UpdatedUtc { get; private set; }
        public DateTime LastActionUtc { get; private set; }
        public bool Discovering { get; private set; }

        internal LolOptimizationSnapshot(
            bool running,
            bool enabled,
            bool cleanupEnabled,
            bool headlessEnabled,
            string lolRoot,
            string weGameRoot,
            bool installationFound,
            bool weGameFound,
            bool clientRunning,
            bool gameRunning,
            bool lcuReady,
            string phase,
            bool headlessActive,
            int weGameProcessCount,
            int crossProcessCount,
            int uxProcessCount,
            int cleanedProcessCount,
            long releasedWorkingSetBytes,
            string lastAction,
            string lastError,
            DateTime updatedUtc,
            DateTime lastActionUtc,
            bool discovering)
        {
            Discovering = discovering;
            Running = running;
            Enabled = enabled;
            CleanupEnabled = cleanupEnabled;
            HeadlessEnabled = headlessEnabled;
            LolRoot = lolRoot;
            WeGameRoot = weGameRoot;
            InstallationFound = installationFound;
            WeGameFound = weGameFound;
            ClientRunning = clientRunning;
            GameRunning = gameRunning;
            LcuReady = lcuReady;
            Phase = phase;
            HeadlessActive = headlessActive;
            WeGameProcessCount = weGameProcessCount;
            CrossProcessCount = crossProcessCount;
            UxProcessCount = uxProcessCount;
            CleanedProcessCount = cleanedProcessCount;
            ReleasedWorkingSetBytes = releasedWorkingSetBytes;
            LastAction = lastAction;
            LastError = lastError;
            UpdatedUtc = updatedUtc;
            LastActionUtc = lastActionUtc;
        }
    }

    internal sealed partial class LolOptimizationService : IDisposable
    {
        private const string EnabledKey = "LolColumnEnabled";
        private const string CleanupEnabledKey = "LolColumnCleanupEnabled";
        private const string HeadlessEnabledKey = "LolColumnHeadlessEnabled";
        private const string LolRootKey = "LolInstallPath";
        private const string WeGameRootKey = "LolWeGamePath";
        private const int DiscoveryBaseSeconds = 15;
        private const int DiscoveryMaxSeconds = 600;
        private const int DiscoverySettledSeconds = 120;
        private const int DisabledCycleMs = 30000;
        private const int DormantCycleMs = 15000;
        private const int TransitionCycleMs = 5000;
        private const int HeadlessStableCycleMs = 20000;
        private const int ClientCycleMs = 6000;
        private const int CleanupOnlyCycleMs = 10000;
        private const int SessionVerifyRetrySeconds = 5;
        private const int SessionVerifiedLobbySeconds = 15;
        private const int SessionVerifiedGameSeconds = 60;
        private const int CleanupFollowUpSeconds = 10;
        private const int CleanupFallbackSeconds = 120;
        private const int CleanupKillWindowSeconds = 60;
        private const int CleanupKillLimit = 3;
        private const int CleanupFollowUpLimit = 2;

        private const int ProcessWakeThrottleMs = 5000;
        internal static int ProcessEventWakeThrottleMs
        {
            get { return ProcessWakeThrottleMs; }
        }
        private const int RecoveryMinIntervalSeconds = 120;
        private const int UxRespawnBackoffSeconds = 10;
        private const int UxRespawnWindowSeconds = 60;
        private const int UxRespawnLimit = 3;
        private const int FullProcessScanSeconds = 60;
        private const int ClientExitConfirmSamples = 4;
        private readonly object stateLock = new object();
        private readonly object actionLock = new object();
        private readonly object publishLock = new object();
        private readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        private readonly AutoResetEvent pokeEvent = new AutoResetEvent(false);
        private readonly Timer processWakeTimer;
        private Thread worker;
        private bool disposed;
        private bool running;
        private bool enabled;
        private bool cleanupEnabled;
        private bool headlessEnabled;
        private string lolRoot;
        private string weGameRoot;
        private bool installationFound;
        private bool weGameFound;
        private int discoveryMisses;
        private bool clientRunning;
        private int clientAbsentSamples;
        private bool gameRunning;
        private bool lcuReady;
        private string phase;
        private bool headlessActive;
        private bool discovering;
        private string headlessLeaseRoot;
        private int headlessLeaseGamePid;
        private long headlessLeaseGameCreation;
        private bool headlessLeaseReleasePending;
        private bool manualUxBypassGame;
        private int headlessExitSamples;
        private int weGameProcessCount;
        private bool weGameMainRunning;
        private int crossProcessCount;
        private int uxProcessCount;
        private int cleanedProcessCount;
        private long releasedWorkingSetBytes;
        private string lastAction;
        private string lastError;
        private DateTime updatedUtc;
        private DateTime lastActionUtc;
        private DateTime nextCleanupUtc;
        private bool cleanupMatchActive;
        private bool cleanupDirty;
        private bool cleanupBurstActive;
        private int cleanupFollowUpsRemaining;
        private bool cleanupCircuitOpen;
        private readonly Queue<DateTime> cleanupKillCycles = new Queue<DateTime>();
        private DateTime nextProcessWakeUtc;
        private bool processWakePending;
        private DateTime nextDiscoveryUtc;
        private int discoveryCancelRequested;
        private bool discoveryRequested;
        private DateTime nextFullProcessScanUtc;
        private LolLcuCredentials cachedCredentials;
        private string cachedCredentialRoot;
        private DateTime nextCredentialRefreshUtc;
        private bool sessionVerified;
        private DateTime nextSessionVerifyUtc;
        private int lcuFailureStreak;
        private int credentialLookupFailures;
        private int credentialGenerationChanged;
        private int restoreCredentialLookupFailures;
        private DateTime nextRestoreCredentialLookupUtc;
        private DateTime nextRecoveryUtc;
        private readonly Queue<DateTime> uxRespawns = new Queue<DateTime>();
        private DateTime nextUxKillUtc;
        private LolOptimizationSnapshot lastPublishedSnapshot;
        private int runGeneration;
        private int waitHandlesDisposed;

        public event Action Changed;

        public LolOptimizationService()
        {
            processWakeTimer = new Timer(
                ProcessWakeTimerElapsed, null,
                Timeout.Infinite, Timeout.Infinite);
            enabled = Settings.Load(EnabledKey, false);
            cleanupEnabled = Settings.Load(CleanupEnabledKey, false);
            headlessEnabled = Settings.Load(HeadlessEnabledKey, false);
            lolRoot = Settings.LoadStr(LolRootKey, null);
            weGameRoot = Settings.LoadStr(WeGameRootKey, null);
            phase = "";
            lastAction = Lang.T("lol.act.waitclient");
            lastError = "";
            updatedUtc = DateTime.UtcNow;
        }

        public bool Enabled
        {
            get { lock (stateLock) return enabled; }
            set
            {
                bool changed;
                lock (stateLock)
                {
                    changed = enabled != value;
                    enabled = value;
                    if (changed && value)
                    {
                        discoveryRequested = true;
                        discoveryMisses = 0;
                        nextDiscoveryUtc = DateTime.MinValue;
                    }
                    updatedUtc = DateTime.UtcNow;
                }
                if (!changed) return;
                Settings.Save(EnabledKey, value);
                Poke();
                RaiseChanged();
            }
        }

        public bool CleanupEnabled
        {
            get { lock (stateLock) return cleanupEnabled; }
            set
            {
                bool changed;
                lock (stateLock)
                {
                    changed = cleanupEnabled != value;
                    cleanupEnabled = value;
                    if (value)
                    {
                        cleanupDirty = true;
                        nextCleanupUtc = DateTime.MinValue;
                    }
                    else
                    {
                        cleanupDirty = false;
                        cleanupBurstActive = false;
                        cleanupFollowUpsRemaining = 0;
                    }
                    updatedUtc = DateTime.UtcNow;
                }
                if (!changed) return;
                Settings.Save(CleanupEnabledKey, value);
                Poke();
                RaiseChanged();
            }
        }

        public bool HeadlessEnabled
        {
            get { lock (stateLock) return headlessEnabled; }
            set
            {
                bool changed;
                lock (stateLock)
                {
                    changed = headlessEnabled != value;
                    headlessEnabled = value;
                    updatedUtc = DateTime.UtcNow;
                }
                if (!changed) return;
                Settings.Save(HeadlessEnabledKey, value);
                Poke();
                RaiseChanged();
            }
        }

        public void Start()
        {
            lock (stateLock)
            {
                if (disposed || running || (worker != null && worker.IsAlive)) return;
                enabled = Settings.Load(EnabledKey, enabled);
                cleanupEnabled = Settings.Load(CleanupEnabledKey, cleanupEnabled);
                headlessEnabled = Settings.Load(HeadlessEnabledKey, headlessEnabled);
                lolRoot = Settings.LoadStr(LolRootKey, lolRoot);
                weGameRoot = Settings.LoadStr(WeGameRootKey, weGameRoot);
                running = true;
                processWakePending = false;
                nextProcessWakeUtc = DateTime.MinValue;
                try
                {
                    processWakeTimer.Change(
                        Timeout.Infinite, Timeout.Infinite);
                }
                catch { }
                lastError = "";
                updatedUtc = DateTime.UtcNow;
                stopEvent.Reset();
                int generation = ++runGeneration;
                worker = new Thread(new ThreadStart(delegate { WorkerLoop(generation); }));
                worker.IsBackground = true;
                worker.Name = "Pavise LoL Runtime";
                worker.Start();
            }
            RaiseChanged();
        }

        public void Stop()
        {
            Thread current;
            bool changed;
            lock (stateLock)
            {
                current = worker;
                changed = running || (current != null && current.IsAlive);
                running = false;
                processWakePending = false;
                runGeneration++;
                updatedUtc = DateTime.UtcNow;
            }
            try
            {
                processWakeTimer.Change(
                    Timeout.Infinite, Timeout.Infinite);
            }
            catch { }
            try { stopEvent.Set(); } catch { }
            try { pokeEvent.Set(); } catch { }
            if (current != null && current != Thread.CurrentThread)
            {
                try { current.Join(3000); } catch { }
            }
            if (Monitor.TryEnter(actionLock, 1000))
            {
                try { InvalidateCredentials(); }
                finally { Monitor.Exit(actionLock); }
            }
            lock (stateLock)
            {
                worker = current != null && current.IsAlive ? current : null;
                updatedUtc = DateTime.UtcNow;
            }
            if (changed) RaiseChanged();
        }

        public void Refresh()
        {
            InvalidateDiscovery();
            Poke();
        }

        public void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            if (batch == null) return;
            bool relevant = batch.Overflowed;
            bool cleanupCandidateStarted = batch.Overflowed;

            bool credentialSourceStarted = false;
            bool discoveryDirty = batch.Overflowed;
            string root;
            string weGame;
            lock (stateLock)
            {
                if (!running || disposed) return;
                root = lolRoot;
                weGame = weGameRoot;
            }
            foreach (ProcessChange change in batch.Changes)
            {
                bool credentialDiscoveryStarted =
                    IsCredentialDiscoveryStartEvent(change);
                if (change == null || (!credentialDiscoveryStarted
                    && !LolRuntimeProcesses.IsRelevantProcessChange(
                        change.Name, change.Path, root, weGame)))
                    continue;
                relevant = true;
                if (IsExactCleanupStartEvent(change, root, weGame))
                {
                    cleanupCandidateStarted = true;
                }
                if (IsCredentialSourceStartEvent(change, root))
                    credentialSourceStarted = true;
                if (credentialDiscoveryStarted)
                {
                    credentialSourceStarted = true;
                    discoveryDirty = true;
                }
            }
            if (!relevant) return;
            if (discoveryDirty) InvalidateDiscovery();
            if (credentialSourceStarted)
                Interlocked.Exchange(ref credentialGenerationChanged, 1);
            bool wake;
            bool scheduleTrailing = false;
            bool cancelTrailing = false;
            int trailingDelay = Timeout.Infinite;
            DateTime now = DateTime.UtcNow;
            lock (stateLock)
            {
                if (batch.Overflowed) nextFullProcessScanUtc = DateTime.MinValue;
                if (cleanupCandidateStarted) cleanupDirty = true;
                wake = credentialSourceStarted || now >= nextProcessWakeUtc;
                if (wake)
                {
                    nextProcessWakeUtc = now.AddMilliseconds(ProcessWakeThrottleMs);
                    cancelTrailing = processWakePending;
                    processWakePending = false;
                }
                else if (!processWakePending)
                {

                    processWakePending = true;
                    scheduleTrailing = true;
                    trailingDelay = ProcessWakeDelayMs(
                        now, nextProcessWakeUtc);
                }
                if (cancelTrailing)
                    try
                    {
                        processWakeTimer.Change(
                            Timeout.Infinite, Timeout.Infinite);
                    }
                    catch { }
                if (scheduleTrailing)
                    try
                    {
                        processWakeTimer.Change(
                            trailingDelay, Timeout.Infinite);
                    }
                    catch { }
            }
            if (wake) Poke();
        }

        private void ProcessWakeTimerElapsed(object unused)
        {
            bool wake = false;
            bool reschedule = false;
            int delay = Timeout.Infinite;
            lock (stateLock)
            {
                if (disposed || !running || !processWakePending) return;
                DateTime now = DateTime.UtcNow;
                if (now < nextProcessWakeUtc)
                {
                    reschedule = true;
                    delay = ProcessWakeDelayMs(now, nextProcessWakeUtc);
                }
                else
                {
                    processWakePending = false;
                    nextProcessWakeUtc =
                        now.AddMilliseconds(ProcessWakeThrottleMs);
                    wake = true;
                }
            }
            if (reschedule)
                try
                {
                    processWakeTimer.Change(delay, Timeout.Infinite);
                }
                catch { }
            if (wake) Poke();
        }

        internal static int ProcessWakeDelayMs(
            DateTime nowUtc, DateTime deadlineUtc)
        {
            long remaining = deadlineUtc.Ticks - nowUtc.Ticks;
            if (remaining <= 0) return 1;
            long milliseconds = (remaining + TimeSpan.TicksPerMillisecond - 1)
                / TimeSpan.TicksPerMillisecond;
            return (int)Math.Min(int.MaxValue, Math.Max(1L, milliseconds));
        }

        internal static bool IsExactCleanupStartEvent(
            ProcessChange change, string root, string weGame)
        {
            if (change == null || change.Kind != ProcessChangeKind.Started
                || string.IsNullOrEmpty(change.Path))
                return false;
            try
            {
                return LolRuntimeProcesses.IsCleanupTarget(
                    change.Path,
                    Path.GetFileName(change.Path),
                    root,
                    weGame);
            }
            catch { return false; }
        }

        internal static bool IsCredentialSourceStartEvent(
            ProcessChange change, string root)
        {
            if (change == null || change.Kind != ProcessChangeKind.Started
                || !LolRuntimeProcesses.IsCredentialSourceName(change.Name))
                return false;
            if (string.IsNullOrEmpty(change.Path)) return true;
            if (!LolRuntimeProcesses.IsUnder(change.Path, root)) return false;
            string file;
            try { file = Path.GetFileName(change.Path); }
            catch { return false; }
            return string.Equals(
                    file, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    file, "LeagueClientUx.exe", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsCredentialDiscoveryStartEvent(
            ProcessChange change)
        {
            if (change == null || change.Kind != ProcessChangeKind.Started
                || !LolRuntimeProcesses.IsCredentialSourceName(change.Name))
                return false;
            if (string.IsNullOrEmpty(change.Path)) return true;
            string file;
            try { file = Path.GetFileName(change.Path); }
            catch { return false; }
            return string.Equals(
                    file, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    file, "LeagueClientUx.exe", StringComparison.OrdinalIgnoreCase);
        }

        public bool CleanNow()
        {
            lock (actionLock)
            {
                string root;
                string weGame;
                Discover(out root, out weGame, true);
                if (root == null)
                {
                    SetError(Lang.T("lol.err.noinstall"));
                    return false;
                }
                LolProcessSnapshot currentProcesses = LolRuntimeProcesses.Scan(root, weGame);
                Interlocked.Exchange(ref credentialGenerationChanged, 0);
                InvalidateCredentials();
                bool initiallyReady;
                LolLcuCredentials credentials = ResolveCredentials(
                    root, currentProcesses.ClientRunning, out initiallyReady);
                if (credentials == null || !initiallyReady)
                {
                    SetError(Lang.T("lol.err.notready"));
                    return false;
                }
                lock (stateLock)
                {
                    cleanupDirty = false;
                    cleanupBurstActive = false;
                    cleanupFollowUpsRemaining = 0;
                }
                LolCleanupResult result = LolRuntimeProcesses.Clean(root, weGame, true);
                bool healthy = LolLcuClient.IsReady(credentials);
                lock (stateLock)
                {
                    cleanedProcessCount += result.Count;
                    releasedWorkingSetBytes += result.WorkingSetBytes;
                    lcuReady = healthy;
                    if (healthy)
                    {
                        lastAction = result.Count > 0
                            ? Lang.F("lol.act.cleaned", result.Count.ToString())
                            : Lang.T("lol.act.cleanidle");
                        lastError = "";
                    }
                    else
                    {
                        lastAction = Lang.T("lol.act.recovering");
                        lastError = Lang.T("lol.err.sessionlost");
                    }
                    lastActionUtc = DateTime.UtcNow;
                    if (cleanupDirty)
                    {
                        cleanupBurstActive = true;
                        cleanupFollowUpsRemaining = 0;
                        nextCleanupUtc = DateTime.UtcNow.AddSeconds(
                            CleanupFollowUpSeconds);
                    }
                    else
                    {
                        nextCleanupUtc = DateTime.UtcNow.AddSeconds(
                            CleanupFallbackSeconds);
                    }
                    updatedUtc = DateTime.UtcNow;
                }
                LolProcessSnapshot afterClean = LolRuntimeProcesses.Scan(root, weGame);
                UpdateProcessState(afterClean);
                Logger.Log("英雄联盟专栏：精准净化完成，结束 " + result.Count + " 个附加进程");
                if (!healthy)
                {
                    InvalidateCredentials();
                    TryRecoverLoginChain(
                        weGame, afterClean, false, result.Count > 0);
                    RaiseChanged();
                    return false;
                }
                RaiseChanged();
                return true;
            }
        }

        public bool RestoreNow()
        {
            lock (actionLock)
            {
                string root;
                string weGame;
                Discover(out root, out weGame, true);
                if (root == null)
                {
                    SetError(Lang.T("lol.err.noinstall"));
                    return false;
                }
                RememberHeadlessLease(root);
                LolLcuCredentials credentials =
                    GetCredentialsForRestore(root, true);
                if (credentials == null)
                {
                    SetError(Lang.T("lol.err.nosession"));
                    return false;
                }
                bool restored = LolLcuClient.RestoreUx(credentials, root);
                if (!restored)
                {
                    InvalidateCredentials();
                    credentials = GetCredentialsForRestore(root, true);
                    restored = credentials != null && LolLcuClient.RestoreUx(credentials, root);
                    if (!restored && credentials != null)
                        ScheduleRestoreCredentialRetry();
                }
                bool inGame = restored && (LolRuntimeProcesses.IsGameRunning(root)
                    || IsSameMatchPhase(
                        LolLcuClient.GetGameflowPhase(credentials)));
                lock (stateLock)
                {
                    if (restored)
                    {
                        headlessActive = false;
                        headlessExitSamples = 0;
                        manualUxBypassGame = inGame;
                        lastAction = Lang.T("lol.act.restored");
                        lastError = "";
                    }
                    else
                    {
                        lastError = Lang.T("lol.err.restore");
                    }
                    lastActionUtc = DateTime.UtcNow;
                    updatedUtc = DateTime.UtcNow;
                }
                bool completed = restored;
                if (restored)
                {
                    completed = ClearOwnedHeadlessMark();
                    if (!completed)
                    {
                        lock (stateLock)
                        {
                            lastError = Lang.T("lol.err.leaseclear");
                            lastActionUtc = DateTime.UtcNow;
                            updatedUtc = DateTime.UtcNow;
                        }
                    }
                    Logger.Log("英雄联盟专栏：大厅界面已恢复");
                }
                RaiseChanged();
                return completed;
            }
        }

        public bool HasRecordedHeadlessState()
        {
            bool localState;
            lock (stateLock)
            {
                localState = headlessActive
                    || !string.IsNullOrEmpty(headlessLeaseRoot);
            }
            return localState || LolHeadlessLease.Exists();
        }

        public bool LaunchWeGame()
        {
            lock (actionLock)
            {
                string root;
                string weGame;
                Discover(out root, out weGame, true);

                if (LolRuntimeProcesses.IsWeGameRunning(weGame))
                {
                    SetAction(Lang.T("lol.act.wegamerunning"));
                    UpdateProcessState(LolRuntimeProcesses.Scan(root, weGame));
                    RaiseChanged();
                    return true;
                }
                if (!StartWeGame(weGame, true)) return false;
                bool alive = WaitForWeGame(weGame, 6000);
                if (!alive) SetError(Lang.T("lol.err.wegamenostart"));
                UpdateProcessState(LolRuntimeProcesses.Scan(root, weGame));
                RaiseChanged();
                return alive;
            }
        }

        private bool WaitForWeGame(string weGame, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                try { if (stopEvent.WaitOne(0)) return LolRuntimeProcesses.IsWeGameRunning(weGame); }
                catch (ObjectDisposedException) { return LolRuntimeProcesses.IsWeGameRunning(weGame); }
                if (LolRuntimeProcesses.IsWeGameRunning(weGame)) return true;
                Thread.Sleep(250);
                waited += 250;
            }
            return LolRuntimeProcesses.IsWeGameRunning(weGame);
        }

        private bool StartWeGame(string weGame, bool updateAction)
        {
            string executable = LolInstallDiscovery.FindWeGameExecutable(weGame);
            if (executable == null)
            {
                if (updateAction) SetError(Lang.T("lol.err.nowegame"));
                return false;
            }
            if (!UserLaunch.Start(executable))
            {
                if (updateAction) SetError(Lang.T("lol.err.wegamestart"));
                return false;
            }
            if (updateAction) SetAction(Lang.T("lol.act.wegamestarting"));
            Logger.Log("英雄联盟专栏：已发起 WeGame 启动");
            return true;
        }

        private void SetAction(string message)
        {
            lock (stateLock)
            {
                lastAction = message ?? "";
                lastError = "";
                lastActionUtc = DateTime.UtcNow;
                updatedUtc = DateTime.UtcNow;
            }
        }

        public LolOptimizationSnapshot GetSnapshot()
        {
            lock (stateLock)
            {
                LolOptimizationSnapshot snapshot = new LolOptimizationSnapshot(
                    running,
                    enabled,
                    cleanupEnabled,
                    headlessEnabled,
                    lolRoot,
                    weGameRoot,
                    installationFound,
                    weGameFound,
                    clientRunning,
                    gameRunning,
                    lcuReady,
                    phase,
                    headlessActive,
                    weGameProcessCount,
                    crossProcessCount,
                    uxProcessCount,
                    cleanedProcessCount,
                    releasedWorkingSetBytes,
                    lastAction,
                    lastError,
                    updatedUtc,
                    lastActionUtc,
                    discovering);
                snapshot.WeGameMainRunning = weGameMainRunning;
                return snapshot;
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed) return;
                disposed = true;
            }
            Stop();
            bool canDispose;
            lock (stateLock) canDispose = worker == null || !worker.IsAlive;
            if (!canDispose) return;
            DisposeWaitHandles();
        }

        private void DisposeWaitHandles()
        {
            if (Interlocked.Exchange(ref waitHandlesDisposed, 1) != 0) return;
            try { processWakeTimer.Dispose(); } catch { }
            try { stopEvent.Dispose(); } catch { }
            try { pokeEvent.Dispose(); } catch { }
        }

        private bool SetHeadlessMark(
            string root, int gamePid, long gameCreation)
        {
            if (!LolHeadlessLease.Set(root, gamePid, gameCreation))
                return false;
            lock (stateLock)
            {
                headlessLeaseRoot = root;
                headlessLeaseGamePid = gamePid;
                headlessLeaseGameCreation = gameCreation;
                headlessLeaseReleasePending = false;
            }
            return true;
        }

        private bool ClearOwnedHeadlessMark()
        {
            string root;
            int pid;
            long creation;
            lock (stateLock)
            {
                root = headlessLeaseRoot;
                pid = headlessLeaseGamePid;
                creation = headlessLeaseGameCreation;
            }
            if (string.IsNullOrEmpty(root)) return true;
            bool cleared = pid > 0 && creation > 0
                && LolHeadlessLease.ClearIfMatches(root, pid, creation);
            bool stillOwned = LolHeadlessLease.Matches(root, pid, creation);
            bool released = cleared || !stillOwned;
            lock (stateLock)
            {
                if (SameLocalLeaseLocked(root, pid, creation))
                {
                    headlessLeaseReleasePending = !released;
                    if (released)
                    {
                        headlessLeaseRoot = null;
                        headlessLeaseGamePid = 0;
                        headlessLeaseGameCreation = 0;
                    }
                }
            }
            return released;
        }

        private void RememberHeadlessLease(string root)
        {
            LolHeadlessLeaseInfo info;
            bool found = LolHeadlessLease.TryRead(out info)
                && SameRootPath(info.Root, root);
            lock (stateLock)
            {
                if (found)
                {
                    bool same = SameLocalLeaseLocked(
                        info.Root, info.GamePid, info.GameCreation);
                    headlessLeaseRoot = info.Root;
                    headlessLeaseGamePid = info.GamePid;
                    headlessLeaseGameCreation = info.GameCreation;
                    if (!same) headlessLeaseReleasePending = false;
                }
                else if (SameRootPath(headlessLeaseRoot, root))
                {
                    headlessLeaseRoot = null;
                    headlessLeaseGamePid = 0;
                    headlessLeaseGameCreation = 0;
                    headlessLeaseReleasePending = false;
                }
            }
        }

        private bool SameLocalLeaseLocked(
            string root, int pid, long creation)
        {
            return headlessLeaseGamePid == pid
                && headlessLeaseGameCreation == creation
                && SameRootPath(headlessLeaseRoot, root);
        }

        private void ForgetRememberedHeadlessLease()
        {
            lock (stateLock)
            {
                headlessLeaseRoot = null;
                headlessLeaseGamePid = 0;
                headlessLeaseGameCreation = 0;
                headlessLeaseReleasePending = false;
            }
        }

        private static bool SameRootPath(string left, string right)
        {
            try
            {
                string a = Path.GetFullPath(left ?? "").TrimEnd('\\');
                string b = Path.GetFullPath(right ?? "").TrimEnd('\\');
                return a.Length > 2 && b.Length > 2
                    && string.Equals(
                        a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool HasHeadlessMark()
        {
            return LolHeadlessLease.Exists();
        }

        private static bool HasHeadlessMark(string root)
        {
            return LolHeadlessLease.MatchesRoot(root);
        }

        private void GoIdle()
        {
            bool changed;
            lock (stateLock)
            {
                changed = clientRunning || gameRunning || lcuReady || phase.Length > 0
                    || weGameProcessCount != 0 || crossProcessCount != 0 || uxProcessCount != 0;
                clientRunning = false;
                gameRunning = false;
                lcuReady = false;
                phase = "";
                weGameProcessCount = 0;
                weGameMainRunning = false;
                crossProcessCount = 0;
                uxProcessCount = 0;
                headlessExitSamples = 0;
                lastAction = Lang.T("lol.act.disabled");
                lastError = "";
                updatedUtc = DateTime.UtcNow;
            }
            InvalidateCredentials();
            if (changed) RaiseChanged();
        }

        internal static bool IsVerifiedCleanupSession(
            bool clientRunning,
            bool lcuReady,
            string currentPhase,
            bool gameRunning,
            bool matchActive)
        {
            if (!clientRunning || !lcuReady
                || string.IsNullOrEmpty(currentPhase))
                return false;
            if (IsSameMatchPhase(currentPhase))
                return matchActive && gameRunning;

            return !gameRunning
                && IsConfirmedMatchExitPhase(currentPhase);
        }

        internal static bool ShouldResetMatchSafetyState(
            bool clientExitConfirmed,
            bool matchExitConfirmed,
            bool matchStateActive)
        {

            return clientExitConfirmed
                || (matchExitConfirmed && matchStateActive);
        }

        internal static bool RegisterUxRespawn(
            DateTime nowUtc, Queue<DateTime> recent)
        {
            if (recent == null) throw new ArgumentNullException("recent");
            while (recent.Count > 0
                && (nowUtc < recent.Peek()
                    || (nowUtc - recent.Peek()).TotalSeconds >= UxRespawnWindowSeconds))
                recent.Dequeue();
            recent.Enqueue(nowUtc);
            return recent.Count > UxRespawnLimit;
        }

        internal static bool RegisterCleanupKillCycle(
            DateTime nowUtc, Queue<DateTime> recent)
        {
            if (recent == null) throw new ArgumentNullException("recent");
            while (recent.Count > 0
                && (nowUtc < recent.Peek()
                    || (nowUtc - recent.Peek()).TotalSeconds
                        >= CleanupKillWindowSeconds))
                recent.Dequeue();
            recent.Enqueue(nowUtc);
            return recent.Count > CleanupKillLimit;
        }

        internal static bool IsConfirmedMatchExitPhase(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return string.Equals(value, "None", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Lobby", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Matchmaking", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    value, "CheckedIntoTournament",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "ReadyCheck", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "ChampSelect", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "FailedToLaunch", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "PreEndOfGame", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "EndOfGame", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "WaitingForStats", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    value, "TerminatedInError", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsSameMatchPhase(string value)
        {
            return string.Equals(value, "InProgress",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Reconnect",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "GameStart",
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ConfirmClientExit(
            bool scanSucceeded, bool clientRunning, bool gameRunning,
            ref int absentSamples)
        {
            if (scanSucceeded)
            {
                if (clientRunning || gameRunning)
                    absentSamples = 0;
                else if (absentSamples < int.MaxValue)
                    absentSamples++;
            }
            return absentSamples >= ClientExitConfirmSamples;
        }

        private void ResetMatchSafetyStateLocked()
        {
            manualUxBypassGame = false;
            uxRespawns.Clear();
            nextUxKillUtc = DateTime.MinValue;
            cleanupDirty = false;
            cleanupMatchActive = false;
            cleanupBurstActive = false;
            cleanupFollowUpsRemaining = 0;
            cleanupCircuitOpen = false;
            cleanupKillCycles.Clear();
            nextCleanupUtc = DateTime.MinValue;
        }

        private void UpdateProcessState(LolProcessSnapshot processes)
        {
            if (processes == null) return;
            lock (stateLock)
            {
                clientRunning = processes.ClientRunning;
                gameRunning = processes.GameRunning;
                weGameProcessCount = processes.WeGameProcessCount;
                weGameMainRunning = processes.WeGameMainRunning;
                crossProcessCount = processes.CrossProcessCount;
                uxProcessCount = processes.UxProcessCount;
                updatedUtc = DateTime.UtcNow;
            }
        }

        private void SetError(string message)
        {
            lock (stateLock)
            {
                lastError = message ?? "";
                lastActionUtc = DateTime.UtcNow;
                updatedUtc = DateTime.UtcNow;
            }
            RaiseChanged();
        }

        private void Poke()
        {
            try { pokeEvent.Set(); } catch { }
        }

        internal static bool SamePublishedState(
            LolOptimizationSnapshot left, LolOptimizationSnapshot right)
        {
            if (left == null || right == null) return left == right;
            return left.Running == right.Running
                && left.Enabled == right.Enabled
                && left.CleanupEnabled == right.CleanupEnabled
                && left.HeadlessEnabled == right.HeadlessEnabled
                && string.Equals(left.LolRoot, right.LolRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    left.WeGameRoot, right.WeGameRoot, StringComparison.OrdinalIgnoreCase)
                && left.InstallationFound == right.InstallationFound
                && left.WeGameFound == right.WeGameFound
                && left.ClientRunning == right.ClientRunning
                && left.GameRunning == right.GameRunning
                && left.LcuReady == right.LcuReady
                && string.Equals(left.Phase, right.Phase, StringComparison.Ordinal)
                && left.HeadlessActive == right.HeadlessActive
                && left.WeGameProcessCount == right.WeGameProcessCount
                && left.WeGameMainRunning == right.WeGameMainRunning
                && left.CrossProcessCount == right.CrossProcessCount
                && left.UxProcessCount == right.UxProcessCount
                && left.CleanedProcessCount == right.CleanedProcessCount
                && left.ReleasedWorkingSetBytes == right.ReleasedWorkingSetBytes
                && string.Equals(left.LastAction, right.LastAction, StringComparison.Ordinal)
                && string.Equals(left.LastError, right.LastError, StringComparison.Ordinal)
                && left.LastActionUtc == right.LastActionUtc;
        }

        internal void RaiseChanged()
        {
            Action handler = Changed;
            if (handler == null) return;
            LolOptimizationSnapshot snapshot = GetSnapshot();
            lock (publishLock)
            {
                if (SamePublishedState(lastPublishedSnapshot, snapshot)) return;
                lastPublishedSnapshot = snapshot;
            }
            try { handler(); } catch { }
        }
    }
}
