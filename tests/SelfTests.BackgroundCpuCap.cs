#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static Process CpuCapHelper(string argument)
        {
            return Process.Start(new ProcessStartInfo(typeof(SelfTests).Assembly.Location, argument)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
        }
        private static string CpuCapLine(Process p)
        {
            Task<string> read = Task.Factory.StartNew(() => p.StandardOutput.ReadLine());
            if (!read.Wait(5000)) throw new TimeoutException("cpu-cap-test");
            return read.Result;
        }
        private static void CpuCapKill(Process p)
        { if (p != null) { try { if (!p.HasExited) p.Kill(); p.WaitForExit(3000); } catch { } p.Dispose(); } }

        internal static bool TryCpuCapHelper(string[] args)
        {
            if (args.Length == 1 && args[0] == "--whitelist-tightening-test-only")
            { Settings.UseTransientStoreForCurrentProcess(); Lang.Init(); RunWhitelistTighteningTests(); return true; }
            if (args.Length == 1 && args[0] == "--background-scope-test-only")
            { Settings.UseTransientStoreForCurrentProcess(); Lang.Init(); RunBackgroundScopeTests(); return true; }
            if (args.Length == 1 && args[0] == "--cpu-cap-test-only")
            { Settings.UseTransientStoreForCurrentProcess(); Lang.Init(); RunBackgroundCpuCapTests(); return true; }
            if (args.Length != 1 || (args[0] != "--cpu-cap-test-target" && args[0] != "--cpu-cap-test-owner")) return false;
            Settings.UseTransientStoreForCurrentProcess();
            if (args[0] == "--cpu-cap-test-owner")
            {
                using (Process target = CpuCapHelper("--cpu-cap-test-target"))
                {
                    CpuCapLine(target);
                    var limiter = new BackgroundCpuCap();
                    limiter.OwnedTargetInHostJobForTest = target.Id;
                    try
                    {
                        Eq(true, limiter.Sync(CpuCapWant(target), BackgroundCpuCap.AutomaticPercent)); Eq(1, limiter.ActiveCount);
                        Console.WriteLine(target.Id + "|" + Settings.LoadStr(BackgroundCpuCap.ReceiptKey, ""));
                        Console.ReadLine();
                    }
                    finally { limiter.Stop(); try { target.Kill(); } catch { } }
                }
            }
            else
            {
                var expiry = new Timer(delegate { Environment.Exit(0); }, null, 120000, Timeout.Infinite);
                Console.WriteLine("READY");
                while (true)
                {
                    string command = Console.ReadLine();
                    if (command == null) { Thread.Sleep(100); continue; }
                    if (command == "SPAWN")
                    {
                        using (Process child = CpuCapHelper("--cpu-cap-test-target"))
                        { CpuCapLine(child); Console.WriteLine(child.Id); }
                    }
                    else if (command == "PING") Console.WriteLine("OK");
                    else if (command == "BURN" || command == "LOAD")
                    {
                        var threads = new List<Thread>();
                        var duration = Stopwatch.StartNew();
                        int durationMs = command == "LOAD" ? 5000 : 1500;
                        for (int i = 0; i < Math.Min(Environment.ProcessorCount, 8); i++)
                        {
                            var t = new Thread(delegate() { while (duration.ElapsedMilliseconds < durationMs) Thread.SpinWait(1000); });
                            threads.Add(t); t.Start();
                        }
                        if (command == "LOAD") { Console.WriteLine("WORKING"); Console.Out.Flush(); }
                        foreach (Thread t in threads) t.Join();
                        Console.WriteLine("OK");
                    }
                }
            }
            return true;
        }

        private static Dictionary<int, long> CpuCapWant(Process target)
        {
            long creation, cpu; ulong io;
            Eq(true, Native.QueryProcessSample(target.Handle, out creation, out cpu, out io));
            return new Dictionary<int, long> { { target.Id, creation } };
        }
        private static IntPtr CpuCapOpenReceipt(int pid = 0)
        {
            string receipt = Settings.LoadStr(BackgroundCpuCap.ReceiptKey, "");
            if (pid > 0)
                foreach (string name in receipt.Split('\n'))
                    if (name.Contains("." + pid + ".")) { receipt = name; break; }
            Eq(true, receipt.StartsWith(CpuCapNative.Prefix));
            IntPtr job = CpuCapNative.OpenJobObject(6, false, receipt);
            Eq(true, job != IntPtr.Zero); return job;
        }
        private static void CpuCapAssertRate(IntPtr job, decimal percent)
        {
            CpuCapNative.Rate rate;
            Eq(true, CpuCapNative.ReadRate(job, out rate));
            Eq(percent == 0 ? 0U : 5U, rate.Flags);
            if (percent > 0) Eq((uint)(percent * 100), rate.Value);
        }
        private static void CpuCapEventuallyOff(IntPtr job)
        {
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 5000)
            {
                CpuCapNative.Rate rate;
                if (CpuCapNative.ReadRate(job, out rate) && rate.Flags == 0) return;
                Thread.Sleep(100);
            }
            CpuCapAssertRate(job, 0);
        }

        internal static void RunBackgroundCpuCapTests()
        {
            Console.WriteLine("CPU_CAP_ISOLATION native writes target only spawned test processes; settings transient");
            Eq(0.01m, BackgroundCpuCap.AutomaticPercent);
            Eq(5m, BackgroundCpuCap.ServicePercent);
            foreach (decimal rate in new[]{0.01m, 0.1m, 1m, 5m, 50m}) Eq(true, CpuCapNative.ValidActivePercent(rate));
            foreach (decimal rate in new[]{0m, -1m, 0.001m, 0.015m, 50.01m, decimal.MaxValue}) Eq(false, CpuCapNative.ValidActivePercent(rate));
            Eq(false, GameMode.CpuCapSupported(PerformancePreset.Standard));
            Eq(false, GameMode.CpuCapSupported(PerformancePreset.Handheld));
            Eq(false, GameMode.CpuCapSupported(PerformancePreset.Competitive));
            Eq(true, GameMode.CpuCapSupported(PerformancePreset.Extreme));
            Process target = CpuCapHelper("--cpu-cap-test-target"), child = null;
            var limiter = new BackgroundCpuCap(); IntPtr job = IntPtr.Zero;
            limiter.OwnedTargetInHostJobForTest = target.Id;
            try
            {
                Eq("READY", CpuCapLine(target));
                var wanted = CpuCapWant(target);
                Eq(true, limiter.Sync(new Dictionary<int, long> { { target.Id, wanted[target.Id] + 1 } }, 20));
                Eq(0, limiter.ActiveCount);
                Eq(true, limiter.Sync(wanted, 20)); Eq(1, limiter.ActiveCount);
                job = CpuCapOpenReceipt(); CpuCapAssertRate(job, 20);
                Eq(true, CpuCapNative.SetRate(job, 0.1m)); CpuCapAssertRate(job, 0.1m);
                Eq(true, CpuCapNative.SetRate(job, 1m)); CpuCapAssertRate(job, 1m);
                Eq(true, CpuCapNative.SetRate(job, 20m)); CpuCapAssertRate(job, 20m);
                foreach (decimal invalid in new[]{-1m, 0.001m, 0.015m, 50.01m, decimal.MaxValue})
                { Eq(false, CpuCapNative.SetRate(job, invalid)); CpuCapAssertRate(job, 20m); }
                Eq(true, limiter.Sync(wanted, BackgroundCpuCap.AutomaticPercent)); CpuCapAssertRate(job, 0.01m);
                Eq(true, limiter.Sync(wanted, BackgroundCpuCap.AutomaticPercent, new HashSet<int>{target.Id})); CpuCapAssertRate(job, 5);
                Eq(true, limiter.Sync(wanted, BackgroundCpuCap.AutomaticPercent)); CpuCapAssertRate(job, 0.01m);
                Eq(true, limiter.Sync(new Dictionary<int, long>(), 10)); CpuCapAssertRate(job, 0);
                Eq(true, limiter.Sync(wanted, 20)); CpuCapAssertRate(job, 20);
                Eq(true, limiter.Release(target.Id)); CpuCapAssertRate(job, 0);
                string modeRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PaviseCpuCapMode-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(modeRoot);
                var mode = new GameMode(modeRoot, new SuppressionCore());
                typeof(GameMode).GetField("backgroundCpuCap", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mode, limiter);
                mode.Preset = PerformancePreset.Competitive;
                Eq(true, limiter.Sync(wanted, BackgroundCpuCap.AutomaticPercent)); mode.Preset = PerformancePreset.Standard; CpuCapAssertRate(job, 0);
                mode.Preset = PerformancePreset.Extreme;
                Eq(true, limiter.Sync(wanted, BackgroundCpuCap.AutomaticPercent)); mode.Preset = PerformancePreset.Handheld; CpuCapAssertRate(job, 0);
                Eq(true, limiter.Sync(wanted, 20));
                target.StandardInput.WriteLine("SPAWN"); target.StandardInput.Flush();
                child = Process.GetProcessById(int.Parse(CpuCapLine(target)));
                bool inside;
                Eq(true, CpuCapNative.IsProcessInJob(child.Handle, job, out inside)); Eq(false, inside);
                Eq(false, CpuCapNative.SetRate(job, 0 - 1)); CpuCapAssertRate(job, 20);
                limiter.OwnedTargetsInHostJobForTest = new HashSet<int>{target.Id, child.Id};
                var mixed = new Dictionary<int, long>(wanted);
                mixed[child.Id] = CpuCapWant(child)[child.Id];
                Eq(true, limiter.Sync(mixed, BackgroundCpuCap.AutomaticPercent, new HashSet<int>{child.Id}));
                Eq(2, limiter.ActiveCount); CpuCapAssertRate(job, 0.01m);
                IntPtr serviceJob = CpuCapOpenReceipt(child.Id);
                try
                {
                    CpuCapAssertRate(serviceJob, 5);
                    Eq(true, limiter.Sync(wanted, BackgroundCpuCap.AutomaticPercent));
                    CpuCapAssertRate(serviceJob, 0); CpuCapAssertRate(job, 0.01m); Eq(1, limiter.ActiveCount);
                }
                finally { Native.CloseHandle(serviceJob); }
                Eq(true, limiter.Clear());
                target.StandardInput.WriteLine("BURN"); target.StandardInput.Flush(); Eq("OK", CpuCapLine(target));
                target.StandardInput.WriteLine("LOAD"); target.StandardInput.Flush(); Eq("WORKING", CpuCapLine(target));
                target.Refresh(); TimeSpan baseCpu = target.TotalProcessorTime; var baselineClock = Stopwatch.StartNew();
                Thread.Sleep(300); target.Refresh();
                double baseline = (target.TotalProcessorTime - baseCpu).TotalMilliseconds
                    / baselineClock.Elapsed.TotalMilliseconds / Environment.ProcessorCount;
                Eq(true, baseline > 0.005);
                Eq(true, limiter.Sync(wanted, BackgroundCpuCap.AutomaticPercent)); CpuCapAssertRate(job, 0.01m);
                target.Refresh(); TimeSpan before = target.TotalProcessorTime; var elapsed = Stopwatch.StartNew();
                Thread.Sleep(3000);
                target.Refresh(); double fraction = (target.TotalProcessorTime - before).TotalMilliseconds
                    / elapsed.Elapsed.TotalMilliseconds / Environment.ProcessorCount;
                Console.WriteLine("CPU_CAP_MEASURE baseline=" + (baseline * 100).ToString("F2")
                    + "% requested=0.01% readback=1/10000 measured=" + (fraction * 100).ToString("F4") + "% window_ms=" + elapsed.ElapsedMilliseconds);
                Eq(true, fraction >= 0 && fraction < 0.005);
                Eq(true, fraction < baseline / 2);
                Process guard = (Process)typeof(BackgroundCpuCap).GetField("guard", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(limiter);
                guard.Kill(); guard.WaitForExit(3000); CpuCapEventuallyOff(job);
                Eq(false, limiter.Sync(wanted, 20));
                Eq(true, limiter.Stop()); CpuCapAssertRate(job, 0);
                Eq("OK", CpuCapLine(target));
                target.StandardInput.WriteLine("PING"); target.StandardInput.Flush(); Eq("OK", CpuCapLine(target));
                var second = new BackgroundCpuCap();
                try { Eq(true, second.Sync(wanted, 20)); Eq(0, second.ActiveCount); CpuCapAssertRate(job, 0); }
                finally { Eq(true, second.Stop()); }
                Console.WriteLine("CPU_CAP_CASES identity, apply/readback, update, eligible-set release, explicit release, child breakaway, real budget, guardian crash, existing-job skip PASS");
            }
            finally { limiter.Stop(); if (job != IntPtr.Zero) Native.CloseHandle(job); CpuCapKill(child); CpuCapKill(target); }

            Process owner = CpuCapHelper("--cpu-cap-test-owner"); target = null; job = IntPtr.Zero;
            try
            {
                string[] ready = CpuCapLine(owner).Split('|'); Eq(2, ready.Length);
                target = Process.GetProcessById(int.Parse(ready[0]));
                job = CpuCapNative.OpenJobObject(6, false, ready[1]); Eq(true, job != IntPtr.Zero);
                CpuCapAssertRate(job, BackgroundCpuCap.AutomaticPercent); owner.Kill(); owner.WaitForExit(3000);
                CpuCapEventuallyOff(job); Eq(false, target.HasExited);
                Eq(true, CpuCapNative.SetRate(job, BackgroundCpuCap.AutomaticPercent));
                Eq(true, Settings.SaveStr(BackgroundCpuCap.ReceiptKey, ready[1]));
                Eq(true, BackgroundCpuCap.Recover()); CpuCapAssertRate(job, 0);
                Eq("", Settings.LoadStr(BackgroundCpuCap.ReceiptKey, ""));
                Console.WriteLine("CPU_CAP_CASES parent crash, durable recovery, target survives PASS");
            }
            finally { if (job != IntPtr.Zero) { CpuCapNative.SetRate(job, 0); Native.CloseHandle(job); } CpuCapKill(owner); CpuCapKill(target); }
            target = CpuCapHelper("--cpu-cap-test-target"); limiter = new BackgroundCpuCap();
            try
            {
                Eq("READY", CpuCapLine(target)); limiter.OwnedTargetInHostJobForTest = target.Id;
                Settings.SuspendWritesForReset();
                Eq(false, limiter.Sync(CpuCapWant(target), BackgroundCpuCap.AutomaticPercent)); Eq(0, limiter.ActiveCount);
                Eq("receipt-write", limiter.Error);
                Console.WriteLine("CPU_CAP_CASES competitive-to-smart, extreme-to-handheld, failed receipt prevents cap PASS");
            }
            finally { Settings.UseTransientStoreForCurrentProcess(); limiter.Stop(); CpuCapKill(target); }
        }
    }
}
#endif
