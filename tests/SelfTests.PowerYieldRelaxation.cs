#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunPowerYieldRelaxationRegressionTests()
        {
            Action<bool>[] tests = {
                PowerYieldWaitsForLateRenderer, PowerYieldDiscardsStaleObservation,
                PowerYieldToleratesShortGaps, PowerYieldStillRestoresWithoutEvidence,
                PowerYieldStillReleasesForLoadShift, PowerYieldStillRejectsGpuHarm,
                PowerYieldInterruptedVerificationDoesNotFuse, PowerYieldVerificationGapBoundary,
                PowerYieldIntermittentVerificationIsBounded, PowerYieldObservationGapBoundary,
                PowerYieldHeldDiscardsInterruptedWindow, PowerYieldClockRollbackDiscardsEvidence,
                PowerYieldWaitsForLateMeter, PowerYieldSparseObservationCanEngage,
                PowerYieldSparseVerificationDoesNotFuse, PowerYieldInterruptedNegativeVerdictsDoNotFuse,
                PowerYieldSparseBaselineDoesNotFuse,
                PowerYieldContinuousJitterStillFuses
            };
            PowerBudgetYield.ClearFuse();
            try
            {
                foreach (bool proxy in new[] { false, true })
                    foreach (Action<bool> test in tests)
                    {
                        PowerBudgetYield.ClearFuse();
                        test(proxy);
                    }
            }
            finally { PowerBudgetYield.ClearFuse(); }
            return tests.Length * 2;
        }

        private static PowerBudgetYield RelaxedYield(bool proxy, bool held)
        {
            var state = new PowerBudgetYield();
            state.Begin(0, true, proxy);
            for (int second = 2; second <= 20; second += 2)
                state.Advance(second * TimeSpan.TicksPerSecond, 98, 40, 45, 150);
            Eq(YieldStage.Engaged, state.Stage);
            if (held)
            {
                for (int second = 22; second <= 36; second += 2)
                    state.Advance(second * TimeSpan.TicksPerSecond, 97, 40, 41, 140);
                Eq(YieldStage.Held, state.Stage);
            }
            return state;
        }

        private static void PowerYieldWaitsForLateRenderer(bool proxy)
        {
            var state = new PowerBudgetYield();
            state.Begin(0, true, proxy);
            for (int second = 2; second <= 90; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, -1, 40, 45, 150));
            Eq(YieldStage.Observing, state.Stage);
            for (int second = 92; second <= 110; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
            Eq(YieldAction.Engage, state.Advance(112 * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
        }

        private static void PowerYieldDiscardsStaleObservation(bool proxy)
        {
            var state = new PowerBudgetYield();
            state.Begin(0, true, proxy);
            // A loading scene must not dilute the new scene after a long telemetry gap.
            for (int second = 2; second <= 8; second += 2)
                state.Advance(second * TimeSpan.TicksPerSecond, 70, 40, 80, 200);
            for (int second = 10; second <= 80; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, -1, 40, 45, 150));
            Eq(YieldStage.Observing, state.Stage);
            for (int second = 82; second <= 100; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
            Eq(YieldAction.Engage, state.Advance(102 * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
            Eq(98.0, state.BaselineGpuUtil);
            if (!proxy) Eq(45.0, state.BaselineWatts);
            for (int second = 104; second <= 118; second += 2)
                state.Advance(second * TimeSpan.TicksPerSecond, 97, 40, 41, 140);
            Eq(YieldStage.Held, state.Stage);
        }

        private static void PowerYieldToleratesShortGaps(bool proxy)
        {
            var state = RelaxedYield(proxy, false);
            for (int second = 22; second <= 28; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, -1, 40, -1, -1));
            Eq(YieldStage.Engaged, state.Stage);
            for (int second = 30; second <= 34; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 97, 40, 41, 140));
            Eq(YieldAction.Keep, state.Advance(36 * TimeSpan.TicksPerSecond, 97, 40, 41, 140));

            state = RelaxedYield(proxy, true);
            for (int second = 38; second <= 76; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, -1, 40, -1, -1));
            for (int second = 78; second <= 84; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 97, 40, 41, 140));
            Eq(YieldStage.Held, state.Stage);

            // A brief meter outage is skipped, never included as zero watts/frequency.
            state = RelaxedYield(proxy, false);
            for (int second = 22; second <= 30; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 97, 40, double.NaN, -1));
            for (int second = 32; second <= 38; second += 2)
                state.Advance(second * TimeSpan.TicksPerSecond, 97, 40, 41, 140);
            Eq(YieldStage.Held, state.Stage);
        }

        private static void PowerYieldStillRestoresWithoutEvidence(bool proxy)
        {
            foreach (bool held in new[] { false, true })
            {
                var state = RelaxedYield(proxy, held);
                int start = held ? 36 : 20;
                for (int delta = 2; delta < 60; delta += 2)
                    Eq(YieldAction.None, state.Advance((start + delta) * TimeSpan.TicksPerSecond, -1, 40, 41, 140));
                Eq(YieldAction.Revert, state.Advance((start + 60) * TimeSpan.TicksPerSecond, -1, 40, 41, 140));
                Eq(YieldStage.Reverted, state.Stage);
                Eq(YieldVerdict.Inconclusive, state.Verdict);
                Eq(false, PowerBudgetYield.Fused);
                Eq(false, PowerBudgetYield.FreqFused);

                state = RelaxedYield(proxy, held);
                // A late valid sample cannot conceal a full minute without evidence.
                Eq(YieldAction.Revert, state.Advance((start + 60) * TimeSpan.TicksPerSecond, 97, 40, 41, 140));
                Eq(YieldVerdict.Inconclusive, state.Verdict);
            }

            // The old bounded verification requirement still applies when only the meter fails.
            var missingMeter = RelaxedYield(proxy, false);
            for (int second = 22; second <= 42; second += 2)
                Eq(YieldAction.None, missingMeter.Advance(second * TimeSpan.TicksPerSecond, 97, 40, -1, -1));
            Eq(YieldAction.Revert, missingMeter.Advance(44 * TimeSpan.TicksPerSecond, 97, 40, -1, -1));
            Eq(YieldVerdict.Inconclusive, missingMeter.Verdict);
        }

        private static void PowerYieldStillReleasesForLoadShift(bool proxy)
        {
            foreach (bool cpuBottleneck in new[] { false, true })
            {
                var state = RelaxedYield(proxy, true);
                for (int second = 38; second <= 64; second += 2)
                    Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond,
                        cpuBottleneck ? 97 : 70, cpuBottleneck ? 80 : 40, 41, 140));
                Eq(YieldAction.Release, state.Advance(66 * TimeSpan.TicksPerSecond,
                    cpuBottleneck ? 97 : 70, cpuBottleneck ? 80 : 40, 41, 140));
                Eq(YieldStage.Observing, state.Stage);
            }
        }

        private static void PowerYieldStillRejectsGpuHarm(bool proxy)
        {
            var state = RelaxedYield(proxy, false);
            for (int second = 22; second <= 34; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 94, 40, 41, 140));
            Eq(YieldAction.Revert, state.Advance(36 * TimeSpan.TicksPerSecond, 94, 40, 41, 140));
            Eq(YieldStage.Reverted, state.Stage);
        }

        private static void AssertYieldInconclusive(PowerBudgetYield state, YieldAction action)
        {
            Eq(YieldAction.Revert, action);
            Eq(YieldStage.Reverted, state.Stage);
            Eq(YieldVerdict.Inconclusive, state.Verdict);
            Eq(false, PowerBudgetYield.Fused);
            Eq(false, PowerBudgetYield.FreqFused);
        }

        private static void PowerYieldInterruptedVerificationDoesNotFuse(bool proxy)
        {
            foreach (bool onlyMeterMissing in new[] { false, true })
            {
                var state = RelaxedYield(proxy, false);
                for (int second = 22; second <= 26; second += 2)
                    Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 97, 40, 41, 140));
                int recovery = onlyMeterMissing ? 42 : 70;
                for (int second = 28; second < recovery; second += 2)
                    Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond,
                        onlyMeterMissing ? 97 : -1, 40, -1, -1));
                // Three old samples plus this changed scene used to set a persistent hardware fuse.
                AssertYieldInconclusive(state, state.Advance(recovery * TimeSpan.TicksPerSecond,
                    70, 40, 41, 140));
            }
        }

        private static void PowerYieldVerificationGapBoundary(bool proxy)
        {
            foreach (int gap in new[] { 14, 15 })
            {
                var state = RelaxedYield(proxy, false);
                for (int second = 22; second <= 26; second += 2)
                    state.Advance(second * TimeSpan.TicksPerSecond, 97, 40, 41, 140);
                YieldAction action = state.Advance((26 + gap) * TimeSpan.TicksPerSecond, 97, 40, 41, 140);
                if (gap == 15) AssertYieldInconclusive(state, action);
                else { Eq(YieldAction.Keep, action); Eq(YieldStage.Held, state.Stage); }
            }
        }

        private static void PowerYieldIntermittentVerificationIsBounded(bool proxy)
        {
            var state = RelaxedYield(proxy, false);
            // Each successful sample is less than one verification-window gap from its predecessor.
            // They must not keep an unverified EPP change alive indefinitely.
            for (int second = 22; second < 80; second += 2)
            {
                bool complete = second == 34 || second == 48 || second == 62;
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond,
                    complete ? 97 : -1, 40, complete ? 41 : -1, complete ? 140 : -1));
            }
            AssertYieldInconclusive(state, state.Advance(80 * TimeSpan.TicksPerSecond, -1, 40, -1, -1));
        }

        private static void PowerYieldObservationGapBoundary(bool proxy)
        {
            foreach (int gap in new[] { 19, 20 })
            {
                var state = new PowerBudgetYield(); state.Begin(0, true, proxy);
                for (int second = 2; second <= 18; second += 2)
                    state.Advance(second * TimeSpan.TicksPerSecond, 98, 40, 80, 200);
                long recovery = (18 + gap) * TimeSpan.TicksPerSecond;
                YieldAction action = state.Advance(recovery, 98, 40, 45, 150);
                if (gap == 19) { Eq(YieldAction.Engage, action); continue; }
                Eq(YieldAction.None, action);
                for (int offset = 2; offset < 20; offset += 2)
                    Eq(YieldAction.None, state.Advance(recovery + offset * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
                Eq(YieldAction.Engage, state.Advance(recovery + PowerBudgetYield.ObserveTicks, 98, 40, 45, 150));
                Eq(98.0, state.BaselineGpuUtil);
                if (!proxy) Eq(45.0, state.BaselineWatts);
                for (int offset = 22; offset <= 36; offset += 2)
                    state.Advance(recovery + offset * TimeSpan.TicksPerSecond, 97, 40, 41, 140);
                Eq(YieldStage.Held, state.Stage);
            }
        }

        private static void PowerYieldHeldDiscardsInterruptedWindow(bool proxy)
        {
            foreach (int gap in new[] { 29, 30 })
            {
                var state = RelaxedYield(proxy, true);
                for (int second = 38; second <= 44; second += 2)
                    Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 70, 80, 41, 140));
                long recovery = (44 + gap) * TimeSpan.TicksPerSecond;
                YieldAction action = state.Advance(recovery, 97, 40, -1, -1);
                if (gap == 29) { Eq(YieldAction.Release, action); continue; }
                Eq(YieldAction.None, action);
                for (int offset = 2; offset <= 30; offset += 2)
                    Eq(YieldAction.None, state.Advance(recovery + offset * TimeSpan.TicksPerSecond, 97, 40, -1, -1));
                Eq(YieldStage.Held, state.Stage);
                for (int offset = 32; offset < 60; offset += 2)
                    Eq(YieldAction.None, state.Advance(recovery + offset * TimeSpan.TicksPerSecond, 70, 80, -1, -1));
                Eq(YieldAction.Release, state.Advance(recovery + 60 * TimeSpan.TicksPerSecond, 70, 80, -1, -1));
                Eq(YieldStage.Observing, state.Stage);
                Eq(false, PowerBudgetYield.Fused); Eq(false, PowerBudgetYield.FreqFused);
            }
        }

        private static void PowerYieldClockRollbackDiscardsEvidence(bool proxy)
        {
            foreach (bool held in new[] { false, true })
            {
                var state = RelaxedYield(proxy, held);
                int start = held ? 36 : 20;
                Eq(YieldAction.None, state.Advance((start + 10) * TimeSpan.TicksPerSecond, -1, 40, -1, -1));
                // Still later than the last valid evidence, but earlier than the last callback.
                AssertYieldInconclusive(state, state.Advance((start + 8) * TimeSpan.TicksPerSecond, 97, 40, 41, 140));
            }
            var observing = new PowerBudgetYield(); observing.Begin(0, true, proxy);
            observing.Advance(10 * TimeSpan.TicksPerSecond, 70, 40, 80, 200);
            for (int second = 8; second <= 26; second += 2)
                Eq(YieldAction.None, observing.Advance(second * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
            Eq(YieldAction.Engage, observing.Advance(28 * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
            if (!proxy) Eq(45.0, observing.BaselineWatts);
        }

        private static void PowerYieldWaitsForLateMeter(bool proxy)
        {
            var state = new PowerBudgetYield(); state.Begin(0, true, proxy);
            for (int second = 2; second <= 90; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 98, 40,
                    proxy ? 45 : double.NaN, proxy ? double.NaN : 150));
            Eq(YieldStage.Observing, state.Stage);
            for (int second = 92; second <= 110; second += 2)
                Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
            Eq(YieldAction.Engage, state.Advance(112 * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
        }

        private static void PowerYieldSparseObservationCanEngage(bool proxy)
        {
            var state = new PowerBudgetYield(); state.Begin(0, true, proxy);
            for (int second = 2; second <= 56; second += 2)
            {
                bool complete = second == 2 || second == 20 || second == 38 || second == 56;
                YieldAction action = state.Advance(second * TimeSpan.TicksPerSecond,
                    complete ? 98 : 20, complete ? 40 : 90,
                    complete ? 45 : proxy ? 45 : double.NaN,
                    complete ? 150 : proxy ? double.NaN : 150);
                Eq(second == 56 ? YieldAction.Engage : YieldAction.None, action);
                if (second < 56) Eq(YieldStage.Observing, state.Stage);
            }
            // Invalid meter samples must not dilute either the meter or its paired load baseline.
            Eq(YieldStage.Engaged, state.Stage);
            Eq(98.0, state.BaselineGpuUtil);
            if (!proxy) Eq(45.0, state.BaselineWatts);
            for (int second = 58; second <= 72; second += 2)
                state.Advance(second * TimeSpan.TicksPerSecond, 97, 40, 41, 140);
            Eq(YieldStage.Held, state.Stage);
            Eq(false, PowerBudgetYield.Fused); Eq(false, PowerBudgetYield.FreqFused);
        }

        private static void PowerYieldSparseVerificationDoesNotFuse(bool proxy)
        {
            foreach (bool deliverInvalidCallbacks in new[] { false, true })
            {
                var state = RelaxedYield(proxy, false);
                for (int second = 22; second <= 64; second += 2)
                {
                    bool complete = second == 22 || second == 36 || second == 50 || second == 64;
                    if (!complete && !deliverInvalidCallbacks) continue;
                    YieldAction action = state.Advance(second * TimeSpan.TicksPerSecond,
                        complete ? second == 64 ? 70 : 97 : -1, 40,
                        complete ? 41 : -1, complete ? 140 : -1);
                    if (second == 64) AssertYieldInconclusive(state, action);
                    else Eq(YieldAction.None, action);
                }
            }
        }

        private static void PowerYieldInterruptedNegativeVerdictsDoNotFuse(bool proxy)
        {
            foreach (bool gpuHarm in new[] { false, true })
                foreach (int interruption in new[] { 0, 1, 2 })
                {
                    var state = RelaxedYield(proxy, false);
                    int firstValid = 24;
                    if (interruption == 0)
                        state.Advance(22 * TimeSpan.TicksPerSecond, -1, 40, 41, 140);
                    else if (interruption == 1)
                        state.Advance(22 * TimeSpan.TicksPerSecond, 97, 40, -1, -1);
                    else firstValid = 26; // Worker paused without delivering an invalid callback.
                    YieldAction action = YieldAction.None;
                    for (int second = firstValid; second <= 36; second += 2)
                        action = state.Advance(second * TimeSpan.TicksPerSecond,
                            gpuHarm ? 94 : 97, 40, gpuHarm ? 41 : 45, gpuHarm ? 140 : 150);
                    AssertYieldInconclusive(state, action);
                }
        }

        private static void PowerYieldSparseBaselineDoesNotFuse(bool proxy)
        {
            foreach (bool deliverInvalidCallbacks in new[] { false, true })
            {
                PowerBudgetYield.ClearFuse();
                var state = new PowerBudgetYield(); state.Begin(0, true, proxy);
                for (int second = 2; second <= 56; second += 2)
                {
                    bool complete = second == 2 || second == 20 || second == 38 || second == 56;
                    if (!complete && !deliverInvalidCallbacks) continue;
                    YieldAction action = state.Advance(second * TimeSpan.TicksPerSecond,
                        complete ? 98 : 20, complete ? 40 : 90,
                        complete ? second == 56 ? 45 : 20 : double.NaN,
                        complete ? second == 56 ? 150 : 80 : double.NaN);
                    Eq(second == 56 ? YieldAction.Engage : YieldAction.None, action);
                }
                if (!proxy) Eq(26.25, state.BaselineWatts);
                // 最新场景的 45→41W / 150→140% 有收益，但稀疏旧基线更低；
                // 即使验证连续，也不能据混合场景写入永久硬件熔断。
                for (int second = 58; second < 72; second += 2)
                    Eq(YieldAction.None, state.Advance(second * TimeSpan.TicksPerSecond, 97, 40, 41, 140));
                AssertYieldInconclusive(state, state.Advance(72 * TimeSpan.TicksPerSecond, 97, 40, 41, 140));
            }

            // 旧观察窗被丢弃后，新连续窗口的真实负结果仍应熔断。
            PowerBudgetYield.ClearFuse();
            var fresh = new PowerBudgetYield(); fresh.Begin(0, true, proxy);
            fresh.Advance(2 * TimeSpan.TicksPerSecond, 98, 40, 45, 150);
            fresh.Advance(4 * TimeSpan.TicksPerSecond, -1, 40, -1, -1);
            for (int second = 24; second < 44; second += 2)
                Eq(YieldAction.None, fresh.Advance(second * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
            Eq(YieldAction.Engage, fresh.Advance(44 * TimeSpan.TicksPerSecond, 98, 40, 45, 150));
            for (int second = 46; second < 60; second += 2)
                Eq(YieldAction.None, fresh.Advance(second * TimeSpan.TicksPerSecond, 97, 40, 45, 150));
            Eq(YieldAction.Revert, fresh.Advance(60 * TimeSpan.TicksPerSecond, 97, 40, 45, 150));
            Eq(YieldVerdict.NoGain, fresh.Verdict);
            Eq(!proxy, PowerBudgetYield.Fused); Eq(proxy, PowerBudgetYield.FreqFused);
        }

        private static void PowerYieldContinuousJitterStillFuses(bool proxy)
        {
            foreach (bool gpuHarm in new[] { false, true })
            {
                PowerBudgetYield.ClearFuse();
                var state = RelaxedYield(proxy, false);
                YieldAction action = YieldAction.None;
                // Three-second scheduling intervals are ordinary jitter, not interrupted evidence.
                for (int second = 23; second <= 35; second += 3)
                    action = state.Advance(second * TimeSpan.TicksPerSecond,
                        gpuHarm ? 94 : 97, 40, gpuHarm ? 41 : 45, gpuHarm ? 140 : 150);
                Eq(YieldAction.Revert, action);
                Eq(proxy && gpuHarm ? YieldVerdict.GpuHarm : YieldVerdict.NoGain, state.Verdict);
                Eq(!proxy, PowerBudgetYield.Fused); Eq(proxy, PowerBudgetYield.FreqFused);
            }
        }
    }
}
#endif
