namespace PaviseApp
{
	internal enum HardThreadPhase
	{
		ApplyIntent,
		PreviousRecorded,
		Applied,
		RestoreIntent,
		RestorePreviousRecorded,
		Restored,
		Gone,
		Conflict,
		NotApplied
	}
}
