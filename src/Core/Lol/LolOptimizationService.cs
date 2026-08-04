// @author bdth 2074055628@qq.com

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

    internal sealed class LolOptimizationService : IDisposable
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
        private bool headlessLeaseLegacy;
        private bool headlessLeaseReleasePending;
        private bool manualUxBypassGame;
        private int headlessExitSamples;
        private int weGameProcessCount;
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

        public void RequestDiscovery()
        {
            lock (stateLock)
            {
                discoveryRequested = true;
                discoveryMisses = 0;
                nextDiscoveryUtc = DateTime.MinValue;
            }
            Poke();
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
                if (stopEvent.WaitOne(0)) return LolRuntimeProcesses.IsWeGameRunning(weGame);
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
                return new LolOptimizationSnapshot(
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
                    return;
                }

                string root;
                string weGame;
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
                    return;
                }
                RememberHeadlessLease(root);
                bool releasePending;
                lock (stateLock)
                    releasePending = headlessLeaseReleasePending;
                if (releasePending) ClearOwnedHeadlessMark();

                bool lightScan;
                lock (stateLock)
                {
                    lightScan = DateTime.UtcNow < nextFullProcessScanUtc;
                    if (!lightScan)
                        nextFullProcessScanUtc = DateTime.UtcNow.AddSeconds(
                            FullProcessScanSeconds);
                }
                LolProcessSnapshot processes = LolRuntimeProcesses.Scan(root, weGame, lightScan);
                string currentPhase;
                bool ready;
                LolLcuCredentials credentials = ProbeLcu(
                    root, processes.ClientRunning, out currentPhase, out ready);
                bool serviceEnabled;
                bool cleanEnabled;
                bool noUxEnabled;
                bool active;
                bool manualBypass;
                bool inProgress = string.Equals(
                    currentPhase, "InProgress", StringComparison.OrdinalIgnoreCase);
                bool sameMatch = IsSameMatchPhase(currentPhase);
                bool clientExitConfirmed;
                lock (stateLock)
                {
                    clientExitConfirmed = ConfirmClientExit(
                        processes.ScanSucceeded
                            && !processes.CoreIdentityIndeterminate,
                        processes.ClientRunning,
                        processes.GameRunning,
                        ref clientAbsentSamples);
                }
                bool matchExitConfirmed = clientExitConfirmed
                    || (!processes.GameRunning
                        && IsConfirmedMatchExitPhase(currentPhase));
                bool cleanupMatch;
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

                UpdateProcessState(processes);
                RaiseChanged();
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
                headlessLeaseLegacy = false;
                headlessLeaseReleasePending = false;
            }
            return true;
        }

        private bool ClearOwnedHeadlessMark()
        {
            string root;
            int pid;
            long creation;
            bool legacy;
            lock (stateLock)
            {
                root = headlessLeaseRoot;
                pid = headlessLeaseGamePid;
                creation = headlessLeaseGameCreation;
                legacy = headlessLeaseLegacy;
            }
            if (string.IsNullOrEmpty(root)) return true;
            bool cleared = legacy
                ? LolHeadlessLease.ClearLegacyIfMatches(root)
                : (pid > 0 && creation > 0
                    && LolHeadlessLease.ClearIfMatches(root, pid, creation));
            bool stillOwned = legacy
                ? LolHeadlessLease.MatchesLegacyRoot(root)
                : LolHeadlessLease.Matches(root, pid, creation);
            bool released = cleared || !stillOwned;
            lock (stateLock)
            {
                if (SameLocalLeaseLocked(root, pid, creation, legacy))
                {
                    headlessLeaseReleasePending = !released;
                    if (released)
                    {
                        headlessLeaseRoot = null;
                        headlessLeaseGamePid = 0;
                        headlessLeaseGameCreation = 0;
                        headlessLeaseLegacy = false;
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
                        info.Root, info.GamePid, info.GameCreation, info.Legacy);
                    headlessLeaseRoot = info.Root;
                    headlessLeaseGamePid = info.GamePid;
                    headlessLeaseGameCreation = info.GameCreation;
                    headlessLeaseLegacy = info.Legacy;
                    if (!same) headlessLeaseReleasePending = false;
                }
                else if (SameRootPath(headlessLeaseRoot, root))
                {
                    headlessLeaseRoot = null;
                    headlessLeaseGamePid = 0;
                    headlessLeaseGameCreation = 0;
                    headlessLeaseLegacy = false;
                    headlessLeaseReleasePending = false;
                }
            }
        }

        private bool SameLocalLeaseLocked(
            string root, int pid, long creation, bool legacy)
        {
            return headlessLeaseLegacy == legacy
                && headlessLeaseGamePid == pid
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
                headlessLeaseLegacy = false;
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

        private LolLcuCredentials ResolveCredentials(
            string root, bool isClientRunning, out bool ready)
        {
            ready = false;
            if (!isClientRunning)
            {
                InvalidateCredentials();
                return null;
            }
            if (!string.Equals(
                cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
                InvalidateCredentials();
            LolLcuCredentials credentials = cachedCredentials;
            if (credentials != null)
            {
                ready = LolLcuClient.IsReady(credentials);
                sessionVerified = ready;
                if (ready)
                {
                    lcuFailureStreak = 0;
                    credentialLookupFailures = 0;
                    nextCredentialRefreshUtc = DateTime.MinValue;
                    return credentials;
                }
                if (DateTime.UtcNow < nextCredentialRefreshUtc) return credentials;
            }
            else if (DateTime.UtcNow < nextCredentialRefreshUtc)
            {
                return null;
            }
            credentials = LolLcuCredentialSource.Find(root);
            if (credentials == null)
            {
                ScheduleCredentialRetry(root);
                return null;
            }
            cachedCredentials = credentials;
            cachedCredentialRoot = root;
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10);
            ready = LolLcuClient.IsReady(credentials);
            sessionVerified = ready;
            if (ready)
            {
                lcuFailureStreak = 0;
                credentialLookupFailures = 0;
                nextCredentialRefreshUtc = DateTime.MinValue;
                nextSessionVerifyUtc = DateTime.UtcNow.AddSeconds(
                    SessionVerifiedLobbySeconds);
            }
            return credentials;
        }

        private LolLcuCredentials ProbeLcu(
            string root, bool isClientRunning, out string currentPhase, out bool ready)
        {
            currentPhase = null;
            ready = false;
            if (!isClientRunning)
            {
                InvalidateCredentials();
                return null;
            }
            if (!string.Equals(
                cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
                InvalidateCredentials();
            LolLcuCredentials credentials = cachedCredentials;
            if (credentials == null)
            {
                if (DateTime.UtcNow < nextCredentialRefreshUtc) return null;
                credentials = LolLcuCredentialSource.Find(root);
                if (credentials == null)
                {
                    ScheduleCredentialRetry(root);
                    return null;
                }
                cachedCredentials = credentials;
                cachedCredentialRoot = root;
                nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10);
                sessionVerified = false;
                nextSessionVerifyUtc = DateTime.MinValue;
            }
            currentPhase = LolLcuClient.GetGameflowPhase(credentials);
            if (currentPhase == null)
            {
                lcuFailureStreak++;
                if (lcuFailureStreak >= 2)
                {
                    sessionVerified = false;
                    nextSessionVerifyUtc = DateTime.MinValue;
                }
                if (lcuFailureStreak >= 3)
                {
                    InvalidateCredentials(false);
                    ScheduleCredentialRetry(root);
                }
                return credentials;
            }
            lcuFailureStreak = 0;
            credentialLookupFailures = 0;
            bool inProgress = string.Equals(
                currentPhase, "InProgress", StringComparison.OrdinalIgnoreCase);
            if (DateTime.UtcNow >= nextSessionVerifyUtc)
            {
                sessionVerified = LolLcuClient.IsReady(credentials);
                nextSessionVerifyUtc = DateTime.UtcNow.AddSeconds(
                    sessionVerified
                        ? (inProgress
                            ? SessionVerifiedGameSeconds : SessionVerifiedLobbySeconds)
                        : SessionVerifyRetrySeconds);
            }
            ready = sessionVerified;
            return credentials;
        }

        private void TryRecoverLoginChain(
            string weGame,
            LolProcessSnapshot processes,
            bool inProgress,
            bool cleanupChangedProcesses)
        {
            if (!cleanupChangedProcesses || processes == null
                || !processes.ClientRunning)
                return;
            if (inProgress || processes.GameRunning) return;
            if (processes.WeGameProcessCount > 0) return;
            if (DateTime.UtcNow < nextRecoveryUtc) return;
            nextRecoveryUtc = DateTime.UtcNow.AddSeconds(RecoveryMinIntervalSeconds);
            StartWeGame(weGame, false);
        }

        private LolLcuCredentials GetCredentialsForRestore(
            string root, bool force)
        {
            if (!string.Equals(
                cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
                InvalidateCredentials();
            if (cachedCredentials != null) return cachedCredentials;
            if (!force && DateTime.UtcNow < nextRestoreCredentialLookupUtc)
                return null;
            cachedCredentials = LolLcuCredentialSource.Find(root);
            if (cachedCredentials == null)
            {
                ScheduleCredentialRetry(root);
                ScheduleRestoreCredentialRetry();
                return null;
            }
            cachedCredentialRoot = root;
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10);
            restoreCredentialLookupFailures = 0;
            nextRestoreCredentialLookupUtc = DateTime.MinValue;
            return cachedCredentials;
        }

        private void InvalidateCredentials()
        {
            InvalidateCredentials(true);
        }

        private void InvalidateCredentials(bool resetBackoff)
        {
            cachedCredentials = null;
            cachedCredentialRoot = null;
            if (resetBackoff)
            {
                nextCredentialRefreshUtc = DateTime.MinValue;
                credentialLookupFailures = 0;
                nextRestoreCredentialLookupUtc = DateTime.MinValue;
                restoreCredentialLookupFailures = 0;
            }
            sessionVerified = false;
            nextSessionVerifyUtc = DateTime.MinValue;
            lcuFailureStreak = 0;
        }

        private void ScheduleCredentialRetry(string root)
        {
            cachedCredentials = null;
            cachedCredentialRoot = root;
            if (credentialLookupFailures < int.MaxValue) credentialLookupFailures++;
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(
                CredentialRetrySeconds(credentialLookupFailures));
            sessionVerified = false;
            nextSessionVerifyUtc = DateTime.MinValue;
            lcuFailureStreak = 0;
        }

        private void ScheduleRestoreCredentialRetry()
        {
            if (restoreCredentialLookupFailures < int.MaxValue)
                restoreCredentialLookupFailures++;
            nextRestoreCredentialLookupUtc = DateTime.UtcNow.AddSeconds(
                RestoreCredentialRetrySeconds(
                    restoreCredentialLookupFailures));
        }

        internal static int CredentialRetrySeconds(int failures)
        {
            if (failures <= 1) return 5;
            if (failures == 2) return 10;
            if (failures == 3) return 30;
            return 60;
        }

        internal static int RestoreCredentialRetrySeconds(int failures)
        {
            if (failures <= 1) return 2;
            if (failures == 2) return 5;
            if (failures == 3) return 10;
            return 30;
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

        public void CancelDiscovery()
        {
            Interlocked.Exchange(ref discoveryCancelRequested, 1);
        }

        private bool DiscoveryCancelRequested()
        {
            return Volatile.Read(ref discoveryCancelRequested) != 0;
        }

        private void SetDiscovering(bool value)
        {
            lock (stateLock)
            {
                if (discovering == value) return;
                discovering = value;
                updatedUtc = DateTime.UtcNow;
            }
            Action handler = Changed;
            if (handler != null) { try { handler(); } catch { } }
        }

        private void Discover(out string root, out string weGame, bool force)
        {
            string preferredRoot;
            string preferredWeGame;
            bool shouldDiscover;
            lock (stateLock)
            {
                preferredRoot = lolRoot;
                preferredWeGame = weGameRoot;
                shouldDiscover = force || DateTime.UtcNow >= nextDiscoveryUtc;
            }
            if (shouldDiscover)
            {
                bool cacheValid = LolInstallDiscovery.IsValidLolRoot(preferredRoot);
                if (!force && !cacheValid)
                {
                    bool armed;
                    lock (stateLock) armed = discoveryRequested;
                    if (!armed)
                    {
                        lock (stateLock) nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(DiscoverySettledSeconds);
                        root = null;
                        weGame = preferredWeGame;
                        return;
                    }
                }
                string discoveredRoot;
                string discoveredWeGame;
                bool announce = force || !cacheValid;
                Func<bool> cancelled = null;
                if (announce)
                {
                    Interlocked.Exchange(ref discoveryCancelRequested, 0);
                    cancelled = DiscoveryCancelRequested;
                    SetDiscovering(true);
                }
                try
                {
                    discoveredRoot = LolInstallDiscovery.FindLolRoot(preferredRoot, force, cancelled);
                    discoveredWeGame = LolInstallDiscovery.FindWeGameRoot(
                        preferredWeGame, discoveredRoot, force, cancelled);
                }
                finally { if (announce) SetDiscovering(false); }
                bool wasCancelled = announce && DiscoveryCancelRequested();
                bool rootChanged;
                bool weGameChanged;
                lock (stateLock)
                {
                    discoveryRequested = false;
                    rootChanged = !string.Equals(
                        lolRoot, discoveredRoot, StringComparison.OrdinalIgnoreCase);
                    weGameChanged = !string.Equals(
                        weGameRoot, discoveredWeGame, StringComparison.OrdinalIgnoreCase);
                    lolRoot = discoveredRoot;
                    weGameRoot = discoveredWeGame;
                    root = discoveredRoot;
                    weGame = discoveredWeGame;
                    installationFound = discoveredRoot != null;
                    weGameFound = LolInstallDiscovery.IsValidWeGameRoot(discoveredWeGame);
                    if (discoveredRoot == null)
                    {
                        if (wasCancelled)
                            nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(DiscoveryMaxSeconds);
                        else
                        {
                            if (discoveryMisses < 8) discoveryMisses++;
                            int backoff = DiscoveryBaseSeconds;
                            for (int i = 1; i < discoveryMisses; i++)
                            {
                                backoff *= 2;
                                if (backoff >= DiscoveryMaxSeconds) break;
                            }
                            if (backoff > DiscoveryMaxSeconds) backoff = DiscoveryMaxSeconds;
                            nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(backoff);
                        }
                    }
                    else
                    {
                        discoveryMisses = 0;
                        nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(DiscoverySettledSeconds);
                    }
                    updatedUtc = DateTime.UtcNow;
                }
                if (rootChanged)
                {
                    InvalidateCredentials();
                    Settings.SaveStr(LolRootKey, root ?? "");
                }
                if (weGameChanged) Settings.SaveStr(WeGameRootKey, weGame ?? "");
                return;
            }
            root = preferredRoot;
            weGame = preferredWeGame;
        }

        private void InvalidateDiscovery()
        {
            lock (stateLock)
            {
                discoveryRequested = true;
                discoveryMisses = 0;
                nextDiscoveryUtc = DateTime.MinValue;
            }
        }

        private void UpdateProcessState(LolProcessSnapshot processes)
        {
            if (processes == null) return;
            lock (stateLock)
            {
                clientRunning = processes.ClientRunning;
                gameRunning = processes.GameRunning;
                weGameProcessCount = processes.WeGameProcessCount;
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
