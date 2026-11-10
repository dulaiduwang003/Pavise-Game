using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {

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
                Logger.Log(Lang.T("log.gamemode.1", 0) + CpuTopology.DescribeMask(CpuTopology.PerfMask) + Lang.T("log.gamemode.2", 0) + CpuTopology.DescribeMask(CpuTopology.EffMask) + Lang.T("log.gamemodesettings.7", 0) + CpuTopology.DescribeMask(throttleMask) + Lang.T("log.gamemode.3", 0) + CpuTopology.DescribeMask(strictBoostMask));
            }
            else if (CpuTopology.AsymCache)
            {
                Logger.Log(Lang.T("log.gamemode.4", 0) + CpuTopology.DescribeMask(strictBoostMask) + Lang.T("log.gamemodesettings.7", 0) + CpuTopology.DescribeMask(throttleMask));
            }
            else if (CpuTopology.PartitionTag == "symmetric-ccd")
            {
                Logger.Log(Lang.T("log.gamemode.5", 0) + CpuTopology.DescribeMask(strictBoostMask) + Lang.T("log.gamemodesettings.7", 0) + CpuTopology.DescribeMask(throttleMask));
            }
            else
            {
                Logger.Log(Lang.T("log.gamemode.6", 0));
            }
            if (CpuTopology.FavoredMask != 0)
            {
                Logger.Log(Lang.T("log.gamemode.favored", 0) + CpuTopology.DescribeMask(CpuTopology.FavoredMask) + Lang.T("log.gamemode.favored.detail", 0) + CpuTopology.FavoredDetail);
            }
            if (!CpuTopology.TopologySourcesAgree)
            {
                Logger.Warn(string.Format(Lang.T("log.gamemode.sourcesplit", 0), CpuTopology.TopologySourceCounts));
            }
            if (CpuTopology.AllMaskReconciled)
            {
                Logger.Warn(string.Format(Lang.T("log.gamemode.countsplit", 0), Environment.ProcessorCount, CpuTopology.CountSetBits(CpuTopology.AllMask)));
            }
            if (CpuTopology.CpuSetPartitionRejected)
            {
                Logger.Log(Lang.T("log.gamemode.8", 0));
            }
            if (CpuTopology.StrictMaskUnsafe)
            {
                Logger.Log(Lang.T("log.gamemode.9", 0));
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
            foreach (string name in AutoCores.RetiredSettings)
            {
                Settings.Remove(name);
            }
            planSwitch = PolicyResolver.Global().PowerPlanOn;
            renderLaneOn = PolicyResolver.Global().RenderLane;
            SuppressionCore.GpuDemoteEnabled = bgSuppressOn;
            foreach (string entryKey in EnvKeys)
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
                bool needsWhitelistUpgrade = true;
                bool malformedWhitelist = false;
                bool whitelistReadFailed = false;
                bool rewriteWhitelist = false;
                string a = null;
                List<WhitelistRule> rules = new List<WhitelistRule>();
                HashSet<string> textsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string entryText in File.ReadAllLines(whitePath))
                {
                    string entryTextTrimmed = entryText.Trim();
                    if (entryTextTrimmed.Length == 0 || entryTextTrimmed.StartsWith("#"))
                    {
                        continue;
                    }
                    if (string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V4", StringComparison.Ordinal) || string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V3", StringComparison.Ordinal) || string.Equals(entryTextTrimmed, "PAVISE_WHITELIST_V2", StringComparison.Ordinal))
                    {
                        if (headerRead || malformedWhitelist || whitelistReadFailed)
                        {
                            throw new InvalidDataException(Lang.T("t.gamemode.11"));
                        }
                        headerRead = true;
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
                    WhitelistRule rule;
                    if (headerRead)
                    {
                        if (!WhitelistRule.TryParseVersioned(entryTextTrimmed, out rule))
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
                        if (!WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, entryTextTrimmed, out rule))
                        {
                            throw new InvalidDataException(Lang.T("t.gamemode.16"));
                        }
                    }
                    malformedWhitelist = true;
                    if (!textsSet.Add(rule.Key))
                    {
                        throw new InvalidDataException(Lang.T("t.gamemode.17"));
                    }
                    rules.Add(rule);
                }
                if (headerRead && whitelistReadFailed)
                {
                    if (!string.Equals(a, BuildWhitelistFooter(rules), StringComparison.Ordinal))
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
                    textsSet.Clear();
                    foreach (WhitelistRule currentRule in rules)
                    {
                        textsSet.Add(currentRule.Key);
                    }
                    rewriteWhitelist = true;
                }
                foreach (string currentPresetWhitelist in SystemProcessCatalog.PresetWhitelist)
                {
                    WhitelistRule candidateRule;
                    if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, currentPresetWhitelist, out candidateRule) && textsSet.Add(candidateRule.Key))
                    {
                        rules.Add(candidateRule);
                        rewriteWhitelist = true;
                    }
                }
                foreach (WhitelistRule resolvedRule in rules)
                {
                    AddWhiteRuleNoSave(resolvedRule);
                }
                if (rewriteWhitelist && !SaveWhite(rules))
                {
                    Logger.Log(Lang.T("log.gamemode.24", 0));
                }
            }
            catch (Exception ex)
            {
                foreach (string n in SystemProcessCatalog.PresetWhitelist)
                {
                    AddWhiteNoSave(n);
                }
                Logger.Log(Lang.T("log.gamemode.25", 0) + ex.Message);
            }
            try
            {
                profiles.AddRange(profileStore.LoadProfiles(!libraryIgnoreTransaction.RecoveryPending));
                List<GameProfile> currentProfiles = GetProfiles();
                if (!ProfileStoreSaveFailed && !libraryIgnoreTransaction.RecoveryPending && RefreshLibraryInstallRoots(currentProfiles) && SaveProfileSnapshotLocked(currentProfiles))
                {
                    profiles.Clear();
                    profiles.AddRange(currentProfiles);
                }
            }
            catch
            {
            }
            InitializeRendererObservations();
        }

    }
}
