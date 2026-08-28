// Reset entry-point regression. Stop/restore/registry are mocks; deletion only
// touches validated, uniquely named temporary fixtures owned by this suite.
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
                ResetFlowDriverAdmissionSealed,
                ResetFlowDriverInFlightMustDrain,
                ResetFlowPowerAdmissionSealed,
                ResetFlowPowerLateResultIsNotPublished,
                ResetFlowDrainRejectsLiveAndReentrantCallers,
                ResetFlowDrainWaitsForOtherWriteGates,
                ResetFlowWhitelistCannotSaveAfterStopping,
                ResetFlowStoreCloseRejectsLateMutation,
                ResetFlowStoreCloseTimeoutIsNotSuccess,
                ResetFlowStoreCloseRejectsReentrantCaller,
                ResetFlowRenderLaneRejectsStaleAndClosedWork,
                ResetFlowRenderLaneDrainKeepsJournal,
                ResetFlowRenderLaneRestoreFailureKeepsJournal,
                ResetFlowRenderLaneInvalidJournalIsNotSuccess,
                ResetFlowRenderLaneAppliedFailureKeepsState,
                ResetFlowYieldRejectsStaleAndDuplicateWork,
                ResetFlowYieldTimedOutJoinKeepsWorker,
                ResetFlowYieldInFlightMutationMustDrain,
                ResetFlowYieldMutationGateOutlivesWorker,
                ResetFlowYieldWorkerCannotJoinItself,
                ResetFlowVramFailedRecoveryKeepsSnapshot,
                ResetFlowVramThrowingRecoveryKeepsSnapshot,
                ResetFlowVramInvalidSnapshotIsPreserved,
                ResetFlowVramRetryClearsOnlyAfterConfirmation,
                ResetFlowVramBlockedRecoveryCannotEnterSampling,
                ResetFlowEppPartialRollbackKeepsOriginals,
                ResetFlowEppReadbackFailureKeepsPending,
                ResetFlowEppReactivationFailureKeepsPending,
                ResetFlowEppRestoresCapturedScheme,
                ResetFlowEppMissingOriginalDoesNotWrite
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
                    // Fail closed if a case forgets to install either mock.
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
            // File existence was checked inside restore and again inside registry,
            // so the real deletion is proven to occur between those two callbacks.
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
                IrqMutationBoundary.Configure(null, null);
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
                IrqMutationBoundary.Configure(null, null);
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
                var platform = new ResetFlowIrqPlatform();
                FamilyPolicySetField(f.Mode, "irqProbe", new IrqSessionProbe(platform));
                FamilyPolicySetField(f.Mode, "powerSessionGen", 7);
                int writes = 0;
                Func<bool> apply = delegate { writes++; return true; };
                ResetFlowCheck((bool)FamilyPolicyInvoke(f.Mode, "RunPowerPlanApply", 7, apply), "current fake power mutation was rejected");
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "RunPowerPlanApply", 6, apply), "stale power generation was admitted");
                FamilyPolicySetField(f.Mode, "stopping", true);
                ResetFlowCheck(!(bool)FamilyPolicyInvoke(f.Mode, "RunPowerPlanApply", 7, apply), "late power apply was admitted");
                ResetFlowCheck(writes == 1 && platform.ForbiddenCalls == 0, "power gate ran an unexpected action or capture");
            }
        }

        private static void ResetFlowPowerLateResultIsNotPublished(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-power-result"))
            {
                var platform = new ResetFlowIrqPlatform();
                FamilyPolicySetField(f.Mode, "irqProbe", new IrqSessionProbe(platform));
                FamilyPolicySetField(f.Mode, "powerSessionGen", 9);
                long originalAudit = (long)FamilyPolicyGetField(f.Mode, "nextPowerAuditTicks");
                bool accepted = true;
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
                    ResetFlowCheck((bool)FamilyPolicyInvoke(f.Mode, "DrainAsyncShutdown", 250), "finished power callback did not drain");
                    ResetFlowCheck(platform.ForbiddenCalls == 0, "fake power test attempted IRQ capture/persistence");
                }
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
                File.Delete(cache); // Exact owned fixture cache; never a user file.
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

        private static void ResetFlowYieldRejectsStaleAndDuplicateWork(string root)
        {
            ResetFlowPrepareYield();
            using (var worker = new ResetFlowYieldWorker(delegate(int generation, Action hold) { hold(); }))
            {
                worker.WaitUntilHeld();
                int writes = 0;
                Func<bool> mutation = delegate { writes++; return true; };
                ResetFlowCheck(PowerBudgetYieldRunner.RunShutdownMutationForTest(worker.Generation, mutation), "current yield fake mutation was rejected");
                ResetFlowCheck(!PowerBudgetYieldRunner.RunShutdownMutationForTest(worker.Generation - 1, mutation), "stale yield generation was admitted");
                ResetFlowCheck(PowerBudgetYieldRunner.StartShutdownWorkerForTest(delegate { }) == -1, "duplicate live yield worker was admitted");
                worker.Finish();
                ResetFlowCheck(!PowerBudgetYieldRunner.RunShutdownMutationForTest(worker.Generation, mutation)
                    && PowerBudgetYieldRunner.StartShutdownWorkerForTest(delegate { }) == -1 && writes == 1,
                    "terminal yield shutdown reopened mutation/worker admission");
            }
        }

        private static void ResetFlowYieldTimedOutJoinKeepsWorker(string root)
        {
            ResetFlowPrepareYield();
            using (var worker = new ResetFlowYieldWorker(delegate(int generation, Action hold) { hold(); }))
            {
                worker.WaitUntilHeld();
                object owned = ResetFlowGetStatic(typeof(PowerBudgetYieldRunner), "worker");
                ResetFlowCheck(!PowerBudgetYieldRunner.CloseForShutdown(20), "live yield worker was reported joined");
                ResetFlowCheck(ReferenceEquals(owned, ResetFlowGetStatic(typeof(PowerBudgetYieldRunner), "worker"))
                    && ((Thread)owned).IsAlive, "timed-out yield join discarded its live thread reference");
                ResetFlowCheck(PowerBudgetYieldRunner.StartShutdownWorkerForTest(delegate { }) == -1,
                    "join timeout admitted a replacement yield worker");
                worker.Finish();
                ResetFlowCheck(!((Thread)owned).IsAlive && ResetFlowGetStatic(typeof(PowerBudgetYieldRunner), "worker") == null,
                    "successful yield close did not join and release the owned thread");
            }
        }

        private static void ResetFlowYieldInFlightMutationMustDrain(string root)
        {
            ResetFlowPrepareYield();
            int writes = 0;
            using (var worker = new ResetFlowYieldWorker(delegate(int generation, Action hold)
            {
                ResetFlowCheck(PowerBudgetYieldRunner.RunShutdownMutationForTest(generation,
                    delegate { writes++; hold(); return true; }), "owned in-flight yield action failed");
            }))
            {
                worker.WaitUntilHeld();
                ResetFlowCheck(!PowerBudgetYieldRunner.CloseForShutdown(20), "in-flight yield mutation was reported drained");
                worker.Finish();
                ResetFlowCheck(!PowerBudgetYieldRunner.RunShutdownMutationForTest(worker.Generation,
                    delegate { writes++; return true; }) && writes == 1, "yield mutation ran again after final drain");
            }
        }

        private static void ResetFlowYieldMutationGateOutlivesWorker(string root)
        {
            ResetFlowPrepareYield();
            using (var finished = new ManualResetEvent(false))
            {
                int generation = PowerBudgetYieldRunner.StartShutdownWorkerForTest(delegate { finished.Set(); });
                ResetFlowCheck(generation > 0 && finished.WaitOne(3000), "owned short yield worker never ran");
                Thread original = (Thread)ResetFlowGetStatic(typeof(PowerBudgetYieldRunner), "worker");
                ResetFlowCheck(original.Join(3000), "owned short yield worker did not exit");
                try
                {
                    using (var writer = new ResetFlowBlockingWorker(delegate(Action hold)
                    {
                        ResetFlowCheck(PowerBudgetYieldRunner.RunShutdownMutationForTest(generation,
                            delegate { hold(); return true; }), "owned outliving yield mutation failed");
                    }))
                    {
                        writer.WaitUntilHeld();
                        ResetFlowCheck(!PowerBudgetYieldRunner.CloseForShutdown(20), "yield writer gate was ignored after worker exit");
                        ResetFlowCheck(ReferenceEquals(original, ResetFlowGetStatic(typeof(PowerBudgetYieldRunner), "worker")),
                            "failed yield gate drain cleared lifecycle state");
                        writer.Finish();
                        ResetFlowCheck(PowerBudgetYieldRunner.CloseForShutdown(250), "released yield writer gate did not close");
                    }
                }
                finally
                {
                    ResetFlowCheck(PowerBudgetYieldRunner.CloseForShutdown(3000), "owned yield state survived fixture teardown");
                    PowerBudgetYieldRunner.ResetShutdownForTest();
                }
            }
        }

        private static void ResetFlowYieldWorkerCannotJoinItself(string root)
        {
            ResetFlowPrepareYield();
            bool selfClosed = true;
            using (var worker = new ResetFlowYieldWorker(delegate(int generation, Action hold)
            {
                selfClosed = PowerBudgetYieldRunner.CloseForShutdown(20);
                hold();
            }))
            {
                worker.WaitUntilHeld();
                ResetFlowCheck(!selfClosed && ResetFlowGetStatic(typeof(PowerBudgetYieldRunner), "worker") != null,
                    "yield worker claimed it had joined itself");
                worker.Finish();
            }
        }

        private static void ResetFlowPrepareRenderLane()
        {
            RenderLane.ResetShutdownForTest();
            RenderLane.ConfigureMutationBoundary(null, null);
            // Never allow an accidental record to reach real OpenThread/priority setters.
            RenderLane.RestoreThreadForTest = delegate { throw new InvalidOperationException("Unexpected thread restore in reset test"); };
        }

        private static void ResetFlowVramFailedRecoveryKeepsSnapshot(string root)
        {
            ResetFlowPrepareVram();
            const string snapshot = "123:456";
            ResetFlowCheck(Settings.SaveStr("VramShieldSnap", snapshot), "could not seed fake VRAM snapshot");
            int restores = 0;
            VramShield.RestoreReservationForTest = delegate(int pid, long creation, uint adapter, uint physical)
            {
                ResetFlowCheck(pid == 123 && creation == 456, "VRAM recovery identity changed");
                restores++; return false;
            };
            try
            {
                ResetFlowCheck(!VramShield.HealFromCrash() && restores == 1 && VramShield.HasResidue()
                    && VramShield.RecoveryBlockedForTest && Settings.LoadStr("VramShieldSnap", "") == snapshot,
                    "unconfirmed VRAM restoration discarded its original snapshot");
                ResetFlowCheck(!VramShield.Begin() && Settings.LoadStr("VramShieldSnap", "") == snapshot,
                    "new VRAM session overwrote unresolved recovery");
            }
            finally { VramShield.ResetRecoveryForTest(); }
        }

        private static void ResetFlowVramThrowingRecoveryKeepsSnapshot(string root)
        {
            ResetFlowPrepareVram();
            const string snapshot = "123:456";
            ResetFlowCheck(Settings.SaveStr("VramShieldSnap", snapshot), "could not seed throwing VRAM fixture");
            VramShield.RestoreReservationForTest = delegate { throw new IOException("isolated VRAM restore failure"); };
            try
            {
                ResetFlowCheck(!VramShield.HealFromCrash() && VramShield.HasResidue()
                    && VramShield.RecoveryBlockedForTest && Settings.LoadStr("VramShieldSnap", "") == snapshot,
                    "VRAM restore exception was reported successful or lost its snapshot");
            }
            finally { VramShield.ResetRecoveryForTest(); }
        }

        private static void ResetFlowVramInvalidSnapshotIsPreserved(string root)
        {
            foreach (string raw in new[] { "invalid-owned-snapshot", "0:456", "123:0" })
            {
                ResetFlowPrepareVram();
                ResetFlowCheck(Settings.SaveStr("VramShieldSnap", raw), "could not seed invalid VRAM snapshot");
                int restores = 0;
                VramShield.RestoreReservationForTest = delegate { restores++; return true; };
                try
                {
                    ResetFlowCheck(!VramShield.HealFromCrash() && restores == 0 && VramShield.HasResidue()
                        && Settings.LoadStr("VramShieldSnap", "") == raw, "invalid VRAM identity was cleared or reached restoration");
                }
                finally { VramShield.ResetRecoveryForTest(); }
            }
        }

        private static void ResetFlowVramRetryClearsOnlyAfterConfirmation(string root)
        {
            ResetFlowPrepareVram();
            const string snapshot = "123:456";
            ResetFlowCheck(Settings.SaveStr("VramShieldSnap", snapshot), "could not seed retry VRAM snapshot");
            int restores = 0;
            bool confirmed = false;
            VramShield.RestoreReservationForTest = delegate { restores++; return confirmed; };
            try
            {
                ResetFlowCheck(!VramShield.HealFromCrash() && VramShield.HasResidue(), "failed VRAM attempt did not retain recovery");
                confirmed = true;
                ResetFlowCheck(VramShield.HealFromCrash() && restores == 2 && !VramShield.HasResidue()
                    && !VramShield.RecoveryBlockedForTest && Settings.LoadStr("VramShieldSnap", "") == "",
                    "confirmed VRAM retry did not clear snapshot/blocking state");
                ResetFlowCheck(VramShield.Begin() && restores == 2, "empty new VRAM session performed another restoration");
            }
            finally { VramShield.ResetRecoveryForTest(); }
        }

        private static void ResetFlowPrepareVram()
        {
            VramShield.ResetRecoveryForTest();
            VramShield.ConfigureMutationBoundary(null, null);
            VramShield.RestoreReservationForTest = delegate { throw new InvalidOperationException("Unexpected VRAM restoration in reset test"); };
            VramShield.SampleStepForTest = delegate { throw new InvalidOperationException("Unexpected VRAM sample in reset test"); };
        }

        private static void ResetFlowVramBlockedRecoveryCannotEnterSampling(string root)
        {
            ResetFlowPrepareVram();
            const string snapshot = "123:456";
            ResetFlowCheck(Settings.SaveStr("VramShieldSnap", snapshot), "could not seed blocked VRAM snapshot");
            bool confirmed = false;
            int samples = 0;
            VramShield.RestoreReservationForTest = delegate { return confirmed; };
            VramShield.SampleStepForTest = delegate(int pid, long creation)
            {
                ResetFlowCheck(pid == 777 && creation == 888, "fake VRAM sampling identity changed");
                samples++;
            };
            try
            {
                ResetFlowCheck(!VramShield.Begin(), "unconfirmed previous VRAM target allowed a new session");
                VramShield.SampleIfDue(true, 777, 888);
                ResetFlowCheck(samples == 0 && Settings.LoadStr("VramShieldSnap", "") == snapshot,
                    "new renderer sampling entered or overwrote unresolved VRAM recovery");
                confirmed = true;
                ResetFlowCheck(VramShield.HealFromCrash() && VramShield.Begin(), "confirmed old VRAM target did not unblock the next session");
                VramShield.SampleIfDue(true, 777, 888);
                ResetFlowCheck(samples == 1 && !VramShield.HasResidue(), "pure fake sampling could not resume after confirmed recovery");
            }
            finally { VramShield.ResetRecoveryForTest(); }
        }

        private static void ResetFlowEppPartialRollbackKeepsOriginals(string root)
        {
            using (var f = new ResetFlowEppFixture())
            {
                f.RejectWrite = delegate(Guid scheme, bool secondary, uint value)
                { return secondary && value == 44 || !secondary && value == 10; };
                ResetFlowCheck(!PowerPlan.TryYieldEpp(44) && PowerPlan.EppYielded, "partial EPP write/rollback failure lost pending recovery");
                int writes = f.Writes.Count;
                ResetFlowCheck(!PowerPlan.TryYieldEpp(99) && f.Writes.Count == writes,
                    "unresolved EPP snapshot was overwritten by a new yield attempt");
                f.RejectWrite = null;
                ResetFlowCheck(PowerPlan.RestoreEpp() && !PowerPlan.EppYielded
                    && f.Value(f.FirstScheme, false) == 10 && f.Value(f.FirstScheme, true) == 20,
                    "EPP retry did not restore the first captured originals");
            }
        }

        private static void ResetFlowEppReadbackFailureKeepsPending(string root)
        {
            using (var f = new ResetFlowEppFixture())
            {
                ResetFlowCheck(PowerPlan.TryYieldEpp(44), "could not seed fake yielded EPP");
                f.IgnoreWrites = true; // APIs claim success but fake readback remains yielded.
                ResetFlowCheck(!PowerPlan.RestoreEpp() && PowerPlan.EppYielded, "unverified EPP write cleared pending recovery");
                int writes = f.Writes.Count;
                ResetFlowCheck(!PowerPlan.TryYieldEpp(99) && f.Writes.Count == writes, "failed EPP restoration allowed a new overwrite");
                f.IgnoreWrites = false;
                ResetFlowCheck(PowerPlan.RestoreEpp() && !PowerPlan.EppYielded
                    && f.Value(f.FirstScheme, false) == 10 && f.Value(f.FirstScheme, true) == 20,
                    "readback-rejected EPP restoration could not finish after correction");
            }
        }

        private static void ResetFlowEppReactivationFailureKeepsPending(string root)
        {
            using (var f = new ResetFlowEppFixture())
            {
                ResetFlowCheck(PowerPlan.TryYieldEpp(44), "could not seed reactivation fixture");
                f.SetActiveResult = false;
                ResetFlowCheck(!PowerPlan.RestoreEpp() && PowerPlan.EppYielded, "failed EPP plan reactivation was reported restored");
                f.SetActiveResult = true;
                ResetFlowCheck(PowerPlan.RestoreEpp() && !PowerPlan.EppYielded, "EPP reactivation retry could not finish");
            }
        }

        private static void ResetFlowEppRestoresCapturedScheme(string root)
        {
            using (var f = new ResetFlowEppFixture())
            {
                ResetFlowCheck(PowerPlan.TryYieldEpp(44), "could not seed captured-scheme EPP fixture");
                f.Managed = f.SecondScheme;
                int before = f.Writes.Count;
                ResetFlowCheck(PowerPlan.RestoreEpp() && !PowerPlan.EppYielded, "captured EPP scheme did not restore after managed reference changed");
                for (int i = before; i < f.Writes.Count; i++)
                    ResetFlowCheck(f.Writes[i].Scheme == f.FirstScheme, "EPP restoration wrote the new scheme using old originals");
                ResetFlowCheck(f.Value(f.FirstScheme, false) == 10 && f.Value(f.FirstScheme, true) == 20
                    && f.Value(f.SecondScheme, false) == 71 && f.Value(f.SecondScheme, true) == 72,
                    "EPP restoration changed an unrelated scheme");
            }
        }

        private static void ResetFlowEppMissingOriginalDoesNotWrite(string root)
        {
            using (var f = new ResetFlowEppFixture())
            {
                f.Managed = Guid.Empty;
                ResetFlowCheck(!PowerPlan.TryYieldEpp(44) && !PowerPlan.EppYielded && f.Writes.Count == 0,
                    "missing EPP scheme caused a write");
            }
            using (var f = new ResetFlowEppFixture())
            {
                f.FailPrimaryRead = true;
                ResetFlowCheck(!PowerPlan.TryYieldEpp(44) && !PowerPlan.EppYielded && f.Writes.Count == 0,
                    "missing original primary EPP value caused a write");
            }
        }

        private sealed class ResetFlowEppFixture : IDisposable
        {
            internal readonly Guid FirstScheme = new Guid("891eabfe-51b7-4ee8-bde4-d107ddf0b0b3");
            internal readonly Guid SecondScheme = new Guid("045c77d8-1d0c-42ee-9fdd-90c39d1c4d15");
            internal Guid Managed;
            internal Guid? Current;
            internal bool IgnoreWrites, FailPrimaryRead;
            internal bool SetActiveResult = true;
            internal Func<Guid, bool, uint, bool> RejectWrite;
            internal readonly List<EppWrite> Writes = new List<EppWrite>();
            private readonly Dictionary<string, uint> values = new Dictionary<string, uint>();

            internal sealed class EppWrite
            { internal Guid Scheme; internal bool Secondary; internal uint Value; }

            internal ResetFlowEppFixture()
            {
                PowerPlan.ResetEppForTest();
                Managed = FirstScheme; Current = FirstScheme;
                values[Key(FirstScheme, false)] = 10; values[Key(FirstScheme, true)] = 20;
                values[Key(SecondScheme, false)] = 71; values[Key(SecondScheme, true)] = 72;
                // All five native boundaries are replaced before invoking production logic.
                PowerPlan.EppManagedSchemeForTest = delegate { return Managed; };
                PowerPlan.EppReadAcForTest = delegate(Guid scheme, bool secondary, out uint value)
                {
                    value = 0;
                    return !(FailPrimaryRead && !secondary) && values.TryGetValue(Key(scheme, secondary), out value);
                };
                PowerPlan.EppWriteAcForTest = delegate(Guid scheme, bool secondary, uint value)
                {
                    Writes.Add(new EppWrite { Scheme = scheme, Secondary = secondary, Value = value });
                    if (RejectWrite != null && RejectWrite(scheme, secondary, value)) return false;
                    if (!IgnoreWrites) values[Key(scheme, secondary)] = value;
                    return true;
                };
                PowerPlan.EppCurrentSchemeForTest = delegate { return Current; };
                PowerPlan.EppSetActiveForTest = delegate(Guid scheme)
                {
                    if (SetActiveResult) Current = scheme;
                    return SetActiveResult;
                };
            }

            private static string Key(Guid scheme, bool secondary) { return scheme.ToString("N") + (secondary ? "-1" : "-0"); }
            internal uint Value(Guid scheme, bool secondary) { return values[Key(scheme, secondary)]; }
            public void Dispose() { PowerPlan.ResetEppForTest(); }
        }

        private static void ResetFlowPrepareYield()
        {
            ResetFlowCheck(!PowerPlan.EppYielded, "isolated yield checks require no real EPP residue");
            PowerBudgetYieldRunner.ResetShutdownForTest();
            PowerBudgetYieldRunner.ConfigureMutationBoundary(null, null);
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

        private sealed class ResetFlowYieldWorker : IDisposable
        {
            internal readonly int Generation;
            private readonly ManualResetEvent held = new ManualResetEvent(false);
            private readonly ManualResetEvent release = new ManualResetEvent(false);
            private Exception error;

            internal ResetFlowYieldWorker(Action<int, Action> body)
            {
                Generation = PowerBudgetYieldRunner.StartShutdownWorkerForTest(delegate(int generation)
                {
                    try
                    {
                        body(generation, delegate
                        {
                            held.Set();
                            if (!release.WaitOne(3000)) throw new TimeoutException("Owned fake yield worker was not released");
                        });
                    }
                    catch (Exception failure) { error = failure; }
                });
                ResetFlowCheck(Generation > 0, "could not create the isolated yield worker");
            }
            internal void WaitUntilHeld()
            { ResetFlowCheck(held.WaitOne(3000), "owned fake yield worker never entered: " + error); }
            internal void Finish()
            {
                release.Set();
                ResetFlowCheck(!PowerPlan.EppYielded, "fake yield worker unexpectedly changed real EPP state");
                ResetFlowCheck(PowerBudgetYieldRunner.CloseForShutdown(3000), "owned fake yield worker did not join/drain");
                ResetFlowCheck(error == null, "owned fake yield worker threw: " + error);
            }
            public void Dispose()
            {
                try { Finish(); }
                finally { PowerBudgetYieldRunner.ResetShutdownForTest(); held.Dispose(); release.Dispose(); }
            }
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

        // No ETW, native process mutation or IRQ journal write is available to this fake.
        private sealed class ResetFlowIrqPlatform : IIrqSessionPlatform
        {
            internal int ForbiddenCalls;
            public bool Enabled { get { return false; } }
            public bool IsElevated { get { return false; } }
            public long UtcTicks { get { return DateTime.UtcNow.Ticks; } }
            public string BootStamp { get { return "reset-fake-boot"; } }
            public string TopologyStamp { get { return "reset-fake-topology"; } }
            public ICoreLoadSource OpenCoreLoadSource()
            { ForbiddenCalls++; throw new InvalidOperationException("Unexpected CPU sampling in reset test"); }
            public IIrqSessionCapture CreateCapture(bool timeline)
            { ForbiddenCalls++; throw new InvalidOperationException("Unexpected capture in reset test"); }
            public bool Append(IrqSessionRecord record)
            { ForbiddenCalls++; throw new InvalidOperationException("Unexpected IRQ persistence in reset test"); }
            public string DriverVersion(string name) { return "reset-fake"; }
            public void Log(string message) { }
            public string LoadLastResult() { return ""; }
            public void SaveLastResult(string result) { }
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
    // Dedicated entry point selected by Run-ResetChecks.ps1. It never invokes
    // Program.Main, the full self-test entry point, or the owned-process matrix.
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
