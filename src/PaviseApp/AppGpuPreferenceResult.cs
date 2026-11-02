namespace PaviseApp
{
	internal enum AppGpuPreferenceResult
	{
		Success,
		AlreadyPresent,
		NeedsConfirmation,
		Unsupported,
		InvalidPath,
		Changed,
		Busy,
		ReadFailed,
		WriteFailed,
		JournalFailed,
		RecoveryPending,
		NotFound
	}
}
