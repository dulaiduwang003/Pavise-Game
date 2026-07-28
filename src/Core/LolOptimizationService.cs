// @author bdth 2074055628@qq.com

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AegisApp
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
            DateTime lastActionUtc)
        {
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
        private const string HeadlessMarkKey = "LolHeadlessEngagedRoot";
        private const int DiscoveryBaseSeconds = 15;
        private const int DiscoveryMaxSeconds = 600;
        private const int DiscoverySettledSeconds = 120;
        private const int IdleCycleMs = 5000;
        private const int ActiveCycleMs = 1500;
        private readonly object stateLock = new object();
        private readonly object actionLock = new object();
        private readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        private readonly AutoResetEvent pokeEvent = new AutoResetEvent(false);
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
        private bool gameRunning;
        private bool lcuReady;
        private string phase;
        private bool headlessActive;
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
        private DateTime nextDiscoveryUtc;
        private LolLcuCredentials cachedCredentials;
        private string cachedCredentialRoot;
        private DateTime nextCredentialRefreshUtc;
        private int runGeneration;

        public event Action Changed;

        public LolOptimizationService()
        {
            enabled = Settings.Load(EnabledKey, false);
            cleanupEnabled = Settings.Load(CleanupEnabledKey, true);
            headlessEnabled = Settings.Load(HeadlessEnabledKey, true);
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
                lastError = "";
                updatedUtc = DateTime.UtcNow;
                stopEvent.Reset();
                int generation = ++runGeneration;
                worker = new Thread(new ThreadStart(delegate { WorkerLoop(generation); }));
                worker.IsBackground = true;
                worker.Name = "Aegis LoL Runtime";
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
                runGeneration++;
                updatedUtc = DateTime.UtcNow;
            }
            stopEvent.Set();
            pokeEvent.Set();
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
                bool initiallyReady;
                LolLcuCredentials credentials = ResolveCredentials(
                    root, currentProcesses.ClientRunning, out initiallyReady);
                if (credentials == null || !initiallyReady)
                {
                    SetError(Lang.T("lol.err.notready"));
                    return false;
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
                    nextCleanupUtc = DateTime.UtcNow.AddSeconds(3);
                    updatedUtc = DateTime.UtcNow;
                }
                UpdateProcessState(LolRuntimeProcesses.Scan(root, weGame));
                Logger.Log("英雄联盟专栏：精准净化完成，结束 " + result.Count + " 个附加进程");
                if (!healthy)
                {
                    InvalidateCredentials();
                    StartWeGame(weGame, false);
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
                LolLcuCredentials credentials = GetCredentialsForRestore(root);
                if (credentials == null)
                {
                    SetError(Lang.T("lol.err.nosession"));
                    return false;
                }
                bool restored = LolLcuClient.RestoreUx(credentials, root);
                if (!restored)
                {
                    InvalidateCredentials();
                    credentials = GetCredentialsForRestore(root);
                    restored = credentials != null && LolLcuClient.RestoreUx(credentials, root);
                }
                bool inGame = restored && (LolRuntimeProcesses.IsGameRunning(root)
                    || string.Equals(
                        LolLcuClient.GetGameflowPhase(credentials),
                        "InProgress",
                        StringComparison.OrdinalIgnoreCase));
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
                    updatedUtc = DateTime.UtcNow;
                }
                if (restored)
                {
                    ClearHeadlessMark();
                    Logger.Log("英雄联盟专栏：大厅界面已恢复");
                }
                RaiseChanged();
                return restored;
            }
        }

        public bool LaunchWeGame()
        {
            lock (actionLock)
            {
                string root;
                string weGame;
                Discover(out root, out weGame, true);
                // WeGame 是单实例：已经在跑时再启动一个只会立刻自退，什么都不会发生。
                // 不先问一句就报"已启动"，等于对着一个空动作宣布成功。
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
                    lastActionUtc);
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
            try { stopEvent.Dispose(); } catch { }
            try { pokeEvent.Dispose(); } catch { }
        }

        private void WorkerLoop(int generation)
        {
            WaitHandle[] waitHandles = new WaitHandle[] { stopEvent, pokeEvent };
            while (Volatile.Read(ref runGeneration) == generation && !stopEvent.WaitOne(0))
            {
                try { RunCycle(); }
                catch
                {
                    SetError(Lang.T("lol.err.cycle"));
                }
                if (Volatile.Read(ref runGeneration) != generation) break;
                bool idle;
                lock (stateLock) idle = !enabled && !headlessActive;
                int signaled = WaitHandle.WaitAny(waitHandles, idle ? IdleCycleMs : ActiveCycleMs);
                if (signaled == 0) break;
            }
        }

        private void RunCycle()
        {
            lock (actionLock)
            {
                bool active0;
                bool enabled0;
                lock (stateLock)
                {
                    enabled0 = enabled;
                    active0 = headlessActive;
                }
                if (!enabled0 && !active0 && !HasHeadlessMark())
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

                LolProcessSnapshot processes = LolRuntimeProcesses.Scan(root, weGame);
                bool ready;
                LolLcuCredentials credentials = ResolveCredentials(
                    root, processes.ClientRunning, out ready);
                string currentPhase = ready ? LolLcuClient.GetGameflowPhase(credentials) : null;
                bool serviceEnabled;
                bool cleanEnabled;
                bool noUxEnabled;
                bool active;
                bool manualBypass;
                bool inProgress = string.Equals(
                    currentPhase, "InProgress", StringComparison.OrdinalIgnoreCase);
                bool phaseKnown = !string.IsNullOrEmpty(currentPhase);
                lock (stateLock)
                {
                    if (manualUxBypassGame && !processes.GameRunning
                        && currentPhase != null && !inProgress)
                        manualUxBypassGame = false;
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
                    updatedUtc = DateTime.UtcNow;
                }

                if (active && !processes.ClientRunning)
                {
                    lock (stateLock)
                    {
                        headlessActive = false;
                        headlessExitSamples = 0;
                        manualUxBypassGame = false;
                    }
                    ClearHeadlessMark();
                    active = false;
                }
                else if (!processes.ClientRunning)
                {
                    ClearHeadlessMark();
                }

                if (active && serviceEnabled && noUxEnabled && !manualBypass
                    && inProgress && LolRuntimeProcesses.IsUxRunning(root))
                {
                    lock (stateLock)
                    {
                        headlessActive = false;
                        headlessExitSamples = 0;
                    }
                    active = false;
                }

                bool restoreActive = false;
                if (active)
                {
                    lock (stateLock)
                    {
                        if (!serviceEnabled || !noUxEnabled)
                        {
                            headlessExitSamples = 0;
                            restoreActive = true;
                        }
                        else if (ready && phaseKnown && !inProgress && !processes.GameRunning)
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
                    processes = LolRuntimeProcesses.Scan(root, weGame);
                }
                else if (!active && processes.ClientRunning && processes.UxProcessCount == 0
                    && credentials != null && HasHeadlessMark(root))
                {
                    RestoreCore(credentials, root);
                    processes = LolRuntimeProcesses.Scan(root, weGame);
                }
                else if (processes.UxProcessCount > 0 && !active)
                {
                    ClearHeadlessMark();
                }

                if (serviceEnabled && cleanEnabled && ready
                    && DateTime.UtcNow >= nextCleanupUtc)
                {
                    LolCleanupResult clean = LolRuntimeProcesses.Clean(root, weGame);
                    bool healthy = LolLcuClient.IsReady(credentials);
                    lock (stateLock)
                    {
                        cleanedProcessCount += clean.Count;
                        releasedWorkingSetBytes += clean.WorkingSetBytes;
                        lcuReady = healthy;
                        if (!healthy)
                        {
                            lastAction = Lang.T("lol.act.recovering");
                            lastError = Lang.T("lol.err.sessionlost");
                        }
                        else if (clean.Count > 0)
                        {
                            lastAction = Lang.F("lol.act.cleaned", clean.Count.ToString());
                            lastError = "";
                            Logger.Log("英雄联盟专栏：自动精准净化 " + clean.Count + " 个附加进程");
                        }
                        nextCleanupUtc = DateTime.UtcNow.AddSeconds(3);
                        updatedUtc = DateTime.UtcNow;
                    }
                    ready = healthy;
                    if (!healthy)
                    {
                        InvalidateCredentials();
                        StartWeGame(weGame, false);
                    }
                    processes = LolRuntimeProcesses.Scan(root, weGame);
                }

                if (serviceEnabled && noUxEnabled && ready
                    && inProgress)
                {
                    bool shouldKill;
                    lock (stateLock) shouldKill = !headlessActive && !manualUxBypassGame;
                    if (shouldKill)
                    {
                        if (LolRuntimeProcesses.IsUxRunning(root)) SetHeadlessMark(root);
                        bool killed = LolLcuClient.KillUx(credentials, root);
                        lock (stateLock)
                        {
                            if (killed)
                            {
                                headlessActive = true;
                                headlessExitSamples = 0;
                                lastAction = Lang.T("lol.act.headless");
                                lastError = "";
                            }
                            else
                            {
                                ClearHeadlessMark();
                                lastError = Lang.T("lol.err.killux");
                            }
                            updatedUtc = DateTime.UtcNow;
                        }
                        if (killed)
                        {
                            bool watchdogStarted = LolWatchdog.StartDetached(root);
                            if (watchdogStarted)
                            {
                                Logger.Log("英雄联盟专栏：对局真无头已启用");
                            }
                            else
                            {
                                bool recovered = RestoreCore(credentials, root);
                                lock (stateLock)
                                {
                                    lastError = recovered
                                        ? "独立恢复器启动失败，本局已自动回显"
                                        : "独立恢复器启动失败，大厅界面恢复失败";
                                    updatedUtc = DateTime.UtcNow;
                                }
                            }
                            Thread.Sleep(400);
                            processes = LolRuntimeProcesses.Scan(root, weGame);
                        }
                    }
                }

                UpdateProcessState(processes);
                RaiseChanged();
            }
        }

        private bool RestoreCore(LolLcuCredentials credentials, string root)
        {
            if (credentials == null) credentials = GetCredentialsForRestore(root);
            bool restored = credentials != null && LolLcuClient.RestoreUx(credentials, root);
            if (!restored)
            {
                InvalidateCredentials();
                credentials = GetCredentialsForRestore(root);
                restored = credentials != null && LolLcuClient.RestoreUx(credentials, root);
            }
            lock (stateLock)
            {
                if (restored)
                {
                    headlessActive = false;
                    headlessExitSamples = 0;
                    lastAction = Lang.T("lol.act.restored");
                    lastError = "";
                }
                else
                {
                    lastError = Lang.T("lol.err.restorepending");
                }
                updatedUtc = DateTime.UtcNow;
            }
            if (restored)
            {
                ClearHeadlessMark();
                Logger.Log("英雄联盟专栏：大厅界面已自动恢复");
            }
            return restored;
        }

        private static void SetHeadlessMark(string root)
        {
            if (!string.IsNullOrEmpty(root)) Settings.SaveStr(HeadlessMarkKey, root);
        }

        private static void ClearHeadlessMark()
        {
            if (HasHeadlessMark()) Settings.SaveStr(HeadlessMarkKey, "");
        }

        private static bool HasHeadlessMark()
        {
            return !string.IsNullOrEmpty(Settings.LoadStr(HeadlessMarkKey, ""));
        }

        private static bool HasHeadlessMark(string root)
        {
            string marked = Settings.LoadStr(HeadlessMarkKey, "");
            return !string.IsNullOrEmpty(marked) && !string.IsNullOrEmpty(root)
                && string.Equals(marked.TrimEnd('\\'), root.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
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
                manualUxBypassGame = false;
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
                if (ready)
                {
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
            cachedCredentials = credentials;
            cachedCredentialRoot = credentials == null ? null : root;
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(
                credentials == null ? 5 : 10);
            ready = credentials != null && LolLcuClient.IsReady(credentials);
            if (ready) nextCredentialRefreshUtc = DateTime.MinValue;
            return credentials;
        }

        private LolLcuCredentials GetCredentialsForRestore(string root)
        {
            if (!string.Equals(
                cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
                InvalidateCredentials();
            if (cachedCredentials != null) return cachedCredentials;
            cachedCredentials = LolLcuCredentialSource.Find(root);
            cachedCredentialRoot = cachedCredentials == null ? null : root;
            nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(
                cachedCredentials == null ? 5 : 10);
            return cachedCredentials;
        }

        private void InvalidateCredentials()
        {
            cachedCredentials = null;
            cachedCredentialRoot = null;
            nextCredentialRefreshUtc = DateTime.MinValue;
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
                string discoveredRoot = LolInstallDiscovery.FindLolRoot(preferredRoot);
                string discoveredWeGame = LolInstallDiscovery.FindWeGameRoot(
                    preferredWeGame, discoveredRoot);
                bool rootChanged;
                bool weGameChanged;
                lock (stateLock)
                {
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

        private void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null)
            {
                try { handler(); } catch { }
            }
        }
    }
}
