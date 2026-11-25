using System;

namespace PaviseApp
{
    internal static class Logger { internal static void Log(Func<string> text) { } }
    internal static class Lang { internal static string T(string text) { return text; } }

    internal sealed class AdaptiveRoundFixture
    {
        private readonly AdaptiveGuard adaptiveGuard = new AdaptiveGuard();
        private readonly CpuSaturation adaptivePressure = new CpuSaturation(AdaptiveGuard.EnterUtilization, AdaptiveGuard.ExitUtilization);
        private bool adaptiveEscalated;
        private long now;
        internal bool AdaptiveEscalationWanted = true;
        internal bool Escalated { get { return adaptiveEscalated; } }
        internal int Episodes { get { return adaptiveGuard.Episodes; } }
        private long AdaptiveNow() { return now; }
        private double AdaptiveSample() { return adaptivePressure.Sample(); }

        // Insert production step
        // Insert production reset

        internal void Tick(long when, bool busy)
        {
            now = when;
            SmartGovernanceThresholdChecks.NativeIdle += busy ? 40 : 120;
            SmartGovernanceThresholdChecks.NativeKernel += 150;
            SmartGovernanceThresholdChecks.NativeUser += 50;
            StepAdaptiveGuard();
        }
    }

    internal static class SmartGovernanceThresholdChecks
    {
        private static int checks;
        private const long Second = TimeSpan.TicksPerSecond;
        private static void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException(message);
        }
        private static CpuSaturation Smart()
        { return new CpuSaturation(AdaptiveGuard.EnterUtilization, AdaptiveGuard.ExitUtilization); }

        private static void SmartBoundaries()
        {
            var cpu = Smart();
            Check(!cpu.Update(0.699, 0) && !cpu.Update(0.699, 20 * Second), "Below 70% must not enter");
            Check(!cpu.Update(0.7, 21 * Second), "A single 70% sample must not enter");
            Check(!cpu.Update(0.7, 31 * Second - 1), "The full ten-second confirmation is required");
            Check(cpu.Update(0.7, 31 * Second), "70% sustained for ten seconds must enter");
            Check(cpu.Update(0.65, 40 * Second), "The 60-70% band must retain the active state");
            Check(cpu.Update(0.6, 50 * Second), "Exactly 60% must not start low-pressure confirmation");
            Check(cpu.Update(0.599, 51 * Second), "One low sample must not exit");
            Check(cpu.Update(0.599, 56 * Second - 1), "The full five-second low-pressure hold is required");
            Check(!cpu.Update(0.599, 56 * Second), "Below 60% for five seconds must clear pressure");
            cpu.Reset();
            Check(!cpu.Update(0.75, 0) && cpu.Update(0.75, 10 * Second), "Reset must keep Smart thresholds");
            cpu.Reset();
            Check(!cpu.Update(0.75, 0) && !cpu.Update(0.69, 9 * Second)
                && !cpu.Update(0.75, 10 * Second) && !cpu.Update(0.75, 19 * Second)
                && cpu.Update(0.75, 20 * Second), "Interrupted confirmation must restart its timer");
        }

        private static void OtherConsumersUnchanged()
        {
            var cpu = new CpuSaturation();
            Check(CpuSaturation.EnterUtilization == 0.9 && CpuSaturation.ExitUtilization == 0.8,
                "Default CPU saturation thresholds must remain 90/80");
            Check(!cpu.Update(0.75, 0) && !cpu.Update(0.75, 20 * Second), "75% must not affect other saturation users");
            Check(!cpu.Update(0.9, 21 * Second) && cpu.Update(0.9, 31 * Second), "Default 90% confirmation changed");
            Check(cpu.Update(0.8, 40 * Second) && cpu.Update(0.8, 60 * Second), "Default exit boundary changed");
            Check(cpu.Update(0.79, 61 * Second) && !cpu.Update(0.79, 66 * Second), "Default 80% exit changed");
        }

