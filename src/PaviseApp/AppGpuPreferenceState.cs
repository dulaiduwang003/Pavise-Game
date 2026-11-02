namespace PaviseApp
{
	internal enum AppGpuPreferenceState
	{
		Owned,
		AlreadyLowPower,
		ExternalChange,
		PendingApply,
		PendingRestore,
		CleanupPending
	}
}
