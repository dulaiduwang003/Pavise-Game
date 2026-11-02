using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class ManagedPowerPlanPolicy
	{
		internal static bool HasManagedName(string name, string signature)
		{
			if (!(name == "由软件调度 " + signature))
			{
				return name == "Scheduled by Pavise " + signature;
			}
			return true;
		}

		internal static Guid ChooseBase(Guid? current, Guid previous, Guid balanced, IManagedPowerPlans plans)
		{
			if (current.HasValue && current.Value != Guid.Empty && plans.Exists(current.Value) && !plans.IsOwned(current.Value))
			{
				return current.Value;
			}
			if (previous != Guid.Empty && plans.Exists(previous) && !plans.IsOwned(previous))
			{
				return previous;
			}
			if (!plans.Exists(balanced) || plans.IsOwned(balanced))
			{
				return Guid.Empty;
			}
			return balanced;
		}

		internal static bool WriteVerified(IManagedPowerPlans plans, Guid scheme, Guid sub, Guid setting, bool ac, uint value)
		{
			uint? num = plans.Read(scheme, sub, setting, ac);
			if (!num.HasValue)
			{
				return false;
			}
			if (num.Value == value)
			{
				return true;
			}
			if (plans.Write(scheme, sub, setting, ac, value))
			{
				return plans.Read(scheme, sub, setting, ac) == value;
			}
			return false;
		}

		internal static bool ParkingDisabled(uint? ac, uint? dc, uint? ac1, uint? dc1, bool onAc, bool secondaryRequired, out bool disabled)
		{
			disabled = false;
			uint? num = (onAc ? ac : dc);
			uint? num2 = (onAc ? ac1 : dc1);
			if (!num.HasValue || num.Value > 100 || (secondaryRequired && (!num2.HasValue || num2.Value > 100)))
			{
				return false;
			}
			disabled = num.Value == 100 && (!secondaryRequired || num2 == 100);
			return true;
		}
	}
}
