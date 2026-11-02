namespace PaviseApp
{
	internal static class DsSurfaces
	{
		public static int CodeAlpha
		{
			get
			{
				if (!Theme.LightMode)
				{
					return 42;
				}
				return 66;
			}
		}

		public static DsSurfaceProfile Profile(bool navigation)
		{
			bool lightMode = Theme.LightMode;
			DsSurfaceProfile result = default(DsSurfaceProfile);
			result.Navigation = navigation;
			result.MeshAlpha = ((!navigation) ? (lightMode ? 8 : 5) : (lightMode ? 13 : 8));
			result.FaceAlpha = ((!navigation) ? (lightMode ? 8 : 5) : (lightMode ? 14 : 9));
			result.EdgeAlpha = ((!navigation) ? (lightMode ? 25 : 17) : (lightMode ? 44 : 30));
			result.MeshStep = (navigation ? 17 : 22);
			result.BandThickness = (navigation ? 15 : 20);
			result.SweepDivisor = (navigation ? 2 : 5);
			result.BusInset = (navigation ? 17 : 24);
			result.BusTop = (navigation ? 96 : 24);
			result.BusBottom = 34;
			result.TickStep = (navigation ? 25 : 31);
			result.BusLevels = ((!navigation) ? new int[3] { 18, 48, 78 } : new int[4] { 15, 35, 57, 79 });
			result.BusReach = ((!navigation) ? new int[3] { 92, 146, 92 } : new int[4] { 56, 82, 56, 82 });
			result.CornerCut = (navigation ? 28 : 42);
			result.CornerReach = (navigation ? 66 : 104);
			result.Code = (navigation ? "" : "PAVISE // CONTROL SURFACE");
			result.CodeRight = (navigation ? 142 : 220);
			result.CodeWidth = (navigation ? 116 : 192);
			result.CodeMinHeight = ((!navigation) ? 120 : 0);
			return result;
		}
	}
}
