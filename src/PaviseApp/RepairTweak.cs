using System;

namespace PaviseApp
{
	internal sealed class RepairTweak
	{
		public readonly string Key;

		public readonly Func<bool> NeedsRepair;

		public readonly Func<bool> Repair;

		public readonly Func<bool> Restore;

		public readonly Func<string> Describe;

		public readonly Func<bool> RepairedByPavise;

		public RepairTweak(string key, Func<bool> needsRepair, Func<bool> repair, Func<bool> restore, Func<string> describe, Func<bool> repairedByPavise)
		{
			Key = key;
			NeedsRepair = needsRepair;
			Repair = repair;
			Restore = restore;
			Describe = describe;
			RepairedByPavise = repairedByPavise;
		}
	}
}
