using System;

namespace PaviseApp
{
	internal sealed class PowerPlanEntry
	{
		public readonly Guid Id;

		public readonly string Name;

		public PowerPlanEntry(Guid id, string name)
		{
			Id = id;
			Name = name;
		}
	}
}
