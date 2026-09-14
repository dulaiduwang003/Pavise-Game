// File purpose Per-game family policy regression, every store is an isolated temp fixture
// Does not go through Program or GameMode's Loop Sweep Boost, never touches the real GPU, registry, or scheduled-task writes
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunFamilySuppressionRegressionTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Lang.Cur = 0;
            string oldLog = Logger.LogPath;
            Logger.LogPath = null;
            string root = Path.Combine(Path.GetTempPath(), "PaviseFamilyPolicy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Action<string>[] tests =
            {
                FamilyPolicyDefaultsProtect,
                FamilyPolicyLegacyLibraryProtects,
                FamilyPolicyRetiredIfeoRejectsOldLibrary,
                FamilyPolicyTwoGamesStayIndependent,
                FamilyPolicyRoundTripKeepsOtherFields,
                FamilyPolicyUnknownProfileDoesNotWrite,
                FamilyPolicyIdempotentDoesNotWrite,
                FamilyPolicyEnableFailureIsTransactional,
                FamilyPolicyDisableFailureIsTransactional,
                FamilyPolicyClonedViewsDoNotMutateStore,
                FamilyPolicyForceDoesNotImplyObservation,
                FamilyPolicyRuntimeDefaultsDoNotInheritGlobal,
                FamilyPolicyClosingRejectsQueuedWrite,
                FamilyPolicyCloseReopenRejectsOldEpoch,
                FamilyPolicyClosingWaitsForOwnedWrite,
                FamilyPolicyLifecycleRejectsQueuedWrite,
                FamilyPolicyGenericSettersUseGate,
                FamilyPolicyResetUsesGate,
                FamilyPolicyResetFailureIsTransactional,
                FamilyObservationRejectsSelectionEvidence,
                FamilyObservationRejectsWeakOrInvalidGpu,
                FamilyObservationPersistsOnlyExactTarget,
                FamilyObservationChangedFileInvalidates,
                FamilyObservationLoadedInvalidCannotRevive,
                FamilyObservationChangedPathDoesNotTransfer,
                FamilyObservationStalePathCannotRemoveNewRecord,
                FamilyObservationFileChangesDuringSample,
                FamilyObservationMissingFileDoesNotConfirm,
                FamilyObservationSaveFailureIsOptional,
                FamilyObservationDamagedCacheDoesNotFuseLibrary,
                FamilyObservationIdempotentAndReadOnlyLookup,
                FamilyObservationLoadIsBounded,
                FamilyPolicyLateDialogCannotChangeNewTarget,
                FamilyObservationPolicyToggleKeepsBadge,
                FamilyObservationAsyncRequiresActualSample,
                FamilyObservationForcedEntryNeedsActualSample,
                FamilyObservationAsyncRejectsWeakSamples,
                FamilyObservationAsyncRejectsIdentityChange,
                FamilyObservationAsyncRejectsFocusChange,
                FamilyObservationAsyncRejectsLifecycleChange,
                FamilyObservationAsyncRejectsChangedExecutable,
                FamilyObservationAsyncRejectsRemovedProfile,
                FamilyObservationAsyncRejectsChangedActiveIdentity,
                FamilyObservationAsyncRejectsChangedFile,
                FamilyObservationSharedSamplingGate,
                FamilyObservationNewTargetResetsCooldown,
                FamilyObservationPendingWorkerDoesNotDelayNewTarget,
                FamilyObservationPollingIsBounded,
                FamilyObservationLogsSampleOutcome,
                FamilyObservationRapidTargetChangesRemainBounded,
                FamilyObservationSlowLogDoesNotBlockSampling,
                FamilyProtectionOtherGameOptInDoesNotDisable,
                FamilyProtectionSharedAncestorDoesNotExpandSiblings,
                FamilyProtectionCrossRootDescendant,
                FamilyProtectionRootSeedAfterLauncherExit,
                FamilyProtectionRejectsInvalidCreationChain,
                FamilyProtectionRejectsOtherSession,
                FamilyProtectionRootlessExactAndLearned,
                FamilyProtectionDoesNotIncludeSelf,
                FamilyOverlayKnownHostsReleaseBackground,
                FamilyOverlayPortablePathsAndNormalization,
                FamilyOverlayRejectsInvalidIdentity,
                FamilyOverlayDoesNotExemptSiblingPrograms,
                FamilyOverlayPreservesOtherReasons,
                FamilyOverlayUnknownCreationDoesNotRestore,
                FamilyOverlayPidReuseDoesNotRestoreNewOwner,
                FamilyOverlayUntrackedAndLateHosts,
                FamilyOverlayFailedRestoreKeepsRecoveryDebt,
                FamilyOverlaySteamPolicyFollowsSavedChoice,
                FamilyOverlayOptInKeepsOwnedSuppression,
                FamilyOverlayOptInKeepsIndependentCapture,
                FamilyOverlayOptInKeepsRendererAndWhitelist,
                FamilyOverlayOptInKeepsOtherGameAncestor,
                FamilyOverlayOptInRespectsForegroundRules,
                FamilyVisibilityLateLobbyUsesCurrentSnapshot,
                FamilyVisibilitySameNameKeepsUnownedIdentities,
                FamilyVisibilityHistoryChecksCurrentIdentity,
                FamilyVisibilityPreservesDefaultAndWhitelist,
                FamilyVisibilityPreservesOtherGameAndForeground
            };
            try
            {
                foreach (Action<string> test in tests)
                {
                    test(root);
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                return tests.Length;
            }
            finally
            {
                Logger.LogPath = oldLog;
                // Only the isolated directory allocated above belongs to this test set
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void FamilyPolicyDefaultsProtect(string root)
        {
            GameProfile first = GameProfileStore.NewProfile("first", root, Path.Combine(root, "first.exe"));
            GameProfile second = GameProfileStore.NewProfile("second", root, Path.Combine(root, "second.exe"));
            Eq(false, first.SuppressFamilyBackground);
            Eq(false, second.SuppressFamilyBackground);
            Eq(false, first.Clone().SuppressFamilyBackground);
        }

        private static void FamilyPolicyLegacyLibraryProtects(string root)
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save("GmFamilyExempt", false); // Historical global choice must not silently opt games in
            string dir = FamilyPolicyDirectory(root, "legacy");
            GameProfile first = FamilyPolicyProfile("legacy-a", dir);
            GameProfile second = FamilyPolicyProfile("legacy-b", dir);
            string file = Path.Combine(dir, GameProfileStore.FileName);
            // This is a real pre-feature V5 record, not the new serializer's defaults
            File.WriteAllLines(file, new[]
            {
                "PAVISE_PROFILES_V5", FamilyPolicyLegacyLine(first), FamilyPolicyLegacyLine(second)
            }, new UTF8Encoding(false));
            var mode = new GameMode(dir, new SuppressionCore());
            Eq(false, mode.ProfileStoreSaveFailed);
            Eq(2, mode.GetProfiles().Count);
            foreach (GameProfile profile in mode.GetProfiles())
            {
                Eq(false, profile.SuppressFamilyBackground);
                Eq(false, mode.HasRendererObservation(profile));
            }
        }

        private static string RetiredIfeoRecord(string id, string value)
        {
            return "O|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(id))
                + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes("GmIfeoBoost"))
                + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + Environment.NewLine;
        }

        private static void FamilyPolicyRetiredIfeoRejectsOldLibrary(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "retired-ifeo-invalid"))
            {
                string original = File.ReadAllText(f.LibraryFile);
                string[] invalid =
                {
                    RetiredIfeoRecord(f.First.Id, "1"),
                    RetiredIfeoRecord(f.First.Id, "0"),
                    RetiredIfeoRecord(f.First.Id, "yes"),
                    RetiredIfeoRecord("missing-profile", "1"),
                    RetiredIfeoRecord(f.First.Id, "1") + RetiredIfeoRecord(f.First.Id, "0")
                };
                foreach (string record in invalid)
                {
                    string contents = original + record;
                    File.WriteAllText(f.LibraryFile, contents, new UTF8Encoding(false));
                    var store = new GameProfileStore(f.DirectoryPath);
                    Eq(0, store.LoadProfiles().Count);
                    Eq(true, store.LoadFailed);
                    Eq(true, store.SaveFailed);
                    Eq(false, store.Save(new List<GameProfile>()));
                    var mode = new GameMode(f.DirectoryPath, new SuppressionCore());
                    Eq(true, mode.ProfileStoreSaveFailed);
                    // The subsequent reset, prompt, and exit belong to Program
                    // A bare load must leave the file alone until that cleanup flow actually starts
                    Eq(contents, File.ReadAllText(f.LibraryFile));
                }
            }
        }

        private static void FamilyPolicyTwoGamesStayIndependent(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "isolation"))
            {
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                Eq(true, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(false, f.Current(f.Second.Id).SuppressFamilyBackground);
                Eq(false, f.First.SuppressFamilyBackground); // Caller snapshot is not the live profile
                Eq(1, f.Changes);
                Eq(true, f.Mode.SetProfileFamilySuppression(f.Second.Id, true));
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, false));
                Eq(false, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(true, f.Current(f.Second.Id).SuppressFamilyBackground);
                Eq(3, f.Changes);
                var reloaded = new GameMode(f.DirectoryPath, new SuppressionCore());
                Eq(false, FamilyPolicyFind(reloaded.GetProfiles(), f.First.Id).SuppressFamilyBackground);
                Eq(true, FamilyPolicyFind(reloaded.GetProfiles(), f.Second.Id).SuppressFamilyBackground);
            }
        }

        private static void FamilyPolicyRoundTripKeepsOtherFields(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "roundtrip"))
            {
                string beforeSecond = FamilyPolicyDescribe(f.Current(f.Second.Id));
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                GameProfile saved = FamilyPolicyFind(new GameProfileStore(f.DirectoryPath).LoadProfiles(), f.First.Id);
                Eq(true, saved.SuppressFamilyBackground);
                Eq(f.First.Id, saved.Id);
                Eq(f.First.Name, saved.Name);
                Eq(f.First.Root, saved.Root);
                Eq(f.First.ExecutablePath, saved.ExecutablePath);
                Eq(f.First.LearnedExecutablePath, saved.LearnedExecutablePath);
                Eq(f.First.ForceTrigger, saved.ForceTrigger);
                Eq(true, saved.Entries.SetEquals(f.First.Entries));
                foreach (KeyValuePair<string, string> item in f.First.Overrides)
                    Eq(item.Value, saved.Overrides[item.Key]);
                Eq(beforeSecond, FamilyPolicyDescribe(f.Current(f.Second.Id)));
                Eq(true, saved.Clone().SuppressFamilyBackground);
            }
        }

        private static void FamilyPolicyUnknownProfileDoesNotWrite(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "missing"))
            {
                string before = File.ReadAllText(f.LibraryFile);
                Eq(false, f.Mode.SetProfileFamilySuppression("missing-profile-id", true));
                Eq(false, f.Mode.SetProfileFamilySuppression(null, true));
                Eq(false, f.Mode.SetProfileFamilySuppression("", true));
                Eq(0, f.Changes);
                Eq(false, f.Mode.ProfileStoreSaveFailed);
                Eq(before, File.ReadAllText(f.LibraryFile));
            }
        }

        private static void FamilyPolicyIdempotentDoesNotWrite(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "idempotent"))
            {
                using (var lease = new FileStream(f.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, false));
                Eq(0, f.Changes);
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                string before = File.ReadAllText(f.LibraryFile);
                using (var lease = new FileStream(f.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                Eq(1, f.Changes);
                Eq(false, f.Mode.ProfileStoreSaveFailed);
                Eq(before, File.ReadAllText(f.LibraryFile));
            }
        }

        private static void FamilyPolicyEnableFailureIsTransactional(string root)
        {
            FamilyPolicyFailedSave(root, "enable-failure", false, true);
        }

        private static void FamilyPolicyDisableFailureIsTransactional(string root)
        {
            FamilyPolicyFailedSave(root, "disable-failure", true, false);
        }

        private static void FamilyPolicyFailedSave(string root, string name, bool original, bool requested)
        {
            using (var f = new FamilyPolicyFixture(root, name))
            {
                if (original) Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                string before = File.ReadAllText(f.LibraryFile);
                int changesBefore = f.Changes, failures = 0;
                bool? atFailure = null;
                f.Mode.ProfileStoreSaveFailure += delegate
                {
                    failures++;
                    atFailure = f.Current(f.First.Id).SuppressFamilyBackground;
                };
                using (var lease = new FileStream(f.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(false, f.Mode.SetProfileFamilySuppression(f.First.Id, requested));
                Eq(false, f.Mode.ProfileStoreSaveFailed);
                Eq(0, failures);
                Eq(null, atFailure);
                Eq(original, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(false, f.Current(f.Second.Id).SuppressFamilyBackground);
                Eq(changesBefore, f.Changes);
                Eq(before, File.ReadAllText(f.LibraryFile));
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, requested));
                Eq(0, failures);
                Eq(requested, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(changesBefore + 1, f.Changes);
                Eq(requested, FamilyPolicyFind(new GameProfileStore(f.DirectoryPath).LoadProfiles(), f.First.Id).SuppressFamilyBackground);
                Eq(0, Directory.GetFiles(f.DirectoryPath, "*.tmp").Length);
            }
        }

        private static void FamilyPolicyClonedViewsDoNotMutateStore(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "clones"))
            {
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                GameProfile copied = f.Current(f.First.Id);
                copied.Name = "not published";
                copied.Overrides.Clear();
                copied.Entries.Clear();
                Eq(true, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(f.First.Name, f.Current(f.First.Id).Name);
                Eq(true, f.Current(f.First.Id).Entries.SetEquals(f.First.Entries));
                Eq(false, f.Mode.ProfileStoreSaveFailed);
            }
        }

        private static void FamilyPolicyForceDoesNotImplyObservation(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "forced"))
            {
                Eq(false, f.Mode.HasRendererObservation(f.Current(f.First.Id)));
                Eq(true, f.Mode.SetProfileForceTrigger(f.First.Id, true));
                Eq(true, f.Current(f.First.Id).ForceTrigger);
                Eq(false, f.Mode.HasRendererObservation(f.Current(f.First.Id)));
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                Eq(true, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(false, f.Mode.HasRendererObservation(f.Current(f.First.Id)));
            }
        }

        private static void FamilyPolicyRuntimeDefaultsDoNotInheritGlobal(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "runtime-defaults"))
            {
                Settings.Save("GmFamilyExempt", false);
                Settings.Save(PolicyCatalog.KeySuppressFamily, true);
                Eq("0", PolicyResolver.GlobalValue(PolicyCatalog.KeySuppressFamily));
                Eq(true, FamilyBoundary.FamilyExemptFor(null));
                Eq(true, FamilyBoundary.FamilyExemptFor(f.Current(f.First.Id)));
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                Eq(false, FamilyBoundary.FamilyExemptFor(f.Current(f.First.Id)));
                Eq(true, FamilyBoundary.FamilyExemptFor(f.Current(f.Second.Id)));
                Eq("0", PolicyResolver.Read(f.Current(f.Second.Id), PolicyCatalog.KeySuppressFamily));
            }
        }

        private static void FamilyPolicyClosingRejectsQueuedWrite(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "late-write"))
            {
                f.EnableFakePolicyGate();
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                int oldEpoch = f.Mode.FamilyPolicyEpoch, writes = 0;
                Eq(true, f.Mode.FamilyPolicyEpochCurrent(oldEpoch));
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, false));
                Eq(false, f.Mode.FamilyPolicyEpochCurrent(oldEpoch));
                Eq(false, FamilyPolicyRunGate(f.Mode, oldEpoch, delegate { writes++; }));
                Eq(0, writes);
                // After the epoch change, ordinary background processes are still handled
                Eq(true, FamilyPolicyRunGate(f.Mode, f.Mode.FamilyPolicyEpoch, delegate { writes++; }));
                Eq(1, writes);
                Eq(false, f.Current(f.First.Id).SuppressFamilyBackground);
            }
        }

        private static void FamilyPolicyCloseReopenRejectsOldEpoch(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "epoch-aba"))
            {
                f.EnableFakePolicyGate();
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                int oldEpoch = f.Mode.FamilyPolicyEpoch, writes = 0;
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, false));
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                Eq(true, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(false, FamilyPolicyRunGate(f.Mode, oldEpoch, delegate { writes++; }));
                Eq(0, writes);
            }
        }

        private static void FamilyPolicyClosingWaitsForOwnedWrite(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "serialized-close"))
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var closeStarted = new ManualResetEvent(false))
            {
                f.EnableFakePolicyGate();
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                int oldEpoch = f.Mode.FamilyPolicyEpoch, writes = 0;
                Task<bool> owned = Task.Factory.StartNew(delegate
                {
                    return FamilyPolicyRunGate(f.Mode, oldEpoch, delegate
                    {
                        entered.Set();
                        if (!release.WaitOne(3000)) throw new Exception("Fake policy action was not released.");
                        Interlocked.Increment(ref writes);
                    });
                });
                Task<bool> close = null;
                try
                {
                    Eq(true, entered.WaitOne(3000));
                    close = Task.Factory.StartNew(delegate
                    {
                        closeStarted.Set();
                        return f.Mode.SetProfileFamilySuppression(f.First.Id, false);
                    });
                    Eq(true, closeStarted.WaitOne(3000));
                    // Gate still held by the old action yet the closure completed: that's a bug
                    Eq(false, close.Wait(50));
                }
                finally
                {
                    release.Set();
                    Eq(true, owned.Wait(3000));
                    if (close != null) Eq(true, close.Wait(3000));
                }
                Eq(true, owned.Result);
                Eq(true, close.Result);
                Eq(1, writes);
                Eq(false, FamilyPolicyRunGate(f.Mode, oldEpoch, delegate { writes++; }));
                Eq(1, writes);
            }
        }

        private static void FamilyPolicyLifecycleRejectsQueuedWrite(string root)
        {
            foreach (string field in new[] { "enabled", "stopping", "panicReq", "bgSuppressOn" })
                using (var f = new FamilyPolicyFixture(root, "lifecycle-" + field))
                {
                    f.EnableFakePolicyGate();
                    int epoch = f.Mode.FamilyPolicyEpoch, writes = 0;
                    Eq(true, f.Mode.FamilyPolicyEpochCurrent(epoch));
                    FamilyPolicySetField(f.Mode, field, field == "stopping" || field == "panicReq");
                    Eq(false, f.Mode.FamilyPolicyEpochCurrent(epoch));
                    Eq(false, FamilyPolicyRunGate(f.Mode, epoch, delegate { writes++; }));
                    Eq(0, writes);
                }
        }

        private static void FamilyPolicyGenericSettersUseGate(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "generic-setters"))
            {
                f.EnableFakePolicyGate();
                int beforeEnable = f.Mode.FamilyPolicyEpoch;
                Eq(true, f.Mode.SetProfileOverride(f.First.Id, PolicyCatalog.KeySuppressFamily, "1"));
                Eq(true, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(false, f.Mode.FamilyPolicyEpochCurrent(beforeEnable));
                int beforeDisable = f.Mode.FamilyPolicyEpoch;
                Eq(true, f.Mode.SetProfileOverride(f.First.Id, PolicyCatalog.KeySuppressFamily, "true"));
                Eq(beforeDisable, f.Mode.FamilyPolicyEpoch);
                Eq(true, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(true, f.Mode.ClearProfileOverride(f.First.Id, PolicyCatalog.KeySuppressFamily));
                Eq(false, f.Current(f.First.Id).SuppressFamilyBackground);
                int writes = 0;
                Eq(false, FamilyPolicyRunGate(f.Mode, beforeDisable, delegate { writes++; }));
                Eq(0, writes);
                Eq(false, f.Current(f.Second.Id).SuppressFamilyBackground);
            }
        }

        private static void FamilyPolicyResetUsesGate(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset"))
            {
                f.EnableFakePolicyGate();
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                Eq(true, f.Mode.SetProfileFamilySuppression(f.Second.Id, true));
                int epoch = f.Mode.FamilyPolicyEpoch, writes = 0;
                int beforeCount = f.Current(f.First.Id).Overrides.Count;
                Eq(beforeCount, f.Mode.ClearProfileOverrides(f.First.Id));
                Eq(false, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(true, f.Current(f.Second.Id).SuppressFamilyBackground);
                Eq(0, f.Current(f.First.Id).Overrides.Count);
                Eq(false, FamilyPolicyRunGate(f.Mode, epoch, delegate { writes++; }));
                Eq(0, writes);
                Eq(false, FamilyPolicyFind(new GameProfileStore(f.DirectoryPath).LoadProfiles(), f.First.Id).SuppressFamilyBackground);
            }
        }

        private static void FamilyPolicyResetFailureIsTransactional(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "reset-failure"))
            {
                f.EnableFakePolicyGate();
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                string before = File.ReadAllText(f.LibraryFile);
                string snapshot = FamilyPolicyDescribe(f.Current(f.First.Id));
                int changes = f.Changes, epoch = f.Mode.FamilyPolicyEpoch, writes = 0;
                using (var lease = new FileStream(f.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(0, f.Mode.ClearProfileOverrides(f.First.Id));
                Eq(false, f.Mode.ProfileStoreSaveFailed);
                Eq(snapshot, FamilyPolicyDescribe(f.Current(f.First.Id)));
                Eq(before, File.ReadAllText(f.LibraryFile));
                Eq(changes, f.Changes);
                Eq(true, FamilyPolicyRunGate(f.Mode, epoch, delegate { writes++; }));
                Eq(1, writes);
                Eq(f.Current(f.First.Id).Overrides.Count, f.Mode.ClearProfileOverrides(f.First.Id));
                Eq(false, f.Current(f.First.Id).SuppressFamilyBackground);
            }
        }

        private static void FamilyObservationRejectsSelectionEvidence(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-selection"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                RendererFileStamp stamp = RendererFileStamp.Read(f.First.ExecutablePath);
                foreach (RendererObservationEvidence evidence in new[]
                    { RendererObservationEvidence.None, RendererObservationEvidence.Window, RendererObservationEvidence.Forced })
                {
                    Eq(false, store.RecordActivity(f.First.Id, f.First.ExecutablePath, evidence, 50, stamp));
                    Eq(false, store.Has(f.First.Id, f.First.ExecutablePath));
                }
                Eq(false, File.Exists(Path.Combine(f.DirectoryPath, RendererObservationStore.FileName)));
            }
        }

        private static void FamilyObservationRejectsWeakOrInvalidGpu(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-invalid-gpu"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                RendererFileStamp stamp = RendererFileStamp.Read(f.First.ExecutablePath);
                foreach (double value in new[] { -1.0, 0, GpuEvidence.MinElectUtilization - 0.001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    Eq(false, store.RecordActivity(f.First.Id, f.First.ExecutablePath,
                        RendererObservationEvidence.Gpu3D, value, stamp));
                }
                Eq(false, store.RecordActivity(f.First.Id, f.First.ExecutablePath,
                    RendererObservationEvidence.Gpu3D, 50, null));
                Eq(false, store.Has(f.First.Id, f.First.ExecutablePath));
            }
        }

        private static void FamilyObservationPersistsOnlyExactTarget(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-roundtrip"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                Eq(true, FamilyObservationRecord(store, f.First));
                Eq(true, store.Has(f.First.Id, f.First.ExecutablePath));
                Eq(false, store.Has(f.Second.Id, f.First.ExecutablePath));
                Eq(false, store.Has(f.First.Id, f.Second.ExecutablePath));
                Eq(false, store.Has(null, f.First.ExecutablePath));
                var loaded = new RendererObservationStore(f.DirectoryPath);
                Eq(false, loaded.Has(f.First.Id, f.First.ExecutablePath));
                Eq(true, loaded.NeedsValidation(f.First.Id, f.First.ExecutablePath, DateTime.UtcNow.Ticks));
                Eq(true, loaded.Validate(f.First.Id, f.First.ExecutablePath));
                Eq(true, loaded.Has(f.First.Id, f.First.ExecutablePath));
                Eq(false, loaded.NeedsValidation(f.First.Id, f.First.ExecutablePath, DateTime.UtcNow.Ticks));
                Eq(true, loaded.NeedsValidation(f.First.Id, f.First.ExecutablePath, DateTime.UtcNow.AddSeconds(6).Ticks));
            }
        }

        private static void FamilyObservationChangedFileInvalidates(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-file-change"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                Eq(true, FamilyObservationRecord(store, f.First));
                File.AppendAllText(f.First.ExecutablePath, "new-file-contents");
                Eq(true, store.Validate(f.First.Id, f.First.ExecutablePath));
                Eq(false, store.Has(f.First.Id, f.First.ExecutablePath));
                store.Persist();
                var loaded = new RendererObservationStore(f.DirectoryPath);
                Eq(false, loaded.NeedsValidation(f.First.Id, f.First.ExecutablePath, DateTime.UtcNow.Ticks));
            }
        }

        private static void FamilyObservationLoadedInvalidCannotRevive(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-no-revive"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                Eq(true, FamilyObservationRecord(store, f.First));
                byte[] original = File.ReadAllBytes(f.First.ExecutablePath);
                DateTime originalTime = File.GetLastWriteTimeUtc(f.First.ExecutablePath);
                var loaded = new RendererObservationStore(f.DirectoryPath);
                File.AppendAllText(f.First.ExecutablePath, "different");
                bool changed = loaded.Validate(f.First.Id, f.First.ExecutablePath);
                Eq(true, changed); // Invalid removal is dirty even before a loaded badge was validated
                if (changed) loaded.Persist(); // Mirrors the background validation consumer
                File.WriteAllBytes(f.First.ExecutablePath, original);
                File.SetLastWriteTimeUtc(f.First.ExecutablePath, originalTime);
                var reopened = new RendererObservationStore(f.DirectoryPath);
                Eq(false, reopened.Validate(f.First.Id, f.First.ExecutablePath));
                Eq(false, reopened.Has(f.First.Id, f.First.ExecutablePath));
            }
        }

        private static void FamilyObservationChangedPathDoesNotTransfer(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-path-change"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                Eq(true, FamilyObservationRecord(store, f.First));
                Eq(false, store.Has(f.First.Id, f.Second.ExecutablePath));
                Eq(false, store.Validate(f.First.Id, f.Second.ExecutablePath));
                // The owning config-change path explicitly forgets the old evidence
                // A random stale validation caller has no authority to delete
                store.Forget(f.First.Id);
                Eq(false, store.Has(f.First.Id, f.First.ExecutablePath));
                Eq(false, store.Has(f.First.Id, f.Second.ExecutablePath));
                store.Persist();
                var loaded = new RendererObservationStore(f.DirectoryPath);
                Eq(false, loaded.NeedsValidation(f.First.Id, f.First.ExecutablePath, DateTime.UtcNow.Ticks));
            }
        }

        private static void FamilyObservationStalePathCannotRemoveNewRecord(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-stale-validation"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                Eq(true, FamilyObservationRecord(store, f.First));
                GameProfile replacement = f.First.Clone();
                replacement.ExecutablePath = f.Second.ExecutablePath;
                Eq(true, FamilyObservationRecord(store, replacement));
                Eq(false, store.Validate(f.First.Id, f.First.ExecutablePath));
                Eq(true, store.Has(replacement.Id, replacement.ExecutablePath));
                Eq(false, store.Has(f.First.Id, f.First.ExecutablePath));
                store.Persist();
                var reloaded = new RendererObservationStore(f.DirectoryPath);
                Eq(true, reloaded.Validate(replacement.Id, replacement.ExecutablePath));
                Eq(true, reloaded.Has(replacement.Id, replacement.ExecutablePath));
            }
        }

        private static void FamilyObservationFileChangesDuringSample(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-changing-sample"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                RendererFileStamp before = RendererFileStamp.Read(f.First.ExecutablePath);
                File.AppendAllText(f.First.ExecutablePath, "changed-while-sampling");
                Eq(false, store.RecordActivity(f.First.Id, f.First.ExecutablePath,
                    RendererObservationEvidence.Gpu3D, 50, before));
                Eq(false, store.Has(f.First.Id, f.First.ExecutablePath));
            }
        }

        private static void FamilyObservationMissingFileDoesNotConfirm(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-missing-file"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                RendererFileStamp before = RendererFileStamp.Read(f.First.ExecutablePath);
                File.Delete(f.First.ExecutablePath); // Only the inert byte file owned by this fixture
                Eq(null, RendererFileStamp.Read(f.First.ExecutablePath));
                Eq(false, store.RecordActivity(f.First.Id, f.First.ExecutablePath,
                    RendererObservationEvidence.Gpu3D, 50, before));
                Eq(false, store.Has(f.First.Id, f.First.ExecutablePath));
            }
        }

        private static void FamilyObservationSaveFailureIsOptional(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-save-failure"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                Eq(true, FamilyObservationRecord(store, f.First));
                string cache = Path.Combine(f.DirectoryPath, RendererObservationStore.FileName);
                string before = File.ReadAllText(cache), libraryBefore = File.ReadAllText(f.LibraryFile);
                using (var lease = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(false, FamilyObservationRecord(store, f.Second));
                Eq(true, store.Has(f.First.Id, f.First.ExecutablePath));
                Eq(false, store.Has(f.Second.Id, f.Second.ExecutablePath));
                Eq(before, File.ReadAllText(cache));
                Eq(libraryBefore, File.ReadAllText(f.LibraryFile));
                Eq(false, f.Mode.ProfileStoreSaveFailed);
                Eq(0, Directory.GetFiles(f.DirectoryPath, "*.tmp").Length);
                Eq(true, FamilyObservationRecord(store, f.Second)); // Optional history is not a fatal library fuse
                Eq(true, store.Has(f.Second.Id, f.Second.ExecutablePath));
            }
        }

        private static void FamilyObservationDamagedCacheDoesNotFuseLibrary(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-damaged"))
            {
                string libraryBefore = File.ReadAllText(f.LibraryFile);
                File.WriteAllText(Path.Combine(f.DirectoryPath, RendererObservationStore.FileName), "not a valid cache\n");
                var loaded = new GameMode(f.DirectoryPath, new SuppressionCore());
                Eq(false, loaded.ProfileStoreSaveFailed);
                Eq(2, loaded.GetProfiles().Count);
                Eq(false, loaded.HasRendererObservation(FamilyPolicyFind(loaded.GetProfiles(), f.First.Id)));
                Eq(libraryBefore, File.ReadAllText(f.LibraryFile));
                var store = new RendererObservationStore(f.DirectoryPath);
                Eq(true, FamilyObservationRecord(store, f.First));
                Eq(true, store.Has(f.First.Id, f.First.ExecutablePath));
            }
        }

        private static void FamilyObservationIdempotentAndReadOnlyLookup(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-read-only"))
            {
                var store = new RendererObservationStore(f.DirectoryPath);
                Eq(true, FamilyObservationRecord(store, f.First));
                string cache = Path.Combine(f.DirectoryPath, RendererObservationStore.FileName);
                string before = File.ReadAllText(cache);
                using (var lease = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(true, FamilyObservationRecord(store, f.First));
                Eq(before, File.ReadAllText(cache));
                using (var fileLease = new FileStream(f.First.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    Eq(true, store.Has(f.First.Id, f.First.ExecutablePath)); // Cached UI lookup never stats or opens EXE
            }
        }

        private static void FamilyObservationLoadIsBounded(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-bounded"))
            {
                var initial = new RendererObservationStore(f.DirectoryPath);
                Eq(true, FamilyObservationRecord(initial, f.First));
                string cachePath = Path.Combine(f.DirectoryPath, RendererObservationStore.FileName);
                string[] sample = File.ReadAllLines(cachePath);
                Eq(2, sample.Length);
                Eq("PAVISE_RENDER_ACTIVITY_V1", sample[0]);
                Eq(6, sample[1].Split('|').Length);
                string recordSuffix = sample[1].Substring(sample[1].IndexOf('|'));
                var records = new List<string> { sample[0] };
                for (int i = 0; i < 2050; i++)
                    records.Add(FamilyPolicyBase64("record-" + i) + recordSuffix);
                File.WriteAllLines(cachePath, records.ToArray(), new UTF8Encoding(false));
                var store = new RendererObservationStore(f.DirectoryPath);
                Eq(true, store.NeedsValidation("record-2047", f.First.ExecutablePath, DateTime.UtcNow.Ticks));
                Eq(false, store.NeedsValidation("record-2048", f.First.ExecutablePath, DateTime.UtcNow.Ticks));
                Eq(false, store.Has("record-2047", f.First.ExecutablePath)); // No auto-validation on disk load
            }
        }

        private static bool FamilyObservationRecord(RendererObservationStore store, GameProfile profile)
        {
            return store.RecordActivity(profile.Id, profile.ExecutablePath,
                RendererObservationEvidence.Gpu3D, GpuEvidence.MinElectUtilization,
                RendererFileStamp.Read(profile.ExecutablePath));
        }

        private static GameDetection FamilyObservationTarget(GameProfile profile)
        {
            var target = new GameDetection
            {
                Profile = profile.Clone(), RendererPid = 80210, RendererCreation = 1337000000000000L,
                RendererPath = profile.ExecutablePath, RendererName = Path.GetFileNameWithoutExtension(profile.ExecutablePath),
                RendererCandidateSelected = true, RendererUserSelected = true, RendererForeground = true
            };
            target.FamilyPids.Add(target.RendererPid);
            return target;
        }

        private static void FamilyPolicyLateDialogCannotChangeNewTarget(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "late-dialog"))
            {
                string oldPath = f.First.ExecutablePath;
                string replacement = FamilyObservationLearnNewTarget(f);
                Eq(false, f.Mode.SetProfileFamilySuppression(f.First.Id, true, oldPath));
                Eq(false, f.Current(f.First.Id).SuppressFamilyBackground);
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true, replacement));
                Eq(true, f.Current(f.First.Id).SuppressFamilyBackground);
            }
        }

        private static void FamilyObservationPolicyToggleKeepsBadge(string root)
        {
            using (var f = new FamilyPolicyFixture(root, "observation-policy-independent"))
            {
                var store = (RendererObservationStore)FamilyPolicyGetField(f.Mode, "rendererObservations");
                Eq(true, FamilyObservationRecord(store, f.First));
                Eq(true, f.Mode.HasRendererObservation(f.Current(f.First.Id)));
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, true));
                Eq(true, f.Mode.HasRendererObservation(f.Current(f.First.Id)));
                Eq(true, f.Mode.SetProfileFamilySuppression(f.First.Id, false));
                Eq(true, f.Mode.HasRendererObservation(f.Current(f.First.Id)));
            }
        }

        private static void FamilyObservationAsyncRequiresActualSample(string root)
        {
            using (var f = new FamilyObservationFixture(root, "async-sample", false))
            {
                Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
                f.Target.Evidence = "synthetic fullscreen/manual selection, not a GPU measurement";
                f.Observe(); f.WaitForSample();
                for (int i = 0; i < 8; i++) f.Observe();
                Eq(1, f.Calls);
                Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
                f.FinishSample();
                Eq(true, f.Mode.HasRendererObservation(f.Library.Current(f.Target.Profile.Id)));
                Eq(false, f.Mode.HasRendererObservation(f.Library.Second));
                f.Observe();
                Eq(1, f.Calls); // A current cached badge does not cause per-frame sampling
            }
        }

        private static void FamilyObservationForcedEntryNeedsActualSample(string root)
        {
            using (var f = new FamilyObservationFixture(root, "async-force", true))
            {
                Eq(true, f.Target.Profile.ForceTrigger);
                Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
                f.Observe(); f.WaitForSample();
                Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
                f.FinishSample();
                Eq(true, f.Mode.HasRendererObservation(f.Target.Profile)); // Actual activity can be observed for a forced target too
            }
        }

        private static void FamilyObservationAsyncRejectsWeakSamples(string root)
        {
            for (int variant = 0; variant < 5; variant++)
                using (var f = new FamilyObservationFixture(root, "async-weak-" + variant, false))
                {
                    f.ReturnNull = variant == 0;
                    f.ReturnMissing = variant == 1;
                    if (variant == 2) f.Utilization = GpuEvidence.MinElectUtilization - 0.01;
                    if (variant == 3) f.Utilization = double.NaN;
                    if (variant == 4) f.Utilization = double.PositiveInfinity;
                    f.Observe(); f.WaitForSample(); f.FinishSample();
                    Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
                    Eq(false, f.Mode.ProfileStoreSaveFailed);
                }
        }

        private static void FamilyObservationAsyncRejectsIdentityChange(string root)
        {
            using (var f = new FamilyObservationFixture(root, "async-identity", false))
            {
                f.Observe(); f.WaitForSample();
                f.IdentityValid = false;
                f.FinishSample();
                Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
            }
        }

        private static void FamilyObservationAsyncRejectsFocusChange(string root)
        {
            using (var f = new FamilyObservationFixture(root, "async-focus", false))
            {
                f.Observe(); f.WaitForSample();
                f.ForegroundPid = 0;
                f.FinishSample();
                Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
            }
        }

        private static void FamilyObservationAsyncRejectsLifecycleChange(string root)
        {
            foreach (string state in new[] { "disabled", "stopping", "panic", "epoch" })
                using (var f = new FamilyObservationFixture(root, "async-state-" + state, false))
                {
                    f.Observe(); f.WaitForSample();
                    if (state == "disabled") FamilyPolicySetField(f.Mode, "enabled", false);
                    if (state == "stopping") FamilyPolicySetField(f.Mode, "stopping", true);
                    if (state == "panic") FamilyPolicySetField(f.Mode, "panicReq", true);
                    if (state == "epoch") FamilyPolicyInvoke(f.Mode, "InvalidateRendererHandoff");
                    f.FinishSample();
                    Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
                }
        }

        private static void FamilyObservationAsyncRejectsChangedExecutable(string root)
        {
            using (var f = new FamilyObservationFixture(root, "async-path", false))
            {
                f.Observe(); f.WaitForSample();
                string replacement = FamilyObservationLearnNewTarget(f.Library);
                f.FinishSample();
                GameProfile live = f.Library.Current(f.Target.Profile.Id);
                Eq(replacement, live.ExecutablePath);
                Eq(false, f.Mode.HasRendererObservation(live));
                Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
            }
        }

        private static void FamilyObservationAsyncRejectsRemovedProfile(string root)
        {
            using (var f = new FamilyObservationFixture(root, "async-removed", false))
            {
                f.Observe(); f.WaitForSample();
                f.Mode.RemoveProfile(f.Target.Profile.Id);
                f.FinishSample();
                Eq(1, f.Mode.GetProfiles().Count);
                Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
            }
        }

        private static void FamilyObservationAsyncRejectsChangedActiveIdentity(string root)
        {
            using (var f = new FamilyObservationFixture(root, "async-active-identity", false))
            {
                f.Observe(); f.WaitForSample();
                GameDetection replacement = RendererHandoffTracker.Copy(f.Target);
                replacement.RendererCreation++;
                FamilyPolicySetField(f.Mode, "activeDetection", replacement);
                f.FinishSample();
                Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
            }
        }

        private static void FamilyObservationAsyncRejectsChangedFile(string root)
        {
            using (var f = new FamilyObservationFixture(root, "async-file", false))
            {
                f.Observe(); f.WaitForSample();
                File.AppendAllText(f.Target.RendererPath, "changed-while-evidence-worker-pending");
                f.FinishSample();
                Eq(false, f.Mode.HasRendererObservation(f.Target.Profile));
            }
        }

        private static void FamilyObservationSharedSamplingGate(string root)
        {
            using (var f = new FamilyObservationFixture(root, "async-shared-gate", false))
            {
                FamilyPolicySetField(f.Mode, "rendererGpuSamplingBusy", 1); // A handoff worker owns the same production gate
                f.Observe();
                Eq(0, f.Calls);
                Eq(0, (int)FamilyPolicyGetField(f.Mode, "rendererActivityBusy"));
                FamilyPolicySetField(f.Mode, "rendererGpuSamplingBusy", 0);
                f.Observe(); f.WaitForSample(); f.FinishSample();
                Eq(1, f.Calls);
                Eq(true, f.Mode.HasRendererObservation(f.Target.Profile));
            }
        }

        private static void FamilyObservationRapidTargetChangesRemainBounded(string root)
        {
            foreach (bool foreground in new[] { true, false })
                using (var f = new FamilyObservationFixture(root, "rapid-targets-" + foreground, false))
                {
                    string oldLog = Logger.LogPath;
                    try
                    {
                        Logger.LogPath = Path.Combine(f.Library.DirectoryPath, "rapid.log");
                        f.Utilization = 0;
                        for (int i = 0; i < 20; i++)
                        {
                            var target = RendererHandoffTracker.Copy(f.Target);
                            target.RendererPid += i % 2;
                            FamilyObservationSelectFixtureTarget(f, target);
                            if (!foreground) f.Mode.RendererTestForeground = delegate { return 0; };
                            FamilyPolicyInvoke(f.Mode, "MaybeObserveRendererActivity", target);
                            f.FinishSample();
                        }
                        Eq(foreground ? 1 : 0, f.Calls);
                        Eq(foreground ? 2 : 1, File.ReadAllLines(Logger.LogPath).Length);
                        if (foreground)
                        {
                            FamilyObservationSelectFixtureTarget(f, f.Target);
                            f.NowMs += 9999;
                            f.Observe(); f.FinishSample();
                            Eq(1, f.Calls);
                            f.NowMs++;
                            f.Observe(); f.FinishSample();
                            Eq(2, f.Calls); // Once the global interval expires, a new target samples immediately, no inheriting the old target's long backoff
                        }
                    }
                    finally { Logger.LogPath = oldLog; }
                }
        }

        private static void FamilyObservationNewTargetResetsCooldown(string root)
        {
            foreach (string change in new[] { "pid", "pid-reused", "profile", "session" })
                using (var f = new FamilyObservationFixture(root, "fresh-observation-" + change, false))
                {
                    f.Utilization = 0;
                    f.Observe(); f.WaitForSample(); f.FinishSample();
                    // Simulate the old target already at max backoff, switching targets must get one sampling chance right away
                    long deadline = (long)FamilyPolicyGetField(f.Mode, "rendererActivityNextMs") + 120000L;
                    FamilyPolicySetField(f.Mode, "rendererActivityNextMs", deadline);
                    var target = RendererHandoffTracker.Copy(f.Target);
                    if (change == "pid") target.RendererPid++;
                    if (change == "pid-reused") target.RendererCreation++;
                    if (change == "profile")
                    {
                        var previous = RendererHandoffTracker.Copy(f.Target);
                        previous.Profile.Id = "old-profile";
                        FamilyPolicySetField(f.Mode, "rendererActivityTarget", previous);
                    }
                    if (change == "session") FamilyPolicyInvoke(f.Mode, "InvalidateRendererHandoff");
                    f.NowMs += 10000;
                    FamilyObservationSelectFixtureTarget(f, target);
                    f.Utilization = 42;
                    FamilyPolicyInvoke(f.Mode, "MaybeObserveRendererActivity", target);
                    f.FinishSample();
                    Eq(2, f.Calls);
                    Eq(1, (int)FamilyPolicyGetField(f.Mode, "rendererActivityAttempts"));
                    Eq(true, f.Mode.HasRendererObservation(target.Profile));
                }
        }

        private static void FamilyObservationSelectFixtureTarget(FamilyObservationFixture f, GameDetection target)
        {
            FamilyPolicySetField(f.Mode, "activeDetection", RendererHandoffTracker.Copy(target));
            f.Mode.RendererTestForeground = delegate { return target.RendererPid; };
            f.Mode.RendererTestIdentity = delegate(GameDetection value)
                { return RendererHandoffTracker.SameIdentity(value, target); };
        }

        private static void FamilyObservationPendingWorkerDoesNotDelayNewTarget(string root)
        {
            using (var f = new FamilyObservationFixture(root, "pending-old-observation", false))
            {
                f.Observe(); f.WaitForSample();
                var target = RendererHandoffTracker.Copy(f.Target);
                target.RendererPid++;
                FamilyObservationSelectFixtureTarget(f, target);
                FamilyPolicyInvoke(f.Mode, "MaybeObserveRendererActivity", target);
                Eq(1, f.Calls); // No concurrent second GPU query while the old sample is still running
                f.FinishSample();
                Eq(false, f.Mode.HasRendererObservation(target.Profile));
                f.NowMs += 10000;
                FamilyPolicyInvoke(f.Mode, "MaybeObserveRendererActivity", target);
                f.FinishSample();
                Eq(2, f.Calls);
                Eq(true, f.Mode.HasRendererObservation(target.Profile));
            }
        }

        private static void FamilyObservationPollingIsBounded(string root)
        {
            string oldLog = Logger.LogPath;
            try
            {
                foreach (string state in new[] { "foreground", "gpu-busy", "cooldown", "recorded" })
                    using (var f = new FamilyObservationFixture(root, "observation-polling-" + state, false))
                    {
                        Logger.LogPath = Path.Combine(f.Library.DirectoryPath, "observation.log");
                        if (state == "foreground") f.ForegroundPid = 0;
                        if (state == "gpu-busy") FamilyPolicySetField(f.Mode, "rendererGpuSamplingBusy", 1);
                        if (state == "cooldown" || state == "recorded")
                        {
                            if (state == "cooldown") f.Utilization = 0;
                            f.Observe(); f.WaitForSample(); f.FinishSample();
                        }
                        else { f.Observe(); f.FinishSample(); }
                        int calls = f.Calls;
                        long logBytes = new FileInfo(Logger.LogPath).Length;
                        for (int i = 0; i < 10000; i++) f.Observe();
                        Eq(calls, f.Calls); // High-frequency state checks don't trigger repeat sampling
                        Eq(logBytes, new FileInfo(Logger.LogPath).Length); // No per-frame log spam during wait and cooldown
                        if (state == "gpu-busy") FamilyPolicySetField(f.Mode, "rendererGpuSamplingBusy", 0);
                    }
            }
            finally { Logger.LogPath = oldLog; }
        }

        private static void FamilyObservationLogsSampleOutcome(string root)
        {
            string oldLog = Logger.LogPath;
            try
            {
                foreach (string outcome in new[] { "GpuUnavailable", "PidMissing", "BelowThreshold",
                    "NearThreshold", "ForegroundChanged", "IdentityUnavailable", "FileUnavailable", "FileChanged", "SaveFailed", "Error", "Recorded" })
                    using (var f = new FamilyObservationFixture(root, "observation-reason-" + outcome, false))
                    {
                        Logger.LogPath = Path.Combine(f.Library.DirectoryPath, "observation.log");
                        f.ReturnNull = outcome == "GpuUnavailable";
                        f.ReturnMissing = outcome == "PidMissing";
                        f.ThrowSample = outcome == "Error";
                        if (outcome == "BelowThreshold") f.Utilization = 9.99;
                        if (outcome == "NearThreshold") f.Utilization = 9.9999999;
                        if (outcome == "FileUnavailable") File.Delete(f.Target.RendererPath);
                        FileStream cacheLock = null;
                        try
                        {
                            if (outcome == "SaveFailed")
                                cacheLock = new FileStream(Path.Combine(f.Library.DirectoryPath, RendererObservationStore.FileName),
                                    FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                            f.Observe();
                            if (outcome != "FileUnavailable") f.WaitForSample();
                            if (outcome == "ForegroundChanged") f.ForegroundPid = 0;
                            if (outcome == "IdentityUnavailable") f.IdentityValid = false;
                            if (outcome == "FileChanged") File.AppendAllText(f.Target.RendererPath, "changed");
                            f.FinishSample();
                        }
                        finally { if (cacheLock != null) cacheLock.Dispose(); }
                        string log = File.ReadAllText(Logger.LogPath);
                        Eq(true, log.Contains("[reason=Started]"));
                        Eq(true, log.Contains("[reason=" + (outcome == "NearThreshold" ? "BelowThreshold" : outcome) + "]"));
                        Eq(true, log.Contains("pid " + f.Target.RendererPid));
                        Eq(true, log.Contains("profile=" + f.Target.Profile.Id));
                        if (outcome == "BelowThreshold") Eq(true, log.Contains("gpu3d=9.99%"));
                        if (outcome == "NearThreshold") Eq(true, log.Contains("gpu3d=9.9999999%"));
                        if (outcome == "ForegroundChanged") Eq(true, log.Contains("foreground=0"));
                        Eq(outcome == "Recorded", f.Mode.HasRendererObservation(f.Target.Profile));
                    }
            }
            finally { Logger.LogPath = oldLog; }
        }

        private static void FamilyObservationSlowLogDoesNotBlockSampling(string root)
        {
            using (var f = new FamilyObservationFixture(root, "blocked-log", false))
            {
                object logGate = typeof(Logger).GetField("lk", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                lock (logGate)
                {
                    Task request = Task.Factory.StartNew(delegate { f.Observe(); });
                    Eq(true, request.Wait(1000)); // The log lock must not stall the detection thread that invokes observation
                    f.WaitForSample(); // The real sample must not wait for the log to hit disk either
                    f.SampleReady.Set();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    while ((int)FamilyPolicyGetField(f.Mode, "rendererActivityBusy") != 0 && timer.ElapsedMilliseconds < 3000)
                        Thread.Sleep(1);
                    Eq(0, (int)FamilyPolicyGetField(f.Mode, "rendererActivityBusy"));
                    Eq(0, (int)FamilyPolicyGetField(f.Mode, "rendererGpuSamplingBusy"));
                    for (int i = 0; i < 50; i++)
                        FamilyPolicyInvoke(f.Mode, "TraceRendererObservation", f.Target, "SamplingBusy", "synthetic");
                    object traceGate = FamilyPolicyGetField(f.Mode, "rendererObservationTraceGate");
                    lock (traceGate)
                    {
                        var queued = (Queue<string>)FamilyPolicyGetField(f.Mode, "rendererObservationTraceLines");
                        Eq(true, queued.Count <= 8);
                        Eq(1, (int)FamilyPolicyGetField(f.Mode, "rendererObservationTraceBusy"));
                    }
                }
                f.FinishSample();
                Eq(true, f.Mode.HasRendererObservation(f.Target.Profile));
                Eq(0, (int)FamilyPolicyGetField(f.Mode, "rendererObservationTraceBusy"));
            }
        }

        private static string FamilyObservationLearnNewTarget(FamilyPolicyFixture fixture)
        {
            string replacement = Path.Combine(fixture.DirectoryPath, "client", "ActualRender.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(replacement));
            File.WriteAllBytes(replacement, new byte[] { 11, 12, 13, 14, 15 });
            GameProfile current = fixture.Current(fixture.First.Id);
            Eq(true, fixture.Mode.TryLearnConfirmedRenderer(new GameDetection
            {
                Profile = current,
                RendererPid = 80211,
                RendererCreation = 1337000000000042L,
                RendererPath = replacement,
                RendererName = Path.GetFileNameWithoutExtension(replacement),
                RendererCandidateSelected = true,
                RendererLearnable = true
            }));
            return replacement;
        }

        private sealed class FamilyObservationFixture : IDisposable
        {
            internal readonly FamilyPolicyFixture Library;
            internal readonly GameMode Mode;
            internal readonly GameDetection Target;
            internal readonly ManualResetEvent SampleStarted = new ManualResetEvent(false);
            internal readonly ManualResetEvent SampleReady = new ManualResetEvent(false);
            internal volatile bool IdentityValid = true;
            internal volatile int ForegroundPid;
            internal int Calls;
            internal long NowMs = 100000;
            internal double Utilization = 42;
            internal bool ReturnNull, ReturnMissing, ThrowSample;

            internal FamilyObservationFixture(string root, string name, bool force)
            {
                Library = new FamilyPolicyFixture(root, name);
                Mode = Library.Mode;
                if (force) Eq(true, Mode.SetProfileForceTrigger(Library.First.Id, true));
                Target = FamilyObservationTarget(Library.Current(Library.First.Id));
                ForegroundPid = Target.RendererPid;
                Library.EnableFakePolicyGate();
                FamilyPolicySetField(Mode, "activeDetection", RendererHandoffTracker.Copy(Target));
                Mode.RendererTestForeground = delegate { return ForegroundPid; };
                Mode.RendererTestObservationNow = delegate { return NowMs; };
                Mode.RendererTestIdentity = delegate(GameDetection value)
                { return IdentityValid && RendererHandoffTracker.SameIdentity(value, Target); };
                Mode.RendererTestGpu = delegate { throw new Exception("Unexpected handoff GPU sampler in badge fixture."); };
                Mode.RendererTestActivityGpu = delegate(GameDetection value, Func<bool> canceled)
                {
                    Interlocked.Increment(ref Calls);
                    SampleStarted.Set();
                    if (!SampleReady.WaitOne(3000)) throw new Exception("Fake activity sample was not released.");
                    if (ThrowSample) throw new InvalidOperationException("Synthetic GPU sample failure");
                    if (ReturnNull || canceled()) return null;
                    var samples = new Dictionary<int, double>();
                    if (!ReturnMissing) samples[value.RendererPid] = Utilization;
                    samples[int.MaxValue] = 99; // An unrelated process never confirms this target
                    return samples;
                };
            }

            internal void Observe() { FamilyPolicyInvoke(Mode, "MaybeObserveRendererActivity", Target); }
            internal void WaitForSample() { Eq(true, SampleStarted.WaitOne(3000)); }
            internal void FinishSample() { SampleReady.Set(); Drain(); }
            private void Drain()
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (((int)FamilyPolicyGetField(Mode, "rendererActivityBusy") != 0
                    || (int)FamilyPolicyGetField(Mode, "rendererObservationTraceBusy") != 0) && clock.ElapsedMilliseconds < 3000)
                    Thread.Sleep(1);
                Eq(0, (int)FamilyPolicyGetField(Mode, "rendererActivityBusy"));
                Eq(0, (int)FamilyPolicyGetField(Mode, "rendererObservationTraceBusy"));
            }
            public void Dispose()
            {
                FamilyPolicyInvoke(Mode, "InvalidateRendererHandoff");
                SampleReady.Set();
                Drain();
                SampleStarted.Dispose(); SampleReady.Dispose(); Library.Dispose();
            }
        }

        private static void FamilyProtectionOtherGameOptInDoesNotDisable(string root)
        {
            GameProfile a = FamilyProtectionProfile(root, "opted-in", true);
            GameProfile b = FamilyProtectionProfile(root, "default-off", false);
            ProcEntry host = FamilyProtectionProcess(100, 0, 100, Path.Combine(root, "host", "Hub.exe"));
            ProcEntry aRoot = FamilyProtectionProcess(200, host.Pid, 200, a.ExecutablePath);
            ProcEntry bRoot = FamilyProtectionProcess(300, host.Pid, 300, b.ExecutablePath);
            ProcEntry aChild = FamilyProtectionProcess(201, aRoot.Pid, 210, Path.Combine(root, "external-a", "Worker.exe"));
            ProcEntry bChild = FamilyProtectionProcess(301, bRoot.Pid, 310, Path.Combine(root, "external-b", "Worker.exe"));
            HashSet<int> protectedPids = FamilyProtectionCollect(new[] { a, b }, host, aRoot, bRoot, aChild, bChild);
            Eq(true, protectedPids.Contains(bRoot.Pid));
            Eq(true, protectedPids.Contains(bChild.Pid));
            Eq(false, protectedPids.Contains(aRoot.Pid));
            Eq(false, protectedPids.Contains(aChild.Pid));
        }

        private static void FamilyProtectionSharedAncestorDoesNotExpandSiblings(string root)
        {
            GameProfile b = FamilyProtectionProfile(root, "shared-ancestor", false);
            ProcEntry host = FamilyProtectionProcess(100, 0, 100, Path.Combine(root, "platform", "Hub.exe"));
            ProcEntry bRoot = FamilyProtectionProcess(200, host.Pid, 200, b.ExecutablePath);
            ProcEntry unrelated = FamilyProtectionProcess(300, host.Pid, 300, Path.Combine(root, "unrelated", "OtherApp.exe"));
            ProcEntry unrelatedChild = FamilyProtectionProcess(301, unrelated.Pid, 310, Path.Combine(root, "unrelated-worker", "Worker.exe"));
            HashSet<int> protectedPids = FamilyProtectionCollect(new[] { b }, host, bRoot, unrelated, unrelatedChild);
            Eq(true, protectedPids.Contains(host.Pid));
            Eq(true, protectedPids.Contains(bRoot.Pid));
            Eq(false, protectedPids.Contains(unrelated.Pid));
            Eq(false, protectedPids.Contains(unrelatedChild.Pid));
        }

        private static void FamilyProtectionCrossRootDescendant(string root)
        {
            GameProfile b = FamilyProtectionProfile(root, "cross-root", false);
            ProcEntry selected = FamilyProtectionProcess(200, 0, 200, b.ExecutablePath);
            ProcEntry child = FamilyProtectionProcess(201, selected.Pid, 210, Path.Combine(root, "external-helper", "Worker.exe"));
            ProcEntry grandchild = FamilyProtectionProcess(202, child.Pid, 220, Path.Combine(root, "another-helper", "Worker.exe"));
            HashSet<int> protectedPids = FamilyProtectionCollect(new[] { b }, selected, child, grandchild);
            Eq(true, protectedPids.Contains(selected.Pid));
            Eq(true, protectedPids.Contains(child.Pid));
            Eq(true, protectedPids.Contains(grandchild.Pid));
        }

        private static void FamilyProtectionRootSeedAfterLauncherExit(string root)
        {
            GameProfile b = FamilyProtectionProfile(root, "launcher-exited", false);
            ProcEntry rootSeed = FamilyProtectionProcess(200, 199, 200, Path.Combine(b.Root, "client", "Render.exe"));
            ProcEntry outside = FamilyProtectionProcess(201, rootSeed.Pid, 210, Path.Combine(root, "outside-after-exit", "Worker.exe"));
            HashSet<int> protectedPids = FamilyProtectionCollect(new[] { b }, rootSeed, outside);
            Eq(true, protectedPids.Contains(rootSeed.Pid));
            Eq(true, protectedPids.Contains(outside.Pid));
            Eq(false, protectedPids.Contains(199));
        }

        private static void FamilyProtectionRejectsInvalidCreationChain(string root)
        {
            GameProfile b = FamilyProtectionProfile(root, "creation-check", false);
            ProcEntry newerParent = FamilyProtectionProcess(200, 0, 300, b.ExecutablePath);
            ProcEntry oldChild = FamilyProtectionProcess(201, newerParent.Pid, 200, Path.Combine(root, "old-child", "Worker.exe"));
            ProcEntry unknownChild = FamilyProtectionProcess(202, newerParent.Pid, 0, Path.Combine(root, "unknown-child", "Worker.exe"));
            HashSet<int> protectedPids = FamilyProtectionCollect(new[] { b }, newerParent, oldChild, unknownChild);
            Eq(true, protectedPids.Contains(newerParent.Pid));
            Eq(false, protectedPids.Contains(oldChild.Pid));
            Eq(false, protectedPids.Contains(unknownChild.Pid));
            newerParent.Creation = 0;
            protectedPids = FamilyProtectionCollect(new[] { b }, newerParent, oldChild);
            Eq(false, protectedPids.Contains(newerParent.Pid));
            Eq(false, protectedPids.Contains(oldChild.Pid));
        }

        private static void FamilyProtectionRejectsOtherSession(string root)
        {
            GameProfile b = FamilyProtectionProfile(root, "session-check", false);
            ProcEntry otherSession = FamilyProtectionProcess(200, 0, 200, b.ExecutablePath);
            otherSession.Session = 8;
            ProcEntry localChild = FamilyProtectionProcess(201, otherSession.Pid, 210, Path.Combine(root, "local-child", "Worker.exe"));
            ProcEntry otherRoot = FamilyProtectionProcess(202, 0, 220, Path.Combine(b.Root, "another.exe"));
            otherRoot.Session = 8;
            HashSet<int> protectedPids = FamilyProtectionCollect(new[] { b }, otherSession, localChild, otherRoot);
            Eq(false, protectedPids.Contains(otherSession.Pid));
            Eq(false, protectedPids.Contains(localChild.Pid));
            Eq(false, protectedPids.Contains(otherRoot.Pid));
        }

        private static void FamilyProtectionRootlessExactAndLearned(string root)
        {
            GameProfile b = FamilyProtectionProfile(root, "rootless", false);
            b.Root = null;
            b.LearnedExecutablePath = Path.Combine(root, "learned", "PriorRender.exe");
            ProcEntry exact = FamilyProtectionProcess(200, 0, 200, b.ExecutablePath);
            ProcEntry learned = FamilyProtectionProcess(300, 0, 300, b.LearnedExecutablePath);
            ProcEntry child = FamilyProtectionProcess(301, learned.Pid, 310, Path.Combine(root, "learned-helper", "Worker.exe"));
            ProcEntry sameNameElsewhere = FamilyProtectionProcess(400, 0, 400, Path.Combine(root, "not-the-entry", Path.GetFileName(b.ExecutablePath)));
            HashSet<int> protectedPids = FamilyProtectionCollect(new[] { b }, exact, learned, child, sameNameElsewhere);
            Eq(true, protectedPids.Contains(exact.Pid));
            Eq(true, protectedPids.Contains(learned.Pid));
            Eq(true, protectedPids.Contains(child.Pid));
            Eq(false, protectedPids.Contains(sameNameElsewhere.Pid));
        }

        private static void FamilyProtectionDoesNotIncludeSelf(string root)
        {
            GameProfile b = FamilyProtectionProfile(root, "self-boundary", false);
            ProcEntry self = FamilyProtectionProcess(99000, 0, 100, Path.Combine(b.Root, "self.exe"));
            ProcEntry selected = FamilyProtectionProcess(200, self.Pid, 200, b.ExecutablePath);
            ProcEntry sibling = FamilyProtectionProcess(300, self.Pid, 300, Path.Combine(root, "self-other-child", "Worker.exe"));
            HashSet<int> protectedPids = FamilyProtectionCollect(new[] { b }, self, selected, sibling);
            Eq(false, protectedPids.Contains(self.Pid));
            Eq(true, protectedPids.Contains(selected.Pid));
            Eq(false, protectedPids.Contains(sibling.Pid));
        }

        private static void FamilyOverlayKnownHostsReleaseBackground(string root)
        {
            // These are just compatibility names, they don't imply a verified signature
            // Nor that some overlay is actually injected into any game right now
            string[] names =
            {
                "steam", "gameoverlayui", "steamwebhelper",
                "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment",
                "DiscordHookHelper", "DiscordHookHelper64",
                "NVIDIA Share", "NVIDIA Overlay", "nvsphelper", "nvsphelper64",
                "RTSS", "RTSSHooksLoader", "RTSSHooksLoader64",
                "MSIAfterburner", "EncoderServer", "EncoderServer64",
                "obs32", "obs64", "obs-browser-page", "obs-ffmpeg-mux",
                "Overwolf", "OverwolfBrowser", "OverwolfHelper", "OverwolfHelper64",
                "NahimicService", "NahimicSvc32", "NahimicSvc64", "fraps"
            };
            using (var f = new FamilyOverlayFixture(root, "overlay-known-hosts"))
            {
                for (int i = 0; i < names.Length; i++)
                {
                    int pid = 75100 + i;
                    long creation = 132500000001234500L + i;
                    string path = @"C:\OverlayHostFixture\" + names[i] + ".exe";
                    f.Seed(pid, creation, names[i], SuppressReason.Background);
                    Eq(true, f.Protect(pid, creation, names[i].ToUpperInvariant() + ".EXE", path));
                    Eq(i + 1, f.RestoreRequests.Count);
                    ProcEntry request = f.RestoreRequests[i];
                    Eq(pid, request.Pid);
                    Eq(creation, request.Creation);
                    Eq(names[i], request.Name);
                    Eq(null, f.Peek(pid));
                    f.AssertTracked(pid, false);
                    // A second snapshot from the same host must not restore twice
                    Eq(true, f.Protect(pid, creation, names[i], path));
                    Eq(i + 1, f.RestoreRequests.Count);
                }
            }
        }

        private static void FamilyOverlayPortablePathsAndNormalization(string root)
        {
            string[,] inputs =
            {
                { "discord", @"D:\Portable Tools\discord.exe" },
                { "  obs64.EXE  ", @"E:\Capture Tools\bin\OBS64.ExE" },
                { "RTSS", "C:/Portable/RTSS.exe" },
                { "NVIDIA Overlay.exe", "\"C:\\Portable\\NVIDIA Overlay.exe\"" },
                { "fraps", @"\\server\share\portable\fraps.exe" },
                { "steamwebhelper", @"\\?\C:\Portable\Steam\steamwebhelper.exe" },
                { "obs32", @"\??\D:\Apps\OBS\obs32.exe" },
                { "Overwolf", @"\\?\UNC\server\share\Overwolf.exe" },
                { "discordcanary", @"\??\UNC\server\share\DiscordCanary.exe" }
            };
            using (var f = new FamilyOverlayFixture(root, "overlay-portable"))
            {
                for (int i = 0; i < inputs.GetLength(0); i++)
                    Eq(true, f.Protect(75200 + i, 100 + i, inputs[i, 0], inputs[i, 1]));
                // Paths are synthetic strings, product files and install roots need not exist
                // An untracked host needs no restore action either
                Eq(0, f.RestoreRequests.Count);
                Eq(0, f.TrackedCount);
            }
        }

        private static void FamilyOverlayRejectsInvalidIdentity(string root)
        {
            const string goodPath = @"C:\Portable\discord.exe";
            string[,] inputs =
            {
                { null, goodPath }, { "", goodPath }, { "discord", null },
                { "discord", "" }, { "discord", "   " },
                { "discord", "discord.exe" }, { "discord", @".\discord.exe" },
                { "discord", @"C:discord.exe" }, { "discord", @"\Portable\discord.exe" },
                { "discord", @"\\.\C:\Portable\discord.exe" },
                { "discord", @"\\?\GLOBALROOT\Device\HarddiskVolume1\discord.exe" },
                { "obs64", @"\\server\obs64.exe" },
                { "discord", @"C:\Portable\discord.dll" },
                { "discord", @"C:\Portable\discord.exe.exe" },
                { "discord", @"C:\Portable\discord.exe:payload" },
                { "discord", goodPath + "\0" },
                { "discord", @"C:\Portable\discord.exe\" },
                { "discord", @"C:\Portable\obs64.exe" },
                { "obs64", goodPath },
                { "DiscordHookHelper64-old", @"C:\Portable\DiscordHookHelper64-old.exe" },
                { "steamwebhelper-child", @"C:\Portable\steamwebhelper-child.exe" }
            };
            using (var f = new FamilyOverlayFixture(root, "overlay-invalid"))
            {
                object entry = f.Seed(75300, 100, "discord", SuppressReason.Background);
                for (int i = 0; i < inputs.GetLength(0); i++)
                {
                    Eq(false, f.Protect(75300, 100, inputs[i, 0], inputs[i, 1]));
                    Eq(entry, f.Peek(75300));
                    Eq(true, f.Core.HasReason(75300, SuppressReason.Background));
                    f.AssertTracked(75300, true);
                }
                foreach (int pid in new[] { -1, 0, 4 })
                    Eq(false, f.Protect(pid, 100, "discord", goodPath));
                Eq(0, f.RestoreRequests.Count);
            }
        }

        private static void FamilyOverlayDoesNotExemptSiblingPrograms(string root)
        {
            using (var f = new FamilyOverlayFixture(root, "overlay-sibling-boundary"))
            {
                f.Seed(75400, 100, "obs64", SuppressReason.Background);
                Eq(true, f.Protect(75400, 100, "obs64", @"C:\Portable\OBS\bin\obs64.exe"));
                string[] names = { "chrome", "update", "updater", "browser", "OtherRecorder", "obs64-helper" };
                for (int i = 0; i < names.Length; i++)
                {
                    int pid = 75401 + i;
                    string path = @"C:\Portable\OBS\bin\" + names[i] + ".exe";
                    object entry = f.Seed(pid, 110 + i, names[i], SuppressReason.Background);
                    Eq(false, f.Protect(pid, 110 + i, names[i], path));
                    Eq(entry, f.Peek(pid));
                    Eq(true, f.Core.HasReason(pid, SuppressReason.Background));
                    f.AssertTracked(pid, true);
                }
                // Exemption does not propagate from the product root down to nested worker processes
                Eq(false, f.Protect(75450, 200, "updater", @"C:\Portable\OBS\bin\helpers\updater.exe"));
                Eq(1, f.RestoreRequests.Count);
            }
        }

        private static void FamilyOverlayPreservesOtherReasons(string root)
        {
            using (var f = new FamilyOverlayFixture(root, "overlay-other-reason"))
            {
                const int pid = 75500;
                object entry = f.Seed(pid, 100, "Discord",
                    SuppressReason.Background | SuppressReason.AntiCheat);
                Eq(true, f.Protect(pid, 100, "Discord", @"C:\Portable\Discord.exe"));
                Eq(entry, f.Peek(pid));
                Eq(false, f.Core.HasReason(pid, SuppressReason.Background));
                Eq(true, f.Core.HasReason(pid, SuppressReason.AntiCheat));
                Eq(SuppressionLevel.None, f.Core.LevelOf(pid, SuppressReason.Background));
                Eq(SuppressionLevel.Isolated, f.Core.LevelOf(pid, SuppressReason.AntiCheat));
                Eq(SuppressionLevel.Isolated, RendererReleaseField<SuppressionLevel>(entry, "Level"));
                f.AssertTracked(pid, false);
                Eq(true, f.Protect(pid, 100, "Discord", @"C:\Portable\Discord.exe"));

                object onlyOther = f.Seed(pid + 1, 110, "obs64", SuppressReason.AntiCheat);
                Eq(true, f.Protect(pid + 1, 110, "obs64", @"D:\Portable\obs64.exe"));
                Eq(onlyOther, f.Peek(pid + 1));
                Eq(true, f.Core.HasReason(pid + 1, SuppressReason.AntiCheat));
                f.AssertTracked(pid + 1, true);
                Eq(0, f.RestoreRequests.Count);
            }
        }

        private static void FamilyOverlayUnknownCreationDoesNotRestore(string root)
        {
            using (var f = new FamilyOverlayFixture(root, "overlay-unknown-creation"))
            {
                object entry = f.Seed(75600, 100, "steamwebhelper", SuppressReason.Background);
                foreach (long creation in new[] { 0L, -1L })
                {
                    Eq(true, f.Protect(75600, creation, "steamwebhelper", @"C:\Portable\steamwebhelper.exe"));
                    Eq(entry, f.Peek(75600));
                    Eq(true, f.Core.HasReason(75600, SuppressReason.Background));
                    f.AssertTracked(75600, true);
                }
                object unknownOwner = f.Seed(75601, 0, "obs64", SuppressReason.Background);
                Eq(true, f.Protect(75601, 100, "obs64", @"D:\Portable\obs64.exe"));
                Eq(unknownOwner, f.Peek(75601));
                Eq(true, f.Core.HasReason(75601, SuppressReason.Background));
                f.AssertTracked(75601, true);
                Eq(0, f.RestoreRequests.Count);
            }
        }

        private static void FamilyOverlayPidReuseDoesNotRestoreNewOwner(string root)
        {
            using (var f = new FamilyOverlayFixture(root, "overlay-pid-reuse"))
            {
                const int pid = 75700;
                object old = f.Seed(pid, 100, "obs64", SuppressReason.Background);
                Eq(true, f.Protect(pid, 200, "obs64", @"C:\Portable\obs64.exe"));
                Eq(old, f.Peek(pid));
                Eq(true, f.Core.HasReason(pid, SuppressReason.Background));
                f.AssertTracked(pid, true);
                Eq(false, f.Protect(pid, 200, "updater", @"C:\Portable\updater.exe"));
                Eq(0, f.RestoreRequests.Count);

                // Simulate a snapshot arriving after the core swapped out the old identity
                // A late snapshot must not release the new holder
                object current = f.Seed(pid, 200, "Discord", SuppressReason.Background);
                Eq(true, f.Protect(pid, 100, "obs64", @"C:\Portable\obs64.exe"));
                Eq(current, f.Peek(pid));
                Eq(0, f.RestoreRequests.Count);
                f.AssertTracked(pid, true);
                Eq(true, f.Protect(pid, 200, "Discord", @"D:\Portable\Discord.exe"));
                Eq(1, f.RestoreRequests.Count);
                Eq(200L, f.RestoreRequests[0].Creation);
                Eq("Discord", f.RestoreRequests[0].Name);
                Eq(null, f.Peek(pid));
                f.AssertTracked(pid, false);
            }
        }

        private static void FamilyOverlayUntrackedAndLateHosts(string root)
        {
            using (var f = new FamilyOverlayFixture(root, "overlay-per-snapshot"))
            {
                Eq(true, f.Protect(75800, 100, "gameoverlayui", @"C:\Steam\gameoverlayui.exe"));
                Eq(0, f.RestoreRequests.Count);
                // One arriving after a directory change is recognized immediately too
                // No once-per-match scan result here, and no install-root cache
                f.Seed(75801, 200, "obs-browser-page", SuppressReason.Background);
                Eq(true, f.Protect(75801, 200, "obs-browser-page", @"D:\Capture\obs-browser-page.exe"));
                Eq(1, f.RestoreRequests.Count);
                f.Seed(75802, 300, "OverwolfHelper64", SuppressReason.Background);
                Eq(true, f.Protect(75802, 300, "OverwolfHelper64", @"E:\OtherGameTools\OverwolfHelper64.exe"));
                Eq(2, f.RestoreRequests.Count);
                // An ordinary program picked up a former host's PID
                // It inherits neither the earlier recognition result nor the product-directory exemption
                object entry = f.Seed(75801, 400, "browser", SuppressReason.Background);
                Eq(false, f.Protect(75801, 400, "browser", @"D:\Capture\browser.exe"));
                Eq(entry, f.Peek(75801));
                Eq(true, f.Core.HasReason(75801, SuppressReason.Background));
                f.AssertTracked(75801, true);
                Eq(2, f.RestoreRequests.Count);
            }
        }

        private static void FamilyOverlayFailedRestoreKeepsRecoveryDebt(string root)
        {
            using (var f = new FamilyOverlayFixture(root, "overlay-pending-restore"))
            {
                const int pid = 75900;
                f.Result = SuppressionCore.RestoreResult.Protected;
                object entry = f.Seed(pid, 100, "Discord", SuppressReason.Background);
                Eq(true, f.Protect(pid, 100, "Discord", @"C:\Portable\Discord.exe"));
                Eq(1, f.RestoreRequests.Count);
                Eq(entry, f.Peek(pid));
                Eq(false, f.Core.HasReason(pid, SuppressReason.Background));
                Eq(SuppressReason.None, RendererReleaseField<SuppressReason>(entry, "Reasons"));
                f.AssertTracked(pid, false);
                // While the restore is still pending, the recognition result stays true
                // The caller must keep skipping Acquire and Reconcile rather than suppressing it again
                Eq(true, f.Protect(pid, 100, "Discord", @"C:\Portable\Discord.exe"));
                Eq(1, f.RestoreRequests.Count);
                Eq(entry, f.Peek(pid));
                f.Result = SuppressionCore.RestoreResult.Restored;
                RendererReleaseSet(entry, "NextRetryTicks", 0L);
                f.Core.RetryPending(); // The restore delegate remains entirely fake
                Eq(2, f.RestoreRequests.Count);
                Eq(null, f.Peek(pid));

                f.Result = SuppressionCore.RestoreResult.Gone;
                f.Seed(pid + 1, 200, "obs64", SuppressReason.Background);
                Eq(true, f.Protect(pid + 1, 200, "obs64", @"D:\Portable\obs64.exe"));
                Eq(3, f.RestoreRequests.Count);
                Eq(null, f.Peek(pid + 1));
                f.AssertTracked(pid + 1, false);
            }
        }

        private static void FamilyOverlaySteamPolicyFollowsSavedChoice(string root)
        {
            using (var f = new FamilyOverlayPolicyFixture(root, "overlay-policy-cycle"))
            {
                Eq(true, f.FamilyExempt);
                HashSet<int> family = f.ProtectedLibraryPids();
                Eq(true, family.Contains(f.Steam.Pid));
                // These are siblings of the renderer process, not its descendants
                // Their default protection must not come from walking up the ancestors
                Eq(false, family.Contains(f.WebHelper.Pid));
                Eq(false, family.Contains(f.GameOverlay.Pid));
                foreach (ProcEntry process in f.SteamHosts)
                    Eq(false, f.CanSuppress(process));

                // What another entry selected must not become the value this game is using
                Eq(true, f.Mode.SetProfileFamilySuppression(f.Overlay.Second.Id, true));
                Eq(true, f.FamilyExempt);
                foreach (ProcEntry process in f.SteamHosts)
                    Eq(false, f.CanSuppress(process));

                f.SetSuppression(true);
                family = f.ProtectedLibraryPids();
                foreach (ProcEntry process in f.SteamHosts)
                {
                    Eq(false, family.Contains(process.Pid));
                    Eq(false, f.EarlyProtected(process));
                    // Swap in the old unconditional overlay gate and this assertion fails
                    // Even though the family switch actually stored is on
                    Eq(true, f.CanSuppress(process));
                }
                f.SetSuppression(false);
                foreach (ProcEntry process in f.SteamHosts)
                    Eq(false, f.CanSuppress(process));
                Eq(0, f.Overlay.RestoreRequests.Count);
            }
            using (var f = new FamilyOverlayPolicyFixture(root, "overlay-policy-save-failed"))
            {
                string file = Path.Combine(f.Overlay.DirectoryPath, GameProfileStore.FileName);
                string before = File.ReadAllText(file);
                // The existing transactional store fixture rejects replacement only for this isolated temp library
                // It never touches real settings
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Eq(false, f.Mode.SetProfileFamilySuppression(f.Overlay.First.Id, true));
                Eq(false, f.Mode.ProfileStoreSaveFailed);
                Eq(false, f.Overlay.First.SuppressFamilyBackground);
                Eq(true, f.FamilyExempt);
                Eq(before, File.ReadAllText(file));
                foreach (ProcEntry process in f.SteamHosts)
                    Eq(false, f.CanSuppress(process));
                Eq(0, f.Overlay.RestoreRequests.Count);
                f.SetSuppression(true);
                Eq(false, f.FamilyExempt);
            }
        }

        private static void FamilyOverlayOptInKeepsOwnedSuppression(string root)
        {
            using (var f = new FamilyOverlayPolicyFixture(root, "overlay-policy-owned"))
            {
                f.SetSuppression(true);
                foreach (ProcEntry process in f.SteamHosts)
                {
                    object entry = f.Overlay.Seed(process.Pid, process.Creation, process.Name,
                        SuppressReason.Background);
                    Eq(true, f.CanSuppress(process));
                    Eq(entry, f.Overlay.Peek(process.Pid));
                    Eq(true, f.Overlay.Core.HasReason(process.Pid, SuppressReason.Background));
                    f.Overlay.AssertTracked(process.Pid, true);
                }
                Eq(0, f.Overlay.RestoreRequests.Count);

                f.SetSuppression(false);
                int restored = 0;
                foreach (ProcEntry process in f.SteamHosts)
                {
                    // Run the helper that can actually restore with the newly saved exit option
                    // Rather than fabricating a restore-succeeded flag ourselves
                    Eq(true, f.Overlay.Protect(process.Pid, process.Creation,
                        process.Name, process.Path, f.FamilyExempt));
                    Eq(++restored, f.Overlay.RestoreRequests.Count);
                    Eq(null, f.Overlay.Peek(process.Pid));
                    f.Overlay.AssertTracked(process.Pid, false);
                    Eq(false, f.CanSuppress(process));
                }
            }
        }

        private static void FamilyOverlayOptInKeepsIndependentCapture(string root)
        {
            using (var f = new FamilyOverlayPolicyFixture(root, "overlay-policy-capture"))
            {
                f.SetSuppression(true);
                foreach (ProcEntry process in new[] { f.Obs, f.Discord })
                {
                    Eq(false, f.ProtectedLibraryPids().Contains(process.Pid));
                    Eq(false, f.EarlyProtected(process));
                    f.Overlay.Seed(process.Pid, process.Creation, process.Name, SuppressReason.Background);
                    Eq(false, f.CanSuppress(process));
                    Eq(null, f.Overlay.Peek(process.Pid));
                    f.Overlay.AssertTracked(process.Pid, false);
                }
                Eq(2, f.Overlay.RestoreRequests.Count);
                f.SetSuppression(false);
                Eq(false, f.CanSuppress(f.Obs));
                Eq(false, f.CanSuppress(f.Discord));
                Eq(2, f.Overlay.RestoreRequests.Count);
            }
        }

        private static void FamilyOverlayOptInKeepsRendererAndWhitelist(string root)
        {
            using (var f = new FamilyOverlayPolicyFixture(root, "overlay-policy-strong-protection"))
            {
                f.SetSuppression(true);
                Eq(false, f.ProtectedLibraryPids().Contains(f.Renderer.Pid));
                Eq(false, f.WhitelistProtected().Contains(f.Renderer.Pid));
                Eq(true, f.EarlyProtected(f.Renderer));
                Eq(false, f.CanSuppress(f.Renderer));

                f.AddExactWhitelist(f.WebHelper.Path);
                Eq(true, f.WhitelistProtected().Contains(f.WebHelper.Pid));
                Eq(false, f.WhitelistProtected().Contains(f.Steam.Pid));
                Eq(true, f.EarlyProtected(f.WebHelper));
                Eq(false, f.CanSuppress(f.WebHelper));
                Eq(true, f.CanSuppress(f.Steam));
                Eq(true, f.CanSuppress(f.GameOverlay));

                // The extracted pre-gate keeps the original nullable family set semantics
                // And keeps the unconditional renderer process boundary
                var member = new HashSet<int> { f.WebHelper.Pid };
                Eq(true, FamilyBoundary.IsGameOrWhitelistProtected(f.WebHelper.Pid,
                    f.Renderer.Pid, false, false, true, member, null));
                Eq(true, FamilyBoundary.IsGameOrWhitelistProtected(f.WebHelper.Pid,
                    f.Renderer.Pid, false, false, true, null, member));
                Eq(false, FamilyBoundary.IsGameOrWhitelistProtected(f.WebHelper.Pid,
                    f.Renderer.Pid, false, false, false, member, member));
                Eq(0, f.Overlay.RestoreRequests.Count);
            }
        }

        private static void FamilyOverlayOptInKeepsOtherGameAncestor(string root)
        {
            using (var f = new FamilyOverlayPolicyFixture(root, "overlay-policy-shared-steam"))
            {
                f.SetSuppression(true);
                ProcEntry otherRenderer = f.Process(76106, f.Steam.Pid, 170,
                    f.Overlay.Second.ExecutablePath);
                f.Processes.Add(otherRenderer);
                HashSet<int> family = f.ProtectedLibraryPids();
                Eq(true, family.Contains(otherRenderer.Pid));
                Eq(true, family.Contains(f.Steam.Pid));
                Eq(false, family.Contains(f.Renderer.Pid));
                Eq(false, family.Contains(f.WebHelper.Pid));
                Eq(false, family.Contains(f.GameOverlay.Pid));
                Eq(true, f.EarlyProtected(f.Steam));
                Eq(true, f.EarlyProtected(otherRenderer));
                Eq(false, f.CanSuppress(f.Steam));
                Eq(false, f.CanSuppress(otherRenderer));
                Eq(false, f.CanSuppress(f.Renderer));
                // Protecting B's confirmed ancestor must not incidentally manufacture evidence that every Steam helper sibling belongs to B too
                Eq(true, f.CanSuppress(f.WebHelper));
                Eq(true, f.CanSuppress(f.GameOverlay));
                Eq(false, f.CanSuppress(f.Obs));
                Eq(0, f.Overlay.RestoreRequests.Count);
            }
        }

        private static void FamilyOverlayOptInRespectsForegroundRules(string root)
        {
            using (var f = new FamilyOverlayPolicyFixture(root, "overlay-policy-foreground"))
            {
                f.SetSuppression(true);
                foreach (ProcEntry process in f.SteamHosts)
                {
                    Eq(true, f.CanSuppress(process));
                    Eq(false, f.CanSuppress(process, process.Pid, false, false));
                    Eq(false, f.CanSuppress(process, f.Renderer.Pid, true, false));
                    Eq(true, f.CanSuppress(process, process.Pid, true, true));
                }
                Eq(false, f.CanSuppress(f.Renderer, f.Renderer.Pid, true, true));
                Eq(0, f.Overlay.RestoreRequests.Count);
            }
        }

        private static void FamilyVisibilityLateLobbyUsesCurrentSnapshot(string root)
        {
            using (var f = new FamilyOverlayPolicyFixture(root, "visible-late-lobby"))
            {
                f.SetSuppression(true);
                HashSet<int> cachedFamily = f.CaptureDetectedFamily();
                Eq(false, cachedFamily.Contains(f.Steam.Pid));
                ProcEntry[] branch = FamilyVisibilityAddLobby(f);
                Eq(false, cachedFamily.Contains(branch[0].Pid));
                Eq(true, f.Overlay.First.ContainsPath(branch[0].Path));
                Eq(false, f.Overlay.First.ContainsPath(branch[1].Path));
                HashSet<int> visible = f.FilteredVisibleFamily(
                    new HashSet<int> { branch[0].Pid }, cachedFamily, null);
                foreach (ProcEntry process in branch)
                {
                    // Members of the current root and its verified cross-root children
                    // Should not wait on the older renderer family to refresh
                    Eq(false, visible.Contains(process.Pid));
                    Eq(true, f.CanSuppress(process, f.Renderer.Pid, visible.Contains(process.Pid), false));
                }
                Eq(false, f.CanSuppress(f.Renderer, f.Renderer.Pid,
                    visible.Contains(f.Renderer.Pid), false));
                Eq(0, f.Overlay.RestoreRequests.Count);
            }
        }

        private static void FamilyVisibilitySameNameKeepsUnownedIdentities(string root)
        {
            using (var f = new FamilyOverlayPolicyFixture(root, "visible-same-name"))
            {
                f.SetSuppression(true);
                HashSet<int> cachedFamily = f.CaptureDetectedFamily();
                ProcEntry[] branch = FamilyVisibilityAddLobby(f);
                ProcEntry otherGame = f.Process(76202, f.Steam.Pid, 230,
                    Path.Combine(f.Overlay.Second.Root, "LeagueClientUx.exe"));
                ProcEntry unrelated = f.Process(76203, f.Steam.Pid, 240,
                    Path.Combine(f.Overlay.DirectoryPath, "Unrelated", "LeagueClientUx.exe"));
                ProcEntry olderChild = f.Process(76204, branch[0].Pid, 190,
                    Path.Combine(f.Overlay.DirectoryPath, "Unrelated", "BrowserOlderThanParent.exe"));
                ProcEntry otherSession = f.Process(76205, branch[0].Pid, 250,
                    Path.Combine(f.Overlay.First.Root, "OtherSession", "LeagueClientUx.exe"));
                otherSession.Session = 8;
                ProcEntry ambiguousOwned = f.Process(76206, f.Steam.Pid, 260,
                    Path.Combine(f.Overlay.First.Root, "Duplicate", "LeagueClientUx.exe"));
                ProcEntry ambiguousOther = f.Process(76206, f.Steam.Pid, 270,
                    Path.Combine(f.Overlay.DirectoryPath, "Unrelated", "LeagueClientUx.exe"));
                ProcEntry unknownCreation = f.Process(76207, f.Steam.Pid, 0,
                    Path.Combine(f.Overlay.First.Root, "UnknownCreation", "LeagueClientUx.exe"));
                ProcEntry self = f.Process(99000, f.Steam.Pid, 280,
                    Path.Combine(f.Overlay.First.Root, "Self", "LeagueClientUx.exe"));
                ProcEntry ambiguousChild = f.Process(76208, ambiguousOwned.Pid, 290,
                    Path.Combine(f.Overlay.DirectoryPath, "Unrelated", "AmbiguousParentChild.exe"));
                f.Processes.AddRange(new[] { otherGame, unrelated, olderChild,
                    otherSession, ambiguousOwned, ambiguousOther, unknownCreation, self, ambiguousChild });
                // In this scenario neither real game UI has a window
                // The existing same-name extension reaches it from outside the window
                HashSet<int> visible = f.FilteredVisibleFamily(
                    new HashSet<int> { otherGame.Pid, unrelated.Pid }, cachedFamily, null);
                Eq(false, visible.Contains(branch[0].Pid));
                Eq(false, visible.Contains(branch[1].Pid));
                foreach (ProcEntry process in new[] { otherGame, unrelated, olderChild, otherSession,
                    ambiguousOther, unknownCreation, self, ambiguousChild })
                    Eq(true, visible.Contains(process.Pid));
                Eq(true, f.CanSuppress(branch[0], f.Renderer.Pid, visible.Contains(branch[0].Pid), false));
                Eq(false, f.CanSuppress(otherGame, f.Renderer.Pid, visible.Contains(otherGame.Pid), false));
                Eq(false, f.CanSuppress(unrelated, f.Renderer.Pid, visible.Contains(unrelated.Pid), false));
                Eq(false, f.CanSuppress(otherSession, f.Renderer.Pid, visible.Contains(otherSession.Pid), false));
                Eq(0, f.Overlay.RestoreRequests.Count);
            }
        }

        private static void FamilyVisibilityHistoryChecksCurrentIdentity(string root)
        {
            using (var f = new FamilyOverlayPolicyFixture(root, "visible-history-identity"))
            {
                f.SetSuppression(true);
                HashSet<int> cachedFamily = f.CaptureDetectedFamily();
                ProcEntry broker = f.Process(76300, f.Steam.Pid, 170,
                    Path.Combine(f.Overlay.First.Root, "ShortLivedBroker.exe"));
                ProcEntry ui = f.Process(76301, broker.Pid, 180,
                    Path.Combine(f.Overlay.DirectoryPath, "OutsideLobby", "LobbyWebView.exe"));
                ProcEntry child = f.Process(76302, ui.Pid, 190,
                    Path.Combine(f.Overlay.DirectoryPath, "OutsideLobby", "LobbyWorker.exe"));
                var history = new GameFamilyHistory();
                var before = new List<ProcEntry>(f.Processes);
                before.AddRange(new[] { broker, ui, child });
                history.Capture(new ProcessSnapshot(before.ToArray()), f.Mode.GetProfiles(), 7, 1000);
                // The owning broker has exited, only history can prove this in-use UI belongs to that profile
                // The cached family and renderer ancestry can't prove it
                f.Processes.AddRange(new[] { ui, child });
                GameFamilyEvidence evidence = history.Capture(new ProcessSnapshot(f.Processes.ToArray()),
                    f.Mode.GetProfiles(), 7, 2000);
                Eq(true, evidence.Contains(f.Overlay.First, ui.Pid, ui.Creation, ui.Path));
                HashSet<int> visible = f.FilteredVisibleFamily(new HashSet<int> { ui.Pid }, cachedFamily, evidence);
                Eq(false, visible.Contains(ui.Pid));
                Eq(false, visible.Contains(child.Pid));
                Eq(true, f.CanSuppress(ui, f.Renderer.Pid, visible.Contains(ui.Pid), false));

                ui.Creation += 1000; // A reused PID is not the old owner kept in history
                Eq(false, evidence.Contains(f.Overlay.First, ui.Pid, ui.Creation, ui.Path));
                visible = f.FilteredVisibleFamily(new HashSet<int> { ui.Pid }, cachedFamily, evidence);
                Eq(true, visible.Contains(ui.Pid));
                Eq(false, f.CanSuppress(ui, f.Renderer.Pid, visible.Contains(ui.Pid), false));
                Eq(0, f.Overlay.RestoreRequests.Count);
            }
        }

        private static void FamilyVisibilityPreservesDefaultAndWhitelist(string root)
        {
            foreach (WhitelistRuleKind kind in new[] { WhitelistRuleKind.ExactPath,
                WhitelistRuleKind.LegacyName, WhitelistRuleKind.ApplicationFamily })
            {
                using (var f = new FamilyOverlayPolicyFixture(root, "visible-white-" + kind))
                {
                    HashSet<int> cachedFamily = f.CaptureDetectedFamily();
                    ProcEntry[] branch = FamilyVisibilityAddLobby(f);
                    var visibleRoots = new HashSet<int> { branch[0].Pid };
                    HashSet<int> visible = f.FilteredVisibleFamily(visibleRoots, cachedFamily, null);
                    Eq(true, visible.Contains(branch[0].Pid));
                    Eq(true, visible.Contains(branch[1].Pid));
                    Eq(false, f.CanSuppress(branch[0], f.Renderer.Pid, true, false));

                    f.SetSuppression(true);
                    f.AddWhitelist(kind, kind == WhitelistRuleKind.LegacyName ? branch[0].Name : branch[0].Path);
                    visible = f.FilteredVisibleFamily(visibleRoots, cachedFamily, null);
                    Eq(false, visible.Contains(branch[0].Pid));
                    Eq(false, visible.Contains(branch[1].Pid));
                    HashSet<int> white = f.WhitelistProtected();
                    Eq(true, white.Contains(branch[0].Pid));
                    Eq(kind == WhitelistRuleKind.ApplicationFamily, white.Contains(branch[1].Pid));
                    Eq(false, f.CanSuppress(branch[0], f.Renderer.Pid, false, false));
                    Eq(kind != WhitelistRuleKind.ApplicationFamily,
                        f.CanSuppress(branch[1], f.Renderer.Pid, false, false));
                    f.SetSuppression(false);
                    visible = f.FilteredVisibleFamily(visibleRoots, cachedFamily, null);
                    Eq(true, visible.Contains(branch[0].Pid));
                    Eq(true, visible.Contains(branch[1].Pid));
                    Eq(0, f.Overlay.RestoreRequests.Count);
                }
            }
        }

        private static void FamilyVisibilityPreservesOtherGameAndForeground(string root)
        {
            using (var f = new FamilyOverlayPolicyFixture(root, "visible-other-game"))
            {
                f.SetSuppression(true);
                HashSet<int> cachedFamily = f.CaptureDetectedFamily();
                ProcEntry[] branch = FamilyVisibilityAddLobby(f);
                ProcEntry otherRenderer = f.Process(76400, f.Steam.Pid, 230, f.Overlay.Second.ExecutablePath);
                f.Processes.Add(otherRenderer);
                HashSet<int> visible = f.FilteredVisibleFamily(new HashSet<int>
                    { f.Steam.Pid, branch[0].Pid, otherRenderer.Pid, f.Obs.Pid }, cachedFamily, null);
                Eq(false, visible.Contains(branch[0].Pid));
                Eq(false, visible.Contains(branch[1].Pid));
                Eq(true, visible.Contains(otherRenderer.Pid));
                Eq(true, visible.Contains(f.Obs.Pid));
                Eq(true, f.ProtectedLibraryPids().Contains(f.Steam.Pid));
                Eq(false, f.CanSuppress(f.Steam, f.Renderer.Pid, visible.Contains(f.Steam.Pid), false));
                Eq(false, f.CanSuppress(otherRenderer, f.Renderer.Pid, true, false));
                Eq(false, f.CanSuppress(f.Obs, f.Renderer.Pid, true, false));
                Eq(true, f.CanSuppress(branch[0], f.Renderer.Pid, false, false));
                Eq(false, f.CanSuppress(branch[0], branch[0].Pid, false, false));
                Eq(false, f.CanSuppress(f.Renderer, f.Renderer.Pid, false, false));

                // The aggressive path hands over a shared empty set
                // The pure visibility filter must keep it empty and make zero native calls
                var empty = new HashSet<int>();
                FamilyBoundary.FilterUserFacingGameFamily(empty, f.Overlay.First,
                    new ProcessSnapshot(f.Processes.ToArray()), f.Renderer.Pid, 99000, 7,
                    cachedFamily, null, null, null);
                Eq(0, empty.Count);
                Eq(0, f.Overlay.RestoreRequests.Count);
            }
        }

        private static ProcEntry[] FamilyVisibilityAddLobby(FamilyOverlayPolicyFixture f)
        {
            ProcEntry ui = f.Process(76200, f.Steam.Pid, 200,
                Path.Combine(f.Overlay.First.Root, "lobby", "LeagueClientUx.exe"));
            ProcEntry child = f.Process(76201, ui.Pid, 210,
                Path.Combine(f.Overlay.DirectoryPath, "OutsideBrowser", "LeagueClientUxRender.exe"));
            f.Processes.AddRange(new[] { ui, child });
            return new[] { ui, child };
        }

        // Runs the real pure and restorable decisions in Sweep order, stopping before Reconcile and Acquire
        // Identity and window flags are all supplied here, no foreground query, no process enumeration
        private sealed class FamilyOverlayPolicyFixture : IDisposable
        {
            internal readonly FamilyOverlayFixture Overlay;
            internal readonly ProcEntry Steam, WebHelper, GameOverlay, Renderer, Obs, Discord;
            internal readonly List<ProcEntry> Processes = new List<ProcEntry>();
            private readonly int selfPid, ownerSession;
            internal GameMode Mode { get { return Overlay.Mode; } }
            internal bool FamilyExempt { get { return FamilyBoundary.FamilyExemptFor(Overlay.First); } }
            internal ProcEntry[] SteamHosts { get { return new[] { Steam, WebHelper, GameOverlay }; } }

            internal FamilyOverlayPolicyFixture(string root, string name)
            {
                Overlay = new FamilyOverlayFixture(root, name, true);
                // Keep the ownership check away from the test process's real PID and desktop session
                // These fields belong to this fixture only
                selfPid = 99000;
                ownerSession = 7;
                FamilyPolicySetField(Mode, "selfPid", selfPid);
                FamilyPolicySetField(Mode, "selfSession", ownerSession);
                string steamRoot = Path.Combine(Overlay.DirectoryPath, "SteamClient");
                Steam = Process(76100, 0, 100, Path.Combine(steamRoot, "steam.exe"));
                WebHelper = Process(76101, Steam.Pid, 120, Path.Combine(steamRoot, "bin", "steamwebhelper.exe"));
                GameOverlay = Process(76102, Steam.Pid, 130, Path.Combine(steamRoot, "gameoverlayui.exe"));
                Renderer = Process(76103, Steam.Pid, 140, Overlay.First.ExecutablePath);
                Obs = Process(76104, 0, 150, Path.Combine(Overlay.DirectoryPath, "Capture", "obs64.exe"));
                Discord = Process(76105, 0, 160, Path.Combine(Overlay.DirectoryPath, "Chat", "Discord.exe"));
                Processes.AddRange(new[] { Steam, WebHelper, GameOverlay, Renderer, Obs, Discord });
            }

            internal ProcEntry Process(int pid, int parent, long creation, string path)
            {
                return new ProcEntry { Pid = pid, ParentPid = parent, Creation = creation,
                    Session = ownerSession, Name = Path.GetFileNameWithoutExtension(path), Path = path };
            }

            internal void SetSuppression(bool value)
            {
                Eq(true, Mode.SetProfileFamilySuppression(Overlay.First.Id, value));
                Eq(value, Overlay.First.SuppressFamilyBackground);
                Eq(!value, FamilyExempt);
            }

            internal HashSet<int> ProtectedLibraryPids()
            {
                return FamilyBoundary.CollectProtectedLibraryFamily(Mode.GetProfiles(),
                    new ProcessSnapshot(Processes.ToArray()), selfPid, ownerSession);
            }

            internal void AddExactWhitelist(string path)
            {
                AddWhitelist(WhitelistRuleKind.ExactPath, path);
            }

            internal void AddWhitelist(WhitelistRuleKind kind, string value)
            {
                WhitelistRule rule;
                Eq(true, WhitelistRule.TryCreate(kind, value, out rule));
                // The public whitelist mutation enumerates real processes
                // The actual no-save primitive and the evaluator only need this fixture's memory
                Eq(true, (bool)FamilyPolicyInvoke(Mode, "AddWhiteRuleNoSave", rule));
            }

            internal HashSet<int> WhitelistProtected()
            {
                object evaluation = FamilyPolicyInvoke(Mode, "EvaluateWhitelist",
                    new ProcessSnapshot(Processes.ToArray()));
                return RendererReleaseField<HashSet<int>>(evaluation, "Protected");
            }

            internal HashSet<int> CaptureDetectedFamily()
            {
                var snapshot = new List<GameProcessSnapshot>();
                foreach (ProcEntry process in Processes)
                    snapshot.Add(new GameProcessSnapshot { Pid = process.Pid, ParentPid = process.ParentPid,
                        Name = process.Name, Path = process.Path, Creation = process.Creation,
                        Foreground = process.Pid == Renderer.Pid, Visible = process.Pid == Renderer.Pid,
                        FullscreenLike = process.Pid == Renderer.Pid });
                string armed, via;
                GameDetection detection = GameSessionDetector.DetectSnapshot(snapshot, Mode.GetProfiles(), out armed, out via);
                Eq(true, detection != null);
                Eq(Renderer.Pid, detection.RendererPid);
                return new HashSet<int>(detection.FamilyPids);
            }

            internal HashSet<int> FilteredVisibleFamily(HashSet<int> visibleRoots,
                HashSet<int> cachedFamily, GameFamilyEvidence evidence)
            {
                var snapshot = new ProcessSnapshot(Processes.ToArray());
                object evaluation = FamilyPolicyInvoke(Mode, "EvaluateWhitelist", snapshot);
                var parents = RendererReleaseField<Dictionary<int, int>>(evaluation, "Parents");
                var names = RendererReleaseField<Dictionary<int, string>>(evaluation, "Names");
                var creations = RendererReleaseField<Dictionary<int, long>>(evaluation, "Creations");
                var roots = new HashSet<int>(visibleRoots);
                if (!FamilyExempt) roots.Remove(Renderer.Pid);
                HashSet<int> visible = FamilyBoundary.ExpandUserFacingFamily(parents, names, roots);
                var seeds = new HashSet<int>(cachedFamily);
                seeds.Add(Renderer.Pid);
                HashSet<int> descendants = FamilyBoundary.WalkDescendants(parents, seeds, selfPid, 24, creations);
                HashSet<int> ancestors = FamilyBoundary.WalkAncestorChain(parents, Renderer.Pid, selfPid, 24, creations);
                FamilyBoundary.FilterUserFacingGameFamily(visible, Overlay.First, snapshot,
                    Renderer.Pid, selfPid, ownerSession, cachedFamily, descendants, ancestors, evidence);
                return visible;
            }

            internal bool EarlyProtected(ProcEntry process)
            {
                return FamilyBoundary.IsGameOrWhitelistProtected(process.Pid, Renderer.Pid,
                    WhitelistProtected().Contains(process.Pid), ProtectedLibraryPids().Contains(process.Pid),
                    FamilyExempt, new HashSet<int> { Renderer.Pid }, Descendants());
            }

            internal bool CanSuppress(ProcEntry process)
            {
                return CanSuppress(process, Renderer.Pid, false, false);
            }

            internal bool CanSuppress(ProcEntry process, int foregroundPid, bool userFacing, bool aggressive)
            {
                if (EarlyProtected(process)) return false;
                bool exempt = FamilyExempt;
                if (Overlay.Protect(process.Pid, process.Creation, process.Name, process.Path, exempt)) return false;
                var roots = new List<string>();
                foreach (GameProfile profile in Mode.GetProfiles())
                    if (FamilyBoundary.FamilyExemptFor(profile)) roots.Add(profile.Root);
                string containRoot = FamilyBoundary.LibraryRootOf(process.Path, roots);
                if (containRoot == null && exempt) containRoot = Overlay.First.Root;
                Dictionary<int, int> parents;
                Dictionary<int, long> creations;
                ParentIdentities(out parents, out creations);
                HashSet<int> ancestors = FamilyBoundary.WalkAncestorChain(parents, Renderer.Pid, selfPid, 24, creations);
                return FamilyBoundary.BasicBackgroundEligible(process.Pid, selfPid, process.Name, process.Path,
                    process.Session, ownerSession, foregroundPid, userFacing, @"C:\Windows\",
                    exempt && ancestors.Contains(process.Pid), containRoot, aggressive, exempt);
            }

            private HashSet<int> Descendants()
            {
                Dictionary<int, int> parents;
                Dictionary<int, long> creations;
                ParentIdentities(out parents, out creations);
                return FamilyBoundary.WalkDescendants(parents, new HashSet<int> { Renderer.Pid }, selfPid, 24, creations);
            }

            private void ParentIdentities(out Dictionary<int, int> parents, out Dictionary<int, long> creations)
            {
                parents = new Dictionary<int, int>();
                creations = new Dictionary<int, long>();
                foreach (ProcEntry process in Processes)
                {
                    parents[process.Pid] = process.ParentPid;
                    creations[process.Pid] = process.Creation;
                }
            }

            public void Dispose() { Overlay.Dispose(); }
        }

        // Reuses the entry reflection helpers and fake restore constructor from the renderer release tests
        // No Sweep, Acquire, topology init, or real process changes
        // Both reason levels stay Isolated so removing Background never enters the native reapply path
        private sealed class FamilyOverlayFixture : IDisposable
        {
            internal readonly SuppressionCore Core;
            private readonly FamilyPolicyFixture policy;
            internal readonly List<ProcEntry> RestoreRequests = new List<ProcEntry>();
            internal SuppressionCore.RestoreResult Result = SuppressionCore.RestoreResult.Restored;
            private int mutations;

            internal GameMode Mode { get { return policy.Mode; } }
            internal GameProfile First { get { return policy.Current(policy.First.Id); } }
            internal GameProfile Second { get { return policy.Current(policy.Second.Id); } }
            internal string DirectoryPath { get { return policy.DirectoryPath; } }

            internal FamilyOverlayFixture(string root, string name, bool separateProfileRoots = false)
            {
                Core = new SuppressionCore(delegate(int pid, long creation, string processName)
                {
                    RestoreRequests.Add(new ProcEntry { Pid = pid, Creation = creation, Name = processName });
                    return Result;
                }, true);
                policy = new FamilyPolicyFixture(root, name, Core, separateProfileRoots);
                Core.ConfigureMutationBoundary(delegate { mutations++; }, delegate { mutations++; });
            }

            internal bool Protect(int pid, long creation, string name, string path, bool familyExempt = true)
            {
                return (bool)FamilyPolicyInvoke(policy.Mode, "TryProtectOverlayHost", pid, creation, name, path, familyExempt);
            }

            internal object Peek(int pid)
            {
                return RendererReleaseField<System.Collections.IDictionary>(Core, "map")[pid];
            }

            internal int TrackedCount
            {
                get { return RendererReleaseField<System.Collections.IDictionary>(Core, "map").Count; }
            }

            internal object Seed(int pid, long creation, string name, SuppressReason reasons)
            {
                Type type = typeof(SuppressionCore).GetNestedType("Entry", BindingFlags.NonPublic);
                object entry = Activator.CreateInstance(type, true);
                RendererReleaseSet(entry, "Name", name);
                RendererReleaseSet(entry, "Group", "overlay-fixture");
                RendererReleaseSet(entry, "Creation", creation);
                RendererReleaseSet(entry, "OrigPri", 32U);
                RendererReleaseSet(entry, "Reasons", reasons);
                RendererReleaseSet(entry, "BackgroundLevel", (reasons & SuppressReason.Background) != 0 ? SuppressionLevel.Isolated : SuppressionLevel.None);
                RendererReleaseSet(entry, "AntiCheatLevel", (reasons & SuppressReason.AntiCheat) != 0 ? SuppressionLevel.Isolated : SuppressionLevel.None);
                RendererReleaseSet(entry, "Level", reasons != SuppressReason.None ? SuppressionLevel.Isolated : SuppressionLevel.None);
                RendererReleaseSet(entry, "Applied", true);
                RendererReleaseSet(entry, "Journaled", true);
                RendererReleaseField<System.Collections.IDictionary>(Core, "map")[pid] = entry;
                RendererReleaseField<Dictionary<int, long>>(policy.Mode, "repCpu")[pid] = 1;
                RendererReleaseField<Dictionary<int, long>>(policy.Mode, "repCreation")[pid] = creation;
                RendererReleaseField<Dictionary<int, string>>(policy.Mode, "repProc")[pid] = name;
                RendererReleaseField<Dictionary<int, long>>(policy.Mode, "repSealed")[pid] = 2;
                return entry;
            }

            internal void AssertTracked(int pid, bool expected)
            {
                Eq(expected, RendererReleaseField<Dictionary<int, long>>(policy.Mode, "repCpu").ContainsKey(pid));
                Eq(expected, RendererReleaseField<Dictionary<int, long>>(policy.Mode, "repCreation").ContainsKey(pid));
                Eq(expected, RendererReleaseField<Dictionary<int, string>>(policy.Mode, "repProc").ContainsKey(pid));
                Eq(expected, RendererReleaseField<Dictionary<int, long>>(policy.Mode, "repSealed").ContainsKey(pid));
            }

            public void Dispose()
            {
                Eq(0L, Core.ApplyOperations);
                Eq(0, mutations);
                Eq(null, RendererReleaseField<string>(Core, "journalPath"));
                Eq(false, RendererReleaseField<bool>(Core, "marked"));
                policy.Dispose();
            }
        }

        private static HashSet<int> FamilyProtectionCollect(IList<GameProfile> profiles, params ProcEntry[] processes)
        {
            return FamilyBoundary.CollectProtectedLibraryFamily(profiles, new ProcessSnapshot(processes), 99000, 7);
        }

        private static GameProfile FamilyProtectionProfile(string root, string id, bool suppress)
        {
            string directory = Path.Combine(root, id);
            GameProfile profile = FamilyPolicyProfile(id, directory);
            if (suppress) Eq(true, PolicyResolver.SetOverride(profile, PolicyCatalog.KeySuppressFamily, "1"));
            return profile;
        }

        private static ProcEntry FamilyProtectionProcess(int pid, int parent, long creation, string path)
        {
            return new ProcEntry { Pid = pid, ParentPid = parent, Creation = creation,
                Name = Path.GetFileNameWithoutExtension(path), Path = path, Session = 7 };
        }

        private sealed class FamilyPolicyFixture : IDisposable
        {
            internal readonly string DirectoryPath, LibraryFile;
            internal readonly GameProfile First, Second;
            internal readonly GameMode Mode;
            internal int Changes;

            internal FamilyPolicyFixture(string root, string name)
                : this(root, name, null)
            {
            }

            internal FamilyPolicyFixture(string root, string name, SuppressionCore core)
                : this(root, name, core, false)
            {
            }

            internal FamilyPolicyFixture(string root, string name, SuppressionCore core, bool separateProfileRoots)
            {
                Settings.UseTransientStoreForCurrentProcess();
                DirectoryPath = FamilyPolicyDirectory(root, name);
                LibraryFile = Path.Combine(DirectoryPath, GameProfileStore.FileName);
                string firstRoot = separateProfileRoots ? Path.Combine(DirectoryPath, "first") : DirectoryPath;
                string secondRoot = separateProfileRoots ? Path.Combine(DirectoryPath, "second") : DirectoryPath;
                Directory.CreateDirectory(firstRoot);
                Directory.CreateDirectory(secondRoot);
                First = FamilyPolicyProfile("first", firstRoot);
                Second = FamilyPolicyProfile("second", secondRoot);
                // These inert bytes just stamp the files, neither is executable and neither gets launched
                File.WriteAllBytes(First.ExecutablePath, new byte[] { 1, 2, 3, 4 });
                File.WriteAllBytes(Second.ExecutablePath, new byte[] { 5, 6, 7, 8 });
                Eq(true, PolicyResolver.SetOverride(First, PolicyCatalog.KeyBoost, "0"));
                Eq(true, PolicyResolver.SetOverride(First, PolicyCatalog.KeyPreset, "2"));
                Eq(true, new GameProfileStore(DirectoryPath).Save(new[] { First, Second }));
                Mode = new GameMode(DirectoryPath, core ?? new SuppressionCore());
                Mode.LibraryChanged += delegate { Changes++; };
                Eq(false, Mode.ProfileStoreSaveFailed);
            }

            internal GameProfile Current(string id) { return FamilyPolicyFind(Mode.GetProfiles(), id); }
            internal void EnableFakePolicyGate()
            {
                // Set the field only, the public lifecycle setter would restore real processes
                FamilyPolicySetField(Mode, "enabled", true);
                FamilyPolicySetField(Mode, "bgSuppressOn", true);
            }
            public void Dispose() { }
        }

        private static bool FamilyPolicyRunGate(GameMode mode, int epoch, Action action)
        {
            MethodInfo method = typeof(GameMode).GetMethod("RunBackgroundPolicy", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new Exception("Missing production background policy gate.");
            return (bool)method.Invoke(mode, new object[] { epoch, action });
        }

        private static void FamilyPolicySetField(GameMode mode, string name, object value)
        {
            FieldInfo field = typeof(GameMode).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new Exception("Missing fixture state field " + name);
            field.SetValue(mode, value);
        }

        private static object FamilyPolicyGetField(GameMode mode, string name)
        {
            FieldInfo field = typeof(GameMode).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new Exception("Missing fixture state field " + name);
            return field.GetValue(mode);
        }

        private static object FamilyPolicyInvoke(GameMode mode, string name, params object[] args)
        {
            MethodInfo method = typeof(GameMode).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new Exception("Missing production method " + name);
            return method.Invoke(mode, args);
        }

        private static string FamilyPolicyDirectory(string root, string name)
        {
            string dir = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static GameProfile FamilyPolicyProfile(string id, string root)
        {
            GameProfile profile = GameProfileStore.NewProfile("user name / " + id, root, Path.Combine(root, id + ".exe"));
            profile.Id = id;
            profile.Entries.Clear();
            profile.Entries.Add(id);
            return profile;
        }

        private static GameProfile FamilyPolicyFind(IList<GameProfile> profiles, string id)
        {
            foreach (GameProfile profile in profiles)
                if (string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)) return profile;
            throw new Exception("Missing fixture profile " + id);
        }

        private static string FamilyPolicyLegacyLine(GameProfile profile)
        {
            return "P|" + FamilyPolicyBase64(profile.Id) + "|" + FamilyPolicyBase64(profile.Name)
                + "|" + FamilyPolicyBase64(profile.Root) + "|" + FamilyPolicyBase64(profile.ExecutablePath)
                + "|" + FamilyPolicyBase64(Path.GetFileNameWithoutExtension(profile.ExecutablePath));
        }

        private static string FamilyPolicyBase64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private static string FamilyPolicyDescribe(GameProfile profile)
        {
            var values = new List<string>();
            foreach (KeyValuePair<string, string> item in profile.Overrides) values.Add(item.Key + "=" + item.Value);
            values.Sort(StringComparer.Ordinal);
            return profile.Id + "|" + profile.Name + "|" + profile.Root + "|" + profile.ExecutablePath
                + "|" + profile.LearnedExecutablePath + "|" + profile.ForceTrigger + "|"
                + profile.SuppressFamilyBackground + "|" + string.Join(";", values.ToArray());
        }
    }
}
#endif
