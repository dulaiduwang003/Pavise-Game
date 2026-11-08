using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal partial class GameMode
	{
		private sealed class AutoAddAttempt
		{
			internal int Pid;

			internal int Misses;

			internal int Confirmations;

			internal long Creation;

			internal long EvidenceAt;

			internal long LastSeen;

			internal long NoteAt;

			internal string Note;
		}

		private readonly ProcessActionFailureBudget autoAddFailureBudget = new ProcessActionFailureBudget();
		private readonly ProcessActionFailureBudget rendererProbeFailureBudget = new ProcessActionFailureBudget();

		private enum ExclusiveWriteBack
		{
			Written,
			Busy,
			Deferred,
			Failed
		}


		private struct AutomationLastInput
		{
			internal uint Size;

			internal uint Time;
		}

		private sealed class BoostPass
		{
			public bool CpuBoost = true;

			public bool MemoryBoost = true;

			public bool IoBoost = true;

			public bool GpuBoost = true;

			public bool NvMaxPerf;

			public string NvLowLat;

			public bool NvSmooth;

			public bool NvShader;

			public bool NvRebar;

			public string NvDlss;

			public bool NvVrr;

			public bool Exclusive;

			public ulong DesiredMask;

			public int RendererPid;

			public long RendererCreation;

			public string RendererName;

			public string ActionTarget;

			public bool WriteDenied;

			public bool LaneAllowed;

			public bool LaneRestorationReady = true;

			public uint PriorityTarget = Native.HIGH_PRIORITY_CLASS;

			public long IdentityGeneration;

			public string PlacementStage;

			public int PlacementError;
		}

		private enum HardPinHandleState
		{
			Match,
			Gone,
			Mismatch,
			Unknown
		}

		private enum BoostTarget
		{
			Alive,
			Gone,
			Unopenable
		}

		private sealed class CriticalThreadMint
		{
			internal readonly int Pid;

			internal readonly long Birth;

			internal readonly long Epoch;

			internal readonly long Serial;

			internal readonly string Path;

			internal readonly string Rule;

			internal readonly ulong Allowed;

			internal CriticalThreadMint(int pid, long birth, string path, ulong allowed, long epoch, long serial, string rule)
			{
				Pid = pid;
				Birth = birth;
				Path = path;
				Allowed = allowed;
				Epoch = epoch;
				Serial = serial;
				Rule = rule;
			}
		}

		private sealed class CriticalProcessPin : IDisposable
		{
			private object held;

			private readonly int thread = Thread.CurrentThread.ManagedThreadId;

			internal CriticalProcessPin(object gate)
			{
				if (!Monitor.TryEnter(gate, 3000))
				{
					throw new TimeoutException("Process/thread placement is still changing");
				}
				held = gate;
			}

			public void Dispose()
			{
				if (held != null)
				{
					if (thread != Thread.CurrentThread.ManagedThreadId)
					{
						throw new InvalidOperationException("Placement pin must close on its original thread");
					}
					object obj = held;
					held = null;
					Monitor.Exit(obj);
				}
			}
		}

		internal sealed class CriticalThreadTargetLease
		{
			private readonly GameMode owner;

			private readonly long epoch;

			private readonly long gameSerial;

			private readonly string store;

			private readonly string rule;

			private int revoked;

			internal readonly Guid SessionId;

			internal readonly int ProcessId;

			internal readonly long ProcessCreation;

			internal readonly string ExecutablePath;

			internal readonly ulong AllowedMask;

			internal readonly bool WorkerAvoidanceAllowed;

			internal string CanonicalStore
			{
				get
				{
					return store;
				}
			}

			internal bool Revoked
			{
				get
				{
					return Volatile.Read(ref revoked) != 0;
				}
			}

			private CriticalThreadTargetLease(GameMode originalOwner, int pid, long birth, string executable, ulong allowed, long originalEpoch, long originalGameSerial, string originalRule)
			{
				owner = originalOwner;
				ProcessId = pid;
				ProcessCreation = birth;
				ExecutablePath = Path.GetFullPath(executable);
				AllowedMask = allowed;
				epoch = originalEpoch;
				gameSerial = originalGameSerial;
				rule = originalRule;
				WorkerAvoidanceAllowed = CriticalThreadRule.ParseForRuntime(originalRule).WorkerAvoidance;
				store = owner.CriticalThreadStoreRoot;
				SessionId = Guid.NewGuid();
			}

			internal static CriticalThreadTargetLease Issue(GameMode originalOwner, object originalMint)
			{
				CriticalThreadMint criticalThreadMint = originalMint as CriticalThreadMint;
				if (originalOwner == null || criticalThreadMint == null || !Monitor.IsEntered(originalOwner.criticalProcessGate) || !object.ReferenceEquals(originalOwner.criticalMintInFlight, criticalThreadMint) || Interlocked.Read(ref originalOwner.criticalIntentEpoch) != criticalThreadMint.Epoch)
				{
					throw new InvalidOperationException("Only the original GameMode renderer factory can issue a target lease");
				}
				originalOwner.criticalMintInFlight = null;
				return new CriticalThreadTargetLease(originalOwner, criticalThreadMint.Pid, criticalThreadMint.Birth, criticalThreadMint.Path, criticalThreadMint.Allowed, criticalThreadMint.Epoch, criticalThreadMint.Serial, criticalThreadMint.Rule);
			}

			internal IDisposable EnterUse()
			{
				return new CriticalProcessPin(owner.criticalProcessGate);
			}

			internal bool RegisterGuardianActor(HardThreadGuardianActor actor)
			{
				RequireActive();
				if (actor == null)
				{
					return false;
				}
				owner.criticalGuardianActor = actor;
				return owner.ReleaseCriticalGuardianBackground();
			}

			internal bool UnregisterGuardianActor(HardThreadGuardianActor actor)
			{
				if (actor == null || !Monitor.TryEnter(owner.criticalProcessGate, 0))
				{
					return false;
				}
				try
				{
					RequireRecovery();
					HardThreadGuardianActor criticalGuardianActor = owner.criticalGuardianActor;
					if (criticalGuardianActor != null && (object.ReferenceEquals(criticalGuardianActor, actor) || criticalGuardianActor.Same(actor)))
					{
						owner.criticalGuardianActor = null;
					}
					return true;
				}
				finally
				{
					Monitor.Exit(owner.criticalProcessGate);
				}
			}

			internal void RequireActive()
			{
				RequireRecovery();
				string text = ((Volatile.Read(ref revoked) != 0) ? "revoked" : ((Interlocked.Read(ref owner.criticalIntentEpoch) != epoch) ? "intent changed" : ((!object.ReferenceEquals(owner.criticalOriginalTarget, this)) ? "superseded" : ((!owner.enabled || owner.stopping || owner.panicReq) ? "game mode stopped" : ((!Volatile.Read(ref owner.active)) ? "session ended" : ((Interlocked.Read(ref owner.gameSessionStarts) != gameSerial) ? "another session" : (owner.ProfileStoreSaveFailed ? "profile store unavailable" : null)))))));
				if (text != null)
				{
					throw new OperationCanceledException("The original game-thread intent is no longer active: " + text);
				}
			}

			internal void RequireRecovery()
			{
				if (!Monitor.IsEntered(owner.criticalProcessGate) || SessionId == Guid.Empty || ProcessId <= 0 || ProcessCreation <= 0 || AllowedMask == 0 || string.IsNullOrEmpty(rule) || store != owner.CriticalThreadStoreRoot)
				{
					throw new InvalidOperationException("No original game-thread restoration identity");
				}
			}

			internal void Revoke()
			{
				Interlocked.Exchange(ref revoked, 1);
			}
		}

		private sealed class CriticalRuntimeSource
		{
			internal readonly Guid Token = Guid.NewGuid();

			internal readonly string Profile;

			internal readonly string RuleText;

			internal readonly string Path;

			internal readonly string Name;

			internal readonly long Serial;

			internal readonly long Epoch;

			internal readonly long Birth;

			internal readonly int Pid;

			internal readonly CriticalThreadRule Rule;

			internal CriticalRuntimeSource(GameDetection detection, string raw, long serial, long epoch)
			{
				Profile = detection.Profile.Id;
				RuleText = raw;
				Rule = CriticalThreadRule.ParseForRuntime(raw);
				Path = detection.RendererPath;
				Name = detection.RendererName;
				Pid = detection.RendererPid;
				Birth = detection.RendererCreation;
				Serial = serial;
				Epoch = epoch;
			}

			internal bool Same(CriticalRuntimeSource other)
			{
				if (other != null && Profile == other.Profile && RuleText == other.RuleText && Serial == other.Serial && Epoch == other.Epoch && Pid == other.Pid && Birth == other.Birth)
				{
					return string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
				}
				return false;
			}

			internal string Difference(CriticalRuntimeSource other)
			{
				if (other != null)
				{
					if (Pid == other.Pid && Birth == other.Birth && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase))
					{
						if (!(Profile != other.Profile) && !(RuleText != other.RuleText))
						{
							if (Serial == other.Serial)
							{
								if (Epoch == other.Epoch)
								{
									return "source changed";
								}
								return "intent changed";
							}
							return "another session";
						}
						return "rule changed";
					}
					return "renderer changed";
				}
				return "source missing";
			}
		}

		private struct Snap
		{
			public uint Pri;

			public ulong Aff;

			public int Io;

			public int Pg;

			public string Name;

			public long Creation;

			public uint[] CpuSets;

			public int QoSControl;

			public int QoSState;

			public bool PlacementYielded;
		}

		private sealed class HardPin
		{
			public int Pid;

			public long Creation;

			public ulong OriginalAffinity;

			public ulong AppliedAffinity;

			public IntPtr RestoreHandle;

			public bool JobBound;

			public bool JobRecoveryOnly;

			public string JobFallbackReason;

			public bool RestoreRequested, RestoreInFlight;

			public long NextRestore;

			public int RestoreDelay;

			public int RestoreAttempts;

			public bool RestoreExitPhase;

			public bool RestoreTargetGone;
		}

		private struct GameId
		{
			public string Name;

			public long Creation;
		}

		private sealed class LaneFallbackTarget
		{
			internal readonly int Pid;

			internal readonly long Birth;

			internal LaneFallbackTarget(int pid, long birth)
			{
				Pid = pid;
				Birth = birth;
			}
		}

		private sealed class AutomaticFsoEntry
		{
			internal bool Disable;

			internal bool Configured;

			internal int Failures;

			internal long CheckAt;
		}

		private sealed class BackgroundRequest
		{
			public int Pid;

			public string Name;

			public long Creation;

			public long Cpu;

			public SuppressionLevel Desired;

			public SuppressionLevel Previous;

			public bool HadBackgroundReason;

			public AcquireResult Result;

			public string FailureDetail;
		}

		private sealed class WhitelistProcessInfo
		{
			public int Pid;

			public int Session = -1;

			public int Parent;

			public string Name;

			public string Path;

			public long Creation;

			public long Cpu;

			public ulong Io;
		}

		private sealed class WhitelistEvaluation
		{
			public readonly HashSet<int> Protected = new HashSet<int>();

			public Dictionary<int, PlatformProcessOwner> PlatformProcesses;

			public readonly HashSet<int> RuntimeProtected = new HashSet<int>();

			public readonly Dictionary<string, HashSet<int>> ByRule = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

			public readonly Dictionary<int, WhitelistProcessInfo> Processes = new Dictionary<int, WhitelistProcessInfo>();

			public readonly Dictionary<int, int> Parents = new Dictionary<int, int>();

			public readonly Dictionary<int, string> Names = new Dictionary<int, string>();

			public readonly Dictionary<int, long> Creations = new Dictionary<int, long>();
		}

		private const double AutoAddMinUtilization = 20.0;

		private const int AutoAddGateMs = 30000;

		private const int AutoAddRejectCacheLimit = 64;

		private const int AutoAddEvidenceAgeMs = 120000;

		private const uint DuplicateSameAccess = 2u;

		internal const string GamePriorityAction = "GamePriority";

		internal const string GameIoAction = "GameIoPriority";

		internal const string GameGpuAction = "GameGpuPriority";

		internal const string GameMemoryAction = "GameMemoryPriority";

		internal const string GameEcoAction = "GameEcoQoS";

		private const int VanishGiveUpTries = 20;

		private const string CriticalThreadAction = "game-critical-thread-hard-affinity";

		private const int BoostRetryMax = 3;

		private const int PlacementRetryMax = 3;

		private const int StateRetryMax = 8;

		internal const int SlowEnvDelaySeconds = 20;

		private const int StickyGraceMisses = 1;

		internal const string ExitGraceSettingKey = "GameExitGraceSeconds";

		internal const int DefaultExitGraceSeconds = 8;

		private const int EnvRetryBaseSeconds = 4;

		private const int EnvRetryCapSeconds = 60;

		private const int EnvRetryMaxSteps = 8;

		private const string PowerFailStreakKey = "PowerPlanFailStreak";

		internal const int SwitchOutDebounceMs = 5000;

		internal const string AutomaticFsoKey = "AutoDisableFullscreenOptimizations";

		private const string WhitelistFooterPrefix = "PAVISE_WHITELIST_END|";

		private const int PollingSweepIntervalMs = 4000;

		private const int EventBackedSweepIntervalMs = 20000;

		internal const int ActiveGameSweepIntervalMs = 500;

		internal const int EventBackedActiveGameSweepIntervalMs = 1000;

		internal const int DirtyScanFloorMs = 500;

		private const int FullGameDetectionIntervalMs = 20000;

		internal const int GameTransitionScanIntervalMs = 5000;

		internal const int FailedProcessScanRetryMs = 1000;

		private const int RendererActivityMaxAttempts = 5;

		internal const string LastSessionKey = "LastSessionBrief";

		internal const string StandbyCleanerOptionsKey = "GmStandbyCleanerOptionsV1";

		private const int MaxOverlayAttempts = 2;

		internal const int OlderRendererTakeoverForegroundMs = 10000;

		private const long TransientProcessTicks = 20000000L;

		private const int WhiteFamilyPruneIntervalMs = 20000;

		private readonly AdaptiveGuard adaptiveGuard = new AdaptiveGuard();

		private readonly CpuSaturation adaptivePressure = new CpuSaturation(AdaptiveGuard.EnterUtilization, AdaptiveGuard.ExitUtilization);

		private volatile bool adaptiveEscalated;

		private readonly Dictionary<string, AutoAddAttempt> autoAddAttempts = new Dictionary<string, AutoAddAttempt>(StringComparer.OrdinalIgnoreCase);

		private int autoAddEpoch;

		private bool autoAddPreferenceUnsaved;

		private string autoIgnorePath;

		private readonly LibraryIgnoreTransaction libraryIgnoreTransaction;

		private bool autoIgnoreLoadFailed;

		private volatile bool autoAddOn;

		private long autoAddGateTicks;

		private readonly HashSet<string> autoAddIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<string, long> autoAddRejectUntil = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

		private bool coreObservationOnly;

		private PerformancePreset corePreset;

		private bool coreAllowed;

		private long corePolicyCheckTicks;

		private int corePolicyDirty;

		private CoreIsolationClient coreIsolation;

		private AutoCorePlan corePlan;

		private ManualCorePlan coreManual;

		private AutoCoreGovernor coreGovernor;

		private int coreWatchPid;

		private long coreWatchCreation;

		private volatile bool coreEngaged;

		private volatile bool corePinPending;

		private long exclusiveCheckTicks;

		private readonly CoreAffinityAuditRetry exclusiveAuditRetry = new CoreAffinityAuditRetry();

		private readonly GameAffinityJobLease coreAffinityJobs = new GameAffinityJobLease();

		private bool exclusiveAuditDeferred;

		private ulong coreGameMask;

		private ulong[] corePhysical;

		private ulong coreAffinityObserved;

		private bool coreAffinityRead;

		private bool coreAffinityVerified;

		private string corePlacementStopReason;

		private int coreBackgroundMoved;

		private bool coreBackgroundPending;

		private string corePlacementEvidence;

		private int exclusiveWriteBacks;

		private int automaticNvPlanKey = -1;

		private readonly TimerModeController timerMode = new TimerModeController();

		private readonly Dictionary<string, string> automationEvidence = new Dictionary<string, string>(StringComparer.Ordinal);

		private string automationSession = "";

		private string automationSessionName = "";

		private bool automationSessionEnding;

		private long awakeAppliedLogTicks;

		private long awakeRestoredLogTicks;

		private bool awakeFinalRestoreLogged;

		private string awakeRecoveryState;

		private bool corePlacementOnly;

		private long boostFirstStampTicks;

		private int boostIdentityPid;

		private long boostIdentityCreation;

		private long boostIdentityGeneration;

		private bool boostIdentityNeedsAudit = true;

		private bool boostLaneReleased;

		private static readonly string[] BoostActions = new string[5] { "GamePriority", "GameIoPriority", "GameGpuPriority", "GameMemoryPriority", "GameEcoQoS" };

		private readonly Dictionary<int, string> boostActionTargets = new Dictionary<int, string>();

		private readonly Dictionary<int, HashSet<string>> boostActionRestorePending = new Dictionary<int, HashSet<string>>();

		private long nvTweakRetryAtTicks;

		private int nvTweakInFlight;

		private long nvTweakGeneration;

		private readonly Dictionary<int, int> boostVanishTries = new Dictionary<int, int>();

		private volatile bool cacheWarmOn;

		private int cacheWarmGeneration;

		private long cacheWarmSessionId;

		private readonly CacheWarmRunner cacheWarm = new CacheWarmRunner();

		private CoreWorkBudget coreBudget = new CoreWorkBudget();

		private readonly object criticalProcessGate = new object();

		private long criticalIntentEpoch;

		private CriticalThreadTargetLease criticalOriginalTarget;

		private CriticalThreadMint criticalMintInFlight;

		private int criticalRolesPid;

		private long criticalRolesBirth;

		private bool criticalRolesTouched;

		private readonly object criticalHistoricalQueueGate = new object();

		private readonly ManualResetEvent criticalHistoricalIdle = new ManualResetEvent(true);

		private bool criticalHistoricalWorker;

		private long criticalHistoricalRetry;

		private volatile CriticalThreadUiState criticalThreadUi = new CriticalThreadUiState("", "off", "", false, false, false, 0, 0L, 0uL, null, null, false, false, CriticalThreadCoverageState.None, null, 0L, 0L, 0, 0L);

		private CriticalRuntimeSource criticalSource;

		private CriticalThreadObservation criticalObservation = new CriticalThreadObservation();

		// Require two consecutive thread-set samples before committing automatic isolation.
		private string criticalAutoSelectionKey = "";

		private int criticalAutoSelectionStable;

		private readonly CriticalThreadIdealHints criticalIdealHints = new CriticalThreadIdealHints();

		private CriticalThreadHardAffinitySession criticalSession;

		private bool criticalRestorationConfirmed;

		// Threads restored, waiting only for the stopped helper to exit and finish its cleanup.
		private bool criticalHelperClosureWaiting;

		// Set by the bounded retry owner while that wait is observed rather than re-disposed.
		private bool criticalClosureObserveOnly;

		private HardThreadPlan criticalPlan;

		private volatile HardThreadGuardianActor criticalGuardianActor;

		private volatile bool criticalThreadResidue;

		private volatile bool criticalHistoricalDebt;

		private long criticalDebtNextRead;

		private bool criticalFailureAdmitted;

		private bool criticalWorkerBudgetReached;

		private int criticalReassertions;
		private CriticalThreadStabilityPolicy criticalStability;
		private CriticalThreadRuntimeTelemetry criticalRuntimeTelemetry;
		private string criticalLastAuditDetail = "";
		private int criticalObservationMisses;

		private string criticalFailureTarget;

		private long criticalNextSample;

		private int criticalForegroundAuditPending;

		private bool criticalGuardianLaunchBlocked;

		private string[] criticalChoiceLabels = new string[0];

		private bool criticalChoiceFrameAssociated;

		private static readonly object criticalStateLogGate = new object();

		private Dictionary<string, long> criticalStateLogged;

		private string criticalStateLast;

		private readonly object sync = new object();

		private readonly List<GameProfile> profiles = new List<GameProfile>();

		private readonly GameProfileStore profileStore;

		private int profileSaveFailureSignaled;

		private readonly string dataDir;

		private readonly AutoResetEvent kick = new AutoResetEvent(true);

		private readonly string whitePath;

		private readonly int selfPid;

		private readonly string selfName;

		private readonly int selfSession;

		private readonly string windowsPrefix;

		private readonly SuppressionCore core;

		private volatile bool enabled;

		private volatile bool stopping;

		private bool active;

		private string activeGame;

		private bool firstSweep = true;

		private readonly Dictionary<int, Snap> gameBoost = new Dictionary<int, Snap>();

		private readonly Dictionary<int, int> gameGpu = new Dictionary<int, int>();

		private readonly Dictionary<int, ulong> gamePlacement = new Dictionary<int, ulong>();

		private readonly Dictionary<int, HardPin> hardPins = new Dictionary<int, HardPin>();

		private readonly Dictionary<int, int> boostFail = new Dictionary<int, int>();

		private readonly Dictionary<int, long> gameBoostNextAudit = new Dictionary<int, long>();

		private readonly HashSet<int> tweakApplied = new HashSet<int>();

		private readonly HashSet<int> boostDenied = new HashSet<int>();

		private readonly HashSet<int> boostStateWarned = new HashSet<int>();

		private readonly HashSet<int> boostStateVerified = new HashSet<int>();

		private readonly HashSet<int> boostHandleStripped = new HashSet<int>();

		private readonly HashSet<int> boostEcoGaveUp = new HashSet<int>();

		private readonly Dictionary<int, int> placementFail = new Dictionary<int, int>();

		private readonly HashSet<int> placementGaveUp = new HashSet<int>();

		private readonly Dictionary<int, int> boostStateFail = new Dictionary<int, int>();

		private volatile bool bgSuppressOn;

		private volatile bool boostOn;


		private volatile bool pauseMaintOn;

		private bool maintActive;

		private volatile bool nvMaxPerf;

		private volatile bool nvVrrWindowedOn;

		private volatile bool intelEnduranceOn;

		private bool nvVrrActive;

		private bool intelEndActive;

		private volatile string nvLowLatMode = "off";

		private volatile bool nvSmoothMotion;

		private volatile bool nvShaderCacheMax;

		private volatile bool nvRebarOn;

		private volatile string nvDlssMode = "off";



		private volatile bool dwmBoostOn;


		private volatile bool amdAfmf;

		private volatile bool rsrOn;

		private bool pqosActive;

		private bool awakeActive;

		private bool dwmBoostActive;

		private bool gpwActive;

		private readonly TimerResolutionLease timerLease = new TimerResolutionLease();

		private volatile bool killGameDvr;

		private volatile bool mmcssOn;

		private bool guardDvrActive;

		private bool guardMmcssActive;

		private volatile bool planSwitch;

		private long slowEnvAtTicks;

		private volatile bool pauseUpdateOn;

		private volatile bool pauseServicesOn;

		private volatile bool renderLaneOn;

		private volatile bool panicReq;

		private int panicSeq;

		private readonly object panicCallGate = new object();

		private int panicServed;

		private volatile bool panicResult;

		private readonly ManualResetEvent panicDone = new ManualResetEvent(true);

		private bool doActive;

		private readonly ulong allMask;

		private PerformancePreset preset;

		private GameDetection activeDetection;

		private volatile PolicySnapshot sessionPolicy;

		private Thread worker;

		private GameDetection stickyDetection;

		private readonly Dictionary<int, GameId> stickyIds = new Dictionary<int, GameId>();

		private int stickyMiss;

		private bool stickyGraceOnly;

		private volatile int exitGraceSeconds = 8;

		private int gracePreReleased;

		private long gameGoneSinceTicks;

		internal static readonly string[] RetiredIrqObservationFiles = new string[4] { "Pavise.irq-sessions.dat", "Pavise.irq-sessions.dat.*.tmp", "irq-adjustment-*.xml", "irq-adjustment-*.xml.*.tmp" };

		private volatile bool englishInputOn;

		private int englishInputGeneration;

		private long englishInputCreationFloor;

		private EnglishInputOnce englishInputOnce;

		private readonly Dictionary<string, long> envNextAttempt = new Dictionary<string, long>(StringComparer.Ordinal);

		private readonly Dictionary<string, int> envFailures = new Dictionary<string, int>(StringComparer.Ordinal);

		private readonly HashSet<string> envFused = new HashSet<string>(StringComparer.Ordinal);

		private readonly HashSet<string> envRestorePending = new HashSet<string>(StringComparer.Ordinal);

		internal static readonly string[] EnvKeys = new string[17]
		{
			"do", "wu", "services", "standby", "pqos", "awake", "dwmboost", "rsr", "gpupower", "amdalag",
			"amdafmf", "maint", "nvvrr", "intelend", "laptopperf", "gamedvr", "mmcss"
		};


		private bool wuActive;

		private bool optionalServicesActive;

		private bool optionalServicesWanted;

		private int optionalServiceGeneration;

		private bool amdAlagActive;

		private bool amdAfmfActive;

		private bool rsrActive;

		private volatile bool planActive;

		private volatile int lastPowerPolicyKey = -1;

		private long nextPowerAuditTicks;

		private int powerApplyInFlight;

		private int powerPlanNotificationPending;

		private int powerSessionGen;

		private volatile bool powerSessionEnding;

		private readonly object powerApplyGate = new object();

		private readonly HashSet<int> powerStoppedKeys = new HashSet<int>();

		private int planFailStreak;

		private int envResidueGeneration = -1;

		private bool envResidueOther;

		private bool envResidueNvList;

		private bool envResidueGpuPref;




		private long residueNextTryTicks;

		private GameFamilyHistory gameFamilyHistory;

		private GameFamilyEvidence gameFamilyEvidence = GameFamilyEvidence.Empty;

		private readonly object familyPolicyGate = new object();

		private int familyPolicyEpoch;

		private readonly SessionForegroundTracker sessionForeground = new SessionForegroundTracker();

		private volatile bool smartSwitchedOut;

		private volatile bool sessionSwitchedOut;

		private volatile string switchedOutForegroundName = "";

		private int switchedOutForegroundPid;

		private static readonly string[][] ForegroundServiceConsumers = new string[9][]
		{
			new string[2] { "searchhost", "services" },
			new string[2] { "searchapp", "services" },
			new string[2] { "startmenuexperiencehost", "services" },
			new string[2] { "outlook", "services" },
			new string[2] { "printdialog", "services" },
			new string[2] { "systemsettings", "wu" },
			new string[2] { "systemsettings", "do" },
			new string[2] { "winstore.app", "wu" },
			new string[2] { "winstore.app", "do" }
		};

		private volatile LaneFallbackTarget laneFallback;

		private readonly Dictionary<string, AutomaticFsoEntry> automaticFsoEntries = new Dictionary<string, AutomaticFsoEntry>(StringComparer.OrdinalIgnoreCase);

		private long automaticFsoCheckAt;

		private string automaticFsoState = "";

		private readonly object gameNvGate = new object();

		private GameNvCanonicalStore gameNvStore;

		private bool gameNvRecoveryChecked;

		private bool gameNvRootClear;

		private bool gameNvWorker;

		private bool gameNvRecoveryStopped;


		private int gameNvPending = 1;

		private readonly ManualResetEvent gameNvIdle = new ManualResetEvent(true);

		private string whitelistLastError = "";

		private volatile bool shutdownWritersStopped;


		private readonly PerformanceActionCoordinator performanceActions = new PerformanceActionCoordinator();

		private Dictionary<int, PlatformProcessOwner> platformMembers = new Dictionary<int, PlatformProcessOwner>();

		private int whiteEvalPlatformMask = -1;

		private int whiteEvalPlatformRenderer;

		private long whiteEvalPlatformRendererCreation;

		private volatile string preStagedNvPath;

		private int preStagedAutomaticNvKey = -1;

		private readonly object driverStageGate = new object();

		private int processSetDirty;

		private int urgentProcessScan;

		private volatile bool armedAwaitingElection;

		private int transitionScanPending;

		private int transitionProbeRendererPid;

		private long transitionProbeRendererCreation;

		private int gameDetectionDirty = 1;

		private int processEventsAvailable;

		private long lastProcessScanTicks;

		private long processScanRetryAfterTicks;

		private long lastFullGameDetectionTicks;

		private long processScanCount;

		private RendererHandoffTracker rendererHandoff;

		private GameDetection rendererDirectPending;

		private int rendererObservedForeground;

		private long rendererForegroundSinceMs;

		private int rendererHandoffEpoch;

		private RendererObservationStore rendererObservations;

		private int rendererValidationBusy;

		private int rendererActivityBusy;

		private int rendererGpuSamplingBusy;

		private long rendererActivityNextMs;

		private GameDetection rendererActivityTarget;

		private int rendererActivityAttempts;

		private int rendererActivityEpoch;

		private long rendererActivityWaitLogMs;

		private long rendererActivityBudgetNextMs;

		private readonly object rendererObservationTraceGate = new object();

		private readonly Queue<string> rendererObservationTraceLines = new Queue<string>();

		private int rendererObservationTraceBusy;

		private readonly Dictionary<int, HashSet<string>> boostResourceSelections = new Dictionary<int, HashSet<string>>();

		private int boostResourceSelection = -1;

		private long gameSessionStarts;

		private volatile string armedGameName;

		private string lastArmedLogged;

		private volatile bool gpuPrefStageOn;


		private readonly Dictionary<int, long> repCpu = new Dictionary<int, long>();

		private readonly Dictionary<int, long> repCreation = new Dictionary<int, long>();

		private readonly Dictionary<int, string> repProc = new Dictionary<int, string>();

		private readonly Dictionary<int, long> repSealed = new Dictionary<int, long>();

		private long repStart;

		private string repGame;

		private long repPaviseCpuStart;

		private string repProfileId;

		private volatile bool standbyCleanerOn;

		private volatile bool standbyCleanerOptionsValid;

		private volatile StandbyCleanerOptions standbyCleanerOptions = StandbyCleanerOptions.Default;

		private int standbyCleanerGeneration;

		private int standbyCleanerRestorePending;

		private StandbyCleanerRunner standbyCleaner;

		private int modeLockGamePid;

		private long modeLockGameCreation;

		private string modeLockGameProfileId;




		private static readonly HashSet<int> EmptyPidSet = new HashSet<int>();

		private ProcessSnapshot servicePolicySnapshot;

		private List<KeyValuePair<string, int>> servicePolicyHosts;

		private Dictionary<int, KeyValuePair<long, string>> serviceImagePaths;

		private readonly List<WhitelistRule> whiteRules = new List<WhitelistRule>();

		private readonly HashSet<string> whiteRuleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<string, Dictionary<int, long>> whiteFamilyMembers = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);

		private readonly object whiteEvalSync = new object();

		private int whiteRevision;

		private int whiteHasFamilyRules;

		private long lastWhiteFamilyPruneTicks;

		private ProcessSnapshot whiteEvalSnapshot;

		private WhitelistEvaluation whiteEvalCached;

		private int whiteEvalRevision;

		private int whiteEvalMembersVersion;

		private int whiteFamilyMembersVersion;

		private Dictionary<int, long> runtimeFamilyMembers = new Dictionary<int, long>();

		private bool AdaptiveEscalationWanted
		{
			get
			{
				if (AutomationPolicySnapshot.AdaptiveEscalation && !stopping)
				{
					return !panicReq;
				}
				return false;
			}
		}

		public bool AutoAddFullscreen
		{
			get
			{
				return autoAddOn;
			}
			set
			{
				lock (sync)
				{
					if (autoAddOn == value)
					{
						return;
					}
					autoAddOn = value;
					Interlocked.Increment(ref autoAddEpoch);
					if (value)
					{
						Interlocked.Exchange(ref autoAddGateTicks, 0L);
					}
					autoAddPreferenceUnsaved = !Settings.Save("GmAutoAdd", value);
				}
				Logger.Log(Lang.T(value ? "log.autoadd.1" : "log.autoadd.2"));
				if (autoAddPreferenceUnsaved)
				{
					Logger.Warn(Lang.T("autoadd.preference.unsaved"));
				}
			}
		}

		private bool CorePending
		{
			get
			{
				if (!corePinPending && !coreBackgroundPending && (core == null || !core.BackgroundAffinityPending) && !CoreThreadPlacement.UnownedPending && (coreAffinityJobs == null || !coreAffinityJobs.HasPending))
				{
					return CoreIsolationClient.State == "schedule.isolation.pending";
				}
				return true;
			}
		}

		internal PolicySnapshot AutomationPolicySnapshot
		{
			get
			{
				return sessionPolicy ?? PolicyResolver.Global();
			}
		}

		public string AutomationStatus
		{
			get
			{
				lock (sync)
				{
					string text = ((!enabled) ? PerformancePolicy.Text("守护已暂停", "Guard paused") : (active ? (PerformancePolicy.Text("正在守护 · ", "Guarding · ") + activeGame) : PerformancePolicy.Text("自动策略就绪 · 等待游戏", "Automatic policy ready · no active game")));
					return text + ((envFused.Count == 0) ? "" : PerformancePolicy.Text(" · 有受限动作，查看记录", " · actions blocked; see records"));
				}
			}
		}

		internal string AutomationSession
		{
			get
			{
				lock (sync)
				{
					return automationSession;
				}
			}
		}

		internal long AutomaticSessionSerial { get { return Interlocked.Read(ref gameSessionStarts); } }

		internal string GlobalTimerResolutionDetail
		{
			get
			{
				bool verified;
				uint actual;
				uint finest;
				if (!timerLease.Readback(out verified, out actual, out finest))
				{
					return "";
				}
				if (!verified)
				{
					return Lang.T("env.gtimer.query-unverified");
				}
				return Lang.F("env.gtimer.readback", 0.5.ToString("F4", CultureInfo.InvariantCulture), ((double)actual / 10000.0).ToString("F4", CultureInfo.InvariantCulture), ((double)finest / 10000.0).ToString("F4", CultureInfo.InvariantCulture));
			}
		}

		public string BackgroundGovernanceStatus
		{
			get
			{
				if (!SuppressBackground) return Lang.T("background.governance.status.off");
				return Lang.T(BackgroundGovernancePolicy.Load() == BackgroundGovernancePolicy.Aggressive
					? "background.governance.status.aggressive" : "background.governance.status.normal");
			}
		}

		public bool CacheWarmOn
		{
			get
			{
				return cacheWarmOn;
			}
			set
			{
				lock (sync)
				{
					if (stopping)
					{
						return;
					}
					InvalidateCacheWarm();
					cacheWarmOn = Settings.Save("GmCacheWarmV2", value) && value;
				}
				RequestPolicyApply("CacheWarmOn");
			}
		}

		public string CacheWarmStatus
		{
			get
			{
				return Lang.T((cacheWarm == null) ? "cachewarm.idle" : cacheWarm.Status);
			}
		}

		private string CriticalThreadStoreRoot
		{
			get
			{
				return Path.GetFullPath(Path.Combine(dataDir, "CriticalThreadHardAffinityV1"));
			}
		}

		private bool timerRaised
		{
			get
			{
				if (timerLease != null)
				{
					return timerLease.HasResidue;
				}
				return false;
			}
		}

		public bool Enabled
		{
			get
			{
				return enabled;
			}
			set
			{
				bool flag;
				lock (sync)
				{
					flag = enabled != value;
					enabled = value;
					if (flag)
					{
						Interlocked.Increment(ref autoAddEpoch);
						InvalidatePowerPlanSession();
						RevokeCriticalThreadIntent("Enabled");
						if (!value)
						{
							RememberModeSwitchLockLocked(activeDetection);
						}
						InvalidateCacheWarm();
						Interlocked.Increment(ref optionalServiceGeneration);
						InvalidateStandbyCleanerWork();
						InvalidateEnglishInputWork();
						ClearFamilyDiscovery();
						InvalidateRendererHandoff();
					}
				}
				if (flag)
				{
					if (value)
					{
						ClearEnvFuse("gamedvr");
						ClearEnvFuse("mmcss");
					}
					SyncGameDvr();
				}
				if (flag && value)
				{
					RequestFullGameDetection();
				}
				if (flag && value)
				{
					RequestPolicyApply("Enabled");
				}
				else
				{
					kick.Set();
				}
			}
		}

		private bool GuardSchedulingNeedsRestore
		{
			get
			{
				if (!GuardSchedulingRecoveryPending)
				{
					if (GameDvr.HasResidue || Mmcss.HasResidue())
					{
						if (enabled && !stopping)
						{
							if (profileStore != null)
							{
								return ProfileStoreSaveFailed;
							}
							return false;
						}
						return true;
					}
					return false;
				}
				return true;
			}
		}

		private bool GuardSchedulingRecoveryPending
		{
			get
			{
				lock (sync)
				{
					return envRestorePending.Contains("gamedvr") || envRestorePending.Contains("mmcss");
				}
			}
		}

		public bool EnglishInputEnabled
		{
			get
			{
				return englishInputOn;
			}
			set
			{
				lock (sync)
				{
					if (stopping)
					{
						return;
					}
					InvalidateEnglishInputWork();
					bool flag = Settings.Save("GmEnglishInputV1", value);
					englishInputOn = value && flag;
				}
				RequestPolicyApply("EnglishInputEnabled");
			}
		}

		private bool EffPauseServices
		{
			get
			{
				return false;
			}
		}

		private bool OptionalServicesRequested
		{
			get
			{
				if (EffPauseServices)
				{
					return !SessionServicesSuspended;
				}
				return false;
			}
		}

		private bool NvGameResidueNeedsRestore
		{
			get
			{
				return NvGameResidueGate(NvDrsTweaks.HasGameResidue);
			}
		}

		private bool GpuPrefStageResidueNeedsRestore
		{
			get
			{
				return GpuPrefResidueGate(GpuPrefStage.HasResidue);
			}
		}

		private GameFamilyHistory FamilyHistory
		{
			get
			{
				if (gameFamilyHistory == null)
				{
					Interlocked.CompareExchange(ref gameFamilyHistory, new GameFamilyHistory(), null);
				}
				return gameFamilyHistory;
			}
		}

		private GameFamilyEvidence FamilyEvidence
		{
			get
			{
				return Volatile.Read(ref gameFamilyEvidence) ?? GameFamilyEvidence.Empty;
			}
		}

		internal int FamilyPolicyEpoch
		{
			get
			{
				return Volatile.Read(ref familyPolicyEpoch);
			}
		}

		internal bool SmartSceneSwitchedOut
		{
			get
			{
				return smartSwitchedOut;
			}
		}

		internal bool SessionSwitchedOut
		{
			get
			{
				return sessionSwitchedOut;
			}
		}

		internal SessionForegroundState SessionForeground
		{
			get
			{
				return sessionForeground.State;
			}
		}

		private long SwitchOutDebounceTicks
		{
			get
			{
				int val = ProcessScanIntervalMs(ProcessEventsAvailable, true);
				return (long)Math.Max(5000, val) * 10000L;
			}
		}

		internal bool SessionServicesSuspended
		{
			get
			{
				return SessionServiceSuspended("services", DateTime.UtcNow.Ticks);
			}
		}

		internal string SwitchOutReleaseText
		{
			get
			{
				return PerformancePolicy.Text("智能切出，释放后台压制", "Smart switch-out: releasing background suppression");
			}
		}

		public bool FrameCoordinationEnabled
		{
			get
			{
				return AutomationPolicySnapshot.FrameCoordinationOn;
			}
		}

		internal bool GameNvDeliveryPending
		{
			get
			{
				return Interlocked.CompareExchange(ref gameNvPending, 0, 0) != 0;
			}
		}

		public string WhitelistLastError
		{
			get
			{
				lock (sync)
				{
					return whitelistLastError;
				}
			}
		}

		internal bool ShutdownWritersStopped
		{
			get
			{
				return shutdownWritersStopped;
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
				Interlocked.Exchange(ref processEventsAvailable, value ? 1 : 0);
				RequestFullGameDetection();
				try
				{
					kick.Set();
				}
				catch
				{
				}
			}
		}

		internal long ProcessScanCount
		{
			get
			{
				return Interlocked.Read(ref processScanCount);
			}
		}

		public bool ProfileStoreSaveFailed
		{
			get
			{
				if (!profileStore.SaveFailed)
				{
					return Interlocked.CompareExchange(ref profileSaveFailureSignaled, 0, 0) != 0;
				}
				return true;
			}
		}

		public string StatusText
		{
			get
			{
				if (!enabled)
				{
					return Lang.T("st.off");
				}
				lock (sync)
				{
					if (!active)
					{
						string text = armedGameName;
						return (text != null) ? Lang.F("st.armed", text) : Lang.T("st.mon");
					}
					long num = gameGoneSinceTicks;
					if (num != 0)
					{
						int num2 = ExitGraceSeconds - (int)((DateTime.UtcNow.Ticks - num) / 10000000);
						if (num2 < 0)
						{
							num2 = 0;
						}
						return Lang.F("st.grace", activeGame, num2);
					}
					int num3 = core.ThrottledCountCached();
					int count = boostStateVerified.Count;
					string text2 = Lang.F("st.active", activeGame, num3);
					return text2 + Lang.T("st.sep") + Lang.F("st.boost", count, PowerPlanStatusText);
				}
			}
		}

		public bool BoostStateVerified
		{
			get
			{
				lock (sync)
				{
					return active && activeDetection != null && boostStateVerified.Contains(activeDetection.RendererPid);
				}
			}
		}

		public bool BoostHandleProtected
		{
			get
			{
				lock (sync)
				{
					return active && activeDetection != null && (boostHandleStripped.Contains(activeDetection.RendererPid) || boostStateWarned.Contains(activeDetection.RendererPid));
				}
			}
		}

		public string BoostStatusText
		{
			get
			{
				if (!enabled || !EffBoost)
				{
					return Lang.T("v14.boost.disabled");
				}
				lock (sync)
				{
					if (!active)
					{
						return Lang.T("v14.boost.wait");
					}
					if (activeDetection == null || activeDetection.RendererPid <= 0)
					{
						return Lang.T("v14.boost.no.renderer");
					}
					string text = activeDetection.RendererName ?? activeGame ?? "Game";
					if (ProtectedGameRoster.Contains(activeDetection.RendererName))
					{
						return Lang.F("v14.boost.protected", text);
					}
					if (boostStateVerified.Contains(activeDetection.RendererPid))
					{
						return Lang.F("v14.boost.verified", text);
					}
					if (boostHandleStripped.Contains(activeDetection.RendererPid) || boostStateWarned.Contains(activeDetection.RendererPid))
					{
						return Lang.F("v14.boost.protected", text);
					}
					return Lang.F("v14.boost.applying", text);
				}
			}
		}

		private RendererHandoffTracker Handoff
		{
			get
			{
				if (rendererHandoff == null)
				{
					Interlocked.CompareExchange(ref rendererHandoff, new RendererHandoffTracker(), null);
				}
				return rendererHandoff;
			}
		}

		internal long GameSessionToken
		{
			get
			{
				lock (sync)
				{
					return (active || activeDetection != null || gameGoneSinceTicks != 0) ? (-1) : gameSessionStarts;
				}
			}
		}

		public string ArmedGame
		{
			get
			{
				lock (sync)
				{
					return active ? null : armedGameName;
				}
			}
		}

		public static string LastSessionBrief
		{
			get
			{
				return Settings.LoadStr("LastSessionBrief", "");
			}
		}

		public int ExitGraceSeconds
		{
			get
			{
				return exitGraceSeconds;
			}
		}

		public bool SuppressBackground
		{
			get
			{
				return bgSuppressOn;
			}
			set
			{
				lock (familyPolicyGate)
				{
					if (!Settings.Save("GmSuppress", value)) return;
					bgSuppressOn = value;
					InvalidateFamilyPolicy();
				}
				RequestPolicyApply("SuppressBackground");
			}
		}

		public bool BoostGame
		{
			get
			{
				return boostOn;
			}
			set
			{
				boostOn = value;
				Settings.Save("GmBoost", value);
				RequestPolicyApply("BoostGame");
			}
		}

		public bool RenderLaneOn
		{
			get
			{
				return renderLaneOn;
			}
			set
			{
				if (ThreadBoostSelection.Save(value ? "on" : "off"))
				{
					renderLaneOn = value;
					if (!value)
					{
						RenderLane.Release();
					}
					RequestPolicyApply("RenderLaneOn");
				}
			}
		}

		public bool PauseWindowsUpdate
		{
			get
			{
				return pauseUpdateOn;
			}
			set
			{
				pauseUpdateOn = value;
				Settings.Save("GmPauseUpdate", value);
				if (value)
				{
					ClearEnvFuse("wu");
				}
				RequestPolicyApply("PauseWindowsUpdate");
			}
		}

		public bool PauseMaintenance
		{
			get
			{
				return pauseMaintOn;
			}
			set
			{
				pauseMaintOn = value;
				Settings.Save("GmPauseMaintenanceV1", value);
				if (value)
				{
					ClearEnvFuse("maint");
				}
				RequestPolicyApply("PauseMaintenance");
			}
		}

		public bool PauseServices
		{
			get
			{
				return false;
			}
			set
			{
				pauseServicesOn = false;
				Settings.Remove("GmPauseServices");
				PauseServicesPolicyChanged(false);
			}
		}

		public bool GpuPowerLift
		{
			get
			{
				return false;
			}
			set
			{
				Settings.Save("GmGpuPowerMax", false);
				RequestPolicyApply("GpuPowerLift");
			}
		}

		public bool DwmBoostOn
		{
			get
			{
				return dwmBoostOn;
			}
			set
			{
				dwmBoostOn = value;
				Settings.Save("GmDwmBoost", value);
				if (value)
				{
					ClearEnvFuse("dwmboost");
				}
				RequestPolicyApply("DwmBoostOn");
			}
		}

		public bool RsrUpscale
		{
			get
			{
				return rsrOn;
			}
			set
			{
				rsrOn = value;
				Settings.Save("GmRsr", value);
				if (value)
				{
					ClearEnvFuse("rsr");
				}
				RequestPolicyApply("RsrUpscale");
			}
		}

		public bool AmdAntiLag
		{
			get
			{
				return false;
			}
			set
			{
				Settings.Save("AmdAntiLag", false);
				RequestPolicyApply("AmdAntiLag");
			}
		}

		public bool AmdAfmf
		{
			get
			{
				return amdAfmf;
			}
			set
			{
				amdAfmf = value;
				Settings.Save("AmdAfmf", value);
				if (!value)
				{
					AdlxTweaks.RestoreAfmf();
				}
				else
				{
					ClearEnvFuse("amdafmf");
				}
				RequestPolicyApply("AmdAfmf");
			}
		}

		public bool NvVrrWindowedEnabled
		{
			get
			{
				return nvVrrWindowedOn;
			}
			set
			{
				lock (sync)
				{
					if (stopping || !Settings.Save("NvVrrWindowed", value))
					{
						return;
					}
					nvVrrWindowedOn = value;
					if (value)
					{
						ClearEnvFuse("nvvrr");
					}
					automaticNvPlanKey = -1;
					ForgetNvTweaksLocked();
				}
				RequestPolicyApply("NvVrrWindowedEnabled");
			}
		}

		public bool IntelEnduranceOff
		{
			get
			{
				return intelEnduranceOn;
			}
			set
			{
				intelEnduranceOn = value;
				Settings.Save("GmIntelEnduranceV1", value);
				if (value)
				{
					ClearEnvFuse("intelend");
				}
				RequestPolicyApply("IntelEnduranceOff");
			}
		}

		public bool NvMaxPerf
		{
			get
			{
				return nvMaxPerf;
			}
			set
			{
				nvMaxPerf = value;
				Settings.Save("NvMaxPerf", value);
				if (!value)
				{
					NvDrsTweaks.RestoreKind("pstate");
					NvDrsTweaks.RestoreKind("forcep2");
				}
				else
				{
					SaveCounter("NvFailStreak_pstate", 0);
				}
				lock (sync)
				{
					ForgetNvTweaksLocked();
				}
				RequestPolicyApply("NvMaxPerf");
			}
		}

		public string NvLowLatMode
		{
			get
			{
				return nvLowLatMode;
			}
			set
			{
				SetGameLowLatency(value == "on" || value == "ultra");
			}
		}

		public bool NvSmoothMotion
		{
			get
			{
				return nvSmoothMotion;
			}
			set
			{
				nvSmoothMotion = value;
				Settings.Save("NvSmoothMotion", value);
				if (!value)
				{
					NvDrsTweaks.RestoreKind("smooth");
				}
				else
				{
					SaveCounter("NvFailStreak_smooth", 0);
				}
				lock (sync)
				{
					ForgetNvTweaksLocked();
				}
				RequestPolicyApply("NvSmoothMotion");
			}
		}

		public bool NvShaderCacheMax
		{
			get
			{
				return nvShaderCacheMax;
			}
			set
			{
				nvShaderCacheMax = value;
				Settings.Save("NvShaderCache", value);
				if (!value)
				{
					NvDrsTweaks.RestoreKind("shadercache");
					NvDrsTweaks.RestoreKind("shaderon");
				}
				else
				{
					SaveCounter("NvFailStreak_shadercache", 0);
				}
				lock (sync)
				{
					ForgetNvTweaksLocked();
				}
				RequestPolicyApply("NvShaderCacheMax");
			}
		}

		public bool NvRebar
		{
			get
			{
				return nvRebarOn;
			}
			set
			{
				nvRebarOn = value;
				Settings.Save("NvRebar", value);
				if (!value)
				{
					NvDrsTweaks.RestoreKinds(NvDrsTweaks.RebarKeys);
				}
				else
				{
					SaveCounter("NvFailStreak_rebarfeat", 0);
				}
				lock (sync)
				{
					ForgetNvTweaksLocked();
				}
				RequestPolicyApply("NvRebar");
			}
		}

		public string NvDlssMode
		{
			get
			{
				return nvDlssMode;
			}
			set
			{
				object obj;
				switch (value)
				{
				default:
					obj = "off";
					break;
				case "latest":
				case "j":
				case "k":
					obj = value;
					break;
				}
				string text = (nvDlssMode = (string)obj);
				Settings.SaveStr("NvDlss", text);
				if (text == "off")
				{
					NvDrsTweaks.RestoreKinds(NvDrsTweaks.DlssKeys);
				}
				else
				{
					SaveCounter("NvFailStreak_dlssovr", 0);
				}
				lock (sync)
				{
					ForgetNvTweaksLocked();
				}
				RequestPolicyApply("NvDlssMode");
			}
		}

		public bool GpuPrefStageOn
		{
			get
			{
				return gpuPrefStageOn;
			}
			set
			{
				gpuPrefStageOn = value;
				Settings.Save("GpuPrefStageOn", value);
				if (!value)
				{
					GpuPrefStage.Restore();
				}
			}
		}

		public bool KillGameDvr
		{
			get
			{
				return killGameDvr;
			}
			set
			{
				SetGameDvrSuppression(value);
			}
		}

		public bool PowerPlanSwitch
		{
			get
			{
				if (!PowerPlan.NoSwitchSelected)
				{
					return planSwitch;
				}
				return false;
			}
			set
			{
				if (!stopping && PowerPlan.SelectPlan((!value) ? "off" : (PowerPlan.NoSwitchSelected ? "managed" : PowerPlan.EffectivePlanId)))
				{
					lock (sync)
					{
						planSwitch = value;
					}
					if (value)
					{
						SaveCounter("PowerPlanFailStreak", 0);
						ClearEnvFuse("overlay");
					}
					RequestPolicyApply("PowerPlanSwitch");
				}
			}
		}

		public bool StandbyCleanerEnabled
		{
			get
			{
				return standbyCleanerOn;
			}
			set
			{
				TrySetStandbyCleanerEnabled(value);
			}
		}

		public StandbyCleanerOptions StandbyCleaningOptions
		{
			get
			{
				return standbyCleanerOptions;
			}
		}

		public bool StandbyCleaningOptionsValid
		{
			get
			{
				return standbyCleanerOptionsValid;
			}
		}

		public bool IsActive
		{
			get
			{
				lock (sync)
				{
					return active;
				}
			}
		}

		private volatile ProcessSnapshot antiCheatProcessSnapshot;

		internal AntiCheatAffinityGameContext GetAntiCheatAffinityContext()
		{
			lock (sync)
			{
				GameDetection detection = activeDetection;
				if (!active || !enabled || stopping || panicReq || ProfileStoreSaveFailed || gameGoneSinceTicks != 0
					|| detection == null || !detection.RendererCandidateSelected || detection.RendererSafetyOnly
					|| detection.RequiresGpuConfirm || detection.RendererPid <= 0 || detection.RendererCreation <= 0)
				{
					AntiCheatContextStable(null, 0);
					return null;
				}
				if (!AntiCheatContextStable(detection, AutoCores.NowTicks)) return null;
				ulong reserved = 0, domain = 0;
				if (!CpuTopology.MultiGroup)
				{
					if (corePlan != null) reserved |= corePlan.Allocated;
					HardPin pin;
					if (coreEngaged && hardPins.TryGetValue(detection.RendererPid, out pin) && pin.Creation == detection.RendererCreation)
					{
						if (pin.AppliedAffinity != allMask) domain = pin.AppliedAffinity;
						if (coreManual != null && coreManual.Isolation && CoreIsolationClient.State == "schedule.isolation.active") reserved |= pin.AppliedAffinity;
					}
					CriticalThreadUiState threads = criticalThreadUi;
					if (threads.TargetPid == detection.RendererPid && threads.TargetBirth == detection.RendererCreation && (threads.Applied || threads.Pending))
						foreach (ulong mask in threads.CandidateMasks) reserved |= mask;
				}
				HashSet<int> ancestors = VerifiedAntiCheatAncestors(antiCheatProcessSnapshot, detection);
				if (ancestors == null) return null;
				return new AntiCheatAffinityGameContext(detection.RendererPid, detection.RendererCreation, gameSessionStarts,
					CpuTopology.MultiGroup ? -1 : 0, reserved, domain, ancestors);
			}
		}

		public string ActiveGame
		{
			get
			{
				lock (sync)
				{
					return active ? activeGame : null;
				}
			}
		}

		public bool ModeSwitchLocked
		{
			get
			{
				lock (sync)
				{
					return ModeSwitchLockedLocked();
				}
			}
		}

		public PerformancePreset Preset
		{
			get
			{
				lock (sync)
				{
					return preset;
				}
			}
			set
			{
				TrySetPreset(value);
			}
		}

		public PerformancePreset ActivePreset
		{
			get
			{
				PolicySnapshot policySnapshot = sessionPolicy;
				if (policySnapshot != null)
				{
					return policySnapshot.Preset;
				}
				lock (sync)
				{
					return preset;
				}
			}
		}

		public string SessionPolicySourceName
		{
			get
			{
				PolicySnapshot policySnapshot = sessionPolicy;
				if (policySnapshot == null || policySnapshot.IsGlobal || !IsActive)
				{
					return null;
				}
				return policySnapshot.ProfileName;
			}
		}

		public string ControlSourceText
		{
			get
			{
				return PerformancePolicy.Text("手动", "Manual");
			}
		}

		public string SessionPolicyProfileId
		{
			get
			{
				PolicySnapshot policySnapshot = sessionPolicy;
				if (policySnapshot == null || !IsActive)
				{
					return null;
				}
				return policySnapshot.ProfileId;
			}
		}

		private bool EffSuppress
		{
			get
			{
				PolicySnapshot policySnapshot = sessionPolicy;
				if (policySnapshot == null)
				{
					return bgSuppressOn;
				}
				return policySnapshot.EffSuppress;
			}
		}

		private PerformancePreset EffPreset
		{
			get
			{
				PolicySnapshot policySnapshot = sessionPolicy;
				if (policySnapshot == null)
				{
					return Preset;
				}
				return policySnapshot.Preset;
			}
		}

		private bool EffBoost
		{
			get
			{
				PolicySnapshot policySnapshot = sessionPolicy;
				if (policySnapshot == null)
				{
					return boostOn;
				}
				return policySnapshot.EffBoost;
			}
		}

		private bool EffLane
		{
			get
			{
				PolicySnapshot policySnapshot = sessionPolicy;
				if (policySnapshot == null)
				{
					return renderLaneOn;
				}
				return policySnapshot.EffLane;
			}
		}

		private bool LaneEligible
		{
			get
			{
				return LaneSupported(EffPreset);
			}
		}

		private bool EffIntelEnduranceOff
		{
			get
			{
				PolicySnapshot policySnapshot = sessionPolicy;
				if (policySnapshot == null)
				{
					return intelEnduranceOn;
				}
				return policySnapshot.IntelEnduranceOff;
			}
		}

		public event Action LibraryChanged;

		public event Action ProfileStoreSaveFailure;

		public event Action<string> SessionEnded;

		public event Action<string> SessionBriefed;

		private void StepAdaptiveGuard()
		{
			if (!AdaptiveEscalationWanted)
			{
				bool flag = adaptiveEscalated;
				ResetAdaptiveGuard();
				if (flag)
				{
					Logger.Log(Lang.T("log.adaptive.3"));
				}
				return;
			}
			long now = AdaptiveNow();
			int num = adaptiveGuard.Step(adaptivePressure.Update(AdaptiveSample(), now), now);
			if (num > 0)
			{
				adaptiveEscalated = true;
				Logger.Log(Lang.T("log.adaptive.1") + adaptiveGuard.Episodes + "/" + 3);
			}
			else if (num < 0)
			{
				adaptiveEscalated = false;
				Logger.Log(Lang.T("log.adaptive.2"));
			}
		}

		private void ResetAdaptiveGuard()
		{
			adaptiveGuard.Reset();
			adaptivePressure.Reset();
			adaptiveEscalated = false;
		}

		private long AdaptiveNow()
		{
			return DateTime.UtcNow.Ticks;
		}

		private double AdaptiveSample()
		{
			return adaptivePressure.Sample();
		}

		private bool WideBackgroundScope(PolicySnapshot policy)
		{
			if (policy != null)
			{
				if (!policy.WideBackgroundScope)
				{
					if (adaptiveEscalated)
					{
						return policy.AdaptiveEscalation;
					}
					return false;
				}
				return true;
			}
			return false;
		}

		private void TryAutoAddForegroundGame()
		{
			if (!autoAddOn || stopping || !enabled || ProfileStoreSaveFailed || autoIgnoreLoadFailed || libraryIgnoreTransaction.RecoveryPending)
			{
				return;
			}
			bool flag;
			int epoch;
			lock (sync)
			{
				flag = active;
				epoch = Volatile.Read(ref autoAddEpoch);
			}
			ForegroundGameWindowSnapshot window;
			if (flag || !AutoAddWindow(out window) || window.Pid <= 0 || window.Pid == selfPid)
			{
				return;
			}
			int pid = window.Pid;
			long now = DateTime.UtcNow.Ticks;
			if (now < autoAddGateTicks)
			{
				return;
			}
			Interlocked.Exchange(ref autoAddGateTicks, now + TimeSpan.FromSeconds(5.0).Ticks);
			GameProcessSnapshot identity;
			if (!CaptureAutoAddIdentity(pid, out identity) || !GameSessionDetector.IsLibraryCandidate(identity.Name, identity.Path, windowsPrefix) || identity.Pid != pid || identity.Creation <= 0)
			{
				return;
			}
			identity = new GameProcessSnapshot
			{
				Pid = pid,
				Creation = identity.Creation,
				Name = identity.Name,
				Path = identity.Path
			};
			string path = identity.Path;
			if (!autoAddFailureBudget.Allowed(pid, identity.Creation, path, "capture")
				|| !autoAddFailureBudget.Allowed(pid, identity.Creation, path, "save")) return;
			lock (sync)
			{
				if (autoAddIgnore.Contains(path))
				{
					return;
				}
				AutoAddAttempt value;
				if (autoAddAttempts.TryGetValue(path, out value) && (value.Pid != pid || value.Creation != identity.Creation))
				{
					autoAddAttempts.Remove(path);
					autoAddRejectUntil.Remove(path);
				}
				long value2;
				if (autoAddRejectUntil.TryGetValue(path, out value2) && now < value2)
				{
					return;
				}
				foreach (GameProfile profile in profiles)
				{
					if (SameLibraryPath(profile.ExecutablePath, path) || SameLibraryPath(profile.LearnedExecutablePath, path) || profile.ContainsPath(path))
					{
						return;
					}
				}
				value = AutoAddAttemptLocked(path, now);
				value.Pid = pid;
				value.Creation = identity.Creation;
				value.LastSeen = now;
			}
			if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0)
			{
				return;
			}
			bool flag2 = false;
			try
			{
				flag2 = ThreadPool.QueueUserWorkItem(delegate
				{
					try
					{
						FinishAutoAdd(identity, window.FullscreenLike, epoch, now);
					}
					catch (Exception ex)
					{
						if (!AutoAddCanceled(epoch))
						{
							RememberAutoAddReject(path, now);
							RecordAutoAddCaptureFailure(pid, identity.Creation, path, "capture", ex.GetType().Name);
							NoteAutoAdd(path, "CaptureUnavailable", ex.GetType().Name, now);
						}
					}
					finally
					{
						Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
					}
				});
			}
			catch
			{
			}
			finally
			{
				if (!flag2)
				{
					RecordAutoAddCaptureFailure(pid, identity.Creation, path, "queue", "QueueUnavailable");
					RememberAutoAddReject(path, DateTime.UtcNow.Ticks);
					Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
				}
				else
				{
					Interlocked.Exchange(ref autoAddGateTicks, now + 300000000);
				}
			}
		}

		private bool AutoAddCanceled(int epoch)
		{
			if (!stopping && !panicReq && enabled && autoAddOn)
			{
				return Volatile.Read(ref autoAddEpoch) != epoch;
			}
			return true;
		}

		private void RecordAutoAddCaptureFailure(int pid, long creation, string path, string phase, string reason)
		{
			int count = autoAddFailureBudget.Failed(pid, creation, path, "capture");
			Logger.Warn("Auto-add " + phase + " failure=" + count + "/3 target=" + pid + "/" + creation
				+ " reason=" + reason + (count >= 3 ? "; exhausted for this process identity" : "; backing off"));
		}

		private void FinishAutoAdd(GameProcessSnapshot identity, bool fullscreen, int epoch, long now)
		{
			if (AutoAddCanceled(epoch))
			{
				return;
			}
			int pid = identity.Pid;
			string path = identity.Path;
			GpuSampleDiagnostics diagnostics = null;
			Dictionary<int, double> dictionary = GpuEvidence.Sample3D(2, 700, () => AutoAddCanceled(epoch), pid, out diagnostics);
			if (AutoAddCanceled(epoch))
			{
				return;
			}
			if (dictionary == null)
			{
				RecordAutoAddCaptureFailure(pid, identity.Creation, path, "capture", diagnostics == null ? "GpuUnavailable" : diagnostics.ToString());
				RememberAutoAddReject(path, now);
				NoteAutoAdd(path, "GpuUnavailable", (diagnostics == null) ? null : diagnostics.ToString(), now);
				return;
			}
			autoAddFailureBudget.Succeeded(pid, identity.Creation, path, "capture");
			double value;
			if (!dictionary.TryGetValue(pid, out value))
			{
				value = 0.0;
			}
			if (double.IsNaN(value) || double.IsInfinity(value) || value < 10.0)
			{
				lock (sync)
				{
					AutoAddAttempt value2;
					if (autoAddAttempts.TryGetValue(path, out value2))
					{
						value2.Confirmations = 0;
					}
				}
				RememberAutoAddReject(path, now);
				NoteAutoAdd(path, "ActivityWaiting", null, now);
				return;
			}
			bool flag;
			lock (sync)
			{
				flag = active;
			}
			ForegroundGameWindowSnapshot window;
			GameProcessSnapshot identity2;
			if (flag || AutoAddCanceled(epoch) || !AutoAddWindow(out window) || window.Pid != pid || !CaptureAutoAddIdentity(pid, out identity2) || identity2 == null || identity2.Pid != pid || identity2.Creation != identity.Creation || !SameLibraryPath(identity2.Path, path))
			{
				return;
			}
			lock (sync)
			{
				if (AutoAddCanceled(epoch) || !AutoDetectedCommitAllowed(path))
				{
					return;
				}
				AutoAddAttempt autoAddAttempt = AutoAddAttemptLocked(path, now);
				if (autoAddAttempt.Pid != pid || autoAddAttempt.Creation != identity.Creation)
				{
					autoAddAttempt.Pid = pid;
					autoAddAttempt.Creation = identity.Creation;
					autoAddAttempt.Confirmations = (autoAddAttempt.Misses = 0);
				}
				autoAddAttempt.LastSeen = now;
				autoAddAttempt.Misses = 0;
				if ((!fullscreen || !window.FullscreenLike || !(value >= 20.0)) && (autoAddAttempt.Confirmations == 0 || now < autoAddAttempt.EvidenceAt || now - autoAddAttempt.EvidenceAt > 1200000000))
				{
					autoAddAttempt.Confirmations = 1;
					autoAddAttempt.EvidenceAt = now;
					autoAddRejectUntil[path] = now + 300000000;
					NoteAutoAdd(path, "ConfirmingActivity", null, now);
					return;
				}
			}
			string error;
			bool persistenceFailed;
			if (!autoAddFailureBudget.Allowed(pid, identity.Creation, path, "save")) return;
			if (!AddAutoDetectedExecutableCore(path, out error, out persistenceFailed, () => !AutoAddCanceled(epoch)))
			{
				if (persistenceFailed)
				{
					int failed = autoAddFailureBudget.Failed(pid, identity.Creation, path, "save");
					Logger.Warn("Auto-add durable save failure=" + failed + "/3 target=" + pid + "/" + identity.Creation
						+ " reason=" + error + (failed >= 3 ? "; automatic save stopped for this process identity" : ""));
				}
				RememberAutoAddReject(path, now);
				return;
			}
			autoAddFailureBudget.Succeeded(pid, identity.Creation, path, "save");
			lock (sync)
			{
				autoAddRejectUntil.Remove(path);
				autoAddAttempts.Remove(path);
			}
			Logger.Log(Lang.T("log.autoadd.3") + (int)value + Lang.T("log.autoadd.4") + identity.Name + Lang.T("log.autoadd.5") + path + Lang.T("log.autoadd.6"));
		}

		private AutoAddAttempt AutoAddAttemptLocked(string path, long now)
		{
			AutoAddAttempt value;
			if (autoAddAttempts.TryGetValue(path, out value))
			{
				return value;
			}
			if (autoAddAttempts.Count >= 64)
			{
				string text = null;
				long num = long.MaxValue;
				foreach (KeyValuePair<string, AutoAddAttempt> autoAddAttempt2 in autoAddAttempts)
				{
					if (autoAddAttempt2.Value.LastSeen < num)
					{
						text = autoAddAttempt2.Key;
						num = autoAddAttempt2.Value.LastSeen;
					}
				}
				if (text != null)
				{
					autoAddAttempts.Remove(text);
					autoAddRejectUntil.Remove(text);
				}
			}
			AutoAddAttempt autoAddAttempt = new AutoAddAttempt();
			autoAddAttempt.LastSeen = now;
			value = autoAddAttempt;
			autoAddAttempts[path] = value;
			return value;
		}

		private void RememberAutoAddReject(string path, long now)
		{
			lock (sync)
			{
				if (autoAddRejectUntil.Count >= 64)
				{
					List<string> list = new List<string>();
					foreach (KeyValuePair<string, long> item in autoAddRejectUntil)
					{
						if (item.Value <= now)
						{
							list.Add(item.Key);
						}
					}
					foreach (string item2 in list)
					{
						autoAddRejectUntil.Remove(item2);
					}
					if (autoAddRejectUntil.Count >= 64)
					{
						autoAddRejectUntil.Clear();
					}
				}
				AutoAddAttempt autoAddAttempt = AutoAddAttemptLocked(path, now);
				autoAddAttempt.LastSeen = now;
				autoAddAttempt.Confirmations = 0;
				if (autoAddAttempt.Misses < 4)
				{
					autoAddAttempt.Misses++;
				}
				int num = ((autoAddAttempt.Misses == 1) ? 30 : ((autoAddAttempt.Misses == 2) ? 60 : ((autoAddAttempt.Misses == 3) ? 120 : 300)));
				autoAddRejectUntil[path] = now + (long)num * 10000000L;
			}
		}

		private void NoteAutoAdd(string path, string reason, string detail, long now)
		{
			if (reason == "ActivityWaiting" || reason == "ConfirmingActivity") return;
			lock (sync)
			{
				AutoAddAttempt autoAddAttempt = AutoAddAttemptLocked(path, now);
				if (autoAddAttempt.Note == reason && now < autoAddAttempt.NoteAt)
				{
					return;
				}
				autoAddAttempt.Note = reason;
				autoAddAttempt.NoteAt = now + TimeSpan.FromMinutes(5.0).Ticks;
			}
			Logger.Info("AUTOADD exe=" + Path.GetFileName(path) + " result=" + reason + (string.IsNullOrEmpty(detail) ? "" : (" detail=" + detail)));
		}

		private bool AutoAddWindow(out ForegroundGameWindowSnapshot window)
		{
			return GameSessionDetector.TryForegroundGameWindow(out window);
		}

		private bool CaptureAutoAddIdentity(int pid, out GameProcessSnapshot identity)
		{
			identity = null;
			try
			{
				if (GameSessionDetector.TryCaptureProcessIdentity(pid, selfSession, out identity))
				{
					return true;
				}
				ProcessSnapshot processSnapshot = ProcessSnapshotSource.Capture(selfSession, 0);
				return processSnapshot != null && GameSessionDetector.TryCaptureProcessIdentity(processSnapshot.Find(pid), selfSession, out identity);
			}
			catch
			{
				identity = null;
				return false;
			}
		}

		private static bool SameLibraryPath(string a, string b)
		{
			if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b))
			{
				return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private bool LoadAutoIgnore()
		{
			try
			{
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				string[] array = (File.Exists(autoIgnorePath) ? File.ReadAllLines(autoIgnorePath) : new string[0]);
				foreach (string text in array)
				{
					string text2 = text.Trim();
					if (text2.Length != 0 && !text2.StartsWith("#"))
					{
						hashSet.Add(text2);
					}
				}
				autoAddIgnore.Clear();
				autoAddIgnore.UnionWith(hashSet);
				autoIgnoreLoadFailed = false;
				return true;
			}
			catch
			{
				autoIgnoreLoadFailed = true;
				return false;
			}
		}

		private bool EnsureLibraryReadyLocked()
		{
			if (libraryIgnoreTransaction == null)
			{
				return true;
			}
			if (!libraryIgnoreTransaction.RecoveryPending && !autoIgnoreLoadFailed)
			{
				return true;
			}
			if (libraryIgnoreTransaction.TryRecover())
			{
				return LoadAutoIgnore();
			}
			return false;
		}

		private bool CommitLibraryLocked(List<GameProfile> next, HashSet<string> nextIgnore, Func<bool> canCommit = null)
		{
			bool persistenceFailed;
			return CommitLibraryLocked(next, nextIgnore, out persistenceFailed, canCommit);
		}

		private bool CommitLibraryLocked(List<GameProfile> next, HashSet<string> nextIgnore, out bool persistenceFailed, Func<bool> canCommit = null)
		{
			persistenceFailed = false;
			if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked() || (canCommit != null && !canCommit()))
			{
				return false;
			}
			bool flag;
			if (autoAddIgnore.SetEquals(nextIgnore))
			{
				flag = SaveProfileSnapshotLocked(next, out persistenceFailed, canCommit);
			}
			else
			{
				List<string> list = new List<string>();
				list.Add(Lang.T("t.autoadd.1"));
				List<string> list2 = list;
				List<string> list3 = new List<string>(nextIgnore);
				list3.Sort(StringComparer.OrdinalIgnoreCase);
				list2.AddRange(list3);
				byte[] bytes = new UTF8Encoding(false).GetBytes(string.Join(Environment.NewLine, list2.ToArray()) + Environment.NewLine);
				flag = libraryIgnoreTransaction.Commit(next, bytes, () => !stopping && (canCommit == null || canCommit()));
				if (!flag && profileStore.SaveFailed)
				{
					SignalProfileStoreSaveFailure();
				}
			}
			if (!flag)
			{
				return false;
			}
			profiles.Clear();
			profiles.AddRange(next);
			autoAddIgnore.Clear();
			autoAddIgnore.UnionWith(nextIgnore);
			return true;
		}

		private AutoCoreGovernor NewCoreGovernor()
		{
			if (corePlan == null || !corePlan.Available)
			{
				return null;
			}
			return new AutoCoreGovernor(corePlan.Cores, corePlan.Logical);
		}

		private void ResetCoreWatch()
		{
			ResetCoreIntelligence();
			coreWatchPid = 0;
			coreWatchCreation = 0L;
			coreEngaged = false;
			coreAffinityObserved = 0uL;
			coreAffinityRead = (coreAffinityVerified = false);
			exclusiveCheckTicks = 0L;
			exclusiveAuditRetry.Reset();
			exclusiveAuditDeferred = false;
			coreGameMask = ((coreManual != null) ? coreManual.GameMask : ((corePlan != null) ? corePlan.Exclusive : 0));
			corePhysical = CpuTopology.PhysicalCoreMasks();
			coreBackgroundMoved = 0;
			corePlacementEvidence = null;
		}

		private void BeginAutoCores(PolicySnapshot snap, bool isolationClean)
		{
			bool flag = RestoreAllHardPins();
			corePinPending = !flag;
			ResetCoreWatch();
			coreManual = ((coreSessionPolicy != null) ? coreSessionPolicy.ManualCores : ((snap != null) ? snap.ManualCores : null));
			corePlan = ((coreManual != null) ? coreManual.RuntimePlan() : new AutoCorePlan
			{
				Blocked = "schedule.manual.required"
			});
			corePreset = ((snap != null) ? snap.Preset : PerformancePreset.Standard);
			coreAllowed = snap != null && snap.AutoCoresAllowed;
			corePolicyCheckTicks = AutoCores.NowTicks;
			coreObservationOnly = coreManual == null || snap == null || !snap.AutoCoresAllowed || !PresetValue.SupportsCoreScheduling(snap.Preset);
			coreGovernor = NewCoreGovernor();
			coreGameMask = ((coreManual != null) ? coreManual.GameMask : corePlan.Exclusive);
			if (coreGovernor != null && (!isolationClean || !flag))
			{
				coreGovernor.Block("schedule.auto.pending");
			}
			if (corePlan.Available)
			{
				Logger.Log(Lang.F("schedule.manual.plan", CpuTopology.DescribeMask(coreManual.GameMask)));
			}
			else
			{
				Logger.Log(Lang.F("log.autocore.unavailable", Lang.T(corePlan.Blocked)));
			}
			if (corePlan.Allocated != 0)
			{
				Logger.Log(Lang.F("log.autocore.allocated", CpuTopology.DescribeMask(corePlan.Allocated)));
			}
			PublishAutoCores(null);
		}

		private void EndAutoCores()
		{
			ResetCoreIntelligence();
			coreGovernor = null;
			corePlan = null;
			coreManual = null;
			corePlacementStopReason = null;
			corePlacementOnly = false;
			ResetCoreWatch();
			lock (sync)
			{
				corePinPending = hardPins.Count > 0;
			}
			AutoCoreStatus autoCoreStatus = new AutoCoreStatus();
			autoCoreStatus.Pending = CorePending;
			AutoCores.Publish(autoCoreStatus);
		}

		private void RefreshAutoCoreMode()
		{
			if (stopping || (Interlocked.Exchange(ref corePolicyDirty, 0) == 0 && AutoCores.NowTicks - corePolicyCheckTicks < 50000000))
			{
				return;
			}
			corePolicyCheckTicks = AutoCores.NowTicks;
			GameProfile gameProfile = null;
			lock (sync)
			{
				if (activeDetection != null && activeDetection.Profile != null)
				{
					foreach (GameProfile profile in profiles)
					{
						if (profile.Id == activeDetection.Profile.Id)
						{
							gameProfile = profile.Clone();
							break;
						}
					}
				}
			}
			PolicySnapshot policySnapshot = coreSessionPolicy ?? ((gameProfile == null) ? PolicyResolver.Global() : PolicyResolver.For(gameProfile));
			if (policySnapshot.AutoCoresAllowed && coreAllowed && PresetValue.SupportsCoreScheduling(policySnapshot.Preset) && PresetValue.SupportsCoreScheduling(corePreset))
			{
				if (corePreset != policySnapshot.Preset)
				{
					StopCoreRefinement();
				}
				corePreset = policySnapshot.Preset;
			}
			else if (policySnapshot.Preset != corePreset || policySnapshot.AutoCoresAllowed != coreAllowed)
			{
				BeginAutoCores(policySnapshot, StopCoreIsolation());
			}
		}

		private void StepAutoCores(BoostPass pass)
		{
			long nowTicks = AutoCores.NowTicks;
			try
			{
				StepAutoCoresImpl(pass);
			}
			finally
			{
				coreBudget.Record(nowTicks);
			}
		}

		private void StepAutoCoresImpl(BoostPass pass)
		{
			RefreshAutoCoreMode();
			AutoCoreGovernor autoCoreGovernor = coreGovernor;
			if (coreManual == null)
			{
				PublishAutoCores(pass.RendererName);
				return;
			}
			if (corePlacementStopReason != null)
			{
				if (CorePending)
				{
					ReleaseExclusive();
				}
				if (autoCoreGovernor != null)
				{
					autoCoreGovernor.Block(corePlacementStopReason);
				}
				PublishAutoCores(pass.RendererName);
				return;
			}
			if (autoCoreGovernor == null || pass.RendererPid <= 0 || pass.RendererCreation <= 0)
			{
				if (autoCoreGovernor != null)
				{
					ApplyAutoCoreStep(autoCoreGovernor.Pause());
				}
				PublishAutoCores(pass.RendererName);
				return;
			}
			if (pass.RendererPid != coreWatchPid || pass.RendererCreation != coreWatchCreation)
			{
				if (coreWatchPid > 0)
				{
					bool flag = ReleaseExclusive();
					autoCoreGovernor = (coreGovernor = NewCoreGovernor());
					if (!flag)
					{
						autoCoreGovernor.Block("schedule.auto.pending");
					}
				}
				ResetCoreWatch();
				coreWatchPid = pass.RendererPid;
				coreWatchCreation = pass.RendererCreation;
			}
			bool flag2;
			lock (sync)
			{
				flag2 = placementGaveUp.Contains(pass.RendererPid);
			}
			if (flag2 && !coreEngaged)
			{
				BlockAutoCores("schedule.auto.placementunavailable");
			}
			else if (coreEngaged && coreManual.Isolation && CoreIsolationClient.State != "schedule.isolation.active")
			{
				BlockAutoCores("schedule.auto.failed");
			}
			if (coreObservationOnly)
			{
				PublishAutoCores(pass.RendererName);
			}
			else if (RendererForegroundPid() != pass.RendererPid)
			{
				if (!coreEngaged)
				{
					ApplyAutoCoreStep(autoCoreGovernor.Pause());
				}
				PublishAutoCores(pass.RendererName);
			}
			else
			{
				autoCoreGovernor.EngageManual();
				PublishAutoCores(pass.RendererName);
			}
		}

		private void ClearBackgroundSqueezes()
		{
			if (core.SqueezedCount(SuppressReason.Background, false) > 0)
			{
				core.ClearSqueezes(SuppressReason.Background);
			}
			bool jobReleased = core.ReleaseAllBackgroundAffinityJobs();
			coreBackgroundMoved = core.SqueezedCount(SuppressReason.Background);
			coreBackgroundPending = !jobReleased || core.SqueezedCount(SuppressReason.Background, false) > 0 || core.BackgroundAffinityPending;
			if (!coreBackgroundPending)
			{
				core.EnforceBackgroundAffinity = false;
			}
		}

		private void SyncCoreBackground(HashSet<int> protectedPids, int foreground)
		{
			long nowTicks = AutoCores.NowTicks;
			try
			{
				SyncCoreBackgroundImpl(protectedPids, foreground);
			}
			finally
			{
				coreBudget.Record(nowTicks);
			}
		}

		private ulong CoreBackgroundTarget()
		{
			if (coreManual == null || stopping || !coreEngaged || coreObservationOnly || corePlan == null || !corePlan.Available || CorePending)
			{
				return 0uL;
			}
			if (!coreManual.Isolation || CoreIsolationClient.State != "schedule.isolation.active" || CoreIsolationClient.ActiveMask != corePlan.Exclusive)
			{
				return 0uL;
			}
			ulong num = allMask & ~corePlan.Exclusive & ~AutoCores.WholeCores(corePlan.Allocated, corePhysical);
			if (AutoCores.CountBits(num) < 2)
			{
				return 0uL;
			}
			return num;
		}

		private void SyncCoreBackgroundImpl(HashSet<int> protectedPids, int foreground)
		{
			core.RetryRetiredAutomaticLayouts();
			if (core.BackgroundAffinityPending)
			{
				ClearBackgroundSqueezes();
				return;
			}
			ulong num = CoreBackgroundTarget();
			if (num == 0)
			{
				ClearBackgroundSqueezes();
				return;
			}
			core.EnforceBackgroundAffinity = true;
			foreach (int item in core.PidsWith(SuppressReason.Background))
			{
				long num2 = core.CreationOf(item);
				string text = core.NameOf(item);
				if (num2 > 0 && !string.IsNullOrEmpty(text))
				{
					bool changed;
					if (IsCriticalThreadGuardian(item, num2))
					{
						ReleaseCriticalGuardianBackground();
					}
					else if (GameAffinityJobLease.IsGuardian(item, num2))
					{
						ReleaseJobGuardianBackground(item, num2, text);
					}
					else if (item == foreground || item == coreWatchPid || protectedPids.Contains(item) || WeGameRuntimeProtection.IsProtectedRuntimeName(text) || PeripheralCatalog.IsInputAudioLike(text))
					{
						core.SetSqueeze(item, num2, text, 0uL, SuppressReason.Background, out changed);
					}
					else if (!core.IsSqueezed(item))
					{
						core.SetSqueeze(item, num2, text, num, SuppressReason.Background, out changed);
					}
				}
			}
			coreBackgroundMoved = core.SqueezedCount(SuppressReason.Background);
		}

		private void ApplyAutoCoreStep(AutoCoreStep step)
		{
			if (step == AutoCoreStep.Release)
			{
				ReleaseExclusive();
			}
		}

		private void BlockAutoCores(string reason)
		{
			if (coreManual != null)
			{
				corePlacementStopReason = reason;
			}
			coreAffinityVerified = false;
			AutoCoreGovernor autoCoreGovernor = coreGovernor;
			if (autoCoreGovernor != null)
			{
				bool flag = autoCoreGovernor.Blocked == null;
				ApplyAutoCoreStep(autoCoreGovernor.Block(reason));
				if (flag)
				{
					Logger.Log(Lang.F("log.autocore.blocked", Lang.T(reason)));
				}
			}
		}

		private bool ReleaseExclusive()
		{
			bool flag = coreEngaged;
			exclusiveAuditRetry.Reset();
			exclusiveAuditDeferred = false;
			coreAffinityVerified = false;
			ClearBackgroundSqueezes();
			coreEngaged = false;
			bool flag2 = StopCoreIsolation();
			bool flag3 = RestoreAllHardPins();
			corePinPending = !flag3;
			if ((!flag2 || !flag3) && coreGovernor != null && coreGovernor.Blocked == null)
			{
				coreGovernor.Block("schedule.auto.pending");
			}
			if (flag)
			{
				Logger.Log((flag2 && flag3) ? Lang.T("log.autocore.released") : Lang.T("log.autocore.releasepending"));
			}
			if (flag2)
			{
				return flag3;
			}
			return false;
		}

		private bool ExclusiveDrifted(int pid, BoostPass pass, out ulong observed, out bool unreadable)
		{
			observed = 0uL;
			unreadable = false;
			if (!pass.Exclusive || !coreEngaged || CpuTopology.MultiGroup || stopping)
			{
				return false;
			}
			long nowTicks = AutoCores.NowTicks;
			if (nowTicks - exclusiveCheckTicks < 10000000)
			{
				return false;
			}
			exclusiveCheckTicks = nowTicks;
			exclusiveAuditDeferred = false;
			lock (sync)
			{
				if (placementGaveUp.Contains(pid))
				{
					return false;
				}
				HardPin value = null;
				int num;
				switch ((gamePlacement.ContainsKey(pid) && hardPins.TryGetValue(pid, out value) && value.Pid == pid && value.Creation == pass.RendererCreation) ? HardPinHandleStateOf(value) : HardPinHandleState.Unknown)
				{
				case HardPinHandleState.Gone:
					return false;
				case HardPinHandleState.Match:
					string jobReason;
					if (value.JobBound && !coreAffinityJobs.Verify(pid, value.Creation, pass.DesiredMask, out jobReason))
					{
						corePlacementEvidence = "pid=" + pid + " stage=job-audit reason=" + jobReason;
						num = 0;
						break;
					}
					num = (ReadCoreAffinity(value.RestoreHandle, out observed) ? 1 : 0);
					if (num == 0)
					{
						int error = Marshal.GetLastWin32Error();
						exclusiveAuditDeferred = DeferExclusiveAuditFailure(pass, "audit-read", error);
					}
					break;
				default:
					corePlacementEvidence = "pid=" + pid + " creation=" + pass.RendererCreation + " stage=audit-identity-or-ownership-unavailable";
					num = 0;
					break;
				}
				bool flag = (byte)num != 0;
				RecordCoreAffinity(pass.DesiredMask, observed, flag);
				if (coreAffinityVerified) exclusiveAuditRetry.Reset();
				unreadable = !flag;
				return !flag || !coreAffinityVerified;
			}
		}

		private bool ReadCoreAffinity(IntPtr handle, out ulong observed)
		{
			return Native.TryQueryAffinity(handle, out observed);
		}

		private bool DeferExclusiveAuditFailure(BoostPass pass, string stage, int error)
		{
			coreAffinityVerified = false;
			bool retry = exclusiveAuditRetry.ShouldRetry(error, AutoCores.NowTicks);
			corePlacementEvidence = "pid=" + pass.RendererPid + " creation=" + pass.RendererCreation
				+ " stage=" + stage + " win32=" + error + " target=0x" + pass.DesiredMask.ToString("X")
				+ " attempt=" + exclusiveAuditRetry.Failures + "/3 action=" + (retry ? "retry" : "stop");
			Logger.Warn("Game affinity maintenance " + corePlacementEvidence);
			return retry;
		}

		private void RecordCoreAffinity(ulong expected, ulong observed, bool readable)
		{
			coreAffinityObserved = (readable ? observed : 0);
			coreAffinityRead = readable;
			coreAffinityVerified = readable && ManualAffinityMatches(observed, expected);
		}

		internal static bool ManualAffinityMatches(ulong observed, ulong expected)
		{
			if (expected != 0)
			{
				return observed == expected;
			}
			return false;
		}

		private bool PlacementChangedOutside(IntPtr h, Snap original, out ulong observed, out bool unreadable)
		{
			observed = 0uL;
			unreadable = false;
			if (CpuTopology.MultiGroup)
			{
				return false;
			}
			ulong num = ((original.Aff != 0) ? original.Aff : allMask);
			if (!Native.TryQueryAffinity(h, out observed))
			{
				unreadable = true;
				return false;
			}
			if (observed != num)
			{
				return true;
			}
			if (coreManual != null)
			{
				return false;
			}
			uint[] array = Native.QueryCpuSets(h);
			if (array == null)
			{
				unreadable = true;
				return false;
			}
			return !CoreThreadPlacement.Same(array, original.CpuSets ?? new uint[0]);
		}

		private bool HoldExclusive(int pid, BoostPass pass, ulong observed)
		{
			switch (WriteBackExclusive(pid, pass, true))
			{
			case ExclusiveWriteBack.Busy:
			case ExclusiveWriteBack.Deferred:
				return false;
			case ExclusiveWriteBack.Failed:
				YieldExclusive(pid, observed);
				return false;
			default:
			{
				int num = ++exclusiveWriteBacks;
				if (num <= 3 || num % 20 == 0)
				{
					Logger.Log(PerformancePolicy.Text("其他程序改动了游戏亲和性 读回 0x", "Another program changed the game's affinity (read back 0x") + observed.ToString("X") + PerformancePolicy.Text(" 已改回 0x", "); written back to 0x") + pass.DesiredMask.ToString("X") + PerformancePolicy.Text(" 本局第 ", " this session: ") + num + PerformancePolicy.Text(" 次", ""));
				}
				return true;
			}
			}
		}

		private ExclusiveWriteBack WriteBackExclusive(int pid, BoostPass pass, bool retryMaintenance = false)
		{
			if (stopping || pass == null || !pass.Exclusive || CpuTopology.MultiGroup || pass.DesiredMask == 0)
			{
				return ExclusiveWriteBack.Failed;
			}
			if (!Monitor.TryEnter(criticalProcessGate, 0))
			{
				return ExclusiveWriteBack.Busy;
			}
			try
			{
				lock (sync)
				{
					HardPin value;
					if (CorePending || placementGaveUp.Contains(pid) || !hardPins.TryGetValue(pid, out value)
						|| value.RestoreRequested || value.RestoreInFlight || value.Creation != pass.RendererCreation
						|| value.AppliedAffinity != pass.DesiredMask || HardPinHandleStateOf(value) != 0)
					{
						return ExclusiveWriteBack.Failed;
					}
					if (value.JobBound)
					{
						string jobReason;
						bool verified = coreAffinityJobs.Verify(pid, value.Creation, pass.DesiredMask, out jobReason);
						RecordCoreAffinity(pass.DesiredMask, verified ? pass.DesiredMask : 0, verified);
						if (!verified) corePlacementEvidence = "pid=" + pid + " stage=job-audit reason=" + jobReason;
						return verified ? ExclusiveWriteBack.Written : ExclusiveWriteBack.Failed;
					}
					ulong currentMask;
					if (!Native.TryQueryAffinity(value.RestoreHandle, out currentMask)) return ExclusiveWriteBack.Failed;
					if (currentMask == pass.DesiredMask)
					{
						RecordCoreAffinity(pass.DesiredMask, currentMask, true);
						return ExclusiveWriteBack.Written;
					}
					string threadReason;
					if (!GameThreadAffinityAdmission.Check(value.RestoreHandle, pid, value.Creation, currentMask, out threadReason))
					{
						corePlacementEvidence = "pid=" + pid + " stage=thread-affinity-refused reason=" + threadReason;
						return ExclusiveWriteBack.Failed;
					}
					bool flag = Native.SetProcessAffinityMask(value.RestoreHandle, (UIntPtr)pass.DesiredMask);
					int writeError = flag ? 0 : Marshal.GetLastWin32Error();
					if (flag) criticalNextSample = 0L;
					ulong mask;
					bool flag2 = Native.TryQueryAffinity(value.RestoreHandle, out mask);
					int readError = flag2 ? 0 : Marshal.GetLastWin32Error();
					RecordCoreAffinity(pass.DesiredMask, mask, flag2);
					if (!flag || !flag2 || mask != pass.DesiredMask)
					{
						if (!retryMaintenance) return ExclusiveWriteBack.Failed;
						return DeferExclusiveAuditFailure(pass, !flag ? "audit-write" : (!flag2 ? "audit-write-readback" : "audit-write-mismatch"), !flag ? writeError : readError)
							? ExclusiveWriteBack.Deferred : ExclusiveWriteBack.Failed;
					}
					exclusiveAuditRetry.Reset();
					criticalNextSample = 0L;
					return ExclusiveWriteBack.Written;
				}
			}
			finally
			{
				Monitor.Exit(criticalProcessGate);
			}
		}

		private void YieldExclusive(int pid, ulong observed)
		{
			long num = 0L;
			bool jobOwned = false;
			lock (sync)
			{
				HardPin pin;
				jobOwned = hardPins.TryGetValue(pid, out pin) && coreAffinityJobs.HasLease(pid, pin.Creation);
			}
			// Process restoration must follow critical-thread restoration under its gate.
			if (jobOwned && !RestoreHardPin(pid))
			{
				BlockAutoCores("schedule.auto.pending");
				return;
			}
			lock (sync)
			{
				placementGaveUp.Add(pid);
				gamePlacement.Remove(pid);
				Snap value;
				if (gameBoost.TryGetValue(pid, out value))
				{
					value.PlacementYielded = true;
					gameBoost[pid] = value;
					num = value.Creation;
				}
			}
			if (num > 0)
			{
				CrashGuard.YieldBoostPlacement(pid, num);
			}
			ForgetHardPin(pid);
			coreAffinityVerified = false;
			if (coreManual != null)
			{
				corePlacementStopReason = "schedule.auto.external";
			}
			coreEngaged = false;
			AutoCoreGovernor autoCoreGovernor = coreGovernor;
			if (autoCoreGovernor != null)
			{
				autoCoreGovernor.Block("schedule.auto.external");
			}
			if (!StopCoreIsolation() && autoCoreGovernor != null)
			{
				autoCoreGovernor.Block("schedule.auto.pending");
			}
			Logger.Log(Lang.F("log.autocore.external", observed.ToString("X")));
		}

		private bool PinExclusive(IntPtr h, int pid, BoostPass pass, Snap original)
		{
			if (!Monitor.TryEnter(criticalProcessGate, 0))
			{
				return false;
			}
			try
			{
				if (!PrepareCriticalThreadProcessMaskMutation(pid, original.Creation))
				{
					return false;
				}
				return PinExclusiveLocked(h, pid, pass, original);
			}
			finally
			{
				Monitor.Exit(criticalProcessGate);
			}
		}

		private bool PinExclusiveLocked(IntPtr h, int pid, BoostPass pass, Snap original)
		{
			if (CpuTopology.MultiGroup || original.Aff == 0 || pass.DesiredMask == 0 || (pass.DesiredMask & ~allMask) != 0 || (coreManual == null && (pass.DesiredMask & ~original.Aff) != 0))
			{
				LogCorePlacement(pass, "affinity-range", 0, h);
				return false;
			}
			string threadAdmissionReason;
			if (!GameThreadAffinityAdmission.Check(h, pid, original.Creation, original.Aff, out threadAdmissionReason))
			{
				LogCorePlacement(pass, threadAdmissionReason, 0, h);
				return false;
			}
			IntPtr intPtr = DuplicateRestoreHandle(h);
			if (intPtr == IntPtr.Zero)
			{
				LogCorePlacement(pass, "restore-handle", Marshal.GetLastWin32Error(), h);
				return false;
			}
			bool retained = false;
			bool verified = false;
			try
			{
				ulong mask;
				lock (sync)
				{
					if (stopping) return false;
					if (!Native.TryQueryAffinity(h, out mask))
					{
						LogCorePlacement(pass, "read-original", Marshal.GetLastWin32Error(), h);
						return false;
					}
					if (mask != original.Aff)
					{
						LogCorePlacement(pass, "original-changed", 0, h);
						return false;
					}
					// Transfer recovery ownership from the legacy claim to the Job receipt before changing affinity.
					if (!CrashGuard.TrackBoostAffinity(pid, original.Creation, 0uL))
					{
						LogCorePlacement(pass, "journal", 0, h);
						return false;
					}
					// Retain the handle before the Job backend can change the target.
					if (!RememberHardPin(pid, original.Creation, original.Aff, pass.DesiredMask, intPtr)) return false;
					hardPins[pid].JobRecoveryOnly = true;
					retained = true;
				}
				string jobReason;
				coreAffinityJobs.ThreadRecoveryRoot = CriticalThreadStoreRoot;
				bool jobBound = coreAffinityJobs.Ensure(pid, original.Creation, pass.RendererName, original.Aff, pass.DesiredMask, out jobReason);
				lock (sync)
				{
					HardPin pin;
					if (hardPins.TryGetValue(pid, out pin) && pin.Creation == original.Creation)
					{
						pin.JobBound = jobBound;
						pin.JobFallbackReason = jobBound ? null : jobReason;
						if (pin.RestoreRequested || pin.RestoreInFlight) return false;
					}
				}
				if (stopping || coreAffinityJobs.HasPending || (!jobBound && coreAffinityJobs.IsActive(pid, original.Creation)))
				{
					LogCorePlacement(pass, "job-recovery", 0, h);
					return false;
				}
				if (!jobBound)
				{
					// A game-owned thread-placement refusal also blocks process-affinity fallback.
					if (GameThreadAffinityAdmission.IsRefusal(jobReason))
					{
						LogCorePlacement(pass, jobReason, 0, h);
						return false;
					}
					ulong fallbackMask;
					if (!Native.TryQueryAffinity(h, out fallbackMask)
						|| !GameThreadAffinityAdmission.Check(h, pid, original.Creation, fallbackMask, out threadAdmissionReason))
					{
						LogCorePlacement(pass, threadAdmissionReason ?? GameThreadAffinityAdmission.RefusalPrefix + "process-mask-unreadable", 0, h);
						return false;
					}
					Logger.Warn("Game affinity Job protection unavailable pid=" + pid + " reason=" + jobReason + "; using process-affinity maintenance");
					// After Job settlement, transfer recovery ownership before process-affinity fallback writes.
					lock (sync)
					{
						HardPin fallbackPin;
						if (stopping || !hardPins.TryGetValue(pid, out fallbackPin) || fallbackPin.Creation != original.Creation
							|| fallbackPin.RestoreRequested || fallbackPin.RestoreInFlight
							|| !CrashGuard.TrackBoostAffinity(pid, original.Creation, pass.DesiredMask))
						{
							LogCorePlacement(pass, "fallback-journal", 0, h);
							return false;
						}
						fallbackPin.JobRecoveryOnly = false;
						if (fallbackMask != pass.DesiredMask && !Native.SetProcessAffinityMask(h, (UIntPtr)pass.DesiredMask))
						{
							LogCorePlacement(pass, "set-affinity", Marshal.GetLastWin32Error(), h);
							return false;
						}
					}
				}
				exclusiveWriteBacks = 0;
				exclusiveAuditRetry.Reset();
				bool readable = Native.TryQueryAffinity(h, out mask);
				int readError = readable ? 0 : Marshal.GetLastWin32Error();
				verified = readable && mask == pass.DesiredMask;
				if (verified && jobBound) verified = coreAffinityJobs.Verify(pid, original.Creation, pass.DesiredMask, out jobReason);
				RecordCoreAffinity(pass.DesiredMask, mask, readable);
				coreAffinityVerified = verified;
				if (!verified) LogCorePlacement(pass, readable ? "readback-mismatch" : "readback", readError, h);
				return verified;
			}
			finally
			{
				if (!retained) Native.CloseHandle(intPtr);
				else if (!verified) RestoreHardPin(pid);
			}
		}

		private bool EngageIsolation(int pid, BoostPass pass)
		{
			if (stopping || coreManual == null)
			{
				return false;
			}
			if (!coreManual.Isolation)
			{
				coreEngaged = true;
				return true;
			}
			if (CriticalCoreIsolationRecoveryOwner != null && !StopCriticalThreads(0)) return false;
			lock (coreIsolationGate)
			{
			if (criticalOwnsCoreIsolation) return false;
			if (coreEngaged && CoreIsolationClient.State == "schedule.isolation.active")
			{
				return true;
			}
			if (coreIsolation == null)
			{
				coreIsolation = new CoreIsolationClient();
			}
			if (coreIsolation.Ensure((corePlan != null) ? corePlan.Exclusive : pass.DesiredMask, pid, pass.RendererCreation, pass.DesiredMask, true))
			{
				coreEngaged = true;
				Logger.Log(Lang.F("log.autocore.engaged", CpuTopology.DescribeMask((corePlan != null) ? corePlan.Exclusive : pass.DesiredMask)));
				return true;
			}
			}
			StopCoreIsolation();
			return false;
		}

		private bool StopCoreIsolation()
		{
			if (HasCriticalCoreIsolationLease && CriticalCoreIsolationRecoveryOwner == null && !StopCriticalThreads(0)) return false;
			coreAffinityVerified = false;
			bool result = StopCoreRefinement();
			ClearBackgroundSqueezes();
			coreEngaged = false;
			lock (coreIsolationGate)
			{
			if (criticalOwnsCoreIsolation) return result && !coreBackgroundPending;
			if (criticalBorrowsCoreIsolation) return false;
			bool flag = coreIsolation == null || coreIsolation.Stop();
			if (flag)
			{
				coreIsolation = null;
			}
			if (flag && !coreBackgroundPending)
			{
				return result;
			}
			return false;
			}
		}

		private void PublishAutoCores(string game)
		{
			AutoCoreGovernor autoCoreGovernor = coreGovernor;
			ulong appliedAffinity = coreGameMask;
			bool jobBound = false;
			string jobFallback = null;
			lock (sync)
			{
				HardPin value;
				if (coreEngaged && hardPins.TryGetValue(coreWatchPid, out value))
				{
					appliedAffinity = value.AppliedAffinity;
					jobBound = value.JobBound;
					jobFallback = value.JobFallbackReason;
				}
			}
			AutoCoreStatus autoCoreStatus = new AutoCoreStatus();
			autoCoreStatus.Plan = corePlan;
			autoCoreStatus.Manual = coreManual != null;
			autoCoreStatus.ManualIsolation = coreManual != null && coreManual.Isolation;
			autoCoreStatus.Game = game;
			autoCoreStatus.ProfileId = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Id : null);
			autoCoreStatus.Watching = autoCoreGovernor != null && coreWatchPid > 0;
			autoCoreStatus.Exclusive = autoCoreGovernor != null && autoCoreGovernor.Exclusive;
			autoCoreStatus.Engaged = coreEngaged;
			autoCoreStatus.BackgroundMoved = coreBackgroundMoved;
			autoCoreStatus.BackgroundAffinityDetail = core == null ? null : core.BackgroundAffinityDetail();
			autoCoreStatus.GameMask = appliedAffinity;
			autoCoreStatus.ExpectedMask = ((coreManual != null) ? coreManual.GameMask : coreGameMask);
			autoCoreStatus.ObservedMask = coreAffinityObserved;
			autoCoreStatus.AffinityRead = coreAffinityRead;
			autoCoreStatus.AffinityVerified = coreEngaged && coreAffinityVerified && !CorePending;
			autoCoreStatus.JobAffinity = jobBound && autoCoreStatus.AffinityVerified;
			autoCoreStatus.JobFallbackReason = jobFallback;
			autoCoreStatus.WorkMs = coreBudget.TotalMs;
			autoCoreStatus.WorkMaxMs = coreBudget.MaxMs;
			autoCoreStatus.Pending = CorePending;
			autoCoreStatus.PlacementEvidence = corePlacementEvidence;
			autoCoreStatus.Blocked = ((autoCoreGovernor != null) ? autoCoreGovernor.Blocked : ((coreManual != null && coreAllowed && corePlan != null) ? corePlan.Blocked : null));
			autoCoreStatus.ObservationOnly = coreObservationOnly;
			AutoCores.Publish(autoCoreStatus);
		}

		public List<string> GetAutoAddIgnoredPaths()
		{
			List<string> list;
			lock (sync)
			{
				list = new List<string>(autoAddIgnore);
			}
			list.Sort(StringComparer.OrdinalIgnoreCase);
			return list;
		}

		public bool RemoveAutoAddIgnoredPath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return false;
			}
			string text = path.Trim();
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked() || !autoAddIgnore.Contains(text))
				{
					return false;
				}
				HashSet<string> hashSet = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
				hashSet.Remove(text);
				if (!CommitLibraryLocked(GetProfiles(), hashSet))
				{
					return false;
				}
				autoAddRejectUntil.Remove(text);
				autoAddAttempts.Remove(text);
				Interlocked.Increment(ref autoAddEpoch);
				Interlocked.Exchange(ref autoAddGateTicks, 0L);
			}
			KickGameDetectionNow();
			RaiseLibraryChanged();
			return true;
		}

		private bool AddAutoDetectedExecutableCore(string executablePath, out string error, Func<bool> canCommit = null)
		{
			bool persistenceFailed;
			return AddAutoDetectedExecutableCore(executablePath, out error, out persistenceFailed, canCommit);
		}

		private bool AddAutoDetectedExecutableCore(string executablePath, out string error, out bool persistenceFailed, Func<bool> canCommit = null)
		{
			error = null;
			persistenceFailed = false;
			lock (sync)
			{
				if (!AutoDetectedCommitAllowed(executablePath) || (canCommit != null && !canCommit()) || !EnsureLibraryReadyLocked())
				{
					return false;
				}
				List<GameProfile> next = GetProfiles();
				HashSet<string> nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
				if (!StageGameExecutable(null, executablePath, null, next, nextIgnore, out error) || !CommitLibraryLocked(next, nextIgnore, out persistenceFailed, () => AutoDetectedCommitAllowed(executablePath) && (canCommit == null || canCommit())))
				{
					return false;
				}
			}
			KickLibraryChanged();
			return true;
		}

		private bool AutoDetectedCommitAllowed(string path)
		{
			if (!stopping && !panicReq && enabled && autoAddOn && !active && !ProfileStoreSaveFailed && !autoIgnoreLoadFailed)
			{
				return !autoAddIgnore.Contains(path);
			}
			return false;
		}

		internal List<string> ApplyNvGamePlan(string path, NvGamePlan plan, out bool retry, long now = -1L, bool preStage = false)
		{
			SyncNvAutomaticPhase(path, plan);
			List<string> list = NvDrsTweaks.ApplyForGame(path, plan, out retry);
			if (list == null && retry && plan != null)
			{
				if (plan.MaxPerf)
				{
					RecordNvAutomation("NvMaxPerf", "failed", preStage);
				}
				if (plan.ShaderCacheMax)
				{
					RecordNvAutomation("NvShaderCache", "failed", preStage);
				}
				if (plan.LowLatMode == "on")
				{
					RecordNvAutomation("NvLowLat", "failed", preStage);
				}
			}
			return list;
		}

		internal void ResetNvApplyAttempts()
		{
		}

		internal NvGamePlan RefreshAutomaticNvPlan(bool maxPerf, bool cache, bool onAc, string lowLatency = "off", bool windowedVrr = false, bool rebar = false)
		{
			NvGamePlan nvGamePlan = NvGamePlan.ForAutomatic(maxPerf, cache, onAc, lowLatency, windowedVrr, rebar);
			int automaticKey = nvGamePlan.AutomaticKey;
			lock (sync)
			{
				if (automaticNvPlanKey != automaticKey)
				{
					automaticNvPlanKey = automaticKey;
					ForgetNvTweaksLocked();
					Interlocked.Exchange(ref nvTweakRetryAtTicks, 0L);
				}
			}
			return nvGamePlan;
		}

		internal void InitializeAutomation()
		{
			Settings.Remove("AutomaticPolicyV1");
			Settings.Remove("AutoFrameCoordinationV1");
			MigrateLegacyPreset();
			MigrateLegacyLowLatency();
			RetireSceneRulePreferences();
			SyncAutomationPolicy();
		}

		private static void RetireSceneRulePreferences()
		{
			bool flag = false;
			string[] array = new string[2] { "GmAggressive", "GmAdaptiveEscalateV1" };
			foreach (string name in array)
			{
				bool value;
				string value2;
				if (Settings.TryLoadStoredBool(name, out value) || (Settings.TryLoadStr(name, out value2) && !string.IsNullOrEmpty(value2)))
				{
					flag = true;
				}
				Settings.Remove(name);
			}
			if (flag)
			{
				Logger.Log(PerformancePolicy.Text("“扩大后台压制范围”与“智能自适应升档”已并入场景规则，旧的独立选择不再生效：竞技在后台治理生效时固定宽范围，智能平时窄范围并在 CPU 持续满载时临时放宽", "Broader background suppression and Smart adaptive escalation are now scene rules; the old separate choices no longer apply: Competitive always uses the wide scope while background control is active, Smart uses the narrow scope and widens it temporarily under sustained CPU saturation"));
			}
		}

		private void MigrateLegacyLowLatency()
		{
			bool value;
			if (!Settings.TryLoadStoredBool("GameLowLatencyOn", out value))
			{
				return;
			}
			if (value && !PolicyResolver.HasExplicitGlobal("NvLowLat"))
			{
				if (!Settings.SaveStr("NvLowLat", "on"))
				{
					return;
				}
				nvLowLatMode = "on";
			}
			Settings.Remove("GameLowLatencyOn");
		}

		private void MigrateLegacyPreset()
		{
			int result;
			if (!int.TryParse(Settings.LoadStr("PerformancePreset", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || !PresetValue.IsLegacyRaw(result))
			{
				return;
			}
			PerformancePreset performancePreset = PresetValue.From(result);
			int num = (int)performancePreset;
			if (Settings.SaveStr("PerformancePreset", num.ToString(CultureInfo.InvariantCulture)))
			{
				lock (sync)
				{
					preset = performancePreset;
				}
				string text = PresetValue.MigrationNote(result);
				if (text != null)
				{
					Logger.Log(text);
				}
			}
		}

		private void SyncAutomationPolicy()
		{
			PolicySnapshot policySnapshot = sessionPolicy ?? PolicyResolver.Global();
			bgSuppressOn = policySnapshot.SuppressBackground;
			boostOn = policySnapshot.BoostGame;
			pauseUpdateOn = policySnapshot.PauseUpdate;
			pauseMaintOn = policySnapshot.PauseMaintenance;
			pauseServicesOn = policySnapshot.PauseServices;
			planSwitch = policySnapshot.PowerPlanOn;
			renderLaneOn = policySnapshot.RenderLane;
			cacheWarmOn = policySnapshot.CacheWarm;
			englishInputOn = policySnapshot.EnglishInput;
			nvMaxPerf = policySnapshot.NvMaxPerf;
			nvLowLatMode = policySnapshot.NvLowLatMode;
			nvSmoothMotion = false;
			nvShaderCacheMax = policySnapshot.NvShaderCacheMax;
			nvRebarOn = false;
			nvDlssMode = "off";
			amdAfmf = false;
			intelEnduranceOn = policySnapshot.IntelEnduranceOff;
			dwmBoostOn = policySnapshot.DwmBoost;
			nvVrrWindowedOn = Settings.LoadCached("NvVrrWindowed", false);
			rsrOn = false;
			gpuPrefStageOn = Settings.LoadCached("GpuPrefStageOn", true) && Native.OnAcPower();
			lock (sync)
			{
				if (!autoAddPreferenceUnsaved)
				{
					autoAddOn = Settings.LoadCached("GmAutoAdd", false);
				}
			}
			killGameDvr = Settings.LoadCached("GameDvrOff", true);
			mmcssOn = Settings.LoadCached("GmMmcss", true);
			SuppressionCore.GpuDemoteEnabled = bgSuppressOn;
		}

		internal bool SetEnvironmentPreference(string key, bool value)
		{
			if (key == "AutoTimerTick" && value && LaboratoryClockRepair.HasReceipt) return false;
			if (!PerformancePolicy.IsEnvironment(key))
			{
				return false;
			}
			if (key == "GmStandbyCleanerV2")
			{
				return TrySetStandbyCleanerEnabled(value);
			}
			lock (timerMode.Gate)
			{
				if (key == "AutoTimerTick" && value && LaboratoryClockRepair.HasReceipt) return false;
				lock (sync)
				{
					if (stopping || !Settings.Save(key, value))
					{
						return false;
					}
					if (key == "GmCacheWarmV2")
					{
						cacheWarmOn = value;
						InvalidateCacheWarm();
					}
				}
			}
			RequestPolicyApply("SetEnvironmentPreference");
			return true;
		}

		internal bool SetGameLowLatency(bool value)
		{
			string val = (value ? "on" : "off");
			lock (sync)
			{
				if (stopping || !Settings.SaveStr("NvLowLat", val))
				{
					return false;
				}
				Settings.Remove("GameLowLatencyOn");
				nvLowLatMode = val;
				SyncAutomationPolicy();
				automaticNvPlanKey = -1;
				ForgetNvTweaksLocked();
			}
			if (!value)
			{
				NvDrsTweaks.RestoreKinds(NvDrsTweaks.UltraKeys);
			}
			else
			{
				NvDrsTweaks.RestoreKinds(new string[2] { "ullenable", "lowlatcpl" });
				SaveCounter("NvFailStreak_prerender", 0);
			}
			RequestPolicyApply("SetGameLowLatency");
			return true;
		}

		internal void RefreshAutomaticPreferences()
		{
			lock (familyPolicyGate)
			{
				InvalidateFamilyPolicy();
				SyncAutomationPolicy();
			}
			RequestPolicyApply("RefreshAutomaticPreferences");
		}

		private void RecordAutomation(string key, string state)
		{
			lock (sync)
			{
				string value;
				automationEvidence.TryGetValue(key, out value);
				bool flag = key == "awake" && state == "restored" && automationSessionEnding && !awakeFinalRestoreLogged;
				string powerDetail = key == "PowerPlanOn" ? PowerPlanFailureDetail(state) : "";
				string powerSignature = powerDetail.Length == 0 ? "" : state + "|" + powerDetail;
				bool powerFailureChanged = powerSignature.Length > 0 && powerFailureLogSignature != powerSignature;
				string timerDetail = key == "AutoGlobalTimer" ? GlobalTimerFailureDetail(state) : "";
				string timerSignature = timerDetail.Length == 0 ? "" : state + "|" + timerDetail;
				bool timerFailureChanged = timerSignature.Length > 0 && timerFailureLogSignature != timerSignature;
				if (value == state && !flag && !powerFailureChanged && !timerFailureChanged)
				{
					return;
				}
				if (key == "PowerPlanOn") powerFailureLogSignature = powerSignature;
				if (key == "AutoGlobalTimer") timerFailureLogSignature = timerSignature;
				automationEvidence[key] = state;
				if ((key == "awake" && !ShouldLogAwakeTransition(state, value)) || string.IsNullOrEmpty(state) || state == "unchanged")
				{
					return;
				}
				if (key == "session" && !string.IsNullOrEmpty(automationSession))
				{
					string text = ((state == "started") ? "start" : state);
					string text2 = "SESSION [" + automationSession + "] " + text + " | " + automationSessionName;
					if (state == "restore-failed")
					{
						Logger.Warn(text2);
					}
					else if (state == "restored")
					{
						Logger.Log("PASS " + text2);
					}
					else
					{
						Logger.Info(text2);
					}
					if (state == "started") Logger.RuntimeIdentity();
					return;
				}
				string text3 = "AUTO [" + automationSession + "] " + key + " | " + state;
				if (key == "PowerPlanOn")
				{
					if (powerDetail.Length > 0) text3 += " | " + powerDetail;
					if (state == "stopped")
					{
						Logger.Warn(text3);
						return;
					}
				}
				if (key == "AutoGlobalTimer")
				{
					bool verified; uint actual, finest;
					bool leaseActive = timerLease.Readback(out verified, out actual, out finest);
					text3 += " | desired100ns=" + TimerResolutionLease.Desired + " active=" + leaseActive
						+ " verified=" + verified + " actual100ns=" + actual + " finest100ns=" + finest;
					if (timerDetail.Length > 0) text3 += " | " + timerDetail;
					if (state == "stopped") { Logger.Warn(text3); return; }
				}
				switch (state)
				{
				case "failed":
				case "restore-failed":
					Logger.Warn(text3);
					break;
				case "applied":
				case "restored":
				case "started":
				case "timer-locked":
					Logger.Log("PASS " + text3);
					break;
				default:
					Logger.Info(text3);
					break;
				}
			}
		}

		private bool ShouldLogAwakeTransition(string state, string previous)
		{
			if (state == "failed")
			{
				awakeRecoveryState = "applied";
				return true;
			}
			if (state == "restore-failed")
			{
				awakeRecoveryState = "restored";
				return true;
			}
			if (state != "applied" && state != "restored")
			{
				return true;
			}
			bool flag = state == "restored" && automationSessionEnding;
			bool flag2 = awakeRecoveryState == state;
			if (flag2)
			{
				awakeRecoveryState = null;
			}
			if (flag && awakeFinalRestoreLogged && !flag2)
			{
				return false;
			}
			long ticks = DateTime.UtcNow.Ticks;
			long num = ((state == "applied") ? awakeAppliedLogTicks : awakeRestoredLogTicks);
			if (!flag && !flag2 && num != 0 && ticks >= num && ticks - num < 600000000)
			{
				return false;
			}
			if (state == "applied")
			{
				awakeAppliedLogTicks = ticks;
			}
			else
			{
				awakeRestoredLogTicks = ticks;
			}
			if (flag)
			{
				awakeFinalRestoreLogged = true;
			}
			return true;
		}

		internal static string AutomationActionLabel(string key)
		{
			if (key == "session")
			{
				return Lang.T("auto.log.session");
			}
			if (key == "AutoDisableFullscreenOptimizations")
			{
				return Lang.T("auto.fso");
			}
			PolicyItem policyItem = PolicyCatalog.ItemOf(key);
			if (policyItem == null)
			{
				return EnvLabel(key);
			}
			return Lang.T(policyItem.LangKey);
		}

		internal static string AutomationStateLabel(string state)
		{
			if (state == "restoring") return Lang.T("auto.log.restoring");
			string obj = state ?? "";
			string key = obj;
			if (obj != null)
			{
				if (_003CPrivateImplementationDetails_003E_007B3F52FF57_002DCB4F_002D442D_002D8B1D_002DAF29EA1BA6DE_007D._0024_0024method0x6000d05_002D1 == null)
				{
					_003CPrivateImplementationDetails_003E_007B3F52FF57_002DCB4F_002D442D_002D8B1D_002DAF29EA1BA6DE_007D._0024_0024method0x6000d05_002D1 = new Dictionary<string, int>(19)
					{
						{ "", 0 },
						{ "started", 1 },
						{ "applied", 2 },
						{ "configured", 3 },
						{ "fso-configured", 4 },
						{ "restored", 5 },
						{ "failed", 6 },
						{ "restore-failed", 7 },
						{ "pending-reboot", 8 },
						{ "restore-pending-reboot", 9 },
						{ "ready", 10 },
						{ "unverified", 11 },
						{ "timer-requested", 12 },
						{ "timer-locked", 13 },
						{ "timer-limited", 14 },
						{ "timer-unverified", 15 },
						{ "unsupported", 16 },
						{ "unchanged", 17 },
						{ "stopped", 18 }
					};
				}
				int value;
				if (_003CPrivateImplementationDetails_003E_007B3F52FF57_002DCB4F_002D442D_002D8B1D_002DAF29EA1BA6DE_007D._0024_0024method0x6000d05_002D1.TryGetValue(key, out value))
				{
					switch (value)
					{
					case 0:
						return Lang.T("auto.log.empty");
					case 1:
						return Lang.T("auto.log.started");
					case 2:
						return Lang.T("auto.log.applied");
					case 3:
						return Lang.T("auto.log.configured");
					case 4:
						return Lang.T("auto.fso.configured");
					case 5:
						return Lang.T("auto.log.restored");
					case 6:
						return Lang.T("auto.log.failed");
					case 7:
						return Lang.T("auto.log.restore-failed");
					case 8:
						return Lang.T("auto.log.pending-reboot");
					case 9:
						return Lang.T("auto.log.restore-pending-reboot");
					case 10:
						return Lang.T("auto.log.ready");
					case 11:
						return Lang.T("env.performance.unverified");
					case 12:
						return Lang.T("auto.log.timer-requested");
					case 13:
						return Lang.T("auto.log.timer-locked");
					case 14:
						return Lang.T("auto.log.timer-limited");
					case 15:
						return Lang.T("auto.log.timer-unverified");
					case 16:
						return Lang.T("auto.log.unsupported");
					case 17:
						return Lang.T("auto.log.unchanged");
					case 18:
						return Lang.T("auto.log.stopped");
					}
				}
			}
			return state;
		}

		internal string AutomaticExecutionState(string key)
		{
			bool flag = key == "GmPauseServices" && OptionalServicePause.Active;
			int num = (flag ? OptionalServicePause.PendingStopCount : 0);
			lock (sync)
			{
				switch (key)
				{
				case "PowerPlanOn":
					return PowerPlanExecutionState;
				case "AutoDisableFullscreenOptimizations":
					return automaticFsoState;
				case "AutoTimerTick":
					return timerMode.TickState;
				case "AutoGlobalTimer":
					return GlobalTimerExecutionState();
				default:
				{
					string text = null;
					string[] envKeys = EnvKeys;
					foreach (string text2 in envKeys)
					{
						if (EnvPolicyKey(text2) == key)
						{
							text = text2;
							break;
						}
					}
					if (text != null && envRestorePending.Contains(text))
					{
						return "restore-failed";
					}
					if (text != null && envFused.Contains(text))
					{
						return "stopped";
					}
					if (flag)
					{
						return (num > 0) ? "unverified" : "applied";
					}
					string value;
					if (automationEvidence.TryGetValue(text ?? key, out value))
					{
						return value;
					}
					return "";
				}
				}
			}
		}

		private void BeginAutomationEvidence()
		{
			lock (sync)
			{
				EndAutomationEvidence();
				automationEvidence.Clear();
				automationSession = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
				automationSessionName = activeGame ?? ((sessionPolicy == null) ? "" : sessionPolicy.ProfileName) ?? "";
				automationSessionEnding = false;
				awakeAppliedLogTicks = (awakeRestoredLogTicks = 0L);
				awakeFinalRestoreLogged = false;
				awakeRecoveryState = null;
			}
			RecordAutomation("session", "started");
			PolicySnapshot automationPolicySnapshot = AutomationPolicySnapshot;
			foreach (PolicyItem item in PolicyCatalog.All)
			{
				if (!PerformancePolicy.IsIntent(item.Key) && !PerformancePolicy.IsRetired(item.Key))
				{
					string text = automationPolicySnapshot.ValueOf(item.Key);
					if (text == "0" || text == "off")
					{
						RecordAutomation(item.Key, "unchanged");
					}
				}
			}
		}

		private void EndAutomationEvidence()
		{
			lock (sync)
			{
				if (!string.IsNullOrEmpty(automationSession) && !automationSessionEnding)
				{
					automationSessionEnding = true;
					Logger.Info("SESSION [" + automationSession + "] end | " + automationSessionName);
				}
			}
		}

		[DllImport("user32.dll")]
		private static extern bool GetLastInputInfo(ref AutomationLastInput input);

		private static bool AutomaticQuietDesktop()
		{
			AutomationLastInput automationLastInput = default(AutomationLastInput);
			automationLastInput.Size = (uint)Marshal.SizeOf(typeof(AutomationLastInput));
			AutomationLastInput input = automationLastInput;
			if (GetLastInputInfo(ref input))
			{
				return (uint)(Environment.TickCount - (int)input.Time) >= 30000u;
			}
			return false;
		}

		private void ApplyGameProcessActions(ProcessSnapshot all)
		{
			if (EffBoost)
			{
				corePlacementOnly = false;
				Boost(all);
				return;
			}
			if (!corePlacementOnly)
			{
				if (!UnboostGames())
				{
					return;
				}
				corePlacementOnly = true;
			}
			BoostPass pass = PrepareBoostPass();
			DropStaleBoosts(pass);
			RunIndependentCorePlacement(pass);
		}

		private void Boost(ProcessSnapshot all)
		{
			if (FrameCoordinationEnabled || EffLane) RefreshThreadBoostRecoveryAdmission();
			BoostPass boostPass = PrepareBoostPass();
			SyncFrameCoordination(boostPass);
			if (boostPass.Exclusive)
			{
				boostPass.DesiredMask = coreGameMask;
			}
			DropStaleBoosts(boostPass);
			RunIndependentCorePlacement(boostPass);
			if (all.ByPid.Count == all.Entries.Length)
			{
				ProcEntry renderer;
				if (all.ByPid.TryGetValue(boostPass.RendererPid, out renderer))
					BoostEntry(renderer, boostPass, null);
				PruneDeadBoosts(all.ByPid.Keys);
				return;
			}
			// Preserve entry order when an unusual snapshot contains duplicate PIDs.
			HashSet<int> hashSet = new HashSet<int>();
			ProcEntry[] entries = all.Entries;
			foreach (ProcEntry procEntry in entries)
				BoostEntry(procEntry, boostPass, hashSet);
			PruneDeadBoosts(hashSet);
		}

		private void BoostEntry(ProcEntry procEntry, BoostPass boostPass, HashSet<int> live)
		{
			try
			{
				int pid = procEntry.Pid;
				if (live != null) live.Add(pid);
				bool known;
				bool needTweak;
				bool needPlacement;
				if (boostPass.RendererPid <= 0 || pid != boostPass.RendererPid || !ComputeAuditDue(pid, boostPass, out known, out needTweak, out needPlacement))
				{
					return;
				}
				IntPtr intPtr = OpenBoostHandle(pid, boostPass);
				if (intPtr == IntPtr.Zero)
				{
					return;
				}
				try
				{
					long currentCreation;
					if (VerifyRendererIdentity(intPtr, pid, boostPass, out currentCreation) && RefreshBoostPriority(boostPass))
					{
						HandlePidReuse(pid, currentCreation, ref known, ref needPlacement);
						bool newlyTracked;
						bool gpuOk;
						bool stateOk;
						bool firstVerified;
						if (CaptureAndTrack(intPtr, pid, currentCreation, boostPass, known, out newlyTracked, out gpuOk) && ApplyBoostStateStage(intPtr, pid, boostPass, needTweak, out stateOk, out firstVerified))
						{
							bool flag = boostPass.Exclusive && coreEngaged && coreAffinityVerified;
							string placementText = (flag ? (Lang.T("t.gamemodeboost.18") + CpuTopology.CountSetBits(boostPass.DesiredMask) + Lang.T("t.gamemodeboost.16")) : Lang.T("t.gamemodeboost.17"));
							bool ecoCleared = ClearEfficiencyMode(intPtr, pid, boostPass);
							EngageLaneAndReport(intPtr, pid, currentCreation, boostPass, stateOk, firstVerified, gpuOk, ecoCleared, placementText, flag);
							ApplyGameTweaks(intPtr, pid, boostPass, needTweak);
						}
					}
				}
				finally
				{
					Native.CloseHandle(intPtr);
				}
			}
			catch
			{
			}
		}

		private BoostPass PrepareBoostPass()
		{
			BoostPass boostPass = new BoostPass();
			PolicySnapshot policySnapshot = sessionPolicy;
			PrepareResourcePriorities(boostPass, policySnapshot);
			boostPass.NvMaxPerf = ((policySnapshot != null) ? policySnapshot.NvMaxPerf : nvMaxPerf);
			boostPass.NvLowLat = ((policySnapshot != null) ? policySnapshot.NvLowLatMode : nvLowLatMode);
			boostPass.NvSmooth = ((policySnapshot != null) ? policySnapshot.NvSmoothMotion : nvSmoothMotion);
			boostPass.NvShader = ((policySnapshot != null) ? policySnapshot.NvShaderCacheMax : nvShaderCacheMax);
			boostPass.NvRebar = ((policySnapshot != null) ? policySnapshot.NvRebar : nvRebarOn);
			boostPass.NvDlss = ((policySnapshot != null) ? policySnapshot.NvDlssMode : nvDlssMode);
			boostPass.NvVrr = nvVrrWindowedOn;
			NvGamePlan nvGamePlan = RefreshAutomaticNvPlan(boostPass.NvMaxPerf, boostPass.NvShader, Native.OnAcPower(), boostPass.NvLowLat, boostPass.NvVrr, boostPass.NvRebar);
			boostPass.NvMaxPerf = nvGamePlan.MaxPerf;
			boostPass.RendererPid = -1;
			boostPass.RendererCreation = 0L;
			boostPass.RendererName = null;
			lock (sync)
			{
				if (activeDetection != null && activeDetection.RendererCandidateSelected)
				{
					boostPass.RendererPid = activeDetection.RendererPid;
					boostPass.RendererCreation = activeDetection.RendererCreation;
					boostPass.RendererName = activeDetection.RendererName;
				}
			}
			boostPass.WriteDenied = boostPass.RendererPid > 0 && ProtectedGameRoster.Contains(boostPass.RendererName);
			RenderLane.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), boostPass.RendererPid, boostPass.RendererCreation, "lane:" + EffLane);
			StepAutoCores(boostPass);
			AutoCorePlan autoCorePlan = corePlan;
			AutoCoreGovernor autoCoreGovernor = coreGovernor;
			boostPass.Exclusive = !coreObservationOnly && !CorePending && autoCoreGovernor != null && autoCoreGovernor.Exclusive && autoCorePlan != null && autoCorePlan.Available;
			boostPass.DesiredMask = (boostPass.Exclusive ? coreGameMask : allMask);
			ResolvePriorityTarget(boostPass);
			return boostPass;
		}

		internal static uint BoostPriorityTarget()
		{
			return Native.HIGH_PRIORITY_CLASS;
		}

		private void ResolvePriorityTarget(BoostPass pass)
		{
			if (pass.RendererPid != boostIdentityPid || pass.RendererCreation != boostIdentityCreation)
			{
				ResetBoostIdentity();
			}
			boostIdentityPid = pass.RendererPid;
			boostIdentityCreation = pass.RendererCreation;
			pass.IdentityGeneration = boostIdentityGeneration;
			if (boostIdentityNeedsAudit && pass.RendererPid > 0)
			{
				lock (sync)
				{
					boostStateVerified.Remove(pass.RendererPid);
					gameBoostNextAudit.Remove(pass.RendererPid);
					boostIdentityNeedsAudit = false;
				}
			}
			RefreshBoostPriority(pass);
		}

		private void ResetBoostIdentity()
		{
			boostIdentityPid = 0;
			boostIdentityCreation = 0L;
			boostIdentityGeneration++;
			boostIdentityNeedsAudit = true;
			boostLaneReleased = false;
		}

		private bool RefreshBoostPriority(BoostPass pass)
		{
			if (pass.IdentityGeneration != boostIdentityGeneration || pass.RendererPid <= 0 || pass.RendererCreation <= 0 || pass.RendererPid != boostIdentityPid || pass.RendererCreation != boostIdentityCreation)
			{
				return false;
			}
			pass.LaneAllowed = EffLane && LaneEligible && !pass.WriteDenied && LaneFallbackFor(pass.RendererPid, pass.RendererCreation)
				&& ThreadBoostRecoveryAllowed(pass.RendererPid, pass.RendererCreation);
			if (!pass.LaneAllowed)
			{
				if (!boostLaneReleased)
				{
					boostLaneReleased = RenderLane.Release();
				}
			}
			else
			{
				boostLaneReleased = false;
			}
			pass.PriorityTarget = BoostPriorityTarget();
			return true;
		}

		private void DropStaleBoosts(BoostPass pass)
		{
			bool flag = false;
			lock (sync)
			{
				foreach (KeyValuePair<int, Snap> item in gameBoost)
				{
					if (item.Key != pass.RendererPid || item.Value.Creation != pass.RendererCreation || !string.Equals(item.Value.Name, pass.RendererName, StringComparison.OrdinalIgnoreCase))
					{
						flag = true;
						break;
					}
				}
			}
			if (flag)
			{
				UnboostGames(pass.RendererPid, pass.RendererCreation, pass.RendererName);
			}
		}

		private bool ComputeAuditDue(int pid, BoostPass pass, out bool known, out bool needTweak, out bool needPlacement)
		{
			lock (sync)
			{
				known = gameBoost.ContainsKey(pid);
				needTweak = !tweakApplied.Contains(pid);
				ulong value;
				needPlacement = pass.Exclusive && !placementGaveUp.Contains(pid) && (!gamePlacement.TryGetValue(pid, out value) || value != pass.DesiredMask);
				long value2;
				return !known || needTweak || needPlacement || !gameBoostNextAudit.TryGetValue(pid, out value2) || DateTime.UtcNow.Ticks >= value2;
			}
		}

		private IntPtr OpenBoostHandle(int pid, BoostPass pass)
		{
			IntPtr intPtr = Native.OpenProcess(12800, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				bool flag = Native.LastOpenProcessFailureWasNoSuchProcess();
				bool flag2;
				lock (sync)
				{
					flag2 = boostDenied.Add(pid);
				}
				if (flag2)
				{
					Logger.Log(Lang.T("log.gamemodeboost.3") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.4"));
				}
				if (!flag)
				{
					ProtectedGameRoster.Remember(pass.RendererName);
				}
			}
			return intPtr;
		}

		private bool VerifyRendererIdentity(IntPtr h, int pid, BoostPass pass, out long currentCreation)
		{
			string actualName = Native.ImageName(h);
			long cpu;
			ulong io;
			if (!Native.QueryProcessSample(h, out currentCreation, out cpu, out io))
			{
				Logger.Warn(Lang.T("log.gamemodeboost.5") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.6"));
				return false;
			}
			if (!RendererIdentityMatches(pass.RendererPid, pass.RendererCreation, pass.RendererName, pid, currentCreation, actualName))
			{
				Logger.Log(Lang.T("log.gamemodeboost.7") + pid + Lang.T("log.gamemodeboost.8"));
				return false;
			}
			return true;
		}

		private void HandlePidReuse(int pid, long currentCreation, ref bool known, ref bool needPlacement)
		{
			if (!known)
			{
				return;
			}
			bool flag = false;
			Snap value;
			lock (sync)
			{
				if (gameBoost.TryGetValue(pid, out value) && value.Creation > 0 && value.Creation != currentCreation)
				{
					gameBoost.Remove(pid);
					gameGpu.Remove(pid);
					gamePlacement.Remove(pid);
					boostFail.Remove(pid);
					ForgetHardPin(pid);
					boostStateWarned.Remove(pid);
					boostStateVerified.Remove(pid);
					gameBoostNextAudit.Remove(pid);
					boostStateFail.Remove(pid);
					boostHandleStripped.Remove(pid);
					boostEcoGaveUp.Remove(pid);
					placementFail.Remove(pid);
					placementGaveUp.Remove(pid);
					ForgetNvTweakLocked(pid);
					flag = true;
					known = false;
					ForgetBoostActionTarget(pid);
				}
			}
			if (flag)
			{
				needPlacement = true;
				CrashGuard.ReleaseBoostProcess(pid, value.Creation);
			}
		}

		private bool CaptureAndTrack(IntPtr h, int pid, long currentCreation, BoostPass pass, bool known, out bool newlyTracked, out bool gpuOk, bool applyGpu = true)
		{
			newlyTracked = false;
			gpuOk = false;
			pass.ActionTarget = BoostActionTarget(h);
			if (known)
			{
				lock (sync)
				{
					if (!string.IsNullOrEmpty(pass.ActionTarget))
					{
						boostActionTargets[pid] = pass.ActionTarget;
					}
				}
			}
			else
			{
				uint num = Native.GetPriorityClass(h);
				if (num == 0)
				{
					return false;
				}
				ulong num2 = Native.QueryAffinity(h);
				uint[] array = Native.QueryCpuSets(h);
				if (array == null)
				{
					Logger.Warn(Lang.T("log.gamemodeboost.9") + pass.RendererName + " pid " + pid);
					return false;
				}
				int num3 = Native.QueryIoPriority(h);
				int num4 = Native.QueryPagePriority(h);
				if (num3 < 0 || num4 < 0)
				{
					return false;
				}
				int cls;
				bool flag = Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) == 0;
				if (!flag)
				{
					cls = -1;
				}
				int controlMask;
				int stateMask;
				if (!Native.TryQueryPowerThrottling(h, out controlMask, out stateMask))
				{
					if (Native.PowerThrottlingSupported)
					{
						return false;
					}
					controlMask = -1;
					stateMask = -1;
				}
				CrashGuard.OriginalBoostState recovered;
				if (!CrashGuard.MarkBoostProcess(pid, currentCreation, pass.RendererName, num, num2, num3, num4, cls, array, controlMask, stateMask, out recovered))
				{
					Logger.Log(Lang.T("log.gamemodeboost.10") + pass.RendererName + " pid " + pid);
					return false;
				}
				if (recovered != null)
				{
					num = recovered.Priority;
					num2 = recovered.Affinity;
					num3 = recovered.Io;
					num4 = recovered.Page;
					cls = recovered.Gpu;
					flag = cls >= 0;
					array = recovered.CpuSets;
					controlMask = recovered.QoSControl;
					stateMask = recovered.QoSState;
				}
				Snap snap = default(Snap);
				snap.Pri = num;
				snap.Aff = num2;
				snap.Io = num3;
				snap.Pg = num4;
				snap.Name = pass.RendererName;
				snap.Creation = currentCreation;
				snap.CpuSets = array;
				snap.QoSControl = controlMask;
				snap.QoSState = stateMask;
				Snap value2 = snap;
				lock (sync)
				{
					gameBoost[pid] = value2;
					if (!string.IsNullOrEmpty(pass.ActionTarget))
					{
						boostActionTargets[pid] = pass.ActionTarget;
					}
					if (flag)
					{
						gameGpu[pid] = cls;
					}
				}
				newlyTracked = true;
			}
			uint granted;
			if (Native.HandleWriteAccessStripped(h, out granted))
			{
				bool flag2;
				lock (sync)
				{
					flag2 = boostHandleStripped.Add(pid);
				}
				pass.WriteDenied = true;
				if (flag2)
				{
					OnGameHandleStripped(pid, pass.RendererName, granted);
				}
			}
			if (applyGpu)
			{
				gpuOk = ApplySelectedBoostAction(h, pid, pass, "GameGpuPriority", pass.GpuBoost, (long value) => value == 4, () => WriteBoostAction("GameGpuPriority", h, 4L));
			}
			return true;
		}

		private bool ApplyBoostStateStage(IntPtr h, int pid, BoostPass pass, bool needTweak, out bool stateOk, out bool firstVerified)
		{
			firstVerified = false;
			if (pass.WriteDenied)
			{
				stateOk = false;
				lock (sync)
				{
					boostStateVerified.Remove(pid);
					gameBoostNextAudit[pid] = DateTime.UtcNow.AddSeconds(20 + Math.Abs(pid % 11)).Ticks;
				}
				return true;
			}
			bool flag = ApplySelectedBoostAction(h, pid, pass, "GamePriority", pass.CpuBoost, (long value) => value == pass.PriorityTarget, () => WriteBoostAction("GamePriority", h, pass.PriorityTarget));
			bool flag2 = ApplySelectedBoostAction(h, pid, pass, "GameIoPriority", pass.IoBoost, (long value) => value == 3, () => WriteBoostAction("GameIoPriority", h, 3L));
			bool flag3 = ApplySelectedBoostAction(h, pid, pass, "GameMemoryPriority", pass.MemoryBoost, (long value) => value >= 5, () => WriteBoostAction("GameMemoryPriority", h, 5L));
			stateOk = flag && flag2 && flag3;
			lock (sync)
			{
				if (stateOk)
				{
					firstVerified = boostStateVerified.Add(pid);
					boostStateWarned.Remove(pid);
				}
				else
				{
					boostStateVerified.Remove(pid);
				}
				boostStateFail.Remove(pid);
				gameBoostNextAudit[pid] = DateTime.UtcNow.AddSeconds(20 + Math.Abs(pid % 11)).Ticks;
			}
			return true;
		}

		private bool PlacementMatches(IntPtr h, BoostPass pass, out bool unreadable, out ulong observed)
		{
			unreadable = false;
			observed = 0uL;
			if (h == IntPtr.Zero || pass == null)
			{
				return false;
			}
			if (!pass.Exclusive && pass.DesiredMask == allMask)
			{
				return true;
			}
			if (pass.DesiredMask == 0 || (pass.DesiredMask & ~allMask) != 0 || CpuTopology.MultiGroup || pass.RendererPid <= 0 || pass.RendererCreation <= 0)
			{
				return false;
			}
			if (!Native.TryQueryAffinity(h, out observed))
			{
				observed = 0uL;
				unreadable = true;
				return false;
			}
			return observed == pass.DesiredMask;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern uint GetProcessId(IntPtr process);

		private static IntPtr DuplicateRestoreHandle(IntPtr source)
		{
			if (source == IntPtr.Zero)
			{
				return IntPtr.Zero;
			}
			IntPtr currentProcess = Native.GetCurrentProcess();
			IntPtr targetHandle;
			if (!DuplicateHandle(currentProcess, source, currentProcess, out targetHandle, 0u, false, 2u))
			{
				return IntPtr.Zero;
			}
			return targetHandle;
		}

		private static HardPinHandleState HardPinHandleStateOf(HardPin pin)
		{
			if (pin == null || pin.RestoreHandle == IntPtr.Zero)
			{
				return HardPinHandleState.Unknown;
			}
			uint processId = GetProcessId(pin.RestoreHandle);
			if (processId != 0 && processId != (uint)pin.Pid)
			{
				return HardPinHandleState.Mismatch;
			}
			if (!Native.StillActive(pin.RestoreHandle))
			{
				return HardPinHandleState.Gone;
			}
			long creation;
			long cpu;
			ulong io;
			if (!Native.QueryProcessSample(pin.RestoreHandle, out creation, out cpu, out io))
			{
				return HardPinHandleState.Unknown;
			}
			if (processId == 0 || creation != pin.Creation)
			{
				return HardPinHandleState.Mismatch;
			}
			return HardPinHandleState.Match;
		}

		private bool RememberHardPin(int pid, long creation, ulong originalAffinity, ulong appliedAffinity, IntPtr restoreHandle)
		{
			if (pid <= 0 || creation <= 0 || originalAffinity == 0 || restoreHandle == IntPtr.Zero)
			{
				return false;
			}
			lock (sync)
			{
				HardPin value;
				if (hardPins.TryGetValue(pid, out value))
				{
					if (value.RestoreRequested || value.RestoreInFlight || (value.Creation != creation && coreAffinityJobs.HasLease(pid, value.Creation)))
					{
						RequestHardPinRestoreLocked(value);
						return false;
					}
					if (value.Creation == creation && value.RestoreHandle != IntPtr.Zero)
					{
						Native.CloseHandle(restoreHandle);
						return true;
					}
					hardPins.Remove(pid);
					if (value.RestoreHandle != IntPtr.Zero)
					{
						Native.CloseHandle(value.RestoreHandle);
					}
				}
				hardPins[pid] = new HardPin
				{
					Pid = pid,
					Creation = creation,
					OriginalAffinity = originalAffinity,
					AppliedAffinity = appliedAffinity,
					RestoreHandle = restoreHandle
				};
				return true;
			}
		}

		private void ForgetHardPin(int pid)
		{
			bool restore = false;
			lock (sync)
			{
				HardPin value;
				if (hardPins.TryGetValue(pid, out value))
				{
					if (value.RestoreInFlight || value.RestoreRequested || coreAffinityJobs.HasLease(pid, value.Creation))
					{
						RequestHardPinRestoreLocked(value);
						restore = true;
					}
					else
					{
						hardPins.Remove(pid);
						if (value.RestoreHandle != IntPtr.Zero) Native.CloseHandle(value.RestoreHandle);
					}
				}
			}
			if (restore && !Monitor.IsEntered(sync)) RestoreHardPin(pid);
		}

		private bool RestoreHardPin(int pid, HardPin expected = null)
		{
			// Callers holding sync defer cleanup to the existing worker; never carry sync into guardian IPC.
			if (Monitor.IsEntered(sync))
			{
				HardPin queued;
				if (!hardPins.TryGetValue(pid, out queued)) return true;
				if (expected != null && !object.ReferenceEquals(queued, expected)) return true;
				RequestHardPinRestoreLocked(queued);
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			if (!Monitor.TryEnter(criticalProcessGate, 0))
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			try
			{
				HardPin value;
				lock (sync)
				{
					if (!hardPins.TryGetValue(pid, out value))
					{
						return true;
					}
					if (expected != null && !object.ReferenceEquals(value, expected)) return true;
					RequestHardPinRestoreLocked(value, false);
					bool targetGone = Native.WaitForSingleObject(value.RestoreHandle, 0) == 0;
					if (targetGone && !value.RestoreExitPhase)
					{
						value.RestoreExitPhase = true;
						value.RestoreAttempts = 0;
						value.NextRestore = 0;
					}
					if (value.RestoreAttempts >= 3)
					{
						RecoveryAttemptTrace.Exhausted(value.RestoreExitPhase ? (Func<bool>)null : () => Native.WaitForSingleObject(value.RestoreHandle, 0) == 0);
						return false;
					}
					if (AutoCores.NowTicks < value.NextRestore)
					{
						RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(value.NextRestore - AutoCores.NowTicks));
						return false;
					}
					value.RestoreAttempts++;
					value.RestoreInFlight = true;
				}
				bool restored = false;
				try
				{
					if (!PrepareCriticalThreadProcessMaskMutation(pid, value.Creation)) return false;
					restored = RestoreHardPinLocked(pid);
					return restored;
				}
				finally
				{
					lock (sync)
					{
						HardPin current;
						if (hardPins.TryGetValue(pid, out current) && object.ReferenceEquals(value, current))
						{
							current.RestoreInFlight = false;
							if (!restored)
							{
								current.RestoreDelay = current.RestoreDelay == 0 ? 500 : Math.Min(30000, current.RestoreDelay * 2);
								current.NextRestore = AutoCores.NowTicks + TimeSpan.FromMilliseconds(current.RestoreDelay).Ticks;
								current.RestoreTargetGone = Native.WaitForSingleObject(current.RestoreHandle, 0) == 0;
								if (current.RestoreAttempts >= 3)
									RecoveryAttemptTrace.Exhausted(current.RestoreExitPhase ? (Func<bool>)null : () => Native.WaitForSingleObject(current.RestoreHandle, 0) == 0);
								else RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(current.NextRestore - AutoCores.NowTicks));
								Logger.Warn("Hard affinity recovery: target=" + pid + "/" + current.Creation
									+ " attempt=" + current.RestoreAttempts + "/3; "
									+ (current.RestoreAttempts >= 3 ? "active retries stopped; receipt and handle retained" : "restoration pending"));
							}
						}
					}
				}
			}
			finally
			{
				Monitor.Exit(criticalProcessGate);
			}
		}

		private bool RestoreHardPinLocked(int pid)
		{
			if (!Monitor.IsEntered(criticalProcessGate) || Monitor.IsEntered(sync)) return false;
			HardPin value;
			lock (sync)
			{
				if (!hardPins.TryGetValue(pid, out value))
				{
					return true;
				}
				if (!value.RestoreInFlight) return false;
			}
			string jobReason;
			if (!coreAffinityJobs.Release(pid, value.Creation, out jobReason))
			{
				lock (sync) corePlacementEvidence = "pid=" + pid + " stage=job-release reason=" + jobReason;
				return false;
			}
			HardPinHandleState state = HardPinHandleStateOf(value);
			if (state == HardPinHandleState.Match)
			{
				if (!value.JobRecoveryOnly && !CrashGuard.RestoreOwnedAffinity(value.RestoreHandle, value.OriginalAffinity, value.AppliedAffinity)) return false;
				if (!CrashGuard.TrackBoostAffinity(pid, value.Creation, 0uL)) return false;
			}
			else if (state != HardPinHandleState.Gone && state != HardPinHandleState.Mismatch) return false;
			lock (sync)
			{
				HardPin current;
				if (!hardPins.TryGetValue(pid, out current) || !object.ReferenceEquals(value, current)) return false;
				hardPins.Remove(pid);
				gamePlacement.Remove(pid);
				if (value.RestoreHandle != IntPtr.Zero) Native.CloseHandle(value.RestoreHandle);
			}
			return true;
		}

		private bool RestoreAllHardPins()
		{
			return RestoreHardPinsExcept(0, 0L);
		}

		private bool RestoreHardPinsExcept(int keepPid, long keepCreation)
		{
			List<int> list = new List<int>();
			lock (sync)
			{
				foreach (KeyValuePair<int, HardPin> hardPin in hardPins)
				{
					if (keepPid <= 0 || hardPin.Key != keepPid || hardPin.Value.Creation != keepCreation)
					{
						list.Add(hardPin.Key);
					}
				}
			}
			bool result = true;
			foreach (int item in list)
			{
				if (!RestoreHardPin(item))
				{
					result = false;
				}
			}
			string jobReason;
			if (!result || Monitor.IsEntered(sync) || !Monitor.TryEnter(criticalProcessGate, 0)) return false;
			try { return coreAffinityJobs.ReleaseExcept(keepPid, keepCreation, out jobReason); }
			finally { Monitor.Exit(criticalProcessGate); }
		}

		private static int BoostActionIdentity(IntPtr handle, long creation)
		{
			return Native.QueryProcessIdentityState(handle, creation);
		}

		private static string BoostActionTarget(IntPtr handle)
		{
			string text = Native.ImagePath(handle);
			try
			{
				return (string.IsNullOrEmpty(text) || !Path.IsPathRooted(text)) ? null : Path.GetFullPath(text);
			}
			catch
			{
				return null;
			}
		}

		private static long QosValue(int control, int state)
		{
			return (long)(((ulong)(uint)control << 32) | (uint)state);
		}

		private static long? ReadBoostAction(string action, IntPtr handle)
		{
			switch (action)
			{
			case "GamePriority":
			{
				uint priorityClass = Native.GetPriorityClass(handle);
				if (priorityClass != 0)
				{
					return priorityClass;
				}
				return null;
			}
			case "GameIoPriority":
			{
				int num2 = Native.QueryIoPriority(handle);
				if (num2 >= 0)
				{
					return num2;
				}
				return null;
			}
			case "GameMemoryPriority":
			{
				int num = Native.QueryPagePriority(handle);
				if (num >= 1)
				{
					return num;
				}
				return null;
			}
			case "GameGpuPriority":
			{
				int cls;
				if (Native.D3DKMTGetProcessSchedulingPriorityClass(handle, out cls) != 0)
				{
					return null;
				}
				return cls;
			}
			case "GameEcoQoS":
			{
				int controlMask;
				int stateMask;
				if (!Native.PowerThrottlingSupported || !Native.TryQueryPowerThrottling(handle, out controlMask, out stateMask))
				{
					return null;
				}
				return QosValue(controlMask, stateMask);
			}
			default:
				return null;
			}
		}

		private static int WriteBoostAction(string action, IntPtr handle, long value)
		{
			switch (action)
			{
			case "GamePriority":
				if (!Native.SetPriorityClass(handle, (uint)value))
				{
					return Marshal.GetLastWin32Error();
				}
				return 0;
			case "GameIoPriority":
			{
				if (value == 3 && !Native.EnsureBoostPrivilege())
				{
					return 1314;
				}
				int status2;
				if (!Native.TrySetIoPriority(handle, (int)value, out status2))
				{
					return status2;
				}
				return 0;
			}
			case "GameGpuPriority":
				return Native.D3DKMTSetProcessSchedulingPriorityClass(handle, (int)value);
			case "GameMemoryPriority":
			{
				int status;
				if (!Native.TrySetPagePriority(handle, (int)value, out status))
				{
					return status;
				}
				return 0;
			}
			case "GameEcoQoS":
				if (!Native.RestorePowerThrottling(handle, (int)(value >> 32), (int)value))
				{
					return Marshal.GetLastWin32Error();
				}
				return 0;
			default:
				return 50;
			}
		}

		private static int WriteHighQoSBoost(IntPtr handle)
		{
			bool timerExemptDropped;
			bool flag = Native.ApplyHighQoS(handle, Native.TimerExemptWanted, out timerExemptDropped);
			if (timerExemptDropped)
			{
				Logger.Log(Lang.T("log.gamemodeboost.59"));
			}
			if (!flag)
			{
				return Marshal.GetLastWin32Error();
			}
			return 0;
		}

		private static bool BoostActionSkipStatus(int status)
		{
			if (status != 5 && status != 50 && status != 87 && status != 120 && status != 127 && status != 1314 && status != -1073741790 && status != -1073741637 && status != -1073741821)
			{
				return status == -1073741811;
			}
			return true;
		}

		private bool TryOriginalBoostAction(int pid, string action, out long original)
		{
			original = 0L;
			lock (sync)
			{
				Snap value;
				if (!gameBoost.TryGetValue(pid, out value))
				{
					return false;
				}
				switch (action)
				{
				case "GamePriority":
					original = value.Pri;
					return value.Pri != 0 && value.Pri != uint.MaxValue;
				case "GameIoPriority":
					original = value.Io;
					return value.Io >= 0;
				case "GameMemoryPriority":
					original = value.Pg;
					return value.Pg >= 1;
				case "GameGpuPriority":
				{
					int value2;
					if (!gameGpu.TryGetValue(pid, out value2) || value2 < 0)
					{
						return false;
					}
					original = value2;
					return true;
				}
				case "GameEcoQoS":
					original = QosValue(value.QoSControl, value.QoSState);
					return value.QoSControl >= 0 && value.QoSState >= 0;
				default:
					return false;
				}
			}
		}

		private void SetBoostActionRecovery(int pid, string action, bool pending)
		{
			lock (sync)
			{
				HashSet<string> value;
				if (!boostActionRestorePending.TryGetValue(pid, out value))
				{
					if (!pending)
					{
						return;
					}
					value = (boostActionRestorePending[pid] = new HashSet<string>(StringComparer.Ordinal));
				}
				if (pending)
				{
					value.Add(action);
					return;
				}
				value.Remove(action);
				if (value.Count == 0)
				{
					boostActionRestorePending.Remove(pid);
				}
			}
		}

		private bool RestoreBoostAction(IntPtr handle, int pid, long creation, string action)
		{
			return RunBoostRecovery(pid, creation, action, () => RestoreBoostActionCore(handle, pid, creation, action));
		}

		private bool RestoreBoostActionCore(IntPtr handle, int pid, long creation, string action)
		{
			try
			{
				switch (BoostActionIdentity(handle, creation))
				{
				case 1:
					SetBoostActionRecovery(pid, action, false);
					return true;
				default:
					return false;
				case 0:
				{
					if (action == "GamePriority")
					{
						bool restored = CrashGuard.RestoreBoostPriority(handle, pid, creation);
						if (restored) SetBoostActionRecovery(pid, action, false);
						return restored;
					}
					long original;
					if (!TryOriginalBoostAction(pid, action, out original))
					{
						return false;
					}
					long? num = ReadBoostAction(action, handle);
					if (!num.HasValue)
					{
						return false;
					}
					bool flag = num.Value == original || WriteBoostAction(action, handle, original) == 0;
					long? num2 = ReadBoostAction(action, handle);
					int num3 = BoostActionIdentity(handle, creation);
					bool flag2 = num3 == 1 || (num3 == 0 && flag && num2.HasValue && num2.Value == original);
					if (flag2)
					{
						SetBoostActionRecovery(pid, action, false);
					}
					return flag2;
				}
				}
			}
			catch
			{
				return false;
			}
		}

		private bool RestoreBoostActionTarget(IntPtr currentHandle, int currentPid, string target, string action)
		{
			List<KeyValuePair<int, Snap>> list = new List<KeyValuePair<int, Snap>>();
			lock (sync)
			{
				foreach (KeyValuePair<int, Snap> item in gameBoost)
				{
					string value;
					if (boostActionTargets.TryGetValue(item.Key, out value) && string.Equals(value, target, StringComparison.OrdinalIgnoreCase))
					{
						list.Add(item);
					}
				}
			}
			foreach (KeyValuePair<int, Snap> item2 in list)
			{
				bool restored = RunBoostRecovery(item2.Key, item2.Value.Creation, "target:" + action, delegate
				{
				IntPtr intPtr = ((item2.Key == currentPid) ? currentHandle : Native.OpenProcess(12800, false, item2.Key));
				if (intPtr == IntPtr.Zero)
				{
					if (ProbeBoostTarget(item2.Key, item2.Value.Creation) != BoostTarget.Gone)
					{
						return false;
					}
					return true;
				}
				try
				{
					if (BoostActionIdentity(intPtr, item2.Value.Creation) == 0 && !string.Equals(BoostActionTarget(intPtr), target, StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					if (!RestoreBoostAction(intPtr, item2.Key, item2.Value.Creation, action))
					{
						return false;
					}
				}
				finally
				{
					if (item2.Key != currentPid)
					{
						Native.CloseHandle(intPtr);
					}
				}
				return true;
				});
				if (!restored) return false;
			}
			return true;
		}

		private bool ApplyIsolatedBoostAction(IntPtr handle, int pid, BoostPass pass, string action, Func<long, bool> desired, Func<int> write)
		{
			if (action == "GamePriority")
			{
				Func<int> requestedWrite = write;
				write = () => WriteOwnedGamePriority(handle, pid, pass, requestedWrite);
			}
			string actionTarget = pass.ActionTarget;
			if (string.IsNullOrEmpty(actionTarget) || pass.WriteDenied || stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration || BoostActionIdentity(handle, pass.RendererCreation) != 0 || !string.Equals(BoostActionTarget(handle), actionTarget, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			ActionFailureStore.BeginAutomaticCycle(action, actionTarget, AutomaticPhaseKey(action));
			lock (sync)
			{
				boostActionTargets[pid] = actionTarget;
			}
			long original;
			if (!TryOriginalBoostAction(pid, action, out original))
			{
				return false;
			}
			long? num = ReadBoostAction(action, handle);
			if (!num.HasValue)
			{
				return false;
			}
			bool flag;
			lock (sync)
			{
				HashSet<string> value;
				flag = boostActionRestorePending.TryGetValue(pid, out value) && value.Contains(action);
			}
			if (flag)
			{
				if (!RestoreBoostActionTarget(handle, pid, actionTarget, action))
				{
					return false;
				}
				if (flag)
				{
					ActionFailureStore.CancelApply(action, actionTarget);
				}
				num = ReadBoostAction(action, handle);
				if (!num.HasValue)
				{
					return false;
				}
			}
			if (ActionFailureStore.IsBlocked(action, actionTarget))
			{
				return false;
			}
			if (desired(num.Value))
			{
				return true;
			}
			string reason;
			if (!ActionFailureStore.TryBeginApply(action, actionTarget, out reason))
			{
				return false;
			}
			bool flag3 = false;
			try
			{
				num = ReadBoostAction(action, handle);
				if (!num.HasValue || pass.WriteDenied || stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration || BoostActionIdentity(handle, pass.RendererCreation) != 0)
				{
					ActionFailureStore.CancelApply(action, actionTarget);
					return false;
				}
				int num2 = 0;
				if (!desired(num.Value))
				{
					flag3 = true;
					SetBoostActionRecovery(pid, action, true);
					num2 = write();
				}
				switch (BoostActionIdentity(handle, pass.RendererCreation))
				{
				case 1:
					SetBoostActionRecovery(pid, action, false);
					ActionFailureStore.CancelApply(action, actionTarget);
					return false;
				default:
					if (!flag3)
					{
						ActionFailureStore.CancelApply(action, actionTarget);
					}
					else
					{
						ActionFailureStore.Trip(action, actionTarget, "Game action completion identity unreadable; recovery pending");
					}
					return false;
				case 0:
				{
					if (stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration)
					{
						if (!flag3 || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
						{
							ActionFailureStore.CancelApply(action, actionTarget);
						}
						else
						{
							ActionFailureStore.Trip(action, actionTarget, "Cancelled game action recovery remains incomplete");
						}
						return false;
					}
					long? num3 = ReadBoostAction(action, handle);
					if (num2 == 0 && num3.HasValue && desired(num3.Value))
					{
						if (ActionFailureStore.CompleteApply(action, actionTarget))
						{
							SetBoostActionRecovery(pid, action, false);
							return true;
						}
						if (RestoreBoostAction(handle, pid, pass.RendererCreation, action))
						{
							ActionFailureStore.CancelApply(action, actionTarget);
						}
						return false;
					}
					bool flag4 = RestoreBoostAction(handle, pid, pass.RendererCreation, action);
					if (BoostActionSkipStatus(num2) || (action == "GamePriority" && num2 == 1237))
					{
						if (flag4)
						{
							ActionFailureStore.CancelApply(action, actionTarget);
						}
						else
						{
							ActionFailureStore.Trip(action, actionTarget, "Skipped game action still requires verified recovery");
						}
						return false;
					}
					reason = "Game action apply/readback failed; native status " + num2 + (flag4 ? "; original restored" : "; recovery pending");
					ActionFailureStore.Trip(action, actionTarget, reason);
					Logger.Warn(action + " | " + actionTarget + " | " + reason);
					return false;
				}
				}
			}
			catch (NotSupportedException)
			{
				if (!flag3 || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
				{
					ActionFailureStore.CancelApply(action, actionTarget);
				}
				else
				{
					ActionFailureStore.Trip(action, actionTarget, "Unsupported game action recovery remains incomplete");
				}
				return false;
			}
			catch (OperationCanceledException)
			{
				if (!flag3 || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
				{
					ActionFailureStore.CancelApply(action, actionTarget);
				}
				else
				{
					ActionFailureStore.Trip(action, actionTarget, "Cancelled game action recovery remains incomplete");
				}
				return false;
			}
			catch (Exception ex3)
			{
				if (!flag3)
				{
					ActionFailureStore.CancelApply(action, actionTarget);
					return false;
				}
				if (flag3)
				{
					RestoreBoostAction(handle, pid, pass.RendererCreation, action);
				}
				ActionFailureStore.Trip(action, actionTarget, ex3.GetType().Name + ": " + ex3.Message);
				return false;
			}
		}

		private void ForgetBoostActionTarget(int pid)
		{
			lock (sync)
			{
				boostActionTargets.Remove(pid);
				boostActionRestorePending.Remove(pid);
				boostResourceSelections.Remove(pid);
			}
		}

		private bool RestoreBoostSnapshot(IntPtr handle, int pid, Snap snapshot)
		{
			bool flag = snapshot.Pri != 0 && snapshot.Pri != uint.MaxValue && snapshot.Io >= 0 && (!Native.PowerThrottlingSupported || (snapshot.QoSControl >= 0 && snapshot.QoSState >= 0));
			string[] boostActions = BoostActions;
			foreach (string text in boostActions)
			{
				long original;
				if (TryOriginalBoostAction(pid, text, out original))
				{
					bool flag2 = RestoreBoostAction(handle, pid, snapshot.Creation, text);
					flag = flag && flag2;
					if (text != "GameEcoQoS")
					{
						RecordAutomation(ResourcePriorityKey(text), flag2 ? "restored" : "restore-failed");
					}
					string value;
					lock (sync)
					{
						boostActionTargets.TryGetValue(pid, out value);
					}
					if (flag2 && !string.IsNullOrEmpty(value))
					{
						ActionFailureStore.CancelApply(text, value);
					}
				}
			}
			return flag;
		}

		private void RunIndependentCorePlacement(BoostPass pass)
		{
			long nowTicks = AutoCores.NowTicks;
			try
			{
				RunIndependentCorePlacementImpl(pass);
			}
			finally
			{
				coreBudget.Record(nowTicks);
			}
		}

		private void RunIndependentCorePlacementImpl(BoostPass pass)
		{
			if (coreManual == null || pass == null || !pass.Exclusive || CorePending || stopping)
			{
				return;
			}
			IntPtr intPtr = IntPtr.Zero;
			try
			{
				ulong observed;
				bool unreadable;
				if (ExclusiveDrifted(pass.RendererPid, pass, out observed, out unreadable))
				{
					if (!unreadable)
					{
						corePlacementEvidence = "pid=" + pass.RendererPid + " creation=" + pass.RendererCreation + " stage=audit-mismatch actual=0x" + observed.ToString("X") + " target=0x" + pass.DesiredMask.ToString("X");
					}
					if (unreadable)
					{
						if (!exclusiveAuditDeferred) BlockAutoCores("schedule.auto.affinityread");
					}
					else
					{
						HoldExclusive(pass.RendererPid, pass, observed);
					}
				}
				else
				{
					if (coreEngaged)
					{
						return;
					}
					if (RendererForegroundPid() != pass.RendererPid)
					{
						if (coreGovernor != null)
						{
							ApplyAutoCoreStep(coreGovernor.Pause());
						}
						return;
					}
					intPtr = Native.OpenAffinityProcess(pass.RendererPid);
					int error = ((intPtr == IntPtr.Zero) ? Marshal.GetLastWin32Error() : 0);
					if (intPtr == IntPtr.Zero)
					{
						LogCorePlacement(pass, "open", error, intPtr);
						BlockAutoCores("schedule.auto.placementunavailable");
						return;
					}
					long currentCreation;
					if (!VerifyRendererIdentity(intPtr, pass.RendererPid, pass, out currentCreation))
					{
						LogCorePlacement(pass, "identity", 0, intPtr);
						BlockAutoCores("schedule.auto.identity");
						return;
					}
					uint granted;
					if (Native.TryQueryGrantedAccess(intPtr, out granted) && (granted & 0x200) == 0)
					{
						LogCorePlacement(pass, "access", 5, intPtr);
						BlockAutoCores("schedule.auto.placementunavailable");
						return;
					}
					bool needPlacement = true;
					bool known;
					lock (sync)
					{
						known = gameBoost.ContainsKey(pass.RendererPid);
					}
					HandlePidReuse(pass.RendererPid, currentCreation, ref known, ref needPlacement);
					bool newlyTracked;
					bool gpuOk;
					if (!CaptureAndTrack(intPtr, pass.RendererPid, currentCreation, pass, known, out newlyTracked, out gpuOk, false))
					{
						LogCorePlacement(pass, "snapshot", 0, intPtr);
						BlockAutoCores("schedule.auto.capture");
						return;
					}
					string placementText;
					bool placementVerified;
					if (!ApplyPlacementStage(intPtr, pass.RendererPid, pass, true, newlyTracked, out placementText, out placementVerified))
					{
						BlockAutoCores("schedule.auto.capture");
					}
					if (placementVerified)
					{
						LogCorePlacement(pass, "verified", 0, intPtr);
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Warn("AutoCore placement exception pid=" + pass.RendererPid + " type=" + ex.GetType().Name);
				BlockAutoCores("schedule.auto.placement");
			}
			finally
			{
				if (intPtr != IntPtr.Zero)
				{
					Native.CloseHandle(intPtr);
				}
				pass.Exclusive = coreGovernor != null && coreGovernor.Exclusive && !CorePending;
				if (!pass.Exclusive)
				{
					pass.DesiredMask = allMask;
				}
				PublishAutoCores(pass.RendererName);
			}
		}

		private void LogCorePlacement(BoostPass pass, string stage, int error, IntPtr handle)
		{
			pass.PlacementStage = stage;
			pass.PlacementError = error;
			uint granted;
			string text = (Native.TryQueryGrantedAccess(handle, out granted) ? ("0x" + granted.ToString("X")) : "unknown");
			ulong mask;
			string text2 = ((handle != IntPtr.Zero && Native.TryQueryAffinity(handle, out mask)) ? ("0x" + mask.ToString("X")) : "unknown");
			ulong num = 0uL;
			lock (sync)
			{
				Snap value;
				if (gameBoost.TryGetValue(pass.RendererPid, out value))
				{
					num = value.Aff;
				}
			}
			corePlacementEvidence = "pid=" + pass.RendererPid + " creation=" + pass.RendererCreation + " stage=" + stage + " win32=" + error + " granted=" + text + " original=0x" + num.ToString("X") + " actual=" + text2 + " target=0x" + pass.DesiredMask.ToString("X") + " cachedBoostRestriction=" + pass.WriteDenied;
		}

		private static string PlacementFailureReason(BoostPass pass)
		{
			switch (pass.PlacementStage)
			{
			case "affinity-range":
				return "schedule.auto.affinityrange";
			case "journal":
				return "schedule.auto.capture";
			case "restore-handle":
				return "schedule.auto.restorehandle";
			case "read-original":
			case "read-placement":
			case "readback":
				return "schedule.auto.affinityread";
			case "set-affinity":
				return "schedule.auto.affinitywrite";
			default:
				return "schedule.auto.placement";
			}
		}

		private bool ApplyPlacementStage(IntPtr h, int pid, BoostPass pass, bool needPlacement, bool newlyTracked, out string placementText, out bool placementVerified)
		{
			placementText = "";
			placementVerified = false;
			bool unreadable = false;
			ulong observed = 0uL;
			if (!needPlacement)
			{
				placementVerified = PlacementMatches(h, pass, out unreadable, out observed);
				if (pass.Exclusive && !placementVerified && !unreadable)
				{
					bool flag;
					lock (sync)
					{
						flag = !placementGaveUp.Contains(pid) && gameBoost.ContainsKey(pid);
					}
					if (flag)
					{
						placementVerified = HoldExclusive(pid, pass, observed);
					}
				}
				return true;
			}
			if (pass.Exclusive && RendererForegroundPid() != pid)
			{
				if (coreGovernor != null)
				{
					ApplyAutoCoreStep(coreGovernor.Pause());
				}
				PublishAutoCores(pass.RendererName);
				return true;
			}
			Snap value;
			lock (sync)
			{
				if (!gameBoost.TryGetValue(pid, out value))
				{
					return false;
				}
			}
			if (pass.Exclusive)
			{
				bool unreadable2;
				bool flag2 = PlacementChangedOutside(h, value, out observed, out unreadable2);
				if (unreadable2)
				{
					LogCorePlacement(pass, "read-placement", 0, h);
					BlockAutoCores("schedule.auto.affinityread");
					return true;
				}
				if (flag2)
				{
					YieldExclusive(pid, observed);
					return true;
				}
			}
			bool flag3 = true;
			if (pass.Exclusive)
			{
				bool flag4 = flag3 && PinExclusive(h, pid, pass, value);
				if (!flag4 && pass.PlacementStage == "set-affinity" && pass.PlacementError == 5)
				{
					if (!Native.TryQueryAffinity(h, out observed) || observed != value.Aff)
					{
						BlockAutoCores("schedule.auto.affinityread");
					}
					else if (!CrashGuard.TrackBoostAffinity(pid, value.Creation, 0uL))
					{
						BlockAutoCores("schedule.auto.capture");
					}
					else
					{
						BlockAutoCores("schedule.auto.placementunavailable");
					}
					return true;
				}
				placementVerified = flag4 && PlacementMatches(h, pass, out unreadable, out observed);
				string text = ((!placementVerified) ? PlacementFailureReason(pass) : ((!EngageIsolation(pid, pass)) ? "schedule.auto.failed" : null));
				if (text != null)
				{
					placementVerified = false;
					BlockAutoCores(text);
					if (coreGovernor == null)
					{
						ReleaseExclusive();
					}
					return true;
				}
				placementVerified = PlacementMatches(h, pass, out unreadable, out observed);
				RecordCoreAffinity(pass.DesiredMask, observed, !unreadable);
				if (!placementVerified && !unreadable && WriteBackExclusive(pid, pass) == ExclusiveWriteBack.Written)
				{
					placementVerified = true;
				}
				if (!placementVerified)
				{
					if (unreadable)
					{
						BlockAutoCores("schedule.auto.affinityread");
					}
					else
					{
						YieldExclusive(pid, observed);
					}
					return true;
				}
				placementText = Lang.T("t.gamemodeboost.18") + CpuTopology.CountSetBits(pass.DesiredMask) + Lang.T("t.gamemodeboost.16");
			}
			else
			{
				placementText = Lang.T("t.gamemodeboost.17");
				placementVerified = PlacementMatches(h, pass, out unreadable, out observed);
				if (!placementVerified)
				{
					flag3 = false;
				}
			}
			int value2 = 0;
			bool flag5 = false;
			bool flag6 = false;
			lock (sync)
			{
				if (flag3)
				{
					gamePlacement[pid] = pass.DesiredMask;
					placementFail.Remove(pid);
					placementGaveUp.Remove(pid);
				}
				else
				{
					gamePlacement.Remove(pid);
					placementFail.TryGetValue(pid, out value2);
					value2++;
					if (value2 >= 3)
					{
						placementFail.Remove(pid);
						flag5 = placementGaveUp.Add(pid);
					}
					else
					{
						placementFail[pid] = value2;
						flag6 = value2 == 1;
					}
				}
			}
			if (flag5 || (!flag3 && flag6))
			{
				Logger.Log(PlacementWarningLine(pass.RendererName, pid, unreadable, flag5));
			}
			if (!newlyTracked && flag3)
			{
				Logger.Log(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid + placementText);
			}
			if (pass.Exclusive && placementVerified && flag3)
			{
				Logger.Info("GAME placement verified pid=" + pid + " birth=" + pass.RendererCreation
					+ " mask=0x" + pass.DesiredMask.ToString("X")
					+ " source=" + (coreManual != null ? "manual" : "auto") + " name=" + pass.RendererName);
			}
			return true;
		}

		private bool ClearEfficiencyMode(IntPtr h, int pid, BoostPass pass)
		{
			if (!Native.PowerThrottlingSupported)
			{
				return true;
			}
			return ApplyIsolatedBoostAction(h, pid, pass, "GameEcoQoS", (long value) => Native.HighQoSMasksOk((int)(value >> 32), (int)value), () => WriteHighQoSBoost(h));
		}

		private void EngageLaneAndReport(IntPtr h, int pid, long currentCreation, BoostPass pass, bool stateOk, bool firstVerified, bool gpuOk, bool ecoCleared, string placementText, bool placementVerified)
		{
			if (pass.LaneAllowed && pass.LaneRestorationReady && EffLane && LaneEligible && pass.PriorityTarget == 128 && !pass.WriteDenied && !RenderLane.IsActiveFor(pid, currentCreation))
			{
				RenderLane.EnsureForGame(pid, currentCreation, pass.RendererName,
					() => ThreadBoostRecoveryAllowed(pid, currentCreation));
			}
			if (stateOk && firstVerified)
			{
				long num = Interlocked.Exchange(ref boostFirstStampTicks, 0L);
				if (num != 0)
				{
					Logger.Log(Lang.T("log.gamemodeboost.60") + (DateTime.UtcNow.Ticks - num) / 10000 + Lang.T("log.gamemodeboost.61"));
				}
				Logger.Log(BoostSuccessLine(pass.RendererName, pid, (!pass.CpuBoost) ? "" : ((pass.PriorityTarget == 128) ? Lang.T("log.gamemodeboost.28") : Lang.T("log.gamemodeboost.29")), placementText, placementVerified, (pass.IoBoost ? Lang.T("log.gamemodeboost.30") : "") + (pass.MemoryBoost ? (" " + Lang.T("gm.boost.memory")) : "") + ((gpuOk && pass.GpuBoost) ? Lang.T("log.gamemodeboost.31") : "") + ((!Native.PowerThrottlingSupported) ? "" : (ecoCleared ? Lang.T("t.gamemodeboost.32") : EcoStateText(h)))));
			}
		}

		internal static string BoostSuccessLine(string renderer, int pid, string priority, string placement, bool placementVerified, string otherStates)
		{
			return "PASS " + Lang.T("log.gamemodeboost.27") + renderer + "(pid " + pid + ") " + priority + (placementVerified ? placement : "") + otherStates;
		}

		internal static string PlacementWarningLine(string renderer, int pid, bool unreadable, bool gaveUp)
		{
			return "WARN " + Lang.T("log.gamemodeboost.23") + renderer + " pid " + pid + " " + Lang.T(unreadable ? "log.placement.unreadable" : "log.placement.unconfirmed") + Lang.T(gaveUp ? "log.placement.stopped" : "log.gamemodeboost.24");
		}

		private void ForgetNvTweaksLocked()
		{
			tweakApplied.Clear();
			nvTweakGeneration++;
		}

		private void ForgetNvTweakLocked(int pid)
		{
			tweakApplied.Remove(pid);
			nvTweakGeneration++;
		}

        private void ApplyGameTweaks(IntPtr h, int pid, BoostPass pass, bool needTweak)
        {
            if (!needTweak) return;
            NvGamePlan plan = NvGamePlan.ForAutomatic(pass.NvMaxPerf, pass.NvShader, Native.OnAcPower(), pass.NvLowLat, pass.NvVrr, pass.NvRebar);
            string targetPath;
            lock (sync) targetPath = activeDetection == null ? pass.ActionTarget : activeDetection.RendererPath;
            NvSessionAttempt attempt = BeginNvSessionAttempt(targetPath ?? pass.RendererName, pid, pass.RendererCreation, plan.AutomaticKey);
            if (attempt == null) return;
            try
            {
                string imagePath = Native.ImagePath(h);
                if (string.IsNullOrEmpty(imagePath)) { CompleteNvSessionAttempt(attempt); return; }
                QueueNvSessionPlan(imagePath, plan, attempt);
            }
            catch (Exception error)
            {
                CompleteNvSessionAttempt(attempt);
                Logger.RepeatedError("nv-session-prepare", "NVIDIA game profile preparation failed: " + error.Message, -1L);
            }
        }

        private void ApplySessionNvPlan(string imagePath, NvGamePlan nvPlan, NvSessionAttempt attempt)
        {
            attempt.Failed = false;
            try
            {
                lock (driverStageGate)
                {
                    lock (sync)
                    {
                        if (gameSessionStarts != attempt.Session
                            || !NvTweakTargetCurrentLocked(attempt.Pid, attempt.Birth, attempt.Generation)) return;
                    }
                    NvDrsTweaks.BeginAutomaticSession(attempt.Session);
                    string oldReceipt;
                    if (nvPlan.Empty && Settings.TryLoadStr("NvDrs_" + Path.GetFileName(imagePath), out oldReceipt) && oldReceipt.Length == 0)
                    {
                        attempt.Processed = true;
                        return;
                    }
                    attempt.Failed = true;
                    bool retry;
                    List<string> failed = ApplyNvGamePlan(imagePath, nvPlan, out retry, -1L);
                    if (!nvPlan.Empty) HandleNvTweakOutcome(failed, nvPlan);
                    attempt.Processed = true;
                    attempt.Failed = retry || (failed != null && failed.Count > 0);
                    attempt.Satisfied = !attempt.Failed && failed != null && failed.Count == 0 && NvDrsTweaks.PlanSatisfied(imagePath, nvPlan);
                }
            }
            catch (Exception error)
            {
                attempt.Failed = true;
                Logger.RepeatedError("nv-session-apply", "NVIDIA game profile apply failed: " + error.Message, -1L);
            }
        }

		private bool NvTweakTargetCurrentLocked(int pid, long creation, long generation)
		{
			if (active && enabled && !stopping && !panicReq && generation == nvTweakGeneration && activeDetection != null && activeDetection.RendererPid == pid)
			{
				return activeDetection.RendererCreation == creation;
			}
			return false;
		}

		private bool UnboostGames()
		{
			return UnboostGames(0, 0L, null);
		}

		private static bool IsKeptBoost(KeyValuePair<int, Snap> boosted, int keepPid, long keepCreation, string keepName)
		{
			if (keepPid > 0 && boosted.Key == keepPid && boosted.Value.Creation == keepCreation)
			{
				return string.Equals(boosted.Value.Name, keepName, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private bool UnboostGames(int keepPid, long keepCreation, string keepName)
		{
			bool flag = RenderLane.ReleaseExcept(keepPid, keepCreation);
			flag &= FrameCoordination.ReleaseExcept(keepPid, keepCreation);
			bool flag2 = (keepPid == coreWatchPid && keepCreation == coreWatchCreation) || StopCoreRefinement();
			bool flag3 = keepPid > 0 || StopCoreIsolation();
			bool flag4 = coreEngaged && !corePinPending;
			bool flag5 = RestoreHardPinsExcept(flag4 ? keepPid : 0, flag4 ? keepCreation : 0);
			corePinPending = !flag5;
			flag3 = flag3 && flag2 && flag && flag5;
			List<KeyValuePair<int, Snap>> list;
			lock (sync)
			{
				if (gameBoost.Count == 0 && gameGpu.Count == 0)
				{
					return flag3;
				}
				list = new List<KeyValuePair<int, Snap>>();
				foreach (KeyValuePair<int, Snap> item in gameBoost)
				{
					if (!IsKeptBoost(item, keepPid, keepCreation, keepName))
					{
						list.Add(item);
					}
				}
				Dictionary<int, int> dictionary = new Dictionary<int, int>();
				foreach (KeyValuePair<int, int> item2 in gameGpu)
				{
					if (keepPid <= 0 || item2.Key != keepPid)
					{
						dictionary[item2.Key] = item2.Value;
					}
				}
				if (list.Count == 0 && dictionary.Count == 0)
				{
					return flag3;
				}
				if (keepPid <= 0)
				{
					boostFail.Clear();
					boostDenied.Clear();
					boostStateWarned.Clear();
					boostStateVerified.Clear();
					gameBoostNextAudit.Clear();
					ForgetNvTweaksLocked();
					boostHandleStripped.Clear();
					boostEcoGaveUp.Clear();
					placementFail.Clear();
					placementGaveUp.Clear();
					boostStateFail.Clear();
				}
				else
				{
					foreach (KeyValuePair<int, Snap> item3 in list)
					{
						boostFail.Remove(item3.Key);
						boostDenied.Remove(item3.Key);
						boostStateWarned.Remove(item3.Key);
						boostStateVerified.Remove(item3.Key);
						gameBoostNextAudit.Remove(item3.Key);
						ForgetNvTweakLocked(item3.Key);
						boostHandleStripped.Remove(item3.Key);
						boostEcoGaveUp.Remove(item3.Key);
						placementFail.Remove(item3.Key);
						placementGaveUp.Remove(item3.Key);
						boostStateFail.Remove(item3.Key);
					}
				}
			}
			foreach (KeyValuePair<int, Snap> item4 in list)
			{
				RestoreHardPin(item4.Key);
			}
			foreach (KeyValuePair<int, Snap> item5 in list)
			{
				int key = item5.Key;
				bool flag6 = RunBoostRecovery(key, item5.Value.Creation, "snapshot", () => RestoreBoostProcess(key, item5.Value));
				if (flag6)
				{
					lock (coreIsolationGate)
					if ((criticalBorrowsCoreIsolation && criticalCoreIsolationPid == key && criticalCoreIsolationBirth == item5.Value.Creation)
						|| (!criticalOwnsCoreIsolation && coreIsolation != null && !coreIsolation.Drop(key, item5.Value.Creation)))
					{
						flag3 = false;
					}
					ForgetHardPin(key);
					CrashGuard.ReleaseBoostProcess(key, item5.Value.Creation);
					lock (sync)
					{
						gameBoost.Remove(key);
						gameGpu.Remove(key);
						gamePlacement.Remove(key);
						gameBoostNextAudit.Remove(key);
						ForgetBoostActionTarget(key);
					}
				}
			}
			lock (sync)
			{
				return flag3 && gameBoost.Count == 0;
			}
		}

		internal static void CurrentPlacement(IntPtr h, ref ulong affinity, ref uint[] cpuSets)
		{
			ulong mask;
			if (!CpuTopology.MultiGroup && Native.TryQueryAffinity(h, out mask) && mask != 0)
			{
				affinity = mask;
			}
			uint[] array = Native.QueryCpuSets(h);
			if (array != null)
			{
				cpuSets = array;
			}
		}

		public bool PanicRestore()
		{
			Interlocked.Increment(ref standbyCleanerRestorePending);
			InvalidateCacheWarm();
			InvalidateStandbyCleanerWork();
			InvalidateEnglishInputWork();
			try
			{
				return PanicRestoreCore();
			}
			finally
			{
				Interlocked.Decrement(ref standbyCleanerRestorePending);
			}
		}

		private bool PanicRestoreCore()
		{
			int num = SelfProtectedRoster.Clear();
			if (num > 0)
			{
				Logger.Log(Lang.T("log.gamemodeboost.46") + num + Lang.T("log.gamemodeboost.47"));
			}
			bool flag = true;
			if (IfeoBoost.HasResidue())
			{
				flag &= IfeoBoost.RestoreAll();
			}
			if (CfgOffTweak.HasResidue())
			{
				flag &= CfgOffTweak.RestoreAll();
			}
			try
			{
				ActionFailureStore.Records();
			}
			catch (Exception ex)
			{
				Logger.Warn("Failure isolation retained during panic recovery: " + ex.Message);
			}
			lock (panicCallGate)
			{
				int num2 = Interlocked.Increment(ref panicSeq);
				panicDone.Reset();
				panicResult = false;
				lock (sync)
				{
					panicReq = true;
					InvalidatePowerPlanSession();
					InvalidateStandbyCleanerWork();
					InvalidateEnglishInputWork();
					InvalidateRendererHandoff();
				}
				kick.Set();
				long num3 = DateTime.UtcNow.Ticks + 120000000;
				while (true)
				{
					long num4 = (num3 - DateTime.UtcNow.Ticks) / 10000;
					if (num4 <= 0)
					{
						return false;
					}
					if (!panicDone.WaitOne((int)num4))
					{
						return false;
					}
					if (Volatile.Read(ref panicServed) == num2)
					{
						break;
					}
					panicDone.Reset();
				}
				return panicResult && flag;
			}
		}

		private bool Deactivate(string reason)
		{
			return Deactivate(reason, false);
		}

        private bool Deactivate(string reason, bool quiet)
        {
            if (sessionRecoveryCaptured && sessionRecoveryRoutes != null && !sessionRecoveryRoutes.Complete && !active)
                return RetrySessionRecoveryRoutes(reason);
            using (BeginSessionRecoveryCapture())
            {
                RecoveryRouteSet owner = recoveryCaptureRoutes;
                bool restored = DeactivateCore(reason, quiet);
                if (!object.ReferenceEquals(owner, sessionRecoveryRoutes) || owner.Session != Interlocked.Read(ref gameSessionStarts)) return false;
                sessionRecoveryCaptured = true;
                residueNextTryTicks = sessionRecoveryRoutes.Next;
                return restored;
            }
        }

        private bool DeactivateCore(string reason, bool quiet)
        {
			// Revoke queued power work before any slower session recovery can run.
			powerSessionEnding = true;
			InvalidatePowerPlanSession();
			RevokeCriticalThreadIntent("Deactivate");
			bool flag = RunSessionRecoveryRoute("critical", () => StopCriticalThreads(0, "Deactivate"));
			EndAutomationEvidence();
			ResetSessionForeground();
			InvalidateCacheWarm();
			InvalidateStandbyCleanerWork();
			EndEnglishInputSession();
			InvalidateRendererHandoff();
            bool reportClean = RunSessionRecoveryRoute("report-finish", delegate { ReportFinish(); return true; });
            RunSessionRecoveryRoute("power-overlay", () => !PowerOverlay.HasResidue || PowerOverlay.Restore());
            RunSessionRecoveryRoute("selfyield", delegate { SelfYield.Release(); return !SelfYield.HasResidue; });
			lock (sync)
			{
				active = false;
				activeGame = null;
				firstSweep = true;
			}
			SyncPerformanceAutomation();
			sessionPolicy = null;
			lock (sync)
			{
				coreSessionPolicy = null;
				criticalSessionPreference = null;
			}
            bool flag4 = RunSessionRecoveryRoute("guard", delegate
            {
                if (enabled && !stopping && !ProfileStoreSaveFailed) { SyncGameDvr(); return !GuardSchedulingRecoveryPending; }
                return RestoreGuardScheduling();
            });
			SuppressionCore.GpuDemoteEnabled = bgSuppressOn;
			gameGoneSinceTicks = 0L;
			ResetBoostIdentity();
			Interlocked.Exchange(ref boostFirstStampTicks, 0L);
			Interlocked.Exchange(ref nvTweakRetryAtTicks, 0L);
			ResetNvApplyAttempts();
			if (!quiet)
			{
				preStagedNvPath = null;
			}
			ResetAdaptiveGuard();
			bool flag5 = RunSessionRecoveryRoute("unboost", () => UnboostGames());
			EndAutoCores();
			slowEnvAtTicks = 0L;
            int num = 0;
            bool flag6 = RunSessionRecoveryRoute("background", delegate
            {
                List<int> list = core.PidsWith(SuppressReason.Background);
                num += core.ReleaseReason(SuppressReason.Background);
                foreach (int item in list) if (core.IsThrottled(item)) return false;
                return true;
            });
			bool flag7 = RestoreEnv();
			ClearEnvRetryState();
			if (flag5)
			{
				CrashGuard.ClearBoost();
			}
			int num2 = num + gracePreReleased;
			gracePreReleased = 0;
			if (!quiet || num2 > 0)
			{
				Logger.Log(Lang.T("log.gamemodeboost.52") + reason + Lang.T("log.gamemodeboost.53") + num2 + Lang.T("log.gamemodeboost.54"));
			}
			lock (sync)
			{
				activeDetection = null;
				transitionProbeRendererPid = 0;
				transitionProbeRendererCreation = 0L;
			}
			ClearSticky();
			NotifyExtensionSession(false);
			bool flag8 = RunRecoveryWait("standby-worker", () => standbyCleaner == null || standbyCleaner.Drain(0));
			bool flag9 = RunRecoveryWait("input-worker", () => DrainEnglishInput(0));
			bool flag10 = RunRecoveryWait("cache-worker", () => cacheWarm == null || cacheWarm.Drain(0));
			bool flag11 = RunSessionRecoveryRoute("performance-worker", () => performanceActions.WaitForSessionRestore(0));
            bool verified = RunSessionRecoveryRoute("verify", () => EnvRestoreCompleted(true) && !SelfYield.HasResidue, "*");
			bool flag12 = flag5 && flag7 && flag6 && flag8 && flag9 && flag10 && flag4 && flag11 && flag && reportClean && verified && !SelfYield.HasResidue;
			// Report failure only after bounded retries are exhausted; queued retries remain restoring.
			RecoveryRouteSet captured = recoveryCaptureRoutes;
			bool retrying = !flag12 && CapturingSessionRecovery && captured != null && captured.RetryScheduled;
			RecordAutomation("session", flag12 ? "restored" : (retrying ? "restoring" : "restore-failed"));
			return flag12;
		}

		private static BoostTarget ProbeBoostTarget(int pid, long creation)
		{
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				if (Marshal.GetLastWin32Error() != 87)
				{
					return BoostTarget.Unopenable;
				}
				return BoostTarget.Gone;
			}
			try
			{
				long creation2;
				long cpu;
				ulong io;
				if (!Native.QueryProcessSample(intPtr, out creation2, out cpu, out io))
				{
					return BoostTarget.Unopenable;
				}
				if (creation > 0 && creation2 != creation)
				{
					return BoostTarget.Gone;
				}
				return (!Native.StillActive(intPtr)) ? BoostTarget.Gone : BoostTarget.Alive;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		internal int DropVanishedBoosts()
		{
			List<KeyValuePair<int, Snap>> list = new List<KeyValuePair<int, Snap>>();
			lock (sync)
			{
				foreach (KeyValuePair<int, Snap> item in gameBoost)
				{
					list.Add(item);
				}
			}
			if (list.Count == 0)
			{
				return 0;
			}
			int num = 0;
			int num2 = 0;
			foreach (KeyValuePair<int, Snap> item2 in list)
			{
				BoostTarget boostTarget = ProbeBoostTarget(item2.Key, item2.Value.Creation);
				if (boostTarget == BoostTarget.Alive)
				{
					lock (sync)
					{
						boostVanishTries.Remove(item2.Key);
					}
					continue;
				}
				bool flag = boostTarget == BoostTarget.Unopenable;
				if (flag)
				{
					int value;
					lock (sync)
					{
						boostVanishTries.TryGetValue(item2.Key, out value);
						value++;
						boostVanishTries[item2.Key] = value;
					}
					if (value < 20)
					{
						continue;
					}
					num2++;
				}
				if (RestoreHardPin(item2.Key))
				{
					if (!flag)
					{
						CrashGuard.ReleaseBoostProcess(item2.Key, item2.Value.Creation);
					}
					lock (sync)
					{
						boostVanishTries.Remove(item2.Key);
						gameBoost.Remove(item2.Key);
						gameGpu.Remove(item2.Key);
						gamePlacement.Remove(item2.Key);
						boostFail.Remove(item2.Key);
						boostStateWarned.Remove(item2.Key);
						boostStateVerified.Remove(item2.Key);
						boostHandleStripped.Remove(item2.Key);
						boostEcoGaveUp.Remove(item2.Key);
						placementGaveUp.Remove(item2.Key);
						placementFail.Remove(item2.Key);
						boostStateFail.Remove(item2.Key);
						gameBoostNextAudit.Remove(item2.Key);
						boostDenied.Remove(item2.Key);
						ForgetNvTweakLocked(item2.Key);
						ForgetBoostActionTarget(item2.Key);
					}
					num++;
				}
			}
			if (num2 > 0)
			{
				Logger.Warn(Lang.T("log.gamemodeboost.58") + num);
			}
			else if (num > 0)
			{
				Logger.Log(Lang.T("log.gamemodeboost.57") + num);
			}
			return num;
		}

		private void PruneDeadBoosts(ICollection<int> live)
		{
			lock (sync)
			{
				boostDenied.RemoveWhere((int x) => !live.Contains(x));
				boostStateWarned.RemoveWhere((int x) => !live.Contains(x));
				boostStateVerified.RemoveWhere((int x) => !live.Contains(x));
				boostHandleStripped.RemoveWhere((int x) => !live.Contains(x));
				boostEcoGaveUp.RemoveWhere((int x) => !live.Contains(x));
				placementGaveUp.RemoveWhere((int x) => !live.Contains(x));
				tweakApplied.RemoveWhere((int x) => !live.Contains(x));
				List<int> list = null;
				foreach (int key in gameBoost.Keys)
				{
					if (!live.Contains(key))
					{
						if (list == null)
						{
							list = new List<int>();
						}
						list.Add(key);
					}
				}
				if (list == null)
				{
					return;
				}
				foreach (int item in list)
				{
					// A reused or incomplete process snapshot is not proof of exit.
					if (ProbeBoostTarget(item, gameBoost[item].Creation) != BoostTarget.Gone) continue;
					if (RestoreHardPin(item))
					{
						CrashGuard.ReleaseBoostProcess(item, gameBoost[item].Creation);
						gameBoost.Remove(item);
						gameGpu.Remove(item);
						gamePlacement.Remove(item);
						boostFail.Remove(item);
						boostStateWarned.Remove(item);
						boostStateVerified.Remove(item);
						gameBoostNextAudit.Remove(item);
						placementFail.Remove(item);
						boostStateFail.Remove(item);
						ForgetBoostActionTarget(item);
					}
				}
			}
		}

		internal static bool RendererIdentityMatches(int expectedPid, long expectedCreation, string expectedName, int actualPid, long actualCreation, string actualName)
		{
			if (expectedPid > 0 && expectedPid == actualPid && expectedCreation > 0 && expectedCreation == actualCreation && !string.IsNullOrEmpty(expectedName) && !string.IsNullOrEmpty(actualName))
			{
				return string.Equals(expectedName, actualName, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private void OnGameHandleStripped(int pid, string rendererName, uint granted)
		{
			string text = KernelAntiCheat.DescribeForLog(rendererName);
			Logger.Log(Lang.T("log.gamemodeboost.3") + rendererName + " pid " + pid + Lang.T("log.gamemodeboost.34") + ((text == null) ? Lang.T("nav.tame") : text) + Lang.T("log.gamemodeboost.35") + granted.ToString("X") + Lang.T("log.gamemodeboost.36"));
			ProtectedGameRoster.Remember(rendererName);
		}

		private static string EcoStateText(IntPtr process)
		{
			int controlMask;
			int stateMask;
			if (!Native.TryQueryPowerThrottling(process, out controlMask, out stateMask))
			{
				return Lang.T("t.gamemodeboost.33") + QoSDump(process);
			}
			return (Native.EcoClearedMasksOk(controlMask, stateMask) ? Lang.T("t.gamemodeboost.38") : Lang.T("t.gamemodeboost.33")) + QoSDump(process);
		}

		internal static string QoSDump(IntPtr process)
		{
			int controlMask;
			int stateMask;
			if (!Native.TryQueryPowerThrottling(process, out controlMask, out stateMask))
			{
				return Lang.T("t.gamemodeboost.37");
			}
			return "(control=0x" + controlMask.ToString("X") + " state=0x" + stateMask.ToString("X") + ")";
		}

		internal static bool HighQoSVerified(IntPtr process)
		{
			if (!Native.PowerThrottlingSupported)
			{
				return true;
			}
			int controlMask;
			int stateMask;
			if (!Native.TryQueryPowerThrottling(process, out controlMask, out stateMask))
			{
				return false;
			}
			return Native.HighQoSMasksOk(controlMask, stateMask);
		}

		private void InvalidateCacheWarm()
		{
			Interlocked.Increment(ref cacheWarmGeneration);
			if (cacheWarm != null)
			{
				cacheWarm.Cancel(true);
			}
		}

		private void BeginCacheWarmSession()
		{
			lock (sync)
			{
				Interlocked.Increment(ref cacheWarmSessionId);
				InvalidateCacheWarm();
			}
		}

		private Func<bool> CaptureCacheWarmAdmission(out string key, out string executable, out string root)
		{
			string profileId;
			string rendererPath;
			string installRoot;
			string profileEntry;
			long session;
			int generation;
			lock (sync)
			{
				GameDetection gameDetection = activeDetection;
				profileId = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Id : null);
				executable = ((gameDetection != null) ? gameDetection.RendererPath : null);
				root = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Root : null);
				rendererPath = executable;
				installRoot = root;
				profileEntry = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.ExecutablePath : null);
				session = Interlocked.Read(ref cacheWarmSessionId);
				generation = cacheWarmGeneration;
				key = profileId + ":" + session + ":" + generation + "|" + installRoot + "|" + rendererPath + "|" + profileEntry;
			}
			return delegate
			{
				lock (sync)
				{
					GameDetection gameDetection2 = activeDetection;
					GameProfile gameProfile = FindProfileLocked(profileId);
					return generation == cacheWarmGeneration && active && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed && !stickyGraceOnly && gameGoneSinceTicks == 0 && session > 0 && Volatile.Read(ref standbyCleanerRestorePending) == 0 && session == Interlocked.Read(ref cacheWarmSessionId) && !string.IsNullOrEmpty(profileId) && gameDetection2 != null && gameDetection2.Profile != null && gameDetection2.Profile.Id == profileId && string.Equals(gameDetection2.RendererPath, rendererPath, StringComparison.OrdinalIgnoreCase) && string.Equals(gameDetection2.Profile.Root, installRoot, StringComparison.OrdinalIgnoreCase) && gameProfile != null && string.Equals(gameProfile.Root, installRoot, StringComparison.OrdinalIgnoreCase) && string.Equals(gameProfile.ExecutablePath, profileEntry, StringComparison.OrdinalIgnoreCase) && LiveBoolPreferenceLocked("GmCacheWarmV2", cacheWarmOn) && !LiveBoolPreferenceLocked("GmStandbyCleanerV2", standbyCleanerOn);
				}
			};
		}

		private void PrewarmAutomatic(GameProfile profile)
		{
			if (cacheWarm == null || profile == null)
			{
				return;
			}
			if (!PolicyResolver.For(profile).CacheWarm)
			{
				cacheWarm.Cancel(false);
				return;
			}
			int foreground = RendererForegroundPid();
			string id = profile.Id;
			string exe = profile.PreferredExecutablePath;
			string root = profile.Root;
			int generation = cacheWarmGeneration;
			Func<bool> allowed = delegate
			{
				if (!active && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed && generation == Volatile.Read(ref cacheWarmGeneration) && foreground > 0 && RendererForegroundPid() == foreground && AutomaticQuietDesktop())
				{
					lock (sync)
					{
						GameProfile gameProfile = FindProfileLocked(id);
						return gameProfile != null && string.Equals(armedGameName, gameProfile.Name, StringComparison.OrdinalIgnoreCase) && gameProfile.PreferredExecutablePath == exe && gameProfile.Root == root && PolicyResolver.For(gameProfile).CacheWarm;
					}
				}
				return false;
			};
			cacheWarm.Update("prelaunch:" + id + ":" + generation, exe, root, allowed);
		}

		private void ApplyCacheWarmPolicy()
		{
			if (cacheWarm != null)
			{
				cacheWarm.Cancel(false);
			}
		}

		private void ResetCoreIntelligence()
		{
			coreBudget = new CoreWorkBudget();
		}

		private bool StopCoreRefinement()
		{
			if (CoreThreadPlacement.UnownedPending)
			{
				return CoreThreadPlacement.Recover();
			}
			return true;
		}

		private static bool SameCriticalThreadDetection(GameDetection left, GameDetection right)
		{
			if (left == null || right == null)
			{
				return left == right;
			}
			if (left.RendererPid == right.RendererPid && left.RendererCreation == right.RendererCreation && left.RendererCandidateSelected == right.RendererCandidateSelected && left.RendererSafetyOnly == right.RendererSafetyOnly && left.RequiresGpuConfirm == right.RequiresGpuConfirm && string.Equals(left.RendererPath, right.RendererPath, StringComparison.OrdinalIgnoreCase) && left.Profile != null && right.Profile != null)
			{
				return left.Profile.Id == right.Profile.Id;
			}
			return false;
		}

		private static bool StoredRuleAdmits(string storedRaw, CriticalThreadRule bound)
		{
			CriticalThreadRule criticalThreadRule = CriticalThreadRule.ParseForRuntime(storedRaw);
			if (criticalThreadRule != null)
			{
				return criticalThreadRule.Admits(bound);
			}
			return false;
		}

		private void RevokeCriticalThreadIntent([CallerMemberName] string reason = null)
		{
			Interlocked.Increment(ref criticalIntentEpoch);
			CriticalThreadTargetLease criticalThreadTargetLease = criticalOriginalTarget;
			if (criticalThreadTargetLease != null)
			{
				if (!criticalThreadTargetLease.Revoked)
				{
					LogCriticalThreadState("revoked", reason);
				}
				criticalThreadTargetLease.Revoke();
			}
			CriticalThreadUiState criticalThreadUiState = criticalThreadUi;
			if (criticalThreadUiState.Applied || criticalThreadUiState.Pending)
			{
				criticalThreadUi = new CriticalThreadUiState(criticalThreadUiState.ProfileId, "recovery-pending", PerformancePolicy.Text("已停止新增绑定，正在核验原设置恢复", "Further bindings stopped; verifying restoration"), criticalThreadUiState.Enabled, false, true, criticalThreadUiState.Workers, criticalThreadUiState.SnapshotTicks, criticalThreadUiState.WorkerMask, criticalThreadUiState.CandidateMasks, criticalThreadUiState.CandidateLabels, criticalThreadUiState.SessionActive, criticalThreadUiState.RequestedWorkerAvoid, criticalThreadUiState.Coverage, criticalThreadUiState.SessionToken, criticalThreadUiState.GameSerial, criticalThreadUiState.IntentEpoch, criticalThreadUiState.TargetPid, criticalThreadUiState.TargetBirth, criticalThreadUiState.RuleSignature);
			}
		}

		private CriticalThreadTargetLease CreateCriticalThreadTarget(BoostPass pass, IntPtr process, CriticalThreadRule expectedRule, string expectedProfile, string expectedRawRule, ulong currentProcessMask, long expectedEpoch, long expectedSerial)
		{
			if (!Monitor.IsEntered(criticalProcessGate) || pass == null || process == IntPtr.Zero || expectedRule == null || currentProcessMask == 0 || GetProcessId(process) != (uint)pass.RendererPid || CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
			{
				return null;
			}
			long num = Interlocked.Read(ref criticalIntentEpoch);
			if (num != expectedEpoch || Interlocked.Read(ref gameSessionStarts) != expectedSerial)
			{
				return null;
			}
			long currentCreation;
			if (!VerifyRendererIdentity(process, pass.RendererPid, pass, out currentCreation) || currentCreation != pass.RendererCreation)
			{
				return null;
			}
			string text = Native.ImagePath(process);
			ulong mask;
			if (string.IsNullOrEmpty(text) || !GamePerformanceTargetPolicy.AllowsGameTarget(text) || !Native.TryQueryAffinity(process, out mask) || mask != currentProcessMask)
			{
				return null;
			}
			lock (sync)
			{
				GameDetection gameDetection = activeDetection;
				GameProfile gameProfile = CriticalPreferenceProfileLocked(expectedProfile);
				if (!enabled || stopping || panicReq || !active || smartSwitchedOut || ProfileStoreSaveFailed || gameDetection == null || gameDetection.Profile == null || gameProfile == null || gameDetection.Profile.Id != gameProfile.Id || !gameDetection.RendererCandidateSelected || gameDetection.RendererSafetyOnly || gameDetection.RequiresGpuConfirm || gameDetection.RendererPid != pass.RendererPid || gameDetection.RendererCreation != currentCreation || !string.Equals(gameDetection.RendererPath, text, StringComparison.OrdinalIgnoreCase) || CriticalThreadRule.Raw(gameProfile) != expectedRawRule || !StoredRuleAdmits(expectedRawRule, expectedRule) || Interlocked.Read(ref criticalIntentEpoch) != expectedEpoch || gameSessionStarts != expectedSerial)
				{
					return null;
				}
				CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(gameProfile);
				if (criticalThreadAllowedRange.Error != null)
				{
					return null;
				}
				ulong num2 = currentProcessMask & criticalThreadAllowedRange.Mask;
				ulong num3 = AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, CpuTopology.PhysicalCoreMasks());
				string text2 = expectedRule.Validate(allMask, CpuTopology.PhysicalCoreMasks(), num2, num3, true);
				if (text2 != null)
				{
					return null;
				}
				criticalMintInFlight = new CriticalThreadMint(pass.RendererPid, currentCreation, text, num2 & ~num3, num, gameSessionStarts, expectedRawRule);
				try
				{
					return criticalOriginalTarget = CriticalThreadTargetLease.Issue(this, criticalMintInFlight);
				}
				finally
				{
					criticalMintInFlight = null;
				}
			}
		}

		private bool CriticalRolesWanted(int pid, long birth)
		{
			if (pid > 0 && birth > 0 && Volatile.Read(ref criticalRolesPid) == pid)
			{
				return Interlocked.Read(ref criticalRolesBirth) == birth;
			}
			return false;
		}

		private void WantCriticalRoles(CriticalRuntimeSource source, ThreadTimes snapshot)
		{
			if (FrameCoordination.ReadCriticalRoles(source.Pid, source.Birth, snapshot, AutoCores.NowTicks)
				.Any(fact => fact.Role == "presentation-associated")) return;
			criticalRolesTouched = true;
			int pid = source.Pid;
			long birth = source.Birth;
			Interlocked.Exchange(ref criticalRolesBirth, birth);
			Volatile.Write(ref criticalRolesPid, pid);
			Func<bool> admitted = delegate
			{
				GameDetection gameDetection = Volatile.Read(ref activeDetection);
				return enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed && CriticalRolesWanted(pid, birth) && gameDetection != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && gameDetection.RendererPid == pid && gameDetection.RendererCreation == birth && RendererForegroundPid() == pid;
			};
			FrameCoordination.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), pid, birth, FrameAutomaticConfiguration());
			FrameCoordination.EnsureObserver(pid, birth, source.Name, admitted);
		}

		private void SettleCriticalRoles()
		{
			if (criticalRolesTouched)
			{
				criticalRolesTouched = false;
			}
			else if (Volatile.Read(ref criticalRolesPid) != 0)
			{
				Volatile.Write(ref criticalRolesPid, 0);
				Interlocked.Exchange(ref criticalRolesBirth, 0L);
				FrameCoordination.ReleaseObserver();
			}
		}

		private void ResetCriticalAuto()
		{
			criticalRolesTouched = false;
			SettleCriticalRoles();
		}

		private void BeginAutomaticCriticalThreadSession(CriticalRuntimeSource source, ThreadTimes snapshot)
		{
			CriticalThreadRoleFact[] roles = FrameCoordination.ReadCriticalRoles(source.Pid, source.Birth, snapshot, AutoCores.NowTicks);
			CriticalAutoTopology topology;
			string refusal;
			if (!ReadCriticalAutoTopology(source, out topology, out refusal))
			{
				criticalAutoSelectionKey = "";
				criticalAutoSelectionStable = 0;
				PublishCriticalThreadState(source, "unsupported", refusal, false, false, 0, 0L);
				return;
			}
			int capacity = CriticalThreadAutoPolicy.Capacity(topology.Physical, topology.Allowed, topology.Reserved, topology.Performance);
			if (capacity < 1)
			{
				criticalAutoSelectionKey = "";
				criticalAutoSelectionStable = 0;
				PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text("可用物理核心不足：至少需要三个不同物理核，其中两个留给其他线程", "Not enough available physical cores: at least three distinct physical cores are needed, two of them left for other threads"), false, false, 0, 0L);
				return;
			}
			CriticalThreadHotspot[] hotspots = criticalObservation.Hotspots;
			bool pooled;
			HardThreadIdentity primary = CriticalThreadAutoPolicy.SelectPrimary(hotspots, roles, out pooled);
			if (primary == null)
			{
				criticalAutoSelectionKey = "";
				criticalAutoSelectionStable = 0;
				string waiting = (hotspots.Length == 0)
					? PerformancePolicy.Text("游戏线程尚未持续占用至少 30% 单核，暂不绑定", "No game thread keeps at least 30% of one core busy yet; nothing is bound")
					: (pooled
						? PerformancePolicy.Text("最忙的几个线程负载接近，像可互换的工作线程，交给系统调度，不硬绑定", "The busiest threads carry similar loads like an interchangeable worker pool; they are left to the scheduler and not bound")
						: ((roles.Length == 0)
							? PerformancePolicy.Text("正在识别渲染线程；识别不到时，最忙的线程持续 30 秒后再绑定", "Identifying the render thread; without it, the busiest thread is bound after 30 seconds of sustained load")
							: PerformancePolicy.Text("等待线程持续占用至少 30% 单核", "Waiting for a thread to keep at least 30% of one core busy")));
				PublishCriticalThreadState(source, "observing", waiting, false, false, 0, 0L);
				return;
			}
			HardThreadIdentity[] secondary = CriticalThreadAutoPolicy.SelectSecondary(hotspots, primary, capacity - 1);
			HardThreadIdentity[] threads = new HardThreadIdentity[1] { primary }.Concat(secondary).ToArray();
			string key = source.Pid + ":" + source.Birth + "|" + primary.Key + "|" + string.Join(",", secondary.Select((HardThreadIdentity x) => x.Key).OrderBy((string x) => x, StringComparer.Ordinal).ToArray());
			if (key == criticalAutoSelectionKey)
			{
				criticalAutoSelectionStable++;
			}
			else
			{
				criticalAutoSelectionKey = key;
				criticalAutoSelectionStable = 1;
			}
			if (criticalAutoSelectionStable < 2)
			{
				PublishCriticalThreadState(source, "observing", (secondary.Length == 0)
					? PerformancePolicy.Text("已选定主线程，确认稳定后硬绑定", "Main thread chosen; it is bound once the choice is stable")
					: PerformancePolicy.Text("已选定主线程，另有 " + secondary.Length + " 个接近跑满的线程将设为优先核（本机最多 " + capacity + " 个），确认稳定后一并施加", "Main thread chosen, plus " + secondary.Length + " near-saturated thread(s) for preferred cores (up to " + capacity + " on this PC); applied together once stable"), false, false, 0, 0L);
				return;
			}
			criticalAutoSelectionKey = "";
			criticalAutoSelectionStable = 0;
			CriticalThreadRule rule;
			HardThreadChoice[] choices;
			HardThreadChoice[] hints;
			if (!ResolveCriticalAutoRule(source, threads, topology, out rule, out choices, out hints, out refusal))
			{
				PublishCriticalThreadState(source, "unsupported", refusal, false, false, 0, 0L);
				return;
			}
			BindCriticalThreads(source, snapshot, rule, choices, roles);
			if (criticalSession != null && criticalPlan != null && criticalThreadUi.Applied)
			{
				string soft = (hints.Length == 0) ? "" : criticalIdealHints.Apply(source.Pid, source.Birth, hints);
				Logger.Info("THREAD " + (source.Rule.ManualTarget
					? PerformancePolicy.Text("关键线程已手动选核：", "Critical thread placed on the manually chosen CPU: ")
					: PerformancePolicy.Text("关键线程已自动选核：", "Critical threads placed on automatically chosen cores: ")) + source.Name
					+ " hard " + string.Join(", ", choices.Select((HardThreadChoice x) => "TID " + x.Thread.Tid + " -> " + CpuTopology.DescribeMask(x.Mask)).ToArray())
					+ (hints.Length == 0 ? "" : " | soft " + string.Join(", ", hints.Select((HardThreadChoice x) => "TID " + x.Thread.Tid + " -> " + CpuTopology.DescribeMask(x.Mask)).ToArray()) + " [" + soft + "]")
					+ (source.Rule.WorkerAvoidance ? " workers-avoid" : " candidates-only core-clearing=off") + " hard=" + choices.Length + " soft=" + criticalIdealHints.Count + "/" + hints.Length + " capacity=" + capacity);
			}
		}

		private sealed class CriticalAutoTopology
		{
			internal ulong[] Physical;
			internal ulong[] Domains;
			internal ulong Allowed, Reserved, Performance, PreferredDomain;
		}

		private bool ReadCriticalAutoTopology(CriticalRuntimeSource source, out CriticalAutoTopology topology, out string refusal)
		{
			topology = null;
			refusal = null;
			if (IntPtr.Size != 8 || CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
			{
				refusal = PerformancePolicy.Text("处理器拓扑或处理器组暂不支持", "CPU topology or processor groups are unsupported");
				return false;
			}
			GameProfile profile;
			lock (sync)
			{
				profile = CriticalPreferenceProfileLocked(source.Profile);
				if (profile != null) profile = profile.Clone();
			}
			CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(profile);
			if (criticalThreadAllowedRange.Error != null)
			{
				refusal = criticalThreadAllowedRange.Error;
				return false;
			}
			ulong mask;
			if (!ReadCriticalProcessMask(source.Pid, source.Birth, out mask))
			{
				refusal = PerformancePolicy.Text("无法读取游戏进程的处理器亲和性；未施加线程硬绑定", "The game's processor affinity cannot be read; no hard thread binding is applied");
				return false;
			}
			topology = new CriticalAutoTopology
			{
				Physical = CpuTopology.PhysicalCoreMasks(),
				Domains = CpuTopology.L3Masks(),
				Allowed = mask & criticalThreadAllowedRange.Mask,
				Reserved = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask,
				Performance = (CpuTopology.Hybrid ? CpuTopology.PerfMask : 0),
				PreferredDomain = (CpuTopology.AsymCache ? CpuTopology.BigL3Mask : 0)
			};
			return true;
		}

		private bool ResolveCriticalAutoRule(CriticalRuntimeSource source, HardThreadIdentity[] threads, CriticalAutoTopology topology, out CriticalThreadRule rule, out HardThreadChoice[] choices, out HardThreadChoice[] hints, out string refusal)
		{
			rule = null;
			choices = null;
			hints = null;
			refusal = null;
			refusal = source.Rule.Validate(allMask, topology.Physical, topology.Allowed, topology.Reserved, true);
			if (refusal != null) return false;
			// Preserve thread priority order when cores are scarce; discard lower-priority soft hints first.
			ulong[] array = null;
			for (int count = Math.Min(threads.Length, CriticalThreadAutoPolicy.MaximumIsolated); count >= 1 && array == null; count--)
			{
				array = CriticalThreadAutoPolicy.Destinations(source.Rule, count, topology.Physical, topology.Allowed, topology.Reserved, CpuTopology.FavoredMask, topology.Performance, topology.Domains, topology.PreferredDomain, CpuTopology.ThrottleMask);
			}
			if (array == null)
			{
				refusal = PerformancePolicy.Text("可用物理核心不足：至少选中三个不同物理核，每核可只选一个逻辑线程", "Not enough available physical cores: select at least three distinct physical cores; one logical processor per core is enough");
				return false;
			}
			CriticalThreadRule criticalThreadRule = new CriticalThreadRule(ManualCorePlan.Stamp(allMask, topology.Physical), source.Rule.WorkerAvoidance, new ulong[1] { array[0] });
			refusal = criticalThreadRule.Validate(allMask, topology.Physical, topology.Allowed, topology.Reserved, true);
			if (refusal != null)
			{
				return false;
			}
			HardThreadIdentity[] chosen = threads;
			rule = criticalThreadRule;
			choices = new HardThreadChoice[1] { new HardThreadChoice(chosen[0], array[0]) };
			hints = array.Skip(1).Select((ulong mask, int index) => new HardThreadChoice(chosen[index + 1], mask)).ToArray();
			return true;
		}

		private static bool ReadCriticalProcessMask(int pid, long birth, out ulong mask)
		{
			mask = 0uL;
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return Native.QueryProcessIdentityState(intPtr, birth) == 0 && Native.TryQueryAffinity(intPtr, out mask) && mask != 0;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private void MaybeRecoverHistoricalCriticalThreads()
		{
			if (stopping || GameSessionToken < 0 || criticalThreadResidue || (!CriticalThreadHasRecoveryDebt(false) && !CriticalThreadGuardian.SettledLeftoversPending))
			{
				return;
			}
			lock (criticalHistoricalQueueGate)
			{
				if (stopping || criticalHistoricalWorker || AutoCores.NowTicks < criticalHistoricalRetry)
				{
					return;
				}
				criticalHistoricalWorker = true;
				criticalHistoricalIdle.Reset();
				try
				{
					GameNvWorkerQueue.TryQueue((WaitCallback callback) => ThreadPool.QueueUserWorkItem(callback), delegate
					{
						try
						{
							if (stopping || GameSessionToken < 0 || !Monitor.TryEnter(criticalProcessGate, 0))
							{
								return;
							}
							try
							{
								if (criticalSession == null && !criticalRestorationConfirmed && !stopping)
								{
									if (CriticalThreadHasRecoveryDebt(false))
									{
										CriticalThreadGuardian.TrySettleHistoricalGone(dataDir);
										criticalDebtNextRead = 0L;
										CriticalThreadHasRecoveryDebt(true);
										ScheduleThreadBoostRecoveryRefresh();
									}
									if (CriticalThreadGuardian.SettledLeftoversPending)
									{
										CriticalThreadGuardian.SweepSettledGuards(dataDir);
									}
								}
							}
							finally
							{
								Monitor.Exit(criticalProcessGate);
							}
						}
						catch (Exception ex2)
						{
							Logger.RepeatedError("critical-history-recovery", "Historical thread recovery is still unconfirmed: " + ex2.Message, -1L);
						}
					}, delegate
					{
						lock (criticalHistoricalQueueGate)
						{
							criticalHistoricalWorker = false;
							criticalHistoricalRetry = AutoCores.NowTicks + 300000000;
							criticalHistoricalIdle.Set();
						}
					});
				}
				catch (Exception ex)
				{
					Logger.RepeatedError("critical-history-queue", "Thread recovery could not be queued: " + ex.Message, -1L);
				}
			}
		}

		private bool DrainHistoricalCriticalThreads(int timeoutMs)
		{
			if (Monitor.IsEntered(sync) || Monitor.IsEntered(criticalProcessGate))
			{
				return false;
			}
			return criticalHistoricalIdle.WaitOne(Math.Max(0, timeoutMs));
		}

		internal CriticalThreadUiState CriticalThreadView(string profileId)
		{
			CriticalThreadUiState criticalThreadUiState = criticalThreadUi;
			if (criticalThreadUiState.Pending && (criticalThreadResidue || criticalHistoricalDebt))
			{
				return criticalThreadUiState;
			}
			GameProfile profile;
			lock (sync)
			{
				if (!criticalThreadUiState.Pending && CriticalThreadSnapshotCurrent(criticalThreadUiState, profileId))
				{
					return criticalThreadUiState;
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				profile = ((gameProfile == null) ? null : gameProfile.Clone());
			}
			CriticalThreadRule criticalThreadRule = CriticalThreadRule.ParseForRuntime(CriticalThreadRule.Raw(profile));
			if (criticalThreadRule == null)
			{
				return new CriticalThreadUiState(profileId, "off", PerformancePolicy.Text("未启用线程硬绑定", "Thread hard affinity is off"), false, false, false, 0, 0L, 0uL, null, null, false, false, CriticalThreadCoverageState.None, null, 0L, 0L, 0, 0L);
			}
			return new CriticalThreadUiState(profileId, "waiting-game", criticalThreadRule.Automatic
				? PerformancePolicy.Text("下次游戏运行时自动识别关键线程并绑定独立物理核心，其他接近满载的线程设为优先核", "The next game session binds the critical thread to its own physical core and gives other near-saturated threads a preferred core")
				: PerformancePolicy.Text("保存的规则在下次有效游戏会话中核验", "The saved rule is checked in the next valid game session"), true, false, false, 0, 0L, 0uL, null, null, false, criticalThreadRule.WorkerAvoidance, CriticalThreadCoverageState.None, null, 0L, 0L, 0, 0L);
		}

		internal string CriticalThreadDiagnosticLine()
		{
			CriticalThreadUiState view = criticalThreadUi;
			if (view == null) return "unavailable";
			long now = AutoCores.NowTicks;
			long age = view.SnapshotTicks > 0 && now >= view.SnapshotTicks ? (now - view.SnapshotTicks) / 10000L : -1;
			return "cached=" + view.State + " applied=" + view.Applied + " pending=" + view.Pending
				+ " pid=" + view.TargetPid + " birth=" + view.TargetBirth + " serial=" + view.GameSerial
				+ " rule=" + view.RuleSignature + " candidates=" + string.Join(",", view.CandidateMasks.Select(mask => "0x" + mask.ToString("X")))
				+ " workers=" + view.Workers + " workerAvoid=" + view.RequestedWorkerAvoid + " workerMask=0x" + view.WorkerMask.ToString("X")
				+ " corrections=" + Volatile.Read(ref criticalReassertions) + " ageMs=" + age + " coupling=" + view.Coupling
				+ (view.Detail.Length == 0 ? "" : " detail=" + view.Detail);
		}

		internal string CriticalThreadRuntimeDiagnosticLine()
		{
			CriticalThreadRuntimeTelemetry telemetry = Volatile.Read(ref criticalRuntimeTelemetry);
			return (telemetry == null ? "no samples" : telemetry.Describe())
				+ " | lastCandidate=" + Volatile.Read(ref criticalLastAuditDetail);
		}

		private void TraceCriticalRuntime(string stage, string result, int pid, long started, bool forwardAttempt = false)
		{
			if (criticalRuntimeTelemetry == null) criticalRuntimeTelemetry = new CriticalThreadRuntimeTelemetry();
			criticalRuntimeTelemetry.Record(stage, result, pid, started, forwardAttempt);
		}

		internal string SaveCriticalThreadRule(string profileId, string expected, CriticalThreadRule rule)
		{
			if (rule != null && rule.Automatic && !rule.ManualTarget) rule = CriticalThreadRule.Auto(true);
			bool affectsSession = false;
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed)
				{
					return PerformancePolicy.Text("游戏库暂时无法保存", "The game library cannot be saved right now");
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile == null || CriticalThreadRule.Raw(gameProfile) != expected)
				{
					return PerformancePolicy.Text("游戏规则已变化，请重新打开", "The game rule changed; reopen the editor");
				}
				CriticalThreadRule savedRule = CriticalThreadRule.ParseForRuntime(CriticalThreadRule.Raw(gameProfile));
				if (rule != null && savedRule != null && rule.Encode() == savedRule.Encode()) return null;
				if (rule != null)
				{
					CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(gameProfile);
					if (criticalThreadAllowedRange.Error != null)
					{
						return criticalThreadAllowedRange.Error;
					}
					string text = rule.Validate(allMask, CpuTopology.PhysicalCoreMasks(), criticalThreadAllowedRange.Mask, AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled);
					if (text != null)
					{
						return text;
					}
				}
				GameProfile gameProfile2 = gameProfile.Clone();
				if (rule == null)
				{
					gameProfile2.Overrides.Remove("GameCriticalThreadHardRuleV1");
				}
				else
				{
					gameProfile2.Overrides["GameCriticalThreadHardRuleV1"] = rule.Encode();
				}
				if (CriticalThreadRule.Raw(gameProfile2) == CriticalThreadRule.Raw(gameProfile)) return null;
				List<GameProfile> list = new List<GameProfile>(profiles);
				list[list.IndexOf(gameProfile)] = gameProfile2;
				if (!SaveProfileSnapshotLocked(list))
				{
					return PerformancePolicy.Text("保存失败，原规则保留", "Save failed; the original rule was retained");
				}
				profiles[profiles.IndexOf(gameProfile)] = gameProfile2;
				if (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId)
				{
					activeDetection.Profile = gameProfile2.Clone();
				}
				if (stickyDetection != null && stickyDetection.Profile != null && stickyDetection.Profile.Id == profileId)
				{
					stickyDetection.Profile = gameProfile2.Clone();
				}
				CriticalRuntimeSource source = Volatile.Read(ref criticalSource);
				affectsSession = rule == null && ((source != null && source.Profile == profileId)
					|| (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId));
				if (rule == null) DisableSessionCriticalPreferenceLocked(profileId);
				if (affectsSession) RevokeCriticalThreadIntent("SaveCriticalThreadRule");
			}
			if (affectsSession) RequestPolicyApply("SaveCriticalThreadRule");
			RaiseLibraryChanged();
			return null;
		}

		private bool CriticalThreadSnapshotCurrent(CriticalThreadUiState view, string profileId)
		{
			if (!Monitor.IsEntered(sync) || view.ProfileId != profileId || !view.SessionActive || view.SessionToken == Guid.Empty || view.TargetPid <= 0 || view.TargetBirth <= 0 || view.GameSerial != gameSessionStarts || view.IntentEpoch != Interlocked.Read(ref criticalIntentEpoch) || !active || stopping || !enabled || activeDetection == null || activeDetection.Profile == null || activeDetection.Profile.Id != profileId || activeDetection.RendererPid != view.TargetPid || activeDetection.RendererCreation != view.TargetBirth)
			{
				return false;
			}
			GameProfile gameProfile = CriticalPreferenceProfileLocked(profileId);
			if (gameProfile != null)
			{
				return CriticalThreadRule.Raw(gameProfile) == view.RuleSignature;
			}
			return false;
		}

		private CriticalRuntimeSource CaptureCriticalThreadSource(out string missing)
		{
			lock (sync)
			{
				GameDetection gameDetection = activeDetection;
				missing = ((!enabled || stopping || panicReq) ? "game mode stopped" : ((!active) ? "session inactive" : (ProfileStoreSaveFailed ? "profile store unavailable" : ((gameDetection == null || gameDetection.Profile == null) ? "no game detection" : ((!gameDetection.RendererCandidateSelected) ? "renderer candidate not selected" : (gameDetection.RendererSafetyOnly ? "renderer is safety-only" : (gameDetection.RequiresGpuConfirm ? "renderer awaits GPU confirmation" : ((gameDetection.RendererPid <= 0 || gameDetection.RendererCreation <= 0 || string.IsNullOrEmpty(gameDetection.RendererPath)) ? "renderer identity incomplete" : null))))))));
				if (missing != null)
				{
					return null;
				}
				GameProfile gameProfile = CriticalPreferenceProfileLocked(gameDetection.Profile.Id);
				if (gameProfile == null) { missing = "game removed"; return null; }
				string text = CriticalThreadRule.Raw(gameProfile);
				CriticalRuntimeSource criticalRuntimeSource = criticalSource;
				if (criticalRuntimeSource != null && criticalRuntimeSource.RuleText == text && criticalRuntimeSource.Profile == gameProfile.Id && criticalRuntimeSource.Pid == gameDetection.RendererPid && criticalRuntimeSource.Birth == gameDetection.RendererCreation && criticalRuntimeSource.Serial == gameSessionStarts && criticalRuntimeSource.Epoch == Interlocked.Read(ref criticalIntentEpoch) && string.Equals(criticalRuntimeSource.Path, gameDetection.RendererPath, StringComparison.OrdinalIgnoreCase))
				{
					return criticalRuntimeSource;
				}
				if (CriticalThreadRule.Parse(text) == null)
				{
					missing = "rule unreadable";
					return null;
				}
				return new CriticalRuntimeSource(gameDetection, text, gameSessionStarts, Interlocked.Read(ref criticalIntentEpoch));
			}
		}

		private void StepCriticalThreadRuntime()
		{
			string missing;
			CriticalRuntimeSource criticalRuntimeSource = CaptureCriticalThreadSource(out missing);
			if (!Monitor.TryEnter(criticalProcessGate, 0))
			{
				return;
			}
			try
			{
				if (criticalStability == null) criticalStability = new CriticalThreadStabilityPolicy();
				if (criticalSession == null && criticalPriorityReservation != null && !RestoreCriticalThreadsLocked()) return;
				if (Interlocked.Exchange(ref criticalForegroundAuditPending, 0) != 0)
				{
					criticalNextSample = 0L;
				}
				if (criticalRestorationConfirmed)
				{
					if (!RestoreCriticalThreadsLocked())
					{
						return;
					}
					criticalObservation.Reset();
					criticalNextSample = 0L;
				}
				if (criticalSource != null && !criticalSource.Same(criticalRuntimeSource))
				{
					if (!WithdrawCriticalThreadsLocked((criticalRuntimeSource == null) ? missing : criticalSource.Difference(criticalRuntimeSource)))
					{
						return;
					}
					criticalSource = null;
					criticalObservation.Reset();
					criticalNextSample = 0L;
				}
				if (criticalRuntimeSource == null)
				{
					WithdrawCriticalThreadsLocked(missing);
					return;
				}
				if (criticalSource == null)
				{
					criticalSource = criticalRuntimeSource;
					criticalReassertions = 0;
					criticalObservationMisses = 0;
				}
				CriticalRuntimeSource criticalRuntimeSource2 = criticalSource;
				if (criticalStability.IsBlocked(criticalSource.Profile, criticalSource.Pid, criticalSource.Birth, criticalSource.Serial, criticalSource.RuleText))
				{
					if (WithdrawCriticalThreadsLocked("repeated thread placement conflict"))
						PublishCriticalThreadState(criticalSource, "conflict", PerformancePolicy.Text("本局线程覆盖或归属未能确认，已恢复原设置并停止重新绑定", "Thread coverage or ownership could not be confirmed; original settings were restored and binding is stopped for this session"), false, false, 0, 0L);
					return;
				}
				if (criticalSession == null && CriticalThreadHasRecoveryDebt(false))
				{
					PublishCriticalThreadState(criticalRuntimeSource2, "recovery-pending", PerformancePolicy.Text("旧线程恢复记录尚未结清；未新增绑定", "Previous thread recovery is unresolved; no new binding is applied"), false, true, 0, 0L);
					return;
				}
				if (ProtectedGameRoster.Contains(criticalRuntimeSource2.Name))
				{
					if (WithdrawCriticalThreadsLocked("the game is on the protected roster"))
					{
						criticalObservation.Reset();
						PublishCriticalThreadState(criticalRuntimeSource2, "unsupported", PerformancePolicy.Text("该游戏此前拒绝或剥夺过写入权限；未施加线程硬绑定", "This game previously denied or stripped write access; no hard thread binding is applied"), false, false, 0, 0L);
					}
					return;
				}
				if (criticalGuardianLaunchBlocked)
				{
					if (WithdrawCriticalThreadsLocked("independent guardian launch unavailable"))
					{
						criticalObservation.Reset();
						ResetCriticalAuto();
						PublishCriticalThreadState(criticalRuntimeSource2, "unsupported", CriticalGuardianLaunchBlockedDetail(), false, false, 0, 0L);
					}
					return;
				}
				if (smartSwitchedOut || RendererForegroundPid() != criticalRuntimeSource2.Pid)
				{
					// Minimization can invalidate samples; retain the current lease and observation history until return.
					if (criticalSession == null)
					{
						PublishCriticalThreadState(criticalRuntimeSource2, "observing", PerformancePolicy.Text("等待游戏回到前台后重新观察", "Observations restart when the game returns to the foreground"), false, false, 0, 0L);
					}
					return;
				}
				bool observerRequested = false;
				if (criticalRuntimeSource2.Rule.Automatic && criticalSession == null && criticalObservation.Hotspots.Length > 0)
				{
					criticalRolesTouched = true;
					if (!FrameCoordination.HasCriticalPresenterCandidate(criticalRuntimeSource2.Pid, criticalRuntimeSource2.Birth, AutoCores.NowTicks))
					{
						WantCriticalRoles(criticalRuntimeSource2, null);
						observerRequested = true;
					}
				}
				long nowTicks = AutoCores.NowTicks;
				if (nowTicks < criticalNextSample)
				{
					return;
				}
				criticalNextSample = nowTicks + 50000000;
				if (criticalRuntimeSource2.Rule.Automatic && criticalSession != null && criticalPlan != null
					&& criticalPlan.WorkerMode == HardThreadWorkerMode.CandidatesOnly)
				{
					AuditAutomaticCriticalThread(criticalRuntimeSource2);
					return;
				}
				long sampleStarted = Stopwatch.GetTimestamp();
				ThreadTimes threadTimes = ProcessSnapshotSource.SampleThreads(criticalRuntimeSource2.Pid, criticalRuntimeSource2.Birth, 1500);
				bool observed = criticalObservation.Observe(threadTimes, criticalRuntimeSource2.Birth, AutoCores.NowTicks);
				TraceCriticalRuntime("sample", observed ? "ok" : "unavailable", criticalRuntimeSource2.Pid, sampleStarted);
				if (!observed)
				{
					if (criticalSession != null && ++criticalObservationMisses < 3)
					{
						PublishCriticalThreadState(criticalRuntimeSource2, "coverage-pending", PerformancePolicy.Text("线程观测暂不可用，暂停新增与纠偏，等待重新核验", "Thread observations are temporarily unavailable; new bindings and corrections are paused until verification resumes"), false, false, 0, 0L);
						return;
					}
					if (criticalSession != null) criticalStability.Block(criticalSource.Profile, criticalSource.Pid, criticalSource.Birth, criticalSource.Serial, criticalSource.RuleText);
					if (WithdrawCriticalThreadsLocked("thread observation unavailable"))
					{
						PublishCriticalThreadState(criticalRuntimeSource2, "observing", PerformancePolicy.Text("线程观测暂不可用；未继续新增绑定", "Thread observations are unavailable; further bindings are stopped"), false, false, 0, 0L);
					}
				}
				else if (criticalSession != null)
				{
					AuditCriticalThreadSession(criticalRuntimeSource2, threadTimes);
				}
				else if (criticalRuntimeSource2.Rule.Automatic)
				{
					if (!observerRequested && criticalObservation.Hotspots.Length > 0) WantCriticalRoles(criticalRuntimeSource2, threadTimes);
					BeginAutomaticCriticalThreadSession(criticalRuntimeSource2, threadTimes);
				}
				else
				{
					BeginCriticalThreadSession(criticalRuntimeSource2, threadTimes);
				}
			}
			catch (OperationCanceledException ex)
			{
				WithdrawCriticalThreadsLocked(ex.Message);
			}
			catch (HardThreadOperationException ex2)
			{
				if (ex2.Kind == HardThreadFailureKind.RecoveryPending)
				{
					criticalHistoricalDebt = true;
					criticalDebtNextRead = 0L;
					WithdrawCriticalThreadsLocked(ex2.Message);
					PublishCriticalThreadState(criticalSource, "recovery-pending", ex2.Message, false, true, 0, 0L);
				}
				else if (WithdrawCriticalThreadsLocked(string.Concat(ex2.Kind, ": ", ex2.Message)))
				{
					PublishCriticalThreadState(criticalSource, "unsupported", ex2.Message, false, false, 0, 0L);
				}
			}
			catch (HardThreadStorageDomainException ex3)
			{
				if (WithdrawCriticalThreadsLocked("storage domain: " + ex3.Message))
				{
					PublishCriticalThreadState(criticalSource, "unsupported", PerformancePolicy.Text("关键线程硬绑定需要本地数据目录；当前目录未获支持", "Hard thread placement requires a supported local data directory"), false, false, 0, 0L);
				}
				Logger.RepeatedError("critical-thread-storage-domain", ex3.Message, -1L);
				criticalNextSample = AutoCores.NowTicks + 300000000;
			}
			catch (Exception ex4)
			{
				if (HardThreadGuardianLaunchBlockedException.Contains(ex4))
				{
					criticalGuardianLaunchBlocked = true;
					bool flag = WithdrawCriticalThreadsLocked("independent guardian launch unavailable");
					criticalObservation.Reset();
					ResetCriticalAuto();
					if (criticalSource != null && flag)
					{
						PublishCriticalThreadState(criticalSource, "unsupported", CriticalGuardianLaunchBlockedDetail(), false, false, 0, 0L);
					}
					Logger.RepeatedError("critical-thread-guardian-launch", "Hard thread affinity: " + ex4.Message, -1L);
				}
				else
				{
					bool flag2 = WithdrawCriticalThreadsLocked(ex4.GetType().Name + ": " + ex4.Message);
					if (criticalSource != null && flag2)
					{
						PublishCriticalThreadState(criticalSource, "unsupported", PerformancePolicy.Text("本次未应用：", "Not applied: ") + ex4.Message, false, false, 0, 0L);
					}
					Logger.RepeatedError("critical-thread-runtime", "Hard thread affinity: " + ex4.GetType().Name + ": " + ex4.Message, -1L);
					criticalNextSample = AutoCores.NowTicks + 300000000;
				}
			}
			finally
			{
				SettleCriticalRoles();
				Monitor.Exit(criticalProcessGate);
			}
		}

		private static string CriticalGuardianLaunchBlockedDetail()
		{
			return PerformancePolicy.Text("Pavise 被启动它的程序放进了禁止脱离的作业（例如计划任务），独立恢复器无法启动；本次运行停止重试。退出 Pavise 后从桌面或开始菜单直接打开即可恢复", "Pavise was started inside a job that forbids breakaway (for example by Task Scheduler), so the independent recovery helper cannot start; retries are stopped for this run. Exit Pavise and open it directly from the desktop or Start menu to restore it.");
		}

		private void BeginCriticalThreadSession(CriticalRuntimeSource source, ThreadTimes snapshot)
		{
			CriticalThreadRoleFact[] roles = FrameCoordination.ReadCriticalRoles(source.Pid, source.Birth, snapshot, AutoCores.NowTicks);
			HardThreadChoice[] array = CriticalThreadRuntimePolicy.Select(source.Rule, criticalObservation.Candidates, roles);
			if (array.Length != source.Rule.CandidateCount)
			{
				PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("确认持续高负载线程后再绑定", "Binding waits for sustained CPU hotspot observations"), false, false, 0, 0L);
			}
			else
			{
				BindCriticalThreads(source, snapshot, source.Rule, array, roles);
			}
		}

		private void BindCriticalThreads(CriticalRuntimeSource source, ThreadTimes snapshot, CriticalThreadRule rule, HardThreadChoice[] choices, CriticalThreadRoleFact[] roles)
		{
			string launchKey = "critical-launch:" + source.Serial + ":" + source.Profile + ":" + source.Pid + ":" + source.Birth + ":" + source.Rule.Encode();
			if (!AutomaticWorkAllowed(launchKey)) return;
			if (criticalSession == null && criticalPriorityReservation != null && !RestoreCriticalThreadsLocked()) return;
			criticalFailureTarget = source.Profile + "|" + source.Path;
			ActionFailureStore.BeginAutomaticCycle("game-critical-thread-hard-affinity", criticalFailureTarget,
				source.Serial + "\n" + source.Profile + ":" + source.Pid + ":" + source.Birth + ":" + source.Rule.Encode());
			if (ActionFailureStore.IsBlocked("game-critical-thread-hard-affinity", criticalFailureTarget))
			{
				PublishCriticalThreadState(source, "conflict", ActionFailureStore.FailureReason("game-critical-thread-hard-affinity", criticalFailureTarget), false, false, 0, 0L);
				return;
			}
			IntPtr intPtr = Native.OpenProcess(4096, false, source.Pid);
			if (intPtr == IntPtr.Zero)
			{
				RecordAutomaticWorkFailure(launchKey, "process-open-failed");
				PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text("无法核验该游戏的进程权限与身份", "The game's process identity and permissions cannot be verified"), false, false, 0, 0L);
				return;
			}
			bool launchAttempted = false;
			try
			{
				ulong mask;
				if (!Native.TryQueryAffinity(intPtr, out mask))
				{
					RecordAutomaticWorkFailure(launchKey, "affinity-read-failed");
					PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text("无法读取游戏进程的处理器亲和性；未施加线程硬绑定", "The game's processor affinity cannot be read; no hard thread binding is applied"), false, false, 0, 0L);
					return;
				}
				BoostPass boostPass = new BoostPass();
				boostPass.RendererPid = source.Pid;
				boostPass.RendererCreation = source.Birth;
				boostPass.RendererName = source.Name;
				BoostPass pass = boostPass;
				CriticalThreadTargetLease criticalThreadTargetLease = CreateCriticalThreadTarget(pass, intPtr, rule, source.Profile, source.RuleText, mask, source.Epoch, source.Serial);
				if (criticalThreadTargetLease == null)
				{
					PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("绑定前核对游戏身份与规则未通过；本次未绑定，稍后重新核对", "Game identity and rule checks before binding did not pass; nothing was bound and they will be checked again"), false, false, 0, 0L);
					return;
				}
				string priorityReason;
				if (!TryReserveCriticalThreadPriority(source.Pid, source.Birth, choices.Select(x => x.Thread).ToArray(), out priorityReason))
				{
					criticalOriginalTarget.Revoke();
					RestoreCriticalThreadsLocked();
					PublishCriticalThreadState(source, "recovery-pending", priorityReason, false, true, 0, 0L);
					return;
				}
				launchAttempted = true;
				criticalSession = CriticalThreadHardAffinitySession.OpenNative(criticalThreadTargetLease);
				criticalRestorationConfirmed = false;
				criticalThreadResidue = true;
				criticalGuardianActor = criticalSession.GuardianActor;
				if (!ReleaseCriticalGuardianBackground())
				{
					throw new InvalidOperationException("Guardian background restoration is unconfirmed");
				}
				criticalPlan = criticalSession.CreatePlan(choices, source.Rule.Automatic && source.Rule.WorkerAvoidance);
				criticalChoiceLabels = choices.Select(delegate(HardThreadChoice choice)
				{
					CriticalThreadRoleFact criticalThreadRoleFact = roles.FirstOrDefault((CriticalThreadRoleFact x) => x.Tid == choice.Thread.Tid && x.Creation == choice.Thread.Creation);
					return "TID " + choice.Thread.Tid + ((criticalThreadRoleFact == null) ? PerformancePolicy.Text(" · CPU 热点", " · CPU hotspot") : ((criticalThreadRoleFact.Role == "presentation-associated") ? PerformancePolicy.Text(" · 呈现关联", " · Presentation-associated") : PerformancePolicy.Text(" · 依赖关联", " · Dependency-associated")));
				}).ToArray();
				criticalChoiceFrameAssociated = CriticalThreadBoostCouplingPolicy.FrameAssociated(choices, roles);
				criticalWorkerBudgetReached = false;
				ThreadTimes admitted = ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 0);
				if (!CriticalThreadObservation.Valid(admitted, source.Birth, AutoCores.NowTicks) || choices.Any((HardThreadChoice x) => !CriticalThreadRuntimePolicy.Contains(admitted, x.Thread)))
				{
					RestoreCriticalThreadsLocked();
					criticalObservation.Reset();
					LogCriticalThreadState("observing", "the snapshot after the guardian handshake was stale or lost a candidate");
				}
				else
				{
					criticalThreadTargetLease.RequireActive();
					ApplyCriticalThreadBatch(source, admitted, true);
				}
			}
			catch (Exception ex)
			{
				if (launchAttempted) RecordAutomaticWorkFailure(launchKey, ex.GetType().Name + ": " + ex.Message);
				throw;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private void AuditCriticalThreadSession(CriticalRuntimeSource source, ThreadTimes snapshot)
		{
			if (criticalPlan == null || criticalPlan.Choices.Any((HardThreadChoice x) => !CriticalThreadRuntimePolicy.Contains(snapshot, x.Thread)))
			{
				if (WithdrawCriticalThreadsLocked((criticalPlan == null) ? "no binding plan" : "a candidate thread is missing from the sample"))
				{
					criticalObservation.Reset();
					PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("原候选线程已退出；恢复后重新观察", "An original candidate exited; observing again after restoration"), false, false, 0, 0L);
				}
			}
			else
			{
				ApplyCriticalThreadBatch(source, snapshot, false);
			}
		}

		private void AuditAutomaticCriticalThread(CriticalRuntimeSource source)
		{
			long started = Stopwatch.GetTimestamp();
			string detail;
			HardThreadCandidateAudit result = criticalSession.AuditCandidates(out detail);
			Volatile.Write(ref criticalLastAuditDetail, result + ": " + detail);
			TraceCriticalRuntime("candidate-audit", result.ToString(), source.Pid, started);
			if (result == HardThreadCandidateAudit.Verified)
			{
				criticalObservationMisses = 0;
				string hintChanges = criticalIdealHints.Audit();
				if (hintChanges.Length != 0)
				{
					Logger.Info("THREAD soft placement audit: " + source.Name + " " + hintChanges + " remaining=" + criticalIdealHints.Count);
				}
				PublishCriticalThreadState(source, "active", "", true, false, 0, AutoCores.NowTicks, CriticalThreadCoverageState.CandidatesOnly);
				return;
			}
			if (result == HardThreadCandidateAudit.Unavailable && ++criticalObservationMisses < 3)
			{
				PublishCriticalThreadState(source, "coverage-pending", PerformancePolicy.Text("原候选暂不可核验，保留已有绑定并暂停写入：", "The original candidate is temporarily unverifiable; the existing binding is retained and writes are paused: ") + detail, false, false, 0, 0L);
				return;
			}
			if (result == HardThreadCandidateAudit.Gone)
			{
				if (!WithdrawCriticalThreadsLocked("original candidate exited: " + detail)) return;
				bool blocked = criticalStability.RecordRediscovery(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
				criticalObservation.Reset();
				PublishCriticalThreadState(source, blocked ? "conflict" : "observing", blocked
					? PerformancePolicy.Text("候选线程反复更换，本局停止重新绑定", "Candidate threads changed repeatedly; rebinding is stopped for this session")
					: PerformancePolicy.Text("原候选已确认退出，恢复后重新观察", "The original candidate has exited; observing again after restoration"), false, false, 0, 0L);
				return;
			}
			criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
			if (WithdrawCriticalThreadsLocked("automatic thread ownership yielded: " + detail))
				PublishCriticalThreadState(source, "conflict", result == HardThreadCandidateAudit.Changed
					? PerformancePolicy.Text("游戏已重新指定线程分配，已让出绑定，本局不再改回", "The game changed thread placement; the binding has been yielded and will not be reasserted this session")
					: PerformancePolicy.Text("原候选连续无法核验，已恢复并停止本局重绑", "The original candidate could not be verified repeatedly; original settings were restored and rebinding is stopped for this session"), false, false, 0, 0L);
		}

		private void ApplyCriticalThreadBatch(CriticalRuntimeSource source, ThreadTimes snapshot, bool initial)
		{
			if (criticalStability == null) criticalStability = new CriticalThreadStabilityPolicy();
			criticalSession.BeforeReassertion = delegate
			{
				if (source.Rule.Automatic)
				{
					criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
					return false;
				}
				return !criticalStability.RecordReassertion(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
			};
			HardThreadOwnership[] activeReceipts = criticalSession.ActiveReceipts;
			HardThreadCoverage coverage;
			HardThreadIdentity[] array = CriticalThreadRuntimePolicy.Batch(snapshot, criticalPlan, activeReceipts, out coverage);
			if (criticalWorkerBudgetReached)
			{
				array = criticalPlan.Choices.Select((HardThreadChoice x) => x.Thread).ToArray();
				coverage = HardThreadCoverage.Partial;
			}
			HashSet<string> owned = new HashSet<string>(activeReceipts.Select((HardThreadOwnership x) => x.Thread.Key), StringComparer.Ordinal);
			if (array.Any((HardThreadIdentity x) => !owned.Contains(x.Key)) && !criticalFailureAdmitted)
			{
				string reason;
				if (!ActionFailureStore.TryBeginApply("game-critical-thread-hard-affinity", criticalFailureTarget, out reason))
				{
					RestoreCriticalThreadsLocked();
					PublishCriticalThreadState(source, "conflict", reason, false, false, 0, 0L);
					return;
				}
				criticalFailureAdmitted = true;
			}
			long nowTicks = AutoCores.NowTicks;
			long ticks = DateTime.UtcNow.Ticks;
			long batchStarted = Stopwatch.GetTimestamp();
			bool flag = (initial ? criticalSession.Apply(criticalPlan, array, coverage) : criticalSession.AdmitNewThreads(array, coverage));
			TraceCriticalRuntime("batch", flag ? "ok" : criticalSession.LastFailureKind.ToString(), source.Pid, batchStarted, criticalSession.LastOperationNativeAttempt);
			HardThreadFailureKind lastFailureKind = criticalSession.LastFailureKind;
			if (!flag && !criticalPlan.RequireWorkerCoverage && lastFailureKind == HardThreadFailureKind.InvalidRequest && !criticalSession.LastOperationWriteIntent && !criticalSession.LastOperationNativeAttempt && !initial)
			{
				criticalWorkerBudgetReached = true;
				PublishCriticalThreadState(source, "coverage-pending", PerformancePolicy.Text("保留原候选绑定；本局不再增加工作线程", "Original candidate bindings retained; no more worker bindings this session"), false, false, 0, 0L);
				return;
			}
			if (!flag)
			{
				string detail = criticalSession.Detail;
				if (criticalPlan.RequireWorkerCoverage)
					detail += " [snapshotThreads=" + snapshot.Ids.Length + " batchThreads=" + array.Length
						+ " requestedCoverage=" + coverage + " ownedBefore=" + activeReceipts.Length
						+ " unknownBirth=" + snapshot.Created.Count(birth => birth <= 0) + "]";
				if (criticalPlan.RequireWorkerCoverage)
					criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
				if (initial && source.Rule.Automatic && lastFailureKind == HardThreadFailureKind.ObservationUnavailable)
					criticalStability.RecordRediscovery(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
				if (lastFailureKind == HardThreadFailureKind.WriteOrDurabilityFault)
				{
					ActionFailureStore.Trip("game-critical-thread-hard-affinity", criticalFailureTarget, detail);
					criticalFailureAdmitted = false;
				}
				if (WithdrawCriticalThreadsLocked(string.Concat(initial ? "apply failed " : "worker admission failed ", lastFailureKind, ": ", detail)))
				{
					criticalObservation.Reset();
					criticalNextSample = AutoCores.NowTicks + 100000000;
					PublishCriticalThreadState(source, (lastFailureKind == HardThreadFailureKind.WriteOrDurabilityFault) ? "conflict" : "observing", detail, false, false, 0, 0L);
				}
				return;
			}
			string text = (initial ? null : criticalSession.LastReassertion);
			if (text != null)
			{
				int num = ++criticalReassertions;
				if (num <= 3 || num % 20 == 0)
				{
					LogCriticalThreadState("reasserted", text + " (this session: " + num + ")");
				}
			}
			criticalOriginalTarget.RequireActive();
			int candidates;
			int workers;
			bool complete;
			ThreadTimes verifiedSnapshot = criticalPlan.RequireWorkerCoverage
				? ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 0) : snapshot;
			if (!CriticalThreadObservation.Valid(verifiedSnapshot, source.Birth, AutoCores.NowTicks))
			{
				verifiedSnapshot = ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 1500);
				if (!CriticalThreadObservation.Valid(verifiedSnapshot, source.Birth, AutoCores.NowTicks))
				{
					if (++criticalObservationMisses >= 3)
					{
						criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
						if (WithdrawCriticalThreadsLocked("fresh thread verification repeatedly unavailable"))
							PublishCriticalThreadState(source, "conflict", PerformancePolicy.Text("线程状态连续无法核验，已恢复原设置并停止本局重绑", "Thread state could not be verified repeatedly; original settings were restored and rebinding is stopped for this session"), false, false, 0, 0L);
						return;
					}
					PublishCriticalThreadState(source, "coverage-pending", PerformancePolicy.Text("写入已回读，等待新的线程快照；暂停新增与纠偏", "Writes were read back; a fresh thread snapshot is pending and further bindings and corrections are paused"), false, false, 0, 0L);
					return;
				}
			}
			bool flag2 = CriticalThreadRuntimePolicy.Verified(verifiedSnapshot, criticalPlan, criticalSession.ActiveReceipts, ticks, out candidates, out workers, out complete);
			if (!flag2)
			{
				if (criticalPlan.RequireWorkerCoverage)
					criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
				if (WithdrawCriticalThreadsLocked("thread coverage unconfirmed (candidates=" + candidates + "/" + criticalPlan.Choices.Length
					+ " workers=" + workers + " snapshotThreads=" + verifiedSnapshot.Ids.Length + " complete=" + complete + ")"))
				{
					PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("绑定回读或采样时效未确认，已撤回", "Readback or sample freshness was not confirmed; bindings were withdrawn"), false, false, 0, 0L);
				}
				return;
			}
			criticalObservationMisses = 0;
			if (criticalPlan.RequireWorkerCoverage && !EnsureCriticalCoreIsolation(source, criticalPlan))
			{
				string isolationFailure = criticalCoreIsolationError;
				criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
				if (WithdrawCriticalThreadsLocked("critical core isolation unavailable: " + isolationFailure))
					PublishCriticalThreadState(source, "conflict", PerformancePolicy.Text("核心隔离未确认，已撤回线程绑定：", "Core isolation was not confirmed; thread bindings were withdrawn: ") + isolationFailure, false, false, 0, 0L);
				return;
			}
			if (initial && criticalPlan.RequireWorkerCoverage)
				Logger.Info("THREAD exclusive verified: " + source.Name + " pid=" + source.Pid
					+ " physical=0x" + criticalCoreIsolationMask.ToString("X") + " workers=" + workers
					+ " workerMask=0x" + criticalPlan.Workers.ToString("X")
					+ " activationMs=" + ((Stopwatch.GetTimestamp() - batchStarted) * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture)
					+ " scope=snapshot; new threads await admission; interrupts are not isolated");
			if (criticalFailureAdmitted && (!source.Rule.WorkerAvoidance || complete))
			{
				if (!ActionFailureStore.CompleteApply("game-critical-thread-hard-affinity", criticalFailureTarget))
				{
					RestoreCriticalThreadsLocked();
					LogCriticalThreadState("withdrawn", "the apply completion could not be recorded");
					return;
				}
				criticalFailureAdmitted = false;
			}
			CriticalThreadCoverageState coverage2 = ((!source.Rule.WorkerAvoidance) ? CriticalThreadCoverageState.CandidatesOnly : (complete ? CriticalThreadCoverageState.WorkersSnapshotComplete : CriticalThreadCoverageState.WorkersPartial));
			PublishCriticalThreadState(source, (source.Rule.WorkerAvoidance && !complete) ? "coverage-pending" : "active", "", true, false, workers, nowTicks, coverage2);
		}

		private bool WithdrawCriticalThreadsLocked(string reason)
		{
			bool track = criticalSession != null || criticalPriorityReservation != null || criticalRestorationConfirmed;
			long started = Stopwatch.GetTimestamp();
			if (criticalSession != null && !criticalRestorationConfirmed)
			{
				LogCriticalThreadState("withdrawn", reason);
			}
			bool restored = RestoreCriticalThreadsLocked();
			if (track) TraceCriticalRuntime("restore", restored ? "ok" : "pending", criticalSource == null ? 0 : criticalSource.Pid, started);
			return restored;
		}

		private bool RestoreCriticalThreadsCore()
		{
			if (!Monitor.IsEntered(criticalProcessGate))
			{
				throw new InvalidOperationException("Original placement gate required");
			}
			if (criticalSession == null)
			{
                object reservationOwner = criticalPriorityReservation;
                bool priorityClean = ReleaseCriticalThreadPriorityReservation();
				bool isolationClean = StopCriticalCoreIsolation();
                if (priorityClean && isolationClean)
                {
                    if (reservationOwner != null) criticalRecoveryPhases.Remove(reservationOwner);
                    criticalOriginalTarget = null;
                }
                // Local reservation debt must not block its restoration or the independent historical recovery worker.
                criticalThreadResidue = !priorityClean || !isolationClean;
                return priorityClean && isolationClean && !CriticalThreadHistoricalRecoveryDebt(false);
			}
			if (criticalOriginalTarget != null)
			{
				criticalOriginalTarget.Revoke();
			}
			if (!criticalRestorationConfirmed && !criticalSession.Restore())
			{
				criticalThreadResidue = true;
				PublishCriticalThreadState(criticalSource, "recovery-pending", criticalSession.Detail, false, true, 0, 0L);
				return false;
			}
			criticalRestorationConfirmed = true;
			if (!StopCriticalCoreIsolation())
			{
				criticalThreadResidue = true;
				PublishCriticalThreadState(criticalSource, "recovery-pending", "Thread placement restored; core isolation restoration is pending: " + criticalCoreIsolationError, false, true, 0, 0L);
				return false;
			}
			if (criticalFailureAdmitted)
			{
				ActionFailureStore.CancelApply("game-critical-thread-hard-affinity", criticalFailureTarget);
				criticalFailureAdmitted = false;
			}
			bool closureObserved = criticalClosureObserveOnly && criticalSession.RefreshGuardianClosure();
			if (!closureObserved)
			{
				try
				{
					criticalSession.Dispose();
				}
				catch (Exception ex)
				{
					criticalHelperClosureWaiting = false;
					criticalThreadResidue = true;
					PublishCriticalThreadState(criticalSource, "recovery-pending", PerformancePolicy.Text("线程已恢复，辅助资源关闭仍待确认：", "Threads restored; helper resource closure is pending: ") + ex.Message, false, true, 0, 0L);
					return false;
				}
			}
			if (!criticalSession.ResourcesClosed)
			{
				criticalHelperClosureWaiting = true;
				criticalThreadResidue = true;
				PublishCriticalThreadState(criticalSource, "recovery-pending", PerformancePolicy.Text("线程已恢复，等待原辅助进程退出与资源关闭", "Threads restored; waiting for the original helper to exit and resources to close"), false, true, 0, 0L);
				return false;
			}
			if (!ReleaseCriticalThreadPriorityReservation())
			{
				criticalThreadResidue = true;
				PublishCriticalThreadState(criticalSource, "recovery-pending", "Thread priority restoration is unconfirmed", false, true, 0, 0L);
				return false;
			}
			criticalSession = null;
			criticalHelperClosureWaiting = false;
			InvalidateThreadBoostRecoveryAdmission();
			criticalPlan = null;
			criticalRestorationConfirmed = false;
			criticalChoiceFrameAssociated = false;
			criticalGuardianActor = null;
			criticalOriginalTarget = null;
			criticalThreadResidue = false;
			criticalHistoricalDebt = false;
			criticalDebtNextRead = 0L;
			if (criticalSource != null)
			{
				PublishCriticalThreadState(criticalSource, "observing", PerformancePolicy.Text("原线程设置已恢复；未继续绑定", "Original thread settings restored; no binding remains active"), false, false, 0, 0L);
			}
			return true;
		}

		private bool StopCriticalThreads(int timeoutMs, [CallerMemberName] string caller = null)
		{
			if (Monitor.IsEntered(sync))
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			if (!Monitor.TryEnter(criticalProcessGate, Math.Max(0, timeoutMs)))
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			try
			{
				bool flag = WithdrawCriticalThreadsLocked("stopped by " + caller);
				if (flag)
				{
					criticalSource = null;
					criticalObservation.Reset();
				}
				ResetCriticalAuto();
				return flag;
			}
			finally
			{
				Monitor.Exit(criticalProcessGate);
			}
		}

		private bool PrepareCriticalThreadProcessMaskMutation(int pid, long birth)
		{
			if (!Monitor.IsEntered(criticalProcessGate))
			{
				throw new InvalidOperationException("Original placement gate required");
			}
			CriticalThreadTargetLease criticalThreadTargetLease = criticalOriginalTarget;
			if (criticalSession == null)
			{
				return !CriticalThreadHasRecoveryDebt(true);
			}
			if (criticalThreadTargetLease == null || criticalThreadTargetLease.ProcessId != pid || criticalThreadTargetLease.ProcessCreation != birth)
			{
				return true;
			}
			RevokeCriticalThreadIntent("PrepareCriticalThreadProcessMaskMutation");
			if (!RestoreCriticalThreadsLocked())
			{
				return false;
			}
			criticalSource = null;
			criticalObservation.Reset();
			criticalNextSample = 0L;
			return true;
		}

		private bool CriticalThreadHasRecoveryDebt(bool force)
		{
			if (criticalThreadResidue)
			{
				return true;
			}
            return CriticalThreadHistoricalRecoveryDebt(force);
        }

        private bool CriticalThreadHistoricalRecoveryDebt(bool force)
        {
			long nowTicks = AutoCores.NowTicks;
			if (force || nowTicks >= Interlocked.Read(ref criticalDebtNextRead))
			{
				criticalHistoricalDebt = HardThreadFileStore.CanonicalRootHasRecoveryDebt(CriticalThreadStoreRoot);
				Interlocked.Exchange(ref criticalDebtNextRead, nowTicks + (long)(criticalHistoricalDebt ? 1 : 15) * 10000000L);
			}
			return criticalHistoricalDebt;
		}

		private void PublishCriticalThreadState(CriticalRuntimeSource source, string state, string detail, bool applied = false, bool pending = false, int workers = 0, long observed = 0L, CriticalThreadCoverageState coverage = CriticalThreadCoverageState.None)
		{
			if (source == null)
			{
				return;
			}
			LogCriticalThreadState(state, detail);
			int sameTid;
			CriticalThreadBoostCoupling coupling = CriticalThreadCoupling(source.Pid, source.Birth, applied, out sameTid);
			CriticalThreadUiState view = new CriticalThreadUiState(source.Profile, state, detail, true, applied, pending, workers, observed, (criticalPlan == null) ? 0 : criticalPlan.Workers, (applied && criticalPlan != null) ? criticalPlan.Choices.Select((HardThreadChoice x) => x.Mask).ToArray() : null, applied ? criticalChoiceLabels : null, true, source.Rule.WorkerAvoidance, coverage, source.Token, source.Serial, source.Epoch, source.Pid, source.Birth, source.RuleText, coupling, sameTid);
			lock (sync)
			{
				if (pending || CriticalThreadSnapshotCurrent(view, source.Profile))
				{
					criticalThreadUi = view;
				}
			}
		}

		private void LogCriticalThreadState(string state, string detail)
		{
			if (state == "observing") return;
			string text = state + "|" + detail;
			long nowTicks = AutoCores.NowTicks;
			lock (criticalStateLogGate)
			{
				if (criticalStateLogged == null || criticalStateLogged.Count > 64)
				{
					criticalStateLogged = new Dictionary<string, long>(StringComparer.Ordinal);
				}
				long value;
				if (text == criticalStateLast || (criticalStateLogged.TryGetValue(text, out value) && nowTicks - value < 1200000000))
				{
					return;
				}
				criticalStateLogged[text] = nowTicks;
				criticalStateLast = text;
			}
			Logger.Info("THREAD [" + AutomationSession + "] result=" + state + (string.IsNullOrEmpty(detail) ? "" : (" detail=" + detail)));
		}

		private CriticalThreadBoostCoupling CriticalThreadCoupling(int pid, long birth, bool applied, out int sameTid)
		{
			PolicySnapshot policySnapshot = sessionPolicy;
			if (policySnapshot == null)
			{
				sameTid = 0;
				return CriticalThreadBoostCoupling.Unknown;
			}
			string value = policySnapshot.ValueOf("GmThreadBoostStrategyV1");
			string mode = ((!ThreadBoostSelection.IsOn(value)) ? "off" : (LaneFallbackFor(pid, birth) ? "candidate" : "frame"));
			int pid2;
			long processBirth;
			int tid;
			long threadBirth;
			CriticalThreadLaneThread lane = (RenderLane.TryGetAppliedThread(out pid2, out processBirth, out tid, out threadBirth) ? new CriticalThreadLaneThread(pid2, processBirth, tid, threadBirth) : null);
			HardThreadPlan hardThreadPlan = criticalPlan;
			return CriticalThreadBoostCouplingPolicy.Classify(policySnapshot.EffBoost, mode, LaneSupported(policySnapshot.Preset), pid, birth, lane, (applied && hardThreadPlan != null) ? hardThreadPlan.Choices.Select((HardThreadChoice x) => x.Thread).ToArray() : null, criticalChoiceFrameAssociated, out sameTid);
		}

		private bool IsCriticalThreadGuardian(int pid, long birth)
		{
			HardThreadGuardianActor hardThreadGuardianActor = criticalGuardianActor;
			if (hardThreadGuardianActor != null && hardThreadGuardianActor.Pid == pid && birth > 0)
			{
				return hardThreadGuardianActor.Creation == birth;
			}
			return false;
		}

		private bool ReleaseCriticalGuardianBackground()
		{
			HardThreadGuardianActor hardThreadGuardianActor = criticalGuardianActor;
			if (hardThreadGuardianActor == null)
			{
				return false;
			}
			BackgroundReleaseState state = core.ReleaseBackgroundForRenderer(hardThreadGuardianActor.Pid, hardThreadGuardianActor.Creation, Path.GetFileNameWithoutExtension(hardThreadGuardianActor.Path));
			return RuntimeBackgroundExempt(state);
		}

		private bool ReleaseJobGuardianBackground(int pid, long birth, string name)
		{
			BackgroundReleaseState state = core.ReleaseBackgroundForRenderer(pid, birth, name);
			return RuntimeBackgroundExempt(state);
		}

		public GameMode(string dir, SuppressionCore core)
		{
			dataDir = dir;
			InitializeGameNvDeliveryMetadata();
			PurgeRetiredIrqObservation(dir);
			whitePath = Path.Combine(dir, "Pavise.whitelist.txt");
			autoIgnorePath = Path.Combine(dir, "Pavise.autoignore.txt");
			profileStore = new GameProfileStore(dir);
			libraryIgnoreTransaction = new LibraryIgnoreTransaction(dir, profileStore);
			if (libraryIgnoreTransaction.TryRecover())
			{
				LoadAutoIgnore();
			}
			using (Process process = Process.GetCurrentProcess())
			{
				selfPid = process.Id;
				selfName = process.ProcessName;
				try
				{
					selfSession = process.SessionId;
				}
				catch
				{
					selfSession = -1;
				}
			}
			string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			windowsPrefix = (string.IsNullOrEmpty(folderPath) ? "C:\\Windows\\" : (folderPath.TrimEnd('\\') + "\\"));
			this.core = core;
			allMask = CpuTopology.AllMask;
			ulong throttleMask = CpuTopology.ThrottleMask;
			ulong strictBoostMask = CpuTopology.StrictBoostMask;
			if (CpuTopology.Hybrid)
			{
				Logger.Log(Lang.T("log.gamemode.1") + CpuTopology.DescribeMask(CpuTopology.PerfMask) + Lang.T("log.gamemode.2") + CpuTopology.DescribeMask(CpuTopology.EffMask) + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(throttleMask) + Lang.T("log.gamemode.3") + CpuTopology.DescribeMask(strictBoostMask));
			}
			else if (CpuTopology.AsymCache)
			{
				Logger.Log(Lang.T("log.gamemode.4") + CpuTopology.DescribeMask(strictBoostMask) + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(throttleMask));
			}
			else if (CpuTopology.PartitionTag == "symmetric-ccd")
			{
				Logger.Log(Lang.T("log.gamemode.5") + CpuTopology.DescribeMask(strictBoostMask) + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(throttleMask));
			}
			else
			{
				Logger.Log(Lang.T("log.gamemode.6"));
			}
			if (CpuTopology.FavoredMask != 0)
			{
				Logger.Log(Lang.T("log.gamemode.favored") + CpuTopology.DescribeMask(CpuTopology.FavoredMask) + Lang.T("log.gamemode.favored.detail") + CpuTopology.FavoredDetail);
			}
			if (!CpuTopology.TopologySourcesAgree)
			{
				Logger.Warn(Lang.F("log.gamemode.sourcesplit", CpuTopology.TopologySourceCounts));
			}
			if (CpuTopology.AllMaskReconciled)
			{
				Logger.Warn(Lang.F("log.gamemode.countsplit", Environment.ProcessorCount, CpuTopology.CountSetBits(CpuTopology.AllMask)));
			}
			if (CpuTopology.CpuSetPartitionRejected)
			{
				Logger.Log(Lang.T("log.gamemode.8"));
			}
			if (CpuTopology.StrictMaskUnsafe)
			{
				Logger.Log(Lang.T("log.gamemode.9"));
			}
			int result;
			exitGraceSeconds = ((int.TryParse(Settings.LoadStr("GameExitGraceSeconds", "8"), out result) && IsExitGraceChoice(result)) ? result : 8);
			bgSuppressOn = Settings.Load("GmSuppress", true);
			boostOn = Settings.Load("GmBoost", true);
			pauseUpdateOn = Settings.Load("GmPauseUpdate", false);
			pauseMaintOn = Settings.Load("GmPauseMaintenanceV1", true);
			pauseServicesOn = false;
			Settings.Remove("GmPauseServices");
			InitializeStandbyCleaner();
			InitializeEnglishInput();
			nvMaxPerf = Settings.Load("NvMaxPerf", false);
			nvVrrWindowedOn = Settings.Load("NvVrrWindowed", false);
			intelEnduranceOn = Settings.Load("GmIntelEnduranceV1", false);
			nvLowLatMode = Settings.LoadStr("NvLowLat", "off");
			nvSmoothMotion = Settings.Load("NvSmoothMotion", false);
			nvShaderCacheMax = Settings.Load("NvShaderCache", false);
			nvRebarOn = Settings.Load("NvRebar", false);
			nvDlssMode = Settings.LoadStr("NvDlss", "off");
			gpuPrefStageOn = Settings.Load("GpuPrefStageOn", true);
			dwmBoostOn = Settings.Load("GmDwmBoost", false);
			Settings.Remove("GmWsTrimV1");
			Settings.Remove("GmIdlePolicyV1");
			Settings.Remove("AutoBackgroundCoreLayoutV1");
			Settings.Remove("AutoLaptopPerformanceV1");
			Settings.Remove("GmLaptopPerfV1");
			Settings.Remove("MatchPausePcaV1");
			PrivacyControl.ClearRetiredPreferences();
			Settings.Remove("GmAudioLowLatV1");
			amdAfmf = Settings.Load("AmdAfmf", false);
			rsrOn = Settings.Load("GmRsr", false);
			autoAddOn = Settings.Load("GmAutoAdd", false);
			Settings.Remove("GmCacheWarm");
			cacheWarmOn = Settings.Load("GmCacheWarmV2", false);
			killGameDvr = Settings.Load("GameDvrOff", true);
			mmcssOn = Settings.Load("GmMmcss", true);
			string[] retiredSettings = AutoCores.RetiredSettings;
			foreach (string name in retiredSettings)
			{
				Settings.Remove(name);
			}
			planSwitch = PolicyResolver.Global().PowerPlanOn;
			renderLaneOn = PolicyResolver.Global().RenderLane;
			SuppressionCore.GpuDemoteEnabled = bgSuppressOn;
			string[] envKeys = EnvKeys;
			foreach (string text in envKeys)
			{
				if (Settings.Load("EnvFuse_" + text, false))
				{
					envFused.Add(text);
				}
			}
			int result2;
			preset = (int.TryParse(Settings.LoadStr("PerformancePreset", "0"), out result2) ? PresetValue.From(result2) : PerformancePreset.Standard);
			try
			{
				if (!File.Exists(whitePath) && !WritePreset())
				{
					throw new IOException(Lang.T("t.gamemode.10"));
				}
				bool flag = false;
				bool flag2 = false;
				bool flag3 = true;
				bool flag4 = false;
				bool flag5 = false;
				bool flag6 = false;
				string a = null;
				List<WhitelistRule> list = new List<WhitelistRule>();
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				string[] array = File.ReadAllLines(whitePath);
				foreach (string text2 in array)
				{
					string text3 = text2.Trim();
					if (text3.Length == 0 || text3.StartsWith("#"))
					{
						continue;
					}
					if (string.Equals(text3, "PAVISE_WHITELIST_V4", StringComparison.Ordinal) || string.Equals(text3, "PAVISE_WHITELIST_V3", StringComparison.Ordinal) || string.Equals(text3, "PAVISE_WHITELIST_V2", StringComparison.Ordinal))
					{
						if (flag || flag4 || flag5)
						{
							throw new InvalidDataException(Lang.T("t.gamemode.11"));
						}
						flag = true;
						flag3 = !string.Equals(text3, "PAVISE_WHITELIST_V4", StringComparison.Ordinal);
						flag2 = string.Equals(text3, "PAVISE_WHITELIST_V2", StringComparison.Ordinal);
						continue;
					}
					if (text3.StartsWith("PAVISE_WHITELIST_END|", StringComparison.Ordinal))
					{
						if (!flag || flag5)
						{
							throw new InvalidDataException(Lang.T("t.gamemode.12"));
						}
						flag5 = true;
						a = text3;
						continue;
					}
					if (flag5)
					{
						throw new InvalidDataException(Lang.T("t.gamemode.13"));
					}
					WhitelistRule rule2;
					if (flag)
					{
						if (!WhitelistRule.TryParseVersioned(text3, out rule2))
						{
							throw new InvalidDataException(Lang.T("t.gamemode.14"));
						}
					}
					else
					{
						if (text3.Length > 1 && text3[1] == '|' && (text3[0] == 'N' || text3[0] == 'n' || text3[0] == 'P' || text3[0] == 'p' || text3[0] == 'F' || text3[0] == 'f'))
						{
							throw new InvalidDataException(Lang.T("t.gamemode.15"));
						}
						if (!WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, text3, out rule2))
						{
							throw new InvalidDataException(Lang.T("t.gamemode.16"));
						}
					}
					flag4 = true;
					if (!hashSet.Add(rule2.Key))
					{
						throw new InvalidDataException(Lang.T("t.gamemode.17"));
					}
					list.Add(rule2);
				}
				if (flag && flag5)
				{
					if (!string.Equals(a, BuildWhitelistFooter(list), StringComparison.Ordinal))
					{
						throw new InvalidDataException(Lang.T("t.gamemode.18"));
					}
					flag6 = flag2;
				}
				else
				{
					if (flag && !flag2)
					{
						throw new InvalidDataException(Lang.T("t.gamemode.19"));
					}
					if (flag)
					{
						if (!flag4)
						{
							throw new InvalidDataException(Lang.T("t.gamemode.20"));
						}
						flag6 = true;
					}
					else
					{
						if (!flag4)
						{
							throw new InvalidDataException(Lang.T("t.gamemode.21"));
						}
						flag6 = true;
					}
				}
				if (flag3)
				{
					hashSet.Clear();
					foreach (WhitelistRule item in list)
					{
						hashSet.Add(item.Key);
					}
					flag6 = true;
				}
				string[] presetWhitelist = SystemProcessCatalog.PresetWhitelist;
				foreach (string value in presetWhitelist)
				{
					WhitelistRule rule3;
					if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, value, out rule3) && hashSet.Add(rule3.Key))
					{
						list.Add(rule3);
						flag6 = true;
					}
				}
				foreach (WhitelistRule item2 in list)
				{
					AddWhiteRuleNoSave(item2);
				}
				if (flag6 && !SaveWhite(list))
				{
					Logger.Log(Lang.T("log.gamemode.24"));
				}
			}
			catch (Exception ex)
			{
				string[] presetWhitelist2 = SystemProcessCatalog.PresetWhitelist;
				foreach (string n in presetWhitelist2)
				{
					AddWhiteNoSave(n);
				}
				Logger.Log(Lang.T("log.gamemode.25") + ex.Message);
			}
			try
			{
				profiles.AddRange(profileStore.LoadProfiles(!libraryIgnoreTransaction.RecoveryPending));
				List<GameProfile> list2 = GetProfiles();
				if (!ProfileStoreSaveFailed && !libraryIgnoreTransaction.RecoveryPending && RefreshLibraryInstallRoots(list2) && SaveProfileSnapshotLocked(list2))
				{
					profiles.Clear();
					profiles.AddRange(list2);
				}
			}
			catch
			{
			}
			InitializeRendererObservations();
		}

		private static void PurgeRetiredIrqObservation(string dir)
		{
			Settings.Remove("IrqSessionProbe");
			Settings.Remove("IrqObserveSkipsV1");
			Settings.Remove("IrqLastCaptureResultV1");
			if (string.IsNullOrEmpty(dir))
			{
				return;
			}
			try
			{
				if (!Directory.Exists(dir))
				{
					return;
				}
				string[] retiredIrqObservationFiles = RetiredIrqObservationFiles;
				foreach (string searchPattern in retiredIrqObservationFiles)
				{
					string[] files = Directory.GetFiles(dir, searchPattern);
					foreach (string path in files)
					{
						try
						{
							File.Delete(path);
						}
						catch
						{
						}
					}
				}
			}
			catch
			{
			}
		}

		private bool WritePreset()
		{
			List<string> list = new List<string>();
			list.Add(Lang.T("t.gamemode.26"));
			list.Add(Lang.T("t.gamemode.27"));
			list.Add(Lang.T("t.gamemode.28"));
			list.Add(Lang.T("t.gamemode.29"));
			list.Add(Lang.T("lib.family.whitelist.note"));
			list.Add("PAVISE_WHITELIST_V4");
			List<WhitelistRule> list2 = new List<WhitelistRule>();
			string[] presetWhitelist = SystemProcessCatalog.PresetWhitelist;
			foreach (string value in presetWhitelist)
			{
				WhitelistRule rule;
				if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, value, out rule))
				{
					list2.Add(rule);
					list.Add(rule.Serialize());
				}
			}
			list.Add(BuildWhitelistFooter(list2));
			return AtomicFile.WriteLines(whitePath, list.ToArray(), Lang.T("t.gamemode.31"));
		}

		private void AddWhiteNoSave(string n)
		{
			WhitelistRule rule;
			if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, n, out rule))
			{
				AddWhiteRuleNoSave(rule);
			}
		}

		private static string StripExe(string s)
		{
			if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return s;
			}
			return s.Substring(0, s.Length - 4);
		}

		private void SyncGameDvr()
		{
			SyncGuardScheduling();
		}

		private bool SyncGuardScheduling()
		{
			lock (sync)
			{
				SyncAutomationPolicy();
				if (!enabled || stopping || ProfileStoreSaveFailed)
				{
					return SetGuardScheduling(false, false);
				}
				guardDvrActive = EnvStep("gamedvr", killGameDvr, guardDvrActive || (!killGameDvr && GameDvr.HasResidue), GameDvr.Activate, GameDvr.Restore);
				guardMmcssActive = EnvStep("mmcss", mmcssOn, guardMmcssActive || (!mmcssOn && Mmcss.HasResidue()), Mmcss.Activate, Mmcss.Restore);
				bool flag = guardDvrActive == killGameDvr && !envFused.Contains("gamedvr") && !envRestorePending.Contains("gamedvr");
				bool flag2 = guardMmcssActive == mmcssOn && !envFused.Contains("mmcss") && !envRestorePending.Contains("mmcss");
				if (flag)
				{
					RecordAutomation("gamedvr", killGameDvr ? "applied" : "restored");
				}
				if (flag2)
				{
					RecordAutomation("mmcss", mmcssOn ? "applied" : "restored");
				}
				return flag && flag2;
			}
		}

		private bool RestoreGuardScheduling()
		{
			lock (sync)
			{
				return SetGuardScheduling(false, false);
			}
		}

		private bool SetGuardScheduling(bool dvr, bool mmcss)
		{
			bool skipped; string reason;
			bool flag = dvr ? RunGuarded(GameDvr.Activate) : RunAutomaticRestoreCore("gamedvr", GameDvr.Restore, out skipped, out reason);
			bool flag2 = mmcss ? RunGuarded(Mmcss.Activate) : RunAutomaticRestoreCore("mmcss", Mmcss.Restore, out skipped, out reason);
			if (flag)
			{
				guardDvrActive = dvr;
				envRestorePending.Remove("gamedvr");
				envFused.Remove("gamedvr");
				envNextAttempt.Remove("gamedvr");
				envFailures.Remove("gamedvr");
			}
			if (flag2)
			{
				guardMmcssActive = mmcss;
				envRestorePending.Remove("mmcss");
				envFused.Remove("mmcss");
				envNextAttempt.Remove("mmcss");
				envFailures.Remove("mmcss");
			}
			RecordAutomation("gamedvr", (!flag) ? (dvr ? "failed" : "restore-failed") : (dvr ? "applied" : "restored"));
			RecordAutomation("mmcss", (!flag2) ? (mmcss ? "failed" : "restore-failed") : (mmcss ? "applied" : "restored"));
			if (flag)
			{
				return flag2;
			}
			return false;
		}

		private void InitializeEnglishInput()
		{
			Interlocked.Exchange(ref englishInputCreationFloor, DateTime.UtcNow.ToFileTimeUtc());
			englishInputOn = Settings.Load("GmEnglishInputV1", false);
			englishInputOnce = new EnglishInputOnce(new GameInputLanguage());
		}

		private bool LiveEnglishInputPreference()
		{
			return LiveBoolPreferenceLocked("GmEnglishInputV1", englishInputOn);
		}

		private Func<bool> CaptureEnglishInputAdmission(bool entry, out string profileId, out string profileName, out GameInputProcess process, out bool wanted)
		{
			int generation;
			long creationFloor;
			string id;
			GameInputProcess renderer;
			bool preference;
			lock (sync)
			{
				generation = Volatile.Read(ref englishInputGeneration);
				creationFloor = Volatile.Read(ref englishInputCreationFloor);
				GameDetection gameDetection = activeDetection;
				profileId = (id = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Id : null));
				profileName = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Name : "");
				bool flag = gameDetection != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm;
				process = (renderer = (flag ? new GameInputProcess(gameDetection.RendererPid, gameDetection.RendererCreation) : default(GameInputProcess)));
				PolicySnapshot policySnapshot = sessionPolicy;
				wanted = (preference = LiveEnglishInputPreference() && !string.IsNullOrEmpty(id) && creationFloor > 0 && (!renderer.IsValid || renderer.Creation >= creationFloor) && (policySnapshot == null || string.IsNullOrEmpty(policySnapshot.ProfileId) || string.Equals(policySnapshot.ProfileId, id, StringComparison.OrdinalIgnoreCase)));
			}
			return delegate
			{
				if (!preference || creationFloor != Volatile.Read(ref englishInputCreationFloor) || generation != Volatile.Read(ref englishInputGeneration) || !Volatile.Read(ref active) || !enabled || stopping || panicReq || Volatile.Read(ref standbyCleanerRestorePending) != 0 || ProfileStoreSaveFailed || Volatile.Read(ref stickyGraceOnly) || Volatile.Read(ref gameGoneSinceTicks) != 0)
				{
					return false;
				}
				GameDetection gameDetection2 = Volatile.Read(ref activeDetection);
				if (gameDetection2 == null || gameDetection2.Profile == null || !string.Equals(gameDetection2.Profile.Id, id, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				bool flag2 = gameDetection2.RendererCandidateSelected && !gameDetection2.RendererSafetyOnly && !gameDetection2.RequiresGpuConfirm;
				if (flag2 && gameDetection2.RendererCreation > 0 && gameDetection2.RendererCreation < creationFloor)
				{
					return false;
				}
				return entry || (flag2 && renderer.IsValid && renderer.Creation >= creationFloor && gameDetection2.RendererPid == renderer.Pid && gameDetection2.RendererCreation == renderer.Creation);
			};
		}

		private void BeginEnglishInputSession()
		{
			EnglishInputOnce englishInputOnce = this.englishInputOnce;
			if (englishInputOnce != null)
			{
				string profileId;
				string profileName;
				GameInputProcess process;
				bool wanted;
				Func<bool> admission = CaptureEnglishInputAdmission(true, out profileId, out profileName, out process, out wanted);
				englishInputOnce.Begin(profileId, process, wanted, admission);
			}
		}

		private void StepEnglishInputSession()
		{
			EnglishInputOnce englishInputOnce = this.englishInputOnce;
			if (englishInputOnce == null)
			{
				return;
			}
			string profileId;
			string profileName;
			GameInputProcess process;
			bool wanted;
			Func<bool> admission = CaptureEnglishInputAdmission(false, out profileId, out profileName, out process, out wanted);
			EnglishInputOutcome englishInputOutcome = englishInputOnce.Step(profileId, process, admission);
			if (englishInputOutcome != null)
			{
				string key;
				switch (englishInputOutcome.Result)
				{
				case EnglishInputResult.Changed:
					key = "englishinput.changed";
					break;
				case EnglishInputResult.AlreadyEnglish:
					key = "englishinput.already";
					break;
				case EnglishInputResult.Unconfirmed:
					key = "englishinput.unconfirmed";
					break;
				case EnglishInputResult.Denied:
					key = "englishinput.denied";
					break;
				case EnglishInputResult.NoEnglishLayout:
					key = "englishinput.nolayout";
					break;
				case EnglishInputResult.TargetChanged:
					key = "englishinput.targetchanged";
					break;
				case EnglishInputResult.Expired:
					key = "englishinput.expired";
					break;
				case EnglishInputResult.Canceled:
					key = "englishinput.canceled";
					break;
				case EnglishInputResult.HistoryFull:
					key = "englishinput.historyfull";
					break;
				default:
					key = "englishinput.unavailable";
					break;
				}
				Logger.Log(Lang.F("log.englishinput.result", profileName, Lang.T(key), englishInputOutcome.Error));
			}
		}

		private void InvalidateEnglishInputWork()
		{
			Interlocked.Increment(ref englishInputGeneration);
			EnglishInputOnce englishInputOnce = this.englishInputOnce;
			if (englishInputOnce != null)
			{
				englishInputOnce.Cancel();
			}
		}

		private void EndEnglishInputSession()
		{
			InvalidateEnglishInputWork();
			EnglishInputOnce englishInputOnce = this.englishInputOnce;
			if (englishInputOnce != null)
			{
				englishInputOnce.End();
			}
		}

		private bool DrainEnglishInput(int timeoutMs)
		{
			EnglishInputOnce englishInputOnce = this.englishInputOnce;
			if (englishInputOnce != null)
			{
				return englishInputOnce.Drain(timeoutMs);
			}
			return true;
		}

		private static string EnvLabel(string key)
		{
			switch (key)
			{
			case "do":
				return Lang.T("t.gamemodeenv.1");
			case "wu":
				return Lang.T("t.gamemodeenv.3");
			case "services":
				return Lang.T("gm.pausesvc");
			case "standby":
				return Lang.T("gm.standbycleaner");
			case "pqos":
				return Lang.T("t.gamemodeenv.4");
			case "awake":
				return Lang.T("t.gamemodeenv.5");
			case "dwmboost":
				return Lang.T("gm.dwmboost");
			case "rsr":
				return Lang.T("set.rsr");
			case "gpupower":
				return Lang.T("t.gamemodeenv.6");
			case "maint":
				return Lang.T("gm.pausemaint");
			case "nvvrr":
				return Lang.T("set.nvvrr");
			case "intelend":
				return Lang.T("set.intel.endurance");
			case "laptopperf":
				return Lang.T("gm.laptopperf");
			case "amdalag":
				return "AMD Anti-Lag";
			case "amdafmf":
				return Lang.T("t.gamemodeenv.8");
			case "GameDvrOff":
				return Lang.T("set.dvr");
			case "GmMmcss":
				return Lang.T("auto.mmcss");
			case "gamedvr":
				return Lang.T("set.dvr");
			case "mmcss":
				return Lang.T("auto.mmcss");
			default:
				return key;
			}
		}

		private bool EnvStep(string key, bool want, bool active, Func<bool> activate, Func<bool> restore)
		{
			long session = Interlocked.Read(ref gameSessionStarts);
			bool originalActive = active;
			string action = EnvFailureAction(key);
			bool flag;
			lock (sync)
			{
				if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
				flag = SyncEnvFailureIsolation(key);
				if (envRestorePending.Contains(key))
				{
					want = false;
					active = true;
				}
			}
			if (want && flag)
			{
				want = false;
			}
			if (want == active)
			{
				return active;
			}
			long ticks = DateTime.UtcNow.Ticks;
			lock (sync)
			{
				long value;
				if (envNextAttempt.TryGetValue(key, out value) && ticks < value)
				{
					if (!want) { if (value == long.MaxValue) RecoveryAttemptTrace.Exhausted(); else RecoveryAttemptTrace.DeferredUntil(value); }
					return active;
				}
			}
			string reason;
			if (want && (activate == null || !EnvironmentApplyReady(key))) return active;
			lock (sync)
			{
				if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
				if (want && !ActionFailureStore.TryBeginApply(action, "system", out reason)) return active;
			}
			bool skipped;
			bool flag2 = want ? RunEnvironmentAction(activate, out skipped, out reason) : RunEnvStepRestore(session, key, restore, out skipped, out reason);
			if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
			if (want && skipped)
			{
				bool skipped2;
				string reason2;
				bool flag3 = RunEnvStepRestore(session, key, restore, out skipped2, out reason2);
				lock (sync)
				{
					if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
					if (flag3)
					{
						envRestorePending.Remove(key);
					}
					else
					{
						envRestorePending.Add(key);
					}
					if (flag3)
					{
						ActionFailureStore.CancelApply(action, "system");
					}
					else
					{
						ActionFailureStore.Trip(action, "system", PerformancePolicy.Text("取消或跳过后的恢复失败：", "Recovery after cancellation or skip failed: ") + reason2);
					}
				}
				return !flag3;
			}
			lock (sync)
			{
				if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
				RecordAutomation(key, (!flag2) ? (want ? "failed" : "restore-failed") : ((!want) ? "restored" : ((key == "services" && OptionalServicePause.PendingStopCount > 0) ? "unverified" : "applied")));
			}
			if (want)
			{
				if (flag2)
				{
					bool flag4;
					lock (sync)
					{
						if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
						flag4 = ActionFailureStore.CompleteApply(action, "system");
					}
					if (!flag4)
					{
						bool skipped3;
						string reason3;
						bool flag5 = RunEnvStepRestore(session, key, restore, out skipped3, out reason3);
						lock (sync)
						{
							if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
							RecordAutomation(key, flag5 ? "restored" : "restore-failed");
							if (flag5)
							{
								envRestorePending.Remove(key);
							}
							else
							{
								envRestorePending.Add(key);
							}
							if (flag5)
							{
								ActionFailureStore.CancelApply(action, "system");
							}
							else
							{
								ActionFailureStore.Trip(action, "system", PerformancePolicy.Text("完成记录失败且回退失败：", "Completion persistence and rollback failed: ") + reason3);
							}
							SyncEnvFailureIsolation(key);
						}
						return !flag5;
					}
					lock (sync)
					{
						if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
						envNextAttempt.Remove(key);
						envFailures.Remove(key);
						SyncEnvFailureIsolation(key);
						if (flag4 && Settings.Load("EnvFuse_" + key, false))
						{
							Settings.Save("EnvFuse_" + key, false);
						}
					}
					return true;
				}
				lock (sync)
				{
					if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
					ActionFailureStore.Trip(action, "system", reason);
					envFused.Add(key);
				}
				Logger.Warn(EnvLabel(key) + PerformancePolicy.Text("：执行失败，本阶段停止应用并核验恢复。", ": apply failed; this phase is stopped and restoration is being verified."));
				bool skipped4;
				string reason4;
				bool flag6 = RunEnvStepRestore(session, key, restore, out skipped4, out reason4);
				lock (sync)
				{
					if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
					RecordAutomation(key, flag6 ? "restored" : "restore-failed");
					if (flag6)
					{
						envRestorePending.Remove(key);
						envNextAttempt.Remove(key);
						envFailures.Remove(key);
					}
					else
					{
						envRestorePending.Add(key);
					}
					if (flag6)
					{
						ActionFailureStore.CancelApply(action, "system");
					}
					else
					{
						ActionFailureStore.Trip(action, "system", reason + PerformancePolicy.Text("；回退失败：", "; rollback failed: ") + reason4);
					}
				}
				return !flag6;
			}
			lock (sync)
			{
				if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
				if (flag2)
				{
					ActionFailureStore.CancelApply(action, "system");
					envRestorePending.Remove(key);
					envNextAttempt.Remove(key);
					envFailures.Remove(key);
				}
				else
				{
					envRestorePending.Add(key);
					if (!skipped)
					{
						ActionFailureStore.Trip(action, "system", PerformancePolicy.Text("恢复失败：", "Recovery failed: ") + reason);
					}
				}
			}
			if (!flag2)
			{
				return active;
			}
			return false;
		}

		private bool RunEnvStepRestore(long session, string key, Func<bool> restore, out bool skipped, out string reason)
		{
			// Charge recovery to the admitting session; publication is fenced separately by the caller.
			if (session != Interlocked.Read(ref gameSessionStarts))
			{ skipped = true; reason = "superseded environment step"; return false; }
			if (CapturingSessionRecovery) return RunAutomaticRestore(key, restore, out skipped, out reason);
			return RunAutomaticRestoreCore(key, restore, out skipped, out reason, session);
		}

		private static string EnvFailureAction(string key)
		{
			string text = EnvPolicyKey(key);
			if (text != null)
			{
				return text;
			}
			switch (key)
			{
			case "rsr":
				return "GmRsr";
			case "nvvrr":
				return "NvVrrWindowed";
			case "pqos":
				return "PresenceQos";
			default:
				return key;
			}
		}

		private bool SyncEnvFailureIsolation(string key)
		{
			bool flag = ActionFailureStore.IsBlocked(EnvFailureAction(key), "system");
			lock (sync)
			{
				if (flag)
				{
					envFused.Add(key);
				}
				else
				{
					envFused.Remove(key);
				}
			}
			return flag;
		}

		private bool EnvironmentApplyReady(string key)
		{
			switch (key)
			{
			case "maint":
			case "pqos":
			case "do":
			case "wu":
			case "services":
				if (!Native.IsElevated())
				{
					return false;
				}
				break;
			}
			if (key == "do" && Native.IsDomainJoined() && SvcState.Query("DoSvc") == 1 && !DoTweak.HasResidue)
			{
				return false;
			}
			switch (key)
			{
			case "maint":
				return Native.IsElevated();
			case "rsr":
				return AdlxTweaks.RsrSupported();
			case "amdafmf":
				return AdlxTweaks.AfmfSupported();
			default:
				return true;
			}
		}

		private static bool RunEnvironmentAction(Func<bool> action, out bool skipped, out string reason)
		{
			skipped = action == null;
			reason = "";
			if (skipped)
			{
				return false;
			}
			try
			{
				if (action())
				{
					return true;
				}
				reason = PerformancePolicy.Text("执行返回失败", "The action returned failure");
			}
			catch (NotSupportedException)
			{
				skipped = true;
			}
			catch (OperationCanceledException)
			{
				skipped = true;
			}
			catch (Exception ex3)
			{
				reason = ex3.GetType().Name + ": " + ex3.Message;
			}
			return false;
		}

		private static bool RunGuarded(Func<bool> action)
		{
			try
			{
				return action != null && action();
			}
			catch
			{
				return false;
			}
		}

		private void ClearEnvRetryState()
		{
			lock (sync)
			{
				foreach (string key in new List<string>(envNextAttempt.Keys))
					if (!envRestorePending.Contains(key)) envNextAttempt.Remove(key);
			}
		}

		private void BeginRetrySession()
		{
			SyncAutomaticRetrySession();
			CoreIsolationClient.SyncAutomaticSession(Interlocked.Read(ref gameSessionStarts));
			SelfYield.BeginSession(Interlocked.Read(ref gameSessionStarts));
			GameDetection retryDetection = Volatile.Read(ref activeDetection);
			RenderLane.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), retryDetection == null ? 0 : retryDetection.RendererPid,
				retryDetection == null ? 0 : retryDetection.RendererCreation, "lane:" + EffLane);
			PerformanceTuning.BeginRecoveryCycle(Interlocked.Read(ref gameSessionStarts));
			string[] envKeys = EnvKeys;
			foreach (string key in envKeys)
			{
				ActionFailureStore.BeginAutomaticCycle(EnvFailureAction(key), "system", AutomaticPhaseKey(key));
				SyncEnvFailureIsolation(key);
			}
			PowerPlanStopped(lastPowerPolicyKey);
		}

		private void DisableEnvSwitch(string key)
		{
			ActionFailureStore.Trip(EnvFailureAction(key), "system", PerformancePolicy.Text("执行失败，已停止后续应用", "Apply failed; later attempts isolated"));
			lock (sync)
			{
				envFused.Add(key);
			}
		}

		private static string EnvPolicyKey(string key)
		{
			switch (key)
			{
			case "do":
				return "GmPauseDl";
			case "wu":
				return "GmPauseUpdate";
			case "dwmboost":
				return "GmDwmBoost";
			case "gpupower":
				return "GmGpuPowerMax";
			case "services":
				return "GmPauseServices";
			case "standby":
				return "GmStandbyCleanerV2";
			case "awake":
				return "GmAwake";
			case "maint":
				return "GmPauseMaintenanceV1";
			case "intelend":
				return "GmIntelEnduranceV1";
			case "laptopperf":
				return LaptopPerfMode.PreferenceKey;
			case "amdalag":
				return "AmdAntiLag";
			case "amdafmf":
				return "AmdAfmf";
			case "gamedvr":
				return "GameDvrOff";
			case "mmcss":
				return "GmMmcss";
			default:
				return null;
			}
		}

		private void ClearEnvFuse(string key)
		{
			SyncEnvFailureIsolation(key);
		}

		private bool ProcessorTuningRequested(PolicySnapshot effective)
		{
			return effective != null && effective.ProcessorTuning;
		}

		internal static int PowerPolicyKey(bool processorTuning, bool usePlan)
		{
			return (processorTuning ? 1 : 0) | (usePlan ? 2 : 0);
		}

		private void ApplyEnv()
		{
			SyncTimerAutomaticCycle();
			long ticks = DateTime.UtcNow.Ticks;
			SyncAutomationPolicy();
			PolicySnapshot policySnapshot = sessionPolicy;
			PolicySnapshot policySnapshot2 = policySnapshot ?? PolicyResolver.Global();
			if (policySnapshot2.GlobalTimerLease && timerMode.GlobalReady)
			{
				RunGuarded(() => timerLease.Update(true, DateTime.UtcNow.Ticks));
			}
			else
			{
				timerLease.Update(false, DateTime.UtcNow.Ticks);
			}
			RecordAutomation("AutoGlobalTimer", AutomaticExecutionState("AutoGlobalTimer"));
			SyncGameDvr();
			PerformancePreset mode = policySnapshot2.Preset;
			bool pauseDownloads = BackgroundSessionPolicy.PauseDownloads(policySnapshot2, enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed, activeDetection);
			bool pauseUpdate = policySnapshot2.PauseUpdate;
			bool pauseMaintenance = policySnapshot2.PauseMaintenance;
			bool awake = BackgroundSessionPolicy.KeepAwake(enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed, activeDetection);
			bool powerPlanOn = policySnapshot2.PowerPlanOn;
			bool flag = false;
			bool dwmBoost = policySnapshot2.DwmBoost;
			bool flag3 = slowEnvAtTicks == 0 || ticks >= slowEnvAtTicks;
			bool want = pauseDownloads && flag3 && Native.IsElevated() && !SessionServiceSuspended("do", ticks)
				&& (doActive || DoTweak.HasResidue || DeliveryOptimizationActivity.IsDownloading());
			bool want2 = pauseUpdate && flag3 && !SessionServiceSuspended("wu", ticks);
			bool want3 = pauseMaintenance && flag3 && !SessionServiceSuspended("maint", ticks);
			bool flag4 = ResolvePowerPlanEnabled(mode, powerPlanOn);
			bool flag6 = flag4;
			SuppressionCore.GpuDemoteEnabled = policySnapshot2.GpuDemote;
			doActive = EnvStep("do", want, doActive, DoTweak.Activate, DoTweak.Restore);
			wuActive = EnvStep("wu", want2, wuActive, UpdatePause.Activate, UpdatePause.Restore);
			maintActive = EnvStep("maint", want3, maintActive, MaintenancePause.Activate, MaintenancePause.Restore);
			RestoreRetiredOptionalServices();
			pqosActive = EnvStep("pqos", true, pqosActive, PresenceQos.Activate, PresenceQos.Restore);
			StepDisplayAwake(awake);
			dwmBoostActive = EnvStep("dwmboost", dwmBoost, dwmBoostActive, DwmBoost.Activate, DwmBoost.Restore);
			rsrActive = EnvStep("rsr", rsrOn, rsrActive, AdlxTweaks.ActivateRsr, AdlxTweaks.RestoreRsr);
			gpwActive = EnvStep("gpupower", false, gpwActive || GpuPowerMax.HasResidue(), null, GpuPowerMax.Restore);
			nvVrrActive = EnvStep("nvvrr", nvVrrWindowedOn && NvApi.Available, nvVrrActive || (!nvVrrWindowedOn && NvVrrWindowed.HasResidue), NvVrrWindowed.Activate, NvVrrWindowed.Restore);
			intelEndActive = EnvStep("intelend", EffIntelEnduranceOff && IntelGraphicsApi.HasAvailable && IntelEndurance.Supported(), intelEndActive, IntelEndurance.Activate, IntelEndurance.Restore);
			amdAlagActive = EnvStep("amdalag", false, amdAlagActive || AdlxTweaks.HasAntiLagResidue(), null, RestoreAmdAntiLagEnv);
			amdAfmfActive = EnvStep("amdafmf", flag && AdlxTweaks.AfmfSupported(), amdAfmfActive, AdlxTweaks.ActivateAfmf, AdlxTweaks.RestoreAfmf);
			StepLaptopPerformancePolicy();
			StepPowerPlanMaintenance();
			bool flag7 = ProcessorTuningRequested(policySnapshot2);
			int num = PowerPolicyKey(flag7, flag6);
			long ticks2 = DateTime.UtcNow.Ticks;
			bool flag8 = RestoreFailedPowerPlan(ticks2);
			if (flag6 && !stopping && flag8)
			{
				if (PowerPlanStopped(num) || !AutomaticWorkAllowed("power-queue") || (planActive && num == lastPowerPolicyKey && ticks2 < Interlocked.Read(ref nextPowerAuditTicks)) || Interlocked.CompareExchange(ref powerApplyInFlight, 1, 0) != 0)
				{
					return;
				}
				int num2 = num;
				bool aggrShot = flag7;
				int genShot = Volatile.Read(ref powerSessionGen);
				planActive = true;
				lastPowerPolicyKey = num2;
				Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
				bool flag9 = false;
				try
				{
					flag9 = ThreadPool.QueueUserWorkItem(delegate
					{
						RunPowerPlanApply(genShot, () => PowerPlan.Enforce(aggrShot));
					});
				}
				catch
				{
				}
				if (!flag9)
				{
					RecordAutomaticWorkFailure("power-queue", "queue-rejected");
					planActive = false;
					lastPowerPolicyKey = -1;
					Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
					Interlocked.Exchange(ref powerApplyInFlight, 0);
				}
			}
			else
			{
				if (!planActive || !flag8)
				{
					return;
				}
				Interlocked.Increment(ref powerSessionGen);
				lock (powerApplyGate)
				{
					if (RunAutomaticRestore("powerplan", () => RestoreLaptopPerformanceForPowerPlan() && PowerPlan.Restore()))
					{
						planActive = false;
						lastPowerPolicyKey = -1;
						Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
					}
				}
			}
		}

		private void RestoreRetiredOptionalServices()
		{
			optionalServicesActive = RestoreRetiredServiceDebt(optionalServicesActive, () => OptionalServicePause.HasResidue, OptionalServicePause.Restore);
		}

		private bool RestoreRetiredServiceDebt(bool pending, Func<bool> hasResidue, Func<bool> restore)
		{
			return EnvStep("services", false, pending || hasResidue(), null, restore);
		}

		private void IsolateOptionalServiceAudit(string reason)
		{
			ActionFailureStore.Trip(EnvFailureAction("services"), "system", reason ?? "Service pause verification failed");
			lock (sync)
			{
				envRestorePending.Add("services");
			}
		}

		private Func<bool> CaptureOptionalServicesAdmission()
		{
			int generation = Volatile.Read(ref optionalServiceGeneration);
			return () => generation == Volatile.Read(ref optionalServiceGeneration) && !stopping && !panicReq && enabled && !ProfileStoreSaveFailed && OptionalServicesRequested;
		}

		private bool StepOptionalServices(bool want, bool applied, Func<bool> hasResidue, Func<bool> isApplied, Func<bool> activate, Func<bool> restore)
		{
			lock (sync)
			{
				if (optionalServicesWanted != want)
				{
					optionalServicesWanted = want;
					if (!envRestorePending.Contains("services"))
					{
						envNextAttempt.Remove("services");
						envFailures.Remove("services");
					}
				}
			}
			if (want)
			{
				applied = isApplied();
			}
			else if (hasResidue())
			{
				applied = true;
			}
			bool flag = EnvStep("services", want, applied, activate, restore);
			if (!want)
			{
				return flag;
			}
			if (flag)
			{
				return isApplied();
			}
			return false;
		}

		private static bool RestoreAmdAntiLagEnv()
		{
			bool flag = AdlxTweaks.RestoreAntiLag();
			return flag & AdlxTweaks.RestoreChill();
		}

		private static bool RestorePowerPlan()
		{
			return PowerPlan.Restore();
		}

		private void RollbackPowerPlan(string reason)
		{
			Interlocked.Exchange(ref laptopPerfPowerGeneration, -1);
			bool flag = RunAutomaticRestore("powerplan", () => RestoreLaptopPerformanceForPowerPlan() && RestorePowerPlan());
			lock (sync)
			{
				if (flag)
				{
					envRestorePending.Remove("powerplan");
					envNextAttempt.Remove("powerplan");
					envFailures.Remove("powerplan");
				}
				else
				{
					envRestorePending.Add("powerplan");
				}
			}
			if (flag)
			{
				ActionFailureStore.CancelApply("PowerPlanOn", "system");
			}
			else
			{
				ActionFailureStore.Trip("PowerPlanOn", "system", reason + PerformancePolicy.Text("；电源计划回退失败", "; power-plan rollback failed"));
			}
			planActive = !flag;
			if (flag)
			{
				lastPowerPolicyKey = -1;
			}
			RecordAutomation("PowerPlanOn", PowerPlanExecutionState);
		}

		private bool PowerPlanStopped(int key)
		{
			AutomaticFailurePhase phase = PowerAttemptPhase(key);
			bool flag = ActionFailureStore.IsBlocked("PowerPlanOn", "system");
			lock (sync)
			{
				flag |= phase.Stopped || phase.Attempts >= 3 || DateTime.UtcNow.Ticks < phase.Next;
				if (flag)
				{
					powerStoppedKeys.Add(key);
				}
				else
				{
					powerStoppedKeys.Remove(key);
				}
			}
			if (flag) RecordAutomation("PowerPlanOn", PowerPlanExecutionState);
			return flag;
		}

		private bool RestoreFailedPowerPlan(long now)
		{
			bool flag;
			lock (sync)
			{
				flag = envRestorePending.Contains("powerplan");
				long value;
				if (flag && envNextAttempt.TryGetValue("powerplan", out value) && now < value)
				{
					return false;
				}
			}
			if (flag || (!planActive && PowerPlan.HasResidue))
			{
				lock (powerApplyGate)
				{
					bool flag2 = RunAutomaticRestore("powerplan", () => RestoreLaptopPerformanceForPowerPlan() && RestorePowerPlan());
					lock (sync)
					{
						if (flag2)
						{
							ActionFailureStore.CancelApply("PowerPlanOn", "system");
							envRestorePending.Remove("powerplan");
							envNextAttempt.Remove("powerplan");
							envFailures.Remove("powerplan");
						}
						else
						{
							envRestorePending.Add("powerplan");
						}
					}
					planActive = !flag2;
					if (flag2)
					{
						lastPowerPolicyKey = -1;
						Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
					}
					return flag2;
				}
			}
			return true;
		}

		internal void NotifyPowerSchemeChanged()
		{
			bool flag;
			lock (sync)
			{
				flag = PowerPlanApplySessionCurrent(Volatile.Read(ref powerSessionGen));
			}
			if (flag)
			{
				Interlocked.Exchange(ref powerPlanNotificationPending, 1);
				QueuePowerPlanNotificationAudit();
			}
		}

		private void QueuePowerPlanNotificationAudit()
		{
			if (!AutomaticWorkAllowed("power-queue")) return;
			if (Interlocked.CompareExchange(ref powerApplyInFlight, 1, 0) != 0)
			{
				return;
			}
			int keyShot;
			int genShot;
			lock (sync)
			{
				keyShot = lastPowerPolicyKey;
				genShot = Volatile.Read(ref powerSessionGen);
				if (!PowerPlanApplySessionCurrent(genShot) || keyShot < 0 || (keyShot & 2) == 0 || PowerPlanStopped(keyShot) || (planFailStreak > 0 && DateTime.UtcNow.Ticks < Interlocked.Read(ref nextPowerAuditTicks)))
				{
					Interlocked.Exchange(ref powerPlanNotificationPending, 0);
					Interlocked.Exchange(ref powerApplyInFlight, 0);
					return;
				}
			}
			Interlocked.Exchange(ref powerPlanNotificationPending, 0);
			bool flag = false;
			try
			{
				flag = ThreadPool.QueueUserWorkItem(delegate
				{
					RunPowerPlanNotificationAudit(genShot, keyShot);
				});
			}
			catch
			{
			}
			if (!flag)
			{
				RecordAutomaticWorkFailure("power-queue", "notification-queue-rejected");
				Interlocked.Exchange(ref powerPlanNotificationPending, 1);
				Interlocked.Exchange(ref powerApplyInFlight, 0);
			}
		}

		private bool PowerPlanNotificationStillCurrent(int genShot, int keyShot)
		{
			if (PowerPlanNotificationSessionCurrent(genShot, keyShot))
			{
				return !PowerPlanStopped(keyShot);
			}
			return false;
		}

		private bool PowerPlanNotificationSessionCurrent(int genShot, int keyShot)
		{
			lock (sync)
			{
				return PowerPlanApplySessionCurrent(genShot) && lastPowerPolicyKey == keyShot;
			}
		}

		private void InvalidatePowerPlanSession()
		{
			Interlocked.Exchange(ref laptopPerfPowerGeneration, -1);
			Interlocked.Increment(ref powerSessionGen);
			Interlocked.Exchange(ref powerPlanNotificationPending, 0);
			Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
		}

		private bool PowerPlanApplySessionCurrent(int genShot)
		{
			lock (sync)
			{
				return enabled && active && planActive && !stopping && !panicReq && !powerSessionEnding && !ProfileStoreSaveFailed
					&& gameGoneSinceTicks == 0 && !PowerPlan.NoSwitchSelected && Volatile.Read(ref powerSessionGen) == genShot;
			}
		}

		private void RunPowerPlanNotificationAudit(int genShot, int keyShot)
		{
			try
			{
				lock (powerApplyGate)
				{
					string reason;
					if (!PowerPlanNotificationStillCurrent(genShot, keyShot) || PowerPlan.EnforceSettled((keyShot & 1) != 0) || !BeginPowerAttempt(keyShot, out reason))
					{
						return;
					}
					bool skipped;
					bool flag = RunPowerPlanAction(delegate
					{
						if (!PowerPlanNotificationSessionCurrent(genShot, keyShot))
						{
							throw new OperationCanceledException();
						}
						return PowerPlan.Enforce((keyShot & 1) != 0);
					}, out skipped, out reason);
					if (skipped)
					{
						RollbackPowerPlan(PerformancePolicy.Text("电源计划应用已跳过", "Power-plan apply was skipped"));
						CompletePowerAttempt(false, "stage=apply-skipped");
					}
					else if (!PowerPlanNotificationSessionCurrent(genShot, keyShot))
					{
						if (!flag)
						{
							ActionFailureStore.Trip("PowerPlanOn", "system", reason);
						}
							RollbackPowerPlan(flag ? "stage=session-cancelled" : reason + " phase=session-cancelled");
						CompletePowerAttempt(false, "stage=session-cancelled");
					}
					else
					{
						OnPowerPlanAppliedWithReason(flag, reason, genShot);
					}
				}
			}
			catch
			{
			}
			finally
			{
				lock (powerApplyGate)
				{
					if (powerAttemptInProgress != null)
					{
						RollbackPowerPlan("stage=attempt-interrupted");
						CompletePowerAttempt(false, "stage=attempt-interrupted");
					}
				}
				Interlocked.Exchange(ref powerApplyInFlight, 0);
				if (Interlocked.CompareExchange(ref powerPlanNotificationPending, 0, 0) != 0)
				{
					QueuePowerPlanNotificationAudit();
				}
			}
		}

		private bool RunPowerPlanApply(int genShot, Func<bool> apply)
		{
			try
			{
				lock (powerApplyGate)
				{
					if (!PowerPlanApplySessionCurrent(genShot) || PowerPlanStopped(lastPowerPolicyKey))
					{
						return false;
					}
					if (apply == null)
					{
						return false;
					}
					if (Volatile.Read(ref laptopPerfPowerGeneration) != genShot && !RestoreLaptopPerformanceForPowerPlan())
					{
						lastPowerPolicyKey = -1;
						Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
						return false;
					}
					string reason;
					if (!BeginPowerAttempt(lastPowerPolicyKey, out reason))
					{
						RecordAutomation("PowerPlanOn", PowerPlanExecutionState);
						return false;
					}
					bool skipped;
					bool flag = RunPowerPlanAction(delegate
					{
						if (!PowerPlanApplySessionCurrent(genShot))
						{
							throw new OperationCanceledException();
						}
						return apply();
					}, out skipped, out reason);
					if (skipped)
					{
						RollbackPowerPlan(PerformancePolicy.Text("电源计划应用已跳过", "Power-plan apply was skipped"));
						CompletePowerAttempt(false, "stage=apply-skipped");
						return false;
					}
					if (!PowerPlanApplySessionCurrent(genShot))
					{
						if (!flag)
						{
							ActionFailureStore.Trip("PowerPlanOn", "system", reason);
						}
						RollbackPowerPlan(flag ? "stage=session-cancelled" : reason + " phase=session-cancelled");
						CompletePowerAttempt(false, "stage=session-cancelled");
						return false;
					}
					OnPowerPlanAppliedWithReason(flag, reason, genShot);
					return flag && !ActionFailureStore.IsBlocked("PowerPlanOn", "system");
				}
			}
			catch
			{
				return false;
			}
			finally
			{
				lock (powerApplyGate)
				{
					if (powerAttemptInProgress != null)
					{
						RollbackPowerPlan("stage=attempt-interrupted");
						CompletePowerAttempt(false, "stage=attempt-interrupted");
					}
				}
				Interlocked.Exchange(ref powerApplyInFlight, 0);
				if (Interlocked.CompareExchange(ref powerPlanNotificationPending, 0, 0) != 0)
				{
					QueuePowerPlanNotificationAudit();
				}
			}
		}

		private void OnPowerPlanApplied(bool planOk)
		{
			OnPowerPlanAppliedWithReason(planOk, PerformancePolicy.Text("电源计划执行返回失败", "The power-plan action returned failure"), Volatile.Read(ref powerSessionGen));
		}

		private void OnPowerPlanAppliedWithReason(bool planOk, string reason, int generation)
		{
			if (planOk)
			{
				if (!ActionFailureStore.CompleteApply("PowerPlanOn", "system"))
				{
					RollbackPowerPlan(PerformancePolicy.Text("电源计划完成记录失败", "Power-plan completion could not be persisted"));
					CompletePowerAttempt(false, "stage=completion-persistence");
					return;
				}
				planFailStreak = 0;
				if (LoadCounter("PowerPlanFailStreak") != 0)
				{
					SaveCounter("PowerPlanFailStreak", 0);
				}
				lock (sync)
				{
					powerStoppedKeys.Clear();
				}
				Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
				Interlocked.Exchange(ref laptopPerfPowerGeneration, generation);
				Interlocked.Exchange(ref laptopPerfNextWork, 0L);
				RecordAutomation("PowerPlanOn", "applied");
				CompletePowerAttempt(true, "stage=write-and-readback");
			}
			else
			{
				planFailStreak = 1;
				ActionFailureStore.Trip("PowerPlanOn", "system", reason);
				Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
				lock (sync)
				{
					powerStoppedKeys.Add(lastPowerPolicyKey);
				}
				RollbackPowerPlan(reason);
				CompletePowerAttempt(false, reason, PowerPlan.LastApplyFailureIsPermanent);
			}
		}

		private static int LoadCounter(string key)
		{
			int result;
			if (!int.TryParse(Settings.LoadStr(key, "0"), out result) || result <= 0)
			{
				return 0;
			}
			return result;
		}

		private static void SaveCounter(string key, int value)
		{
			Settings.SaveStr(key, value.ToString());
		}

		private void RecordNvAutomation(string key, string state, bool preStage)
		{
			if (!preStage)
			{
				RecordAutomation(key, state);
				return;
			}
			string msg = "PRESTAGE " + Lang.T("auto.log.prestage") + " | " + AutomationActionLabel(key) + " | " + AutomationStateLabel(state);
			if (state == "failed")
			{
				Logger.Warn(msg);
			}
			else
			{
				Logger.Info(msg);
			}
		}

		private void HandleNvTweakOutcome(List<string> failed, NvGamePlan plan, bool preStage = false)
		{
			if (failed == null || plan == null)
			{
				return;
			}
			if (plan.Automatic)
			{
				if (plan.LowLatMode == "on")
				{
					RecordNvAutomation("NvLowLat", failed.Contains("prerender") ? "failed" : "configured", preStage);
				}
				if (plan.MaxPerf)
				{
					RecordNvAutomation("NvMaxPerf", failed.Contains("pstate") ? "failed" : "configured", preStage);
				}
				if (plan.ShaderCacheMax)
				{
					RecordNvAutomation("NvShaderCache", (failed.Contains("shadercache") || failed.Contains("shaderon")) ? "failed" : "configured", preStage);
				}
			}
			NoteNvKey("pstate", plan.MaxPerf, failed.Contains("pstate"));
			bool wanted = plan.LowLatMode == "on" || plan.LowLatMode == "ultra";
			NoteNvKey("prerender", wanted, (plan.LowLatMode == "ultra") ? NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.UltraKeys) : failed.Contains("prerender"));
			NoteNvKey("smooth", plan.SmoothMotion && NvDrsTweaks.SmoothMotionSupported(), failed.Contains("smooth"));
			NoteNvKey("shadercache", plan.ShaderCacheMax, failed.Contains("shadercache") || failed.Contains("shaderon"));
			NoteNvKey("rebarfeat", plan.Rebar, NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.RebarKeys));
			bool wanted2 = (plan.DlssMode == "latest" || plan.DlssMode == "j" || plan.DlssMode == "k") && NvDrsTweaks.DlssOverrideSupported();
			NoteNvKey("dlssovr", wanted2, NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.DlssKeys));
		}

		private void NoteNvKey(string key, bool wanted, bool didFail)
		{
		}

		private bool EnvActive()
		{
			if (SessionRecoveryPending) return true;
			if (SelfYield.HasResidue) return true;
			if (laboratory != null && !laboratory.IsRestored) return true;
			if (GameNvDeliveryPending)
			{
				return true;
			}
			if (CriticalThreadHasRecoveryDebt(false))
			{
				return true;
			}
			if (FrameCoordination.HasResidue)
			{
				return true;
			}
			if (core.AutomaticLayoutPending)
			{
				return true;
			}
			if (GuardSchedulingNeedsRestore)
			{
				return true;
			}
			lock (sync)
			{
				if (envRestorePending.Count > 0)
				{
					return true;
				}
			}
			if (CoreThreadPlacement.UnownedPending)
			{
				return true;
			}
			if (doActive || wuActive || maintActive || optionalServicesActive || pqosActive || awakeActive || dwmBoostActive || gpwActive || nvVrrActive || intelEndActive || laptopPerfActive || Volatile.Read(ref laptopPerfWorker) != 0 || planActive || timerRaised || rsrActive || amdAlagActive || amdAfmfActive || (standbyCleaner != null && standbyCleaner.HasInFlight))
			{
				return true;
			}
			int mutationGeneration = Settings.MutationGeneration;
			if (mutationGeneration != envResidueGeneration)
			{
				envResidueOther = LaboratoryPower.HasResidue || LaboratoryGpu.HasResidue || DoTweak.HasResidue || UpdatePause.HasResidue || MaintenancePause.HasResidue || OptionalServicePause.HasResidue || PresenceQos.HasResidue || GpuPowerMax.HasResidue() || GpuClockLock.HasResidue || AdlxTweaks.HasResidue() || NvVrrWindowed.HasResidue || IntelEndurance.HasResidue || LaptopPerfMode.HasResidue || PowerPlan.HasResidue || PowerPlan.HasParkResidue() || PowerOverlay.HasResidue || DisplaySolo.HasResidue();
				envResidueNvList = NvDrsTweaks.HasGameResidue;
				envResidueGpuPref = GpuPrefStage.HasResidue;
				envResidueGeneration = mutationGeneration;
			}
			if (!envResidueOther && !NvGameResidueGate(envResidueNvList))
			{
				return GpuPrefResidueGate(envResidueGpuPref);
			}
			return true;
		}

		private bool NvGameResidueGate(bool hasResidue)
		{
			if (hasResidue)
			{
				if (enabled)
				{
					return preStagedNvPath == null;
				}
				return true;
			}
			return false;
		}

		private bool GpuPrefResidueGate(bool hasResidue)
		{
			if (hasResidue)
			{
				if (enabled && gpuPrefStageOn)
				{
					return preStagedNvPath == null;
				}
				return true;
			}
			return false;
		}

		private string ResidueDetail()
		{
			List<string> list = new List<string>();
			if (SelfYield.HasResidue) list.Add(PerformancePolicy.Text("自身及辅助进程让路恢复", "Self/helper scheduling recovery"));
			if (GameNvDeliveryPending)
			{
				list.Add(PerformancePolicy.Text("游戏 NVIDIA 配置恢复", "Game NVIDIA configuration recovery"));
			}
			if (CriticalThreadHasRecoveryDebt(false))
			{
				list.Add(PerformancePolicy.Text("关键线程硬绑定恢复", "Hard thread affinity recovery"));
			}
			if (FrameCoordination.HasResidue)
			{
				list.Add(PerformancePolicy.Text("线程协同恢复", "Thread coordination recovery"));
			}
			if (GuardSchedulingNeedsRestore)
			{
				if (GameDvr.HasResidue)
				{
					list.Add(Lang.T("set.dvr"));
				}
				if (Mmcss.HasResidue())
				{
					list.Add(Lang.T("auto.mmcss"));
				}
			}
			bool flag;
			int count;
			lock (sync)
			{
				flag = active;
				count = gameBoost.Count;
			}
			if (flag)
			{
				list.Add(Lang.T("t.gamemodeenv.29"));
			}
			if (count > 0)
			{
				list.Add(Lang.T("log.gamemodeboost.3") + count + Lang.T("t.gamemodeenv.30"));
			}
			if (core.AnyWith(SuppressReason.Background))
			{
				list.Add(Lang.T("cfg.group.bg"));
			}
			if (doActive || DoTweak.HasResidue)
			{
				list.Add(Lang.T("t.gamemodeenv.31"));
			}
			if (wuActive || UpdatePause.HasResidue)
			{
				list.Add(Lang.T("t.gamemodeenv.32"));
			}
			if (maintActive || MaintenancePause.HasResidue)
			{
				list.Add(Lang.T("gm.pausemaint"));
			}
			if (optionalServicesActive || OptionalServicePause.HasResidue)
			{
				list.Add(Lang.T("gm.pausesvc"));
			}
			if (standbyCleaner != null && standbyCleaner.HasInFlight)
			{
				list.Add(Lang.T("gm.standbycleaner"));
			}
			if (pqosActive || PresenceQos.HasResidue)
			{
				list.Add(Lang.T("t.gamemodeenv.33"));
			}
			if (awakeActive)
			{
				list.Add(Lang.T("t.gamemodeenv.34"));
			}
			if (dwmBoostActive)
			{
				list.Add(Lang.T("gm.dwmboost"));
			}
			if (RssSteer.HasResidue)
			{
				list.Add(Lang.T("gm.rsssteer"));
			}
			if (gpwActive || GpuPowerMax.HasResidue())
			{
				list.Add(EnvLabel("gpupower"));
			}
			if (GpuClockLock.HasResidue)
			{
				list.Add(Lang.T("set.gpuclock"));
			}
			if (nvVrrActive || NvVrrWindowed.HasResidue)
			{
				list.Add(EnvLabel("nvvrr"));
			}
			if (intelEndActive || IntelEndurance.HasResidue)
			{
				list.Add(EnvLabel("intelend"));
			}
			if (laptopPerfActive || LaptopPerfMode.HasResidue)
			{
				list.Add(Lang.T("gm.laptopperf"));
			}
			if (DisplaySolo.HasResidue())
			{
				list.Add(Lang.T("gm.solo"));
			}
			if (rsrActive || amdAlagActive || amdAfmfActive || AdlxTweaks.HasResidue())
			{
				list.Add("AMD");
			}
			if (planActive || PowerPlan.HasResidue || PowerPlan.HasParkResidue())
			{
				list.Add(Lang.T("t.gamemodeenv.35"));
			}
			if (NvGameResidueNeedsRestore)
			{
				list.Add("NVIDIA Profile");
			}
			if (GpuPrefStageResidueNeedsRestore)
			{
				list.Add(Lang.T("set.gpupref"));
			}
			if (timerRaised)
			{
				list.Add(Lang.T("t.gamemodeenv.36"));
			}
			if (list.Count <= 0)
			{
				return Lang.T("t.gamemodeenv.37");
			}
			return string.Join(" ", list.ToArray());
		}

		private static long ResidueBackoffTicks(int tries)
		{
			long num = 4000L << Math.Min(Math.Max(tries - 1, 0), 7);
			if (num > 300000)
			{
				num = 300000L;
			}
			return num * 10000;
		}

        private bool RetryDeactivate(string reason)
        {
            if (sessionRecoveryCaptured && sessionRecoveryRoutes != null && !sessionRecoveryRoutes.Complete)
            {
                RefreshSessionRecoveryIntents();
                if (DateTime.UtcNow.Ticks < residueNextTryTicks) return false;
                return RetrySessionRecoveryRoutes(reason);
            }
            return Deactivate(reason, true);
        }

		private bool StepDisplayAwake(bool want)
		{
			awakeActive = EnvStep("awake", want, awakeActive, DisplayAwake.ActivateForSession, DisplayAwake.RestoreForSession);
			return awakeActive;
		}

		private bool RestoreDisplayAwake()
		{
			bool flag;
			lock (sync)
			{
				int num;
				if (!awakeActive)
				{
					string value;
					if (automationEvidence.TryGetValue("awake", out value))
					{
						switch (value)
						{
						default:
							num = ((value == "restore-failed") ? 1 : 0);
							break;
						case "applied":
						case "restored":
						case "failed":
							num = 1;
							break;
						}
					}
					else
					{
						num = 0;
					}
				}
				else
				{
					num = 1;
				}
				flag = (byte)num != 0;
			}
			bool flag2 = RunAutomaticRestore("awake", flag ? (Func<bool>)DisplayAwake.RestoreForSession : DisplayAwake.Restore);
			if (flag2)
			{
				awakeActive = false;
			}
			if (flag)
			{
				RecordAutomation("awake", flag2 ? "restored" : "restore-failed");
			}
			return flag2;
		}

		private bool RestoreEnv()
		{
			InvalidatePowerPlanSession();
			RunSessionRecoveryRoute("selfyield", delegate { SelfYield.Release(); return !SelfYield.HasResidue; });
			bool labClean = RunSessionRecoveryRoute("laboratory", () => RestoreLaboratory());
			bool operationsSucceeded = labClean && !SelfYield.HasResidue;
			if (!RunSessionRecoveryRoute("critical", () => StopCriticalThreads(0, "RestoreEnv")))
			{
				operationsSucceeded = false;
			}
			if (!RunSessionRecoveryRoute("legacy-nv", () => RestoreGameNvDelivery()))
			{
				operationsSucceeded = false;
			}
			if (!RunSessionRecoveryRoute("timer", () => timerLease.Update(false, DateTime.UtcNow.Ticks)))
			{
				operationsSucceeded = false;
			}
			RecordAutomation("AutoGlobalTimer", AutomaticExecutionState("AutoGlobalTimer"));
			if (!RunSessionRecoveryRoute("layouts", () => core.RestoreAutomaticLayouts()))
			{
				operationsSucceeded = false;
			}
			if (RunAutomaticRestore("do", () => DoTweak.Restore()))
			{
				doActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			if (RunAutomaticRestore("wu", () => UpdatePause.Restore()))
			{
				wuActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			if (RunAutomaticRestore("maint", () => MaintenancePause.Restore()))
			{
				maintActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			if (RunAutomaticRestore("services", () => OptionalServicePause.Restore()))
			{
				optionalServicesActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			if (RunAutomaticRestore("pqos", () => PresenceQos.Restore()))
			{
				pqosActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			if (!RestoreDisplayAwake())
			{
				operationsSucceeded = false;
			}
			if (RunAutomaticRestore("dwmboost", () => DwmBoost.Restore()))
			{
				dwmBoostActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			if (!RunAutomaticRestore("rss", () => RssSteer.Restore()))
			{
				operationsSucceeded = false;
			}
			if (RunAutomaticRestore("rsr", () => AdlxTweaks.RestoreRsr()))
			{
				rsrActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
            {
			if (RunAutomaticRestore("gpupower", () => GpuPowerMax.Restore()))
			{
				gpwActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			if (!RunAutomaticRestore("gpuclock", () => GpuClockLock.Restore()))
			{
				operationsSucceeded = false;
			}
			}
			if (RunAutomaticRestore("nvvrr", () => NvVrrWindowed.Restore()))
			{
				nvVrrActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			if (RunAutomaticRestore("intelend", () => IntelEndurance.Restore()))
			{
				intelEndActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			bool laptopClean = RunSessionRecoveryRoute("laptop", () => RestoreLaptopPerformanceForPowerPlan());
			if (!laptopClean)
			{
				operationsSucceeded = false;
			}
			if (RunAutomaticRestore("amdalag", () => RestoreAmdAntiLagEnv()))
			{
				amdAlagActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			if (RunAutomaticRestore("amdafmf", () => AdlxTweaks.RestoreAfmf()))
			{
				amdAfmfActive = false;
			}
			else
			{
				operationsSucceeded = false;
			}
			if (!RunAutomaticRestore("amdsync", () => AdlxTweaks.RestoreEnhancedSync()))
			{
				operationsSucceeded = false;
			}
			if (!RunAutomaticRestore("amdris", () => AdlxTweaks.RestoreRis()))
			{
				operationsSucceeded = false;
			}
			if (!RunAutomaticRestore("amdfrtc", () => AdlxTweaks.RestoreFrtc()))
			{
				operationsSucceeded = false;
			}
            if (!RunSessionRecoveryRoute("nv-profiles", delegate
            {
                lock (driverStageGate)
                {
                    if (!NvGameResidueGate(true)) return true;
                    preStagedNvPath = null;
                    bool hadResidue = NvDrsTweaks.HasGameResidue;
                    bool clean = NvDrsTweaks.RestoreAllGames();
                    if (hadResidue) { RecordAutomation("NvMaxPerf", clean ? "restored" : "restore-failed"); RecordAutomation("NvShaderCache", clean ? "restored" : "restore-failed"); }
                    return clean;
                }
            })) operationsSucceeded = false;
            if (!RunAutomaticRestore("gpupref", delegate
            {
                lock (driverStageGate) return !GpuPrefResidueGate(true) || GpuPrefStage.Restore();
            })) operationsSucceeded = false;
			Interlocked.Increment(ref powerSessionGen);
			if (CapturingSessionRecovery || (labClean && laptopClean))
			{
				if (RunAutomaticRestore("powerplan", delegate { lock (powerApplyGate) return PowerPlan.Restore(); }))
				{
					planActive = false;
					lastPowerPolicyKey = -1;
					Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
				}
				else
				{
					operationsSucceeded = false;
				}
				if (!RunAutomaticRestore("powerpark", delegate { lock (powerApplyGate) return PowerPlan.RestoreParkState(); }))
				{
					operationsSucceeded = false;
				}
			}
			return CapturingSessionRecovery ? operationsSucceeded : EnvRestoreCompleted(operationsSucceeded);
		}

		private bool EnvRestoreCompleted(bool operationsSucceeded)
		{
			if (Volatile.Read(ref laptopPerfWorker) != 0) return false;
			if (LaboratoryPower.HasResidue || LaboratoryGpu.HasResidue || (laboratory != null && !laboratory.IsRestored)) return false;
			if (!operationsSucceeded || PowerPlan.HasResidue || PowerPlan.HasParkResidue() || PowerOverlay.HasResidue || OptionalServicePause.HasResidue)
			{
				return false;
			}
			string[] array = new string[16]
			{
				"PrevDoBgBw", "PrevDoSvcStopped", "PrevUpdatePaused", "PrevPresenceQos", "GpuPowerSnap", "AmdSnap", "NvDrsList", "GpuPrefStage", "PrevPowerPlan", "PowerOverlaySnap",
				"CpuIdleStateV1", "PrevOptionalServicesPausedV1", "LaptopPerfSnap", "FrameCoordinationJournalV1", "FrameMemoryProtectionJournalV1", "FrameCoordinationTraceV1"
			};
			string[] array2 = array;
			foreach (string name in array2)
			{
				string value;
				if (!Settings.TryLoadStr(name, out value) || !string.IsNullOrEmpty(value))
				{
					return false;
				}
			}
			lock (sync)
			{
				envRestorePending.RemoveWhere((string key) => key != "gamedvr" && key != "mmcss");
				return envRestorePending.Count == 0;
			}
		}

		private void ReleaseBackground()
		{
			ReleaseBackground(Lang.T("t.gamemodeenv.41"));
		}

		private int ReleaseBackground(string reasonPrefix)
		{
			if (!core.AnyWith(SuppressReason.Background))
			{
				return 0;
			}
			int num = 0;
			foreach (int item in core.PidsWith(SuppressReason.Background))
			{
				if (core.Release(item, SuppressReason.Background))
				{
					ReportSeal(item);
					num++;
				}
			}
			if (num > 0)
			{
				Logger.Log(reasonPrefix + Lang.T("log.gamemodeenv.42") + num + Lang.T("log.gamemodeenv.43"));
			}
			return num;
		}

		private void NotifyExtensionSession(bool active)
		{
			GameProfile gameProfile = null;
			int rendererPid = 0;
			long rendererCreation = 0L;
			GameDetection renderer = null;
			if (active)
			{
				lock (sync)
				{
					if (this.active && activeDetection != null && activeDetection.RendererCandidateSelected
						&& !activeDetection.RendererSafetyOnly && !activeDetection.RequiresGpuConfirm)
					{
						renderer = RendererHandoffTracker.Copy(activeDetection);
					}
				}
			}
			if (renderer != null && GameExtension.Current != null
				&& GameExtension.Current.AppliesTo(renderer.Profile) && HasRendererObservation(renderer.Profile))
			{
				RendererObservationStore observations = rendererObservations;
				if (observations != null)
				{
					observations.Validate(renderer.Profile.Id, renderer.RendererPath);
					if (observations.Has(renderer.Profile.Id, renderer.RendererPath))
					{
						lock (sync)
						{
							if (this.active && !stopping && enabled && !panicReq && !ProfileStoreSaveFailed
								&& RendererHandoffTracker.SameIdentity(renderer, activeDetection)
								&& RendererHandoffTracker.SameProfile(renderer.Profile, activeDetection.Profile))
							{
								gameProfile = renderer.Profile;
								rendererPid = renderer.RendererPid;
								rendererCreation = renderer.RendererCreation;
							}
						}
					}
				}
			}
			GameExtension.NotifySession(gameProfile, rendererPid, rendererCreation, gameProfile != null);
		}

		private void ClearFamilyDiscovery()
		{
			lock (sync)
			{
				GameFamilyHistory gameFamilyHistory = this.gameFamilyHistory;
				if (gameFamilyHistory != null)
				{
					gameFamilyHistory.Clear();
				}
				Volatile.Write(ref gameFamilyEvidence, GameFamilyEvidence.Empty);
			}
		}

		public bool NeedsGameFamilyIdentity(int session)
		{
			if (session != selfSession || session < 0 || stopping || !enabled)
			{
				return false;
			}
			lock (sync)
			{
				return profiles.Count > 0;
			}
		}

		private void ObserveGameFamilyChanges(ProcessChangeBatch batch)
		{
			if (batch == null || stopping || !enabled)
			{
				return;
			}
			lock (sync)
			{
				if (!stopping && enabled && profiles.Count != 0)
				{
					FamilyHistory.ObserveEvents(batch, selfSession, RendererNowMs());
				}
			}
		}

		private void CaptureGameFamily(ProcessSnapshot snapshot, IList<GameProfile> library)
		{
			Volatile.Write(ref gameFamilyEvidence, FamilyHistory.Capture(snapshot, library, selfSession, RendererNowMs()));
		}

		internal static string ResolveLibraryInstallRoot(string executablePath, string fallbackRoot)
		{
			string text = NormalizeGameRoot(fallbackRoot);
			if (text == null)
			{
				text = NormalizeGameRoot(GameScan.InferGameRoot(executablePath));
			}
			text = GameInstallScope.RestrictFallback(executablePath, text);
			string text2 = NormalizeGameRoot(GameInstallScope.Resolve(executablePath, text));
			if (text2 == null || !FamilyBoundary.UnderRoot(executablePath, text2))
			{
				return text;
			}
			return text2;
		}

		internal static bool RefreshLibraryInstallRoots(IList<GameProfile> library)
		{
			bool result = false;
			if (library == null)
			{
				return false;
			}
			foreach (GameProfile item in library)
			{
				if (item != null && !string.IsNullOrEmpty(item.ExecutablePath))
				{
					string text = GameInstallScope.RestrictFallback(item.ExecutablePath, NormalizeGameRoot(item.Root));
					bool flag = !string.IsNullOrEmpty(item.Root) && text == null;
					string text2 = NormalizeGameRoot(GameInstallScope.Resolve(item.ExecutablePath, text));
					if (text2 != null && !FamilyBoundary.UnderRoot(item.ExecutablePath, text2))
					{
						text2 = text;
					}
					if (!string.Equals(text2, item.Root, StringComparison.OrdinalIgnoreCase) && (flag || (text2 != null && (string.IsNullOrEmpty(item.Root) || FamilyBoundary.UnderRoot(item.Root, text2)) && (string.IsNullOrEmpty(item.LearnedExecutablePath) || FamilyBoundary.UnderRoot(item.LearnedExecutablePath, text2)))))
					{
						item.Root = text2;
						result = true;
					}
				}
			}
			return result;
		}

		public bool SetProfileFamilySuppression(string profileId, bool on)
		{
			return SetProfileFamilySuppression(profileId, on, null);
		}

		public bool SetProfileFamilySuppression(string profileId, bool on, string expectedExecutablePath)
		{
			bool flag = false;
			lock (familyPolicyGate)
			{
				lock (sync)
				{
					GameProfile gameProfile = FindProfileLocked(profileId);
					if (stopping || gameProfile == null || ProfileStoreSaveFailed)
					{
						return false;
					}
					if (expectedExecutablePath != null && !string.Equals(gameProfile.ExecutablePath, expectedExecutablePath, StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					if (gameProfile.SuppressFamilyBackground == on)
					{
						return true;
					}
					GameProfile gameProfile2 = gameProfile.Clone();
					if (on)
					{
						gameProfile2.Overrides["GameFamilyBackground"] = "1";
					}
					else
					{
						gameProfile2.Overrides.Remove("GameFamilyBackground");
					}
					List<GameProfile> list = new List<GameProfile>(profiles);
					int index = profiles.IndexOf(gameProfile);
					list[index] = gameProfile2;
					if (!SaveProfileSnapshotLocked(list))
					{
						return false;
					}
					profiles[index] = gameProfile2;
					if (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId)
					{
						activeDetection.Profile = gameProfile2.Clone();
					}
					if (stickyDetection != null && stickyDetection.Profile != null && stickyDetection.Profile.Id == profileId)
					{
						stickyDetection.Profile = gameProfile2.Clone();
					}
					Interlocked.Increment(ref familyPolicyEpoch);
					firstSweep = true;
					flag = true;
				}
			}
			if (flag)
			{
				RequestPolicyApply("SetProfileFamilySuppression");
				RaiseLibraryChanged();
			}
			return true;
		}

		internal bool FamilyPolicyEpochCurrent(int epoch)
		{
			if (epoch == Volatile.Read(ref familyPolicyEpoch) && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed)
			{
				return EffSuppress;
			}
			return false;
		}

		private bool RunBackgroundPolicy(int epoch, Action action)
		{
			lock (familyPolicyGate)
			{
				if (!FamilyPolicyEpochCurrent(epoch))
				{
					return false;
				}
				action();
				return true;
			}
		}

		private void InvalidateFamilyPolicy()
		{
			Interlocked.Increment(ref familyPolicyEpoch);
		}

		internal static bool ForegroundConsumesService(string foregroundName, string envKey)
		{
			if (string.IsNullOrEmpty(foregroundName) || string.IsNullOrEmpty(envKey))
			{
				return false;
			}
			string text = WhitelistRule.NormalizeName(foregroundName).ToLowerInvariant();
			string[][] foregroundServiceConsumers = ForegroundServiceConsumers;
			foreach (string[] array in foregroundServiceConsumers)
			{
				if (array[0] == text && array[1] == envKey)
				{
					return true;
				}
			}
			return false;
		}

		private bool SwitchOutDebounceElapsed(long nowTicks)
		{
			if (smartSwitchedOut)
			{
				return sessionForeground.DebounceElapsed(nowTicks, SwitchOutDebounceTicks);
			}
			return false;
		}

		private bool SessionServiceSuspended(string envKey, long nowTicks)
		{
			if (!smartSwitchedOut)
			{
				return false;
			}
			if (ForegroundConsumesService(switchedOutForegroundName, envKey))
			{
				return true;
			}
			return SwitchOutDebounceElapsed(nowTicks);
		}

		private bool UpdateSessionForeground(ProcessSnapshot all, int rendererPid, HashSet<int> gamePids)
		{
			int num = RendererForegroundPid();
			bool flag = num > 4;
			bool flag2 = flag && rendererPid > 0 && (num == rendererPid || (gamePids != null && gamePids.Contains(num)));
			long ticks = DateTime.UtcNow.Ticks;
			bool returning = sessionForeground.State == SessionForegroundState.SwitchedOut;
			bool flag3 = sessionForeground.Observe(flag2, flag, ticks);
			if (flag && !flag2)
			{
				switchedOutForegroundPid = num;
				ProcEntry procEntry = ((all == null) ? null : all.Find(num));
				switchedOutForegroundName = ((procEntry == null || string.IsNullOrEmpty(procEntry.Name)) ? "" : procEntry.Name);
			}
			bool flag4 = sessionForeground.State == SessionForegroundState.SwitchedOut;
			bool smart = flag4 && EffPreset == PerformancePreset.Standard;
			sessionSwitchedOut = flag4;
			smartSwitchedOut = smart;
			if (flag3)
			{
				if (flag4)
				{
					OnSessionSwitchedOut(smart);
				}
				else
				{
					OnSessionSwitchedBack(returning);
				}
			}
			return !flag4;
		}

		private void OnSessionSwitchedOut(bool smart)
		{
			Interlocked.Increment(ref optionalServiceGeneration);
			string text = " foregroundPid=" + switchedOutForegroundPid + " foregroundExe=" + switchedOutForegroundName;
			if (!smart)
			{
				Logger.Info("FOCUS [" + AutomationSession + "] event=switched-out scene=competitive backgroundPolicy=retain powerPolicy=retain affinityPolicy=retain" + text);
				return;
			}
			Logger.Info("FOCUS [" + AutomationSession + "] event=switched-out scene=smart backgroundPolicy=release powerPolicy=retain affinityPolicy=retain" + text);
			ResetAdaptiveGuard();
		}

		private void OnSessionSwitchedBack(bool returning)
		{
			Interlocked.Increment(ref optionalServiceGeneration);
			lock (sync)
			{
				firstSweep = true;
				if (returning && activeDetection != null)
				{
					gameBoostNextAudit.Remove(activeDetection.RendererPid);
				}
			}
			if (returning)
			{
				exclusiveCheckTicks = 0L;
				Interlocked.Exchange(ref criticalForegroundAuditPending, 1);
				Logger.Info("FOCUS [" + AutomationSession + "] event=switched-back scene=" + (EffPreset == PerformancePreset.Standard ? "smart" : "competitive"));
			}
			Interlocked.Exchange(ref urgentProcessScan, 1);
			try
			{
				kick.Set();
			}
			catch
			{
			}
		}

		private void ResetSessionForeground()
		{
			sessionForeground.Reset();
			Interlocked.Exchange(ref criticalForegroundAuditPending, 0);
			smartSwitchedOut = false;
			sessionSwitchedOut = false;
			switchedOutForegroundName = "";
			switchedOutForegroundPid = 0;
		}

		private bool LaneFallbackFor(int pid, long birth)
		{
			LaneFallbackTarget laneFallbackTarget = laneFallback;
			if (laneFallbackTarget != null && pid > 0 && laneFallbackTarget.Pid == pid)
			{
				return laneFallbackTarget.Birth == birth;
			}
			return false;
		}

		private void UpdateLaneFallback(int pid, long birth, string name)
		{
			bool flag = FrameCoordinationEnabled && FrameCoordination.IdentificationExhausted(pid, birth, name);
			LaneFallbackTarget laneFallbackTarget = laneFallback;
			if (!flag)
			{
				if (laneFallbackTarget != null)
				{
					laneFallback = null;
				}
			}
			else if (laneFallbackTarget == null || laneFallbackTarget.Pid != pid || laneFallbackTarget.Birth != birth)
			{
				laneFallback = new LaneFallbackTarget(pid, birth);
			}
		}

		internal string ThreadBoostExecutionState()
		{
			string text = AutomaticExecutionState("GmRenderLane");
			string text2 = AutomaticExecutionState("FrameCoordination");
			string[] array = new string[2] { text, text2 };
			foreach (string text3 in array)
			{
				switch (text3)
				{
				case "failed":
				case "restore-failed":
				case "stopped":
				case "cooldown":
					return text3;
				}
			}
			if (!AutomationPolicySnapshot.ThreadBoostOn)
			{
				return "off";
			}
			if (laneFallback == null)
			{
				return text2;
			}
			return text;
		}

		private void SyncFrameCoordination(BoostPass pass)
		{
			bool flag = CriticalRolesWanted(pass.RendererPid, pass.RendererCreation);
			UpdateLaneFallback(pass.RendererPid, pass.RendererCreation, pass.RendererName);
			bool flag2 = FrameCoordinationEnabled && !LaneFallbackFor(pass.RendererPid, pass.RendererCreation);
			if (flag2 && (!RenderLane.Release() || RenderLane.HasResidue()))
			{
				pass.LaneRestorationReady = false;
				if (flag)
				{
					FrameCoordination.ReleaseBoost(pass.RendererPid, pass.RendererCreation);
				}
				else
				{
					FrameCoordination.ReleaseExcept(0, 0L);
				}
				return;
			}
			int pid = pass.RendererPid;
			long birth = pass.RendererCreation;
			int epoch = Volatile.Read(ref rendererHandoffEpoch);
			Func<bool> func = delegate
			{
				GameDetection gameDetection = Volatile.Read(ref activeDetection);
				return FrameCoordinationEnabled && enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed && EffBoost && !pass.WriteDenied && epoch == Volatile.Read(ref rendererHandoffEpoch) && gameDetection != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && gameDetection.RendererPid == pid && gameDetection.RendererCreation == birth
					&& ThreadBoostRecoveryAllowed(pid, birth);
			};
			if (flag2 && pid > 0 && birth > 0 && func())
			{
				FrameCoordination.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), pid, birth, FrameAutomaticConfiguration());
				FrameCoordination.Ensure(pid, birth, pass.RendererName, func);
			}
			else
			{
				pass.LaneRestorationReady = (flag ? FrameCoordination.ReleaseBoost(pid, birth) : FrameCoordination.ReleaseExcept(0, 0L));
			}
		}

		private void InvalidateAutomaticFullscreenOptimizations()
		{
			lock (sync)
			{
				automaticFsoCheckAt = 0L;
				foreach (AutomaticFsoEntry value in automaticFsoEntries.Values)
				{
					value.CheckAt = 0L;
				}
			}
		}

		internal bool SetProfileFullscreenOptimizations(string profileId, bool on)
		{
			return SetProfileOverride(profileId, "GameDisableFullscreenOptimizationsV1", on ? "1" : "0");
		}

		internal void SyncAutomaticFullscreenOptimizations(bool discoverTargets = false, long nowTicks = -1L)
		{
			if (nowTicks < 0)
			{
				nowTicks = DateTime.UtcNow.Ticks;
			}
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || autoIgnoreLoadFailed || (libraryIgnoreTransaction != null && libraryIgnoreTransaction.RecoveryPending))
				{
					return;
				}
				GameDetection gameDetection = activeDetection;
				GameProfile gameProfile = ((gameDetection == null || gameDetection.Profile == null) ? null : FindProfileLocked(gameDetection.Profile.Id));
				bool flag = gameDetection != null && gameDetection.Profile != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && gameProfile != null && gameProfile.DisableFullscreenOptimizations && RendererHandoffTracker.SameProfile(gameDetection.Profile, gameProfile);
				if ((!discoverTargets || !flag || string.IsNullOrEmpty(gameDetection.RendererPath) || automaticFsoEntries.ContainsKey(gameDetection.RendererPath)) && nowTicks < automaticFsoCheckAt)
				{
					return;
				}
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (GameProfile profile in profiles)
				{
					if (profile.DisableFullscreenOptimizations)
					{
						AddAutomaticFsoPath(hashSet, profile.ExecutablePath);
						AddAutomaticFsoPath(hashSet, profile.LearnedExecutablePath);
					}
				}
				if (flag)
				{
					AddAutomaticFsoPath(hashSet, gameDetection.RendererPath);
				}
				HashSet<string> hashSet2 = new HashSet<string>(hashSet, StringComparer.OrdinalIgnoreCase);
				bool flag2 = false;
				try
				{
					string[] array = FsoTweak.TrackedExes();
					foreach (string item in array)
					{
						hashSet2.Add(item);
						if (!hashSet.Contains(item))
						{
							flag2 = true;
						}
					}
				}
				catch
				{
					automaticFsoCheckAt = nowTicks + 600000000;
					automaticFsoState = "failed";
					RecordAutomation("AutoDisableFullscreenOptimizations", automaticFsoState);
					return;
				}
				foreach (string item2 in new List<string>(automaticFsoEntries.Keys))
				{
					if (!hashSet2.Contains(item2))
					{
						automaticFsoEntries.Remove(item2);
					}
				}
				bool flag3 = true;
				automaticFsoCheckAt = nowTicks + 600000000;
				foreach (string item3 in hashSet2)
				{
					if (stopping || ProfileStoreSaveFailed)
					{
						return;
					}
					bool flag4 = hashSet.Contains(item3);
					AutomaticFsoEntry value;
					if (!automaticFsoEntries.TryGetValue(item3, out value))
					{
						AutomaticFsoEntry automaticFsoEntry = new AutomaticFsoEntry();
						automaticFsoEntry.Disable = flag4;
						value = automaticFsoEntry;
						automaticFsoEntries[item3] = value;
					}
					if (value.Disable != flag4)
					{
						value.Disable = flag4;
						value.Configured = false;
						value.Failures = 0;
						value.CheckAt = 0L;
					}
					if (nowTicks >= value.CheckAt)
					{
						value.Configured = FsoTweak.SetForExe(item3, flag4);
						value.Failures = ((!value.Configured) ? Math.Min(value.Failures + 1, 5) : 0);
						int num = (value.Configured ? 60 : Math.Min(300, 30 << value.Failures - 1));
						value.CheckAt = nowTicks + (long)num * 10000000L;
					}
					flag3 &= value.Configured;
					automaticFsoCheckAt = Math.Min(automaticFsoCheckAt, value.CheckAt);
				}
				automaticFsoState = ((!flag3) ? "failed" : ((hashSet.Count > 0) ? "fso-configured" : (flag2 ? "restored" : "")));
				RecordAutomation("AutoDisableFullscreenOptimizations", automaticFsoState);
			}
		}

		private void AddAutomaticFsoPath(HashSet<string> paths, string path)
		{
			if (!string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && string.Equals(GameProfileStore.NormalizePath(path), path, StringComparison.OrdinalIgnoreCase) && GamePerformanceTargetPolicy.AllowsGameTarget(path) && File.Exists(path))
			{
				paths.Add(path);
			}
		}

		private void PublishLegacyNvPending()
		{
			Interlocked.Exchange(ref gameNvPending, (gameNvWorker || !gameNvRecoveryChecked || !gameNvRootClear) ? 1 : 0);
		}

		private void InitializeGameNvDeliveryMetadata()
		{
			lock (gameNvGate)
			{
				if (!gameNvRecoveryChecked)
				{
					gameNvRootClear = GameNvCanonicalStore.RootConfirmedAbsent(dataDir);
					gameNvRecoveryChecked = true;
					PublishLegacyNvPending();
				}
			}
		}

        private void SyncGameNvDelivery()
        {
            InitializeGameNvDeliveryMetadata();
            if (IsActive || stopping) return;
            string key;
            lock (gameNvGate)
            {
                if (gameNvRecoveryStopped || gameNvRootClear || !BeginLegacyNvRecovery(out key)) return;
            }
            bool restored = false;
            try
            {
                GameNvWorkerQueue.TryQueue(QueueLegacyNvWorker, delegate
                {
                    try { RestoreLegacyGameNvCore(); restored = true; }
                    catch (Exception error) { Logger.Warn("Legacy NVIDIA recovery pending: " + error.Message); }
                }, delegate { CompleteLegacyNvRecovery(key, restored); });
            }
            catch (Exception error) { Logger.Warn("Legacy NVIDIA recovery queue failed: " + error.Message); }
        }

        private bool RestoreLegacyGameNv()
        {
            string key;
            lock (gameNvGate)
            {
                if (gameNvRecoveryChecked && gameNvRootClear) return true;
                if (!BeginLegacyNvRecovery(out key)) return false;
            }
            bool restored = false;
            try { RestoreLegacyGameNvCore(); restored = true; return true; }
            catch (Exception error) { Logger.Warn("Legacy NVIDIA recovery pending: " + error.Message); return false; }
            finally { CompleteLegacyNvRecovery(key, restored); }
        }

		private void RestoreLegacyGameNvCore()
		{
			if (gameNvStore == null && !GameNvCanonicalStore.RootConfirmedAbsent(dataDir))
			{
				gameNvStore = GameNvCanonicalStore.Open(dataDir);
			}
			if (gameNvStore != null)
			{
                // Original journal paths are immutable recovery identity, not cached driver state.
                foreach (string journal in gameNvStore.PendingJournals())
                    legacyNvOwners.Add(gameNvStore.ReadOriginal(journal).Executable);
                LegacyNvOwnersExited();
				GameNvPersistentRecovery.RequireClear(gameNvStore);
			}
			lock (gameNvGate)
			{
				gameNvRecoveryChecked = true;
				gameNvRootClear = true;
				PublishLegacyNvPending();
			}
		}

		private bool StopGameNvDelivery(int timeoutMs)
		{
			if (Monitor.IsEntered(sync))
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			lock (gameNvGate)
			{
				gameNvRecoveryStopped = true;
			}
			if (!gameNvIdle.WaitOne(Math.Max(0, timeoutMs)))
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			try
			{
				return RestoreLegacyGameNv();
			}
			catch
			{
				lock (gameNvGate)
				{
					gameNvRootClear = false;
					PublishLegacyNvPending();
				}
				return false;
			}
		}

		private void ResumeGameNvAfterRecovery()
		{
			lock (gameNvGate)
			{
				gameNvRecoveryStopped = stopping;
			}
		}

		private bool RestoreGameNvDelivery()
		{
			bool flag = StopGameNvDelivery(5000);
			if (flag)
			{
				ResumeGameNvAfterRecovery();
			}
			return flag;
		}

		public bool AddGameExecutable(string name, string executablePath)
		{
			string error;
			return AddGameExecutableCore(name, executablePath, null, out error);
		}

		private bool AddGameExecutableCore(string name, string executablePath, string preferredRoot, out string error)
		{
			error = null;
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked())
				{
					return false;
				}
				List<GameProfile> next = GetProfiles();
				HashSet<string> nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
				if (!StageGameExecutable(name, executablePath, preferredRoot, next, nextIgnore, out error) || !CommitLibraryLocked(next, nextIgnore))
				{
					return false;
				}
			}
			KickLibraryChanged();
			return true;
		}

		private bool StageGameExecutable(string name, string executablePath, string preferredRoot, List<GameProfile> next, HashSet<string> nextIgnore, out string error)
		{
			string executablePath2;
			string suggestedName;
			if (!GameExecutableResolver.TryResolve(executablePath, out executablePath2, out error, out suggestedName))
			{
				return false;
			}
			string item = StripExe(Path.GetFileName(executablePath2));
			string name2 = DisplayName(executablePath2, string.IsNullOrWhiteSpace(name) ? suggestedName : name);
			string text = null;
			if (!string.IsNullOrEmpty(preferredRoot))
			{
				string text2 = NormalizeGameRoot(preferredRoot);
				if (text2 != null && FamilyBoundary.UnderRoot(executablePath2, text2))
				{
					text = text2;
				}
			}
			if (text == null)
			{
				text = NormalizeGameRoot(GameScan.InferGameRoot(executablePath2));
			}
			text = ResolveLibraryInstallRoot(executablePath2, text);
			foreach (GameProfile item2 in next)
			{
				if (string.Equals(item2.ExecutablePath, executablePath2, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				if (string.Equals(item2.LearnedExecutablePath, executablePath2, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				if (string.IsNullOrEmpty(item2.ExecutablePath) && item2.Entries.Contains(item))
				{
					item2.ExecutablePath = executablePath2;
					item2.Root = text;
					item2.Name = name2;
					item2.LearnedExecutablePath = null;
					nextIgnore.Remove(executablePath2);
					return true;
				}
			}
			GameProfile gameProfile = GameProfileStore.NewProfile(name2, text, executablePath2);
			gameProfile.Entries.Clear();
			gameProfile.Entries.Add(item);
			next.Add(gameProfile);
			nextIgnore.Remove(executablePath2);
			return true;
		}

		private bool PersistLibraryLocked()
		{
			return SaveProfilesLocked();
		}

		private void RaiseLibraryChanged()
		{
			Action libraryChanged = this.LibraryChanged;
			if (libraryChanged != null)
			{
				try
				{
					libraryChanged();
				}
				catch
				{
				}
			}
		}

		private void KickLibraryChanged()
		{
			InvalidateAutomaticFullscreenOptimizations();
			ClearFamilyDiscovery();
			InvalidateRendererHandoff();
			InvalidateFamilyPolicy();
			RequestFullGameDetection();
			RequestPolicyApply("KickLibraryChanged");
			RaiseLibraryChanged();
		}

		public bool AddGameFile(string selectedPath, out string error)
		{
			string executablePath;
			if (!GameExecutableResolver.TryResolve(selectedPath, out executablePath, out error))
			{
				return false;
			}
			if (!AddGameExecutable(null, executablePath))
			{
				error = Lang.T("t.gamemodelibrary.1");
				return false;
			}
			error = null;
			return true;
		}

		public int AddScannedGames(IList<ScanHit> hits, out string lastError)
		{
			lastError = null;
			int num = 0;
			if (hits == null)
			{
				return 0;
			}
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked())
				{
					return 0;
				}
				List<GameProfile> next = GetProfiles();
				HashSet<string> nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
				foreach (ScanHit hit in hits)
				{
					if (hit != null && !string.IsNullOrEmpty(hit.Exe))
					{
						string error;
						if (StageGameExecutable(hit.Name, hit.Exe, hit.Root, next, nextIgnore, out error))
						{
							num++;
						}
						else if (!string.IsNullOrEmpty(error))
						{
							lastError = error;
						}
					}
				}
				if (num > 0 && !CommitLibraryLocked(next, nextIgnore))
				{
					return 0;
				}
			}
			if (num > 0)
			{
				KickLibraryChanged();
			}
			return num;
		}

		internal bool TryLearnConfirmedRenderer(GameDetection hit)
		{
			return TryLearnConfirmedRenderer(hit, null);
		}

		private bool TryLearnConfirmedRenderer(GameDetection hit, Func<bool> stillCurrent)
		{
			if (hit == null || hit.Profile == null || !hit.RendererCandidateSelected || !hit.RendererLearnable || hit.RendererSafetyOnly || hit.RequiresGpuConfirm || hit.Profile.ForceTrigger || hit.RendererPid <= 0 || hit.RendererCreation <= 0)
			{
				return false;
			}
			return TryLearnRendererCore(hit.Profile.Id, hit.RendererPath, hit.RendererName, hit.Profile, stillCurrent);
		}

		private bool TryLearnRendererCore(string profileId, string rendererPath, string rendererName, GameProfile observedProfile, Func<bool> stillCurrent)
		{
			if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(rendererPath) || string.IsNullOrWhiteSpace(rendererName))
			{
				return false;
			}
			string text = rendererPath.Trim().Trim('"');
			string text2 = GameProfileStore.NormalizePath(text);
			if (text2 == null || !Path.IsPathRooted(text))
			{
				return false;
			}
			string pathRoot = Path.GetPathRoot(text);
			if (string.IsNullOrEmpty(pathRoot) || pathRoot.Length < 3 || (!pathRoot.EndsWith("\\", StringComparison.Ordinal) && !pathRoot.EndsWith("/", StringComparison.Ordinal)))
			{
				return false;
			}
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(text2);
			if (string.IsNullOrEmpty(fileNameWithoutExtension) || !string.Equals(fileNameWithoutExtension, StripExe(rendererName), StringComparison.OrdinalIgnoreCase) || !GameSessionDetector.IsLibraryCandidate(fileNameWithoutExtension, text2, windowsPrefix))
			{
				return false;
			}
			string text3 = ResolveLibraryInstallRoot(text2, GameScan.InferGameRoot(text2));
			if (text3 != null && !FamilyBoundary.UnderRoot(text2, text3))
			{
				text3 = null;
			}
			string name;
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || (stillCurrent != null && !stillCurrent()))
				{
					return false;
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile == null || gameProfile.ForceTrigger || RendererPathClaimedLocked(gameProfile, text2))
				{
					return false;
				}
				string text4 = GameInstallScope.RestrictFallback(gameProfile.ExecutablePath, NormalizeGameRoot(gameProfile.Root));
				if (text4 != null && FamilyBoundary.UnderRoot(text2, text4))
				{
					text3 = text4;
				}
				bool flag = string.Equals(gameProfile.ExecutablePath, text2, StringComparison.OrdinalIgnoreCase);
				if (!flag && observedProfile != null && (!string.Equals(gameProfile.ExecutablePath, observedProfile.ExecutablePath, StringComparison.OrdinalIgnoreCase) || !string.Equals(gameProfile.LearnedExecutablePath, observedProfile.LearnedExecutablePath, StringComparison.OrdinalIgnoreCase) || !string.Equals(gameProfile.Root, observedProfile.Root, StringComparison.OrdinalIgnoreCase)))
				{
					return false;
				}
				if (flag && gameProfile.LearnedExecutablePath == null && string.Equals(gameProfile.Root, text3, StringComparison.OrdinalIgnoreCase) && gameProfile.Entries.Count == 1 && gameProfile.Entries.Contains(fileNameWithoutExtension))
				{
					return true;
				}
				GameProfile gameProfile2 = gameProfile.Clone();
				gameProfile2.ExecutablePath = text2;
				gameProfile2.LearnedExecutablePath = null;
				gameProfile2.Root = text3;
				gameProfile2.Entries.Clear();
				gameProfile2.Entries.Add(fileNameWithoutExtension);
				int index = profiles.IndexOf(gameProfile);
				List<GameProfile> list = new List<GameProfile>(profiles);
				list[index] = gameProfile2;
				if (stillCurrent != null && !stillCurrent())
				{
					return false;
				}
				if (!SaveProfileSnapshotLocked(list, stillCurrent))
				{
					return false;
				}
				profiles[index] = gameProfile2;
				if (!flag)
				{
					ForgetRendererObservation(profileId);
				}
				name = gameProfile2.Name;
			}
			Logger.Log(Lang.T("log.gamemodelibrary.2") + name + Lang.T("log.gamemodelibrary.3") + fileNameWithoutExtension + Lang.T("log.gamemodelibrary.4") + text2 + " ");
			InvalidateAutomaticFullscreenOptimizations();
			RequestFullGameDetection();
			RaiseLibraryChanged();
			return true;
		}

		private bool RendererPathClaimedLocked(GameProfile self, string resolved)
		{
			foreach (GameProfile profile in profiles)
			{
				if (!object.ReferenceEquals(profile, self))
				{
					if (string.Equals(profile.ExecutablePath, resolved, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
					if (string.Equals(profile.LearnedExecutablePath, resolved, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			return false;
		}

		public bool SetProfileForceTrigger(string profileId, bool on)
		{
			bool flag = false;
			bool flag2 = false;
			string text = null;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				foreach (GameProfile profile in profiles)
				{
					if (string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
					{
						if (on && string.IsNullOrEmpty(profile.ExecutablePath) && string.IsNullOrEmpty(profile.LearnedExecutablePath))
						{
							return false;
						}
						if (profile.ForceTrigger != on)
						{
							profile.ForceTrigger = on;
							text = profile.Name;
							flag = true;
							if (!PersistLibraryLocked())
							{
								profile.ForceTrigger = !on;
								return false;
							}
							break;
						}
						break;
					}
				}
				if (flag && !on)
				{
					flag2 = activeDetection != null && activeDetection.Profile != null && string.Equals(activeDetection.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase);
				}
			}
			if (!flag)
			{
				return true;
			}
			if (flag2)
			{
				panicReq = true;
			}
			Logger.Log((on ? Lang.T("log.gamemodelibrary.5") : Lang.T("log.gamemodelibrary.6")) + text + (on ? Lang.T("log.gamemodelibrary.7") : Lang.T("log.gamemodelibrary.8")));
			KickLibraryChanged();
			return true;
		}

		private GameProfile FindProfileLocked(string profileId)
		{
			foreach (GameProfile profile in profiles)
			{
				if (string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
				{
					return profile;
				}
			}
			return null;
		}

		private bool LiveBoolPreferenceLocked(string policyKey, bool globalOn)
		{
			return (sessionPolicy ?? PolicyResolver.Global()).ValueOf(policyKey) == "1";
		}

		private void InvalidateOverrideWorkLocked(string key)
		{
			if (key == "GmCacheWarmV2" || key == "GmStandbyCleanerV2")
			{
				InvalidateCacheWarm();
			}
			if (key == "GmStandbyCleanerV2")
			{
				InvalidateStandbyCleanerWork();
			}
			if (key == "GmEnglishInputV1")
			{
				InvalidateEnglishInputWork();
			}
		}

		private void NotifyOverridePolicyChanged(string key, bool effectiveOn)
		{
			if (key == "GmAutoCoresV2" || key == "GmManualCorePlanV2")
			{
				RaiseLibraryChanged();
				return;
			}
			if (key == "GameDisableFullscreenOptimizationsV1")
			{
				InvalidateAutomaticFullscreenOptimizations();
				SyncAutomaticFullscreenOptimizations(true, -1L);
				RaiseLibraryChanged();
				return;
			}
			if (key == "GameNvRebarV1")
			{
				lock (sync)
				{
					if (sessionPolicy != null)
					{
						sessionPolicy = PolicyResolver.For(FindProfileLocked(sessionPolicy.ProfileId));
					}
				}
			}
			switch (key)
			{
			case "GmPauseServices":
				PauseServicesPolicyChanged(effectiveOn);
				break;
			case "GmStandbyCleanerV2":
				StandbyCleanerPolicyChanged(effectiveOn);
				break;
			case "PowerPlanOn":
			case "GmEnglishInputV1":
			case "GmCacheWarmV2":
			case "GameNvRebarV1":
			case "GmAutoCoresV2":
			case "GmManualCorePlanV2":
			case "PerformancePreset":
				RequestPolicyApply("NotifyOverridePolicyChanged");
				break;
			}
		}

		private bool GlobalPolicyOn(string key)
		{
			switch (key)
			{
			case "GmPauseServices":
				return pauseServicesOn;
			case "GmStandbyCleanerV2":
				return standbyCleanerOn;
			case "GmCacheWarmV2":
				return cacheWarmOn;
			default:
				return false;
			}
		}

		public bool SetProfileOverride(string profileId, string key, string value)
		{
			if (key == "GameFamilyBackground")
			{
				string text = PolicyCatalog.Canonical(key, value);
				if (text != null)
				{
					return SetProfileFamilySuppression(profileId, text == "1");
				}
				return false;
			}
			bool flag = false;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				if (key == "PerformancePreset" && ModeSwitchLockedLocked())
				{
					return false;
				}
				if (key == "GmStandbyCleanerV2" && PolicyCatalog.Canonical(key, value) == "1" && !standbyCleanerOptionsValid)
				{
					return false;
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile != null)
				{
					if (key == "GmAutoCoresV2")
					{
						GameProfile draft = gameProfile.Clone();
						if (!PolicyResolver.SetOverride(draft, key, value) || ValidateCriticalPreference(draft) != null) return false;
					}
					string value2 = null;
					bool flag2 = key != null && gameProfile.Overrides.TryGetValue(key, out value2);
					InvalidateOverrideWorkLocked(key);
					flag = PolicyResolver.SetOverride(gameProfile, key, value);
					if (flag && !SaveProfilesLocked())
					{
						if (flag2)
						{
							gameProfile.Overrides[key] = value2;
						}
						else
						{
							gameProfile.Overrides.Remove(key);
						}
						flag = false;
					}
				}
			}
			if (flag)
			{
				NotifyOverridePolicyChanged(key, PolicyCatalog.Canonical(key, value) == "1");
			}
			return flag;
		}

		public bool ClearProfileOverride(string profileId, string key)
		{
			if (key == "GameFamilyBackground")
			{
				return SetProfileFamilySuppression(profileId, false);
			}
			bool flag = false;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile != null && gameProfile.Overrides.ContainsKey(key))
				{
					string value = gameProfile.Overrides[key];
					InvalidateOverrideWorkLocked(key);
					gameProfile.Overrides.Remove(key);
					flag = (key != "GmAutoCoresV2" || ValidateCriticalPreference(gameProfile) == null) && SaveProfilesLocked();
					if (!flag)
					{
						gameProfile.Overrides[key] = value;
					}
				}
			}
			if (flag)
			{
				NotifyOverridePolicyChanged(key, GlobalPolicyOn(key));
			}
			return flag;
		}

		public int ClearProfileOverrides(string profileId)
		{
			int num = 0;
			string text = null;
			bool flag = false;
			bool flag2 = false;
			lock (familyPolicyGate)
			{
				lock (sync)
				{
					if (stopping)
					{
						return 0;
					}
					GameProfile gameProfile = FindProfileLocked(profileId);
					if (gameProfile != null && gameProfile.Overrides.Count > 0 && !ProfileStoreSaveFailed)
					{
						flag2 = gameProfile.Overrides.ContainsKey("GmStandbyCleanerV2");
						foreach (string key in gameProfile.Overrides.Keys)
						{
							InvalidateOverrideWorkLocked(key);
						}
						GameProfile gameProfile2 = gameProfile.Clone();
						num = PolicyResolver.ClearAllOverrides(gameProfile2);
						List<GameProfile> list = new List<GameProfile>(profiles);
						int index = profiles.IndexOf(gameProfile);
						list[index] = gameProfile2;
						if (!SaveProfileSnapshotLocked(list))
						{
							return 0;
						}
						profiles[index] = gameProfile2;
						DisableSessionCriticalPreferenceLocked(profileId);
						flag = gameProfile.Overrides.ContainsKey("GmPauseServices");
						text = gameProfile.Name;
						InvalidateFamilyPolicy();
					}
				}
			}
			if (num > 0)
			{
				NotifyOverridePolicyChanged("GameDisableFullscreenOptimizationsV1", false);
				NotifyOverridePolicyChanged("GameNvRebarV1", false);
				if (flag)
				{
					PauseServicesPolicyChanged(pauseServicesOn);
				}
				if (flag2)
				{
					StandbyCleanerPolicyChanged(standbyCleanerOn);
				}
				if (!flag && !flag2)
				{
					RequestPolicyApply("ClearProfileOverrides");
				}
				RaiseLibraryChanged();
			}
			if (text != null)
			{
				Logger.Log(Lang.T("log.gamemodelibrary.9") + text + Lang.T("log.gamemodelibrary.10") + num + Lang.T("log.gamemodelibrary.11"));
			}
			return num;
		}

		public List<PolicyDiff> DiffProfile(string profileId)
		{
			GameProfile profile = null;
			lock (sync)
			{
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile != null)
				{
					profile = gameProfile.Clone();
				}
			}
			return PolicyResolver.Diff(profile);
		}

		public bool RenameProfile(string profileId, string name)
		{
			string text = (name ?? "").Trim();
			if (text.Length == 0)
			{
				return false;
			}
			string text2 = null;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile == null)
				{
					return false;
				}
				if (string.Equals(gameProfile.Name, text, StringComparison.Ordinal))
				{
					return true;
				}
				text2 = gameProfile.Name;
				gameProfile.Name = text;
				if (!SaveProfilesLocked())
				{
					gameProfile.Name = text2;
					return false;
				}
			}
			Logger.Log(Lang.T("log.gamemodelibrary.14") + text2 + Lang.T("log.gamemodelibrary.15") + text + Lang.T("log.gamemodelibrary.16"));
			RaiseLibraryChanged();
			return true;
		}

		public void RemoveProfile(string profileId)
		{
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked())
				{
					return;
				}
				HashSet<string> hashSet = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
				foreach (GameProfile profile in profiles)
				{
					if (string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
					{
						if (!string.IsNullOrEmpty(profile.ExecutablePath))
						{
							hashSet.Add(profile.ExecutablePath);
						}
						if (!string.IsNullOrEmpty(profile.LearnedExecutablePath))
						{
							hashSet.Add(profile.LearnedExecutablePath);
						}
					}
				}
				List<GameProfile> list = GetProfiles();
				if (list.RemoveAll((GameProfile p) => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) == 0 || !CommitLibraryLocked(list, hashSet))
				{
					return;
				}
				ClearModeSwitchLockForProfileLocked(profileId);
				InvalidateAutomaticFullscreenOptimizations();
				InvalidateCacheWarm();
				InvalidateStandbyCleanerWork();
				InvalidateEnglishInputWork();
				ClearFamilyDiscovery();
				ForgetRendererObservation(profileId);
				if ((activeDetection != null && activeDetection.Profile != null && string.Equals(activeDetection.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase)) || (sessionPolicy != null && string.Equals(sessionPolicy.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)))
				{
					panicReq = true;
				}
				InvalidateRendererHandoff();
			}
			SyncAutomaticFullscreenOptimizations(true, -1L);
			RequestFullGameDetection();
			RequestPolicyApply("RemoveProfile");
			RaiseLibraryChanged();
		}

		public List<WhitelistRuleView> GetWhitelistRules()
		{
			List<WhitelistRuleView> list = SnapshotWhitelistRuleViews();
			list.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
			{
				int num = a.Rule.Kind.CompareTo(b.Rule.Kind);
				return (num == 0) ? string.Compare(a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase) : num;
			});
			return list;
		}

		public List<WhitelistRuleView> GetWhitelistRulesFast()
		{
			List<WhitelistRuleView> list = new List<WhitelistRuleView>();
			AppendPlatformRuleViews(list, null);
			lock (sync)
			{
				foreach (WhitelistRule whiteRule in whiteRules)
				{
					list.Add(new WhitelistRuleView(whiteRule, -1, whiteRule.Kind == WhitelistRuleKind.LegacyName && IsPresetWhitelistName(whiteRule.Value)));
				}
			}
			list.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
			{
				int num = a.Rule.Kind.CompareTo(b.Rule.Kind);
				return (num == 0) ? string.Compare(a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase) : num;
			});
			return list;
		}

		public bool AddWhitelistPath(string executablePath)
		{
			return AddWhitelistRule(WhitelistRuleKind.ExactPath, executablePath);
		}

		public bool AddWhitelistAuto(string executablePath)
		{
			return AddWhitelistRule(ResolveAutoKind(executablePath), executablePath);
		}

		internal static WhitelistRuleKind ResolveAutoKind(string executablePath)
		{
			if (!WhitelistRule.IsUnsafeFamilyAnchor(executablePath))
			{
				return WhitelistRuleKind.ApplicationFamily;
			}
			return WhitelistRuleKind.ExactPath;
		}

		public bool NarrowWhitelistRule(string key)
		{
			WhitelistRule whitelistRule = null;
			lock (sync)
			{
				foreach (WhitelistRule whiteRule in whiteRules)
				{
					if (whiteRule.Key == key)
					{
						whitelistRule = whiteRule;
						break;
					}
				}
			}
			if (whitelistRule == null || whitelistRule.Kind != WhitelistRuleKind.ApplicationFamily)
			{
				return false;
			}
			if (!RemoveWhitelistRule(key))
			{
				return false;
			}
			if (AddWhitelistRule(WhitelistRuleKind.ExactPath, whitelistRule.Value))
			{
				return true;
			}
			AddWhitelistRule(WhitelistRuleKind.ApplicationFamily, whitelistRule.Value);
			return false;
		}

		public bool WidenWhitelistRule(string key)
		{
			WhitelistRule whitelistRule = null;
			lock (sync)
			{
				foreach (WhitelistRule whiteRule in whiteRules)
				{
					if (whiteRule.Key == key)
					{
						whitelistRule = whiteRule;
						break;
					}
				}
			}
			if (whitelistRule == null || whitelistRule.Kind != WhitelistRuleKind.ExactPath)
			{
				return false;
			}
			if (WhitelistRule.IsUnsafeFamilyAnchor(whitelistRule.Value))
			{
				return false;
			}
			if (!RemoveWhitelistRule(key))
			{
				return false;
			}
			if (AddWhitelistRule(WhitelistRuleKind.ApplicationFamily, whitelistRule.Value))
			{
				return true;
			}
			AddWhitelistRule(WhitelistRuleKind.ExactPath, whitelistRule.Value);
			return false;
		}

		private bool AddWhitelistRule(WhitelistRuleKind kind, string value)
		{
			WhitelistRule rule;
			if (!WhitelistRule.TryCreate(kind, value, out rule))
			{
				lock (sync)
				{
					whitelistLastError = Lang.T("white.duplicate");
				}
				return false;
			}
			int num;
			int matched;
			lock (whiteEvalSync)
			{
				lock (sync)
				{
					if (stopping)
					{
						return false;
					}
					if (whiteRuleKeys.Contains(rule.Key))
					{
						whitelistLastError = Lang.T("white.duplicate");
						return false;
					}
					List<WhitelistRule> list = new List<WhitelistRule>(whiteRules);
					list.Add(rule);
					List<WhitelistRule> rules = list;
					if (!SaveWhite(rules))
					{
						whitelistLastError = Lang.T("white.save.failed");
						return false;
					}
					AddWhiteRuleNoSave(rule);
					whitelistLastError = "";
				}
				num = ReleaseCurrentWhitelistMatches(out matched);
			}
			Logger.Log(string.Concat(Lang.T("log.gamemodelibrary.17"), rule.Kind, " ", rule.Value, Lang.T("log.gamemodelibrary.18"), matched, Lang.T("log.gamemodelibrary.19"), num, Lang.T("log.gamemodelibrary.20")));
			RequestPolicyApply("AddWhitelistRule");
			return true;
		}

		public bool RemoveWhitelistRule(string key)
		{
			if (string.IsNullOrEmpty(key))
			{
				return false;
			}
			if (key.StartsWith("platform:", StringComparison.Ordinal))
			{
				return RemovePlatformProtection(key.Substring("platform:".Length));
			}
			lock (whiteEvalSync)
			{
				lock (sync)
				{
					if (stopping)
					{
						return false;
					}
					WhitelistRule whitelistRule = whiteRules.Find((WhitelistRule rule) => string.Equals(rule.Key, key, StringComparison.OrdinalIgnoreCase));
					if (whitelistRule == null)
					{
						return false;
					}
					if (whitelistRule.Kind == WhitelistRuleKind.LegacyName && IsPresetWhitelistName(whitelistRule.Value))
					{
						whitelistLastError = Lang.T("white.required");
						return false;
					}
					List<WhitelistRule> list = new List<WhitelistRule>(whiteRules);
					list.Remove(whitelistRule);
					if (!SaveWhite(list))
					{
						whitelistLastError = Lang.T("white.save.failed");
						return false;
					}
					whiteRules.Remove(whitelistRule);
					whiteRuleKeys.Remove(key);
					whiteFamilyMembers.Remove(key);
					whiteFamilyMembersVersion++;
					whiteRevision++;
					RefreshWhitelistFamilyFlagLocked();
					whitelistLastError = "";
				}
			}
			RequestPolicyApply("RemoveWhitelistRule");
			return true;
		}

		public bool ResetWhitelist()
		{
			List<WhitelistRule> list = new List<WhitelistRule>();
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string[] presetWhitelist = SystemProcessCatalog.PresetWhitelist;
			foreach (string value in presetWhitelist)
			{
				WhitelistRule rule;
				if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, value, out rule) && hashSet.Add(rule.Key))
				{
					list.Add(rule);
				}
			}
			int num;
			int matched;
			lock (whiteEvalSync)
			{
				lock (sync)
				{
					if (stopping)
					{
						return false;
					}
					if (!SaveWhite(list))
					{
						whitelistLastError = Lang.T("white.save.failed");
						return false;
					}
					whiteRules.Clear();
					whiteRuleKeys.Clear();
					whiteFamilyMembers.Clear();
					whiteFamilyMembersVersion++;
					whiteRevision++;
					RefreshWhitelistFamilyFlagLocked();
					foreach (WhitelistRule item in list)
					{
						AddWhiteRuleNoSave(item);
					}
					whitelistLastError = "";
				}
				num = ReleaseCurrentWhitelistMatches(out matched);
			}
			Logger.Log(Lang.T("log.gamemodelibrary.21") + SystemProcessCatalog.PresetWhitelist.Length + Lang.T("log.gamemodelibrary.22") + matched + Lang.T("log.gamemodelibrary.19") + num + Lang.T("log.gamemodelibrary.20"));
			RequestPolicyApply("ResetWhitelist");
			return true;
		}

		private bool SaveWhite(IList<WhitelistRule> rules)
		{
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				try
				{
					List<string> list = new List<string>();
					list.Add(Lang.T("t.gamemodelibrary.23"));
					list.Add(Lang.T("t.gamemodelibrary.24"));
					list.Add(Lang.T("t.gamemodelibrary.25"));
					list.Add("PAVISE_WHITELIST_V4");
					if (rules != null)
					{
						foreach (WhitelistRule rule in rules)
						{
							list.Add(rule.Serialize());
						}
					}
					list.Add(BuildWhitelistFooter(rules));
					return AtomicFile.WriteLines(whitePath, list.ToArray(), Lang.T("nav.white"));
				}
				catch (Exception error)
				{
					Logger.LogFailure(Lang.T("log.gamemodelibrary.26"), error);
					return false;
				}
			}
		}

		internal static string BuildWhitelistFooter(IList<WhitelistRule> rules)
		{
			ulong num = 1469598103934665603uL;
			int num2 = 0;
			if (rules != null)
			{
				foreach (WhitelistRule rule in rules)
				{
					if (rule != null)
					{
						string text = rule.Serialize();
						num2++;
						for (int i = 0; i < text.Length; i++)
						{
							num ^= (byte)text[i];
							num *= 1099511628211L;
						}
						num ^= 0xA;
						num *= 1099511628211L;
					}
				}
			}
			return "PAVISE_WHITELIST_END|" + num2 + "|" + num.ToString("X16");
		}

		private static string DisplayName(string executablePath, string fallback)
		{
			if (!string.IsNullOrWhiteSpace(fallback))
			{
				return fallback.Trim();
			}
			try
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
				string text = ((!string.IsNullOrWhiteSpace(versionInfo.FileDescription)) ? versionInfo.FileDescription : versionInfo.ProductName);
				if (!string.IsNullOrWhiteSpace(text))
				{
					return text.Trim();
				}
			}
			catch
			{
			}
			return Path.GetFileNameWithoutExtension(executablePath);
		}

		private static string NormalizeGameRoot(string root)
		{
			if (string.IsNullOrWhiteSpace(root))
			{
				return null;
			}
			try
			{
				string text = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
				return FamilyBoundary.SafeFamilyDir(text) ? text : null;
			}
			catch
			{
				return null;
			}
		}

		public void Start()
		{
			shutdownWritersStopped = false;
			worker = new Thread(Loop);
			worker.IsBackground = true;
			worker.Start();
		}

		public bool Stop()
		{
			shutdownWritersStopped = false;
			Stopwatch elapsed = Stopwatch.StartNew();
			stopping = true;
			InvalidatePowerPlanSession();
			SyncLaboratory();
			RevokeCriticalThreadIntent("Stop");
			SyncPerformanceAutomation();
			InvalidateCacheWarm();
			InvalidateStandbyCleanerWork();
			InvalidateEnglishInputWork();
			kick.Set();
			if (Monitor.IsEntered(sync) || !Monitor.TryEnter(sync, 8000))
			{
				return false;
			}
			try
			{
				ClearFamilyDiscovery();
				InvalidateRendererHandoff();
			}
			finally
			{
				Monitor.Exit(sync);
			}
			Thread thread = worker;
			if (thread != null && (thread == Thread.CurrentThread || !thread.Join(RemainingShutdownMs(elapsed, 8000))))
			{
				return false;
			}
			if (!DrainAsyncShutdown(RemainingShutdownMs(elapsed, 8000)))
			{
				return false;
			}
			bool manualRestored = false;
			try { manualRestored = LaboratoryManualActions.CloseAndRestoreRuntime(8000); }
			catch { }
			// A stopped writer may retain debt; only a running writer blocks unrelated cleanup.
			if (!manualRestored && LaboratoryManualActions.Busy) return false;
			bool flag = true;
			try { if (laboratory != null && !laboratory.Close(8000)) flag = false; }
			catch { flag = false; }
			try
			{
				if (!FrameCoordination.Close(8000))
				{
					flag = false;
				}
			}
			catch
			{
				flag = false;
			}
			try
			{
				if (!RenderLane.CloseForShutdown(8000))
				{
					flag = false;
				}
			}
			catch
			{
				flag = false;
			}
			try
			{
				if (standbyCleaner != null && !standbyCleaner.Close(8000))
				{
					flag = false;
				}
			}
			catch
			{
				flag = false;
			}
			if (!flag)
			{
				return false;
			}
			if (!performanceActions.Close(8000))
			{
				return false;
			}
			bool flag2 = StopGameNvDelivery(RemainingShutdownMs(elapsed, 8000));
			bool flag3 = DrainHistoricalCriticalThreads(RemainingShutdownMs(elapsed, 8000));
			bool flag4 = StopCriticalThreads(RemainingShutdownMs(elapsed, 8000), "Stop");
			if (!flag2 || !flag3 || !flag4)
			{
				return false;
			}
			bool recovered = FinishShutdownRecovery(core.RestoreAutomaticLayouts,
				() => { bool isolationClean = StopCoreIsolation(); bool backgroundClean = core.StopBackgroundAffinityJobs(); return coreAffinityJobs.Stop() && backgroundClean && isolationClean; }, RestoreAllHardPins, RestoreGuardScheduling);
			SelfYield.Release();
			return recovered && manualRestored && !SelfYield.HasResidue;
		}

		internal bool FinishShutdownRecovery(Func<bool> restoreLayouts, Func<bool> stopIsolation, Func<bool> restorePins, Func<bool> restoreGuard)
		{
			shutdownWritersStopped = false;
			bool clean = true;
			bool writersStopped = true;
			ShutdownRecoveryStep(restoreLayouts, false, ref clean, ref writersStopped);
			ShutdownRecoveryStep(stopIsolation, true, ref clean, ref writersStopped);
			ShutdownRecoveryStep(restorePins, false, ref clean, ref writersStopped);
			ShutdownRecoveryStep(restoreGuard, false, ref clean, ref writersStopped);
			shutdownWritersStopped = writersStopped;
			return clean;
		}

		private static void ShutdownRecoveryStep(Func<bool> work, bool requiresWriterExit, ref bool clean, ref bool writersStopped)
		{
			try
			{
				if (work != null && work())
				{
					return;
				}
			}
			catch
			{
			}
			clean = false;
			if (requiresWriterExit)
			{
				writersStopped = false;
			}
		}

		private bool DrainAsyncShutdown(int timeoutMs)
		{
			if (!stopping || timeoutMs < 0)
			{
				return false;
			}
			Stopwatch elapsed = Stopwatch.StartNew();
			if (cacheWarm != null && !cacheWarm.Drain(RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (!DrainShutdownGate(driverStageGate, RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (!DrainLaptopPerformanceWork(RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (!DrainShutdownGate(powerApplyGate, RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (!DrainShutdownGate(whiteEvalSync, RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (!DrainEnglishInput(RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (standbyCleaner != null && !standbyCleaner.Close(RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			RendererObservationStore rendererObservationStore = rendererObservations;
			if (rendererObservationStore != null)
			{
				return rendererObservationStore.Close(RemainingShutdownMs(elapsed, timeoutMs));
			}
			return true;
		}

		private static int RemainingShutdownMs(Stopwatch elapsed, int timeoutMs)
		{
			return (int)Math.Max(0L, timeoutMs - elapsed.ElapsedMilliseconds);
		}

		private static bool DrainShutdownGate(object gate, int timeoutMs)
		{
			if (Monitor.IsEntered(gate) || !Monitor.TryEnter(gate, timeoutMs))
			{
				return false;
			}
			try
			{
				return true;
			}
			finally
			{
				Monitor.Exit(gate);
			}
		}

		public void Poke()
		{
			RequestPolicyApply("Poke");
		}

		private void Loop()
		{
			while (!stopping)
			{
				if (manualLaboratoryMutation) { kick.WaitOne(100); continue; }
				IDisposable disposable = PolicyResolver.BeginReadScope();
				try
				{
					RetryPendingHardPinRestores();
					ApplyEnvironmentTimerOptions();
					if (panicReq)
					{
						int value = Volatile.Read(ref panicSeq);
						panicReq = false;
						panicResult = Deactivate(Lang.T("t.gamemode.42"));
						Volatile.Write(ref panicServed, value);
						panicDone.Set();
						kick.WaitOne(4000);
						continue;
					}
					PrivacyControl.TryRestoreRetired(() => !active && !stopping && !panicReq);
					SyncPerformanceAutomation();
					SyncAutomaticFullscreenOptimizations(false, -1L);
					SyncGameNvDelivery();
					MaybeRecoverHistoricalCriticalThreads();
					if (!enabled || ProfileStoreSaveFailed)
					{
						InvalidateRendererHandoff();
						bool flag;
						lock (sync)
						{
							flag = active || gameBoost.Count > 0;
						}
						if (flag || EnvActive() || core.AnyWith(SuppressReason.Background))
						{
							RetryDeactivate(Lang.T("t.gamemode.43"));
						}
						goto IL_068f;
					}
					bool fallbackOnly;
					if (!ShouldRunProcessScan(out fallbackOnly))
					{
						kick.WaitOne(LoopWaitMs(ProcessScanWaitMs()));
						continue;
					}
					ProcessSnapshot processSnapshot = null;
					CountProcessScan();
					int maxAgeMs = ((fallbackOnly && ProcessEventsAvailable && IsActive) ? 5000 : 0);
					try
					{
						processSnapshot = ProcessSnapshotSource.Capture(selfSession, maxAgeMs);
					}
					catch
					{
						processSnapshot = null;
					}
					if (processSnapshot == null)
					{
						RequeueProcessScanAfterFailure();
						goto IL_068f;
					}
					PruneWhitelistFamilyMembersIfDue(processSnapshot);
					antiCheatProcessSnapshot = processSnapshot;
					HashSet<int> gamePids;
					string text = FindRunningGame(processSnapshot, out gamePids);
					if (!enabled || stopping || panicReq || ProfileStoreSaveFailed)
					{
						continue;
					}
					if (text != null)
					{
						string detectedProfileId;
						lock (sync)
						{
							detectedProfileId = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Id : null);
						}
						if (gameGoneSinceTicks != 0)
						{
							gameGoneSinceTicks = 0L;
							if (active)
							{
								Logger.Log(Lang.T("log.gamemode.44"));
								lock (sync)
								{
									firstSweep = true;
								}
							}
						}
						if (!active)
						{
							lock (sync)
							{
								if (manualLaboratoryMutation) continue;
								active = true;
								activeGame = text;
								firstSweep = true;
							}
							Logger.Log(Lang.T("log.gamemode.45") + text);
							ResetAdaptiveGuard();
							Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
							BeginSessionPolicy();
							ReportBegin(text);
							NotifyExtensionSession(true);
							slowEnvAtTicks = DateTime.UtcNow.AddSeconds(20.0).Ticks;
						}
						else if (!SameReportedProfile(repProfileId, detectedProfileId))
						{
							lock (sync)
							{
								activeGame = text;
							}
							Logger.Log(Lang.T("log.gamemode.46") + text);
							ResetAdaptiveGuard();
							Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
							ReportFinish();
							BeginSessionPolicy();
							ReportBegin(text);
							NotifyExtensionSession(true);
						}
						else if (!string.Equals(activeGame, text, StringComparison.Ordinal))
						{
							lock (sync)
							{
								activeGame = text;
							}
						}
						int num;
						long birth;
						lock (sync)
						{
							num = ((activeDetection != null) ? activeDetection.RendererPid : 0);
							birth = ((activeDetection != null) ? activeDetection.RendererCreation : 0);
						}
						UpdateSessionForeground(processSnapshot, num, gamePids);
						bool smartSceneSwitchedOut = SmartSceneSwitchedOut;
						StepEnglishInputSession();
						ApplyEnv();
						SyncPerformanceAutomation();
						ApplyStandbyCleanerPolicy();
						ApplyCacheWarmPolicy();
						RefreshAutoCoreMode();
						if (EffSuppress && !smartSceneSwitchedOut)
						{
							Sweep(processSnapshot, gamePids);
						}
						else
						{
							ReleaseBackground(smartSceneSwitchedOut ? SwitchOutReleaseText : Lang.T("t.gamemodeenv.41"));
						}
						SelfYield.TrackHelperRenderer(num, birth);
						SelfYield.Engage();
						ApplyGameProcessActions(processSnapshot);
						StepCriticalThreadRuntime();
						if (!smartSceneSwitchedOut)
						{
							StepAdaptiveGuard();
						}
					}
					else if (active)
					{
						StepDisplayAwake(false);
						long ticks = DateTime.UtcNow.Ticks;
						if (gameGoneSinceTicks == 0)
						{
							gameGoneSinceTicks = ticks;
							InvalidatePowerPlanSession();
							RevokeCriticalThreadIntent("session-unconfirmed/exit-grace");
							StopCriticalThreads(0, "session-unconfirmed/exit-grace");
							InvalidateStandbyCleanerWork();
							InvalidateCacheWarm();
							InvalidateEnglishInputWork();
							Logger.Log(Lang.T("log.gamemode.47") + ExitGraceSeconds + Lang.T("log.gamemode.48"));
							gracePreReleased += ReleaseBackground(Lang.T("t.gamemode.49"));
						}
						if (ExitGraceElapsed(gameGoneSinceTicks, ticks, ExitGraceSeconds))
						{
							Deactivate(Lang.T("t.gamemode.50"));
						}
						if (gameGoneSinceTicks != 0)
						{
							Interlocked.Exchange(ref transitionScanPending, 1);
						}
					}
					else
					{
						DropVanishedBoosts();
						bool flag2;
						lock (sync)
						{
							flag2 = gameBoost.Count > 0;
						}
						if (flag2 || EnvActive() || core.AnyWith(SuppressReason.Background))
						{
							RetryDeactivate(Lang.T("t.gamemode.51"));
						}
						TryAutoAddForegroundGame();
					}
					goto IL_068f;
					IL_068f:
					Logger.RepeatedRecovered("GameMode");
					goto IL_0710;
				}
				catch (Exception ex)
				{
					RevokeCriticalThreadIntent("loop-exception");
					StopCriticalThreads(0, "loop-exception");
					Logger.RepeatedError("GameMode", Lang.T("log.gamemode.52") + ex.GetType().Name + ": " + ex.Message, -1L);
					goto IL_0710;
				}
				finally
				{
					disposable.Dispose();
				}
				IL_0710:
				kick.WaitOne(LoopWaitMs(enabled ? ProcessScanWaitMs() : 4000));
			}
			bool flag3;
			lock (sync)
			{
				flag3 = active || gameBoost.Count > 0;
			}
			bool flag4 = true;
			if (flag3 || core.AnyWith(SuppressReason.Background) || EnvActive())
			{
				flag4 = Deactivate(Lang.T("t.gamemode.53"));
			}
			if (panicReq)
			{
				int value2 = Volatile.Read(ref panicSeq);
				panicReq = false;
				panicResult = flag4;
				Volatile.Write(ref panicServed, value2);
				panicDone.Set();
			}
			try
			{
				RestoreGuardScheduling();
			}
			catch
			{
			}
		}

		internal string SaveManualCorePlan(string profileId, string expected, ManualCorePlan plan)
		{
			string rawRule, enabled;
			lock (sync)
			{
				GameProfile current = FindProfileLocked(profileId);
				if (current == null || ManualCorePlan.Raw(current) != expected) return "schedule.manual.changed";
				rawRule = CriticalThreadRule.Raw(current);
				enabled = PolicyResolver.For(current).ValueOf("GmAutoCoresV2");
			}
			if (rawRule != null && CriticalThreadRule.Parse(rawRule) == null) return "schedule.manual.changed";
			return SaveCoreConfiguration(profileId, expected, rawRule, enabled, plan,
				plan != null && ManualCorePlan.Parse(expected) != null && enabled == "1", CriticalThreadRule.Parse(rawRule));
		}

		private void SyncPerformanceAutomation()
		{
			SyncLaboratory();
			string[] games = null;
			bool flag;
			bool flag2;
			bool visualMatch;
			lock (sync)
			{
				flag = enabled && !stopping && !panicReq && !ProfileStoreSaveFailed && !autoIgnoreLoadFailed && (libraryIgnoreTransaction == null || !libraryIgnoreTransaction.RecoveryPending);
				flag2 = flag && active;
                visualMatch = false; // Desktop visual simplification is retired; only restore old records.
				if (flag && !flag2 && (PerformanceTuning.ActionSelected("AutoGameMitigationsV1") || PerformanceTuning.LegacyActionSelected("AutoOpenGlThreadsV1") || PerformanceTuning.ActionSelected("AutoNtfsShortNamesV1")))
				{
					HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (GameProfile profile in profiles)
					{
						AddGamePerformancePath(hashSet, profile.ExecutablePath);
						AddGamePerformancePath(hashSet, profile.LearnedExecutablePath);
					}
					GameDetection gameDetection = activeDetection;
					if (gameDetection != null && gameDetection.Profile != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && RendererHandoffTracker.SameProfile(gameDetection.Profile, FindProfileLocked(gameDetection.Profile.Id)))
					{
						AddGamePerformancePath(hashSet, gameDetection.RendererPath);
					}
					games = new List<string>(hashSet).ToArray();
				}
			}
			performanceActions.Queue(flag, flag2, games, visualMatch, true);
		}

		internal bool SetVisualEffectsReduction(bool value)
		{
			lock (sync)
			{
                if (value || stopping || !Settings.Save("MatchVisualEffectsV1", false))
				{
					return false;
				}
			}
			SyncPerformanceAutomation();
			RequestPolicyApply("SetVisualEffectsReduction");
			return true;
		}

		internal bool SetPerformanceActionSelected(string key, bool value)
		{
			PerformanceAction performanceAction = PerformanceActionCatalog.Find(key);
			if (performanceAction == null || performanceAction.Environment)
			{
				return false;
			}
			if (key == "MatchVisualEffectsV1")
			{
				return SetVisualEffectsReduction(value);
			}
			lock (sync)
			{
				if (stopping || !Settings.Save(key, value))
				{
					return false;
				}
			}
			SyncPerformanceAutomation();
			RequestPolicyApply("SetPerformanceActionSelected");
			return true;
		}

		internal static bool PerformanceActionSelected(string key)
		{
			return PerformanceTuning.ActionSelected(key);
		}

		private void AddGamePerformancePath(HashSet<string> games, string path)
		{
			if (!string.IsNullOrEmpty(path))
			{
				games.Add(path);
			}
		}

		internal string PerformanceExecutionState(string key)
		{
			return performanceActions.State(key);
		}

		private Dictionary<int, PlatformProcessOwner> CollectPlatformMembers(ProcessSnapshot snapshot)
		{
			HashSet<int> hashSet = new HashSet<int>();
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			lock (sync)
			{
				if (activeDetection != null && activeDetection.RendererPid > 4)
				{
					hashSet.Add(activeDetection.RendererPid);
				}
				foreach (GameProfile profile in profiles)
				{
					if (!string.IsNullOrEmpty(profile.ExecutablePath))
					{
						hashSet2.Add(profile.ExecutablePath);
					}
					if (!string.IsNullOrEmpty(profile.LearnedExecutablePath))
					{
						hashSet2.Add(profile.LearnedExecutablePath);
					}
				}
			}
			if (snapshot != null)
			{
				ProcEntry[] entries = snapshot.Entries;
				foreach (ProcEntry procEntry in entries)
				{
					if (procEntry != null && (hashSet2.Contains(procEntry.Path) || (PlatformRuntimePolicy.RuntimePlatform(procEntry.Name) == null && FamilyBoundary.CatalogProtected(procEntry.Pid, procEntry.Creation, procEntry.Name, procEntry.Path))))
					{
						hashSet.Add(procEntry.Pid);
					}
				}
			}
			platformMembers = PlatformRuntimePolicy.Collect(snapshot, selfPid, selfSession, hashSet, platformMembers);
			return platformMembers;
		}

		private static bool PlatformRuleEnabled(PlatformProcessOwner owner)
		{
			if (owner.Platform == "wegame")
			{
				return PlatformRuntimePolicy.Enabled(owner.Platform);
			}
			return false;
		}

		private void AppendPlatformRuleViews(List<WhitelistRuleView> views, Dictionary<int, PlatformProcessOwner> members)
		{
			string[] ids = PlatformRuntimePolicy.Ids;
			foreach (string text in ids)
			{
				if (!PlatformRuntimePolicy.Enabled(text))
				{
					continue;
				}
				int num = ((members == null) ? (-1) : 0);
				if (members != null)
				{
					foreach (PlatformProcessOwner value in members.Values)
					{
						if (value.Platform == text)
						{
							num++;
						}
					}
				}
				WhitelistRule rule;
				if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, text, out rule))
				{
					views.Add(new WhitelistRuleView(rule, num, false, text));
				}
			}
		}

		private bool RemovePlatformProtection(string id)
		{
			lock (whiteEvalSync)
			{
				lock (sync)
				{
					if (stopping)
					{
						return false;
					}
					if (!PlatformRuntimePolicy.TryRemove(id))
					{
						whitelistLastError = Lang.T("white.save.failed");
						return false;
					}
					whiteRevision++;
					whitelistLastError = "";
				}
			}
			RequestPolicyApply("RemovePlatformProtection");
			return true;
		}

		public bool RestorePlatformProtection()
		{
			lock (whiteEvalSync)
			{
				lock (sync)
				{
					if (stopping)
					{
						return false;
					}
					if (!PlatformRuntimePolicy.TryRestore())
					{
						whitelistLastError = Lang.T("white.save.failed");
						return false;
					}
					whiteRevision++;
					whitelistLastError = "";
				}
				int matched;
				ReleaseCurrentWhitelistMatches(out matched);
			}
			RequestPolicyApply("RestorePlatformProtection");
			return true;
		}

		internal static NvGamePlan AutomaticPreStagePlan(PolicySnapshot policy, bool onAc, bool windowedVrr)
		{
			return NvGamePlan.ForAutomatic(policy.NvMaxPerf, policy.NvShaderCacheMax, onAc, policy.NvLowLatMode, windowedVrr, policy.NvRebar);
		}

		private bool RunPreStagedMutation(Action mutation)
		{
			return RunPreStagedWork(mutation, false);
		}

		private bool RunPreStagedWork(Action mutation, bool restoring)
		{
			lock (driverStageGate)
			{
				if (stopping || IsActive || mutation == null || (!restoring && sessionRecoveryCaptured && sessionRecoveryRoutes != null && !sessionRecoveryRoutes.Complete))
				{
					return false;
				}
				mutation();
				return true;
			}
		}

		private void PreStageDriverTuning(List<GameProfile> copy, string armedName)
		{
			if (stopping || (sessionRecoveryCaptured && sessionRecoveryRoutes != null && !sessionRecoveryRoutes.Complete))
			{
				return;
			}
			GameProfile armed = null;
			foreach (GameProfile item in copy)
			{
				if (string.Equals(item.Name, armedName, StringComparison.OrdinalIgnoreCase))
				{
					armed = item;
					break;
				}
			}
			if (armed == null)
			{
				return;
			}
			PrewarmAutomatic(armed);
			string path = armed.PreferredExecutablePath;
			if (string.IsNullOrEmpty(path))
			{
				return;
			}
			PolicySnapshot policySnapshot;
			try
			{
				policySnapshot = PolicyResolver.For(armed);
			}
			catch
			{
				return;
			}
			NvGamePlan plan = new NvGamePlan
			{
				MaxPerf = policySnapshot.NvMaxPerf,
				LowLatMode = policySnapshot.NvLowLatMode,
				SmoothMotion = policySnapshot.NvSmoothMotion,
				ShaderCacheMax = policySnapshot.NvShaderCacheMax,
				Rebar = policySnapshot.NvRebar,
				DlssMode = policySnapshot.NvDlssMode,
				WindowedVrr = nvVrrWindowedOn
			};
			plan = AutomaticPreStagePlan(policySnapshot, Native.OnAcPower(), nvVrrWindowedOn);
			bool stageGpuPref = gpuPrefStageOn && GpuPrefStage.Supported;
			QueuePreStagedNvPlan(armed, path, plan, stageGpuPref);
		}

		private void ReleasePreStagedTuning()
		{
			InvalidateCacheWarm();
			if (stopping || preStagedNvPath == null)
			{
				return;
			}
			preStagedNvPath = null;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					RunPreStagedWork(delegate
					{
						NvDrsTweaks.RestoreAllGames();
						GpuPrefStage.Restore();
					}, true);
				}
				catch
				{
				}
			});
		}

		private void UpdateArmedStatus(string name, string via, bool engaged)
		{
			armedGameName = name;
			if (!string.Equals(name, lastArmedLogged, StringComparison.OrdinalIgnoreCase))
			{
				if (name != null)
				{
					Logger.Log(Lang.T("log.gamemode.54") + name + Lang.T("log.gamemode.55") + (string.IsNullOrEmpty(via) ? "" : (Lang.T("log.gamemode.62") + via)));
				}
				else if (lastArmedLogged != null && !engaged)
				{
					Logger.Log(Lang.T("log.gamemode.56") + lastArmedLogged + Lang.T("log.gamemode.57"));
					ReleasePreStagedTuning();
				}
				lastArmedLogged = name;
			}
		}

		public void KickGameDetectionNow()
		{
			RequestFullGameDetection();
			try
			{
				kick.Set();
			}
			catch
			{
			}
		}

		public bool NeedsGameProcessIdentity(string name, int session)
		{
			if (session != selfSession || string.IsNullOrEmpty(name))
			{
				return false;
			}
			lock (sync)
			{
				foreach (GameProfile profile in profiles)
				{
					if (GameSessionDetector.IsProfileEntryName(profile, name))
					{
						return true;
					}
				}
			}
			return false;
		}

		private void CountProcessScan()
		{
			Interlocked.Increment(ref processScanCount);
		}

		public void NotifyProcessChanges(ProcessChangeBatch batch)
		{
			if (batch == null || stopping)
			{
				return;
			}
			ObserveWhitelistProcessChanges(batch);
			ObserveGameFamilyChanges(batch);
			bool flag = batch.Overflowed;
			bool gameSessionBoundary = false;
			bool flag2 = false;
			lock (sync)
			{
				flag |= active || armedAwaitingElection;
				ProcessChange[] changes = batch.Changes;
				foreach (ProcessChange processChange in changes)
				{
					if (processChange == null)
					{
						continue;
					}
					if (processChange.Kind == ProcessChangeKind.Stopped)
					{
						if (activeDetection != null && activeDetection.RendererPid == processChange.Pid)
						{
							gameSessionBoundary = true;
						}
						continue;
					}
					if (IsActiveFamilyChildStart(activeDetection, processChange, selfSession))
					{
						flag = true;
						long rendererCreation = activeDetection.RendererCreation;
						if (!IsSameTransitionEpoch(transitionProbeRendererPid, transitionProbeRendererCreation, activeDetection.RendererPid, rendererCreation))
						{
							transitionProbeRendererPid = activeDetection.RendererPid;
							transitionProbeRendererCreation = rendererCreation;
							flag2 = true;
						}
					}
					if (string.IsNullOrEmpty(processChange.Name))
					{
						continue;
					}
					foreach (GameProfile profile in profiles)
					{
						if (GameSessionDetector.IsProfileEntryProcess(profile, processChange.Name, processChange.Path))
						{

							flag = true;
							gameSessionBoundary = true;
							break;
						}
					}
				}
			}
			if (batch.Overflowed)
			{
				Interlocked.Exchange(ref gameDetectionDirty, 1);
			}
			if (flag2)
			{
				Interlocked.Exchange(ref gameDetectionDirty, 1);
				Interlocked.Exchange(ref transitionScanPending, 1);
			}
			if (ProcessEventNeedsImmediateScan(gameSessionBoundary))
			{
				Interlocked.Exchange(ref gameDetectionDirty, 1);
				Interlocked.Exchange(ref urgentProcessScan, 1);
			}
			if (flag)
			{
				Interlocked.Exchange(ref processSetDirty, 1);
				kick.Set();
			}
		}

		public bool NeedsLauncherChildParentIdentity(int eventParentPid, string eventName, int session)
		{
			if (session != selfSession || eventParentPid <= 0)
			{
				return false;
			}
			lock (sync)
			{
				return ShouldCaptureLauncherParentIdentity(activeDetection, eventParentPid);
			}
		}

		internal static bool ShouldCaptureLauncherParentIdentity(GameDetection detection, int eventParentPid)
		{
			if (detection != null && eventParentPid > 0 && detection.RendererPid == eventParentPid)
			{
				return detection.RendererCreation > 0;
			}
			return false;
		}

		internal static bool IsActiveFamilyChildStart(GameDetection detection, ProcessChange change, int ownerSession)
		{
			if (detection != null && change != null && change.Kind == ProcessChangeKind.Started && ownerSession >= 0 && change.Session == ownerSession && change.Creation > 0 && change.ParentPid > 0 && change.ParentPid == detection.RendererPid && detection.RendererCreation > 0 && change.ParentCreation == detection.RendererCreation)
			{
				return change.Creation > detection.RendererCreation;
			}
			return false;
		}

		internal static bool IsSameTransitionEpoch(int storedPid, long storedCreation, int currentPid, long currentCreation)
		{
			if (storedPid <= 0 || storedPid != currentPid)
			{
				return false;
			}
			if (storedCreation > 0 && currentCreation > 0)
			{
				return storedCreation == currentCreation;
			}
			return true;
		}

		internal static bool ShouldRearmLauncherTransition(GameDetection previous, GameDetection next)
		{
			if (next != null)
			{
				if (previous != null)
				{
					return !RendererHandoffTracker.SameIdentity(previous, next);
				}
				return true;
			}
			return false;
		}

		internal static bool ProcessEventNeedsImmediateScan(bool gameSessionBoundary)
		{
			return gameSessionBoundary;
		}

		internal static int ProcessScanIntervalMs(bool eventsAvailable)
		{
			if (!eventsAvailable)
			{
				return 4000;
			}
			return 20000;
		}

		internal static int ProcessScanIntervalMs(bool eventsAvailable, bool gameActive)
		{
			if (gameActive)
			{
				if (!eventsAvailable)
				{
					return 500;
				}
				return 1000;
			}
			return ProcessScanIntervalMs(eventsAvailable);
		}

		internal static bool DirtyScanDue(bool eventsAvailable, bool dirty, long elapsedMs)
		{
			if (!eventsAvailable || !dirty)
			{
				return false;
			}
			if (elapsedMs >= 0)
			{
				return elapsedMs >= 500;
			}
			return true;
		}

		private bool ShouldRunProcessScan(out bool fallbackOnly)
		{
			fallbackOnly = false;
			long ticks = DateTime.UtcNow.Ticks;
			long num = Interlocked.Read(ref processScanRetryAfterTicks);
			if (num > ticks)
			{
				return false;
			}
			if (num > 0)
			{
				Interlocked.CompareExchange(ref processScanRetryAfterTicks, 0L, num);
			}
			long num2 = Interlocked.Read(ref lastProcessScanTicks);
			long num3 = ticks - num2;
			bool flag = Interlocked.Exchange(ref urgentProcessScan, 0) != 0;
			bool flag2 = Interlocked.Exchange(ref processSetDirty, 0) != 0;
			if (flag)
			{
				Interlocked.Exchange(ref transitionScanPending, 0);
				Interlocked.Exchange(ref lastProcessScanTicks, ticks);
				return true;
			}
			if (Interlocked.CompareExchange(ref transitionScanPending, 0, 0) != 0 && (num2 <= 0 || num3 < 0 || num3 >= 50000000))
			{
				Interlocked.Exchange(ref transitionScanPending, 0);
				Interlocked.Exchange(ref lastProcessScanTicks, ticks);
				return true;
			}
			if (DirtyScanDue(ProcessEventsAvailable, flag2, ElapsedMsOrMax(num2, num3)))
			{
				Interlocked.Exchange(ref transitionScanPending, 0);
				Interlocked.Exchange(ref lastProcessScanTicks, ticks);
				return true;
			}
			int num4 = ProcessScanIntervalMs(ProcessEventsAvailable, IsActive);
			if (armedAwaitingElection)
			{
				num4 = Math.Min(num4, 4000);
			}
			long num5 = (long)num4 * 10000L;
			if (num2 <= 0 || num3 < 0 || num3 >= num5)
			{
				fallbackOnly = !armedAwaitingElection;
				Interlocked.Exchange(ref transitionScanPending, 0);
				Interlocked.Exchange(ref lastProcessScanTicks, ticks);
				return true;
			}
			if (flag2)
			{
				Interlocked.Exchange(ref processSetDirty, 1);
			}
			return false;
		}

		private int ProcessScanWaitMs()
		{
			long ticks = DateTime.UtcNow.Ticks;
			long num = Interlocked.Read(ref processScanRetryAfterTicks);
			if (num > ticks)
			{
				return TicksToWaitMilliseconds(num - ticks);
			}
			if (Interlocked.CompareExchange(ref urgentProcessScan, 0, 0) != 0)
			{
				return 1;
			}
			int num2 = ProcessScanIntervalMs(ProcessEventsAvailable, IsActive);
			if (armedAwaitingElection)
			{
				num2 = Math.Min(num2, 4000);
			}
			long num3 = Interlocked.Read(ref lastProcessScanTicks);
			if (num3 <= 0 || ticks < num3)
			{
				return 1;
			}
			long num4 = (ticks - num3) / 10000;
			long num5 = num2 - num4;
			if (Interlocked.CompareExchange(ref transitionScanPending, 0, 0) != 0)
			{
				num5 = Math.Min(num5, 5000 - num4);
			}
			if (ProcessEventsAvailable && Interlocked.CompareExchange(ref processSetDirty, 0, 0) != 0)
			{
				num5 = Math.Min(num5, 500 - num4);
			}
			if (num5 <= 0)
			{
				return 1;
			}
			return (int)Math.Min(num2, num5);
		}

		private static long ElapsedMsOrMax(long last, long elapsedTicks)
		{
			if (last <= 0 || elapsedTicks < 0)
			{
				return long.MaxValue;
			}
			return elapsedTicks / 10000;
		}

		private static int TicksToWaitMilliseconds(long ticks)
		{
			if (ticks <= 0)
			{
				return 1;
			}
			long val = (ticks + 10000 - 1) / 10000;
			return (int)Math.Min(2147483647L, Math.Max(1L, val));
		}

		private void RequeueProcessScanAfterFailure()
		{
			Interlocked.Exchange(ref urgentProcessScan, 1);
			Interlocked.Exchange(ref processSetDirty, 1);
			Interlocked.Exchange(ref processScanRetryAfterTicks, DateTime.UtcNow.AddMilliseconds(1000.0).Ticks);
		}

		private bool ShouldRunFullGameDetection()
		{
			long ticks = DateTime.UtcNow.Ticks;
			long num = Interlocked.Read(ref lastFullGameDetectionTicks);
			int num2 = (ProcessEventsAvailable ? 20000 : 4000);
			if (armedAwaitingElection)
			{
				num2 = 4000;
			}
			bool flag = num <= 0 || ticks < num || ticks - num >= (long)num2 * 10000L;
			if (Interlocked.Exchange(ref gameDetectionDirty, 0) != 0 || flag)
			{
				Interlocked.Exchange(ref lastFullGameDetectionTicks, ticks);
				return true;
			}
			return false;
		}

		private void RequestFullGameDetection()
		{
			Interlocked.Exchange(ref gameDetectionDirty, 1);
			Interlocked.Exchange(ref urgentProcessScan, 1);
			Interlocked.Exchange(ref processSetDirty, 1);
		}

		private void RequestPolicyApply([CallerMemberName] string caller = null)
		{
			RevokeCriticalThreadIntent("RequestPolicyApply/" + caller);
			Interlocked.Exchange(ref corePolicyDirty, 1);
			Interlocked.Exchange(ref gameDetectionDirty, 1);
			Interlocked.Exchange(ref urgentProcessScan, 1);
			Interlocked.Exchange(ref processSetDirty, 1);
			try
			{
				kick.Set();
			}
			catch
			{
			}
		}

		public List<GameProfile> GetProfiles()
		{
			lock (sync)
			{
				List<GameProfile> list = new List<GameProfile>();
				foreach (GameProfile profile in profiles)
				{
					list.Add(profile.Clone());
				}
				return list;
			}
		}

		private bool SaveProfilesLocked()
		{
			return SaveProfileSnapshotLocked(profiles);
		}

		private bool SaveProfileSnapshotLocked(IList<GameProfile> next, Func<bool> canCommit = null)
		{
			bool persistenceFailed;
			return SaveProfileSnapshotLocked(next, out persistenceFailed, canCommit);
		}

		private bool SaveProfileSnapshotLocked(IList<GameProfile> next, out bool persistenceFailed, Func<bool> canCommit = null)
		{
			persistenceFailed = false;
			if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked())
			{
				return false;
			}
			if (profileStore.Save(next, () => !stopping && (canCommit == null || canCommit())))
			{
				return true;
			}
			persistenceFailed = !profileStore.SaveCanceled && (profileStore.RetryableSaveFailure || profileStore.SaveFailed);
			SignalProfileStoreSaveFailure(profileStore.RetryableSaveFailure);
			return false;
		}

		private void SignalProfileStoreSaveFailure(bool retryableProfileSave = false)
		{
			if (((retryableProfileSave || profileStore.SaveCanceled) && !profileStore.SaveFailed) || Interlocked.Exchange(ref profileSaveFailureSignaled, 1) != 0)
			{
				return;
			}
			InvalidateStandbyCleanerWork();
			InvalidateEnglishInputWork();
			InvalidateRendererHandoff();
			Action profileStoreSaveFailure = this.ProfileStoreSaveFailure;
			if (profileStoreSaveFailure == null)
			{
				return;
			}
			try
			{
				profileStoreSaveFailure();
			}
			catch
			{
			}
		}

		private static long RendererNowMs()
		{
			long timestamp = Stopwatch.GetTimestamp();
			long frequency = Stopwatch.Frequency;
			return timestamp / frequency * 1000 + timestamp % frequency * 1000 / frequency;
		}

		private int RendererForegroundPid()
		{
			return GameSessionDetector.ForegroundPid();
		}

		private void ObserveRendererForegroundChange()
		{
			int num = RendererForegroundPid();
			if (num != rendererObservedForeground)
			{
				rendererObservedForeground = num;
				rendererForegroundSinceMs = RendererNowMs();
				Interlocked.Exchange(ref gameDetectionDirty, 1);
			}
		}

		private long RendererForegroundHeldMs(int pid)
		{
			if (pid <= 0 || pid != rendererObservedForeground || RendererForegroundPid() != pid)
			{
				return 0L;
			}
			long num = RendererNowMs() - rendererForegroundSinceMs;
			if (num >= 0)
			{
				return num;
			}
			return 0L;
		}

		private void InvalidateRendererHandoff()
		{
			lock (sync)
			{
				Interlocked.Increment(ref rendererHandoffEpoch);
				RendererHandoffTracker rendererHandoffTracker = rendererHandoff;
				if (rendererHandoffTracker != null)
				{
					rendererHandoffTracker.Clear();
				}
				rendererDirectPending = null;
			}
		}

		private bool RendererEpochCurrent(int epoch)
		{
			if (epoch == Volatile.Read(ref rendererHandoffEpoch) && enabled && !stopping && !panicReq)
			{
				return !ProfileStoreSaveFailed;
			}
			return false;
		}

		private bool VerifyRendererCandidate(GameDetection candidate)
		{
			if (candidate == null || candidate.RendererPid <= 0 || candidate.RendererCreation <= 0)
			{
				return false;
			}
			GameProcessSnapshot identity;
			if (GameSessionDetector.TryCaptureProcessIdentity(candidate.RendererPid, selfSession, out identity) && identity.Creation == candidate.RendererCreation && string.Equals(identity.Name, candidate.RendererName, StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(identity.Path, candidate.RendererPath, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static GameProfile RendererProfile(IList<GameProfile> library, string id)
		{
			if (library == null || string.IsNullOrEmpty(id))
			{
				return null;
			}
			foreach (GameProfile item in library)
			{
				if (item != null && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
				{
					return item;
				}
			}
			return null;
		}

		private GameDetection CaptureRendererChallenger(ProcessSnapshot all, IList<GameProfile> library, GameDetection incumbent)
		{
			return GameSessionDetector.CaptureForegroundCandidate(all, selfSession, library, incumbent, FamilyEvidence);
		}

		private BackgroundReleaseState ReleaseRendererBackground(GameDetection candidate)
		{
			BackgroundReleaseState backgroundReleaseState = core.ReleaseBackgroundForRenderer(candidate.RendererPid, candidate.RendererCreation, candidate.RendererName);
			if (backgroundReleaseState == BackgroundReleaseState.IdentityMismatch && VerifyRendererCandidate(candidate) && core.DiscardReusedRendererTracking(candidate.RendererPid, candidate.RendererCreation, candidate.RendererName))
			{
				backgroundReleaseState = core.ReleaseBackgroundForRenderer(candidate.RendererPid, candidate.RendererCreation, candidate.RendererName);
			}
			if (backgroundReleaseState == BackgroundReleaseState.Ready || backgroundReleaseState == BackgroundReleaseState.Gone)
			{
				ReportUntrack(candidate.RendererPid);
			}
			return backgroundReleaseState;
		}

		private GameDetection ResolveRendererHandoff(ProcessSnapshot all, IList<GameProfile> library, GameDetection raw, out int epoch)
		{
			epoch = Volatile.Read(ref rendererHandoffEpoch);
			if (!enabled || stopping || panicReq || ProfileStoreSaveFailed)
			{
				InvalidateRendererHandoff();
				return null;
			}
			long nowMs = RendererNowMs();
			GameDetection gameDetection = stickyDetection;
			if (gameDetection != null && gameDetection.Profile != null && !RendererHandoffTracker.SameProfile(gameDetection.Profile, RendererProfile(library, gameDetection.Profile.Id)))
			{
				ClearSticky();
				InvalidateRendererHandoff();
				gameDetection = null;
				epoch = Volatile.Read(ref rendererHandoffEpoch);
			}
			GameDetection current = Handoff.Current;
			if (current != null && (current.Profile == null || !RendererHandoffTracker.SameProfile(current.Profile, RendererProfile(library, current.Profile.Id))))
			{
				Handoff.Clear();
			}
			GameDetection gameDetection2 = CaptureRendererChallenger(all, library, gameDetection);
			GameDetection gameDetection3 = gameDetection2;
			if (gameDetection3 == null && raw != null && !raw.RendererForeground && raw.RendererCandidateSelected && !raw.RequiresGpuConfirm && !RendererHandoffTracker.SameIdentity(raw, gameDetection))
			{
				gameDetection3 = raw;
			}
			lock (sync)
			{
				if (!RendererEpochCurrent(epoch))
				{
					return null;
				}
				if (gameDetection3 != null && gameDetection3.Profile != null && RendererHandoffTracker.SameProfile(gameDetection3.Profile, FindProfileLocked(gameDetection3.Profile.Id)))
				{
					Handoff.Offer(gameDetection3, nowMs);
				}
			}
			current = Handoff.Current;
			if (current != null)
			{
				bool identityValid = RendererHandoffTracker.SameProfile(current.Profile, RendererProfile(library, current.Profile.Id)) && VerifyRendererCandidate(current);
				Handoff.Refresh(identityValid, RendererForegroundPid() == current.RendererPid, nowMs);
				current = Handoff.Current;
				if (current != null)
				{
					BackgroundReleaseState backgroundReleaseState = ReleaseRendererBackground(current);
					if ((backgroundReleaseState == BackgroundReleaseState.Gone || backgroundReleaseState == BackgroundReleaseState.IdentityMismatch) && !VerifyRendererCandidate(current))
					{
						Handoff.Clear();
					}
					else
					{
						Handoff.Recovery(current, backgroundReleaseState == BackgroundReleaseState.Ready);
						TryStartRendererProbe(epoch);
						if (Handoff.TakeUncertainNotice(nowMs))
						{
							Logger.Log(Lang.T("log.renderer.unconfirmed") + current.RendererName);
						}
					}
				}
			}
			GameDetection gameDetection4 = Handoff.Confirmed(RendererNowMs());
			if (gameDetection4 != null && VerifyRendererCandidate(gameDetection4) && (!gameDetection4.RendererForeground || RendererForegroundPid() == gameDetection4.RendererPid) && RendererEpochCurrent(epoch))
			{
				return gameDetection4;
			}
			GameDetection gameDetection5 = ((raw != null && raw.RendererCandidateSelected && !raw.RendererForeground && !raw.RequiresGpuConfirm && !raw.RendererSafetyOnly) ? raw : rendererDirectPending);
			if (gameDetection5 != null && !RendererHandoffTracker.SameIdentity(gameDetection5, gameDetection))
			{
				if (gameDetection5.Profile != null && RendererHandoffTracker.SameProfile(gameDetection5.Profile, RendererProfile(library, gameDetection5.Profile.Id)) && VerifyRendererCandidate(gameDetection5) && (!gameDetection5.RendererForeground || RendererForegroundPid() == gameDetection5.RendererPid))
				{
					if (ReleaseRendererBackground(gameDetection5) == BackgroundReleaseState.Ready && RendererEpochCurrent(epoch))
					{
						rendererDirectPending = null;
						return gameDetection5;
					}
					lock (sync)
					{
						if (!RendererEpochCurrent(epoch))
						{
							return null;
						}
						rendererDirectPending = (VerifyRendererCandidate(gameDetection5) ? RendererHandoffTracker.Copy(gameDetection5) : null);
					}
				}
				else
				{
					rendererDirectPending = null;
				}
			}
			else
			{
				rendererDirectPending = null;
			}
			if (!RendererEpochCurrent(epoch))
			{
				return null;
			}
			if (raw == null || !raw.RendererCandidateSelected || raw.RequiresGpuConfirm || raw.RendererSafetyOnly || !RendererHandoffTracker.SameIdentity(raw, gameDetection))
			{
				return null;
			}
			return raw;
		}

		private void TryStartRendererProbe(int epoch)
		{
			if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0)
			{
				return;
			}
			RendererProbeTicket ticket;
			lock (sync)
			{
				if (!RendererEpochCurrent(epoch))
				{
					Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
					return;
				}
				GameDetection current = Handoff.Current;
				if (current != null)
				{
					Handoff.Refresh(true, RendererForegroundPid() == current.RendererPid, RendererNowMs());
				}
				ticket = Handoff.BeginProbe(RendererNowMs());
			}
			if (ticket == null)
			{
				Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
				return;
			}
			if (!rendererProbeFailureBudget.Allowed(ticket.Detection.RendererPid, ticket.Detection.RendererCreation, ticket.Detection.RendererPath, "probe"))
			{
				Handoff.Complete(ticket, null, false, RendererNowMs());
				Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
				return;
			}
			try
			{
				if (!ThreadPool.QueueUserWorkItem(delegate
				{
					IDictionary<int, double> utilization = null;
					PresentActivitySample presents = null;
					bool identityValid = false;
					bool sampled = false;
					Func<bool> func = () => ticket.Canceled || !RendererEpochCurrent(epoch);
					try
					{
						if (!func() && VerifyRendererCandidate(ticket.Detection))
						{
							sampled = true;
							// Frame submissions and GPU utilization are observed over the same window.
							string presentFailure;
							PresentActivityCapture capture = TryStartRendererPresent(rendererProbeFailureBudget, ticket.Detection, out presentFailure);
							Stopwatch presentWindow = capture != null ? Stopwatch.StartNew() : null;
							try
							{
								utilization = SampleRendererGpu(rendererProbeFailureBudget, ticket.Detection, func,
									() => GpuEvidence.Sample3D(2, 700, func));
							}
							finally
							{
								if (presentWindow != null) WaitRendererPresentWindow(presentWindow, func);
								presents = FinishRendererPresent(rendererProbeFailureBudget, ticket.Detection, capture, presentFailure);
							}
							identityValid = !func() && VerifyRendererCandidate(ticket.Detection) && RendererForegroundPid() == ticket.Detection.RendererPid;
						}
					}
					catch
					{
					}
					finally
					{
						if (sampled && !func())
						{
							if (utilization == null && (presents == null || !presents.Valid)) RecordRendererProbeFailure(ticket, "capture");
							else rendererProbeFailureBudget.Succeeded(ticket.Detection.RendererPid, ticket.Detection.RendererCreation, ticket.Detection.RendererPath, "probe");
						}
						RendererProof verdict = Handoff.Complete(ticket, utilization, presents, identityValid, RendererNowMs());
						if (sampled) LogRendererProbe(ticket.Detection, verdict, utilization, presents);
						Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
						if (!stopping && enabled)
						{
							Interlocked.Exchange(ref urgentProcessScan, 1);
							Interlocked.Exchange(ref processSetDirty, 1);
							try
							{
								kick.Set();
							}
							catch
							{
							}
						}
					}
				}))
				{
					Handoff.Complete(ticket, null, false, RendererNowMs());
					RecordRendererProbeFailure(ticket, "queue");
					Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
				}
			}
			catch
			{
				Handoff.Complete(ticket, null, false, RendererNowMs());
				RecordRendererProbeFailure(ticket, "queue-exception");
				Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
			}
		}

		internal static PresentActivityCapture TryStartRendererPresent(ProcessActionFailureBudget budget, GameDetection candidate, out string failure)
		{
			if (!budget.Allowed(candidate.RendererPid, candidate.RendererCreation, candidate.RendererPath, "present"))
			{
				failure = "budget-exhausted";
				return null;
			}
			return PresentActivityCapture.TryStart(out failure);
		}

		internal static IDictionary<int, double> SampleRendererGpu(ProcessActionFailureBudget budget, GameDetection candidate,
			Func<bool> canceled, Func<IDictionary<int, double>> sample)
		{
			if (canceled() || !budget.Allowed(candidate.RendererPid, candidate.RendererCreation, candidate.RendererPath, "gpu")) return null;
			IDictionary<int, double> result = null;
			try { result = sample(); return result; }
			finally
			{
				if (!canceled()) RecordRendererCaptureResult(budget, candidate, "gpu", result != null, "capture");
			}
		}

		internal static void WaitRendererPresentWindow(Stopwatch observed, Func<bool> canceled)
		{
			const int windowMs = GpuEvidence.BurstRounds * GpuEvidence.BurstIntervalMs;
			while (!canceled())
			{
				long remaining = windowMs - observed.ElapsedMilliseconds;
				if (remaining <= 0) return;
				Thread.Sleep((int)Math.Min(50, remaining));
			}
		}

		internal static PresentActivitySample FinishRendererPresent(ProcessActionFailureBudget budget, GameDetection candidate,
			PresentActivityCapture capture, string failure)
		{
			PresentActivitySample sample = capture != null ? capture.Finish() : PresentActivitySample.Unavailable(failure);
			if (failure == "budget-exhausted") return sample;
			RecordRendererCaptureResult(budget, candidate, "present", sample.Valid, sample.Failure);
			return sample;
		}

		internal static void RecordRendererCaptureResult(ProcessActionFailureBudget budget, GameDetection candidate,
			string stage, bool succeeded, string failure)
		{
			if (!budget.Allowed(candidate.RendererPid, candidate.RendererCreation, candidate.RendererPath, stage)) return;
			if (succeeded)
				budget.Succeeded(candidate.RendererPid, candidate.RendererCreation, candidate.RendererPath, stage);
			else
			{
				int count = budget.Failed(candidate.RendererPid, candidate.RendererCreation, candidate.RendererPath, stage);
				Logger.Warn("Renderer " + stage + " probe failure=" + count + "/3 pid=" + candidate.RendererPid + " reason=" + failure
					+ (count >= 3 ? "; attempts exhausted for this channel" : ""));
			}
		}

		private static void LogRendererProbe(GameDetection candidate, RendererProof verdict, IDictionary<int, double> utilization, PresentActivitySample presents)
		{
			if (candidate == null) return;
			int bestPid = 0, bestFrames = 0;
			if (presents != null && presents.Valid)
				foreach (int pid in candidate.FamilyPids)
					if (pid != candidate.RendererPid && presents.FramesOf(pid) > bestFrames) { bestFrames = presents.FramesOf(pid); bestPid = pid; }
			double gpu;
			string gpuText = utilization != null && utilization.TryGetValue(candidate.RendererPid, out gpu) && !double.IsNaN(gpu) ? ((int)gpu).ToString() + "%" : "n/a";
			string presentText = presents == null ? "none" : presents.Valid
				? "frames=" + presents.FramesOf(candidate.RendererPid) + " fps=" + ((int)Math.Round(presents.FpsOf(candidate.RendererPid))) + " windowMs=" + presents.WindowMs
					+ " familyBest=" + (bestPid > 0 ? bestPid + ":" + bestFrames : "none") + " presenters=" + presents.ProcessCount
				: "unavailable=" + presents.Failure;
			Logger.Info("RENDERER probe " + candidate.RendererName + " pid=" + candidate.RendererPid + " verdict=" + verdict.ToString().ToLowerInvariant()
				+ " gpu3d=" + gpuText + " " + presentText);
		}

		private void RecordRendererProbeFailure(RendererProbeTicket ticket, string stage)
		{
			if (ticket.Canceled) return;
			GameDetection target = ticket.Detection;
			int count = rendererProbeFailureBudget.Failed(target.RendererPid, target.RendererCreation, target.RendererPath, "probe");
			Logger.Warn("Renderer GPU probe failure=" + count + "/3 stage=" + stage + " pid=" + target.RendererPid);
		}

		private bool FinalizeRendererSelection(GameDetection selected, int expectedEpoch)
		{
			if (selected == null || selected.Profile == null || !selected.RendererCandidateSelected || selected.RequiresGpuConfirm || selected.RendererSafetyOnly || stopping || !enabled || panicReq || ProfileStoreSaveFailed || expectedEpoch != Volatile.Read(ref rendererHandoffEpoch) || !VerifyRendererCandidate(selected) || !RendererCommitEvidenceCurrent(selected, expectedEpoch))
			{
				return false;
			}
			GameProfile gameProfile2;
			lock (sync)
			{
				GameProfile gameProfile = FindProfileLocked(selected.Profile.Id);
				gameProfile2 = ((gameProfile == null) ? null : gameProfile.Clone());
			}
			if (!RendererHandoffTracker.SameProfile(selected.Profile, gameProfile2))
			{
				return false;
			}
			GameDetection current = Handoff.Current;
			bool flag = current != null && !RendererHandoffTracker.SameIdentity(current, selected);
			bool flag2 = string.Equals(gameProfile2.LearnedExecutablePath, selected.RendererPath, StringComparison.OrdinalIgnoreCase);
			bool flag3 = string.Equals(gameProfile2.ExecutablePath, selected.RendererPath, StringComparison.OrdinalIgnoreCase);
			if (!gameProfile2.ForceTrigger && !flag && (selected.RendererLearnable || flag2 || (flag3 && !string.IsNullOrEmpty(gameProfile2.LearnedExecutablePath))))
			{
				GameDetection gameDetection = RendererHandoffTracker.Copy(selected);
				gameDetection.RendererLearnable = true;
				if (!TryLearnConfirmedRenderer(gameDetection, () => RendererCommitEvidenceCurrent(selected, expectedEpoch)))
				{
					return false;
				}
				lock (sync)
				{
					GameProfile gameProfile3 = FindProfileLocked(selected.Profile.Id);
					gameProfile2 = ((gameProfile3 == null) ? null : gameProfile3.Clone());
				}
				if (gameProfile2 == null)
				{
					return false;
				}
				selected.RendererLearnable = false;
				selected.RendererUserSelected = true;
			}
			selected.Profile = gameProfile2;
			return RendererCommitEvidenceCurrent(selected, expectedEpoch);
		}

		private bool RendererCommitEvidenceCurrent(GameDetection selected, int epoch)
		{
			if (!RendererEpochCurrent(epoch) || !VerifyRendererCandidate(selected))
			{
				return false;
			}
			lock (sync)
			{
				if (selected.Profile == null || !RendererHandoffTracker.SameProfile(selected.Profile, FindProfileLocked(selected.Profile.Id)))
				{
					return false;
				}
			}
			GameDetection gameDetection = stickyDetection;
			if (RendererHandoffTracker.SameIdentity(gameDetection, selected))
			{
				return RendererEpochCurrent(epoch);
			}
			if (selected.RendererGpuProofExpiresMs > 0)
			{
				long num = RendererNowMs();
				GameDetection a = Handoff.Confirmed(num);
				if (num > selected.RendererGpuProofExpiresMs || !RendererHandoffTracker.SameIdentity(a, selected))
				{
					return false;
				}
			}
			if (selected.RendererForeground && RendererForegroundPid() != selected.RendererPid)
			{
				return false;
			}
			if (gameDetection != null && gameDetection.Profile != null && selected.Profile != null && string.Equals(gameDetection.Profile.Id, selected.Profile.Id, StringComparison.OrdinalIgnoreCase) && VerifyRendererCandidate(gameDetection) && !FreshRendererMayReplaceSticky(selected, selected.RendererName, selected.RendererCreation, gameDetection.RendererCreation, RendererForegroundHeldMs(selected.RendererPid)))
			{
				return false;
			}
			return RendererEpochCurrent(epoch);
		}

		private void CompleteRendererSelection(GameDetection selected)
		{
			GameDetection current = Handoff.Current;
			if (RendererHandoffTracker.SameIdentity(current, selected))
			{
				if (current.RequiresGpuConfirm)
				{
					Logger.Log(Lang.T("log.renderer.confirmed") + selected.Profile.Name + " / " + selected.RendererName + " pid " + selected.RendererPid + " / " + selected.Evidence);
				}
				Handoff.Clear();
			}
			if (RendererHandoffTracker.SameIdentity(rendererDirectPending, selected))
			{
				rendererDirectPending = null;
			}
		}

		private bool IsRendererHandoffProtected(int pid, long creation, string path)
		{
			if (!enabled || stopping || panicReq || ProfileStoreSaveFailed)
			{
				return false;
			}
			RendererHandoffTracker rendererHandoffTracker = rendererHandoff;
			if (rendererHandoffTracker != null && rendererHandoffTracker.Protects(pid, creation, path, RendererNowMs()))
			{
				return true;
			}
			GameDetection gameDetection = rendererDirectPending;
			if (gameDetection != null && pid == gameDetection.RendererPid && creation > 0 && creation == gameDetection.RendererCreation)
			{
				return string.Equals(path, gameDetection.RendererPath, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private void InitializeRendererObservations()
		{
			rendererObservations = new RendererObservationStore(dataDir);
		}

		public bool HasRendererObservation(GameProfile profile)
		{
			RendererObservationStore rendererObservationStore = rendererObservations;
			if (rendererObservationStore == null || profile == null)
			{
				return false;
			}
			if (!stopping && rendererObservationStore.NeedsValidation(profile.Id, profile.ExecutablePath, DateTime.UtcNow.Ticks))
			{
				QueueRendererObservationValidation();
			}
			return rendererObservationStore.Has(profile.Id, profile.ExecutablePath);
		}

		private void QueueRendererObservationValidation()
		{
			if (stopping || Interlocked.CompareExchange(ref rendererValidationBusy, 1, 0) != 0)
			{
				return;
			}
			try
			{
				if (!ThreadPool.QueueUserWorkItem(delegate
				{
					bool flag = false;
					try
					{
						if (stopping)
						{
							return;
						}
						foreach (GameProfile profile in GetProfiles())
						{
							if (stopping)
							{
								break;
							}
							if (rendererObservations.NeedsValidation(profile.Id, profile.ExecutablePath, DateTime.UtcNow.Ticks))
							{
								flag |= rendererObservations.Validate(profile.Id, profile.ExecutablePath);
							}
						}
					}
					finally
					{
						try
						{
							if (flag && !stopping)
							{
								rendererObservations.Persist();
								RaiseLibraryChanged();
							}
						}
						finally
						{
							Interlocked.Exchange(ref rendererValidationBusy, 0);
						}
					}
				}))
				{
					Interlocked.Exchange(ref rendererValidationBusy, 0);
				}
			}
			catch
			{
				Interlocked.Exchange(ref rendererValidationBusy, 0);
			}
		}

		private void ForgetRendererObservation(string profileId)
		{
			if (stopping || rendererObservations == null)
			{
				return;
			}
			rendererObservations.Forget(profileId);
			try
			{
				ThreadPool.QueueUserWorkItem(delegate
				{
					if (!stopping)
					{
						rendererObservations.Persist();
					}
				});
			}
			catch
			{
			}
		}

		private void MaybeObserveRendererActivity(GameDetection selected)
		{
			if (rendererObservations == null || selected == null || selected.Profile == null || !selected.RendererCandidateSelected || selected.RendererSafetyOnly || selected.RequiresGpuConfirm || !enabled || stopping || panicReq || ProfileStoreSaveFailed)
			{
				return;
			}
			int num = RendererForegroundPid();
			if ((num == selected.RendererPid) ? HasRendererObservation(selected.Profile) : rendererObservations.Has(selected.Profile.Id, selected.Profile.ExecutablePath))
			{
				return;
			}
			long num2 = RendererNowMs();
			int epoch = Volatile.Read(ref rendererHandoffEpoch);
			if (!RendererHandoffTracker.SameIdentity(rendererActivityTarget, selected) || rendererActivityTarget.Profile == null || !string.Equals(rendererActivityTarget.Profile.Id, selected.Profile.Id, StringComparison.OrdinalIgnoreCase) || rendererActivityEpoch != epoch)
			{
				rendererActivityTarget = RendererHandoffTracker.Copy(selected);
				rendererActivityAttempts = 0;
				rendererActivityNextMs = 0L;
				rendererActivityEpoch = epoch;
			}
			if (rendererActivityAttempts >= 5)
			{
				return;
			}
			if (num != selected.RendererPid)
			{
				TraceRendererObservationWait(selected, num2, "ForegroundWaiting", "foreground=" + num);
			}
			else
			{
				if (num2 < rendererActivityNextMs || num2 < rendererActivityBudgetNextMs)
				{
					return;
				}
				if (Handoff.HasProbe)
				{
					TraceRendererObservationWait(selected, num2, "SamplingBusy", "owner=handoff");
				}
				else
				{
					if (Interlocked.CompareExchange(ref rendererActivityBusy, 1, 0) != 0)
					{
						return;
					}
					if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) == 0)
					{
						rendererActivityAttempts++;
						rendererActivityBudgetNextMs = num2 + 10000;
						rendererActivityNextMs = num2 + ((rendererActivityAttempts <= 2) ? 10000 : ((rendererActivityAttempts <= 3) ? 30000 : ((rendererActivityAttempts <= 4) ? 60000 : 120000)));
						GameDetection target = RendererHandoffTracker.Copy(selected);
						TraceRendererObservation(target, "Started", "attempt=" + rendererActivityAttempts + " retryMs=" + (rendererActivityNextMs - num2) + " exe=" + target.RendererPath);
						try
						{
							if (!ThreadPool.QueueUserWorkItem(delegate
							{
								string reason = "Canceled";
								string text = null;
								try
								{
									Func<bool> func = () => !RendererEpochCurrent(epoch);
									if (!func())
									{
										if (!VerifyRendererCandidate(target))
										{
											reason = "IdentityUnavailable";
											text = "phase=before";
										}
										else
										{
											RendererFileStamp rendererFileStamp = RendererFileStamp.Read(target.RendererPath);
											if (rendererFileStamp == null)
											{
												reason = "FileUnavailable";
											}
											else
											{
												GpuSampleDiagnostics diagnostics = null;
												IDictionary<int, double> dictionary = GpuEvidence.Sample3D(2, 700, func, target.RendererPid, out diagnostics);
												text = ((diagnostics == null) ? null : diagnostics.ToString());
												if (!func())
												{
													if (dictionary == null)
													{
														reason = "GpuUnavailable";
													}
													else
													{
														text = "pidCount=" + dictionary.Count + " " + text;
														double value;
														if (!dictionary.TryGetValue(target.RendererPid, out value))
														{
															reason = "PidMissing";
														}
														else
														{
															text = "gpu3d=" + value.ToString("R", CultureInfo.InvariantCulture) + "% " + text;
															int num3 = RendererForegroundPid();
															if (num3 != target.RendererPid)
															{
																reason = "ForegroundChanged";
																text = text + " foreground=" + num3;
															}
															else if (!VerifyRendererCandidate(target))
															{
																reason = "IdentityUnavailable";
																text += " phase=after";
															}
															else if (double.IsNaN(value) || double.IsInfinity(value))
															{
																reason = "InvalidEvidence";
															}
															else if (value < 10.0)
															{
																reason = "BelowThreshold";
																text = text + " minimum=" + 10.0.ToString(CultureInfo.InvariantCulture) + "%";
															}
															else
															{
																RendererObservationWriteResult rendererObservationWriteResult;
																lock (sync)
																{
																	GameProfile gameProfile = FindProfileLocked(target.Profile.Id);
																	if (!RendererEpochCurrent(epoch))
																	{
																		return;
																	}
																	if (gameProfile == null || !string.Equals(gameProfile.ExecutablePath, target.RendererPath, StringComparison.OrdinalIgnoreCase))
																	{
																		reason = "ProfileChanged";
																		return;
																	}
																	if (activeDetection == null || !RendererHandoffTracker.SameIdentity(activeDetection, target))
																	{
																		reason = "ActiveChanged";
																		return;
																	}
																	if (!VerifyRendererCandidate(target))
																	{
																		reason = "IdentityUnavailable";
																		text += " phase=commit";
																		return;
																	}
																	rendererObservationWriteResult = rendererObservations.RecordActivityWithResult(gameProfile.Id, target.RendererPath, RendererObservationEvidence.Gpu3D, value, rendererFileStamp);
																}
																reason = rendererObservationWriteResult.ToString();
																if (rendererObservationWriteResult == RendererObservationWriteResult.Recorded)
																{
																	RaiseLibraryChanged();
																}
															}
														}
													}
												}
											}
										}
									}
								}
								catch (Exception ex2)
								{
									reason = "Error";
									text = ex2.GetType().Name;
								}
								finally
								{
									TraceRendererObservation(target, reason, text);
									Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
									Interlocked.Exchange(ref rendererActivityBusy, 0);
								}
							}))
							{
								TraceRendererObservation(target, "QueueFailed", null);
								Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
								Interlocked.Exchange(ref rendererActivityBusy, 0);
							}
							return;
						}
						catch (Exception ex)
						{
							TraceRendererObservation(target, "QueueFailed", ex.GetType().Name);
							Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
							Interlocked.Exchange(ref rendererActivityBusy, 0);
							return;
						}
					}
					Interlocked.Exchange(ref rendererActivityBusy, 0);
					TraceRendererObservationWait(selected, num2, "SamplingBusy", "owner=gpu");
				}
			}
		}

		private void TraceRendererObservationWait(GameDetection target, long now, string reason, string detail)
		{
			if (now >= rendererActivityWaitLogMs)
			{
				rendererActivityWaitLogMs = now + 30000;
				TraceRendererObservation(target, reason, detail);
			}
		}

		private void TraceRendererObservation(GameDetection target, string reason, string detail)
		{
			try
			{
				string item = SeverityTagFor(reason) + Lang.F("lib.render.observation.trace", target.RendererName, target.RendererPid, Lang.T("lib.render.observation." + reason)) + " [reason=" + reason + "] profile=" + target.Profile.Id + (string.IsNullOrEmpty(detail) ? "" : (" " + detail));
				lock (rendererObservationTraceGate)
				{
					if (rendererObservationTraceLines.Count >= 8)
					{
						rendererObservationTraceLines.Dequeue();
					}
					rendererObservationTraceLines.Enqueue(item);
					if (rendererObservationTraceBusy != 0)
					{
						return;
					}
					rendererObservationTraceBusy = 1;
					try
					{
						if (ThreadPool.QueueUserWorkItem(delegate
						{
							DrainRendererObservationTrace();
						}))
						{
							return;
						}
					}
					catch
					{
					}
					rendererObservationTraceBusy = 0;
					rendererObservationTraceLines.Clear();
				}
			}
			catch
			{
			}
		}

		internal static string SeverityTagFor(string reason)
		{
			switch (reason)
			{
			case "Error":
			case "QueueFailed":
			case "SaveFailed":
				return "FAIL ";
			case "GpuUnavailable":
			case "FileUnavailable":
			case "InvalidEvidence":
			case "FileChanged":
				return "WARN ";
			default:
				return "INFO ";
			}
		}

		private void DrainRendererObservationTrace()
		{
			while (true)
			{
				string msg;
				lock (rendererObservationTraceGate)
				{
					if (rendererObservationTraceLines.Count == 0)
					{
						rendererObservationTraceBusy = 0;
						break;
					}
					msg = rendererObservationTraceLines.Dequeue();
				}
				Logger.Log(msg);
			}
		}

		private void PrepareResourcePriorities(BoostPass pass, PolicySnapshot policy)
		{
			policy = policy ?? PolicyResolver.Global();
			pass.CpuBoost = pass.MemoryBoost = pass.IoBoost = pass.GpuBoost = policy.BoostGame;
			int num = (pass.CpuBoost ? 1 : 0) | (pass.MemoryBoost ? 2 : 0) | (pass.IoBoost ? 4 : 0) | (pass.GpuBoost ? 8 : 0);
			lock (sync)
			{
				if (num != boostResourceSelection)
				{
					boostResourceSelection = num;
					gameBoostNextAudit.Clear();
					boostStateVerified.Clear();
				}
			}
		}

		private static string ResourcePriorityKey(string action)
		{
			switch (action)
			{
			case "GamePriority":
				return "GmBoostCpuV1";
			case "GameMemoryPriority":
				return "GmBoostMemoryV1";
			case "GameIoPriority":
				return "GmBoostIoV1";
			case "GameGpuPriority":
				return "GmBoostGpuV1";
			default:
				throw new ArgumentException("Unknown resource priority", "action");
			}
		}

		private bool ApplySelectedBoostAction(IntPtr handle, int pid, BoostPass pass, string action, bool enabled, Func<long, bool> desired, Func<int> write)
		{
			string key = ResourcePriorityKey(action);
			HashSet<string> value;
			if (!enabled)
			{
				bool flag;
				lock (sync)
				{
					HashSet<string> value2;
					flag = (boostResourceSelections.TryGetValue(pid, out value) && value.Contains(action)) || (boostActionRestorePending.TryGetValue(pid, out value2) && value2.Contains(action));
				}
				bool flag2 = !flag || RestoreBoostAction(handle, pid, pass.RendererCreation, action);
				if (flag2)
				{
					lock (sync)
					{
						if (boostResourceSelections.TryGetValue(pid, out value))
						{
							value.Remove(action);
						}
					}
					if (!string.IsNullOrEmpty(pass.ActionTarget))
					{
						ActionFailureStore.CancelApply(action, pass.ActionTarget);
					}
				}
				RecordAutomation(key, (!flag2) ? "restore-failed" : (flag ? "restored" : "unchanged"));
				return flag2;
			}
			bool flag3 = ApplyIsolatedBoostAction(handle, pid, pass, action, desired, write);
			if (flag3)
			{
				lock (sync)
				{
					if (!boostResourceSelections.TryGetValue(pid, out value))
					{
						value = (boostResourceSelections[pid] = new HashSet<string>(StringComparer.Ordinal));
					}
					value.Add(action);
				}
			}
			RecordAutomation(key, flag3 ? "applied" : (pass.WriteDenied ? "unsupported" : "unverified"));
			return flag3;
		}

		private void SetActiveDetectionLocked(GameDetection detection)
		{
			string previousProfileId = activeDetection == null || activeDetection.Profile == null ? null : activeDetection.Profile.Id;
			string nextProfileId = detection == null || detection.Profile == null ? null : detection.Profile.Id;
			bool samePowerProfile = string.Equals(previousProfileId, nextProfileId, StringComparison.OrdinalIgnoreCase);
			bool samePowerIdentity = object.ReferenceEquals(activeDetection, detection)
				|| (activeDetection != null && detection != null && activeDetection.RendererPid == detection.RendererPid
					&& activeDetection.RendererCreation == detection.RendererCreation
					&& string.Equals(activeDetection.RendererPath, detection.RendererPath, StringComparison.OrdinalIgnoreCase)
					&& samePowerProfile);
			if (!samePowerIdentity)
			{
				if (!samePowerProfile)
				{
					powerSessionEnding = true;
				}
				InvalidatePowerPlanSession();
			}
			if (!SameCriticalThreadDetection(activeDetection, detection))
			{
				RevokeCriticalThreadIntent("SetActiveDetectionLocked");
			}
			if (activeDetection == null)
			{
				gameSessionStarts++;
			}
			activeDetection = detection;
		}

		private string FindRunningGame(ProcessSnapshot all, out HashSet<int> gamePids)
		{
			gamePids = new HashSet<int>();
			List<GameProfile> list;
			lock (sync)
			{
				list = new List<GameProfile>();
				foreach (GameProfile profile in profiles)
				{
					list.Add(profile.Clone());
				}
				CaptureGameFamily(all, list);
			}
			ObserveRendererForegroundChange();
			string armedProfile = null;
			string armedVia = null;
			GameDetection raw = null;
			bool flag = ShouldRunFullGameDetection();
			if (flag)
			{
				raw = GameSessionDetector.Detect(all, list, selfSession, out armedProfile, out armedVia, FamilyEvidence);
			}
			int epoch;
			GameDetection gameDetection = ResolveRendererHandoff(all, list, raw, out epoch);
			if (gameDetection != null && !FinalizeRendererSelection(gameDetection, epoch))
			{
				gameDetection = null;
			}
			if (epoch != Volatile.Read(ref rendererHandoffEpoch) || stopping || !enabled || panicReq || ProfileStoreSaveFailed)
			{
				return null;
			}
			GameDetection gameDetection2 = ApplyStickiness(gameDetection);
			if (gameDetection2 != null && !stickyGraceOnly && !VerifyRendererCandidate(gameDetection2))
			{
				ClearSticky();
				RequestFullGameDetection();
				return null;
			}
			if (gameDetection != null && RendererHandoffTracker.SameIdentity(gameDetection, gameDetection2))
			{
				CompleteRendererSelection(gameDetection2);
			}
			if (flag)
			{
				armedAwaitingElection = armedProfile != null && gameDetection2 == null;
				UpdateArmedStatus((gameDetection2 == null) ? armedProfile : null, armedVia, gameDetection2 != null);
				if (gameDetection2 == null && armedProfile != null)
				{
					PreStageDriverTuning(list, armedProfile);
				}
			}
			else if (gameDetection2 != null && armedAwaitingElection)
			{
				armedAwaitingElection = false;
				UpdateArmedStatus(null, null, true);
			}
			if (gameDetection2 == null)
			{
				return null;
			}
			if (stickyGraceOnly)
			{
				return null;
			}
			foreach (int familyPid in gameDetection2.FamilyPids)
			{
				gamePids.Add(familyPid);
			}
			lock (sync)
			{
				if (!RendererEpochCurrent(epoch) || gameDetection2.Profile == null || !RendererHandoffTracker.SameProfile(gameDetection2.Profile, FindProfileLocked(gameDetection2.Profile.Id)))
				{
					return null;
				}
				if (ShouldRearmLauncherTransition(activeDetection, gameDetection2))
				{
					transitionProbeRendererPid = 0;
					transitionProbeRendererCreation = 0L;
				}
				SetActiveDetectionLocked(gameDetection2);
			}
			SyncAutomaticFullscreenOptimizations(true, -1L);
			NotifyExtensionSession(true);
			MaybeObserveRendererActivity(gameDetection2);
			return gameDetection2.Profile.Name;
		}

		public List<string> LibraryExecutablePaths()
		{
			List<string> list = new List<string>();
			lock (sync)
			{
				foreach (GameProfile profile in profiles)
				{
					string preferredExecutablePath = profile.PreferredExecutablePath;
					if (!string.IsNullOrEmpty(preferredExecutablePath))
					{
						list.Add(preferredExecutablePath);
					}
				}
				return list;
			}
		}

		internal static bool RuntimeBackgroundExempt(BackgroundReleaseState state)
		{
			if (state != 0 && state != BackgroundReleaseState.Gone)
			{
				return state == BackgroundReleaseState.OtherReasonActive;
			}
			return true;
		}

		private void RecordRuntimeProtection(ProcEntry process, BackgroundReleaseState backgroundState)
		{
			string source = "platform-runtime-" + process.Pid + "-" + process.Creation;
			if (!RuntimeBackgroundExempt(backgroundState))
			{
				string text = "pid=" + process.Pid + " born=" + process.Creation + " exe=" + Path.GetFileName(process.Path ?? "");
				string text2 = " background=" + backgroundState;
				Logger.RepeatedError(source, "AUTO [" + AutomationSession + "] [PLATFORM] restore-pending " + text + text2, -1L);
			}
			else
			{
				Logger.RepeatedRecovered(source);
			}
		}

		private void BeginSessionPolicy()
		{
			powerSessionEnding = true;
			InvalidatePowerPlanSession();
			corePlacementStopReason = null;
			corePlacementOnly = false;
			lock (sync)
			{
				RememberModeSwitchLockLocked(activeDetection);
			}
			ResetSessionForeground();
			BeginRetrySession();
			BeginLaptopPerformanceSession();
			bool isolationClean = StopCoreIsolation();
			InvalidateCacheWarm();
			Interlocked.Increment(ref optionalServiceGeneration);
			InvalidateStandbyCleanerWork();
			GameProfile gameProfile;
			lock (sync)
			{
				gameProfile = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Clone() : null);
			}
			PolicySnapshot policySnapshot;
			lock (sync)
			{
				policySnapshot = sessionPolicy = ((gameProfile != null) ? PolicyResolver.For(gameProfile) : PolicyResolver.Global());
				CaptureSessionCorePreferencesLocked(gameProfile, policySnapshot);
			}
			SyncAutomationPolicy();
			BeginAutomationEvidence();
			BeginEnglishInputSession();
			if (!policySnapshot.IsGlobal)
			{
				Logger.Log(Lang.T("log.gamemode.34") + policySnapshot.ProfileName + Lang.T("log.gamemode.35") + policySnapshot.OverrideCount + Lang.T("log.gamemode.36"));
			}
			BeginAutoCores(policySnapshot, isolationClean);
			lock (sync)
			{
				lastPowerPolicyKey = -1;
				powerSessionEnding = false;
			}
		}

		internal static bool SameReportedProfile(string sessionProfileId, string detectedProfileId)
		{
			if (!string.IsNullOrEmpty(sessionProfileId) && !string.IsNullOrEmpty(detectedProfileId))
			{
				return string.Equals(sessionProfileId, detectedProfileId, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private void ReportBegin(string game)
		{
			BeginCacheWarmSession();
			ResetBoostIdentity();
			long num = CurrentProcessCpuTicks();
			lock (sync)
			{
				repCpu.Clear();
				repCreation.Clear();
				repProc.Clear();
				repSealed.Clear();
				repGame = game;
				repStart = Stopwatch.GetTimestamp();
				repPaviseCpuStart = num;
				repProfileId = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Id : null);
			}
		}

		private void ReportUntrack(int pid)
		{
			lock (sync)
			{
				repCpu.Remove(pid);
				repCreation.Remove(pid);
				repProc.Remove(pid);
				repSealed.Remove(pid);
			}
		}

		private void ReportSeal(int pid)
		{
			long value;
			long value2;
			lock (sync)
			{
				if (!repCpu.TryGetValue(pid, out value))
				{
					return;
				}
				repCreation.TryGetValue(pid, out value2);
				repCpu.Remove(pid);
				repCreation.Remove(pid);
			}
			long num = 0L;
			long ticks;
			long creation;
			if (CpuTicks(pid, out ticks, out creation) && creation == value2 && ticks > value)
			{
				num = ticks - value;
			}
			lock (sync)
			{
				long value3;
				repSealed.TryGetValue(pid, out value3);
				repSealed[pid] = value3 + num;
			}
		}

		private void ReportTrack(int pid, string name)
		{
			lock (sync)
			{
				if (repGame == null || repCpu.ContainsKey(pid))
				{
					return;
				}
			}
			long ticks;
			long creation;
			if (!CpuTicks(pid, out ticks, out creation))
			{
				return;
			}
			lock (sync)
			{
				if (!repCpu.ContainsKey(pid))
				{
					repCpu[pid] = ticks;
					repCreation[pid] = creation;
					repProc[pid] = name;
				}
			}
		}

		private void ReportFinish()
		{
			string text;
			long num;
			Dictionary<int, long> dictionary;
			Dictionary<int, string> dictionary2;
			Dictionary<int, long> dictionary3;
			Dictionary<int, long> dictionary4;
			long num2;
			lock (sync)
			{
				text = repGame;
				num = repStart;
				dictionary = new Dictionary<int, long>(repCpu);
				dictionary2 = new Dictionary<int, string>(repProc);
				dictionary3 = new Dictionary<int, long>(repCreation);
				dictionary4 = new Dictionary<int, long>(repSealed);
				num2 = repPaviseCpuStart;
				repCpu.Clear();
				repCreation.Clear();
				repProc.Clear();
				repSealed.Clear();
				repGame = null;
				repPaviseCpuStart = 0L;
				repProfileId = null;
			}
			if (text == null)
			{
				return;
			}
			TimeSpan timeSpan = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - num) / (double)Stopwatch.Frequency);
			foreach (KeyValuePair<int, long> item in dictionary)
			{
				long value;
				if (!dictionary4.TryGetValue(item.Key, out value))
				{
					value = 0L;
					dictionary4[item.Key] = 0L;
				}
				long ticks;
				long creation;
				long value2;
				if (CpuTicks(item.Key, out ticks, out creation) && dictionary3.TryGetValue(item.Key, out value2) && creation == value2)
				{
					long num3 = ticks - item.Value;
					if (num3 >= 0)
					{
						dictionary4[item.Key] = value + num3;
					}
				}
			}
			long num4 = 0L;
			long num5 = 0L;
			string text2 = null;
			foreach (KeyValuePair<int, long> item2 in dictionary4)
			{
				num4 += item2.Value;
				if (item2.Value > num5)
				{
					num5 = item2.Value;
					string value3;
					if (dictionary2.TryGetValue(item2.Key, out value3))
					{
						text2 = value3;
					}
				}
			}
			string text3 = Lang.F("rep.done", text, FmtDur(timeSpan), dictionary4.Count, FmtCpu(num4));
			if (text2 != null && num5 >= 10000000)
			{
				text3 += Lang.F("rep.top", text2, FmtCpu(num5));
			}
			long num6 = CurrentProcessCpuTicks();
			long cpuTicks = ((num2 > 0 && num6 >= num2) ? (num6 - num2) : 0);
			double num7 = AverageCpuPercent(cpuTicks, timeSpan);
			text3 += Lang.F("rep.pavise.cpu", num7.ToString("0.00", CultureInfo.InvariantCulture));
			Logger.Log(Lang.T("log.gamemodesession.1") + text3);
			if (!(timeSpan.TotalSeconds >= 60.0))
			{
				return;
			}
			string text4 = Lang.F("rep.brief", text, FmtDur(timeSpan), dictionary4.Count);
			try
			{
				Settings.SaveStr("LastSessionBrief", text4);
			}
			catch
			{
			}
			Action<string> sessionBriefed = this.SessionBriefed;
			if (sessionBriefed != null)
			{
				try
				{
					sessionBriefed(text4);
				}
				catch
				{
				}
			}
			Action<string> sessionEnded = this.SessionEnded;
			if (sessionEnded == null)
			{
				return;
			}
			try
			{
				sessionEnded(text3);
			}
			catch
			{
			}
		}

		private static bool CpuTicks(int pid, out long ticks, out long creation)
		{
			ticks = 0L;
			creation = 0L;
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				long exit;
				long kernel;
				long user;
				if (!GetProcessTimes(intPtr, out creation, out exit, out kernel, out user))
				{
					return false;
				}
				ticks = kernel + user;
				return true;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private static long CurrentProcessCpuTicks()
		{
			try
			{
				using (Process process = Process.GetCurrentProcess())
				{
					return process.TotalProcessorTime.Ticks;
				}
			}
			catch
			{
				return 0L;
			}
		}

		internal static double AverageCpuPercent(long cpuTicks, TimeSpan duration)
		{
			if (cpuTicks <= 0 || duration.Ticks <= 0)
			{
				return 0.0;
			}
			int num = Math.Max(1, Environment.ProcessorCount);
			double val = (double)cpuTicks * 100.0 / ((double)duration.Ticks * (double)num);
			return Math.Max(0.0, Math.Min(100.0, val));
		}

		private static string FmtDur(TimeSpan t)
		{
			if (t.TotalHours >= 1.0)
			{
				return (int)t.TotalHours + "h" + t.Minutes.ToString("00") + "m";
			}
			if (t.TotalMinutes >= 1.0)
			{
				return t.Minutes + "m" + t.Seconds.ToString("00") + "s";
			}
			return t.Seconds + "s";
		}

		private static string FmtCpu(long ticks)
		{
			TimeSpan timeSpan = TimeSpan.FromTicks(ticks);
			if (timeSpan.TotalSeconds < 1.0)
			{
				return "<1s";
			}
			if (timeSpan.TotalMinutes >= 1.0)
			{
				return (int)timeSpan.TotalMinutes + "m" + timeSpan.Seconds.ToString("00") + "s";
			}
			return timeSpan.TotalSeconds.ToString("0.0") + "s";
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);

		internal static bool IsExitGraceChoice(int seconds)
		{
			if (seconds != 0 && seconds != 2 && seconds != 5 && seconds != 8 && seconds != 15)
			{
				return seconds == 30;
			}
			return true;
		}

		public bool TrySetExitGraceSeconds(int seconds)
		{
			if (!IsExitGraceChoice(seconds))
			{
				return false;
			}
			lock (sync)
			{
				if (stopping || !Settings.SaveStr("GameExitGraceSeconds", seconds.ToString(CultureInfo.InvariantCulture)))
				{
					return false;
				}
				exitGraceSeconds = seconds;
			}
			KickGameDetectionNow();
			return true;
		}

		internal static bool ExitGraceElapsed(long since, long now, int seconds)
		{
			if (since > 0 && now >= since)
			{
				return now - since >= (long)seconds * 10000000L;
			}
			return false;
		}

		private void PauseServicesPolicyChanged(bool enabledValue)
		{
			Interlocked.Increment(ref optionalServiceGeneration);
			if (!enabledValue)
			{
				lock (sync)
				{
					envNextAttempt.Remove("services");
					envFailures.Remove("services");
				}
			}
			else
			{
				ClearEnvFuse("services");
			}
			RequestPolicyApply("PauseServicesPolicyChanged");
		}

		internal bool SetGameDvrSuppression(bool value)
		{
			lock (sync)
			{
				if (stopping || !Settings.Save("GameDvrOff", value))
				{
					return false;
				}
				killGameDvr = value;
				ClearEnvFuse("gamedvr");
			}
			SyncGameDvr();
			RequestPolicyApply("SetGameDvrSuppression");
			return true;
		}

		private void InitializeStandbyCleaner()
		{
			standbyCleanerOn = Settings.Load("GmStandbyCleanerV2", false);
			string value;
			bool flag = Settings.TryLoadStr("GmStandbyCleanerOptionsV1", out value);
			StandbyCleanerOptions options;
			standbyCleanerOptionsValid = flag && (value.Length == 0 || StandbyCleanerOptions.TryParse(value, out options));
			if (flag && value.Length != 0 && StandbyCleanerOptions.TryParse(value, out options))
			{
				standbyCleanerOptions = options;
			}
			if (!standbyCleanerOptionsValid)
			{
				standbyCleanerOn = false;
				Logger.Log(Lang.T("standbycleaner.config.invalid"));
			}
			standbyCleaner = new StandbyCleanerRunner(new StandbyCleanerEngine(new StandbyMemoryControl()), OnStandbyCleanerFault);
		}

		internal bool TrySetStandbyCleanerEnabled(bool value)
		{
			bool flag;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				InvalidateCacheWarm();
				InvalidateStandbyCleanerWork();
				if (value && !standbyCleanerOptionsValid)
				{
					return false;
				}
				flag = Settings.Save("GmStandbyCleanerV2", value);
				standbyCleanerOn = value && flag;
				if (standbyCleanerOn)
				{
					ClearEnvFuse("standby");
				}
			}
			RequestPolicyApply("TrySetStandbyCleanerEnabled");
			return flag;
		}

		public bool TrySetStandbyCleanerOptions(StandbyCleanerOptions value)
		{
			if (value == null || !value.IsValid)
			{
				return false;
			}
			bool flag;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				InvalidateStandbyCleanerWork();
				if (!standbyCleanerOptionsValid)
				{
					standbyCleanerOn = false;
				}
				flag = Settings.SaveStr("GmStandbyCleanerOptionsV1", value.Serialize());
				if (flag)
				{
					standbyCleanerOptions = value;
					standbyCleanerOptionsValid = true;
				}
			}
			RequestPolicyApply("TrySetStandbyCleanerOptions");
			return flag;
		}

		private void InvalidateStandbyCleanerWork()
		{
			Interlocked.Increment(ref standbyCleanerGeneration);
			StandbyCleanerRunner standbyCleanerRunner = standbyCleaner;
			if (standbyCleanerRunner != null)
			{
				standbyCleanerRunner.Pause();
			}
		}

		private void StandbyCleanerPolicyChanged(bool enabledValue)
		{
			if (enabledValue)
			{
				ClearEnvFuse("standby");
			}
			RequestPolicyApply("StandbyCleanerPolicyChanged");
		}

		private bool LiveStandbyCleanerPreference()
		{
			if (standbyCleanerOn)
			{
				return PerformancePolicy.EnvironmentPreference("GmStandbyCleanerV2");
			}
			return false;
		}

		private Func<bool> CaptureStandbyCleanerAdmission(out StandbyCleanerOptions options, out int generation)
		{
			int capturedGeneration;
			string profileId;
			bool wanted;
			lock (sync)
			{
				generation = (capturedGeneration = Volatile.Read(ref standbyCleanerGeneration));
				options = standbyCleanerOptions;
				if (options != null && options.IsValid)
					ActionFailureStore.BeginAutomaticCycle("GmStandbyCleanerV2", "system", AutomaticPhaseKey(options.Serialize()));
				GameDetection gameDetection = activeDetection;
				profileId = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Id : null);
				PolicySnapshot policySnapshot = sessionPolicy;
				wanted = standbyCleanerOptionsValid && LiveStandbyCleanerPreference() && !ActionFailureStore.IsBlocked("GmStandbyCleanerV2", "system") && !string.IsNullOrEmpty(profileId) && (policySnapshot == null || string.IsNullOrEmpty(policySnapshot.ProfileId) || string.Equals(policySnapshot.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
			}
			return delegate
			{
				if (!wanted || capturedGeneration != Volatile.Read(ref standbyCleanerGeneration) || !Volatile.Read(ref active) || !enabled || stopping || panicReq || Volatile.Read(ref standbyCleanerRestorePending) != 0 || ProfileStoreSaveFailed || Volatile.Read(ref stickyGraceOnly) || Volatile.Read(ref gameGoneSinceTicks) != 0)
				{
					return false;
				}
				GameDetection gameDetection2 = Volatile.Read(ref activeDetection);
				return gameDetection2 != null && gameDetection2.Profile != null && string.Equals(gameDetection2.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase);
			};
		}

		private void ApplyStandbyCleanerPolicy()
		{
			StandbyCleanerRunner standbyCleanerRunner = standbyCleaner;
			if (standbyCleanerRunner != null)
			{
				StandbyCleanerOptions options;
				int generation;
				Func<bool> func = CaptureStandbyCleanerAdmission(out options, out generation);
				if (func())
				{
					standbyCleanerRunner.Update(generation, options, func);
				}
				else
				{
					standbyCleanerRunner.Pause();
				}
			}
		}

		private void OnStandbyCleanerFault(int generation, StandbyCleanerResult result, int nativeStatus)
		{
			lock (sync)
			{
				if (generation != Volatile.Read(ref standbyCleanerGeneration) || stopping || !active || !enabled || panicReq || ProfileStoreSaveFailed || !envFused.Add("standby"))
				{
					return;
				}
				ActionFailureStore.Trip("GmStandbyCleanerV2", "system", string.Concat(result, " | 0x", nativeStatus.ToString("X8", CultureInfo.InvariantCulture)));
			}
			Logger.Warn(string.Concat(Lang.T("log.gamemodeenv.9"), EnvLabel("standby"), Lang.T("log.gamemodeenv.10"), "1", Lang.T("log.gamemodeenv.11"), " [", result, ", 0x", nativeStatus.ToString("X8", CultureInfo.InvariantCulture), "]"));
			RequestPolicyApply("OnStandbyCleanerFault");
		}

		private void RememberModeSwitchLockLocked(GameDetection detection)
		{
			if (detection != null && detection.RendererCreation > 0)
			{
				string text = ((detection.Profile != null) ? detection.Profile.Id : null);
				if (string.IsNullOrEmpty(text) || FindProfileLocked(text) != null)
				{
					modeLockGamePid = detection.RendererPid;
					modeLockGameCreation = detection.RendererCreation;
					modeLockGameProfileId = text;
				}
			}
		}

		private void ClearModeSwitchLockForProfileLocked(string profileId)
		{
			if (!string.IsNullOrEmpty(profileId) && string.Equals(modeLockGameProfileId, profileId, StringComparison.OrdinalIgnoreCase))
			{
				modeLockGamePid = 0;
				modeLockGameCreation = 0L;
				modeLockGameProfileId = null;
			}
		}

		private bool ModeSwitchLockedLocked()
		{
			if (active || sessionPolicy != null || (activeDetection != null && activeDetection.RendererPid > 4))
			{
				return true;
			}
			if (modeLockGamePid <= 4 || modeLockGameCreation <= 0)
			{
				return false;
			}
			IntPtr intPtr = Native.OpenProcess(1052672, false, modeLockGamePid);
			if (intPtr == IntPtr.Zero)
			{
				return !Native.LastOpenProcessFailureWasNoSuchProcess();
			}
			try
			{
				if (Native.WaitForSingleObject(intPtr, 0u) != 258u)
				{
					return false;
				}
				long creation;
				long cpu;
				ulong io;
				return !Native.QueryProcessSample(intPtr, out creation, out cpu, out io) || creation == modeLockGameCreation;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		public bool TrySetPreset(PerformancePreset value)
		{
			if (!PresetValue.IsValid((int)value))
			{
				return false;
			}
			lock (sync)
			{
				if (stopping || ModeSwitchLockedLocked())
				{
					return false;
				}
				int num = (int)value;
				if (!Settings.SaveStr("PerformancePreset", num.ToString()))
				{
					return false;
				}
				preset = value;
			}
			RequestPolicyApply("TrySetPreset");
			return true;
		}

        internal void RestorePowerOverlay()
        {
            // Recovery only: new managed sources do not add a second implicit power policy.
            if (PowerOverlay.HasResidue) PowerOverlay.Restore();
        }

		internal static bool LaneSupported(PerformancePreset mode)
		{
			return !IsHandheld(mode);
		}

		private GameDetection ApplyStickiness(GameDetection hit)
		{
			if (hit != null)
			{
				stickyGraceOnly = false;
				stickyMiss = 0;
				if (stickyDetection != null && stickyDetection.Profile != null && hit.Profile != null && string.Equals(stickyDetection.Profile.Id, hit.Profile.Id, StringComparison.OrdinalIgnoreCase) && stickyDetection.RendererPid > 0 && AliveWithIdentity(stickyDetection.RendererPid))
				{
					bool flag = false;
					GameId value;
					string name;
					long creation;
					if (hit.RendererPid > 0 && hit.RendererPid != stickyDetection.RendererPid && stickyIds.TryGetValue(stickyDetection.RendererPid, out value) && TryStickyIdentity(hit.RendererPid, out name, out creation) && FreshRendererMayReplaceSticky(hit, name, creation, value.Creation, RendererForegroundHeldMs(hit.RendererPid)))
					{
						flag = true;
					}
					if (hit.RendererPid != stickyDetection.RendererPid && !flag)
					{
						List<int> verifiedStickyPids = LiveStickyFamily();
						if (!ReanchorToStickyInstance(hit, stickyDetection, verifiedStickyPids))
						{
							ClearSticky();
						}
					}
				}
				if (!RememberSticky(hit))
				{
					return ApplyStickiness(null);
				}
				{
					foreach (int item in LiveStickyFamily())
					{
						hit.FamilyPids.Add(item);
					}
					return hit;
				}
			}
			if (stickyDetection != null && stickyDetection.RendererPid > 0 && AliveWithIdentity(stickyDetection.RendererPid))
			{
				stickyGraceOnly = false;
				stickyMiss = 0;
				GameDetection gameDetection = new GameDetection();
				gameDetection.Profile = stickyDetection.Profile;
				gameDetection.RendererPid = stickyDetection.RendererPid;
				gameDetection.RendererCreation = stickyDetection.RendererCreation;
				gameDetection.RendererName = stickyDetection.RendererName;
				gameDetection.RendererPath = stickyDetection.RendererPath;
				gameDetection.RendererForeground = stickyDetection.RendererForeground;
				gameDetection.RendererCandidateSelected = true;
				gameDetection.RendererUserSelected = stickyDetection.RendererUserSelected;
				gameDetection.RendererLearnable = stickyDetection.RendererLearnable;
				gameDetection.Evidence = stickyDetection.Evidence;
				GameDetection gameDetection2 = gameDetection;
				gameDetection2.FamilyPids.Add(stickyDetection.RendererPid);
				foreach (int item2 in LiveStickyFamily())
				{
					gameDetection2.FamilyPids.Add(item2);
				}
				foreach (string familyName in stickyDetection.FamilyNames)
				{
					gameDetection2.FamilyNames.Add(familyName);
				}
				stickyDetection = gameDetection2;
				return gameDetection2;
			}
			if (stickyDetection != null && stickyMiss < 1 && !AnyStickyReused())
			{
				stickyMiss++;
				stickyGraceOnly = true;
				RequestFullGameDetection();
				try
				{
					kick.Set();
				}
				catch
				{
				}
				return CloneWithAnchoredFamily(stickyDetection);
			}
			ClearSticky();
			return null;
		}

		internal static bool FreshRendererMayReplaceSticky(GameDetection fresh, string verifiedName, long verifiedCreation, long stickyCreation, long foregroundHeldMs)
		{
			if (fresh == null || fresh.RendererPid <= 0 || string.IsNullOrEmpty(fresh.RendererName) || string.IsNullOrEmpty(verifiedName) || verifiedCreation <= 0 || stickyCreation <= 0 || (fresh.RendererCreation > 0 && fresh.RendererCreation != verifiedCreation) || !string.Equals(verifiedName, fresh.RendererName, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (verifiedCreation > stickyCreation)
			{
				return true;
			}
			if (fresh.RendererForeground)
			{
				if (fresh.RendererGpuProofExpiresMs <= 0)
				{
					return foregroundHeldMs >= 10000;
				}
				return true;
			}
			return false;
		}

		internal static bool ReanchorToStickyInstance(GameDetection fresh, GameDetection sticky, IList<int> verifiedStickyPids)
		{
			if (fresh == null || sticky == null || sticky.RendererPid <= 0 || verifiedStickyPids == null)
			{
				return false;
			}
			bool flag = false;
			foreach (int verifiedStickyPid in verifiedStickyPids)
			{
				if (verifiedStickyPid == sticky.RendererPid)
				{
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				return false;
			}
			fresh.RendererPid = sticky.RendererPid;
			fresh.RendererCreation = sticky.RendererCreation;
			fresh.RendererName = sticky.RendererName;
			fresh.RendererPath = sticky.RendererPath;
			fresh.RendererForeground = sticky.RendererForeground;
			fresh.RendererCandidateSelected = true;
			fresh.RendererUserSelected = sticky.RendererUserSelected;
			fresh.RendererLearnable = sticky.RendererLearnable;
			fresh.RequiresGpuConfirm = false;
			fresh.RendererSafetyOnly = false;
			fresh.RendererGpuProofExpiresMs = 0L;
			fresh.Evidence = sticky.Evidence;
			fresh.FamilyPids.Clear();
			foreach (int verifiedStickyPid2 in verifiedStickyPids)
			{
				if (verifiedStickyPid2 > 0)
				{
					fresh.FamilyPids.Add(verifiedStickyPid2);
				}
			}
			fresh.FamilyNames.Clear();
			foreach (string familyName in sticky.FamilyNames)
			{
				fresh.FamilyNames.Add(familyName);
			}
			return true;
		}

		private GameDetection CloneWithAnchoredFamily(GameDetection src)
		{
			GameDetection gameDetection = new GameDetection();
			gameDetection.Profile = src.Profile;
			gameDetection.RendererPid = src.RendererPid;
			gameDetection.RendererCreation = src.RendererCreation;
			gameDetection.RendererName = src.RendererName;
			gameDetection.RendererPath = src.RendererPath;
			gameDetection.RendererForeground = src.RendererForeground;
			gameDetection.RendererCandidateSelected = src.RendererCandidateSelected;
			gameDetection.RendererUserSelected = src.RendererUserSelected;
			gameDetection.RendererLearnable = src.RendererLearnable;
			gameDetection.Evidence = src.Evidence;
			GameDetection gameDetection2 = gameDetection;
			foreach (int familyPid in src.FamilyPids)
			{
				if (stickyIds.ContainsKey(familyPid))
				{
					gameDetection2.FamilyPids.Add(familyPid);
				}
			}
			foreach (string familyName in src.FamilyNames)
			{
				gameDetection2.FamilyNames.Add(familyName);
			}
			return gameDetection2;
		}

		private bool RememberSticky(GameDetection hit)
		{
			if (hit == null)
			{
				ClearSticky();
				return false;
			}
			Dictionary<int, GameId> dictionary = new Dictionary<int, GameId>();
			foreach (int familyPid in hit.FamilyPids)
			{
				string name;
				long creation;
				if (TryStickyIdentity(familyPid, out name, out creation) && (familyPid != hit.RendererPid || StickyIdentityMatches(hit, name, creation)))
				{
					dictionary[familyPid] = new GameId
					{
						Name = name,
						Creation = creation
					};
				}
			}
			string name2;
			long creation2;
			if (hit.RendererPid > 0 && !dictionary.ContainsKey(hit.RendererPid) && TryStickyIdentity(hit.RendererPid, out name2, out creation2) && StickyIdentityMatches(hit, name2, creation2))
			{
				dictionary[hit.RendererPid] = new GameId
				{
					Name = name2,
					Creation = creation2
				};
			}
			GameId value;
			if (hit.RendererCreation <= 0 && dictionary.TryGetValue(hit.RendererPid, out value))
			{
				hit.RendererCreation = value.Creation;
			}
			if (hit.RendererPid <= 0 || !dictionary.ContainsKey(hit.RendererPid))
			{
				return false;
			}
			stickyIds.Clear();
			foreach (KeyValuePair<int, GameId> item in dictionary)
			{
				stickyIds[item.Key] = item.Value;
			}
			stickyDetection = hit;
			return true;
		}

		private static bool StickyIdentityMatches(GameDetection hit, string name, long creation)
		{
			if (creation > 0 && (hit.RendererCreation <= 0 || hit.RendererCreation == creation))
			{
				return string.Equals(hit.RendererName, name, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private List<int> LiveStickyFamily()
		{
			List<int> list = new List<int>();
			foreach (KeyValuePair<int, GameId> stickyId in stickyIds)
			{
				if (AliveWithIdentity(stickyId.Key))
				{
					list.Add(stickyId.Key);
				}
			}
			return list;
		}

		private void ClearSticky()
		{
			stickyDetection = null;
			stickyIds.Clear();
			stickyMiss = 0;
			stickyGraceOnly = false;
		}

		private bool AliveWithIdentity(int pid)
		{
			GameId value;
			if (!stickyIds.TryGetValue(pid, out value))
			{
				return false;
			}
			string name;
			long creation;
			if (TryStickyIdentity(pid, out name, out creation) && creation == value.Creation)
			{
				return string.Equals(name, value.Name, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private bool AnyStickyReused()
		{
			foreach (KeyValuePair<int, GameId> stickyId in stickyIds)
			{
				string name;
				long creation;
				if (TryStickyIdentity(stickyId.Key, out name, out creation) && (creation != stickyId.Value.Creation || !string.Equals(name, stickyId.Value.Name, StringComparison.OrdinalIgnoreCase)))
				{
					return true;
				}
			}
			return false;
		}

		private bool TryStickyIdentity(int pid, out string name, out long creation)
		{
			return TryIdentity(pid, out name, out creation);
		}

		private static bool TryIdentity(int pid, out string name, out long creation)
		{
			name = null;
			creation = 0L;
			if (pid <= 0)
			{
				return false;
			}
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				name = Native.ImageName(intPtr);
				long cpu;
				ulong io;
				return Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && !string.IsNullOrEmpty(name);
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private bool PerformanceScopeAllows(string imagePath)
		{
			return true;
		}

		internal static bool IsHandheld(PerformancePreset mode)
		{
			return false;
		}

		internal static bool ResolvePowerPlanEnabled(PerformancePreset mode, bool manuallyEnabled)
		{
			if (!PowerPlan.NoSwitchSelected)
			{
				return manuallyEnabled;
			}
			return false;
		}

		internal static SuppressionLevel BackgroundLevel()
		{
			return SuppressionLevel.Isolated;
		}

		private string ServiceImagePath(ProcEntry p, PerformancePreset mode)
		{
			if (p.Session != 0 || !BackgroundServicePolicy.SupportedMode(mode) || !string.IsNullOrEmpty(p.Path))
			{
				return p.Path;
			}
			if (serviceImagePaths == null)
			{
				serviceImagePaths = new Dictionary<int, KeyValuePair<long, string>>();
			}
			KeyValuePair<long, string> value;
			if (serviceImagePaths.TryGetValue(p.Pid, out value) && value.Key == p.Creation)
			{
				return value.Value;
			}
			string text = null;
			IntPtr intPtr = Native.OpenProcess(4096, false, p.Pid);
			if (intPtr != IntPtr.Zero)
			{
				try
				{
					long creation;
					long cpu;
					ulong io;
					if (Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation == p.Creation && !Native.IsProtectedProcess(intPtr))
					{
						text = Native.ImagePath(intPtr);
					}
				}
				finally
				{
					Native.CloseHandle(intPtr);
				}
			}
			if (serviceImagePaths.Count > 1024)
			{
				serviceImagePaths.Clear();
			}
			serviceImagePaths[p.Pid] = new KeyValuePair<long, string>(p.Creation, text);
			return text;
		}

		private List<KeyValuePair<string, int>> BackgroundServiceHosts(ProcessSnapshot all)
		{
			if (!object.ReferenceEquals(servicePolicySnapshot, all))
			{
				servicePolicyHosts = null;
				bool flag = false;
				if (all != null)
				{
					ProcEntry[] entries = all.Entries;
					foreach (ProcEntry procEntry in entries)
					{
						if (procEntry.Pid > 4 && procEntry.Session == 0 && procEntry.Creation > 0 && BackgroundServicePolicy.CandidateName(procEntry.Name))
						{
							flag = true;
							break;
						}
					}
				}
				if (flag)
				{
					servicePolicyHosts = SvcQuery.RunningServices();
				}
				servicePolicySnapshot = all;
			}
			return servicePolicyHosts;
		}

		private void Sweep(ProcessSnapshot all, HashSet<int> gamePids)
		{
			lock (whiteEvalSync)
			{
				SweepWithStableWhitelist(all, gamePids);
			}
		}

		private void SweepWithStableWhitelist(ProcessSnapshot all, HashSet<int> gamePids)
		{
			using (SuccessLogBatch.Begin("background-sweep session=" + AutomationSession))
				SweepWithStableWhitelistCore(all, gamePids);
		}

		private void SweepWithStableWhitelistCore(ProcessSnapshot all, HashSet<int> gamePids)
		{
			PolicySnapshot policySnapshot = sessionPolicy;
			PerformancePreset performancePreset = ((policySnapshot != null) ? policySnapshot.Preset : ActivePreset);
			SuppressionCore.BackgroundBoostLimit = (policySnapshot ?? PolicyResolver.Global()).BackgroundBoostLimitRequested;
			bool preserveOnForeground = performancePreset == PerformancePreset.Competitive;
			int foregroundPid = GameSessionDetector.ForegroundPid();
			bool flag = WideBackgroundScope(policySnapshot ?? PolicyResolver.Global());
			WhitelistEvaluation whitelistEvaluation = EvaluateWhitelist(all);
			int epoch = FamilyPolicyEpoch;
			List<KeyValuePair<string, int>> services = (BackgroundServicePolicy.SupportedMode(performancePreset) ? BackgroundServiceHosts(all) : null);
			bool flag2 = true;
			int rendererPid = 0;
			string text = null;
			GameProfile profile = null;
			List<string> list = new List<string>();
			List<GameProfile> exactGameProfiles = new List<GameProfile>();
			bool lockTaken = false;
			object obj = default(object);
			List<GameProfile> list2;
			try
			{
				Monitor.Enter(obj = sync, ref lockTaken);
				if (activeDetection != null)
				{
					rendererPid = activeDetection.RendererPid;
					GameProfile gameProfile = ((activeDetection.Profile == null) ? null : FindProfileLocked(activeDetection.Profile.Id));
					profile = ((gameProfile == null) ? null : gameProfile.Clone());
					flag2 = FamilyBoundary.FamilyExemptFor(profile);
					if (flag2 && activeDetection.Profile != null && FamilyBoundary.SafeFamilyDir(activeDetection.Profile.Root))
					{
						text = activeDetection.Profile.Root;
					}
				}
				list2 = new List<GameProfile>();
				foreach (GameProfile profile2 in profiles)
				{
					GameProfile snapshotProfile = profile2.Clone();
					exactGameProfiles.Add(snapshotProfile);
					if (FamilyBoundary.FamilyExemptFor(profile2))
					{
						list2.Add(snapshotProfile);
						if (FamilyBoundary.SafeFamilyDir(profile2.Root))
						{
							list.Add(profile2.Root);
						}
					}
				}
			}
			finally
			{
				if (lockTaken)
				{
					Monitor.Exit(obj);
				}
			}
			HashSet<int> hashSet = FamilyBoundary.CollectProtectedLibraryFamily(list2, all, selfPid, selfSession, FamilyEvidence);
			// Protect registered and learned game bodies from family suppression, even without a usable library root.
			HashSet<int> exactGameProcesses = FamilyBoundary.CollectProtectedLibraryFamily(exactGameProfiles, all, selfPid, selfSession, FamilyEvidence, true, true);
			hashSet.UnionWith(exactGameProcesses);
			var gameCreationSeeds = new HashSet<int>(exactGameProcesses);
			gameCreationSeeds.UnionWith(gamePids ?? EmptyPidSet);
			if (rendererPid > 0) gameCreationSeeds.Add(rendererPid);
			HashSet<int> gameCreationHosts = FamilyBoundary.CollectGameCreationHosts(all, gameCreationSeeds, selfPid, selfSession);
			bool flag3 = rendererPid > 0;
			bool flag4 = flag2 && flag3;
			bool flag5 = flag3 && (flag2 || !flag);
			Dictionary<int, long> creations = (flag5 ? whitelistEvaluation.Creations : null);
			HashSet<int> hashSet2 = (flag5 ? FamilyBoundary.WalkAncestorChain(whitelistEvaluation.Parents, rendererPid, selfPid, 24, creations) : EmptyPidSet);
			HashSet<int> hashSet3 = null;
			if (flag5)
			{
				hashSet3 = new HashSet<int>(gamePids ?? EmptyPidSet);
				hashSet3.Add(rendererPid);
			}
			HashSet<int> gameDescendants = (flag5 ? FamilyBoundary.WalkDescendants(whitelistEvaluation.Parents, hashSet3, selfPid, 24, creations) : EmptyPidSet);
			HashSet<int> hashSet4 = (flag ? EmptyPidSet : CollectUserFacingFamily(foregroundPid, whitelistEvaluation, (!flag2) ? rendererPid : 0));
			FamilyBoundary.FilterUserFacingGameFamily(hashSet4, profile, all, rendererPid, selfPid, selfSession, gamePids, gameDescendants, hashSet2, FamilyEvidence);
			bool lockTaken2 = false;
			bool flag6;
			try
			{
				Monitor.Enter(obj = sync, ref lockTaken2);
				flag6 = firstSweep;
			}
			finally
			{
				if (lockTaken2)
				{
					Monitor.Exit(obj);
				}
			}
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			int num5 = 0;
			HashSet<int> hashSet5 = new HashSet<int>();
			List<BackgroundRequest> pending = new List<BackgroundRequest>();
			long num6 = DateTime.UtcNow.ToFileTimeUtc();
			ProcEntry[] entries = all.Entries;
			foreach (ProcEntry procEntry in entries)
			{
				try
				{
					int pid = procEntry.Pid;
					WhitelistProcessInfo value;
					whitelistEvaluation.Processes.TryGetValue(pid, out value);
					hashSet5.Add(pid);
					if (pid <= 4 || pid == selfPid)
					{
						continue;
					}
					string nm;
					long creation;
					long cpu;
					SuppressionLevel suppressionLevel;
					if (IsCriticalThreadGuardian(pid, procEntry.Creation))
					{
						ReleaseCriticalGuardianBackground();
					}
					else if (GameAffinityJobLease.IsGuardian(pid, procEntry.Creation))
					{
						ReleaseJobGuardianBackground(pid, procEntry.Creation, value != null ? value.Name : procEntry.Name);
					}
					else
					{
						if (procEntry.Creation > 0 && num6 - procEntry.Creation < 20000000)
						{
							continue;
						}
						nm = ((value != null) ? value.Name : procEntry.Name);
						if (whitelistEvaluation.RuntimeProtected.Contains(pid))
						{
							BackgroundReleaseState backgroundReleaseState = core.ReleaseBackgroundForRenderer(pid, procEntry.Creation, nm);
							if (backgroundReleaseState == BackgroundReleaseState.IdentityMismatch && core.DiscardReusedRendererTracking(pid, procEntry.Creation, nm))
							{
								backgroundReleaseState = core.ReleaseBackgroundForRenderer(pid, procEntry.Creation, nm);
							}
							if (RuntimeBackgroundExempt(backgroundReleaseState))
							{
								ReportUntrack(pid);
							}
							RecordRuntimeProtection(procEntry, backgroundReleaseState);
							continue;
						}
						if (string.Equals(nm, selfName, StringComparison.OrdinalIgnoreCase))
						{
							if (core.Release(pid, SuppressReason.Background))
							{
								ReportUntrack(pid);
							}
							continue;
						}
						bool flag7 = value != null && selfSession >= 0 && value.Session == selfSession;
						if (value == null)
						{
							flag7 = selfSession >= 0 && procEntry.Session == selfSession;
						}
						string imagePath = (flag7 ? null : ServiceImagePath(procEntry, performancePreset));
						bool flag8 = !flag7 && BackgroundServicePolicy.Eligible(performancePreset, procEntry, imagePath, services, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
						if (!flag7 && !flag8)
						{
							ReleaseBackgroundExemption(pid, nm, null);
							continue;
						}
						long num7 = ((value != null) ? value.Creation : procEntry.Creation);
						string path = ((value != null) ? value.Path : procEntry.Path);
						if (IsRendererHandoffProtected(pid, num7, path))
						{
							BackgroundReleaseState backgroundReleaseState2 = core.ReleaseBackgroundForRenderer(pid, num7, nm);
							if (backgroundReleaseState2 == BackgroundReleaseState.Ready || backgroundReleaseState2 == BackgroundReleaseState.Gone)
							{
								ReportUntrack(pid);
							}
							continue;
						}
						bool lockTaken3 = false;
						bool flag9;
						try
						{
							Monitor.Enter(obj = sync, ref lockTaken3);
							flag9 = gameBoost.ContainsKey(pid);
						}
						finally
						{
							if (lockTaken3)
							{
								Monitor.Exit(obj);
							}
						}
						if (flag9)
						{
							continue;
						}
						PlatformProcessOwner value2;
						// Protect verified live creators and required launch backends from passing throttled state into new games.
						if (exactGameProcesses.Contains(pid) || gameCreationHosts.Contains(pid) || FamilyBoundary.IsRequiredGameLaunchHost(nm, path))
						{
							ReleaseBackgroundExemption(pid, nm, null);
							continue;
						}
						bool platformRemoved = whitelistEvaluation.PlatformProcesses.TryGetValue(pid, out value2) && !PlatformRuntimePolicy.Enabled(value2.Platform);
						bool whitelisted = whitelistEvaluation.Protected.Contains(pid) || (flag8 && ServiceWhitelisted(nm, imagePath));
						if (FamilyBoundary.IsGameOrWhitelistProtected(pid, rendererPid, whitelisted, hashSet.Contains(pid), flag2, gamePids, gameDescendants, platformRemoved))
						{
							if (core.Release(pid, SuppressReason.Background))
							{
								ReportUntrack(pid);
							}
							continue;
						}
						string text2 = ((value != null) ? value.Path : null);
						creation = ((value != null) ? value.Creation : 0);
						cpu = ((value != null) ? value.Cpu : 0);
						ulong io = ((value != null) ? value.Io : 0);
						if (value == null)
						{
							IntPtr intPtr = Native.OpenProcess(4096, false, pid);
							if (intPtr != IntPtr.Zero)
							{
								try
								{
									text2 = Native.ImagePath(intPtr);
									Native.QueryProcessSample(intPtr, out creation, out cpu, out io);
								}
								finally
								{
									Native.CloseHandle(intPtr);
								}
							}
						}
						if (SelfBuildGuard.IsOwnBuild(nm, text2))
						{
							if (core.Release(pid, SuppressReason.Background))
							{
								ReportUntrack(pid);
							}
						}
						else if (!PerformanceScopeAllows(text2))
						{
							ReleaseBackgroundExemption(pid, nm, null);
						}
						else
						{
							if (TryProtectOverlayHost(pid, creation, nm, text2, flag2))
							{
								continue;
							}
							string text3 = FamilyBoundary.LibraryRootOf(text2, list);
							if (text3 == null && flag2)
							{
								text3 = text;
							}
							if (!FamilyBoundary.BasicBackgroundEligible(pid, selfPid, nm, text2, (flag7 || flag8) ? selfSession : (-1), selfSession, foregroundPid, hashSet4.Contains(pid), windowsPrefix, flag4 && hashSet2.Contains(pid), text3, flag, flag2, creation, platformRemoved, preserveOnForeground))
							{
								ReleaseBackgroundExemption(pid, nm, null);
								continue;
							}
							if (SelfProtectedRoster.Contains(nm))
							{
								ReleaseBackgroundExemption(pid, nm, null);
								num4++;
								continue;
							}
							if (flag8)
							{
								num5++;
							}
							suppressionLevel = (EffSuppress ? BackgroundLevel() : SuppressionLevel.None);
							string text4 = core.NameOf(pid);
							if (text4 == null)
							{
								goto IL_09c6;
							}
							if (!string.Equals(text4, nm, StringComparison.OrdinalIgnoreCase))
							{
								ReportUntrack(pid);
								goto IL_09c6;
							}
							if (suppressionLevel == SuppressionLevel.None || !core.HasReason(pid, SuppressReason.Background) || core.LevelOf(pid, SuppressReason.Background) != suppressionLevel)
							{
								goto IL_09c6;
							}
							bool reconciled = false;
							if (!RunBackgroundPolicy(epoch, delegate
							{
								reconciled = core.Reconcile(pid, nm, SuppressReason.Background);
							}))
							{
								return;
							}
							if (!reconciled)
							{
								goto IL_09c6;
							}
						}
						continue;
					}
					goto end_IL_0358;
					IL_09c6:
					if (suppressionLevel == SuppressionLevel.None)
					{
						if (core.Release(pid, SuppressReason.Background))
						{
							ReportUntrack(pid);
						}
					}
					else if (EffSuppress)
					{
						pending.Add(new BackgroundRequest
						{
							Pid = pid,
							Name = nm,
							Creation = creation,
							Cpu = cpu,
							Desired = suppressionLevel,
							Previous = core.LevelOf(pid, SuppressReason.Background),
							HadBackgroundReason = core.HasReason(pid, SuppressReason.Background)
						});
					}
					end_IL_0358:;
				}
				catch
				{
				}
			}
			SuppressionCore.BatchResult batchResult = null;
			if (!RunBackgroundPolicy(epoch, delegate
			{
				core.BeginBatch();
				try
				{
					foreach (BackgroundRequest item in pending)
					{
						try
						{
							item.Result = core.Acquire(item.Pid, item.Name, SuppressReason.Background, null, item.Desired, item.Creation);
						}
						catch
						{
							item.Result = AcquireResult.AlreadyProtected;
						}
					}
				}
				finally
				{
					batchResult = core.EndBatch();
				}
			}))
			{
				return;
			}
			foreach (BackgroundRequest item2 in pending)
			{
				if ((item2.Result == AcquireResult.NewlyThrottled || item2.Result == AcquireResult.AlreadyThrottled) && (batchResult == null || !batchResult.WasApplied(item2.Pid)))
				{
					string text5 = ((batchResult != null) ? batchResult.FailureOf(item2.Pid) : "batch-missing");
					if (text5 == "self-protected")
					{
						item2.Result = AcquireResult.NewlyProtected;
						continue;
					}
					item2.Result = AcquireResult.ApplyFailed;
					item2.FailureDetail = text5;
				}
			}
			foreach (BackgroundRequest item3 in pending)
			{
				if (item3.Result == AcquireResult.NewlyThrottled)
				{
					num++;
					ReportTrack(item3.Pid, item3.Name);
					SuccessLogBatch.Record("background-applied", item3.Name, item3.Pid, item3.Creation,
						"level=" + item3.Desired);
				}
				else if (item3.Result == AcquireResult.AlreadyThrottled)
				{
					if (!item3.HadBackgroundReason)
					{
						ReportTrack(item3.Pid, item3.Name);
						SuccessLogBatch.Record("background-applied", item3.Name, item3.Pid, item3.Creation,
							"level=" + item3.Desired);
					}
					else if (item3.Previous != item3.Desired)
					{
						SuccessLogBatch.Record("background-level-changed", item3.Name, item3.Pid, item3.Creation,
							"from=" + item3.Previous + " to=" + item3.Desired);
					}
				}
				else if (item3.Result == AcquireResult.NewlyProtected)
				{
					num2++;
				}
				else if (item3.Result == AcquireResult.ApplyFailed)
				{
					num3++;
					SuccessLogBatch.FlushPending();
					Logger.Log(Lang.T("log.gamemodesweep.1") + item3.Name + "(pid " + item3.Pid + ") " + ApplyFailureText.Of(item3.FailureDetail) + Lang.T("log.gamemodeboost.24"));
				}
			}
			HashSet<int> coreProtected = new HashSet<int>(hashSet4);
			coreProtected.UnionWith(whitelistEvaluation.Protected);
			foreach (KeyValuePair<int, PlatformProcessOwner> platformProcess in whitelistEvaluation.PlatformProcesses)
			{
				if (!PlatformRuntimePolicy.Enabled(platformProcess.Value.Platform) && !whitelistEvaluation.Protected.Contains(platformProcess.Key))
				{
					coreProtected.Remove(platformProcess.Key);
				}
			}
			if (!RunBackgroundPolicy(epoch, delegate
			{
				SyncCoreBackground(coreProtected, foregroundPid);
			}))
			{
				return;
			}
			FamilyBoundary.PruneCatalogVerdicts(hashSet5);
			foreach (int item4 in core.PidsWith(SuppressReason.Background))
			{
				if (!hashSet5.Contains(item4) && core.Release(item4, SuppressReason.Background))
				{
					ReportUntrack(item4);
				}
			}
			if (!flag6)
			{
				return;
			}
			if (EffSuppress)
			{
				string text6 = ((performancePreset == PerformancePreset.Extreme) ? Lang.T("preset.extreme") : (PresetValue.IsCompetitive(performancePreset) ? Lang.T("preset.competitive") : ((performancePreset == PerformancePreset.Handheld) ? Lang.T("preset.handheld") : Lang.T("preset.standard"))));
				bool flag10 = PresetValue.IsCompetitive(performancePreset) || performancePreset == PerformancePreset.Handheld;
				string text7 = text6 + (flag10 ? Lang.T("t.gamemodesweep.2") : Lang.T("t.gamemodesweep.3")) + (flag ? Lang.T("t.gamemodesweep.5") : "");
				Logger.Log(Lang.F("background.scope", num5, SuppressionCore.DisableDynamicBoost(BackgroundLevel(), false)));
				Logger.Log(Lang.T("log.gamemodesweep.1") + text7 + (SuppressionCore.GpuDemoteEnabled ? Lang.T("log.gamemodesweep.6") : "") + Lang.T("log.gamemodesweep.7") + num + Lang.T("log.gamemodesweep.8") + ((num3 > 0) ? (" " + num3 + Lang.T("log.gamemodesweep.9")) : "") + ((num2 > 0) ? (" " + num2 + Lang.T("log.gamemodesweep.10")) : "") + ((num4 > 0) ? (" " + num4 + Lang.T("t.gamemodesweep.11")) : ""));
			}
			bool lockTaken4 = false;
			try
			{
				Monitor.Enter(obj = sync, ref lockTaken4);
				firstSweep = false;
			}
			finally
			{
				if (lockTaken4)
				{
					Monitor.Exit(obj);
				}
			}
		}

		private HashSet<int> CollectUserFacingFamily(int foregroundPid, WhitelistEvaluation whitelist, int excludeRootPid)
		{
			return CollectUserFacingFamily(foregroundPid, GameSessionDetector.VisibleWindowPids(true), whitelist, excludeRootPid);
		}

		private HashSet<int> CollectUserFacingFamily(int foregroundPid, HashSet<int> visible, WhitelistEvaluation whitelist, int excludeRootPid)
		{
			HashSet<int> hashSet = new HashSet<int>();
			foreach (KeyValuePair<int, WhitelistProcessInfo> process in whitelist.Processes)
			{
				try
				{
					int key = process.Key;
					if (excludeRootPid <= 0 || key != excludeRootPid)
					{
						WhitelistProcessInfo value = process.Value;
						if (selfSession >= 0 && value.Session == selfSession && (key == foregroundPid || visible.Contains(key)))
						{
							hashSet.Add(key);
						}
					}
				}
				catch
				{
				}
			}
			return FamilyBoundary.ExpandUserFacingFamily(whitelist.Parents, whitelist.Names, hashSet);
		}

		private bool TryProtectOverlayHost(int pid, long creation, string name, string imagePath, bool familyExempt)
		{
			if (pid <= 4 || !OverlayHostCatalog.ShouldProtectProcess(name, imagePath, familyExempt))
			{
				return false;
			}
			if (creation > 0 && core.ReleaseIfCreation(pid, SuppressReason.Background, creation))
			{
				ReportUntrack(pid);
			}
			return true;
		}

		private void ReleaseBackgroundExemption(int pid, string name, string reason)
		{
			if (core.Release(pid, SuppressReason.Background))
			{
				ReportUntrack(pid);
				if (!string.IsNullOrEmpty(reason))
				{
					Logger.Log(reason + Lang.T("log.gamemodesweep.12") + name + " pid " + pid);
				}
			}
		}

		public bool NeedsWhitelistParentIdentity(int session)
		{
			if (session == selfSession)
			{
				return Interlocked.CompareExchange(ref whiteHasFamilyRules, 0, 0) != 0;
			}
			return false;
		}

		private bool AddWhiteRuleNoSave(WhitelistRule rule)
		{
			if (rule == null || !whiteRuleKeys.Add(rule.Key))
			{
				return false;
			}
			whiteRules.Add(rule);
			whiteRevision++;
			if (RuleProtectsFamily(rule))
			{
				Interlocked.Exchange(ref whiteHasFamilyRules, 1);
			}
			return true;
		}

		private void RefreshWhitelistFamilyFlagLocked()
		{
			int value = 0;
			foreach (WhitelistRule whiteRule in whiteRules)
			{
				if (RuleProtectsFamily(whiteRule))
				{
					value = 1;
					break;
				}
			}
			Interlocked.Exchange(ref whiteHasFamilyRules, value);
		}

		private static bool RuleProtectsFamily(WhitelistRule rule)
		{
			if (rule != null)
			{
				if (rule.Kind == WhitelistRuleKind.LegacyName)
				{
					return !IsPresetWhitelistName(rule.Value);
				}
				return true;
			}
			return false;
		}

		private WhitelistEvaluation EvaluateWhitelist(ProcessSnapshot all)
		{
			lock (whiteEvalSync)
			{
				if (all != null && object.ReferenceEquals(all, whiteEvalSnapshot) && whiteEvalCached != null)
				{
					bool flag;
					lock (sync)
					{
						flag = whiteEvalRevision == whiteRevision && whiteEvalMembersVersion == whiteFamilyMembersVersion && whiteEvalPlatformMask == PlatformRuntimePolicy.Mask && whiteEvalPlatformRenderer == ((activeDetection != null) ? activeDetection.RendererPid : 0) && whiteEvalPlatformRendererCreation == ((activeDetection == null) ? 0 : activeDetection.RendererCreation);
					}
					if (flag)
					{
						return whiteEvalCached;
					}
				}
				WhitelistEvaluation result = EvaluateWhitelistLocked(all);
				whiteEvalSnapshot = all;
				whiteEvalCached = result;
				return result;
			}
		}

		private sealed class WhitelistMatchIndex
		{
			private readonly Dictionary<string, List<WhitelistProcessInfo>> names = new Dictionary<string, List<WhitelistProcessInfo>>(StringComparer.OrdinalIgnoreCase);
			private readonly Dictionary<string, List<WhitelistProcessInfo>> paths = new Dictionary<string, List<WhitelistProcessInfo>>(StringComparer.OrdinalIgnoreCase);
			private static readonly List<WhitelistProcessInfo> empty = new List<WhitelistProcessInfo>();

			internal WhitelistMatchIndex(Dictionary<int, WhitelistProcessInfo> processes, IList<WhitelistRule> rules)
			{
				foreach (WhitelistRule rule in rules)
				{
					if (rule == null || rule.Value == null || (rule.Kind != 0 && rule.Value.Length == 0)) continue;
					(rule.Kind == 0 ? names : paths)[rule.Value] = null;
				}
				foreach (WhitelistProcessInfo process in processes.Values)
				{
					if (process.Name != null) Add(names, process.Name, process);
					if (!string.IsNullOrEmpty(process.Path)) Add(paths, process.Path, process);
				}
			}

			private static void Add(Dictionary<string, List<WhitelistProcessInfo>> index, string key, WhitelistProcessInfo process)
			{
				List<WhitelistProcessInfo> matches;
				if (!index.TryGetValue(key, out matches)) return;
				if (matches == null) index[key] = matches = new List<WhitelistProcessInfo>();
				matches.Add(process);
			}

			internal List<WhitelistProcessInfo> Candidates(WhitelistRule rule)
			{
				List<WhitelistProcessInfo> matches;
				return (rule.Kind == 0 ? names : paths).TryGetValue(rule.Value, out matches) && matches != null ? matches : empty;
			}
		}

		private WhitelistEvaluation EvaluateWhitelistLocked(ProcessSnapshot all)
		{
			WhitelistEvaluation whitelistEvaluation = BuildWhitelistProcessSnapshot(all);
			int mask = PlatformRuntimePolicy.Mask;
			int num;
			long num2;
			lock (sync)
			{
				num = ((activeDetection != null) ? activeDetection.RendererPid : 0);
				num2 = ((activeDetection == null) ? 0 : activeDetection.RendererCreation);
			}
			whitelistEvaluation.PlatformProcesses = CollectPlatformMembers(all);
			Dictionary<int, long> dictionary = new Dictionary<int, long>(runtimeFamilyMembers);
			foreach (KeyValuePair<int, PlatformProcessOwner> platformProcess in whitelistEvaluation.PlatformProcesses)
			{
				if (PlatformRuleEnabled(platformProcess.Value))
				{
					whitelistEvaluation.RuntimeProtected.Add(platformProcess.Key);
				}
				else
				{
					dictionary.Remove(platformProcess.Key);
				}
			}
			whitelistEvaluation.RuntimeProtected.UnionWith(WeGameRuntimeProtection.Collect(all, selfPid, selfSession));
			whitelistEvaluation.RuntimeProtected.UnionWith(ExpandApplicationFamily(whitelistEvaluation.Parents, whitelistEvaluation.RuntimeProtected, dictionary, whitelistEvaluation.Creations));
			runtimeFamilyMembers = new Dictionary<int, long>();
			foreach (int item in whitelistEvaluation.RuntimeProtected)
			{
				long value;
				if (whitelistEvaluation.Creations.TryGetValue(item, out value) && value > 0)
				{
					runtimeFamilyMembers[item] = value;
				}
			}
			whitelistEvaluation.Protected.UnionWith(whitelistEvaluation.RuntimeProtected);
			Dictionary<string, Dictionary<int, long>> dictionary2 = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
			int num3;
			int num4;
			List<WhitelistRule> list;
			lock (sync)
			{
				num3 = whiteRevision;
				num4 = whiteFamilyMembersVersion;
				list = new List<WhitelistRule>(whiteRules);
				foreach (KeyValuePair<string, Dictionary<int, long>> whiteFamilyMember in whiteFamilyMembers)
				{
					dictionary2[whiteFamilyMember.Key] = new Dictionary<int, long>(whiteFamilyMember.Value);
				}
			}
			Dictionary<int, long> creations = whitelistEvaluation.Creations;
			foreach (KeyValuePair<int, WhitelistProcessInfo> process in whitelistEvaluation.Processes)
			{
				if (process.Value.Creation > 0)
				{
					creations[process.Key] = process.Value.Creation;
				}
			}
			Dictionary<string, Dictionary<int, long>> dictionary3 = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
			WhitelistMatchIndex matchIndex = (list.Count >= 32 && whitelistEvaluation.Processes.Count >= 32)
				|| (list.Count >= 16 && whitelistEvaluation.Processes.Count >= 256)
				? new WhitelistMatchIndex(whitelistEvaluation.Processes, list) : null;
			foreach (WhitelistRule item2 in list)
			{
				HashSet<int> hashSet = new HashSet<int>();
				if (matchIndex == null || item2 == null || item2.Value == null)
				{
					foreach (KeyValuePair<int, WhitelistProcessInfo> process2 in whitelistEvaluation.Processes)
					{
						WhitelistProcessInfo value2 = process2.Value;
						if ((value2.Session == selfSession || (value2.Session == 0 && BackgroundServicePolicy.CandidateName(value2.Name))) && item2.MatchesNormalized(value2.Name, value2.Path) && (item2.Kind != 0 || !IsPresetWhitelistName(item2.Value) || IsTrustedPresetProcessPath(item2.Value, value2.Path)))
						{
							hashSet.Add(value2.Pid);
						}
					}
				}
				else
				{
					foreach (WhitelistProcessInfo value2 in matchIndex.Candidates(item2))
					{
						if ((value2.Session == selfSession || (value2.Session == 0 && BackgroundServicePolicy.CandidateName(value2.Name))) && item2.MatchesNormalized(value2.Name, value2.Path) && (item2.Kind != 0 || !IsPresetWhitelistName(item2.Value) || IsTrustedPresetProcessPath(item2.Value, value2.Path)))
						{
							hashSet.Add(value2.Pid);
						}
					}
				}
				HashSet<int> hashSet2 = hashSet;
				if (RuleProtectsFamily(item2))
				{
					HashSet<int> hashSet3 = new HashSet<int>();
					foreach (int item3 in hashSet)
					{
						WhitelistProcessInfo whitelistProcessInfo = whitelistEvaluation.Processes[item3];
						if (!string.IsNullOrEmpty(whitelistProcessInfo.Path) && !WhitelistRule.IsUnsafeFamilyAnchor(whitelistProcessInfo.Path))
						{
							hashSet3.Add(item3);
						}
					}
					Dictionary<int, long> value3;
					if (!dictionary2.TryGetValue(item2.Key, out value3))
					{
						value3 = new Dictionary<int, long>();
					}
					hashSet2 = ExpandApplicationFamily(whitelistEvaluation.Parents, hashSet3, value3, creations);
					hashSet2.UnionWith(hashSet);
					Dictionary<int, long> dictionary4 = new Dictionary<int, long>();
					foreach (int item4 in hashSet2)
					{
						long value4;
						if (creations.TryGetValue(item4, out value4) && value4 > 0)
						{
							dictionary4[item4] = value4;
						}
					}
					dictionary3[item2.Key] = dictionary4;
				}
				whitelistEvaluation.ByRule[item2.Key] = hashSet2;
				whitelistEvaluation.Protected.UnionWith(hashSet2);
			}
			lock (sync)
			{
				bool flag = num3 == whiteRevision && num4 == whiteFamilyMembersVersion;
				if (flag)
				{
					whiteFamilyMembers.Clear();
					foreach (KeyValuePair<string, Dictionary<int, long>> item5 in dictionary3)
					{
						whiteFamilyMembers[item5.Key] = item5.Value;
					}
					whiteFamilyMembersVersion++;
				}
				whiteEvalRevision = (flag ? num3 : (whiteRevision - 1));
				whiteEvalMembersVersion = whiteFamilyMembersVersion;
				whiteEvalPlatformMask = mask;
				whiteEvalPlatformRenderer = num;
				whiteEvalPlatformRendererCreation = num2;
				return whitelistEvaluation;
			}
		}

		private WhitelistEvaluation BuildWhitelistProcessSnapshot(ProcessSnapshot all)
		{
			WhitelistEvaluation whitelistEvaluation = new WhitelistEvaluation();
			if (all == null)
			{
				return whitelistEvaluation;
			}
			ProcEntry[] entries = all.Entries;
			foreach (ProcEntry procEntry in entries)
			{
				try
				{
					int pid = procEntry.Pid;
					WhitelistProcessInfo whitelistProcessInfo = new WhitelistProcessInfo();
					whitelistProcessInfo.Pid = pid;
					WhitelistProcessInfo whitelistProcessInfo2 = whitelistProcessInfo;
					whitelistProcessInfo2.Name = WhitelistRule.NormalizeName(procEntry.Name);
					whitelistProcessInfo2.Session = procEntry.Session;
					whitelistEvaluation.Processes[pid] = whitelistProcessInfo2;
					whitelistEvaluation.Names[pid] = whitelistProcessInfo2.Name;
					if (pid > 4 && selfSession >= 0 && (whitelistProcessInfo2.Session == selfSession || (whitelistProcessInfo2.Session == 0 && BackgroundServicePolicy.CandidateName(whitelistProcessInfo2.Name))) && procEntry.Path != null)
					{
						whitelistProcessInfo2.Path = WhitelistRule.NormalizeImagePath(procEntry.Path);
						whitelistProcessInfo2.Parent = procEntry.ParentPid;
						whitelistProcessInfo2.Creation = procEntry.Creation;
						whitelistEvaluation.Creations[pid] = whitelistProcessInfo2.Creation;
						whitelistProcessInfo2.Cpu = procEntry.Cpu;
						whitelistProcessInfo2.Io = procEntry.Io;
						if (whitelistProcessInfo2.Session == selfSession)
						{
							whitelistEvaluation.Parents[pid] = whitelistProcessInfo2.Parent;
						}
					}
				}
				catch
				{
				}
			}
			return whitelistEvaluation;
		}

		internal static HashSet<int> ExpandApplicationFamily(Dictionary<int, int> parents, HashSet<int> anchors, Dictionary<int, long> retained, Dictionary<int, long> currentCreation)
		{
			HashSet<int> hashSet = ((anchors == null) ? new HashSet<int>() : new HashSet<int>(anchors));
			if (retained != null && currentCreation != null)
			{
				foreach (KeyValuePair<int, long> item in retained)
				{
					long value;
					if (item.Value > 0 && currentCreation.TryGetValue(item.Key, out value) && value == item.Value)
					{
						hashSet.Add(item.Key);
					}
				}
			}
			bool flag;
			do
			{
				flag = false;
				if (parents == null)
				{
					break;
				}
				foreach (KeyValuePair<int, int> parent in parents)
				{
					long value2;
					long value3;
					if (!hashSet.Contains(parent.Key) && hashSet.Contains(parent.Value) && currentCreation != null && currentCreation.TryGetValue(parent.Key, out value2) && currentCreation.TryGetValue(parent.Value, out value3) && value2 > 0 && value3 > 0 && value2 > value3)
					{
						hashSet.Add(parent.Key);
						flag = true;
					}
				}
			}
			while (flag);
			return hashSet;
		}

		private bool ServiceWhitelisted(string name, string imagePath)
		{
			List<WhitelistRule> rules;
			lock (sync)
			{
				rules = new List<WhitelistRule>(whiteRules);
			}
			return ServiceRuleMatches(rules, name, imagePath);
		}

		internal static bool ServiceRuleMatches(IList<WhitelistRule> rules, string name, string imagePath)
		{
			if (rules == null)
			{
				return false;
			}
			string normalizedName = WhitelistRule.NormalizeName(name);
			string normalizedImagePath = WhitelistRule.NormalizeImagePath(imagePath);
			foreach (WhitelistRule rule in rules)
			{
				if (rule.MatchesNormalized(normalizedName, normalizedImagePath) && (rule.Kind != 0 || !IsPresetWhitelistName(rule.Value) || IsTrustedPresetProcessPath(rule.Value, imagePath)))
				{
					return true;
				}
			}
			return false;
		}

		private static bool IsPresetWhitelistName(string name)
		{
			string[] presetWhitelist = SystemProcessCatalog.PresetWhitelist;
			foreach (string a in presetWhitelist)
			{
				if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		internal static bool IsTrustedPresetProcessPath(string name, string imagePath)
		{
			string text = WhitelistRule.NormalizeImagePath(imagePath);
			if (text.Length == 0)
			{
				return false;
			}
			string text2 = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			if (string.IsNullOrEmpty(text2))
			{
				text2 = "C:\\Windows";
			}
			text2 = text2.TrimEnd('\\');
			if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase) && WhitelistRule.PathEquals(text, Path.Combine(text2, "explorer.exe")))
			{
				return true;
			}
			if (FamilyBoundary.UnderRoot(text, Path.Combine(text2, "System32")) || FamilyBoundary.UnderRoot(text, Path.Combine(text2, "SysWOW64")) || FamilyBoundary.UnderRoot(text, Path.Combine(text2, "SystemApps")))
			{
				return true;
			}
			if (string.Equals(name, "wslservice", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "vmmem", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "vmmemwsl", StringComparison.OrdinalIgnoreCase))
			{
				string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
				if (string.IsNullOrEmpty(folderPath))
				{
					return false;
				}
				if (FamilyBoundary.UnderRoot(text, Path.Combine(folderPath, "WindowsApps")))
				{
					return true;
				}
				if (string.Equals(name, "wslservice", StringComparison.OrdinalIgnoreCase))
				{
					return WhitelistRule.PathEquals(text, Path.Combine(folderPath, "WSL", "wslservice.exe"));
				}
				return false;
			}
			return false;
		}

		private List<WhitelistRuleView> SnapshotWhitelistRuleViews()
		{
			ProcessSnapshot all = ProcessSnapshotSource.Capture(selfSession);
			WhitelistEvaluation whitelistEvaluation = EvaluateWhitelist(all);
			List<WhitelistRuleView> list = new List<WhitelistRuleView>();
			AppendPlatformRuleViews(list, whitelistEvaluation.PlatformProcesses);
			lock (sync)
			{
				foreach (WhitelistRule whiteRule in whiteRules)
				{
					HashSet<int> value;
					int currentMatches = (whitelistEvaluation.ByRule.TryGetValue(whiteRule.Key, out value) ? value.Count : 0);
					list.Add(new WhitelistRuleView(whiteRule, currentMatches, whiteRule.Kind == WhitelistRuleKind.LegacyName && IsPresetWhitelistName(whiteRule.Value)));
				}
				return list;
			}
		}

		private int ReleaseCurrentWhitelistMatches(out int matched)
		{
			matched = 0;
			lock (whiteEvalSync)
			{
				ProcessSnapshot all = ProcessSnapshotSource.Capture(selfSession);
				WhitelistEvaluation whitelistEvaluation = EvaluateWhitelist(all);
				matched = whitelistEvaluation.Protected.Count;
				int num = 0;
				foreach (int item in whitelistEvaluation.Protected)
				{
					WhitelistProcessInfo value;
					if (whitelistEvaluation.Processes.TryGetValue(item, out value) && value.Creation > 0)
					{
						if (core.ReleaseIfCreation(item, SuppressReason.Background, value.Creation))
						{
							ReportUntrack(item);
							num++;
						}
					}
				}
				return num;
			}
		}

		private void ObserveWhitelistProcessChanges(ProcessChangeBatch batch)
		{
			if (batch == null || batch.Changes.Length == 0)
			{
				return;
			}
			lock (whiteEvalSync)
			{
				List<WhitelistRule> list = new List<WhitelistRule>();
				Dictionary<int, long> dictionary = new Dictionary<int, long>();
				HashSet<int> hashSet = new HashSet<int>();
				int num;
				lock (sync)
				{
					num = whiteRevision;
					foreach (WhitelistRule whiteRule in whiteRules)
					{
						if (RuleProtectsFamily(whiteRule))
						{
							list.Add(whiteRule);
						}
					}
					ProcessChange[] changes = batch.Changes;
					foreach (ProcessChange processChange in changes)
					{
						if (processChange == null || processChange.Kind != ProcessChangeKind.Stopped)
						{
							continue;
						}
						foreach (WhitelistRule item in list)
						{
							Dictionary<int, long> value;
							if (whiteFamilyMembers.TryGetValue(item.Key, out value) && value.ContainsKey(processChange.Pid))
							{
								hashSet.Add(processChange.Pid);
								break;
							}
						}
					}
				}
				if (list.Count == 0)
				{
					return;
				}
				Dictionary<int, long> stopIdentity = CaptureStoppedProcessIdentity(hashSet);
				lock (sync)
				{
					if (num != whiteRevision)
					{
						return;
					}
					foreach (WhitelistRule item2 in list)
					{
						Dictionary<int, long> value2;
						if (!whiteFamilyMembers.TryGetValue(item2.Key, out value2))
						{
							value2 = new Dictionary<int, long>();
							whiteFamilyMembers[item2.Key] = value2;
						}
						foreach (KeyValuePair<int, long> item3 in ApplyApplicationFamilyEvents(item2, value2, batch.Changes, selfSession, stopIdentity))
						{
							dictionary[item3.Key] = item3.Value;
						}
					}
					whiteFamilyMembersVersion++;
				}
				foreach (KeyValuePair<int, long> item4 in dictionary)
				{
					if (core.ReleaseIfCreation(item4.Key, SuppressReason.Background, item4.Value))
					{
						ReportUntrack(item4.Key);
					}
				}
			}
		}

		internal static Dictionary<int, long> ApplyApplicationFamilyEvents(WhitelistRule rule, Dictionary<int, long> members, IList<ProcessChange> changes, int ownerSession)
		{
			return ApplyApplicationFamilyEvents(rule, members, changes, ownerSession, null);
		}

		internal static Dictionary<int, long> ApplyApplicationFamilyEvents(WhitelistRule rule, Dictionary<int, long> members, IList<ProcessChange> changes, int ownerSession, IDictionary<int, long> stopIdentity)
		{
			Dictionary<int, long> dictionary = new Dictionary<int, long>();
			if (rule == null || members == null || changes == null)
			{
				return dictionary;
			}
			List<ProcessChange> list = new List<ProcessChange>();
			foreach (ProcessChange change in changes)
			{
				if (change != null && change.Pid > 0)
				{
					list.Add(change);
				}
			}
			list.Sort((ProcessChange left, ProcessChange right) => left.Sequence.CompareTo(right.Sequence));
			Dictionary<int, ProcessChange> dictionary2 = new Dictionary<int, ProcessChange>();
			foreach (ProcessChange item in list)
			{
				if (item.Kind == ProcessChangeKind.Stopped)
				{
					long value;
					long value2;
					if (stopIdentity != null && stopIdentity.TryGetValue(item.Pid, out value) && members.TryGetValue(item.Pid, out value2) && (value <= 0 || value != value2))
					{
						members.Remove(item.Pid);
						dictionary.Remove(item.Pid);
						dictionary2.Remove(item.Pid);
					}
				}
				else
				{
					if (item.Creation <= 0 || item.Session != ownerSession)
					{
						continue;
					}
					long value3;
					if (members.TryGetValue(item.Pid, out value3) && value3 != item.Creation)
					{
						members.Remove(item.Pid);
						dictionary.Remove(item.Pid);
					}
					dictionary2[item.Pid] = item;
					string normalizedName = WhitelistRule.NormalizeName(item.Name);
					string text = WhitelistRule.NormalizeImagePath(item.Path);
					if ((rule.MatchesNormalized(normalizedName, text) && !WhitelistRule.IsUnsafeFamilyAnchor(text)) || HasVerifiedFamilyParent(item, members, dictionary2))
					{
						bool flag = !members.TryGetValue(item.Pid, out value3) || value3 != item.Creation;
						members[item.Pid] = item.Creation;
						if (flag)
						{
							dictionary[item.Pid] = item.Creation;
						}
					}
				}
			}
			bool flag2;
			do
			{
				flag2 = false;
				foreach (ProcessChange value4 in dictionary2.Values)
				{
					if (!members.ContainsKey(value4.Pid) && HasVerifiedFamilyParent(value4, members, dictionary2))
					{
						members[value4.Pid] = value4.Creation;
						dictionary[value4.Pid] = value4.Creation;
						flag2 = true;
					}
				}
			}
			while (flag2);
			return dictionary;
		}

		private static Dictionary<int, long> CaptureStoppedProcessIdentity(IEnumerable<int> pids)
		{
			Dictionary<int, long> dictionary = new Dictionary<int, long>();
			if (pids == null)
			{
				return dictionary;
			}
			foreach (int pid in pids)
			{
				IntPtr intPtr = Native.OpenProcess(4096, false, pid);
				if (intPtr == IntPtr.Zero)
				{
					if (Native.LastOpenProcessFailureWasNoSuchProcess())
					{
						dictionary[pid] = 0L;
					}
					continue;
				}
				try
				{
					long creation;
					long cpu;
					ulong io;
					if (Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation > 0)
					{
						dictionary[pid] = creation;
					}
				}
				finally
				{
					Native.CloseHandle(intPtr);
				}
			}
			return dictionary;
		}

		private void PruneWhitelistFamilyMembersIfDue(ProcessSnapshot all)
		{
			if (all == null || Interlocked.CompareExchange(ref whiteHasFamilyRules, 0, 0) == 0)
			{
				return;
			}
			long ticks = DateTime.UtcNow.Ticks;
			long num = Interlocked.Read(ref lastWhiteFamilyPruneTicks);
			if ((num > 0 && ticks >= num && ticks - num < 200000000) || Interlocked.CompareExchange(ref lastWhiteFamilyPruneTicks, ticks, num) != num)
			{
				return;
			}
			lock (whiteEvalSync)
			{
				HashSet<int> hashSet = new HashSet<int>();
				lock (sync)
				{
					foreach (Dictionary<int, long> value2 in whiteFamilyMembers.Values)
					{
						hashSet.UnionWith(value2.Keys);
					}
				}
				if (hashSet.Count == 0)
				{
					return;
				}
				HashSet<int> hashSet2 = new HashSet<int>();
				Dictionary<int, long> dictionary = new Dictionary<int, long>();
				ProcEntry[] entries = all.Entries;
				foreach (ProcEntry procEntry in entries)
				{
					int pid = procEntry.Pid;
					if (hashSet.Contains(pid))
					{
						hashSet2.Add(pid);
						dictionary[pid] = procEntry.Creation;
					}
				}
				lock (sync)
				{
					foreach (Dictionary<int, long> value3 in whiteFamilyMembers.Values)
					{
						List<int> list = null;
						foreach (KeyValuePair<int, long> item in value3)
						{
							long value;
							if (!hashSet2.Contains(item.Key) || (dictionary.TryGetValue(item.Key, out value) && value != item.Value))
							{
								if (list == null)
								{
									list = new List<int>();
								}
								list.Add(item.Key);
							}
						}
						if (list == null)
						{
							continue;
						}
						foreach (int item2 in list)
						{
							value3.Remove(item2);
						}
						whiteFamilyMembersVersion++;
					}
				}
			}
		}

		private static bool HasVerifiedFamilyParent(ProcessChange change, Dictionary<int, long> members, Dictionary<int, ProcessChange> liveStarts)
		{
			long value;
			if (change == null || change.ParentPid <= 0 || change.Creation <= 0 || !members.TryGetValue(change.ParentPid, out value) || value <= 0 || change.Creation <= value)
			{
				return false;
			}
			long num = change.ParentCreation;
			ProcessChange value2;
			if (num <= 0 && liveStarts != null && liveStarts.TryGetValue(change.ParentPid, out value2))
			{
				num = value2.Creation;
			}
			return num == value;
		}
	}
}
