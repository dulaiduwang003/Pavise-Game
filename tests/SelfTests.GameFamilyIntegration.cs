// End-to-end library/family regression using real AddGameExecutable/AddScannedGames.
// All EXEs are inert copies of this test assembly and are never executed. Every
// install record, process identity, whitelist and data file belongs to the fixture.
// No Program.Main, GameMode.Enabled/Start/Stop/Loop/Sweep, GPU or native tuning.
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunGameFamilyIntegrationRegressionTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            string previousLog = Logger.LogPath;
            int previousLanguage = Lang.Cur;
            string root = Path.Combine(Path.GetTempPath(), "PaviseGameFamilyIntegration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Logger.LogPath = Path.Combine(root, "integration.log");
            Lang.Cur = 0;
            Action<string>[] tests =
            {
                GfiManualInstallLocationFindsSibling,
                GfiManualSourceRequiresCorroboration,
                GfiManualUnverifiedSourceDoesNotGuess,
                GfiScannedNarrowRootIsRepaired,
                GfiScannedWithoutRootUsesInstallRecord,
                GfiLoginLayoutUsesMetadataNotNames,
                GfiExistingProfileRepairPersistsFields,
                GfiExistingNoRecordStaysNarrow,
                GfiExistingRepairSaveFailureIsFatal,
                GfiKnownPlatformFallbackDoesNotClaimNestedGames,
                GfiExistingPlatformRootIsClearedAndPersisted,
                GfiConcreteGameWithinKnownPlatformKeepsRoot,
                GfiHistoryRetainsCrossRootBroker,
                GfiHistoryRetainsRendererAfterParentsExit,
                GfiHistoryColdStartDoesNotGuess,
                GfiHistoryClearDropsCapturedChain,
                GfiHistoryReusedPidIsNotInherited,
                GfiHistoryCrossSessionIsRejected,
                GfiHistoryEventsBridgeShortLivedParents,
                GfiHistoryMissingEventParentProofIsRejected,
                GfiFamilyToggleAndWhitelistAreIndependent,
                GfiHistoryProtectionFollowsSwitch
            };
            try
            {
                // Empty injection is deliberate: no helper may fall back to the
                // host's registry/platform catalog while another test is running.
                using (GameInstallScope.UseSnapshotForTest(null, null))
                    foreach (Action<string> test in tests)
                    {
                        test(root);
                        Console.WriteLine("PASS " + test.Method.Name);
                    }
                return tests.Length;
            }
            finally
            {
                Logger.LogPath = previousLog;
                Lang.Cur = previousLanguage;
                string full = Path.GetFullPath(root).TrimEnd('\\');
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(full).StartsWith("PaviseGameFamilyIntegration-", StringComparison.Ordinal)
                    || (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0))
                    throw new Exception("Refusing cleanup outside the owned game-family fixture.");
                if (Directory.Exists(full)) Directory.Delete(full, true);
            }
        }

        private static void GfiRequire(bool condition, string detail)
        {
            if (!condition) throw new Exception("Game-family integration: " + detail);
        }

        private static bool GfiSamePath(string first, string second)
        {
            return string.Equals(first == null ? null : first.TrimEnd('\\'),
                second == null ? null : second.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static void GfiManualInstallLocationFindsSibling(string root)
        {
            using (var f = new GfiFixture(root, "manual-location", false))
            using (GameInstallScope.UseSnapshotForTest(new[] { f.LocationRecord() }, new string[0]))
            {
                GameMode mode = f.CreateMode();
                GameProfile profile = f.AddManual(mode);
                f.AssertInstallRoot(profile);
                f.AssertRendererOnlyCandidate(profile, null, f.Renderer);
                GfiRequire(!mode.HasRendererObservation(profile), "install metadata falsely confirmed renderer activity");
                GfiRequire(GfiSamePath(f.Launcher, profile.ExecutablePath), "adding a launcher silently replaced the selected EXE");
            }
        }

        private static void GfiManualSourceRequiresCorroboration(string root)
        {
            using (var f = new GfiFixture(root, "manual-source", false))
            using (GameInstallScope.UseSnapshotForTest(new[] { f.SourceRecord() }, new string[0]))
            {
                GameProfile profile = f.AddManual(f.CreateMode());
                f.AssertInstallRoot(profile);
                f.AssertRendererOnlyCandidate(profile, null, f.Renderer);
            }
        }

        private static void GfiManualUnverifiedSourceDoesNotGuess(string root)
        {
            foreach (bool conflictingUninstall in new[] { false, true })
                using (var f = new GfiFixture(root, "unverified-source", false))
                using (GameInstallScope.UseSnapshotForTest(new[] { new GameInstallRecord
                {
                    InstallSource = f.InstallRoot,
                    UninstallString = conflictingUninstall ? "\"" + Path.Combine(f.DirectoryPath, "OtherInstaller", "remove.exe") + "\" /remove" : null
                } }, new string[0]))
                {
                    GameProfile profile = f.AddManual(f.CreateMode());
                    f.AssertNarrowRoot(profile);
                    f.AssertNoRendererCandidate(profile, null, f.Renderer);
                }
        }

        private static void GfiScannedNarrowRootIsRepaired(string root)
        {
            using (var f = new GfiFixture(root, "scanned-narrow", false))
            using (GameInstallScope.UseSnapshotForTest(new[] { f.LocationRecord() }, new string[0]))
            {
                GameMode mode = f.CreateMode();
                string error;
                int added = mode.AddScannedGames(new[] { new ScanHit
                {
                    Name = "scanned custom display", Exe = f.Launcher,
                    Root = Path.GetDirectoryName(f.Launcher)
                } }, out error);
                GfiRequire(added == 1 && string.IsNullOrEmpty(error), "scanned launcher was not added: " + error);
                GameProfile profile = f.OnlyProfile(mode);
                f.AssertInstallRoot(profile);
                GfiRequire(profile.Name == "scanned custom display", "scanned name was replaced");
                f.AssertRendererOnlyCandidate(profile, null, f.Renderer);
                f.AssertInstallRoot(f.OnlySavedProfile());
            }
        }

        private static void GfiScannedWithoutRootUsesInstallRecord(string root)
        {
            using (var f = new GfiFixture(root, "scanned-no-root", false))
            using (GameInstallScope.UseSnapshotForTest(new[] { f.SourceRecord() }, new string[0]))
            {
                GameMode mode = f.CreateMode();
                string error;
                GfiRequire(mode.AddScannedGames(new[] { new ScanHit
                { Name = "no prefilled root", Exe = f.Launcher } }, out error) == 1,
                    "scan without a prefilled root failed: " + error);
                GameProfile profile = f.OnlyProfile(mode);
                f.AssertInstallRoot(profile);
                f.AssertRendererOnlyCandidate(profile, null, f.Renderer);
            }
        }

        private static void GfiLoginLayoutUsesMetadataNotNames(string root)
        {
            // Concrete reported shape, paired with the randomly named sibling
            // tests above: no game/launcher executable name earns membership.
            using (var f = new GfiFixture(root, "login-layout", true))
            using (GameInstallScope.UseSnapshotForTest(new[] { f.SourceRecord() }, new string[0]))
            {
                GameMode mode = f.CreateMode();
                GameProfile profile = f.AddManual(mode);
                f.AssertInstallRoot(profile);
                GfiRequire(profile.Entries.SetEquals(new[] { "client" }), "selected login EXE entry changed prematurely");
                f.AssertRendererOnlyCandidate(profile, null, f.Renderer);
                var fullscreen = f.GameProcess(f.RendererProcess(f.Renderer), true);
                string armed, via;
                GameDetection detection = GameSessionDetector.DetectSnapshot(new[] { fullscreen }, new[] { profile }, out armed, out via, null);
                GfiRequire(detection != null && detection.RendererCandidateSelected
                    && detection.RendererPid == fullscreen.Pid && detection.RendererLearnable,
                    "fullscreen sibling renderer did not enter normal renderer selection");
                GfiRequire(GfiSamePath(f.OnlySavedProfile().ExecutablePath, f.Launcher),
                    "a mere snapshot automatically rewrote the game library");
            }
        }

        private static void GfiExistingProfileRepairPersistsFields(string root)
        {
            using (var f = new GfiFixture(root, "existing-fields", false))
            {
                GameProfile before;
                using (GameInstallScope.UseSnapshotForTest(null, null))
                {
                    GameMode mode = f.CreateMode();
                    GameProfile added = f.AddManual(mode);
                    f.AssertNarrowRoot(added);
                    GfiRequire(mode.SetProfileFamilySuppression(added.Id, true), "could not opt fixture into family suppression");
                    GfiRequire(mode.SetProfileOverride(added.Id, PolicyCatalog.KeyBoost, "0"), "could not save ordinary override");
                    GfiRequire(mode.SetProfileForceTrigger(added.Id, true), "could not preserve force-entry setting");
                    GfiRequire(mode.RenameProfile(added.Id, "用户名称 · keep me"), "could not save the user name");
                    before = f.OnlyProfile(mode);
                    before.LearnedExecutablePath = f.Renderer;
                    GfiRequire(new GameProfileStore(f.DataDirectory).Save(new[] { before }), "could not create the legacy learned-path record");
                }
                using (GameInstallScope.UseSnapshotForTest(new[] { f.LocationRecord() }, new string[0]))
                {
                    GameMode reloaded = f.CreateMode();
                    GameProfile after = f.OnlyProfile(reloaded);
                    f.AssertInstallRoot(after);
                    f.AssertInstallRoot(f.OnlySavedProfile());
                    GfiRequire(before.Id == after.Id && before.Name == after.Name
                        && GfiSamePath(before.ExecutablePath, after.ExecutablePath)
                        && GfiSamePath(before.LearnedExecutablePath, after.LearnedExecutablePath)
                        && before.ForceTrigger == after.ForceTrigger && after.SuppressFamilyBackground
                        && before.Entries.SetEquals(after.Entries), "root repair changed identity or user options");
                    GfiRequire(before.Overrides.Count == after.Overrides.Count, "root repair added or removed an override");
                    foreach (KeyValuePair<string, string> pair in before.Overrides)
                    {
                        string value;
                        GfiRequire(after.Overrides.TryGetValue(pair.Key, out value) && value == pair.Value,
                            "root repair changed override " + pair.Key);
                    }
                    GameProfile saved = f.OnlySavedProfile();
                    GfiRequire(saved.Name == before.Name && saved.SuppressFamilyBackground
                        && saved.ForceTrigger == before.ForceTrigger && saved.Entries.SetEquals(before.Entries)
                        && GfiSamePath(saved.ExecutablePath, before.ExecutablePath)
                        && GfiSamePath(saved.LearnedExecutablePath, before.LearnedExecutablePath),
                        "root repair was not persisted with the original options");
                }
            }
        }

        private static void GfiExistingNoRecordStaysNarrow(string root)
        {
            using (var f = new GfiFixture(root, "existing-no-record", false))
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                GameProfile before = f.AddManual(f.CreateMode());
                f.AssertNarrowRoot(before);
                string content = File.ReadAllText(f.LibraryFile);
                GameProfile after = f.OnlyProfile(f.CreateMode());
                f.AssertNarrowRoot(after);
                GfiRequire(content == File.ReadAllText(f.LibraryFile), "loading without metadata rewrote the profile");
                f.AssertNoRendererCandidate(after, null, f.Renderer);
            }
        }

        private static void GfiExistingRepairSaveFailureIsFatal(string root)
        {
            using (var f = new GfiFixture(root, "existing-save-failure", false))
            {
                using (GameInstallScope.UseSnapshotForTest(null, null))
                    f.AssertNarrowRoot(f.AddManual(f.CreateMode()));
                string before = File.ReadAllText(f.LibraryFile);
                using (GameInstallScope.UseSnapshotForTest(new[] { f.LocationRecord() }, new string[0]))
                using (var held = new FileStream(f.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    GameMode reloaded = f.CreateMode(true);
                    GfiRequire(reloaded.ProfileStoreSaveFailed, "failed load-time root commit was reported as healthy");
                    GfiRequire(before == File.ReadAllText(f.LibraryFile), "failed root repair damaged the original profile");
                }
                f.AssertNarrowRoot(f.OnlySavedProfile());
            }
        }

        private static void GfiKnownPlatformFallbackDoesNotClaimNestedGames(string root)
        {
            using (var f = new GfiFixture(root, "known-platform-fallback", false))
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 10);
                string platformRoot = Path.Combine(f.DirectoryPath, "Hub-" + suffix);
                string platformExe = Path.Combine(platformRoot, "entry-" + suffix + ".exe");
                string nestedGame = Path.Combine(platformRoot, "Packages-" + suffix, "Product-" + suffix, "match-" + suffix + ".exe");
                f.CopyExecutable(platformExe);
                f.CopyExecutable(nestedGame);
                using (GameInstallScope.UseSnapshotForTest(null, new[] { platformRoot }))
                {
                    GameMode mode = f.CreateMode();
                    GfiRequire(mode.AddGameExecutable("user-selected random platform", platformExe),
                        "a known platform EXE should remain a valid exact library entry");
                    GameProfile profile = f.OnlyProfile(mode);
                    GfiRequire(profile.Root == null && GfiSamePath(profile.ExecutablePath, platformExe)
                        && profile.Entries.SetEquals(new[] { Path.GetFileNameWithoutExtension(platformExe) }),
                        "known platform fallback retained a shared directory family");
                    f.AssertNoRendererCandidate(profile, null, nestedGame);
                    ProcEntry nested = f.RendererProcess(nestedGame);
                    nested.ParentPid = 0;
                    GfiRequire(GameMode.CollectProtectedLibraryFamily(new[] { profile },
                        new ProcessSnapshot(new[] { nested }), f.SelfPid, f.Session).Count == 0,
                        "platform directory alone protected an unrelated nested game");
                    GameProfile saved = f.OnlySavedProfile();
                    GfiRequire(saved.Root == null && GfiSamePath(saved.ExecutablePath, platformExe),
                        "exact platform entry was not saved without the shared root");
                }
            }
        }

        private static void GfiExistingPlatformRootIsClearedAndPersisted(string root)
        {
            using (var f = new GfiFixture(root, "existing-platform-root", false))
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 10);
                string platformRoot = Path.Combine(f.DirectoryPath, "Hub-" + suffix);
                string platformExe = Path.Combine(platformRoot, "entry-" + suffix + ".exe");
                f.CopyExecutable(platformExe);
                GameProfile before;
                using (GameInstallScope.UseSnapshotForTest(null, null))
                {
                    GameMode original = f.CreateMode();
                    GfiRequire(original.AddGameExecutable("用户平台名称 · retain", platformExe),
                        "could not create the old platform-root profile through AddGameExecutable");
                    GameProfile profile = f.OnlyProfile(original);
                    GfiRequire(GfiSamePath(profile.Root, platformRoot), "fixture did not reproduce the legacy fallback root");
                    GfiRequire(original.SetProfileFamilySuppression(profile.Id, true), "could not set the platform family preference");
                    GfiRequire(original.SetProfileOverride(profile.Id, PolicyCatalog.KeyBoost, "0"), "could not set the platform ordinary override");
                    GfiRequire(original.SetProfileForceTrigger(profile.Id, true), "could not set the platform force-entry preference");
                    before = f.OnlyProfile(original);
                }
                using (GameInstallScope.UseSnapshotForTest(null, new[] { platformRoot }))
                {
                    GameMode reloaded = f.CreateMode();
                    GameProfile after = f.OnlyProfile(reloaded);
                    GameProfile saved = f.OnlySavedProfile();
                    foreach (GameProfile current in new[] { after, saved })
                    {
                        GfiRequire(current.Root == null, "load-time repair did not clear the known shared platform root");
                        GfiRequire(current.Id == before.Id && current.Name == before.Name
                            && GfiSamePath(current.ExecutablePath, before.ExecutablePath)
                            && GfiSamePath(current.LearnedExecutablePath, before.LearnedExecutablePath)
                            && current.ForceTrigger == before.ForceTrigger
                            && current.SuppressFamilyBackground == before.SuppressFamilyBackground
                            && current.Entries.SetEquals(before.Entries)
                            && current.Overrides.Count == before.Overrides.Count,
                            "clearing a platform root changed the selected EXE or user options");
                        foreach (KeyValuePair<string, string> pair in before.Overrides)
                        {
                            string value;
                            GfiRequire(current.Overrides.TryGetValue(pair.Key, out value) && value == pair.Value,
                                "clearing platform root changed option " + pair.Key);
                        }
                    }
                }
            }
        }

        private static void GfiConcreteGameWithinKnownPlatformKeepsRoot(string root)
        {
            using (var f = new GfiFixture(root, "known-platform-specific-game", false))
            {
                string platformRoot = Path.GetDirectoryName(f.InstallRoot);
                using (GameInstallScope.UseSnapshotForTest(new[] { f.LocationRecord() }, new[] { platformRoot }))
                {
                    GameMode mode = f.CreateMode();
                    GameProfile profile = f.AddManual(mode);
                    f.AssertInstallRoot(profile);
                    f.AssertRendererOnlyCandidate(profile, null, f.Renderer);
                    GameProfile reloaded = f.OnlyProfile(f.CreateMode());
                    f.AssertInstallRoot(reloaded);
                    f.AssertInstallRoot(f.OnlySavedProfile());
                    GfiRequire(GfiSamePath(reloaded.ExecutablePath, f.Launcher),
                        "concrete game inside a platform lost its exact selected EXE");
                }
                // Even without install metadata, an already specific child
                // directory must not be mistaken for the shared platform itself.
                using (GameInstallScope.UseSnapshotForTest(null, new[] { platformRoot }))
                {
                    GameProfile reloaded = f.OnlyProfile(f.CreateMode());
                    f.AssertInstallRoot(reloaded);
                    f.AssertRendererOnlyCandidate(reloaded, null, f.Renderer);
                }
            }
        }

        private static void GfiHistoryRetainsCrossRootBroker(string root)
        {
            using (var f = new GfiFixture(root, "history-broker", false))
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                GameProfile profile = f.AddManual(f.CreateMode());
                ProcEntry launcher = f.LauncherProcess(), broker = f.BrokerProcess();
                ProcEntry renderer = f.RendererProcess(f.RemoteRenderer);
                renderer.ParentPid = broker.Pid;
                var history = new GameFamilyHistory();
                history.Capture(new ProcessSnapshot(new[] { launcher, broker }), new[] { profile }, f.Session, 1000);
                GameFamilyEvidence evidence = history.Capture(new ProcessSnapshot(new[] { broker, renderer }), new[] { profile }, f.Session, 2000);
                GfiRequire(evidence.Contains(profile, renderer.Pid, renderer.Creation, renderer.Path),
                    "live broker lost verified launcher ancestry after launcher exit");
                f.AssertNoRendererCandidate(profile, null, f.RemoteRenderer);
                f.AssertRendererOnlyCandidate(profile, evidence, f.RemoteRenderer);
            }
        }

        private static void GfiHistoryRetainsRendererAfterParentsExit(string root)
        {
            using (var f = new GfiFixture(root, "history-renderer", false))
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                GameProfile profile = f.AddManual(f.CreateMode());
                ProcEntry renderer = f.RendererProcess(f.RemoteRenderer);
                renderer.ParentPid = GfiFixture.BrokerPid;
                var history = new GameFamilyHistory();
                history.Capture(new ProcessSnapshot(new[] { f.LauncherProcess(), f.BrokerProcess(), renderer }),
                    new[] { profile }, f.Session, 1000);
                GameFamilyEvidence evidence = history.Capture(new ProcessSnapshot(new[] { renderer }), new[] { profile }, f.Session, 2000);
                f.AssertRendererOnlyCandidate(profile, evidence, f.RemoteRenderer);
                GfiRequire(evidence.Contains(profile, renderer.Pid, renderer.Creation, renderer.Path),
                    "already verified renderer was forgotten after both parents exited");
            }
        }

        private static void GfiHistoryColdStartDoesNotGuess(string root)
        {
            using (var f = new GfiFixture(root, "history-cold", false))
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                GameProfile profile = f.AddManual(f.CreateMode());
                ProcEntry renderer = f.RendererProcess(f.RemoteRenderer);
                renderer.ParentPid = GfiFixture.BrokerPid;
                GameFamilyEvidence evidence = new GameFamilyHistory().Capture(new ProcessSnapshot(new[] { renderer }),
                    new[] { profile }, f.Session, 1000);
                GfiRequire(!evidence.Contains(profile, renderer.Pid, renderer.Creation, renderer.Path),
                    "cold start guessed ancestry from a dead numeric parent PID");
                f.AssertNoRendererCandidate(profile, evidence, f.RemoteRenderer);
            }
        }

        private static void GfiHistoryClearDropsCapturedChain(string root)
        {
            using (var f = new GfiFixture(root, "history-clear", false))
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                GameProfile profile = f.AddManual(f.CreateMode());
                ProcEntry renderer = f.RendererProcess(f.RemoteRenderer);
                renderer.ParentPid = GfiFixture.BrokerPid;
                var history = new GameFamilyHistory();
                history.Capture(new ProcessSnapshot(new[] { f.LauncherProcess(), f.BrokerProcess(), renderer }),
                    new[] { profile }, f.Session, 1000);
                history.Clear();
                GameFamilyEvidence evidence = history.Capture(new ProcessSnapshot(new[] { renderer }), new[] { profile }, f.Session, 2000);
                f.AssertNoRendererCandidate(profile, evidence, f.RemoteRenderer);
            }
        }

        private static void GfiHistoryReusedPidIsNotInherited(string root)
        {
            using (var f = new GfiFixture(root, "history-pid-reuse", false))
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                GameProfile profile = f.AddManual(f.CreateMode());
                ProcEntry renderer = f.RendererProcess(f.RemoteRenderer);
                renderer.ParentPid = GfiFixture.BrokerPid;
                var history = new GameFamilyHistory();
                GameFamilyEvidence old = history.Capture(new ProcessSnapshot(new[] { f.LauncherProcess(), f.BrokerProcess(), renderer }),
                    new[] { profile }, f.Session, 1000);
                GfiRequire(old.Contains(profile, renderer.Pid, renderer.Creation, renderer.Path), "fixture never established renderer ancestry");
                ProcEntry reused = f.RendererProcess(f.RemoteRenderer);
                reused.Creation += 1000;
                reused.ParentPid = 0;
                GameFamilyEvidence evidence = history.Capture(new ProcessSnapshot(new[] { reused }), new[] { profile }, f.Session, 2000);
                GfiRequire(!evidence.Contains(profile, reused.Pid, reused.Creation, reused.Path),
                    "new process inherited a previous process's family by PID");
                var window = f.GameProcess(reused, true);
                GfiRequire(GameSessionDetector.FindForegroundCandidateSnapshot(new[] { window }, profile, null, evidence) == null,
                    "PID reuse became a renderer candidate");
            }
        }

        private static void GfiHistoryCrossSessionIsRejected(string root)
        {
            using (var f = new GfiFixture(root, "history-other-session", false))
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                GameProfile profile = f.AddManual(f.CreateMode());
                ProcEntry broker = f.BrokerProcess();
                broker.Session = f.Session + 1;
                ProcEntry renderer = f.RendererProcess(f.RemoteRenderer);
                renderer.ParentPid = broker.Pid;
                var history = new GameFamilyHistory();
                GameFamilyEvidence evidence = history.Capture(new ProcessSnapshot(new[] { f.LauncherProcess(), broker, renderer }),
                    new[] { profile }, f.Session, 1000);
                GfiRequire(!evidence.Contains(profile, renderer.Pid, renderer.Creation, renderer.Path),
                    "a different session supplied the missing family link");
                f.AssertNoRendererCandidate(profile, evidence, f.RemoteRenderer);
            }
        }

        private static void GfiHistoryEventsBridgeShortLivedParents(string root)
        {
            using (var f = new GfiFixture(root, "history-event-chain", false))
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                GameProfile profile = f.AddManual(f.CreateMode());
                ProcEntry launcher = f.LauncherProcess(), broker = f.BrokerProcess();
                ProcEntry renderer = f.RendererProcess(f.RemoteRenderer);
                renderer.ParentPid = broker.Pid;
                var history = new GameFamilyHistory();
                history.Capture(new ProcessSnapshot(new[] { launcher }), new[] { profile }, f.Session, 1000);
                history.ObserveEvents(new ProcessChangeBatch(new[]
                {
                    GfiChange(broker, launcher.Creation, ProcessChangeKind.Started, 1),
                    GfiChange(renderer, broker.Creation, ProcessChangeKind.Started, 2),
                    GfiChange(launcher, 0, ProcessChangeKind.Stopped, 3),
                    GfiChange(broker, launcher.Creation, ProcessChangeKind.Stopped, 4)
                }, false), f.Session, 1200);
                GameFamilyEvidence evidence = history.Capture(new ProcessSnapshot(new[] { renderer }), new[] { profile }, f.Session, 1500);
                GfiRequire(evidence.Contains(profile, renderer.Pid, renderer.Creation, renderer.Path),
                    "verified start events between snapshots lost a short-lived parent chain");
                f.AssertRendererOnlyCandidate(profile, evidence, f.RemoteRenderer);
            }
        }

        private static void GfiHistoryMissingEventParentProofIsRejected(string root)
        {
            using (var f = new GfiFixture(root, "history-unproven-event", false))
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                GameProfile profile = f.AddManual(f.CreateMode());
                ProcEntry launcher = f.LauncherProcess(), broker = f.BrokerProcess();
                ProcEntry renderer = f.RendererProcess(f.RemoteRenderer);
                renderer.ParentPid = broker.Pid;
                var history = new GameFamilyHistory();
                history.Capture(new ProcessSnapshot(new[] { launcher }), new[] { profile }, f.Session, 1000);
                // A mismatched exact parent creation must not be rescued by PID.
                history.ObserveEvents(new ProcessChangeBatch(new[]
                {
                    GfiChange(broker, launcher.Creation + 100000, ProcessChangeKind.Started, 1),
                    GfiChange(renderer, broker.Creation, ProcessChangeKind.Started, 2)
                }, false), f.Session, 1200);
                GameFamilyEvidence evidence = history.Capture(new ProcessSnapshot(new[] { renderer }), new[] { profile }, f.Session, 1500);
                GfiRequire(!evidence.Contains(profile, renderer.Pid, renderer.Creation, renderer.Path),
                    "an event linked to the wrong parent lifetime");
                f.AssertNoRendererCandidate(profile, evidence, f.RemoteRenderer);
            }
        }

        private static void GfiFamilyToggleAndWhitelistAreIndependent(string root)
        {
            using (var f = new GfiFixture(root, "family-whitelist", false))
            using (GameInstallScope.UseSnapshotForTest(new[] { f.LocationRecord() }, new string[0]))
            {
                f.WriteExactWhitelist(f.Broker);
                GameMode mode = f.CreateMode();
                GameProfile profile = f.AddManual(mode);
                ProcEntry launcher = f.LauncherProcess(), renderer = f.RendererProcess(f.Renderer);
                renderer.ParentPid = launcher.Pid;
                ProcEntry broker = f.BrokerProcess();
                var snapshot = new ProcessSnapshot(new[] { launcher, renderer, broker });
                HashSet<int> protectedPids = GameMode.CollectProtectedLibraryFamily(new[] { profile }, snapshot, f.SelfPid, f.Session);
                GfiRequire(protectedPids.Contains(launcher.Pid) && protectedPids.Contains(renderer.Pid)
                    && protectedPids.Contains(broker.Pid), "default family protection missed a child or sibling renderer");
                GfiRequire(f.WhitelistProtected(mode, snapshot).Contains(broker.Pid), "exact whitelist fixture did not load");
                GfiRequire(mode.SetProfileFamilySuppression(profile.Id, true), "family switch did not enable");
                profile = f.OnlyProfile(mode);
                protectedPids = GameMode.CollectProtectedLibraryFamily(new[] { profile }, snapshot, f.SelfPid, f.Session);
                GfiRequire(protectedPids.Count == 0, "family opt-in retained implicit family protection");
                GfiRequire(f.WhitelistProtected(mode, snapshot).Contains(broker.Pid), "family switch erased explicit whitelist protection");
                f.AssertRendererOnlyCandidate(profile, null, f.Renderer);
                GfiRequire(mode.SetProfileFamilySuppression(profile.Id, false), "family switch did not disable");
                profile = f.OnlyProfile(mode);
                protectedPids = GameMode.CollectProtectedLibraryFamily(new[] { profile }, snapshot, f.SelfPid, f.Session);
                GfiRequire(protectedPids.Contains(broker.Pid) && protectedPids.Contains(renderer.Pid),
                    "closing family suppression failed to restore family protection");
            }
        }

        private static void GfiHistoryProtectionFollowsSwitch(string root)
        {
            using (var f = new GfiFixture(root, "history-policy", false))
            using (GameInstallScope.UseSnapshotForTest(null, null))
            {
                GameMode mode = f.CreateMode();
                GameProfile profile = f.AddManual(mode);
                ProcEntry renderer = f.RendererProcess(f.RemoteRenderer);
                renderer.ParentPid = GfiFixture.BrokerPid;
                var history = new GameFamilyHistory();
                history.Capture(new ProcessSnapshot(new[] { f.LauncherProcess(), f.BrokerProcess(), renderer }),
                    new[] { profile }, f.Session, 1000);
                var snapshot = new ProcessSnapshot(new[] { renderer });
                GameFamilyEvidence evidence = history.Capture(snapshot, new[] { profile }, f.Session, 2000);
                GfiRequire(GameMode.CollectProtectedLibraryFamily(new[] { profile }, snapshot, f.SelfPid, f.Session, evidence).Contains(renderer.Pid),
                    "history-discovered family is unprotected while family suppression is off");
                GfiRequire(mode.SetProfileFamilySuppression(profile.Id, true), "history fixture could not enable family suppression");
                profile = f.OnlyProfile(mode);
                GfiRequire(GameMode.CollectProtectedLibraryFamily(new[] { profile }, snapshot, f.SelfPid, f.Session, evidence).Count == 0,
                    "history bypassed the per-game opt-in");
                // Family membership and rendering evidence are separate from a
                // suppression policy switch; opt-in must not disable detection.
                f.AssertRendererOnlyCandidate(profile, evidence, f.RemoteRenderer);
                GfiRequire(mode.SetProfileFamilySuppression(profile.Id, false), "history fixture could not disable family suppression");
                profile = f.OnlyProfile(mode);
                GfiRequire(GameMode.CollectProtectedLibraryFamily(new[] { profile }, snapshot, f.SelfPid, f.Session, evidence).Contains(renderer.Pid),
                    "history family was not protected again after opt-out");
            }
        }

        private static ProcessChange GfiChange(ProcEntry identity, long parentCreation, ProcessChangeKind kind, long sequence)
        {
            return new ProcessChange { Pid = identity.Pid, ParentPid = identity.ParentPid, ParentCreation = parentCreation,
                Creation = identity.Creation, Name = identity.Name, Path = identity.Path, Session = identity.Session,
                Kind = kind, Sequence = sequence };
        }

        private sealed class GfiFixture : IDisposable
        {
            internal const int LauncherPid = 760001, BrokerPid = 760002, RendererPid = 760003;
            private const long CreationBase = 132500000001230000L;
            internal readonly string DirectoryPath, DataDirectory, InstallRoot, Launcher, Renderer, Broker, RemoteRenderer;
            internal int Session, SelfPid;
            private bool unexpectedRestore;
            private readonly List<GameMode> modes = new List<GameMode>();
            internal string LibraryFile { get { return Path.Combine(DataDirectory, GameProfileStore.FileName); } }

            internal GfiFixture(string root, string name, bool reportedShape)
            {
                Settings.UseTransientStoreForCurrentProcess();
                DirectoryPath = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
                DataDirectory = Path.Combine(DirectoryPath, "data");
                Directory.CreateDirectory(DataDirectory);
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 10);
                InstallRoot = reportedShape
                    ? Path.Combine(DirectoryPath, "WeGameApps", "WeGameApps", "英雄联盟")
                    : Path.Combine(DirectoryPath, "Catalog-" + suffix, "Title-" + suffix);
                Launcher = reportedShape ? Path.Combine(InstallRoot, "TCLS", "client.exe")
                    : Path.Combine(InstallRoot, "Entry-" + suffix, "entry-" + suffix + ".exe");
                Renderer = reportedShape ? Path.Combine(InstallRoot, "Game", "League of Legends.exe")
                    : Path.Combine(InstallRoot, "Arena-" + suffix, "frame-" + suffix + ".exe");
                Broker = Path.Combine(DirectoryPath, "OutsideBroker-" + suffix, "broker-" + suffix + ".exe");
                RemoteRenderer = Path.Combine(DirectoryPath, "OutsideArena-" + suffix, "frame-" + suffix + ".exe");
                foreach (string executable in new[] { Launcher, Renderer, Broker, RemoteRenderer })
                    CopyExecutable(executable);
            }

            internal void CopyExecutable(string executable)
            {
                string assembly = Assembly.GetExecutingAssembly().Location;
                GfiRequire(!string.IsNullOrEmpty(assembly) && File.Exists(assembly), "test assembly is not available for inert EXE copies");
                Directory.CreateDirectory(Path.GetDirectoryName(executable));
                File.Copy(assembly, executable, false);
                GfiRequire(GameExecutableResolver.IsPortableExecutable(executable), "fixture is not a real PE file");
            }

            internal GameInstallRecord LocationRecord() { return new GameInstallRecord { InstallLocation = InstallRoot }; }
            internal GameInstallRecord SourceRecord()
            {
                return new GameInstallRecord { InstallSource = InstallRoot,
                    UninstallString = "\"" + Path.Combine(InstallRoot, "remove-original.exe") + "\" /all" };
            }

            internal GameMode CreateMode() { return CreateMode(false); }
            internal GameMode CreateMode(bool expectSaveFailure)
            {
                var core = new SuppressionCore(delegate(int pid, long creation, string processName)
                {
                    unexpectedRestore = true;
                    throw new Exception("Integration fixture attempted real process restoration.");
                }, true);
                var mode = new GameMode(DataDirectory, core);
                modes.Add(mode);
                Session = (int)typeof(GameMode).GetField("selfSession", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode);
                SelfPid = (int)typeof(GameMode).GetField("selfPid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode);
                GfiRequire(Session >= 0, "could not establish a synthetic owner session");
                if (!expectSaveFailure) GfiRequire(!mode.ProfileStoreSaveFailed, "fixture library initialization failed");
                return mode;
            }

            internal GameProfile AddManual(GameMode mode)
            {
                GfiRequire(mode.AddGameExecutable("自定义游戏名 / keep", Launcher), "real AddGameExecutable failed for the chosen launcher");
                return OnlyProfile(mode);
            }

            internal GameProfile OnlyProfile(GameMode mode)
            {
                List<GameProfile> profiles = mode.GetProfiles();
                GfiRequire(profiles.Count == 1, "expected one saved game, got " + profiles.Count);
                return profiles[0];
            }

            internal GameProfile OnlySavedProfile()
            {
                var store = new GameProfileStore(DataDirectory);
                List<GameProfile> profiles = store.LoadProfiles();
                GfiRequire(!store.SaveFailed && profiles.Count == 1, "persisted game profile is missing or invalid");
                return profiles[0];
            }

            internal void AssertInstallRoot(GameProfile profile)
            {
                GfiRequire(GfiSamePath(profile.Root, InstallRoot), "recorded installation root was not selected: " + profile.Root);
                GfiRequire(profile.ContainsPath(Renderer), "sibling renderer remains outside the family's installation scope");
            }

            internal void AssertNarrowRoot(GameProfile profile)
            {
                GfiRequire(GfiSamePath(profile.Root, Path.GetDirectoryName(Launcher)), "missing metadata caused an unproven root expansion: " + profile.Root);
                GfiRequire(!profile.ContainsPath(Renderer), "a sibling was guessed without any record or lineage");
            }

            internal ProcEntry LauncherProcess() { return Process(LauncherPid, 0, CreationBase + 10, Launcher); }
            internal ProcEntry BrokerProcess() { return Process(BrokerPid, LauncherPid, CreationBase + 20, Broker); }
            internal ProcEntry RendererProcess(string path) { return Process(RendererPid, BrokerPid, CreationBase + 30, path); }
            private ProcEntry Process(int pid, int parent, long creation, string path)
            {
                return new ProcEntry { Pid = pid, ParentPid = parent, Creation = creation, Session = Session,
                    Path = path, Name = Path.GetFileNameWithoutExtension(path) };
            }

            internal GameProcessSnapshot GameProcess(ProcEntry identity, bool fullscreen)
            {
                return new GameProcessSnapshot { Pid = identity.Pid, ParentPid = identity.ParentPid,
                    Creation = identity.Creation, Name = identity.Name, Path = identity.Path,
                    Visible = true, Foreground = true, FullscreenLike = fullscreen };
            }

            internal void AssertRendererOnlyCandidate(GameProfile profile, GameFamilyEvidence evidence, string path)
            {
                var renderer = GameProcess(RendererProcess(path), false);
                string armed, via;
                GameDetection detection = GameSessionDetector.DetectSnapshot(new[] { renderer }, new[] { profile }, out armed, out via, evidence);
                GameDetection candidate = GameSessionDetector.FindForegroundCandidateSnapshot(new[] { renderer }, profile, null, evidence);
                GfiRequire(detection != null && candidate != null && detection.RendererPid == renderer.Pid
                    && candidate.RendererPid == renderer.Pid && GfiSamePath(candidate.RendererPath, path),
                    "renderer-only snapshot did not find the actual sibling/descendant candidate");
                GfiRequire(detection.RequiresGpuConfirm && candidate.RequiresGpuConfirm
                    && !candidate.RendererCandidateSelected && candidate.RendererLearnable,
                    "family membership bypassed ordinary windowed-renderer GPU confirmation");
                GfiRequire(!string.IsNullOrEmpty(armed) && !string.IsNullOrEmpty(via), "recognized family did not arm the game");
            }

            internal void AssertNoRendererCandidate(GameProfile profile, GameFamilyEvidence evidence, string path)
            {
                var renderer = GameProcess(RendererProcess(path), true);
                string armed, via;
                GfiRequire(GameSessionDetector.DetectSnapshot(new[] { renderer }, new[] { profile }, out armed, out via, evidence) == null
                    && GameSessionDetector.FindForegroundCandidateSnapshot(new[] { renderer }, profile, null, evidence) == null,
                    "unrelated fullscreen EXE was admitted without installation or process-family evidence");
            }

            internal void WriteExactWhitelist(string executable)
            {
                WhitelistRule rule;
                GfiRequire(WhitelistRule.TryCreate(WhitelistRuleKind.ExactPath, executable, out rule), "invalid whitelist fixture");
                var rules = new List<WhitelistRule> { rule };
                File.WriteAllLines(Path.Combine(DataDirectory, "Pavise.whitelist.txt"), new[]
                { WhitelistRule.Header, rule.Serialize(), GameMode.BuildWhitelistFooter(rules) }, new UTF8Encoding(false));
            }

            internal HashSet<int> WhitelistProtected(GameMode mode, ProcessSnapshot snapshot)
            {
                // The public mutation API would inspect/release real processes.
                // This existing pure evaluator sees only our synthetic identities.
                MethodInfo evaluate = typeof(GameMode).GetMethod("EvaluateWhitelist", BindingFlags.Instance | BindingFlags.NonPublic);
                object result = evaluate.Invoke(mode, new object[] { snapshot });
                return (HashSet<int>)result.GetType().GetField("Protected").GetValue(result);
            }

            public void Dispose()
            {
                GfiRequire(!unexpectedRestore, "an isolated test entered a process restore path");
                foreach (GameMode mode in modes)
                {
                    Type type = typeof(GameMode);
                    BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    GfiRequire(type.GetField("worker", flags).GetValue(mode) == null
                        && !(bool)type.GetField("enabled", flags).GetValue(mode)
                        && !(bool)type.GetField("active", flags).GetValue(mode),
                        "an isolated test started the game-mode runtime");
                }
            }
        }
    }
}
#endif
