namespace PaviseApp
{
	internal static class BackgroundFreezePolicy
	{
		internal static bool Eligible(PerformancePreset mode, int pid, long creation, string path, int session, int ownerSession, bool protectedFamily, bool service, string windows, bool approved = false)
		{
			if (!PresetValue.IsValid((int)mode))
			{
				return false;
			}
			if (approved && mode == PerformancePreset.Extreme && pid > 4 && creation > 0 && ownerSession > 0 && session == ownerSession && !protectedFamily && !service && !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(windows))
			{
				return !FamilyBoundary.UnderRoot(path, windows);
			}
			return false;
		}
	}
}
