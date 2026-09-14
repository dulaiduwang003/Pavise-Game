// @author bdth 2074055628@qq.com
// File purpose System-wide CPU saturation decision with time-window hysteresis, used by boost to pick the main process priority tier
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class CpuSaturation
    {
        public const double EnterUtilization = 0.90;
        public const double ExitUtilization = 0.80;
        public const long EnterHoldTicks = TimeSpan.TicksPerSecond * 10;
        public const long ExitHoldTicks = TimeSpan.TicksPerSecond * 5;
        // Stop claiming saturation after this long without samples, otherwise one sampling failure could pin tier escalation until match end
        public const long StaleTicks = TimeSpan.TicksPerSecond * 10;

        private bool saturated;
        private const long NotTiming = -1;
        private long enterSince = NotTiming;
        private long exitSince = NotTiming;
        private long staleSince = NotTiming;
        private long lastIdle = -1;
        private long lastTotal = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        public bool Saturated { get { return saturated; } }

        public bool Update(double utilization, long now)
        {
            if (double.IsNaN(utilization))
            {
                enterSince = NotTiming;
                exitSince = NotTiming;
                if (staleSince == NotTiming) staleSince = now;
                else if (saturated && now - staleSince >= StaleTicks) saturated = false;
                return saturated;
            }
            staleSince = NotTiming;
            if (!saturated)
            {
                if (utilization >= EnterUtilization)
                {
                    if (enterSince == NotTiming) enterSince = now;
                    else if (now - enterSince >= EnterHoldTicks)
                    { saturated = true; enterSince = NotTiming; exitSince = NotTiming; }
                }
                else enterSince = NotTiming;
            }
            else
            {
                if (utilization < ExitUtilization)
                {
                    if (exitSince == NotTiming) exitSince = now;
                    else if (now - exitSince >= ExitHoldTicks)
                    { saturated = false; enterSince = NotTiming; exitSince = NotTiming; }
                }
                else exitSince = NotTiming;
            }
            return saturated;
        }

        public void Reset()
        {
            saturated = false;
            enterSince = NotTiming;
            exitSince = NotTiming;
            staleSince = NotTiming;
            lastIdle = -1;
            lastTotal = -1;
        }

        public double Sample()
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return double.NaN;
            long total = kernel + user;
            double result = double.NaN;
            if (lastIdle >= 0)
                result = Utilization(idle - lastIdle, total - lastTotal);
            lastIdle = idle;
            lastTotal = total;
            return result;
        }

        internal static double Utilization(long idleDelta, long totalDelta)
        {
            if (totalDelta <= 0 || idleDelta < 0 || idleDelta > totalDelta) return double.NaN;
            return 1.0 - (double)idleDelta / totalDelta;
        }
    }
}
