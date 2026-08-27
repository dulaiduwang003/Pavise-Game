// @author bdth 2074055628@qq.com
// 文件用途 维护游戏模式状态 配置和工作线程
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
        private volatile bool wlanGuardOn;
        private volatile bool nvMaxPerf;
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
        private volatile bool familyExemptOn;
        // 家族豁免的静态镜像 平台目录那种静态调用点没有 GameMode 实例
        //   只用来选文案 判压制一律走实例字段 不要拿它当策略依据
        internal static volatile bool FamilyExemptHint;
        private volatile bool vramShieldOn;
        private bool pqosActive;
        private bool awakeActive;
        private bool gpwActive;
        private volatile bool killGameDvr;
        private volatile bool mmcssOn;
        private volatile bool planSwitch;
        private long slowEnvAtTicks;

        internal const int SlowEnvDelaySeconds = 20;
        private volatile bool pauseUpdateOn;
        private volatile bool corePartitionOn;
        private volatile bool coreDomainAltOn;
        private volatile bool aggressiveOn;
        private volatile bool ifeoOn;
        private volatile bool renderLaneOn;
        private volatile bool gpuDemoteOn;
        private volatile bool panicReq;
        private int panicSeq;
        private readonly object panicCallGate = new object();
        private int panicServed;
        private volatile bool panicResult;
        private readonly ManualResetEvent panicDone = new ManualResetEvent(true);
        private bool wlanActive;
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
            autoIgnorePath = Path.Combine(dir, "Pavise.autoignore.txt");
            LoadAutoIgnore();
            profileStore = new GameProfileStore(dir);
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
            wlanGuardOn = Settings.Load("GmWlanGuard", false);
            LoadCustomCoreMask();
            pauseUpdateOn = Settings.Load("GmPauseUpdate", false);
            nvMaxPerf = Settings.Load("NvMaxPerf", false);
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
            familyExemptOn = Settings.Load("GmFamilyExempt", true);
            FamilyExemptHint = familyExemptOn;
            vramShieldOn = Settings.Load(VramShield.EnabledKey, false);
            killGameDvr = Settings.Load("GameDvrOff", true);
            mmcssOn = Settings.Load("GmMmcss", true);
            planSwitch = Settings.Load("PowerPlanOn", true);
            corePartitionOn = Settings.Load("GmStrictCores", false);
            coreDomainAltOn = Settings.Load("GmCoreDomainAlt", false);
            aggressiveOn = Settings.Load("GmAggressive", false);
            ifeoOn = Settings.Load("GmIfeoBoost", false);
            renderLaneOn = Settings.Load("GmRenderLane", true);
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
                profiles.AddRange(profileStore.LoadProfiles());
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
            lines.Add(Lang.T(familyExemptOn ? "t.gamemode.30.exempt" : "t.gamemode.30"));
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
                bool changed = enabled != value;
                enabled = value;
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

        public bool IsActive { get { lock (sync) return active; } }

        public string ActiveGame { get { lock (sync) return active ? activeGame : null; } }

        public PerformancePreset Preset
        {
            get { lock (sync) return preset; }
            set
            {
                if (!PresetValue.IsValid((int)value)) value = PerformancePreset.Standard;
                lock (sync) preset = value;
                Settings.SaveStr("PerformancePreset", ((int)value).ToString());
                RequestPolicyApply();
            }
        }

        public PerformancePreset ActivePreset
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                if (s != null) return s.Preset;
                lock (sync) return preset;
            }
        }

        public string SessionPolicySourceName
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null && !s.IsGlobal && IsActive ? s.ProfileName : null;
            }
        }

        public string SessionPolicyProfileId
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null && IsActive ? s.ProfileId : null;
            }
        }

        private bool EffSuppress
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffSuppress : bgSuppressOn;
            }
        }

        private bool overlayRaised;
        private int overlayAttempts;

        // 这个挂点每轮扫描都会跑 对局中 500ms 一次
        //   拨失败了不设上限的话 整局每 500ms 重试一次 每次写注册表加打一条失败日志
        //   跟 EnvFuse 一个道理 试够就不试了 下一局重新给机会
        private const int MaxOverlayAttempts = 3;

        // 电源滑块拨到最佳性能 只有笔记本插电打专注档才动 退场必还原
        private void MaybeActivatePowerOverlay(bool competitive)
        {
            if (overlayRaised || overlayAttempts >= MaxOverlayAttempts) return;
            if (!PowerOverlay.ShouldActivate(Native.HasSystemBattery(),
                    Native.OnAcPower(), competitive)) return;
            if (!PowerOverlay.Supported()) { overlayAttempts = MaxOverlayAttempts; return; }
            overlayAttempts++;
            overlayRaised = RunIrqIsolatedMutation(
                delegate { return PowerOverlay.Activate(); });
        }

        internal void RestorePowerOverlay()
        {
            overlayAttempts = 0;
            if (!overlayRaised) return;
            overlayRaised = false;
            PowerOverlay.Restore();
        }

        // 对局激活那一刻定格的档位 功耗让路只在专注档参与
        private PerformancePreset EffPreset
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.Preset : Preset;
            }
        }

        private bool EffBoost
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffBoost : boostOn;
            }
        }

        private bool EffIfeo
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffIfeo : ifeoOn;
            }
        }

        private bool EffLane
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffLane : renderLaneOn;
            }
        }

        // 显存驻留与功耗让路都在对局中途反复读 所以一并走冻结快照
        //   对局里关掉全局开关 撤销会立刻发生 但下一轮采样又按本局定格值重新声明
        //   这跟其余逐游戏项一致 生效值只在对局激活那一刻定格
        private bool EffVramShield
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.VramShield : vramShieldOn;
            }
        }

        private bool EffPowerYield
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.PowerYield : PowerBudgetYieldRunner.EnabledSetting;
            }
        }

        private void BeginSessionPolicy()
        {
            GameProfile source;
            lock (sync) source = activeDetection != null ? activeDetection.Profile : null;
            PolicySnapshot snap = source != null ? PolicyResolver.For(source) : PolicyResolver.Global();
            sessionPolicy = snap;
            if (!snap.IsGlobal)
                Logger.Log(Lang.T("log.gamemode.34") + snap.ProfileName + Lang.T("log.gamemode.35")
                    + snap.OverrideCount + Lang.T("log.gamemode.36"));
            ApplySessionCoreMask(snap);
            ApplySessionCoreDomain(snap);
        }

        private void ApplySessionCoreMask(PolicySnapshot snap)
        {
            if (!snap.HasOverride(PolicyCatalog.KeyCoreMask))
            {
                RestoreGlobalCoreMask();
                return;
            }
            ulong wanted = snap.CoreMask;
            if (wanted == CpuTopology.CustomMask) return;
            if (wanted == 0)
            {
                CpuTopology.SetCustomMask(0);
                Logger.Log(Lang.T("log.gamemodeenv.12") + snap.ProfileName + Lang.T("log.gamemode.37"));
                return;
            }
            if (CpuTopology.SetCustomMask(wanted))
                Logger.Log(Lang.T("log.gamemodeenv.12") + snap.ProfileName + Lang.T("log.gamemode.38")
                    + CpuTopology.DescribeMask(CpuTopology.CustomMask));
            else Logger.Log(Lang.T("log.gamemodeenv.12") + snap.ProfileName + Lang.T("log.gamemode.39"));
        }

        private void RestoreGlobalCoreMask()
        {
            string raw = Settings.LoadStr(CoreMaskKey, "");
            ulong parsed;
            if (raw.Length > 0 && ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed)
                && CpuTopology.SetCustomMask(parsed))
                return;
            CpuTopology.SetCustomMask(0);
        }

        private void ApplySessionCoreDomain(PolicySnapshot snap)
        {
            if (!CpuTopology.DomainPreferenceApplied || !CpuTopology.HasAltPartition()) return;
            if (snap.CoreDomainAlt == CpuTopology.AltDomainActive) return;
            if (!CpuTopology.SwapDomains()) return;
            throttleMask = CpuTopology.ThrottleMask;
            strictMask = CpuTopology.StrictBoostMask;
            core.RefreshTopologyMasks();
            if (snap.HasOverride(PolicyCatalog.KeyCoreDomainAlt))
                Logger.Log(Lang.T("log.gamemodeenv.12") + snap.ProfileName + Lang.T("log.gamemode.40")
                    + CpuTopology.DescribeMask(strictMask) + Lang.T("log.gamemodesettings.3") + CpuTopology.DescribeMask(throttleMask));
            else
                Logger.Log(Lang.T("log.gamemode.41")
                    + CpuTopology.DescribeMask(strictMask) + Lang.T("log.gamemodesettings.3") + CpuTopology.DescribeMask(throttleMask));
        }

