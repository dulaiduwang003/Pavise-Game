#if PAVISE_SELFTEST && PAVISE_CRITICAL_THREAD_AUTO_CHECKS
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    { internal static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application startup is forbidden in automatic candidate-thread fixtures"); } }

    // Automatic candidate-thread affinity. Pure rule, selection and core choice; the observing frame
    // worker with capture and wait doubles (no ETW session, no priority write); the real target
    // factory and rule resolution with this test process standing in for the renderer; and the real
    // Forms card against a temporary profile store on a private desktop. No game, guardian or native
    // thread write is involved.
    internal static class CriticalThreadAutoChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const ulong All = 0xFFUL;
        private static readonly ulong[] Physical = { 0x03UL, 0x0CUL, 0x30UL, 0xC0UL };
        private static int checks;
        private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException("FAIL " + reason); }
        private static void Same(ulong[] actual, ulong[] expected, string reason)
        { Check(actual != null && actual.SequenceEqual(expected), reason + " (got " + (actual == null ? "null" : string.Join(",", actual.Select(x => x.ToString("X")).ToArray())) + ")"); }

        private static void Rules()
        {
            CriticalThreadRule auto = CriticalThreadRule.Auto(true);
            Check(auto.Automatic && auto.WorkerAvoidance && auto.CandidateCount == 0 && auto.Targets.Length == 0, "an automatic intent holds no processors");
            Check(auto.Encode() == "2|auto|1" && CriticalThreadRule.Auto(false).Encode() == "2|auto|0", "automatic intent encoding changed");
            CriticalThreadRule parsed = CriticalThreadRule.Parse("2|auto|1");
            Check(parsed != null && parsed.Automatic && parsed.WorkerAvoidance && parsed.Encode() == "2|auto|1", "automatic intent did not round trip");
            parsed = CriticalThreadRule.Parse("2|auto|0");
            Check(parsed != null && parsed.Automatic && !parsed.WorkerAvoidance, "automatic worker choice did not persist");
            foreach (string bad in new[] { "2|auto|2", "2|auto", "2|AUTO|1", "2|x|1", "2|auto|1|0", "3|auto|1", "1|auto|1|0", "2|auto|", " 2|auto|1", "2|auto|1 " })
                Check(CriticalThreadRule.Parse(bad) == null, "a malformed automatic intent was accepted: " + bad);
            Check(PolicyCatalog.Canonical(CriticalThreadRule.Key, "2|auto|1") == "2|auto|1" && PolicyCatalog.Canonical(CriticalThreadRule.Key, "2|auto|9") == null,
                "the profile canonicalizer disagrees with the automatic intent");
            bool refused = false;
            try { new CriticalThreadRule(CriticalThreadRule.AutomaticTopology, 1UL); } catch (ArgumentException) { refused = true; }
            Check(refused, "a manual rule claimed the automatic topology");
            string stamp = ManualCorePlan.Stamp(All, Physical);
            var avoid = new CriticalThreadRule(stamp, true, 0x40UL);
            var only = new CriticalThreadRule(stamp, false, 0x40UL);
            Check(auto.Admits(avoid) && !auto.Admits(only) && !auto.Admits(CriticalThreadRule.Auto(true)) && !auto.Admits(null),
                "an automatic intent admitted something other than a concrete rule with its worker choice");
            Check(avoid.Admits(new CriticalThreadRule(stamp, true, 0x40UL)) && !avoid.Admits(new CriticalThreadRule(stamp, true, 0x10UL)) && !avoid.Admits(only),
                "a manual rule admitted something other than itself");
            Check(auto.Validate(All, Physical, All, 0, true) == null, "four free cores refused an automatic intent");
            Check(auto.Validate(All, Physical, 0x3F, 0, true) == null, "three free cores refused an automatic intent");
            Check(auto.Validate(All, Physical, 0x54, 0, true) == null, "three physical cores with one allowed logical processor each refused an automatic intent");
            Check(auto.Validate(All, Physical, 0xA8, 0, true) == null, "odd-only logical processors refused an automatic intent");
            Check(auto.Validate(All, Physical, 0x07, 0, true) != null, "three logical processors on only two physical cores accepted an automatic intent");
            Check(auto.Validate(All, Physical, 0x0F, 0, true) != null, "two free cores accepted an automatic intent");
            Check(auto.Validate(All, Physical, All, 0xF0, true) != null, "held cores counted as automatic capacity");
            Check(auto.Validate(All, Physical, All, 0, false) != null, "an untrusted topology accepted an automatic intent");
            Check(auto.Validate(0x3F, new ulong[] { 3, 0xC, 0x30 }, 0x3F, 0, true) == null, "a three-core machine refused an automatic intent");
            Check(auto.Validate(0x0F, new ulong[] { 3, 0xC }, 0x0F, 0, true) != null, "a two-core machine accepted an automatic intent");
        }

        private static ulong[] Targets(int count, ulong[] physical, ulong allowed, ulong reserved, ulong favored, ulong performance, ulong[] domains, ulong preferred, ulong crowded = 0)
        { return CriticalThreadAutoPolicy.Targets(count, physical, allowed, reserved, favored, performance, domains, preferred, crowded); }

        private static void Cores()
        {
            Same(Targets(1, Physical, All, 0, 0, 0, null, 0), new[] { 0x40UL }, "the highest whole core away from CPU 0 was not chosen");
            Same(Targets(1, Physical, All, 0, 0x04, 0, null, 0), new[] { 0x04UL }, "a favored core was not preferred");
            Same(Targets(1, Physical, All, 0, 0x01, 0, null, 0), new[] { 0x40UL }, "the core of CPU 0 was not avoided while it was favored");
            Same(Targets(1, Physical, All, 0xC0, 0, 0, null, 0), new[] { 0x10UL }, "a held core was chosen");
            Check(Targets(1, Physical, 0x0F, 0, 0, 0, null, 0) == null, "two free cores left no worker capacity but a target was chosen");
            Same(Targets(1, Physical, 0x7F, 0, 0, 0, null, 0), new[] { 0x40UL }, "an allowed logical processor on a partial core was skipped");
            Same(Targets(1, Physical, 0xA8, 0, 0, 0, null, 0), new[] { 0x80UL }, "an automatic target escaped the odd-only allowed range");
            Same(Targets(1, Physical, 0x54, 0, 0, 0, null, 0), new[] { 0x40UL }, "three single-thread physical cores lost their valid target");
            Same(Targets(2, Physical, 0xAA, 0, 0, 0, null, 0), new[] { 0x80UL, 0x20UL }, "two targets did not stay inside four odd-only physical cores");
            Check(Targets(2, Physical, 0xA8, 0, 0, 0, null, 0) == null, "two candidates were placed on three single-thread physical cores");
            Check(Targets(1, Physical, 0x07, 0, 0, 0, null, 0) == null, "logical processor count replaced distinct physical core capacity");
            Check(Targets(1, Physical, 0x54, 0x80, 0, 0, null, 0) == null, "an allowed sibling of a reserved processor counted as free capacity");
            Same(Targets(1, new ulong[] { 3, 12, 0xC000000000000000UL }, 0x800000000000000AUL, 0, 0, 0, null, 0),
                new[] { 0x8000000000000000UL }, "the highest allowed logical processor was lost");
            ulong[] screenshot = Enumerable.Range(0, 16).Select(i => 3UL << (i * 2)).ToArray();
            Check(CriticalThreadRule.Auto(true).Validate(0xFFFFFFFFUL, screenshot, 0x5554, 0, true) == null,
                "the user's seven physical cores required their unselected SMT siblings");
            Same(Targets(1, screenshot, 0x5554, 0, 0, 0, null, 0), new[] { 0x4000UL }, "the user's 2,4,6,8,10,12,14 selection lost its valid target");
            Same(Targets(2, Physical, All, 0, 0, 0, null, 0), new[] { 0x40UL, 0x10UL }, "two candidates did not take two whole cores");
            Check(Targets(2, Physical, 0x3F, 0, 0, 0, null, 0) == null, "two candidates were placed without two worker cores");
            Check(Targets(3, Physical, All, 0, 0, 0, null, 0) == null && Targets(0, Physical, All, 0, 0, 0, null, 0) == null, "the candidate count is unbounded");
            Same(Targets(1, new ulong[] { 3, 0xC, 0x30 }, 0x3F, 0, 0, 0, null, 0), new[] { 0x10UL }, "a three-core machine lost its only valid target");
            Same(Targets(1, new ulong[] { 3, 0xC, 0x30 }, 0x3F, 0x30, 0, 0, null, 0) ?? new ulong[0], new ulong[0], "the core of CPU 0 counted as spare capacity");
            // Four performance cores with SMT and four efficiency cores.
            ulong[] hybrid = { 0x03, 0x0C, 0x30, 0xC0, 0x100, 0x200, 0x400, 0x800 };
            Same(Targets(1, hybrid, 0xFFF, 0, 0, 0xFF, null, 0), new[] { 0x40UL }, "an efficiency core was chosen on a hybrid part");
            Same(Targets(1, hybrid, 0xF0F, 0, 0, 0xFF, null, 0), new[] { 0x04UL }, "an allowed performance core was not preferred");
            // Two cache domains: the large-cache domain of an asymmetric part wins over a favored core elsewhere.
            ulong[] domains = { 0x0F, 0xF0 };
            Same(Targets(1, Physical, All, 0, 0x10, 0, domains, 0x0F), new[] { 0x04UL }, "the large-cache domain was not preferred");
            Same(Targets(1, Physical, All, 0x0C, 0, 0, domains, 0x0F), new[] { 0x40UL }, "the core of CPU 0 was used while another domain had room");
            Same(Targets(1, Physical, All, 0, 0x10, 0, domains, 0), new[] { 0x10UL }, "the domain holding the favored core was not chosen");
            Same(Targets(2, Physical, All, 0, 0, 0, domains, 0), new[] { 0x40UL, 0x10UL }, "two candidates left their cache domain");
            // Background and anti-cheat confinement shares the last core of a uniform part (a 6C/12T laptop chip without favored cores).
            ulong[] uniform = { 0x003, 0x00C, 0x030, 0x0C0, 0x300, 0xC00 };
            Same(Targets(1, uniform, 0xFFF, 0, 0, 0, null, 0, 0xC00), new[] { 0x100UL }, "the critical thread joined the throttle core of confined background work");
            Same(Targets(1, uniform, 0xFFF, 0, 0, 0, null, 0), new[] { 0x400UL }, "without confinement the highest core was not chosen");
            Same(Targets(1, Physical, All, 0, 0x40, 0, null, 0, 0xC0), new[] { 0x10UL }, "a favored core inside the throttle cores outranked an uncrowded core");
            Same(Targets(2, uniform, 0xFFF, 0, 0, 0, null, 0, 0xC00), new[] { 0x100UL, 0x40UL }, "two candidates did not both leave the throttle core");
            Same(Targets(1, new ulong[] { 3, 0xC, 0x30 }, 0x3F, 0, 0, 0, null, 0, 0x3C), new[] { 0x10UL }, "throttle avoidance pushed the candidate onto the core of CPU 0");
            Same(Targets(1, Physical, All, 0, 0, 0, null, 0, 0xFF), new[] { 0x40UL }, "a fully crowded part lost its target instead of falling back");
            ulong[] hybridParts = { 0x03, 0x0C, 0x30, 0xC0, 0x100, 0x200, 0x400, 0x800 };
            Same(Targets(1, hybridParts, 0xFFF, 0, 0, 0xFF, null, 0, 0xF00), new[] { 0x40UL }, "efficiency-core confinement changed a hybrid choice");
        }

        private static void Scenario(bool value, string reason, ulong allowed, ulong reserved, int count)
        {
            if (!value) throw new InvalidOperationException("FAIL exhaustive range " + reason + "; allowed=" + allowed.ToString("X2")
                + "; reserved=" + reserved.ToString("X2") + "; candidates=" + count);
        }

        // This oracle enumerates logical CPU IDs and maps CPU / 2 to physical IDs. It deliberately
        // does not use the policy's physical-mask predicates or WholeCores to decide capacity.
        private static void ExhaustiveRanges()
        {
            int scenarios = 0, plans = 0, manualRefusals = 0;
            string stamp = ManualCorePlan.Stamp(All, Physical);
            CriticalThreadRule automatic = CriticalThreadRule.Auto(true);
            var machine = new HardThreadMachine { Read = HardThreadRead.Known, GroupCount = 1, Group = 0,
                ProcessMask = All, PhysicalCores = Physical, CpuSetsEmpty = true };
            Guid session = new Guid("a01f5dfb-f319-485a-a9a5-cb2533a56b2f");
            HardThreadIdentity[] identities = { new HardThreadIdentity(7101, 17101), new HardThreadIdentity(7102, 17102) };
            for (ulong allowed = 1; allowed <= All; allowed++) {
                var allowedCores = new HashSet<int>();
                for (int cpu = 0; cpu < 8; cpu++) if ((allowed & (1UL << cpu)) != 0) allowedCores.Add(cpu / 2);
                for (ulong reserved = 0; reserved <= All; reserved++) {
                    var heldCores = new HashSet<int>();
                    for (int cpu = 0; cpu < 8; cpu++) if ((reserved & (1UL << cpu)) != 0) heldCores.Add(cpu / 2);
                    var freeCores = new HashSet<int>(allowedCores); freeCores.ExceptWith(heldCores);
                    ulong unreservedAllowed = 0;
                    for (int cpu = 0; cpu < 8; cpu++)
                        if ((allowed & (1UL << cpu)) != 0 && !heldCores.Contains(cpu / 2)) unreservedAllowed |= 1UL << cpu;
                    for (int count = 1; count <= 2; count++) {
                        scenarios++;
                        bool capacity = freeCores.Count >= count + 2;
                        ulong[] picked = Targets(count, Physical, allowed, reserved, 0, 0, null, 0);
                        Scenario((picked != null) == capacity, "target availability differs from logical-ID oracle", allowed, reserved, count);
                        if (count == 1)
                            Scenario((automatic.Validate(All, Physical, allowed, reserved, true) == null) == capacity,
                                "automatic rule capacity differs from logical-ID oracle", allowed, reserved, count);
                        if (picked == null) {
                            // When enough distinct candidates exist but fewer than two worker cores
                            // remain, a concrete/manual rule and the low-level plan must also refuse.
                            if (freeCores.Count >= count) {
                                var candidates = new List<ulong>(); var candidateCores = new HashSet<int>();
                                for (int cpu = 0; cpu < 8 && candidates.Count < count; cpu++)
                                    if ((unreservedAllowed & (1UL << cpu)) != 0 && candidateCores.Add(cpu / 2)) candidates.Add(1UL << cpu);
                                ulong[] manual = candidates.ToArray();
                                Scenario(new CriticalThreadRule(stamp, true, manual).Validate(All, Physical, allowed, reserved, true) != null,
                                    "manual rule accepted insufficient remaining physical cores", allowed, reserved, count);
                                var target = new HardThreadTarget(session, 7100, 17100, @"C:\PaviseFake\oracle.exe", unreservedAllowed);
                                var choices = manual.Select((mask, i) => new HardThreadChoice(identities[i], mask)).ToArray();
                                foreach (HardThreadWorkerMode mode in new[] { HardThreadWorkerMode.AvoidWorkers, HardThreadWorkerMode.CandidatesOnly }) {
                                    bool refused = false;
                                    try { HardThreadPlan.Create(target, machine, choices, mode); } catch (InvalidOperationException) { refused = true; }
                                    Scenario(refused, "low-level plan accepted insufficient remaining physical cores in " + mode, allowed, reserved, count);
                                }
                                manualRefusals++;
                            }
                            continue;
                        }
                        Scenario(picked.Length == count, "target count changed", allowed, reserved, count);
                        var pickedCores = new HashSet<int>();
                        for (int index = 0; index < picked.Length; index++) {
                            int selectedCpu = -1;
                            for (int cpu = 0; cpu < 8; cpu++) if (picked[index] == (1UL << cpu)) selectedCpu = cpu;
                            Scenario(selectedCpu >= 0, "target is not one known logical processor", allowed, reserved, count);
                            Scenario((allowed & (1UL << selectedCpu)) != 0 && !heldCores.Contains(selectedCpu / 2)
                                && pickedCores.Add(selectedCpu / 2), "target escaped allowed range, shared a physical core or used a reserved core", allowed, reserved, count);
                        }
                        Scenario(new CriticalThreadRule(stamp, true, picked).Validate(All, Physical, allowed, reserved, true) == null
                            && new CriticalThreadRule(stamp, false, picked).Validate(All, Physical, allowed, reserved, true) == null,
                            "concrete rule refused oracle-valid targets", allowed, reserved, count);
                        ulong expectedWorkers = 0; var workerCores = new HashSet<int>();
                        for (int cpu = 0; cpu < 8; cpu++)
                            if ((allowed & (1UL << cpu)) != 0 && !heldCores.Contains(cpu / 2) && !pickedCores.Contains(cpu / 2)) {
                                expectedWorkers |= 1UL << cpu; workerCores.Add(cpu / 2);
                            }
                        Scenario(workerCores.Count >= 2, "oracle-valid targets left fewer than two worker cores", allowed, reserved, count);
                        try {
                            var target = new HardThreadTarget(session, 7100, 17100, @"C:\PaviseFake\oracle.exe", unreservedAllowed);
                            var choices = picked.Select((mask, i) => new HardThreadChoice(identities[i], mask)).ToArray();
                            foreach (HardThreadWorkerMode mode in new[] { HardThreadWorkerMode.AvoidWorkers, HardThreadWorkerMode.CandidatesOnly }) {
                                HardThreadPlan plan = HardThreadPlan.Create(target, machine, choices, mode);
                                Scenario(plan.Workers == expectedWorkers, "worker mask expanded, lost allowed processors or retained a candidate/reserved sibling", allowed, reserved, count);
                            }
                        } catch (Exception error) {
                            throw new InvalidOperationException("FAIL exhaustive range low-level plan; allowed=" + allowed.ToString("X2")
                                + "; reserved=" + reserved.ToString("X2") + "; candidates=" + count, error);
                        }
                        plans++;
                    }
                }
            }
            Check(scenarios == 255 * 256 * 2 && plans > 0 && manualRefusals > 0, "the exhaustive range oracle skipped scenarios");
            Console.WriteLine("PASS critical thread exhaustive range scenarios=" + scenarios + "; valid plans=" + plans + "; insufficient manual cases=" + manualRefusals);
        }

        private static CriticalThreadHotspot Hot(int tid, long creation, int windows, double share)
        { return new CriticalThreadHotspot(new CoreThreadCandidate { Tid = tid, Creation = creation, Windows = windows, Mean = share }); }

        private static void Presenter()
        {
            var present = new CriticalThreadRoleFact(101, 10, "presentation-associated");
            var depend = new CriticalThreadRoleFact(102, 20, "dependency-associated");
            CriticalThreadHotspot[] hot = { Hot(103, 30, 9, .95), Hot(102, 20, 5, .8), Hot(101, 10, 3, .5) };
            HardThreadIdentity[] picked = CriticalThreadAutoPolicy.SelectPresenter(hot, new[] { depend, present });
            Check(picked.Length == 1 && picked[0].Tid == 101 && picked[0].Creation == 10, "the presenting hotspot was not chosen alone");
            Check(CriticalThreadAutoPolicy.SelectPresenter(new[] { Hot(101, 10, 3, .34) }, new[] { present }).Length == 0, "a presenter below the hotspot share was chosen");
            Check(CriticalThreadAutoPolicy.SelectPresenter(new[] { Hot(101, 10, 2, .9) }, new[] { present }).Length == 0, "a presenter without sustained windows was chosen");
            Check(CriticalThreadAutoPolicy.SelectPresenter(new[] { Hot(101, 11, 5, .9) }, new[] { present }).Length == 0, "a recreated thread inherited the presenter role");
            Check(CriticalThreadAutoPolicy.SelectPresenter(hot, new[] { depend }).Length == 0, "a dependency thread was chosen without a presenter");
            Check(CriticalThreadAutoPolicy.SelectPresenter(hot, new[] { present, new CriticalThreadRoleFact(103, 30, "presentation-associated") }).Length == 0,
                "an ambiguous presenter role was resolved by guessing");
            Check(CriticalThreadAutoPolicy.SelectPresenter(hot, new CriticalThreadRoleFact[0]).Length == 0 && CriticalThreadAutoPolicy.SelectPresenter(null, null).Length == 0,
                "a presenter was chosen without role facts");
        }

        private static ThreadTimes Sample(int window, double[] loads)
        {
            long elapsed = window * 5L * TimeSpan.TicksPerSecond;
            var sample = new ThreadTimes { Creation = 9000, Ticks = 1000000000L + elapsed, ProcessCpu = elapsed * 3,
                Ids = new long[loads.Length], Created = new long[loads.Length], Cpu = new long[loads.Length] };
            for (int i = 0; i < loads.Length; i++) { sample.Ids[i] = 101 + i; sample.Created[i] = 10 * (i + 1); sample.Cpu[i] = (long)(elapsed * loads[i]); }
            return sample;
        }

        private static void Hotspots()
        {
            double[] loads = { .9, .4, .7, .5, .1 };
            var observation = new CriticalThreadObservation();
            for (int window = 0; window <= 3; window++) { ThreadTimes sample = Sample(window, loads); Check(observation.Observe(sample, 9000, sample.Ticks), "hotspot fixture sample refused"); }
            int[] all = observation.Hotspots.Select(x => x.Tid).ToArray(), busiest = observation.Candidates.Select(x => x.Tid).ToArray();
            Check(all.SequenceEqual(new[] { 101, 103, 104, 102 }), "sustained hotspots beyond the two busiest were lost: " + string.Join(",", all.Select(x => x.ToString()).ToArray()));
            Check(busiest.SequenceEqual(new[] { 101, 103 }), "manual rules no longer see exactly the two busiest hotspots");
            observation.Reset();
            Check(observation.Hotspots.Length == 0 && observation.Candidates.Length == 0, "reset kept hotspot observations");
            var tracker = new CoreThreadTracker();
            Check(tracker.Observe(Sample(0, loads), Sample(1, loads)).Count == 0, "a first window produced hotspots");
            Check(new CoreThreadTracker().Observe(Sample(0, loads), Sample(1, loads), 0).Count == 0, "a zero keep produced hotspots");
        }

        private sealed class ThreadsFake : IFrameThreads
        {
            internal int Writes;
            public int Read(FrameThreadReceipt id, out int value) { value = 0; return 0; }
            public bool Set(FrameThreadReceipt id, int expected, int desired) { Writes++; return false; }
        }

        private static void WaitUntil(Func<bool> condition, string reason)
        {
            for (int i = 0; i < 500 && !condition(); i++) Thread.Sleep(10);
            Check(condition(), reason);
        }

        private static FrameCoordinationSample Presenting(int target, long created, bool presenterOnly)
        {
            var sample = new FrameCoordinationSample { Pid = target, Birth = created, PresentTid = 101, Valid = true, Issue = FrameCaptureIssue.None, PresenterOnly = presenterOnly };
            sample.Threads[101] = 10;
            return sample;
        }

        private static void Observer()
        {
            var fake = new ThreadsFake();
            FrameCoordination.ResetWorkerForTest(); Settings.UseTransientStoreForCurrentProcess(); FrameCoordination.SetPlatformForTest(fake);
            FrameCoordination.ProcessStateForTest = delegate { return 0; };
            bool admitted = true; Func<bool> admission = delegate { return admitted; };
            int captures = 0; var modes = new List<bool>(); var waits = new List<int>();
            try
            {
                FrameCoordination.CaptureForTest = delegate(int target, long created, Func<bool> alive, bool presenterOnly)
                { captures++; modes.Add(presenterOnly); return Presenting(target, created, presenterOnly); };
                FrameCoordination.WaitForTest = delegate(int ms, Func<bool> alive) { waits.Add(ms); if (waits.Count >= 2) admitted = false; return alive(); };
                FrameCoordination.EnsureObserver(505, 999, "observer fixture", admission);
                Check(FrameCoordination.DrainWorkerForTest(5000), "the observer did not stop when its admission ended");
                Check(captures == 2 && modes.All(x => x), "the observer captured without presenter-only mode");
                Check(waits.Count == 2 && waits.All(x => x == FrameCoordination.ObserveGapMs), "the observer waited other than its observation gap");
                Check(fake.Writes == 0, "the observer wrote a thread priority");
                string journal;
                Check(Settings.TryLoadStr(FrameCoordination.JournalKey, out journal) && journal.Length == 0, "the observer left a priority receipt");
                long now = AutoCores.NowTicks;
                var latest = new ThreadTimes { Creation = 999, Ticks = now, ProcessCpu = 0, Ids = new long[] { 101 }, Created = new long[] { 10 }, Cpu = new long[] { 0 } };
                CriticalThreadRoleFact[] roles = FrameCoordination.ReadCriticalRoles(505, 999, latest, now);
                Check(roles.Length == 1 && roles[0].Tid == 101 && roles[0].Role == "presentation-associated", "the observer did not publish the presenter role");

                // Holding waits keep a worker alive until its generation is retired.
                admitted = true;
                FrameCoordination.WaitForTest = delegate(int ms, Func<bool> alive) { while (alive()) Thread.Sleep(5); return false; };
                FrameCoordination.EnsureObserver(505, 999, "observer fixture", admission);
                WaitUntil(delegate { return FrameCoordination.Observing; }, "the observer did not start");
                FrameCoordination.ReleaseObserver();
                Check(FrameCoordination.DrainWorkerForTest(5000) && !FrameCoordination.Observing, "ReleaseObserver did not stop the observer");

                FrameCoordination.EnsureObserver(505, 999, "observer fixture", admission);
                WaitUntil(delegate { return FrameCoordination.Observing; }, "the observer did not restart");
                bool released = FrameCoordination.ReleaseBoost(505, 999);
                Thread.Sleep(300);
                Check(released && FrameCoordination.Observing, "a boost release stopped an observer that is still wanted");
                Check(FrameCoordination.ReleaseBoost(0, 0), "a boost release without an observer did not settle");
                Check(FrameCoordination.DrainWorkerForTest(5000) && !FrameCoordination.Observing, "a boost release kept an observer nobody wants");

                FrameCoordination.EnsureObserver(505, 999, "observer fixture", admission);
                WaitUntil(delegate { return FrameCoordination.Observing; }, "the observer did not start before the boost");
                FrameCoordination.Ensure(505, 999, "boost fixture", admission);
                Check(FrameCoordination.DrainWorkerForTest(5000) && !FrameCoordination.Observing, "a boost Ensure did not retire the observer of the same renderer");

                // A live boost worker already serves roles: an observer request neither replaces nor stops it.
                FrameCoordination.ResetWorkerForTest(); Settings.UseTransientStoreForCurrentProcess(); FrameCoordination.SetPlatformForTest(fake);
                FrameCoordination.ProcessStateForTest = delegate { return 0; };
                FrameCoordination.CaptureForTest = delegate(int target, long created, Func<bool> alive, bool presenterOnly)
                { return new FrameCoordinationSample { Pid = target, Birth = created }.Reject(FrameCaptureIssue.InsufficientEvidence); };
                FrameCoordination.WaitForTest = delegate(int ms, Func<bool> alive) { while (alive()) Thread.Sleep(5); return false; };
                FrameCoordination.Ensure(505, 999, "boost fixture", admission);
                WaitUntil(delegate { return !FrameCoordination.DrainWorkerForTest(0); }, "the boost fixture did not start");
                FrameCoordination.EnsureObserver(505, 999, "observer fixture", admission);
                FrameCoordination.ReleaseObserver();
                Check(!FrameCoordination.Observing && !FrameCoordination.DrainWorkerForTest(200), "an observer request disturbed a live boost worker");
                Check(FrameCoordination.ReleaseExcept(0, 0) && FrameCoordination.DrainWorkerForTest(5000), "the boost fixture did not stop");

                // Admission: a blocked capture never observes; game-managed priorities do not stop observation, capture failures do.
                FrameCoordination.ResetWorkerForTest(); Settings.UseTransientStoreForCurrentProcess(); FrameCoordination.SetPlatformForTest(fake);
                FrameCoordination.ProcessStateForTest = delegate { return 0; };
                FrameCoordination.WaitForTest = delegate(int ms, Func<bool> alive) { while (alive()) Thread.Sleep(5); return false; };
                FrameCoordination.CaptureForTest = delegate(int target, long created, Func<bool> alive, bool presenterOnly) { return Presenting(target, created, presenterOnly); };
                ActionFailureStore.Trip(FrameCoordination.ActionKey, "blocked observer", "fixture isolation");
                FrameCoordination.EnsureObserver(506, 999, "blocked observer", admission);
                Check(!FrameCoordination.Observing && FrameCoordination.DrainWorkerForTest(0), "a blocked capture started an observer");
                FrameCaptureSession managed = FrameCoordination.CaptureSessionForTest(507, 999);
                managed.Stopped = true; managed.NoChangeNeeded = true;
                FrameCoordination.EnsureObserver(507, 999, "game-managed observer", admission);
                WaitUntil(delegate { return FrameCoordination.Observing; }, "game-managed priorities stopped an observation that writes none");
                FrameCoordination.ReleaseObserver(); Check(FrameCoordination.DrainWorkerForTest(5000), "the game-managed observer did not stop");
                FrameCaptureSession failed = FrameCoordination.CaptureSessionForTest(508, 999);
                failed.Stopped = true; failed.Failures = FrameCoordination.MaxCaptureFailures;
                FrameCoordination.EnsureObserver(508, 999, "failed observer", admission);
                Check(!FrameCoordination.Observing && FrameCoordination.DrainWorkerForTest(0), "an observer started after its capture budget was exhausted");

                // A game-managed session keeps observing through insufficient samples, within the capture budget.
                Func<int, long, Func<bool>, bool, FrameCoordinationSample> insufficient = delegate(int target, long created, Func<bool> alive, bool presenterOnly)
                { captures++; return new FrameCoordinationSample { Pid = target, Birth = created, PresenterOnly = presenterOnly }.Reject(FrameCaptureIssue.InsufficientEvidence); };
                FrameCaptureSession retried = FrameCoordination.CaptureSessionForTest(510, 999);
                retried.Stopped = true; retried.NoChangeNeeded = true;
                captures = 0; waits.Clear();
                FrameCoordination.CaptureForTest = insufficient;
                FrameCoordination.WaitForTest = delegate(int ms, Func<bool> alive) { waits.Add(ms); return alive(); };
                FrameCoordination.EnsureObserver(510, 999, "insufficient observer", admission);
                Check(FrameCoordination.DrainWorkerForTest(5000) && captures == FrameCoordination.MaxCaptureFailures
                    && waits.Count == FrameCoordination.MaxCaptureFailures - 1 && waits.All(x => x == FrameCoordinationRetry.BaseDelayMs),
                    "a game-managed observer did not retry an insufficient sample up to its capture budget (captures=" + captures + " waits=" + waits.Count + ")");
                FrameCoordination.EnsureObserver(510, 999, "insufficient observer", admission);
                Check(!FrameCoordination.Observing && FrameCoordination.DrainWorkerForTest(0) && captures == FrameCoordination.MaxCaptureFailures,
                    "an observer restarted after its capture budget ran out");

                // Handoff: the boost worker's current facts stay readable when the observer of the same renderer
                // takes over; a boost that never runs again leaves that observer alone; released facts stay retired.
                FrameCoordination.ResetWorkerForTest(); Settings.UseTransientStoreForCurrentProcess(); FrameCoordination.SetPlatformForTest(fake);
                FrameCoordination.ProcessStateForTest = delegate { return 0; };
                FrameCaptureSession handed = FrameCoordination.CaptureSessionForTest(505, 999);
                handed.Stopped = true; handed.NoChangeNeeded = true;
                FrameCoordination.SeedCriticalRolesForTest(Presenting(505, 999, false), 40, AutoCores.NowTicks);
                Func<ThreadTimes> presenterAlive = delegate {
                    return new ThreadTimes { Creation = 999, Ticks = AutoCores.NowTicks, ProcessCpu = 0, Ids = new long[] { 101 }, Created = new long[] { 10 }, Cpu = new long[] { 0 } }; };
                Check(FrameCoordination.ReadCriticalRoles(505, 999, presenterAlive(), AutoCores.NowTicks).Length == 1, "the seeded boost facts were not readable");
                FrameCoordination.CaptureForTest = insufficient;
                FrameCoordination.WaitForTest = delegate(int ms, Func<bool> alive) { while (alive()) Thread.Sleep(5); return false; };
                FrameCoordination.EnsureObserver(505, 999, "handoff observer", admission);
                WaitUntil(delegate { return FrameCoordination.Observing; }, "the handoff observer did not start");
                Check(FrameCoordination.ReadCriticalRoles(505, 999, presenterAlive(), AutoCores.NowTicks).Length == 1,
                    "the observer start retired the boost worker's current facts");
                FrameCoordination.Ensure(505, 999, "handoff observer", admission);
                Thread.Sleep(200);
                Check(FrameCoordination.Observing && FrameCoordination.ReadCriticalRoles(505, 999, presenterAlive(), AutoCores.NowTicks).Length == 1,
                    "a boost that never runs again retired the observer of a game-managed renderer");
                FrameCoordination.ReleaseObserver();
                Check(FrameCoordination.DrainWorkerForTest(5000) && !FrameCoordination.Observing, "the handoff observer did not stop");
                Check(FrameCoordination.ReadCriticalRoles(505, 999, presenterAlive(), AutoCores.NowTicks).Length == 0, "a released observation left its facts readable");
                FrameCoordination.EnsureObserver(505, 999, "handoff observer", admission);
                WaitUntil(delegate { return FrameCoordination.Observing; }, "the handoff observer did not restart");
                Check(FrameCoordination.ReadCriticalRoles(505, 999, presenterAlive(), AutoCores.NowTicks).Length == 0, "a new observer revived retired facts");
                FrameCoordination.ReleaseObserver();
                Check(FrameCoordination.DrainWorkerForTest(5000), "the restarted handoff observer did not stop");
                admitted = false;
                FrameCoordination.EnsureObserver(509, 999, "unadmitted observer", admission);
                Check(!FrameCoordination.Observing && FrameCoordination.DrainWorkerForTest(0), "an unadmitted observer started");
            }
            finally
            {
                admitted = false;
                FrameCoordination.DrainWorkerForTest(5000);
                FrameCoordination.ResetWorkerForTest(); FrameCoordination.SetPlatformForTest(null);
                Settings.UseTransientStoreForCurrentProcess();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private sealed class Self { internal IntPtr Handle; internal int Pid; internal long Birth; internal ulong Mask; internal string Name, Path; }
        private static Self Open()
        {
            var self = new Self { Pid = System.Diagnostics.Process.GetCurrentProcess().Id };
            self.Handle = OpenProcess(0x1000, false, self.Pid);
            if (self.Handle == IntPtr.Zero) throw new InvalidOperationException("FAIL a query-limited handle to the test process could not be opened");
            long cpu; ulong io;
            if (!Native.QueryProcessSample(self.Handle, out self.Birth, out cpu, out io) || !Native.TryQueryAffinity(self.Handle, out self.Mask))
                throw new InvalidOperationException("FAIL the test process could not be read through its query-limited handle");
            self.Name = Native.ImageName(self.Handle); self.Path = Native.ImagePath(self.Handle);
            Check((self.Mask & All) == All && !string.IsNullOrEmpty(self.Name) && !string.IsNullOrEmpty(self.Path), "the test process affinity does not cover the fixture processors");
            return self;
        }
        private static void Set(object owner, string name, object value)
        {
            FieldInfo field = typeof(GameMode).GetField(name, Hidden);
            if (field == null) throw new InvalidOperationException("FAIL missing GameMode field " + name);
            field.SetValue(owner, value);
        }
        private static GameMode Mode(Self self, GameProfile profile)
        {
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            Set(mode, "sync", new object()); Set(mode, "criticalProcessGate", new object()); Set(mode, "allMask", All);
            Set(mode, "dataDir", Path.Combine(Path.GetTempPath(), "pavise-critical-auto-fixture"));
            Set(mode, "profileStore", FormatterServices.GetUninitializedObject(typeof(GameProfileStore)));
            Set(mode, "enabled", true); Set(mode, "active", true); Set(mode, "gameSessionStarts", 1L); Set(mode, "criticalIntentEpoch", 1L);
            Set(mode, "profiles", new List<GameProfile> { profile });
            Set(mode, "activeDetection", new GameDetection { Profile = profile.Clone(), RendererPid = self.Pid, RendererCreation = self.Birth,
                RendererName = self.Name, RendererPath = self.Path, RendererCandidateSelected = true });
            return mode;
        }
        private static GameProfile Profile(string raw, string coreRule)
        {
            var profile = new GameProfile { Id = "critical-auto-fixture", Name = "Critical auto fixture" };
            profile.Overrides[CriticalThreadRule.Key] = raw;
            profile.Overrides[PolicyCatalog.KeyAutoCores] = coreRule;
            return profile;
        }
        // One real CreateCriticalThreadTarget call with the placement gate held.
        private static GameMode.CriticalThreadTargetLease Issue(Self self, string storedRaw, CriticalThreadRule bound, ulong allowed = All)
        {
            GameProfile profile = Profile(storedRaw, allowed == All ? "0" : "1");
            if (allowed != All) profile.Overrides[ManualCorePlan.Key] = new ManualCorePlan(allowed, false, ManualCorePlan.Stamp(All, Physical)).Encode();
            GameMode mode = Mode(self, profile);
            Type passType = typeof(GameMode).GetNestedType("BoostPass", BindingFlags.NonPublic);
            MethodInfo create = typeof(GameMode).GetMethod("CreateCriticalThreadTarget", Hidden);
            if (passType == null || create == null) throw new InvalidOperationException("FAIL the hard-affinity target factory changed");
            object pass = Activator.CreateInstance(passType, true);
            passType.GetField("RendererPid").SetValue(pass, self.Pid);
            passType.GetField("RendererCreation").SetValue(pass, self.Birth);
            passType.GetField("RendererName").SetValue(pass, self.Name);
            object gate = typeof(GameMode).GetField("criticalProcessGate", Hidden).GetValue(mode);
            Monitor.Enter(gate);
            try { return (GameMode.CriticalThreadTargetLease)create.Invoke(mode, new object[] { pass, self.Handle, bound, "critical-auto-fixture", storedRaw, self.Mask, 1L, 1L }); }
            finally { Monitor.Exit(gate); }
        }
        private static object Source(Self self, GameMode mode, string raw)
        {
            Type type = typeof(GameMode).GetNestedType("CriticalRuntimeSource", BindingFlags.NonPublic);
            ConstructorInfo create = type == null ? null : type.GetConstructor(Hidden, null, new[] { typeof(GameDetection), typeof(string), typeof(long), typeof(long) }, null);
            if (create == null) throw new InvalidOperationException("FAIL the runtime source changed");
            var detection = (GameDetection)typeof(GameMode).GetField("activeDetection", Hidden).GetValue(mode);
            return create.Invoke(new object[] { detection, raw, 1L, 1L });
        }
        private static bool Resolve(GameMode mode, object source, HardThreadIdentity[] threads, out CriticalThreadRule rule, out HardThreadChoice[] choices, out string refusal)
        {
            MethodInfo resolve = typeof(GameMode).GetMethod("ResolveCriticalAutoRule", Hidden);
            if (resolve == null) throw new InvalidOperationException("FAIL the automatic rule resolution changed");
            object[] args = { source, threads, null, null, null };
            bool ok = (bool)resolve.Invoke(mode, args);
            rule = (CriticalThreadRule)args[2]; choices = (HardThreadChoice[])args[3]; refusal = (string)args[4];
            return ok;
        }

        private static void Factory()
        {
            Self self = Open();
            Func<ulong> allocated = AutoCores.AllocatedForTest;
            bool? supported = AutoCores.SupportedForTest;
            ulong favored = CpuTopology.FavoredMask, throttle = CpuTopology.ThrottleMask;
            try
            {
                CpuTopology.InjectTopologyForTest(All, Physical, new ulong[0], 0, 0, 0, 0, false, false);
                CpuTopology.InjectCacheDomainsForTest(null); CpuTopology.FavoredMask = 0; CpuTopology.ThrottleMask = 0;
                CpuTopology.MultiGroup = false; CoreIsolationClient.ActiveMask = 0; AutoCores.SupportedForTest = true;
                ulong held = 0; AutoCores.AllocatedForTest = delegate { return held; };
                string stamp = ManualCorePlan.Stamp(All, Physical);
                GameMode.CriticalThreadTargetLease lease = Issue(self, "2|auto|1", new CriticalThreadRule(stamp, true, 0x40UL));
                Check(lease != null && lease.WorkerAvoidanceAllowed && lease.AllowedMask == All, "an automatic intent did not admit its concrete rule");
                Check(Issue(self, "2|auto|1", new CriticalThreadRule(stamp, false, 0x40UL)) == null, "an automatic intent admitted a concrete rule with another worker choice");
                lease = Issue(self, "2|auto|0", new CriticalThreadRule(stamp, false, 0x40UL));
                Check(lease != null && !lease.WorkerAvoidanceAllowed, "an automatic intent without worker avoidance lost its own choice");
                Check(Issue(self, "2|auto|1", CriticalThreadRule.Auto(true)) == null, "an automatic intent was bound without concrete processors");
                Check(Issue(self, "2|auto|1", new CriticalThreadRule(stamp, true, 0x01UL, 0x02UL)) == null, "a concrete rule on SMT siblings was admitted");
                string manual = new CriticalThreadRule(stamp, true, 0x40UL).Encode();
                Check(Issue(self, manual, new CriticalThreadRule(stamp, true, 0x40UL)) != null, "a manual rule lost its exact admission");
                Check(Issue(self, manual, new CriticalThreadRule(stamp, true, 0x10UL)) == null, "a manual rule admitted other processors");

                var thread = new[] { new HardThreadIdentity(7001, 133000000000007001L) };
                GameMode mode = Mode(self, Profile("2|auto|1", "0"));
                object source = Source(self, mode, "2|auto|1");
                CriticalThreadRule rule; HardThreadChoice[] choices; string refusal;
                Check(Resolve(mode, source, thread, out rule, out choices, out refusal) && refusal == null, "a free machine refused automatic resolution: " + refusal);
                Check(rule != null && !rule.Automatic && rule.WorkerAvoidance && rule.Targets.SequenceEqual(new[] { 0x40UL }) && rule.Validate(All, Physical, All, 0, true) == null,
                    "automatic resolution did not yield the expected concrete rule");
                Check(choices.Length == 1 && choices[0].Thread.Tid == 7001 && choices[0].Mask == 0x40UL, "automatic resolution lost the chosen thread");
                Check(CriticalThreadRule.Parse("2|auto|1").Admits(rule), "the resolved rule is not admitted by its own intent");
                held = 0xC0;
                Check(Resolve(mode, source, thread, out rule, out choices, out refusal) && rule.Targets.SequenceEqual(new[] { 0x10UL }), "a held core was resolved as a target");
                held = 0xF0;
                Check(!Resolve(mode, source, thread, out rule, out choices, out refusal) && refusal != null && rule == null, "resolution placed a target without worker capacity");
                held = 0;
                CpuTopology.ThrottleMask = 0xC0;
                Check(Resolve(mode, source, thread, out rule, out choices, out refusal) && rule.Targets.SequenceEqual(new[] { 0x10UL }),
                    "resolution bound the critical thread inside the background throttle cores");
                CpuTopology.ThrottleMask = 0;
                GameProfile singleThread = Profile("2|auto|1", "1");
                singleThread.Overrides[ManualCorePlan.Key] = new ManualCorePlan(0xA8, false, stamp).Encode();
                GameMode oddOnly = Mode(self, singleThread);
                Check(Resolve(oddOnly, Source(self, oddOnly, "2|auto|1"), thread, out rule, out choices, out refusal)
                    && rule.Targets.SequenceEqual(new[] { 0x80UL }) && choices[0].Mask == 0x80UL,
                    "the real resolver could not bind an odd-only processor inside the game range");
                lease = Issue(self, "2|auto|1", rule, 0xA8);
                Check(lease != null && lease.AllowedMask == 0xA8 && lease.WorkerAvoidanceAllowed,
                    "the real target factory rejected three single-thread physical cores or expanded the game range");
                singleThread.Overrides[ManualCorePlan.Key] = new ManualCorePlan(7, false, stamp).Encode();
                GameMode onlyTwoCores = Mode(self, singleThread);
                Check(!Resolve(onlyTwoCores, Source(self, onlyTwoCores, "2|auto|1"), thread, out rule, out choices, out refusal),
                    "the real resolver accepted three logical processors on only two physical cores");
                GameMode restricted = Mode(self, Profile("2|auto|1", "1"));
                Check(!Resolve(restricted, Source(self, restricted, "2|auto|1"), thread, out rule, out choices, out refusal)
                    && refusal == PerformancePolicy.Text("已启用的游戏选核规则无效", "The enabled game processor rule is invalid"),
                    "an enabled but invalid game processor rule was ignored by automatic resolution");
            }
            finally
            {
                CloseHandle(self.Handle);
                AutoCores.AllocatedForTest = allocated; AutoCores.SupportedForTest = supported; CpuTopology.FavoredMask = favored; CpuTopology.ThrottleMask = throttle;
            }
        }

        private static void Roles()
        {
            Self self = Open();
            try
            {
                GameMode mode = Mode(self, Profile("2|auto|1", "0"));
                Set(mode, "enabled", false);
                object source = Source(self, mode, "2|auto|1");
                MethodInfo want = typeof(GameMode).GetMethod("WantCriticalRoles", Hidden), settle = typeof(GameMode).GetMethod("SettleCriticalRoles", Hidden);
                MethodInfo wanted = typeof(GameMode).GetMethod("CriticalRolesWanted", Hidden);
                Func<bool> current = delegate { return (bool)wanted.Invoke(mode, new object[] { self.Pid, self.Birth }); };
                want.Invoke(mode, new[] { source });
                Check(current() && !FrameCoordination.Observing, "a wanted role started an observer for a disabled runtime");
                settle.Invoke(mode, null);
                Check(current(), "roles asked for in this pass were withdrawn by its own settlement");
                settle.Invoke(mode, null);
                Check(!current(), "roles nobody asked for in a pass were kept");
                Check(!(bool)wanted.Invoke(mode, new object[] { 0, 0L }), "an empty renderer identity matched wanted roles");
            }
            finally { CloseHandle(self.Handle); }
        }

        private sealed class FixtureForm : PanelForm
        {
            internal FixtureForm(Tamer tamer, GameMode mode, Icon icon) : base(tamer, mode, icon, true) { }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }
        }
        private static T Field<T>(object owner, string name)
        {
            FieldInfo field = typeof(PanelForm).GetField(name, Hidden);
            if (field == null) throw new InvalidOperationException("FAIL missing PanelForm field " + name);
            return (T)field.GetValue(owner);
        }
        private static object Call(PanelForm form, string name, params object[] args)
        {
            MethodInfo method = typeof(PanelForm).GetMethod(name, Hidden);
            if (method == null) throw new InvalidOperationException("FAIL missing PanelForm method " + name);
            return method.Invoke(form, args);
        }
        private static GameProfile Stored(GameMode mode, string id) { return mode.GetProfiles().Single(x => x.Id == id); }

        private static void Card(string output, int language)
        {
            string folder = Path.Combine(output, "card-" + language); Directory.CreateDirectory(folder);
            string gameRoot = Path.Combine(folder, "game"); Directory.CreateDirectory(gameRoot);
            var profile = new GameProfile { Id = Guid.NewGuid().ToString("N"), Name = "Card fixture", Root = gameRoot };
            profile.Entries.Add("card-fixture-game");
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            Check(new GameProfileStore(folder).Save(new[] { profile }), "the temporary profile seed failed");
            CpuTopology.InjectTopologyForTest(All, Physical, new ulong[0], 0, 0, 0, 0, false, false);
            AutoCores.SupportedForTest = true; AutoCores.AllocatedForTest = delegate { return 0; };
            PrivacyControl.ResetForTest(); PrivacyControl.AdmissionAllowed = delegate { return false; };
            PrivacyControl.QueueForTest = delegate { throw new InvalidOperationException("No privacy worker in the card fixture"); };
            PanelForm.CpuNameOverride = "CARD FIXTURE";
            EnvironmentEnhancementEligibility.SampleForTest = delegate { return new EnvironmentEnhancementEligibility.MemorySnapshot {
                Installed = 32 * EnvironmentEnhancementEligibility.GiB, Total = 31 * EnvironmentEnhancementEligibility.GiB, Available = 8 * EnvironmentEnhancementEligibility.GiB }; };
            PanelForm.WorkspaceAreaForTest = delegate { return new Rectangle(0, 0, 1280, 760); }; PanelForm.WorkspaceDpiForTest = delegate { return 96; };
            Dpi.NoFit = false; Dpi.Scale = 1; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(true);
            PanelForm.SuppressUiWorkersForTest = true;
            var core = new SuppressionCore(); var mode = new GameMode(folder, core); var tamer = new Tamer(core);
            PanelForm form;
            using (var icon = IconArt.MakeIcon(24)) form = new FixtureForm(tamer, mode, icon);
            using (form)
            {
                form.ShowInTaskbar = false;
                foreach (var privacy in PerformanceWorkspaceLayout.Descendants(Field<DBPanel>(form, "pagePolicy")).OfType<PrivacyOptionsPanel>()) {
                    privacy.InspectionVisibleForTest = delegate { return false; };
                    privacy.SetForTest = delegate { throw new InvalidOperationException("The card fixture must not change system settings"); };
                    privacy.RefreshForTest = delegate { throw new InvalidOperationException("The card fixture must not query real settings"); };
                    privacy.StateForTest = delegate(PrivacyAction action, string target) { return new PrivacyResult { State = "restored", Supported = true, Detail = "Synthetic UI state" }; };
                }
                form.Show(); Call(form, "OpenGameCoreScheduling", profile.Id); Application.DoEvents();
                var toggle = Field<Toggle>(form, "criticalThreadEnabled");
                var result = Field<Label>(form, "criticalThreadResult");
                Check(Field<RoundPanel>(form, "criticalThreadEditor").Visible, "the card is not shown for the selected game");
                Check(!toggle.Checked && toggle.Enabled && CriticalThreadRule.Raw(Stored(mode, profile.Id)) == null, "the default card is not off and ready");
                foreach (string removed in new[] { "SaveCriticalThreadRule", "CriticalThreadMode", "CriticalThreadCount", "CriticalThreadWorkerAvoid", "CriticalThreadPhysicalCore1", "CriticalThreadSelection" })
                    Check(form.Controls.Find(removed, true).Length == 0, "a manual or mode control remains on the card: " + removed);
                Check(result.Text == PerformancePolicy.Text("默认关闭；打开右上角开关即可启用。", "Off by default. Turn on the switch above to enable it."), "the default card text changed: " + result.Text);

                toggle.Checked = true; Application.DoEvents();
                Check(CriticalThreadRule.Raw(Stored(mode, profile.Id)) == "2|auto|1", "the switch did not store the automatic intent");
                Check(CriticalThreadRule.Raw(new GameProfileStore(folder).LoadProfiles(false).Single(x => x.Id == profile.Id)) == "2|auto|1", "the automatic intent did not reach the on-disk store");
                Check(toggle.Checked && result.Text == PerformancePolicy.Text("已启用；实际状态见下方。", "On; see the runtime state below."), "the enabled card text changed: " + result.Text);
                CriticalThreadUiState view = mode.CriticalThreadView(profile.Id);
                Check(view.State == "waiting-game" && view.RequestedWorkerAvoid && view.Detail == PerformancePolicy.Text("下次游戏运行时自动识别渲染线程并选择核心",
                    "The next game session identifies the render thread and chooses cores automatically"), "the waiting view of an automatic intent changed");
                toggle.Checked = false; Application.DoEvents();
                Check(CriticalThreadRule.Raw(Stored(mode, profile.Id)) == null && CriticalThreadRule.Raw(new GameProfileStore(folder).LoadProfiles(false).Single(x => x.Id == profile.Id)) == null,
                    "turning the switch off did not clear the stored rule");

                // An older manual rule keeps applying until the switch is turned off and on.
                string manual = new CriticalThreadRule(ManualCorePlan.Stamp(All, Physical), true, 0x40UL).Encode();
                Check(mode.SaveCriticalThreadRule(profile.Id, null, CriticalThreadRule.Parse(manual)) == null, "the manual fixture rule could not be stored");
                Call(form, "SyncCriticalThreadEditor", Stored(mode, profile.Id)); Application.DoEvents();
                Check(toggle.Checked && result.Text.Contains(PerformancePolicy.Text("手动指定核心的旧规则", "older rule with manually chosen cores")), "an older manual rule was not disclosed");
                toggle.Checked = false; Application.DoEvents(); toggle.Checked = true; Application.DoEvents();
                Check(CriticalThreadRule.Raw(Stored(mode, profile.Id)) == "2|auto|1", "turning the switch off and on did not move a manual rule to automatic");
                toggle.Checked = false; Application.DoEvents();

                // The saved game range may expose either SMT sibling on each physical core.
                foreach (ulong allowed in new[] { 0x54UL, 0xA8UL }) {
                    var selected = new ManualCorePlan(allowed, false, ManualCorePlan.Stamp(All, Physical));
                    Check(mode.SaveManualCorePlan(profile.Id, ManualCorePlan.Raw(Stored(mode, profile.Id)), selected) == null
                        && mode.SetProfileOverride(profile.Id, PolicyCatalog.KeyAutoCores, "1"), "the single-thread game selection could not be stored");
                    Call(form, "SyncCriticalThreadEditor", Stored(mode, profile.Id)); Application.DoEvents();
                    Check(toggle.Enabled && !toggle.Checked, "the card refused three distinct physical cores with one selected logical processor each");
                    toggle.Checked = true; Application.DoEvents();
                    Check(toggle.Checked && CriticalThreadRule.Raw(Stored(mode, profile.Id)) == "2|auto|1"
                        && ManualCorePlan.Parse(ManualCorePlan.Raw(Stored(mode, profile.Id))).GameMask == allowed,
                        "the UI could not enable affinity without expanding the selected SMT threads");
                    toggle.Checked = false; Application.DoEvents();
                }
                var tooFew = new ManualCorePlan(7, false, ManualCorePlan.Stamp(All, Physical));
                Check(mode.SaveManualCorePlan(profile.Id, ManualCorePlan.Raw(Stored(mode, profile.Id)), tooFew) == null,
                    "the two-physical-core game selection could not be stored");
                Call(form, "SyncCriticalThreadEditor", Stored(mode, profile.Id)); Application.DoEvents();
                Check(!toggle.Enabled && !toggle.Checked && result.Text.EndsWith(PerformancePolicy.Text(
                    "可用物理核心不足：至少选中三个不同物理核，每核可只选一个逻辑线程",
                    "Not enough available physical cores: select at least three distinct physical cores; one logical processor per core is enough"), StringComparison.Ordinal),
                    "the card counted three logical processors as three physical cores: " + result.Text);
                Check(mode.SetProfileOverride(profile.Id, PolicyCatalog.KeyAutoCores, "0"), "the game processor rule could not be disabled after the card check");

                // Without room for one candidate and two worker cores the switch cannot be turned on.
                CpuTopology.InjectTopologyForTest(0x0FUL, new ulong[] { 0x03, 0x0C }, new ulong[0], 0, 0, 0, 0, false, false);
                Call(form, "SyncCriticalThreadEditor", Stored(mode, profile.Id)); Application.DoEvents();
                Check(!toggle.Enabled && !toggle.Checked && result.Text.EndsWith(PerformancePolicy.Text("至少需要三个不同物理核心：一个重点核心和两个工作核心",
                    "At least three distinct physical cores are required: one candidate core and two worker cores"), StringComparison.Ordinal), "a machine without capacity offered the switch: " + result.Text);
                CpuTopology.InjectTopologyForTest(All, Physical, new ulong[0], 0, 0, 0, 0, false, false);
                Check(!mode.Enabled && !form.UiActive, "the card fixture activated runtime workers");
                form.Hide();
            }
        }

        [STAThread] private static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional automatic candidate-thread negative control"); return 3; }
            if (args.Length != 1) { Console.WriteLine("FAIL usage: CriticalThreadAutoChecks <output directory>"); return 2; }
            try
            {
                Directory.CreateDirectory(args[0]);
                Settings.UseTransientStoreForCurrentProcess(); Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                foreach (int language in new[] { 0, 1 }) { Lang.Cur = language; Rules(); }
                Lang.Cur = 0; Cores(); ExhaustiveRanges(); Presenter(); Hotspots(); Observer(); Factory(); Roles();
                // The private desktop ends this process itself once its fixture returns, so the verdict
                // is printed inside it; a throwing fixture is reported there and exits with 1.
                return PerformanceWorkspaceLayout.PrivateDesktop(delegate {
                    Settings.UseTransientStoreForCurrentProcess(); Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                    PanelForm.SuppressUiWorkersForTest = true; Dpi.Init(); Lang.Init();
                    foreach (int language in new[] { 0, 1 }) Card(args[0], language);
                    Console.WriteLine("PASS critical thread auto checks=" + checks + "; pure rules and selection, observer doubles, real target factory on this test process, real card on a temporary store");
                });
            }
            catch (Exception error)
            {
                Exception inner = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
                Console.WriteLine(inner.Message.StartsWith("FAIL ", StringComparison.Ordinal) ? inner.Message : "FAIL unexpected " + inner);
                return 1;
            }
            finally { Lang.Cur = 0; }
        }
    }
}
#endif
