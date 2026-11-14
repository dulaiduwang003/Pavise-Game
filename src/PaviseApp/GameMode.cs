using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {

        private readonly ProcessActionFailureBudget autoAddFailureBudget = new ProcessActionFailureBudget();
        private readonly ProcessActionFailureBudget rendererProbeFailureBudget = new ProcessActionFailureBudget();

        private const double AutoAddMinUtilization = 20.0;

        private const int AutoAddGateMs = 30000;

        private const int AutoAddRejectCacheLimit = 64;

        private const int AutoAddEvidenceAgeMs = 120000;

        private const uint DuplicateSameAccess = 2u;

        internal const string GamePriorityAction = "GamePriority";

        internal const string GameIoAction = "GameIoPriority";

        internal const string GameGpuAction = "GameGpuPriority";

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

        private bool coreRangeUnrestricted;

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

        private readonly GameAffinityJobLease coreAffinityJobs = new GameAffinityJobLease { OverrideThreadPlacement = true };

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

        private static readonly string[] BoostActions = new string[4] { "GamePriority", "GameIoPriority", "GameGpuPriority", "GameEcoQoS" };

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

        // 没有画面提交证据时，连续两次线程集采样一致后才提交自动隔离。
        private string criticalAutoSelectionKey = "";

        private int criticalAutoSelectionStable;

        private string criticalAutoSelectionLabel = "";

        private readonly CriticalThreadIdealHints criticalIdealHints = new CriticalThreadIdealHints();

        private CriticalThreadHardAffinitySession criticalSession;

        private bool criticalRestorationConfirmed;

        // 线程已恢复，仅等待已停止的辅助进程退出并完成清理。
        private bool criticalHelperClosureWaiting;

        // 有界重试所有者在观察等待期间设置，避免重复调用 Dispose。
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

        private string[] criticalChoiceLabels = new string[0];

        private bool criticalChoiceFrameAssociated;

        private static readonly object criticalStateLogGate = new object();

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
                Logger.Log(Lang.T(value ? "log.autoadd.1" : "log.autoadd.2", 0));
                if (autoAddPreferenceUnsaved)
                {
                    Logger.Warn(Lang.T("autoadd.preference.unsaved", 0));
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
                bool enabledChanged;
                lock (sync)
                {
                    enabledChanged = enabled != value;
                    enabled = value;
                    if (enabledChanged)
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
                if (enabledChanged)
                {
                    if (value)
                    {
                        ClearEnvFuse("gamedvr");
                        ClearEnvFuse("mmcss");
                    }
                    SyncGameDvr();
                }
                if (enabledChanged && value)
                {
                    RequestFullGameDetection();
                }
                if (enabledChanged && value)
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

    }
}
