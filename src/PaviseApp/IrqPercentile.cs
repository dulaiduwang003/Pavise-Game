using System;

namespace PaviseApp
{
    internal static class IrqPercentile
    {
        internal static double ApproxUs(long[] buckets, double maxUs, double q)
        {
            if (buckets == null || buckets.Length == 0)
            {
                return maxUs;
            }
            long sampleCount = 0L;
            foreach (long bucket in buckets)
            {
                sampleCount += bucket;
            }
            if (sampleCount <= 0)
            {
                return maxUs;
            }
            long roundedValue = (long)Math.Ceiling((double)sampleCount * q);
            long cumulativeSamples = 0L;
            for (int j = 0; j < buckets.Length; j++)
            {
                cumulativeSamples += buckets[j];
                if (cumulativeSamples >= roundedValue)
                {
                    double maxValue = ((j < InterruptAttribution.BucketUpperUs.Length) ? InterruptAttribution.BucketUpperUs[j] : double.MaxValue);
                    if (!double.IsInfinity(maxValue) && maxValue != double.MaxValue)
                    {
                        return Math.Min(maxValue, maxUs);
                    }
                    return maxUs;
                }
            }
            return maxUs;
        }
    }
}
