// @author bdth 2074055628@qq.com
// File purpose Joint bench for core isolation, core assignment and IRQ hard-pinning, unprivileged, no system-wide writes, no normal runtime started, no window
//   Isolation's system writes need admin, this bench does not elevate, that part only runs the logic layer, real system writes are covered by Test-CoreScheduling.ps1 -LiveIsolation
#if PAVISE_SELFTEST && PAVISE_SELFTEST_RUNNER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // Only records the call sequence and masks, never touches the real system CPU range
    internal sealed class JointIsolationPlatform : ICoreIsolationPlatform, ICoreIsolationStore
    {
        internal string Boot = "aabbccddaabbccddaabbccddaabbccdd", Receipt = "";
        internal ulong All, Allocated, Allowed;
        internal ulong[] Physical;
        internal int TargetPid;
        internal long TargetCreation;
        internal readonly List<string> Operations = new List<string>();

        public string BootIdentity() { return Boot; }
        public IsolationCpuState ReadSystem(IntPtr p)
        {
            return new IsolationCpuState { All = All, Allocated = Allocated,
                Admitted = p == IntPtr.Zero ? 0 : Allocated & Allowed, Physical = Physical };
        }
        public bool SetSystemAllowed(ulong mask)
        { Operations.Add("system:" + Hex(mask)); Allocated = All & ~mask; return true; }
        public IntPtr Open(int pid, long creation)
        { return pid == TargetPid && creation == TargetCreation ? new IntPtr(7) : IntPtr.Zero; }
        public bool Exited(IntPtr h) { return false; }
        public bool Gone(int pid, long creation) { return pid != TargetPid || creation != TargetCreation; }
        public bool ReadAllowed(IntPtr h, out ulong mask) { mask = Allowed; return true; }
        public bool SetAllowed(IntPtr h, ulong mask)
        { Operations.Add("process:" + Hex(mask)); Allowed = mask; return true; }
        public void Close(IntPtr h) { }
        public bool Read(out string value) { value = Receipt; return true; }
        public bool Write(string value) { Operations.Add("journal"); Receipt = value; return true; }
        internal CoreIsolationEngine Engine() { return new CoreIsolationEngine(this, this); }
        private static string Hex(ulong v) { return v.ToString("X"); }
    }

    internal static class CoreJointBench
    {
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();
        private static string Exe { get { return typeof(CoreJointBench).Assembly.Location; } }
        private static int checks, failures;
        private static TextWriter report;

        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--sample") { Sample(); return 0; }
            Settings.UseTransientStoreForCurrentProcess();
            Lang.Init();
            string path = args.Length >= 1 ? args[0]
                : Path.Combine(Path.GetDirectoryName(Exe), "core-joint-bench.txt");
            using (var writer = new StreamWriter(path, false))
            {
                writer.AutoFlush = true;
                report = writer;
                Line("CORE JOINT BENCH  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Line("privileged=false  system_cpu_range_written=false  normal_runtime_started=false  windows_shown=0");
                Line("");
                try
                {
                    Topology();
                    ValidationOnRealTopology();
                    IsolationEngineOnRealTopology();
                    JointPlacement();
                }
                catch (Exception e) { Fail("bench aborted: " + e.Message); Line(e.ToString()); }
                Line("");
                Line("TOTAL checks=" + checks + " failed=" + failures);
                return failures == 0 ? 0 : 1;
            }
        }

        internal static void Line(string text) { report.WriteLine(text); Console.WriteLine(text); }
        private static void Ok(string name) { checks++; Line("  PASS " + name); }
        private static void Fail(string name) { checks++; failures++; Line("  FAIL " + name); }
        internal static void Check(bool value, string name) { if (value) Ok(name); else Fail(name); }
        internal static string Hex(ulong v) { return "0x" + v.ToString("X"); }
        internal static int Bits(ulong v) { return CpuTopology.CountSetBits(v); }
        internal static ulong[] Cores { get { return cores; } }

        // ---- 1 Real topology ----
        private static ulong[] cores;
        private static ulong all;

        private static void Topology()
        {
            Line("[1] REAL TOPOLOGY");
            all = CpuTopology.AllMask;
            cores = CpuTopology.PhysicalCoreMasks();
            Line("  all=" + Hex(all) + " logical=" + Bits(all) + " physical=" + cores.Length);
            Line("  hybrid=" + CpuTopology.Hybrid + " multiGroup=" + CpuTopology.MultiGroup);
            Line("  perf=" + Hex(CpuTopology.PerfMask) + " (" + Bits(CpuTopology.PerfMask) + " lp)"
                + "  eff=" + Hex(CpuTopology.EffMask) + " (" + Bits(CpuTopology.EffMask) + " lp)");
            int smt = 0, single = 0;
            foreach (ulong core in cores) { if (Bits(core) > 1) smt++; else single++; }
            Line("  physical cores: smt=" + smt + " non-smt=" + single);
            Line("  isolationSupported=" + CoreScheduling.IsolationSupported);
            Check(!CpuTopology.MultiGroup, "single processor group");
            Check(cores.Length >= 4, "at least four physical cores");
            ulong covered = 0;
            bool disjoint = true;
            foreach (ulong core in cores) { if ((covered & core) != 0) disjoint = false; covered |= core; }
            Check(disjoint && covered == all, "physical core masks partition the machine");
            // On hybrid parts P-cores have SMT and E-cores do not, both widths must be present to truly cover hybrid topology
            if (CpuTopology.Hybrid) Check(smt > 0 && single > 0, "hybrid machine exposes both SMT and non-SMT cores");
            Line("");
        }

        private static ulong WholeCore(int index) { return cores[index]; }

        private static CoreSchedulingPlan Plan(ulong game, bool on, ulong isolation)
        {
            return new CoreSchedulingPlan { GameMask = game, IsolationOn = on, IsolationMask = isolation,
                Topology = CoreScheduling.Stamp(all, cores) };
        }

        // ---- 2 Plan validation ----
        private static void ValidationOnRealTopology()
        {
            Line("[2] PLAN VALIDATION ON REAL TOPOLOGY");
            ulong cpu0Core = 0;
            foreach (ulong core in cores) if ((core & 1UL) != 0) { cpu0Core = core; break; }
            Line("  cpu0 core=" + Hex(cpu0Core) + " width=" + Bits(cpu0Core));

            // Pick two whole cores excluding CPU0 to isolate, prefer SMT ones so the sibling linkage gets verified
            var picked = new List<ulong>();
            foreach (ulong core in cores)
            { if (core != cpu0Core && Bits(core) > 1 && picked.Count < 2) picked.Add(core); }
            foreach (ulong core in cores)
            { if (core != cpu0Core && !picked.Contains(core) && picked.Count < 2) picked.Add(core); }
            ulong isolated = 0;
            foreach (ulong core in picked) isolated |= core;
            Line("  isolation candidate=" + Hex(isolated) + " cores=" + picked.Count + " lp=" + Bits(isolated));

            ulong game = isolated;
            Check(CoreScheduling.Validate(Plan(game, true, isolated), all, cores, false, true) == null,
                "whole-core isolation plan accepted");
            // Half a core will not do, on hybrid parts only a core that really has SMT exercises this
            ulong half = 0;
            foreach (ulong core in picked)
                if (Bits(core) > 1) { half = core & (~core + 1); break; }
            if (half != 0)
                Check(CoreScheduling.Validate(Plan(game, true, (isolated & ~LowestCore(picked)) | half), all, cores, false, true)
                    == "schedule.error.whole", "half of an SMT core refused");
            else Line("  SKIP half-core check: no SMT core available outside cpu0");
            Check(CoreScheduling.Validate(Plan(game, true, cpu0Core), all, cores, false, true) == "schedule.error.cpu0",
                "cpu0 physical core refused");
            Check(CoreScheduling.Validate(Plan(game, true, isolated | (1UL << 63)), all, cores, false, true) != null,
                "out-of-range isolation bit refused");
            // Must refuse when only the cpu0 physical core would remain, at least two cores must stay outside isolation
            ulong greedy = 0;
            foreach (ulong core in cores) { if (core == cpu0Core) continue; greedy |= core; }
            Check(CoreScheduling.Validate(Plan(game, true, greedy), all, cores, false, true) == "schedule.error.spare",
                "fewer than two spare physical cores refused");
            // Leaving exactly two must pass, the boundary must not reject a valid plan along with the bad ones
            ulong spareTwo = 0;
            bool skipped = false;
            foreach (ulong core in cores)
            {
                if (core == cpu0Core) continue;
                if (!skipped) { skipped = true; continue; }
                spareTwo |= core;
            }
            Check(CoreScheduling.Validate(Plan(game, true, spareTwo), all, cores, false, true) == null,
                "exactly two spare physical cores accepted");
            Check(CoreScheduling.Validate(Plan(game, true, 0), all, cores, false, true) == "schedule.error.isolationempty",
                "isolation on with empty range refused");
            Check(CoreScheduling.Validate(Plan(game, true, isolated), all, cores, false, false) == "schedule.error.isolationunsupported",
                "unsupported platform refused");
            var stale = Plan(game, true, isolated);
            stale.Topology = "stale-topology-stamp";
            Check(CoreScheduling.Validate(stale, all, cores, false, true) == "schedule.error.topology",
                "stale topology stamp refused");
            Line("");
        }

        private static ulong LowestCore(List<ulong> picked)
        {
            foreach (ulong core in picked) if (Bits(core) > 1) return core;
            return picked.Count > 0 ? picked[0] : 0;
        }

        // ---- 3 Isolation engine, real topology, fake platform ----
        private static void IsolationEngineOnRealTopology()
        {
            Line("[3] ISOLATION ENGINE ON REAL TOPOLOGY (fake platform, no system write)");
            ulong cpu0Core = 0;
            foreach (ulong core in cores) if ((core & 1UL) != 0) { cpu0Core = core; break; }
            ulong isolated = 0;
            int taken = 0;
            foreach (ulong core in cores)
            { if (core == cpu0Core) continue; isolated |= core; if (++taken == 2) break; }

            var os = new JointIsolationPlatform { All = all, Physical = cores, Allowed = all,
                TargetPid = 7, TargetCreation = 100 };
            using (CoreIsolationEngine engine = os.Engine())
            {
                bool began = engine.Begin(isolated, 7, 100, isolated);
                Check(began, "Begin accepted on real 32-lp topology");
                if (!began) { Line("  error=" + Describe(engine)); Line(""); return; }
                Check(os.Allocated == isolated, "system allocated equals isolation range " + Hex(os.Allocated));
                Check(os.Allowed == (all | isolated), "game admission keeps original allowed and adds isolation");
                Check(engine.Active && engine.Pending, "engine reports active lease");
                Check(engine.Audit(), "audit passes with matching state");
                // PID reuse must not inherit admission
                Check(!engine.Allow(7, 101, isolated), "pid reuse refused");
                Check(engine.Restore(), "restore succeeds");
                Check(os.Allocated == 0, "system range fully released");
                Check(os.Allowed == all, "process allowed restored to original");
                Check(os.Receipt == "", "receipt cleared");
            }
            Line("  operations=" + string.Join(" -> ", os.Operations.ToArray()));

            // Must refuse to enable when the system range is held externally, never overwrite someone else's mask
            var busy = new JointIsolationPlatform { All = all, Physical = cores, Allowed = all,
                Allocated = isolated, TargetPid = 7, TargetCreation = 100 };
            using (CoreIsolationEngine engine = busy.Engine())
            {
                Check(!engine.Begin(isolated, 7, 100, isolated), "external allocation refuses to start");
                Check(busy.Operations.Count == 0, "nothing written when refusing external allocation");
            }
            Line("");
        }

        private static string Describe(CoreIsolationEngine engine)
        {
            try
            {
                System.Reflection.PropertyInfo p = typeof(CoreIsolationEngine).GetProperty("Error",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public);
                object v = p == null ? null : p.GetValue(engine, null);
                return v == null ? "(none)" : v.ToString();
            }
            catch { return "(unreadable)"; }
        }

        // ---- 4 Joint, real child process, manual assignment + IRQ hard-pin ----
        //   Fixture goes through the same FamilyPolicyFixture as CoreSchedulingIntegrationRunner, no new construction path
        private static void JointPlacement()
        {
            Line("[4] JOINT: MANUAL PLACEMENT + IRQ HARD PIN (real child processes, unprivileged)");
            SelfTests.RunJointPlacementBench();
            Line("");
        }

        // Measured on this machine: the child never receives redirected stdin lines, command-driven sampling silently hangs in ReadLine
        //   Switched to stdout only, the child samples continuously at its own pace, the parent drops the line spanning the change and takes the next
        internal static ulong SampleChild(Process child)
        {
            ReadSampleLine(child); // this line may straddle the affinity change, drop it
            return ReadSampleLine(child);
        }

        private static ulong ReadSampleLine(Process child)
        {
            var read = System.Threading.Tasks.Task.Factory.StartNew(() => child.StandardOutput.ReadLine());
            if (!read.Wait(15000)) { Line("  WARN sample timed out"); return 0; }
            string raw = read.Result;
            if (raw == null) { Line("  WARN sample stream closed"); return 0; }
            raw = raw.Trim().Trim('﻿');
            ulong value;
            if (ulong.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return value;
            Line("  WARN unparsed sample line [" + raw + "]");
            return 0;
        }

        internal static Process Child(string arguments)
        {
            return Process.Start(new ProcessStartInfo(Exe, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true });
        }

        // Parent sends no commands, finishing is a plain Kill, the child only writes stdout and has no input to wait on
        internal static void Finish(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } }
            catch { }
            finally { p.Dispose(); }
        }

        // Child: eight threads spin 1.2 s per round, record which logical CPUs they actually landed on, print continuously until killed
        private static void Sample()
        {
            Console.WriteLine("READY");
            while (true)
            {
                long observed = 0;
                DateTime end = DateTime.UtcNow.AddMilliseconds(1200);
                var threads = new Thread[8];
                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i] = new Thread(delegate()
                    {
                        while (DateTime.UtcNow < end)
                        {
                            long bit = unchecked((long)(1UL << (int)GetCurrentProcessorNumber())), old;
                            do { old = Interlocked.Read(ref observed); }
                            while (Interlocked.CompareExchange(ref observed, old | bit, old) != old);
                            Thread.SpinWait(100);
                        }
                    });
                    threads[i].IsBackground = true;
                    threads[i].Start();
                }
                foreach (Thread t in threads) t.Join();
                Console.WriteLine(unchecked((ulong)observed).ToString("X"));
                Console.Out.Flush();
            }
        }
    }

    internal static partial class SelfTests
    {
        // Lives in SelfTests to reuse the existing FamilyPolicyFixture, same fixture path as the placement integration
        //   GameMode is only constructed, never Started, the runtime loop lives in Start and is not launched here
        internal static void RunJointPlacementBench()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseJointBench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Logger.LogPath = Path.Combine(root, "joint.log");
            Process game = null;
            try
            {
                game = CoreJointBench.Child("--sample");
                if (game.StandardOutput.ReadLine() != "READY")
                { CoreJointBench.Check(false, "child ready"); return; }
                IntPtr h = game.Handle;
                ulong original = Native.QueryAffinity(h);
                long creation, cpu; ulong io;
                CoreJointBench.Check(Native.QueryProcessSample(h, out creation, out cpu, out io),
                    "child identity readable");
                CoreJointBench.Line("  child pid=" + game.Id + " original affinity="
                    + CoreJointBench.Hex(original) + " lp=" + CoreJointBench.Bits(original));

                // Manual core selection: take two whole physical cores excluding CPU0
                ulong[] cores = CoreJointBench.Cores;
                ulong cpu0Core = 0;
                foreach (ulong core in cores) if ((core & 1UL) != 0) { cpu0Core = core; break; }
                ulong target = 0;
                int taken = 0;
                foreach (ulong core in cores)
                { if (core == cpu0Core) continue; target |= core; if (++taken == 2) break; }
                CoreJointBench.Line("  manual target=" + CoreJointBench.Hex(target)
                    + " lp=" + CoreJointBench.Bits(target));

                using (var fixture = new FamilyPolicyFixture(root, "joint-bench"))
                {
                    GameMode mode = fixture.Mode;
                    CoreJointBench.Check(mode.ProbeManualPlacement(h, game.Id, creation, original, target),
                        "manual placement applied");
                    CoreJointBench.Check(Native.QueryAffinity(h) == target,
                        "hard affinity equals manual target");

                    // Measured core placement, isolation is off, this proves manual assignment alone confines the process
                    ulong observed = CoreJointBench.SampleChild(game);
                    CoreJointBench.Line("  observed game cpus=" + CoreJointBench.Hex(observed)
                        + " lp=" + CoreJointBench.Bits(observed));
                    CoreJointBench.Check(observed != 0 && (observed & ~target) == 0,
                        "game ran only on manually assigned cpus");

                    // Withdraw interrupt observation, the manual binding must stay, this is the boundary between the two restore ownerships
                    CoreJointBench.Check(mode.ProbeRestoreManualPlacement(game.Id, false),
                        "IRQ observation cleanup accepted");
                    CoreJointBench.Check(Native.QueryAffinity(h) == target,
                        "stopping IRQ observation preserves manual placement");
                    ulong afterIrq = CoreJointBench.SampleChild(game);
                    CoreJointBench.Line("  observed after irq cleanup=" + CoreJointBench.Hex(afterIrq));
                    CoreJointBench.Check(afterIrq != 0 && (afterIrq & ~target) == 0,
                        "placement still effective after IRQ cleanup");

                    CoreJointBench.Check(mode.ProbeRestoreManualPlacement(game.Id, true),
                        "manual restore accepted");
                    CoreJointBench.Check(Native.QueryAffinity(h) == original,
                        "original affinity restored exactly");
                    ulong afterRestore = CoreJointBench.SampleChild(game);
                    CoreJointBench.Line("  observed after full restore=" + CoreJointBench.Hex(afterRestore)
                        + " lp=" + CoreJointBench.Bits(afterRestore));
                    CoreJointBench.Check(afterRestore != 0 && (afterRestore & ~original) == 0,
                        "restored process runs within its original mask");
                    CoreJointBench.Check(CoreJointBench.Bits(afterRestore) > CoreJointBench.Bits(target),
                        "restored process reaches more cpus than the manual target");

                    // Must not widen the process's pre-existing restricted range
                    CoreJointBench.Check(Native.SetProcessAffinityMask(h, (UIntPtr)target),
                        "apply pre-existing restriction");
                    CoreJointBench.Check(!mode.ProbeManualPlacement(h, game.Id, creation, target, original),
                        "refuses to widen a pre-existing restriction");
                    CoreJointBench.Check(Native.QueryAffinity(h) == target,
                        "restricted affinity preserved on refusal");
                    CoreJointBench.Check(Native.SetProcessAffinityMask(h, (UIntPtr)original),
                        "restriction cleaned up");
                }
            }
            finally
            {
                CoreJointBench.Finish(game);
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
#endif
