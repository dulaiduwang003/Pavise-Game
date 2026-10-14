#if PAVISE_SELFTEST && PAVISE_SELFTEST_RUNNER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace PaviseApp
{
    internal static class CoreIsolationIntegrationRunner
    {
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();
        private static string Exe { get { return typeof(CoreIsolationIntegrationRunner).Assembly.Location; } }
        private static ulong Pre;
        private static int Main(string[] args)
        {
            if (CoreIsolationWorker.TryHandleArgs(args)) return Environment.ExitCode;
            if (args.Length == 1 && args[0] == "--sample") { Sample(); return 0; }
            if (args.Length == 1 && args[0] == "--load") { Load(); return 0; }
            Settings.UseTransientStoreForCurrentProcess(); Lang.Init();
            if (args.Length == 5 && args[0] == "--owner") return Owner(args);
            bool smoke = args.Length == 1 && args[0] == "--smoke";
            if (!smoke && (args.Length != 1 || args[0] != "--integration")) return 2;
            string report = Path.Combine(Path.GetDirectoryName(Exe), "core-isolation-integration.txt");
            using (var log = new StreamWriter(report, false))
            {
                log.AutoFlush = true; Console.SetOut(log); Console.SetError(log);
                try
                {
                    Require(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), "administrator required");
                    Require(CoreIsolationWorker.Recover(new CoreIsolationTestStore()), "existing test receipt recovery");
                    var os = new CoreIsolationNative(); var baseline = os.ReadSystem(IntPtr.Zero);
                    Require(baseline != null && baseline.Physical.Length >= 4, ">=4 physical cores required");
                    Pre = baseline.Allocated;
                    ulong isolated = 0;
                    foreach (ulong core in baseline.Physical)
                    {
                        if ((core & 1UL) != 0 || (core & Pre) != 0) continue;
                        isolated |= core;
                        if (CpuTopology.CountSetBits(isolated) >= 2) break;
                    }
                    Require(CpuTopology.CountSetBits(isolated) >= 2, "two unreserved logical CPUs outside CPU 0's core required");
                    RunPlacement(isolated);
                    RunCrash(isolated, false);
                    RunCrash(isolated, true);
                    if (!smoke) SelfTests.RunAutoCoresLiveIntegration(Pre);
                    Require(os.ReadSystem(IntPtr.Zero).Allocated == Pre, "final global state");
                    string receipt; Require(new CoreIsolationTestStore().Read(out receipt) && receipt == "", "final receipt empty");
                    Console.WriteLine("PASS all isolation integration checks; system state restored; no normal app runtime or user settings started");
                    return 0;
                }
                catch (Exception e) { Console.WriteLine("FAIL " + e); return 1; }
                finally { CoreIsolationWorker.Recover(new CoreIsolationTestStore()); }
            }
        }

        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static Process Child(string arguments)
        {
            return Process.Start(new ProcessStartInfo(Exe, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
        }
        private static long Creation(Process p)
        {
            long creation, cpu; ulong io;
            Require(Native.QueryProcessSample(p.Handle, out creation, out cpu, out io), "child identity"); return creation;
        }
        private static void Finish(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) { p.StandardInput.WriteLine("exit"); if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(3000); } } }
            finally { p.Dispose(); }
        }
        private static void Sample()
        {
            Console.WriteLine("READY"); string command;
            while ((command = Console.ReadLine()) != null && command != "exit")
            {
                if (command != "go") continue;
                long observed = 0; var end = DateTime.UtcNow.AddMilliseconds(1500);
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
                    }); threads[i].Start();
                }
                foreach (Thread t in threads) t.Join();
                Console.WriteLine(unchecked((ulong)observed).ToString("X"));
            }
        }
        private static int loadWanted;
        private static long loadSeen;

        private static void Load()
        {
            var workers = new List<Thread>();
            Console.WriteLine("READY"); string command;
            while ((command = Console.ReadLine()) != null && command != "exit")
            {
                if (command.StartsWith("threads "))
                {
                    int wanted = int.Parse(command.Substring(8), CultureInfo.InvariantCulture);
                    Volatile.Write(ref loadWanted, wanted);
                    for (int i = 0; i < wanted; i++)
                    {
                        if (i < workers.Count && workers[i].IsAlive) continue;
                        int index = i;
                        var worker = new Thread(delegate()
                        {
                            while (index < Volatile.Read(ref loadWanted))
                            {
                                long bit = unchecked((long)(1UL << (int)GetCurrentProcessorNumber())), old;
                                do { old = Interlocked.Read(ref loadSeen); }
                                while ((old & bit) == 0 && Interlocked.CompareExchange(ref loadSeen, old | bit, old) != old);
                                Thread.SpinWait(2000);
                            }
                        });
                        worker.IsBackground = true; worker.Start();
                        if (i < workers.Count) workers[i] = worker; else workers.Add(worker);
                    }
                    Console.WriteLine("OK");
                }
                else if (command == "where")
                {
                    Interlocked.Exchange(ref loadSeen, 0); Thread.Sleep(1500);
                    Console.WriteLine(unchecked((ulong)Interlocked.Read(ref loadSeen)).ToString("X"));
                }
            }
            Volatile.Write(ref loadWanted, 0);
        }

        internal static Process StartChild(string arguments) { return Child(arguments); }
        internal static long ChildCreation(Process p) { return Creation(p); }
        internal static void FinishChild(Process p) { Finish(p); }
        internal static ulong ReadMask(Process p) { return Result(p); }
        internal static void Check(bool value, string message) { Require(value, message); }
        internal static string ExePath { get { return Exe; } }

        private static ulong Result(Process p)
        {
            var read = System.Threading.Tasks.Task.Factory.StartNew(() => p.StandardOutput.ReadLine());
            Require(read.Wait(6000), "sample timeout");
            return ulong.Parse(read.Result, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static void RunPlacement(ulong isolated)
        {
            SelfTests.RunCoreIsolationPlacementIntegration(isolated);
        }

        private static int Owner(string[] args)
        {
            var client = new CoreIsolationClient(); client.UseTestWorker();
            Logger.LogPath = Exe + ".owner.log";
            try
            {
                int pid = int.Parse(args[1]); long creation = long.Parse(args[2]);
                ulong iso = ulong.Parse(args[3], NumberStyles.HexNumber), game = ulong.Parse(args[4], NumberStyles.HexNumber);
                Require(client.Ensure(iso, pid, creation, game), "owner admission " + client.LastError);
                Console.WriteLine("READY"); string command;
                while ((command = Console.ReadLine()) != null && command != "exit")
                    if (command == "kill-helper") { client.KillWorkerForTest(); Console.WriteLine("KILLED"); }
                return client.Stop() ? 0 : 1;
            }
            finally { client.Stop(); }
        }
        private static void RunCrash(ulong isolated, bool helperCrash)
        {
            var os = new CoreIsolationNative(); Process game = null, owner = null;
            try
            {
                game = Child("--sample"); Require(game.StandardOutput.ReadLine() == "READY", "game ready");
                long creation = Creation(game); ulong original;
                Require(os.ReadAllowed(game.Handle, out original), "original allowed");
                owner = Child("--owner " + game.Id + " " + creation + " " + isolated.ToString("X") + " " + isolated.ToString("X"));
                Require(owner.StandardOutput.ReadLine() == "READY", "owner ready");
                Require(os.ReadSystem(IntPtr.Zero).Allocated == (Pre | isolated), "owner isolated");
                if (helperCrash)
                { owner.StandardInput.WriteLine("kill-helper"); Require(owner.StandardOutput.ReadLine() == "KILLED", "helper killed"); }
                else { owner.Kill(); owner.WaitForExit(3000); }
                var until = Stopwatch.StartNew(); bool restored = false;
                while (until.ElapsedMilliseconds < 12000)
                {
                    ulong now; string receipt; var state = os.ReadSystem(IntPtr.Zero);
                    restored = state != null && state.Allocated == Pre && os.ReadAllowed(game.Handle, out now) && now == original
                        && new CoreIsolationTestStore().Read(out receipt) && receipt == "";
                    if (restored) break; Thread.Sleep(100);
                }
                Require(restored, "crash restoration timeout");
                Require(Native.StillActive(game.Handle), "test game remains alive through recovery");
                Console.WriteLine("PASS " + (helperCrash ? "helper crash: durable receipt recovered by live client" : "parent crash: independent helper restored system and live child admission"));
            }
            finally { Finish(owner); Finish(game); CoreIsolationWorker.Recover(new CoreIsolationTestStore()); }
        }

        internal static void PlacementFixture(GameMode mode, ulong isolated)
        {
            var os = new CoreIsolationNative(); Process ordinary = null, game = null;
            try
            {
                ordinary = Child("--sample"); game = Child("--sample");
                Require(ordinary.StandardOutput.ReadLine() == "READY" && game.StandardOutput.ReadLine() == "READY", "children ready");
                ulong original = Native.QueryAffinity(game.Handle), oldAllowed;
                Require(os.ReadAllowed(game.Handle, out oldAllowed), "old allowed"); long creation = Creation(game);
                mode.ProbeUseIsolationWorker();
                Require(mode.ProbeExclusivePlacement(game.Handle, game.Id, creation, original, isolated), "production placement and admission");
                Require(Native.QueryAffinity(game.Handle) == isolated && os.ReadSystem(IntPtr.Zero).Allocated == (Pre | isolated), "placement and isolation applied");
                ordinary.StandardInput.WriteLine("go"); game.StandardInput.WriteLine("go");
                ulong ordinaryMask = Result(ordinary), gameMask = Result(game);
                Require(ordinaryMask != 0 && (ordinaryMask & isolated) == 0, "ordinary entered isolated CPUs");
                Require(gameMask != 0 && (gameMask & ~isolated) == 0, "game admission failed");
                Require(mode.ProbeStopIsolationWorker(), "normal stop");
                ulong allowed;
                Require(os.ReadSystem(IntPtr.Zero).Allocated == Pre && os.ReadAllowed(game.Handle, out allowed) && allowed == oldAllowed, "normal stop exact allowed restore");
                Require(mode.ProbeRestoreHardPin(game.Id) && Native.QueryAffinity(game.Handle) == original, "hard affinity restore");
                Console.WriteLine("PASS production game placement + isolation: ordinary=" + ordinaryMask.ToString("X") + " game=" + gameMask.ToString("X") + " isolated=" + isolated.ToString("X"));
                Console.WriteLine("PASS live process admission restoration and original affinity restoration");
                ulong limited = CpuTopology.PhysicalCoreMasks()[0];
                Require(Native.SetProcessAffinityMask(game.Handle, (UIntPtr)limited), "pre-existing restriction");
                mode.ProbeUseIsolationWorker();
                Require(!mode.ProbeExclusivePlacement(game.Handle, game.Id, creation, limited, isolated), "must refuse widening original affinity");
                Require(os.ReadSystem(IntPtr.Zero).Allocated == Pre && os.ReadAllowed(game.Handle, out allowed) && allowed == oldAllowed,
                    "unconfirmed game placement must roll back isolation and admission");
                Require(Native.QueryAffinity(game.Handle) == limited, "preserve original restricted affinity");
                Require(Native.SetProcessAffinityMask(game.Handle, (UIntPtr)original), "test restriction cleanup");
                Console.WriteLine("PASS isolation never starts when exact game placement cannot be confirmed");
            }
            finally { mode.ProbeStopIsolationWorker(); Finish(ordinary); Finish(game); }
        }
    }
    internal static partial class SelfTests
    {
        internal static void RunAutoCoresLiveIntegration(ulong pre)
        {
            AutoCorePlan plan = AutoCores.Current(false);
            CoreIsolationIntegrationRunner.Check(plan.Available, "auto plan unavailable: " + plan.Blocked);
            Console.WriteLine("AUTO plan " + plan.Kind + " exclusive=" + plan.Exclusive.ToString("X") + " cores=" + plan.Cores
                + " allocated=" + plan.Allocated.ToString("X"));
            string root = Path.Combine(Path.GetTempPath(), "PaviseAutoCores-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Logger.LogPath = Path.Combine(root, "auto-cores.log");
            var os = new CoreIsolationNative();
            using (var fixture = new FamilyPolicyFixture(root, "auto-cores"))
            {
                GameMode mode = fixture.Mode;
                Process game = null, ordinary = null;
                try
                {
                    game = AutoStartLoad(mode, plan, out ordinary);
                    long creation = CoreIsolationIntegrationRunner.ChildCreation(game);
                    ulong original = Native.QueryAffinity(game.Handle);
                    AutoSend(game, "threads 3");
                    long started = Stopwatch.GetTimestamp();
                    CoreIsolationIntegrationRunner.Check(AutoTickUntil(mode, 30000, delegate { return mode.ProbeCoreEngaged; }),
                        "three busy threads did not engage exclusive cores");
                    Console.WriteLine("PASS light load engaged after " + AutoSeconds(started) + " s");
                    CoreIsolationIntegrationRunner.Check(Native.QueryAffinity(game.Handle) == plan.Exclusive, "game affinity is not the exclusive range");
                    CoreIsolationIntegrationRunner.Check(os.ReadSystem(IntPtr.Zero).Allocated == (pre | plan.Exclusive), "system isolation missing");
                    ulong where = AutoWhere(game);
                    CoreIsolationIntegrationRunner.Check(where != 0 && (where & ~plan.Exclusive) == 0, "game ran outside exclusive cores: " + where.ToString("X"));
                    ordinary.StandardInput.WriteLine("go");
                    ulong other = CoreIsolationIntegrationRunner.ReadMask(ordinary);
                    CoreIsolationIntegrationRunner.Check(other != 0 && (other & plan.Exclusive) == 0, "ordinary process entered exclusive cores: " + other.ToString("X"));
                    Console.WriteLine("PASS exclusive: game=" + where.ToString("X") + " ordinary=" + other.ToString("X"));

                    AutoSend(game, "threads " + (plan.Cores * 2 + 2));
                    started = Stopwatch.GetTimestamp();
                    CoreIsolationIntegrationRunner.Check(AutoTickUntil(mode, 30000,
                        delegate { return AutoCores.Status.Expansions > 0; }), "wide load did not expand game capacity");
                    Console.WriteLine("PASS wide load expanded after " + AutoSeconds(started) + " s");
                    ulong expanded = Native.QueryAffinity(game.Handle);
                    CoreIsolationIntegrationRunner.Check((expanded & plan.Exclusive) == plan.Exclusive && expanded != plan.Exclusive,
                        "expansion dropped the reserved base or added no capacity");
                    CoreIsolationIntegrationRunner.Check(mode.ProbeCoreEngaged && os.ReadSystem(IntPtr.Zero).Allocated == (pre | plan.Exclusive),
                        "expansion released the original reservation");
                    AutoSend(game, "threads 0");
                    CoreIsolationIntegrationRunner.Check(!AutoTickUntil(mode, 12000, delegate { return !mode.ProbeCoreEngaged; }),
                        "idle load released the reservation");
                    Console.WriteLine("PASS idle retains exclusive base and expanded game affinity");
                }
                finally { AutoFinish(mode, game, ordinary); }
                CoreIsolationIntegrationRunner.Check(os.ReadSystem(IntPtr.Zero).Allocated == pre, "system state after first fixture");

                try
                {
                    game = AutoStartLoad(mode, plan, out ordinary);
                    AutoSend(game, "threads 2");
                    CoreIsolationIntegrationRunner.Check(AutoTickUntil(mode, 30000, delegate { return mode.ProbeCoreEngaged; }),
                        "two busy threads did not engage exclusive cores");
                    ulong outside = CpuTopology.AllMask & ~1UL;
                    CoreIsolationIntegrationRunner.Check(Native.SetProcessAffinityMask(game.Handle, (UIntPtr)outside), "external affinity write");
                    CoreIsolationIntegrationRunner.Check(AutoTickUntil(mode, 5000, delegate { return !mode.ProbeCoreEngaged; }),
                        "external affinity change was not honoured");
                    CoreIsolationIntegrationRunner.Check(mode.ProbeGovernor.Blocked == "schedule.auto.external", "blocked reason " + mode.ProbeGovernor.Blocked);
                    CoreIsolationIntegrationRunner.Check(Native.QueryAffinity(game.Handle) == outside, "external affinity overwritten");
                    CoreIsolationIntegrationRunner.Check(os.ReadSystem(IntPtr.Zero).Allocated == pre, "isolation kept after external change");
                    CoreIsolationIntegrationRunner.Check(!AutoTickUntil(mode, 6000, delegate { return mode.ProbeCoreEngaged; }),
                        "exclusive cores came back after the session was blocked");
                    Console.WriteLine("PASS external affinity change: exclusive withdrawn, external value kept, session stays shared");
                    AutoUnboost(mode);
                    CoreIsolationIntegrationRunner.Check(Native.QueryAffinity(game.Handle) == outside, "match end overwrote the yielded affinity");
                    Console.WriteLine("PASS match end keeps the yielded external affinity");
                }
                finally { AutoFinish(mode, game, ordinary); }

                try
                {
                    game = AutoStartLoad(mode, plan, out ordinary);
                    AutoSend(game, "threads 2");
                    AutoTickUntil(mode, 3000, delegate { return false; });
                    ulong early = CpuTopology.AllMask & ~1UL;
                    CoreIsolationIntegrationRunner.Check(Native.SetProcessAffinityMask(game.Handle, (UIntPtr)early), "shared-phase affinity write");
                    CoreIsolationIntegrationRunner.Check(AutoTickUntil(mode, 30000,
                        delegate { return mode.ProbeGovernor.Blocked != null; }), "shared-phase change was not noticed");
                    CoreIsolationIntegrationRunner.Check(mode.ProbeGovernor.Blocked == "schedule.auto.external", "blocked reason " + mode.ProbeGovernor.Blocked);
                    CoreIsolationIntegrationRunner.Check(!mode.ProbeCoreEngaged && Native.QueryAffinity(game.Handle) == early,
                        "entering exclusive overwrote a shared-phase affinity change");
                    CoreIsolationIntegrationRunner.Check(os.ReadSystem(IntPtr.Zero).Allocated == pre, "isolation started after a shared-phase change");
                    Console.WriteLine("PASS affinity changed before exclusivity: no write, session stays shared");
                }
                finally { AutoFinish(mode, game, ordinary); }
            }
            CoreIsolationIntegrationRunner.Check(os.ReadSystem(IntPtr.Zero).Allocated == pre, "final auto state");
            Console.WriteLine("PASS automatic exclusive cores live run; log " + Logger.LogPath);
        }

        private static Process AutoStartLoad(GameMode mode, AutoCorePlan plan, out Process ordinary)
        {
            FamilyPolicySetField(mode, "renderLaneOn", false);
            mode.ProbeUseIsolationWorker();
            mode.Preset = PerformancePreset.Competitive;
            mode.ProbeBeginAutoCores(plan);
            Process game = CoreIsolationIntegrationRunner.StartChild("--load");
            ordinary = CoreIsolationIntegrationRunner.StartChild("--sample");
            CoreIsolationIntegrationRunner.Check(game.StandardOutput.ReadLine() == "READY"
                && ordinary.StandardOutput.ReadLine() == "READY", "children ready");
            mode.RendererTestForeground = delegate { return game.Id; };
            string exe = CoreIsolationIntegrationRunner.ExePath;
            FamilyPolicySetField(mode, "activeDetection", new GameDetection
            {
                RendererPid = game.Id, RendererCreation = CoreIsolationIntegrationRunner.ChildCreation(game),
                RendererName = Path.GetFileNameWithoutExtension(exe), RendererPath = exe,
                RendererCandidateSelected = true, RendererForeground = true
            });
            return game;
        }

        private static void AutoUnboost(GameMode mode)
        {
            CoreIsolationIntegrationRunner.Check((bool)typeof(GameMode).GetMethod("UnboostGames",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                .Invoke(mode, null), "unboost restored the test game");
        }

        private static void AutoFinish(GameMode mode, Process game, Process ordinary)
        {
            AutoUnboost(mode);
            mode.ProbeStopIsolationWorker();
            FamilyPolicySetField(mode, "activeDetection", null);
            if (game != null) { try { game.StandardInput.WriteLine("threads 0"); } catch { } }
            CoreIsolationIntegrationRunner.FinishChild(game);
            CoreIsolationIntegrationRunner.FinishChild(ordinary);
            CoreIsolationWorker.Recover(new CoreIsolationTestStore());
        }

        private static void AutoSend(Process p, string command)
        {
            p.StandardInput.WriteLine(command);
            CoreIsolationIntegrationRunner.Check(p.StandardOutput.ReadLine() == "OK", "child did not accept " + command);
        }

        private static ulong AutoWhere(Process p)
        {
            p.StandardInput.WriteLine("where");
            return CoreIsolationIntegrationRunner.ReadMask(p);
        }

        private static string AutoSeconds(long started)
        {
            return ((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static bool AutoTickUntil(GameMode mode, int timeoutMs, Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                FamilyPolicyInvoke(mode, "Boost", ProcessSnapshotSource.Capture());
                AutoCoreStatus s = AutoCores.Status;
                Console.WriteLine("  t=" + clock.ElapsedMilliseconds + "ms active=" + s.Active + " load="
                    + s.Load.ToString("0.00", CultureInfo.InvariantCulture) + " windows=" + s.Windows
                    + " exclusive=" + s.Exclusive + " engaged=" + s.Engaged + " releases=" + s.Releases
                    + (s.Blocked == null ? "" : " blocked=" + s.Blocked));
                if (done()) return true;
                Thread.Sleep(1000);
            }
            return false;
        }

        internal static void RunCoreIsolationPlacementIntegration(ulong isolated)
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseIsolationIntegration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Logger.LogPath = Path.Combine(root, "integration.log");
            using (var fixture = new FamilyPolicyFixture(root, "isolation"))
                CoreIsolationIntegrationRunner.PlacementFixture(fixture.Mode, isolated);
        }
    }
}
#endif
