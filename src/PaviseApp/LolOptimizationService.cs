using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class LolOptimizationService : IDisposable
    {
        private readonly ProcessActionFailureBudget headlessFailures = new ProcessActionFailureBudget();
        private readonly HashSet<string> exitedHeadlessLeases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#if PAVISE_SELFTEST
        internal Func<string, bool, Func<bool>, string> FindLolRootForTest;
        internal Func<string, string, bool, Func<bool>, string> FindWeGameRootForTest;
#endif

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

        internal static int ProcessEventWakeThrottleMs
        {
            get
            {
                return 5000;
            }
        }

        public bool Enabled
        {
            get
            {
                lock (stateLock)
                {
                    return enabled;
                }
            }
            set
            {
                bool enabledChanged;
                lock (stateLock)
                {
                    enabledChanged = enabled != value;
                    enabled = value;
                    if (enabledChanged && value)
                    {
                        discoveryRequested = true;
                        discoveryMisses = 0;
                        nextDiscoveryUtc = DateTime.MinValue;
                    }
                    updatedUtc = DateTime.UtcNow;
                }
                if (enabledChanged)
                {
                    Settings.Save("LolColumnEnabled", value);
                    Poke();
                    RaiseChanged();
                }
            }
        }

        public bool CleanupEnabled
        {
            get
            {
                lock (stateLock)
                {
                    return cleanupEnabled;
                }
            }
            set
            {
                bool cleanupChanged;
                lock (stateLock)
                {
                    cleanupChanged = cleanupEnabled != value;
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
                if (cleanupChanged)
                {
                    Settings.Save("LolColumnCleanupEnabled", value);
                    Poke();
                    RaiseChanged();
                }
            }
        }

        public bool HeadlessEnabled
        {
            get
            {
                lock (stateLock)
                {
                    return headlessEnabled;
                }
            }
            set
            {
                bool headlessChanged;
                lock (stateLock)
                {
                    headlessChanged = headlessEnabled != value;
                    headlessEnabled = value;
                    updatedUtc = DateTime.UtcNow;
                }
                if (headlessChanged)
                {
                    Settings.Save("LolColumnHeadlessEnabled", value);
                    Poke();
                    RaiseChanged();
                }
            }
        }

        public event Action Changed;

        public LolOptimizationService()
        {
            TimerCallback callback = delegate(object state)
            {
                ProcessWakeTimerElapsed(state);
            };
            processWakeTimer = new Timer(callback, null, -1, -1);
            enabled = Settings.Load("LolColumnEnabled", false);
            cleanupEnabled = Settings.Load("LolColumnCleanupEnabled", false);
            headlessEnabled = Settings.Load("LolColumnHeadlessEnabled", false);
            lolRoot = Settings.LoadStr("LolInstallPath", null);
            weGameRoot = Settings.LoadStr("LolWeGamePath", null);
            phase = "";
            lastAction = Lang.T("lol.act.waitclient");
            lastError = "";
            updatedUtc = DateTime.UtcNow;
        }

        public void Start()
        {
            lock (stateLock)
            {
                if (disposed || running || (worker != null && worker.IsAlive))
                {
                    return;
                }
                enabled = Settings.Load("LolColumnEnabled", enabled);
                cleanupEnabled = Settings.Load("LolColumnCleanupEnabled", cleanupEnabled);
                headlessEnabled = Settings.Load("LolColumnHeadlessEnabled", headlessEnabled);
                lolRoot = Settings.LoadStr("LolInstallPath", lolRoot);
                weGameRoot = Settings.LoadStr("LolWeGamePath", weGameRoot);
                running = true;
                processWakePending = false;
                nextProcessWakeUtc = DateTime.MinValue;
                try
                {
                    processWakeTimer.Change(-1, -1);
                }
                catch
                {
                }
                lastError = "";
                updatedUtc = DateTime.UtcNow;
                stopEvent.Reset();
                int generation = ++runGeneration;
                worker = new Thread((ThreadStart)delegate
                {
                    WorkerLoop(generation);
                });
                worker.IsBackground = true;
                worker.Name = "Pavise LoL Runtime";
                worker.Start();
            }
            RaiseChanged();
        }

        public void Stop()
        {
            Thread thread;
            bool wasRunning;
            lock (stateLock)
            {
                thread = worker;
                wasRunning = running || (thread != null && thread.IsAlive);
                running = false;
                processWakePending = false;
                runGeneration++;
                updatedUtc = DateTime.UtcNow;
            }
            try
            {
                processWakeTimer.Change(-1, -1);
            }
            catch
            {
            }
            try
            {
                stopEvent.Set();
            }
            catch
            {
            }
            try
            {
                pokeEvent.Set();
            }
            catch
            {
            }
            if (thread != null && thread != Thread.CurrentThread)
            {
                try
                {
                    thread.Join(3000);
                }
                catch
                {
                }
            }
            if (Monitor.TryEnter(actionLock, 1000))
            {
                try
                {
                    InvalidateCredentials();
                }
                finally
                {
                    Monitor.Exit(actionLock);
                }
            }
            lock (stateLock)
            {
                worker = ((thread != null && thread.IsAlive) ? thread : null);
                updatedUtc = DateTime.UtcNow;
            }
            if (wasRunning)
            {
                RaiseChanged();
            }
        }

        public void Refresh()
        {
            InvalidateDiscovery();
            Poke();
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
                LolOptimizationSnapshot lolOptimizationSnapshot = new LolOptimizationSnapshot(running, enabled, cleanupEnabled, headlessEnabled, lolRoot, weGameRoot, installationFound, weGameFound, clientRunning, gameRunning, lcuReady, phase, headlessActive, weGameProcessCount, crossProcessCount, uxProcessCount, cleanedProcessCount, releasedWorkingSetBytes, lastAction, lastError, updatedUtc, lastActionUtc, discovering);
                lolOptimizationSnapshot.WeGameMainRunning = weGameMainRunning;
                return lolOptimizationSnapshot;
            }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
            }
            Stop();
            bool workerStopped;
            lock (stateLock)
            {
                workerStopped = worker == null || !worker.IsAlive;
            }
            if (workerStopped)
            {
                DisposeWaitHandles();
            }
        }

        private void DisposeWaitHandles()
        {
            if (Interlocked.Exchange(ref waitHandlesDisposed, 1) != 0)
            {
                return;
            }
            try
            {
                processWakeTimer.Dispose();
            }
            catch
            {
            }
            try
            {
                stopEvent.Dispose();
            }
            catch
            {
            }
            try
            {
                pokeEvent.Dispose();
            }
            catch
            {
            }
        }
    }
}
