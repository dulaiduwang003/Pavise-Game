#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunRenderLaneEligibilityTests()
        {
            LaneRestoredAndSceneContracts();
            LaneSamplingDistinguishesIdleAndReusedThreads();
            LaneStopsAtOldLimit();
            LaneFailuresBreakConfirmation();
            LaneRetryGapBreaksConfirmation();
            LaneReusedThreadBreaksConfirmation();
            LaneSessionEndCancelsPin();
            LaneLateFailureCannotPoisonNextSession();
            LaneTerminalFailuresStop();
            LaneMockReceiptAndRestore();
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

        private static void LaneLateFailureCannotPoisonNextSession()
        {
            LaneRetryScope(delegate
            {
                RenderLane.SampleForTest = delegate { return LaneSample(true, 44, RenderLane.SampleFailure.None); };
                RenderLane.PinCandidateForTest = delegate { RenderLane.Release(); return RenderLane.PinOutcome.Fatal; };
                RenderLane.RunWorkerForTest(42, 100);
                Eq(LaneState.Idle, RenderLane.StateFor(42, 100)); Eq(false, RenderLane.HasResidue());
            });
            Console.WriteLine("PASS late candidate failure cannot mark a released session unavailable");
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

        private static void LaneMockReceiptAndRestore()
        {
            LaneRetryScope(delegate
            {
                int priority = -1, writes = 0, restores = 0;
                RenderLane.RestoreThreadForTest = delegate(int pid, long birth, int tid, int original) {
                    Eq(42, pid); Eq(100L, birth); Eq(44, tid); Eq(-1, original);
                    restores++; priority = original; return true;
                };
                int gen = RenderLane.ShutdownGenerationForTest;
                Eq(RenderLane.PinOutcome.Pinned, RenderLane.TryPinForTest(42, 100, 44, gen,
                    delegate { return priority; }, delegate(int value) { writes++; priority = value; return true; }));
                Eq(1, writes); Eq(Native.THREAD_PRIORITY_HIGHEST, priority); Eq(true, RenderLane.HasResidue());
                Eq(true, RenderLane.Release()); Eq(-1, priority); Eq(1, restores); Eq(false, RenderLane.HasResidue());
                Eq(RenderLane.PinOutcome.Canceled, RenderLane.TryPinForTest(42, 100, 44, gen,
                    delegate { return priority; }, delegate(int value) { writes++; priority = value; return true; }));
                Eq(1, writes); Eq(-1, priority); Eq(false, RenderLane.HasResidue());
            });
            Console.WriteLine("PASS LaneMockReceiptAndRestore: legacy receipt rollback and stale generation, thread writes mocked");
        }

        private static void LaneRestoredAndSceneContracts()
        {
            var oldRequested = AutomaticPolicy.RequestedForTest;
            var oldEffective = AutomaticPolicy.EffectiveForTest;
            var oldApproved = AutomaticPolicy.ApprovedForTest;
            var oldSupported = GameMode.LaneSupportedForTest;
            bool requested = false;
            AutomaticPolicy.RequestedForTest = delegate { return requested; };
            AutomaticPolicy.EffectiveForTest = delegate { return true; };
            AutomaticPolicy.ApprovedForTest = delegate { return "1"; };
            GameMode.LaneSupportedForTest = delegate { return true; };
            try
            {
                foreach (PerformancePreset scene in new[] { PerformancePreset.Standard, PerformancePreset.Competitive })
                {
                    Eq(true, PresetValue.SupportsCoreScheduling(scene));
                    foreach (bool automatic in new[] { false, true })
                    LaneRetryScope(delegate {
                        requested = automatic;
                        Settings.SaveStr(PolicyCatalog.KeyPreset, ((int)scene).ToString());
                        Settings.Save(PolicyCatalog.KeyRenderLane, true);
                        Settings.Save("GpuPrefStageOn", false);
                        int samples = 0, pins = 0;
                        using (var pinned = new System.Threading.ManualResetEventSlim())
                        {
                        RenderLane.SampleForTest = delegate { System.Threading.Interlocked.Increment(ref samples); return LaneSample(true, 44, RenderLane.SampleFailure.None); };
                        RenderLane.PinCandidateForTest = delegate { System.Threading.Interlocked.Increment(ref pins); pinned.Set(); return RenderLane.PinOutcome.Fatal; };
                        Eq(!automatic, PolicyResolver.Global().RenderLane);
                        Settings.Save(PolicyCatalog.KeyFrameCoordination, true);
                        Eq(false, PolicyResolver.Global().FrameCoordinationOn);
                        var mode = new GameMode(Path.Combine(Path.GetTempPath(), "PaviseLaneRestored-" + Guid.NewGuid().ToString("N")), new SuppressionCore());
                        mode.InitializeAutomation();
                        var passType = typeof(GameMode).GetNestedType("BoostPass", BindingFlags.NonPublic);
                        object pass = Activator.CreateInstance(passType, true);
                        passType.GetField("LaneAllowed").SetValue(pass, true);
                        passType.GetField("PriorityTarget").SetValue(pass, Native.HIGH_PRIORITY_CLASS);
                        passType.GetField("LaneRestorationReady").SetValue(pass, false);
                        typeof(GameMode).GetMethod("EngageLaneAndReport", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode,
                            new object[] { IntPtr.Zero, 42, 100L, pass, false, false, false, false, "", false });
                        Eq(0, samples); Eq(0, pins); Eq(LaneState.Idle, RenderLane.StateFor(42, 100));
                        passType.GetField("LaneRestorationReady").SetValue(pass, true);
                        typeof(GameMode).GetMethod("EngageLaneAndReport", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode,
                            new object[] { IntPtr.Zero, 42, 100L, pass, false, false, false, false, "", false });
                        if (!automatic) Eq(true, pinned.Wait(5000));
                        Eq(true, RenderLane.Release());
                        Eq(automatic ? 0 : 3, samples); Eq(automatic ? 0 : 1, pins);
                        Eq(false, RenderLane.HasResidue());
                        }
                    });
                }
                Eq(false, PresetValue.SupportsCoreScheduling(PerformancePreset.Handheld));
                Eq(false, PresetValue.SupportsCoreScheduling(PerformancePreset.Extreme));
                Eq(true, PresetValue.SupportsCoreScheduling(PresetValue.From(4)));
                Eq(true, PresetValue.SupportsCoreScheduling(PresetValue.From(6)));
            }
            finally
            {
                AutomaticPolicy.RequestedForTest = oldRequested;
                AutomaticPolicy.EffectiveForTest = oldEffective;
                AutomaticPolicy.ApprovedForTest = oldApproved;
                GameMode.LaneSupportedForTest = oldSupported;
                Settings.UseTransientStoreForCurrentProcess();
            }
            Console.WriteLine("PASS restored 2223 candidate reaches boost entry in Smart/Esports; restoration debt blocks admission, frame coordination excluded, sampling and writes mocked");
        }
    }
}
#endif
