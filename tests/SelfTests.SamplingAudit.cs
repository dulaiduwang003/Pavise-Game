#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunSamplingLifecycleTests()
        {
            int queries = 0, waits = 0;
            Eq(true, SuppressionCore.ConfirmEcoState(delegate { queries++; return true; }, delegate { waits++; }));
            Eq(1, queries); Eq(0, waits);
            queries = waits = 0;
            Eq(true, SuppressionCore.ConfirmEcoState(delegate { return ++queries == 3; }, delegate { waits++; }));
            Eq(3, queries); Eq(2, waits);
            queries = waits = 0;
            Eq(false, SuppressionCore.ConfirmEcoState(delegate { queries++; return false; }, delegate { waits++; }));
            Eq(4, queries); Eq(3, waits);
            using (Process self = Process.GetCurrentProcess())
            {
                long creation = self.StartTime.ToFileTimeUtc();
                ThreadTimes first = ProcessSnapshotSource.SampleThreads(self.Id, creation, 0);
                long reuses = ProcessSnapshotSource.ThreadSampleReuses;
                var cpuWindow = Stopwatch.StartNew();
                while (cpuWindow.ElapsedMilliseconds < 60) Thread.SpinWait(10000);
                ThreadTimes second = ProcessSnapshotSource.SampleThreads(self.Id, creation, 0);
                if (first == null || second == null || second.Ticks <= first.Ticks)
                    throw new Exception("Thread sampling must return fresh timestamps");
                Eq(reuses, ProcessSnapshotSource.ThreadSampleReuses);
                if (second.ProcessCpu <= first.ProcessCpu) throw new Exception("Fresh sample missed owned CPU progress");
                Eq(null, ProcessSnapshotSource.SampleThreads(self.Id, creation + 1, 0));
                var sample = typeof(RenderLane).GetMethod("SampleThreads", BindingFlags.Static | BindingFlags.NonPublic);
                var data = new Dictionary<int, RenderLane.ThreadSample>();
                object[] call = { self.Id, creation, data, new RenderLane.Candidate() };
                Eq(true, (bool)sample.Invoke(null, call));
                if (data.Count == 0 || data.Count > 512) throw new Exception("Invalid owned thread sample size");
                data.Clear(); call[1] = creation + 1;
                Eq(false, (bool)sample.Invoke(null, call)); Eq(0, data.Count);
                Eq(RenderLane.SampleFailure.IdentityChanged, ((RenderLane.Candidate)call[3]).Failure);
            }
            Console.WriteLine("PASS necessary control samples: owned thread freshness, identity changes, bounded EcoQoS readback");
        }

        internal static int RunSamplingAudit(string[] args)
        {
            Settings.UseTransientStoreForCurrentProcess();
            Logger.ResetWriteBarrierForTest();
            Logger.LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PaviseSamplingAudit-" + Guid.NewGuid().ToString("N") + ".log");
            Lang.Init();
            if (args[0] == "--sampling-test")
            {
                for (int i = 1; i <= 5; i++) { RunSamplingLifecycleTests(); Console.WriteLine("PASS round " + i); }
                return 0;
            }
            Console.WriteLine("READ ONLY microbench; no tuning writes; wall time includes waits; CPU is calling thread only; not an FPS benchmark");
            using (Process self = Process.GetCurrentProcess())
            {
                int session = self.SessionId;
                Console.WriteLine("logicalProcessors=" + Environment.ProcessorCount + " processes=" + ProcessSnapshotSource.Capture(session, 0).Count);
                AuditMeasure("process snapshot fresh", 100, delegate { ProcessSnapshotSource.Capture(session, 0); });
                AuditMeasure("process snapshot reused", 1000, delegate { ProcessSnapshotSource.Capture(session, 5000); });
                AuditMeasure("LoL/WeGame names-only discovery", 10, delegate { LolRuntimeProcesses.Scan(null, null, true); });
                AuditMeasure("LoL/WeGame full discovery without roots", 10, delegate { LolRuntimeProcesses.Scan(null, null, false); });
                var saturation = new CpuSaturation();
                AuditMeasure("GetSystemTimes saturation", 1000, delegate { saturation.Sample(); });
                var sample = typeof(RenderLane).GetMethod("SampleThreads", BindingFlags.Static | BindingFlags.NonPublic);
                Action sampleSelf = delegate
                {
                    var data = new Dictionary<int, RenderLane.ThreadSample>();
                    object[] call = { self.Id, self.StartTime.ToFileTimeUtc(), data, new RenderLane.Candidate() };
                    if (!(bool)sample.Invoke(null, call)) throw new Exception("Owned thread sampling failed");
                };
                sampleSelf();
                AuditMeasure("RenderLane one snapshot owned process", 100, sampleSelf);
                using (var stop = new ManualResetEvent(false))
                {
                    var sleepers = new List<Thread>();
                    try
                    {
                        for (int i = 0; i < 128; i++)
                        { var t = new Thread(delegate() { stop.WaitOne(); }); t.IsBackground = true; t.Start(); sleepers.Add(t); }
                        Thread.Sleep(100);
                        AuditMeasure("RenderLane one snapshot +128 owned sleeping threads", 100, sampleSelf);
                    }
                    finally { stop.Set(); foreach (Thread t in sleepers) t.Join(3000); }
                }
            }
            AuditMeasure("GPU election 2 x 700ms window", 3, delegate
            {
                GpuSampleDiagnostics diagnostics;
                GpuEvidence.Sample3D(2, 700, null, 0, out diagnostics);
                Console.WriteLine("GPU " + diagnostics);
            });
            return 0;
        }

        private static void AuditMeasure(string name, int iterations, Action action)
        {
            long born, exited, kernel, user;
            Native.GetThreadTimes(AuditCurrentThread(), out born, out exited, out kernel, out user);
            long before = kernel + user;
            var durations = new List<double>();
            for (int i = 0; i < iterations; i++)
            { var clock = Stopwatch.StartNew(); action(); durations.Add(clock.Elapsed.TotalMilliseconds); }
            Native.GetThreadTimes(AuditCurrentThread(), out born, out exited, out kernel, out user);
            double total = 0;
            foreach (double value in durations) total += value;
            durations.Sort();
            Console.WriteLine("MEASURE {0}: n={1} wallMeanMs={2:F3} wallP95Ms={3:F3} wallMaxMs={4:F3} threadCpuMeanMs={5:F3}",
                name, iterations, total / iterations, durations[(int)Math.Ceiling(iterations * .95) - 1], durations[iterations - 1],
                (kernel + user - before) / 10000.0 / iterations);
        }

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThread")] private static extern IntPtr AuditCurrentThread();
    }
}
#endif
