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
				bool flag;
				lock (stateLock)
				{
					flag = enabled != value;
					enabled = value;
					if (flag && value)
					{
						discoveryRequested = true;
						discoveryMisses = 0;
						nextDiscoveryUtc = DateTime.MinValue;
					}
					updatedUtc = DateTime.UtcNow;
				}
				if (flag)
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
				bool flag;
				lock (stateLock)
				{
					flag = cleanupEnabled != value;
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
				if (flag)
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
				bool flag;
				lock (stateLock)
				{
					flag = headlessEnabled != value;
					headlessEnabled = value;
					updatedUtc = DateTime.UtcNow;
				}
				if (flag)
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
			bool flag = string.Equals(currentPhase, "InProgress", StringComparison.OrdinalIgnoreCase);
			if (DateTime.UtcNow >= nextSessionVerifyUtc)
			{
				sessionVerified = LolLcuClient.IsReady(lolLcuCredentials);
				nextSessionVerifyUtc = DateTime.UtcNow.AddSeconds((!sessionVerified) ? 5 : (flag ? 60 : 15));
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
			bool flag;
			lock (stateLock)
			{
				thread = worker;
				flag = running || (thread != null && thread.IsAlive);
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
			if (flag)
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
			bool flag = batch.Overflowed;
			bool flag2 = batch.Overflowed;
			bool flag3 = false;
			bool flag4 = batch.Overflowed;
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
				bool flag5 = IsCredentialDiscoveryStartEvent(processChange);
				if (processChange != null && (flag5 || LolRuntimeProcesses.IsRelevantProcessChange(processChange.Name, processChange.Path, root, weGame)))
				{
					flag = true;
					if (IsExactCleanupStartEvent(processChange, root, weGame))
					{
						flag2 = true;
					}
					if (IsCredentialSourceStartEvent(processChange, root))
					{
						flag3 = true;
					}
					if (flag5)
					{
						flag3 = true;
						flag4 = true;
					}
				}
			}
			if (!flag)
			{
				return;
			}
			if (flag4)
			{
				InvalidateDiscovery();
			}
			if (flag3)
			{
				Interlocked.Exchange(ref credentialGenerationChanged, 1);
			}
			bool flag6 = false;
			bool flag7 = false;
			int dueTime = -1;
			DateTime utcNow = DateTime.UtcNow;
			bool flag8;
			lock (stateLock)
			{
				if (batch.Overflowed)
				{
					nextFullProcessScanUtc = DateTime.MinValue;
				}
				if (flag2)
				{
					cleanupDirty = true;
				}
				flag8 = flag3 || utcNow >= nextProcessWakeUtc;
				if (flag8)
				{
					nextProcessWakeUtc = utcNow.AddMilliseconds(5000.0);
					flag7 = processWakePending;
					processWakePending = false;
				}
				else if (!processWakePending)
				{
					processWakePending = true;
					flag6 = true;
					dueTime = ProcessWakeDelayMs(utcNow, nextProcessWakeUtc);
				}
				if (flag7)
				{
					try
					{
						processWakeTimer.Change(-1, -1);
					}
					catch
					{
					}
				}
				if (flag6)
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
			if (flag8)
			{
				Poke();
			}
		}

		private void ProcessWakeTimerElapsed(object unused)
		{
			bool flag = false;
			bool flag2 = false;
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
					flag2 = true;
					dueTime = ProcessWakeDelayMs(utcNow, nextProcessWakeUtc);
				}
				else
				{
					processWakePending = false;
					nextProcessWakeUtc = utcNow.AddMilliseconds(5000.0);
					flag = true;
				}
			}
			if (flag2)
			{
				try
				{
					processWakeTimer.Change(dueTime, -1);
				}
				catch
				{
				}
			}
			if (flag)
			{
				Poke();
			}
		}

		internal static int ProcessWakeDelayMs(DateTime nowUtc, DateTime deadlineUtc)
		{
			long num = deadlineUtc.Ticks - nowUtc.Ticks;
			if (num <= 0)
			{
				return 1;
			}
			long val = (num + 10000 - 1) / 10000;
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
				bool flag = LolLcuClient.IsReady(lolLcuCredentials);
				lock (stateLock)
				{
					cleanedProcessCount += lolCleanupResult.Count;
					releasedWorkingSetBytes += lolCleanupResult.WorkingSetBytes;
					lcuReady = flag;
					if (flag)
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
				if (!flag)
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
				bool flag = TryRestoreUxBounded(credentialsForRestore, root);
				if (!flag)
				{
					InvalidateCredentials();
					credentialsForRestore = GetCredentialsForRestore(root, true);
					flag = TryRestoreUxBounded(credentialsForRestore, root);
					if (!flag && credentialsForRestore != null)
					{
						ScheduleRestoreCredentialRetry();
					}
				}
				bool flag2 = flag && (LolRuntimeProcesses.IsGameRunning(root) || IsSameMatchPhase(LolLcuClient.GetGameflowPhase(credentialsForRestore)));
				lock (stateLock)
				{
					if (flag)
					{
						headlessActive = false;
						headlessExitSamples = 0;
						manualUxBypassGame = flag2;
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
				bool flag3 = flag;
				if (flag)
				{
					flag3 = ClearOwnedHeadlessMark();
					if (!flag3)
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
				return flag3;
			}
		}

		public bool HasRecordedHeadlessState()
		{
			bool flag;
			lock (stateLock)
			{
				flag = headlessActive || !string.IsNullOrEmpty(headlessLeaseRoot);
			}
			if (!flag)
			{
				return LolHeadlessLease.Exists();
			}
			return true;
		}

		private bool StartWeGame(string weGame, bool updateAction)
		{
			string text = LolInstallDiscovery.FindWeGameExecutable(weGame);
			if (text == null)
			{
				if (updateAction)
				{
					SetError(Lang.T("lol.err.nowegame"));
				}
				return false;
			}
			if (!UserLaunch.Start(text))
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
			bool flag;
			lock (stateLock)
			{
				flag = worker == null || !worker.IsAlive;
			}
			if (flag)
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
			string text;
			int num;
			long num2;
			lock (stateLock)
			{
				text = headlessLeaseRoot;
				num = headlessLeaseGamePid;
				num2 = headlessLeaseGameCreation;
			}
			if (string.IsNullOrEmpty(text))
			{
				return true;
			}
			string recoveryStage = HeadlessRecoveryStage(text, num, num2, "clear-mark");
			if (!headlessFailures.Allowed(num, num2, text, recoveryStage)) return false;
			bool flag = num > 0 && num2 > 0 && LolHeadlessLease.ClearIfMatches(text, num, num2);
			string current; LolHeadlessLeaseInfo remaining;
			bool confirmedReleased = Settings.TryLoadStr("LolHeadlessEngagedRoot", out current)
				&& (string.IsNullOrEmpty(current) || LolHeadlessLease.TryParse(current, out remaining)
				&& (!SameRootPath(remaining.Root, text) || remaining.GamePid != num || remaining.GameCreation != num2));
			bool flag3 = flag || confirmedReleased;
			if (flag3) headlessFailures.Succeeded(num, num2, text, recoveryStage);
			else Logger.Warn("LoL recovery mark failure=" + headlessFailures.Failed(num, num2, text, recoveryStage) + "/3; owned mark retained");
			lock (stateLock)
			{
				if (SameLocalLeaseLocked(text, num, num2))
				{
					headlessLeaseReleasePending = !flag3;
					if (flag3)
					{
						headlessLeaseRoot = null;
						headlessLeaseGamePid = 0;
						headlessLeaseGameCreation = 0L;
					}
				}
			}
			return flag3;
		}

		private void RememberHeadlessLease(string root)
		{
			string stored;
			LolHeadlessLeaseInfo info;
			if (!Settings.TryLoadStr("LolHeadlessEngagedRoot", out stored)) return;
			if (!string.IsNullOrEmpty(stored) && !LolHeadlessLease.TryParse(stored, out info)) return;
			bool flag = LolHeadlessLease.TryParse(stored, out info) && SameRootPath(info.Root, root);
			lock (stateLock)
			{
				if (flag)
				{
					bool flag2 = SameLocalLeaseLocked(info.Root, info.GamePid, info.GameCreation);
					headlessLeaseRoot = info.Root;
					headlessLeaseGamePid = info.GamePid;
					headlessLeaseGameCreation = info.GameCreation;
					if (!flag2)
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
				string text = Path.GetFullPath(left ?? "").TrimEnd('\\');
				string text2 = Path.GetFullPath(right ?? "").TrimEnd('\\');
				return text.Length > 2 && text2.Length > 2 && string.Equals(text, text2, StringComparison.OrdinalIgnoreCase);
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
			bool flag;
			lock (stateLock)
			{
				flag = clientRunning || gameRunning || lcuReady || phase.Length > 0 || weGameProcessCount != 0 || crossProcessCount != 0 || uxProcessCount != 0;
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
			if (flag)
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
					bool flag;
					bool flag2;
					bool flag3;
					bool flag4;
					bool flag5;
					bool recoveryMarked;
					lock (stateLock)
					{
						serviceEnabled = enabled;
						flag = cleanupEnabled;
						flag2 = headlessEnabled;
						flag3 = clientRunning;
						flag4 = gameRunning;
						flag5 = headlessActive;
						recoveryMarked = !string.IsNullOrEmpty(headlessLeaseRoot);
					}
					int millisecondsTimeout = WorkerDelayMs(serviceEnabled, flag, flag2, flag3, flag4, flag5, recoveryMarked);
					if (WaitHandle.WaitAny(waitHandles, millisecondsTimeout) == 0)
					{
						break;
					}
				}
			}
			finally
			{
				bool flag6;
				lock (stateLock)
				{
					if (object.ReferenceEquals(worker, Thread.CurrentThread))
					{
						worker = null;
					}
					flag6 = disposed;
				}
				if (flag6)
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
			bool flag;
			bool flag2;
			lock (stateLock)
			{
				flag = enabled && (cleanupEnabled || headlessEnabled);
				flag2 = headlessActive;
			}
			bool flag3 = HasHeadlessMark();
			if (!flag3)
			{
				ForgetRememberedHeadlessLease();
			}
			if (!flag && !flag2 && !flag3)
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
			bool flag;
			lock (stateLock)
			{
				flag = headlessLeaseReleasePending;
			}
			if (flag)
			{
				ClearOwnedHeadlessMark();
			}
			return true;
		}

		private LolProcessSnapshot ScanProcesses(string root, string weGame)
		{
			bool flag;
			lock (stateLock)
			{
				flag = DateTime.UtcNow < nextFullProcessScanUtc;
				if (!flag)
				{
					nextFullProcessScanUtc = DateTime.UtcNow.AddSeconds(60.0);
				}
			}
			return LolRuntimeProcesses.Scan(root, weGame, flag);
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
			bool flag = false;
			if (active && serviceEnabled && noUxEnabled && !manualBypass && inProgress && processes.MainUxProcessCount > 0)
			{
				lock (stateLock)
				{
					headlessActive = false;
					headlessExitSamples = 0;
					nextUxKillUtc = DateTime.UtcNow.AddSeconds(10.0);
					flag = RegisterUxRespawn(DateTime.UtcNow, uxRespawns);
					if (flag)
					{
						manualUxBypassGame = true;
					}
				}
				active = false;
			}
			if (flag)
			{
				bool flag2 = RestoreCore(credentials, root, true);
				lock (stateLock)
				{
					headlessActive = !flag2;
					headlessExitSamples = 0;
					manualUxBypassGame = true;
					active = !flag2;
					manualBypass = true;
					lastAction = Lang.T("lol.act.uxcircuit");
					lastError = (flag2 ? "" : Lang.T("lol.err.uxcircuitrestore"));
					lastActionUtc = DateTime.UtcNow;
					updatedUtc = DateTime.UtcNow;
				}
				processes = LolRuntimeProcesses.Scan(root, weGame, true);
			}
		}

		private void RunRestoreStage(LolLcuCredentials credentials, string root, string weGame, bool serviceEnabled, bool noUxEnabled, bool manualBypass, bool active, bool ready, bool matchExitConfirmed, ref LolProcessSnapshot processes)
		{
			if (matchExitConfirmed) ObserveHeadlessOwnerExit();
			bool flag = false;
			if (active)
			{
				lock (stateLock)
				{
					if (!serviceEnabled || !noUxEnabled || manualBypass)
					{
						headlessExitSamples = 0;
						flag = true;
					}
					else if (ready && matchExitConfirmed)
					{
						if (headlessExitSamples < int.MaxValue)
						{
							headlessExitSamples++;
						}
						flag = headlessExitSamples >= 2;
					}
					else
					{
						headlessExitSamples = 0;
					}
				}
			}
			if (flag)
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
			bool flag = IsVerifiedCleanupSession(processes.ClientRunning, ready, currentPhase, processes.GameRunning, cleanupMatch);
			DateTime utcNow = DateTime.UtcNow;
			bool flag2;
			bool flag3;
			lock (stateLock)
			{
				flag2 = utcNow >= nextCleanupUtc || (cleanupDirty && !cleanupBurstActive);
				flag3 = cleanupCircuitOpen;
			}
			if (!serviceEnabled || !cleanEnabled || !flag || flag3 || !flag2)
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
			bool flag4 = lolCleanupResult.Count == 0 || LolLcuClient.IsReady(credentials);
			DateTime utcNow2 = DateTime.UtcNow;
			bool flag5 = false;
			lock (stateLock)
			{
				cleanedProcessCount += lolCleanupResult.Count;
				releasedWorkingSetBytes += lolCleanupResult.WorkingSetBytes;
				lcuReady = flag4;
				if (lolCleanupResult.Count > 0)
				{
					flag5 = RegisterCleanupKillCycle(utcNow2, cleanupKillCycles) || !flag4;
					if (flag5)
					{
						cleanupCircuitOpen = true;
					}
				}
				if (flag5)
				{
					cleanupDirty = false;
					cleanupBurstActive = false;
					cleanupFollowUpsRemaining = 0;
					nextCleanupUtc = DateTime.MaxValue;
					lastAction = Lang.T("lol.act.cleanupcircuit");
					lastError = (flag4 ? "" : Lang.T("lol.err.sessionlost"));
					lastActionUtc = utcNow2;
				}
				else if (!flag4)
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
				if (!flag5)
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
			ready = flag4;
			if (lolCleanupResult.Count > 0)
			{
				processes = LolRuntimeProcesses.Scan(root, weGame, true);
			}
			if (!flag4 && lolCleanupResult.Count > 0)
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
			bool flag;
			lock (stateLock)
			{
				flag = !headlessActive && !manualUxBypassGame && processes.MainUxProcessCount > 0 && DateTime.UtcNow >= nextUxKillUtc;
			}
			if (!flag)
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
			bool flag2 = LolRuntimeProcesses.TryGetGameIdentity(root, processes.GameProcessId, out pid, out creation);
			if (flag2 && !headlessFailures.Allowed(pid, creation, root, "apply")) return;
			bool flag3 = false, flag4 = false, flag5 = false;
			try
			{
				flag3 = flag2 && SetHeadlessMark(root, pid, creation);
				flag4 = flag3 && LolWatchdog.StartDetached(root, pid, creation);
				flag5 = flag4 && LolLcuClient.KillUx(credentials, root);
			}
			finally
			{
				if (flag5) headlessFailures.Succeeded(pid, creation, root, "apply");
				else if (flag2) Logger.Warn("LoL headless apply failure=" + headlessFailures.Failed(pid, creation, root, "apply")
					+ "/3 target=" + pid + "/" + creation + "; recovery ownership retained where present");
			}
			// Timeout or request failure does not prove the helper/mutation absent; retain the lease until verified restoration.
			lock (stateLock)
			{
				if (flag5)
				{
					headlessActive = true;
					headlessExitSamples = 0;
					lastAction = Lang.T("lol.act.headless");
					lastError = "";
					lastActionUtc = DateTime.UtcNow;
				}
				else
				{
					lastError = ((!flag2) ? Lang.T("lol.err.gameidentity") : ((!flag3) ? Lang.T("lol.err.lease") : ((!flag4) ? Lang.T("lol.err.watchdog") : Lang.T("lol.err.killux"))));
					lastActionUtc = DateTime.UtcNow;
				}
				updatedUtc = DateTime.UtcNow;
			}
			if (flag5)
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
			bool flag = TryRestoreUxBounded(credentials, root);
			lock (stateLock)
			{
				string a = lastAction;
				string a2 = lastError;
				if (flag)
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
			if (flag)
			{
				ClearOwnedHeadlessMark();
				Logger.Log("英雄联盟专栏：大厅界面已自动恢复");
			}
			return flag;
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
			string text = null;
			try
			{
				string text2 = (string.IsNullOrEmpty(executablePath) ? null : Path.GetDirectoryName(executablePath));
				for (int i = 0; i < 6; i++)
				{
					if (string.IsNullOrEmpty(text2))
					{
						break;
					}
					if (LolInstallDiscovery.IsValidLolRoot(text2))
					{
						text = text2;
						break;
					}
					text2 = Path.GetDirectoryName(text2);
				}
			}
			catch
			{
				text = null;
			}
			if (text == null)
			{
				return false;
			}
			bool flag;
			lock (stateLock)
			{
				flag = !string.Equals(lolRoot, text, StringComparison.OrdinalIgnoreCase) || !installationFound;
				lolRoot = text;
				installationFound = true;
				discoveryRequested = false;
				discoveryMisses = 0;
				nextDiscoveryUtc = DateTime.MinValue;
				updatedUtc = DateTime.UtcNow;
			}
			if (!flag)
			{
				return true;
			}
			InvalidateCredentials();
			Settings.SaveStr("LolInstallPath", text);
			Logger.Log("英雄联盟增强 按游戏库条目定位安装目录 " + text);
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
			string text;
			string text2;
			bool flag;
			lock (stateLock)
			{
				text = lolRoot;
				text2 = weGameRoot;
				flag = force || DateTime.UtcNow >= nextDiscoveryUtc;
			}
			if (flag)
			{
				bool flag2 = LolInstallDiscovery.IsValidLolRoot(text);
				if (!force && !flag2)
				{
					bool flag3;
					lock (stateLock)
					{
						flag3 = discoveryRequested;
					}
					if (!flag3)
					{
						lock (stateLock)
						{
							nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(120.0);
						}
						root = null;
						weGame = text2;
						return;
					}
				}
				bool flag4;
				lock (stateLock)
				{
					flag4 = discoveryRequested;
				}
				bool flag5 = !flag2 || flag4;
				bool flag6 = flag5;
				Func<bool> func = null;
				if (flag6)
				{
					Interlocked.Exchange(ref discoveryCancelRequested, 0);
					func = () => DiscoveryCancelRequested() || (stopWithWorker && RuntimeStopped());
					SetDiscovering(true);
				}
				string text3;
				string text4;
				try
				{
#if PAVISE_SELFTEST
					text3 = FindLolRootForTest != null ? FindLolRootForTest(text, flag5, func) : LolInstallDiscovery.FindLolRoot(text, flag5, func);
					text4 = ((func != null && func()) ? text2 : (FindWeGameRootForTest != null ? FindWeGameRootForTest(text2, text3, flag5, func) : LolInstallDiscovery.FindWeGameRoot(text2, text3, flag5, func)));
#else
					text3 = LolInstallDiscovery.FindLolRoot(text, flag5, func);
					text4 = ((func != null && func()) ? text2 : LolInstallDiscovery.FindWeGameRoot(text2, text3, flag5, func));
#endif
				}
				finally
				{
					if (flag6)
					{
						SetDiscovering(false);
					}
				}
				if (stopWithWorker && RuntimeStopped())
				{
					root = text;
					weGame = text2;
					return;
				}
				bool flag7 = flag6 && func();
				bool flag8;
				bool flag9;
				lock (stateLock)
				{
					discoveryRequested = false;
					flag8 = !string.Equals(lolRoot, text3, StringComparison.OrdinalIgnoreCase);
					flag9 = !string.Equals(weGameRoot, text4, StringComparison.OrdinalIgnoreCase);
					lolRoot = text3;
					weGameRoot = text4;
					root = text3;
					weGame = text4;
					installationFound = text3 != null;
					weGameFound = LolInstallDiscovery.IsValidWeGameRoot(text4);
					if (text3 == null)
					{
						if (flag7)
						{
							nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(600.0);
						}
						else
						{
							if (discoveryMisses < 8)
							{
								discoveryMisses++;
							}
							int num = 15;
							for (int i = 1; i < discoveryMisses; i++)
							{
								num *= 2;
								if (num >= 600)
								{
									break;
								}
							}
							if (num > 600)
							{
								num = 600;
							}
							nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(num);
						}
					}
					else
					{
						discoveryMisses = 0;
						nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(120.0);
					}
					updatedUtc = DateTime.UtcNow;
				}
				if (flag8)
				{
					InvalidateCredentials();
					Settings.SaveStr("LolInstallPath", root ?? "");
				}
				if (flag9)
				{
					Settings.SaveStr("LolWeGamePath", weGame ?? "");
				}
			}
			else
			{
				root = text;
				weGame = text2;
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
