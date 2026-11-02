namespace PaviseApp
{
	internal enum AcquireResult
	{
		AlreadyThrottled,
		NewlyThrottled,
		NewlyProtected,
		AlreadyProtected,
		ApplyFailed
	}
}
