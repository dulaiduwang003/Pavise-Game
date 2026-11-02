namespace PaviseApp
{
	internal static class SuppressionLevelText
	{
		public static string Of(SuppressionLevel level)
		{
			switch (level)
			{
			case SuppressionLevel.Eco:
				return Lang.T("t.backgroundpressurecontroller.5");
			case SuppressionLevel.Restrained:
				return Lang.T("t.backgroundpressurecontroller.6");
			case SuppressionLevel.Isolated:
				return Lang.T("t.backgroundpressurecontroller.7");
			default:
				return Lang.T("t.backgroundpressurecontroller.9");
			}
		}
	}
}
