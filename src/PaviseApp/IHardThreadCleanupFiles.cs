namespace PaviseApp
{
	internal interface IHardThreadCleanupFiles
	{
		bool OriginalSourceReady { get; }

		bool ClosureCertain { get; }

		bool DeleteExact(HardThreadCleanupPart part);

		bool HeldElsewhere(HardThreadCleanupPart part);
	}
}
