using System;

namespace PaviseApp
{
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

        public static int Main()
        {
            try
            {
                SmartBoundaries(); OtherConsumersUnchanged(); GuardTiming(); InvalidInput();
                Console.WriteLine("PASS Smart governance thresholds assertions=" + checks
                    + "; Smart=70/60; other_users=90/80; confirmation=10s; exit_hold=5s; relaxation=120s; native_samples=false; Pavise_started=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
