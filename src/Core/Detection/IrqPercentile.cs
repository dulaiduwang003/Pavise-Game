// @author bdth 2074055628@qq.com
// 文件用途 从 DPC 时长直方图折出近似分位
using System;

namespace PaviseApp
{
    internal static class IrqPercentile
    {
        internal static int BucketOf(long[] buckets, double q)
        {
            if (buckets == null || buckets.Length == 0) return -1;
            long total = 0;
            foreach (long b in buckets) total += b;
            if (total <= 0) return -1;
            long want = (long)Math.Ceiling(total * q);
            long acc = 0;
            for (int i = 0; i < buckets.Length; i++)
            {
                acc += buckets[i];
                if (acc >= want) return i;
            }
            return buckets.Length - 1;
        }

        internal static double LowerUs(long[] buckets, double q)
        {
            int i = BucketOf(buckets, q);
            if (i <= 0) return 0;
            int prev = i - 1;
            return prev < InterruptAttribution.BucketUpperUs.Length
                ? InterruptAttribution.BucketUpperUs[prev] : 0;
        }

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
