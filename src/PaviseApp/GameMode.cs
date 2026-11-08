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
					object heldSnapshot = held;
					held = null;
					Monitor.Exit(heldSnapshot);
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
				string message = ((Volatile.Read(ref revoked) != 0) ? "revoked" : ((Interlocked.Read(ref owner.criticalIntentEpoch) != epoch) ? "intent changed" : ((!object.ReferenceEquals(owner.criticalOriginalTarget, this)) ? "superseded" : ((!owner.enabled || owner.stopping || owner.panicReq) ? "game mode stopped" : ((!Volatile.Read(ref owner.active)) ? "session ended" : ((Interlocked.Read(ref owner.gameSessionStarts) != gameSerial) ? "another session" : (owner.ProfileStoreSaveFailed ? "profile store unavailable" : null)))))));
				if (message != null)
				{
					throw new OperationCanceledException("The original game-thread intent is no longer active: " + message);
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
					string textResult = ((!enabled) ? PerformancePolicy.Text("守护已暂停", "Guard paused") : (active ? (PerformancePolicy.Text("正在守护 · ", "Guarding · ") + activeGame) : PerformancePolicy.Text("自动策略就绪 · 等待游戏", "Automatic policy ready · no active game")));
					return textResult + ((envFused.Count == 0) ? "" : PerformancePolicy.Text(" · 有受限动作，查看记录", " · actions blocked; see records"));
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
				bool matchesEnabled;
				lock (sync)
				{
					matchesEnabled = enabled != value;
					enabled = value;
					if (matchesEnabled)
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
				if (matchesEnabled)
				{
					if (value)
					{
						ClearEnvFuse("gamedvr");
						ClearEnvFuse("mmcss");
					}
					SyncGameDvr();
				}
				if (matchesEnabled && value)
				{
					RequestFullGameDetection();
				}
				if (matchesEnabled && value)
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
					bool gmEnglishInputV1Enabled = Settings.Save("GmEnglishInputV1", value);
					englishInputOn = value && gmEnglishInputV1Enabled;
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
						string armedGameNameSnapshot = armedGameName;
						return (armedGameNameSnapshot != null) ? Lang.F("st.armed", armedGameNameSnapshot) : Lang.T("st.mon");
					}
					long gameGoneSinceTicksSnapshot = gameGoneSinceTicks;
					if (gameGoneSinceTicksSnapshot != 0)
					{
						int remainingExitGraceSeconds = ExitGraceSeconds - (int)((DateTime.UtcNow.Ticks - gameGoneSinceTicksSnapshot) / 10000000);
						if (remainingExitGraceSeconds < 0)
						{
							remainingExitGraceSeconds = 0;
						}
						return Lang.F("st.grace", activeGame, remainingExitGraceSeconds);
					}
					int throttledCountCachedResult = core.ThrottledCountCached();
					int count = boostStateVerified.Count;
					string stActiveText = Lang.F("st.active", activeGame, throttledCountCachedResult);
					return stActiveText + Lang.T("st.sep") + Lang.F("st.boost", count, PowerPlanStatusText);
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
					string rendererNameOffset = activeDetection.RendererName ?? activeGame ?? "Game";
					if (ProtectedGameRoster.Contains(activeDetection.RendererName))
					{
						return Lang.F("v14.boost.protected", rendererNameOffset);
					}
					if (boostStateVerified.Contains(activeDetection.RendererPid))
					{
						return Lang.F("v14.boost.verified", rendererNameOffset);
					}
					if (boostHandleStripped.Contains(activeDetection.RendererPid) || boostStateWarned.Contains(activeDetection.RendererPid))
					{
						return Lang.F("v14.boost.protected", rendererNameOffset);
					}
					return Lang.F("v14.boost.applying", rendererNameOffset);
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
				object instance;
				switch (value)
				{
				default:
					instance = "off";
					break;
				case "latest":
				case "j":
				case "k":
					instance = value;
					break;
				}
				string message = (nvDlssMode = (string)instance);
				Settings.SaveStr("NvDlss", message);
				if (message == "off")
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

		[DllImport("user32.dll")]
		private static extern bool GetLastInputInfo(ref AutomationLastInput input);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern uint GetProcessId(IntPtr process);

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
			bool runSessionRecoveryRouteSucceeded = RunSessionRecoveryRoute("critical", () => StopCriticalThreads(0, "Deactivate"));
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
            bool runSessionRecoveryRouteSucceeded2 = RunSessionRecoveryRoute("guard", delegate
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
			bool runSessionRecoveryRouteSucceeded3 = RunSessionRecoveryRoute("unboost", () => UnboostGames());
			EndAutoCores();
			slowEnvAtTicks = 0L;
            int deactivateCoreValue = 0;
            bool runSessionRecoveryRouteSucceeded4 = RunSessionRecoveryRoute("background", delegate
            {
                List<int> list = core.PidsWith(SuppressReason.Background);
                deactivateCoreValue += core.ReleaseReason(SuppressReason.Background);
                foreach (int item in list) if (core.IsThrottled(item)) return false;
                return true;
            });
			bool restoreEnvSucceeded = RestoreEnv();
			ClearEnvRetryState();
			if (runSessionRecoveryRouteSucceeded3)
			{
				CrashGuard.ClearBoost();
			}
			int deactivateCoreValueOffset = deactivateCoreValue + gracePreReleased;
			gracePreReleased = 0;
			if (!quiet || deactivateCoreValueOffset > 0)
			{
				Logger.Log(Lang.T("log.gamemodeboost.52") + reason + Lang.T("log.gamemodeboost.53") + deactivateCoreValueOffset + Lang.T("log.gamemodeboost.54"));
			}
			lock (sync)
			{
				activeDetection = null;
				transitionProbeRendererPid = 0;
				transitionProbeRendererCreation = 0L;
			}
			ClearSticky();
			NotifyExtensionSession(false);
			bool runRecoveryWaitSucceeded = RunRecoveryWait("standby-worker", () => standbyCleaner == null || standbyCleaner.Drain(0));
			bool runRecoveryWaitSucceeded2 = RunRecoveryWait("input-worker", () => DrainEnglishInput(0));
			bool runRecoveryWaitSucceeded3 = RunRecoveryWait("cache-worker", () => cacheWarm == null || cacheWarm.Drain(0));
			bool runSessionRecoveryRouteSucceeded5 = RunSessionRecoveryRoute("performance-worker", () => performanceActions.WaitForSessionRestore(0));
            bool verified = RunSessionRecoveryRoute("verify", () => EnvRestoreCompleted(true) && !SelfYield.HasResidue, "*");
			bool allConditionsMet = runSessionRecoveryRouteSucceeded3 && restoreEnvSucceeded && runSessionRecoveryRouteSucceeded4 && runRecoveryWaitSucceeded && runRecoveryWaitSucceeded2 && runRecoveryWaitSucceeded3 && runSessionRecoveryRouteSucceeded2 && runSessionRecoveryRouteSucceeded5 && runSessionRecoveryRouteSucceeded && reportClean && verified && !SelfYield.HasResidue;
			// Report failure only after bounded retries are exhausted; queued retries remain restoring.
			RecoveryRouteSet captured = recoveryCaptureRoutes;
			bool retrying = !allConditionsMet && CapturingSessionRecovery && captured != null && captured.RetryScheduled;
			RecordAutomation("session", allConditionsMet ? "restored" : (retrying ? "restoring" : "restore-failed"));
			return allConditionsMet;
		}

		private sealed class CriticalAutoTopology
		{
			internal ulong[] Physical;
			internal ulong[] Domains;
			internal ulong Allowed, Reserved, Performance, PreferredDomain;
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
			foreach (string entryKey in envKeys)
			{
				if (Settings.Load("EnvFuse_" + entryKey, false))
				{
					envFused.Add(entryKey);
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
				bool ctorReady = false;
				bool ctorReady2 = false;
				bool ctorReady3 = true;
				bool ctorReady4 = false;
				bool ctorReady5 = false;
				bool ctorReady6 = false;
				string a = null;
				List<WhitelistRule> list = new List<WhitelistRule>();
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				string[] allLines = File.ReadAllLines(whitePath);
				foreach (string entryText in allLines)
				{
					string entryTextTrimmed = entryText.Trim();
					if (entryTextTrimmed.Length == 0 || entryTextTrimmed.StartsWith("#"))
					{
						continue;
					}
					if (string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V4", StringComparison.Ordinal) || string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V3", StringComparison.Ordinal) || string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V2", StringComparison.Ordinal))
					{
						if (ctorReady || ctorReady4 || ctorReady5)
						{
							throw new InvalidDataException(Lang.T("t.gamemode.11"));
						}
						ctorReady = true;
						ctorReady3 = !string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V4", StringComparison.Ordinal);
						ctorReady2 = string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V2", StringComparison.Ordinal);
						continue;
					}
					if (entryTextTrimmed.StartsWith("PAVISE_WHITELIST_END|", StringComparison.Ordinal))
					{
						if (!ctorReady || ctorReady5)
						{
							throw new InvalidDataException(Lang.T("t.gamemode.12"));
						}
						ctorReady5 = true;
						a = entryTextTrimmed;
						continue;
					}
					if (ctorReady5)
					{
						throw new InvalidDataException(Lang.T("t.gamemode.13"));
					}
					WhitelistRule rule2;
					if (ctorReady)
					{
						if (!WhitelistRule.TryParseVersioned(entryTextTrimmed, out rule2))
						{
							throw new InvalidDataException(Lang.T("t.gamemode.14"));
						}
					}
					else
					{
						if (entryTextTrimmed.Length > 1 && entryTextTrimmed[1] == '|' && (entryTextTrimmed[0] == 'N' || entryTextTrimmed[0] == 'n' || entryTextTrimmed[0] == 'P' || entryTextTrimmed[0] == 'p' || entryTextTrimmed[0] == 'F' || entryTextTrimmed[0] == 'f'))
						{
							throw new InvalidDataException(Lang.T("t.gamemode.15"));
						}
						if (!WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, entryTextTrimmed, out rule2))
						{
							throw new InvalidDataException(Lang.T("t.gamemode.16"));
						}
					}
					ctorReady4 = true;
					if (!hashSet.Add(rule2.Key))
					{
						throw new InvalidDataException(Lang.T("t.gamemode.17"));
					}
					list.Add(rule2);
				}
				if (ctorReady && ctorReady5)
				{
					if (!string.Equals(a, BuildWhitelistFooter(list), StringComparison.Ordinal))
					{
						throw new InvalidDataException(Lang.T("t.gamemode.18"));
					}
					ctorReady6 = ctorReady2;
				}
				else
				{
					if (ctorReady && !ctorReady2)
					{
						throw new InvalidDataException(Lang.T("t.gamemode.19"));
					}
					if (ctorReady)
					{
						if (!ctorReady4)
						{
							throw new InvalidDataException(Lang.T("t.gamemode.20"));
						}
						ctorReady6 = true;
					}
					else
					{
						if (!ctorReady4)
						{
							throw new InvalidDataException(Lang.T("t.gamemode.21"));
						}
						ctorReady6 = true;
					}
				}
				if (ctorReady3)
				{
					hashSet.Clear();
					foreach (WhitelistRule item in list)
					{
						hashSet.Add(item.Key);
					}
					ctorReady6 = true;
				}
				string[] presetWhitelist = SystemProcessCatalog.PresetWhitelist;
				foreach (string value in presetWhitelist)
				{
					WhitelistRule rule3;
					if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, value, out rule3) && hashSet.Add(rule3.Key))
					{
						list.Add(rule3);
						ctorReady6 = true;
					}
				}
				foreach (WhitelistRule item2 in list)
				{
					AddWhiteRuleNoSave(item2);
				}
				if (ctorReady6 && !SaveWhite(list))
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

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);

        internal void RestorePowerOverlay()
        {
            // Recovery only: new managed sources do not add a second implicit power policy.
            if (PowerOverlay.HasResidue) PowerOverlay.Restore();
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

	}
}
