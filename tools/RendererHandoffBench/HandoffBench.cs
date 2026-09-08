// 文件用途 隔离的正确性台架 身份是真的自有身份 窗口 GPU 和恢复证据都是合成的
// 不跑 Program 不跑完整 SelfTests 不跑 GameMode 的 Loop Sweep Boost 也不做真实 GPU 采样
#if !PAVISE_RENDERER_BENCH || !PAVISE_SELFTEST
#error This bench requires PAVISE_RENDERER_BENCH and PAVISE_SELFTEST.
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PaviseApp
{
    internal static class RendererHandoffBench
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly List<Row> rows = new List<Row>();
        private static readonly List<Dictionary<string, object>> fixtures = new List<Dictionary<string, object>>();
        private static readonly List<string> failures = new List<string>();
        private static string output;
        private static int selfPid, session;
        private static IDisposable installSnapshot;

        public sealed class Row
        {
            public int Repeat;
            public string Layout, Id, Outcome, Observation;
            public Dictionary<string, object> Details;
        }
        private sealed class StepResult
        {
            internal GameDetection Raw, Proposal, Hit;
            internal bool Finalized;
        }

        private static int Main(string[] args)
        {
            if (args.Length != 2) { Console.Error.WriteLine("Usage: RendererHandoffBench.exe <run-directory> <repeats>"); return 2; }
            output = Path.GetFullPath(args[0]);
            int repeats;
            if (!int.TryParse(args[1], out repeats) || repeats < 1 || repeats > 10) return 2;
            Check(SamePath(output.TrimEnd('\\'), AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')), "Output must be this isolated executable's directory.");
            using (Process self = Process.GetCurrentProcess()) { selfPid = self.Id; session = self.SessionId; }
            Settings.UseTransientStoreForCurrentProcess(); // Before any application constructor/test.
            Lang.Init();
            Logger.LogPath = Path.Combine(output, "production-decision.log");
            DateTime started = DateTime.UtcNow;
            try
            {
                IsolateCatalogs();
                for (int repeat = 1; repeat <= repeats; repeat++)
                {
                    FocusedTests(repeat);
                    RunLayout(repeat, "same-root", false);
                    RunLayout(repeat, "split-root", true);
                }
            }
            catch (Exception error) { Failure(0, "bench", "unexpected", error); }
            var payload = Detail("StartedUtc", started.ToString("o"), "FinishedUtc", DateTime.UtcNow.ToString("o"),
                "BenchKind", "current per-game family-policy and renderer-handoff correctness; not FPS/performance",
                "ProductionEntryPointRun", false, "FullSelfTestEntryPointRun", false, "GameModeConstructorRun", true,
                "GameModeLoopOrSweepRun", false, "ActualGpuSampling", false, "BoostInvoked", false, "ActualSuppressionApplied", false,
                "FocusedPassedGroupExecutions", FocusedPassedGroups(),
                "RealGameOrBrowserLaunched", false, "SettingsStore", "process-local transient dictionary",
                "LibraryStore", "isolated run and unique focused-test temporary directories",
                "ProductionChain", new[] { "ResolveRendererHandoff", "FinalizeRendererSelection", "ApplyStickiness", "CompleteRendererSelection" },
                "RealEvidence", new[] { "owned EXE paths and SHA256", "PID/creation FILETIME", "native identity checks", "actual parent PID", "unchanged helper priority classes", "normal helper exits" },
                "SyntheticEvidence", new[] { "window/foreground flags", "event-gated GPU dictionaries", "release results", "historical sticky anchor", "profile/preset/family settings", "stale identity counterexamples", "empty platform/device caches" },
                "Rows", rows, "Fixtures", fixtures, "Failures", failures);
            File.WriteAllText(Path.Combine(output, "results.json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(payload), new UTF8Encoding(false));
            WriteReport(started);
            Console.WriteLine("SUMMARY rows=" + rows.Count + " failures=" + failures.Count + " output=" + output);
            return failures.Count == 0 ? 0 : 1;
        }

        private static void FocusedTests(int repeat)
        {
            RunCase(repeat, "focused", "tracker-12", delegate
            {
                Check((int)Test("RunRendererHandoffRegressionTests") == 0, "Tracker runner reported a regression.");
                return Detail("PassedGroups", 12, "ReturnProtocol", "0=pass; 1=fail");
            });
            foreach (string name in new[] { "RunRendererReleaseRegressionTests", "RunRendererCoordinatorRegressionTests", "RunFamilySuppressionRegressionTests", "RunGenericRendererRegressionTests",
                "RunGameInstallScopeRegressionTests", "TestGameFamilyHistory", "RunGameFamilyIntegrationRegressionTests" })
            {
                string method = name;
                RunCase(repeat, "focused", method, delegate
                {
                    int count = (int)Test(method);
                    Check(count > 0, "Focused runner returned no passing groups.");
                    return Detail("PassedGroups", count);
                });
            }
            RunCase(repeat, "focused", "candidate-11", delegate { Test("TestRendererCandidateDetection"); return Detail("PassedGroups", 11); });
            string root = Path.Combine(output, "libraries", "focused-" + repeat);
            Directory.CreateDirectory(root);
            foreach (string name in new[] { "TestConfirmedRendererReplacement", "TestConfirmedRendererLearningGuards", "TestConfirmedRendererSaveFailure" })
            {
                string method = name;
                RunCase(repeat, "focused", method, delegate { Test(method, root); return Detail("PassedGroups", 1, "LibraryRoot", root); });
            }
        }
        private static object Test(string name, params object[] args)
        {
            MethodInfo method = typeof(SelfTests).GetMethod(name, Static);
            Check(method != null, "Missing focused test: " + name);
            return method.Invoke(null, args);
        }

        private static void RunLayout(int repeat, string layout, bool split)
        {
            string root = Path.Combine(output, "fixtures", layout);
            string launcherPath = Path.Combine(root, split ? "Menu\\GameLauncher.exe" : "GameLauncher.exe");
            string rendererPath = Path.Combine(root, split ? "Client\\GameRenderer.exe" : "GameRenderer.exe");
            string learnedPath = Path.Combine(root, "Previous", "OldRenderer.exe");
            OwnedPair pair = null;
            OwnedLeaf learned = null;
            Dictionary<string, object> description = null;
            try
            {
                pair = OwnedPair.Start(launcherPath, rendererPath);
                learned = OwnedLeaf.Start(learnedPath);
                description = pair.Describe();
                description["Repeat"] = repeat; description["Layout"] = layout;
                description["OldLearnedPid"] = learned.Identity.Pid; description["OldLearnedPath"] = learned.Identity.Path;
                description["OldLearnedParentPid"] = learned.Identity.ParentPid;
                fixtures.Add(description);
                Record(repeat, layout, "owned-identities", "PASS", "Actual parent/child and independent old-learned identities verified.", description);
                foreach (PerformancePreset preset in new[] { PerformancePreset.Standard, PerformancePreset.Competitive, PerformancePreset.Handheld })
                    foreach (bool family in new[] { false, true })
                        foreach (bool oldLearned in new[] { false, true })
                        {
                            string id = preset + "-family-" + family + "-anchor-" + (oldLearned ? "learned" : "launcher");
                            PerformancePreset casePreset = preset;
                            bool caseFamily = family, caseLearned = oldLearned;
                            RunCase(repeat, layout, id, delegate { return WindowedReplacement(repeat, layout, id, root, pair, learned, casePreset, caseFamily, caseLearned); });
                        }
                RunCase(repeat, layout, "native-rejects-stale-identity", delegate { return IdentityRejection(repeat, layout, root, pair); });
                RunCase(repeat, layout, "force-safety-only", delegate { return ForceSafety(repeat, layout, root, pair); });
                Check(pair.PrioritiesUnchanged() && learned.PriorityUnchanged(), "Helper priority changed.");
                pair.Stop(); learned.Stop();
                Check(pair.CleanExit && learned.CleanExit && !pair.CleanupUsedKill && !learned.CleanupUsedKill, "Helpers did not exit normally without kill.");
                Record(repeat, layout, "normal-pair-exit", "PASS", "Launcher, original child and old learned all exited with code zero.", Detail("CleanupUsedKill", false));

                // root 是故意把子进程关掉的 别说成子进程活下来了
                RunCase(repeat, layout, "trusted-root-after-launcher-exit", delegate
                {
                    using (OwnedLeaf detached = OwnedLeaf.Start(rendererPath))
                    using (var c = new BenchContext(repeat, layout, "detached", Profile("detached-" + repeat + layout, root, launcherPath, null, PerformancePreset.Standard, false), PerformancePreset.Standard, false))
                    {
                        Check(detached.Identity.ParentPid == selfPid, "Independent renderer's real parent must be the bench.");
                        c.Snapshot = new[] { Window(detached.Identity, true) }; c.ForegroundPid = detached.Identity.Pid;
                        StepResult pending = c.Step();
                        Check(pending.Hit == null && c.Protected(detached.Identity), "Independent renderer did not wait safely for proof.");
                        c.WaitForProbeEntry(); c.FinishProbe();
                        StepResult done = c.Step();
                        Check(done.Finalized && SameIdentity(done.Hit, detached.Identity), "Trusted Root did not permit the independent renderer handoff.");
                        Check(SamePath(c.CurrentProfile.ExecutablePath, rendererPath), "Independent renderer was not saved as sole entry.");
                        Check(detached.PriorityUnchanged(), "Independent renderer priority changed.");
                        detached.Stop();
                        Check(detached.CleanExit && !detached.CleanupUsedKill, "Independent renderer did not exit normally.");
                        return Detail("OldLauncherActuallyExited", pair.LauncherExited, "OriginalChildActuallyExited", pair.RendererExited,
                            "IndependentRendererPid", detached.Identity.Pid, "ActualParentPid", detached.Identity.ParentPid, "BenchPid", selfPid,
                            "OriginalChildSurvived", false, "Association", "declared trusted Root, not a surviving child",
                            "SavedExecutable", c.CurrentProfile.ExecutablePath, "NormalExit", detached.CleanExit);
                    }
                });
            }
            catch (Exception error) { Failure(repeat, layout, "fixture-lifecycle", error); }
            finally
            {
                if (learned != null) learned.Dispose();
                if (pair != null) pair.Dispose();
                if (description != null)
                {
                    description["LauncherExited"] = pair.LauncherExited; description["RendererExited"] = pair.RendererExited;
                    description["OldLearnedExited"] = learned != null && learned.Exited;
                    description["CleanupUsedKill"] = pair.CleanupUsedKill || (learned != null && learned.CleanupUsedKill);
                    description["AllExitCodesZero"] = pair.CleanExit && learned != null && learned.CleanExit;
                }
            }
        }

        private static Dictionary<string, object> WindowedReplacement(int repeat, string layout, string id, string root,
            OwnedPair pair, OwnedLeaf learned, PerformancePreset preset, bool family, bool oldLearned)
        {
            GameProfile profile = Profile(id + "-" + repeat + "-" + layout, root, pair.Launcher.Path, oldLearned ? learned.Identity.Path : null, preset, false);
            using (var c = new BenchContext(repeat, layout, id, profile, preset, family))
            {
                profile = c.CurrentProfile; // Includes the per-game setting saved before the scenario.
                GameProcessSnapshot old = oldLearned ? learned.Identity : pair.Launcher;
                c.SeedSticky(old); // Historical setup only; fresh renderer takes the entire chain.
                c.Snapshot = new[] { Window(pair.Launcher, false), Window(learned.Identity, false), Window(pair.Renderer, true) };
                c.ForegroundPid = pair.Renderer.Pid; c.ReleaseState = BackgroundReleaseState.Pending;
                StepResult recovery = c.Step();
                Check(SameIdentity(recovery.Hit, old) && c.Protected(pair.Renderer), "Pending recovery lost anchor/protection.");
                Check(c.GpuCalls == 0 && !c.HasProbe, "Probe ran before recovery completed.");
                c.AssertLibraryUnchanged();
                c.ReleaseState = BackgroundReleaseState.Ready;
                StepResult waiting = c.Step(); c.WaitForProbeEntry();
                Check(SameIdentity(waiting.Hit, old), "Unfinished GPU work replaced the old anchor.");
                for (int poll = 0; poll < 8; poll++)
                    Check(SameIdentity(c.Step().Hit, old) && c.Protected(pair.Renderer), "Pending polls lost safe state.");
                Check(c.GpuCalls == 1 && c.HasProbe, "Confirmation is not single-flight.");
                Check(!(bool)Get(c.Mode, "boostOn") && c.CurrentProfile.SuppressFamilyBackground == !family
                    && (PerformancePreset)Get(c.Mode, "preset") == preset, "Matrix settings not loaded.");
                Check(!c.Protected(WithCreation(pair.Renderer, pair.Renderer.Creation + 1)), "Protection accepted another PID lifetime.");
                c.AssertLibraryUnchanged();
                c.FinishProbe();
                StepResult confirmed = c.Step();
                Check(confirmed.Finalized && SameIdentity(confirmed.Hit, pair.Renderer) && !c.HasCandidate, "Confirmed windowed renderer did not replace/complete the live old anchor.");
                GameProfile saved = c.CurrentProfile;
                Check(saved.Id == profile.Id && saved.Name == profile.Name && saved.ForceTrigger == profile.ForceTrigger, "User profile identity/name/Force changed.");
                Check(saved.SuppressFamilyBackground == !family, "Handoff changed this game's family setting.");
                Check(SamePath(saved.ExecutablePath, pair.Renderer.Path) && saved.LearnedExecutablePath == null, "Old executable/learned alias survived.");
                Check(saved.Entries.Count == 1 && saved.Entries.Contains(pair.Renderer.Name), "Historical Entries survived.");
                Check(SamePath(saved.Root, profile.Root), "Trusted containing Root was not preserved.");
                // 在同一个目录里不等于精确指认了某个可执行文件或别名
                Check(!GameSessionDetector.IsProfileEntryName(saved, pair.Launcher.Name)
                    && !GameSessionDetector.IsProfileEntryName(saved, learned.Identity.Name), "Old entry names survived.");
                Check(saved.Overrides.Count == profile.Overrides.Count, "Overrides were lost.");
                foreach (var entry in profile.Overrides)
                    Check(saved.Overrides.ContainsKey(entry.Key) && saved.Overrides[entry.Key] == entry.Value, "Override changed: " + entry.Key);
                Check(c.LibraryChanges == 1 && !c.Mode.ProfileStoreSaveFailed, "Expected one successful library notification.");
                GameProfile reloaded = new GameProfileStore(c.LibraryDirectory).LoadProfiles()[0];
                Check(SamePath(reloaded.ExecutablePath, saved.ExecutablePath) && reloaded.LearnedExecutablePath == null && reloaded.Entries.Count == 1, "Replacement did not persist.");
                Check(pair.PrioritiesUnchanged() && learned.PriorityUnchanged(), "Helper priority changed.");
                Check(File.Exists(pair.Launcher.Path) && File.Exists(learned.Identity.Path) && File.Exists(pair.Renderer.Path), "An old fixture file was deleted.");
                // 这把只读租约在手 多余的那次保存会失败
                using (var readLease = new FileStream(c.LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Check(SameIdentity(c.Step().Hit, pair.Renderer) && !c.Mode.ProfileStoreSaveFailed, "Idempotent confirmation attempted a save.");
                Check(c.LibraryChanges == 1 && c.GpuCalls == 1, "Steady state repeated save/probe.");
                return Detail("Preset", preset.ToString(), "FamilyExempt", family, "SuppressFamilyBackground", !family, "PolicyScope", "this profile only", "BoostEnabled", false,
                    "OldAnchor", oldLearned ? "old learned" : "launcher", "OldAnchorPid", old.Pid, "OldLauncherAndLearnedStillAlive", true,
                    "WindowedRendererPid", pair.Renderer.Pid, "RecoveryPendingProtected", true, "PendingGpuProtectedAcrossEightPolls", true,
                    "NativeIdentityCheck", true, "FakeGpuCalls", c.GpuCalls, "BackgroundReleaseCalls", c.ReleaseCalls,
                    "LibraryChangedEvents", c.LibraryChanges, "SavedExecutable", saved.ExecutablePath, "LearnedAliasCleared", true,
                    "Entries", new List<string>(saved.Entries), "Root", saved.Root, "OldFilesRemain", true, "IdempotentSaveAvoided", true, "SchedulingWriteAttempted", false);
            }
        }

        private static Dictionary<string, object> IdentityRejection(int repeat, string layout, string root, OwnedPair pair)
        {
            using (var c = new BenchContext(repeat, layout, "identity-rejection", Profile("identity-" + repeat + layout, root, pair.Launcher.Path, null, PerformancePreset.Standard, false), PerformancePreset.Standard, false))
            {
                c.SeedSticky(pair.Launcher); c.ForegroundPid = pair.Renderer.Pid;
                GameProcessSnapshot stale = WithCreation(pair.Renderer, pair.Renderer.Creation + 1); stale.Foreground = true;
                c.Snapshot = new[] { Window(pair.Launcher, false), stale };
                Check(SameIdentity(c.Step().Hit, pair.Launcher) && !c.HasCandidate && c.GpuCalls == 0, "Native verification accepted stale creation.");
                GameProcessSnapshot wrongPath = Window(pair.Renderer, true); wrongPath.Path = Path.Combine(root, "Different", "GameRenderer.exe");
                c.Snapshot = new[] { Window(pair.Launcher, false), wrongPath };
                Check(SameIdentity(c.Step().Hit, pair.Launcher) && !c.HasCandidate && c.GpuCalls == 0, "Native verification accepted a changed path.");
                c.AssertLibraryUnchanged();
                return Detail("ActualOwnedPid", pair.Renderer.Pid, "RealCreation", pair.Renderer.Creation, "InjectedStaleCreation", stale.Creation,
                    "NativeIdentitySeamUsed", false, "ActualOsPidReuseClaimed", false, "StaleCreationRejected", true, "ChangedPathRejected", true, "FakeGpuCalls", 0);
            }
        }

        private static Dictionary<string, object> ForceSafety(int repeat, string layout, string root, OwnedPair pair)
        {
            using (var c = new BenchContext(repeat, layout, "force", Profile("force-" + repeat + layout, root, pair.Launcher.Path, null, PerformancePreset.Competitive, true), PerformancePreset.Competitive, false))
            {
                c.SeedSticky(pair.Launcher); c.ForegroundPid = pair.Renderer.Pid;
                c.Snapshot = new[] { Window(pair.Launcher, false), Window(pair.Renderer, true) };
                Check(SameIdentity(c.Step().Hit, pair.Launcher) && c.Protected(pair.Renderer), "Force lost anchor or safety protection.");
                Check(c.Tracker.Current.RendererSafetyOnly && c.GpuCalls == 0 && !c.HasProbe, "SafetyOnly started confirmation.");
                c.AssertLibraryUnchanged();
                return Detail("ForceTrigger", true, "CandidateSafetyOnly", true, "TemporaryProtection", true, "FakeGpuCalls", 0, "LibraryUnchanged", true);
            }
        }

        private static GameProfile Profile(string id, string root, string executable, string learned, PerformancePreset preset, bool force)
        {
            GameProfile profile = GameProfileStore.NewProfile("用户游戏名 / " + id, root, executable);
            profile.Id = id; profile.ForceTrigger = force; profile.LearnedExecutablePath = learned;
            profile.Entries.Add("HistoricalAlias");
            if (learned != null) profile.Entries.Add(Path.GetFileNameWithoutExtension(learned));
            Check(PolicyResolver.SetOverride(profile, PolicyCatalog.KeyBoost, "0"), "Cannot set Boost override.");
            Check(PolicyResolver.SetOverride(profile, PolicyCatalog.KeyPreset, ((int)preset).ToString()), "Cannot set preset override.");
            return profile;
        }

        private sealed class BenchContext : IDisposable
        {
            internal readonly GameMode Mode;
            internal readonly string LibraryDirectory, LibraryFile;
            internal IList<GameProcessSnapshot> Snapshot = new GameProcessSnapshot[0];
            internal volatile int ForegroundPid;
            internal BackgroundReleaseState ReleaseState = BackgroundReleaseState.Ready;
            internal int GpuCalls, ReleaseCalls, LibraryChanges;
            private readonly string originalStore;
            private readonly ManualResetEvent entered = new ManualResetEvent(false), evidenceReady = new ManualResetEvent(false);
            private bool disposed;
            internal BenchContext(int repeat, string layout, string id, GameProfile profile, PerformancePreset preset, bool family)
            {
                Settings.UseTransientStoreForCurrentProcess();
                Settings.Save(PolicyCatalog.KeyBoost, false); Settings.SaveStr(PolicyCatalog.KeyPreset, ((int)preset).ToString());
                IsolateCatalogs();
                LibraryDirectory = Path.Combine(output, "libraries", "repeat-" + repeat, layout, id);
                Directory.CreateDirectory(LibraryDirectory);
                Check(new GameProfileStore(LibraryDirectory).Save(new[] { profile }), "Cannot seed isolated library.");
                LibraryFile = Path.Combine(LibraryDirectory, GameProfileStore.FileName);
                Mode = new GameMode(LibraryDirectory, new SuppressionCore());
                Check(Mode.SetProfileFamilySuppression(profile.Id, !family), "Cannot set the isolated profile's family policy.");
                originalStore = File.ReadAllText(LibraryFile);
                // 绝不调 Enabled 那个有副作用的 setter 也不调 Start Loop Stop Sweep Boost
                Set(Mode, "enabled", true); Set(Mode, "active", true);
                Mode.RendererTestForeground = delegate { return ForegroundPid; };
                Mode.RendererTestCandidate = delegate(ProcessSnapshot ignored, IList<GameProfile> profiles, GameDetection incumbent)
                { return GameSessionDetector.FindForegroundCandidateSnapshot(Snapshot, profiles, incumbent); };
                // 身份和粘性身份的接缝保持 NULL 原生检查只看自有的辅助进程
                Mode.RendererTestRelease = delegate(int pid, long creation, string name) { Interlocked.Increment(ref ReleaseCalls); return ReleaseState; };
                Mode.RendererTestGpu = FakeGpu;
                Mode.LibraryChanged += delegate { LibraryChanges++; };
            }
            internal GameProfile CurrentProfile { get { return Mode.GetProfiles()[0]; } }
            internal RendererHandoffTracker Tracker { get { return (RendererHandoffTracker)Get(Mode, "rendererHandoff"); } }
            internal bool HasCandidate { get { return Tracker != null && Tracker.HasCandidate; } }
            internal bool HasProbe { get { return Tracker != null && Tracker.HasProbe; } }
            internal void SeedSticky(GameProcessSnapshot old)
            {
                var hit = new GameDetection { Profile = CurrentProfile, RendererPid = old.Pid, RendererCreation = old.Creation,
                    RendererName = old.Name, RendererPath = old.Path, RendererCandidateSelected = true, RendererUserSelected = true,
                    RendererForeground = false, Evidence = "historical sticky fixture" };
                hit.FamilyPids.Add(old.Pid); hit.FamilyNames.Add(old.Name);
                Check(SameIdentity((GameDetection)Invoke(Mode, "ApplyStickiness", hit), old), "Cannot seed verified historical sticky state.");
            }
            internal StepResult Step()
            {
                List<GameProfile> library = Mode.GetProfiles();
                var entries = new List<ProcEntry>();
                foreach (GameProcessSnapshot identity in Snapshot)
                    entries.Add(new ProcEntry { Pid = identity.Pid, ParentPid = identity.ParentPid, Creation = identity.Creation,
                        Path = identity.Path, Name = identity.Name, Session = session });
                var result = new StepResult { Raw = GameSessionDetector.DetectSnapshot(Snapshot, library) };
                object[] resolve = { new ProcessSnapshot(entries.ToArray()), library, result.Raw, 0 };
                result.Proposal = (GameDetection)Invoke(Mode, "ResolveRendererHandoff", resolve);
                int epoch = (int)resolve[3];
                if (result.Proposal != null)
                {
                    result.Finalized = (bool)Invoke(Mode, "FinalizeRendererSelection", result.Proposal, epoch);
                    if (!result.Finalized) result.Proposal = null;
                }
                Check(epoch == (int)Get(Mode, "rendererHandoffEpoch"), "Unexpected epoch change in a normal bench step.");
                result.Hit = (GameDetection)Invoke(Mode, "ApplyStickiness", result.Proposal);
                if (result.Hit != null) Check((bool)Invoke(Mode, "VerifyRendererCandidate", result.Hit), "Selected helper failed native identity verification.");
                if (result.Proposal != null && RendererHandoffTracker.SameIdentity(result.Proposal, result.Hit)) Invoke(Mode, "CompleteRendererSelection", result.Hit);
                return result;
            }
            internal bool Protected(GameProcessSnapshot identity)
            { return (bool)Invoke(Mode, "IsRendererHandoffProtected", identity.Pid, identity.Creation, identity.Path); }
            internal void AssertLibraryUnchanged()
            { Check(LibraryChanges == 0 && File.ReadAllText(LibraryFile) == originalStore && !Mode.ProfileStoreSaveFailed, "Unconfirmed candidate modified library."); }
            private IDictionary<int, double> FakeGpu(RendererProbeTicket ticket, Func<bool> canceled)
            {
                Interlocked.Increment(ref GpuCalls); entered.Set();
                Stopwatch wait = Stopwatch.StartNew();
                while (!evidenceReady.WaitOne(10))
                {
                    if (canceled()) return null;
                    if (wait.ElapsedMilliseconds > 3000) throw new TimeoutException("Bench evidence gate was not released.");
                }
                if (canceled()) return null;
                var evidence = new Dictionary<int, double>();
                foreach (int pid in ticket.Detection.FamilyPids) evidence[pid] = 1;
                evidence[ticket.Detection.RendererPid] = 38;
                evidence[int.MaxValue] = 99; // Synthetic unrelated family; never opened as an OS PID.
                return evidence;
            }
            internal void WaitForProbeEntry() { Check(entered.WaitOne(2000), "Fake GPU worker did not reach event gate."); }
            internal void FinishProbe() { evidenceReady.Set(); Drain(); Check(GpuCalls == 1, "Expected one fake GPU call."); }
            private void Drain()
            {
                Stopwatch wait = Stopwatch.StartNew();
                while (HasProbe && wait.ElapsedMilliseconds < 3000) Thread.Sleep(1);
                Check(!HasProbe, "Owned fake GPU worker did not finish.");
            }
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                Invoke(Mode, "InvalidateRendererHandoff"); evidenceReady.Set(); Drain();
                Set(Mode, "stopping", true); Set(Mode, "enabled", false);
                entered.Dispose(); evidenceReady.Dispose();
            }
        }

        private static GameProcessSnapshot Window(GameProcessSnapshot p, bool foreground)
        { return new GameProcessSnapshot { Pid = p.Pid, ParentPid = p.ParentPid, Creation = p.Creation, Name = p.Name, Path = p.Path, Visible = true, Foreground = foreground, FullscreenLike = false }; }
        private static GameProcessSnapshot WithCreation(GameProcessSnapshot p, long creation) { GameProcessSnapshot changed = Window(p, false); changed.Creation = creation; return changed; }
        private static bool SameIdentity(GameDetection d, GameProcessSnapshot p) { return d != null && d.RendererPid == p.Pid && d.RendererCreation == p.Creation && SamePath(d.RendererPath, p.Path); }
        private static bool SamePath(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static object Get(GameMode mode, string name) { return typeof(GameMode).GetField(name, Instance).GetValue(mode); }
        private static void Set(GameMode mode, string name, object value) { typeof(GameMode).GetField(name, Instance).SetValue(mode, value); }
        private static object Invoke(GameMode mode, string name, params object[] args) { return typeof(GameMode).GetMethod(name, Instance).Invoke(mode, args); }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static Dictionary<string, object> Detail(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
        private static void IsolateCatalogs()
        {
            if (installSnapshot != null) installSnapshot.Dispose();
            installSnapshot = GameInstallScope.UseSnapshotForTest(new GameInstallRecord[0], new string[0]);
            typeof(GamePlatformCatalog).GetField("rootsResolved", Static).SetValue(null, true);
            typeof(GamePlatformCatalog).GetField("lastResolveTicks", Static).SetValue(null, DateTime.UtcNow.Ticks);
            foreach (object platform in (IEnumerable)typeof(GamePlatformCatalog).GetField("Platforms", Static).GetValue(null))
                platform.GetType().GetField("Roots", Instance).SetValue(platform, new List<string>());
            typeof(PeripheralVendorProbe).GetField("tokens", Static).SetValue(null, new string[0]);
            typeof(PeripheralVendorProbe).GetField("stamp", Static).SetValue(null, Environment.TickCount);
            typeof(PeripheralVendorProbe).GetField("scanned", Static).SetValue(null, true);
        }
        private static void RunCase(int repeat, string layout, string id, Func<Dictionary<string, object>> test)
        { try { Record(repeat, layout, id, "PASS", "All stated invariants passed.", test()); } catch (Exception error) { Failure(repeat, layout, id, error); } }
        private static void Failure(int repeat, string layout, string id, Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            failures.Add("repeat=" + repeat + " " + layout + "/" + id + ": " + error);
            Record(repeat, layout, id, "FAIL", error.Message, Detail("Exception", error.ToString()));
        }
        private static void Record(int repeat, string layout, string id, string outcome, string observation, Dictionary<string, object> details)
        {
            rows.Add(new Row { Repeat = repeat, Layout = layout, Id = id, Outcome = outcome, Observation = observation, Details = details });
            Console.WriteLine("[" + outcome + "] repeat=" + repeat + " " + layout + "/" + id + " " + observation);
        }

        private static void WriteReport(DateTime started)
        {
            var report = new StringBuilder();
            report.AppendLine("# Per-game family policy and renderer handoff verification\n");
            report.AppendLine("Started UTC: " + started.ToString("o") + "\n");
            report.AppendLine("Result: " + (failures.Count == 0 ? "PASS" : "FAIL") + "; records=" + rows.Count + "; failures=" + failures.Count + ". Not an FPS/performance benchmark.\n");
            report.AppendLine("Focused regression group executions passed: " + FocusedPassedGroups() + ". Runner-group counts are recorded in each focused result's Details.PassedGroups; owned-process records are reported separately.\n");
            report.AppendLine("The current ResolveRendererHandoff → FinalizeRendererSelection → ApplyStickiness → CompleteRendererSelection chain runs with Boost disabled. Helper identities/parentage are real; windows, foreground, GPU and release evidence are synthetic. No Program/full-self-test entry, GameMode loop, Sweep, Boost, actual GPU sampling or system-policy write is run.\n");
            report.AppendLine("## Coverage\n");
            report.AppendLine("- Same-directory and Menu/Client sibling layouts; Standard/Competitive/Handheld × per-game family suppression on/off × old launcher/old learned anchors. No global family setting drives the matrix.");
            report.AppendLine("- Per-game defaults and legacy V5 libraries protect families; two profiles persist independently. Missing/idempotent changes, failed commits, stale background-policy epochs and serialized closure use temporary files and fake write actions.");
            report.AppendLine("- Pure snapshot family collection protects another opted-out game's cross-root descendants without granting its shared host's unrelated siblings protection; creation/session/self boundaries and launcher-exit/rootless cases are covered.");
            report.AppendLine("- Optional render-activity history requires an actual GPU 3D sample for the precise target, never selection/fullscreen/Force alone. Exact-path/file-stamp invalidation, corrupt or unwritable cache isolation, bounded loading, stale async samples and the shared GPU gate have focused coverage; GPU evidence is synthetic.");
            report.AppendLine("- Generic renderer rules are tested independently of game/client filenames. A render-activity badge is not a claim that a process is the principal game renderer or that family suppression is safe.");
            report.AppendLine("- Pending recovery and event-gated GPU proof preserve the old live anchor and protect the precise candidate without saving. Proof then replaces the sole library entry with Boost disabled.");
            report.AppendLine("- User Id/Name/overrides/Force remain; obsolete Learned/Entries are cleared, trusted containing Root retained, and old EXE files kept. Repeated confirmation does not save.");
            report.AppendLine("- Native checks reject synthetic stale creation/path evidence for an owned live PID; no actual OS PID reuse is claimed.");
            report.AppendLine("- Force SafetyOnly protects without probing or retargeting. Focused tracker/release/detector/coordinator/learning/family-policy suites cover additional boundaries.");
            report.AppendLine("- Launcher exit also exits its original child. A new independent leaf (real parent=bench) tests trusted Root without a live launcher; it is not a surviving child.\n");
            report.AppendLine("## Results\n");
            report.AppendLine("| Repeat | Layout | Case | Result | Observation |\n|---|---|---|---|---|");
            foreach (Row row in rows)
                report.AppendLine("| " + row.Repeat + " | " + row.Layout + " | " + row.Id + " | " + row.Outcome + " | " + row.Observation.Replace("|", "/").Replace("\n", " ").Replace("\r", " ") + " |");
            report.AppendLine("\n## Safety and interpretation limits\n");
            report.AppendLine("- Settings are transient before constructors/tests. Bench libraries are below this run; focused tests may create/clean unique test temp directories. GameMode is constructed, never started; Enabled's mutating setter is not called.");
            report.AppendLine("- Owned-process cases inject window/foreground, GPU and release seams but retain native identity/sticky checks. Focused tests may use wholly synthetic identities.");
            report.AppendLine("- Event-gated confirmation workers and fake policy-action workers run. No actual restore or GPU capture occurs. Pending protection and the production write gate are queried directly; Sweep/Acquire are not invoked.");
            report.AppendLine("- Platform/device caches are empty in-process. No real game/browser/production executable is launched. Helper priority classes are read only.");
            report.AppendLine("- Helpers use stdin exit/EOF, exact retained Process instances, and independent 40-second lifetimes. Emergency kill or nonzero helper exit fails the bench.");
            report.AppendLine("- Run-Bench.ps1 checks source/test/bench and production-executable hashes in verification.json. Historical reports remain. PASS does not prove real-window capture, GPU accuracy, scheduling success or FPS improvement.");
            if (failures.Count > 0)
            {
                report.AppendLine("\n## Failures\n");
                foreach (string failure in failures) report.AppendLine("    " + failure.Replace("\n", "\n    "));
            }
            File.WriteAllText(Path.Combine(output, "REPORT.md"), report.ToString(), new UTF8Encoding(false));
        }

        private static int FocusedPassedGroups()
        {
            int count = 0;
            foreach (Row row in rows)
            {
                object value;
                if (row.Layout == "focused" && row.Outcome == "PASS" && row.Details != null
                    && row.Details.TryGetValue("PassedGroups", out value)) count += Convert.ToInt32(value);
            }
            return count;
        }

        private static void ValidateFixturePath(string path)
        {
            string resolved = Path.GetFullPath(path), allowed = Path.Combine(output, "fixtures") + "\\";
            Check(SamePath(resolved, path) && resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), "Fixture is outside isolated run.");
            using (SHA256 hash = SHA256.Create())
            using (FileStream reference = File.OpenRead(Path.Combine(output, "FixtureHost.exe")))
            using (FileStream candidate = File.OpenRead(resolved))
                Check(Convert.ToBase64String(hash.ComputeHash(reference)) == Convert.ToBase64String(hash.ComputeHash(candidate)), "Fixture is not the compiled harmless helper.");
        }
        private static bool WaitOrKillOwned(Process process, ref bool usedKill)
        {
            try
            {
                if (process.WaitForExit(3000)) return true;
                usedKill = true; process.Kill(); // Only this retained owned instance, never a name search.
                return process.WaitForExit(2000);
            }
            catch (InvalidOperationException) { return true; }
            catch { return false; }
        }
        private static void RequestExit(Process process)
        {
            try { if (process != null && !process.HasExited) { process.StandardInput.WriteLine("exit"); process.StandardInput.Flush(); process.StandardInput.Close(); } }
            catch { }
        }

        private sealed class OwnedLeaf : IDisposable
        {
            private Process process;
            private Task<string> errors;
            private ProcessPriorityClass priority;
            private bool stopped;
            internal GameProcessSnapshot Identity;
            internal bool Exited, CleanupUsedKill, CleanExit;
            internal static OwnedLeaf Start(string path)
            {
                var leaf = new OwnedLeaf();
                try
                {
                    ValidateFixturePath(path);
                    leaf.process = Process.Start(new ProcessStartInfo(path, "--leaf") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(path) });
                    Check(leaf.process != null && leaf.process.Handle != IntPtr.Zero, "Could not start/pin leaf.");
                    leaf.errors = leaf.process.StandardError.ReadToEndAsync();
                    Task<string> ready = leaf.process.StandardOutput.ReadLineAsync();
                    Check(ready.Wait(7000) && ready.Result == "READY|" + leaf.process.Id, "Bad leaf READY.");
                    Check(GameSessionDetector.TryCaptureProcessIdentity(leaf.process.Id, session, out leaf.Identity) && SamePath(leaf.Identity.Path, path)
                        && leaf.Identity.ParentPid == selfPid, "Unexpected leaf identity.");
                    leaf.priority = leaf.process.PriorityClass;
                    return leaf;
                }
                catch { leaf.Dispose(); throw; }
            }
            internal bool PriorityUnchanged() { process.Refresh(); return !process.HasExited && process.PriorityClass == priority; }
            internal void Stop()
            {
                if (stopped) return; stopped = true;
                RequestExit(process);
                if (process != null) { Exited = WaitOrKillOwned(process, ref CleanupUsedKill); CleanExit = Exited && process.ExitCode == 0; }
                if (errors != null && errors.Wait(1000) && !string.IsNullOrWhiteSpace(errors.Result)) { CleanExit = false; Console.Error.WriteLine("Leaf stderr: " + errors.Result); }
            }
            public void Dispose() { Stop(); if (process != null) process.Dispose(); }
        }

        private sealed class OwnedPair : IDisposable
        {
            private Process launcher, renderer;
            private Task<string> errors;
            private ProcessPriorityClass launcherPriority, rendererPriority;
            private bool stopped;
            internal GameProcessSnapshot Launcher, Renderer;
            internal bool LauncherExited, RendererExited, CleanupUsedKill, CleanExit;
            internal static OwnedPair Start(string launcherPath, string rendererPath)
            {
                var pair = new OwnedPair();
                try
                {
                    ValidateFixturePath(launcherPath); ValidateFixturePath(rendererPath);
                    pair.launcher = Process.Start(new ProcessStartInfo(launcherPath, "--root --renderer \"" + rendererPath + "\"") { UseShellExecute = false,
                        CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true,
                        RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(launcherPath) });
                    Check(pair.launcher != null && pair.launcher.Handle != IntPtr.Zero, "Launcher start/pin failed.");
                    pair.errors = pair.launcher.StandardError.ReadToEndAsync();
                    Task<string> ready = pair.launcher.StandardOutput.ReadLineAsync();
                    Check(ready.Wait(7000), "Launcher READY timeout.");
                    string[] parts = (ready.Result ?? "").Split('|');
                    Check(parts.Length == 3 && parts[0] == "READY" && int.Parse(parts[1]) == pair.launcher.Id, "Bad root READY.");
                    int child = int.Parse(parts[2]);
                    Check(GameSessionDetector.TryCaptureProcessIdentity(pair.launcher.Id, session, out pair.Launcher), "Launcher identity unavailable.");
                    Check(GameSessionDetector.TryCaptureProcessIdentity(child, session, out pair.Renderer), "Renderer identity unavailable.");
                    Check(pair.Renderer.ParentPid == pair.Launcher.Pid && pair.Renderer.Creation >= pair.Launcher.Creation, "Not a real parent-child pair.");
                    Check(SamePath(pair.Launcher.Path, launcherPath) && SamePath(pair.Renderer.Path, rendererPath), "Unexpected helper image.");
                    Process pinned = null;
                    try
                    {
                        pinned = Process.GetProcessById(child); // Only this verified owned child.
                        Check(pinned.Handle != IntPtr.Zero && pinned.StartTime.ToUniversalTime().ToFileTimeUtc() == pair.Renderer.Creation, "Child changed before pinning.");
                        pair.renderer = pinned; pinned = null; // Cleanup authority only after pinned identity validation.
                    }
                    finally { if (pinned != null) pinned.Dispose(); }
                    pair.launcherPriority = pair.launcher.PriorityClass; pair.rendererPriority = pair.renderer.PriorityClass;
                    return pair;
                }
                catch { pair.Dispose(); throw; }
            }
            internal Dictionary<string, object> Describe()
            { return Detail("LauncherPid", Launcher.Pid, "RendererPid", Renderer.Pid, "LauncherCreation", Launcher.Creation, "RendererCreation", Renderer.Creation,
                "RendererParentPid", Renderer.ParentPid, "LauncherPath", Launcher.Path, "RendererPath", Renderer.Path,
                "LauncherPriorityAtStart", launcherPriority.ToString(), "RendererPriorityAtStart", rendererPriority.ToString()); }
            internal bool PrioritiesUnchanged()
            {
                launcher.Refresh(); renderer.Refresh();
                return !launcher.HasExited && !renderer.HasExited && launcher.PriorityClass == launcherPriority && renderer.PriorityClass == rendererPriority;
            }
            internal void Stop()
            {
                if (stopped) return; stopped = true;
                RequestExit(launcher);
                if (launcher != null) LauncherExited = WaitOrKillOwned(launcher, ref CleanupUsedKill);
                if (renderer != null) RendererExited = WaitOrKillOwned(renderer, ref CleanupUsedKill);
                CleanExit = LauncherExited && RendererExited && launcher.ExitCode == 0 && renderer.ExitCode == 0;
                if (errors != null && errors.Wait(1000) && !string.IsNullOrWhiteSpace(errors.Result)) { CleanExit = false; Console.Error.WriteLine("Pair stderr: " + errors.Result); }
            }
            public void Dispose() { Stop(); if (renderer != null) renderer.Dispose(); if (launcher != null) launcher.Dispose(); }
        }
    }

#if PAVISE_RENDERER_BENCH
    // 只给显式的聚焦测试白名单提供最小支持
    // 这不是完整的自测运行时 也派发不了应用的运行模式
    internal static partial class SelfTests
    {
        private static void Eq<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException("Expected " + expected + ", actual " + actual);
        }
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Isolated renderer bench cannot start application runtime"); }
    }
#endif
}
