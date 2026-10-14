#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunRenderLaneEligibilityTests()
        {
            LaneNeverRunsOnHandheld();
            LaneNeedsEnoughPhysicalCores();
            LaneThresholdMatchesDocumentedValue();
            LaneSamplingDistinguishesIdleAndReusedThreads();
            LaneStopsAtOldLimit();
            LaneFailuresBreakConfirmation();
            LaneRetryGapBreaksConfirmation();
            LaneReusedThreadBreaksConfirmation();
            LaneSessionEndCancelsPin();
            LaneTerminalFailuresStop();
            LaneNativeSamplingAndRestore();
        }

        private static RenderLane.SampleResult LaneSample(bool success, int tid, RenderLane.SampleFailure failure)
        {
            return new RenderLane.SampleResult { Success = success, WatchShare = 0,
                Best = new RenderLane.Candidate { Tid = tid, Share = 0.6, ThreadCount = 4, Failure = failure } };
        }

        private static void LaneSamplingDistinguishesIdleAndReusedThreads()
        {
            var first = new Dictionary<int, RenderLane.ThreadSample>();
            var second = new Dictionary<int, RenderLane.ThreadSample>();
            first[11] = new RenderLane.ThreadSample { Creation = 100, CpuTicks = 1000 };
            second[11] = first[11];
            RenderLane.Candidate best; double share;
            Eq(false, RenderLane.SelectCandidate(first, second, 11, out best, out share));
            Eq(RenderLane.SampleFailure.NoCpuProgress, best.Failure);
            second[11] = new RenderLane.ThreadSample { Creation = 200, CpuTicks = 99999 };
            Eq(false, RenderLane.SelectCandidate(first, second, 11, out best, out share));
            first[22] = new RenderLane.ThreadSample { Creation = 300, CpuTicks = 500 };
            second[22] = new RenderLane.ThreadSample { Creation = 300, CpuTicks = 600 };
            Eq(true, RenderLane.SelectCandidate(first, second, 22, out best, out share));
            Eq(22, best.Tid); Eq(300L, best.ThreadCreation); Eq(1.0, best.Share); Eq(1.0, share);
            var judge = new LaneJudge();
            for (int i = 0; i < 20; i++) Eq(LaneDecision.None, judge.Observe(22, 0.34, 0));
            Eq(LaneDecision.None, judge.Observe(22, 0.6, 0));
            Eq(LaneDecision.None, judge.Observe(22, 0.6, 0));
            Eq(LaneDecision.Pin, judge.Observe(22, 0.6, 0));
            Console.WriteLine("PASS LaneSamplingDistinguishesIdleAndReusedThreads");
        }

        private static void LaneRetryScope(Action action)
        {
            Settings.UseTransientStoreForCurrentProcess(); RenderLane.ResetShutdownForTest();
            try { action(); }
            finally { RenderLane.Release(); RenderLane.ResetShutdownForTest(); }
        }

        private static void LaneStopsAtOldLimit()
        {
            foreach (RenderLane.SampleFailure reason in new[] { RenderLane.SampleFailure.NoReadableThreads,
                RenderLane.SampleFailure.NoCpuProgress, RenderLane.SampleFailure.ProcessUnavailable })
            LaneRetryScope(delegate
            {
                int samples = 0, pins = 0, waits = 0;
                RenderLane.SampleForTest = delegate { samples++; return LaneSample(false, 0, reason); };
                RenderLane.PinCandidateForTest = delegate { pins++; return RenderLane.PinOutcome.Fatal; };
                RenderLane.WaitForTest = delegate(int ms, int gen)
                { Eq(60000, ms); if (++waits > 4) throw new Exception("Retry exceeded old limit"); return true; };
                RenderLane.RunWorkerForTest(42, 100);
                Eq(reason == RenderLane.SampleFailure.NoCpuProgress ? 30 : 5, samples);
                Eq(4, waits); Eq(0, pins); Eq(false, RenderLane.HasResidue());
                Eq(LaneState.Unavailable, RenderLane.StateFor(42, 100));
                RenderLane.EnsureForGame(42, 100, "fixture");
                Eq(LaneState.Unavailable, RenderLane.StateFor(42, 100));
            });
            LaneRetryScope(delegate
            {
                int samples = 0, priority = 0, pins = 0;
                RenderLane.SampleForTest = delegate { samples++; return LaneSample(true, 44, RenderLane.SampleFailure.None); };
                RenderLane.RestoreThreadForTest = delegate(int p, long c, int t, int original) { priority = original; return true; };
                RenderLane.PinCandidateForTest = delegate(int p, long c, RenderLane.Candidate b, int gen)
                {
                    pins++;
                    return RenderLane.TryPinForTest(p, c, b.Tid, gen, delegate { return priority; },
                        delegate(int v) { priority = v; return true; });
                };
                RenderLane.WaitForTest = delegate(int ms, int gen)
                { Eq(30000, ms); Eq(Native.THREAD_PRIORITY_HIGHEST, priority); Eq(true, RenderLane.Release()); return false; };
                RenderLane.RunWorkerForTest(42, 100);
                Eq(3, samples); Eq(1, pins); Eq(0, priority); Eq(false, RenderLane.HasResidue());
            });
            Console.WriteLine("PASS LaneStopsAtOldLimit and successful boost retains old verification cadence");
        }

        private static void LaneFailuresBreakConfirmation()
        {
            LaneRetryScope(delegate
            {
                int calls = 0, pins = 0;
                RenderLane.SampleForTest = delegate
                {
                    calls++;
                    return LaneSample(calls % 3 != 0, 44, calls % 3 == 0 ? RenderLane.SampleFailure.NoCpuProgress : RenderLane.SampleFailure.None);
                };
                RenderLane.PinCandidateForTest = delegate { pins++; return RenderLane.PinOutcome.Fatal; };
                RenderLane.WaitForTest = delegate { RenderLane.Release(); return false; };
                RenderLane.RunWorkerForTest(42, 100);
                Eq(6, calls); Eq(0, pins);
            });
            Console.WriteLine("PASS LaneFailuresBreakConfirmation");
        }

        private static void LaneRetryGapBreaksConfirmation()
        {
            LaneRetryScope(delegate
            {
                int calls = 0, pinAt = 0, waits = 0;
                RenderLane.SampleForTest = delegate
                {
                    calls++;
                    return LaneSample(calls > 4, 44, calls <= 4 ? RenderLane.SampleFailure.NoCpuProgress : RenderLane.SampleFailure.None);
                };
                RenderLane.PinCandidateForTest = delegate { pinAt = calls; return RenderLane.PinOutcome.Fatal; };
                RenderLane.WaitForTest = delegate { if (++waits > 1) throw new Exception("Unexpected retry"); return true; };
                RenderLane.RunWorkerForTest(42, 100);
                Eq(9, pinAt); Eq(1, waits); Eq(LaneState.Unavailable, RenderLane.StateFor(42, 100));
            });
            Console.WriteLine("PASS LaneRetryGapBreaksConfirmation");
        }

        private static void LaneSessionEndCancelsPin()
        {
            LaneRetryScope(delegate
            {
                int calls = 0, pins = 0;
                RenderLane.SampleForTest = delegate
                {
                    if (++calls == 3) RenderLane.Release();
                    return LaneSample(true, 44, RenderLane.SampleFailure.None);
                };
                RenderLane.PinCandidateForTest = delegate { pins++; return RenderLane.PinOutcome.Fatal; };
                RenderLane.RunWorkerForTest(42, 100);
                Eq(3, calls); Eq(0, pins); Eq(false, RenderLane.HasResidue());
            });
            Console.WriteLine("PASS LaneSessionEndCancelsPin");
        }

        private static void LaneReusedThreadBreaksConfirmation()
        {
            LaneRetryScope(delegate
            {
                int calls = 0, pinAt = 0;
                RenderLane.SampleForTest = delegate
                {
                    calls++;
                    var sample = LaneSample(true, 44, RenderLane.SampleFailure.None);
                    sample.Best.ThreadCreation = calls <= 2 ? 100 : 200;
                    return sample;
                };
                RenderLane.PinCandidateForTest = delegate { pinAt = calls; return RenderLane.PinOutcome.Fatal; };
                RenderLane.RunWorkerForTest(42, 100);
                Eq(5, pinAt);
            });
            Console.WriteLine("PASS LaneReusedThreadBreaksConfirmation");
        }

        private static void LaneTerminalFailuresStop()
        {
            foreach (RenderLane.SampleFailure reason in new[] { RenderLane.SampleFailure.ProcessGone, RenderLane.SampleFailure.IdentityChanged })
                LaneRetryScope(delegate
                {
                    int calls = 0;
                    RenderLane.SampleForTest = delegate { calls++; return LaneSample(false, 0, reason); };
                    RenderLane.WaitForTest = delegate { throw new Exception("Dead process must not keep retrying"); };
                    RenderLane.RunWorkerForTest(42, 100);
                    Eq(1, calls); Eq(LaneState.Idle, RenderLane.StateFor(42, 100));
                });
            Console.WriteLine("PASS LaneTerminalFailuresStop");
        }

        private static void LaneNativeSamplingAndRestore()
        {
            LaneRetryScope(delegate
            {
                int tid = 0;
                using (var ready = new ManualResetEvent(false))
                using (var stop = new ManualResetEvent(false))
                using (Process self = Process.GetCurrentProcess())
                {
                    var worker = new Thread(delegate() { tid = Native.GetCurrentThreadId(); ready.Set(); stop.WaitOne(); });
                    worker.IsBackground = true; worker.Start();
                    IntPtr handle = IntPtr.Zero;
                    try
                    {
                        Eq(true, ready.WaitOne(3000));
                        long creation = self.StartTime.ToFileTimeUtc();
                        handle = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, tid);
                        Eq(true, handle != IntPtr.Zero);
                        long born, exited, kernel, user;
                        Eq(true, Native.GetThreadTimes(handle, out born, out exited, out kernel, out user));
                        int original = Native.GetThreadPriority(handle);
                        var candidate = new RenderLane.Candidate { Tid = tid, ThreadCreation = born + 1, Share = 0.6 };
                        int gen = RenderLane.ShutdownGenerationForTest;
                        Eq(RenderLane.PinOutcome.Retryable, RenderLane.TryPinNativeForTest(self.Id, creation, candidate, gen));
                        Eq(original, Native.GetThreadPriority(handle)); Eq(false, RenderLane.HasResidue());
                        candidate.ThreadCreation = born;
                        Eq(RenderLane.PinOutcome.Pinned, RenderLane.TryPinNativeForTest(self.Id, creation, candidate, gen));
                        Eq(Native.THREAD_PRIORITY_HIGHEST, Native.GetThreadPriority(handle));
                        Eq(true, RenderLane.Release()); Eq(original, Native.GetThreadPriority(handle));
                        Eq(false, RenderLane.HasResidue());
                        candidate.Tid = int.MaxValue;
                        Eq(RenderLane.PinOutcome.Retryable, RenderLane.TryPinNativeForTest(self.Id, creation, candidate, RenderLane.ShutdownGenerationForTest));
                        RenderLane.Candidate sampled; double share;
                        Eq(false, RenderLane.TryIdentifyProcess(self.Id, creation + 1, 0, out sampled, out share));
                        Eq(RenderLane.SampleFailure.IdentityChanged, sampled.Failure);
                        RenderLane.TryIdentifyProcess(self.Id, creation, 0, out sampled, out share);
                        Eq(true, sampled.Failure == RenderLane.SampleFailure.None || sampled.Failure == RenderLane.SampleFailure.NoCpuProgress);
                        Eq(true, sampled.ThreadCount > 0);
                    }
                    finally
                    {
                        RenderLane.Release();
                        if (handle != IntPtr.Zero) Native.CloseHandle(handle);
                        stop.Set(); Eq(true, worker.Join(3000));
                    }
                }
            });
            Console.WriteLine("PASS LaneNativeSamplingAndRestore");
        }

        private static void LaneNeverRunsOnHandheld()
        {
            Eq(false, GameMode.LaneSupported(PerformancePreset.Handheld));
        }

        private static void LaneNeedsEnoughPhysicalCores()
        {
            int cores;
            try { cores = CpuTopology.PhysicalCoreCount; }
            catch { return; }

            bool expected = cores >= GameMode.LaneMinPhysicalCores;
            Eq(expected, GameMode.LaneSupported(PerformancePreset.Standard));
            Eq(expected, GameMode.LaneSupported(PerformancePreset.Competitive));
            Eq(expected, GameMode.LaneSupported(PerformancePreset.Standard));

        }

        private static void LaneThresholdMatchesDocumentedValue()
        {
            Eq(6, GameMode.LaneMinPhysicalCores);
            string zh = Lang.T("gm.lane.unsupported");
            if (string.IsNullOrEmpty(zh) || zh == "gm.lane.unsupported")
                throw new InvalidOperationException("gm.lane.unsupported 文案缺失");
            if (zh.IndexOf("6", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("不可用文案没有写出门槛核数");
        }
    }
}
#endif