        private static void GuardTiming()
        {
            var cpu = Smart(); var guard = new AdaptiveGuard();
            Check(guard.Step(cpu.Update(0.75, 0), 0) == 0, "Guard entered on one sample");
            Check(guard.Step(cpu.Update(0.75, 10 * Second), 10 * Second) == 1, "Guard did not expand at sustained 70%");
            Check(guard.Step(cpu.Update(0.55, 11 * Second), 11 * Second) == 0, "Guard relaxed before pressure cleared");
            Check(guard.Step(cpu.Update(0.55, 16 * Second), 16 * Second) == 0, "Guard must retain its relaxation delay");
            Check(guard.Step(cpu.Update(0.55, 136 * Second - 1), 136 * Second - 1) == 0, "Guard relaxed before 120 calm seconds");
            Check(guard.Step(cpu.Update(0.55, 136 * Second), 136 * Second) == -1, "Guard did not relax after 120 calm seconds");
            for (int episode = 1; episode <= 2; episode++)
            {
                long start = (200 + episode * 200) * Second;
                Check(guard.Step(true, start) == 1, "Existing episode allowance changed");
                Check(guard.Step(false, start + Second) == 0
                    && guard.Step(false, start + 121 * Second) == -1, "Repeated episode did not settle");
            }
            Check(guard.Episodes == AdaptiveGuard.MaxEpisodes && guard.Step(true, 1000 * Second) == 0,
                "Existing three-episode bound changed");
        }

        private static void ExhaustedAdaptiveRoundSkipsNativeReads()
        {
            var round = new AdaptiveRoundFixture();
            NativeGroups = 1; NativeReads = 0;
            NativeIdle = NativeKernel = NativeUser = 0;
            for (int episode = 1; episode <= 3; episode++)
            {
                long now = (episode * 200) * Second;
                round.Tick(now, true);
                round.Tick(now + Second, true);
                round.Tick(now + 11 * Second, true);
                Check(round.Escalated && round.Episodes == episode, "native guard lost an escalation");
                int before = NativeReads;
                round.Tick(now + 12 * Second, false);
                round.Tick(now + 17 * Second, false);
                Check(round.Escalated && NativeReads == before + 2, "guard stopped sampling during its last active episode");
                round.Tick(now + 137 * Second, false);
                Check(!round.Escalated && NativeReads == before + 3, "guard failed to sample the final de-escalation");
            }
            int exhausted = NativeReads;
            for (int i = 0; i < 5; i++) round.Tick((800 + i) * Second, true);
            Console.WriteLine("Exhausted adaptive native queries for five rounds=" + (NativeReads - exhausted));
            Check(NativeReads == exhausted && !round.Escalated, "settled adaptive guard still queried native CPU");
            round.AdaptiveEscalationWanted = false;
            round.Tick(900 * Second, true);
            Check(round.Episodes == 0 && NativeReads == exhausted, "disabled guard did not reset without sampling");
            round.AdaptiveEscalationWanted = true;
            round.Tick(901 * Second, true);
            Check(NativeReads == exhausted + 1 && !round.Escalated, "new adaptive phase did not sample again");
        }

        private static void InvalidInput()
        {
            foreach (double[] pair in new[] {
                new[] { 0.0, 0.0 }, new[] { 0.7, 0.7 }, new[] { 0.7, 0.8 },
                new[] { 1.01, 0.6 }, new[] { 0.7, -0.1 }, new[] { double.NaN, 0.6 },
                new[] { 0.7, double.NaN }, new[] { double.PositiveInfinity, 0.6 } })
            {
                bool rejected = false;
                try { new CpuSaturation(pair[0], pair[1]); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected, "Invalid hysteresis thresholds were accepted");
            }
            var cpu = Smart();
            Check(!cpu.Update(0.75, 0) && !cpu.Update(double.NaN, 9 * Second)
                && !cpu.Update(0.75, 10 * Second) && cpu.Update(0.75, 20 * Second),
                "Missing samples must not complete a confirmation window");
        }

