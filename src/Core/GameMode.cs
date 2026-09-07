// @author bdth 2074055628@qq.com
// 文件用途 对局状态字段 构造 预设写入与系统开关同步
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
        }

        private sealed class IrqProofHardPin
        {
            public int Pid;
            public long Creation;
            public ulong OriginalAffinity;
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
        private readonly Dictionary<int, bool> gamePlacementStrict = new Dictionary<int, bool>();
        private readonly Dictionary<int, IrqProofHardPin> irqProofHardPins =
            new Dictionary<int, IrqProofHardPin>();
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
        private volatile bool laptopPerfOn;
        private bool nvVrrActive, intelEndActive, oemPerfActive;
        private volatile string nvLowLatMode = "off";
        private volatile bool nvSmoothMotion;
        private volatile bool nvShaderCacheMax;
        private volatile bool nvRebarOn;
        private volatile string nvDlssMode = "off";
        private volatile bool awakeOn;
        private volatile bool gpuPowerMaxOn;
        private volatile bool amdAntiLag;
        private volatile bool amdAfmf;
        private volatile bool rsrOn;
        private volatile bool vramShieldOn;
        private bool pqosActive;
        private bool awakeActive;
        private bool audioLatActive;
        private bool dwmBoostActive;
        private bool gpwActive;
        // 0.5ms 请求走的是 ntdll 释放也要走同一条路 记住本局用的是哪条
        private bool timerHalfMs;
        private const uint HalfMsUnits = 5000;
        private volatile bool killGameDvr;
        private volatile bool mmcssOn;
        private volatile bool planSwitch;
        private long slowEnvAtTicks;

        internal const int SlowEnvDelaySeconds = 20;
        private volatile bool pauseUpdateOn;
        private volatile bool pauseServicesOn;
        private volatile bool disableCpuIdleOn;
        private volatile bool corePartitionOn;
        private volatile bool coreDomainAltOn;
        private volatile bool aggressiveOn;
        private volatile bool renderLaneOn;
        private volatile bool gpuDemoteOn;
        private volatile bool panicReq;
        private int panicSeq;
        private readonly object panicCallGate = new object();
        private int panicServed;
        private volatile bool panicResult;
        private readonly ManualResetEvent panicDone = new ManualResetEvent(true);
        private bool timerRaised;
        private bool timerSkipLogged;
        private bool doActive;
        private ulong throttleMask;
        private readonly ulong allMask;
        private readonly ulong gameMask;
        private ulong strictMask;
        private readonly IrqSessionProbe irqProbe = new IrqSessionProbe();
        // present 采集按局新建 与 irqProbe 内部 new InterruptAttribution 同理
        //   每局一份 避免换局时上一局的帧残留累积(PresentProbe.frames 不自清)
        private PresentProbe presentProbe;
        private PerformancePreset preset;
        private GameDetection activeDetection;
        private volatile PolicySnapshot sessionPolicy;
        private Thread worker;

        private struct GameId { public string Name; public long Creation; }
        private GameDetection stickyDetection;
        private readonly Dictionary<int, GameId> stickyIds =
            new Dictionary<int, GameId>();
        private int stickyMiss;
        private bool stickyGraceOnly;
        private const int StickyGraceMisses = 1;

        private const int ExitGraceSeconds = 8;
        private int gracePreReleased;
        private long gameGoneSinceTicks;

        public GameMode(string dir, SuppressionCore core)
        {
            dataDir = dir;
            IrqSessionLedger.Bind(dir);
            RenderLane.ConfigureMutationBoundary(
                irqProbe.BeginExternalMutation,
                irqProbe.EndExternalMutation);
            PowerBudgetYieldRunner.ConfigureMutationBoundary(
                irqProbe.BeginExternalMutation,
                irqProbe.EndExternalMutation);
            VramShield.ConfigureMutationBoundary(
                irqProbe.BeginExternalMutation,
                irqProbe.EndExternalMutation);
            SelfYield.ConfigureMutationBoundary(
                irqProbe.BeginExternalMutation,
                irqProbe.EndExternalMutation);
            core.ConfigureMutationBoundary(
                irqProbe.BeginExternalMutation,
                irqProbe.EndExternalMutation);
            IrqMutationBoundary.Configure(
                irqProbe.BeginExternalMutation,
                irqProbe.EndExternalMutation);
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
            throttleMask = CpuTopology.ThrottleMask;
            gameMask = CpuTopology.BoostMask;
            strictMask = CpuTopology.StrictBoostMask;
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
            if (CpuTopology.AltDomainActive)
                Logger.Log(Lang.T("log.gamemode.7") + CpuTopology.DescribeMask(strictMask)
                    + Lang.T("log.gamemodesettings.3") + CpuTopology.DescribeMask(throttleMask));
            if (CpuTopology.CpuSetPartitionRejected)
                Logger.Log(Lang.T("log.gamemode.8"));
            if (CpuTopology.StrictMaskUnsafe)
                Logger.Log(Lang.T("log.gamemode.9"));
            bgSuppressOn = Settings.Load("GmSuppress", true);
            boostOn = Settings.Load("GmBoost", true);
            pauseDlOn = Settings.Load("GmPauseDl", true);
            LoadCustomCoreMask();
            pauseUpdateOn = Settings.Load("GmPauseUpdate", false);
            pauseMaintOn = Settings.Load(PolicyCatalog.KeyPauseMaintenance, true);
            pauseServicesOn = Settings.Load(PolicyCatalog.KeyPauseServices, false);
            disableCpuIdleOn = Settings.Load(PolicyCatalog.KeyDisableCpuIdle, false);
            InitializeStandbyCleaner();
            InitializeAutoGpu();
            InitializeEnglishInput();
            InitializeIntelGraphics();
            nvMaxPerf = Settings.Load("NvMaxPerf", false);
            nvVrrWindowedOn = Settings.Load("NvVrrWindowed", false);
            intelEnduranceOn = Settings.Load(PolicyCatalog.KeyIntelEndurance, false);
            laptopPerfOn = Settings.Load(PolicyCatalog.KeyLaptopPerf, PolicyCatalog.LaptopPerfDefault);
            nvLowLatMode = Settings.LoadStr("NvLowLat", "off");
            nvSmoothMotion = Settings.Load("NvSmoothMotion", false);
            nvShaderCacheMax = Settings.Load("NvShaderCache", false);
            nvRebarOn = Settings.Load("NvRebar", false);
            nvDlssMode = Settings.LoadStr("NvDlss", "off");
            gpuPrefStageOn = Settings.Load("GpuPrefStageOn", true);
            awakeOn = Settings.Load("GmAwake", true);
            gpuPowerMaxOn = Settings.Load("GmGpuPowerMax", false);
            amdAntiLag = Settings.Load("AmdAntiLag", false);
            amdAfmf = Settings.Load("AmdAfmf", false);
            rsrOn = Settings.Load("GmRsr", false);
            autoAddOn = Settings.Load("GmAutoAdd", false);
            vramShieldOn = Settings.Load(VramShield.EnabledKey, false);
            Settings.Remove("GmCacheWarm"); // Retired read-only feature: discard legacy global preference.
            heavySqueezeOn = Settings.Load(PolicyCatalog.KeyHeavySqueeze, false);
            adaptiveEscalateOn = Settings.Load(PolicyCatalog.KeyAdaptiveEscalate, false);
            killGameDvr = Settings.Load("GameDvrOff", true);
            mmcssOn = Settings.Load("GmMmcss", true);
            planSwitch = Settings.Load("PowerPlanOn", true);
            corePartitionOn = Settings.Load("GmStrictCores", false);
            coreDomainAltOn = Settings.Load("GmCoreDomainAlt", false);
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
                        || string.Equals(
                            t, WhitelistRule.LegacyHeader, StringComparison.Ordinal))
                    {
                        if (versioned || sawRule || sawFooter)
                            throw new InvalidDataException(Lang.T("t.gamemode.11"));
                        versioned = true;
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
                // An unresolved receipt must not be invalidated by seeding a missing primary.
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
                        Interlocked.Increment(ref optionalServiceGeneration);
                        Interlocked.Increment(ref cpuIdleGeneration);
                        InvalidateStandbyCleanerWork();
                        InvalidateEnglishInputWork();
                        InvalidateIntelGraphicsWork();
                        ClearFamilyDiscovery();
                        InvalidateRendererHandoff();
                    }
                }
                if (changed)
                    IrqMutationBoundary.Run(delegate
                    {
                        SyncGameDvr();
                        SyncMmcss();
                    });
                if (changed && value) RequestFullGameDetection();
                if (changed && value) RequestPolicyApply();
                else kick.Set();
            }
        }

        private void SyncGameDvr()
        {
            try { if (enabled && killGameDvr) GameDvr.Activate(); else GameDvr.Restore(); }
            catch { }
        }

        private void SyncMmcss()
        {
            try { if (enabled && mmcssOn) Mmcss.Activate(); else Mmcss.Restore(); }
            catch { }
        }
    }
}
