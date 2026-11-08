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
			int rawMask = raw & 0x3F;
			int combinedMask = (rawMask >> 4) & 3;
			int combinedMask2 = (rawMask >> 2) & 3;
			int rawPrioSepValue = rawMask & 3;
			QuantumShape result = default(QuantumShape);
			result.ShortInterval = combinedMask != 1;
			result.VariableLength = combinedMask2 != 2;
			result.RawPrioSep = rawPrioSepValue;
			result.PrioSep = ((rawPrioSepValue > 2) ? 2 : rawPrioSepValue);
			int[] longVariableSnapshot = ((!result.ShortInterval) ? (result.VariableLength ? LongVariable : LongFixed) : (result.VariableLength ? ShortVariable : ShortFixed));
			result.ForegroundQuantum = longVariableSnapshot[result.PrioSep];
			result.BackgroundQuantum = longVariableSnapshot[0];
			return result;
		}

		public static bool Equivalent(int a, int b)
		{
			return Decode(a).SameAs(Decode(b));
		}
	}
}
