#if PAVISE_SELFTEST && PAVISE_CORE_EXIT_DRIFT_CHECKS
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    // An engaged exclusive placement whose game exits: a real child process stands in for the game,
    // the profile store and settings are temporary, and core isolation is a test double.
    internal static class CoreExitDriftChecks
    {
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--child") { Console.ReadLine(); return 0; }
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional exit drift negative control"); return 3; }
            if (CpuTopology.MultiGroup) { Console.WriteLine("FAIL this fixture requires a single processor group"); return 2; }
            string root = Path.Combine(Path.GetTempPath(), "PaviseCoreExitDrift-" + Guid.NewGuid().ToString("N"));
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Lang.Init();
                Directory.CreateDirectory(root); Logger.LogPath = Path.Combine(root, "exit-drift.log");
                SelfTests.VerifyExitedGameIsNotDrift(root);
                Console.WriteLine("PASS core exit drift checks=" + SelfTests.ExitDriftChecks);
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL " + (error is TargetInvocationException && error.InnerException != null ? error.InnerException : error)); return 1; }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }

    internal static partial class SelfTests
    {
        internal static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application startup is forbidden in the exit drift fixture"); }
        internal static int ExitDriftChecks;
        // The shared family-policy fixture asserts through Eq.
        private static void Eq<T>(T expected, T actual) { ExitDriftEq(expected, actual, "fixture assertion failed"); }
        private static void ExitDriftEq<T>(T expected, T actual, string reason)
        {
            ExitDriftChecks++;
            if (!Equals(expected, actual)) throw new InvalidOperationException(reason + " (expected " + expected + ", got " + actual + ")");
        }

        // A game that exits while its exclusive placement is engaged has not drifted: the audit must not
        // stop core exclusivity as unreadable before the session end releases the placement.
        internal static void VerifyExitedGameIsNotDrift(string root)
        {
            var oldAllocated = AutoCores.AllocatedForTest;
            var oldSupported = AutoCores.SupportedForTest;
            AutoCores.AllocatedForTest = delegate { return 0; };
            AutoCores.SupportedForTest = true;
            using (var child = Process.Start(new ProcessStartInfo
            {
                FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--child",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
            }))
            using (var fixture = new FamilyPolicyFixture(root, "exit-not-drift"))
            {
                GameMode mode = fixture.Mode;
                int pid = child.Id; long creation = 0;
                try
                {
                    IntPtr handle = child.Handle;
                    ulong original = Native.QueryAffinity(handle), target = 0;
                    foreach (ulong physical in CpuTopology.PhysicalCoreMasks())
                        if ((physical & 1UL) == 0 && (physical & ~original) == 0 && AutoCores.CountBits(target) < 2)
                            target |= physical;
                    ExitDriftEq(true, AutoCores.CountBits(target) >= 2, "the fixture needs two whole cores away from CPU 0");
                    long cpu; ulong disk;
                    ExitDriftEq(true, Native.QueryProcessSample(handle, out creation, out cpu, out disk), "the child identity could not be read");
                    mode.Preset = PerformancePreset.Competitive;
                    mode.RendererTestForeground = delegate { return pid; };
                    ExitDriftEq(null, mode.SaveManualCorePlan(fixture.First.Id, ManualCorePlan.Raw(fixture.Current(fixture.First.Id)),
                        new ManualCorePlan(target, false, ManualCorePlan.CurrentStamp())), "the manual core plan was refused");
                    ExitDriftEq(true, mode.SetProfileOverride(fixture.First.Id, PolicyCatalog.KeyAutoCores, "1"), "automatic cores could not be enabled");
                    var profile = fixture.Current(fixture.First.Id);
                    var policy = PolicyResolver.For(profile);
                    FamilyPolicySetField(mode, "sessionPolicy", policy);
                    FamilyPolicySetField(mode, "activeDetection", new GameDetection { Profile = profile,
                        RendererPid = pid, RendererCreation = creation, RendererName = Native.ImageName(handle), RendererCandidateSelected = true });
                    mode.CoreEngageForTest = delegate { return true; };
                    FamilyPolicyInvoke(mode, "BeginAutoCores", policy, true);
                    object pass = FamilyPolicyInvoke(mode, "PrepareBoostPass");
                    FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", pass);
                    ExitDriftEq(true, mode.ProbeCoreEngaged, "the exclusive placement did not engage");
                    ExitDriftEq(target, Native.QueryAffinity(handle), "the child was not pinned to the planned cores");
                    child.Kill(); child.WaitForExit();
                    FamilyPolicySetField(mode, "exclusiveCheckTicks", 0L);
                    FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", pass);
                    ExitDriftEq(false, AutoCores.Status.Blocked == "schedule.auto.affinityread", "an exiting game was reported as unreadable affinity");
                    ExitDriftEq(true, mode.ProbeCoreEngaged, "an exiting game stopped core exclusivity before its session ended");
                    // The session end in Deactivate's order: games are unboosted, then automatic cores end.
                    ExitDriftEq(true, (bool)typeof(GameMode).GetMethod("UnboostGames", BindingFlags.Instance | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null).Invoke(mode, null), "the session end could not release the exited game");
                    FamilyPolicyInvoke(mode, "EndAutoCores");
                    ExitDriftEq(false, mode.ProbeCoreEngaged, "the session end did not release core exclusivity");
                    ExitDriftEq(0, ((System.Collections.IDictionary)FamilyPolicyGetField(mode, "hardPins")).Count, "the exited game's hard pin was retained");
                }
                finally
                {
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
                    FamilyPolicySetField(mode, "activeDetection", null);
                    if (creation > 0) CrashGuard.ReleaseBoostProcess(pid, creation);
                    AutoCores.AllocatedForTest = oldAllocated; AutoCores.SupportedForTest = oldSupported;
                    CoreIsolationClient.State = "schedule.isolation.idle";
                }
            }
        }
    }
}
#endif
