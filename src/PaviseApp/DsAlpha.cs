namespace PaviseApp
{
	internal static class DsAlpha
	{
		public const int VeilButton = 158;

		public const int VeilToggle = 105;

		public const int VeilTierLight = 104;

		public const int VeilTierDark = 138;

		public const int PressDarken = 45;

		public const int HoverLight = 16;

		public const int KnobShadow = 70;

		public const int BusyRail = 110;

		public const int BusySweep = 235;

		public static int VeilTier
		{
			get
			{
				if (!Theme.LightMode)
				{
					return 138;
				}
				return 104;
			}
		}
	}
}
