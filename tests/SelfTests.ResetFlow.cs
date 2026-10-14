#if PAVISE_SELFTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
#if PAVISE_RENDERER_BENCH
using System.Web.Script.Serialization;
#endif

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void ExitGracePreferenceRoundTrip(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "exit-grace"))
            {
                Eq(8, f.Mode.ExitGraceSeconds);
                long now = DateTime.UtcNow.Ticks;
                foreach (int seconds in new[] { 0, 2, 5, 8, 15, 30 })
                {
                    Eq(true, f.Mode.TrySetExitGraceSeconds(seconds));
                    Eq(seconds, f.Mode.ExitGraceSeconds);
                    Eq(seconds, new GameMode(f.DirectoryPath, new SuppressionCore()).ExitGraceSeconds);
                    Eq(true, GameMode.ExitGraceElapsed(now, now + seconds * TimeSpan.TicksPerSecond, seconds));
                    if (seconds > 0)
                        Eq(false, GameMode.ExitGraceElapsed(now, now + seconds * TimeSpan.TicksPerSecond - 1, seconds));
                }
                Eq(false, GameMode.ExitGraceElapsed(0, now, 0));
                Eq(false, GameMode.ExitGraceElapsed(now, now - 1, 0));
                Eq(false, f.Mode.TrySetExitGraceSeconds(-1));
                Eq(false, f.Mode.TrySetExitGraceSeconds(999));
                Eq(30, f.Mode.ExitGraceSeconds);
                Settings.SaveStr(GameMode.ExitGraceSettingKey, "broken");
                Eq(8, new GameMode(f.DirectoryPath, new SuppressionCore()).ExitGraceSeconds);
                Settings.SaveStr(GameMode.ExitGraceSettingKey, "999");
                Eq(8, new GameMode(f.DirectoryPath, new SuppressionCore()).ExitGraceSeconds);
                Settings.SuspendWritesForReset();
                Eq(false, f.Mode.TrySetExitGraceSeconds(0));
                Eq(30, f.Mode.ExitGraceSeconds);
                Settings.UseTransientStoreForCurrentProcess();
                FamilyPolicySetField(f.Mode, "stopping", true);
                Eq(false, f.Mode.TrySetExitGraceSeconds(0));
                Eq(30, f.Mode.ExitGraceSeconds);
            }
        }

        internal static int RunResetFlowRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseResetFlow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string oldLog = Logger.LogPath;
            Func<List<string>> oldRestore = LegacyPurge.RestoreHook;
            Func<bool> oldRegistry = LegacyPurge.DeleteRegistryHook;
            bool oldSkip = LegacyPurge.SkipRegistryDelete;
            Action<string>[] tests =
            {
                ExitGracePreferenceRoundTrip,
                ResetFlowEmptyPathsDoNotStop,
                ResetFlowMissingStopRejects,
                ResetFlowFalseStopKeepsBothStores,
                ResetFlowThrowingStopKeepsBothStores,
                ResetFlowStopRestoreDeleteRegistryOrder,
                ResetFlowRestoreFailureKeepsBothStores,
                ResetFlowRestoreExceptionKeepsBothStores,
                ResetFlowRestoreNullKeepsBothStores,
                ResetFlowLockedDataBlocksRegistry,
                ResetFlowRegistryFailureIsNotSuccess,
                ResetFlowRegistryExceptionIsNotSuccess,
                ResetFlowPortableSuccessKeepsForeignFiles,
                ResetFlowLateWritesCannotReviveData,
                ResetFlowStrictStringReadDistinguishesFailure,
                ResetFlowAmdResidueKeepsEnvironmentRecoveryArmed,
                ResetFlowSessionLedgersKeepEnvironmentRecoveryArmed,
                ResetFlowEnvironmentCompletionRequiresSettledReceipts,
                ResetFlowDriverAdmissionSealed,
                ResetFlowDriverInFlightMustDrain,
                ResetFlowPowerAdmissionSealed,
                ResetFlowAutonomousMinimumPolicy,
                ResetFlowPowerSchemeNotificationUsesLightweightAudit,
                ResetFlowPowerLateResultIsNotPublished,
                ResetFlowDrainRejectsLiveAndReentrantCallers,
                ResetFlowDrainWaitsForOtherWriteGates,
                ResetFlowWhitelistCannotSaveAfterStopping,
                ResetFlowStoreCloseRejectsLateMutation,
                ResetFlowStoreCloseTimeoutIsNotSuccess,
                ResetFlowStoreCloseRejectsReentrantCaller,
                ResetFlowRenderLaneRejectsStaleAndClosedWork,
                ResetFlowRenderLaneSessionEndCancelsSampling,
                ResetFlowRenderLaneDrainKeepsJournal,
                ResetFlowRenderLaneRestoreFailureKeepsJournal,
                ResetFlowRenderLaneInvalidJournalIsNotSuccess,
                ResetFlowRenderLaneAppliedFailureKeepsState,
                ResetFlowRenderLaneRollbackDebtBlocksNewPin,
                ResetFlowRenderLaneAppliedDebtBlocksNewPin,
                ResetFlowRenderLaneChangedJournalIsNotOverwritten,
                ResetFlowRenderLaneUnreadableLedgerBlocksWork,
                ResetFlowRenderLaneJournalReadbackFailureBlocksPin,
                ResetFlowRenderLaneClearReadFailureDoesNotSucceed,
                ResetFlowFsoTrackingFailureDoesNotWrite,
                ResetFlowFsoExistingUserTokenIsNotOwned,
                ResetFlowFsoTracksBeforeWritingAndPreservesOthers,
                ResetFlowFsoPartialWriteRollsBackOwnedToken,
                ResetFlowFsoFailedRollbackKeepsRecovery,
                ResetFlowFsoRemovalReadFailureKeepsRecovery,
                ResetFlowFsoLedgerCleanupFailureIsNotSuccess,
                ResetFlowFsoUnreadableLedgerPreservesAllEntries,
                ResetFlowFsoLedgerReadbackFailureBlocksApply,
                ResetFlowFsoLedgerReadFailureDuringCleanup,
                ResetFlowPowerPlanRecoveryOwnership,
                ResetFlowRetiredPolicies,
                ResetFlowLegacyIdleRecovery,
                ResetFlowLegacyIdleUncertainRecovery,
                ResetFlowCpuIdleResetKeepsRecovery
            };
            try
            {
                foreach (Action<string> test in tests)
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest();
                    Logger.LogPath = null;
                    Lang.Cur = 0;
                    LegacyPurge.SkipRegistryDelete = false;
                    LegacyPurge.RestoreHook = delegate { throw new InvalidOperationException("Unmocked restore in reset-flow test"); };
                    LegacyPurge.DeleteRegistryHook = delegate { throw new InvalidOperationException("Unmocked registry in reset-flow test"); };
                    test(root);
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                return tests.Length;
            }
            finally
            {
                LegacyPurge.RestoreHook = oldRestore;
                LegacyPurge.DeleteRegistryHook = oldRegistry;
                LegacyPurge.SkipRegistryDelete = oldSkip;
                Logger.ResetWriteBarrierForTest();
                Logger.LogPath = oldLog;
                Settings.UseTransientStoreForCurrentProcess();
                ResetFlowDeleteScratch(root);
            }
        }

        private static void ResetFlowCheck(bool condition, string message)
        {
            if (!condition) throw new Exception("Reset flow regression: " + message);
        }

        private static void ResetFlowStrictStringReadDistinguishesFailure(string root)
        {
            const string key = "ResetFlowStrictString";
            string value;
            ResetFlowCheck(Settings.TryLoadStr(key, out value) && value == ""
                && Settings.LoadStr(key, "fallback") == "fallback", "a missing string was not distinguished from failure");
            Settings.SaveStr(key, "owned");
            using (var reads = new ResetFlowStrictReadScope(key))
            {
                reads.Deny = true;
                ResetFlowCheck(!Settings.TryLoadStr(key, out value) && value == ""
                    && Settings.LoadStr(key, "fallback") == "owned", "strict failure changed forgiving string reads");
                reads.Deny = false;
                ResetFlowCheck(Settings.TryLoadStr(key, out value) && value == "owned", "a strict read could not retry");
                Settings.Save(key, true);
                ResetFlowCheck(!Settings.TryLoadStr(key, out value) && value == ""
                    && Settings.LoadStr(key, "fallback") == "1", "a non-string ledger was accepted or legacy conversion changed");
                Settings.SaveStr(key, "");
                ResetFlowCheck(Settings.TryLoadStr(key, out value) && value == "", "a readable cleared string was treated as a failure");
            }
        }

        private static void ResetFlowAmdResidueKeepsEnvironmentRecoveryArmed(string root)
        {
            GameMode mode = (GameMode)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameMode));
            FamilyPolicySetField(mode, "sync", new object());
            FamilyPolicySetField(mode, "core", new SuppressionCore());
            FamilyPolicySetField(mode, "envRestorePending", new HashSet<string>());
            var missed = new List<string>();
            try
            {
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(mode, "EnvActive"), "empty environment reported residue");
                foreach (string field in new[] { "rsrActive", "amdAlagActive", "amdAfmfActive" })
                {
                    FamilyPolicySetField(mode, field, true);
                    if (!(bool)FamilyPolicyInvoke(mode, "EnvActive")) missed.Add(field);
                    FamilyPolicySetField(mode, field, false);
                }
                foreach (string key in new[] { "sys.rsr", "sys.afmf", "g0.alag", "g0.chill", "g0.esync", "g0.ris", "g0.frtc" })
                {
                    var snapshot = new Dictionary<string, string>();
                    snapshot[key] = key == "g0.chill" ? "1|48|144" : key == "g0.ris" ? "0|80"
                        : key == "g0.frtc" ? "0|144" : "0";
                    if (key == "sys.rsr") snapshot["sys.rsrsharp"] = "75";
                    ResetFlowCheck(Settings.SaveStr("AmdSnap", NvDrsTweaks.SerializeSnapshot(snapshot)), "mock AMD snapshot save failed");
                    ResetFlowCheck(AdlxTweaks.HasResidue(), "mock AMD snapshot did not represent recovery debt");
                    if (!(bool)FamilyPolicyInvoke(mode, "EnvActive")) missed.Add("snapshot:" + key);
                    ResetFlowCheck(Settings.SaveStr("AmdSnap", ""), "mock AMD snapshot clear failed");
                }
                ResetFlowCheck(missed.Count == 0, "AMD recovery was not armed for " + string.Join(", ", missed.ToArray()));
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(mode, "EnvActive"), "settled AMD recovery remained armed");
            }
            finally { Settings.SaveStr("AmdSnap", ""); }
        }

        private static void ResetFlowSessionLedgersKeepEnvironmentRecoveryArmed(string root)
        {
            GameMode mode = (GameMode)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameMode));
            FamilyPolicySetField(mode, "sync", new object());
            FamilyPolicySetField(mode, "core", new SuppressionCore());
            FamilyPolicySetField(mode, "envRestorePending", new HashSet<string>());
            string stagedPath = Path.Combine(root, "pending-game.exe");
            var records = new Dictionary<string, string>
            {
                { "GpuPowerSnap", "nv:VEN_10DE&DEV_2684:100000:110000" },
                { "PrevUpdatePaused", "wuauserv" },
                { "PrevDoBgBw", "=0\u001F=1" },
                { "PrevDoSvcStopped", "1" },
                { "PrevPresenceQos", "=0\u001F=1" },
                { "GpuPrefStage", GpuPrefStage.EncodeJournal(stagedPath, null) },
                { FrameCoordination.JournalKey, "v1;505|999|101|1001|0" },
                { FrameCoordinationTrace.TraceKey, "Pavise.FrameCoordination.505." + Guid.NewGuid().ToString("N") }
            };
            var missed = new List<string>();
            try
            {
                foreach (KeyValuePair<string, string> record in records)
                {
                    ResetFlowCheck(Settings.SaveStr(record.Key, record.Value), "mock session journal save failed");
                    if (!(bool)FamilyPolicyInvoke(mode, "EnvActive")) missed.Add(record.Key);
                    ResetFlowCheck(Settings.SaveStr(record.Key, ""), "mock session journal clear failed");
                }
                foreach (string key in new[] { "GpuPrefStage", "NvDrsList" })
                {
                    ResetFlowCheck(Settings.SaveStr(key, key == "GpuPrefStage"
                        ? GpuPrefStage.EncodeJournal(stagedPath, null) : "pending-game.exe"), "mock pre-stage save failed");
                    FamilyPolicySetField(mode, "enabled", true);
                    FamilyPolicySetField(mode, "gpuPrefStageOn", true);
                    FamilyPolicySetField(mode, "preStagedNvPath", stagedPath);
                    ResetFlowCheck(!(bool)FamilyPolicyInvoke(mode, "EnvActive"), "legitimate standby pre-stage was treated as orphaned: " + key);
                    FamilyPolicySetField(mode, "gpuPrefStageOn", false);
                    if (key == "GpuPrefStage" && !(bool)FamilyPolicyInvoke(mode, "EnvActive"))
                        missed.Add("disabled-setting:" + key);
                    if (key == "NvDrsList") ResetFlowCheck(!(bool)FamilyPolicyInvoke(mode, "EnvActive"),
                        "turning off GPU preference staging orphaned unrelated NVIDIA pre-staging");
                    FamilyPolicySetField(mode, "enabled", false);
                    if (!(bool)FamilyPolicyInvoke(mode, "EnvActive")) missed.Add("disarmed:" + key);
                    FamilyPolicySetField(mode, "enabled", true);
                    FamilyPolicySetField(mode, "preStagedNvPath", null);
                    if (!(bool)FamilyPolicyInvoke(mode, "EnvActive")) missed.Add("released:" + key);
                    FamilyPolicySetField(mode, "enabled", false);
                    ResetFlowCheck(Settings.SaveStr(key, ""), "mock pre-stage clear failed");
                }
                ResetFlowCheck(missed.Count == 0, "session recovery was not armed for " + string.Join(", ", missed.ToArray()));
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(mode, "EnvActive"), "cleared session journals still armed recovery");
            }
            finally
            {
                foreach (string key in records.Keys) Settings.SaveStr(key, "");
                Settings.SaveStr("NvDrsList", "");
            }
        }

        private static void ResetFlowEnvironmentCompletionRequiresSettledReceipts(string root)
        {
            GameMode mode = (GameMode)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameMode));
            FamilyPolicySetField(mode, "sync", new object());
            FamilyPolicySetField(mode, "core", new SuppressionCore());
            FamilyPolicySetField(mode, "envRestorePending", new HashSet<string>());
            var missed = new List<string>();
            string[] keys = { "PrevDoBgBw", "PrevDoSvcStopped", "PrevUpdatePaused", "PrevPresenceQos",
                "GpuPowerSnap", "AmdSnap", "NvDrsList", "GpuPrefStage", "PrevPowerPlan",
                PowerPlan.LegacyCpuIdleLedgerKey, OptionalServicePause.LedgerKey,
                FrameCoordination.JournalKey, FrameCoordinationTrace.TraceKey };
            try
            {
                ResetFlowCheck((bool)FamilyPolicyInvoke(mode, "EnvRestoreCompleted", true), "empty environment completion failed");
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(mode, "EnvRestoreCompleted", false), "failed native action was reported complete");
                foreach (string key in keys)
                {
                    ResetFlowCheck(Settings.SaveStr(key, "unsettled"), "mock completion receipt save failed");
                    if ((bool)FamilyPolicyInvoke(mode, "EnvRestoreCompleted", true)) missed.Add(key);
                    ResetFlowCheck(Settings.SaveStr(key, ""), "mock completion receipt clear failed");
                    using (var reads = new ResetFlowStrictReadScope(key))
                    {
                        reads.Deny = true;
                        if ((bool)FamilyPolicyInvoke(mode, "EnvRestoreCompleted", true)) missed.Add("unreadable:" + key);
                    }
                }
                Settings.SaveStr(AppGpuPreferences.LedgerKey, "persistent-user-choice");
                ResetFlowCheck((bool)FamilyPolicyInvoke(mode, "EnvRestoreCompleted", true)
                    && !(bool)FamilyPolicyInvoke(mode, "EnvActive"), "persistent app preference armed session restoration");
                Settings.SaveStr("AmdSnap", "uncleared");
                Settings.SuspendWritesForReset();
                ResetFlowCheck(!Settings.SaveStr("AmdSnap", ""), "mock journal cleanup failure did not reject the write");
                if ((bool)FamilyPolicyInvoke(mode, "EnvRestoreCompleted", true)) missed.Add("failed-clear:AmdSnap");
                ResetFlowCheck(missed.Count == 0, "reported recovery complete with unsettled receipts: " + string.Join(", ", missed.ToArray()));
            }
            finally { Settings.UseTransientStoreForCurrentProcess(); }
        }

        private static void ResetFlowEmptyPathsDoNotStop(string root)
        {
            foreach (string path in new[] { null, "", "   " })
            {
                var f = new ResetFlowFixture(root, "empty-path");
                int files;
                string failure;
                ResetFlowCheck(!Program.TryResetUserData(path, f.Stop(true), out files, out failure), "empty path was accepted");
                ResetFlowCheck(files == 0 && !string.IsNullOrEmpty(failure), "empty path did not report rejection");
                ResetFlowCheck(f.StopCalls == 0 && f.RestoreCalls == 0 && f.RegistryCalls == 0, "empty path started teardown");
                f.AssertOriginalFiles(); f.AssertRegistryPresent();
            }
        }

        private static void ResetFlowMissingStopRejects(string root)
        {
            var f = new ResetFlowFixture(root, "missing-stop");
            int files;
            string failure;
            ResetFlowCheck(!Program.TryResetUserData(f.DirectoryPath, null, out files, out failure), "missing stop callback was accepted");
            ResetFlowCheck(files == 0 && !string.IsNullOrEmpty(failure), "missing stop callback was not explained");
            ResetFlowCheck(f.StopCalls == 0 && f.RestoreCalls == 0 && f.RegistryCalls == 0, "missing stop ran restore or deletion");
            f.AssertOriginalFiles(); f.AssertRegistryPresent();
        }

        private static void ResetFlowFalseStopKeepsBothStores(string root)
        {
            var f = new ResetFlowFixture(root, "stop-false");
            int files;
            string failure;
            ResetFlowCheck(!Program.TryResetUserData(f.DirectoryPath, f.Stop(false), out files, out failure), "failed stop was accepted");
            ResetFlowCheck(files == 0 && !string.IsNullOrEmpty(failure), "failed stop did not preserve the cleanup count");
            ResetFlowCheck(f.StopCalls == 1 && f.RestoreCalls == 0 && f.RegistryCalls == 0, "failed stop continued or retried");
            f.AssertOriginalFiles(); f.AssertRegistryPresent();
            ResetFlowCheck(Settings.SaveStr("ResetFlowAfterStopFailure", "allowed"), "stop failure prematurely sealed settings");
        }

        private static void ResetFlowThrowingStopKeepsBothStores(string root)
        {
            var f = new ResetFlowFixture(root, "stop-throw");
            int files;
            string failure;
            Func<bool> stop = delegate
            {
                f.StopCalls++;
                f.AssertOriginalFiles(); f.AssertRegistryPresent();
                throw new InvalidOperationException("isolated stop failure");
            };
            ResetFlowCheck(!Program.TryResetUserData(f.DirectoryPath, stop, out files, out failure), "throwing stop was accepted");
            ResetFlowCheck(files == 0 && failure != null && failure.Contains("InvalidOperationException"), "stop exception phase/type was lost");
            ResetFlowCheck(f.StopCalls == 1 && f.RestoreCalls == 0 && f.RegistryCalls == 0, "stop exception continued or retried");
            f.AssertOriginalFiles(); f.AssertRegistryPresent();
            ResetFlowCheck(Settings.Save("ResetFlowAfterStopException", true), "stop exception prematurely sealed settings");
        }

        private static void ResetFlowStopRestoreDeleteRegistryOrder(string root)
        {
            var f = new ResetFlowFixture(root, "ordered-success");
            f.SetRestore(delegate
            {
                ResetFlowCheck(f.StopCalls == 1 && f.RegistryCalls == 0, "restoration ran before confirmed stop or after registry deletion");
                f.AssertOriginalFiles(); f.AssertRegistryPresent();
                ResetFlowCheck(Settings.SaveStr("ResetFlowRestoreWrite", "restored"), "restoration could not update its recovery settings");
                return new List<string>();
            });
            f.SetRegistry(delegate
            {
                ResetFlowCheck(f.StopCalls == 1 && f.RestoreCalls == 1, "registry ran before stop and restore");
                f.AssertOwnedFilesGone();
                ResetFlowCheck(Settings.LoadStr("ResetFlowRestoreWrite", "") == "restored", "restoration's setting was not written before sealing");
                ResetFlowCheck(!Settings.SaveStr("ResetFlowDuringRegistry", "forbidden"), "settings were not sealed before registry deletion");
                return true;
            });
            int files;
            string failure;
            ResetFlowCheck(Program.TryResetUserData(f.DirectoryPath, f.Stop(true), out files, out failure), "ordered reset failed: " + failure);
            ResetFlowCheck(failure == null && files == f.OwnedPaths.Count, "successful reset returned an inconsistent result");
            ResetFlowCheck(string.Join(",", f.Events.ToArray()) == "stop,restore,registry", "stop/restore/registry order was wrong");
            ResetFlowCheck(f.RegistryCalls == 1 && !f.MockRegistryPresent, "registry was not deleted exactly once");
            f.AssertForeignFiles();
        }

        private static void ResetFlowRestoreFailureKeepsBothStores(string root)
        {
            ResetFlowExpectRestoreRejected(root, "restore-false", delegate { return new List<string> { "pending recovery journal" }; });
        }

        private static void ResetFlowRestoreExceptionKeepsBothStores(string root)
        {
            ResetFlowExpectRestoreRejected(root, "restore-throw", delegate { throw new IOException("isolated restore exception"); });
        }

        private static void ResetFlowRestoreNullKeepsBothStores(string root)
        {
            ResetFlowExpectRestoreRejected(root, "restore-null", delegate { return null; });
        }

        private static void ResetFlowExpectRestoreRejected(string root, string name, Func<List<string>> restore)
        {
            var f = new ResetFlowFixture(root, name);
            f.SetRestore(restore);
            int files;
            string failure;
            ResetFlowCheck(!Program.TryResetUserData(f.DirectoryPath, f.Stop(true), out files, out failure), "unconfirmed restoration was accepted");
            ResetFlowCheck(files == 0 && !string.IsNullOrEmpty(failure), "unconfirmed restoration did not explain failure");
            ResetFlowCheck(f.StopCalls == 1 && f.RestoreCalls == 1 && f.RegistryCalls == 0, "restoration rejection continued or retried");
            f.AssertOriginalFiles(); f.AssertRegistryPresent();
            ResetFlowCheck(Settings.SaveStr("ResetFlowAfterRestoreFailure", "allowed"), "failed restoration prematurely sealed settings");
        }

        private static void ResetFlowLockedDataBlocksRegistry(string root)
        {
            var f = new ResetFlowFixture(root, "locked-data");
            int files;
            string failure;
            using (var lease = new FileStream(f.ProfilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                ResetFlowCheck(!Program.TryResetUserData(f.DirectoryPath, f.Stop(true), out files, out failure), "locked data was reported cleared");
            ResetFlowCheck(failure != null && failure.Contains("清理"), "locked data did not identify cleanup failure");
            ResetFlowCheck(f.StopCalls == 1 && f.RestoreCalls == 1 && f.RegistryCalls == 0, "locked data proceeded to registry deletion");
            ResetFlowCheck(File.ReadAllText(f.ProfilePath) == f.OriginalText[f.ProfilePath], "locked profile bytes changed");
            f.AssertRegistryPresent(); f.AssertForeignFiles();
        }

        private static void ResetFlowRegistryFailureIsNotSuccess(string root)
        {
            ResetFlowExpectRegistryRejected(root, "registry-false", delegate { return false; });
        }

        private static void ResetFlowRegistryExceptionIsNotSuccess(string root)
        {
            ResetFlowExpectRegistryRejected(root, "registry-throw", delegate { throw new IOException("isolated registry exception"); });
        }

        private static void ResetFlowExpectRegistryRejected(string root, string name, Func<bool> registry)
        {
            var f = new ResetFlowFixture(root, name);
            f.SetRegistry(delegate { f.AssertOwnedFilesGone(); return registry(); });
            int files;
            string failure;
            ResetFlowCheck(!Program.TryResetUserData(f.DirectoryPath, f.Stop(true), out files, out failure), "registry failure was reported successful");
            ResetFlowCheck(failure != null && failure.Contains("注册表") && files == f.OwnedPaths.Count, "registry failure result lost its phase or deleted-file count");
            ResetFlowCheck(f.StopCalls == 1 && f.RestoreCalls == 1 && f.RegistryCalls == 1, "registry failure retried or skipped earlier phases");
            f.AssertOwnedFilesGone(); f.AssertRegistryPresent(); f.AssertForeignFiles();
        }

        private static void ResetFlowPortableSuccessKeepsForeignFiles(string root)
        {
            var f = new ResetFlowFixture(root, "portable-success");
            int files;
            string failure;
            ResetFlowCheck(Program.TryResetUserData(f.DirectoryPath, f.Stop(true), out files, out failure), "portable reset failed: " + failure);
            ResetFlowCheck(failure == null && files == f.OwnedPaths.Count && Directory.Exists(f.DirectoryPath), "portable reset removed its whole directory or returned the wrong count");
            ResetFlowCheck(f.StopCalls == 1 && f.RestoreCalls == 1 && f.RegistryCalls == 1 && !f.MockRegistryPresent, "portable reset missed a phase");
            f.AssertOwnedFilesGone(); f.AssertForeignFiles();
        }

        private static void ResetFlowLateWritesCannotReviveData(string root)
        {
            var f = new ResetFlowFixture(root, "late-callback");
            string log = Path.Combine(f.DirectoryPath, "Pavise.log");
            string crash = Path.Combine(f.DirectoryPath, "crash.log");
            File.WriteAllText(log, "before reset\n");
            File.WriteAllText(crash, "before reset crash\n");
            Logger.LogPath = log;
            ResetFlowCheck(Settings.SaveStr("ResetFlowRetainedValue", "original"), "could not seed transient settings");
            bool lateBool = true, lateString = true;
            Exception workerError = null;
            using (var release = new ManualResetEvent(false))
            {
                var worker = new Thread(delegate()
                {
                    try
                    {
                        if (!release.WaitOne(3000)) throw new TimeoutException("Late callback was not released");
                        lateBool = Settings.Save("ResetFlowLateBool", true);
                        lateString = Settings.SaveStr("ResetFlowLateString", "forbidden");
                        Settings.Remove("ResetFlowRetainedValue");
                        Logger.Log("late callback must not recreate Pavise.log");
                        Logger.Clear();
                        Logger.AppendCrash(crash, "late exception must not recreate crash.log");
                    }
                    catch (Exception error) { workerError = error; }
                });
                worker.IsBackground = true;
                worker.Start();
                try
                {
                    int files;
                    string failure;
                    ResetFlowCheck(Program.TryResetUserData(f.DirectoryPath, f.Stop(true), out files, out failure), "reset before late callback failed: " + failure);
                    ResetFlowCheck(!File.Exists(log) && !File.Exists(crash), "active log/crash file was not deleted");
                    release.Set();
                    ResetFlowCheck(worker.Join(3000), "late callback did not finish");
                    ResetFlowCheck(workerError == null, "late callback threw: " + workerError);
                    ResetFlowCheck(!lateBool && !lateString, "late settings writes were accepted");
                    ResetFlowCheck(Settings.LoadStr("ResetFlowLateString", null) == null
                        && !Settings.Load("ResetFlowLateBool", false), "late callback recreated a setting");
                    ResetFlowCheck(Settings.LoadStr("ResetFlowRetainedValue", "") == "original", "late remove modified the sealed transient store");
                    ResetFlowCheck(!File.Exists(log) && !File.Exists(crash), "late log, clear or exception recreated data");
                    f.AssertOwnedFilesGone(); f.AssertForeignFiles();
                }
                finally { release.Set(); ResetFlowCheck(worker.Join(3000), "owned callback survived fixture teardown"); }
            }
        }

        private static void ResetFlowDriverAdmissionSealed(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-driver-admission"))
            {
                int writes = 0;
                Action mutation = delegate { writes++; };
                ResetFlowCheck((bool)FamilyPolicyInvoke(f.Mode, "RunPreStagedMutation", mutation), "idle fake driver mutation was rejected");
                FamilyPolicySetField(f.Mode, "active", true);
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "RunPreStagedMutation", mutation), "active-session pre-stage was admitted");
                FamilyPolicySetField(f.Mode, "active", false);
                FamilyPolicySetField(f.Mode, "stopping", true);
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "RunPreStagedMutation", mutation), "late driver pre-stage was admitted");
                ResetFlowCheck(writes == 1, "driver mutation ran after admission was closed");
            }
        }

        private static void ResetFlowDriverInFlightMustDrain(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-driver-drain"))
            {
                string before = File.ReadAllText(f.LibraryFile);
                bool applied = false;
                using (var worker = new ResetFlowBlockingWorker(delegate(Action hold)
                { applied = (bool)FamilyPolicyInvoke(f.Mode, "RunPreStagedMutation", hold); }))
                {
                    worker.WaitUntilHeld();
                    FamilyPolicySetField(f.Mode, "stopping", true);
                    ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 20), "in-flight driver callback was reported drained");
                    ResetFlowCheck(File.ReadAllText(f.LibraryFile) == before, "failed drain altered recovery/configuration bytes");
                    worker.Finish();
                    ResetFlowCheck(applied && (bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 250), "completed driver callback could not drain");
                }
                int lateWrites = 0;
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "RunPreStagedMutation", new Action(delegate { lateWrites++; }))
                    && lateWrites == 0, "completed drain reopened driver admission");
            }
        }

        private static void ResetFlowPowerAdmissionSealed(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-power-admission"))
            {
                FamilyPolicySetField(f.Mode, "powerSessionGen", 7);
                int writes = 0;
                Func<bool> apply = delegate { writes++; return true; };
                ResetFlowCheck((bool)FamilyPolicyInvoke(f.Mode, "RunPowerPlanApply", 7, apply), "current fake power mutation was rejected");
                long audit = (long)FamilyPolicyGetField(f.Mode, "nextPowerAuditTicks");
                ResetFlowCheck(audit == long.MaxValue,
                    "successful power apply left periodic ownership auditing armed");
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "RunPowerPlanApply", 6, apply), "stale power generation was admitted");
                FamilyPolicySetField(f.Mode, "stopping", true);
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "RunPowerPlanApply", 7, apply), "late power apply was admitted");
                ResetFlowCheck(writes == 1, "power gate ran an unexpected action");
            }
        }

        private static void ResetFlowPowerSchemeNotificationUsesLightweightAudit(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-power-notification"))
            {
                FamilyPolicySetField(f.Mode, "enabled", true);
                FamilyPolicySetField(f.Mode, "active", true);
                FamilyPolicySetField(f.Mode, "planActive", true);
                FamilyPolicySetField(f.Mode, "nextPowerAuditTicks", long.MaxValue);
                FamilyPolicySetField(f.Mode, "powerApplyInFlight", 1);
                FamilyPolicySetField(f.Mode, "powerPlanNotificationPending", 0);
                FamilyPolicySetField(f.Mode, "urgentProcessScan", 0);

                FamilyPolicyInvoke(f.Mode, "NotifyPowerSchemeChanged");

                ResetFlowCheck((int)FamilyPolicyGetField(f.Mode,
                    "powerPlanNotificationPending") == 1,
                    "active-plan notification was not coalesced for its lightweight audit");
                ResetFlowCheck((long)FamilyPolicyGetField(f.Mode, "nextPowerAuditTicks") == long.MaxValue
                    && (int)FamilyPolicyGetField(f.Mode, "urgentProcessScan") == 0,
                    "active-plan notification woke the full process policy scan");

                FamilyPolicySetField(f.Mode, "active", false);
                FamilyPolicySetField(f.Mode, "powerApplyInFlight", 0);
                FamilyPolicySetField(f.Mode, "powerPlanNotificationPending", 0);
                FamilyPolicySetField(f.Mode, "nextPowerAuditTicks", long.MaxValue);
                FamilyPolicySetField(f.Mode, "urgentProcessScan", 0);
                FamilyPolicyInvoke(f.Mode, "NotifyPowerSchemeChanged");
                ResetFlowCheck((long)FamilyPolicyGetField(f.Mode, "nextPowerAuditTicks") == long.MaxValue
                    && (int)FamilyPolicyGetField(f.Mode, "urgentProcessScan") == 0,
                    "idle notification woke power-plan ownership enforcement");
            }
        }

        private static void ResetFlowAutonomousMinimumPolicy(string root)
        {
            ResetFlowCheck(PowerPlan.AutonomousMinimumForTest(false, true, false) == 100
                && PowerPlan.AutonomousMinimumForTest(true, true, false) == 100,
                "legacy scaling no longer preserves the aggressive minimum");
            ResetFlowCheck(PowerPlan.AutonomousMinimumForTest(false, true, true) == 20
                && PowerPlan.AutonomousMinimumForTest(true, true, true) == 20,
                "autonomous AC scaling did not release the processor minimum");
            ResetFlowCheck(PowerPlan.AutonomousMinimumForTest(false, false, true) == 10
                && PowerPlan.AutonomousMinimumForTest(true, false, true) == 10,
                "autonomous DC scaling did not release the processor minimum");
        }

        private static void ResetFlowPowerLateResultIsNotPublished(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-power-result"))
            {
                FamilyPolicySetField(f.Mode, "powerSessionGen", 9);
                long originalAudit = (long)FamilyPolicyGetField(f.Mode, "nextPowerAuditTicks");
                bool accepted = true;
                int restored = 0;
                GameMode.PowerRestoreForTest = delegate { restored++; return true; };
                try
                {
                using (var worker = new ResetFlowBlockingWorker(delegate(Action hold)
                {
                    accepted = (bool)FamilyPolicyInvoke(f.Mode, "RunPowerPlanApply", 9,
                        new Func<bool>(delegate { hold(); return true; }));
                }))
                {
                    worker.WaitUntilHeld();
                    FamilyPolicySetField(f.Mode, "stopping", true);
                    ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 20), "in-flight power callback was reported drained");
                    worker.Finish();
                    ResetFlowCheck(!accepted && (long)FamilyPolicyGetField(f.Mode, "nextPowerAuditTicks") == originalAudit,
                        "power callback published success after shutdown began");
                    ResetFlowCheck(restored == 1 && !ActionFailureStore.IsIsolated(PolicyCatalog.KeyPowerPlan, "system"),
                        "cancelled late power apply did not verify restoration or was treated as an execution fault");
                    ResetFlowCheck((bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 250), "finished power callback did not drain");
                }
                }
                finally { GameMode.PowerRestoreForTest = null; }
            }
        }

        private static void ResetFlowDrainRejectsLiveAndReentrantCallers(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-reentrant-drain"))
            {
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 20), "live mode accepted permanent drain");
                FamilyPolicySetField(f.Mode, "stopping", true);
                foreach (string name in new[] { "driverStageGate", "powerApplyGate", "whiteEvalSync" })
                    lock (FamilyPolicyGetField(f.Mode, name))
                        ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 20), "self-owned gate was treated as drained: " + name);
                ResetFlowCheck((bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 250), "unheld shutdown gates did not drain");
            }
        }

        private static void ResetFlowDrainWaitsForOtherWriteGates(string root)
        {
            foreach (string name in new[] { "powerApplyGate", "whiteEvalSync" })
                using (var f = new FamilyPolicyFixture(root, "reset-held-" + name))
                {
                    string before = File.ReadAllText(f.LibraryFile);
                    object gate = FamilyPolicyGetField(f.Mode, name);
                    using (var worker = new ResetFlowBlockingWorker(delegate(Action hold) { lock (gate) hold(); }))
                    {
                        worker.WaitUntilHeld();
                        FamilyPolicySetField(f.Mode, "stopping", true);
                        ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 20), "held gate was treated as drained: " + name);
                        ResetFlowCheck(File.ReadAllText(f.LibraryFile) == before, "gate timeout altered game library");
                        worker.Finish();
                        ResetFlowCheck((bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 250), "released gate did not drain: " + name);
                    }
                }
        }

        private static void ResetFlowWhitelistCannotSaveAfterStopping(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-whitelist"))
            {
                string path = Path.Combine(f.DirectoryPath, "Pavise.whitelist.txt");
                const string before = "isolated whitelist recovery sentinel";
                File.WriteAllText(path, before);
                FamilyPolicySetField(f.Mode, "stopping", true);
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "SaveWhite", new List<WhitelistRule>()), "late whitelist file save was accepted");
                ResetFlowCheck(!f.Mode.ResetWhitelist(), "late whitelist reset was accepted");
                ResetFlowCheck(File.ReadAllText(path) == before, "late whitelist callback altered configuration");
            }
        }

        private static void ResetFlowStoreCloseRejectsLateMutation(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-closed-cache"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                ResetFlowCheck(FamilyObservationRecord(store, f.First), "could not seed observation cache");
                ResetFlowCheck(store.Close(250) && store.Close(0), "observation close was not successful/idempotent");
                string cache = Path.Combine(f.DirectoryPath, RendererObservationStore.FileName);
                File.Delete(cache);
                store.Forget(f.First.Id);
                store.Persist();
                ResetFlowCheck(!store.Validate(f.First.Id, f.First.ExecutablePath)
                    && !store.NeedsValidation(f.First.Id, f.First.ExecutablePath, DateTime.UtcNow.AddMinutes(1).Ticks),
                    "closed cache still admitted validation");
                ResetFlowCheck(!FamilyObservationRecord(store, f.Second), "closed cache accepted new evidence");
                ResetFlowCheck(store.Has(f.First.Id, f.First.ExecutablePath), "close destroyed its readable in-memory observation");
                ResetFlowCheck(!File.Exists(cache)
                    && Directory.GetFiles(f.DirectoryPath, RendererObservationStore.FileName + ".*.tmp").Length == 0,
                    "late cache callback recreated files");
            }
        }

        private static void ResetFlowStoreCloseTimeoutIsNotSuccess(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-cache-timeout"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                ResetFlowCheck(FamilyObservationRecord(store, f.First), "could not seed timed cache");
                object gate = typeof(RendererObservationStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
                using (var worker = new ResetFlowBlockingWorker(delegate(Action hold) { lock (gate) hold(); }))
                {
                    worker.WaitUntilHeld();
                    ResetFlowCheck(!store.Close(20), "cache close succeeded while a save gate was held");
                    worker.Finish();
                    ResetFlowCheck(FamilyObservationRecord(store, f.First), "failed cache close incorrectly claimed a permanent seal");
                    ResetFlowCheck(store.Close(250), "released cache save gate could not close");
                }
            }
        }

        private static void ResetFlowStoreCloseRejectsReentrantCaller(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-cache-self-close"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                object gate = typeof(RendererObservationStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store);
                lock (gate) ResetFlowCheck(!store.Close(20), "cache writer could claim its own callback was drained");
                ResetFlowCheck(!store.Close(-1) && store.Close(250), "invalid timeout or normal close contract failed");
            }
        }

        private static void ResetFlowRenderLaneRejectsStaleAndClosedWork(string root)
        {
            ResetFlowPrepareRenderLane();
            try
            {
                int generation = RenderLane.ShutdownGenerationForTest;
                int writes = 0;
                Func<bool> mutation = delegate { writes++; return true; };
                ResetFlowCheck(RenderLane.RunShutdownMutationForTest(generation, mutation), "current render-lane fake mutation was rejected");
                ResetFlowCheck(!RenderLane.RunShutdownMutationForTest(generation - 1, mutation), "stale render-lane generation was admitted");
                ResetFlowCheck(RenderLane.CloseForShutdown(250), "empty render-lane shutdown failed");
                ResetFlowCheck(!RenderLane.RunShutdownMutationForTest(RenderLane.ShutdownGenerationForTest, mutation)
                    && writes == 1, "render-lane shutdown reopened admission");
            }
            finally { RenderLane.ResetShutdownForTest(); }
        }

        private static void ResetFlowRenderLaneSessionEndCancelsSampling(string root)
        {
            ResetFlowPrepareRenderLane();
            try
            {
                using (var f = new FamilyPolicyFixture(root, "lane-pending-session-end"))
                {
                    int generation = RenderLane.ShutdownGenerationForTest;
                    typeof(RenderLane).GetField("trackPid", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 42);
                    typeof(RenderLane).GetField("trackCreation", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, 100L);
                    typeof(RenderLane).GetField("trackGen", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, generation);
                    Eq(LaneState.Trying, RenderLane.StateFor(42, 100));
                    Eq(true, RenderLane.ReleaseExcept(42, 100));
                    Eq(generation, RenderLane.ShutdownGenerationForTest);
                    MethodInfo end = typeof(GameMode).GetMethod("UnboostGames", BindingFlags.Instance | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                    Eq(true, (bool)end.Invoke(f.Mode, null));
                    Eq(LaneState.Idle, RenderLane.StateFor(42, 100));
                    Eq(false, RenderLane.RunShutdownMutationForTest(generation, delegate { throw new Exception("Stale lane mutation admitted"); }));
                }
            }
            finally { RenderLane.ResetShutdownForTest(); }
        }

        private static void ResetFlowRenderLaneDrainKeepsJournal(string root)
        {
            ResetFlowPrepareRenderLane();
            const string journal = "123|456|789|0";
            ResetFlowCheck(Settings.SaveStr("RenderLane", journal), "could not seed transient render-lane journal");
            int restores = 0;
            RenderLane.RestoreThreadForTest = delegate(int pid, long creation, int tid, int priority)
            {
                ResetFlowCheck(pid == 123 && creation == 456 && tid == 789 && priority == 0, "render-lane restore identity changed");
                restores++; return true;
            };
            try
            {
                int generation = RenderLane.ShutdownGenerationForTest;
                using (var worker = new ResetFlowBlockingWorker(delegate(Action hold)
                {
                    ResetFlowCheck(RenderLane.RunShutdownMutationForTest(generation,
                        delegate { hold(); return true; }), "in-flight render-lane fake mutation failed");
                }))
                {
                    worker.WaitUntilHeld();
                    ResetFlowCheck(!RenderLane.CloseForShutdown(20), "held render-lane mutation was reported drained");
                    ResetFlowCheck(restores == 0 && Settings.LoadStr("RenderLane", "") == journal,
                        "render-lane timeout restored or discarded its journal before the writer exited");
                    worker.Finish();
                    ResetFlowCheck(RenderLane.CloseForShutdown(250) && restores == 1
                        && Settings.LoadStr("RenderLane", "") == "", "drained render-lane journal was not restored/cleared");
                    int late = 0;
                    ResetFlowCheck(!RenderLane.RunShutdownMutationForTest(generation, delegate { late++; return true; })
                        && late == 0, "old render-lane task wrote after cleanup");
                }
            }
            finally { RenderLane.ResetShutdownForTest(); }
        }

        private static void ResetFlowRenderLaneRestoreFailureKeepsJournal(string root)
        {
            ResetFlowPrepareRenderLane();
            const string journal = "123|456|789|0";
            ResetFlowCheck(Settings.SaveStr("RenderLane", journal), "could not seed failed render-lane journal");
            try
            {
                RenderLane.RestoreThreadForTest = delegate { return false; };
                ResetFlowCheck(!RenderLane.CloseForShutdown(250) && Settings.LoadStr("RenderLane", "") == journal,
                    "failed thread restoration lost its only original snapshot");
                RenderLane.RestoreThreadForTest = delegate { return true; };
                ResetFlowCheck(RenderLane.CloseForShutdown(250) && Settings.LoadStr("RenderLane", "") == "",
                    "confirmed fake restoration could not finish a previously failed close");
            }
            finally { RenderLane.ResetShutdownForTest(); }
        }

        private static void ResetFlowRenderLaneInvalidJournalIsNotSuccess(string root)
        {
            ResetFlowPrepareRenderLane();
            const string journal = "invalid-owned-test-journal";
            ResetFlowCheck(Settings.SaveStr("RenderLane", journal), "could not seed invalid transient journal");
            int restores = 0;
            RenderLane.RestoreThreadForTest = delegate { restores++; return true; };
            try
            {
                int priorityAccesses = 0;
                ResetFlowCheck(RenderLane.TryPinForTest(42, 100, 111, RenderLane.ShutdownGenerationForTest,
                    delegate { priorityAccesses++; return 0; }, delegate { priorityAccesses++; return true; })
                    == RenderLane.PinOutcome.Retryable && priorityAccesses == 0,
                    "invalid journal admitted a new pin");
                ResetFlowCheck(!RenderLane.CloseForShutdown(250) && restores == 0
                    && Settings.LoadStr("RenderLane", "") == journal,
                    "invalid render-lane journal was discarded or sent to a native restore");
            }
            finally { RenderLane.ResetShutdownForTest(); }
        }

        private static void ResetFlowRenderLaneAppliedFailureKeepsState(string root)
        {
            ResetFlowPrepareRenderLane();
            const string journal = "123|456|789|0";
            ResetFlowCheck(Settings.SaveStr("RenderLane", journal), "could not seed active transient journal");
            ResetFlowSetStatic(typeof(RenderLane), "laneApplied", true);
            ResetFlowSetStatic(typeof(RenderLane), "lanePid", 123);
            ResetFlowSetStatic(typeof(RenderLane), "laneCreation", 456L);
            ResetFlowSetStatic(typeof(RenderLane), "laneTid", 789);
            ResetFlowSetStatic(typeof(RenderLane), "laneOriginalPriority", 0);
            try
            {
                RenderLane.RestoreThreadForTest = delegate { return false; };
                ResetFlowCheck(!RenderLane.CloseForShutdown(250)
                    && (bool)ResetFlowGetStatic(typeof(RenderLane), "laneApplied")
                    && Settings.LoadStr("RenderLane", "") == journal, "failed applied-lane restore cleared live state or journal");
                RenderLane.RestoreThreadForTest = delegate { return true; };
                ResetFlowCheck(RenderLane.CloseForShutdown(250)
                    && !(bool)ResetFlowGetStatic(typeof(RenderLane), "laneApplied")
                    && Settings.LoadStr("RenderLane", "") == "", "successful fake lane restore retained applied state");
            }
            finally { RenderLane.ResetShutdownForTest(); }
        }

        private static void ResetFlowRenderLaneRollbackDebtBlocksNewPin(string root)
        {
            ResetFlowPrepareRenderLane();
            var priorities = new Dictionary<int, int> { { 111, 0 }, { 222, 0 } };
            bool allowRestore = false;
            RenderLane.RestoreThreadForTest = delegate(int pid, long creation, int tid, int original)
            {
                if (!allowRestore) return false;
                priorities[tid] = original;
                return true;
            };
            try
            {
                int generation = RenderLane.ShutdownGenerationForTest;
                int reads = 0;
                RenderLane.PinOutcome first = RenderLane.TryPinForTest(42, 100, 111, generation,
                    delegate { return ++reads == 2 ? Native.THREAD_PRIORITY_ERROR_RETURN : priorities[111]; },
                    delegate(int priority)
                    {
                        if (priority == 0) return false;
                        priorities[111] = priority; return true;
                    });
                const string originalJournal = "42|100|111|0";
                ResetFlowCheck(first == RenderLane.PinOutcome.Retryable && priorities[111] == Native.THREAD_PRIORITY_HIGHEST
                    && Settings.LoadStr("RenderLane", "") == originalJournal, "failed pin rollback did not preserve the original");
                int newReads = 0, newWrites = 0;
                Func<int> readNew = delegate { newReads++; return priorities[222]; };
                Func<int, bool> writeNew = delegate(int priority) { newWrites++; priorities[222] = priority; return true; };
                ResetFlowCheck(RenderLane.TryPinForTest(42, 100, 222, generation, readNew, writeNew)
                    == RenderLane.PinOutcome.Retryable && newReads == 0 && newWrites == 0
                    && Settings.LoadStr("RenderLane", "") == originalJournal,
                    "new pin overwrote unresolved rollback debt");
                allowRestore = true;
                ResetFlowCheck(RenderLane.TryPinForTest(42, 100, 222, generation, readNew, writeNew)
                    == RenderLane.PinOutcome.Pinned && priorities[111] == 0
                    && Settings.LoadStr("RenderLane", "") == "42|100|222|0",
                    "new pin was not admitted after confirmed recovery");
                ResetFlowCheck(RenderLane.Release() && priorities[111] == 0 && priorities[222] == 0
                    && !RenderLane.HasResidue(), "recovered pin sequence left a modified thread behind");
            }
            finally { RenderLane.ResetShutdownForTest(); }
        }

        private static void ResetFlowRenderLaneAppliedDebtBlocksNewPin(string root)
        {
            ResetFlowPrepareRenderLane();
            int oldPriority = 0, newPriority = 0, newWrites = 0;
            bool allowRestore = false;
            RenderLane.RestoreThreadForTest = delegate(int pid, long creation, int tid, int original)
            {
                if (!allowRestore) return false;
                if (tid == 111) oldPriority = original;
                else if (tid == 222) newPriority = original;
                else throw new InvalidOperationException("Unexpected fake thread");
                return true;
            };
            try
            {
                int generation = RenderLane.ShutdownGenerationForTest;
                ResetFlowCheck(RenderLane.TryPinForTest(42, 100, 111, generation,
                    delegate { return oldPriority; }, delegate(int priority) { oldPriority = priority; return true; })
                    == RenderLane.PinOutcome.Pinned, "could not establish in-memory active pin");
                ResetFlowCheck(RenderLane.TryPinForTest(43, 200, 222, generation,
                    delegate { return newPriority; }, delegate(int priority) { newWrites++; newPriority = priority; return true; })
                    == RenderLane.PinOutcome.Retryable && newWrites == 0
                    && RenderLane.IsActiveFor(42, 100) && Settings.LoadStr("RenderLane", "") == "42|100|111|0",
                    "failed restoration of an applied lane was replaced by another game");
                allowRestore = true;
                ResetFlowCheck(RenderLane.TryPinForTest(43, 200, 222, generation,
                    delegate { return newPriority; }, delegate(int priority) { newWrites++; newPriority = priority; return true; })
                    == RenderLane.PinOutcome.Pinned && oldPriority == 0 && RenderLane.IsActiveFor(43, 200),
                    "confirmed old lane restoration invalidated the new generation");
                ResetFlowCheck(RenderLane.Release() && newPriority == 0 && !RenderLane.HasResidue(),
                    "second in-memory game was not restored");
            }
            finally { RenderLane.ResetShutdownForTest(); }
        }

        private static void ResetFlowRenderLaneChangedJournalIsNotOverwritten(string root)
        {
            ResetFlowPrepareRenderLane();
            const string otherJournal = "123|456|789|0";
            try
            {
                int writes = 0;
                int generation = RenderLane.ShutdownGenerationForTest;
                ResetFlowCheck(RenderLane.TryPinForTest(42, 100, 111, generation,
                    delegate { Settings.SaveStr("RenderLane", otherJournal); return 0; },
                    delegate { writes++; return true; }) == RenderLane.PinOutcome.Retryable
                    && writes == 0 && Settings.LoadStr("RenderLane", "") == otherJournal,
                    "a journal appearing before the setter was overwritten");
                RenderLane.RestoreThreadForTest = delegate
                {
                    Settings.SaveStr("RenderLane", "321|654|987|1"); return true;
                };
                int reads = 0;
                ResetFlowCheck(RenderLane.TryPinForTest(42, 100, 111, generation,
                    delegate { reads++; return 0; }, delegate { writes++; return true; })
                    == RenderLane.PinOutcome.Retryable && reads == 0 && writes == 0
                    && Settings.LoadStr("RenderLane", "") == "321|654|987|1",
                    "restoring one journal cleared a replacement record");
            }
            finally { RenderLane.ResetShutdownForTest(); }
        }

        private static void ResetFlowRenderLaneUnreadableLedgerBlocksWork(string root)
        {
            const string journal = "42|100|111|0";
            for (int active = 0; active < 2; active++)
            {
                ResetFlowPrepareRenderLane();
                Settings.SaveStr("RenderLane", journal);
                if (active != 0)
                {
                    ResetFlowSetStatic(typeof(RenderLane), "laneApplied", true);
                    ResetFlowSetStatic(typeof(RenderLane), "lanePid", 42);
                    ResetFlowSetStatic(typeof(RenderLane), "laneCreation", 100L);
                    ResetFlowSetStatic(typeof(RenderLane), "laneTid", 111);
                    ResetFlowSetStatic(typeof(RenderLane), "laneOriginalPriority", 0);
                }
                using (var reads = new ResetFlowStrictReadScope("RenderLane"))
                {
                    int restores = 0, accesses = 0, oldPriority = Native.THREAD_PRIORITY_HIGHEST, nextPriority = 0;
                    RenderLane.RestoreThreadForTest = delegate(int pid, long creation, int tid, int original)
                    {
                        restores++;
                        if (tid == 111) oldPriority = original;
                        else if (tid == 222) nextPriority = original;
                        else throw new Exception("Unexpected in-memory thread");
                        return true;
                    };
                    try
                    {
                        reads.Deny = true;
                        ResetFlowCheck(RenderLane.HasResidue() && !RenderLane.Release() && restores == 0,
                            "an unreadable lane ledger was acknowledged or sent to a restore");
                        int generation = RenderLane.ShutdownGenerationForTest;
                        ResetFlowCheck(RenderLane.TryPinForTest(43, 200, 222, generation,
                            delegate { accesses++; return nextPriority; },
                            delegate(int priority) { accesses++; nextPriority = priority; return true; })
                            == RenderLane.PinOutcome.Retryable && accesses == 0 && restores == 0
                            && Settings.LoadStr("RenderLane", "") == journal
                            && oldPriority == Native.THREAD_PRIORITY_HIGHEST,
                            "an unreadable prior journal admitted a new pin or lost its original");
                        reads.Deny = false;
                        ResetFlowCheck(RenderLane.TryPinForTest(43, 200, 222, generation,
                            delegate { return nextPriority; }, delegate(int priority) { nextPriority = priority; return true; })
                            == RenderLane.PinOutcome.Pinned && oldPriority == 0
                            && Settings.LoadStr("RenderLane", "") == "43|200|222|0",
                            "restored read access did not recover the old lane before pinning");
                        ResetFlowCheck(RenderLane.Release() && nextPriority == 0 && !RenderLane.HasResidue(),
                            "lane retry could not finish restoration after read access returned");
                    }
                    finally { RenderLane.ResetShutdownForTest(); }
                }
            }
        }

        private static void ResetFlowRenderLaneJournalReadbackFailureBlocksPin(string root)
        {
            const string journal = "42|100|111|0";
            for (int phase = 0; phase < 2; phase++)
            {
                ResetFlowPrepareRenderLane();
                Settings.SaveStr("RenderLane", "");
                using (var reads = new ResetFlowStrictReadScope("RenderLane"))
                {
                    int priority = 0, writes = 0;
                    bool inject = true;
                    reads.OnRead = delegate
                    {
                        if (inject && phase == 1 && Settings.LoadStr("RenderLane", "") == journal) reads.Deny = true;
                    };
                    RenderLane.RestoreThreadForTest = delegate(int pid, long creation, int tid, int original)
                    { priority = original; return true; };
                    try
                    {
                        int generation = RenderLane.ShutdownGenerationForTest;
                        ResetFlowCheck(RenderLane.TryPinForTest(42, 100, 111, generation,
                            delegate { if (phase == 0) reads.Deny = true; return priority; },
                            delegate(int value) { writes++; priority = value; return true; })
                            == RenderLane.PinOutcome.Retryable && writes == 0 && RenderLane.HasResidue()
                            && Settings.LoadStr("RenderLane", "") == (phase == 0 ? "" : journal),
                            "a failed journal admission/readback still changed thread priority");
                        inject = false;
                        reads.Deny = false;
                        ResetFlowCheck(RenderLane.TryPinForTest(42, 100, 111, generation,
                            delegate { return priority; }, delegate(int value) { writes++; priority = value; return true; })
                            == RenderLane.PinOutcome.Pinned && writes == 1,
                            "a pin could not retry after journal reads recovered");
                        ResetFlowCheck(RenderLane.Release() && priority == 0 && !RenderLane.HasResidue(),
                            "journal readback retry left priority or recovery data behind");
                    }
                    finally { RenderLane.ResetShutdownForTest(); }
                }
            }
        }

        private static void ResetFlowRenderLaneClearReadFailureDoesNotSucceed(string root)
        {
            const string journal = "42|100|111|0";
            for (int afterClear = 0; afterClear < 2; afterClear++)
            {
                ResetFlowPrepareRenderLane();
                Settings.SaveStr("RenderLane", journal);
                using (var reads = new ResetFlowStrictReadScope("RenderLane"))
                {
                    int restores = 0, priority = Native.THREAD_PRIORITY_HIGHEST;
                    bool inject = true;
                    reads.OnRead = delegate
                    {
                        if (inject && afterClear != 0 && restores > 0
                            && Settings.LoadStr("RenderLane", "") == "") reads.Deny = true;
                    };
                    RenderLane.RestoreThreadForTest = delegate(int pid, long creation, int tid, int original)
                    {
                        restores++; priority = original;
                        if (inject && afterClear == 0) reads.Deny = true;
                        return true;
                    };
                    try
                    {
                        ResetFlowCheck(!RenderLane.Release() && restores == 1 && priority == 0
                            && RenderLane.HasResidue()
                            && Settings.LoadStr("RenderLane", "") == (afterClear == 0 ? journal : ""),
                            "an unreadable journal clear or clear readback was reported complete");
                        ResetFlowCheck(!RenderLane.Release() && restores == 1,
                            "a failed strict read admitted another restore");
                        inject = false;
                        reads.Deny = false;
                        ResetFlowCheck(RenderLane.Release() && !RenderLane.HasResidue(),
                            "a confirmed restoration could not finish after journal reads recovered");
                    }
                    finally { RenderLane.ResetShutdownForTest(); }
                }
            }
        }

        private static void ResetFlowFsoTrackingFailureDoesNotWrite(string root)
        {
            using (var f = new ResetFlowFsoStore())
            {
                f.Layers[ResetFlowFsoStore.Exe] = "~ RUNASADMIN";
                const string otherExe = @"C:\PaviseResetFixture\Other.exe";
                Settings.SaveStr("FsoExeList", otherExe);
                f.SaveTracked = delegate { return false; };
                ResetFlowCheck(!FsoTweak.SetForExe(ResetFlowFsoStore.Exe, true)
                    && f.Writes == 0 && f.Layers[ResetFlowFsoStore.Exe] == "~ RUNASADMIN"
                    && Settings.LoadStr("FsoExeList", "") == otherExe,
                    "failed FSO ledger persistence changed Windows or another recovery entry");
                f.SaveTracked = delegate { return true; };
                ResetFlowCheck(!FsoTweak.SetForExe(ResetFlowFsoStore.Exe, true) && f.Writes == 0,
                    "FSO trusted a successful save without the matching persisted record");
            }
        }

        private static void ResetFlowFsoExistingUserTokenIsNotOwned(string root)
        {
            using (var f = new ResetFlowFsoStore())
            {
                string original = "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE";
                f.Layers[ResetFlowFsoStore.Exe] = original;
                ResetFlowCheck(FsoTweak.SetForExe(ResetFlowFsoStore.Exe, true) && f.Writes == 0
                    && !FsoTweak.HasResidue(), "an existing user FSO preference became Pavise-owned");
                ResetFlowCheck(FsoTweak.RestoreAll() && f.Layers[ResetFlowFsoStore.Exe] == original,
                    "reset removed a compatibility token Pavise did not add");
            }
        }

        private static void ResetFlowFsoTracksBeforeWritingAndPreservesOthers(string root)
        {
            using (var f = new ResetFlowFsoStore())
            {
                f.Layers[ResetFlowFsoStore.Exe] = "~ RUNASADMIN";
                f.BeforeWrite = delegate
                {
                    ResetFlowCheck(Array.IndexOf(FsoTweak.TrackedExes(), ResetFlowFsoStore.Exe) >= 0,
                        "FSO modified its compatibility layer before persisting ownership");
                };
                ResetFlowCheck(FsoTweak.SetForExe(ResetFlowFsoStore.Exe, true)
                    && FsoTweak.IsDisabledForExe(ResetFlowFsoStore.Exe), "in-memory FSO apply failed");
                f.Layers[ResetFlowFsoStore.Exe] += " HIGHDPIAWARE";
                ResetFlowCheck(FsoTweak.RestoreAll() && !FsoTweak.HasResidue()
                    && f.Layers[ResetFlowFsoStore.Exe] == "~ RUNASADMIN HIGHDPIAWARE",
                    "FSO cleanup damaged another compatibility setting or retained ownership");
            }
        }

        private static void ResetFlowFsoPartialWriteRollsBackOwnedToken(string root)
        {
            using (var f = new ResetFlowFsoStore())
            {
                f.Layers[ResetFlowFsoStore.Exe] = "~ RUNASADMIN";
                f.AfterWrite = delegate
                {
                    if (f.Writes != 1) return;
                    f.Layers[ResetFlowFsoStore.Exe] += " HIGHDPIAWARE";
                    throw new IOException("simulated partial compatibility-layer write");
                };
                ResetFlowCheck(!FsoTweak.SetForExe(ResetFlowFsoStore.Exe, true) && f.Writes == 2
                    && f.Layers[ResetFlowFsoStore.Exe] == "~ RUNASADMIN HIGHDPIAWARE"
                    && !FsoTweak.HasResidue(), "FSO failed write did not roll back only its own token");
            }
        }

        private static void ResetFlowFsoFailedRollbackKeepsRecovery(string root)
        {
            using (var f = new ResetFlowFsoStore())
            {
                bool blockRestore = true;
                f.BeforeWrite = delegate
                {
                    if (f.Writes > 1 && blockRestore) throw new UnauthorizedAccessException("simulated rollback denial");
                };
                f.AfterWrite = delegate
                {
                    if (f.Writes == 1) throw new IOException("simulated apply failure after write");
                };
                ResetFlowCheck(!FsoTweak.SetForExe(ResetFlowFsoStore.Exe, true)
                    && FsoTweak.IsDisabledForExe(ResetFlowFsoStore.Exe) && FsoTweak.HasResidue(),
                    "failed FSO rollback lost the recovery record");
                ResetFlowCheck(!FsoTweak.RestoreAll() && FsoTweak.HasResidue(),
                    "FSO reset acknowledged an unconfirmed rollback");
                blockRestore = false;
                ResetFlowCheck(FsoTweak.RestoreAll() && !FsoTweak.IsDisabledForExe(ResetFlowFsoStore.Exe)
                    && !FsoTweak.HasResidue(), "FSO recovery could not retry after access returned");
            }
        }

        private static void ResetFlowFsoRemovalReadFailureKeepsRecovery(string root)
        {
            using (var f = new ResetFlowFsoStore())
            {
                ResetFlowCheck(FsoTweak.SetForExe(ResetFlowFsoStore.Exe, true), "could not seed in-memory FSO preference");
                f.AfterWrite = delegate { f.DenyReads = true; };
                ResetFlowCheck(!FsoTweak.RestoreAll() && FsoTweak.HasResidue(),
                    "a denied FSO readback was treated as confirmed token removal");
                f.DenyReads = false;
                f.AfterWrite = null;
                ResetFlowCheck(FsoTweak.RestoreAll() && !FsoTweak.HasResidue(),
                    "FSO cleanup could not finish after read access returned");
            }
        }

        private static void ResetFlowFsoLedgerCleanupFailureIsNotSuccess(string root)
        {
            using (var f = new ResetFlowFsoStore())
            {
                ResetFlowCheck(FsoTweak.SetForExe(ResetFlowFsoStore.Exe, true), "could not seed tracked FSO preference");
                f.SaveTracked = delegate { return false; };
                ResetFlowCheck(!FsoTweak.RestoreAll() && FsoTweak.HasResidue()
                    && !FsoTweak.IsDisabledForExe(ResetFlowFsoStore.Exe),
                    "FSO cleanup reported success while its recovery ledger was not cleared");
                int writes = f.Writes;
                f.SaveTracked = null;
                ResetFlowCheck(FsoTweak.RestoreAll() && !FsoTweak.HasResidue() && f.Writes == writes,
                    "FSO cleanup retry changed an already restored compatibility layer");
            }
        }

        private static void ResetFlowFsoUnreadableLedgerPreservesAllEntries(string root)
        {
            const string first = ResetFlowFsoStore.Exe;
            const string second = @"C:\PaviseResetFixture\Second.exe";
            const string third = @"C:\PaviseResetFixture\Third.exe";
            string ledger = first + "\u001F" + second;
            using (var f = new ResetFlowFsoStore())
            using (var reads = new ResetFlowStrictReadScope("FsoExeList"))
            {
                f.Layers[first] = "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE";
                f.Layers[second] = "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE";
                f.Layers[third] = "~ RUNASADMIN";
                Settings.SaveStr("FsoExeList", ledger);
                reads.Deny = true;
                ResetFlowCheck(FsoTweak.HasResidue() && !FsoTweak.RestoreAll()
                    && !FsoTweak.SetForExe(third, true) && !FsoTweak.SetForExe(first, false)
                    && f.Writes == 0 && Settings.LoadStr("FsoExeList", "") == "!unreadable-recovery-record!",
                    "an unreadable FSO list was treated as empty or changed a compatibility layer");
                reads.Deny = false;
                ResetFlowCheck(Settings.LoadStr("FsoExeList", "") == ledger
                    && f.Layers[first] == "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE"
                    && f.Layers[second] == "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE"
                    && f.Layers[third] == "~ RUNASADMIN",
                    "unreadable FSO authority lost its ledger or changed compatibility flags");
                int attempts = 0;
                reads.OnRead = delegate { if (++attempts == 2) throw new IOException("ledger changed availability during tracking"); };
                ResetFlowCheck(!FsoTweak.SetForExe(third, true) && f.Writes == 0
                    && Settings.LoadStr("FsoExeList", "") == ledger,
                    "a failed ownership re-read discarded other FSO entries");
                reads.OnRead = null;
                ResetFlowCheck(FsoTweak.SetForExe(third, true)
                    && Settings.LoadStr("FsoExeList", "") == ledger + "\u001F" + third,
                    "FSO could not retry without overwriting existing ownership");
                ResetFlowCheck(FsoTweak.RestoreAll() && !FsoTweak.HasResidue()
                    && f.Layers[first] == "~ RUNASADMIN" && f.Layers[second] == "~ HIGHDPIAWARE"
                    && f.Layers[third] == "~ RUNASADMIN", "FSO recovery damaged other flags or lost an entry");
            }
        }

        private static void ResetFlowFsoLedgerReadbackFailureBlocksApply(string root)
        {
            const string other = @"C:\PaviseResetFixture\Other.exe";
            using (var f = new ResetFlowFsoStore())
            using (var reads = new ResetFlowStrictReadScope("FsoExeList"))
            {
                f.Layers[other] = "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE";
                Settings.SaveStr("FsoExeList", other);
                f.SaveTracked = delegate(string value)
                {
                    bool saved = Settings.SaveStr("FsoExeList", value);
                    reads.Deny = true;
                    return saved;
                };
                ResetFlowCheck(!FsoTweak.SetForExe(ResetFlowFsoStore.Exe, true) && f.Writes == 0
                    && FsoTweak.HasResidue()
                    && Settings.LoadStr("FsoExeList", "") == "!unreadable-recovery-record!",
                    "unconfirmed FSO ledger persistence admitted a system write or lost another entry");
                reads.Deny = false;
                ResetFlowCheck(Settings.LoadStr("FsoExeList", "") == other + "\u001F" + ResetFlowFsoStore.Exe,
                    "unconfirmed FSO authority lost another entry");
                f.SaveTracked = null;
                ResetFlowCheck(FsoTweak.SetForExe(ResetFlowFsoStore.Exe, true) && f.Writes == 1,
                    "FSO could not reuse its pending ownership after strict reads recovered");
                ResetFlowCheck(FsoTweak.RestoreAll() && !FsoTweak.HasResidue()
                    && f.Layers[other] == "~ HIGHDPIAWARE", "FSO pending ownership retry was not reversible");
            }
        }

        private static void ResetFlowFsoLedgerReadFailureDuringCleanup(string root)
        {
            const string first = ResetFlowFsoStore.Exe;
            const string second = @"C:\PaviseResetFixture\Second.exe";
            string ledger = first + "\u001F" + second;
            for (int afterClear = 0; afterClear < 2; afterClear++)
            {
                using (var f = new ResetFlowFsoStore())
                using (var reads = new ResetFlowStrictReadScope("FsoExeList"))
                {
                    f.Layers[first] = "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE";
                    f.Layers[second] = "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE";
                    Settings.SaveStr("FsoExeList", ledger);
                    f.AfterWrite = delegate { if (afterClear == 0) reads.Deny = true; };
                    f.SaveTracked = delegate(string value)
                    {
                        bool saved = Settings.SaveStr("FsoExeList", value);
                        if (afterClear != 0 && value.Length == 0) reads.Deny = true;
                        return saved;
                    };
                    ResetFlowCheck(!FsoTweak.RestoreAll() && FsoTweak.HasResidue()
                        && Settings.LoadStr("FsoExeList", "") == "!unreadable-recovery-record!"
                        && f.Writes == (afterClear == 0 ? 1 : 2),
                        "FSO cleanup claimed success on an unreadable ledger or clear readback");
                    reads.Deny = false;
                    ResetFlowCheck(Settings.LoadStr("FsoExeList", "") == (afterClear == 0 ? ledger : ""),
                        "FSO cleanup lost its pending authority or changed a confirmed clear");
                    f.AfterWrite = null;
                    f.SaveTracked = null;
                    ResetFlowCheck(FsoTweak.RestoreAll() && !FsoTweak.HasResidue() && f.Writes == 2
                        && f.Layers[first] == "~ RUNASADMIN" && f.Layers[second] == "~ HIGHDPIAWARE",
                        "FSO could not finish cleanup without repeating confirmed layer removals");
                }
            }
        }

        private sealed class ResetFlowStrictReadScope : IDisposable
        {
            internal bool Deny;
            internal Action OnRead;
            private readonly Action<string> previous = Settings.BeforeStrictStringReadForTest;

            internal ResetFlowStrictReadScope(string key)
            {
                Settings.BeforeStrictStringReadForTest = delegate(string name)
                {
                    if (previous != null) previous(name);
                    if (name != key) return;
                    if (OnRead != null) OnRead();
                    if (Deny) throw new IOException("simulated strict settings read failure");
                };
            }
            public void Dispose() { Settings.BeforeStrictStringReadForTest = previous; }
        }
        private sealed class ResetFlowFsoStore : IDisposable
        {
            internal const string Exe = @"C:\PaviseResetFixture\Game One; Custom.exe";
            internal readonly Dictionary<string, string> Layers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal int Writes;
            internal bool DenyReads;
            internal Action BeforeWrite, AfterWrite;
            internal Func<string, bool> SaveTracked;
            private readonly Func<string, string> oldRead = FsoTweak.ReadLayerForTest;
            private readonly Action<string, string> oldWrite = FsoTweak.WriteLayerForTest;
            private readonly Func<string, bool> oldSave = FsoTweak.SaveTrackedForTest;

            internal ResetFlowFsoStore()
            {
                FsoTweak.ReadLayerForTest = delegate(string path)
                {
                    if (DenyReads) throw new UnauthorizedAccessException("simulated layer read denial");
                    string value; return Layers.TryGetValue(path, out value) ? value : null;
                };
                FsoTweak.WriteLayerForTest = delegate(string path, string value)
                {
                    Writes++;
                    if (BeforeWrite != null) BeforeWrite();
                    Layers[path] = value;
                    if (AfterWrite != null) AfterWrite();
                };
                FsoTweak.SaveTrackedForTest = delegate(string value)
                {
                    return SaveTracked == null ? Settings.SaveStr("FsoExeList", value) : SaveTracked(value);
                };
            }

            public void Dispose()
            {
                FsoTweak.ReadLayerForTest = oldRead;
                FsoTweak.WriteLayerForTest = oldWrite;
                FsoTweak.SaveTrackedForTest = oldSave;
            }
        }

        private static void ResetFlowPrepareRenderLane()
        {
            RenderLane.ResetShutdownForTest();
            RenderLane.RestoreThreadForTest = delegate { throw new InvalidOperationException("Unexpected thread restore in reset test"); };
        }

        private static void ResetFlowUiSet(PanelForm form, string name, object value)
        {
            typeof(PanelForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, value);
        }

        private static object ResetFlowUiCall(PanelForm form, string name, params object[] args)
        {
            return typeof(PanelForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, args);
        }

        private static void ResetFlowRetiredPolicies(string root)
        {
            var profile = new GameProfile { Id = "retired-idle" };
            foreach (string key in new[] { "GmDisableCpuIdle", "GmDisableCpuIdleV2", "GmAudioLowLatV1", "GmMmcss" })
            {
                Settings.Save(key, true);
                profile.Overrides[key] = "1";
                ResetFlowCheck(PolicyCatalog.ItemOf(key) == null
                    && !PolicyResolver.SetOverride(profile, key, "1"),
                    "retired setting was still accepted");
            }
            profile.Overrides[PolicyCatalog.KeyCacheWarm] = "1";
            PolicyResolver.Sanitize(profile);
            ResetFlowCheck(profile.Overrides.Count == 1
                && profile.Overrides.ContainsKey(PolicyCatalog.KeyCacheWarm),
                "legacy overrides survived sanitization or a remaining feature was removed");
            ResetFlowCheck(typeof(GameMode).GetProperty("DisableCpuIdle") == null
                && typeof(PolicySnapshot).GetProperty("DisableCpuIdle") == null
                && typeof(PowerPlan).GetMethod("TryDisableCpuIdle", BindingFlags.Static | BindingFlags.NonPublic) == null
                && typeof(PanelForm).GetField("swPolicyDisableCpuIdle", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                "retired idle activation or UI API remains");
            ResetFlowCheck(typeof(GameMode).GetProperty("IdlePolicyOn") == null
                && typeof(GameMode).GetProperty("MmcssOn") == null
                && typeof(PolicySnapshot).GetProperty("IdlePolicy") != null
                && typeof(Mmcss).GetMethod("Activate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null
                && typeof(PanelForm).GetField("swPolicyIdlePolicy", BindingFlags.Instance | BindingFlags.NonPublic) == null
                && typeof(PanelForm).GetField("swPolicyMmcss", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                "retired idle controls returned or automatic MMCSS activation is missing");
        }

        private static void ResetFlowLegacyIdleRecovery(string root)
        {
            foreach (bool active in new[] { true, false })
            using (var f = new ResetFlowCpuIdleFixture())
            {
                f.Own();
                if (!active) f.Current = f.SecondScheme;
                ResetFlowCheck(PowerPlan.RestoreLegacyCpuIdle() && !PowerPlan.LegacyCpuIdleHasResidue
                    && f.Values[f.FirstScheme] == 0 && f.Values[f.SecondScheme] == 1
                    && f.Writes.Count == 1 && f.Writes[0].Value == 0
                    && f.Activations.Count == (active ? 1 : 0),
                    "legacy owned receipt did not restore only its captured plan");
                ResetFlowCheck(PowerPlan.RestoreLegacyCpuIdle() && f.Writes.Count == 1,
                    "completed legacy recovery repeated its write");
            }
            foreach (string phase in new[] { "2|P", "1|A", "2|R" })
            using (var f = new ResetFlowCpuIdleFixture())
            {
                string[] tags = phase.Split('|');
                f.Ledger = tags[0] + "|" + f.FirstScheme.ToString("D") + "|0|" + tags[1];
                f.Values[f.FirstScheme] = 1;
                ResetFlowCheck(!PowerPlan.RestoreLegacyCpuIdle() && PowerPlan.LegacyCpuIdleHasResidue
                    && f.Writes.Count == 0 && f.Activations.Count == 0,
                    "uncertain legacy receipt overwrote an unowned value");
                f.Values[f.FirstScheme] = 0;
                ResetFlowCheck(PowerPlan.RestoreLegacyCpuIdle() && !PowerPlan.LegacyCpuIdleHasResidue
                    && f.Writes.Count == 0, "confirmed original value did not settle legacy recovery");
            }
            foreach (string ledger in new[] { "", "invalid", "2|bad-guid|0|O" })
            using (var f = new ResetFlowCpuIdleFixture())
            {
                f.Ledger = ledger;
                ResetFlowCheck(PowerPlan.RestoreLegacyCpuIdle() == (ledger == "")
                    && f.Ledger == ledger && f.Writes.Count == 0 && f.Activations.Count == 0,
                    "missing or corrupt receipt caused mutation or lost recovery evidence");
            }
        }

        private static void ResetFlowLegacyIdleUncertainRecovery(string root)
        {
            using (var f = new ResetFlowCpuIdleFixture())
            {
                f.Own(); f.SetActiveResult = false;
                ResetFlowCheck(!PowerPlan.RestoreLegacyCpuIdle() && f.Values[f.FirstScheme] == 0
                    && PowerPlan.LegacyCpuIdleHasResidue, "failed reactivation erased recovery receipt");
                f.Reload(); f.SetActiveResult = true;
                ResetFlowCheck(PowerPlan.RestoreLegacyCpuIdle() && !PowerPlan.LegacyCpuIdleHasResidue
                    && f.Writes.Count == 1 && f.Activations.Count == 2,
                    "restart could not finish reactivation without repeating the value write");
            }
            using (var f = new ResetFlowCpuIdleFixture())
            {
                f.Own(); f.RejectLedger = delegate(string value) { return value == ""; };
                ResetFlowCheck(!PowerPlan.RestoreLegacyCpuIdle() && f.Ledger.EndsWith("|S", StringComparison.Ordinal),
                    "cleanup failure did not persist a settled receipt");
                f.Values[f.FirstScheme] = 1; f.Reload(); f.RejectLedger = null;
                ResetFlowCheck(PowerPlan.RestoreLegacyCpuIdle() && f.Values[f.FirstScheme] == 1
                    && f.Writes.Count == 1 && f.Activations.Count == 1,
                    "cleanup retry overwrote a later external edit");
            }
            using (var f = new ResetFlowCpuIdleFixture())
            {
                f.Own(); f.Values[f.FirstScheme] = 2;
                ResetFlowCheck(PowerPlan.RestoreLegacyCpuIdle() && f.Values[f.FirstScheme] == 2
                    && f.Writes.Count == 0 && f.Activations.Count == 0,
                    "external/mixed AC-DC values were overwritten");
            }
        }

        private static void ResetFlowCpuIdleResetKeepsRecovery(string root)
        {
            using (var f = new ResetFlowCpuIdleFixture())
            {
                var reset = new ResetFlowFixture(root, "cpu-idle-reset");
                f.Own();
                f.RejectWrite = delegate(Guid scheme, uint value) { return value == 0; };
                reset.SetRestore(delegate { return PowerPlan.RestoreLegacyCpuIdle()
                    ? new List<string>() : new List<string> { "CPU idle recovery" }; });
                int files; string failure;
                ResetFlowCheck(!Program.TryResetUserData(reset.DirectoryPath, reset.Stop(true), out files, out failure),
                    "reset succeeded while CPU idle recovery was pending");
                reset.AssertOriginalFiles(); reset.AssertRegistryPresent();
                ResetFlowCheck(PowerPlan.LegacyCpuIdleHasResidue && reset.RegistryCalls == 0,
                    "reset deleted the CPU idle recovery record before restoration");
                f.RejectWrite = null;
                int writes = f.Writes.Count;
                ResetFlowCheck(!Program.TryResetUserData(reset.DirectoryPath, delegate { return true; }, out files, out failure)
                    && PowerPlan.LegacyCpuIdleHasResidue && f.Writes.Count == writes && reset.RegistryCalls == 0,
                    "reset retried an uncertain idle write or erased its recovery record");
                reset.AssertOriginalFiles(); reset.AssertRegistryPresent();
                f.Values[f.FirstScheme] = 0;
                ResetFlowCheck(Program.TryResetUserData(reset.DirectoryPath, delegate { return true; }, out files, out failure),
                    "reset could not finish after the original CPU idle value was confirmed");
                reset.AssertOwnedFilesGone(); reset.AssertForeignFiles();
                ResetFlowCheck(!PowerPlan.LegacyCpuIdleHasResidue && reset.RegistryCalls == 1,
                    "verified idle recovery did not allow reset completion");
            }
        }

        private static void ResetFlowPowerPlanRecoveryOwnership(string root) {
            foreach(bool crash in new[]{false,true}) {
                foreach(string currentKind in new[]{"original","external","managed","selected","legacy","resolved"})
                using(var f=new ResetFlowPowerPlanFixture()) {
                    if(currentKind=="original")f.Current=f.Original;
                    if(currentKind=="external")f.Current=f.External;
                    if(currentKind=="selected"){f.Current=f.External;Settings.SaveStr("PowerPlanChoice",f.External.ToString());}
                    if(currentKind=="legacy"){f.Current=f.External;Settings.SaveStr("ArenaPlanGuid",f.External.ToString());}
                    if(currentKind=="resolved"){f.Current=f.External;ResetFlowSetStatic(typeof(PowerPlan),"target",f.External);
                        ResetFlowSetStatic(typeof(PowerPlan),"resolved",true);}
                    bool owned=currentKind!="original" && currentKind!="external";
                    ResetFlowCheck(PowerPlan.RestorePlanForTest(crash),"power recovery rejected "+currentKind);
                    ResetFlowCheck(f.Sets==(owned?1:0) && f.Current==(currentKind=="external"?f.External:f.Original)
                        && Settings.LoadStr("PrevPowerPlan","missing")=="","power recovery changed foreign plan or missed owner: "+currentKind);
                }
                foreach(string missing in new[]{"current-null","current-empty","current-throw","ownership-unknown","default-hooks"})
                using(var f=new ResetFlowPowerPlanFixture()) {
                    if(missing=="current-null")f.Current=null;
                    if(missing=="current-empty")f.Current=System.Guid.Empty;
                    if(missing=="current-throw")PowerPlan.RestoreCurrentPlanForTest=delegate{throw new InvalidOperationException("mock current failure");};
                    if(missing=="ownership-unknown"){f.Current=f.External;Settings.SaveStr("PgPlanGuid","invalid");}
                    if(missing=="default-hooks")PowerPlan.ResetPlanRestoreForTest();
                    ResetFlowCheck(!PowerPlan.RestorePlanForTest(crash) && f.Sets==0 && Settings.LoadStr("PrevPowerPlan","")==f.Original.ToString(),
                        "unknown recovery boundary altered or dropped the original: "+missing);
                }
                foreach(string unavailable in new[]{"missing","unknown","existing","throw","changed-with-failure","lying-success"})
                using(var f=new ResetFlowPowerPlanFixture()) {
                    f.SetResult=unavailable=="lying-success";
                    f.MutateOnSet=unavailable=="changed-with-failure";
                    f.Usable=unavailable=="missing"?(bool?)false:unavailable=="unknown"?(bool?)null:true;
                    if(unavailable=="throw")PowerPlan.RestoreSetPlanForTest=delegate(System.Guid g){f.Sets++;throw new InvalidOperationException("mock set failure");};
                    ResetFlowCheck(!PowerPlan.RestorePlanForTest(crash) && f.Sets==1,"failed/uncertain plan Set reported recovery");
                    ResetFlowCheck((Settings.LoadStr("PrevPowerPlan","").Length==0)==(unavailable=="missing"),
                        "failed/uncertain/missing original lost wrong receipt: "+unavailable);
                    if(unavailable=="changed-with-failure") {
                        ResetFlowCheck(PowerPlan.RestorePlanForTest(crash) && f.Sets==1 && Settings.LoadStr("PrevPowerPlan","")=="",
                            "verified original after failed Set did not settle without another switch");
                    }
                }
                using(var f=new ResetFlowPowerPlanFixture()) {
                    PowerPlan.RestorePlanIsOwnedForTest=delegate(System.Guid g){
                        bool? result=PowerPlan.ClassifyRestorePlanForTest(g);f.Current=f.External;return result;};
                    ResetFlowCheck(!PowerPlan.RestorePlanForTest(crash) && f.Sets==0 && f.Current==f.External
                        && Settings.LoadStr("PrevPowerPlan","")!="","slow ownership read overwrote newly selected plan");
                }
                using(var f=new ResetFlowPowerPlanFixture()) {
                    Settings.SuspendWritesForReset();
                    ResetFlowCheck(!PowerPlan.RestorePlanForTest(crash) && f.Sets==1 && f.Current==f.Original
                        && Settings.LoadStr("PrevPowerPlan","")==f.Original.ToString(),"failed clear lost restored receipt");
                    f.Current=f.External;
                    ResetFlowCheck(ResetFlowGetStatic(typeof(Settings),"transientValues")!=null,"test settings were not transient");
                    lock(ResetFlowGetStatic(typeof(Settings),"writeSync"))ResetFlowSetStatic(typeof(Settings),"writesSuspendedForReset",false);
                    ResetFlowCheck(PowerPlan.RestorePlanForTest(crash) && f.Sets==1 && f.Current==f.External
                        && Settings.LoadStr("PrevPowerPlan","")=="","retry after failed clear overwrote a later user plan");
                }
                foreach(string invalid in new[]{"broken",System.Guid.Empty.ToString()})
                using(var f=new ResetFlowPowerPlanFixture()) {
                    Settings.SaveStr("PrevPowerPlan",invalid);
                    ResetFlowCheck(PowerPlan.RestorePlanForTest(crash) && f.Sets==0 && f.Queries==0
                        && Settings.LoadStr("PrevPowerPlan","")=="","legacy invalid receipt triggered a power operation");
                }
                using(var f=new ResetFlowPowerPlanFixture()) {
                    Settings.SaveStr("PrevPowerPlan","");
                    ResetFlowCheck(PowerPlan.RestorePlanForTest(crash) && f.Sets==0 && f.Queries==0
                        && f.OrphanCalls==(crash?0:1),"startup empty journal ran orphan recovery or shutdown omitted it");
                }
            }
            using(var f=new ResetFlowPowerPlanFixture()) {
                Settings.SaveStr("PrevPowerPlan","");f.Current=f.Original;
                ResetFlowCheck(PowerPlan.ActivatePlanForTest() && f.Current==f.Managed,
                    "forced-ownership fixture could not activate managed plan");
                f.Current=f.External;
                int sets=f.Sets;
                ResetFlowCheck(PowerPlan.RestorePlanForTest(false)
                    && f.Sets==sets+1 && f.Current==f.Original
                    && Settings.LoadStr("PrevPowerPlan","")=="",
                    "live session surrendered restore ownership to an external plan switch");
            }
            ResetFlowPowerPlanActivationRecovery();
            ResetFlowPowerPlanSettledCleanup();
        }

        private static void ResetFlowPowerPlanSettledCleanup() {
            foreach(bool crash in new[]{false,true})
            foreach(string settledKind in new[]{"restored","original","external","missing"})
            foreach(string nextKind in new[]{"managed","selected","resolved"})
            foreach(bool readback in new[]{false,true})
            using(var f=new ResetFlowPowerPlanFixture())
            using(var reads=new ResetFlowStrictReadScope("PrevPowerPlan")) {
                Settings.SaveStr("PrevPowerPlan","");f.Current=f.Original;
                ResetFlowCheck(PowerPlan.ActivatePlanForTest() && f.Current==f.Managed,"settled test could not activate");
                if(crash)f.Reload();
                if(nextKind=="selected")Settings.SaveStr("PowerPlanChoice",f.External.ToString());
                if(nextKind=="resolved") {
                    ResetFlowSetStatic(typeof(PowerPlan),"target",f.External);
                    ResetFlowSetStatic(typeof(PowerPlan),"resolved",true);
                }
                if(settledKind=="original")f.Current=f.Original;
                if(settledKind=="external")f.Current=new Guid("11111111-2222-4333-8444-555555555555");
                if(settledKind=="missing"){f.SetResult=false;f.MutateOnSet=false;f.Usable=false;}
                string label=settledKind+"/"+nextKind+"/"+crash+"/"+readback;
                var rawValues=(Dictionary<string,object>)ResetFlowGetStatic(typeof(Settings),"transientValues");
                object rawSync=ResetFlowGetStatic(typeof(Settings),"transientSync");
                ResetFlowCheck(rawValues!=null,"settings were not isolated");
                Func<string> rawReceipt=delegate {
                    lock(rawSync) {
                        object value;
                        if(!rawValues.TryGetValue("PrevPowerPlan",out value) || !(value is string))
                            throw new Exception("settled cleanup lost its raw receipt: "+label);
                        return (string)value;
                    }
                };
                // Observe the fake backing store directly: a managed LoadStr here would
                // re-enter this strict-read hook instead of simulating lost read access.
                if(readback)reads.OnRead=delegate {if(rawReceipt()=="")reads.Deny=true;};
                else Settings.SuspendWritesForReset();
                string strictReceipt;
                ResetFlowCheck(!PowerPlan.RestorePlanForTest(crash)
                    && rawReceipt()==(readback?"":f.Original.ToString())
                    && Settings.LoadStr("PrevPowerPlan","")==(readback?"!unreadable-recovery-record!":f.Original.ToString())
                    && (!readback || !Settings.TryLoadStr("PrevPowerPlan",out strictReceipt))
                    && PowerPlan.HasResidue,"blocked settled cleanup lost receipt: "+label);
                int sets=f.Sets,queries=f.Queries;
                Guid userChoice=nextKind=="managed"?f.Managed:f.External;
                f.Current=userChoice;f.SetResult=true;f.MutateOnSet=true;f.Usable=true;
                ResetFlowCheck(!PowerPlan.RestorePlanForTest(crash) && f.Sets==sets && f.Queries==queries
                    && f.Current==userChoice,"settled cleanup retried a native restore over user choice: "+label);
                ResetFlowCheck(!(bool)ResetFlowGetStatic(typeof(PowerPlan),"active")
                    && !PowerPlan.ActivatePlanForTest() && f.Sets==sets && f.Current==userChoice,
                    "uncleared settled receipt was reused as active: "+label);
                reads.Deny=false;reads.OnRead=null;
                lock(ResetFlowGetStatic(typeof(Settings),"writeSync"))ResetFlowSetStatic(typeof(Settings),"writesSuspendedForReset",false);
                ResetFlowCheck(PowerPlan.RestorePlanForTest(crash) && f.Sets==sets && f.Queries==queries
                    && f.Current==userChoice && rawReceipt()==""
                    && Settings.TryLoadStr("PrevPowerPlan",out strictReceipt) && strictReceipt==""
                    && Settings.LoadStr("PrevPowerPlan","")=="" && !PowerPlan.HasResidue,
                    "settled cleanup changed a user plan after writes recovered: "+label);
                f.Current=f.External;
                ResetFlowCheck(PowerPlan.ActivatePlanForTest() && f.Sets==sets+1 && f.Current==f.Managed
                    && Settings.LoadStr("PrevPowerPlan","")==f.External.ToString(),"new session could not activate: "+label);
                ResetFlowCheck(PowerPlan.ActivatePlanForTest() && f.Sets==sets+1,"active session repeated activation: "+label);
                ResetFlowCheck(PowerPlan.RestorePlanForTest(false) && f.Sets==sets+2 && f.Current==f.External
                    && Settings.LoadStr("PrevPowerPlan","")=="","new session lost its own restore target: "+label);
            }
        }

        private static void ResetFlowPowerPlanActivationRecovery() {
            foreach(bool crash in new[]{false,true})
            foreach(string outcome in new[]{"not-changed","changed","query-unknown","success-query-unknown","restore-blocked"})
            using(var f=new ResetFlowPowerPlanFixture()) {
                Settings.SaveStr("PrevPowerPlan",""); f.Current=f.Original;
                bool first=true, blockRestore=outcome=="restore-blocked";
                PowerPlan.RestoreSetPlanForTest=delegate(Guid scheme) {
                    f.Sets++;
                    if(first) {
                        first=false;
                        if(outcome=="not-changed")return false;
                        f.Current=outcome.IndexOf("query",StringComparison.Ordinal)>=0?(Guid?)null:f.Managed;
                        return outcome=="success-query-unknown";
                    }
                    if(blockRestore && scheme==f.Original)return false;
                    f.Current=scheme;return true;
                };
                ResetFlowCheck(!PowerPlan.ActivatePlanForTest() && f.Sets==1
                    && Settings.LoadStr("PrevPowerPlan","")==f.Original.ToString()
                    && (Guid)ResetFlowGetStatic(typeof(PowerPlan),"saved")==f.Original,
                    "uncertain plan activation lost its captured original: "+outcome);
                if(!f.Current.HasValue || blockRestore) {
                    int sets=f.Sets;
                    ResetFlowCheck(!PowerPlan.ActivatePlanForTest()
                        && f.Sets==sets+(blockRestore?1:0)
                        && Settings.LoadStr("PrevPowerPlan","")==f.Original.ToString()
                        && (Guid)ResetFlowGetStatic(typeof(PowerPlan),"saved")==f.Original,
                        "activation retry replaced the original before pending recovery completed: "+outcome);
                }
                f.Current=outcome=="not-changed"?f.Original:f.Managed;blockRestore=false;
                ResetFlowCheck(PowerPlan.ActivatePlanForTest() && f.Current==f.Managed
                    && Settings.LoadStr("PrevPowerPlan","")==f.Original.ToString()
                    && (Guid)ResetFlowGetStatic(typeof(PowerPlan),"saved")==f.Original,
                    "activation retry captured the managed plan as the original: "+outcome);
                int appliedWrites=f.Sets;
                ResetFlowCheck(PowerPlan.ActivatePlanForTest() && f.Sets==appliedWrites,
                    "already active plan repeated activation");
                if(crash)f.Reload();
                ResetFlowCheck(PowerPlan.RestorePlanForTest(crash) && f.Current==f.Original
                    && Settings.LoadStr("PrevPowerPlan","")=="",
                    "uncertain activation original could not restore after retry/restart: "+outcome);
            }
        }

        private sealed class ResetFlowPowerPlanFixture : System.IDisposable {
            internal readonly System.Guid Original=new System.Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
            internal readonly System.Guid Managed=new System.Guid("27018216-8761-442c-a0dc-5a678ae322f4");
            internal readonly System.Guid External=new System.Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
            internal System.Guid? Current;
            internal int Queries,Sets,OrphanCalls;
            internal bool SetResult=true,MutateOnSet=true;
            internal bool? Usable=true;
            internal ResetFlowPowerPlanFixture() {
                Settings.UseTransientStoreForCurrentProcess();
                Current=Managed;Settings.SaveStr("PrevPowerPlan",Original.ToString());
                Settings.SaveStr("PgPlanGuid",Managed.ToString());Settings.SaveStr("PowerPlanChoice","managed");
                Reload();
            }
            internal void Reload() {
                PowerPlan.ResetPlanRestoreForTest();
                PowerPlan.ActivateTargetPlanForTest=delegate{return Managed;};
                PowerPlan.RestoreCurrentPlanForTest=delegate {Queries++;return Current;};
                PowerPlan.RestoreSetPlanForTest=delegate(System.Guid scheme){Sets++;if(MutateOnSet)Current=scheme;return SetResult;};
                PowerPlan.RestorePlanIsOwnedForTest=PowerPlan.ClassifyRestorePlanForTest;
                PowerPlan.RestorePlanIsUsableForTest=delegate(System.Guid scheme){return Usable;};
                PowerPlan.RestoreOrphanManagedPlanForTest=delegate{OrphanCalls++;return true;};
            }
            public void Dispose(){PowerPlan.ResetPlanRestoreForTest();Settings.UseTransientStoreForCurrentProcess();}
        }

        private sealed class ResetFlowCpuIdleFixture : IDisposable
        {
            internal readonly Guid FirstScheme = new Guid("27018216-8761-442c-a0dc-5a678ae322f4");
            internal readonly Guid SecondScheme = new Guid("587ec1a1-05a5-4941-8c9b-6b142093e7db");
            internal readonly Dictionary<Guid, uint> Values = new Dictionary<Guid, uint>();
            internal readonly List<IdleWrite> Writes = new List<IdleWrite>();
            internal readonly List<Guid> Activations = new List<Guid>();
            internal Guid? Current;
            internal string Ledger = "", LastRead = "";
            internal bool SetActiveResult = true;
            internal bool DenyRead, IgnoreWrites, DenyLedgerRead, IgnoreLedgerWrites;
            internal Func<Guid, uint, bool> RejectWrite;
            internal Func<string, bool> RejectLedger;
            internal Action AfterRead, AfterLedgerWrite;
            internal Action<uint> AfterWrite;
            internal sealed class IdleWrite { internal Guid Scheme; internal uint Value; }

            internal ResetFlowCpuIdleFixture()
            {
                Current = FirstScheme;
                Values[FirstScheme] = 0; Values[SecondScheme] = 1;
                Reload();
            }

            internal void Reload()
            {
                PowerPlan.ResetCpuIdleForTest();
                PowerPlan.CpuIdleCurrentSchemeForTest = delegate { return Current; };
                PowerPlan.CpuIdleReadAcForTest = delegate(Guid scheme, out uint value)
                {
                    value = 0;
                    bool ok = !DenyRead && Values.TryGetValue(scheme, out value);
                    if (AfterRead != null) AfterRead();
                    return ok;
                };
                PowerPlan.CpuIdleWriteAcForTest = delegate(Guid scheme, uint value)
                {
                    Writes.Add(new IdleWrite { Scheme = scheme, Value = value });
                    ResetFlowCheck(value == 0 && Ledger.Length > 0 && LastRead == Ledger,
                        "legacy recovery tried to disable idle or wrote without a verified receipt");
                    if (RejectWrite != null && RejectWrite(scheme, value)) return false;
                    if (!IgnoreWrites) Values[scheme] = value;
                    if (AfterWrite != null) AfterWrite(value);
                    return true;
                };
                PowerPlan.CpuIdleSetActiveForTest = delegate(Guid scheme)
                {
                    Activations.Add(scheme);
                    if (SetActiveResult) Current = scheme;
                    return SetActiveResult;
                };
                PowerPlan.CpuIdleReadLedgerForTest = delegate(out string value)
                {
                    value = Ledger;
                    if (DenyLedgerRead) return false;
                    LastRead = value;
                    return true;
                };
                PowerPlan.CpuIdleWriteLedgerForTest = delegate(string value)
                {
                    if (RejectLedger != null && RejectLedger(value)) return false;
                    if (!IgnoreLedgerWrites) Ledger = value;
                    if (AfterLedgerWrite != null) AfterLedgerWrite();
                    return true;
                };
            }

            internal void Own()
            {
                Values[FirstScheme] = 1;
                Ledger = "2|" + FirstScheme.ToString("D") + "|0|O";
                Reload();
            }
            public void Dispose() { PowerPlan.ResetCpuIdleForTest(); }
        }

        private static object ResetFlowGetStatic(Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            ResetFlowCheck(field != null, "missing isolated state field " + type.Name + "." + name);
            return field.GetValue(null);
        }

        private static void ResetFlowSetStatic(Type type, string name, object value)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            ResetFlowCheck(field != null, "missing isolated state field " + type.Name + "." + name);
            field.SetValue(null, value);
        }

        private sealed class ResetFlowBlockingWorker : IDisposable
        {
            private readonly ManualResetEvent held = new ManualResetEvent(false);
            private readonly ManualResetEvent release = new ManualResetEvent(false);
            private readonly Thread worker;
            private Exception error;

            internal ResetFlowBlockingWorker(Action<Action> body)
            {
                worker = new Thread(delegate()
                {
                    try
                    {
                        body(delegate
                        {
                            held.Set();
                            if (!release.WaitOne(3000)) throw new TimeoutException("Owned fake callback was not released");
                        });
                    }
                    catch (Exception failure) { error = failure; }
                });
                worker.IsBackground = true;
                worker.Start();
            }
            internal void WaitUntilHeld()
            { ResetFlowCheck(held.WaitOne(3000), "owned fake callback never entered its gate: " + error); }
            internal void Finish()
            {
                release.Set();
                ResetFlowCheck(worker.Join(3000), "owned fake callback did not exit");
                ResetFlowCheck(error == null, "owned fake callback threw: " + error);
            }
            public void Dispose()
            {
                try { Finish(); }
                finally { held.Dispose(); release.Dispose(); }
            }
        }

        private sealed class ResetFlowFixture
        {
            internal readonly string DirectoryPath, ProfilePath;
            internal readonly List<string> OwnedPaths = new List<string>();
            internal readonly List<string> ForeignPaths = new List<string>();
            internal readonly Dictionary<string, string> OriginalText = new Dictionary<string, string>();
            internal readonly List<string> Events = new List<string>();
            internal int StopCalls, RestoreCalls, RegistryCalls;
            internal bool MockRegistryPresent = true;
            private readonly string registryValue;

            internal ResetFlowFixture(string root, string name)
            {
                DirectoryPath = Path.GetFullPath(Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")));
                ResetFlowCheck(DirectoryPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), "fixture escaped its temporary root");
                Directory.CreateDirectory(DirectoryPath);
                ProfilePath = Seed(GameProfileStore.FileName, true);
                Seed(SuppressionCore.StateFileName, true);
                Seed(RendererObservationStore.FileName, true);
                Seed("Pavise.exe", false);
                Seed("Pavise.portable", false);
                Seed("user-save.sav", false);
                registryValue = Guid.NewGuid().ToString("N");
                ResetFlowCheck(Settings.SaveStr("ResetFlowMockRegistry", registryValue), "could not seed transient registry fixture");
                SetRestore(delegate { return new List<string>(); });
                SetRegistry(delegate { return true; });
            }

            private string Seed(string name, bool owned)
            {
                string path = Path.Combine(DirectoryPath, name);
                string text = "isolated-reset-flow:" + name + ":" + Guid.NewGuid().ToString("N");
                File.WriteAllText(path, text, new UTF8Encoding(false));
                OriginalText.Add(path, text);
                (owned ? OwnedPaths : ForeignPaths).Add(path);
                return path;
            }

            internal Func<bool> Stop(bool result)
            {
                return delegate
                {
                    StopCalls++; Events.Add("stop");
                    ResetFlowCheck(RestoreCalls == 0 && RegistryCalls == 0, "stop was not the first phase");
                    AssertOriginalFiles(); AssertRegistryPresent();
                    return result;
                };
            }

            internal void SetRestore(Func<List<string>> body)
            {
                LegacyPurge.RestoreHook = delegate { RestoreCalls++; Events.Add("restore"); return body(); };
            }

            internal void SetRegistry(Func<bool> body)
            {
                LegacyPurge.DeleteRegistryHook = delegate
                {
                    RegistryCalls++; Events.Add("registry");
                    bool result = body();
                    if (result) MockRegistryPresent = false;
                    return result;
                };
            }

            internal void AssertOriginalFiles()
            {
                foreach (KeyValuePair<string, string> item in OriginalText)
                    ResetFlowCheck(File.Exists(item.Key) && File.ReadAllText(item.Key) == item.Value, "configuration/recovery/foreign bytes changed before deletion: " + item.Key);
            }
            internal void AssertRegistryPresent()
            {
                ResetFlowCheck(MockRegistryPresent && Settings.LoadStr("ResetFlowMockRegistry", "") == registryValue, "mock registry changed before confirmed deletion");
            }
            internal void AssertOwnedFilesGone()
            {
                foreach (string path in OwnedPaths) ResetFlowCheck(!File.Exists(path), "owned data remains: " + path);
            }
            internal void AssertForeignFiles()
            {
                foreach (string path in ForeignPaths)
                    ResetFlowCheck(File.Exists(path) && File.ReadAllText(path) == OriginalText[path], "portable EXE/marker/user file was changed: " + path);
            }
        }

        private static void ResetFlowDeleteScratch(string root)
        {
            string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string name = Path.GetFileName(actual);
            Guid id;
            ResetFlowCheck(string.Equals(Path.GetDirectoryName(actual), parent, StringComparison.OrdinalIgnoreCase)
                && name.StartsWith("PaviseResetFlow-", StringComparison.Ordinal)
                && Guid.TryParseExact(name.Substring("PaviseResetFlow-".Length), "N", out id), "refusing cleanup outside the owned temporary root");
            if (Directory.Exists(actual)) ResetFlowDeleteScratchLevel(actual, actual);
        }

        private static void ResetFlowDeleteScratchLevel(string root, string path)
        {
            string actual = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            ResetFlowCheck(string.Equals(root, actual, StringComparison.OrdinalIgnoreCase)
                || actual.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "scratch cleanup escaped its root");
            FileAttributes attributes = File.GetAttributes(actual);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(actual, false);
                else File.Delete(actual);
                return;
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string child in Directory.GetFileSystemEntries(actual)) ResetFlowDeleteScratchLevel(root, child);
                File.SetAttributes(actual, attributes & ~FileAttributes.ReadOnly);
                Directory.Delete(actual, false);
            }
            else
            {
                File.SetAttributes(actual, attributes & ~FileAttributes.ReadOnly);
                File.Delete(actual);
            }
        }
    }

