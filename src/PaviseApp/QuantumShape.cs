using System.Globalization;

namespace PaviseApp
{
	internal struct QuantumShape
	{
		public bool ShortInterval;

		public bool VariableLength;

		public int RawPrioSep;

		public int PrioSep;

		public int ForegroundQuantum;

		public int BackgroundQuantum;

		public int Ratio
		{
			get
			{
				if (BackgroundQuantum > 0)
				{
					return ForegroundQuantum / BackgroundQuantum;
				}
				return 0;
			}
		}

		public bool SameAs(QuantumShape other)
		{
			if (ShortInterval == other.ShortInterval && VariableLength == other.VariableLength && ForegroundQuantum == other.ForegroundQuantum)
			{
				return BackgroundQuantum == other.BackgroundQuantum;
			}
			return false;
		}

		public string Describe()
		{
			string text = Lang.T(ShortInterval ? "quantum.short" : "quantum.long");
			string text2 = Lang.T(VariableLength ? "quantum.variable" : "quantum.fixed");
			return text + " " + text2 + " " + ForegroundQuantum.ToString(CultureInfo.InvariantCulture) + ":" + BackgroundQuantum.ToString(CultureInfo.InvariantCulture);
		}
	}
}
