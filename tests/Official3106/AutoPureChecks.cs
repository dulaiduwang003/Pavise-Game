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

        private static int Main(string[] args)
        {
            try { if(args.Length>0 && args[0]=="--negative-control")return 3; Lang.Cur=1; Rules(); Cores(); ExhaustiveRanges(); Presenter(); Console.WriteLine("PASS migrated official auto pure assertions="+checks); return 0; }
            catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}