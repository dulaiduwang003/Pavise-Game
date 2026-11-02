#if PAVISE_SELFTEST && PAVISE_APP_DATA_RESET_RUNNER
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Normal product startup is forbidden in app reset tests"); }
    }

    internal static class AppDataResetRunner
    {
        private static string output;
        private static int assertions, suites, failures;
        private static readonly BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
        private static Dictionary<string, object> Values
        { get { return (Dictionary<string, object>)typeof(Settings).GetField("transientValues", Hidden).GetValue(null); } }
        private static readonly string[] RecoveryNames =
        {
            "PrevPowerPlan", "PgPlanGuid", "DefaultPlanGuid", "PrevTimerTick", "TimerTickByPavise",
            "PrevGlobalTimerRes", "GlobalTimerResByPavise", "AutoTimerTick", "AutoGlobalTimer",
            "AutoTimerTickBoot", "AutoGlobalTimerBoot", "PrevHvLaunch", "VbsDisabledByPavise",
            "HagsOnByPavise", "PrevHags", "PerformanceActionReceiptsV1", "PerformanceReceipt_ABC",
            "CfgMk_game", "IfeoMk_game", "NvSnap_game", "BackgroundCpuCapJobsV1",
            "AutoCoreThreadPlacementV1", "FrameCoordinationJournalV1", "FrameCoordinationTraceV1",
            "Crash_ThrottleMasks", "Crash_BoostEntriesV2", "Crash_CoreIsolationV1", "RenderLane", "RenderLaneThreadBirthV1",
            "FrameMemoryProtectionJournalV1", "AutostartExe", "AutostartRepairFails",
            "EnvCpuMitigationsV1", "EnvCpuMitigationsV1State", "EnvCpuMitigationsV1Boot",
            "EnvironmentBundleV1", "UnknownFutureReceipt", "UnknownFutureOwnership",
            "PrivacySetting_EnvPrivacyNtfsMemoryV1Boot", "PrivacySetting_EnvPrivacyNtfsMemoryV1Phase"
        };
        private static void Check(bool condition, string description)
        { assertions++; if (!condition) throw new InvalidOperationException(description); }
        private static void Run(string name, Action work)
        {
            suites++;
            try { work(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + " | " + error); }
            finally
            {
                Settings.BeforePreferenceDeleteForTest = null;
                AppDataReset.BeforeFileDeleteForTest = null;
                AppDataReset.ClearUserPreferencesForTest = null;
                LegacyPurge.RestoreHook = null;
            }
        }
        private static string Fixture(string name)
        {
            Settings.UseTransientStoreForCurrentProcess();
            Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
            string directory = Path.Combine(output, name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); return directory;
        }
        private static void Write(string directory, string name)
        { File.WriteAllText(Path.Combine(directory, name), "fixture " + name); }
        private static Dictionary<string, object> SeedRecovery()
        {
            var copy = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in RecoveryNames)
            {
                object value = name.EndsWith("ByPavise", StringComparison.Ordinal) ? (object)1 : "original:" + name;
                Values[name] = value; copy[name] = value;
            }
            return copy;
        }
        private static void VerifyRecovery(Dictionary<string, object> expected)
        {
            foreach (var entry in expected)
                Check(Values.ContainsKey(entry.Key) && object.Equals(Values[entry.Key], entry.Value), "Recovery field changed: " + entry.Key);
        }
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 25000, Timeout.Infinite))
            {
                Run("successful reset clears preferences and user files, preserves recovery and executable", Success);
                Run("false and exceptional stop preserve files and admit no cleanup", StopFailure);
                Run("invalid paths fail before stop or preference deletion", InvalidPaths);
                Run("missing data directory still resets preferences", MissingDirectory);
                Run("locked user file is skipped while other files and preferences clear", LockedFile);
                Run("read-only owned file clears without touching foreign files", ReadOnlyFile);
                Run("directory occupying an owned filename is preserved and reported", OccupiedName);
                Run("preference deletion failure skips one key and continues file cleanup", PreferenceFailure);
                Run("late preference and log writers cannot recreate cleared data", LateWriters);
                Run("reset does not run restoration and tolerates broken progress callback", NoRestore);
                Run("already-stopped cleanup has the same selective semantics", ClearStopped);
                Run("preference classification preserves unknown recovery namespaces", PreferenceSelection);
                Run("atomic remnants are selected precisely and recovery files stay intact", FileSelection);
                Run("restart reads defaults while exact recovery and autostart values persist", RestartReads);
                Run("root and ancestor junctions cannot redirect reset", Junctions);
                Console.WriteLine("RESULT suites=" + suites + " failed=" + failures + " assertions=" + assertions
                    + " settings=transient data=owned-temp native_restore=false task_scheduler=not_called runtime_started=false");
                return failures == 0 ? 0 : 1;
            }
        }
        private static void Success()
        {
            string directory = Fixture("success"); var kept = SeedRecovery();
            string[] preferences = { "PerformancePreset", "Lang", "GameModeOn", "UiLight", "ModeAccent6",
                "LolColumnHeadlessEnabled", "Tame_Steam", "WgShellAuto_game", "CoreLearn_test", "AppGpuAutoHandledV1",
                "MatchPausePcaV1", "PrivacySetting_EnvPrivacyDiagnosticsV1" };
            foreach (string name in preferences) Values[name] = "old:" + name;
            string[] files = { "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.autoignore.txt", "Pavise.profiles.dat",
                "Pavise.renderer-observations.dat", "Pavise.library-ignore.txn", "Pavise.log", "crash.log", "backdrop.img" };
            foreach (string name in files) Write(directory, name);
            foreach (string name in new[] { "Pavise.exe", "Pavise.suppression.state", "Pavise.freeze.state", "Pavise.irq-sessions.dat", "Notes.txt" }) Write(directory, name);
            Directory.CreateDirectory(Path.Combine(directory, "Autostart")); Write(Path.Combine(directory, "Autostart"), "PaviseAuto.exe");
            int stops = 0; var stages = new List<string>();
            ResetDataReport result = AppDataReset.Execute(directory, delegate { stops++; return true; }, stages.Add);
            Check(result.Completed && result.Warnings == 0 && result.Files == files.Length, "Successful reset report is wrong");
            Check(stops == 1 && stages.Count >= 4, "Stop/progress did not run");
            foreach (string name in preferences) Check(!Values.ContainsKey(name), "Preference remains: " + name);
            foreach (string name in files) Check(!File.Exists(Path.Combine(directory, name)), "User file remains: " + name);
            foreach (string name in new[] { "Pavise.exe", "Pavise.suppression.state", "Pavise.freeze.state", "Pavise.irq-sessions.dat", "Notes.txt" })
                Check(File.Exists(Path.Combine(directory, name)), "Non-user or recovery file was deleted: " + name);
            Check(File.Exists(Path.Combine(directory, "Autostart", "PaviseAuto.exe")), "Managed startup payload was deleted");
            VerifyRecovery(kept);
            ResetDataReport again = AppDataReset.ClearStoppedData(directory);
            Check(again.Completed && again.Files == 0, "Second reset is not idempotent"); VerifyRecovery(kept);
        }
        private static void StopFailure()
        {
            string directory = Fixture("stop-failure"); Write(directory, "Pavise.games.txt"); Settings.Save("GameModeOn", false);
            int clears = 0; AppDataReset.BeforeFileDeleteForTest = delegate { clears++; };
            foreach (Func<bool> stop in new Func<bool>[] { delegate { return false; }, delegate { throw new IOException("fake"); } })
            {
                ResetDataReport result = AppDataReset.Execute(directory, stop);
                Check(!result.Completed && result.Warnings == 1 && clears == 0, "Failed stop admitted cleanup");
                Check(File.Exists(Path.Combine(directory, "Pavise.games.txt")) && Values.ContainsKey("GameModeOn"), "Failed stop erased data");
                Check(Settings.Save("UiLight", true), "Failed stop sealed settings writes");
            }
        }
        private static void InvalidPaths()
        {
            string directory = Fixture("invalid-path"); Settings.Save("GameModeOn", false);
            int stops = 0;
            foreach (string path in new[] { null, "", "relative", Path.GetPathRoot(directory) })
            {
                ResetDataReport result = AppDataReset.Execute(path, delegate { stops++; return true; });
                Check(!result.Completed && result.Warnings == 1, "Invalid path passed");
            }
            Check(stops == 0 && Values.ContainsKey("GameModeOn"), "Invalid path caused side effects");
        }
        private static void MissingDirectory()
        {
            string directory = Fixture("missing"); Directory.Delete(directory); Settings.Save("GameModeOn", false);
            ResetDataReport result = AppDataReset.Execute(directory, delegate { return true; });
            Check(result.Completed && !Values.ContainsKey("GameModeOn") && !Directory.Exists(directory), "Missing directory reset failed");
        }
        private static void LockedFile()
        {
            string directory = Fixture("locked"); Write(directory, "Pavise.games.txt"); Write(directory, "Pavise.whitelist.txt"); Settings.Save("UiLight", true);
            using (var held = new FileStream(Path.Combine(directory, "Pavise.games.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                ResetDataReport result = AppDataReset.Execute(directory, delegate { return true; });
                Check(!result.Completed && result.Warnings == 1 && result.Files == 1, "Lock did not give partial report");
                Check(File.Exists(Path.Combine(directory, "Pavise.games.txt")) && !File.Exists(Path.Combine(directory, "Pavise.whitelist.txt")), "Lock stopped independent cleanup");
                Check(!Values.ContainsKey("UiLight"), "Lock prevented preference cleanup");
            }
            Check(AppDataReset.ClearStoppedData(directory).Completed, "Owned lock release cannot be retried");
        }
        private static void ReadOnlyFile()
        {
            string directory = Fixture("read-only"); Write(directory, "Pavise.games.txt"); Write(directory, "Foreign.txt");
            File.SetAttributes(Path.Combine(directory, "Pavise.games.txt"), FileAttributes.ReadOnly);
            Check(AppDataReset.Execute(directory, delegate { return true; }).Completed, "Read-only own file failed");
            Check(File.Exists(Path.Combine(directory, "Foreign.txt")), "Foreign file deleted");
        }
        private static void OccupiedName()
        {
            string directory = Fixture("occupied"); Directory.CreateDirectory(Path.Combine(directory, "Pavise.games.txt"));
            Write(Path.Combine(directory, "Pavise.games.txt"), "child.txt"); Write(directory, "Pavise.whitelist.txt");
            ResetDataReport result = AppDataReset.Execute(directory, delegate { return true; });
            Check(!result.Completed && result.Warnings == 1 && result.Files == 1, "Occupied file not reported");
            Check(File.Exists(Path.Combine(directory, "Pavise.games.txt", "child.txt")), "Owned-looking directory recursed");
        }
        private static void PreferenceFailure()
        {
            string directory = Fixture("preference-failure"); Write(directory, "Pavise.games.txt");
            Values["Lang"] = "en"; Values["UiLight"] = 1; var kept = SeedRecovery();
            Settings.BeforePreferenceDeleteForTest = delegate(string name) { if (name == "Lang") throw new IOException("fake read-only value"); };
            ResetDataReport result = AppDataReset.Execute(directory, delegate { return true; });
            Check(!result.Completed && result.Warnings == 1 && result.Files == 1, "Preference failure report is wrong");
            Check(Values.ContainsKey("Lang") && !Values.ContainsKey("UiLight"), "Independent preferences did not clear"); VerifyRecovery(kept);
            Settings.BeforePreferenceDeleteForTest = null;
            Check(AppDataReset.ClearStoppedData(directory).Completed && !Values.ContainsKey("Lang"), "Preference failure cannot retry");
        }
        private static void LateWriters()
        {
            string directory = Fixture("late-writers"); Write(directory, "Pavise.log"); Logger.LogPath = Path.Combine(directory, "Pavise.log");
            Check(AppDataReset.Execute(directory, delegate { return true; }).Completed, "Late writer setup failed");
            Check(!Settings.Save("UiLight", true) && !Settings.SaveStr("Lang", "en") && !Settings.SaveStrDurable("Late", "value"), "Late settings writes admitted");
            Logger.WritesEnabled = true; Logger.Info("late fixture");
            Check(!File.Exists(Path.Combine(directory, "Pavise.log")), "Log recreated after reset");
        }
        private static void NoRestore()
        {
            string directory = Fixture("no-restore"); Write(directory, "Pavise.games.txt"); int restores = 0;
            LegacyPurge.RestoreHook = delegate { restores++; return new List<string> { "fake system failure" }; };
            ResetDataReport result = AppDataReset.Execute(directory, delegate { return true; }, delegate { throw new IOException("progress fixture"); });
            Check(result.Completed && restores == 0, "App reset called full-system restoration");
        }
        private static void ClearStopped()
        {
            string directory = Fixture("stopped"); Write(directory, "Pavise.games.txt"); Settings.Save("UiLight", true); var kept = SeedRecovery();
            ResetDataReport result = AppDataReset.ClearStoppedData(directory);
            Check(result.Completed && result.Files == 1 && !Values.ContainsKey("UiLight"), "Already-stopped reset failed"); VerifyRecovery(kept);
        }
        private static void PreferenceSelection()
        {
            Check(AppDataReset.IsUserPreference(FrameCoordination.CaptureSessionKey), "Sampling budget is an application preference, not a system recovery receipt");
            foreach (string name in RecoveryNames) Check(!AppDataReset.IsUserPreference(name), "Recovery field selected: " + name);
            foreach (string name in new[] { "NvNewReceipt", "GmFutureSnapshot", "AutoFutureReceipt", "ModeAccentBackup", "Tame", "CoreLearn_" })
                Check(!AppDataReset.IsUserPreference(name), "Broad preference namespace selected: " + name);
            foreach (PolicyItem item in PolicyCatalog.All)
                Check(AppDataReset.IsUserPreference(item.Key) == (item.Key != PolicyCatalog.KeyTimerTick && item.Key != PolicyCatalog.KeyGlobalTimer), "Policy classification: " + item.Key);
            foreach (PerformanceAction action in PerformanceActionCatalog.All)
                Check(AppDataReset.IsUserPreference(action.Key) == !action.Environment, "Automatic action classification: " + action.Key);
        }
        private static void FileSelection()
        {
            string directory = Fixture("atomic");
            string unique = "Pavise.profiles.dat." + Guid.NewGuid().ToString("N") + ".tmp";
            foreach (string name in new[] { unique, "Pavise.whitelist.txt.tmp", "Pavise.autoignore.txt.stale.bak" }) Write(directory, name);
            foreach (string name in new[] { "Pavise.profiles.dat.notes.tmp", "Pavise.suppression.state", "Pavise.irq-sessions.dat", "Pavise.exe", "Unrelated.log" }) Write(directory, name);
            ResetDataReport result = AppDataReset.Execute(directory, delegate { return true; });
            Check(result.Completed && result.Files == 3, "Atomic file classification is wrong");
            foreach (string name in new[] { "Pavise.profiles.dat.notes.tmp", "Pavise.suppression.state", "Pavise.irq-sessions.dat", "Pavise.exe", "Unrelated.log" })
                Check(File.Exists(Path.Combine(directory, name)), "Non-user file selected: " + name);
        }
        private static void RestartReads()
        {
            string directory = Fixture("restart"); Settings.Save("UiLight", true); Settings.SaveStr("PerformancePreset", "4"); var kept = SeedRecovery();
            Settings.SaveStr(FrameCoordination.CaptureSessionKey, "v1;505|999|1|3|1|0");
            Check(Settings.LoadCached("UiLight", false), "Cached preference seed failed");
            Check(AppDataReset.Execute(directory, delegate { return true; }).Completed, "Restart reset failed");
            Check(!Settings.LoadCached("UiLight", false) && Settings.LoadStr("PerformancePreset", "0") == "0", "Deleted preferences still served from cache");
            Check(Settings.LoadStr(FrameCoordination.CaptureSessionKey, "") == "", "Reset retained the process sampling budget");
            var durable = new Dictionary<string, object>(Values, StringComparer.OrdinalIgnoreCase);
            Settings.UseTransientStoreForCurrentProcess(); foreach (var entry in durable) Values[entry.Key] = entry.Value;
            Check(!Settings.Load("UiLight", false) && Settings.LoadStr("PerformancePreset", "0") == "0", "New process reads old preferences");
            VerifyRecovery(kept); Check(Settings.Save("UiLight", false), "New process write barrier not reset");
        }
        private static void Junctions()
        {
            Fixture("junction-reset");
            string link = Path.Combine(output, "root-junction"), ancestor = Path.Combine(output, "ancestor-junction", "data");
            Check(Directory.Exists(link) && Directory.Exists(ancestor), "Tool did not create owned junction fixtures");
            int stops = 0;
            foreach (string path in new[] { link, ancestor })
            {
                ResetDataReport result = AppDataReset.Execute(path, delegate { stops++; return true; });
                Check(!result.Completed && result.Warnings == 1, "Junction reset admitted");
            }
            Check(stops == 0 && File.Exists(Path.Combine(output, "link-target", "Pavise.games.txt")), "Junction touched target");
        }
    }
}
#endif