#if PAVISE_SELFTEST
        internal void ProbeSessionPolicyApply(GameProfile profile)
        {
            sessionPolicy = profile != null ? PolicyResolver.For(profile) : PolicyResolver.Global();
        }

        internal void ProbeSessionPolicyClear() { sessionPolicy = null; }

        internal bool ProbeEffSuppress { get { return EffSuppress; } }

        internal bool ProbeEffIfeo { get { return EffIfeo; } }
#endif

        internal static bool ShouldUseCorePartition(bool manuallySelected, bool partitionAvailable)
        {
            return manuallySelected && partitionAvailable;
        }

        internal const int WideGameThreads = 64;
        internal const int PartitionKeepPercent = 70;

        internal static bool PartitionLikelyHurts(int threads, int givenCores, int totalCores)
        {
            if (threads < WideGameThreads) return false;
            if (givenCores <= 0 || totalCores <= 0 || givenCores >= totalCores) return false;
            return givenCores * 100 / totalCores <= PartitionKeepPercent;
        }

        public List<GameProfile> GetProfiles()
        {
            lock (sync)
            {
                var copy = new List<GameProfile>();
                foreach (GameProfile p in profiles) copy.Add(p.Clone());
                return copy;
            }
        }

        public bool ProfileStoreSaveFailed
        {
            get
            {
                return profileStore.SaveFailed
                    || Interlocked.CompareExchange(ref profileSaveFailureSignaled, 0, 0) != 0;
            }
        }

        public event Action ProfileStoreSaveFailure;

        private bool SaveProfilesLocked()
        {
            // 首次落盘失败就熔断：强制清空的 UI 回调是异步的，
            // 回调执行前不得再尝试写入任何游戏库数据。
            if (ProfileStoreSaveFailed) return false;
            if (profileStore.Save(profiles)) return true;
            SignalProfileStoreSaveFailure();
            return false;
        }

        private void SignalProfileStoreSaveFailure()
        {
            if (Interlocked.Exchange(ref profileSaveFailureSignaled, 1) != 0) return;
            Action handler = ProfileStoreSaveFailure;
            if (handler != null) { try { handler(); } catch { } }
        }

        private const string CoreMaskKey = "GmCoreMask";

        public string StatusText
        {
            get
            {
                if (!enabled) return Lang.T("st.off");
                lock (sync)
                {
                    if (!active)
                    {
                        string armedName = armedGameName;
                        return armedName != null
                            ? Lang.F("st.armed", armedName) : Lang.T("st.mon");
                    }
                    long gone = gameGoneSinceTicks;
                    if (gone != 0)
                    {
                        int remain = ExitGraceSeconds
                            - (int)((DateTime.UtcNow.Ticks - gone) / TimeSpan.TicksPerSecond);
                        if (remain < 0) remain = 0;
                        return Lang.F("st.grace", activeGame, remain);
                    }
                    int n = core.ThrottledCountCached();
                    int b = boostStateVerified.Count;
                    string s = Lang.F("st.active", activeGame, n);
                    // 两段以前是直接拼的 出来是"已压制 28 个进程已提优 1" 中间没有断句
                    s += Lang.T("st.sep") + Lang.F("st.boost", b, Lang.T(planSwitch ? "st.hp" : "st.pr"));
                    return s;
                }
            }
        }

        public bool BoostStateVerified
        {
            get
            {
                lock (sync) return active && activeDetection != null
                    && boostStateVerified.Contains(activeDetection.RendererPid);
            }
        }

        public bool BoostHandleProtected
        {
            get
            {
                lock (sync) return active && activeDetection != null
                    && (boostHandleStripped.Contains(activeDetection.RendererPid)
                        || boostStateWarned.Contains(activeDetection.RendererPid));
            }
        }

        public string BoostStatusText
        {
            get
            {
                if (!enabled || !EffBoost) return Lang.T("v14.boost.disabled");
                lock (sync)
                {
                    if (!active) return Lang.T("v14.boost.wait");
                    if (activeDetection == null || activeDetection.RendererPid <= 0)
                        return Lang.T("v14.boost.no.renderer");
                    string name = activeDetection.RendererName ?? activeGame ?? "Game";
                    if (ProtectedGameRoster.Contains(activeDetection.RendererName))
                        return Lang.F("v14.boost.protected", name);
                    if (boostStateVerified.Contains(activeDetection.RendererPid))
                        return Lang.F("v14.boost.verified", name);
                    if (boostHandleStripped.Contains(activeDetection.RendererPid)
                        || boostStateWarned.Contains(activeDetection.RendererPid))
                        return Lang.F("v14.boost.protected", name);
                    return Lang.F("v14.boost.applying", name);
                }
            }
        }

        public void Start()
        {
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Start();
        }

        public void Stop()
        {
            stopping = true;
            kick.Set();
            if (worker != null) worker.Join(8000);
            RenderLane.ConfigureMutationBoundary(null, null);
            PowerBudgetYieldRunner.ConfigureMutationBoundary(null, null);
            VramShield.ConfigureMutationBoundary(null, null);
            SelfYield.ConfigureMutationBoundary(null, null);
            core.ConfigureMutationBoundary(null, null);
            IrqMutationBoundary.Configure(null, null);
            try { irqProbe.Dispose(); } catch { }
            // probe 已先停；即使 worker 超时，临时 proof hard pin 也不能
            // 把仍在运行的游戏留在强制亲和状态。
            try { RestoreAllIrqProofHardPins(); } catch { }
            // worker.Join 之后没有并发 Loop 收尾把可能仍开着的 present 会话关干净不泄漏
            try { PresentProbe p = presentProbe; presentProbe = null; if (p != null) p.Stop(); } catch { }
        }

        public void Poke() { RequestPolicyApply(); }

        private void Loop()
        {
            while (!stopping)
            {
                try
                {
                    if (panicReq)
                    {

                        int serving = Volatile.Read(ref panicSeq);
                        panicReq = false;
                        panicResult = Deactivate(Lang.T("t.gamemode.42"));
                        Volatile.Write(ref panicServed, serving);
                        panicDone.Set();
                        kick.WaitOne(4000);
                        continue;
                    }
                    TryDomainSwap();
                    if (!enabled)
                    {
                        bool residue;
                        lock (sync) residue = active || gameBoost.Count > 0;
                        if (residue || EnvActive() || core.AnyWith(SuppressReason.Background))
                            RetryDeactivate(Lang.T("t.gamemode.43"));
                    }
                    else
                    {
                        if (!ShouldRunProcessScan())
                        {
                            kick.WaitOne(ProcessScanWaitMs());
                            continue;
                        }
                        ProcessSnapshot all = null;
                        CountProcessScan();
                        try { all = ProcessSnapshotSource.Capture(selfSession); }
                        catch { all = null; }
                        if (all == null) RequeueProcessScanAfterFailure();
                        else
                        {
                                PruneWhitelistFamilyMembersIfDue(all);
                                HashSet<int> gamePids;
                                string running = FindRunningGame(all, out gamePids);
                                if (running != null)
                                {
                                    string runningProfileId;
                                    lock (sync)
                                        runningProfileId = activeDetection != null && activeDetection.Profile != null
                                            ? activeDetection.Profile.Id : null;
                                    if (gameGoneSinceTicks != 0)
                                    {
                                        gameGoneSinceTicks = 0;
                                        if (active)
                                        {
                                            Logger.Log(Lang.T("log.gamemode.44"));
                                            bool sameGraceProfile;
                                            string graceGame;
                                            lock (sync)
                                            {
                                                firstSweep = true;
                                                sameGraceProfile = SameReportedProfile(
                                                    repProfileId, runningProfileId);
                                                graceGame = repGame;
                                            }
                                            if (sameGraceProfile)
                                            {
                                                // Seal 的前缀尚未落盘。同一 profile 在宽限内
                                                // 恢复时丢弃它，并从新 renderer 证明重开干净
                                                // epoch；否则一次短暂漏检会把同一局拆成两条。
                                                irqProbe.Arm(graceGame ?? running, allMask);
                                                if (IrqSessionProbe.EnabledSetting)
                                                    StartPresentProbe();
                                            }
                                        }
                                    }
                                    if (!active)
                                    {
                                        lock (sync) { active = true; activeGame = running; firstSweep = true; }
                                        Logger.Log(Lang.T("log.gamemode.45") + running);
                                        Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
                                        Interlocked.Exchange(ref sessionStartTicks, DateTime.UtcNow.Ticks);
                                        overlayScanned = false;
                                        overlayExemptRoots = EmptyOverlayRoots;
                                        try { cpuLimit.Start(); } catch { }
                                        BeginSessionPolicy();
                                        ReportBegin(running);
                                        slowEnvAtTicks = DateTime.UtcNow
                                            .AddSeconds(SlowEnvDelaySeconds).Ticks;
                                    }
                                    else if (!SameReportedProfile(repProfileId, runningProfileId))
                                    {
                                        lock (sync) activeGame = running;
                                        Logger.Log(Lang.T("log.gamemode.46") + running);
                                        Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
                                        Interlocked.Exchange(ref sessionStartTicks, DateTime.UtcNow.Ticks);
                                        overlayScanned = false;
                                        overlayExemptRoots = EmptyOverlayRoots;
                                        // activeDetection 此时已经指向新 profile，旧 renderer 无法再终验。
                                        // 直接作废旧 IRQ epoch；并且必须先结旧局，再启用新策略。
                                        // 直接 A→B 时必须作废 A 的 live epoch；但 A 已在首次
                                        // 失联时 Seal 的前缀已有完整结束边界，应由紧接着的
                                        // ReportFinish 提交，不能再被 Invalidate 清掉。
                                        if (!irqProbe.HasSealedPending)
                                            irqProbe.InvalidateGameMask();
                                        ReportFinish();
                                        BeginSessionPolicy();
                                        ReportBegin(running);
                                    }
                                    else if (!string.Equals(activeGame, running, StringComparison.Ordinal))
                                    {
                                        // 同一 profile 局内改名只更新展示，不能伪造一次换局。
                                        lock (sync) activeGame = running;
                                    }
                                    ApplyEnv();
                                    string rendererPath;
                                    int rendererPid;
                                    long rendererCreation;
                                    lock (sync)
                                    {
                                        rendererPath = activeDetection != null
                                            ? activeDetection.RendererPath : null;
                                        rendererPid = activeDetection != null
                                            ? activeDetection.RendererPid : 0;
                                        rendererCreation = activeDetection != null
                                            ? activeDetection.RendererCreation : 0;
                                    }
                                    GpuThrottleProbe.SampleIfDue(rendererPath);
                                    // 显存溢出仍按整个家族测量 那是观测不是策略 多进程游戏的显存要合起来看
                                    VramSpillProbe.SampleIfDue(gamePids);
                                    // 护盾只认渲染进程本体 预留是按进程声明的 给家族其它成员挂没有意义
                                    VramShield.SampleIfDue(EffVramShield, rendererPid, rendererCreation);
                                    // 压制默认只认渲染进程本体 家族其余成员当普通后台
                                    //   游戏库页的家族豁免开关打开后才整族放行 家族集合在 Sweep 里
                                    //   还会拿本轮快照的父子关系补算一遍 免得子进程随检测周期忽压忽放
                                    if (EffSuppress) Sweep(all, gamePids);
                                    if (!EffSuppress) ReleaseBackground();
                                    SelfYield.Engage();
                                    // 电源滑块只认专注 掌机档不传真 那块的 PL 归厂商工具管 拨过去只会跟它顶
                                    MaybeActivatePowerOverlay(EffPreset == PerformancePreset.Competitive);
                                    // 功耗让路掌机档照样参与 方向本来就对 掌机 CPU 和集显抢的就是同一份预算
                                    //   掌机档放开的是纯省电项 EPP 仍写专注档的激进值 让路的前提还在
                                    PowerBudgetYieldRunner.Start(EffPowerYield,
                                        EffPreset == PerformancePreset.Competitive
                                            || EffPreset == PerformancePreset.Handheld);
                                    if (EffBoost) Boost(all);
                                    else
                                    {
                                        irqProbe.InvalidateGameMask();
                                        UnboostGames();
                                    }
                                    MaybeScanOverlays();
                                }
                                else if (active)
                                {
                                    long nowTicks = DateTime.UtcNow.Ticks;
                                    if (gameGoneSinceTicks == 0)
                                    {
                                        gameGoneSinceTicks = nowTicks;
                                        // 退出宽限只用于避免游戏检测抖动，不属于可验证的对局采样窗。
                                        // 首次失联立即封存最近一次落核证明对应的 epoch。
                                        try { irqProbe.Seal(); }
                                        catch { irqProbe.InvalidateGameMask(); }
                                        Logger.Log(Lang.T("log.gamemode.47")
                                            + ExitGraceSeconds + Lang.T("log.gamemode.48"));
                                        gracePreReleased += ReleaseBackground(Lang.T("t.gamemode.49"));
                                    }
                                    else if (nowTicks - gameGoneSinceTicks
                                        >= ExitGraceSeconds * TimeSpan.TicksPerSecond)
                                    {
                                        Deactivate(Lang.T("t.gamemode.50"));
                                    }
                                    if (gameGoneSinceTicks != 0)
                                        Interlocked.Exchange(ref transitionScanPending, 1);
                                }
                                else
                                {
                                    DropVanishedBoosts();
                                    bool boostResidue;
                                    lock (sync) boostResidue = gameBoost.Count > 0;
                                    if (boostResidue || EnvActive() || core.AnyWith(SuppressReason.Background))
                                        RetryDeactivate(Lang.T("t.gamemode.51"));
                                    TryAutoAddForegroundGame();
                                }
                        }
                    }
                }
                catch (Exception ex) { Logger.Log(Lang.T("log.gamemode.52") + ex.Message); }
                kick.WaitOne(enabled
                    ? ProcessScanWaitMs() : PollingSweepIntervalMs);
            }
            bool exitResidue;
            lock (sync) exitResidue = active || gameBoost.Count > 0;
            bool exitClean = true;
            if (exitResidue || core.AnyWith(SuppressReason.Background) || EnvActive())
                exitClean = Deactivate(Lang.T("t.gamemode.53"));
            if (panicReq)
            {
                int servingAtExit = Volatile.Read(ref panicSeq);
                panicReq = false;
                panicResult = exitClean;
                Volatile.Write(ref panicServed, servingAtExit);
                panicDone.Set();
            }
            try { GameDvr.Restore(); } catch { }
            try { Mmcss.Restore(); } catch { }
        }

        private string FindRunningGame(ProcessSnapshot all, out HashSet<int> gamePids)
        {
            List<GameProfile> copy;
            lock (sync)
            {
                copy = new List<GameProfile>();
                foreach (GameProfile p in profiles) copy.Add(p.Clone());
            }
            gamePids = new HashSet<int>();
            GameDetection hit;
            if (ShouldRunFullGameDetection())
            {
                string armedName;
                string armedVia;
                GameDetection raw = GameSessionDetector.Detect(
                    all, copy, selfSession, out armedName, out armedVia);
                if (raw != null && raw.RequiresGpuConfirm)
                    raw = ConfirmRendererByGpu(raw);
                hit = ApplyStickiness(raw);
                armedAwaitingElection = armedName != null && hit == null;
                UpdateArmedStatus(hit == null ? armedName : null, armedVia, hit != null);
                if (hit == null && armedName != null) PreStageDriverTuning(copy, armedName);
            }
            else
                hit = ApplyStickiness(null);
            if (hit == null) return null;
            // 渲染锚已证实不存在时，sticky 的一轮快速重扫只是
            // detector 内部缓冲，不能再当成一轮 running 去执行 Boost。
            // 交给主循环的 8 秒宽限处理，它会先 Seal 而不是丢局。
            if (stickyGraceOnly) return null;
            foreach (int pid in hit.FamilyPids) gamePids.Add(pid);
            if (irqProbe.HasSealedPending)
            {
                bool sameSealedProfile;
                string sealedGame;
                lock (sync)
                {
                    sameSealedProfile = SameReportedProfile(
                        repProfileId,
                        hit.Profile != null ? hit.Profile.Id : null);
                    sealedGame = repGame;
                }
                if (sameSealedProfile)
                {
                    // renderer 可能在上轮快照后、OpenProcess 前退出，
                    // 因而已 Seal 但尚未进入 gameGone 宽限。同 profile
                    // 新 renderer 出现时仍要丢弃旧前缀并重武装。
                    irqProbe.Arm(sealedGame ?? hit.Profile.Name, allMask);
                    if (IrqSessionProbe.EnabledSetting)
                        StartPresentProbe();
                }
            }
            if (irqProbe.IsCapturing)
            {
                bool proofStrict;
                ulong proofMask = EffectiveGameMask(sessionPolicy, out proofStrict);
                if (!irqProbe.ProofMatches(
                        proofMask, hit.RendererPid,
                        hit.RendererCreation))
                {
                    bool sameSessionProfile;
                    string sameSessionGame;
                    lock (sync)
                    {
                        sameSessionProfile = SameReportedProfile(
                            repProfileId,
                            hit.Profile != null ? hit.Profile.Id : null);
                        sameSessionGame = repGame;
                    }
                    irqProbe.InvalidateGameMask();
                    // 同一局从启动器换成真实 renderer：旧片段彻底丢弃，但允许新
                    // renderer 从零开始一个 epoch；不会把一局拆成两条台账记录。
                    if (sameSessionProfile)
                        irqProbe.Arm(sameSessionGame ?? hit.Profile.Name, allMask);
                }
            }
            lock (sync)
            {

                if (ShouldRearmLauncherTransition(
                        activeDetection, hit))
                {
                    transitionProbeRendererPid = 0;
                    transitionProbeRendererCreation = 0;
                }
                // 同一 profile 局内可从启动器更新为真实渲染器；换到另一个
                // profile 时不能在旧局 ReportFinish 前把它的 present 过滤 PID 覆盖掉。
                repRendererPid = UpdateSessionRendererPid(
                    repProfileId, repRendererPid,
                    hit.Profile != null ? hit.Profile.Id : null, hit.RendererPid);
                activeDetection = hit;
            }
            return hit.Profile.Name;
        }

        private const int GpuProbeCooldownMs = 8000;
        private long gpuProbeGateTicks;
        private bool gpuEvidenceWarned;

        private volatile string armedGameName;
        private string lastArmedLogged;

        public string ArmedGame
        {
            get { lock (sync) return active ? null : armedGameName; }
        }

        private volatile bool gpuPrefStageOn;
        private readonly CpuLimitProbe cpuLimit = new CpuLimitProbe();
        private long sessionStartTicks;
        private volatile bool overlayScanned;

        // 注入了游戏进程的覆盖层宿主安装根 对局内豁免后台压制
        //   压它们等于压游戏自己的渲染路径 游戏等一个零 CPU 的宿主回话就是偶发整秒卡顿
        private static readonly string[] EmptyOverlayRoots = new string[0];
        private volatile string[] overlayExemptRoots = EmptyOverlayRoots;

        public List<string> LibraryExecutablePaths()
        {
            var paths = new List<string>();
            lock (sync)
                foreach (GameProfile p in profiles)
                {
                    string path = p.PreferredExecutablePath;
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
            return paths;
        }

        private void MaybeScanOverlays()
        {
            if (overlayScanned) return;
            long start = Interlocked.Read(ref sessionStartTicks);
            if (start == 0 || DateTime.UtcNow.Ticks - start < 30L * TimeSpan.TicksPerSecond) return;
            int pid;
            string path;
            lock (sync)
            {
                if (!active || activeDetection == null) return;
                pid = activeDetection.RendererPid;
                path = activeDetection.RendererPath;
            }
            if (pid <= 0) return;
            overlayScanned = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string dir = null;
                    try { dir = string.IsNullOrEmpty(path) ? null : System.IO.Path.GetDirectoryName(path); }
                    catch { }
                    bool denied;
                    List<string> injectorPaths;
                    List<string> hits = OverlayScan.Scan(pid, dir, out denied, out injectorPaths);
                    if (denied) Logger.Log(Lang.T("log.overlay.1"));
                    else if (hits.Count > 0)
                    {
                        Logger.Log(Lang.T("log.overlay.2")
                            + string.Join(Lang.T("log.overlay.3"), hits.ToArray()));
                        var roots = new List<string>();
                        foreach (string module in injectorPaths)
                        {
                            string root = OverlayScan.ProductRootOf(module);
                            if (root != null && !roots.Contains(root)) roots.Add(root);
                        }
                        if (roots.Count > 0)
                        {
                            overlayExemptRoots = roots.ToArray();
                            Logger.Log(Lang.T("log.overlay.4")
                                + string.Join(Lang.T("log.overlay.3"), roots.ToArray()));
                        }
                    }
                }
                catch { }
            });
        }

        private volatile string preStagedNvPath;

        private void PreStageDriverTuning(List<GameProfile> copy, string armedName)
        {
            GameProfile armed = null;
            foreach (GameProfile p in copy)
                if (string.Equals(p.Name, armedName, StringComparison.OrdinalIgnoreCase))
                { armed = p; break; }
            if (armed == null) return;
            string path = armed.PreferredExecutablePath;
            if (string.IsNullOrEmpty(path)) return;
            if (string.Equals(preStagedNvPath, path, StringComparison.OrdinalIgnoreCase)) return;
            PolicySnapshot sp;
            try { sp = PolicyResolver.For(armed); } catch { return; }
            var plan = new NvGamePlan
            {
                MaxPerf = sp.NvMaxPerf,
                LowLatMode = sp.NvLowLatMode,
                SmoothMotion = sp.NvSmoothMotion,
                ShaderCacheMax = sp.NvShaderCacheMax,
                Rebar = sp.NvRebar,
                DlssMode = sp.NvDlssMode
            };
            bool stageGpuPref = gpuPrefStageOn && GpuPrefStage.Supported;
            if (plan.Empty && !stageGpuPref) return;
            string previous = preStagedNvPath;
            preStagedNvPath = path;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (IsActive) return;
                    IrqMutationBoundary.Run(delegate
                    {
                        // 外层检查与开局可能竞态；boundary 先阻止 Confirm，
                        // 内层再查一次，已经开局就完全不碰预热设置。
                        if (IsActive) return;
                        if (!string.IsNullOrEmpty(previous)
                            && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
                            NvDrsTweaks.RestoreAllGames();
                        if (stageGpuPref) GpuPrefStage.Stage(path);
                        if (plan.Empty) return;
                        bool retry;
                        NvDrsTweaks.ApplyForGame(path, plan, out retry);
                        if (retry) preStagedNvPath = null;
                    });
                }
                catch { }
            });
        }

        private void ReleasePreStagedTuning()
        {
            if (preStagedNvPath == null) return;
            preStagedNvPath = null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (IsActive) return;
                    IrqMutationBoundary.Run(delegate
                    {
                        if (IsActive) return;
                        NvDrsTweaks.RestoreAllGames();
                        GpuPrefStage.Restore();
                    });
                }
                catch { }
            });
        }

        private void UpdateArmedStatus(string name, string via, bool engaged)
        {
            armedGameName = name;
            if (string.Equals(name, lastArmedLogged, StringComparison.OrdinalIgnoreCase)) return;
            if (name != null)
                Logger.Log(Lang.T("log.gamemode.54") + name + Lang.T("log.gamemode.55")
                    + (string.IsNullOrEmpty(via) ? "" : Lang.T("log.gamemode.62") + via));
            else if (lastArmedLogged != null && !engaged)
            {
                Logger.Log(Lang.T("log.gamemode.56") + lastArmedLogged + Lang.T("log.gamemode.57"));
                ReleasePreStagedTuning();
            }
            lastArmedLogged = name;
        }

        private GameDetection ConfirmRendererByGpu(GameDetection pending)
        {
            if (pending == null || pending.RendererPid <= 0) return null;
            bool sessionActive;
            lock (sync) sessionActive = active;
            if (sessionActive || stopping || !enabled) return null;
            long now = DateTime.UtcNow.Ticks;
            if (now < gpuProbeGateTicks) return null;
            gpuProbeGateTicks = now + GpuProbeCooldownMs * TimeSpan.TicksPerMillisecond;
            Dictionary<int, double> util = GpuEvidence.Sample3D(
                GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs,
                delegate { return stopping || panicReq; });
            if (util == null)
            {
                if (!gpuEvidenceWarned)
                {
                    gpuEvidenceWarned = true;
                    Logger.Log(Lang.T("log.gamemode.58"));
                }
                return null;
            }
            double candidate;
            if (!util.TryGetValue(pending.RendererPid, out candidate)) candidate = 0;
            if (candidate < GpuEvidence.MinElectUtilization) return null;
            foreach (int pid in pending.FamilyPids)
            {
                double other;
                if (pid != pending.RendererPid
                    && util.TryGetValue(pid, out other) && other > candidate)
                    return null;
            }
            pending.RequiresGpuConfirm = false;
            pending.RendererCandidateSelected = true;
            pending.Evidence = Lang.F("detect.gpu", (int)candidate);
            Logger.Log(Lang.T("log.gamemode.59") + pending.Profile.Name + Lang.T("log.gamemode.60")
                + pending.RendererName + Lang.T("log.gamemode.61") + (int)candidate + "% pid " + pending.RendererPid + " ");
            return pending;
        }

    }
}
