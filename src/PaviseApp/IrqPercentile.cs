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
			long num = 0L;
			foreach (long num2 in buckets)
			{
				num += num2;
			}
			if (num <= 0)
			{
				return maxUs;
			}
			long num3 = (long)Math.Ceiling((double)num * q);
			long num4 = 0L;
			for (int j = 0; j < buckets.Length; j++)
			{
				num4 += buckets[j];
				if (num4 >= num3)
				{
					double num5 = ((j < InterruptAttribution.BucketUpperUs.Length) ? InterruptAttribution.BucketUpperUs[j] : double.MaxValue);
					if (!double.IsInfinity(num5) && num5 != double.MaxValue)
					{
						return Math.Min(num5, maxUs);
					}
					return maxUs;
				}
			}
			return maxUs;
		}
	}
}
