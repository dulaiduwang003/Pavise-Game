// Compiled with verbatim methods extracted from Tamer.cs by Test-TamerRetry.ps1.
// Collaborators stay in memory; the production clock, event, loop and request methods run unchanged.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal static class Logger
    {
        internal static void Log(string text) { }
        internal static void RepeatedRecovered(string key) { }
        internal static void RepeatedError(string key, string text, long elapsed) { }
    }
    internal static class Lang { internal static string T(string key) { return key; } }
    internal enum ProcessChangeKind { Started, Stopped }
    internal sealed class ProcessChange { internal int Pid; internal long Sequence; internal string Name; internal ProcessChangeKind Kind; }
    internal sealed class ProcessChangeBatch
    {
        internal readonly ProcessChange[] Changes;
        internal readonly bool Overflowed;
        internal ProcessChangeBatch(ProcessChange[] changes, bool overflowed) { Changes = changes; Overflowed = overflowed; }
    }
    internal sealed class AntiCheatAffinityGameContext { internal string Key; }
    internal sealed class ProcessSnapshot { }
    internal static class ProcessSnapshotSource { internal static bool Fail; internal static int Calls; internal static ProcessSnapshot Capture() { Interlocked.Increment(ref Calls); if (Fail) throw new Exception("snapshot unavailable"); return new ProcessSnapshot(); } }
    internal sealed class RetryCore
    {
        internal void RetryPending() { }
        internal long CreationOf(int pid) { return pid == 99 ? 29 : 0; }
        internal string NameOf(int pid) { return pid == 99 ? "background-only" : null; }
        internal void ObserveTargetExit(int pid, long birth) { }
        internal bool PrepareAntiCheatAffinity(int pid, long birth, string name) { return true; }
    }
    internal sealed class AntiCheatAffinityRuntime
    {
        internal AntiCheatAffinityRuntime(Func<int, long, string, bool> prepare, Func<int, string> admission) { }
        internal bool Shutdown() { return true; }
    }

    internal sealed partial class TamerLoopFixture : IDisposable
    {
        private sealed class AcquireRequest { internal long Birth; }
        private readonly Dictionary<string, SweepRetryPhase> sweepRetryPhases = new Dictionary<string, SweepRetryPhase>(StringComparer.Ordinal);
        private readonly Dictionary<int, long> sweepEventBirths = new Dictionary<int, long>();
        private readonly HashSet<string> sweepObservedEvents = new HashSet<string>(StringComparer.Ordinal);
        private long sweepEventPhase;
        private string sweepPhaseKey;
        private string sweepConfigurationKey;
        private SweepRetryPhase captureRetryPhase = new SweepRetryPhase();
        private ProcessSnapshot iterationSnapshot;
        private bool iterationCaptureAttempted;
        private int mode;
        internal long Birth;
        private Dictionary<string, string> BuildActive() { return new Dictionary<string, string> { { "fixture", "group" } }; }
        private bool TryBuildAcquireRequest(int pid, string name, string group, out AcquireRequest request) { request = new AcquireRequest { Birth = Birth }; return Birth > 0; }
        private bool IsPidCurrentlyAlive(int pid) { return Birth > 0; }
        internal void Configure(int value) { mode = value; Poke(); }
        internal void TargetEvent(long sequence, bool stopped) { NotifyProcessChanges(new ProcessChangeBatch(new[] { new ProcessChange { Pid = 42, Name = "fixture", Sequence = sequence, Kind = stopped ? ProcessChangeKind.Stopped : ProcessChangeKind.Started } }, false)); }
        internal void UnrelatedExit() { NotifyProcessChanges(new ProcessChangeBatch(new[] { new ProcessChange { Pid = 99, Name = "background-only", Kind = ProcessChangeKind.Stopped } }, false)); }
        private readonly object engineSync = new object(), eventSync = new object(), evidenceSync = new object();
        private readonly List<ProcessChange> pendingChanges = new List<ProcessChange>();
        private readonly RetryCore core = new RetryCore();
        private readonly AutoResetEvent kick;
        private Thread worker;
        private volatile bool stopping;
        private int fullSweepRequested = 1, overflowSweepRequested;
        private long panicUntilUtcTicks, governanceServicesNextRead;
        private string governanceContextKey;
        private AntiCheatAffinityRuntime affinityRuntime;
        private bool affinityFastWake, affinityShutdownOk = true;
        private bool ProcessEventsAvailable { get { return true; } }
        internal volatile bool Paused;
        internal volatile bool IncrementalThrows;
        internal bool UseCapture;
        internal void ObserveOnce() { lock (engineSync) { iterationSnapshot = null; iterationCaptureAttempted = false; ObserveRecordOnlyProcesses(CaptureSweepSnapshot()); } }
        internal AntiCheatAffinityGameContext Context;
        internal Func<int, bool> SweepOutcome = delegate { return false; };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<double> attempts = new List<double>();
        private readonly List<long> handled = new List<long>();
        private int releaseCalls, incrementalCalls;
        internal TamerLoopFixture(bool initialSignal) { kick = new AutoResetEvent(initialSignal); }
        internal double[] Attempts { get { lock (evidenceSync) return attempts.ToArray(); } }
        internal long[] Handled { get { lock (evidenceSync) return handled.ToArray(); } }
        internal int ReleaseCalls { get { return Interlocked.CompareExchange(ref releaseCalls, 0, 0); } }
        internal int IncrementalCalls { get { return Interlocked.CompareExchange(ref incrementalCalls, 0, 0); } }
        internal bool OverflowPending { get { return Interlocked.CompareExchange(ref overflowSweepRequested, 0, 0) != 0; } }
        private string CheckAffinityAdmission(int pid) { return null; }
        private AntiCheatAffinityGameContext GovernanceContext() { return Context; }
        private void ObserveRecordOnlyProcesses(object snapshot) { }
        private bool ReleaseAll(string reason) { Interlocked.Increment(ref releaseCalls); return true; }
        private static int FullSweepInterval(bool eventsAvailable) { return eventsAvailable ? 60000 : 8000; }
        private void SyncAffinity(bool suspended) { }
        private bool Sweep()
        {
            int count;
            lock (evidenceSync) { attempts.Add(clock.Elapsed.TotalMilliseconds); count = attempts.Count; }
            return UseCapture ? CaptureSweepSnapshot() != null : SweepOutcome(count);
        }
        private void HandleProcessChanges(List<ProcessChange> changes)
        {
            if (changes == null || changes.Count == 0) return;
            Interlocked.Increment(ref incrementalCalls);
            if (IncrementalThrows) throw new InvalidOperationException("injected-incremental-failure");
            lock (evidenceSync) foreach (ProcessChange change in changes) handled.Add(change.Sequence);
        }
        internal void Event(long sequence) { NotifyProcessChanges(new ProcessChangeBatch(new[] { new ProcessChange { Pid = 42, Sequence = sequence } }, false)); }
        internal void Overflow() { NotifyProcessChanges(new ProcessChangeBatch(new ProcessChange[0], true)); }
        public void Dispose() { if (!Stop()) throw new Exception("Fixture worker did not stop"); kick.Dispose(); }
    }

    internal static class TamerRetryChecks
    {
        private static int checks;
        private static void Check(bool valid, string message) { checks++; if (!valid) throw new Exception(message); }
        private static void Until(Func<bool> condition, string message)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < 4000) Thread.Sleep(5);
            Check(condition(), message);
        }
        private static void Begin(TamerLoopFixture fixture) { fixture.Start(); Until(() => fixture.Attempts.Length >= 1, "No initial sweep"); }
        private static void Gaps(TamerLoopFixture fixture, string label)
        {
            double[] values = fixture.Attempts;
            for (int i = 1; i < values.Length; i++) Check(values[i] - values[i - 1] >= 900, label);
            Console.Write(label + " attempts=" + values.Length + " gaps_ms=");
            for (int i = 1; i < values.Length; i++) Console.Write((i == 1 ? "" : ",") + (values[i] - values[i - 1]).ToString("F1"));
            Console.WriteLine();
        }
        private static void NoEvent(bool initialSignal)
        {
            using (var f = new TamerLoopFixture(initialSignal))
            {
                Begin(f); Until(() => f.Attempts.Length >= 3, "No deadline retry"); f.Stop();
                Gaps(f, initialSignal ? "failure deadline bypassed by initial signal" : "failure deadline bypassed without event");
            }
        }
        private static void EventBackoff(int delay)
        {
            using (var f = new TamerLoopFixture(false))
            {
                Begin(f);
                for (int i = 1; i <= 3; i++) { Thread.Sleep(delay); f.Event(i); }
                Until(() => f.Handled.Length == 3, "Ordinary events were lost during sweep cooldown");
                Until(() => f.Attempts.Length >= 2, "Event traffic prevented deadline retry"); f.Stop();
                Gaps(f, "failure deadline bypassed by " + delay + "ms ordinary events");
                Check(f.Handled.Length == 3, "Ordinary events were replayed more than once");
            }
        }
        private static void HealthyEventsAndPoke()
        {
            using (var f = new TamerLoopFixture(true))
            {
                f.SweepOutcome = delegate { return true; }; Begin(f);
                for (int i = 1; i <= 3; i++) { Thread.Sleep(100); f.Event(i); }
                Until(() => f.Handled.Length == 3, "Healthy events were not incremental");
                Check(f.Attempts.Length == 1, "Healthy ordinary event forced a full sweep");
                f.Birth = 15; f.TargetEvent(4, false); Until(() => f.Handled.Length == 4, "Healthy new target event was not incremental");
                Check(f.Attempts.Length == 1, "Healthy real target event forced a full sweep");
                f.Poke(); Until(() => f.Attempts.Length == 2, "Explicit Poke did not sweep promptly");
                Thread.Sleep(100); f.Poke(); Until(() => f.Attempts.Length == 3, "Second Poke was lost");
                Check(f.Attempts[2] - f.Attempts[1] < 700, "Healthy configuration request was delayed");
                for (int i = 4; i <= 10; i++) { f.Poke(); int expected = i; Until(() => f.Attempts.Length == expected, "Healthy scans consumed failure budget"); }
            }
        }
        private static void ConfigurationAndContextDuringFailure()
        {
            using (var f = new TamerLoopFixture(true))
            {
                Begin(f); Thread.Sleep(100); f.Configure(1);
                Until(() => f.Attempts.Length == 2, "Configuration request waited on failed sweep deadline");
                Check(f.Attempts[1] - f.Attempts[0] < 700, "Configuration request did not bypass prior failure once");
                Thread.Sleep(100); f.Context = new AntiCheatAffinityGameContext { Key = "new-game" }; f.Event(1);
                Until(() => f.Attempts.Length == 3, "New governance context did not sweep promptly");
                Check(f.Attempts[2] - f.Attempts[1] < 700, "Governance context was delayed");
                Until(() => f.Handled.Length == 1, "Context sweep failure lost its drained event");
                for (int i = 2; i <= 4; i++) { Thread.Sleep(100); f.Event(i); }
                Until(() => f.Handled.Length == 4, "Post-request ordinary events were not handled");
                Check(f.Attempts.Length == 3, "One explicit request left an unlimited immediate retry permit");
                Until(() => f.Attempts.Length >= 4, "Retry after explicit request never ran");
                Check(f.Attempts[3] - f.Attempts[2] >= 900, "Explicit retry failure did not restart cooldown");
            }
        }
        private static void FailedSweepEvents(bool throws)
        {
            using (var f = new TamerLoopFixture(true))
            {
                if (throws) f.SweepOutcome = delegate { throw new InvalidOperationException("injected-sweep-failure"); };
                f.Event(5); f.Event(3); Begin(f);
                Until(() => f.Handled.Length == 2, "Failed full sweep lost its drained events");
                Check(f.Handled[0] == 3 && f.Handled[1] == 5, "Compensated event ordering changed");
                Thread.Sleep(100);
                Check(f.Attempts.Length == 1 && f.Handled.Length == 2, "Failed batch compensation looped or retried the sweep immediately");
            }
        }
        private static void OverflowBackoff()
        {
            using (var f = new TamerLoopFixture(true))
            {
                f.SweepOutcome = n => n > 1; Begin(f);
                f.Overflow(); Thread.Sleep(100); f.Overflow(); Thread.Sleep(100);
                Check(f.Attempts.Length == 1 && f.OverflowPending, "Overflow bypassed backoff or was dropped");
                Until(() => f.Attempts.Length >= 2, "Overflow full sweep never retried");
                Until(() => !f.OverflowPending, "Successful sweep did not consume overflow");
                Gaps(f, "failure deadline bypassed by overflow");
                f.Event(1); Until(() => f.Handled.Length == 1, "Ordinary event after recovery was lost");
                Check(f.Attempts.Length == 2, "Successful overflow left repeated full sweeps");
            }
        }
        private static void IncrementalFailureCompensation()
        {
            using (var f = new TamerLoopFixture(true))
            {
                f.SweepOutcome = delegate { return true; }; Begin(f); f.IncrementalThrows = true;
                double start = f.Attempts[0];
                f.Event(1); Until(() => f.IncrementalCalls == 1, "Incremental failure did not execute");
                for (int i = 2; i <= 4; i++) { Thread.Sleep(200); f.Event(i); }
                Until(() => f.IncrementalCalls == 4, "Incremental failures self-looped or batches were lost");
                Check(f.Attempts.Length == 1 && f.OverflowPending, "Incremental failure immediately forced a sweep or lost compensation");
                Until(() => f.Attempts.Length >= 2, "Failed incremental batch was never reconciled");
                double gap = f.Attempts[1] - start;
                Check(gap >= 900 && gap < 1450, "Repeated bad events bypassed or kept postponing full-sweep compensation");
                Check(f.IncrementalCalls == 4, "Failed incremental batch was replayed forever");
            }
        }
        private static void RecoveryAndStopRemainPrompt()
        {
            using (var f = new TamerLoopFixture(true))
            {
                Begin(f); Stopwatch clock = Stopwatch.StartNew();
                Check(f.PanicRestore(), "Panic restore failed");
                Check(clock.ElapsedMilliseconds < 500 && f.ReleaseCalls > 0, "Panic restore waited for retry deadline");
                f.Event(1); Thread.Sleep(100); Check(f.Attempts.Length == 1, "Panic window allowed a retry");
                clock.Restart(); Check(f.Stop(), "Stop failed");
                Check(clock.ElapsedMilliseconds < 500, "Stop waited for failed sweep cooldown");
            }
        }
        private static void ExhaustionAndStablePhases()
        {
            using (var f = new TamerLoopFixture(true))
            {
                Begin(f); Until(() => f.Attempts.Length == 3, "Failure phase never reached its budget");
                for (int i = 0; i < 100; i++) { f.Poke(); f.Event(i); f.Overflow(); }
                Thread.Sleep(150); Check(f.Attempts.Length == 3, "Ordinary wake or overflow refilled exhausted phase");
                f.UnrelatedExit(); Thread.Sleep(100); Check(f.Attempts.Length == 3, "Unrelated background exit refilled anti-cheat sweep phase");
                f.Configure(1); Until(() => f.Attempts.Length == 4, "Real configuration did not open a bounded phase");
                f.Configure(0); Thread.Sleep(150); Check(f.Attempts.Length == 4, "A B A refilled exhausted A");
                f.Birth = 17; f.TargetEvent(1, false); Until(() => f.Attempts.Length == 5, "New verified target did not open a phase");
                for (int i = 2; i < 50; i++) f.TargetEvent(i, false);
                Thread.Sleep(150); Check(f.Attempts.Length == 5, "Same birth with a new sequence refilled or bypassed deadline");
                f.Birth = 0; f.TargetEvent(50, true); Until(() => f.Attempts.Length == 6, "Verified target exit did not open a phase");
                f.TargetEvent(51, true); Thread.Sleep(150); Check(f.Attempts.Length == 6, "Repeated target exit refilled phase");
            }
        }
        private static void RecordOnlyCaptureSharesBudget()
        {
            ProcessSnapshotSource.Calls = 0; ProcessSnapshotSource.Fail = true;
            try
            {
                using (var f = new TamerLoopFixture(true))
                {
                    f.UseCapture = true; Begin(f); Until(() => f.Attempts.Length == 3, "Shared snapshot failures did not reach bounded phase");
                    Check(ProcessSnapshotSource.Calls == 3, "Record-only and full sweep double-captured the same failing round");
                    for (int i = 0; i < 1000; i++) f.ObserveOnce();
                    Check(ProcessSnapshotSource.Calls == 3, "Record-only observations bypassed exhausted snapshot budget");
                    ProcessSnapshotSource.Fail = false; f.Poke(); Thread.Sleep(100);
                    Check(ProcessSnapshotSource.Calls == 3, "Plain Poke bypassed exhausted snapshot budget");
                    f.Configure(1); Until(() => ProcessSnapshotSource.Calls == 4, "New real configuration did not reobserve source");
                    f.Stop();
                }
                ProcessSnapshotSource.Calls = 0;
                using (var f = new TamerLoopFixture(true))
                {
                    f.UseCapture = true; Begin(f); f.Stop();
                    Check(ProcessSnapshotSource.Calls == 1, "Successful observation and sweep failed to share their snapshot");
                }
            }
            finally { ProcessSnapshotSource.Fail = false; }
        }
        private static void CompleteSweepEndsFailureEpisode()
        {
            using (var f = new TamerLoopFixture(true))
            {
                f.SweepOutcome = n => n == 3; Begin(f);
                Until(() => f.Attempts.Length == 3, "Sweep did not recover after two failures");
                Thread.Sleep(20); f.Poke(); Until(() => f.Attempts.Length == 6, "Complete successful sweep failed to end the old failure episode");
                for (int i = 0; i < 50; i++) f.Poke(); Thread.Sleep(100);
                Check(f.Attempts.Length == 6, "New episode did not stop after three real failures");
            }
        }
        private static int Main()
        {
            try
            {
                NoEvent(false); NoEvent(true); EventBackoff(100); EventBackoff(750);
                HealthyEventsAndPoke(); ConfigurationAndContextDuringFailure();
                FailedSweepEvents(false); FailedSweepEvents(true); OverflowBackoff();
                IncrementalFailureCompensation(); RecoveryAndStopRemainPrompt(); ExhaustionAndStablePhases(); RecordOnlyCaptureSharesBudget(); CompleteSweepEndsFailureEpisode();
                Console.WriteLine("PASS TamerRetryChecks checks=" + checks + " scenarios=14 real_clock=true real_AutoResetEvent=true native_writes=0 product_started=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
