using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class CpuSaturation
    {
        public const double EnterUtilization = 0.9;

        public const double ExitUtilization = 0.8;

        public const long EnterHoldTicks = 100000000L;

        public const long ExitHoldTicks = 50000000L;

        public const long StaleTicks = 100000000L;

        private const long NotTiming = -1L;

        private readonly double enterUtilization;

        private readonly double exitUtilization;

        internal CpuSaturation() : this(EnterUtilization, ExitUtilization) { }

        internal CpuSaturation(double enter, double exit)
        {
            if (!(enter > 0 && enter <= 1 && exit >= 0 && exit < enter))
                throw new ArgumentOutOfRangeException("enter", "CPU thresholds must satisfy 0 <= exit < enter <= 1.");
            enterUtilization = enter;
            exitUtilization = exit;
        }

        private bool saturated;

        private long enterSince = NotTiming;

        private long exitSince = NotTiming;

        private long staleSince = NotTiming;

        private long lastIdle = NotTiming;

        private long lastTotal = NotTiming;

        public bool Saturated
        {
            get
            {
                return saturated;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        private static extern ushort GetActiveProcessorGroupCount();

        public bool Update(double utilization, long now)
        {
            if (double.IsNaN(utilization))
            {
                enterSince = NotTiming;
                exitSince = NotTiming;
                if (staleSince == -1)
                {
                    staleSince = now;
                }
                else if (saturated && now - staleSince >= 100000000)
                {
                    saturated = false;
                }
                return saturated;
            }
            staleSince = NotTiming;
            if (!saturated)
            {
                if (utilization >= enterUtilization)
                {
                    if (enterSince == -1)
                    {
                        enterSince = now;
                    }
                    else if (now - enterSince >= 100000000)
                    {
                        saturated = true;
                        enterSince = NotTiming;
                        exitSince = NotTiming;
                    }
                }
                else
                {
                    enterSince = NotTiming;
                }
            }
            else if (utilization < exitUtilization)
            {
                if (exitSince == -1)
                {
                    exitSince = now;
                }
                else if (now - exitSince >= 50000000)
                {
                    saturated = false;
                    enterSince = NotTiming;
                    exitSince = NotTiming;
                }
            }
            else
            {
                exitSince = NotTiming;
            }
            return saturated;
        }

        public void Reset()
        {
            saturated = false;
            enterSince = NotTiming;
            exitSince = NotTiming;
            staleSince = NotTiming;
            lastIdle = NotTiming;
            lastTotal = NotTiming;
        }

        public double Sample()
        {
            int groups = GetActiveProcessorGroupCount();
            // 多处理器组时该 API 只返回调用线程主组的时间 不能据此判断整机压力
            if (groups != 1) return ObserveSystemTimes(groups, false, 0, 0, 0);
            long idle;
            long kernel;
            long user;
            bool known = GetSystemTimes(out idle, out kernel, out user);
            return ObserveSystemTimes(groups, known, idle, kernel, user);
        }

        internal double ObserveSystemTimes(int groups, bool known, long idle, long kernel, long user)
        {
            if (groups != 1 || !known || idle < 0 || kernel < idle || user < 0 || kernel > long.MaxValue - user)
            {
                // 不跨未知区间拼接差值 恢复可读后先建立新基线
                lastIdle = lastTotal = NotTiming;
                return double.NaN;
            }
            long kernelOffset = kernel + user;
            double result = double.NaN;
            if (lastIdle >= 0)
            {
                result = Utilization(idle - lastIdle, kernelOffset - lastTotal);
            }
            lastIdle = idle;
            lastTotal = kernelOffset;
            return result;
        }

        internal static double Utilization(long idleDelta, long totalDelta)
        {
            if (totalDelta <= 0 || idleDelta < 0 || idleDelta > totalDelta)
            {
                return double.NaN;
            }
            return 1.0 - (double)idleDelta / (double)totalDelta;
        }
    }
}
