#if PAVISE_SELFTEST && PAVISE_SELFTEST_RUNNER
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class CoreSchedulingIntegrationRunner
    {
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--child") { Console.ReadLine(); return 0; }
            if (args.Length != 1 || args[0] != "--integration") return 2;
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Lang.Init();
                SelfTests.RunCoreSchedulingIntegration();
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }

    internal static partial class SelfTests
    {
        private static void VerifyIndependentCorePlacement(GameMode mode, int pid, long creation,
            ulong original, ulong target, IntPtr queryHandle)
        {
            string name = Native.ImageName(queryHandle);
            ProtectedGameRoster.ResetCache();
            ProtectedGameRoster.Remember(name);
            var stripped = (HashSet<int>)FamilyPolicyGetField(mode, "boostHandleStripped");
            stripped.Add(pid);
            mode.Preset = PerformancePreset.Competitive;
            mode.RendererTestForeground = delegate { return pid; };
            FamilyPolicySetField(mode, "activeDetection", new GameDetection { RendererPid = pid,
                RendererCreation = creation, RendererName = name, RendererCandidateSelected = true });
            var plan = new AutoCorePlan { Exclusive = target, Region = original, Cores = 2, Logical = 2, Kind = "fixture" };
            Type passType = typeof(GameMode).GetNestedType("BoostPass", BindingFlags.NonPublic);
            int isolationCalls = 0;
            try
            {
                mode.ProbeBeginAutoCores(plan);
                FamilyPolicyInvoke(mode, "PrepareBoostPass");
                Eq(null, mode.ProbeGovernor.Blocked);
                mode.ProbeGovernor.Observe(1, 0.5, AutoCores.NowTicks);
                mode.ProbeGovernor.Observe(1, 0.5, AutoCores.NowTicks + 1);
                object pass = FamilyPolicyInvoke(mode, "PrepareBoostPass");
                Eq(true, AutoPassField<bool>(pass, "WriteDenied"));
                Eq(true, AutoPassField<bool>(pass, "Exclusive"));
                mode.CoreEngageForTest = delegate {
                    isolationCalls++; Eq(target, Native.QueryAffinity(queryHandle)); return true;
                };
                FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", pass);
                Eq(true, mode.ProbeCoreEngaged); Eq(target, Native.QueryAffinity(queryHandle));
                Eq(null, AutoCores.Status.Blocked); Eq(1, isolationCalls);
                object[] stateArgs = { queryHandle, pid, pass, false, false, false };
                typeof(GameMode).GetMethod("ApplyBoostStateStage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode, stateArgs);
                Eq(false, ((HashSet<int>)FamilyPolicyGetField(mode, "placementGaveUp")).Contains(pid));
                IntPtr boostReadOnly = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                Eq(true, boostReadOnly != IntPtr.Zero);
                try
                {
                    passType.GetField("WriteDenied").SetValue(pass, false);
                    object[] deniedArgs = { boostReadOnly, pid, pass, false, false, false };
                    typeof(GameMode).GetMethod("ApplyBoostStateStage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode, deniedArgs);
                    Eq(false, (bool)deniedArgs[4]);
                    Eq(false, ((HashSet<int>)FamilyPolicyGetField(mode, "placementGaveUp")).Contains(pid));
                    Eq(true, ((System.Collections.IDictionary)FamilyPolicyGetField(mode, "gamePlacement")).Contains(pid));
                    Eq(target, Native.QueryAffinity(queryHandle));
                    passType.GetField("WriteDenied").SetValue(pass, true);
                }
                finally { Native.CloseHandle(boostReadOnly); }
                FamilyPolicyInvoke(mode, "StepAutoCores", pass);
                Eq(null, mode.ProbeGovernor.Blocked); Eq(target, Native.QueryAffinity(queryHandle));
                mode.RendererTestForeground = delegate { return 0; };
                FamilyPolicyInvoke(mode, "StepAutoCores", pass);
                Eq(true, mode.ProbeCoreEngaged); Eq(target, Native.QueryAffinity(queryHandle));
                Eq(true, mode.ProbeGovernor.Exclusive);
                mode.ProbeGovernor.Observe(0, 0, AutoCores.NowTicks + TimeSpan.TicksPerDay);
                Eq(true, mode.ProbeGovernor.Exclusive);
                mode.RendererTestForeground = delegate { return pid; };
                ulong expanded = target;
                for (int i = 0; i < 64; i++) if ((original & ~target & (1UL << i)) != 0) { expanded |= 1UL << i; break; }
                passType.GetField("DesiredMask").SetValue(pass, expanded);
                FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", pass);
                Eq(expanded, Native.QueryAffinity(queryHandle)); Eq(1, isolationCalls);
                Eq(target, plan.Exclusive); Eq(true, mode.ProbeCoreEngaged);
                Eq(1, AutoCores.Status.Expansions);
                Console.WriteLine("PASS native in-place expansion: base unchanged, no isolation restart, retained on focus loss + idle + 24-hour gap");
                FamilyPolicyInvoke(mode, "ApplyAutoCoreStep", mode.ProbeGovernor.Pause("schedule.auto.foreground", AutoCores.NowTicks));
                Eq(original, Native.QueryAffinity(queryHandle));
                CoreIsolationClient.State = "schedule.isolation.idle";
                Console.WriteLine("PASS independent production placement despite cached + stripped BOOST restriction: native pin/readback/restore; isolation mocked");

                for (int scenario = 0; scenario < 4; scenario++)
                {
                    mode.ProbeBeginAutoCores(plan);
                    mode.ProbeGovernor.Observe(1, 0.5, AutoCores.NowTicks);
                    mode.ProbeGovernor.Observe(1, 0.5, AutoCores.NowTicks + 1);
                    object check = Activator.CreateInstance(passType, true);
                    passType.GetField("RendererPid").SetValue(check, pid);
                    passType.GetField("RendererCreation").SetValue(check, scenario == 1 ? creation + 1 : creation);
                    passType.GetField("RendererName").SetValue(check, name);
                    passType.GetField("WriteDenied").SetValue(check, true);
                    passType.GetField("Exclusive").SetValue(check, true);
                    passType.GetField("DesiredMask").SetValue(check, target);
                    mode.CorePlacementOpenForTest = scenario == 0
                        ? (Func<int, IntPtr>)delegate(int id) { return Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, id); }
                        : null;
                    mode.CoreEngageForTest = delegate {
                        isolationCalls++; Eq(target, Native.QueryAffinity(queryHandle)); return false;
                    };
                    if (scenario == 3)
                    {
                        ((System.Collections.IDictionary)FamilyPolicyGetField(mode, "gameBoost")).Clear();
                        CrashGuard.ReleaseBoostProcess(pid, creation);
                        Settings.SuspendWritesForReset();
                    }
                    int beforeCalls = isolationCalls;
                    FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", check);
                    Eq(scenario == 2 ? target : original, Native.QueryAffinity(queryHandle));
                    Eq(scenario == 0 || scenario == 2, mode.ProbeCoreEngaged);
                    string reason = scenario == 0 ? "schedule.auto.affinityaccess" : scenario == 1 ? "schedule.auto.identity"
                        : scenario == 2 ? "schedule.auto.failed" : "schedule.auto.capture";
                    Eq(scenario == 0 || scenario == 2 ? null : reason, AutoCores.Status.Blocked);
                    Eq(scenario == 0, AutoCores.Status.BackgroundOnly);
                    Eq(scenario == 0 || scenario == 2, AutoPassField<bool>(check, "Exclusive"));
                    Eq(scenario == 0 || scenario == 2, AutoCores.Status.Cooperative);
                    if (scenario == 0 || scenario == 2)
                    {
                        mode.ProbeRestoreHardPin(pid);
                        FamilyPolicyInvoke(mode, "ReleaseExclusive");
                        Eq(original, Native.QueryAffinity(queryHandle));
                    }
                    Eq(beforeCalls + (scenario == 2 ? 1 : 0), isolationCalls);
                    if (scenario == 3) Settings.UseTransientStoreForCurrentProcess();
                }
                Console.WriteLine("PASS denied game access uses background-only; clean kernel rejection uses cooperative placement; identity/journal failures block writes; all restore");

                mode.ProbeBeginAutoCores(plan);
                mode.ProbeGovernor.Observe(1, 0.5, AutoCores.NowTicks);
                mode.ProbeGovernor.Observe(1, 0.5, AutoCores.NowTicks + 1);
                IntPtr deniedWrite = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                Eq(true, deniedWrite != IntPtr.Zero);
                try
                {
                    Eq(false, mode.ProbeExclusivePlacement(deniedWrite, pid, creation, original, target));
                    Eq(original, Native.QueryAffinity(queryHandle));
                    Eq(true, mode.ProbeCoreEngaged);
                    FamilyPolicyInvoke(mode, "PublishAutoCores", name);
                    Eq(true, AutoCores.Status.BackgroundOnly);
                    Eq(null, AutoCores.Status.Blocked);
                    Eq(true, AutoCores.DiagnosticLine().Contains("stage=set-affinity win32=5"));
                    Eq(true, AutoCores.DiagnosticLine().Contains("actual=0x" + original.ToString("X")));
                }
                finally
                {
                    Native.CloseHandle(deniedWrite);
                    FamilyPolicyInvoke(mode, "ReleaseExclusive");
                }
                Console.WriteLine("PASS actual native affinity write denial: original unchanged, background-only stays available");
            }
            finally
            {
                mode.CorePlacementOpenForTest = null; mode.CoreEngageForTest = null;
                mode.RendererTestForeground = null; stripped.Remove(pid);
                FamilyPolicySetField(mode, "activeDetection", null);
                mode.ProbeRestoreHardPin(pid); FamilyPolicyInvoke(mode, "EndAutoCores");
                CoreIsolationClient.State = "schedule.isolation.idle";
                ProtectedGameRoster.Forget(name); ProtectedGameRoster.ResetCache();
            }
        }

        internal static void RunCoreSchedulingIntegration()
        {
            if (CpuTopology.MultiGroup) throw new InvalidOperationException("This integration fixture requires a single processor group.");
            string root = Path.Combine(Path.GetTempPath(), "PaviseCoreIntegration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Logger.LogPath = Path.Combine(root, "integration.log");
            using (var f = new FamilyPolicyFixture(root, "manual-affinity"))
            using (var child = Process.Start(new ProcessStartInfo
            {
                FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--child",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
            }))
            {
                try
                {
                    IntPtr h = child.Handle;
                    ulong original = Native.QueryAffinity(h), target = 0;
                    for (int i = 0; i < 64 && CpuTopology.CountSetBits(target) < 2; i++)
                        if ((original & (1UL << i)) != 0) target |= 1UL << i;
                    if (CpuTopology.CountSetBits(original) < 4) throw new InvalidOperationException("Fixture needs at least four logical CPUs.");
                    long creation, cpu; ulong disk;
                    Eq(true, Native.QueryProcessSample(h, out creation, out cpu, out disk));
                    RunCoreThreadPlacementNative(child.Id, creation, h);
                    VerifyManualCorePlacement(root, child.Id, creation, original, h);
                    VerifyManualBackgroundAffinity(root, child.Id, creation, original, h);
                    Eq(true, f.Mode.ProbePinExclusive(h, child.Id, creation, original, target));
                    Eq(target, Native.QueryAffinity(h));
                    Eq(true, f.Mode.ProbeRestoreHardPin(child.Id));
                    Eq(original, Native.QueryAffinity(h));
                    Console.WriteLine("PASS exclusive pin + retained-handle restoration: original=" + original.ToString("X") + " pinned=" + target.ToString("X"));

                    Eq(true, Native.SetProcessAffinityMask(h, (UIntPtr)target));
                    Eq(false, f.Mode.ProbePinExclusive(h, child.Id, creation, target, original));
                    Eq(target, Native.QueryAffinity(h));
                    Eq(true, Native.SetProcessAffinityMask(h, (UIntPtr)original));
                    Eq(original, Native.QueryAffinity(h));
                    Console.WriteLine("PASS pre-existing affinity restriction is preserved on refused placement");
                    ulong external = original & ~target;
                    Eq(true, f.Mode.ProbePinExclusive(h, child.Id, creation, original, target));
                    Eq(true, Native.SetProcessAffinityMask(h, (UIntPtr)external));
                    Eq(true, f.Mode.ProbeRestoreHardPin(child.Id));
                    Eq(external, Native.QueryAffinity(h));
                    CrashGuard.ReleaseBoostProcess(child.Id, creation);
                    Console.WriteLine("PASS retained restore yields to an external affinity change without a drift poll");

                    uint[] externalSets = CpuTopology.CpuSetIdsFor(external);
                    Eq(true, externalSets != null && externalSets.Length > 0);
                    Eq(true, Native.TrySetCpuSets(h, externalSets));
                    Eq(true, f.Mode.ProbeSharedPlacement(h, child.Id, creation, original));
                    Eq(external, Native.QueryAffinity(h)); Eq(true, Native.CpuSetsMatch(h, externalSets));
                    Console.WriteLine("PASS shared placement preserves current affinity and CPU Sets");

                    for (int scenario = 0; scenario < 3; scenario++)
                    {
                        int qc, qs; Native.TryQueryPowerThrottling(h, out qc, out qs);
                        Eq(true, CrashGuard.MarkBoostProcess(child.Id, creation, Native.ImageName(h),
                            Native.GetPriorityClass(h), original, Native.QueryIoPriority(h), Native.QueryPagePriority(h),
                            -1, new uint[0], qc, qs));
                        if (scenario > 0) Eq(true, CrashGuard.TrackBoostAffinity(child.Id, creation, target));
                        Eq(true, Native.SetProcessAffinityMask(h, (UIntPtr)(scenario == 1 ? target : external)));
                        CrashGuard.HealFromCrash();
                        Eq(scenario == 1 ? original : external, Native.QueryAffinity(h));
                        Eq(true, Native.CpuSetsMatch(h, externalSets)); Eq(false, CrashGuard.HasPending());
                    }
                    for (int stage = 0; stage < 2; stage++)
                    {
                        Eq(true, CrashGuard.MarkBoostProcess(child.Id, creation, Native.ImageName(h),
                            Native.GetPriorityClass(h), original, Native.QueryIoPriority(h), Native.QueryPagePriority(h), -1, new uint[0]));
                        Eq(true, CrashGuard.TrackBoostAffinityTransition(child.Id, creation, target | (external & (~external + 1)), target));
                        Eq(true, Native.SetProcessAffinityMask(h, (UIntPtr)(stage == 0 ? target : target | (external & (~external + 1)))));
                        CrashGuard.HealFromCrash();
                        Eq(original, Native.QueryAffinity(h)); Eq(false, CrashGuard.HasPending());
                    }
                    Console.WriteLine("PASS interrupted expansion recovery restores both pre-write and post-write states");
                    ProcessSnapshotSource.Capture();
                    long reuseCount = ProcessSnapshotSource.ThreadSampleReuses;
                    var sampleWatch = Stopwatch.StartNew();
                    double maxSampleMs = 0;
                    for (int i = 0; i < 100; i++)
                    {
                        long start = Stopwatch.GetTimestamp();
                        Eq(true, ProcessSnapshotSource.SampleThreads(child.Id, creation) != null);
                        maxSampleMs = Math.Max(maxSampleMs, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
                    }
                    sampleWatch.Stop();
                    Console.WriteLine("SAMPLING_BENCH count=100 reused=" + (ProcessSnapshotSource.ThreadSampleReuses - reuseCount)
                        + " average_ms=" + (sampleWatch.Elapsed.TotalMilliseconds / 100).ToString("0.0000") + " max_ms=" + maxSampleMs.ToString("0.0000"));
                    Console.WriteLine("PASS serialized crash recovery: no placement ownership, owned pin, external replacement; CPU Sets preserved");
                    Eq(true, Native.TryClearCpuSets(h));
                    Eq(true, Native.SetProcessAffinityMask(h, (UIntPtr)original));
                    Settings.SuspendWritesForReset();
                    Eq(false, f.Mode.ProbePinExclusive(h, child.Id, creation, original, target));
                    Eq(original, Native.QueryAffinity(h));
                    Settings.UseTransientStoreForCurrentProcess();
                    Console.WriteLine("PASS failed durable journal prevents the native affinity write");
                    var background = new SuppressionCore(Path.Combine(root, "background.state"));
                    string childName = Native.ImageName(h);
                    bool moved;
                    background.Acquire(child.Id, childName, SuppressReason.Background, null, SuppressionLevel.Eco);
                    Eq(false, background.SetSqueeze(child.Id, creation + 1, childName, target, SuppressReason.Background, out moved));
                    Eq(original, Native.QueryAffinity(h));
                    Eq(true, background.SetSqueeze(child.Id, creation, childName, target, SuppressReason.Background, out moved));
                    Eq(true, moved); Eq(target, Native.QueryAffinity(h)); Eq(1, background.SqueezedCount(SuppressReason.Background));
                    Eq(true, background.SetSqueeze(child.Id, creation, childName, 0, SuppressReason.Background, out moved));
                    Eq(original, Native.QueryAffinity(h)); Eq(0, background.SqueezedCount(SuppressReason.Background));
                    Eq(true, background.SetSqueeze(child.Id, creation, childName, target, SuppressReason.Background, out moved));
                    Eq(true, Native.SetProcessAffinityMask(h, (UIntPtr)external));
                    background.Reconcile(child.Id, childName, SuppressReason.Background, true);
                    Eq(external, Native.QueryAffinity(h));
                    background.Release(child.Id, SuppressReason.Background);
                    Eq(external, Native.QueryAffinity(h));
                    Eq(true, Native.SetProcessAffinityMask(h, (UIntPtr)original));
                    Console.WriteLine("PASS background avoidance native pin/readback/clear; reused PID rejected; external affinity respected during patrol and release");
                    background.Acquire(child.Id, childName, SuppressReason.Background, null, SuppressionLevel.Eco);
                    Eq(true, background.SetSqueeze(child.Id, creation, childName, target, SuppressReason.Background, out moved));
                    Eq(1, SuppressionCore.HealFromCrash(Path.Combine(root, "background.state")));
                    Eq(original, Native.QueryAffinity(h));
                    Console.WriteLine("PASS background placement journal crash recovery restores the original affinity");
                    Console.WriteLine("ISOLATION fixture_child_only=true system_reservation_written=false normal_app_started=false");
                }
                finally
                {
                    try { child.StandardInput.WriteLine("exit"); } catch { }
                    if (!child.WaitForExit(3000)) { child.Kill(); child.WaitForExit(3000); }
                }
            }
        }

        private static void VerifyManualBackgroundAffinity(string root, int pid, long creation, ulong original, IntPtr handle)
        {
            ulong selected = 0;
            foreach (ulong physical in CpuTopology.PhysicalCoreMasks())
                if ((physical & 1UL) == 0 && (physical & ~original) == 0 && AutoCores.CountBits(selected) < 2) selected |= physical;
            var hard = new ManualCorePlan(selected, true, ManualCorePlan.CurrentStamp(), true);
            var oldAllocated = AutoCores.AllocatedForTest;
            var oldSupported = AutoCores.SupportedForTest;
            string oldState = CoreIsolationClient.State;
            ulong oldMask = CoreIsolationClient.ActiveMask;
            var background = new SuppressionCore(Path.Combine(root, "manual-background.state"));
            string name = Native.ImageName(handle);
            try
            {
                AutoCores.AllocatedForTest = delegate { return 0; };
                AutoCores.SupportedForTest = true;
                using (var f = new FamilyPolicyFixture(root, "manual-background", background))
                {
                    var plan = hard.RuntimePlan();
                    Eq(true, plan.Available);
                    FamilyPolicySetField(f.Mode, "coreManual", hard);
                    FamilyPolicySetField(f.Mode, "corePlan", plan);
                    FamilyPolicySetField(f.Mode, "coreEngaged", true);
                    FamilyPolicySetField(f.Mode, "corePhysical", CpuTopology.PhysicalCoreMasks());
                    FamilyPolicySetField(f.Mode, "allMask", original);
                    CoreIsolationClient.State = "schedule.isolation.active";
                    CoreIsolationClient.ActiveMask = plan.Exclusive;
                    var snapshot = new ProcessSnapshot(new[] { new ProcEntry { Pid = pid, Creation = creation, Name = name, Cpu = 0 } });
                    var protectedPids = new HashSet<int>();
                    background.Acquire(pid, name, SuppressReason.Background, null, SuppressionLevel.Eco);
                    Eq(original & ~plan.Exclusive, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    Eq(true, background.PidsWith(SuppressReason.Background).Contains(pid));
                    ManualBackgroundSweep(f.Mode, snapshot, protectedPids, 0);
                    ulong outside = original & ~plan.Exclusive;
                    CheckManualBackgroundMask("initial idle placement", outside, handle);
                    Eq(1, background.SqueezedCount(SuppressReason.Background));
                    for (int repeat = 0; repeat < 3; repeat++)
                    {
                        Eq(true, Native.SetProcessAffinityMask(handle, (UIntPtr)original));
                        background.Reconcile(pid, name, SuppressReason.Background, true);
                        CheckManualBackgroundMask("patrol correction " + repeat, outside, handle);
                    }
                    protectedPids.Add(pid);
                    ManualBackgroundSweep(f.Mode, snapshot, protectedPids, 0);
                    Eq(original, Native.QueryAffinity(handle));
                    protectedPids.Clear();
                    ManualBackgroundSweep(f.Mode, snapshot, protectedPids, pid);
                    Eq(original, Native.QueryAffinity(handle));
                    ManualBackgroundSweep(f.Mode, snapshot, protectedPids, 0);
                    CheckManualBackgroundMask("after foreground exemption", outside, handle);
                    CoreIsolationClient.State = "schedule.isolation.idle";
                    ManualBackgroundSweep(f.Mode, snapshot, protectedPids, 0);
                    Eq(original, Native.QueryAffinity(handle));
                    CoreIsolationClient.State = "schedule.isolation.active";
                    ManualBackgroundSweep(f.Mode, snapshot, protectedPids, 0);
                    CheckManualBackgroundMask("isolation resumed", outside, handle);
                    FamilyPolicySetField(f.Mode, "coreManual", new ManualCorePlan(selected, true, hard.Topology));
                    Eq(true, Native.SetProcessAffinityMask(handle, (UIntPtr)(outside | selected & (~selected + 1))));
                    background.Reconcile(pid, name, SuppressReason.Background, true);
                    ManualBackgroundSweep(f.Mode, snapshot, protectedPids, 0);
                    CheckManualBackgroundMask("legacy off flag still enforces isolation", outside, handle);
                    FamilyPolicySetField(f.Mode, "coreManual", new ManualCorePlan(selected, false, hard.Topology));
                    ManualBackgroundSweep(f.Mode, snapshot, protectedPids, 0);
                    Eq(original, Native.QueryAffinity(handle));
                    FamilyPolicySetField(f.Mode, "coreManual", new ManualCorePlan(selected, true, hard.Topology));
                    ManualBackgroundSweep(f.Mode, snapshot, protectedPids, 0);
                    CheckManualBackgroundMask("isolation automatically re-enables hard affinity", outside, handle);
                    background.SqueezeReadbackFailsForTest = true;
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        FamilyPolicyInvoke(f.Mode, "ClearBackgroundSqueezes");
                        Eq(original, Native.QueryAffinity(handle));
                        Eq(true, (bool)FamilyPolicyGetField(f.Mode, "coreBackgroundPending"));
                        Eq(1, background.SqueezedCount(SuppressReason.Background, false));
                        Eq(0, background.SqueezedCount(SuppressReason.Background));
                        background.Reconcile(pid, name, SuppressReason.Background, true);
                        Eq(original, Native.QueryAffinity(handle));
                    }
                    background.SqueezeReadbackFailsForTest = false;
                    FamilyPolicyInvoke(f.Mode, "ClearBackgroundSqueezes");
                    Eq(false, (bool)FamilyPolicyGetField(f.Mode, "coreBackgroundPending"));
                    Eq(false, background.EnforceBackgroundAffinity);
                    Eq(0, background.SqueezedCount(SuppressReason.Background, false));
                    Console.WriteLine("PASS background restore readback failure: pending retained, patrol does not re-pin, repeated retries and final verified recovery");
                    FamilyPolicyInvoke(f.Mode, "StopCoreIsolation");
                    Eq(original, Native.QueryAffinity(handle));
                    Eq(0, background.SqueezedCount(SuppressReason.Background));
                    Console.WriteLine("PASS automatic isolation hard affinity: native idle-child pin for both legacy formats, repeated patrol correction, protected/foreground exemptions, isolation loss/disable and stop all restore original mask");
                }
            }
            finally
            {
                background.ClearSqueezes(SuppressReason.Background);
                background.Release(pid, SuppressReason.Background);
                Native.SetProcessAffinityMask(handle, (UIntPtr)original);
                AutoCores.AllocatedForTest = oldAllocated; AutoCores.SupportedForTest = oldSupported;
                CoreIsolationClient.State = oldState; CoreIsolationClient.ActiveMask = oldMask;
            }
        }

        private static void ManualBackgroundSweep(GameMode mode, ProcessSnapshot snapshot, HashSet<int> protectedPids, int foreground)
        {
            FamilyPolicySetField(mode, "coreBudget", new CoreWorkBudget());
            FamilyPolicyInvoke(mode, "SyncCoreBackground", snapshot, protectedPids, foreground);
        }

        private static void CheckManualBackgroundMask(string phase, ulong expected, IntPtr handle)
        {
            ulong actual = Native.QueryAffinity(handle);
            if (actual != expected) throw new InvalidOperationException("Manual background " + phase + ": expected 0x" + expected.ToString("X") + ", got 0x" + actual.ToString("X"));
        }

        private static void VerifyManualCorePlacement(string root, int pid, long creation, ulong original, IntPtr handle)
        {
            ulong target = 0;
            foreach (ulong physical in CpuTopology.PhysicalCoreMasks())
                if ((physical & 1UL) == 0 && (physical & ~original) == 0 && AutoCores.CountBits(target) < 2)
                    target |= physical;
            Eq(true, AutoCores.CountBits(target) >= 2);
            var oldAllocated = AutoCores.AllocatedForTest;
            var oldSupported = AutoCores.SupportedForTest;
            AutoCores.AllocatedForTest = delegate { return 0; };
            AutoCores.SupportedForTest = true;
            using (var fixture = new FamilyPolicyFixture(root, "manual-native"))
            {
                GameMode mode = fixture.Mode;
                mode.Preset = PerformancePreset.Competitive;
                mode.RendererTestForeground = delegate { return pid; };
                try
                {
                    for (int cycle = 0; cycle < 30; cycle++)
                    {
                        ulong desired = cycle < 3 ? target & (~target + 1UL)
                            : cycle < 6 ? original & (~original + 1UL) : target;
                        var manual = new ManualCorePlan(desired, cycle % 3 != 0, ManualCorePlan.CurrentStamp());
                        Eq(null, mode.SaveManualCorePlan(fixture.First.Id, ManualCorePlan.Raw(fixture.Current(fixture.First.Id)), manual));
                        Eq(true, mode.SetProfileOverride(fixture.First.Id, PolicyCatalog.KeyAutoCores, "1"));
                        var profile = fixture.Current(fixture.First.Id);
                        var policy = PolicyResolver.For(profile);
                        FamilyPolicySetField(mode, "sessionPolicy", policy);
                        FamilyPolicySetField(mode, "activeDetection", new GameDetection { Profile = profile,
                            RendererPid = pid, RendererCreation = creation, RendererName = Native.ImageName(handle), RendererCandidateSelected = true });
                        int isolationCalls = 0;
                        mode.CoreEngageForTest = delegate { isolationCalls++; return cycle % 3 == 1; };
                        FamilyPolicyInvoke(mode, "BeginAutoCores", policy, true);
                        object pass = FamilyPolicyInvoke(mode, "PrepareBoostPass");
                        Eq(true, AutoPassField<bool>(pass, "Exclusive"));
                        FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", pass);
                        bool success = cycle % 3 != 2;
                        Eq(success, mode.ProbeCoreEngaged);
                        Eq(success ? desired : original, Native.QueryAffinity(handle));
                        Eq(manual.Isolation ? 1 : 0, isolationCalls);
                        if (success)
                        {
                            FamilyPolicyInvoke(mode, "GrowCoreCapacity", 64, 64.0);
                            Eq(desired, (ulong)FamilyPolicyGetField(mode, "coreGameMask"));
                            mode.RendererTestForeground = delegate { return 0; };
                            FamilyPolicyInvoke(mode, "StepAutoCores", pass);
                            Eq(true, mode.ProbeCoreEngaged); Eq(desired, Native.QueryAffinity(handle));
                            mode.RendererTestForeground = delegate { return pid; };
                            if (manual.Isolation)
                            {
                                Eq(true, Native.SetProcessAffinityMask(handle, (UIntPtr)original));
                                FamilyPolicySetField(mode, "exclusiveCheckTicks", 0L);
                                FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", pass);
                                Eq(true, mode.ProbeCoreEngaged);
                                Eq(original, Native.QueryAffinity(handle));
                                Eq(true, Native.SetProcessAffinityMask(handle, (UIntPtr)(original & ~desired)));
                                FamilyPolicySetField(mode, "exclusiveCheckTicks", 0L);
                                FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", pass);
                                Eq(false, mode.ProbeCoreEngaged);
                                Eq(original & ~desired, Native.QueryAffinity(handle));
                                Eq(true, Native.SetProcessAffinityMask(handle, (UIntPtr)original));
                            }
                        }
                        FamilyPolicySetField(mode, "corePolicyCheckTicks", AutoCores.NowTicks);
                        switch (cycle % 4)
                        {
                            case 0: Eq(true, mode.SetProfileOverride(fixture.First.Id, PolicyCatalog.KeyAutoCores, "0")); break;
                            case 1: Eq(true, mode.ClearProfileOverride(fixture.First.Id, PolicyCatalog.KeyAutoCores)); break;
                            case 2: Eq(true, mode.ClearProfileOverrides(fixture.First.Id) > 0); break;
                            default: Eq(null, mode.SaveManualCorePlan(fixture.First.Id,
                                ManualCorePlan.Raw(fixture.Current(fixture.First.Id)), null)); break;
                        }
                        Eq(false, AutoPassField<bool>(FamilyPolicyInvoke(mode, "PrepareBoostPass"), "Exclusive"));
                        Eq(original, Native.QueryAffinity(handle));
                        Eq(0, ((System.Collections.IDictionary)FamilyPolicyGetField(mode, "hardPins")).Count);
                        Eq(true, (bool)typeof(GameMode).GetMethod("UnboostGames", BindingFlags.Instance | BindingFlags.NonPublic,
                            null, Type.EmptyTypes, null).Invoke(mode, null));
                        CoreIsolationClient.State = "schedule.isolation.idle";
                    }
                    var pinOnly = new ManualCorePlan(target, false, ManualCorePlan.CurrentStamp());
                    Eq(null, mode.SaveManualCorePlan(fixture.First.Id, ManualCorePlan.Raw(fixture.Current(fixture.First.Id)), pinOnly));
                    Eq(true, mode.SetProfileOverride(fixture.First.Id, PolicyCatalog.KeyAutoCores, "1"));
                    var frozen = PolicyResolver.For(fixture.Current(fixture.First.Id));
                    FamilyPolicySetField(mode, "sessionPolicy", frozen);
                    FamilyPolicyInvoke(mode, "BeginAutoCores", frozen, true);
                    object pendingPass = FamilyPolicyInvoke(mode, "PrepareBoostPass");
                    FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", pendingPass);
                    Eq(target, Native.QueryAffinity(handle));
                    Settings.SuspendWritesForReset();
                    try
                    {
                        for (int handoff = 0; handoff < 3; handoff++)
                        {
                            int nextPid = 789010 + handoff;
                            FamilyPolicySetField(mode, "activeDetection", new GameDetection { Profile = fixture.Current(fixture.First.Id),
                                RendererPid = nextPid, RendererCreation = 12345 + handoff, RendererName = "handoff-fixture", RendererCandidateSelected = true });
                            mode.RendererTestForeground = delegate { return nextPid; };
                            pendingPass = FamilyPolicyInvoke(mode, "PrepareBoostPass");
                            Eq(false, AutoPassField<bool>(pendingPass, "Exclusive"));
                            Eq(true, AutoCores.Status.Pending);
                            Eq(1, ((System.Collections.IDictionary)FamilyPolicyGetField(mode, "hardPins")).Count);
                            Eq(original, Native.QueryAffinity(handle));
                        }
                        Console.WriteLine("PASS renderer handoff: repeated identity changes cannot discard an older unresolved restore receipt");
                        FamilyPolicySetField(mode, "activeDetection", new GameDetection { Profile = fixture.Current(fixture.First.Id),
                            RendererPid = pid, RendererCreation = creation, RendererName = Native.ImageName(handle), RendererCandidateSelected = true });
                        mode.RendererTestForeground = delegate { return pid; };
                        for (int retry = 0; retry < 3; retry++)
                        {
                            FamilyPolicyInvoke(mode, "BeginAutoCores", frozen, true);
                            Eq(true, AutoCores.Status.Pending);
                            pendingPass = FamilyPolicyInvoke(mode, "PrepareBoostPass");
                            Eq(false, AutoPassField<bool>(pendingPass, "Exclusive"));
                            Eq(original, Native.QueryAffinity(handle));
                        }
                    }
                    finally { typeof(Settings).GetField("writesSuspendedForReset", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false); }
                    FamilyPolicyInvoke(mode, "BeginAutoCores", frozen, true);
                    Eq(false, AutoCores.Status.Pending);
                    pendingPass = FamilyPolicyInvoke(mode, "PrepareBoostPass");
                    FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", pendingPass);
                    Eq(target, Native.QueryAffinity(handle));
                    ulong external = original & ~target;
                    Eq(true, Native.SetProcessAffinityMask(handle, (UIntPtr)external));
                    Eq(true, (bool)FamilyPolicyInvoke(mode, "ReleaseExclusive"));
                    Eq(external, Native.QueryAffinity(handle));
                    Eq(true, Native.SetProcessAffinityMask(handle, (UIntPtr)original));
                    FamilyPolicyInvoke(mode, "BeginAutoCores", frozen, true);
                    pendingPass = FamilyPolicyInvoke(mode, "PrepareBoostPass");
                    FamilyPolicyInvoke(mode, "RunIndependentCorePlacement", pendingPass);
                    Eq(target, Native.QueryAffinity(handle));
                    typeof(GameMode).GetMethod("UnboostGames", BindingFlags.Instance | BindingFlags.NonPublic,
                        null, new[] { typeof(int), typeof(long), typeof(string) }, null).Invoke(mode,
                            new object[] { pid, creation, Native.ImageName(handle) });
                    Eq(target, Native.QueryAffinity(handle));
                    ((System.Collections.IDictionary)FamilyPolicyGetField(mode, "gameBoost")).Clear();
                    ((System.Collections.IDictionary)FamilyPolicyGetField(mode, "gameGpu")).Clear();
                    Eq(true, (bool)typeof(GameMode).GetMethod("UnboostGames", BindingFlags.Instance | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null).Invoke(mode, null));
                    Eq(original, Native.QueryAffinity(handle));
                    Eq(0, ((System.Collections.IDictionary)FamilyPolicyGetField(mode, "hardPins")).Count);
                    Console.WriteLine("PASS independent owner teardown: current healthy pin retained on handoff, empty broad ledger still restores native affinity");
                    Console.WriteLine("PASS manual native cycles=30: fixed masks, focus loss, live disable, isolation success/failure (mocked), exact restore, pending recovery retries, external affinity preserved");
                }
                finally
                {
                    mode.ProbeRestoreHardPin(pid);
                    Native.SetProcessAffinityMask(handle, (UIntPtr)original);
                    FamilyPolicyInvoke(mode, "EndAutoCores");
                    mode.CoreEngageForTest = null; mode.RendererTestForeground = null;
                    AutoCores.AllocatedForTest = oldAllocated; AutoCores.SupportedForTest = oldSupported;
                    CoreIsolationClient.State = "schedule.isolation.idle";
                }
            }
        }
    }
}
#endif
