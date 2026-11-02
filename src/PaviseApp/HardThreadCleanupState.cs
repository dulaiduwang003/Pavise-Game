using System;

namespace PaviseApp
{
	internal sealed class HardThreadCleanupState
	{
		private readonly bool[] removed = new bool[6];

		private readonly bool[] deferred = new bool[6];

		internal bool Removed(HardThreadCleanupPart part)
		{
			return removed[(int)part];
		}

		internal bool Deferred(HardThreadCleanupPart part)
		{
			return deferred[(int)part];
		}

		internal bool Step(IHardThreadCleanupFiles original)
		{
			if (original == null || !original.OriginalSourceReady)
			{
				return false;
			}
			foreach (HardThreadCleanupPart value in Enum.GetValues(typeof(HardThreadCleanupPart)))
			{
				int num = (int)value;
				if (removed[num] || deferred[num])
				{
					continue;
				}
				if (value == HardThreadCleanupPart.Directory && Array.IndexOf(deferred, true) >= 0)
				{
					deferred[num] = true;
					continue;
				}
				if (original.DeleteExact(value))
				{
					removed[num] = true;
					continue;
				}
				if (value < HardThreadCleanupPart.Configuration || !original.HeldElsewhere(value))
				{
					return false;
				}
				deferred[num] = true;
			}
			return original.ClosureCertain;
		}
	}
}
