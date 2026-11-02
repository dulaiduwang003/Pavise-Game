#if PAVISE_SELFTEST && PAVISE_PERFORMANCE_RESTORE_REGRESSION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static class PerformanceRestoreRegressionRunner
    {
        private static int assertions, suites, failures;
        private static string output;
        private const BindingFlags StaticHidden = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 45000, Timeout.Infinite))
                try
                {
                    output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
                    Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                    Run("close waits for the actual admitted environment writer", CloseWaitsForWriter);
                    Run("timeout retains receipt and suppresses later bundle members", TimeoutStopsBundle);
                    Run("seal refuses new individual bundle and VBS work", SealRefusesAdmission);
                    Run("queue refusal and exceptions leave no sticky busy state", QueueFailures);
                    Run("close budget includes acquisition of the shared gate", GateAcquisitionBudget);
                    Run("individual failure details retain receipts and allow other restores", DetailedFailures);
                    Run("unavailable Defender and damaged receipts remain recoverable", UnavailableAndMalformed);
                    Run("retired Defender exclusions restore only explicit ownership and retain failures", RetiredDefenderResources);
                    Run("restore failure preserves data and stop failure remains distinct", WipeFailureBoundary);
                    Run("progress failures do not interrupt owned setting restoration", ProgressFailureBoundary);
                    Run("component restoration retains external ownership and failed receipts", delegate {
                        SelfTests.RunPerformanceRestoreRegressionTests();
                        assertions += SelfTests.PerformanceRestoreAssertions;
                    });
                    Console.WriteLine("RESULT suites=" + suites + " failed=" + failures + " assertions=" + assertions
                        + " settings=transient data=temp native_restore=false runtime_started=false");
                    return failures == 0 ? 0 : 1;
                }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
                finally
                {
                    PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null;
                    PerformanceActionBackend.TargetsForTest = null; PerformanceActionBackend.DefenderServiceForTest = null;
                    PerformanceTuning.BootForTest = null; PerformanceTuning.VbsConfiguredForTest = null;
                    PerformanceTuning.VbsChangeForTest = null; PerformanceTuning.EnvironmentQueueForTest = null;
                    LegacyPurge.RestoreHook = null; LegacyPurge.SkipRegistryDelete = false;
                    Settings.UseTransientStoreForCurrentProcess();
                }
        }
        private static void Run(string name, Action test)
        {
            suites++;
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                failures++; Console.Error.WriteLine("FAIL " + name + ": " + error);
            }
        }
        private static void Check(bool good, string message)
        { Interlocked.Increment(ref assertions); if (!good) throw new InvalidOperationException(message); }
        private static List<string> RestoreFailures()
        {
            MethodInfo method = typeof(PerformanceTuning).GetMethod("RestoreAllFailures", StaticHidden, null, new[] { typeof(Action<string>) }, null);
            Check(method != null, "Missing detailed performance restore helper");
            return new List<string>((IEnumerable<string>)method.Invoke(null, new object[] { null }));
        }
        private static string FailureText(List<string> failures)
        { return string.Join("\n", failures.ToArray()); }
        private static void CloseWaitsForWriter()
        {
            using (var fixture = new Fixture())
            {
                var coordinator = new PerformanceActionCoordinator();
                Check(coordinator.Close(0), "Empty automation coordinator failed to drain");
                fixture.HoldWrites = true;
                Check(PerformanceTuning.SetEnvironment(PerformanceActionCatalog.CpuMitigations, true, delegate { fixture.Done.Set(); }), "Individual work was not admitted");
                Check(fixture.Entered.WaitOne(3000), "Writer never reached the fake system write");
                bool closed = false; Exception closeError = null;
                var closer = new Thread(delegate()
                { try { closed = PerformanceTuning.CloseEnvironmentWork(3000); } catch (Exception error) { closeError = error; } });
                closer.IsBackground = true; closer.Start();
                Check(SpinWait.SpinUntil(delegate { return PerformanceTuning.EnvironmentWorkClosing; }, 1000), "Close did not seal admission before waiting");
                Check(!closer.Join(50), "Close returned success before the admitted write finished");
                fixture.Release.Set(); Check(closer.Join(3000), "Close did not wake after busy removal");
                Check(closeError == null && closed && fixture.Done.WaitOne(3000), "Environment work did not finish before close confirmed");
                Check(!PerformanceTuning.EnvironmentBundleBusy(), "Closed environment is still marked busy");
                Check(PerformanceActionStore.All().Count == 1, "Completed writer lost its restore receipt");
                Check(RestoreFailures().Count == 0 && PerformanceActionStore.All().Count == 0, "Completed owned setting did not restore after drain");
            }
        }
        private static void TimeoutStopsBundle()
        {
            using (var fixture = new Fixture())
            {
                fixture.HoldWrites = true; fixture.TwoCpuTargets = true;
                Check(PerformanceTuning.SetEnvironmentBundle(true, delegate { fixture.Done.Set(); }), "Bundle was not admitted");
                Check(fixture.Entered.WaitOne(3000), "Bundle did not enter the first fake write");
                string slot = PerformanceActionStore.Slot(PerformanceActionCatalog.CpuMitigations, "first");
                string prepared = Settings.LoadStr(slot, ""); Check(prepared.Length > 0, "Writer had no durable prepared receipt");
                var elapsed = Stopwatch.StartNew(); Check(!PerformanceTuning.CloseEnvironmentWork(40), "Blocked environment incorrectly drained");
                Check(elapsed.ElapsedMilliseconds < 1000, "Close ignored its time budget");
                Check(Settings.LoadStr(slot, "") == prepared, "Close timeout deleted or changed pending recovery evidence");
                fixture.Release.Set(); Check(fixture.Done.WaitOne(3000), "Timed-out bundle never completed admitted work");
                Check(PerformanceTuning.CloseEnvironmentWork(1000), "Repeated close did not confirm later drain");
                Check(fixture.Writes == 2 && fixture.ReadsFor("second") == 0 && fixture.OtherTargets == 0 && fixture.VbsWrites == 0,
                    "Closing bundle admitted a second target, later member or VBS write");
                Check(fixture.Value(PerformanceActionCatalog.CpuMitigations, "first") == "d:256"
                    && PerformanceActionStore.All().Count == 0, "Cancelled partial group was not immediately restored and verified");
                Check(RestoreFailures().Count == 0 && fixture.Value(PerformanceActionCatalog.CpuMitigations, "first") == "d:256", "Owned first target did not restore after drain");
            }
        }
        private static void SealRefusesAdmission()
        {
            using (var fixture = new Fixture())
            {
                Check(PerformanceTuning.CloseEnvironmentWork(0), "Idle environment did not seal immediately");
                Check(!PerformanceTuning.SetEnvironment(PerformanceActionCatalog.CpuMitigations, true, null), "Closed individual admission reopened");
                Check(!PerformanceTuning.SetEnvironmentBundle(true, null), "Closed bundle admission reopened");
                Check(!Settings.Load(PerformanceActionCatalog.CpuMitigations, false), "Rejected closed admission still changed user preference");
                Check(!PerformanceTuning.ApplyEnvironment(PerformanceActionCatalog.CpuMitigations, true), "Direct environment apply bypassed seal");
                Check(!PerformanceTuning.ApplyEnvironmentBundle(true), "Direct bundle apply bypassed seal");
                Check(!PerformanceTuning.ApplyEnvironmentVbs(true), "Direct VBS apply bypassed seal");
                Check(fixture.Writes == 0 && fixture.TargetCalls == 0 && fixture.VbsWrites == 0 && PerformanceActionStore.All().Count == 0,
                    "Closed environment changed a target or recovery journal");
            }
        }
        private static void QueueFailures()
        {
            foreach (bool throws in new[] { false, true })
                foreach (bool bundle in new[] { false, true })
                    using (var fixture = new Fixture())
                    {
                        PerformanceTuning.EnvironmentQueueForTest = delegate(WaitCallback work)
                        { if (throws) throw new InvalidOperationException("Injected queue failure"); return false; };
                        bool queued = bundle ? PerformanceTuning.SetEnvironmentBundle(true, null)
                            : PerformanceTuning.SetEnvironment(PerformanceActionCatalog.CpuMitigations, true, null);
                        Check(!queued, "Refused queue was reported admitted");
                        Check(!PerformanceTuning.EnvironmentBundleBusy() && PerformanceTuning.CloseEnvironmentWork(0), "Queue failure stranded busy admission");
                        Check(fixture.Writes == 0 && PerformanceActionStore.All().Count == 0, "Refused queue touched settings targets");
                        if (bundle) Check(!PerformanceTuning.EnvironmentBundleEnabled, "Refused bundle did not restore prior preferences");
                    }
        }
        private static void GateAcquisitionBudget()
        {
            using (var fixture = new Fixture())
            using (var held = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                object gate = typeof(PerformanceTuning).GetField("gate", StaticHidden).GetValue(null);
                var owner = new Thread(delegate() { lock (gate) { held.Set(); release.WaitOne(3000); } });
                owner.IsBackground = true; owner.Start(); Check(held.WaitOne(1000), "Fixture never held the gate");
                try
                {
                    var elapsed = Stopwatch.StartNew(); Check(!PerformanceTuning.CloseEnvironmentWork(40), "Contended close entered a held gate");
                    Check(elapsed.ElapsedMilliseconds < 1000 && PerformanceTuning.EnvironmentWorkClosing, "Gate acquisition was unbounded or admission remained open");
                }
                finally { release.Set(); Check(owner.Join(3000), "Fixture gate owner did not release"); }
                Check(PerformanceTuning.CloseEnvironmentWork(1000), "Previously contended close could not finish");
            }
        }
        private static void DetailedFailures()
        {
            using (var fixture = new Fixture())
            {
                fixture.Seed(PerformanceActionCatalog.StorageIdle, "restore-blocked", "d:1");
                fixture.Seed(PerformanceActionCatalog.Quantum, "restore-good", "d:38");
                fixture.FailRestoreTarget = "restore-blocked";
                List<string> failures = RestoreFailures(); string detail = FailureText(failures);
                Check(failures.Count == 1 && detail.Contains("restore-blocked"), "Boolean backend failure still reports only a generic category");
                Check(detail.Contains(PerformanceActionCatalog.StorageIdle) || detail.Contains(PerformanceActionCatalog.Find(PerformanceActionCatalog.StorageIdle).Title), "Failure omitted the actual action");
                List<PerformanceActionStore.Receipt> receipts = PerformanceActionStore.All();
                Check(receipts.Count == 1 && receipts[0].Target == "restore-blocked", "Failed restore lost its receipt or prevented the healthy restore");
                fixture.FailRestoreTarget = null; Check(RestoreFailures().Count == 0 && PerformanceActionStore.All().Count == 0, "Recovered restore was not retried");
            }
        }
        private static void UnavailableAndMalformed()
        {
            using (var fixture = new Fixture())
            {
                fixture.Seed(PerformanceActionCatalog.DefenderResources, @"C:\Fixture\Game", "1", "0"); fixture.Service = 1;
                List<string> failures = RestoreFailures(); string detail = FailureText(failures);
                Check(failures.Count == 1 && detail.Contains(@"C:\Fixture\Game"), "Unavailable service did not identify the blocked receipt");
                Check(PerformanceActionStore.All().Count == 1, "Unavailable Defender lost recovery evidence");
                fixture.Service = 4; Check(RestoreFailures().Count == 0, "Recovered fake Defender did not restore");
            }
            using (var fixture = new Fixture())
            {
                Settings.SaveStr(PerformanceActionStore.ListKey, "malformed-slot"); Settings.SaveStr("malformed-slot", "damaged-record");
                List<string> failures = RestoreFailures();
                Check(failures.Count > 0 && Settings.LoadStr("malformed-slot", "") == "damaged-record", "Unreadable journal was mistaken for no pending restore");
                Check(!PerformanceTuning.RestoreAll(), "Legacy boolean wrapper swallowed a damaged receipt");
            }
        }
        private static void RetiredDefenderResources()
        {
            const string id = PerformanceActionCatalog.DefenderResources, target = @"C:\Fixture\RetiredGame";
            using (var fixture = new Fixture())
            {
                int serviceQueries = 0;
                PerformanceActionBackend.DefenderServiceForTest = delegate { serviceQueries++; return 4; };
                Check(RestoreFailures().Count == 0 && serviceQueries == 0 && fixture.Writes == 0,
                    "Empty retirement queried or modified Defender");
                bool offered = false;
                foreach (PerformanceAction item in PerformanceActionCatalog.All) if (item.Key == id) offered = true;
                Check(!offered && PerformanceActionCatalog.Find(id) != null, "Retired resource action is offered or cannot be identified for restoration");
                Check(!PerformanceActionBackend.Apply(id, target, "1"), "Retired resource apply was admitted");
                Check(!PerformanceActionBackend.Write(id, target, "1"), "Retired resource backend still admits adding an exclusion");
                Check(serviceQueries == 0 && fixture.ReadsFor(target) == 0 && fixture.Writes == 0
                    && PerformanceActionStore.All().Count == 0, "Rejected retired apply touched Defender or left new ownership");
            }
            using (var fixture = new Fixture())
            {
                fixture.Seed(id, target, "1", "0");
                string slot = PerformanceActionStore.Slot(id, target), receipt = Settings.LoadStr(slot, "");
                Func<string, string, string, bool> writer = PerformanceActionBackend.WriteForTest;
                PerformanceActionBackend.WriteForTest = delegate(string action, string path, string value) {
                    return action == id && value == "0" ? false : writer(action, path, value);
                };
                Check(RestoreFailures().Count == 1 && fixture.Value(id, target) == "1"
                    && Settings.LoadStr(slot, "") == receipt, "Failed exclusion removal lost the original receipt or changed the exclusion");
                PerformanceActionBackend.WriteForTest = writer;
                Check(RestoreFailures().Count == 0 && fixture.Value(id, target) == "0"
                    && PerformanceActionStore.All().Count == 0, "Later retirement retry did not remove only the owned exclusion");
            }
            foreach (string current in new[] { "0", "1", "unknown" })
                using (var fixture = new Fixture())
                {
                    fixture.SetValue(id, target, current);
                    var legacy = new PerformanceActionStore.Receipt { Id = id, Target = target, Original = "1", Expected = "1", Committed = true };
                    string slot = PerformanceActionStore.Slot(id, target), receipt = PerformanceActionStore.Pack(legacy);
                    Check(Settings.SaveStr(slot, receipt), "Could not seed a pre-existing exclusion receipt");
                    List<string> failures = RestoreFailures();
                    Check(fixture.Writes == 0 && fixture.Value(id, target) == current,
                        "Retirement removed or re-created a pre-existing or externally changed exclusion");
                    Check(current == "1" ? failures.Count == 0 && PerformanceActionStore.All().Count == 0
                        : failures.Count == 1 && Settings.LoadStr(slot, "") == receipt,
                        "Retirement did not distinguish a verified no-op from unresolved external state");
                }
            using (var fixture = new Fixture())
            {
                fixture.Seed(id, target, "1", "0");
                string slot = PerformanceActionStore.Slot(id, target), receipt = Settings.LoadStr(slot, "");
                Func<string, string, string> reader = PerformanceActionBackend.ReadForTest;
                PerformanceActionBackend.ReadForTest = delegate { throw new InvalidOperationException("Injected exclusion read failure"); };
                Check(RestoreFailures().Count == 1 && fixture.Writes == 0 && Settings.LoadStr(slot, "") == receipt,
                    "Unreadable exclusion state authorized a removal or lost its recovery evidence");
                PerformanceActionBackend.ReadForTest = reader;
                Check(RestoreFailures().Count == 0 && fixture.Value(id, target) == "0", "Read-failed retirement could not recover on a later attempt");
            }
        }
        private static void WipeFailureBoundary()
        {
            using (var fixture = new Fixture())
            {
                fixture.Seed(PerformanceActionCatalog.StorageIdle, "restore-blocked", "d:1"); fixture.FailRestoreTarget = "restore-blocked";
                string data = Path.Combine(output, "wipe"); Directory.CreateDirectory(data);
                string marker = Path.Combine(data, GameProfileStore.FileName); File.WriteAllText(marker, "must-retain");
                LegacyPurge.RestoreHook = RestoreFailures; LegacyPurge.SkipRegistryDelete = true;
                int files; string error;
                Check(!LegacyPurge.WipeAll(data, false, "fixture", out files, out error), "Failed restore permitted deleting data");
                Check(files == 0 && File.ReadAllText(marker) == "must-retain" && error.Contains("restore-blocked"), "Wipe lost data or omitted concrete failure");
                int calls = 0; LegacyPurge.RestoreHook = delegate { calls++; return RestoreFailures(); };
                Check(!Program.TryResetUserData(data, delegate { return false; }, out files, out error), "Failed runtime stop admitted restoration");
                Check(calls == 0 && error == Lang.T("wipe.stopfail"), "Failed coordinator drain masqueraded as a performance restore failure");
                LegacyPurge.RestoreHook = null; LegacyPurge.SkipRegistryDelete = false;
                fixture.FailRestoreTarget = null; Check(RestoreFailures().Count == 0, "Wipe-failure receipt did not survive for a later retry");
            }
        }
        private static void ProgressFailureBoundary()
        {
            using (var fixture = new Fixture())
            {
                fixture.Seed(PerformanceActionCatalog.StorageIdle, "progress-first", "d:1");
                fixture.Seed(PerformanceActionCatalog.Quantum, "progress-second", "d:38");
                int callbacks = 0;
                List<string> failures = PerformanceTuning.RestoreAllFailures(delegate(string item)
                {
                    Check(!string.IsNullOrEmpty(item), "Progress omitted the actual restoring item");
                    callbacks++; throw new InvalidOperationException("Injected UI progress failure");
                });
                Check(callbacks == 2 && failures.Count == 0, "A failed progress callback stopped or misclassified restoration");
                Check(PerformanceActionStore.All().Count == 0 && fixture.Writes == 2,
                    "Progress failure prevented verified restores from clearing their receipts");
                Check(fixture.Value(PerformanceActionCatalog.StorageIdle, "progress-first") == "d:256"
                    && fixture.Value(PerformanceActionCatalog.Quantum, "progress-second") == "d:256",
                    "Progress exception lost an owned original value");
            }
        }
        private sealed class Fixture : IDisposable
        {
            private readonly object valueGate = new object();
            private readonly Dictionary<string, string> values = new Dictionary<string, string>();
            private readonly Dictionary<string, int> reads = new Dictionary<string, int>();
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false), Done = new ManualResetEvent(false);
            internal bool HoldWrites, TwoCpuTargets;
            internal int Writes, TargetCalls, OtherTargets, VbsWrites, Service = 4;
            internal string FailRestoreTarget;
            internal Fixture()
            {
                Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest(); Logger.WritesEnabled = false;
                string boot = BootSessionIdentity.Create(Guid.NewGuid(), 1); PerformanceTuning.BootForTest = delegate { return boot; };
                PerformanceActionBackend.DefenderServiceForTest = delegate { return Service; };
                PerformanceActionBackend.TargetsForTest = delegate(string id)
                {
                    Interlocked.Increment(ref TargetCalls);
                    if (id == PerformanceActionCatalog.CpuMitigations) return TwoCpuTargets ? new[] { "first", "second" } : new[] { "first" };
                    Interlocked.Increment(ref OtherTargets); return new[] { "other" };
                };
                PerformanceActionBackend.ReadForTest = delegate(string id, string target)
                {
                    lock (valueGate)
                    {
                        int count; reads.TryGetValue(target, out count); reads[target] = count + 1;
                        string value; return values.TryGetValue(id + "\n" + target, out value) ? value : "d:256";
                    }
                };
                PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value)
                {
                    if (HoldWrites) { Entered.Set(); if (!Release.WaitOne(4000)) throw new InvalidOperationException("Fake write barrier timed out"); }
                    if (target == FailRestoreTarget && value == "d:256") return false;
                    Interlocked.Increment(ref Writes); lock (valueGate) values[id + "\n" + target] = value; return true;
                };
                PerformanceTuning.VbsConfiguredForTest = delegate { return false; };
                PerformanceTuning.VbsChangeForTest = delegate { Interlocked.Increment(ref VbsWrites); return true; };
            }
            internal int ReadsFor(string target) { lock (valueGate) { int count; return reads.TryGetValue(target, out count) ? count : 0; } }
            internal string Value(string id, string target) { lock (valueGate) return values[id + "\n" + target]; }
            internal void SetValue(string id, string target, string value) { lock (valueGate) values[id + "\n" + target] = value; }
            internal void Seed(string id, string target, string expected, string original = "d:256")
            {
                SetValue(id, target, original);
                Check(PerformanceActionStore.Change(id, target, expected,
                    delegate { lock (valueGate) { string value; return values.TryGetValue(id + "\n" + target, out value) ? value : "d:256"; } },
                    delegate(string value) { lock (valueGate) values[id + "\n" + target] = value; return true; },
                    delegate(string value) { return value == expected; }), "Could not seed an isolated recovery receipt");
            }
            public void Dispose()
            {
                Release.Set(); Check(PerformanceTuning.CloseEnvironmentWork(3000), "Fake environment worker did not drain at fixture cleanup");
                PerformanceTuning.EnvironmentQueueForTest = null; LegacyPurge.RestoreHook = null; LegacyPurge.SkipRegistryDelete = false;
                Entered.Dispose(); Release.Dispose(); Done.Dispose();
            }
        }
    }
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Performance restoration fixtures cannot start the runtime"); }
    }
}
#endif
