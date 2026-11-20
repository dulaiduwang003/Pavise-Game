using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class UnifiedThreadPressurePolicy
    {
        // 持续提优须满足稳定压力门槛 短促唤醒 单边尖峰或低覆盖均不准入
        internal static bool Accept(ThreadRelationSnapshot relation, ThreadRelationMetrics role, out string reason)
        {
            reason = Logger.Text("缺少可核验的两半窗口 CPU 排队证据", "no verifiable two-half-window CPU queueing evidence");
            if (relation == null || !relation.Valid || relation.LostEvents != 0 || role == null || role.StateConflicts != 0
                || !Window(relation.FirstHalfDurationMs, role.KnownFirstMs, role.ReadyFirstMs)
                || !Window(relation.SecondHalfDurationMs, role.KnownSecondMs, role.ReadySecondMs)) return false;
            double firstCoverage = role.KnownFirstMs / relation.FirstHalfDurationMs;
            double secondCoverage = role.KnownSecondMs / relation.SecondHalfDurationMs;
            double firstReady = role.ReadyFirstMs / relation.FirstHalfDurationMs;
            double secondReady = role.ReadySecondMs / relation.SecondHalfDurationMs;
            reason = Logger.Text("线程=", "thread=") + role.Tid + Logger.Text("；前后半已知覆盖=", "; halfKnownCoverage=") + Percent(firstCoverage) + "/" + Percent(secondCoverage)
                + Logger.Text("；前后半Ready占窗口=", "; halfReadyShare=") + Percent(firstReady) + "/" + Percent(secondReady)
                + Logger.Text("；ReadyP95毫秒=", "; readyP95Ms=") + role.ReadyP95Ms.ToString("F3", CultureInfo.InvariantCulture);
            if (firstCoverage < .9 || secondCoverage < .9 || firstReady < .01 || secondReady < .01
                || role.ReadyFirstIntervals < 2 || role.ReadySecondIntervals < 2 || role.ReadyIntervals < 4
                || !Finite(role.ReadyP95Ms) || role.ReadyP95Ms < .2)
            {
                reason += Logger.Text("；未达到持续排队门槛：两半覆盖90%、各自Ready占1%、各2次起始片段且P95至少0.2毫秒", "; sustained queueing threshold not met: each half needs 90% coverage, 1% Ready, 2 onset intervals, and P95 of at least 0.2 ms");
                return false;
            }
            reason += Logger.Text("；两半持续CPU排队已核验，收益仍由独立对照判断", "; sustained CPU queueing verified in both halves; benefit still judged by an independent comparison");
            return true;
        }

        private static bool Window(double duration, double known, double ready)
        {
            return Finite(duration) && Finite(known) && Finite(ready) && duration > 0 && known >= 0 && ready >= 0
                && known <= duration + .000001 && ready <= known + .000001;
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static string Percent(double value) { return (100 * value).ToString("F2", CultureInfo.InvariantCulture) + "%"; }
    }
}
