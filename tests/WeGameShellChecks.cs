#if PAVISE_SELFTEST && PAVISE_WEGAME_SHELL_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    // All service observations and mutations are injected into its test-only
    // synchronous runner. No live process, GPU, window or registry actions.
    internal static class WeGameShellChecks
    {
        private const string Root = @"C:\PaviseFixture\WeGame";
        private static int assertions;
        private static int failures;
        private static int fixtureIds;

        private static int Main()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Logger.WritesEnabled = false;
            Lang.Init();
            using (var isolatedScope = new SyntheticInstallScope())
            {
                Run("starting the service without an observed-renderer notification never cleans", NoSessionNoCleanup);
                Run("an eligible observed renderer triggers once without clicks, windows, load or elapsed time", ImmediateAutomaticAttempt);
                Run("a missing platform root does not clean; later discovery can retry", MissingRootRetry);
                Run("renderer identity is validated before automatic cleanup", InvalidRenderer);
                Run("renderer PID, birth and path changes revoke an in-flight automatic attempt", ChangedRendererBeforeMutation);
                Run("profile switches and session revocation cancel pending cleanup", ChangedSessionBeforeMutation);
                Run("turning the feature off cancels pending cleanup", DisabledBeforeMutation);
                Run("shutdown and disposal cancel pending cleanup", StoppedBeforeMutation);
                Run("cleanup failures and exceptions do not create automatic retry loops", FailureDoesNotLoop);
                Run("a later renderer instance can make its own single attempt", NewRendererInstance);
                Run("inactive notifications and learned-path updates cannot retry the same renderer instance", SameInstanceReceipts);
                Run("the full automatic path retains runtime and anti-cheat families", AutomaticCandidateBoundary);
                Run("candidate PID reuse and changed paths invalidate the fresh mutation boundary", ChangedCandidateBeforeMutation);
                Run("test-only services fail closed when native-operation hooks are absent", MissingHooksFailClosed);
                Run("shell candidates require an exact filename within the verified directory", CandidatePaths);
                Run("helpers require a verified UI ancestor and children precede parents", UiAncestry);
                Run("runtime, downloader and anti-cheat ancestors protect their descendants", RuntimeFamilies);
                Run("ambiguous PIDs, cycles and reversed parent births are rejected", InvalidGraphs);
                Run("ancestor names must agree with their executable paths", AncestorIdentity);
                Run("renderer ownership is tied to the profile executable or a restricted game root", ProfileOwnership);
            }
            Console.WriteLine("RESULT " + assertions + " assertions; " + failures + " failed groups; injected automatic service path and pure candidates only; no live process actions");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action check)
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
        }

        private static void Check(bool condition, string detail)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(detail);
        }

        private sealed class SyntheticInstallScope : IDisposable
        {
            private readonly FieldInfo cache;
            private readonly FieldInfo cacheAt;
            private readonly object previous;
            private readonly object previousAt;
            internal SyntheticInstallScope()
            {
                const BindingFlags fields = BindingFlags.Static | BindingFlags.NonPublic;
                Type snapshotType = typeof(GameInstallScope).GetNestedType("Snapshot", BindingFlags.NonPublic);
                object snapshot = Activator.CreateInstance(snapshotType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { new List<GameInstallRecord>(), new List<string>(), true }, null);
                cache = typeof(GameInstallScope).GetField("cached", fields);
                cacheAt = typeof(GameInstallScope).GetField("cachedAt", fields);
                previous = cache.GetValue(null); previousAt = cacheAt.GetValue(null);
                cache.SetValue(null, snapshot); cacheAt.SetValue(null, DateTime.UtcNow.Ticks);
            }
            public void Dispose() { cache.SetValue(null, previous); cacheAt.SetValue(null, previousAt); }
        }

        private sealed class AutomaticFixture : IDisposable
        {
            internal readonly WeGameShellService Service;
            internal GameProfile Profile;
            internal GameProcessSnapshot Renderer;
            internal readonly List<GameProcessSnapshot> Candidates = new List<GameProcessSnapshot>();
            internal readonly List<int> Terminated = new List<int>();
            internal string DiscoveredRoot = Root;
            internal bool CaptureComplete = true;
            internal bool TerminationSucceeds = true;
            internal bool ThrowOnTermination;
            internal int ProbeCalls;
            internal int RootCalls;
            internal int CaptureCalls;
            internal int TerminationCalls;
            internal long Clock;
            internal Action<int> BeforeCapture;

            internal AutomaticFixture()
            {
                string id = "automatic-fixture-" + (++fixtureIds);
                Profile = new GameProfile { Id = id, Name = "Fixture game", Root = @"C:\PaviseFixture\WeGameApps\GameAlpha", ExecutablePath = @"C:\PaviseFixture\WeGameApps\GameAlpha\game.exe" };
                Renderer = Entry(1101, 0, 100000, Profile.ExecutablePath);
                Candidates.Add(Entry(201, 0, 1000, "wegame.exe"));
                Service = new WeGameShellService(true);
                Service.ClockForTest = delegate { return Clock; };
                Service.ProbeRendererForTest = delegate(GameProfile profile, int pid, long birth, string path)
                {
                    ProbeCalls++;
                    return Clone(Renderer);
                };
                Service.FindRootForTest = delegate(GameProfile profile) { RootCalls++; return DiscoveredRoot; };
                Service.CaptureCandidatesForTest = delegate(string root)
                {
                    CaptureCalls++;
                    if (BeforeCapture != null) BeforeCapture(CaptureCalls);
                    var entries = new List<GameProcessSnapshot>();
                    foreach (GameProcessSnapshot item in Candidates) entries.Add(Clone(item));
                    return new WeGameShellCapture { Complete = CaptureComplete, Entries = entries };
                };
                Service.TerminateForTest = delegate(GameProcessSnapshot candidate, string root)
                {
                    TerminationCalls++;
                    if (ThrowOnTermination) throw new InvalidOperationException("Injected native failure");
                    if (TerminationSucceeds) Terminated.Add(candidate.Pid);
                    return TerminationSucceeds;
                };
                Service.Start();
            }
            internal void Notify() { Service.NotifySession(Profile, 1101, 100000, true); }
            internal void Step() { Service.RunCycleForTest(); }
            public void Dispose() { Service.Dispose(); }
        }

        private static GameProcessSnapshot Clone(GameProcessSnapshot value)
        {
            if (value == null) return null;
            return new GameProcessSnapshot { Pid = value.Pid, ParentPid = value.ParentPid, Creation = value.Creation, Name = value.Name, Path = value.Path, Foreground = value.Foreground, Visible = value.Visible, FullscreenLike = value.FullscreenLike };
        }

        private static void NoSessionNoCleanup()
        {
            using (var fixture = new AutomaticFixture())
            {
                for (int i = 0; i < 4; i++) { fixture.Clock += TimeSpan.TicksPerHour; fixture.Step(); }
                Check(fixture.RootCalls == 0 && fixture.CaptureCalls == 0 && fixture.TerminationCalls == 0, "The service acted without an observed-renderer notification");
                fixture.Service.NotifySession(fixture.Profile, 1101, 100000, false);
                fixture.Step();
                Check(fixture.TerminationCalls == 0, "An inactive session triggered cleanup");
            }
        }

        private static void ImmediateAutomaticAttempt()
        {
            using (var fixture = new AutomaticFixture())
            {
                Check(!fixture.Renderer.Visible && !fixture.Renderer.Foreground && !fixture.Renderer.FullscreenLike, "The fixture unexpectedly supplied window evidence");
                fixture.Notify(); fixture.Step();
                Check(fixture.Clock == 0, "The test advanced time before the automatic attempt");
                Check(fixture.TerminationCalls == 1 && fixture.Terminated.Count == 1, "A valid observed renderer did not trigger one immediate automatic attempt");
                Check(fixture.Service.GetSnapshot(fixture.Profile).Detached, "Successful cleanup was not reported");
                for (int i = 0; i < 5; i++) { fixture.Notify(); fixture.Step(); }
                Check(fixture.TerminationCalls == 1, "Repeated notifications retried a completed renderer instance");
            }
        }

        private static void MissingRootRetry()
        {
            using (var fixture = new AutomaticFixture())
            {
                fixture.DiscoveredRoot = null; fixture.Notify(); fixture.Step();
                Check(fixture.RootCalls == 1 && fixture.CaptureCalls == 0 && fixture.TerminationCalls == 0, "Missing platform provenance did not prevent cleanup");
                fixture.DiscoveredRoot = Root; fixture.Clock += TimeSpan.TicksPerHour; fixture.Step();
                Check(fixture.RootCalls >= 2 && fixture.TerminationCalls == 1, "Later valid platform discovery could not retry the unattempted session");
                fixture.Step(); Check(fixture.TerminationCalls == 1, "Root rediscovery caused repeated cleanup");
            }
            using (var fixture = new AutomaticFixture())
            {
                fixture.DiscoveredRoot = null; fixture.Notify(); fixture.Step();
                fixture.Service.NotifySession(null, 0, 0, false);
                fixture.DiscoveredRoot = Root; fixture.Notify(); fixture.Step();
                Check(fixture.TerminationCalls == 1, "Missing root discovery consumed the renderer's attempt receipt before any cleanup was authorized");
            }
        }

        private static void InvalidRenderer()
        {
            foreach (int variation in new[] { 0, 1, 2, 3, 4, 5 })
            {
                using (var fixture = new AutomaticFixture())
                {
                    if (variation == 0) fixture.Renderer = null;
                    if (variation == 1) fixture.Renderer.Pid++;
                    if (variation == 2) fixture.Renderer.Creation++;
                    if (variation == 3) fixture.Renderer.Path = @"C:\Other\game.exe";
                    if (variation == 4) fixture.Renderer.Name = "wegame";
                    if (variation == 5)
                    {
                        fixture.Renderer.Path = fixture.Profile.Root + @"\unobserved.exe";
                        fixture.Renderer.Name = "unobserved";
                    }
                    fixture.Notify(); fixture.Step();
                    Check(fixture.TerminationCalls == 0, "Unverified renderer identity variation " + variation + " authorized cleanup");
                }
            }
        }

        private static void BeforeSecondCapture(AutomaticFixture fixture, Action invalidate)
        {
            fixture.BeforeCapture = delegate(int call) { if (call == 2) invalidate(); };
            fixture.Notify(); fixture.Step();
            Check(fixture.CaptureCalls >= 2, "The final recapture boundary was not exercised");
            Check(fixture.TerminationCalls == 0, "A mutation ran after its authorization became stale");
            fixture.BeforeCapture = null;
        }

        private static void ChangedRendererBeforeMutation()
        {
            foreach (int variation in new[] { 0, 1, 2, 3 })
                using (var fixture = new AutomaticFixture())
                {
                    int selected = variation;
                    BeforeSecondCapture(fixture, delegate
                    {
                        if (selected == 0) fixture.Renderer.Pid++;
                        if (selected == 1) fixture.Renderer.Creation++;
                        if (selected == 2) fixture.Renderer.Path = fixture.Profile.Root + @"\different-renderer.exe";
                        if (selected == 3) fixture.Renderer = null;
                    });
                }
        }

        private static void ChangedSessionBeforeMutation()
        {
            using (var fixture = new AutomaticFixture())
                BeforeSecondCapture(fixture, delegate { fixture.Service.NotifySession(null, 0, 0, false); });
            using (var fixture = new AutomaticFixture())
                BeforeSecondCapture(fixture, delegate { fixture.Service.CancelProfile(fixture.Profile); });
            using (var fixture = new AutomaticFixture())
                BeforeSecondCapture(fixture, delegate
                {
                    GameProfile replacement = fixture.Profile.Clone(); replacement.Id += "-other";
                    fixture.Service.NotifySession(replacement, 1101, 100000, true);
                });
            using (var fixture = new AutomaticFixture())
                BeforeSecondCapture(fixture, delegate
                {
                    GameProfile replacement = fixture.Profile.Clone();
                    replacement.ExecutablePath = replacement.Root + @"\other.exe";
                    fixture.Service.NotifySession(replacement, 1101, 100000, true);
                });
        }

        private static void DisabledBeforeMutation()
        {
            using (var fixture = new AutomaticFixture())
            {
                BeforeSecondCapture(fixture, delegate { Check(WeGameGameExtension.SetProfileEnabled(fixture.Profile, false), "The isolated off preference was not saved"); });
                fixture.Step(); Check(fixture.TerminationCalls == 0, "A disabled feature resumed its pending cleanup");
                Check(WeGameGameExtension.SetProfileEnabled(fixture.Profile, true), "The isolated on preference was not saved");
                fixture.Notify(); fixture.Step();
                Check(fixture.TerminationCalls == 0, "Toggling the feature back on retried the already claimed renderer instance");
            }
        }

        private static void StoppedBeforeMutation()
        {
            using (var fixture = new AutomaticFixture())
            {
                BeforeSecondCapture(fixture, delegate { fixture.Service.Stop(); });
                fixture.Step(); Check(fixture.TerminationCalls == 0, "A stopped service resumed pending cleanup");
            }
            using (var fixture = new AutomaticFixture())
            {
                BeforeSecondCapture(fixture, delegate { fixture.Service.Dispose(); });
                fixture.Step(); Check(fixture.TerminationCalls == 0, "A disposed service resumed pending cleanup");
            }
            using (var fixture = new AutomaticFixture())
            {
                BeforeSecondCapture(fixture, delegate
                {
                    fixture.Service.Stop(); fixture.Service.Start(); fixture.Notify();
                });
                fixture.Step();
                Check(fixture.TerminationCalls == 0, "Restarting the service retried the already claimed renderer instance");
                fixture.Renderer.Creation = 200000;
                fixture.Service.NotifySession(fixture.Profile, 1101, 200000, true); fixture.Step();
                Check(fixture.TerminationCalls == 1, "A new renderer birth after restart could not make its own single attempt");
            }
        }

        private static void FailureDoesNotLoop()
        {
            foreach (int variation in new[] { 0, 1, 2 })
                using (var fixture = new AutomaticFixture())
                {
                    fixture.TerminationSucceeds = false;
                    if (variation == 1) fixture.ThrowOnTermination = true;
                    if (variation == 2) fixture.CaptureComplete = false;
                    fixture.Notify(); fixture.Step();
                    int mutations = fixture.TerminationCalls; int captures = fixture.CaptureCalls;
                    Check(!fixture.Service.GetSnapshot(fixture.Profile).Detached, "Failed cleanup was reported as completed");
                    for (int i = 0; i < 5; i++) { fixture.Clock += TimeSpan.TicksPerHour; fixture.Notify(); fixture.Step(); }
                    Check(fixture.TerminationCalls == mutations && fixture.CaptureCalls == captures, "A failed attempt entered an automatic retry loop");
                }
        }

        private static void NewRendererInstance()
        {
            using (var fixture = new AutomaticFixture())
            {
                fixture.Notify(); fixture.Step();
                Check(fixture.TerminationCalls == 1, "The first renderer instance did not attempt cleanup");
                fixture.Renderer.Pid = 1102; fixture.Renderer.Creation = 200000;
                fixture.Service.NotifySession(fixture.Profile, 1102, 200000, true); fixture.Step();
                Check(fixture.TerminationCalls == 2, "A later renderer instance did not get its own single attempt");
                fixture.Service.NotifySession(fixture.Profile, 1102, 200000, true); fixture.Step();
                Check(fixture.TerminationCalls == 2, "The later instance retried after completion");
            }
            using (var fixture = new AutomaticFixture())
            {
                fixture.Notify(); fixture.Step();
                fixture.Renderer.Creation = 200000;
                fixture.Service.NotifySession(fixture.Profile, 1101, 200000, true); fixture.Step();
                Check(fixture.TerminationCalls == 2, "A reused PID with a different creation time was mistaken for the previous renderer instance");
            }
        }

        private static void SameInstanceReceipts()
        {
            using (var fixture = new AutomaticFixture())
            {
                fixture.Notify(); fixture.Step();
                for (int i = 0; i < 3; i++)
                {
                    fixture.Service.NotifySession(null, 0, 0, false);
                    fixture.Notify(); fixture.Step();
                }
                Check(fixture.TerminationCalls == 1, "Inactive/active notification jitter retried the same renderer process");
                fixture.Profile.LearnedExecutablePath = fixture.Profile.Root + @"\bin\observed.exe";
                fixture.Notify(); fixture.Step();
                Check(fixture.TerminationCalls == 1, "A learned-path metadata change retried the same renderer process");
            }
            using (var fixture = new AutomaticFixture())
            {
                fixture.TerminationSucceeds = false;
                fixture.Notify(); fixture.Step();
                Check(fixture.TerminationCalls == 1, "The injected cleanup failure was not exercised");
                fixture.Service.NotifySession(null, 0, 0, false);
                fixture.TerminationSucceeds = true;
                fixture.Notify(); fixture.Step();
                Check(fixture.TerminationCalls == 1, "An inactive/active notification retried a failed renderer attempt");
                fixture.Profile.LearnedExecutablePath = fixture.Profile.Root + @"\bin\observed.exe";
                fixture.Notify(); fixture.Step();
                Check(fixture.TerminationCalls == 1, "A profile metadata update retried a failed renderer attempt");
            }
        }

        private static void AutomaticCandidateBoundary()
        {
            using (var fixture = new AutomaticFixture())
            {
                fixture.Candidates.Clear();
                fixture.Candidates.Add(Entry(201, 0, 1000, "wegame.exe"));
                fixture.Candidates.Add(Entry(202, 201, 1100, "browser.exe"));
                fixture.Candidates.Add(Entry(203, 201, 1200, "wegame_env.exe"));
                fixture.Candidates.Add(Entry(204, 203, 1300, "wegameclient.exe"));
                fixture.Candidates.Add(Entry(205, 0, 1400, "SGuard64.exe"));
                fixture.Candidates.Add(Entry(206, 205, 1500, "wegameclient.exe"));
                fixture.Candidates.Add(Entry(207, 0, 1600, "browser.exe"));
                fixture.Candidates.Add(Entry(208, 0, 1700, @"C:\Other\wegame.exe"));
                fixture.Notify(); fixture.Step();
                Check(fixture.Terminated.Count == 2 && fixture.Terminated[0] == 202 && fixture.Terminated[1] == 201,
                    "The automatic path escaped its narrow, ancestry-verified UI candidates");
            }
        }

        private static void MissingHooksFailClosed()
        {
            foreach (int missing in new[] { 0, 1, 2, 3, 4 })
            {
                using (var fixture = new AutomaticFixture())
                {
                    if (missing == 0) fixture.Service.ProbeRendererForTest = null;
                    if (missing == 1) fixture.Service.FindRootForTest = null;
                    if (missing == 2) fixture.Service.CaptureCandidatesForTest = null;
                    if (missing == 3) fixture.Service.TerminateForTest = null;
                    if (missing == 4) fixture.Service.ClockForTest = null;
                    fixture.Notify(); fixture.Step();
                    Check(!fixture.Service.GetSnapshot(fixture.Profile).Detached && fixture.TerminationCalls == 0,
                        "A test-only service fell through to native execution with missing hook " + missing);
                }
            }
        }

        private static void ChangedCandidateBeforeMutation()
        {
            foreach (int variation in new[] { 0, 1, 2 })
                using (var fixture = new AutomaticFixture())
                {
                    int selected = variation;
                    BeforeSecondCapture(fixture, delegate
                    {
                        if (selected == 0) fixture.Candidates[0].Creation++;
                        if (selected == 1) fixture.Candidates[0].Pid++;
                        if (selected == 2)
                        {
                            fixture.Candidates[0].Path = Root + @"\wegameclient.exe";
                            fixture.Candidates[0].Name = "wegameclient";
                        }
                    });
                }
        }

        private static void CandidatePaths()
        {
            foreach (string file in new[] { "wegame.exe", "WeGameClient.EXE", "browser.exe", "crashpad_handler.exe" })
                Check(WeGameShellService.IsShellCandidate(Root + @"\ui\" + file, Root + @"\"), "A supported UI filename was rejected: " + file);
            foreach (string file in new[] { "wegame_env.exe", "pallas.exe", "rail.exe", "tcls_core.exe", "teniodl.exe", "wegameupdate.exe", "wegame.exe.bak", "mywegame.exe", "browser_helper.exe", "game.exe" })
                Check(!WeGameShellService.IsShellCandidate(Root + @"\" + file, Root), "An unrelated/runtime filename was accepted: " + file);
            foreach (string path in new[] { Root + @"Other\wegame.exe", Root + @"\..\Other\wegame.exe", @"C:\Other\browser.exe", Root, "", null })
                Check(!WeGameShellService.IsShellCandidate(path, Root), "A sibling, escaped or malformed candidate was accepted: " + path);
            Check(!WeGameShellService.IsShellCandidate(Root + @"\wegame.exe", @"C:\"), "A drive root was accepted as a verified installation scope");
            Check(!WeGameShellService.IsShellCandidate(Root + @"\wegame.exe", null), "A missing installation root was accepted");
        }

        private static GameProcessSnapshot Entry(int pid, int parent, long birth, string file)
        {
            string path = file.IndexOf(':') >= 0 ? file : Root + @"\" + file;
            return new GameProcessSnapshot { Pid = pid, ParentPid = parent, Creation = birth, Path = path, Name = Path.GetFileNameWithoutExtension(path) };
        }

        private static void Selected(IList<GameProcessSnapshot> entries, params int[] expected)
        {
            List<GameProcessSnapshot> selected = WeGameShellService.SelectShellCandidates(entries, Root);
            Check(selected.Count == expected.Length, "Expected " + expected.Length + " candidates; got " + selected.Count);
            for (int i = 0; i < expected.Length; i++) Check(selected[i].Pid == expected[i], "Unexpected candidate/order at " + i + ": " + selected[i].Pid);
        }

        private static void UiAncestry()
        {
            Selected(new[] { Entry(10, 0, 100, "wegame.exe"), Entry(11, 10, 110, "wegameclient.exe"), Entry(12, 11, 120, "browser.exe"), Entry(13, 12, 130, "crashpad_handler.exe") }, 13, 12, 11, 10);
            Selected(new[] { Entry(10, 0, 100, "browser.exe") });
            Selected(new[] { Entry(10, 999, 100, "browser.exe") });
            Selected(new[] { Entry(10, 0, 100, @"C:\Other\wegame.exe"), Entry(11, 10, 110, "browser.exe") });
            Selected(new[] { Entry(10, 0, 100, "wegame.exe"), Entry(12, 11, 120, "browser.exe") }, 10);
            Selected(new[] { Entry(10, 0, 100, "wegame.exe"), Entry(11, 10, 110, @"C:\PaviseFixture\WeGameOther\browser.exe") }, 10);
        }

        private static void RuntimeFamilies()
        {
            foreach (string protectedFile in new[] { "wegame_env.exe", "pallas.exe", "rail.exe", "tcls_core.exe", "teniodl.exe", "wegameupdate.exe", "SGuard64.exe", "BEService.exe", "vgc.exe" })
            {
                Selected(new[] { Entry(10, 0, 100, protectedFile), Entry(11, 10, 110, "wegameclient.exe"), Entry(12, 11, 120, "browser.exe") });
                Selected(new[] { Entry(10, 0, 100, "wegame.exe"), Entry(11, 10, 110, protectedFile), Entry(12, 11, 120, "browser.exe") }, 10);
            }
            Selected(new[] { Entry(10, 0, 100, @"C:\AntiCheatExpert\wegame.exe"), Entry(11, 10, 110, "wegameclient.exe"), Entry(12, 11, 120, "browser.exe") });
            Selected(new[] { Entry(10, 0, 100, "wegame.exe"), Entry(11, 0, 110, "wegame_env.exe") }, 10);
        }

        private static void InvalidGraphs()
        {
            Selected(null);
            Selected(new GameProcessSnapshot[] { null });
            Selected(new[] { Entry(10, 0, 100, "wegame.exe"), Entry(10, 0, 101, "browser.exe") });
            Selected(new[] { Entry(0, 0, 100, "wegame.exe") });
            Selected(new[] { Entry(10, 0, 0, "wegame.exe") });
            Selected(new[] { Entry(10, 10, 100, "wegame.exe") });
            Selected(new[] { Entry(10, 11, 100, "wegame.exe"), Entry(11, 10, 110, "wegameclient.exe") });
            Selected(new[] { Entry(10, 0, 200, "wegame.exe"), Entry(11, 10, 100, "browser.exe") }, 10);
            Selected(new[] { Entry(10, 0, 100, "wegame.exe"), Entry(11, 10, 100, "browser.exe") }, 10);
        }

        private static void AncestorIdentity()
        {
            GameProcessSnapshot mismatched = Entry(10, 0, 100, "wegame.exe");
            mismatched.Name = "unrelated";
            Selected(new[] { mismatched, Entry(11, 10, 110, "browser.exe") });
            GameProcessSnapshot child = Entry(11, 10, 110, "browser.exe");
            child.Name = "wegameclient";
            Selected(new[] { Entry(10, 0, 100, "wegame.exe"), child }, 10);
            Selected(new[] { Entry(10, 0, 100, "wegame.exe.bak"), Entry(11, 10, 110, "browser.exe") });
        }

        private static void ProfileOwnership()
        {
            var profile = new GameProfile { Id = "fixture", Root = @"C:\PaviseFixture\GameAlpha", ExecutablePath = @"C:\PaviseFixture\GameAlpha\launcher.exe", LearnedExecutablePath = @"C:\PaviseFixture\GameAlpha\bin\game.exe" };
            Check(WeGameShellService.BelongsToProfile(profile, profile.ExecutablePath), "The exact launch executable was not owned");
            Check(WeGameShellService.BelongsToProfile(profile, profile.LearnedExecutablePath.ToUpperInvariant()), "The learned renderer path was not owned case-insensitively");
            Check(!WeGameShellService.BelongsToProfile(null, profile.ExecutablePath), "A missing profile owned a process");
            Check(!WeGameShellService.BelongsToProfile(profile, null), "A profile owned a missing path");

            // Seed the read-only install-scope cache with a synthetic snapshot so
            // fallback ownership never enumerates the user's installed games.
            const BindingFlags fields = BindingFlags.Static | BindingFlags.NonPublic;
            Type snapshotType = typeof(GameInstallScope).GetNestedType("Snapshot", BindingFlags.NonPublic);
            object snapshot = Activator.CreateInstance(snapshotType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { new List<GameInstallRecord>(), new List<string>(), true }, null);
            FieldInfo cache = typeof(GameInstallScope).GetField("cached", fields);
            FieldInfo cacheAt = typeof(GameInstallScope).GetField("cachedAt", fields);
            object previous = cache.GetValue(null); object previousAt = cacheAt.GetValue(null);
            try
            {
                cache.SetValue(null, snapshot); cacheAt.SetValue(null, DateTime.UtcNow.Ticks);
                Check(WeGameShellService.BelongsToProfile(profile, @"C:\PaviseFixture\GameAlpha\bin\render.exe"), "A renderer inside the restricted game root was rejected");
                Check(!WeGameShellService.BelongsToProfile(profile, @"C:\PaviseFixture\GameAlphaOther\game.exe"), "A similar sibling game directory was accepted");
                Check(!WeGameShellService.BelongsToProfile(profile, @"C:\PaviseFixture\GameAlpha\..\Other\game.exe"), "An escaped renderer path was accepted");
                profile.Root = @"C:\PaviseFixture\Games";
                profile.ExecutablePath = @"C:\PaviseFixture\Games\launcher.exe";
                profile.LearnedExecutablePath = null;
                Check(!WeGameShellService.BelongsToProfile(profile, @"C:\PaviseFixture\Games\Other\game.exe"), "A broad games-library fallback root owned another game's process");
                profile.Root = @"C:\";
                Check(!WeGameShellService.BelongsToProfile(profile, @"C:\Other\game.exe"), "A drive-wide fallback root owned another game's process");
            }
            finally { cache.SetValue(null, previous); cacheAt.SetValue(null, previousAt); }
        }
    }
}
#endif
