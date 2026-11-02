namespace PaviseApp
{
	internal static class QuantumDecoder
	{
		private static readonly int[] ShortVariable = new int[3] { 6, 12, 18 };

		private static readonly int[] ShortFixed = new int[3] { 18, 18, 18 };

		private static readonly int[] LongVariable = new int[3] { 12, 24, 36 };

		private static readonly int[] LongFixed = new int[3] { 36, 36, 36 };

		public static QuantumShape Decode(int raw)
		{
			int num = raw & 0x3F;
			int num2 = (num >> 4) & 3;
			int num3 = (num >> 2) & 3;
			int num4 = num & 3;
			QuantumShape result = default(QuantumShape);
			result.ShortInterval = num2 != 1;
			result.VariableLength = num3 != 2;
			result.RawPrioSep = num4;
			result.PrioSep = ((num4 > 2) ? 2 : num4);
			int[] array = ((!result.ShortInterval) ? (result.VariableLength ? LongVariable : LongFixed) : (result.VariableLength ? ShortVariable : ShortFixed));
			result.ForegroundQuantum = array[result.PrioSep];
			result.BackgroundQuantum = array[0];
			return result;
		}

		public static bool Equivalent(int a, int b)
		{
			return Decode(a).SameAs(Decode(b));
		}
	}
}
