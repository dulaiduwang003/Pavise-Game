namespace PaviseApp
{
	internal static class HardThreadCleanupPolicy
	{
		internal static bool ChildSettled(bool spawnSucceeded, HardThreadRead originalChild)
		{
			if (spawnSucceeded)
			{
				return originalChild == HardThreadRead.Gone;
			}
			return true;
		}
	}
}
