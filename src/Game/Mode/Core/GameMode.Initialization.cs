using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // 已删除功能留在设置里的开关值和旧记录 启动时清掉 不碰系统里已经写下的值
        internal static readonly string[] RemovedFeatureSettings = new string[]
        {
            "GmIntelEnduranceV1", "IntelEnduranceSnap", "EnvFuse_intelend",
            "NvVrrWindowed", "NvVrrModeSnap", "EnvFuse_nvvrr",
            "AutoGameMitigationsV1", "AutoOpenGlThreadsV1",
            "GmMmcss", "GmMmcssDefaultOffV1", "EnvFuse_mmcss", "Mmcss_Resp", "Mmcss_Sched", "Mmcss_NoLazy",
            "AmdSamByPavise", "AmdSamPreferenceV2",
            "PrevHwSch", "HagsOnByPavise",
            "EnvStorageIdleV1", "EnvStorageIdleV1State", "EnvStorageIdleV1Boot",
            "EnvStorageRuntimeV1", "EnvStorageRuntimeV1State", "EnvStorageRuntimeV1Boot",
            "EnvStorageD3ColdV1", "EnvStorageD3ColdV1State", "EnvStorageD3ColdV1Boot",
            "GmEnglishInputV1",
            "ConfigurationPresetIdentityV1",
            "NvSmoothMotion",
            "GmCacheWarmV2",
            "NvLowLat", "GameLowLatencyOn", "NvUllUnsupDrv", "NvFailStreak_prerender", "ActionFailure_Migrated_NvFailStreak_prerender",
            "DirectXOwned_SwapEffectUpgradeEnable", "DirectXOwned_VRROptimizeEnable",
            "PrivacySetting_EnvPrivacyBackgroundGlobalV1", "PrivacySetting_EnvPrivacyBackgroundGlobalV1Phase"
        };

        public GameMode(string dir, SuppressionCore core)
        {
            // 组件最先创建 下面任何代码都可能用到它们
            laptopPerformance = new LaptopPerformanceCoordinator(this);
            rendererObservationTrace = new BackgroundLogQueue(8);
            laboratoryCoordinator = new LaboratoryCoordinator(this);
            nvDriverRetry = new NvDriverRetryCoordinator(this);
            powerMaintenance = new PowerMaintenance(this);
            displayAwakeLog = new DisplayAwakeLogThrottle();
            exeLaunchSettings = new ExeLaunchSettingCoordinator(this);
            automationLog = new AutomationLogDedup();
            modeLockGame = new ModeLockGameIdentity(this);
            autoAdd = new AutoAddCoordinator(this);
            rendererProbes = new RendererProbeCoordinator(this);
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
                Logger.Log(() => Lang.T("log.gamemode.1") + CpuTopology.DescribeMask(CpuTopology.PerfMask) + Lang.T("log.gamemode.2") + CpuTopology.DescribeMask(CpuTopology.EffMask) + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(throttleMask) + Lang.T("log.gamemode.3") + CpuTopology.DescribeMask(strictBoostMask));
            }
            else if (CpuTopology.AsymCache)
            {
                Logger.Log(() => Lang.T("log.gamemode.4") + CpuTopology.DescribeMask(strictBoostMask) + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(throttleMask));
            }
            else if (CpuTopology.PartitionTag == "symmetric-ccd")
            {
                Logger.Log(() => Lang.T("log.gamemode.5") + CpuTopology.DescribeMask(strictBoostMask) + Lang.T("log.gamemodesettings.7") + CpuTopology.DescribeMask(throttleMask));
            }
            else
            {
                Logger.Log(() => Lang.T("log.gamemode.6"));
            }
            if (CpuTopology.FavoredMask != 0)
            {
                Logger.Log(() => Lang.T("log.gamemode.favored") + CpuTopology.DescribeMask(CpuTopology.FavoredMask) + Lang.T("log.gamemode.favored.detail") + CpuTopology.FavoredDetail);
            }
            if (!CpuTopology.TopologySourcesAgree)
            {
                Logger.Warn(string.Format(Lang.T("log.gamemode.sourcesplit"), CpuTopology.TopologySourceCounts));
            }
            if (CpuTopology.AllMaskReconciled)
            {
                Logger.Warn(string.Format(Lang.T("log.gamemode.countsplit"), Environment.ProcessorCount, CpuTopology.CountSetBits(CpuTopology.AllMask)));
            }
            if (CpuTopology.CpuSetPartitionRejected)
            {
                Logger.Log(() => Lang.T("log.gamemode.8"));
            }
            if (CpuTopology.StrictMaskUnsafe)
            {
                Logger.Log(() => Lang.T("log.gamemode.9"));
            }
            int result;
            exitGraceSeconds = ((int.TryParse(Settings.LoadStr("GameExitGraceSeconds", "8"), out result) && IsExitGraceChoice(result)) ? result : 8);
            bgSuppressOn = Settings.Load("GmSuppress", true);
            // 性能优先级没有全局开关 本局策略算出来之前按默认开处理
            boostOn = true;
            pauseUpdateOn = Settings.Load("GmPauseUpdate", false);
            pauseServicesOn = false;
            Settings.Remove("GmPauseServices");
            InitializeStandbyCleaner();
            nvMaxPerf = Settings.Load("NvMaxPerf", false);
            nvShaderCacheMax = Settings.Load("NvShaderCache", false);
            nvRebarOn = Settings.Load("NvRebar", false);
            nvDlssMode = Settings.LoadStr("NvDlss", "off");
            // 桌面合成调度已下线 DWM 的 MMCSS 请求只随本进程存活 不留系统残留 只需清除旧开关
            Settings.Remove("GmDwmBoost");
            // 独显预选已下线 开关和旧台账一并清除 旧版写入的显卡偏好保持现值 不再恢复
            Settings.Remove("GpuPrefStageOn");
            Settings.Remove("GpuPrefStage");
            Settings.Remove("GmWsTrimV1");
            Settings.Remove("GmIdlePolicyV1");
            // 计时器节拍已下线 开关值不再有意义 启动配置的恢复只看 Pavise 自己的记录
            Settings.Remove("AutoTimerTick");
            // 时间片 最后访问时间 短文件名已下线 只按恢复记录还原 开关值和时间片档位一并清掉
            Settings.Remove(PerformanceActionCatalog.Quantum);
            Settings.Remove("EnvQuantumSelectedProfileV1");
            Settings.Remove(PerformanceActionCatalog.LastAccess);
            Settings.Remove(PerformanceActionCatalog.ShortNames);
            Settings.Remove("AutoBackgroundCoreLayoutV1");
            Settings.Remove("AutoLaptopPerformanceV1");
            Settings.Remove("GmLaptopPerfV1");
            Settings.Remove("MatchPausePcaV1");
            PrivacyControl.ClearRetiredPreferences();
            Settings.Remove("GmAudioLowLatV1");
            // 实验室音频低缓冲已删除 不留系统状态 只清掉开关值
            Settings.Remove("LabAudioLowBufferV1");
            // 禁止 CPU 空闲这一档已去掉 存过这个值的按跟随原策略处理 恢复由电源日志负责
            if (Settings.LoadStr(LaboratoryPower.IdleKey, "") == "disabled") Settings.Remove(LaboratoryPower.IdleKey);
            amdAfmf = Settings.Load("AmdAfmf", false);
            rsrOn = Settings.Load("GmRsr", false);
            autoAddOn = Settings.Load("GmAutoAdd", false);
            Settings.Remove("GmCacheWarm");
            killGameDvr = Settings.Load("GameDvrOff", true);
            foreach (string name in RemovedFeatureSettings)
            {
                Settings.Remove(name);
            }
            string[] retiredSettings = AutoCores.RetiredSettings;
            foreach (string name in retiredSettings)
            {
                Settings.Remove(name);
            }
            planSwitch = PolicyResolver.Global().PowerPlanOn;
            renderLaneOn = PolicyResolver.Global().RenderLane;
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
                bool headerRead = false;
                bool legacyWhitelist = false;
                bool retiredServiceFile = false;
                int retiredServiceRules = 0;
                bool needsWhitelistUpgrade = true;
                bool malformedWhitelist = false;
                bool whitelistReadFailed = false;
                bool rewriteWhitelist = false;
                string a = null;
                List<WhitelistRule> list = new List<WhitelistRule>();
                List<string> footerLines = new List<string>();
                HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] allLines = File.ReadAllLines(whitePath);
                foreach (string entryText in allLines)
                {
                    string entryTextTrimmed = entryText.Trim();
                    if (entryTextTrimmed.Length == 0 || entryTextTrimmed.StartsWith("#"))
                    {
                        continue;
                    }
                    if (string.Equals(entryTextTrimmed, WhitelistRule.RetiredServiceHeader, StringComparison.Ordinal)
                        || string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V4", StringComparison.Ordinal) || string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V3", StringComparison.Ordinal) || string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V2", StringComparison.Ordinal))
                    {
                        if (headerRead || malformedWhitelist || whitelistReadFailed)
                        {
                            throw new InvalidDataException(Lang.T("t.gamemode.11"));
                        }
                        headerRead = true;
                        retiredServiceFile = string.Equals(entryTextTrimmed, WhitelistRule.RetiredServiceHeader, StringComparison.Ordinal);
                        needsWhitelistUpgrade = !string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V4", StringComparison.Ordinal);
                        legacyWhitelist = string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V2", StringComparison.Ordinal);
                        continue;
                    }
                    if (entryTextTrimmed.StartsWith("PAVISE_WHITELIST_END|", StringComparison.Ordinal))
                    {
                        if (!headerRead || whitelistReadFailed)
                        {
                            throw new InvalidDataException(Lang.T("t.gamemode.12"));
                        }
                        whitelistReadFailed = true;
                        a = entryTextTrimmed;
                        continue;
                    }
                    if (whitelistReadFailed)
                    {
                        throw new InvalidDataException(Lang.T("t.gamemode.13"));
                    }
                    WhitelistRule rule2;
                    if (headerRead)
                    {
                        // 已移除的服务规则只参与文件尾核对 不加载
                        string retiredService;
                        if (retiredServiceFile && WhitelistRule.TryReadRetiredService(entryTextTrimmed, out retiredService))
                        {
                            footerLines.Add(retiredService);
                            retiredServiceRules++;
                            malformedWhitelist = true;
                            continue;
                        }
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
                    malformedWhitelist = true;
                    if (!hashSet.Add(rule2.Key))
                    {
                        throw new InvalidDataException(Lang.T("t.gamemode.17"));
                    }
                    list.Add(rule2);
                    footerLines.Add(rule2.Serialize());
                }
                if (headerRead && whitelistReadFailed)
                {
                    if (!string.Equals(a, WhitelistFooterOf(footerLines), StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(Lang.T("t.gamemode.18"));
                    }
                    rewriteWhitelist = legacyWhitelist;
                }
                else
                {
                    if (headerRead && !legacyWhitelist)
                    {
                        throw new InvalidDataException(Lang.T("t.gamemode.19"));
                    }
                    if (headerRead)
                    {
                        if (!malformedWhitelist)
                        {
                            throw new InvalidDataException(Lang.T("t.gamemode.20"));
                        }
                        rewriteWhitelist = true;
                    }
                    else
                    {
                        if (!malformedWhitelist)
                        {
                            throw new InvalidDataException(Lang.T("t.gamemode.21"));
                        }
                        rewriteWhitelist = true;
                    }
                }
                if (needsWhitelistUpgrade)
                {
                    hashSet.Clear();
                    foreach (WhitelistRule item in list)
                    {
                        hashSet.Add(item.Key);
                    }
                    rewriteWhitelist = true;
                }
                if (retiredServiceRules > 0)
                {
                    Logger.Log(() => Logger.Format("服务白名单已移除：丢弃旧的服务规则 {0} 条，其余白名单不变", "Service whitelist removed: discarded {0} old service rules, the rest of the whitelist is unchanged", retiredServiceRules));
                }
                string[] presetWhitelist = SystemProcessCatalog.PresetWhitelist;
                foreach (string value in presetWhitelist)
                {
                    WhitelistRule rule3;
                    if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, value, out rule3) && hashSet.Add(rule3.Key))
                    {
                        list.Add(rule3);
                        rewriteWhitelist = true;
                    }
                }
                foreach (WhitelistRule item2 in list)
                {
                    AddWhiteRuleNoSave(item2);
                }
                if (rewriteWhitelist && !SaveWhite(list))
                {
                    Logger.Log(() => Lang.T("log.gamemode.24"));
                }
            }
            catch (Exception ex)
            {
                string[] presetWhitelist2 = SystemProcessCatalog.PresetWhitelist;
                foreach (string n in presetWhitelist2)
                {
                    AddWhiteNoSave(n);
                }
                Logger.Log(() => Lang.T("log.gamemode.25") + ex.Message);
            }
            try
            {
                profiles.AddRange(profileStore.LoadProfiles(!libraryIgnoreTransaction.RecoveryPending));
                List<GameProfile> list2 = GetProfiles();
                if (!ProfileStoreSaveFailed && !libraryIgnoreTransaction.RecoveryPending)
                {
                    // 读档时读到过旧的渲染进程记录 安装目录没有变化也要保存一次
                    bool rootsRefreshed = RefreshLibraryInstallRoots(list2);
                    if ((rootsRefreshed || profileStore.ReadLegacyRendererRecords) && SaveProfileSnapshotLocked(list2))
                    {
                        profiles.Clear();
                        profiles.AddRange(list2);
                    }
                }
            }
            catch
            {
            }
            InitializeRendererObservations();
        }

    }
}
