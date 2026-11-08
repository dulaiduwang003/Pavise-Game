using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
	internal class Tamer
	{
		private sealed class AcquireRequest
		{
			public int Pid;

			public string Name;

			public string Group;

			public long Birth;


			public AcquireResult Result;

			public string FailureDetail;
		}

		private const int PollingFullSweepIntervalMs = 8000;

		private const int EventBackedFullSweepIntervalMs = 60000;

		private const int WorkerWakeIntervalMs = 8000;

		internal const int OverflowSweepIntervalMs = 8000;

		internal const int FailedSweepRetryMs = 1000;

		private sealed class SweepRetryPhase
		{
			internal int Failures;
			internal long NotBefore;
			internal int CaptureFailures;
			internal long CaptureNotBefore;
		}
		private readonly Dictionary<string, SweepRetryPhase> sweepRetryPhases = new Dictionary<string, SweepRetryPhase>(StringComparer.Ordinal);
		private readonly Dictionary<int, long> sweepEventBirths = new Dictionary<int, long>();
		private readonly HashSet<string> sweepObservedEvents = new HashSet<string>(StringComparer.Ordinal);
		private long sweepEventPhase;
		private string sweepPhaseKey;
		private string sweepConfigurationKey;
		private SweepRetryPhase captureRetryPhase = new SweepRetryPhase();
		private ProcessSnapshot iterationSnapshot;
		private bool iterationCaptureAttempted;

		private ProcessSnapshot CaptureSweepSnapshot()
		{
			if (iterationCaptureAttempted) return iterationSnapshot;
			iterationCaptureAttempted = true;
			if (captureRetryPhase.CaptureFailures >= 3 || DateTime.UtcNow.Ticks < captureRetryPhase.CaptureNotBefore) return null;
			try { iterationSnapshot = ProcessSnapshotSource.Capture(); } catch { iterationSnapshot = null; }
			if (iterationSnapshot == null)
			{
				captureRetryPhase.CaptureFailures++;
				captureRetryPhase.CaptureNotBefore = DateTime.UtcNow.AddMilliseconds(FailedSweepRetryMs).Ticks;
				Logger.Log("Tamer snapshot: attempt=" + captureRetryPhase.CaptureFailures + "/3; failed capture shared by observation and sweep");
			}
			else { captureRetryPhase.CaptureFailures = 0; captureRetryPhase.CaptureNotBefore = 0; }
			return iterationSnapshot;
		}

		private SweepRetryPhase SelectSweepRetryPhase(List<ProcessChange> changes, out bool changed)
		{
			Dictionary<string, string> active = BuildActive();
			if (changes != null)
			{
				foreach (ProcessChange change in changes)
				{
					long birth;
					string group;
					AcquireRequest request;
					if (change.Kind == ProcessChangeKind.Started)
					{
						if (string.IsNullOrEmpty(change.Name) || !active.TryGetValue(change.Name, out group)
							|| !TryBuildAcquireRequest(change.Pid, change.Name, group, out request) || request.Birth <= 0) continue;
						birth = request.Birth;
						sweepEventBirths[change.Pid] = birth;
					}
					else
					{
						if (!sweepEventBirths.TryGetValue(change.Pid, out birth))
						{
							string name = core.NameOf(change.Pid);
							if (name == null || !active.ContainsKey(name)) continue;
							birth = core.CreationOf(change.Pid);
						}
						if (birth <= 0 || IsPidCurrentlyAlive(change.Pid)) continue;
						core.ObserveTargetExit(change.Pid, birth);
					}
					// Sequence alone is not process identity. Replayed notifications cannot refill a budget.
					string identity = change.Pid + "/" + birth + "/" + change.Kind;
					if (sweepObservedEvents.Add(identity)) sweepEventPhase++;
				}
			}
			var names = new List<string>(active.Keys);
			names.Sort(StringComparer.OrdinalIgnoreCase);
			string configuration = (governanceContextKey ?? "") + "\n" + mode + "\n" + string.Join("\n", names.ToArray());
			string key = configuration + "\nevent:" + sweepEventPhase;
			SweepRetryPhase previous;
			bool recovering = sweepPhaseKey != null && sweepRetryPhases.TryGetValue(sweepPhaseKey, out previous) && previous.Failures > 0;
			changed = configuration != sweepConfigurationKey || (recovering && key != sweepPhaseKey);
			sweepConfigurationKey = configuration;
			sweepPhaseKey = key;
			SweepRetryPhase phase;
			if (!sweepRetryPhases.TryGetValue(key, out phase)) sweepRetryPhases[key] = phase = new SweepRetryPhase();
			return phase;
		}

		private const int MaxPendingChanges = 512;

		private readonly object sync = new object();

		private readonly object engineSync = new object();

		private readonly Dictionary<string, bool> enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

		private readonly object eventSync = new object();

		private readonly List<ProcessChange> pendingChanges = new List<ProcessChange>();

		private readonly AutoResetEvent kick = new AutoResetEvent(true);

		private readonly SuppressionCore core;

		private readonly int selfPid;

		private readonly int selfSession;

		private volatile int mode = 2;

		private volatile bool stopping;

		private readonly Dictionary<int, long> recordedProcesses = new Dictionary<int, long>();

		private int processEventsAvailable;

		private long panicUntilUtcTicks;

		private int fullSweepRequested = 1;

		private int overflowSweepRequested;

		private Thread worker;

		internal Func<AntiCheatAffinityGameContext> AffinityGameContext;
		private AntiCheatAffinityRuntime affinityRuntime;
		private bool affinityFastWake;
		private bool affinityShutdownOk = true;
		private bool groupMigrationReady;
		private List<KeyValuePair<string, int>> governanceServices;
		private long governanceServicesNextRead;
		private string governanceContextKey;

		internal string AffinityDetails(string group)
		{
			AntiCheatAffinityRuntime runtime = affinityRuntime;
			return runtime == null ? "" : runtime.Details(group);
		}

		private string WithAffinity(string status, string group)
		{
			AntiCheatAffinityRuntime runtime = affinityRuntime;
			string affinity = runtime == null ? "" : runtime.Summary(group);
			return affinity.Length == 0 ? status : status + " · " + affinity;
		}

		public AntiCheatMode Mode
		{
			get
			{
				return (AntiCheatMode)mode;
			}
			set
			{
				if (mode == (int)value)
				{
					return;
				}
				bool flag = AntiCheatModes.ShouldReleasePins((AntiCheatMode)mode, value);
				mode = (int)value;
				AntiCheatModes.Save(value);
				if (flag)
				{
					int num = 0;
					try
					{
						lock (engineSync)
						{
							num = core.ClearSqueezes(SuppressReason.AntiCheat);
						}
					}
					catch
					{
						num = 0;
					}
					if (num > 0)
					{
						Logger.Info(Lang.F("log.tamer.unpin", num));
					}
				}
				Interlocked.Exchange(ref fullSweepRequested, 1);
				Poke();
				Logger.Info(Lang.F("log.tamer.mode", AntiCheatModes.NameOf(value)));
			}
		}

		public bool Paused
		{
			get
			{
				if (!groupMigrationReady) return true;
				lock (sync)
				{
					foreach (AcGroup group in AntiCheatCatalog.Groups)
					{
						bool on;
						if (group.Suppressible && enabled.TryGetValue(group.Key, out on) && on) return false;
					}
					return true;
				}
			}
		}

		public bool ProcessEventsAvailable
		{
			get
			{
				return Interlocked.CompareExchange(ref processEventsAvailable, 0, 0) != 0;
			}
			set
			{
				int num = (value ? 1 : 0);
				if (Interlocked.Exchange(ref processEventsAvailable, num) != num)
				{
					Poke();
				}
			}
		}

		public Tamer(SuppressionCore core)
		{
			this.core = core;
			using (Process process = Process.GetCurrentProcess())
			{
				selfPid = process.Id;
				try
				{
					selfSession = process.SessionId;
				}
				catch
				{
					selfSession = -1;
				}
			}
			groupMigrationReady = MigrateGroupControls();
			AcGroup[] groups = AntiCheatCatalog.Groups;
			foreach (AcGroup acGroup in groups)
			{
				enabled[acGroup.Key] = groupMigrationReady && Settings.Load("Tame_" + acGroup.Key, acGroup.Default);
			}
			mode = (int)AntiCheatModes.Current;
			RetireNonSuppressibleGroups();
		}

		private static bool MigrateGroupControls()
		{
			List<string> groups = new List<string>();
			foreach (AcGroup group in AntiCheatCatalog.Groups) if (group.Suppressible) groups.Add(group.Key);
			bool ready = AntiCheatGroupMigration.Ensure(AntiCheatGroupMigration.ReadStored, Settings.SaveDurable, groups);
			if (!ready) Logger.Warn("AC group-control migration unresolved; governance remains paused");
			return ready;
		}

		private void RetireNonSuppressibleGroups()
		{
			AcGroup[] groups = AntiCheatCatalog.Groups;
			foreach (AcGroup acGroup in groups)
			{
				if (!acGroup.Suppressible && Settings.Load("Tame_" + acGroup.Key, false))
				{
					Settings.Save("Tame_" + acGroup.Key, false);
					lock (sync)
					{
						enabled[acGroup.Key] = false;
					}
					Logger.Info(Lang.F(acGroup.RecordOnly ? "log.tamer.recordonly.retired" : "log.tamer.retired", acGroup.Name));
				}
			}
		}

		public bool IsGroupEnabled(string key)
		{
			lock (sync)
			{
				bool value;
				return groupMigrationReady && enabled.TryGetValue(key, out value) && value;
			}
		}

		public bool PanicRestore()
		{
			Interlocked.Exchange(ref panicUntilUtcTicks, DateTime.UtcNow.AddSeconds(4.0).Ticks);
			Interlocked.Exchange(ref fullSweepRequested, 1);
			kick.Set();
			lock (engineSync)
			{
				return ReleaseAll(Lang.T("t.gamemode.42"));
			}
		}

		public void SetGroupEnabled(string key, bool on)
		{
			AcGroup acGroup = Array.Find(AntiCheatCatalog.Groups, (AcGroup g) => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
			if (acGroup != null && acGroup.Suppressible)
			{
				lock (sync)
				{
					if (!groupMigrationReady) groupMigrationReady = MigrateGroupControls();
					if (!groupMigrationReady || !Settings.SaveDurable("Tame_" + key, on)) return;
					enabled[key] = on;
				}
				Poke();
				Logger.Log(Lang.T("log.tamer.1") + key + " " + (on ? Lang.T("log.tamer.3") : Lang.T("log.tamer.4")));
			}
		}

		public string GroupStatus(string key)
		{
			if (Paused)
			{
				return WithAffinity(Lang.T("gs.noff"), key);
			}
			lock (sync)
			{
				bool value;
				if (!enabled.TryGetValue(key, out value) || !value)
				{
					return WithAffinity(Lang.T("gs.noff"), key);
				}
			}
			int throttled;
			int protectedCnt;
			core.AntiCheatGroupCountsCached(key, out throttled, out protectedCnt);
			if (throttled == 0 && protectedCnt == 0)
			{
				return WithAffinity(Lang.T("gs.noproc"), key);
			}
			string text = Lang.F("gs.thr", throttled);
			if (protectedCnt > 0)
			{
				text += Lang.F("gs.prot", protectedCnt);
			}
			return WithAffinity(text, key);
		}

		public int GroupState(string key)
		{
			if (Paused)
			{
				return 0;
			}
			lock (sync)
			{
				bool value;
				if (!enabled.TryGetValue(key, out value) || !value)
				{
					return 0;
				}
			}
			int throttled;
			int protectedCnt;
			core.AntiCheatGroupCountsCached(key, out throttled, out protectedCnt);
			if (throttled <= 0)
			{
				return 2;
			}
			return 1;
		}

		public void Start()
		{
			worker = new Thread(Loop);
			worker.IsBackground = true;
			worker.Start();
		}

		public bool Stop()
		{
			stopping = true;
			kick.Set();
			Thread thread = worker;
			if (thread != null)
			{
				if (thread != Thread.CurrentThread)
				{
					return thread.Join(6000) && affinityShutdownOk;
				}
				return false;
			}
			return true;
		}

		public void Poke()
		{
			Interlocked.Exchange(ref fullSweepRequested, 1);
			kick.Set();
		}

		public void NotifyProcessChanges(ProcessChangeBatch batch)
		{
			if (batch == null)
			{
				return;
			}
			lock (eventSync)
			{
				if (batch.Overflowed || pendingChanges.Count + batch.Changes.Length > 512)
				{
					pendingChanges.Clear();
					Interlocked.Exchange(ref overflowSweepRequested, 1);
				}
				else
				{
					ProcessChange[] changes = batch.Changes;
					foreach (ProcessChange processChange in changes)
					{
						if (processChange != null && processChange.Pid > 0)
						{
							pendingChanges.Add(processChange);
						}
					}
				}
			}
			kick.Set();
		}

		private void Loop()
		{
			Logger.Log(Lang.T("log.tamer.5"));
			affinityRuntime = new AntiCheatAffinityRuntime(core.PrepareAntiCheatAffinity, CheckAffinityAdmission);
			long num = 0L;
			long num2 = 0L;
			long num3 = 0L;
			long nextCoreRetry = 0L;
			long failedSweepRetryNotBefore = 0L;
			while (!stopping)
			{
				try
				{
					lock (engineSync)
					{
						long ticks = DateTime.UtcNow.Ticks;
						bool flag = ticks < Interlocked.Read(ref panicUntilUtcTicks);
						List<ProcessChange> changes = DrainProcessChanges();
						bool flag2 = false;
						bool flag3 = false;
						bool phaseChanged = false;
						RefreshGovernanceSession();
						SweepRetryPhase retryPhase = SelectSweepRetryPhase(changes, out phaseChanged);
						captureRetryPhase = retryPhase;
						iterationSnapshot = null;
						iterationCaptureAttempted = false;
						if (ticks >= num3)
						{
							num3 = ticks + TimeSpan.FromSeconds(30).Ticks;
							try { ObserveRecordOnlyProcesses(CaptureSweepSnapshot()); } catch { }
						}
						if (!Paused && !flag)
						{
							failedSweepRetryNotBefore = retryPhase.NotBefore;
							if (phaseChanged) num = retryPhase.NotBefore;
							flag2 = Interlocked.Exchange(ref fullSweepRequested, 0) != 0;
							flag3 = Interlocked.Exchange(ref overflowSweepRequested, 0) != 0;
						}
						if (Paused || flag)
						{
							ReleaseAll(Paused ? Lang.T("gs.noff") : Lang.T("t.tamer.6"));
						}
						// Only failed execution spends a phase budget; Poke cannot reset its budget or deadline.
						else if (retryPhase.Failures < 3 && ticks >= failedSweepRetryNotBefore
							&& (phaseChanged || (flag2 && retryPhase.Failures == 0) || ticks >= num || (flag3 && ticks >= num2)))
						{
							bool flag4 = false;
							try
							{
								flag4 = Sweep();
							}
							finally
							{
								if (flag4)
								{
									failedSweepRetryNotBefore = 0L;
									retryPhase.Failures = 0;
									retryPhase.NotBefore = 0L;
									num = DateTime.UtcNow.AddMilliseconds(FullSweepInterval(ProcessEventsAvailable)).Ticks;
									if (flag3)
									{
										num2 = DateTime.UtcNow.AddMilliseconds(8000.0).Ticks;
									}
								}
								else
								{
									if (flag3)
									{
										Interlocked.Exchange(ref overflowSweepRequested, 1);
									}
									retryPhase.Failures++;
									failedSweepRetryNotBefore = retryPhase.Failures >= 3 ? DateTime.MaxValue.Ticks : DateTime.UtcNow.AddMilliseconds(FailedSweepRetryMs).Ticks;
									retryPhase.NotBefore = failedSweepRetryNotBefore;
									Logger.Log("Tamer sweep recovery: phase=" + sweepPhaseKey + " attempt=" + retryPhase.Failures + "/3; "
										+ (retryPhase.Failures >= 3 ? "active retries stopped; process events remain active" : "backoff"));
									num = failedSweepRetryNotBefore;
									// Requeue drained events after snapshot failure so the bounded queue can retry them.
									if (changes != null && changes.Count > 0)
										NotifyProcessChanges(new ProcessChangeBatch(changes.ToArray(), false));
								}
							}
						}
						else
						{
							if (flag3)
							{
								Interlocked.Exchange(ref overflowSweepRequested, 1);
							}
							try
							{
								HandleProcessChanges(changes);
							}
							catch
							{
								// Reconcile partial batches from a full snapshot without immediate self-wake replay.
								Interlocked.Exchange(ref overflowSweepRequested, 1);
								if (ticks >= failedSweepRetryNotBefore)
									failedSweepRetryNotBefore = DateTime.UtcNow.AddMilliseconds(FailedSweepRetryMs).Ticks;
								retryPhase.NotBefore = failedSweepRetryNotBefore;
								num = failedSweepRetryNotBefore;
								throw;
							}
						}
						if (ticks >= nextCoreRetry)
						{
							core.RetryPending();
							nextCoreRetry = ticks + TimeSpan.FromSeconds(8).Ticks;
						}
						SyncAffinity(Paused || flag);
					}
					Logger.RepeatedRecovered("Tamer");
				}
				catch (Exception ex)
				{
					Logger.RepeatedError("Tamer", Lang.T("log.tamer.7") + ex.GetType().Name + ": " + ex.Message, -1L);
				}
				long num4 = num - DateTime.UtcNow.Ticks;
				long num5 = Math.Max(num2, failedSweepRetryNotBefore) - DateTime.UtcNow.Ticks;
				if (Interlocked.CompareExchange(ref overflowSweepRequested, 0, 0) != 0 && num5 > 0 && (num4 <= 0 || num5 < num4))
				{
					num4 = num5;
				}
				int millisecondsTimeout = (int)((num4 <= 0) ? 8000 : Math.Min(8000L, Math.Max(1L, num4 / 10000)));
				if (affinityFastWake) millisecondsTimeout = Math.Min(millisecondsTimeout, 1000);
				kick.WaitOne(millisecondsTimeout);
			}
			affinityShutdownOk = ReleaseAll(Lang.T("t.gamemode.53"));
			if (affinityRuntime != null) affinityShutdownOk = affinityRuntime.Shutdown() && affinityShutdownOk;
		}

		private void SyncAffinity(bool suspended)
		{
			AntiCheatAffinityGameContext context = suspended ? null : GovernanceContext();
			List<KeyValuePair<string, int>> services = context == null ? null : GovernanceServices();
			Dictionary<string, string> activeGroups = BuildActive();
			List<AntiCheatAffinityTarget> targets = new List<AntiCheatAffinityTarget>();
			foreach (int pid in core.PidsWith(SuppressReason.AntiCheat))
			{
				string name = core.NameOf(pid), group;
				long birth = core.CreationOf(pid);
				AcquireRequest retained = new AcquireRequest { Pid = pid, Name = name, Birth = birth };
				if (suspended || name == null || !activeGroups.TryGetValue(name, out group)
					|| CheckGovernanceAdmission(retained, context, services) != null)
				{
					core.Release(pid, SuppressReason.AntiCheat);
					continue;
				}
				if (!suspended && birth > 0 && name != null)
					core.Reconcile(pid, name, SuppressReason.AntiCheat);
				if (birth > 0 && core.IsThrottled(pid) && name != null && activeGroups.TryGetValue(name, out group))
					targets.Add(new AntiCheatAffinityTarget { Pid = pid, Birth = birth, Name = name, Group = group });
			}
			if (targets.Count == 0) context = null;
			affinityRuntime.Tick(context, targets, AntiCheatModes.CoreLimitOf((AntiCheatMode)mode));
			affinityFastWake = context != null || affinityRuntime.PendingRecovery || (!suspended && targets.Count > 0);
		}

		private AntiCheatAffinityGameContext GovernanceContext()
		{
			try { return AffinityGameContext == null ? null : AffinityGameContext(); }
			catch { return null; }
		}

		private void RefreshGovernanceSession()
		{
			AntiCheatAffinityGameContext context = GovernanceContext();
			string key = context == null ? null : context.Key;
			if (key == governanceContextKey) return;
			governanceContextKey = key;
			governanceServicesNextRead = 0L;
			Interlocked.Exchange(ref fullSweepRequested, 1);
		}

		private List<KeyValuePair<string, int>> GovernanceServices()
		{
			long now = DateTime.UtcNow.Ticks;
			if (now >= governanceServicesNextRead)
			{
				try { governanceServices = SvcQuery.RunningServices(); }
				catch { governanceServices = null; }
				governanceServicesNextRead = now + TimeSpan.FromSeconds(8).Ticks;
			}
			return governanceServices;
		}

		private static bool ReadGovernanceIdentity(int pid, out string name, out long birth, out int session)
		{
			name = null; birth = 0; session = -1;
			try
			{
				using (Process process = Process.GetProcessById(pid))
				{
					name = process.ProcessName;
					birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
					session = process.SessionId;
					return birth > 0 && !process.HasExited;
				}
			}
			catch { return false; }
		}

		private static string CheckGovernanceAdmission(AcquireRequest request, AntiCheatAffinityGameContext context,
			IEnumerable<KeyValuePair<string, int>> services)
		{
			if (context == null || context.GameAncestors == null) return "game-ancestry-unavailable";
			if (request == null || request.Birth <= 0) return "process-identity-unavailable";
			string name; long birth; int session;
			if (!ReadGovernanceIdentity(request.Pid, out name, out birth, out session)
				|| birth != request.Birth || !string.Equals(name, request.Name, StringComparison.OrdinalIgnoreCase))
				return "process-identity-changed";
			string reason;
			return AntiCheatAffinityEligibility.Eligible(name, request.Pid, session, services, context.GameAncestors, out reason)
				? null : reason;
		}

		private string CheckAffinityAdmission(AntiCheatAffinityTarget target, AntiCheatAffinityGameContext context)
		{
			try
			{
				using (Process process = Process.GetProcessById(target.Pid))
				{
					if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != target.Birth) return "process-identity-changed";
					string reason;
					return AntiCheatAffinityEligibility.Eligible(target.Name, target.Pid, process.SessionId,
						SvcQuery.RunningServices(), context.GameAncestors, out reason) ? null : reason;
				}
			}
			catch { return "service-identity-unavailable"; }
		}

		internal static int FullSweepInterval(bool eventsAvailable)
		{
			if (!eventsAvailable)
			{
				return 8000;
			}
			return 60000;
		}

		private List<ProcessChange> DrainProcessChanges()
		{
			lock (eventSync)
			{
				if (pendingChanges.Count == 0)
				{
					return null;
				}
				List<ProcessChange> list = new List<ProcessChange>(pendingChanges);
				pendingChanges.Clear();
				list.Sort((ProcessChange left, ProcessChange right) => left.Sequence.CompareTo(right.Sequence));
				return list;
			}
		}

		private void HandleProcessChanges(List<ProcessChange> changes)
		{
			if (changes == null || changes.Count == 0)
			{
				return;
			}
			Dictionary<string, string> dictionary = BuildActive();
			Dictionary<int, AcquireRequest> dictionary2 = new Dictionary<int, AcquireRequest>();
			HashSet<int> hashSet = new HashSet<int>();
			HashSet<int> hashSet2 = new HashSet<int>(core.PidsWith(SuppressReason.AntiCheat));
			foreach (ProcessChange change in changes)
			{
				string value;
				AcquireRequest request;
				if (change.Kind == ProcessChangeKind.Stopped)
				{
					if ((hashSet2.Contains(change.Pid) || dictionary2.ContainsKey(change.Pid)) && !IsPidCurrentlyAlive(change.Pid))
					{
						dictionary2.Remove(change.Pid);
						hashSet.Add(change.Pid);
						hashSet2.Remove(change.Pid);
					}
				}
				else if (!string.IsNullOrEmpty(change.Name) && dictionary.TryGetValue(change.Name, out value) && TryBuildAcquireRequest(change.Pid, change.Name, value, out request))
				{
					hashSet.Remove(change.Pid);
					dictionary2[change.Pid] = request;
				}
			}
			ApplyBatch(new List<AcquireRequest>(dictionary2.Values), new List<int>(hashSet));
		}

		private static bool IsPidCurrentlyAlive(int pid)
		{
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return !Native.LastOpenProcessFailureWasNoSuchProcess();
			}
			try
			{
				return true;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private Dictionary<string, string> BuildActive()
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			lock (sync)
			{
				if (!groupMigrationReady) return dictionary;
				AcGroup[] groups = AntiCheatCatalog.Groups;
				foreach (AcGroup acGroup in groups)
				{
					bool value;
					if (acGroup.Suppressible && enabled.TryGetValue(acGroup.Key, out value) && value)
					{
						string[] procs = acGroup.Procs;
						foreach (string key in procs)
						{
							dictionary[key] = acGroup.Key;
						}
					}
				}
				return dictionary;
			}
		}

		internal int ObserveRecordOnlyProcesses(ProcessSnapshot snapshot)
		{
			if (snapshot == null)
			{
				return 0;
			}
			HashSet<int> hashSet = new HashSet<int>();
			int num = 0;
			ProcEntry[] entries = snapshot.Entries;
			foreach (ProcEntry procEntry in entries)
			{
				if (procEntry.Pid <= 0 || procEntry.Pid == selfPid || procEntry.Creation <= 0 || selfSession < 0 || (procEntry.Session != selfSession && procEntry.Session != 0))
				{
					continue;
				}
				AcGroup acGroup = AntiCheatCatalog.RecordOnlyGroupOf(procEntry.Name);
				if (acGroup != null)
				{
					hashSet.Add(procEntry.Pid);
					long value;
					if (!recordedProcesses.TryGetValue(procEntry.Pid, out value) || value != procEntry.Creation)
					{
						recordedProcesses[procEntry.Pid] = procEntry.Creation;
						Logger.Info(Lang.F("log.tamer.recordonly", acGroup.Name, procEntry.Name, procEntry.Pid));
						num++;
					}
				}
			}
			foreach (int item in new List<int>(recordedProcesses.Keys))
			{
				if (!hashSet.Contains(item))
				{
					recordedProcesses.Remove(item);
				}
			}
			return num;
		}

		private bool Sweep()
		{
			Dictionary<string, string> dictionary = BuildActive();
			HashSet<int> hashSet = new HashSet<int>();
			HashSet<int> hashSet2 = new HashSet<int>(core.PidsWith(SuppressReason.AntiCheat));
			List<AcquireRequest> list = new List<AcquireRequest>();
			if (dictionary.Count > 0)
			{
				ProcessSnapshot processSnapshot;
				try
				{
					processSnapshot = CaptureSweepSnapshot();
				}
				catch
				{
					processSnapshot = null;
				}
				if (processSnapshot == null)
				{
					return false;
				}
				ProcEntry[] entries = processSnapshot.Entries;
				foreach (ProcEntry procEntry in entries)
				{
					int num = -1;
					try
					{
						num = procEntry.Pid;
						string name = procEntry.Name;
						if (num != selfPid)
						{
							int session = procEntry.Session;
							string value;
							if (dictionary.TryGetValue(name, out value) && selfSession >= 0 && (session == selfSession || session == 0))
							{
								hashSet.Add(num);
								list.Add(new AcquireRequest
								{
									Pid = num,
									Name = name,
									Group = value,
									Birth = procEntry.Creation
								});
							}
						}
					}
					catch
					{
						if (num > 0 && hashSet2.Contains(num))
						{
							hashSet.Add(num);
						}
					}
				}
			}
			List<int> list2 = new List<int>();
			foreach (int item in hashSet2)
			{
				if (!hashSet.Contains(item))
				{
					list2.Add(item);
				}
			}
			ApplyBatch(list, list2);
			return true;
		}

		private bool TryBuildAcquireRequest(int pid, string expectedName, string group, out AcquireRequest request)
		{
			request = null;
			if (pid == selfPid) return false;
			string name; long birth; int session;
			if (!ReadGovernanceIdentity(pid, out name, out birth, out session)
				|| !string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase)
				|| selfSession < 0 || (session != selfSession && session != 0)) return false;
			request = new AcquireRequest { Pid = pid, Name = name, Group = group, Birth = birth };
			return true;
		}

		private void ApplyBatch(List<AcquireRequest> acquisitions, List<int> releases)
		{
			int num = ((acquisitions != null) ? acquisitions.Count : 0);
			int num2 = ((releases != null) ? releases.Count : 0);
			if (num == 0 && num2 == 0)
			{
				return;
			}
			SuppressionCore.BatchResult batchResult = null;
			AntiCheatAffinityGameContext context = GovernanceContext();
			List<KeyValuePair<string, int>> services = context == null ? null : GovernanceServices();
			core.BeginBatch();
			try
			{
				if (acquisitions != null)
				{
					foreach (AcquireRequest acquisition in acquisitions)
					{
						try
						{
							string blocked = !IsGroupEnabled(acquisition.Group) ? "group-disabled"
								: CheckGovernanceAdmission(acquisition, context, services);
							if (blocked != null)
							{
								core.Release(acquisition.Pid, SuppressReason.AntiCheat);
								acquisition.Result = AcquireResult.AlreadyProtected;
								continue;
							}
							acquisition.Result = core.Acquire(acquisition.Pid, acquisition.Name, SuppressReason.AntiCheat, acquisition.Group, AntiCheatModes.LevelOf((AntiCheatMode)mode), acquisition.Birth);
						}
						catch
						{
							acquisition.Result = AcquireResult.ApplyFailed;
						}
					}
				}
				if (releases != null)
				{
					foreach (int release in releases)
					{
						try
						{
							core.Release(release, SuppressReason.AntiCheat);
						}
						catch
						{
						}
					}
				}
			}
			finally
			{
				batchResult = core.EndBatch();
			}
			if (acquisitions == null)
			{
				return;
			}
			foreach (AcquireRequest acquisition2 in acquisitions)
			{
				if ((acquisition2.Result == AcquireResult.NewlyThrottled || acquisition2.Result == AcquireResult.AlreadyThrottled) && (batchResult == null || !batchResult.WasApplied(acquisition2.Pid)))
				{
					string text = ((batchResult != null) ? batchResult.FailureOf(acquisition2.Pid) : "batch-missing");
					if (text == "self-protected")
					{
						acquisition2.Result = AcquireResult.NewlyProtected;
						acquisition2.FailureDetail = text;
					}
					else
					{
						acquisition2.Result = AcquireResult.ApplyFailed;
						acquisition2.FailureDetail = text;
					}
				}
				LogAcquireResult(acquisition2);
			}
		}

		private static void LogAcquireResult(AcquireRequest request)
		{
			if (request.Result == AcquireResult.NewlyThrottled)
			{
				Logger.Log(Lang.T("log.tamer.8") + request.Name + " pid " + request.Pid);
			}
			else if (request.Result == AcquireResult.NewlyProtected && request.FailureDetail != "self-protected")
			{
				Logger.Log(Lang.T("log.tamer.9") + request.Name + " pid " + request.Pid + Lang.T("log.tamer.10"));
			}
			else if (request.Result == AcquireResult.ApplyFailed)
			{
				Logger.Log(Lang.T("log.tamer.8") + request.Name + " pid " + request.Pid + Lang.T("log.tamer.11") + (string.IsNullOrEmpty(request.FailureDetail) ? "" : (Lang.T("log.tamer.12") + request.FailureDetail)) + Lang.T("log.tamer.13"));
			}
		}

		private bool ReleaseAll(string reason)
		{
			bool affinityRestored = affinityRuntime == null || affinityRuntime.Stop();
			int num = core.ReleaseReason(SuppressReason.AntiCheat);
			if (num > 0)
			{
				Logger.Log(Lang.T("log.tamer.14") + reason + Lang.T("log.gamemodeboost.53") + num + Lang.T("log.program.3"));
			}
			return affinityRestored && !core.AnyWith(SuppressReason.AntiCheat);
		}
	}
}
