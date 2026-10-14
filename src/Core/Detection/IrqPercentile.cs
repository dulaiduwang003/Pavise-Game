// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal static class IrqPercentile
    {
        internal static double ApproxUs(long[] buckets, double maxUs, double q)
        {
            if (buckets == null || buckets.Length == 0) return maxUs;
            long total = 0;
            foreach (long b in buckets) total += b;
            if (total <= 0) return maxUs;
            long want = (long)Math.Ceiling(total * q);
            long acc = 0;
            for (int i = 0; i < buckets.Length; i++)
            {
                acc += buckets[i];
                if (acc < want) continue;
                double upper = i < InterruptAttribution.BucketUpperUs.Length
                    ? InterruptAttribution.BucketUpperUs[i] : double.MaxValue;
                return double.IsInfinity(upper) || upper == double.MaxValue
                    ? maxUs : Math.Min(upper, maxUs);
            }
            return maxUs;
        }
    }
}
