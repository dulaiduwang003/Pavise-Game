using System;

namespace PaviseApp
{
	internal sealed class CoreWorkBudget
	{
		internal long TotalTicks;

		internal double MaxMs;

		internal double TotalMs
		{
			get
			{
				return (double)TotalTicks / 10000.0;
			}
		}

		internal void Record(long start)
		{
			long num = Math.Max(0L, AutoCores.NowTicks - start);
			TotalTicks += num;
			MaxMs = Math.Max(MaxMs, (double)num / 10000.0);
		}
	}
}
