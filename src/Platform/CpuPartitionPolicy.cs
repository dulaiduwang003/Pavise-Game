// @author bdth 2074055628@qq.com
// 文件用途 只负责 CPU 分区决策 不读取硬件也不调用 Windows API

using System;

namespace PaviseApp
{
    internal static class CpuPartitionPolicy
    {
        public static ulong StrictMask(ulong all, ulong background, ulong performance, ulong cache)
        {
            ulong preferred = cache != 0 ? cache
                : (performance != 0 ? performance : (all & ~background));
            preferred &= all;
            return preferred != 0 ? preferred : all;
        }

        public static int BackgroundCoreCount(int physicalCoreCount)
        {
            if (physicalCoreCount <= 6) return 0;
            if (physicalCoreCount <= 10) return 1;
            return Math.Min(4, Math.Max(2, physicalCoreCount / 8));
        }

        public const int InterruptAvoidMinPhysicalCores = 8;
        public const double InterruptAvoidMinRate = 0.01;
        public const double InterruptAvoidRatio = 2.0;

        public static double Median(double[] values)
        {
            if (values == null || values.Length == 0) return 0;
            var copy = new double[values.Length];
            Array.Copy(values, copy, values.Length);
            Array.Sort(copy);
            int mid = copy.Length / 2;
            return copy.Length % 2 == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) / 2.0;
        }

        public static double CoreInterruptRate(double[] rates, ulong coreMask)
        {
            if (rates == null || coreMask == 0) return 0;
            double peak = 0;
            for (int i = 0; i < rates.Length && i < 64; i++)
                if (((coreMask >> i) & 1UL) != 0 && rates[i] > peak) peak = rates[i];
            return peak;
        }

        public static ulong FindInterruptCore(double[] rates, ulong[] coreMasks, int physicalCoreCount)
        {
            if (physicalCoreCount < InterruptAvoidMinPhysicalCores) return 0;
            if (rates == null || coreMasks == null || coreMasks.Length < 2) return 0;
            ulong worst = 0;
            double worstRate = 0, secondRate = 0;
            foreach (ulong mask in coreMasks)
            {
                if (mask == 0) continue;
                double rate = CoreInterruptRate(rates, mask);
                if (rate > worstRate) { secondRate = worstRate; worstRate = rate; worst = mask; }
                else if (rate > secondRate) secondRate = rate;
            }
            if (worst == 0 || worstRate < InterruptAvoidMinRate) return 0;
            if (secondRate > 0 && worstRate < secondRate * InterruptAvoidRatio) return 0;
            return worst;
        }
    }
}