        internal static ushort NativeGroups = 1;
        internal static int NativeReads;
        internal static bool NativeAvailable = true;
        internal static long NativeIdle, NativeKernel, NativeUser;
        internal static ushort ReadNativeGroups() { return NativeGroups; }
        internal static bool ReadNativeTimes(out long idle, out long kernel, out long user)
        {
            NativeReads++; idle = NativeIdle; kernel = NativeKernel; user = NativeUser; return NativeAvailable;
        }

        private static void NativeCounterScope()
        {
            var cpu = Smart();
            NativeReads = 0; NativeGroups = 2;
            Check(double.IsNaN(cpu.Sample()) && NativeReads == 0, "Multiple groups still queried partial system times");
            NativeGroups = 1; NativeIdle = 100; NativeKernel = NativeUser = 200;
            Check(double.IsNaN(cpu.Sample()) && NativeReads == 1, "Single-group wrapper did not prime its native sample");
            NativeIdle = 200; NativeKernel = NativeUser = 400;
            Check(cpu.Sample() == 0.75 && NativeReads == 2, "Single-group wrapper did not preserve CPU deltas");
            NativeGroups = 2;
            for (int i = 0; i < 1000; i++)
                Check(double.IsNaN(cpu.Sample()) && NativeReads == 2, "Multi-group polling continued partial native samples");
            NativeGroups = 1; NativeIdle = 300; NativeKernel = NativeUser = 600;
            Check(double.IsNaN(cpu.Sample()), "Native wrapper reused the pre-group-change baseline");
            cpu = Smart();
            Check(double.IsNaN(cpu.ObserveSystemTimes(1, true, 100, 200, 200)), "First native reading was treated as a delta");
            Check(cpu.ObserveSystemTimes(1, true, 200, 400, 400) == 0.75, "Single-group CPU delta changed");
            for (int i = 0; i < 1000; i++)
                Check(double.IsNaN(cpu.ObserveSystemTimes(2, true, 300 + i, 600 + i, 600 + i)), "Partial processor-group data became whole-system load");
            Check(double.IsNaN(cpu.ObserveSystemTimes(1, true, 2000, 4000, 4000)), "A return from multiple groups reused the old baseline");
            Check(cpu.ObserveSystemTimes(1, true, 2100, 4200, 4200) == 0.75, "Single-group sampling did not recover after priming");
            Check(double.IsNaN(cpu.ObserveSystemTimes(1, false, 2200, 4400, 4400)), "A failed native read fabricated CPU load");
            Check(double.IsNaN(cpu.ObserveSystemTimes(1, true, 2300, 4600, 4600)), "A failed read did not invalidate the baseline");
            Check(double.IsNaN(cpu.ObserveSystemTimes(0, true, 1, 2, 2)), "Unknown group count became usable CPU load");
            Check(double.IsNaN(cpu.ObserveSystemTimes(1, true, 2, 1, 1)), "Idle time exceeded kernel time without rejection");
            Check(double.IsNaN(cpu.ObserveSystemTimes(1, true, 1, long.MaxValue, 1)), "Counter sum overflow was accepted");
            Check(double.IsNaN(cpu.ObserveSystemTimes(1, true, 1, 2, -1)), "Negative user counter was accepted");
        }

        public static int Main()
        {
            try
            {
                SmartBoundaries(); OtherConsumersUnchanged(); GuardTiming(); InvalidInput(); NativeCounterScope(); ExhaustedAdaptiveRoundSkipsNativeReads();
                Console.WriteLine("PASS Smart governance thresholds assertions=" + checks
                    + "; Smart=70/60; other_users=90/80; confirmation=10s; exit_hold=5s; relaxation=120s; native_samples=false; Pavise_started=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
