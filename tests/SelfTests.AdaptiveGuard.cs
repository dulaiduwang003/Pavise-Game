// 文件用途 纯状态机 不采样 不碰注册表 不建窗口
#if PAVISE_SELFTEST
using System;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int adaptiveChecks;

        internal static int RunAdaptiveGuardRegressionTests()
        {
            Action[] tests =
            {
                AdaptiveGuardEscalatesAndCaps,
                AdaptiveGuardRelaxNeedsSustainedCalm,
                AdaptiveGuardResetClearsEverything
            };
            adaptiveChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS adaptive-guard assertions=" + adaptiveChecks
                + " sampling=none settings=untouched windows_shown=false");
            return tests.Length;
        }

        private static void AdaptiveCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Adaptive suppression regression: " + message);
            Interlocked.Increment(ref adaptiveChecks);
        }

        private static long AdaptiveRelax(int cycle)
        {
            return AdaptiveGuard.RelaxTicks * cycle;
        }

        private static void AdaptiveGuardEscalatesAndCaps()
        {
            var guard = new AdaptiveGuard();
            AdaptiveCheck(guard.Step(false, 1) == 0 && !guard.Escalated,
                "calm samples must never escalate");
            for (int episode = 1; episode <= AdaptiveGuard.MaxEpisodes; episode++)
            {
                long t = AdaptiveRelax(episode * 4);
                AdaptiveCheck(guard.Step(true, t) == 1 && guard.Escalated
                    && guard.Episodes == episode, "saturation must escalate episode " + episode);
                AdaptiveCheck(guard.Step(true, t + 1) == 0 && guard.Escalated,
                    "an escalated guard must hold while saturation continues");
                AdaptiveCheck(guard.Step(false, t + 2) == 0,
                    "the first calm sample only starts the relax timer");
                AdaptiveCheck(guard.Step(false, t + 2 + AdaptiveGuard.RelaxTicks) == -1
                    && !guard.Escalated, "sustained calm must step back down");
            }
            AdaptiveCheck(guard.Step(true, AdaptiveRelax(100)) == 0 && !guard.Escalated,
                "the per-match episode cap must hold");
        }

        private static void AdaptiveGuardRelaxNeedsSustainedCalm()
        {
            var guard = new AdaptiveGuard();
            AdaptiveCheck(guard.Step(true, 10) == 1, "escalate for the relax test");
            AdaptiveCheck(guard.Step(false, 20) == 0, "calm starts the timer");
            // 稳定期没满又饱和 计时清零 之后必须重新攒满整段稳定期
            AdaptiveCheck(guard.Step(true, 20 + AdaptiveGuard.RelaxTicks / 2) == 0 && guard.Escalated,
                "re-saturation inside the calm window must keep the escalation");
            long again = AdaptiveGuard.RelaxTicks * 2;
            AdaptiveCheck(guard.Step(false, again) == 0 && guard.Escalated,
                "the relax timer must restart from zero after re-saturation");
            AdaptiveCheck(guard.Step(false, again + AdaptiveGuard.RelaxTicks - 1) == 0 && guard.Escalated,
                "almost-enough calm must not step down");
            AdaptiveCheck(guard.Step(false, again + AdaptiveGuard.RelaxTicks) == -1 && !guard.Escalated,
                "a full calm window must step down");
        }

        private static void AdaptiveGuardResetClearsEverything()
        {
            var guard = new AdaptiveGuard();
            for (int episode = 0; episode < AdaptiveGuard.MaxEpisodes; episode++)
            {
                guard.Step(true, AdaptiveRelax(episode * 4) + 1);
                guard.Step(false, AdaptiveRelax(episode * 4 + 1));
                guard.Step(false, AdaptiveRelax(episode * 4 + 3));
            }
            AdaptiveCheck(guard.Episodes == AdaptiveGuard.MaxEpisodes, "episodes must accumulate");
            guard.Reset();
            AdaptiveCheck(!guard.Escalated && guard.Episodes == 0,
                "a new match must start from a clean guard");
            AdaptiveCheck(guard.Step(true, 5) == 1, "a reset guard must escalate again");
        }
    }
}
#endif
