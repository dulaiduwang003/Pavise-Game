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
			long numericValue = 0L;
			foreach (long numericValueEntry in buckets)
			{
				numericValue += numericValueEntry;
			}
			if (numericValue <= 0)
			{
				return maxUs;
			}
			long roundedValue = (long)Math.Ceiling((double)numericValue * q);
			long numericValue2 = 0L;
			for (int j = 0; j < buckets.Length; j++)
			{
				numericValue2 += buckets[j];
				if (numericValue2 >= roundedValue)
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
