// @author bdth 2074055628@qq.com
// 文件用途 系统级 CPU 饱和判定 带滞回与连续样本门槛 供智能保帧决定提优档位

using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class CpuSaturation
    {
        public const double EnterUtilization = 0.90;
        public const double ExitUtilization = 0.80;
        public const int RequiredStreak = 2;

        private bool saturated;
        private int streak;
        private long lastIdle = -1;
        private long lastTotal = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        public bool Saturated { get { return saturated; } }

        public bool Update(double utilization)
        {
            if (double.IsNaN(utilization)) { streak = 0; return saturated; }
            if (!saturated)
            {
                streak = utilization >= EnterUtilization ? streak + 1 : 0;
                if (streak >= RequiredStreak) { saturated = true; streak = 0; }
            }
            else
            {
                streak = utilization < ExitUtilization ? streak + 1 : 0;
                if (streak >= RequiredStreak) { saturated = false; streak = 0; }
            }
            return saturated;
        }

        public void Reset()
        {
            saturated = false;
            streak = 0;
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
