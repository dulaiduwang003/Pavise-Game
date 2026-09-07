// Dedicated review entry: owned-process scheduling counterexamples and read-only probes.
// No Program.Main, GameMode loop, Sweep, Boost, real settings, power or driver writes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static class OptimizationReviewBench
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static long sink;
        private static void Require(bool ok, string message)
        { if (!ok) throw new InvalidOperationException(message); }
        private static string F(double value) { return value.ToString("F4", Inv); }
        private static int Main(string[] args)
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null,
                args[0] == "worker" ? 15000 : 150000, Timeout.Infinite))
            {
                try
                {
                    if (args[0] == "worker") Worker(args);
                    else if (args[0] == "priority") Priority(args[1]);
                    else if (args[0] == "power-inputs") PowerInputs();
                    else if (args[0] == "read-cost") ReadCost();
                    else if (args[0] == "auto-gpu-filter") AutoGpuFilter();
                    else throw new ArgumentException("priority directory | power-inputs | read-cost");
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            }
        }

        private static void Worker(string[] args)
        {
            bool game = args[2] == "game", boosted = args[4] == "guarded";
            ulong mask = ulong.Parse(args[3], NumberStyles.HexNumber, Inv);
            using (Process self = Process.GetCurrentProcess())
            using (var map = MemoryMappedFile.OpenExisting(args[1]))
            using (var view = map.CreateViewAccessor())
            {
                IntPtr originalMask = self.ProcessorAffinity;
                ProcessPriorityClass originalPriority = self.PriorityClass;
                var intervals = new List<double>();
                int completed = 0; long cpuStart = 0, cpuEnd = 0;
                ProcessPriorityClass applied = ProcessPriorityClass.Normal;
                try
                {
                    self.ProcessorAffinity = new IntPtr(unchecked((long)mask));
                    // Production CPU-domain gate: whole-machine spare CPU is insufficient.
                    applied = game && boosted ? (ProcessPriorityClass)GameMode.BoostPriorityTarget(false, false,
                        mask, unchecked((ulong)originalMask.ToInt64()), false)
                        : ProcessPriorityClass.Normal;
                    self.PriorityClass = applied;
                    Require(self.PriorityClass == applied && unchecked((ulong)self.ProcessorAffinity.ToInt64()) == mask,
                        "owned worker priority/affinity readback failed");
                    view.Write(game ? 4 : 0, 1);
                    long start;
                    while ((start = view.ReadInt64(16)) == 0) Thread.Sleep(1);
                    long end = view.ReadInt64(24);
                    while (Stopwatch.GetTimestamp() < start) Thread.Sleep(1);
                    cpuStart = self.TotalProcessorTime.Ticks;
                    if (game)
                    {
                        int request = 0;
                        while (Stopwatch.GetTimestamp() < end)
                        {
                            long sent = Stopwatch.GetTimestamp();
                            view.Write(8, ++request);
                            // Explicit synthetic busy-wait dependency across two processes.
                            // Never claim that all games use this pattern.
                            while (view.ReadInt32(12) != request && Stopwatch.GetTimestamp() < end)
                                Thread.SpinWait(64);
                            long received = Stopwatch.GetTimestamp();
                            if (view.ReadInt32(12) != request || received > end) break;
                            completed++;
                            intervals.Add((received - sent) * 1000.0 / Stopwatch.Frequency);
                        }
                    }
                    else
                    {
                        int previous = 0;
                        while (Stopwatch.GetTimestamp() < end)
                        {
                            int requested = view.ReadInt32(8);
                            if (requested == previous) { Thread.SpinWait(64); continue; }
                            // Fixed CPU work per reply, independent of priority and wall-clock preemption.
                            long value = requested;
                            for (int k = 0; k < 40000; k++) value = unchecked(value * 1664525 + 1013904223);
                            Interlocked.Exchange(ref sink, value);
                            if (Stopwatch.GetTimestamp() >= end) break;
                            previous = requested; view.Write(12, requested); completed++;
                        }
                    }
                    cpuEnd = self.TotalProcessorTime.Ticks;
                }
                finally
                {
                    self.PriorityClass = originalPriority; self.ProcessorAffinity = originalMask;
                    Require(self.PriorityClass == originalPriority && self.ProcessorAffinity == originalMask,
                        "owned worker restoration failed");
                }
                if (game) File.WriteAllLines(args[5], intervals.ConvertAll(F).ToArray());
                intervals.Sort();
                Console.WriteLine(string.Join(",", new[] { args[2], completed.ToString(),
                    F(Quantile(intervals, .5)), F(Quantile(intervals, .95)),
                    F((cpuEnd - cpuStart) / (double)TimeSpan.TicksPerMillisecond), applied.ToString(), "True" }));
            }
        }

        private static double Quantile(List<double> sorted, double fraction)
        { return sorted.Count == 0 ? double.NaN : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * fraction) - 1)]; }

        private static Process StartWorker(string map, string role, ulong mask, string priority, string raw)
        {
            var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "worker " + map + " " + role + " " + mask.ToString("X") + " " + priority + " \"" + raw + "\"");
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.RedirectStandardOutput = info.RedirectStandardError = true;
            return Process.Start(info);
        }

        private static void Priority(string directory)
        {
            ulong allowed;
            using (Process self = Process.GetCurrentProcess()) allowed = unchecked((ulong)self.ProcessorAffinity.ToInt64());
            var bits = new List<ulong>();
            for (int bit = 2; bit < 64; bit++) if ((allowed & (1UL << bit)) != 0) bits.Add(1UL << bit);
            Require(bits.Count >= 3, "need three allowed logical processors other than CPU 0/1");
            // Spread is explicitly distinct logical CPUs, not a claim about physical-core mapping.
            ulong gameMask = bits[0], separateMask = bits[2];
            Console.WriteLine("layout,arm,treatment,game_mask,helper_mask,completed,p50_reply_ms,p95_reply_ms,game_cpu_ms,helper_cpu_ms,system_cpu_percent,restored,game_priority");
            foreach (string layout in new[] { "same-logical", "separate-logical" })
            for (int arm = 0; arm < 8; arm++)
            {
                string treatment = arm % 4 == 1 || arm % 4 == 2 ? "guarded" : "normal";
                string name = "PaviseReview-" + Guid.NewGuid().ToString("N");
                ulong helperMask = layout == "same-logical" ? gameMask : separateMask;
                Process helper = null, game = null;
                using (var map = MemoryMappedFile.CreateNew(name, 4096))
                using (var view = map.CreateViewAccessor())
                {
                    try
                    {
                        helper = StartWorker(name, "helper", helperMask, "normal", "unused");
                        game = StartWorker(name, "game", gameMask, treatment,
                            Path.Combine(directory, layout + "-" + arm + "-" + treatment + ".samples.txt"));
                        var readiness = Stopwatch.StartNew();
                        while (view.ReadInt32(0) != 1 || view.ReadInt32(4) != 1)
                        { Require(readiness.ElapsedMilliseconds < 5000, "owned workers not ready"); Thread.Sleep(5); }
                        var cpu = new CpuSaturation(); cpu.Sample();
                        long start = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
                        view.Write(24, start + 2 * Stopwatch.Frequency); view.Write(16, start);
                        Require(game.WaitForExit(7000) && helper.WaitForExit(3000), "owned worker timeout");
                        double cpuPct = cpu.Sample() * 100;
                        string gameText = game.StandardOutput.ReadToEnd().Trim(), helperText = helper.StandardOutput.ReadToEnd().Trim();
                        Require(game.ExitCode == 0 && helper.ExitCode == 0,
                            "owned worker failure: " + game.StandardError.ReadToEnd() + helper.StandardError.ReadToEnd());
                        string[] g = gameText.Split(','), h = helperText.Split(',');
                        Require(g.Length == 7 && h.Length == 7 && g[6] == "True" && h[6] == "True", "invalid worker results");
                        Console.WriteLine(string.Join(",", new[] { layout, arm.ToString(), treatment, gameMask.ToString("X"),
                            helperMask.ToString("X"), g[1], g[2], g[3], g[4], h[4], F(cpuPct), "True", g[5] }));
                        Require(g[5] == "Normal" && int.Parse(g[1], Inv) > 0,
                            "restricted-domain policy caused a zero-progress window or promoted priority");
                        Console.Out.Flush();
                    }
                    finally
                    {
                        foreach (Process owned in new[] { game, helper })
                        {
                            if (owned == null) continue;
                            if (!owned.HasExited) { owned.Kill(); owned.WaitForExit(2000); }
                            owned.Dispose();
                        }
                    }
                }
            }
        }

        private static void PowerInputs()
        {
            Console.WriteLine("repeat,case,first_cpu_sample,observe_stage,observe_action,verify_stage,verify_action,verdict");
            for (int repeat = 0; repeat < 5; repeat++)
            foreach (string variant in new[] { "valid", "cold-cpu-first", "verify-cpu-unavailable", "verify-gpu-unavailable" })
            {
                Settings.UseTransientStoreForCurrentProcess();
                var cpu = new CpuSaturation();
                double first = variant == "cold-cpu-first" ? cpu.Sample() * 100 : 40;
                var state = new PowerBudgetYield(); state.Begin(0, true);
                YieldAction action = YieldAction.None;
                for (int second = 2; second <= 20; second += 2)
                    action = state.Advance(second * TimeSpan.TicksPerSecond, 98, second == 2 ? first : 40, 45);
                string stage = state.Stage.ToString(), observedAction = action.ToString();
                YieldAction lastDecision = YieldAction.None;
                for (int second = 22; second <= 36; second += 2)
                {
                    action = state.Advance(second * TimeSpan.TicksPerSecond, variant == "verify-gpu-unavailable" ? -1 : 97,
                        variant == "verify-cpu-unavailable" ? double.NaN : 40, 41);
                    if (action != YieldAction.None) lastDecision = action;
                }
                Console.WriteLine(string.Join(",", new[] { repeat.ToString(), variant, F(first), stage,
                    observedAction, state.Stage.ToString(), lastDecision.ToString(), state.Verdict.ToString() }));
                Require(stage == "Engaged", "valid baseline did not engage after cold-sample filtering");
                Require(variant.StartsWith("verify-") ? state.Stage == YieldStage.Reverted
                    && state.Verdict == YieldVerdict.Inconclusive : state.Stage == YieldStage.Held,
                    "missing telemetry was accepted or valid telemetry rejected");
            }
            // Samples are synthetic except cold CpuSaturation.Sample; no laptop eligibility override
            // runs through the real runner and no EPP or telemetry hardware is modified.
        }

        private static void ReadCost()
        {
            Console.WriteLine("kind,sample,elapsed_ms,items,path_queries,reused");
            int session;
            using (Process self = Process.GetCurrentProcess()) session = self.SessionId;
            for (int sample = 0; sample < 12; sample++)
            {
                foreach (bool reuse in new[] { false, true })
                {
                    long queries = ProcessSnapshotSource.PathQueryCount, reused = ProcessSnapshotSource.ReuseCount;
                    var watch = Stopwatch.StartNew();
                    ProcessSnapshot snapshot = ProcessSnapshotSource.Capture(session, reuse ? 5000 : 0);
                    watch.Stop(); Require(snapshot != null, "read-only process snapshot unavailable");
                    Console.WriteLine(string.Join(",", new[] { reuse ? "snapshot-reused" : "snapshot-fresh", sample.ToString(),
                        F(watch.Elapsed.TotalMilliseconds), snapshot.Count.ToString(),
                        (ProcessSnapshotSource.PathQueryCount - queries).ToString(), (ProcessSnapshotSource.ReuseCount - reused).ToString() }));
                }
                Thread.Sleep(50);
            }
            VramSpillProbe.ResetForTest();
            int probePid;
            using (Process self = Process.GetCurrentProcess()) probePid = self.Id;
            for (int sample = 0; sample < 8; sample++)
            {
                // Real production background query, including cold GPU inventory/PDH initialization.
                int before = VramSpillProbe.SamplesForTest;
                var watch = Stopwatch.StartNew();
                VramSpillProbe.SampleIfDueAt(new[] { probePid },
                    sample * 20L * TimeSpan.TicksPerSecond);
                double enqueue = watch.Elapsed.TotalMilliseconds;
                Require(VramSpillProbe.WaitForIdle(10000), "production VRAM worker did not drain");
                double complete = watch.Elapsed.TotalMilliseconds;
                string available = VramSpillProbe.SamplesForTest > before ? "1" : "unavailable";
                Console.WriteLine(string.Join(",", new[] { "vram-shared-enqueue", sample.ToString(), F(enqueue), available, "0", "0" }));
                Console.WriteLine(string.Join(",", new[] { "vram-shared-complete", sample.ToString(), F(complete), available, "0", "0" }));
                Thread.Sleep(250);
            }
            Require(VramSpillProbe.CloseForShutdown(3000), "VRAM shutdown did not drain");
        }

        private sealed class MockPreferenceControl : IAppGpuPreferenceControl, IAppGpuPreferenceLedger
        {
            internal string Preference, Ledger = "";
            public bool Supported { get { return true; } }
            public bool IsExecutablePresent(string path) { return true; }
            public bool TryRead(string path, out string value) { value = Preference; return true; }
            public bool TryRead(out string value) { value = Ledger; return true; }
            public bool TryWrite(string value) { Ledger = value; return true; }
            public bool TryGetStagedOriginal(string path, out bool staged, out string original)
            { staged = false; original = null; return true; }
            public bool TryReleaseStage(string path) { return true; }
            public AppGpuPreferenceWriteResult CompareExchange(string path, string expected, string replacement)
            {
                if (Preference != expected) return AppGpuPreferenceWriteResult.Changed;
                Preference = replacement; return AppGpuPreferenceWriteResult.Written;
            }
        }

        private static void AutoGpuFilter()
        {
            // A focused composition probe, NOT the live GPU/window/identity discovery loop.
            // Calls the production path-visibility gate, then the production preference manager.
            // All remaining external prerequisites are explicit synthetic assumptions.
            Console.WriteLine("repeat,case,visible_pid,candidate_pid,same_exe,passes_path_visibility,passes_production_name_filter,enroll_result,mock_low_power_written");
            for (int repeat = 0; repeat < 5; repeat++)
            foreach (bool helper in new[] { false, true })
            {
                const string path = @"C:\PaviseSyntheticFixture\VisibleApp.exe";
                var visible = new HashSet<int> { 1001 };
                int candidate = helper ? 1002 : 1001;
                var snapshot = new ProcessSnapshot(new[] {
                    new ProcEntry { Pid = 1001, Creation = 100, Session = 1, Path = path },
                    new ProcEntry { Pid = 1002, ParentPid = 1001, Creation = 200, Session = 1, Path = path } });
                var identity = new GameProcessSnapshot { Pid = candidate, Creation = helper ? 200 : 100, Path = path };
                bool allowedVisibility = GameMode.AutoGpuVisibilityAllows(identity, snapshot, visible, 1);
                bool eligible = GameMode.AutoGpuEligible("VisibleApp", path, @"C:\Windows\");
                var mock = new MockPreferenceControl();
                var manager = new AppGpuPreferenceManager(mock, mock);
                string result = allowedVisibility && eligible ? GameMode.AutoGpuEnroll(manager, path).ToString() : "Skipped";
                Console.WriteLine(string.Join(",", new[] { repeat.ToString(), helper ? "hidden-helper-same-exe" : "visible-process",
                    "1001", candidate.ToString(), "True", allowedVisibility.ToString(), eligible.ToString(), result,
                    (PrefFieldText.ReadField(mock.Preference, "GpuPreference") == "1").ToString() }));
                Require(!allowedVisibility && mock.Preference == null, "visible EXE enrolled through a hidden PID");
            }
        }
    }
}
