using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private sealed class TestFreezeLease : IDisposable
        {
            private Action release;
            internal TestFreezeLease(Action action) { release = action; }
            public void Dispose() { Action action = release; release = null; if (action != null) action(); }
        }
        internal static bool TryFreezeHelper(string[] args)
        {
            if (args.Length == 1 && args[0] == "--freeze-test-only")
            {
                Settings.UseTransientStoreForCurrentProcess(); Lang.Init();
                try { RunBackgroundFreezeTests(); }
                catch (Exception error) { Console.WriteLine("FAIL FREEZE " + error.GetType().Name + ": " + error.Message + "\n" + error.StackTrace); Environment.ExitCode = 1; }
                return true;
            }
            if (args.Length == 1 && args[0] == "--freeze-test-exit259")
            { Console.WriteLine("READY"); Console.Out.Flush(); Console.ReadLine(); Environment.ExitCode = 259; return true; }
            if (args.Length == 5 && args[0] == "--freeze-test-guard")
            {
                string file = Encoding.UTF8.GetString(Convert.FromBase64String(args[3]));
                FreezeGuard.RunIsolatedTestGuard(int.Parse(args[1]), long.Parse(args[2]), delegate(FreezeTarget target) {
                    if (target.Path.EndsWith("refused.exe", StringComparison.OrdinalIgnoreCase)) return null;
                    File.AppendAllText(file, "ACQUIRE " + target.Pid + "\n");
                    return new TestFreezeLease(delegate { File.AppendAllText(file, "RELEASE " + target.Pid + "\n"); });
                }, int.Parse(args[4]));
                return true;
            }
            if (args.Length == 4 && args[0] == "--freeze-native-owner")
            {
                var target = new FreezeTarget { Pid = int.Parse(args[1]), Creation = long.Parse(args[2]),
                    Path = Encoding.UTF8.GetString(Convert.FromBase64String(args[3])) };
                using (Process self = Process.GetCurrentProcess())
                using (IDisposable lease = FreezeNative.Acquire(target, self.SessionId, self.Id))
                { Console.WriteLine(lease == null ? "FAILED" : "FROZEN"); Console.Out.Flush(); Console.ReadLine(); }
                return true;
            }
            if (args.Length == 3 && args[0] == "--freeze-test-parent")
            {
                string journal = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
                string command = Encoding.UTF8.GetString(Convert.FromBase64String(args[2]));
                using (Process guardian = FakeFreezeGuard(journal, 3000))
                {
                    Eq("READY", CpuCapLine(guardian));
                    guardian.StandardInput.WriteLine(command); guardian.StandardInput.Flush();
                    Eq(true, CpuCapLine(guardian).StartsWith("OK "));
                    Console.WriteLine(guardian.Id); Console.Out.Flush();
                    try { Console.ReadLine(); }
                    finally { guardian.StandardInput.Close(); if (!guardian.WaitForExit(4000)) guardian.Kill(); }
                }
                return true;
            }
            return false;
        }

        private static FreezeTarget FreezeWant(Process p)
        { return new FreezeTarget { Pid = p.Id, Creation = CpuCapWant(p)[p.Id], Path = Native.ImagePath(p.Handle) }; }
        private static string FreezeCommand(FreezeTarget game, params FreezeTarget[] targets)
        {
            string result = "SYNC " + game.Pid + " " + game.Creation;
            foreach (FreezeTarget target in targets)
                result += " " + target.Pid + ":" + target.Creation + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(target.Path));
            return result;
        }
        private static Process FakeFreezeGuard(string journal, int staleMs)
        {
            using (Process self = Process.GetCurrentProcess())
                return CpuCapHelper("--freeze-test-guard " + self.Id + " " + CpuCapWant(self)[self.Id] + " "
                    + Convert.ToBase64String(Encoding.UTF8.GetBytes(journal)) + " " + staleMs);
        }
        private static void FreezeSend(Process guard, string command, string expected)
        { guard.StandardInput.WriteLine(command); guard.StandardInput.Flush(); Eq(expected, CpuCapLine(guard)); }
        private static string FreezeReport(Process guard)
        {
            guard.StandardInput.WriteLine("REPORT"); guard.StandardInput.Flush();
            string line = CpuCapLine(guard); Eq(true, line.StartsWith("TRACE "));
            return Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(6)));
        }
        private static void FreezeWaitRelease(string journal, int pid)
        {
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 4000)
            {
                if (File.Exists(journal) && File.ReadAllText(journal).Contains("RELEASE " + pid + "\n")) return;
                Thread.Sleep(20);
            }
            throw new InvalidOperationException("Freeze recovery absent: " + pid);
        }

        internal static void RunBackgroundFreezeTests()
        {
            Console.WriteLine("FREEZE_ISOLATION policy/model tests + guardian IPC using simulated leases; native suspension only on owned children when supported");
            var boundedTrace = new FreezeTrace();
            for (int n = 0; n < FreezeTrace.Capacity + 3; n++) boundedTrace.Add("test-event");
            string traceText = Encoding.UTF8.GetString(Convert.FromBase64String(boundedTrace.Drain().Substring(6)));
            Eq(true, traceText.EndsWith("trace-truncated dropped=3")); Eq("TRACE ", boundedTrace.Drain());
            string safeIdentity = FreezeTrace.Identity(new FreezeTarget { Pid=9, Creation=10, Path=@"C:\Private\User\app.exe" });
            Eq(true, safeIdentity.Contains("exe=app.exe")); Eq(false, safeIdentity.Contains("Private"));
            foreach (PerformancePreset mode in new[] { PerformancePreset.Standard, PerformancePreset.Competitive, PerformancePreset.Handheld, PerformancePreset.Extreme })
                Eq(mode == PerformancePreset.Extreme, BackgroundFreezePolicy.Eligible(mode, 100, 10, @"C:\Apps\anything.exe", 7, 7, false, false, @"C:\Windows", true));
            Eq(false, BackgroundFreezePolicy.Eligible(PerformancePreset.Extreme, 100, 10, @"C:\Apps\anything.exe", 7, 7, true, false, @"C:\Windows"));
            Eq(false, BackgroundFreezePolicy.Eligible(PerformancePreset.Extreme, 100, 10, @"C:\Apps\anything.exe", 7, 7, false, true, @"C:\Windows"));
            Eq(false, BackgroundFreezePolicy.Eligible(PerformancePreset.Extreme, 100, 10, @"C:\Windows\System32\worker.exe", 7, 7, false, false, @"C:\Windows", true));
            Eq(true, BackgroundFreezePolicy.Eligible(PerformancePreset.Extreme, 100, 10, @"C:\WindowsExtra\worker.exe", 7, 7, false, false, @"C:\Windows", true));
            Eq(false, BackgroundFreezePolicy.Eligible(PerformancePreset.Extreme, 100, 10, @"C:\Apps\anything.exe", 0, 7, false, false, @"C:\Windows", true));
            Eq(false, BackgroundFreezePolicy.Eligible(PerformancePreset.Extreme, 100, 0, @"C:\Apps\anything.exe", 7, 7, false, false, @"C:\Windows", true));

            var profile = new GameProfile { Root = @"C:\Games\Example", ExecutablePath = @"C:\Games\Example\game.exe" };
            var snapshot = new ProcessSnapshot(new[] {
                new ProcEntry { Pid=100, ParentPid=90, Session=7, Creation=10, Path=profile.ExecutablePath },
                new ProcEntry { Pid=101, Session=7, Creation=11, Path=@"C:\Games\Example\sub\updater.exe" },
                new ProcEntry { Pid=102, ParentPid=100, Session=7, Creation=12, Path=@"C:\Tools\helper.exe" },
                new ProcEntry { Pid=90, Session=7, Creation=5, Path=@"C:\Client\launcher.exe" },
                new ProcEntry { Pid=103, ParentPid=90, Session=7, Creation=14, Path=@"C:\Tools\unrelated.exe" },
                new ProcEntry { Pid=104, ParentPid=100, Session=7, Creation=4, Path=@"C:\Tools\reused.exe" },
                new ProcEntry { Pid=105, Session=7, Creation=4, Path=@"C:\Games\ExampleExtra\unrelated.exe" }
            });
            var protectedFamily = FamilyBoundary.CollectProtectedLibraryFamily(new[]{profile}, snapshot, 999, 7);
            foreach (int id in new[]{90,100,101,102}) Eq(true, protectedFamily.Contains(id));
            foreach (int id in new[]{103,104,105}) Eq(false, protectedFamily.Contains(id));
            profile.Overrides[PolicyCatalog.KeySuppressFamily] = "1";
            protectedFamily = FamilyBoundary.CollectProtectedLibraryFamily(new[]{profile}, snapshot, 999, 7, null, false, true);
            foreach (int id in new[]{90,100,101,102}) Eq(true, protectedFamily.Contains(id));
            Eq(false, FamilyBoundary.SafeFamilyDir(@"C:\"));
            Eq(false, FamilyBoundary.SafeFamilyDir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));

            var a = new FreezeTarget { Pid = 100, Creation = 10, Path = @"C:\Apps\a.exe" };
            var b = new FreezeTarget { Pid = 101, Creation = 11, Path = @"C:\Apps\b.exe" };
            var game = new FreezeTarget { Pid = 200, Creation = 5, Path = @"C:\Games\game.exe" };
            int anchor; long born; Dictionary<int, FreezeTarget> parsed;
            Eq(true, FreezeGuard.Parse(FreezeCommand(game, a, b), out anchor, out born, out parsed));
            Eq(200, anchor); Eq(5L, born); Eq(2, parsed.Count); Eq(a.Path, parsed[100].Path);
            foreach (string command in new[]{"", "SYNC 0 1", "SYNC 200 0", FreezeCommand(game, game), FreezeCommand(game, a, a), "SYNC 200 5 100:10:!!"})
                Eq(false, FreezeGuard.Parse(command, out anchor, out born, out parsed));

            int acquired = 0, released = 0, calls = 0; bool allowed = true;
            Func<FreezeTarget, IDisposable> acquire = delegate(FreezeTarget target) {
                calls++; acquired++; return new TestFreezeLease(delegate { released++; });
            };
            var wanted = new Dictionary<int, FreezeTarget> { {100, a}, {101, b} };
            using (var leases = new FreezeLeaseSet())
            {
                leases.Sync(wanted, () => allowed, acquire); Eq(2, leases.Count);
                for (int i=0;i<20;i++) leases.Sync(wanted, () => allowed, acquire);
                Eq(2, calls);
                leases.Release(100, 999); Eq(2, leases.Count);
                leases.Release(100, 10); Eq(1, leases.Count); Eq(1, released);
                wanted.Remove(100); leases.Sync(wanted, () => allowed, acquire); Eq(1, leases.Count);
                wanted[101] = new FreezeTarget { Pid=101, Creation=99, Path=b.Path };
                leases.Sync(wanted, () => allowed, acquire); Eq(3, acquired); Eq(2, released);
                allowed = false; leases.Sync(wanted, () => allowed, acquire); Eq(0, leases.Count); Eq(acquired, released);
                allowed = true;
                leases.Sync(wanted, () => allowed, delegate(FreezeTarget target) { calls++; return null; });
                int refusedCalls = calls;
                leases.Sync(wanted, () => allowed, acquire); Eq(refusedCalls, calls); Eq(0, leases.Count);
                leases.Dispose();
                leases.Sync(wanted, () => allowed, delegate(FreezeTarget target) { allowed = false; return acquire(target); });
                Eq(0, leases.Count); Eq(acquired, released);
                allowed = true; wanted[100] = a;
                bool threw = false;
                try { leases.Sync(wanted, () => allowed, delegate(FreezeTarget target) {
                    if (leases.Count > 0) throw new IOException("simulated"); return acquire(target); }); }
                catch (IOException) { threw = true; }
                Eq(true, threw); Eq(0, leases.Count); Eq(acquired, released);
            }
            Console.WriteLine("FREEZE policy/family/PID reuse/idempotence/refusal/partial failure PASS");

            string dir = Path.Combine(Path.GetTempPath(), "PaviseFreezeTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Process anchorProcess = CpuCapHelper("--cpu-cap-test-target"), targetProcess = CpuCapHelper("--cpu-cap-test-target"), guard = null;
            try
            {
                Eq("READY", CpuCapLine(anchorProcess)); Eq("READY", CpuCapLine(targetProcess));
                FreezeTarget anchorTarget = FreezeWant(anchorProcess), target = FreezeWant(targetProcess);
                string journal = Path.Combine(dir, "normal.txt");
                guard = FakeFreezeGuard(journal, 3000); Eq("READY", CpuCapLine(guard));
                string command = FreezeCommand(anchorTarget, target), ack = "OK " + target.Pid;
                FreezeSend(guard, command, ack);
                string report = FreezeReport(guard);
                Eq(true, report.Contains("acquired pid=" + target.Pid + " born=" + target.Creation));
                Eq(true, report.Contains("result=simulated-lease"));
                FreezeSend(guard, command, ack);
                Eq("", FreezeReport(guard));
                Eq(1, File.ReadAllLines(journal).Length);
                FreezeSend(guard, "RELEASE " + target.Pid + " " + (target.Creation+1), "OK");
                Eq(1, File.ReadAllLines(journal).Length);
                FreezeSend(guard, "RELEASE " + target.Pid + " " + target.Creation, "OK");
                report = FreezeReport(guard);
                Eq(true, report.Contains("released pid=" + target.Pid)); Eq(true, report.Contains("reason=explicit-release"));
                FreezeWaitRelease(journal, target.Pid);
                FreezeSend(guard, command, ack);
                FreezeSend(guard, FreezeCommand(new FreezeTarget { Pid=anchorTarget.Pid, Creation=anchorTarget.Creation+1 }), "OK ");
                report = FreezeReport(guard);
                Eq(true, report.Contains("anchor-rejected")); Eq(true, report.Contains("released pid=" + target.Pid));
                FreezeSend(guard, command, ack);
                var loggingClient = new BackgroundFreeze();
                typeof(BackgroundFreeze).GetField("guard", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(loggingClient, guard);
                typeof(BackgroundFreeze).GetField("automationToken", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(loggingClient, "freeze-test-session");
                string oldLog = Logger.LogPath;
                try
                {
                    Logger.LogPath = Path.Combine(dir, "trace.log");
                    typeof(BackgroundFreeze).GetMethod("ReadTrace", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(loggingClient, null);
                    Eq(false, File.Exists(Logger.LogPath));
                    typeof(BackgroundFreeze).GetMethod("ReadTrace", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(loggingClient, null);
                    Eq(false, File.Exists(Logger.LogPath));
                    Logger.WritesEnabled = false;
                    FreezeSend(guard, "RELEASE " + target.Pid + " " + target.Creation, "OK");
                    string invalidAnchor = FreezeCommand(new FreezeTarget { Pid=anchorTarget.Pid, Creation=anchorTarget.Creation+1 });
                    FreezeSend(guard, invalidAnchor, "OK ");
                    typeof(BackgroundFreeze).GetMethod("ReadTrace", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(loggingClient, null);
                    Eq(false, File.Exists(Logger.LogPath));
                    Logger.WritesEnabled = true;
                    FreezeSend(guard, invalidAnchor, "OK ");
                    typeof(BackgroundFreeze).GetMethod("ReadTrace", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(loggingClient, null);
                    string[] warningLines = File.ReadAllLines(Logger.LogPath);
                    Eq(1, warningLines.Length);
                    Eq(LogEventSeverity.Warning, LogStreamView.Parse(warningLines[0]).Severity);
                    Eq(true, warningLines[0].Contains("anchor-rejected"));
                    Eq(true, warningLines[0].Contains("AUTO [freeze-test-session] [FREEZE]"));
                    FreezeSend(guard, command, ack);
                }
                finally { Logger.WritesEnabled = true; Logger.LogPath = oldLog; }
                guard.StandardInput.Close(); Eq(true, guard.WaitForExit(4000));
                Eq(8, File.ReadAllLines(journal).Length);
                CpuCapKill(guard); guard = null;

                guard = FakeFreezeGuard(Path.Combine(dir, "refusal.txt"), 3000); Eq("READY", CpuCapLine(guard));
                var refusedTarget = new FreezeTarget { Pid=target.Pid, Creation=target.Creation, Path=@"C:\Test\refused.exe" };
                FreezeSend(guard, FreezeCommand(anchorTarget, refusedTarget), "OK ");
                report = FreezeReport(guard);
                Eq(true, report.Contains("refused pid=" + target.Pid)); Eq(true, report.Contains("fallback=job-candidate"));
                FreezeSend(guard, FreezeCommand(anchorTarget, refusedTarget), "OK "); Eq("", FreezeReport(guard));
                guard.StandardInput.Close(); Eq(true, guard.WaitForExit(4000)); CpuCapKill(guard); guard = null;
                Console.WriteLine("FREEZE diagnostics: identity, success/refusal/release, bounded buffer, REPORT drain, quiet steady state, log switch PASS");

                journal = Path.Combine(dir, "stale.txt");
                guard = FakeFreezeGuard(journal, 600); Eq("READY", CpuCapLine(guard));
                FreezeSend(guard, command, ack); Eq(true, guard.WaitForExit(4000));
                Eq(5, guard.ExitCode);
                FreezeWaitRelease(journal, target.Pid); CpuCapKill(guard); guard = null;

                journal = Path.Combine(dir, "blocked-pipe.txt");
                guard = FakeFreezeGuard(journal, 2000); Eq("READY", CpuCapLine(guard));
                var many = new FreezeTarget[3000];
                for (int i=0;i<many.Length;i++) many[i] = new FreezeTarget { Pid=1000000+i, Creation=10, Path=@"C:\Test\mock.exe" };
                guard.StandardInput.WriteLine(FreezeCommand(anchorTarget, many)); guard.StandardInput.Flush();
                Eq(true, guard.WaitForExit(6000)); Eq(2, guard.ExitCode);
                Console.WriteLine("FREEZE independent watchdog: blocked reply cannot retain guardian PASS (simulated leases)");
                CpuCapKill(guard); guard = null;

                journal = Path.Combine(dir, "parent-crash.txt");
                using (Process owner = CpuCapHelper("--freeze-test-parent " + Convert.ToBase64String(Encoding.UTF8.GetBytes(journal))
                    + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(command))))
                using (Process watcher = Process.GetProcessById(int.Parse(CpuCapLine(owner))))
                {
                    try
                    {
                        owner.Kill(); Eq(true, owner.WaitForExit(3000));
                        FreezeWaitRelease(journal, target.Pid); Eq(true, watcher.WaitForExit(4000));
                    }
                    finally
                    { if (!owner.HasExited) owner.Kill(); if (!watcher.HasExited) watcher.Kill(); }
                }

                journal = Path.Combine(dir, "game-exit.txt");
                guard = FakeFreezeGuard(journal, 3000); Eq("READY", CpuCapLine(guard));
                FreezeSend(guard, command, ack); anchorProcess.Kill(); anchorProcess.WaitForExit(3000);
                FreezeWaitRelease(journal, target.Pid);
                report = FreezeReport(guard); Eq(true, report.Contains("reason=game-exited-or-invalid"));
                guard.StandardInput.Close(); Eq(true, guard.WaitForExit(4000));
                CpuCapKill(guard); guard = null;
                Console.WriteLine("FREEZE guardian IPC: hidden game/alt-tab persistence, identity release, stale anchor, EOF, parent crash, stale scan, game exit PASS (simulated leases)");

                journal = Path.Combine(dir, "exit259.txt");
                using (Process unusualExit = CpuCapHelper("--freeze-test-exit259"))
                {
                    Eq("READY", CpuCapLine(unusualExit));
                    guard = FakeFreezeGuard(journal, 3000); Eq("READY", CpuCapLine(guard));
                    FreezeSend(guard, FreezeCommand(FreezeWant(unusualExit), target), ack);
                    unusualExit.StandardInput.WriteLine("EXIT"); unusualExit.StandardInput.Flush();
                    Eq(true, unusualExit.WaitForExit(3000)); Eq(259, unusualExit.ExitCode);
                    Eq(false, FreezeNative.IsAlive(unusualExit.Handle));
                    FreezeWaitRelease(journal, target.Pid); guard.StandardInput.Close(); Eq(true, guard.WaitForExit(4000));
                    CpuCapKill(guard); guard = null;
                }
                Console.WriteLine("FREEZE exit-code 259 cannot masquerade as a live game PASS");

                var mode = new GameMode(dir, new SuppressionCore());
                var ownedFreezer = (BackgroundFreeze)typeof(GameMode).GetField("backgroundFreeze", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode);
                foreach (string action in new[]{"mode", "disable", "stop"})
                {
                    mode.Preset = PerformancePreset.Extreme;
                    typeof(GameMode).GetField("enabled", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mode, true);
                    guard = FakeFreezeGuard(Path.Combine(dir, action + ".txt"), 3000); Eq("READY", CpuCapLine(guard));
                    typeof(BackgroundFreeze).GetField("guard", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ownedFreezer, guard);
                    int helperId = guard.Id;
                    if (action == "mode") mode.Preset = PerformancePreset.Competitive;
                    else if (action == "disable") mode.Enabled = false;
                    else Eq(true, mode.Stop());
                    Eq(false, ownedFreezer.HasPending);
                    bool alive;
                    try { using (Process helper = Process.GetProcessById(helperId)) alive = !helper.HasExited; }
                    catch (ArgumentException) { alive = false; }
                    Eq(false, alive); guard = null;
                }
                Console.WriteLine("FREEZE lifecycle: Extreme-to-Competitive, master disable and shutdown guardian termination PASS");

                if (!FreezeNative.Supported)
                {
                    var freezer = new BackgroundFreeze();
                    Eq(0, freezer.Sync(new Dictionary<int, FreezeTarget>{{target.Pid,target}}, target.Pid+1, 1).Count);
                    Eq(0, freezer.ActiveCount); Eq(true, freezer.Clear());
                    Console.WriteLine("SKIP FREEZE native state-object suspension/crash cleanup: OS exports unavailable; Job fallback PASS");
                }
                else
                {
                    for (int round=0;round<3;round++)
                    {
                        guard = CpuCapHelper("--freeze-native-owner " + target.Pid + " " + target.Creation + " "
                            + Convert.ToBase64String(Encoding.UTF8.GetBytes(target.Path)));
                        Eq("FROZEN", CpuCapLine(guard));
                        targetProcess.StandardInput.WriteLine("PING"); targetProcess.StandardInput.Flush();
                        Task<string> reply = Task.Factory.StartNew(() => targetProcess.StandardOutput.ReadLine());
                        Eq(false, reply.Wait(100));
                        guard.Kill(); Eq(true, guard.WaitForExit(3000));
                        Eq(true, reply.Wait(3000)); Eq("OK", reply.Result);
                        CpuCapKill(guard); guard = null;
                    }
                    Console.WriteLine("FREEZE native state-object suspension and owner-crash restoration PASS");
                }
            }
            finally { CpuCapKill(guard); CpuCapKill(anchorProcess); CpuCapKill(targetProcess); }
        }
    }
}
