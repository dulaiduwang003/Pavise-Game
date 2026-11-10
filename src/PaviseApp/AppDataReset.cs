using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static class AppDataReset
    {
        private static readonly HashSet<string> Preferences = BuildPreferences();

        private static readonly HashSet<string> UserFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt", "Pavise.autoignore.txt", "Pavise.profiles.dat", "Pavise.renderer-observations.dat", "Pavise.library-ignore.txn", "Pavise.log", "Pavise.log.old", "Pavise.preview.log",
            "crash.log", "backdrop.img", "donate.png"
        };

        private static readonly string[] AtomicFiles = new string[7] { "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt", "Pavise.autoignore.txt", "Pavise.profiles.dat", "Pavise.renderer-observations.dat", "Pavise.library-ignore.txn" };

        private static HashSet<string> BuildPreferences()
        {
            HashSet<string> preferenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            preferenceKeys.Add("Lang");
            preferenceKeys.Add("UiLight");
            preferenceKeys.Add("SecurityCompatibilityWarningHidden");
            preferenceKeys.Add("AutoHideOnGame");
            preferenceKeys.Add("DeepTuningLastPage");
            preferenceKeys.Add("BackdropDim");
            preferenceKeys.Add("LogWritesEnabled");
            preferenceKeys.Add("GameModeOn");
            preferenceKeys.Add("GameExitGraceSeconds");
            preferenceKeys.Add("TameOn");
            preferenceKeys.Add("AcModeV1");
            preferenceKeys.Add("GameDvrOff");
            preferenceKeys.Add("GmMmcss");
            preferenceKeys.Add("GmAutoAdd");
            preferenceKeys.Add("GmAudioLowLatV1");
            preferenceKeys.Add("GmRsr");
            preferenceKeys.Add("GmGpuPowerMax");
            preferenceKeys.Add("GpuPrefStageOn");
            preferenceKeys.Add("AppGpuAutoV1");
            preferenceKeys.Add("AppGpuAutoHandledV1");
            preferenceKeys.Add("GmEnglishInputV1");
            preferenceKeys.Add("GmLaptopPerfV1");
            preferenceKeys.Add("AutoLaptopPerformanceV1");
            preferenceKeys.Add(LaptopPerfMode.PreferenceKey);
            preferenceKeys.Add("AutoBackgroundCoreLayoutV1");
            preferenceKeys.Add("AutoDisableFullscreenOptimizations");
            preferenceKeys.Add("GmAggressive");
            preferenceKeys.Add("GmAdaptiveEscalateV1");
            preferenceKeys.Add("GmRenderLane");
            preferenceKeys.Add("FrameCoordination");
            preferenceKeys.Add("GmStandbyCleanerOptionsV1");
            preferenceKeys.Add("GmCacheWarm");
            preferenceKeys.Add("GameLowLatencyOn");
            preferenceKeys.Add("GmWsTrimV1");
            preferenceKeys.Add("GmIdlePolicyV1");
            preferenceKeys.Add("GmPauseServices");
            preferenceKeys.Add("NvVrrWindowed");
            preferenceKeys.Add("NvSmoothMotion");
            preferenceKeys.Add("NvRebar");
            preferenceKeys.Add("NvDlss");
            preferenceKeys.Add("AmdAfmf");
            preferenceKeys.Add("AmdAntiLag");
            preferenceKeys.Add("PowerPlanChoice");
            preferenceKeys.Add("PowerPlanSourceV1");
            preferenceKeys.Add("PowerPlanFailStreak");
            preferenceKeys.Add("CoreLearnIndex");
            preferenceKeys.Add("LastSessionBrief");
            preferenceKeys.Add("EvidenceMode");
            preferenceKeys.Add("IrqSessionProbe");
            preferenceKeys.Add("IrqObserveSkipsV1");
            preferenceKeys.Add("IrqLastCaptureResultV1");
            preferenceKeys.Add("AcProtectedGames");
            preferenceKeys.Add("SelfProtectedProcs");
            preferenceKeys.Add("LolColumnEnabled");
            preferenceKeys.Add("LolColumnCleanupEnabled");
            preferenceKeys.Add("LolColumnHeadlessEnabled");
            preferenceKeys.Add("LolInstallPath");
            preferenceKeys.Add("LolWeGamePath");
            preferenceKeys.Add("WgWeGamePath");
            preferenceKeys.Add("DonateImageId");
            preferenceKeys.Add("AutoFrameCoordinationV1");
            preferenceKeys.Add("FrameCoordinationCaptureSessionsV1");
            preferenceKeys.Add("PerfMonitorEnabledV1");
            preferenceKeys.Add("PerfMonitorEnabledV2");
            preferenceKeys.Add("GameRunReportsEnabledV1");
            preferenceKeys.Add("PerfMonitorDisplayIntervalMsV2");
            preferenceKeys.Add("PerfMonitorTextStyleV2");
            preferenceKeys.Add("PerfMonitorColorsV1");
            preferenceKeys.Add("PerfMonitorLayoutV1");
            preferenceKeys.Add("PerfMonitorMetricsV1");
            preferenceKeys.Add("PerfMonitorIntervalMsV1");
            preferenceKeys.Add("PerfMonitorAppearanceV1");
            preferenceKeys.Add("PerfMonitorHotkeyV1");
            preferenceKeys.Add("PlatformRuntimeRemovedV1");
            preferenceKeys.Add("AutomaticPolicyV1");
            preferenceKeys.Add(CpuMitigationPolicy.ExpandedPreferenceKey);
            HashSet<string> textsSet = preferenceKeys;
            foreach (PolicyItem currentAll in PolicyCatalog.All)
            {
                if (currentAll.Key != "AutoTimerTick" && currentAll.Key != "AutoGlobalTimer")
                {
                    textsSet.Add(currentAll.Key);
                }
            }
            foreach (PerformanceAction performanceAction in PerformanceActionCatalog.All)
            {
                if (!performanceAction.Environment)
                {
                    textsSet.Add(performanceAction.Key);
                }
            }
            foreach (PerformanceAction currentRetired in PerformanceActionCatalog.Retired)
            {
                textsSet.Add(currentRetired.Key);
            }
            foreach (string retiredSetting in AutoCores.RetiredSettings)
            {
                textsSet.Add(retiredSetting);
            }
            for (int l = 0; l < 7; l++)
            {
                textsSet.Add("ModeAccent" + l);
                textsSet.Add(CoreMotionPreferences.KeyPrefix + l);
            }
            foreach (string entryKey in GameMode.EnvKeys)
            {
                textsSet.Add("EnvFuse_" + entryKey);
            }
            return textsSet;
        }

        internal static bool IsUserPreference(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            if (RecoveryRecordKeys.Contains(name) || RecoveryRecordKeys.IsFailureRecord(name))
            {
                return false;
            }
            if (name.Equals("PrivacySetting_EnvPrivacyNtfsMemoryV1Boot", StringComparison.OrdinalIgnoreCase) || name.Equals("PrivacySetting_EnvPrivacyNtfsMemoryV1Phase", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (Preferences.Contains(name) || LaboratorySelection.IsPreference(name))
            {
                return true;
            }
            if (name == "MatchPausePcaV1")
            {
                return true;
            }
            foreach (string entryText in new string[6] { "Tame_", "CoreLearn_", "WgShellAuto_", "WgShellFuse_", "PrivacySetting_", "WgShellProfileV2_" })
            {
                if (name.Length > entryText.Length && name.StartsWith(entryText, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool IsUserDataFile(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            if (UserFiles.Contains(name))
            {
                return true;
            }
            foreach (string filePath in AtomicFiles)
            {
                if (string.Equals(name, filePath + ".tmp", StringComparison.OrdinalIgnoreCase) || string.Equals(name, filePath + ".stale.bak", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                string filePathOffset = filePath + ".";
                Guid result;
                if (name.Length == filePathOffset.Length + 32 + 4 && name.StartsWith(filePathOffset, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(name.Substring(filePathOffset.Length, 32), "N", out result))
                {
                    return true;
                }
            }
            if (name.StartsWith("Pavise.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("crash.", StringComparison.OrdinalIgnoreCase))
            {
                return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static string ValidateDataDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Paths.IsAbsoluteStartupDataPath(directory))
            {
                throw new IOException("Invalid app data directory");
            }
            string getFullPathTrimmed = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(getFullPathTrimmed, Path.GetPathRoot(getFullPathTrimmed).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("A drive root is not an app data directory");
            }
            for (string iterationIndex = getFullPathTrimmed; !string.IsNullOrEmpty(iterationIndex); iterationIndex = Path.GetDirectoryName(iterationIndex))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(iterationIndex);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException("App data directory is redirected or is not a directory");
                }
            }
            return getFullPathTrimmed;
        }

        private static void Progress(Action<string> progress, string message)
        {
            try
            {
                if (progress != null)
                {
                    progress(message);
                }
            }
            catch
            {
            }
        }

        private static void Warning(ResetDataReport result, List<string> failures, string message)
        {
            result.Warnings++;
            if (failures.Count < 3)
            {
                failures.Add(message);
            }
        }

        internal static ResetDataReport Execute(string dataDir, Func<bool> stopRuntime, Action<string> progress = null)
        {
            ResetDataReport resetDataReport = new ResetDataReport();
            try
            {
                ValidateDataDirectory(dataDir);
            }
            catch (Exception ex)
            {
                resetDataReport.Warnings = 1;
                resetDataReport.Failure = Lang.T("wipe.pathfail") + " (" + ex.GetType().Name + ")";
                return resetDataReport;
            }
            Progress(progress, PerformancePolicy.Text("正在停止后台任务", "Stopping background tasks"));
            try
            {
                if (stopRuntime == null || !stopRuntime())
                {
                    resetDataReport.Warnings = 1;
                    resetDataReport.Failure = Lang.T("wipe.stopfail");
                    return resetDataReport;
                }
            }
            catch (Exception ex2)
            {
                resetDataReport.Warnings = 1;
                resetDataReport.Failure = Lang.T("wipe.stopfail") + " (" + ex2.GetType().Name + ")";
                return resetDataReport;
            }
            return ClearStoppedData(dataDir, progress);
        }

        internal static ResetDataReport ClearStoppedData(string dataDir, Action<string> progress = null)
        {
            ResetDataReport resetDataReport = new ResetDataReport();
            List<string> texts = new List<string>();
            string validateDataDirectoryResult;
            try
            {
                validateDataDirectoryResult = ValidateDataDirectory(dataDir);
            }
            catch (Exception ex)
            {
                resetDataReport.Warnings = 1;
                resetDataReport.Failure = Lang.T("wipe.pathfail") + " (" + ex.GetType().Name + ")";
                return resetDataReport;
            }
            if (LegacyPurge.HasHardThreadRecoveryDebt(validateDataDirectoryResult) || !GameNvCanonicalStore.SafeToClear(validateDataDirectoryResult)
                || LaboratoryFileFilter.HasReceipt || LaboratoryAudioEffects.HasReceipt || LaboratoryClockRepair.HasUnfinishedRecovery || LaboratoryManualActions.Busy)
            {
                resetDataReport.Warnings = 1;
                resetDataReport.Failure = "Original runtime recovery is pending; original records and preferences retained";
                return resetDataReport;
            }
            Settings.SuspendWritesForReset();
            Logger.SuspendWritesForReset();
            Progress(progress, PerformancePolicy.Text("正在重置软件设置", "Resetting application preferences"));
            try
            {
                int removed;
                string failure;
                if (!Settings.ClearUserPreferencesForReset(IsUserPreference, out removed, out failure))
                {
                    Warning(resetDataReport, texts, PerformancePolicy.Text("部分软件设置未能删除", "Some preferences could not be removed") + " | " + failure);
                }
            }
            catch (Exception ex2)
            {
                Warning(resetDataReport, texts, "Preference cleanup | " + ex2.GetType().Name);
            }
            Progress(progress, PerformancePolicy.Text("正在清理游戏库和记录", "Clearing game library and records"));
            try
            {
                ValidateDataDirectory(validateDataDirectoryResult);
                if (Directory.Exists(validateDataDirectoryResult))
                {
                    foreach (string path in Directory.GetFileSystemEntries(validateDataDirectoryResult, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (IsUserDataFile(Path.GetFileName(path)))
                        {
                            try
                            {
                                ValidateDataDirectory(validateDataDirectoryResult);
                                DeleteFile(path);
                                resetDataReport.Files++;
                            }
                            catch (Exception ex3)
                            {
                                Warning(resetDataReport, texts, Path.GetFileName(path) + " (" + ex3.GetType().Name + ")");
                            }
                        }
                    }
                }
            }
            catch (Exception ex4)
            {
                Warning(resetDataReport, texts, "User data cleanup | " + ex4.GetType().Name);
            }
            resetDataReport.Completed = resetDataReport.Warnings == 0;
            resetDataReport.Failure = ((texts.Count == 0) ? null : string.Join("; ", texts.ToArray()));
            Progress(progress, PerformancePolicy.Text("软件数据清理完成", "Application data cleanup finished"));
            return resetDataReport;
        }

        private static void DeleteFile(string path)
        {
            int processedCount = 0;
            while (true)
            {
                try
                {
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(path);
                    }
                    catch (FileNotFoundException)
                    {
                        break;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        break;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        throw new IOException("Expected an owned file, found a directory");
                    }
                    if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                    {
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    }
                    File.Delete(path);
                    try
                    {
                        File.GetAttributes(path);
                    }
                    catch (FileNotFoundException)
                    {
                        break;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        break;
                    }
                    throw new IOException("File is still present");
                }
                catch (IOException)
                {
                    if (processedCount >= 2)
                    {
                        throw;
                    }
                    Thread.Sleep(100);
                }
                processedCount++;
            }
        }
    }
}
