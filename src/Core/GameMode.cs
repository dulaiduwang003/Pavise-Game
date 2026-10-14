// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
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
        }

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
        private readonly Dictionary<int, long> gameBoostNextAudit =
            new Dictionary<int, long>();
        private readonly HashSet<int> tweakApplied = new HashSet<int>();
        private readonly HashSet<int> boostDenied = new HashSet<int>();
        private readonly HashSet<int> boostStateWarned = new HashSet<int>();
        private readonly HashSet<int> boostStateVerified = new HashSet<int>();
        private readonly HashSet<int> boostHandleStripped = new HashSet<int>();
        private readonly HashSet<int> boostEcoGaveUp = new HashSet<int>();
        private readonly Dictionary<int, int> placementFail = new Dictionary<int, int>();
        private readonly HashSet<int> placementGaveUp = new HashSet<int>();
        private readonly Dictionary<int, int> boostStateFail = new Dictionary<int, int>();
        private const int BoostRetryMax = 3;
        private const int PlacementRetryMax = 3;
        private const int StateRetryMax = 8;
        private volatile bool bgSuppressOn;
        private volatile bool boostOn;
        private volatile bool pauseDlOn;
        private volatile bool pauseMaintOn;
        private bool maintActive;
        private volatile bool nvMaxPerf;
        private volatile bool nvVrrWindowedOn;
        private volatile bool intelEnduranceOn;
        private bool nvVrrActive, intelEndActive;
        private volatile string nvLowLatMode = "off";
        private volatile bool nvSmoothMotion;
        private volatile bool nvShaderCacheMax;
        private volatile bool nvRebarOn;
        private volatile string nvDlssMode = "off";
        private volatile bool awakeOn;
        private volatile bool gpuPowerMaxOn;
        private volatile bool dwmBoostOn;
        private volatile bool wsTrimOn;
        private volatile bool amdAntiLag;
        private volatile bool amdAfmf;
        private volatile bool rsrOn;
        private bool pqosActive;
        private bool awakeActive;
        private bool dwmBoostActive;
        private bool gpwActive;
        // 计时器请求与释放必须走同一接口。
        private readonly TimerResolutionLease timerLease = new TimerResolutionLease();
        private volatile bool killGameDvr;
        private volatile bool mmcssOn;
        private bool guardDvrActive, guardMmcssActive;
        private volatile bool planSwitch;
        private long slowEnvAtTicks;

        internal const int SlowEnvDelaySeconds = 20;
        private volatile bool pauseUpdateOn;
        private volatile bool pauseServicesOn;
        private volatile bool aggressiveOn;
        private volatile bool renderLaneOn;
        private volatile bool gpuDemoteOn;
        private volatile bool panicReq;
        private int panicSeq;
        private readonly object panicCallGate = new object();
        private int panicServed;
        private volatile bool panicResult;
        private readonly ManualResetEvent panicDone = new ManualResetEvent(true);
        private bool timerRaised { get { return timerLease != null && timerLease.HasResidue; } }
        private bool doActive;
        private readonly ulong allMask;
        private PerformancePreset preset;
        private GameDetection activeDetection;
        internal int GetMonitoringRendererPid()
        {
            lock (sync) return active && activeDetection != null ? activeDetection.RendererPid : 0;
        }
        internal long GetMonitoringRendererBirth(int pid)
        {
            lock (sync) return active && activeDetection != null && activeDetection.RendererPid == pid
                ? activeDetection.RendererCreation : 0;
        }
        private volatile PolicySnapshot sessionPolicy;
        private Thread worker;

        private struct GameId { public string Name; public long Creation; }
        private GameDetection stickyDetection;
        private readonly Dictionary<int, GameId> stickyIds =
            new Dictionary<int, GameId>();
        private int stickyMiss;
        private bool stickyGraceOnly;
        private const int StickyGraceMisses = 1;

        internal const string ExitGraceSettingKey = "GameExitGraceSeconds";
        internal const int DefaultExitGraceSeconds = 8;
        private volatile int exitGraceSeconds = DefaultExitGraceSeconds;
        private int gracePreReleased;
        private long gameGoneSinceTicks;

        public GameMode(string dir, SuppressionCore core)
        {
            dataDir = dir;
            PurgeRetiredIrqObservation(dir);
            whitePath = Path.Combine(dir, "Pavise.whitelist.txt");
            autoIgnorePath = Path.Combine(dir, LibraryIgnoreTransaction.IgnoreFileName);
            profileStore = new GameProfileStore(dir);
            libraryIgnoreTransaction = new LibraryIgnoreTransaction(dir, profileStore);
            if (libraryIgnoreTransaction.TryRecover()) LoadAutoIgnore();
            using (Process self = Process.GetCurrentProcess())
            {
                selfPid = self.Id;
                selfName = self.ProcessName;
                try { selfSession = self.SessionId; } catch { selfSession = -1; }
            }
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            windowsPrefix = string.IsNullOrEmpty(winDir) ? @"C:\Windows\" : winDir.TrimEnd('\\') + "\\";
            this.core = core;
            allMask = CpuTopology.AllMask;
            ulong throttleMask = CpuTopology.ThrottleMask, strictMask = CpuTopology.StrictBoostMask;
            if (CpuTopology.Hybrid)
                Logger.Log(Lang.T("log.gamemode.1") + CpuTopology.DescribeMask(CpuTopology.PerfMask)
                    + Lang.T("log.gamemode.2") + CpuTopology.DescribeMask(CpuTopology.EffMask)
                    + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(throttleMask)
                    + Lang.T("log.gamemode.3") + CpuTopology.DescribeMask(strictMask));
            else if (CpuTopology.AsymCache)
                Logger.Log(Lang.T("log.gamemode.4")
                    + CpuTopology.DescribeMask(strictMask)
                    + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(throttleMask));
            else if (CpuTopology.PartitionTag == "symmetric-ccd")
                Logger.Log(Lang.T("log.gamemode.5")
                    + CpuTopology.DescribeMask(strictMask)
                    + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(throttleMask));
            else
                Logger.Log(Lang.T("log.gamemode.6"));
            if (CpuTopology.FavoredMask != 0)
                Logger.Log(Lang.T("log.gamemode.favored") + CpuTopology.DescribeMask(CpuTopology.FavoredMask)
                    + Lang.T("log.gamemode.favored.detail") + CpuTopology.FavoredDetail);
            if (!CpuTopology.TopologySourcesAgree)
                Logger.Warn(Lang.F("log.gamemode.sourcesplit", CpuTopology.TopologySourceCounts));
            if (CpuTopology.AllMaskReconciled)
                Logger.Warn(Lang.F("log.gamemode.countsplit",
                    Environment.ProcessorCount, CpuTopology.CountSetBits(CpuTopology.AllMask)));
            if (CpuTopology.CpuSetPartitionRejected)
                Logger.Log(Lang.T("log.gamemode.8"));
            if (CpuTopology.StrictMaskUnsafe)
                Logger.Log(Lang.T("log.gamemode.9"));
            int savedExitGrace;
            exitGraceSeconds = int.TryParse(Settings.LoadStr(ExitGraceSettingKey, "8"), out savedExitGrace)
                && IsExitGraceChoice(savedExitGrace) ? savedExitGrace : DefaultExitGraceSeconds;
            bgSuppressOn = Settings.Load("GmSuppress", true);
            boostOn = Settings.Load("GmBoost", true);
            pauseDlOn = Settings.Load("GmPauseDl", true);
            pauseUpdateOn = Settings.Load("GmPauseUpdate", false);
            pauseMaintOn = Settings.Load(PolicyCatalog.KeyPauseMaintenance, true);
            pauseServicesOn = Settings.Load(PolicyCatalog.KeyPauseServices, false);
            InitializeStandbyCleaner();
            InitializeAutoGpu();
            InitializeEnglishInput();
            nvMaxPerf = Settings.Load("NvMaxPerf", false);
            nvVrrWindowedOn = Settings.Load("NvVrrWindowed", false);
            intelEnduranceOn = Settings.Load(PolicyCatalog.KeyIntelEndurance, false);
            nvLowLatMode = Settings.LoadStr("NvLowLat", "off");
            nvSmoothMotion = Settings.Load("NvSmoothMotion", false);
            nvShaderCacheMax = Settings.Load("NvShaderCache", false);
            nvRebarOn = Settings.Load("NvRebar", false);
            nvDlssMode = Settings.LoadStr("NvDlss", "off");
            gpuPrefStageOn = Settings.Load("GpuPrefStageOn", true);
            awakeOn = Settings.Load("GmAwake", true);
            gpuPowerMaxOn = false;
            dwmBoostOn = Settings.Load("GmDwmBoost", false);
            wsTrimOn = Settings.Load(PolicyCatalog.KeyWsTrim, false);
            Settings.Remove("GmIdlePolicyV1");
            Settings.Remove("GmAudioLowLatV1");
            amdAntiLag = false;
            amdAfmf = Settings.Load("AmdAfmf", false);
            rsrOn = Settings.Load("GmRsr", false);
            autoAddOn = Settings.Load("GmAutoAdd", false);
            Settings.Remove("GmCacheWarm");
            cacheWarmOn = Settings.Load(PolicyCatalog.KeyCacheWarm, false);
            adaptiveEscalateOn = Settings.Load(PolicyCatalog.KeyAdaptiveEscalate, false);
            killGameDvr = Settings.Load("GameDvrOff", true);
            mmcssOn = Settings.Load("GmMmcss", true);
            foreach (string retired in AutoCores.RetiredSettings) Settings.Remove(retired);
            planSwitch = Settings.Load("PowerPlanOn", true);
            aggressiveOn = Settings.Load("GmAggressive", false);
            renderLaneOn = Settings.Load(PolicyCatalog.KeyRenderLane,
                PolicyCatalog.ItemOf(PolicyCatalog.KeyRenderLane).Fallback == "1");
            gpuDemoteOn = Settings.Load("GmGpuDemote", false);
            SuppressionCore.GpuDemoteEnabled = gpuDemoteOn;
            foreach (string envKey in EnvKeys)
                if (Settings.Load("EnvFuse_" + envKey, false)) envFused.Add(envKey);
            int presetRaw;
            preset = int.TryParse(Settings.LoadStr("PerformancePreset", "0"), out presetRaw)
                ? PresetValue.From(presetRaw) : PerformancePreset.Standard;

            try
            {
                if (!File.Exists(whitePath) && !WritePreset())
                    throw new IOException(Lang.T("t.gamemode.10"));
                bool versioned = false;
                bool legacyVersion = false;
                bool previousDefaults = true;
                bool sawRule = false;
                bool sawFooter = false;
                bool rewriteFormat = false;
                string footer = null;
                var loadedRules = new List<WhitelistRule>();
                var loadedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(whitePath))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    if (string.Equals(t, WhitelistRule.Header, StringComparison.Ordinal)
                        || string.Equals(t, WhitelistRule.PreviousHeader, StringComparison.Ordinal)
                        || string.Equals(
                            t, WhitelistRule.LegacyHeader, StringComparison.Ordinal))
                    {
                        if (versioned || sawRule || sawFooter)
                            throw new InvalidDataException(Lang.T("t.gamemode.11"));
                        versioned = true;
                        previousDefaults = !string.Equals(t, WhitelistRule.Header, StringComparison.Ordinal);
                        legacyVersion = string.Equals(
                            t, WhitelistRule.LegacyHeader, StringComparison.Ordinal);
                        continue;
                    }
                    if (t.StartsWith(WhitelistFooterPrefix, StringComparison.Ordinal))
                    {
                        if (!versioned || sawFooter)
                            throw new InvalidDataException(Lang.T("t.gamemode.12"));
                        sawFooter = true;
                        footer = t;
                        continue;
                    }
                    if (sawFooter)
                        throw new InvalidDataException(Lang.T("t.gamemode.13"));

                    WhitelistRule rule;
                    if (versioned)
                    {
                        if (!WhitelistRule.TryParseVersioned(t, out rule))
                            throw new InvalidDataException(Lang.T("t.gamemode.14"));
                    }
                    else
                    {

                        if (t.Length > 1 && t[1] == '|'
                            && (t[0] == 'N' || t[0] == 'n' || t[0] == 'P'
                                || t[0] == 'p' || t[0] == 'F' || t[0] == 'f'))
                            throw new InvalidDataException(Lang.T("t.gamemode.15"));
                        if (!WhitelistRule.TryCreate(
                            WhitelistRuleKind.LegacyName, t, out rule))
                            throw new InvalidDataException(Lang.T("t.gamemode.16"));
                    }
                    sawRule = true;
                    if (!loadedKeys.Add(rule.Key))
                        throw new InvalidDataException(Lang.T("t.gamemode.17"));
                    loadedRules.Add(rule);
                }
                if (versioned && sawFooter)
                {
                    if (!string.Equals(
                        footer, BuildWhitelistFooter(loadedRules),
                        StringComparison.Ordinal))
                        throw new InvalidDataException(Lang.T("t.gamemode.18"));
                    rewriteFormat = legacyVersion;
                }
                else if (versioned && !legacyVersion)
                {
                    throw new InvalidDataException(Lang.T("t.gamemode.19"));
                }
                else if (versioned)
                {

                    if (!sawRule)
                        throw new InvalidDataException(Lang.T("t.gamemode.20"));
                    rewriteFormat = true;
                }
                else if (!sawRule)
                    throw new InvalidDataException(Lang.T("t.gamemode.21"));
                else rewriteFormat = true;

                if (previousDefaults)
                {
                    loadedRules.RemoveAll(delegate(WhitelistRule rule) {
                        return rule.Kind == WhitelistRuleKind.LegacyName
                            && SystemProcessCatalog.RetiredInteractiveName(rule.Value);
                    });
                    loadedKeys.Clear();
                    foreach (WhitelistRule rule in loadedRules) loadedKeys.Add(rule.Key);
                    rewriteFormat = true;
                }
                foreach (string entry in SystemProcessCatalog.PresetWhitelist)
                {
                    WhitelistRule presetRule;
                    if (WhitelistRule.TryCreate(
                        WhitelistRuleKind.LegacyName, entry, out presetRule)
                        && loadedKeys.Add(presetRule.Key))
                    {
                        loadedRules.Add(presetRule);
                        rewriteFormat = true;
                    }
                }
                foreach (WhitelistRule rule in loadedRules) AddWhiteRuleNoSave(rule);
                if (rewriteFormat && !SaveWhite(loadedRules))
                    Logger.Log(Lang.T("log.gamemode.24"));
            }
            catch (Exception ex)
            {

                foreach (string entry in SystemProcessCatalog.PresetWhitelist) AddWhiteNoSave(entry);
                Logger.Log(Lang.T("log.gamemode.25") + ex.Message);
            }

            try
            {
                profiles.AddRange(profileStore.LoadProfiles(!libraryIgnoreTransaction.RecoveryPending));
                List<GameProfile> refreshed = GetProfiles();
                if (!ProfileStoreSaveFailed && !libraryIgnoreTransaction.RecoveryPending
                    && RefreshLibraryInstallRoots(refreshed) && SaveProfileSnapshotLocked(refreshed))
                {
                    profiles.Clear(); profiles.AddRange(refreshed);
                }
            }
            catch { }
            InitializeRendererObservations();
        }

        internal static readonly string[] RetiredIrqObservationFiles =
        {
            "Pavise.irq-sessions.dat", "Pavise.irq-sessions.dat.*.tmp",
            "irq-adjustment-*.xml", "irq-adjustment-*.xml.*.tmp"
        };

        private static void PurgeRetiredIrqObservation(string dir)
        {
            Settings.Remove("IrqSessionProbe");
            Settings.Remove("IrqObserveSkipsV1");
            Settings.Remove("IrqLastCaptureResultV1");
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string pattern in RetiredIrqObservationFiles)
                    foreach (string path in Directory.GetFiles(dir, pattern))
                        try { File.Delete(path); } catch { }
            }
            catch { }
        }

        private bool WritePreset()
        {
            var lines = new List<string>();
            lines.Add(Lang.T("t.gamemode.26"));
            lines.Add(Lang.T("t.gamemode.27"));
            lines.Add(Lang.T("t.gamemode.28"));
            lines.Add(Lang.T("t.gamemode.29"));
            lines.Add(Lang.T("lib.family.whitelist.note"));
            lines.Add(WhitelistRule.Header);
            var rules = new List<WhitelistRule>();
            foreach (string entry in SystemProcessCatalog.PresetWhitelist)
            {
                WhitelistRule rule;
                if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, entry, out rule))
                {
                    rules.Add(rule);
                    lines.Add(rule.Serialize());
                }
            }
            lines.Add(BuildWhitelistFooter(rules));
            return AtomicFile.WriteLines(whitePath, lines.ToArray(), Lang.T("t.gamemode.31"));
        }

        private void AddWhiteNoSave(string n)
        {
            WhitelistRule rule;
            if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, n, out rule))
                AddWhiteRuleNoSave(rule);
        }

        private static string StripExe(string s)
        {
            return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4) : s;
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                bool changed;
                lock (sync)
                {
                    changed = enabled != value;
                    enabled = value;
                    if (changed)
                    {
                        if (!value) RememberModeSwitchLockLocked(activeDetection);
                        InvalidateCacheWarm();
                        Interlocked.Increment(ref optionalServiceGeneration);
                        InvalidateStandbyCleanerWork();
                        InvalidateEnglishInputWork();
                        ClearFamilyDiscovery();
                        InvalidateRendererHandoff();
                    }
                }
                if (changed)
                {
                    if (!value) backgroundFreeze.Clear("guard-disabled");
                    else { ClearEnvFuse("gamedvr"); ClearEnvFuse("mmcss"); }
                    SyncGameDvr();
                }
                if (changed && value) RequestFullGameDetection();
                if (changed && value) RequestPolicyApply();
                else kick.Set();
            }
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
                bool wanted = enabled && !stopping && !ProfileStoreSaveFailed;
                if (!wanted) return SetGuardScheduling(false, false);
                guardDvrActive = EnvStep("gamedvr", killGameDvr,
                    guardDvrActive || (!killGameDvr && GameDvr.HasResidue), GameDvr.Activate, GameDvr.Restore);
                guardMmcssActive = EnvStep("mmcss", mmcssOn,
                    guardMmcssActive || (!mmcssOn && Mmcss.HasResidue()), Mmcss.Activate, Mmcss.Restore);
                bool dvrOk = guardDvrActive == killGameDvr && !envFused.Contains("gamedvr") && !envRestorePending.Contains("gamedvr");
                bool mmcssOk = guardMmcssActive == mmcssOn && !envFused.Contains("mmcss") && !envRestorePending.Contains("mmcss");
                if (dvrOk) RecordAutomation("gamedvr", killGameDvr ? "applied" : "restored");
                if (mmcssOk) RecordAutomation("mmcss", mmcssOn ? "applied" : "restored");
                return dvrOk && mmcssOk;
            }
        }

        private bool RestoreGuardScheduling()
        {
            lock (sync) return SetGuardScheduling(false, false);
        }

        private bool GuardSchedulingNeedsRestore
        {
            get { return GuardSchedulingRecoveryPending || ((GameDvr.HasResidue || Mmcss.HasResidue())
                && (!enabled || stopping || (profileStore != null && ProfileStoreSaveFailed))); }
        }

        private bool GuardSchedulingRecoveryPending
        {
            get { lock (sync) return envRestorePending.Contains("gamedvr") || envRestorePending.Contains("mmcss"); }
        }

        private bool SetGuardScheduling(bool dvr, bool mmcss)
        {
            bool dvrOk = RunGuarded(dvr ? (Func<bool>)GameDvr.Activate : GameDvr.Restore);
            bool mmcssOk = RunGuarded(mmcss ? (Func<bool>)Mmcss.Activate : Mmcss.Restore);
            if (dvrOk)
            {
                guardDvrActive = dvr;
                envRestorePending.Remove("gamedvr"); envFused.Remove("gamedvr");
                envNextAttempt.Remove("gamedvr"); envFailures.Remove("gamedvr");
            }
            if (mmcssOk)
            {
                guardMmcssActive = mmcss;
                envRestorePending.Remove("mmcss"); envFused.Remove("mmcss");
                envNextAttempt.Remove("mmcss"); envFailures.Remove("mmcss");
            }
            RecordAutomation("gamedvr", dvrOk ? (dvr ? "applied" : "restored") : (dvr ? "failed" : "restore-failed"));
            RecordAutomation("mmcss", mmcssOk ? (mmcss ? "applied" : "restored") : (mmcss ? "failed" : "restore-failed"));
            return dvrOk && mmcssOk;
        }
    }
}
