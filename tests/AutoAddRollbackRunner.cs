#if PAVISE_SELFTEST && PAVISE_AUTOADD_ROLLBACK
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static class AutoAddRollbackRunner
    {
        private static int assertions, suites, failures, performanceCalls;
        private static string output;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static int Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 2 || (args.Length == 2 && args[1] != "--native-gpu")) return 2;
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 45000, Timeout.Infinite))
            {
                try
                {
                    output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
                    Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                    Paths.Data = Path.Combine(output, "isolated-data"); Directory.CreateDirectory(Paths.Data);
                    PerformanceActionBackend.ReadForTest = delegate { Interlocked.Increment(ref performanceCalls); throw new InvalidOperationException("Unexpected tuning read"); };
                    PerformanceActionBackend.WriteForTest = delegate { Interlocked.Increment(ref performanceCalls); throw new InvalidOperationException("Unexpected tuning write"); };
                    using (GameInstallScope.UseSnapshotForTest(new GameInstallRecord[0], new string[0]))
                    {
                        Run("ordinary DeltaForce and Left4Dead2 fixtures", OrdinaryGames);
                        Run("Minecraft Java hosts retain fullscreen GPU admission and reload", MinecraftJavaGames);
                        Run("Minecraft launcher profiles learn and re-elect Java renderers", MinecraftJavaRendererLearning);
                        Run("ordinary Java still needs a visible foreground and sufficient GPU evidence", MinecraftJavaEvidenceRequired);
                        Run("Java host removal stays ignored until explicit unignore", MinecraftJavaIgnore);
                        Run("busy and unreadable identities use a five-second check gate", EarlyGate);
                        Run("failed evidence backs off thirty sixty one-twenty and three-hundred seconds", BoundedReject);
                        Run("SCUM startup low activity retries after thirty seconds", ScumStartup);
                        Run("toggle cancels old work and new identities release old failures", ToggleAndReuse);
                        Run("strong fullscreen activity survives higher background GPU activity", Dominance);
                        Run("weak fullscreen activity needs two separate same-identity bursts", WeakActivity);
                        Run("ordinary foreground windows need two separate activity bursts", WindowedActivity);
                        Run("partial confirmation expires and fails closed after missing evidence", ConfirmationExpiry);
                        Run("different process lifetimes cannot share confirmation", ConfirmationIdentity);
                        Run("invalid activity values never qualify", InvalidActivity);
                        Run("post-sample foreground and cancellation checks", CompletionChecks);
                        Run("post-sample PID creation and path must match captured identity", IdentityRevalidation);
                        Run("same-session system snapshots recover inaccessible process handles", SnapshotIdentityFallback);
                        Run("invalid and changed snapshot identities fail closed", SnapshotIdentityRejected);
                        Run("policy synchronization preserves saved and unsaved off preferences", AutoAddPreference);
                        Run("enhanced fullscreen geometry does not accept ordinary caption windows", WindowGeometry);
                        Run("permanent ignore survives toggle and reload", PermanentIgnore);
                        Run("manual removal during a GPU burst cannot revive the game", ManualRemovalDuringSample);
                        Run("manual addition during a GPU burst keeps its user-defined entry", ManualAdditionDuringSample);
                        Run("reject cache stays bounded and expires", RejectCache);
                        Run("optional renderer budget cannot strand auto-add shared gate", RendererGate);
                        Run("new non-game filter is retained", NonGameFilter);
                    }
                    Check(performanceCalls == 0, "A tuning backend was invoked");
                    if (args.Length == 2) NativeGpuSmoke();
                    Console.WriteLine("RESULT suites=" + suites + " failed=" + failures + " assertions=" + assertions
                        + " settings=transient data=temp application_started=false system_tuning_calls=" + performanceCalls
                        + " native_gpu_bursts=" + (args.Length == 2 ? 1 : 0));
                    return failures == 0 ? 0 : 1;
                }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
                finally
                {
                    PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null;
                    Logger.WritesEnabled = false; Settings.UseTransientStoreForCurrentProcess();
                }
            }
        }
        private static void Run(string name, Action action)
        {
            suites++;
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                failures++; Console.Error.WriteLine("FAIL " + name + ": " + error);
            }
        }
        private static void Check(bool good, string message)
        { assertions++; if (!good) throw new InvalidOperationException(message); }
        private static object Get(GameMode mode, string field)
        { return typeof(GameMode).GetField(field, Hidden).GetValue(mode); }
        private static void Set(GameMode mode, string field, object value)
        { typeof(GameMode).GetField(field, Hidden).SetValue(mode, value); }
        private static Dictionary<string, long> Rejects(GameMode mode)
        { return (Dictionary<string, long>)Get(mode, "autoAddRejectUntil"); }
        private static IDictionary Attempts(GameMode mode)
        { return (IDictionary)Get(mode, "autoAddAttempts"); }
        private static bool Ignored(GameMode mode, string path)
        { return ((HashSet<string>)Get(mode, "autoAddIgnore")).Contains(path); }
        private static void AssertCommitted(Fixture f)
        {
            var profiles = f.Mode.GetProfiles();
            Check(profiles.Count == 1 && string.Equals(profiles[0].ExecutablePath, f.GamePath, StringComparison.OrdinalIgnoreCase), "Passing evidence did not add the captured game");
            var store = new GameProfileStore(f.Data); var loaded = store.LoadProfiles();
            Check(!store.SaveFailed && loaded.Count == 1 && string.Equals(loaded[0].ExecutablePath, f.GamePath, StringComparison.OrdinalIgnoreCase), "Committed game did not survive reload");
            Check(Get(f.Mode, "worker") == null && !f.Mode.IsActive, "Admission started runtime optimization");
        }
        private static void OrdinaryGames()
        {
            foreach (string image in new[] { "DeltaForceClient-Win64-Shipping", "left4dead2", "deltaforce" })
                using (var f = new Fixture("ordinary-" + image, image))
                {
                    Check(f.Mode.AutoAddFullscreen, "Automation did not enable Auto-add");
                    Check(GameSessionDetector.IsLibraryCandidate(image, f.GamePath, @"C:\Windows\"), "Known game name was vetoed");
                    f.TryAndDrain(); Check(f.GpuCalls == 1, "Necessary GPU admission burst did not run"); AssertCommitted(f);
                    f.Advance(31); f.TryAndDrain(); Check(f.GpuCalls == 1, "Already registered game sampled again");
                }
        }
        private static void MinecraftJavaGames()
        {
            foreach (string image in new[] { "java", "javaw", "java-17", "javaw21" })
                using (var f = new Fixture("minecraft-java-" + image, image))
                {
                    Check(f.Mode.AutoAddFullscreen, "Automation did not enable Java host admission");
                    Check(WhitelistRule.IsUnsafeFamilyAnchor(f.GamePath), "Java host became an application-family whitelist anchor");
                    Check(GameSessionDetector.IsLibraryCandidate(image, f.GamePath, @"C:\Windows\"), "Java host was statically denied before fullscreen/GPU evidence");
                    f.TryAndDrain();
                    Check(f.GpuCalls == 1, "Java host did not use the original GPU admission burst");
                    AssertCommitted(f);
                    GameDetection selected = GameSessionDetector.DetectSnapshot(new[] { f.Live }, f.Mode.GetProfiles());
                    Check(selected != null && selected.RendererPid == f.Live.Pid && selected.RendererCreation == f.Live.Creation,
                        "Registered Java host did not elect the expected renderer identity");
                    Check(selected.RendererCandidateSelected && !selected.RequiresGpuConfirm,
                        "Registered Java host remained pending GPU confirmation");
                    f.Advance(31); f.TryAndDrain();
                    Check(f.GpuCalls == 1, "Registered Java host was sampled again");
                }
        }
        private static void MinecraftJavaRendererLearning()
        {
            foreach (string image in new[] { "java", "javaw" })
                using (var f = new Fixture("minecraft-java-learning-" + image, image))
                {
                    string launcher = Path.Combine(Path.GetDirectoryName(f.GamePath), "MinecraftLauncher.exe");
                    File.Copy(f.GamePath, launcher, true);
                    Check(f.Mode.AddGameExecutable("Minecraft Java fixture", launcher), "Could not add the inert launcher profile");
                    GameProfile original = f.Mode.GetProfiles()[0];
                    GameDetection renderer = GameSessionDetector.DetectSnapshot(new[] { f.Live }, new[] { original });
                    Check(renderer != null && renderer.RendererPid == f.Live.Pid && renderer.RendererCreation == f.Live.Creation,
                        "Launcher profile did not detect its Java renderer");
                    Check(renderer.RendererCandidateSelected && renderer.RendererLearnable && !renderer.RequiresGpuConfirm,
                        "Java renderer was not eligible for the existing confirmed-renderer learning path");
                    Check(f.Mode.TryLearnConfirmedRenderer(renderer), "Confirmed Java renderer was rejected by renderer learning");
                    AssertCommitted(f);
                    var stored = new GameProfileStore(f.Data).LoadProfiles();
                    GameProfile learned = stored[0];
                    Check(learned.Id == original.Id && learned.Name == original.Name,
                        "Java renderer learning replaced the launcher's profile identity or user name");
                    Check(learned.LearnedExecutablePath == null && learned.Entries.Count == 1 && learned.Entries.Contains(image),
                        "Java renderer learning retained the launcher or an old learned alias");
                    f.Live.FullscreenLike = false;
                    GameDetection selected = GameSessionDetector.DetectSnapshot(new[] { f.Live }, stored);
                    Check(selected != null && selected.RendererPid == f.Live.Pid && selected.RendererCreation == f.Live.Creation,
                        "Reloaded Java renderer target did not elect the expected windowed identity");
                    Check(selected.RendererCandidateSelected && !selected.RequiresGpuConfirm,
                        "Reloaded Java renderer target unnecessarily awaited GPU confirmation");
                    Check(f.GpuCalls == 0, "Renderer learning started automatic admission GPU sampling");
                }
        }
        private static void MinecraftJavaEvidenceRequired()
        {
            foreach (string image in new[] { "java", "javaw" })
            {
                using (var f = new Fixture("ordinary-java-windowed-" + image, image))
                {
                    f.Fullscreen = false; f.Live.FullscreenLike = false;
                    f.TryAndDrain();
                    Check(f.Mode.GetProfiles().Count == 0 && f.GpuCalls == 0 && f.IdentityCalls == 0,
                        "Windowed Java entered the library or started admission sampling");
                    Check((long)Get(f.Mode, "autoAddGateTicks") == 0, "Windowed Java consumed the fullscreen admission gate");
                }
                using (var f = new Fixture("ordinary-java-low-gpu-" + image, image))
                {
                    f.Result = f.Evidence(9.9); f.TryAndDrain();
                    Check(f.Mode.GetProfiles().Count == 0 && f.GpuCalls == 1,
                        "Java host bypassed the minimum GPU evidence threshold");
                    Check(Rejects(f.Mode).ContainsKey(f.GamePath), "Low-GPU Java did not retain the path rejection cooldown");
                    Check(new GameProfileStore(f.Data).LoadProfiles().Count == 0, "Rejected Java host was persisted to the library");
                }
            }
        }
        private static void MinecraftJavaIgnore()
        {
            foreach (string image in new[] { "java", "javaw" })
                using (var f = new Fixture("minecraft-java-ignore-" + image, image))
                {
                    f.TryAndDrain(); AssertCommitted(f);
                    f.Mode.RemoveProfile(f.Mode.GetProfiles()[0].Id);
                    Check(Ignored(f.Mode, f.GamePath) && f.Mode.GetProfiles().Count == 0,
                        "Removing a Java host did not persist the automatic admission ignore");
                    var loaded = new GameMode(f.Data, new SuppressionCore()); Set(loaded, "stopping", true);
                    Check(loaded.GetProfiles().Count == 0 && Ignored(loaded, f.GamePath),
                        "Removed Java host or its ignore changed after reload");
                    f.Mode.AutoAddFullscreen = false; f.Mode.AutoAddFullscreen = true;
                    f.Advance(31); f.TryAndDrain();
                    Check(f.GpuCalls == 1 && f.Mode.GetProfiles().Count == 0,
                        "Java host toggle/retry bypassed permanent ignore");
                    Check(f.Mode.RemoveAutoAddIgnoredPath(f.GamePath), "Explicit Java host unignore failed");
                    Check(!Ignored(f.Mode, f.GamePath), "Explicit Java host unignore kept its path ignored");
                    f.TryAndDrain();
                    Check(f.GpuCalls == 2, "Explicit Java host unignore did not allow another admission burst");
                    AssertCommitted(f);
                }
        }
        private static void EarlyGate()
        {
            foreach (bool busy in new[] { false, true })
                using (var f = new Fixture("gate-" + busy, "left4dead2"))
                {
                    if (busy) Set(f.Mode, "rendererGpuSamplingBusy", 1); else f.IdentityAvailable = false;
                    long before = f.Clock; f.Try();
                    Check(f.GpuCalls == 0 && (long)Get(f.Mode, "autoAddGateTicks") == before + 5 * TimeSpan.TicksPerSecond, "Busy/unreadable identity consumed a full GPU burst interval");
                    Set(f.Mode, "rendererGpuSamplingBusy", 0); f.IdentityAvailable = true;
                    f.Advance(4); f.TryAndDrain(); Check(f.GpuCalls == 0, "Cheap check cooldown was bypassed");
                    f.Advance(1); f.TryAndDrain(); AssertCommitted(f);
                    Check(f.GpuCalls == 1 && (long)Get(f.Mode, "autoAddGateTicks") == f.Clock + 30 * TimeSpan.TicksPerSecond,
                        "Successful retry did not consume exactly one normal burst interval");
                }
            using (var f = new Fixture("windowed", "left4dead2"))
            {
                f.Fullscreen = false; f.TryAndDrain();
                Check((long)Get(f.Mode, "autoAddGateTicks") == 0 && f.GpuCalls == 0 && f.IdentityCalls == 0, "Absent foreground window consumed admission work");
            }
        }
        private static void BoundedReject()
        {
            foreach (bool unavailable in new[] { false, true })
                using (var f = new Fixture("backoff-" + unavailable, "left4dead2"))
                {
                    f.Result = unavailable ? null : f.Evidence(2);
                    int expectedBursts = 0;
                    foreach (int interval in new[] { 30, 60, 120, 300, 300 })
                    {
                        long before = f.Clock; f.TryAndDrain(); expectedBursts++;
                        Check(f.GpuCalls == expectedBursts && f.Mode.GetProfiles().Count == 0,
                            "Missing/low evidence admitted a target or skipped the scheduled burst");
                        Check(Rejects(f.Mode)[f.GamePath] == before + interval * TimeSpan.TicksPerSecond,
                            "Same process failure did not use the expected bounded backoff: " + interval);
                        for (int second = 1; second < interval; second++)
                        {
                            f.Advance(1); f.TryAndDrain();
                            Check(f.GpuCalls == expectedBursts, "Repeated foreground checks caused uncontrolled GPU sampling");
                        }
                        f.Advance(1);
                    }
                    f.Result = f.Evidence(40); f.TryAndDrain();
                    Check(f.GpuCalls == expectedBursts + 1, "Evidence was not retried when bounded backoff expired"); AssertCommitted(f);
                }
        }
        private static void ScumStartup()
        {
            using (var f = new Fixture("scum-startup", "SCUM-Win64-Shipping", @"SCUM\SCUM\Binaries\Win64"))
            {
                Check(GameSessionDetector.IsLibraryCandidate(f.Live.Name, f.GamePath, @"C:\Windows\"), "SCUM renderer path was vetoed");
                f.Result = f.Evidence(3); f.TryAndDrain();
                Check(f.GpuCalls == 1 && f.Mode.GetProfiles().Count == 0, "SCUM loading activity was accepted");
                f.Result = f.Evidence(42);
                for (int second = 1; second < 30; second++)
                { f.Advance(1); f.TryAndDrain(); Check(f.GpuCalls == 1, "SCUM retry burst ran before thirty seconds"); }
                f.Advance(1); f.TryAndDrain();
                Check(f.GpuCalls == 2, "SCUM stayed blocked by a ten-minute startup rejection"); AssertCommitted(f);
                f.Advance(300); f.TryAndDrain(); Check(f.GpuCalls == 2, "Registered SCUM continued admission sampling");
            }
        }
        private static void ToggleAndReuse()
        {
            using (var f = new Fixture("toggle-inflight", "DeltaForceClient-Win64-Shipping"))
            {
                int epoch = (int)Get(f.Mode, "autoAddEpoch");
                f.SampleAction = delegate { f.Mode.AutoAddFullscreen = false; f.Mode.AutoAddFullscreen = true; };
                f.TryAndDrain();
                Check(f.Mode.GetProfiles().Count == 0 && f.GpuCalls == 1, "An old completion survived off/on cancellation");
                Check((int)Get(f.Mode, "autoAddEpoch") != epoch, "Toggle did not invalidate the sampling epoch");
                Check(Settings.Load("GmAutoAdd", false), "Switch was not persisted");
                f.SampleAction = null; f.Advance(30); f.TryAndDrain(); AssertCommitted(f);
            }
            foreach (bool reusedPid in new[] { false, true })
                using (var f = new Fixture("new-lifetime-" + reusedPid, "SCUM-Win64-Shipping"))
                {
                    f.Result = null; f.TryAndDrain();
                    f.Advance(30); f.TryAndDrain();
                    Check(Rejects(f.Mode)[f.GamePath] == f.Clock + 60 * TimeSpan.TicksPerSecond, "Second failure did not retain process identity");
                    f.Live.Creation++; if (!reusedPid) f.Live.Pid++;
                    f.Result = f.Evidence(40); f.Advance(30); f.TryAndDrain();
                    Check(f.GpuCalls == 3, "A new PID/lifetime inherited the previous process's failure cache"); AssertCommitted(f);
                }
        }
        private static void Dominance()
        {
            using (var f = new Fixture("threshold", "left4dead2"))
            { f.Result = f.Evidence(20); f.TryAndDrain(); AssertCommitted(f); }
            using (var f = new Fixture("background-higher", "SCUM-Win64-Shipping"))
            { f.Result = f.Evidence(25); f.Result[780002] = 95; f.TryAndDrain(); AssertCommitted(f); }
            using (var f = new Fixture("missing-pid", "left4dead2"))
            { f.Result = new Dictionary<int, double>(); f.TryAndDrain(); Check(f.Mode.GetProfiles().Count == 0 && Rejects(f.Mode).Count == 1, "Missing PID did not reject"); }
        }
        private static void WeakActivity()
        {
            foreach (double activity in new[] { 10.0, 19.9 })
                using (var f = new Fixture("weak-" + activity.ToString(System.Globalization.CultureInfo.InvariantCulture), "SCUM-Win64-Shipping"))
                {
                    f.Result = f.Evidence(activity); f.TryAndDrain();
                    Check(f.GpuCalls == 1 && f.Mode.GetProfiles().Count == 0, "One weak fullscreen burst was treated as complete evidence");
                    f.Advance(29); f.TryAndDrain(); Check(f.GpuCalls == 1, "Weak confirmation bypassed the burst interval");
                    f.Advance(1); f.TryAndDrain(); Check(f.GpuCalls == 2, "Second weak confirmation did not run"); AssertCommitted(f);
                    f.Advance(300); f.TryAndDrain(); Check(f.GpuCalls == 2, "Confirmed weak target was sampled repeatedly");
                }
        }
        private static void WindowedActivity()
        {
            foreach (double activity in new[] { 10.0, 40.0 })
                using (var f = new Fixture("windowed-activity-" + activity, "SCUM-Win64-Shipping"))
                {
                    f.Windowed = true; f.Live.FullscreenLike = false; f.Result = f.Evidence(activity);
                    f.TryAndDrain();
                    Check(f.GpuCalls == 1 && f.Mode.GetProfiles().Count == 0, "A single ordinary-window burst admitted the program");
                    f.Advance(30); f.TryAndDrain(); Check(f.GpuCalls == 2, "Ordinary window did not receive bounded confirmation"); AssertCommitted(f);
                }
        }
        private static void ConfirmationExpiry()
        {
            using (var f = new Fixture("confirmation-expired", "SCUM-Win64-Shipping"))
            {
                f.Result = f.Evidence(15); f.TryAndDrain();
                f.Advance(121); f.TryAndDrain();
                Check(f.GpuCalls == 2 && f.Mode.GetProfiles().Count == 0, "Expired weak evidence admitted a target");
                f.Advance(30); f.TryAndDrain(); Check(f.GpuCalls == 3, "Fresh confirmation did not qualify"); AssertCommitted(f);
            }
            foreach (string failure in new[] { "unavailable", "low", "exception" })
                using (var f = new Fixture("confirmation-reset-" + failure, "SCUM-Win64-Shipping"))
                {
                    f.Result = f.Evidence(15); f.TryAndDrain();
                    if (failure == "exception") f.SampleAction = delegate { throw new InvalidOperationException("Inert capture failure"); };
                    else f.Result = failure == "unavailable" ? null : f.Evidence(5);
                    f.Advance(30); f.TryAndDrain();
                    Check(f.GpuCalls == 2 && f.Mode.GetProfiles().Count == 0, "Failure after partial confirmation admitted a target");
                    f.SampleAction = null; f.Result = f.Evidence(15); f.Advance(30); f.TryAndDrain();
                    Check(f.GpuCalls == 3 && f.Mode.GetProfiles().Count == 0, "A failure did not invalidate previous partial evidence");
                    f.Advance(30); f.TryAndDrain(); Check(f.GpuCalls == 4, "Fresh proof pair did not qualify after recovery"); AssertCommitted(f);
                }
        }
        private static void ConfirmationIdentity()
        {
            foreach (bool reusedPid in new[] { false, true })
                using (var f = new Fixture("confirmation-new-identity-" + reusedPid, "SCUM-Win64-Shipping"))
                {
                    f.Result = f.Evidence(15); f.TryAndDrain();
                    f.Live.Creation++; if (!reusedPid) f.Live.Pid++;
                    f.Result = f.Evidence(15); f.Advance(30); f.TryAndDrain();
                    Check(f.GpuCalls == 2 && f.Mode.GetProfiles().Count == 0, "Different process lifetimes shared a confirmation pair");
                    f.Advance(30); f.TryAndDrain(); Check(f.GpuCalls == 3, "New process proof pair did not qualify"); AssertCommitted(f);
                }
        }
        private static void InvalidActivity()
        {
            foreach (double activity in new[] { -1.0, 0.0, 9.99, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                using (var f = new Fixture("invalid-" + activity.ToString(System.Globalization.CultureInfo.InvariantCulture), "SCUM-Win64-Shipping"))
                {
                    f.Result = f.Evidence(activity); f.TryAndDrain();
                    Check(f.GpuCalls == 1 && f.Mode.GetProfiles().Count == 0 && Rejects(f.Mode).ContainsKey(f.GamePath),
                        "Invalid or subthreshold GPU data was accepted: " + activity);
                    Check(new GameProfileStore(f.Data).LoadProfiles().Count == 0, "Invalid activity was persisted");
                }
            using (var f = new Fixture("invalid-null", "SCUM-Win64-Shipping"))
            { f.Result = null; f.TryAndDrain(); Check(f.Mode.GetProfiles().Count == 0 && Rejects(f.Mode).ContainsKey(f.GamePath), "Unavailable GPU data was admitted"); }
        }
        private static void CompletionChecks()
        {
            foreach (string state in new[] { "foreground", "active", "disabled", "panic", "stopping", "throw" })
                using (var f = new Fixture("completion-" + state, "left4dead2"))
                {
                    f.SampleAction = delegate
                    {
                        if (state == "foreground") f.Fullscreen = false;
                        else if (state == "active") Set(f.Mode, "active", true);
                        else if (state == "disabled") f.Mode.AutoAddFullscreen = false;
                        else if (state == "panic") Set(f.Mode, "panicReq", true);
                        else if (state == "stopping") Set(f.Mode, "stopping", true);
                        else throw new InvalidOperationException("Fake PDH exception");
                    };
                    f.TryAndDrain();
                    Check(f.Mode.GetProfiles().Count == 0 && (int)Get(f.Mode, "rendererGpuSamplingBusy") == 0, "Completion state admitted or stranded sampling gate: " + state);
                }
        }
        private static void IdentityRevalidation()
        {
            foreach (string change in new[] { "pid", "creation", "path", "unreadable" })
                using (var f = new Fixture("identity-revalidation-" + change, "SCUM-Win64-Shipping"))
                {
                    f.SampleAction = delegate
                    {
                        if (change == "pid") f.Live.Pid++;
                        else if (change == "creation") f.Live.Creation++;
                        else if (change == "path") f.Live.Path = Path.Combine(Path.GetDirectoryName(f.GamePath), "DifferentRenderer.exe");
                        else f.IdentityAvailable = false;
                    };
                    f.TryAndDrain();
                    Check(f.GpuCalls == 1 && f.Mode.GetProfiles().Count == 0, "A changed post-sample process identity was committed: " + change);
                    Check(new GameProfileStore(f.Data).LoadProfiles().Count == 0, "Stale captured executable was persisted: " + change);
                    if (change != "pid") Check(f.IdentityCalls == 2, "Identity was not checked again after GPU capture");
                }
        }
        private static ProcEntry SnapshotEntry(Fixture f, int session)
        {
            return new ProcEntry { Pid = f.Live.Pid, ParentPid = f.Live.ParentPid, Creation = f.Live.Creation,
                Path = f.Live.Path, Name = "UntrustedSnapshotName", Session = session };
        }
        private static void SnapshotIdentityFallback()
        {
            using (var f = new Fixture("snapshot-valid", "SCUM-Win64-Shipping"))
            {
                int session = (int)Get(f.Mode, "selfSession");
                var correct = SnapshotEntry(f, session);
                var unrelated = new ProcEntry { Pid = f.Live.Pid + 1, Creation = f.Live.Creation,
                    Session = session + 1, Name = "WrongSession", Path = Path.Combine(Path.GetDirectoryName(f.GamePath), "WrongSession.exe") };
                f.UseSnapshotFallback(delegate { return new ProcessSnapshot(new[] { unrelated, correct }); });
                f.TryAndDrain();
                Check(f.GpuCalls == 1 && f.IdentityCalls == 2 && f.SnapshotCalls == 2,
                    "Same-session fallback did not validate both sides of the GPU burst");
                AssertCommitted(f);
                Check(f.Mode.GetProfiles()[0].Entries.Contains(f.Live.Name) && !f.Mode.GetProfiles()[0].Entries.Contains("UntrustedSnapshotName"),
                    "Fallback trusted a snapshot name instead of the verified executable path");
            }
            using (var f = new Fixture("snapshot-not-needed", "SCUM-Win64-Shipping"))
            {
                f.Mode.AutoAddTestSnapshot = delegate
                { Interlocked.Increment(ref f.SnapshotCalls); throw new InvalidOperationException("Normal handle identity must not enumerate processes"); };
                f.TryAndDrain(); AssertCommitted(f);
                Check(f.IdentityCalls == 2 && f.SnapshotCalls == 0, "Readable process identity performed unnecessary fallback captures");
            }
            using (var f = new Fixture("snapshot-post-sample-only", "SCUM-Win64-Shipping"))
            {
                int session = (int)Get(f.Mode, "selfSession");
                f.Mode.AutoAddTestSnapshot = delegate
                { Interlocked.Increment(ref f.SnapshotCalls); return new ProcessSnapshot(new[] { SnapshotEntry(f, session) }); };
                f.SampleAction = delegate { f.IdentityAvailable = false; };
                f.TryAndDrain(); AssertCommitted(f);
                Check(f.IdentityCalls == 2 && f.SnapshotCalls == 1, "Fallback did not run only after the strict post-sample query failed");
            }
        }
        private static void SnapshotIdentityRejected()
        {
            foreach (string invalid in new[] { "session", "pid", "creation", "path", "null" })
                using (var f = new Fixture("snapshot-invalid-" + invalid, "SCUM-Win64-Shipping"))
                {
                    var entry = SnapshotEntry(f, (int)Get(f.Mode, "selfSession"));
                    if (invalid == "session") entry.Session++;
                    else if (invalid == "pid") entry.Pid++;
                    else if (invalid == "creation") entry.Creation = 0;
                    else if (invalid == "path") entry.Path = null;
                    f.UseSnapshotFallback(delegate { return invalid == "null" ? null : new ProcessSnapshot(new[] { entry }); });
                    f.TryAndDrain();
                    Check(f.IdentityCalls == 1 && f.SnapshotCalls == 1 && f.GpuCalls == 0 && f.Mode.GetProfiles().Count == 0,
                        "Invalid snapshot started GPU admission: " + invalid);
                    Check(new GameProfileStore(f.Data).LoadProfiles().Count == 0, "Invalid snapshot identity was persisted: " + invalid);
                    Check((long)Get(f.Mode, "autoAddGateTicks") == f.Clock + 5 * TimeSpan.TicksPerSecond,
                        "Rejected snapshot consumed a normal burst budget: " + invalid);
                }
            foreach (string changed in new[] { "session", "pid", "creation", "path" })
                using (var f = new Fixture("snapshot-post-sample-changed-" + changed, "SCUM-Win64-Shipping"))
                {
                    var entry = SnapshotEntry(f, (int)Get(f.Mode, "selfSession"));
                    f.UseSnapshotFallback(delegate { return new ProcessSnapshot(new[] { entry }); });
                    f.SampleAction = delegate
                    {
                        if (changed == "session") entry.Session++;
                        else if (changed == "pid") entry.Pid++;
                        else if (changed == "creation") entry.Creation++;
                        else entry.Path = Path.Combine(Path.GetDirectoryName(f.GamePath), "DifferentRenderer.exe");
                    };
                    f.TryAndDrain();
                    Check(f.IdentityCalls == 2 && f.SnapshotCalls == 2 && f.GpuCalls == 1 && f.Mode.GetProfiles().Count == 0,
                        "Post-sample fallback accepted a changed identity: " + changed);
                    Check(new GameProfileStore(f.Data).LoadProfiles().Count == 0, "Post-sample stale snapshot was persisted: " + changed);
                }
        }
        private static void AutoAddPreference()
        {
            using (var f = new Fixture("preference-saved-off", "SCUM-Win64-Shipping"))
            {
                f.Mode.AutoAddFullscreen = false;
                Check(!Settings.Load("GmAutoAdd", true) && !(bool)Get(f.Mode, "autoAddPreferenceUnsaved"),
                    "Successful off preference did not persist");
                f.Mode.InitializeAutomation();
                Check(!f.Mode.AutoAddFullscreen, "Automation initialization reopened saved automatic admission");
                foreach (string preset in new[] { "0", "1", "4", "6" })
                {
                    Check(Settings.SaveStr(PolicyCatalog.KeyPreset, preset), "Inert preset preference could not be saved");
                    typeof(GameMode).GetMethod("SyncAutomationPolicy", Hidden).Invoke(f.Mode, null);
                    Check(!f.Mode.AutoAddFullscreen, "Policy synchronization reopened saved automatic admission: " + preset);
                }
                f.TryAndDrain(); Check(f.GpuCalls == 0 && f.IdentityCalls == 0, "Saved off preference started admission sampling");
            }
            using (var f = new Fixture("preference-unsaved-off", "SCUM-Win64-Shipping"))
            {
                Check(Settings.Save("GmAutoAdd", true), "Could not establish the old stored on preference");
                Settings.SuspendWritesForReset();
                try
                {
                    f.Mode.AutoAddFullscreen = false;
                    Check(!f.Mode.AutoAddFullscreen && (bool)Get(f.Mode, "autoAddPreferenceUnsaved"),
                        "Failed save discarded the user's current off intention");
                    Check(Settings.Load("GmAutoAdd", false), "The rejected test write unexpectedly changed the stored value");
                    for (int pass = 0; pass < 3; pass++)
                    {
                        typeof(GameMode).GetMethod("SyncAutomationPolicy", Hidden).Invoke(f.Mode, null);
                        Check(!f.Mode.AutoAddFullscreen, "Policy synchronization replaced the unsaved off intention");
                    }
                    f.TryAndDrain(); Check(f.GpuCalls == 0 && f.IdentityCalls == 0, "Unsaved off intention started admission sampling");
                }
                finally { Settings.UseTransientStoreForCurrentProcess(); }
            }
        }
        private static GameSessionDetector.NativeRect Rect(int left, int top, int right, int bottom)
        { return new GameSessionDetector.NativeRect { Left = left, Top = top, Right = right, Bottom = bottom }; }
        private static void WindowGeometry()
        {
            var monitor = Rect(0, 0, 1920, 1080); var work = Rect(0, 0, 1920, 1032);
            Check(GameSessionDetector.IsFullscreenLikeGeometry(false, monitor, monitor, monitor, work), "Borderless monitor was missed");
            Check(GameSessionDetector.IsFullscreenLikeGeometry(false, work, work, monitor, work), "Borderless work area was missed");
            Check(!GameSessionDetector.RectCoversMonitor(work, monitor), "Work-area enhancement changed the old monitor coverage threshold");
            Check(GameSessionDetector.IsFullscreenLikeGeometry(true, monitor, monitor, monitor, work), "Residual caption flag blocked a screen-covering client");
            Check(!GameSessionDetector.IsFullscreenLikeGeometry(true, Rect(-8, -8, 1928, 1040), Rect(0, 31, 1920, 1032), monitor, work), "Maximized caption window became fullscreen");
            Check(!GameSessionDetector.IsFullscreenLikeGeometry(true, Rect(-8, -8, 3848, 2168), Rect(0, 31, 3840, 2160), Rect(0, 0, 3840, 2160), Rect(0, 0, 3840, 2112)), "Four-K caption title bar was hidden by a percentage threshold");
            Check(!GameSessionDetector.IsFullscreenLikeGeometry(false, Rect(20, 20, 1500, 900), Rect(20, 20, 1500, 900), monitor, work), "Ordinary window became fullscreen");
            var negative = Rect(-1920, -1080, 0, 0);
            Check(GameSessionDetector.IsFullscreenLikeGeometry(false, negative, negative, negative, Rect(-1920, -1080, 0, -48)), "Negative-coordinate display was missed");
            Check(!GameSessionDetector.IsFullscreenLikeGeometry(false, work, Rect(0, 0, 0, 0), monitor, work), "Empty client area qualified");
            Check(!GameSessionDetector.IsFullscreenLikeGeometry(false, work, work, monitor, Rect(-10, 0, 1920, 1032)), "Invalid work-area bounds qualified");
            Check(GameSessionDetector.IsFullscreenLikeGeometry(false, Rect(4, 4, 1916, 1028), Rect(4, 4, 1916, 1028), monitor, work), "Work-area edge tolerance was lost");
            Check(!GameSessionDetector.IsFullscreenLikeGeometry(false, Rect(5, 5, 1915, 1027), Rect(5, 5, 1915, 1027), monitor, work), "Loose work-area edge match qualified");
            Check(!GameSessionDetector.RectCoversMonitor(Rect(1, 1, 0, 0), Rect(1, 1, 0, 0)), "Reversed rectangles qualified");
            var extremes = Rect(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue);
            Check(GameSessionDetector.RectCoversMonitor(extremes, extremes), "Coordinate subtraction overflowed");
        }
        private static void PermanentIgnore()
        {
            using (var f = new Fixture("ignore", "left4dead2"))
            {
                f.TryAndDrain(); AssertCommitted(f); f.Mode.RemoveProfile(f.Mode.GetProfiles()[0].Id);
                Check(Ignored(f.Mode, f.GamePath) && f.Mode.GetProfiles().Count == 0, "Removal did not permanently ignore the executable");
                f.Mode.AutoAddFullscreen = false; f.Mode.AutoAddFullscreen = true; f.Advance(31); f.TryAndDrain();
                Check(f.GpuCalls == 1 && f.Mode.GetProfiles().Count == 0, "Toggle bypassed permanent ignore");
                var reloaded = new GameMode(f.Data, new SuppressionCore()); Set(reloaded, "stopping", true);
                Check(Ignored(reloaded, f.GamePath), "Permanent ignore did not survive reload");
                Check(f.Mode.AddGameExecutable("Manual", f.GamePath) && !Ignored(f.Mode, f.GamePath), "Manual addition did not unignore"); AssertCommitted(f);
            }
        }
        private static void RejectCache()
        {
            using (var f = new Fixture("reject-cache", "left4dead2"))
            {
                var method = typeof(GameMode).GetMethod("RememberAutoAddReject", Hidden);
                for (int i = 0; i < 64; i++) method.Invoke(f.Mode, new object[] { "old-" + i, f.Clock });
                Check(Rejects(f.Mode).Count == 64 && Attempts(f.Mode).Count == 64, "Failure evidence cache did not retain the bounded set");
                method.Invoke(f.Mode, new object[] { "next", f.Clock });
                Check(Rejects(f.Mode).Count <= 64 && Attempts(f.Mode).Count <= 64 && Rejects(f.Mode).ContainsKey("next"), "Full live failure caches were not bounded");
                Rejects(f.Mode).Clear(); Attempts(f.Mode).Clear();
                for (int i = 0; i < 64; i++) method.Invoke(f.Mode, new object[] { "expired-" + i, f.Clock });
                f.Advance(600); method.Invoke(f.Mode, new object[] { "fresh", f.Clock });
                Check(Rejects(f.Mode).Count == 1 && Attempts(f.Mode).Count <= 64, "Expired reject cache was not pruned or attempts grew without bound");
            }
        }
        private static void ManualRemovalDuringSample()
        {
            using (var f = new Fixture("manual-removal-inflight", "left4dead2"))
            {
                f.SampleAction = delegate
                {
                    Check(f.Mode.AddGameExecutable("User selected", f.GamePath), "User addition during GPU burst failed");
                    f.Mode.RemoveProfile(f.Mode.GetProfiles()[0].Id);
                    Check(Ignored(f.Mode, f.GamePath) && f.Mode.GetProfiles().Count == 0, "User removal did not persist the ignore");
                };
                f.TryAndDrain(); f.SampleAction = null;
                Check(f.GpuCalls == 1 && f.Mode.GetProfiles().Count == 0 && Ignored(f.Mode, f.GamePath), "Old automatic completion revived a user-deleted game");
                var loaded = new GameMode(f.Data, new SuppressionCore()); Set(loaded, "stopping", true);
                Check(loaded.GetProfiles().Count == 0 && Ignored(loaded, f.GamePath), "In-flight admission erased the durable ignore");
                f.Advance(31); f.TryAndDrain(); Check(f.GpuCalls == 1, "Deleted game was resampled");
                Check(f.Mode.RemoveAutoAddIgnoredPath(f.GamePath), "Explicit removal from ignore list failed");
                f.TryAndDrain(); Check(f.GpuCalls == 2, "Explicit unignore did not release the restored gate/cache"); AssertCommitted(f);
            }
        }
        private static void ManualAdditionDuringSample()
        {
            using (var f = new Fixture("manual-addition-inflight", "DeltaForceClient-Win64-Shipping"))
            {
                string selectedId = null;
                f.SampleAction = delegate
                {
                    Check(f.Mode.AddGameExecutable("User selected", f.GamePath), "User addition during GPU burst failed");
                    selectedId = f.Mode.GetProfiles()[0].Id;
                };
                f.TryAndDrain(); AssertCommitted(f);
                Check(f.Mode.GetProfiles()[0].Id == selectedId && f.Mode.GetProfiles()[0].Name == "User selected", "Automatic completion replaced the user's profile");
            }
        }
        private static void RendererGate()
        {
            using (var f = new Fixture("renderer-gate", "left4dead2"))
            {
                Set(f.Mode, "rendererActivityAttempts", 5);
                typeof(GameMode).GetMethod("MaybeObserveRendererActivity", Hidden).Invoke(f.Mode, new object[] { new GameDetection { RendererPid = f.Live.Pid, RendererCandidateSelected = true } });
                Check((int)Get(f.Mode, "rendererGpuSamplingBusy") == 0, "Unknown profile acquired renderer activity gate");
                f.TryAndDrain(); AssertCommitted(f);
            }
        }
        private static void NonGameFilter()
        {
            foreach (string image in new[] { "ChatGPT", "chrome", "GHelper" })
                using (var f = new Fixture("nongame-" + image, image))
                {
                    GamePerformanceTargetPolicy.ResetForTest();
                    GamePerformanceTargetPolicy.MetadataForTest = delegate
                    {
                        return image == "ChatGPT" ? new GamePerformanceTargetPolicy.Metadata { Company = "OpenAI", Product = "ChatGPT" }
                            : image == "chrome" ? new GamePerformanceTargetPolicy.Metadata { Company = "Google LLC", Product = "Google Chrome" } : null;
                    };
                    try
                    {
                        Check(!GameSessionDetector.IsLibraryCandidate(image, f.GamePath, @"C:\Windows\"), "Verified non-game filter was not retained");
                        f.TryAndDrain(); Check(f.GpuCalls == 0 && f.Mode.GetProfiles().Count == 0, "Verified non-game sampled or added");
                    }
                    finally { GamePerformanceTargetPolicy.ResetForTest(); }
                }
        }
        private static void NativeGpuSmoke()
        {
            var elapsed = Stopwatch.StartNew(); GpuSampleDiagnostics diagnostics;
            var values = GpuEvidence.Sample3D(GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs, delegate { return false; }, Process.GetCurrentProcess().Id, out diagnostics);
            elapsed.Stop();
            string report = "readOnly=true bursts=1 rounds=" + GpuEvidence.BurstRounds + " intervalMs=" + GpuEvidence.BurstIntervalMs
                + " elapsedMs=" + elapsed.ElapsedMilliseconds + " result=" + (values == null ? "unavailable" : "available")
                + " pidCount=" + (values == null ? 0 : values.Count) + " " + diagnostics;
            File.WriteAllText(Path.Combine(output, "native-gpu-smoke.txt"), report);
            Console.WriteLine("NATIVE_GPU_SMOKE " + report);
        }
        private sealed class Fixture : IDisposable
        {
            internal readonly GameMode Mode;
            internal readonly string Data, GamePath;
            internal GameProcessSnapshot Live;
            internal Dictionary<int, double> Result;
            internal bool Fullscreen = true, Windowed, IdentityAvailable = true;
            internal Action SampleAction;
            internal int GpuCalls, IdentityCalls, SnapshotCalls;
            internal long Clock = DateTime.UtcNow.Ticks;
            internal Fixture(string label, string image, string relativeDirectory = null)
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                Data = Path.Combine(output, label, "data"); Directory.CreateDirectory(Data);
                string directory = Path.Combine(output, label, "game", relativeDirectory ?? image); Directory.CreateDirectory(directory);
                GamePath = Path.Combine(directory, image + ".exe"); File.Copy(Assembly.GetExecutingAssembly().Location, GamePath, true);
                Live = new GameProcessSnapshot { Pid = 780001, Creation = 10000001, Name = image, Path = GamePath, FullscreenLike = true, Foreground = true, Visible = true };
                Mode = new GameMode(Data, new SuppressionCore()); Mode.InitializeAutomation(); Set(Mode, "enabled", true); Result = Evidence(40);
                Mode.AutoAddTestNow = delegate { return Interlocked.Read(ref Clock); };
                Mode.AutoAddTestForeground = delegate { return Fullscreen ? Live.Pid : 0; };
                Mode.AutoAddTestWindow = delegate
                {
                    return !Fullscreen ? null : new ForegroundGameWindowSnapshot
                    { Pid = Live.Pid, FullscreenLike = !Windowed };
                };
                Mode.AutoAddTestIdentity = delegate(int pid)
                {
                    Interlocked.Increment(ref IdentityCalls);
                    return IdentityAvailable && pid == Live.Pid ? new GameProcessSnapshot { Pid = Live.Pid, Creation = Live.Creation, Name = Live.Name, Path = Live.Path } : null;
                };
                Mode.AutoAddTestGpu = delegate(Func<bool> canceled)
                { Interlocked.Increment(ref GpuCalls); if (SampleAction != null) SampleAction(); return canceled() ? null : Result; };
            }
            internal Dictionary<int, double> Evidence(double value) { return new Dictionary<int, double> { { Live.Pid, value } }; }
            internal void UseSnapshotFallback(Func<ProcessSnapshot> capture)
            {
                IdentityAvailable = false;
                Mode.AutoAddTestSnapshot = delegate { Interlocked.Increment(ref SnapshotCalls); return capture(); };
            }
            internal void Advance(int seconds) { Interlocked.Add(ref Clock, seconds * TimeSpan.TicksPerSecond); }
            internal void Try() { typeof(GameMode).GetMethod("TryAutoAddForegroundGame", Hidden).Invoke(Mode, null); }
            internal void TryAndDrain() { Try(); Drain(); }
            internal void Drain() { Check(SpinWait.SpinUntil(delegate { return (int)Get(Mode, "rendererGpuSamplingBusy") == 0; }, 3000), "Auto-add stranded sampler gate"); }
            public void Dispose()
            {
                Set(Mode, "stopping", true); Drain();
                var observations = (RendererObservationStore)Get(Mode, "rendererObservations");
                if (observations != null) Check(observations.Close(3000), "Temporary observation store did not close");
                Check(Get(Mode, "worker") == null, "Fixture started runtime worker");
            }
        }
    }
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Admission runner cannot start application runtime"); } }
}
#endif
