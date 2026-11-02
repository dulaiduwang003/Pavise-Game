namespace PaviseApp
{
	internal enum StandbyCleanerResult
	{
		Cancelled,
		BelowThreshold,
		Purged,
		QueryFailed,
		PurgeFailed,
		InvalidOptions
	}
}
