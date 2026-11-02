using System;

namespace PaviseApp
{
	internal sealed class CacheWarmEnvironment
	{
		internal bool Ac;

		internal ulong Total;

		internal ulong Available;

		internal bool Ready
		{
			get
			{
				if (Ac && Total != 0 && Available >= 4294967296L)
				{
					return Available >= Total / 4;
				}
				return false;
			}
		}

		internal long Budget
		{
			get
			{
				if (Ready)
				{
					return (long)Math.Min(2147483648uL, Math.Min(Total / 16, (Available - 4294967296L) / 2));
				}
				return 0L;
			}
		}
	}
}
