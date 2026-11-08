using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
	internal sealed class LolOptimizationService : IDisposable
	{
		private readonly ProcessActionFailureBudget headlessFailures = new ProcessActionFailureBudget();
		private readonly HashSet<string> exitedHeadlessLeases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#if PAVISE_SELFTEST
		internal Func<string, bool, Func<bool>, string> FindLolRootForTest;
		internal Func<string, string, bool, Func<bool>, string> FindWeGameRootForTest;
#endif

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
				bool matchesEnabled;
				lock (stateLock)
				{
					matchesEnabled = enabled != value;
					enabled = value;
					if (matchesEnabled && value)
					{
						discoveryRequested = true;
						discoveryMisses = 0;
						nextDiscoveryUtc = DateTime.MinValue;
					}
					updatedUtc = DateTime.UtcNow;
				}
				if (matchesEnabled)
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
				bool matchesCleanupEnabled;
				lock (stateLock)
				{
					matchesCleanupEnabled = cleanupEnabled != value;
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
				if (matchesCleanupEnabled)
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
				bool matchesHeadlessEnabled;
				lock (stateLock)
				{
					matchesHeadlessEnabled = headlessEnabled != value;
					headlessEnabled = value;
					updatedUtc = DateTime.UtcNow;
				}
				if (matchesHeadlessEnabled)
				{
					Settings.Save("LolColumnHeadlessEnabled", value);
					Poke();
					RaiseChanged();
				}
			}
		}

		public event Action Changed;

		private LolLcuCredentials ResolveCredentials(string root, bool isClientRunning, out bool ready)
		{
			ready = false;
			if (!isClientRunning)
			{
				InvalidateCredentials();
				return null;
			}
			if (!string.Equals(cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
			{
				InvalidateCredentials();
			}
			LolLcuCredentials lolLcuCredentials = cachedCredentials;
			if (lolLcuCredentials != null)
			{
				ready = LolLcuClient.IsReady(lolLcuCredentials);
				sessionVerified = ready;
				if (ready)
				{
					lcuFailureStreak = 0;
					credentialLookupFailures = 0;
					nextCredentialRefreshUtc = DateTime.MinValue;
					return lolLcuCredentials;
				}
				if (DateTime.UtcNow < nextCredentialRefreshUtc)
				{
					return lolLcuCredentials;
				}
			}
			else if (DateTime.UtcNow < nextCredentialRefreshUtc)
			{
				return null;
			}
			lolLcuCredentials = LolLcuCredentialSource.Find(root);
			if (lolLcuCredentials == null)
			{
				ScheduleCredentialRetry(root);
				return null;
			}
			cachedCredentials = lolLcuCredentials;
			cachedCredentialRoot = root;
			nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10.0);
			ready = LolLcuClient.IsReady(lolLcuCredentials);
			sessionVerified = ready;
			if (ready)
			{
				lcuFailureStreak = 0;
				credentialLookupFailures = 0;
				nextCredentialRefreshUtc = DateTime.MinValue;
				nextSessionVerifyUtc = DateTime.UtcNow.AddSeconds(15.0);
			}
			return lolLcuCredentials;
		}

		private LolLcuCredentials ProbeLcu(string root, bool isClientRunning, out string currentPhase, out bool ready)
		{
			currentPhase = null;
			ready = false;
			if (!isClientRunning)
			{
				InvalidateCredentials();
				return null;
			}
			if (!string.Equals(cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
			{
				InvalidateCredentials();
			}
			LolLcuCredentials lolLcuCredentials = cachedCredentials;
			if (lolLcuCredentials == null)
			{
				if (DateTime.UtcNow < nextCredentialRefreshUtc)
				{
					return null;
				}
				lolLcuCredentials = LolLcuCredentialSource.Find(root);
				if (lolLcuCredentials == null)
				{
					ScheduleCredentialRetry(root);
					return null;
				}
				cachedCredentials = lolLcuCredentials;
				cachedCredentialRoot = root;
				nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10.0);
				sessionVerified = false;
				nextSessionVerifyUtc = DateTime.MinValue;
			}
			currentPhase = LolLcuClient.GetGameflowPhase(lolLcuCredentials);
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
				return lolLcuCredentials;
			}
			lcuFailureStreak = 0;
			credentialLookupFailures = 0;
			bool valuesEqual = string.Equals(currentPhase, "InProgress", StringComparison.OrdinalIgnoreCase);
			if (DateTime.UtcNow >= nextSessionVerifyUtc)
			{
				sessionVerified = LolLcuClient.IsReady(lolLcuCredentials);
				nextSessionVerifyUtc = DateTime.UtcNow.AddSeconds((!sessionVerified) ? 5 : (valuesEqual ? 60 : 15));
			}
			ready = sessionVerified;
			return lolLcuCredentials;
		}

		private void TryRecoverLoginChain(string weGame, LolProcessSnapshot processes, bool inProgress, bool cleanupChangedProcesses)
		{
			if (cleanupChangedProcesses && processes != null && processes.ClientRunning && !inProgress && !processes.GameRunning && processes.WeGameProcessCount <= 0 && !(DateTime.UtcNow < nextRecoveryUtc))
			{
				nextRecoveryUtc = DateTime.UtcNow.AddSeconds(120.0);
				StartWeGame(weGame, false);
			}
		}

		private LolLcuCredentials GetCredentialsForRestore(string root, bool force)
		{
			if (!string.Equals(cachedCredentialRoot, root, StringComparison.OrdinalIgnoreCase))
			{
				InvalidateCredentials();
			}
			if (cachedCredentials != null)
			{
				return cachedCredentials;
			}
			if (!force && DateTime.UtcNow < nextRestoreCredentialLookupUtc)
			{
				return null;
			}
			cachedCredentials = LolLcuCredentialSource.Find(root);
			if (cachedCredentials == null)
			{
				ScheduleCredentialRetry(root);
				ScheduleRestoreCredentialRetry();
				return null;
			}
			cachedCredentialRoot = root;
			nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(10.0);
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
			if (credentialLookupFailures < int.MaxValue)
			{
				credentialLookupFailures++;
			}
			nextCredentialRefreshUtc = DateTime.UtcNow.AddSeconds(CredentialRetrySeconds(credentialLookupFailures));
			sessionVerified = false;
			nextSessionVerifyUtc = DateTime.MinValue;
			lcuFailureStreak = 0;
		}

		private void ScheduleRestoreCredentialRetry()
		{
			if (restoreCredentialLookupFailures < int.MaxValue)
			{
				restoreCredentialLookupFailures++;
			}
			nextRestoreCredentialLookupUtc = DateTime.UtcNow.AddSeconds(RestoreCredentialRetrySeconds(restoreCredentialLookupFailures));
		}

		internal static int CredentialRetrySeconds(int failures)
		{
			if (failures <= 1)
			{
				return 5;
			}
			switch (failures)
			{
			case 2:
				return 10;
			case 3:
				return 30;
			default:
				return 60;
			}
		}

		internal static int RestoreCredentialRetrySeconds(int failures)
		{
			if (failures <= 1)
			{
				return 2;
			}
			switch (failures)
			{
			case 2:
				return 5;
			case 3:
				return 10;
			default:
				return 30;
			}
		}

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
			bool matchesRunning;
			lock (stateLock)
			{
				thread = worker;
				matchesRunning = running || (thread != null && thread.IsAlive);
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
			if (matchesRunning)
			{
				RaiseChanged();
			}
		}

		public void Refresh()
		{
			InvalidateDiscovery();
			Poke();
		}

		public void NotifyProcessChanges(ProcessChangeBatch batch)
		{
			if (batch == null)
			{
				return;
			}
			bool overflowed = batch.Overflowed;
			bool overflowed2 = batch.Overflowed;
			bool notifyProcessChangesReady = false;
			bool overflowed3 = batch.Overflowed;
			string root;
			string weGame;
			lock (stateLock)
			{
				if (!running || disposed)
				{
					return;
				}
				root = lolRoot;
				weGame = weGameRoot;
			}
			ProcessChange[] changes = batch.Changes;
			foreach (ProcessChange processChange in changes)
			{
				bool isCredentialDiscoveryStartEvent = IsCredentialDiscoveryStartEvent(processChange);
				if (processChange != null && (isCredentialDiscoveryStartEvent || LolRuntimeProcesses.IsRelevantProcessChange(processChange.Name, processChange.Path, root, weGame)))
				{
					overflowed = true;
					if (IsExactCleanupStartEvent(processChange, root, weGame))
					{
						overflowed2 = true;
					}
					if (IsCredentialSourceStartEvent(processChange, root))
					{
						notifyProcessChangesReady = true;
					}
					if (isCredentialDiscoveryStartEvent)
					{
						notifyProcessChangesReady = true;
						overflowed3 = true;
					}
				}
			}
			if (!overflowed)
			{
				return;
			}
			if (overflowed3)
			{
				InvalidateDiscovery();
			}
			if (notifyProcessChangesReady)
			{
				Interlocked.Exchange(ref credentialGenerationChanged, 1);
			}
			bool notifyProcessChangesReady2 = false;
			bool notifyProcessChangesReady3 = false;
			int dueTime = -1;
			DateTime utcNow = DateTime.UtcNow;
			bool matchesNotifyProcessChangesReady;
			lock (stateLock)
			{
				if (batch.Overflowed)
				{
					nextFullProcessScanUtc = DateTime.MinValue;
				}
				if (overflowed2)
				{
					cleanupDirty = true;
				}
				matchesNotifyProcessChangesReady = notifyProcessChangesReady || utcNow >= nextProcessWakeUtc;
				if (matchesNotifyProcessChangesReady)
				{
					nextProcessWakeUtc = utcNow.AddMilliseconds(5000.0);
					notifyProcessChangesReady3 = processWakePending;
					processWakePending = false;
				}
				else if (!processWakePending)
				{
					processWakePending = true;
					notifyProcessChangesReady2 = true;
					dueTime = ProcessWakeDelayMs(utcNow, nextProcessWakeUtc);
				}
				if (notifyProcessChangesReady3)
				{
					try
					{
						processWakeTimer.Change(-1, -1);
					}
					catch
					{
					}
				}
				if (notifyProcessChangesReady2)
				{
					try
					{
						processWakeTimer.Change(dueTime, -1);
					}
					catch
					{
					}
				}
			}
			if (matchesNotifyProcessChangesReady)
			{
				Poke();
			}
		}

		private void ProcessWakeTimerElapsed(object unused)
		{
			bool processWakeTimerElapsedReady = false;
			bool processWakeTimerElapsedReady2 = false;
			int dueTime = -1;
			lock (stateLock)
			{
				if (disposed || !running || !processWakePending)
				{
					return;
				}
				DateTime utcNow = DateTime.UtcNow;
				if (utcNow < nextProcessWakeUtc)
				{
					processWakeTimerElapsedReady2 = true;
					dueTime = ProcessWakeDelayMs(utcNow, nextProcessWakeUtc);
				}
				else
				{
					processWakePending = false;
					nextProcessWakeUtc = utcNow.AddMilliseconds(5000.0);
					processWakeTimerElapsedReady = true;
				}
			}
			if (processWakeTimerElapsedReady2)
			{
				try
				{
					processWakeTimer.Change(dueTime, -1);
				}
				catch
				{
				}
			}
			if (processWakeTimerElapsedReady)
			{
				Poke();
			}
		}

		internal static int ProcessWakeDelayMs(DateTime nowUtc, DateTime deadlineUtc)
		{
			long remainingTicks = deadlineUtc.Ticks - nowUtc.Ticks;
			if (remainingTicks <= 0)
			{
				return 1;
			}
			long val = (remainingTicks + 10000 - 1) / 10000;
			return (int)Math.Min(2147483647L, Math.Max(1L, val));
		}

		internal static bool IsExactCleanupStartEvent(ProcessChange change, string root, string weGame)
		{
			if (change == null || change.Kind != 0 || string.IsNullOrEmpty(change.Path))
			{
				return false;
			}
			try
			{
				return LolRuntimeProcesses.IsCleanupTarget(change.Path, Path.GetFileName(change.Path), root, weGame);
			}
			catch
			{
				return false;
			}
		}

		internal static bool IsCredentialSourceStartEvent(ProcessChange change, string root)
		{
			if (change == null || change.Kind != 0 || !LolRuntimeProcesses.IsCredentialSourceName(change.Name))
			{
				return false;
			}
			if (string.IsNullOrEmpty(change.Path))
			{
				return true;
			}
			if (!LolRuntimeProcesses.IsUnder(change.Path, root))
			{
				return false;
			}
			string fileName;
			try
			{
				fileName = Path.GetFileName(change.Path);
			}
			catch
			{
				return false;
			}
			if (!string.Equals(fileName, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(fileName, "LeagueClientUx.exe", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		internal static bool IsCredentialDiscoveryStartEvent(ProcessChange change)
		{
			if (change == null || change.Kind != 0 || !LolRuntimeProcesses.IsCredentialSourceName(change.Name))
			{
				return false;
			}
			if (string.IsNullOrEmpty(change.Path))
			{
				return true;
			}
			string fileName;
			try
			{
				fileName = Path.GetFileName(change.Path);
			}
			catch
			{
				return false;
			}
			if (!string.Equals(fileName, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(fileName, "LeagueClientUx.exe", StringComparison.OrdinalIgnoreCase);
			}
			return true;
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
				LolProcessSnapshot lolProcessSnapshot = LolRuntimeProcesses.Scan(root, weGame);
				Interlocked.Exchange(ref credentialGenerationChanged, 0);
				InvalidateCredentials();
				bool ready;
				LolLcuCredentials lolLcuCredentials = ResolveCredentials(root, lolProcessSnapshot.ClientRunning, out ready);
				if (lolLcuCredentials == null || !ready)
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
				LolCleanupResult lolCleanupResult = LolRuntimeProcesses.Clean(root, weGame, true);
				bool isReady = LolLcuClient.IsReady(lolLcuCredentials);
				lock (stateLock)
				{
					cleanedProcessCount += lolCleanupResult.Count;
					releasedWorkingSetBytes += lolCleanupResult.WorkingSetBytes;
					lcuReady = isReady;
					if (isReady)
					{
						lastAction = ((lolCleanupResult.Count > 0) ? Lang.F("lol.act.cleaned", lolCleanupResult.Count.ToString()) : Lang.T("lol.act.cleanidle"));
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
						nextCleanupUtc = DateTime.UtcNow.AddSeconds(10.0);
					}
					else
					{
						nextCleanupUtc = DateTime.UtcNow.AddSeconds(120.0);
					}
					updatedUtc = DateTime.UtcNow;
				}
				LolProcessSnapshot processes = LolRuntimeProcesses.Scan(root, weGame);
				UpdateProcessState(processes);
				Logger.Log("英雄联盟专栏：精准净化完成，结束 " + lolCleanupResult.Count + " 个附加进程");
				if (!isReady)
				{
					InvalidateCredentials();
					TryRecoverLoginChain(weGame, processes, false, lolCleanupResult.Count > 0);
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
				ObserveHeadlessOwnerExit();
				LolLcuCredentials credentialsForRestore = GetCredentialsForRestore(root, true);
				if (credentialsForRestore == null)
				{
					SetError(Lang.T("lol.err.nosession"));
					return false;
				}
				bool tryRestoreUxBounded = TryRestoreUxBounded(credentialsForRestore, root);
				if (!tryRestoreUxBounded)
				{
					InvalidateCredentials();
					credentialsForRestore = GetCredentialsForRestore(root, true);
					tryRestoreUxBounded = TryRestoreUxBounded(credentialsForRestore, root);
					if (!tryRestoreUxBounded && credentialsForRestore != null)
					{
						ScheduleRestoreCredentialRetry();
					}
				}
				bool matchesTryRestoreUxBounded = tryRestoreUxBounded && (LolRuntimeProcesses.IsGameRunning(root) || IsSameMatchPhase(LolLcuClient.GetGameflowPhase(credentialsForRestore)));
				lock (stateLock)
				{
					if (tryRestoreUxBounded)
					{
						headlessActive = false;
						headlessExitSamples = 0;
						manualUxBypassGame = matchesTryRestoreUxBounded;
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
				bool restoreNowSucceeded = tryRestoreUxBounded;
				if (tryRestoreUxBounded)
				{
					restoreNowSucceeded = ClearOwnedHeadlessMark();
					if (!restoreNowSucceeded)
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
				return restoreNowSucceeded;
			}
		}

		public bool HasRecordedHeadlessState()
		{
			bool matchesHeadlessActive;
			lock (stateLock)
			{
				matchesHeadlessActive = headlessActive || !string.IsNullOrEmpty(headlessLeaseRoot);
			}
			if (!matchesHeadlessActive)
			{
				return LolHeadlessLease.Exists();
			}
			return true;
		}

		private bool StartWeGame(string weGame, bool updateAction)
		{
			string weGameExecutable = LolInstallDiscovery.FindWeGameExecutable(weGame);
			if (weGameExecutable == null)
			{
				if (updateAction)
				{
					SetError(Lang.T("lol.err.nowegame"));
				}
				return false;
			}
			if (!UserLaunch.Start(weGameExecutable))
			{
				if (updateAction)
				{
					SetError(Lang.T("lol.err.wegamestart"));
				}
				return false;
			}
			if (updateAction)
			{
				SetAction(Lang.T("lol.act.wegamestarting"));
			}
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
			bool anyConditionMet;
			lock (stateLock)
			{
				anyConditionMet = worker == null || !worker.IsAlive;
			}
			if (anyConditionMet)
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

		private bool SetHeadlessMark(string root, int gamePid, long gameCreation)
		{
			string existing; LolHeadlessLeaseInfo previous;
			if (!Settings.TryLoadStr("LolHeadlessEngagedRoot", out existing)) return false;
			if (!string.IsNullOrEmpty(existing) && (!LolHeadlessLease.TryParse(existing, out previous)
				|| !SameRootPath(previous.Root, root) || previous.GamePid != gamePid || previous.GameCreation != gameCreation)) return false;
			if (!LolHeadlessLease.Set(root, gamePid, gameCreation))
			{
				return false;
			}
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
			string headlessLeaseRootSnapshot;
			int headlessLeaseGamePidSnapshot;
			long headlessLeaseGameCreationSnapshot;
			lock (stateLock)
			{
				headlessLeaseRootSnapshot = headlessLeaseRoot;
				headlessLeaseGamePidSnapshot = headlessLeaseGamePid;
				headlessLeaseGameCreationSnapshot = headlessLeaseGameCreation;
			}
			if (string.IsNullOrEmpty(headlessLeaseRootSnapshot))
			{
				return true;
			}
			string recoveryStage = HeadlessRecoveryStage(headlessLeaseRootSnapshot, headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot, "clear-mark");
			if (!headlessFailures.Allowed(headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot, headlessLeaseRootSnapshot, recoveryStage)) return false;
			bool matchesClearIfMatches = headlessLeaseGamePidSnapshot > 0 && headlessLeaseGameCreationSnapshot > 0 && LolHeadlessLease.ClearIfMatches(headlessLeaseRootSnapshot, headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot);
			string current; LolHeadlessLeaseInfo remaining;
			bool confirmedReleased = Settings.TryLoadStr("LolHeadlessEngagedRoot", out current)
				&& (string.IsNullOrEmpty(current) || LolHeadlessLease.TryParse(current, out remaining)
				&& (!SameRootPath(remaining.Root, headlessLeaseRootSnapshot) || remaining.GamePid != headlessLeaseGamePidSnapshot || remaining.GameCreation != headlessLeaseGameCreationSnapshot));
			bool matchesMatchesClearIfMatches = matchesClearIfMatches || confirmedReleased;
			if (matchesMatchesClearIfMatches) headlessFailures.Succeeded(headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot, headlessLeaseRootSnapshot, recoveryStage);
			else Logger.Warn("LoL recovery mark failure=" + headlessFailures.Failed(headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot, headlessLeaseRootSnapshot, recoveryStage) + "/3; owned mark retained");
			lock (stateLock)
			{
				if (SameLocalLeaseLocked(headlessLeaseRootSnapshot, headlessLeaseGamePidSnapshot, headlessLeaseGameCreationSnapshot))
				{
					headlessLeaseReleasePending = !matchesMatchesClearIfMatches;
					if (matchesMatchesClearIfMatches)
					{
						headlessLeaseRoot = null;
						headlessLeaseGamePid = 0;
						headlessLeaseGameCreation = 0L;
					}
				}
			}
			return matchesMatchesClearIfMatches;
		}

		private void RememberHeadlessLease(string root)
		{
			string stored;
			LolHeadlessLeaseInfo info;
			if (!Settings.TryLoadStr("LolHeadlessEngagedRoot", out stored)) return;
			if (!string.IsNullOrEmpty(stored) && !LolHeadlessLease.TryParse(stored, out info)) return;
			bool matchesTryParse = LolHeadlessLease.TryParse(stored, out info) && SameRootPath(info.Root, root);
			lock (stateLock)
			{
				if (matchesTryParse)
				{
					bool sameLocalLeaseLockedSucceeded = SameLocalLeaseLocked(info.Root, info.GamePid, info.GameCreation);
					headlessLeaseRoot = info.Root;
					headlessLeaseGamePid = info.GamePid;
					headlessLeaseGameCreation = info.GameCreation;
					if (!sameLocalLeaseLockedSucceeded)
					{
						headlessLeaseReleasePending = false;
					}
				}
				else if (SameRootPath(headlessLeaseRoot, root))
				{
					headlessLeaseRoot = null;
					headlessLeaseGamePid = 0;
					headlessLeaseGameCreation = 0L;
					headlessLeaseReleasePending = false;
				}
			}
		}

		private bool SameLocalLeaseLocked(string root, int pid, long creation)
		{
			if (headlessLeaseGamePid == pid && headlessLeaseGameCreation == creation)
			{
				return SameRootPath(headlessLeaseRoot, root);
			}
			return false;
		}

		private void ForgetRememberedHeadlessLease()
		{
			string stored;
			if (!Settings.TryLoadStr("LolHeadlessEngagedRoot", out stored) || !string.IsNullOrEmpty(stored)) return;
			lock (stateLock)
			{
				headlessLeaseRoot = null;
				headlessLeaseGamePid = 0;
				headlessLeaseGameCreation = 0L;
				headlessLeaseReleasePending = false;
			}
		}

		private static bool SameRootPath(string left, string right)
		{
			try
			{
				string getFullPathTrimmed = Path.GetFullPath(left ?? "").TrimEnd('\\');
				string getFullPathTrimmed2 = Path.GetFullPath(right ?? "").TrimEnd('\\');
				return getFullPathTrimmed.Length > 2 && getFullPathTrimmed2.Length > 2 && string.Equals(getFullPathTrimmed, getFullPathTrimmed2, StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
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
			bool anyConditionMet;
			lock (stateLock)
			{
				anyConditionMet = clientRunning || gameRunning || lcuReady || phase.Length > 0 || weGameProcessCount != 0 || crossProcessCount != 0 || uxProcessCount != 0;
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
			if (anyConditionMet)
			{
				RaiseChanged();
			}
		}

		internal static bool IsVerifiedCleanupSession(bool clientRunning, bool lcuReady, string currentPhase, bool gameRunning, bool matchActive)
		{
			if (!clientRunning || !lcuReady || string.IsNullOrEmpty(currentPhase))
			{
				return false;
			}
			if (IsSameMatchPhase(currentPhase))
			{
				if (matchActive)
				{
					return gameRunning;
				}
				return false;
			}
			if (!gameRunning)
			{
				return IsConfirmedMatchExitPhase(currentPhase);
			}
			return false;
		}

		internal static bool ShouldResetMatchSafetyState(bool clientExitConfirmed, bool matchExitConfirmed, bool matchStateActive)
		{
			if (!clientExitConfirmed)
			{
				if (matchExitConfirmed)
				{
					return matchStateActive;
				}
				return false;
			}
			return true;
		}

		internal static bool RegisterUxRespawn(DateTime nowUtc, Queue<DateTime> recent)
		{
			if (recent == null)
			{
				throw new ArgumentNullException("recent");
			}
			while (recent.Count > 0 && (nowUtc < recent.Peek() || (nowUtc - recent.Peek()).TotalSeconds >= 60.0))
			{
				recent.Dequeue();
			}
			recent.Enqueue(nowUtc);
			return recent.Count > 3;
		}

		internal static bool RegisterCleanupKillCycle(DateTime nowUtc, Queue<DateTime> recent)
		{
			if (recent == null)
			{
				throw new ArgumentNullException("recent");
			}
			while (recent.Count > 0 && (nowUtc < recent.Peek() || (nowUtc - recent.Peek()).TotalSeconds >= 60.0))
			{
				recent.Dequeue();
			}
			recent.Enqueue(nowUtc);
			return recent.Count > 3;
		}

		internal static bool IsConfirmedMatchExitPhase(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return false;
			}
			if (!string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "Lobby", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "Matchmaking", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "CheckedIntoTournament", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "ReadyCheck", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "ChampSelect", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "FailedToLaunch", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "PreEndOfGame", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "EndOfGame", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "WaitingForStats", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(value, "TerminatedInError", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		internal static bool IsSameMatchPhase(string value)
		{
			if (!string.Equals(value, "InProgress", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "Reconnect", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(value, "GameStart", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		internal static bool ConfirmClientExit(bool scanSucceeded, bool clientRunning, bool gameRunning, ref int absentSamples)
		{
			if (scanSucceeded)
			{
				if (clientRunning || gameRunning)
				{
					absentSamples = 0;
				}
				else if (absentSamples < int.MaxValue)
				{
					absentSamples++;
				}
			}
			return absentSamples >= 4;
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
			if (processes == null)
			{
				return;
			}
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
			try
			{
				pokeEvent.Set();
			}
			catch
			{
			}
		}

		internal static bool SamePublishedState(LolOptimizationSnapshot left, LolOptimizationSnapshot right)
		{
			if (left == null || right == null)
			{
				return left == right;
			}
			if (left.Running == right.Running && left.Enabled == right.Enabled && left.CleanupEnabled == right.CleanupEnabled && left.HeadlessEnabled == right.HeadlessEnabled && string.Equals(left.LolRoot, right.LolRoot, StringComparison.OrdinalIgnoreCase) && string.Equals(left.WeGameRoot, right.WeGameRoot, StringComparison.OrdinalIgnoreCase) && left.InstallationFound == right.InstallationFound && left.WeGameFound == right.WeGameFound && left.ClientRunning == right.ClientRunning && left.GameRunning == right.GameRunning && left.LcuReady == right.LcuReady && string.Equals(left.Phase, right.Phase, StringComparison.Ordinal) && left.HeadlessActive == right.HeadlessActive && left.WeGameProcessCount == right.WeGameProcessCount && left.WeGameMainRunning == right.WeGameMainRunning && left.CrossProcessCount == right.CrossProcessCount && left.UxProcessCount == right.UxProcessCount && left.CleanedProcessCount == right.CleanedProcessCount && left.ReleasedWorkingSetBytes == right.ReleasedWorkingSetBytes && string.Equals(left.LastAction, right.LastAction, StringComparison.Ordinal) && string.Equals(left.LastError, right.LastError, StringComparison.Ordinal))
			{
				return left.LastActionUtc == right.LastActionUtc;
			}
			return false;
		}

		internal void RaiseChanged()
		{
			Action changed = this.Changed;
			if (changed == null)
			{
				return;
			}
			LolOptimizationSnapshot snapshot = GetSnapshot();
			lock (publishLock)
			{
				if (SamePublishedState(lastPublishedSnapshot, snapshot))
				{
					return;
				}
				lastPublishedSnapshot = snapshot;
			}
			try
			{
				changed();
			}
			catch
			{
			}
		}

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
			bool matchesEnabled;
			bool headlessActiveSnapshot;
			lock (stateLock)
			{
				matchesEnabled = enabled && (cleanupEnabled || headlessEnabled);
				headlessActiveSnapshot = headlessActive;
			}
			bool hasHeadlessMark = HasHeadlessMark();
			if (!hasHeadlessMark)
			{
				ForgetRememberedHeadlessLease();
			}
			if (!matchesEnabled && !headlessActiveSnapshot && !hasHeadlessMark)
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
			bool matchesUtcNow;
			lock (stateLock)
			{
				matchesUtcNow = DateTime.UtcNow < nextFullProcessScanUtc;
				if (!matchesUtcNow)
				{
					nextFullProcessScanUtc = DateTime.UtcNow.AddSeconds(60.0);
				}
			}
			return LolRuntimeProcesses.Scan(root, weGame, matchesUtcNow);
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

		private bool AdoptHeadlessLease(string root, LolProcessSnapshot processes, bool active, bool sameMatch)
		{
			int pid;
			long creation;
			if (!active && sameMatch && processes.GameRunning && LolRuntimeProcesses.TryGetGameIdentity(root, processes.GameProcessId, out pid, out creation) && LolHeadlessLease.Matches(root, pid, creation))
			{
				lock (stateLock)
				{
					headlessActive = true;
					active = true;
				}
			}
			return active;
		}

		private bool ReleaseOnClientExit(bool active, bool clientExitConfirmed)
		{
			if (clientExitConfirmed) ObserveHeadlessOwnerExit();
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

		private void HandleUxRespawn(LolLcuCredentials credentials, string root, string weGame, bool serviceEnabled, bool noUxEnabled, bool inProgress, ref bool active, ref bool manualBypass, ref LolProcessSnapshot processes)
		{
			bool handleUxRespawnReady = false;
			if (active && serviceEnabled && noUxEnabled && !manualBypass && inProgress && processes.MainUxProcessCount > 0)
			{
				lock (stateLock)
				{
					headlessActive = false;
					headlessExitSamples = 0;
					nextUxKillUtc = DateTime.UtcNow.AddSeconds(10.0);
					handleUxRespawnReady = RegisterUxRespawn(DateTime.UtcNow, uxRespawns);
					if (handleUxRespawnReady)
					{
						manualUxBypassGame = true;
					}
				}
				active = false;
			}
			if (handleUxRespawnReady)
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
			bool runRestoreStageReady = false;
			if (active)
			{
				lock (stateLock)
				{
					if (!serviceEnabled || !noUxEnabled || manualBypass)
					{
						headlessExitSamples = 0;
						runRestoreStageReady = true;
					}
					else if (ready && matchExitConfirmed)
					{
						if (headlessExitSamples < int.MaxValue)
						{
							headlessExitSamples++;
						}
						runRestoreStageReady = headlessExitSamples >= 2;
					}
					else
					{
						headlessExitSamples = 0;
					}
				}
			}
			if (runRestoreStageReady)
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
			bool anyConditionMet;
			bool cleanupCircuitOpenSnapshot;
			lock (stateLock)
			{
				anyConditionMet = utcNow >= nextCleanupUtc || (cleanupDirty && !cleanupBurstActive);
				cleanupCircuitOpenSnapshot = cleanupCircuitOpen;
			}
			if (!serviceEnabled || !cleanEnabled || !isVerifiedCleanupSession || cleanupCircuitOpenSnapshot || !anyConditionMet)
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
			bool matchesIsReady = lolCleanupResult.Count == 0 || LolLcuClient.IsReady(credentials);
			DateTime utcNow2 = DateTime.UtcNow;
			bool runCleanupStageReady = false;
			lock (stateLock)
			{
				cleanedProcessCount += lolCleanupResult.Count;
				releasedWorkingSetBytes += lolCleanupResult.WorkingSetBytes;
				lcuReady = matchesIsReady;
				if (lolCleanupResult.Count > 0)
				{
					runCleanupStageReady = RegisterCleanupKillCycle(utcNow2, cleanupKillCycles) || !matchesIsReady;
					if (runCleanupStageReady)
					{
						cleanupCircuitOpen = true;
					}
				}
				if (runCleanupStageReady)
				{
					cleanupDirty = false;
					cleanupBurstActive = false;
					cleanupFollowUpsRemaining = 0;
					nextCleanupUtc = DateTime.MaxValue;
					lastAction = Lang.T("lol.act.cleanupcircuit");
					lastError = (matchesIsReady ? "" : Lang.T("lol.err.sessionlost"));
					lastActionUtc = utcNow2;
				}
				else if (!matchesIsReady)
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
				if (!runCleanupStageReady)
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
			ready = matchesIsReady;
			if (lolCleanupResult.Count > 0)
			{
				processes = LolRuntimeProcesses.Scan(root, weGame, true);
			}
			if (!matchesIsReady && lolCleanupResult.Count > 0)
			{
				InvalidateCredentials(false);
				ScheduleCredentialRetry(root);
				TryRecoverLoginChain(weGame, processes, inProgress, true);
			}
		}

		private void RunHeadlessKillStage(LolLcuCredentials credentials, string root, string weGame, bool serviceEnabled, bool noUxEnabled, bool ready, bool inProgress, ref LolProcessSnapshot processes)
		{
			if (!serviceEnabled || !noUxEnabled || !ready || !inProgress)
			{
				return;
			}
			bool allConditionsMet;
			lock (stateLock)
			{
				allConditionsMet = !headlessActive && !manualUxBypassGame && processes.MainUxProcessCount > 0 && DateTime.UtcNow >= nextUxKillUtc;
			}
			if (!allConditionsMet)
			{
				return;
			}
			lock (stateLock)
			{
				nextUxKillUtc = DateTime.UtcNow.AddSeconds(10.0);
			}
			string gameflowPhase = LolLcuClient.GetGameflowPhase(credentials);
			if (!string.Equals(gameflowPhase, "InProgress", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			int pid;
			long creation;
			bool gameIdentity = LolRuntimeProcesses.TryGetGameIdentity(root, processes.GameProcessId, out pid, out creation);
			if (gameIdentity && !headlessFailures.Allowed(pid, creation, root, "apply")) return;
			bool runHeadlessKillStageReady = false, runHeadlessKillStageReady2 = false, runHeadlessKillStageReady3 = false;
			try
			{
				runHeadlessKillStageReady = gameIdentity && SetHeadlessMark(root, pid, creation);
				runHeadlessKillStageReady2 = runHeadlessKillStageReady && LolWatchdog.StartDetached(root, pid, creation);
				runHeadlessKillStageReady3 = runHeadlessKillStageReady2 && LolLcuClient.KillUx(credentials, root);
			}
			finally
			{
				if (runHeadlessKillStageReady3) headlessFailures.Succeeded(pid, creation, root, "apply");
				else if (gameIdentity) Logger.Warn("LoL headless apply failure=" + headlessFailures.Failed(pid, creation, root, "apply")
					+ "/3 target=" + pid + "/" + creation + "; recovery ownership retained where present");
			}
			// Timeout or request failure does not prove the helper/mutation absent; retain the lease until verified restoration.
			lock (stateLock)
			{
				if (runHeadlessKillStageReady3)
				{
					headlessActive = true;
					headlessExitSamples = 0;
					lastAction = Lang.T("lol.act.headless");
					lastError = "";
					lastActionUtc = DateTime.UtcNow;
				}
				else
				{
					lastError = ((!gameIdentity) ? Lang.T("lol.err.gameidentity") : ((!runHeadlessKillStageReady) ? Lang.T("lol.err.lease") : ((!runHeadlessKillStageReady2) ? Lang.T("lol.err.watchdog") : Lang.T("lol.err.killux"))));
					lastActionUtc = DateTime.UtcNow;
				}
				updatedUtc = DateTime.UtcNow;
			}
			if (runHeadlessKillStageReady3)
			{
				Logger.Log("英雄联盟专栏：独立恢复器已确认守护，对局真无头已启用");
				Thread.Sleep(400);
				processes = LolRuntimeProcesses.Scan(root, weGame, true);
			}
		}

		private bool RestoreCore(LolLcuCredentials credentials, string root)
		{
			return RestoreCore(credentials, root, false);
		}

		private bool RestoreCore(LolLcuCredentials credentials, string root, bool force)
		{
			DateTime utcNow = DateTime.UtcNow;
			if (!force && utcNow < nextRestoreCredentialLookupUtc)
			{
				return false;
			}
			if (credentials == null)
			{
				credentials = GetCredentialsForRestore(root, force);
			}
			bool tryRestoreUxBounded = TryRestoreUxBounded(credentials, root);
			lock (stateLock)
			{
				string a = lastAction;
				string a2 = lastError;
				if (tryRestoreUxBounded)
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
					{
						ScheduleRestoreCredentialRetry();
					}
				}
				if (!string.Equals(a, lastAction, StringComparison.Ordinal) || !string.Equals(a2, lastError, StringComparison.Ordinal))
				{
					lastActionUtc = DateTime.UtcNow;
				}
				updatedUtc = DateTime.UtcNow;
			}
			if (tryRestoreUxBounded)
			{
				ClearOwnedHeadlessMark();
				Logger.Log("英雄联盟专栏：大厅界面已自动恢复");
			}
			return tryRestoreUxBounded;
		}

		private string HeadlessRecoveryStage(string root, int pid, long birth, string stage)
		{
			lock (stateLock) return stage + (exitedHeadlessLeases.Contains(root + "|" + pid + "|" + birth) ? "-exited" : "");
		}

		private void ObserveHeadlessOwnerExit()
		{
			string root; int pid; long birth;
			lock (stateLock)
			{
				root = headlessLeaseRoot; pid = headlessLeaseGamePid; birth = headlessLeaseGameCreation;
				if (pid <= 0 || birth <= 0 || string.IsNullOrEmpty(root) || exitedHeadlessLeases.Contains(root + "|" + pid + "|" + birth)) return;
			}
			if (!LolWatchdog.OriginalGameIdentityGone(root, pid, birth)) return;
			lock (stateLock) if (SameLocalLeaseLocked(root, pid, birth)) exitedHeadlessLeases.Add(root + "|" + pid + "|" + birth);
		}

		private bool TryRestoreUxBounded(LolLcuCredentials credentials, string root)
		{
			// Missing credentials are a normal discovery/availability observation, not a failed write.
			if (credentials == null) return false;
			int pid; long birth;
			lock (stateLock) { pid = headlessLeaseGamePid; birth = headlessLeaseGameCreation; }
			string stage = HeadlessRecoveryStage(root, pid, birth, "restore");
			if (!headlessFailures.Allowed(pid, birth, root, stage)) return false;
			bool ok = false;
			try { ok = LolLcuClient.RestoreUx(credentials, root); return ok; }
			finally
			{
				if (ok) headlessFailures.Succeeded(pid, birth, root, stage);
				else Logger.Warn("LoL UI recovery failure=" + headlessFailures.Failed(pid, birth, root, stage) + "/3 target=" + pid + "/" + birth + "; lease retained");
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

		public void CancelDiscovery()
		{
			Interlocked.Exchange(ref discoveryCancelRequested, 1);
		}

		public bool AdoptRootFromPath(string executablePath)
		{
			string message = null;
			try
			{
				string directoryName = (string.IsNullOrEmpty(executablePath) ? null : Path.GetDirectoryName(executablePath));
				for (int i = 0; i < 6; i++)
				{
					if (string.IsNullOrEmpty(directoryName))
					{
						break;
					}
					if (LolInstallDiscovery.IsValidLolRoot(directoryName))
					{
						message = directoryName;
						break;
					}
					directoryName = Path.GetDirectoryName(directoryName);
				}
			}
			catch
			{
				message = null;
			}
			if (message == null)
			{
				return false;
			}
			bool anyConditionMet;
			lock (stateLock)
			{
				anyConditionMet = !string.Equals(lolRoot, message, StringComparison.OrdinalIgnoreCase) || !installationFound;
				lolRoot = message;
				installationFound = true;
				discoveryRequested = false;
				discoveryMisses = 0;
				nextDiscoveryUtc = DateTime.MinValue;
				updatedUtc = DateTime.UtcNow;
			}
			if (!anyConditionMet)
			{
				return true;
			}
			InvalidateCredentials();
			Settings.SaveStr("LolInstallPath", message);
			Logger.Log("英雄联盟增强 按游戏库条目定位安装目录 " + message);
			Poke();
			RaiseChanged();
			return true;
		}

		private bool DiscoveryCancelRequested()
		{
			return Volatile.Read(ref discoveryCancelRequested) != 0;
		}

		private bool RuntimeStopped()
		{
			lock (stateLock)
			{
				return disposed || !running;
			}
		}

		private void SetDiscovering(bool value)
		{
			lock (stateLock)
			{
				if (discovering == value)
				{
					return;
				}
				discovering = value;
				updatedUtc = DateTime.UtcNow;
			}
			Action changed = this.Changed;
			if (changed != null)
			{
				try
				{
					changed();
				}
				catch
				{
				}
			}
		}

		private void Discover(out string root, out string weGame, bool force, bool stopWithWorker = false)
		{
			string lolRootSnapshot;
			string weGameRootSnapshot;
			bool matchesForce;
			lock (stateLock)
			{
				lolRootSnapshot = lolRoot;
				weGameRootSnapshot = weGameRoot;
				matchesForce = force || DateTime.UtcNow >= nextDiscoveryUtc;
			}
			if (matchesForce)
			{
				bool isValidLolRoot = LolInstallDiscovery.IsValidLolRoot(lolRootSnapshot);
				if (!force && !isValidLolRoot)
				{
					bool discoveryRequestedSnapshot;
					lock (stateLock)
					{
						discoveryRequestedSnapshot = discoveryRequested;
					}
					if (!discoveryRequestedSnapshot)
					{
						lock (stateLock)
						{
							nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(120.0);
						}
						root = null;
						weGame = weGameRootSnapshot;
						return;
					}
				}
				bool discoveryRequestedSnapshot2;
				lock (stateLock)
				{
					discoveryRequestedSnapshot2 = discoveryRequested;
				}
				bool matchesDiscoveryRequestedSnapshot2 = !isValidLolRoot || discoveryRequestedSnapshot2;
				bool discoverReady = matchesDiscoveryRequestedSnapshot2;
				Func<bool> func = null;
				if (discoverReady)
				{
					Interlocked.Exchange(ref discoveryCancelRequested, 0);
					func = () => DiscoveryCancelRequested() || (stopWithWorker && RuntimeStopped());
					SetDiscovering(true);
				}
				string lolRoot2;
				string weGameRoot2;
				try
				{
#if PAVISE_SELFTEST
					lolRoot2 = FindLolRootForTest != null ? FindLolRootForTest(lolRootSnapshot, matchesDiscoveryRequestedSnapshot2, func) : LolInstallDiscovery.FindLolRoot(lolRootSnapshot, matchesDiscoveryRequestedSnapshot2, func);
					weGameRoot2 = ((func != null && func()) ? weGameRootSnapshot : (FindWeGameRootForTest != null ? FindWeGameRootForTest(weGameRootSnapshot, lolRoot2, matchesDiscoveryRequestedSnapshot2, func) : LolInstallDiscovery.FindWeGameRoot(weGameRootSnapshot, lolRoot2, matchesDiscoveryRequestedSnapshot2, func)));
#else
					lolRoot2 = LolInstallDiscovery.FindLolRoot(lolRootSnapshot, matchesDiscoveryRequestedSnapshot2, func);
					weGameRoot2 = ((func != null && func()) ? weGameRootSnapshot : LolInstallDiscovery.FindWeGameRoot(weGameRootSnapshot, lolRoot2, matchesDiscoveryRequestedSnapshot2, func));
#endif
				}
				finally
				{
					if (discoverReady)
					{
						SetDiscovering(false);
					}
				}
				if (stopWithWorker && RuntimeStopped())
				{
					root = lolRootSnapshot;
					weGame = weGameRootSnapshot;
					return;
				}
				bool matchesDiscoverReady = discoverReady && func();
				bool isNotValuesEqual;
				bool isNotValuesEqual2;
				lock (stateLock)
				{
					discoveryRequested = false;
					isNotValuesEqual = !string.Equals(lolRoot, lolRoot2, StringComparison.OrdinalIgnoreCase);
					isNotValuesEqual2 = !string.Equals(weGameRoot, weGameRoot2, StringComparison.OrdinalIgnoreCase);
					lolRoot = lolRoot2;
					weGameRoot = weGameRoot2;
					root = lolRoot2;
					weGame = weGameRoot2;
					installationFound = lolRoot2 != null;
					weGameFound = LolInstallDiscovery.IsValidWeGameRoot(weGameRoot2);
					if (lolRoot2 == null)
					{
						if (matchesDiscoverReady)
						{
							nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(600.0);
						}
						else
						{
							if (discoveryMisses < 8)
							{
								discoveryMisses++;
							}
							int discoverValue = 15;
							for (int i = 1; i < discoveryMisses; i++)
							{
								discoverValue *= 2;
								if (discoverValue >= 600)
								{
									break;
								}
							}
							if (discoverValue > 600)
							{
								discoverValue = 600;
							}
							nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(discoverValue);
						}
					}
					else
					{
						discoveryMisses = 0;
						nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(120.0);
					}
					updatedUtc = DateTime.UtcNow;
				}
				if (isNotValuesEqual)
				{
					InvalidateCredentials();
					Settings.SaveStr("LolInstallPath", root ?? "");
				}
				if (isNotValuesEqual2)
				{
					Settings.SaveStr("LolWeGamePath", weGame ?? "");
				}
			}
			else
			{
				root = lolRootSnapshot;
				weGame = weGameRootSnapshot;
			}
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
	}
}
