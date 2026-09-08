#if PAVISE_SELFTEST && PAVISE_SELFTEST_RUNNER
using System;
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
        private static int Main(string[] args)
        {
            if (CoreIsolationWorker.TryHandleArgs(args)) return Environment.ExitCode;
            if (args.Length == 1 && args[0] == "--sample") { Sample(); return 0; }
            Settings.UseTransientStoreForCurrentProcess(); Lang.Init();
            if (args.Length == 5 && args[0] == "--owner") return Owner(args);
            if (args.Length != 1 || args[0] != "--integration") return 2;
            string report = Path.Combine(Path.GetDirectoryName(Exe), "core-isolation-integration.txt");
            using (var log = new StreamWriter(report, false))
            {
                log.AutoFlush = true; Console.SetOut(log); Console.SetError(log);
                try
                {
                    Require(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), "administrator required");
                    Require(CoreIsolationWorker.Recover(new CoreIsolationTestStore()), "existing test receipt recovery");
                    var os = new CoreIsolationNative(); var baseline = os.ReadSystem(IntPtr.Zero);
                    Require(baseline != null && baseline.Allocated == 0 && baseline.Physical.Length >= 4, "clean baseline and >=4 physical cores required");
                    ulong isolated = baseline.Physical[2];
                    if (CpuTopology.CountSetBits(isolated) < 2) isolated |= baseline.Physical[3];
                    RunPlacement(isolated);
                    RunCrash(isolated, false);
                    RunCrash(isolated, true);
                    Require(os.ReadSystem(IntPtr.Zero).Allocated == 0, "final global state");
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
                Require(os.ReadSystem(IntPtr.Zero).Allocated == isolated, "owner isolated");
                if (helperCrash)
                { owner.StandardInput.WriteLine("kill-helper"); Require(owner.StandardOutput.ReadLine() == "KILLED", "helper killed"); }
                else { owner.Kill(); owner.WaitForExit(3000); }
                var until = Stopwatch.StartNew(); bool restored = false;
                while (until.ElapsedMilliseconds < 12000)
                {
                    ulong now; string receipt; var state = os.ReadSystem(IntPtr.Zero);
                    restored = state != null && state.Allocated == 0 && os.ReadAllowed(game.Handle, out now) && now == original
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
                var plan = new CoreSchedulingPlan { GameMask = isolated, IsolationOn = true, IsolationMask = isolated, Topology = CoreScheduling.CurrentStamp };
                mode.ProbeUseIsolationWorker(plan);
                Require(mode.ProbeManualPlacement(game.Handle, game.Id, creation, original, isolated), "production placement and admission");
                Require(mode.ProbeRestoreManualPlacement(game.Id, false), "IRQ observation cleanup");
                Require(Native.QueryAffinity(game.Handle) == isolated && os.ReadSystem(IntPtr.Zero).Allocated == isolated, "IRQ cleanup preserved placement and isolation");
                ordinary.StandardInput.WriteLine("go"); game.StandardInput.WriteLine("go");
                ulong ordinaryMask = Result(ordinary), gameMask = Result(game);
                Require(ordinaryMask != 0 && (ordinaryMask & isolated) == 0, "ordinary entered isolated CPUs");
                Require(gameMask != 0 && (gameMask & ~isolated) == 0, "game admission failed");
                Require(mode.ProbeStopIsolationWorker(), "normal stop");
                ulong allowed;
                Require(os.ReadSystem(IntPtr.Zero).Allocated == 0 && os.ReadAllowed(game.Handle, out allowed) && allowed == oldAllowed, "normal stop exact allowed restore");
                Require(mode.ProbeRestoreManualPlacement(game.Id, true) && Native.QueryAffinity(game.Handle) == original, "hard affinity restore");
                Console.WriteLine("PASS production game placement + isolation: ordinary=" + ordinaryMask.ToString("X") + " game=" + gameMask.ToString("X") + " isolated=" + isolated.ToString("X"));
                Console.WriteLine("PASS observation independence, live process admission restoration and original affinity restoration");
                ulong limited = CpuTopology.PhysicalCoreMasks()[0];
                Require(Native.SetProcessAffinityMask(game.Handle, (UIntPtr)limited), "pre-existing restriction");
                mode.ProbeUseIsolationWorker(plan);
                Require(!mode.ProbeManualPlacement(game.Handle, game.Id, creation, limited, isolated), "must refuse widening original affinity");
                Require(os.ReadSystem(IntPtr.Zero).Allocated == 0 && os.ReadAllowed(game.Handle, out allowed) && allowed == oldAllowed,
                    "unconfirmed game placement must roll back isolation and admission");
                Require(Native.QueryAffinity(game.Handle) == limited, "preserve original restricted affinity");
                Require(Native.SetProcessAffinityMask(game.Handle, (UIntPtr)original), "test restriction cleanup");
                Console.WriteLine("PASS isolation rolls back when exact game placement cannot be confirmed");
            }
            finally { mode.ProbeStopIsolationWorker(); Finish(ordinary); Finish(game); }
        }
    }
    internal static partial class SelfTests
    {
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
