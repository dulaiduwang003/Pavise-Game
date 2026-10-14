using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal sealed class ResetDataReport
    {
        internal bool Completed;
        internal int Files;
        internal int Warnings;
        internal string Failure;
    }

    // 清除偏好不删除系统原值、所有权标记或未知恢复记录。
    internal static class AppDataReset
    {
        private static readonly HashSet<string> Preferences = BuildPreferences();
        private static readonly HashSet<string> UserFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt",
            "Pavise.autoignore.txt", "Pavise.profiles.dat", "Pavise.renderer-observations.dat",
            "Pavise.library-ignore.txn", "Pavise.log", "Pavise.log.old", "Pavise.preview.log",
            "crash.log", "backdrop.img", "donate.png"
        };
        private static readonly string[] AtomicFiles =
        {
            "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt", "Pavise.autoignore.txt",
            "Pavise.profiles.dat", "Pavise.renderer-observations.dat", "Pavise.library-ignore.txn"
        };
#if PAVISE_SELFTEST
        internal static Func<ResetDataReport> ClearUserPreferencesForTest;
        internal static Action<string> BeforeFileDeleteForTest;
#endif

        private static HashSet<string> BuildPreferences()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Lang", "UiLight", "AutoHideOnGame", "DeepTuningLastPage", "BackdropDim",
                "LogWritesEnabled", "GameModeOn", "GameExitGraceSeconds", "TameOn", "AcModeV1",
                "GameDvrOff", "GmMmcss", "GmAutoAdd", "GmAudioLowLatV1", "GmRsr", "GmGpuPowerMax",
                "GpuPrefStageOn", "AppGpuAutoV1", "AppGpuAutoHandledV1", "GmEnglishInputV1",
                "GmLaptopPerfV1", "AutoBackgroundCoreLayoutV1", "AutoDisableFullscreenOptimizations",
                "GmStandbyCleanerOptionsV1", "GmCacheWarm", "GameLowLatencyOn",
                "NvVrrWindowed", "NvSmoothMotion", "NvRebar", "NvDlss", "AmdAfmf", "AmdAntiLag",
                "PowerPlanChoice", "PowerPlanFailStreak", "CoreLearnIndex", "LastSessionBrief",
                "EvidenceMode", "IrqSessionProbe", "IrqObserveSkipsV1", "IrqLastCaptureResultV1",
                "AcProtectedGames", "SelfProtectedProcs", "LolColumnEnabled", "LolColumnCleanupEnabled",
                "LolColumnHeadlessEnabled", "LolInstallPath", "LolWeGamePath", "WgWeGamePath",
                "DonateImageId", "AutoFrameCoordinationV1", FrameCoordination.CaptureSessionKey,
                MonitoringPreferences.EnabledKey, MonitoringPreferences.LayoutKey,
                MonitoringPreferences.MetricsKey, MonitoringPreferences.RetiredIntervalKey,
                MonitoringPreferences.AppearanceKey, MonitoringHotkey.SettingsKey, PlatformRuntimePolicy.RemovedKey
            };
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                // 保留开机环境偏好，避免数据重置间接恢复系统配置。
                if (item.Key != PolicyCatalog.KeyTimerTick && item.Key != PolicyCatalog.KeyGlobalTimer)
                    names.Add(item.Key);
            }
            foreach (PerformanceAction action in PerformanceActionCatalog.All)
                if (!action.Environment) names.Add(action.Key);
            foreach (PerformanceAction action in PerformanceActionCatalog.Retired) names.Add(action.Key);
            foreach (string name in AutoCores.RetiredSettings) names.Add(name);
            for (int i = 0; i < 7; i++) names.Add("ModeAccent" + i);
            foreach (string name in GameMode.EnvKeys) names.Add("EnvFuse_" + name);
            return names;
        }

        internal static bool IsUserPreference(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (RecoveryRecordKeys.Contains(name) || RecoveryRecordKeys.IsFailureRecord(name)) return false;
            if (name.Equals("PrivacySetting_EnvPrivacyNtfsMemoryV1Boot", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PrivacySetting_EnvPrivacyNtfsMemoryV1Phase", StringComparison.OrdinalIgnoreCase)) return false;
            if (Preferences.Contains(name)) return true;
            if (name == "MatchPausePcaV1") return true;
            foreach (string prefix in new[] { "Tame_", "CoreLearn_", "WgShellAuto_", "WgShellFuse_", "PrivacySetting_" })
                if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static bool IsUserDataFile(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (UserFiles.Contains(name)) return true;
            foreach (string file in AtomicFiles)
            {
                if (string.Equals(name, file + ".tmp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, file + ".stale.bak", StringComparison.OrdinalIgnoreCase)) return true;
                string prefix = file + ".";
                Guid token;
                if (name.Length == prefix.Length + 32 + 4
                    && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out token)) return true;
            }
            return (name.StartsWith("Pavise.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("crash.", StringComparison.OrdinalIgnoreCase))
                && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
        }

        private static string ValidateDataDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Paths.IsAbsoluteStartupDataPath(directory))
                throw new IOException("Invalid app data directory");
            string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, Path.GetPathRoot(full).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("A drive root is not an app data directory");
            for (string current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("App data directory is redirected or is not a directory");
            }
            return full;
        }

        private static void Progress(Action<string> progress, string message)
        { try { if (progress != null) progress(message); } catch { } }
        private static void Warning(ResetDataReport result, List<string> failures, string message)
        {
            result.Warnings++;
            if (failures.Count < 3) failures.Add(message);
        }

        internal static ResetDataReport Execute(string dataDir, Func<bool> stopRuntime, Action<string> progress = null)
        {
            var result = new ResetDataReport();
            try { ValidateDataDirectory(dataDir); }
            catch (Exception error) { result.Warnings = 1; result.Failure = Lang.T("wipe.pathfail") + " (" + error.GetType().Name + ")"; return result; }
            Progress(progress, AutomaticPolicy.Text("正在停止后台任务", "Stopping background tasks"));
            try
            {
                if (stopRuntime == null || !stopRuntime())
                { result.Warnings = 1; result.Failure = Lang.T("wipe.stopfail"); return result; }
            }
            catch (Exception error)
            { result.Warnings = 1; result.Failure = Lang.T("wipe.stopfail") + " (" + error.GetType().Name + ")"; return result; }
            return ClearStoppedData(dataDir, progress);
        }

        internal static ResetDataReport ClearStoppedData(string dataDir, Action<string> progress = null)
        {
            var result = new ResetDataReport();
            var failures = new List<string>();
            string root;
            try { root = ValidateDataDirectory(dataDir); }
            catch (Exception error)
            { result.Warnings = 1; result.Failure = Lang.T("wipe.pathfail") + " (" + error.GetType().Name + ")"; return result; }
            Settings.SuspendWritesForReset();
            Logger.SuspendWritesForReset();
            Progress(progress, AutomaticPolicy.Text("正在重置软件设置", "Resetting application preferences"));
            try
            {
                int removed;
                string failure;
#if PAVISE_SELFTEST
                if (ClearUserPreferencesForTest != null)
                {
                    ResetDataReport preferences = ClearUserPreferencesForTest();
                    if (preferences == null || !preferences.Completed)
                        Warning(result, failures, preferences == null ? "Preference cleanup returned no result" : preferences.Failure ?? "Preference cleanup incomplete");
                }
                else
#endif
                if (!Settings.ClearUserPreferencesForReset(IsUserPreference, out removed, out failure))
                    Warning(result, failures, AutomaticPolicy.Text("部分软件设置未能删除", "Some preferences could not be removed") + " | " + failure);
            }
            catch (Exception error) { Warning(result, failures, "Preference cleanup | " + error.GetType().Name); }
            Progress(progress, AutomaticPolicy.Text("正在清理游戏库和记录", "Clearing game library and records"));
            try
            {
                ValidateDataDirectory(root);
                if (Directory.Exists(root))
                    foreach (string path in Directory.GetFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (!IsUserDataFile(Path.GetFileName(path))) continue;
                        try
                        {
                            ValidateDataDirectory(root);
#if PAVISE_SELFTEST
                            if (BeforeFileDeleteForTest != null) BeforeFileDeleteForTest(path);
#endif
                            DeleteFile(path);
                            result.Files++;
                        }
                        catch (Exception error) { Warning(result, failures, Path.GetFileName(path) + " (" + error.GetType().Name + ")"); }
                    }
            }
            catch (Exception error) { Warning(result, failures, "User data cleanup | " + error.GetType().Name); }
            result.Completed = result.Warnings == 0;
            result.Failure = failures.Count == 0 ? null : string.Join("; ", failures.ToArray());
            Progress(progress, AutomaticPolicy.Text("软件数据清理完成", "Application data cleanup finished"));
            return result;
        }

        private static void DeleteFile(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (FileNotFoundException) { return; }
                    catch (DirectoryNotFoundException) { return; }
                    if ((attributes & FileAttributes.Directory) != 0) throw new IOException("Expected an owned file, found a directory");
                    // 可删除文件链接，但不能修改链接目标属性。
                    if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(path);
                    try { File.GetAttributes(path); }
                    catch (FileNotFoundException) { return; }
                    catch (DirectoryNotFoundException) { return; }
                    throw new IOException("File is still present");
                }
                catch (IOException)
                {
                    if (attempt >= 2) throw;
                    Thread.Sleep(100);
                }
            }
        }
    }
}
