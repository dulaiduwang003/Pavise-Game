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
				int valueSnapshot = (int)value;
				if (removed[valueSnapshot] || deferred[valueSnapshot])
				{
					continue;
				}
				if (value == HardThreadCleanupPart.Directory && Array.IndexOf(deferred, true) >= 0)
				{
					deferred[valueSnapshot] = true;
					continue;
				}
				if (original.DeleteExact(value))
				{
					removed[valueSnapshot] = true;
					continue;
				}
				if (value < HardThreadCleanupPart.Configuration || !original.HeldElsewhere(value))
				{
					return false;
				}
				deferred[valueSnapshot] = true;
			}
			return original.ClosureCertain;
		}
	}
}
