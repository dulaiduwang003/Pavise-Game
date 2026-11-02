namespace PaviseApp
{
	internal enum GpuIrqOutcome
	{
		Failed,
		Cancelled,
		AwaitingReboot,
		NoEvidence,
		NoRegion,
		NoTarget,
		Clear,
		Scheduled,
		Verified,
		RolledBack,
		Skipped,
		Restored,
		NeedsPlacement,
		NeedsVerification
	}
}
