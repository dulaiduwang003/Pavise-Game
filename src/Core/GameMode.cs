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

        private static readonly string[] PresetWhitelist =
        {

            "system", "secure system", "registry", "memory compression",
            "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
            "dwm", "fontdrvhost", "audiodg", "wudfhost", "wmiprvse",

            "explorer", "ctfmon", "textinputhost", "sihost", "taskhostw",
            "conhost", "dllhost", "runtimebroker", "applicationframehost",
            "shellexperiencehost", "startmenuexperiencehost", "searchhost",
            "lockapp", "logonui", "securityhealthsystray",

            "vmmem", "vmmemwsl", "wslservice"
        };

        private static readonly HashSet<string> PurgedPresetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "obs64", "obs32", "streamlabs obs", "xsplit.core", "livehime", "直播伴侣",
            "zoom", "teams", "ms-teams", "wemeetapp", "dingtalk"
        };

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
        private volatile bool notifQuiet;
        private volatile bool trimWs;
        private volatile bool gpuHighPerf;
        private volatile bool disableFso;
        private volatile bool nvMaxPerf;
        private volatile bool nvLowLatency;
        private volatile string nvFrlMode = "off";
        private volatile bool nvAnselOff;
        private volatile bool nvRebarOn;
        private volatile string nvDlssMode = "off";
        private volatile bool nvBattFull;
        private volatile bool nvBgFrlOn;
        private bool nvbgActive;
        private volatile bool amdAntiLagOn;
        private volatile string amdChillMode = "off";
        private volatile bool amdEnhSyncOn;
        private volatile bool amdRisOn;
        private bool alagActive;
        private bool chillActive;
        private bool esyncActive;
        private bool risActive;
        private volatile bool presenceQosOn;
        private volatile bool awakeOn;
        private bool pqosActive;
        private bool awakeActive;
        private bool overlayActive;
        private volatile bool killGameDvr;
        private volatile bool hzGuard;
        private volatile bool planSwitch;
        private volatile bool idleDisableOn;
        private volatile bool visualFxOn;
        private volatile bool standbySweepOn;
        private volatile bool pauseUpdateOn;
        private volatile bool strictCoreOn;
        private volatile bool aggressiveOn;
        private volatile bool freezeOn;
        private volatile bool ifeoOn;
        private volatile bool renderLaneOn;
        private volatile bool gpuDemoteOn;
        private volatile bool panicReq;
        private int panicSeq;
        private int panicServed;
        private volatile bool panicResult;
        private readonly ManualResetEvent panicDone = new ManualResetEvent(true);
        private bool svcActive;
        private bool dvrActive;
        private bool timerRaised;
        private bool timerSkipLogged;
        private bool notifActive;
        private bool doActive;
        private bool hzActive;
        private readonly ulong throttleMask;
        private readonly ulong allMask;
        private readonly ulong gameMask;
        private readonly ulong strictMask;
        private readonly BackgroundPressureController pressure = new BackgroundPressureController();
        private readonly FreezeDwellTracker freezeDwell = new FreezeDwellTracker();
        private PerformancePreset preset;
        private GameDetection activeDetection;
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
                Logger.Log("CPU 拓扑：混合架构，性能核 0x" + CpuTopology.PerfMask.ToString("X")
                    + "，能效核 0x" + CpuTopology.EffMask.ToString("X")
                    + "；后台压 0x" + throttleMask.ToString("X")
                    + "，严格分区绑 0x" + strictMask.ToString("X"));
            else if (CpuTopology.AsymCache)
                Logger.Log("CPU 拓扑：非对称 L3（X3D），游戏绑大缓存 CCD 0x" + gameMask.ToString("X") + "，后台压另一 CCD 0x" + throttleMask.ToString("X"));
            else
                Logger.Log("CPU 拓扑：同构，游戏不绑核");
            if (CpuTopology.CpuSetPartitionRejected)
                Logger.Log("CPU 拓扑：CPU Set 分区与性能核判定矛盾，已弃用该分区");
            if (CpuTopology.StrictMaskUnsafe)
                Logger.Log("CPU 拓扑：绑核目标未通过校验，已退回不限核");
            bgSuppressOn = Settings.Load("GmSuppress", true);
            boostOn = Settings.Load("GmBoost", true);
            pauseDlOn = Settings.Load("GmPauseDl", true);
            svcPauseOn = Settings.Load("GmSvcPause", false);
            notifQuiet = Settings.Load("NotifQuiet", false);
            idleDisableOn = Settings.Load("GmIdleDisable", true);
            visualFxOn = Settings.Load("GmVisualFx", false);
            standbySweepOn = Settings.Load("GmStandbySweep", false);
            pauseUpdateOn = Settings.Load("GmPauseUpdate", false);
            trimWs = Settings.Load("TrimWS", false);
            gpuHighPerf = Settings.Load("GpuHighPerf", true);
            disableFso = Settings.Load("DisableFso", false);
            nvMaxPerf = Settings.Load("NvMaxPerf", false);
            nvLowLatency = Settings.Load("NvLowLatency", false);
            nvFrlMode = Settings.LoadStr("NvFrl", "off");
            nvAnselOff = Settings.Load("NvAnselOff", false);
            nvRebarOn = Settings.Load("NvRebar", false);
            nvDlssMode = Settings.LoadStr("NvDlss", "off");
            nvBattFull = Settings.Load("NvBattFull", false);
            nvBgFrlOn = Settings.Load("NvBgFrl", false);
            amdAntiLagOn = Settings.Load("AmdAntiLag", false);
            amdChillMode = Settings.LoadStr("AmdChill", "off");
            amdEnhSyncOn = Settings.Load("AmdEnhSync", false);
            amdRisOn = Settings.Load("AmdRis", false);
            presenceQosOn = Settings.Load("GmPresenceQos", true);
            awakeOn = Settings.Load("GmAwake", true);
            killGameDvr = Settings.Load("GameDvrOff", true);
            hzGuard = Settings.Load("HzGuardOn", false);
            planSwitch = Settings.Load("PowerPlanOn", true);
            strictCoreOn = Settings.Load("GmStrictCores", false);
            aggressiveOn = Settings.Load("GmAggressive", false);
            freezeOn = Settings.Load("GmFreeze", false);
            ifeoOn = Settings.Load("GmIfeoBoost", false);
            renderLaneOn = Settings.Load("GmRenderLane", false);
            gpuDemoteOn = Settings.Load("GmGpuDemote", false);
            foreach (string envKey in EnvKeys)
                if (Settings.Load("EnvFuse_" + envKey, false)) envFused.Add(envKey);
            SuppressionCore.GpuDemoteEnabled = gpuDemoteOn;
            int presetRaw;
            preset = int.TryParse(Settings.LoadStr("PerformancePreset", "0"), out presetRaw) && presetRaw >= 0 && presetRaw <= 2
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

                foreach (string entry in PresetWhitelist)
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
                        && PurgedPresetNames.Contains(r.Value));
                    if (purged > 0)
                    {
                        rewriteFormat = true;
                        Logger.Log("白名单清理：移除旧版本预置的 " + purged + " 条第三方豁免（预设收敛为系统核心，需要的例外请自行重新添加）");
                    }
                    Settings.Save("WhitelistPurge1Done", true);
                }
                foreach (WhitelistRule rule in loadedRules) AddWhiteRuleNoSave(rule);
                if (rewriteFormat && !SaveWhite(loadedRules))
                    Logger.Log("白名单旧格式迁移失败；本次规则已安全载入，下次将重试");
                MigrateWhitelistHeader();
            }
            catch (Exception ex)
            {

                foreach (string entry in PresetWhitelist) AddWhiteNoSave(entry);
                Logger.Log("白名单加载失败，本次运行改用预置白名单（用户自定义项本次不生效）：" + ex.Message);
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
                if (profileStore.ClearedLegacyLibrary)
                {
                    games.Clear();
                    gameRoots.Clear();
                    SaveGames();
                }
                else if (profiles.Count > 0) RebuildLegacyGameIndex();
            }
            catch { }
        }

        private bool WritePreset()
        {
            var lines = new List<string>();
            lines.Add("# Pavise 智能守护白名单——这些进程始终不进入后台控制");
            lines.Add("# V3 规则支持进程名、精确路径和应用家族，并带完整性尾标防止截断。");
            lines.Add("# Windows 核心、其它会话和 Pavise 自身受安全保护；其余例外只来自本白名单。");
            lines.Add("# Steam / Epic / EA / 育碧 / 战网 / GOG / R星 / Riot / WeGame / Xbox / HoYoPlay 等");
            lines.Add("# 游戏平台的客户端家族由程序内置豁免（按各平台安装目录校验），无需在此列出。");
            lines.Add(WhitelistRule.Header);
            var rules = new List<WhitelistRule>();
            foreach (string entry in PresetWhitelist)
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

#if PAVISE_SELFTEST
        internal void SimulateActiveForTest(bool value) { lock (sync) active = value; }
#endif

        public string ActiveGame { get { lock (sync) return active ? activeGame : null; } }

        public PerformancePreset Preset
        {
            get { lock (sync) return preset; }
            set
            {
                if ((int)value < 0 || (int)value > 2) value = PerformancePreset.Standard;
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
                        lines[i] = "# Windows 核心、其它会话和 Pavise 自身受安全保护；其余例外只来自本白名单。";
                        changed = true;
                    }
                    else if (lines[i].Contains("压制会扫全部会话、不因会话 0 而豁免"))
                    {
                        lines[i] = "# 仅处理当前用户会话；系统关键项仍建议保留，用户可追加自己的明确例外。";
                        changed = true;
                    }
                }
                if (changed) AtomicFile.WriteLines(whitePath, lines, "白名单表头迁移");
            }
            catch { }
        }

        public PerformancePreset ActivePreset
        {
            get { lock (sync) return preset; }
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

        public bool SuppressBackground
        {
            get { return bgSuppressOn; }
            set { bgSuppressOn = value; Settings.Save("GmSuppress", value); RequestPolicyApply(); }
        }

        public bool BoostGame
        {
            get { return boostOn; }
            set { boostOn = value; Settings.Save("GmBoost", value); RequestPolicyApply(); }
        }

        public bool StrictCoreIsolation
        {
            get { return strictCoreOn; }
            set { strictCoreOn = value; Settings.Save("GmStrictCores", value); RequestPolicyApply(); }
        }

        public bool AggressiveSuppression
        {
            get { return aggressiveOn; }
            set { aggressiveOn = value; Settings.Save("GmAggressive", value); RequestPolicyApply(); }
        }

        public bool GpuDemote
        {
            get { return gpuDemoteOn; }
            set { gpuDemoteOn = value; SuppressionCore.GpuDemoteEnabled = value; Settings.Save("GmGpuDemote", value); RequestPolicyApply(); }
        }

        public bool FreezeBackground
        {
            get { return freezeOn; }
            set { freezeOn = value; Settings.Save("GmFreeze", value); RequestPolicyApply(); }
        }

        public bool RenderLaneOn
        {
            get { return renderLaneOn; }
            set
            {
                renderLaneOn = value; Settings.Save("GmRenderLane", value);
                if (!value) RenderLane.Release();
                RequestPolicyApply();
            }
        }

        public bool IfeoBoostFallback
        {
            get { return ifeoOn; }
            set
            {
                ifeoOn = value; Settings.Save("GmIfeoBoost", value);
                if (!value) IfeoBoost.RestoreAll();
                RequestPolicyApply();
            }
        }

        public bool IdleStateDisable
        {
            get { return idleDisableOn; }
            set { idleDisableOn = value; Settings.Save("GmIdleDisable", value); RequestPolicyApply(); }
        }

        public bool VisualFxDowngrade
        {
            get { return visualFxOn; }
            set { visualFxOn = value; Settings.Save("GmVisualFx", value); if (value) ClearEnvFuse("fx"); RequestPolicyApply(); }
        }

        public bool PurgeStandby
        {
            get { return standbySweepOn; }
            set { standbySweepOn = value; Settings.Save("GmStandbySweep", value); }
        }

        public bool PauseWindowsUpdate
        {
            get { return pauseUpdateOn; }
            set { pauseUpdateOn = value; Settings.Save("GmPauseUpdate", value); if (value) ClearEnvFuse("wu"); RequestPolicyApply(); }
        }

        public bool PauseDownloads
        {
            get { return pauseDlOn; }
            set { pauseDlOn = value; Settings.Save("GmPauseDl", value); if (value) ClearEnvFuse("do"); RequestPolicyApply(); }
        }

        public bool PauseSvcIndex
        {
            get { return svcPauseOn; }
            set { svcPauseOn = value; Settings.Save("GmSvcPause", value); if (value) ClearEnvFuse("svc"); RequestPolicyApply(); }
        }

        public bool NotifQuiet
        {
            get { return notifQuiet; }
            set { notifQuiet = value; Settings.Save("NotifQuiet", value); if (value) ClearEnvFuse("notif"); RequestPolicyApply(); }
        }

        public bool TrimWorkingSet
        {
            get { return trimWs; }
            set { trimWs = value; Settings.Save("TrimWS", value); RequestPolicyApply(); }
        }

        public bool GpuHighPerf
        {
            get { return gpuHighPerf; }
            set
            {
                gpuHighPerf = value; Settings.Save("GpuHighPerf", value);
                if (!value) GameExeTweaks.RestoreKind("gpu");
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool NvMaxPerf
        {
            get { return nvMaxPerf; }
            set
            {
                nvMaxPerf = value; Settings.Save("NvMaxPerf", value);
                if (!value) NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyPState);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPState, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool NvLowLatency
        {
            get { return nvLowLatency; }
            set
            {
                nvLowLatency = value; Settings.Save("NvLowLatency", value);
                if (!value)
                {
                    NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyPreRender);
                    NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyLowLatCpl);
                }
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyPreRender, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool NvAnselOff
        {
            get { return nvAnselOff; }
            set
            {
                nvAnselOff = value; Settings.Save("NvAnselOff", value);
                if (!value) NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyAnsel);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyAnsel, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool NvRebar
        {
            get { return nvRebarOn; }
            set
            {
                nvRebarOn = value; Settings.Save("NvRebar", value);
                if (!value) NvDrsTweaks.RestoreKinds(NvDrsTweaks.RebarKeys);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyRebarFeat, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public string NvDlssMode
        {
            get { return nvDlssMode; }
            set
            {
                string mode = value == "latest" || value == "j" || value == "k" ? value : "off";
                nvDlssMode = mode; Settings.SaveStr("NvDlss", mode);
                if (mode == "off") NvDrsTweaks.RestoreKinds(NvDrsTweaks.DlssKeys);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyDlssOvr, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool NvBattFull
        {
            get { return nvBattFull; }
            set
            {
                nvBattFull = value; Settings.Save("NvBattFull", value);
                if (!value) NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyBattFps);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyBattFps, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool NvBgFrl
        {
            get { return nvBgFrlOn; }
            set
            {
                nvBgFrlOn = value; Settings.Save("NvBgFrl", value);
                if (value) ClearEnvFuse("nvbg");
                RequestPolicyApply();
            }
        }

        public bool PresenceQosOff
        {
            get { return presenceQosOn; }
            set
            {
                presenceQosOn = value; Settings.Save("GmPresenceQos", value);
                if (value) ClearEnvFuse("pqos");
                RequestPolicyApply();
            }
        }

        public bool KeepAwake
        {
            get { return awakeOn; }
            set
            {
                awakeOn = value; Settings.Save("GmAwake", value);
                if (value) ClearEnvFuse("awake");
                RequestPolicyApply();
            }
        }

        public bool AmdAntiLag
        {
            get { return amdAntiLagOn; }
            set
            {
                amdAntiLagOn = value; Settings.Save("AmdAntiLag", value);
                if (value) ClearEnvFuse("alag");
                RequestPolicyApply();
            }
        }

        public string AmdChillMode
        {
            get { return amdChillMode; }
            set
            {
                string mode = value == "60" || value == "120" || value == "240" || value == "screen" ? value : "off";
                amdChillMode = mode; Settings.SaveStr("AmdChill", mode);
                if (mode != "off") ClearEnvFuse("chill");
                RequestPolicyApply();
            }
        }

        public bool AmdEnhSync
        {
            get { return amdEnhSyncOn; }
            set
            {
                amdEnhSyncOn = value; Settings.Save("AmdEnhSync", value);
                if (value) ClearEnvFuse("esync");
                RequestPolicyApply();
            }
        }

        public bool AmdRis
        {
            get { return amdRisOn; }
            set
            {
                amdRisOn = value; Settings.Save("AmdRis", value);
                if (value) ClearEnvFuse("ris");
                RequestPolicyApply();
            }
        }

        public string NvFrlMode
        {
            get { return nvFrlMode; }
            set
            {
                string mode = value == "60" || value == "120" || value == "240" || value == "screen" ? value : "off";
                nvFrlMode = mode; Settings.SaveStr("NvFrl", mode);
                if (mode == "off") NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyFrl);
                else SaveCounter("NvFailStreak_" + NvDrsTweaks.KeyFrl, 0);
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool DisableFso
        {
            get { return disableFso; }
            set
            {
                disableFso = value; Settings.Save("DisableFso", value);
                if (!value) GameExeTweaks.RestoreKind("fso");
                lock (sync) tweakApplied.Clear();
                RequestPolicyApply();
            }
        }

        public bool KillGameDvr
        {
            get { return killGameDvr; }
            set { killGameDvr = value; Settings.Save("GameDvrOff", value); if (value) ClearEnvFuse("dvr"); RequestPolicyApply(); }
        }

        public bool HzGuard
        {
            get { return hzGuard; }
            set { hzGuard = value; Settings.Save("HzGuardOn", value); if (value) ClearEnvFuse("hz"); RequestPolicyApply(); }
        }

        public bool PowerPlanSwitch
        {
            get { return planSwitch; }
            set
            {
                planSwitch = value; Settings.Save("PowerPlanOn", value);
                if (value) { SaveCounter(PowerFailStreakKey, 0); ClearEnvFuse("overlay"); }
                RequestPolicyApply();
            }
        }

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
                    int n = core.CountThrottled(SuppressReason.Background);
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

        public bool BoostStateFailed
        {
            get
            {
                lock (sync) return active && activeDetection != null
                    && boostStateWarned.Contains(activeDetection.RendererPid)
                    && !boostHandleStripped.Contains(activeDetection.RendererPid);
            }
        }

        public bool BoostHandleProtected
        {
            get
            {
                lock (sync) return active && activeDetection != null
                    && boostHandleStripped.Contains(activeDetection.RendererPid);
            }
        }

        public string BoostStatusText
        {
            get
            {
                if (!enabled || !boostOn) return Lang.T("v14.boost.disabled");
                lock (sync)
                {
                    if (!active) return Lang.T("v14.boost.wait");
                    if (activeDetection == null || activeDetection.RendererPid <= 0)
                        return Lang.T("v14.boost.no.renderer");
                    string name = activeDetection.RendererName ?? activeGame ?? "Game";
                    if (boostStateVerified.Contains(activeDetection.RendererPid))
                        return Lang.F("v14.boost.verified", name);
                    if (boostHandleStripped.Contains(activeDetection.RendererPid))
                        return Lang.F("v14.boost.protected", name);
                    if (boostStateWarned.Contains(activeDetection.RendererPid))
                        return Lang.F("v14.boost.failed", name);
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
                    if (!enabled)
                    {
                        bool residue;
                        lock (sync) residue = active || gameBoost.Count > 0;
                        if (residue || EnvActive() || core.AnyWith(SuppressReason.Background))
                            Deactivate("手动关闭游戏模式");
                    }
                    else
                    {
                        if (!ShouldRunProcessScan())
                        {
                            kick.WaitOne(ProcessScanWaitMs());
                            continue;
                        }
                        Process[] all = null;
                        CountProcessScan();
                        try { all = Process.GetProcesses(); }
                        catch { RequeueProcessScanAfterFailure(); }
                        if (all != null)
                        {
                            try
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
                                            Logger.Log("游戏在宽限期内重新出现（启动器换壳/快速重启），游戏模式保持不中断，后台重新静默接管");
                                            lock (sync) firstSweep = true;
                                        }
                                    }
                                    if (!active)
                                    {
                                        lock (sync) { active = true; activeGame = running; firstSweep = true; }
                                        Logger.Log("游戏模式激活：检测到 " + running);
                                        ReportBegin(running);
                                    }
                                    else if (!string.Equals(activeGame, running, StringComparison.OrdinalIgnoreCase))
                                    {
                                        lock (sync) activeGame = running;
                                        Logger.Log("游戏模式：检测目标变更 → " + running);
                                        ReportFinish();
                                        ReportBegin(running);
                                    }
                                    ApplyEnv();
                                    GpuThrottleProbe.SampleIfDue();
                                    if (bgSuppressOn) Sweep(all, gamePids);
                                    if (!bgSuppressOn) ReleaseBackground();
                                    if (boostOn) Boost(all);
                                    else UnboostGames();
                                }
                                else if (active)
                                {
                                    long nowTicks = DateTime.UtcNow.Ticks;
                                    if (gameGoneSinceTicks == 0)
                                    {
                                        gameGoneSinceTicks = nowTicks;
                                        Logger.Log("游戏进程消失：后台压制立即还原，电源/环境保留 "
                                            + ExitGraceSeconds + " 秒宽限（防换壳/快速重启抖动）");
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
                                        Deactivate("残留恢复重试");
                                    TryAutoAddForegroundGame();
                                }
                            }
                            finally { foreach (Process p in all) p.Dispose(); }
                        }
                    }
                }
                catch (Exception ex) { Logger.Log("游戏模式异常: " + ex.Message); }
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

        private string FindRunningGame(Process[] all, out HashSet<int> gamePids)
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
                Logger.Log("待命：《" + name + "》的家族进程在运行；客户端/大厅阶段零介入，"
                    + "出现对局画面（前台全屏或 GPU 3D 主导）后自动升格接管");
            else if (lastArmedLogged != null && !engaged)
                Logger.Log("待命解除：《" + lastArmedLogged + "》的家族进程已全部退出");
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
                    Logger.Log("GPU 证据不可用（PDH GPU Engine 计数器读取失败），窗口化候选将等待全屏几何证据或用户直选命中");
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
            Logger.Log("渲染进程选举：GPU 证据确认《" + pending.Profile.Name + "》的真身是 "
                + pending.RendererName + "（3D 引擎 " + (int)candidate + "%，pid " + pending.RendererPid + "）");
            return pending;
        }

        private GameDetection ApplyStickiness(GameDetection hit)
        {
            if (hit != null)
            {
                stickyMiss = 0;
                if (stickyDetection != null && stickyDetection.Profile != null && hit.Profile != null
                    && string.Equals(stickyDetection.Profile.Id, hit.Profile.Id, StringComparison.OrdinalIgnoreCase)
                    && stickyDetection.RendererPid > 0 && AliveWithIdentity(stickyDetection.RendererPid))
                {
                    bool freshMayReplace = false;
                    if (hit.RendererPid > 0 && hit.RendererPid != stickyDetection.RendererPid)
                    {
                        GameId anchorId; string nm; long cr;
                        if (stickyIds.TryGetValue(stickyDetection.RendererPid, out anchorId)
                            && TryIdentity(hit.RendererPid, out nm, out cr)
                            && FreshRendererMayReplaceSticky(
                                hit, nm, cr,
                                anchorId.Creation))
                            freshMayReplace = true;
                    }
                    if (hit.RendererPid
                            != stickyDetection.RendererPid
                        && !freshMayReplace)
                    {
                        List<int> retainedFamily =
                            LiveStickyFamily();
                        if (!ReanchorToStickyInstance(
                                hit, stickyDetection,
                                retainedFamily))
                        {

                            ClearSticky();
                        }
                    }
                }
                RememberSticky(hit);
                foreach (int pid in LiveStickyFamily()) hit.FamilyPids.Add(pid);
                return hit;
            }
            if (stickyDetection != null && stickyDetection.RendererPid > 0
                && AliveWithIdentity(stickyDetection.RendererPid))
            {
                stickyMiss = 0;
                var r = new GameDetection
                {
                    Profile = stickyDetection.Profile,
                    RendererPid = stickyDetection.RendererPid,
                    RendererCreation =
                        stickyDetection.RendererCreation,
                    RendererName = stickyDetection.RendererName,
                    RendererPath = stickyDetection.RendererPath,
                    RendererForeground =
                        stickyDetection.RendererForeground,
                    RendererCandidateSelected = true,
                    RendererUserSelected = stickyDetection.RendererUserSelected,
                    RendererLearnable = stickyDetection.RendererLearnable,
                    Evidence = stickyDetection.Evidence
                };
                r.FamilyPids.Add(stickyDetection.RendererPid);
                foreach (int pid in LiveStickyFamily()) r.FamilyPids.Add(pid);
                foreach (string n in stickyDetection.FamilyNames) r.FamilyNames.Add(n);
                stickyDetection = r;
                return r;
            }
            if (stickyDetection != null && stickyMiss < StickyGraceMisses && !AnyStickyReused())
            {
                stickyMiss++;

                RequestFullGameDetection();
                try { kick.Set(); } catch { }
                return CloneWithAnchoredFamily(stickyDetection);
            }
            ClearSticky();
            return null;
        }

        internal static bool FreshRendererMayReplaceSticky(
            GameDetection fresh, string verifiedName,
            long verifiedCreation, long stickyCreation)
        {
            if (fresh == null || fresh.RendererPid <= 0
                || string.IsNullOrEmpty(fresh.RendererName)
                || string.IsNullOrEmpty(verifiedName)
                || verifiedCreation <= 0 || stickyCreation <= 0
                || !string.Equals(
                    verifiedName, fresh.RendererName,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (fresh.RendererForeground
                && !GameSessionDetector.IsLauncherLikeName(
                    fresh.RendererName))
                return true;
            return verifiedCreation > stickyCreation;
        }

        internal static bool ReanchorToStickyInstance(
            GameDetection fresh, GameDetection sticky,
            IList<int> verifiedStickyPids)
        {
            if (fresh == null || sticky == null
                || sticky.RendererPid <= 0
                || verifiedStickyPids == null)
                return false;
            bool rendererVerified = false;
            foreach (int pid in verifiedStickyPids)
                if (pid == sticky.RendererPid)
                {
                    rendererVerified = true;
                    break;
                }
            if (!rendererVerified) return false;

            fresh.RendererPid = sticky.RendererPid;
            fresh.RendererCreation = sticky.RendererCreation;
            fresh.RendererName = sticky.RendererName;
            fresh.RendererPath = sticky.RendererPath;
            fresh.RendererForeground =
                sticky.RendererForeground;
            fresh.RendererCandidateSelected = true;
            fresh.RendererUserSelected =
                sticky.RendererUserSelected;
            fresh.Evidence = sticky.Evidence;
            fresh.FamilyPids.Clear();
            foreach (int pid in verifiedStickyPids)
                if (pid > 0) fresh.FamilyPids.Add(pid);
            fresh.FamilyNames.Clear();
            foreach (string name in sticky.FamilyNames)
                fresh.FamilyNames.Add(name);
            return true;
        }

        private GameDetection CloneWithAnchoredFamily(GameDetection src)
        {
            var g = new GameDetection
            {
                Profile = src.Profile,
                RendererPid = src.RendererPid,
                RendererCreation = src.RendererCreation,
                RendererName = src.RendererName,
                RendererPath = src.RendererPath,
                RendererForeground =
                    src.RendererForeground,
                RendererCandidateSelected = src.RendererCandidateSelected,
                RendererUserSelected = src.RendererUserSelected,
                RendererLearnable = src.RendererLearnable,
                Evidence = src.Evidence
            };
            foreach (int pid in src.FamilyPids) if (stickyIds.ContainsKey(pid)) g.FamilyPids.Add(pid);
            foreach (string n in src.FamilyNames) g.FamilyNames.Add(n);
            return g;
        }

        private void RememberSticky(GameDetection hit)
        {
            if (hit == null) { ClearSticky(); return; }
            if (!hit.RendererUserSelected && GameSessionDetector.IsLauncherLikeName(hit.RendererName)) { ClearSticky(); return; }
            var fresh = new Dictionary<int, GameId>();
            foreach (int pid in hit.FamilyPids)
            {
                string nm; long cr;
                if (TryIdentity(pid, out nm, out cr)) fresh[pid] = new GameId { Name = nm, Creation = cr };
            }
            if (hit.RendererPid > 0 && !fresh.ContainsKey(hit.RendererPid))
            {
                string nm; long cr;
                if (TryIdentity(hit.RendererPid, out nm, out cr)) fresh[hit.RendererPid] = new GameId { Name = nm, Creation = cr };
            }
            GameId rendererIdentity;
            if (fresh.TryGetValue(
                    hit.RendererPid, out rendererIdentity))
                hit.RendererCreation = rendererIdentity.Creation;
            if (hit.RendererPid > 0 && !fresh.ContainsKey(hit.RendererPid))
            {
                int anchorPid = stickyDetection != null ? stickyDetection.RendererPid : 0;
                List<int> gone = null;
                foreach (var kv in stickyIds)
                {
                    if (fresh.ContainsKey(kv.Key) || kv.Key == hit.RendererPid || kv.Key == anchorPid) continue;
                    if (AliveWithIdentity(kv.Key)) continue;
                    if (gone == null) gone = new List<int>();
                    gone.Add(kv.Key);
                }
                if (gone != null) foreach (int dead in gone) stickyIds.Remove(dead);
                foreach (var kv in fresh) stickyIds[kv.Key] = kv.Value;
                return;
            }
            stickyIds.Clear();
            foreach (var kv in fresh) stickyIds[kv.Key] = kv.Value;
            stickyDetection = hit;
        }

        private List<int> LiveStickyFamily()
        {
            var l = new List<int>();
            foreach (var kv in stickyIds) if (AliveWithIdentity(kv.Key)) l.Add(kv.Key);
            return l;
        }

        private void ClearSticky()
        {
            stickyDetection = null;
            stickyIds.Clear();
            stickyMiss = 0;
        }

        private bool AliveWithIdentity(int pid)
        {
            GameId id;
            if (!stickyIds.TryGetValue(pid, out id)) return false;
            string nm; long cr;
            return TryIdentity(pid, out nm, out cr) && cr == id.Creation
                && string.Equals(nm, id.Name, StringComparison.OrdinalIgnoreCase);
        }

        private bool AnyStickyReused()
        {
            foreach (var kv in stickyIds)
            {
                string nm; long cr;
                if (TryIdentity(kv.Key, out nm, out cr)
                    && (cr != kv.Value.Creation
                        || !string.Equals(nm, kv.Value.Name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        private static bool TryIdentity(int pid, out string name, out long creation)
        {
            name = null; creation = 0;
            if (pid <= 0) return false;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                name = Native.ImageName(h);
                long cpu; ulong io;
                return Native.QueryProcessSample(h, out creation, out cpu, out io) && !string.IsNullOrEmpty(name);
            }
            finally { Native.CloseHandle(h); }
        }

    }
}