#if PAVISE_RENDERER_BENCH
    internal static class ResetCheckRunner
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static int Main(string[] args)
        {
            if (args.Length != 2) return 2;
            string output = Path.GetFullPath(args[0]);
            int repeats;
            if (!int.TryParse(args[1], out repeats) || repeats < 1 || repeats > 10) return 2;
            if (!string.Equals(output.TrimEnd(Path.DirectorySeparatorChar),
                AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return 2;
            Settings.UseTransientStoreForCurrentProcess();
            Logger.ResetWriteBarrierForTest();
            Lang.Init();
            Logger.LogPath = Path.Combine(output, "production-decision.log");
            var rows = new List<Dictionary<string, object>>();
            int failures = 0, groups = 0;
            Type bench = typeof(RendererHandoffBench);
            bench.GetField("output", Static).SetValue(null, output);
            bench.GetMethod("IsolateCatalogs", Static).Invoke(null, null);
            for (int repeat = 1; repeat <= repeats; repeat++)
            {
                foreach (string name in new[] { "RunResetCleanupRegressionTests", "RunResetFlowRegressionTests" })
                {
                    var row = new Dictionary<string, object>();
                    row["Repeat"] = repeat; row["Id"] = name;
                    try
                    {
                        int count = (int)typeof(SelfTests).GetMethod(name, Static).Invoke(null, null);
                        if (count <= 0) throw new Exception("Reset runner returned no groups");
                        row["Outcome"] = "PASS"; row["PassedGroups"] = count; groups += count;
                    }
                    catch (Exception error)
                    {
                        while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                        row["Outcome"] = "FAIL"; row["Exception"] = error.ToString(); failures++;
                    }
                    rows.Add(row);
                    Console.WriteLine("RESET_ROW repeat=" + repeat + " id=" + name + " result=" + row["Outcome"]);
                }
                Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest();
                bench.GetMethod("FocusedTests", Static).Invoke(null, new object[] { repeat });
            }
            foreach (RendererHandoffBench.Row source in (IEnumerable)bench.GetField("rows", Static).GetValue(null))
            {
                var row = new Dictionary<string, object>();
                row["Repeat"] = source.Repeat; row["Id"] = source.Id; row["Outcome"] = source.Outcome;
                row["Details"] = source.Details;
                if (source.Outcome == "PASS") groups += Convert.ToInt32(source.Details["PassedGroups"]);
                else failures++;
                rows.Add(row);
            }
            var report = new Dictionary<string, object>();
            report["Repeated"] = repeats; report["Rows"] = rows; report["PassedGroupExecutions"] = groups;
            report["Failures"] = failures; report["ProductionMainRun"] = false; report["RealRestoreOrRegistryDeletion"] = false;
            report["GameModeStartOrStopRun"] = false; report["OwnedProcessMatrixRun"] = false;
            report["Settings"] = "process-local transient dictionary";
            report["Deletion"] = "validated unique temporary fixtures only; restore and registry are mocked";
            File.WriteAllText(Path.Combine(output, "results.json"),
                new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(report), new UTF8Encoding(false));
            Console.WriteLine("SUMMARY groups=" + groups + " failures=" + failures + " output=" + output);
            return failures == 0 ? 0 : 1;
        }
    }
#endif
}
#endif
