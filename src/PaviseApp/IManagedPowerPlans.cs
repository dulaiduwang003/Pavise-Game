using System;

namespace PaviseApp
{
	internal interface IManagedPowerPlans
	{
		Guid? Current();

		bool Exists(Guid scheme);

		bool IsOwned(Guid scheme);

		bool Duplicate(Guid source, out Guid created);

		bool Activate(Guid scheme);

		bool Delete(Guid scheme);

		bool Rename(Guid scheme, string title, string note);

		string Name(Guid scheme);

		Guid[] ProcessorSettings(Guid scheme, Guid processor);

		uint? Read(Guid scheme, Guid sub, Guid setting, bool ac);

		bool Write(Guid scheme, Guid sub, Guid setting, bool ac, uint value);
	}
}
