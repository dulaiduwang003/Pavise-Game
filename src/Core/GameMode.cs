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

        private readonly object sync = new object();
        private readonly List<string> games = new List<string>();
        private readonly Dictionary<string, string> gameRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<GameProfile> profiles = new List<GameProfile>();
        private readonly GameProfileStore profileStore;
        private readonly string dataDir;
        private readonly AutoResetEvent kick = new AutoResetEvent(true);
        private readonly string gamesPath;
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
        private const int BoostRetryMax = 3;
        private const int PlacementRetryMax = 3;
        private volatile bool bgSuppressOn;
        private volatile bool boostOn;
        private volatile bool pauseDlOn;
        private volatile bool svcPauseOn;
        private volatile bool svcYieldOn;
        private volatile bool wlanGuardOn;
        private volatile bool nvMaxPerf;
        private volatile string nvLowLatMode = "off";
        private volatile bool nvSmoothMotion;
        private volatile bool nvShaderCacheMax;
        private volatile bool amdAntiLag;
        private volatile bool amdAfmf;
        private volatile string amdFrlMode = "off";
        private volatile string nvFrlMode = "off";
        private volatile bool nvAnselOff;
        private volatile bool nvRebarOn;
        private volatile string nvDlssMode = "off";
        private volatile bool nvBattFull;
        private volatile bool awakeOn;
        private bool pqosActive;
        private bool awakeActive;
        private bool overlayActive;
        private volatile bool killGameDvr;
        private volatile bool planSwitch;
        private volatile bool standbySweepOn;
        private volatile bool squeezeBgOn;
        private long slowEnvAtTicks;

        internal const int SlowEnvDelaySeconds = 20;
        private volatile bool pauseUpdateOn;
        private volatile bool corePartitionOn;
        private volatile bool coreDomainAltOn;
        private volatile bool aggressiveOn;
        private volatile bool ifeoOn;
        private volatile bool renderLaneOn;
        private volatile bool uploadYieldOn;
        private long uploadYieldAtTicks;
        private bool uploadYieldDone;
        private long uplinkSampleTicks;
        private long uplinkSampleBytes;
        private volatile bool gpuDemoteOn;
        private volatile bool panicReq;
        private int panicSeq;
        private readonly object panicCallGate = new object();
        private int panicServed;
        private volatile bool panicResult;
        private readonly ManualResetEvent panicDone = new ManualResetEvent(true);
        private bool svcActive;
        private bool svcYieldActive;
        private bool wlanActive;
        private bool dvrActive;
        private bool timerRaised;
        private bool timerSkipLogged;
        private bool doActive;
        private ulong throttleMask;
        private readonly ulong allMask;
        private readonly ulong gameMask;
        private ulong strictMask;
        private readonly BackgroundPressureController pressure = new BackgroundPressureController();
        private readonly CpuSaturation cpuSaturation = new CpuSaturation();
        private uint boostPriorityTarget = Native.HIGH_PRIORITY_CLASS;
        private PerformancePreset preset;
        private GameDetection activeDetection;
        private volatile PolicySnapshot sessionPolicy;
        private Thread worker;

        private struct GameId { public string Name; public long Creation; }
        private GameDetection stickyDetection;
        private readonly Dictionary<int, GameId> stickyIds =
            new Dictionary<int, GameId>();
        private int stickyMiss;
        private const int StickyGraceMisses = 1;

        private const int ExitGraceSeconds = 8;
        private int gracePreReleased;
        private long gameGoneSinceTicks;

        public GameMode(string dir, SuppressionCore core)
        {
            dataDir = dir;
            gamesPath = Path.Combine(dir, "Pavise.games.txt");
            whitePath = Path.Combine(dir, "Pavise.whitelist.txt");
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
                Logger.Log("处理器 混合架构 大核 " + CpuTopology.DescribeMask(CpuTopology.PerfMask)
                    + " 小核 " + CpuTopology.DescribeMask(CpuTopology.EffMask)
                    + " 后台去 " + CpuTopology.DescribeMask(throttleMask)
                    + " 选只用大核时给 " + CpuTopology.DescribeMask(strictMask));
            else if (CpuTopology.AsymCache)
                Logger.Log("处理器 带 3D 缓存 默认全核 选单 CCD 时给大缓存那块 "
                    + CpuTopology.DescribeMask(strictMask)
                    + " 后台去 " + CpuTopology.DescribeMask(throttleMask));
            else if (CpuTopology.PartitionTag == "symmetric-ccd")
                Logger.Log("处理器 多 CCD 默认全核 选单 CCD 时给 "
                    + CpuTopology.DescribeMask(strictMask)
                    + " 后台去 " + CpuTopology.DescribeMask(throttleMask));
            else
                Logger.Log("处理器 单一架构 游戏不限核");
            if (CpuTopology.AltDomainActive)
                Logger.Log("游戏核心范围 已换到另一块 游戏 " + CpuTopology.DescribeMask(strictMask)
                    + " 后台 " + CpuTopology.DescribeMask(throttleMask));
            if (CpuTopology.CpuSetPartitionRejected)
                Logger.Log("处理器 分区与大小核判定矛盾 已弃用该分区");
            if (CpuTopology.StrictMaskUnsafe)
                Logger.Log("CPU 拓扑 绑核目标未通过校验 已退回不限核");
            bgSuppressOn = Settings.Load("GmSuppress", true);
            boostOn = Settings.Load("GmBoost", true);
            pauseDlOn = Settings.Load("GmPauseDl", true);
            svcPauseOn = Settings.Load("GmSvcPause", false);
            svcYieldOn = Settings.Load("GmSvcYield", false);
            wlanGuardOn = Settings.Load("GmWlanGuard", false);
            VersionMigrations.EnsureSettingsMigrated();
            LoadCustomCoreMask();
            standbySweepOn = Settings.Load("GmStandbySweep", false);
            squeezeBgOn = Settings.Load("GmSqueezeBg", true);
            SuppressionCore.SqueezeBackground = squeezeBgOn;
            pauseUpdateOn = Settings.Load("GmPauseUpdate", false);
            nvMaxPerf = Settings.Load("NvMaxPerf", false);
            nvLowLatMode = Settings.LoadStr("NvLowLat", "off");
            nvSmoothMotion = Settings.Load("NvSmoothMotion", false);
            nvShaderCacheMax = Settings.Load("NvShaderCache", false);
            amdAntiLag = Settings.Load("AmdAntiLag", false);
            amdAfmf = Settings.Load("AmdAfmf", false);
            amdFrlMode = Settings.LoadStr("AmdFrl", "off");
            nvFrlMode = Settings.LoadStr("NvFrl", "off");
            nvAnselOff = Settings.Load("NvAnselOff", false);
            nvRebarOn = Settings.Load("NvRebar", false);
            nvDlssMode = Settings.LoadStr("NvDlss", "off");
            nvBattFull = Settings.Load("NvBattFull", false);
            awakeOn = Settings.Load("GmAwake", true);
            killGameDvr = Settings.Load("GameDvrOff", true);
            planSwitch = Settings.Load("PowerPlanOn", true);
            corePartitionOn = Settings.Load("GmStrictCores", false);
            coreDomainAltOn = Settings.Load("GmCoreDomainAlt", false);
            aggressiveOn = Settings.Load("GmAggressive", false);
            ifeoOn = Settings.Load("GmIfeoBoost", false);
            renderLaneOn = Settings.Load("GmRenderLane", true);
            uploadYieldOn = Settings.Load("GmUploadYield", false);
            gpuDemoteOn = Settings.Load("GmGpuDemote", false);
            SuppressionCore.GpuDemoteEnabled = gpuDemoteOn;
            foreach (string envKey in EnvKeys)
                if (Settings.Load("EnvFuse_" + envKey, false)) envFused.Add(envKey);
            int presetRaw;
            preset = int.TryParse(Settings.LoadStr("PerformancePreset", "0"), out presetRaw) && presetRaw >= 0 && presetRaw <= 3
                ? (PerformancePreset)presetRaw : PerformancePreset.Standard;

            try
            {
                if (!File.Exists(whitePath) && !WritePreset())
                    throw new IOException("无法创建默认白名单");
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
                            throw new InvalidDataException("白名单版本头位置无效");
                        versioned = true;
                        legacyVersion = string.Equals(
                            t, WhitelistRule.LegacyHeader, StringComparison.Ordinal);
                        continue;
                    }
                    if (t.StartsWith(WhitelistFooterPrefix, StringComparison.Ordinal))
                    {
                        if (!versioned || sawFooter)
                            throw new InvalidDataException("白名单完整性尾标位置无效");
                        sawFooter = true;
                        footer = t;
                        continue;
                    }
                    if (sawFooter)
                        throw new InvalidDataException("白名单完整性尾标后仍有规则");

                    WhitelistRule rule;
                    if (versioned)
                    {
                        if (!WhitelistRule.TryParseVersioned(t, out rule))
                            throw new InvalidDataException("白名单 V2 规则格式无效");
                    }
                    else
                    {

                        if (t.Length > 1 && t[1] == '|'
                            && (t[0] == 'N' || t[0] == 'n' || t[0] == 'P'
                                || t[0] == 'p' || t[0] == 'F' || t[0] == 'f'))
                            throw new InvalidDataException("白名单缺少 V2 版本头");
                        if (!WhitelistRule.TryCreate(
                            WhitelistRuleKind.LegacyName, t, out rule))
                            throw new InvalidDataException("旧版白名单规则无效");
                    }
                    sawRule = true;
                    if (!loadedKeys.Add(rule.Key))
                        throw new InvalidDataException("白名单含重复规则");
                    loadedRules.Add(rule);
                }
                if (versioned && sawFooter)
                {
                    if (!string.Equals(
                        footer, BuildWhitelistFooter(loadedRules),
                        StringComparison.Ordinal))
                        throw new InvalidDataException("白名单完整性校验失败");
                    rewriteFormat = legacyVersion;
                }
                else if (versioned && !legacyVersion)
                {
                    throw new InvalidDataException("白名单 V3 缺少完整性尾标");
                }
                else if (versioned)
                {

                    if (!sawRule)
                        throw new InvalidDataException("白名单缺少完整性尾标");
                    rewriteFormat = true;
                }
                else if (!sawRule)
                    throw new InvalidDataException("白名单为空且没有 V2 版本头");
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
                if (!Settings.Load("WhitelistPurge1Done", false))
                {
                    int purged = loadedRules.RemoveAll(r => r.Kind == WhitelistRuleKind.LegacyName
                        && SystemProcessCatalog.PurgedPresetNames.Contains(r.Value));
                    if (purged > 0)
                    {
                        rewriteFormat = true;
                        Logger.Log("白名单清理 移除旧版本预置的 " + purged + " 条第三方豁免 预设收敛为系统核心 需要的例外请自行重新添加");
                    }
                    Settings.Save("WhitelistPurge1Done", true);
                }
                foreach (WhitelistRule rule in loadedRules) AddWhiteRuleNoSave(rule);
                if (rewriteFormat && !SaveWhite(loadedRules))
                    Logger.Log("白名单旧格式迁移失败 本次规则已安全载入 下次将重试");
                MigrateWhitelistHeader();
            }
            catch (Exception ex)
            {

                foreach (string entry in SystemProcessCatalog.PresetWhitelist) AddWhiteNoSave(entry);
                Logger.Log("白名单加载失败 本次运行改用预置白名单 用户自定义项本次不生效 " + ex.Message);
            }

            try
            {
                if (!File.Exists(gamesPath))
                    File.WriteAllLines(gamesPath, new string[0]);
                foreach (string line in File.ReadAllLines(gamesPath))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    string name, root;
                    if (!TryParseGameLine(t, out name, out root)) continue;
                    bool exists = false;
                    foreach (string g in games)
                        if (string.Equals(g, name, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                    if (!exists) games.Add(name);
                    if (root != null) gameRoots[name] = root;
                }
            }
            catch { }

            try
            {
                profiles.AddRange(profileStore.LoadOrMigrate(gamesPath));
                if (profiles.Count > 0) RebuildLegacyGameIndex();
            }
            catch { }
        }

        private bool WritePreset()
        {
            var lines = new List<string>();
            lines.Add("# Pavise 智能守护白名单 这些进程始终不进入后台控制");
            lines.Add("# V3 规则支持进程名 精确路径和应用家族 并带完整性尾标防止截断");
            lines.Add("# Windows 核心 其它会话和 Pavise 自身受安全保护 其余例外只来自本白名单");
            lines.Add("# Steam Epic EA 育碧 战网 GOG R星 Riot WeGame Xbox HoYoPlay 等");
            lines.Add("# 游戏平台的客户端家族由程序内置豁免 按各平台安装目录校验 无需在此列出");
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
            return AtomicFile.WriteLines(whitePath, lines.ToArray(), "白名单预置");
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
                if (changed && value) RequestFullGameDetection();
                if (changed && value) RequestPolicyApply();
                else kick.Set();
            }
        }

        public bool IsActive { get { lock (sync) return active; } }

        public string ActiveGame { get { lock (sync) return active ? activeGame : null; } }

        public PerformancePreset Preset
        {
            get { lock (sync) return preset; }
            set
            {
                if ((int)value < 0 || (int)value > 3) value = PerformancePreset.Standard;
                lock (sync) preset = value;
                Settings.SaveStr("PerformancePreset", ((int)value).ToString());
                RequestPolicyApply();
            }
        }

        private void MigrateWhitelistHeader()
        {
            try
            {
                string[] lines = File.ReadAllLines(whitePath);
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("Windows、前台、音频/直播、驱动、游戏家族和反作弊"))
                    {
                        lines[i] = "# Windows 核心 其它会话和 Pavise 自身受安全保护 其余例外只来自本白名单";
                        changed = true;
                    }
                    else if (lines[i].Contains("压制会扫全部会话、不因会话 0 而豁免"))
                    {
                        lines[i] = "# 仅处理当前用户会话 系统关键项仍建议保留 用户可追加自己的明确例外";
                        changed = true;
                    }
                }
                if (changed) AtomicFile.WriteLines(whitePath, lines, "白名单表头迁移");
            }
            catch { }
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

        private bool ExtremeNow { get { return ActivePreset == PerformancePreset.Extreme; } }

        private bool EffSuppress
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffSuppress : (bgSuppressOn || ExtremeNow);
            }
        }

        private bool EffBoost
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffBoost : (boostOn || ExtremeNow);
            }
        }

        private bool EffIfeo
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffIfeo : (ifeoOn || ExtremeNow);
            }
        }

        private bool EffLane
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.EffLane : (renderLaneOn || ExtremeNow);
            }
        }

        private bool EffUploadYield
        {
            get
            {
                PolicySnapshot s = sessionPolicy;
                return s != null ? s.UploadYield : uploadYieldOn;
            }
        }

        private void BeginSessionPolicy()
        {
            GameProfile source;
            lock (sync) source = activeDetection != null ? activeDetection.Profile : null;
            PolicySnapshot snap = source != null ? PolicyResolver.For(source) : PolicyResolver.Global();
            sessionPolicy = snap;
            if (!snap.IsGlobal)
                Logger.Log("本局使用 " + snap.ProfileName + " 的独立配置 覆盖 "
                    + snap.OverrideCount + " 项 对局中的修改下局生效");
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
                Logger.Log("独立配置 " + snap.ProfileName + " 本局不用自定义核心 走默认分区");
                return;
            }
            if (CpuTopology.SetCustomMask(wanted))
                Logger.Log("独立配置 " + snap.ProfileName + " 本局核心 "
                    + CpuTopology.DescribeMask(CpuTopology.CustomMask));
            else Logger.Log("独立配置 " + snap.ProfileName + " 存的核心集合在本机不可用 保持全局设置");
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
                Logger.Log("独立配置 " + snap.ProfileName + " 本局核心范围换到另一块 游戏 "
                    + CpuTopology.DescribeMask(strictMask) + " 后台 " + CpuTopology.DescribeMask(throttleMask));
            else
                Logger.Log("核心范围回到全局设置 游戏 "
                    + CpuTopology.DescribeMask(strictMask) + " 后台 " + CpuTopology.DescribeMask(throttleMask));
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

        public bool ProfileStoreReadOnly { get { return profileStore.LoadFailed; } }

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
                    s += Lang.F("st.boost", b, Lang.T(planSwitch ? "st.hp" : "st.pr"));
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
                    if (boostStateVerified.Contains(activeDetection.RendererPid))
                        return Lang.F("v14.boost.verified", name);
                    if (boostHandleStripped.Contains(activeDetection.RendererPid)
                        || boostStateWarned.Contains(activeDetection.RendererPid))
                        return Lang.F(EffIfeo ? "v14.boost.protected.ifeo" : "v14.boost.protected", name);
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
                        panicResult = Deactivate("紧急恢复");
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
                            RetryDeactivate("手动关闭游戏模式");
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
                                    if (gameGoneSinceTicks != 0)
                                    {
                                        gameGoneSinceTicks = 0;
                                        if (active)
                                        {
                                            Logger.Log("游戏在宽限期内重新出现 启动器换壳或快速重启 游戏模式保持不中断 后台重新静默接管");
                                            lock (sync) firstSweep = true;
                                        }
                                    }
                                    if (!active)
                                    {
                                        lock (sync) { active = true; activeGame = running; firstSweep = true; }
                                        Logger.Log("游戏模式激活 检测到 " + running);
                                        BeginSessionPolicy();
                                        ReportBegin(running);
                                        uploadYieldDone = false;
                                        uploadYieldAtTicks = DateTime.UtcNow
                                            .AddSeconds(UplinkGate.WarmupSeconds).Ticks;
                                        uplinkSampleTicks = 0;
                                        uplinkSampleBytes = 0;
                                        slowEnvAtTicks = DateTime.UtcNow
                                            .AddSeconds(SlowEnvDelaySeconds).Ticks;
                                    }
                                    else if (!string.Equals(activeGame, running, StringComparison.OrdinalIgnoreCase))
                                    {
                                        lock (sync) activeGame = running;
                                        Logger.Log("游戏模式 检测目标变更 " + running);
                                        BeginSessionPolicy();
                                        ReportFinish();
                                        ReportBegin(running);
                                    }
                                    ApplyEnv();
                                    GpuThrottleProbe.SampleIfDue();
                                    VramSpillProbe.SampleIfDue(gamePids);
                                    if (EffSuppress) Sweep(all, gamePids);
                                    if (!EffSuppress) ReleaseBackground();
                                    if (EffBoost) Boost(all);
                                    else UnboostGames();
                                    UploadYieldTick(all);
                                }
                                else if (active)
                                {
                                    long nowTicks = DateTime.UtcNow.Ticks;
                                    if (gameGoneSinceTicks == 0)
                                    {
                                        gameGoneSinceTicks = nowTicks;
                                        Logger.Log("游戏进程消失 后台压制立即还原 电源和环境保留 "
                                            + ExitGraceSeconds + " 秒宽限 防换壳和快速重启抖动");
                                        gracePreReleased += ReleaseBackground("游戏退出宽限");
                                    }
                                    else if (nowTicks - gameGoneSinceTicks
                                        >= ExitGraceSeconds * TimeSpan.TicksPerSecond)
                                    {
                                        Deactivate("游戏已退出");
                                    }
                                    if (gameGoneSinceTicks != 0)
                                        Interlocked.Exchange(ref transitionScanPending, 1);
                                }
                                else
                                {
                                    bool boostResidue;
                                    lock (sync) boostResidue = gameBoost.Count > 0;
                                    if (boostResidue || EnvActive() || core.AnyWith(SuppressReason.Background))
                                        RetryDeactivate("残留恢复重试");
                                }
                        }
                    }
                }
                catch (Exception ex) { Logger.Log("游戏模式异常 " + ex.Message); }
                kick.WaitOne(enabled
                    ? ProcessScanWaitMs() : PollingSweepIntervalMs);
            }
            bool exitResidue;
            lock (sync) exitResidue = active || gameBoost.Count > 0;
            bool exitClean = true;
            if (exitResidue || core.AnyWith(SuppressReason.Background) || EnvActive())
                exitClean = Deactivate("Pavise 退出");
            if (panicReq)
            {
                int servingAtExit = Volatile.Read(ref panicSeq);
                panicReq = false;
                panicResult = exitClean;
                Volatile.Write(ref panicServed, servingAtExit);
                panicDone.Set();
            }
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
                GameDetection raw = GameSessionDetector.Detect(
                    all, copy, selfSession, out armedName);
                if (raw != null && raw.RequiresGpuConfirm)
                    raw = ConfirmRendererByGpu(raw);
                hit = ApplyStickiness(raw);
                armedAwaitingElection = armedName != null && hit == null;
                UpdateArmedStatus(hit == null ? armedName : null, hit != null);
            }
            else
                hit = ApplyStickiness(null);
            if (hit == null) return null;
            foreach (int pid in hit.FamilyPids) gamePids.Add(pid);
            lock (sync)
            {

                if (ShouldRearmLauncherTransition(
                        activeDetection, hit))
                {
                    transitionProbeRendererPid = 0;
                    transitionProbeRendererCreation = 0;
                }
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

        private void UpdateArmedStatus(string name, bool engaged)
        {
            armedGameName = name;
            if (string.Equals(name, lastArmedLogged, StringComparison.OrdinalIgnoreCase)) return;
            if (name != null)
                Logger.Log("待命 " + name + " 已启动 进对局后接管");
            else if (lastArmedLogged != null && !engaged)
                Logger.Log("待命解除 " + lastArmedLogged + " 已退出");
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
                    Logger.Log("GPU 证据不可用 PDH GPU Engine 计数器读取失败 窗口化候选将等待全屏几何证据或用户直选命中");
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
            Logger.Log("渲染进程选举 GPU 证据确认 " + pending.Profile.Name + " 的真身是 "
                + pending.RendererName + " 3D 引擎 " + (int)candidate + "% pid " + pending.RendererPid + " ");
            return pending;
        }

        private void UploadYieldTick(ProcessSnapshot all)
        {
            if (!EffUploadYield || uploadYieldDone || all == null) return;
            long now = DateTime.UtcNow.Ticks;
            if (now < uploadYieldAtTicks) return;

            long bytes = UplinkGate.TotalBytesSent();
            if (uplinkSampleTicks == 0)
            {
                uplinkSampleTicks = now;
                uplinkSampleBytes = bytes;
                return;
            }
            double seconds = (now - uplinkSampleTicks) / (double)TimeSpan.TicksPerSecond;
            double mbps;
            bool engage = UplinkGate.ShouldEngage(bytes - uplinkSampleBytes, seconds, out mbps);
            if (seconds >= UplinkGate.SampleGapSeconds)
            {
                uplinkSampleTicks = now;
                uplinkSampleBytes = bytes;
            }
            if (!engage) return;

            var names = new List<string>();
            foreach (int pid in core.PidsWith(SuppressReason.Background))
            {
                ProcEntry entry = all.Find(pid);
                if (entry != null && !string.IsNullOrEmpty(entry.Name)) names.Add(entry.Name);
            }
            if (names.Count == 0) return;
            uploadYieldDone = true;
            Logger.Log("上传让位 后台上行 " + mbps.ToString("F1") + " Mbps 超过闸门 "
                + UplinkGate.BusyMbps.ToString("F1") + " Mbps 开始限速");
            UploadYield.Apply(names);
        }

    }
}
