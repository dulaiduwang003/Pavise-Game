using System;
using System.Collections.Generic;
using System.Text;

namespace PaviseApp
{
	internal static class IrqRelocate
	{
		private static readonly IrqAffinityEngine engine = new IrqAffinityEngine("IrqRelocateOn", "IrqReloc_", Lang.T("irqmove.logprefix"));

		public static bool HasResidue
		{
			get
			{
				return engine.HasResidue;
			}
		}

		internal static IrqRestoreResult RestoreDevices(string selected)
		{
			if (!Native.IsElevated())
			{
				return new IrqRestoreResult();
			}
			IrqRestoreResult irqRestoreResult = IrqRestoreResult.Run(selected, engine.TryTouchedDevices, IrqPriorityTweak.TryTouchedDevices, OwnedByOtherTweaks, (string id) => engine.RestoreOnly(new List<string> { id }), IrqPriorityTweak.RestoreOnly);
			if (selected == null && irqRestoreResult.ScopeReadSucceeded)
			{
				bool restoreDevicesReady = false;
				foreach (IrqDeviceRestoreResult device in irqRestoreResult.Devices)
				{
					restoreDevicesReady |= device.AffinityRequested;
				}
				if (!restoreDevicesReady)
				{
					try
					{
						irqRestoreResult.AffinityFinalized = engine.Disable(null);
					}
					catch
					{
						irqRestoreResult.AffinityFinalized = false;
					}
				}
			}
			return irqRestoreResult;
		}

		public static bool Revert()
		{
			return engine.Disable(null);
		}

		public static void HealFromCrash()
		{
			try
			{
				if (engine.HasResidue && !engine.EnabledByPavise)
				{
					Revert();
				}
			}
			catch
			{
			}
		}

		private static List<string> OwnedByOtherTweaks()
		{
			List<string> list = new List<string>();
			try
			{
				if (NetworkAffinityTweak.EnabledByPavise)
				{
					foreach (string item in NetworkAffinityTweak.EnumerateNicDeviceIds())
					{
						if (!Has(list, item))
						{
							list.Add(item);
						}
					}
				}
			}
			catch
			{
			}
			try
			{
				foreach (string item2 in IrqAutoPilot.TouchedDevices())
				{
					if (!Has(list, item2))
					{
						list.Add(item2);
					}
				}
			}
			catch
			{
			}
			try
			{
				foreach (string item3 in GpuIrqCheck.TouchedDevices())
				{
					if (!Has(list, item3))
					{
						list.Add(item3);
					}
				}
			}
			catch
			{
			}
			return list;
		}

		private static bool Has(List<string> list, string id)
		{
			if (id == null || id.Length == 0)
			{
				return true;
			}
			foreach (string item in list)
			{
				if (string.Equals(item, id, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		internal static string MaskText(ulong mask)
		{
			if (mask == 0)
			{
				return "?";
			}
			StringBuilder stringBuilder = new StringBuilder();
			int maskTextValue = -1;
			for (int i = 0; i <= 64; i++)
			{
				bool allConditionsMet = i < 64 && (mask & (ulong)(1L << i)) != 0;
				if (allConditionsMet && maskTextValue < 0)
				{
					maskTextValue = i;
				}
				else if (!allConditionsMet && maskTextValue >= 0)
				{
					if (stringBuilder.Length > 0)
					{
						stringBuilder.Append(',');
					}
					if (i - 1 == maskTextValue)
					{
						stringBuilder.Append(maskTextValue);
					}
					else
					{
						stringBuilder.Append(maskTextValue).Append('-').Append(i - 1);
					}
					maskTextValue = -1;
				}
			}
			return stringBuilder.ToString();
		}
	}
}
