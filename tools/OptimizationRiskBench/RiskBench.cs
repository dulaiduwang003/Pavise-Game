// Bounded counterexamples, not a game benchmark. Only this process is modified.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class OptimizationRiskBench
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCounters
        {
            public uint Size, Faults;
            public UIntPtr PeakWorkingSet, WorkingSet, PeakPaged, Paged, PeakNonPaged,
                NonPaged, Pagefile, PeakPagefile;
        }
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters value, uint size);
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
        [DllImport("kernel32.dll")]
        private static extern int GetThreadPriority(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);
        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        private static long sink;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message + "; Win32=" + Marshal.GetLastWin32Error());
        }

        private static int Main(string[] args)
        {
            // No Program.Main, GameMode constructor/runtime, production logger or real settings.
            Settings.UseTransientStoreForCurrentProcess();
            Logger.WritesEnabled = false;
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 45000, Timeout.Infinite))
            {
                try
                {
                    if (args.Length == 1 && args[0] == "policy") Policy();
                    else if (args.Length == 1 && args[0] == "baseline") Baseline();
                    else if (args.Length == 1 && args[0] == "trim") Trim();
                    else if (args.Length == 2 && args[0] == "audio") AudioProbe(args[1] == "active");
                    else if (args.Length == 1 && args[0] == "store-lock") StoreLockProbe();
                    else if ((args.Length == 2 || args.Length == 3) && args[0] == "lane"
                        && (args[1] == "baseline" || args[1] == "boost"))
                        Lane(args[1] == "boost", args.Length == 3 && args[2] == "spread");
                    else throw new ArgumentException("policy | trim | lane baseline/boost");
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            }
        }

        private static void Baseline()
        {
            Process[] matches = Process.GetProcessesByName("Pavise.dev");
            Process observed = matches.Length == 1 ? matches[0] : null;
            try
            {
                Console.WriteLine("sample,seconds,system_cpu_percent,pavise_cpu_percent_of_machine,pavise_ws_MiB");
                for (int i = 0; i < 3; i++)
                {
                    long idle0, kernel0, user0, idle1, kernel1, user1;
                    Require(GetSystemTimes(out idle0, out kernel0, out user0), "system times before");
                    double cpu0 = observed == null ? double.NaN : observed.TotalProcessorTime.TotalMilliseconds;
                    long start = Stopwatch.GetTimestamp();
                    Thread.Sleep(5000);
                    double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    Require(GetSystemTimes(out idle1, out kernel1, out user1), "system times after");
                    if (observed != null) observed.Refresh();
                    double cpu1 = observed == null ? double.NaN : observed.TotalProcessorTime.TotalMilliseconds;
                    long total = kernel1 - kernel0 + user1 - user0;
                    Console.WriteLine(string.Join(",", new[] { i.ToString(), (elapsed / 1000).ToString("F4", Inv),
                        (100.0 * (total - (idle1 - idle0)) / total).ToString("F4", Inv),
                        (100 * (cpu1 - cpu0) / elapsed / Environment.ProcessorCount).ToString("F4", Inv),
                        (observed == null ? double.NaN : observed.WorkingSet64 / 1048576.0).ToString("F2", Inv) }));
                }
            }
            finally { foreach (Process process in matches) process.Dispose(); }
        }

        private static void Policy()
        {
            Console.WriteLine("check,result,interpretation");
            Console.WriteLine("power_yield_45_to_41W_gpu98_to96," + PowerBudgetYield.VerifyHold(45, 41, 98, 96)
                + ",synthetic values; no FPS input exists");
            Console.WriteLine("saturated_no_lane," + GameMode.BoostPriorityTarget(true, false) + ",32=Normal");
            Console.WriteLine("saturated_with_lane," + GameMode.BoostPriorityTarget(true, true) + ",32=Normal");
            Console.WriteLine("trim_64GiB_12GiB_available," + WsTrim.ShouldTrim(64UL << 30, 12UL << 30)
                + ",synthetic pressure boundary");
            Console.WriteLine("cache_warm_retired," + (PolicyCatalog.ItemOf("GmCacheWarm") == null) + ",no runtime feature");
        }

        private static MemoryCounters Counters(Process process)
        {
            MemoryCounters result;
            Require(GetProcessMemoryInfo(process.Handle, out result, (uint)Marshal.SizeOf(typeof(MemoryCounters))), "memory counters");
            return result;
        }

        private static long Touch(IntPtr memory, int bytes)
        {
            long sum = 0;
            for (int offset = 0; offset < bytes; offset += 4096)
            {
                byte value = Marshal.ReadByte(memory, offset);
                Marshal.WriteByte(memory, offset, (byte)(value + 1));
                sum += value;
            }
            Interlocked.Exchange(ref sink, sum);
            return sum;
        }

        private static void Trim()
        {
            const int bytes = 256 * 1024 * 1024;
            IntPtr memory = VirtualAlloc(IntPtr.Zero, new UIntPtr(bytes), 0x3000, 4);
            Require(memory != IntPtr.Zero, "allocate 256 MiB");
            try
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    Touch(memory, bytes); Touch(memory, bytes);
                    Counters(self); Stopwatch.GetTimestamp();
                    Console.WriteLine("round,trim,retouch_ms,new_page_faults,ws_before_MiB,ws_after_action_MiB,ws_after_retouch_MiB");
                    for (int i = 0; i < 12; i++)
                    {
                        bool trim = i % 4 == 1 || i % 4 == 2;
                        Touch(memory, bytes); Touch(memory, bytes);
                        Thread.Sleep(60);
                        MemoryCounters before = Counters(self);
                        if (trim) Require(EmptyWorkingSet(self.Handle), "trim owned process only");
                        MemoryCounters afterAction = Counters(self);
                        long start = Stopwatch.GetTimestamp();
                        Touch(memory, bytes);
                        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                        MemoryCounters after = Counters(self);
                        Console.WriteLine(string.Join(",", new[] { i.ToString(), trim ? "1" : "0", ms.ToString("F4", Inv),
                            unchecked(after.Faults - afterAction.Faults).ToString(),
                            (before.WorkingSet.ToUInt64() / 1048576.0).ToString("F2", Inv),
                            (afterAction.WorkingSet.ToUInt64() / 1048576.0).ToString("F2", Inv),
                            (after.WorkingSet.ToUInt64() / 1048576.0).ToString("F2", Inv) }));
                    }
                }
            }
            finally { Require(VirtualFree(memory, UIntPtr.Zero, 0x8000), "release owned memory"); }
        }

        private sealed class LaneLoad
        {
            public volatile bool Stop;
            public int BurnerTid, FrameTid;
            public Exception Error;
            public readonly AutoResetEvent FrameReady = new AutoResetEvent(false);
            public readonly AutoResetEvent BurnerReady = new AutoResetEvent(false);
            public readonly AutoResetEvent PriorityRequest = new AutoResetEvent(false);
            public readonly AutoResetEvent PriorityAck = new AutoResetEvent(false);
            public readonly List<double> Frames = new List<double>(50000);
            public long MeasurementStart, MeasurementEnd;
            public int CompletedInsideWindow;
            public ulong Mask, BurnerMask;
            public int DesiredPriority, ActualPriority;
            public bool FrameAffinityRestored, BurnerAffinityRestored, PriorityRestored;
        }

        // Fixed work, not a deadline spin: preemption must not count as completed work.
        private static void Work(int iterations)
        {
            ulong value = 0x9e3779b97f4a7c15UL;
            for (int i = 0; i < iterations; i++) value = (value ^ (value >> 11)) * 6364136223846793005UL + 1;
            Interlocked.Exchange(ref sink, unchecked((long)value));
        }

        private static void FrameThread(LaneLoad load)
        {
            UIntPtr oldMask = UIntPtr.Zero;
            try
            {
                oldMask = SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(load.Mask));
                Require(oldMask != UIntPtr.Zero, "frame affinity");
                load.FrameTid = (int)GetCurrentThreadId(); load.FrameReady.Set();
                long previous = Stopwatch.GetTimestamp();
                while (!load.Stop)
                {
                    Work(60000);
                    Thread.Sleep(1);
                    long now = Stopwatch.GetTimestamp();
                    long start = Interlocked.Read(ref load.MeasurementStart);
                    long finish = Interlocked.Read(ref load.MeasurementEnd);
                    if (start != 0 && now >= start && (finish == 0 || now <= finish))
                    {
                        Interlocked.Increment(ref load.CompletedInsideWindow);
                        if (previous >= start)
                            load.Frames.Add((now - previous) * 1000.0 / Stopwatch.Frequency);
                    }
                    previous = now;
                }
            }
            catch (Exception ex) { load.Error = ex; load.Stop = true; load.FrameReady.Set(); }
            finally
            {
                if (oldMask != UIntPtr.Zero)
                    load.FrameAffinityRestored = SetThreadAffinityMask(GetCurrentThread(), oldMask) != UIntPtr.Zero;
            }
        }

        private static void BurnerThread(LaneLoad load)
        {
            UIntPtr oldMask = UIntPtr.Zero;
            int originalPriority = GetThreadPriority(GetCurrentThread());
            try
            {
                oldMask = SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(load.BurnerMask));
                Require(oldMask != UIntPtr.Zero, "burner affinity");
                load.BurnerTid = (int)GetCurrentThreadId(); load.BurnerReady.Set();
                while (!load.Stop)
                {
                    Work(60000);
                    if (load.PriorityRequest.WaitOne(0))
                    {
                        Require(SetThreadPriority(GetCurrentThread(), load.DesiredPriority), "owned thread priority");
                        load.ActualPriority = GetThreadPriority(GetCurrentThread());
                        load.PriorityAck.Set();
                    }
                }
            }
            catch (Exception ex) { load.Error = ex; load.Stop = true; load.BurnerReady.Set(); load.PriorityAck.Set(); }
            finally
            {
                load.PriorityRestored = SetThreadPriority(GetCurrentThread(), originalPriority)
                    && GetThreadPriority(GetCurrentThread()) == originalPriority;
                if (oldMask != UIntPtr.Zero)
                    load.BurnerAffinityRestored = SetThreadAffinityMask(GetCurrentThread(), oldMask) != UIntPtr.Zero;
            }
        }

        private static double Percentile(List<double> sorted, double fraction)
        {
            return sorted.Count == 0 ? double.NaN : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * fraction) - 1)];
        }

        private static void Lane(bool boost, bool spread)
        {
            var load = new LaneLoad();
            using (Process self = Process.GetCurrentProcess())
            {
                ulong allowed = unchecked((ulong)self.ProcessorAffinity.ToInt64());
                int seen = 0;
                for (int bit = 0; bit < 63; bit++)
                    if ((allowed & (1UL << bit)) != 0) { load.Mask = 1UL << bit; if (++seen == 3) break; }
                Require(load.Mask != 0, "available logical CPU");
                load.BurnerMask = load.Mask;
                if (spread)
                {
                    seen = 0;
                    for (int bit = 0; bit < 63; bit++)
                        if ((allowed & (1UL << bit)) != 0)
                        { load.BurnerMask = 1UL << bit; if (++seen == 5) break; }
                    Require(load.BurnerMask != load.Mask, "two different allowed logical CPUs");
                }
                var frame = new Thread(delegate() { FrameThread(load); }) { IsBackground = true };
                var burner = new Thread(delegate() { BurnerThread(load); }) { IsBackground = true };
                frame.Start(); burner.Start();
                LaneDecision decision = LaneDecision.None;
                var judge = new LaneJudge();
                double smallestWinnerShare = 1;
                int picked = 0;
                long end = 0;
                try
                {
                    Require(load.FrameReady.WaitOne(2000) && load.BurnerReady.WaitOne(2000), "worker ready");
                    if (load.Error != null) throw load.Error;
                    // This is the production CPU-time sampler and production confirmation judge.
                    for (int i = 0; i < 3; i++)
                    {
                        RenderLane.Candidate candidate;
                        Require(RenderLane.TryIdentify(self.Id, out candidate), "production thread sampler");
                        picked = candidate.Tid;
                        smallestWinnerShare = Math.Min(smallestWinnerShare, candidate.Share);
                        decision = judge.Observe(candidate.Tid, candidate.Share, 0);
                    }
                    Require(decision == LaneDecision.Pin && picked == load.BurnerTid,
                        "counterexample inconclusive: noncritical burner was not selected");
                    load.DesiredPriority = boost ? 2 : 0;
                    load.PriorityRequest.Set();
                    Require(load.PriorityAck.WaitOne(2000), "priority acknowledgement");
                    if (load.Error != null) throw load.Error;
                    Require(load.ActualPriority == load.DesiredPriority, "priority readback");
                    Interlocked.Exchange(ref load.MeasurementStart, Stopwatch.GetTimestamp());
                    Thread.Sleep(4000);
                    end = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref load.MeasurementEnd, end);
                }
                finally
                {
                    load.Stop = true;
                    Require(frame.Join(4000) && burner.Join(4000), "owned workers stopped");
                }
                if (load.Error != null) throw load.Error;
                Require(load.PriorityRestored && load.FrameAffinityRestored && load.BurnerAffinityRestored,
                    "owned priority/affinity restored");
                load.Frames.Sort();
                double elapsed = (end - load.MeasurementStart) / (double)Stopwatch.Frequency;
                Console.WriteLine("arm,pid,cpu_mask,frame_tid,burner_tid,picked_tid,min_winner_share,thread_priority,samples,window_seconds,p50_ms,p95_ms,p99_ms,max_ms,restored,layout,burner_mask,completed_inside_window");
                Console.WriteLine(string.Join(",", new[] { boost ? "boost" : "baseline", self.Id.ToString(),
                    load.Mask.ToString("X"), load.FrameTid.ToString(), load.BurnerTid.ToString(), picked.ToString(),
                    smallestWinnerShare.ToString("F4", Inv), load.ActualPriority.ToString(), load.Frames.Count.ToString(),
                    elapsed.ToString("F4", Inv), Percentile(load.Frames, 0.50).ToString("F4", Inv),
                    Percentile(load.Frames, 0.95).ToString("F4", Inv), Percentile(load.Frames, 0.99).ToString("F4", Inv),
                    Percentile(load.Frames, 1).ToString("F4", Inv), "true", spread ? "spread" : "shared",
                    load.BurnerMask.ToString("X"), load.CompletedInsideWindow.ToString() }));
            }
        }
    }
}
