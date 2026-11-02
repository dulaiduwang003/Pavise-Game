using System;
using System.Globalization;

namespace PaviseApp
{
	internal sealed class ManualCorePlan
	{
		internal const string Key = "GmManualCorePlanV2";

		internal const int MinLogical = 1;

		internal readonly ulong GameMask;

		internal readonly bool Isolation;

		internal readonly bool HardAffinity;

		internal readonly string Topology;

		internal ManualCorePlan(ulong gameMask, bool isolation, string topology, bool hardAffinity = false)
		{
			GameMask = gameMask;
			Isolation = isolation;
			Topology = topology;
			HardAffinity = hardAffinity;
		}

		internal string Encode()
		{
			return (HardAffinity ? "2|" : "1|") + Topology + "|" + GameMask.ToString("X", CultureInfo.InvariantCulture) + "|" + (Isolation ? "1" : "0") + (HardAffinity ? "|1" : "");
		}

		internal static string Stamp(ulong all, ulong[] physical)
		{
			string text = all.ToString("X", CultureInfo.InvariantCulture);
			if (physical != null)
			{
				foreach (ulong num in physical)
				{
					text = text + "," + num.ToString("X", CultureInfo.InvariantCulture);
				}
			}
			return text;
		}

		internal static string CurrentStamp()
		{
			return Stamp(CpuTopology.AllMask, CpuTopology.PhysicalCoreMasks());
		}

		internal static ManualCorePlan Parse(string text)
		{
			if (string.IsNullOrEmpty(text) || text.Length > 1200)
			{
				return null;
			}
			string[] array = text.Split('|');
			bool flag = array.Length == 5 && array[0] == "2" && array[4] == "1";
			if ((array.Length != 4 || !(array[0] == "1")) && !flag)
			{
				return null;
			}
			ulong value;
			if ((array[3] != "0" && array[3] != "1") || !Hex(array[2], out value) || value == 0)
			{
				return null;
			}
			string[] array2 = array[1].Split(',');
			if (array2.Length < 2 || array2.Length > 65)
			{
				return null;
			}
			string[] array3 = array2;
			foreach (string text2 in array3)
			{
				ulong value2;
				if (!Hex(text2, out value2) || value2 == 0)
				{
					return null;
				}
			}
			return new ManualCorePlan(value, array[3] == "1", array[1].ToUpperInvariant(), flag);
		}

		private static bool Hex(string text, out ulong value)
		{
			value = 0uL;
			if (text.Length > 0 && text.Length <= 16)
			{
				return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
			}
			return false;
		}

		internal static string Raw(GameProfile profile)
		{
			string value;
			if (profile == null || !profile.Overrides.TryGetValue("GmManualCorePlanV2", out value))
			{
				return null;
			}
			return value;
		}

		internal string Validate(ulong all, ulong[] physical, ulong allocated, bool supported, bool topologyTrusted)
		{
			if (!topologyTrusted || Topology != Stamp(all, physical))
			{
				return "schedule.manual.topology";
			}
			ulong num = 0uL;
			if (physical == null || physical.Length == 0)
			{
				return "schedule.manual.topology";
			}
			foreach (ulong num2 in physical)
			{
				if (num2 == 0 || (num2 & ~all) != 0 || (num & num2) != 0)
				{
					return "schedule.manual.topology";
				}
				num |= num2;
			}
			if (num != all)
			{
				return "schedule.manual.topology";
			}
			if ((GameMask & ~all) != 0 || AutoCores.CountBits(GameMask) < 1)
			{
				return "schedule.manual.minimum";
			}
			ulong num3 = AutoCores.WholeCores(GameMask, physical);
			if (((Isolation ? num3 : GameMask) & allocated) != 0)
			{
				return "schedule.manual.allocated";
			}
			if (!Isolation)
			{
				return null;
			}
			if (!supported)
			{
				return "schedule.auto.unsupported";
			}
			if (allocated != 0)
			{
				return "schedule.manual.allocated";
			}
			if (AutoCores.ValidateRange(num3, GameMask, all, physical, allocated, true) != null)
			{
				return "schedule.manual.spare";
			}
			return null;
		}

		internal string ValidateCurrent(ulong allocated)
		{
			return Validate(CpuTopology.AllMask, CpuTopology.PhysicalCoreMasks(), allocated, AutoCores.Supported, IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled);
		}

		internal AutoCorePlan RuntimePlan()
		{
			ulong allocated = AutoCores.LiveAllocated();
			ulong[] cores = CpuTopology.PhysicalCoreMasks();
			ulong num = AutoCores.WholeCores(GameMask, cores);
			AutoCorePlan autoCorePlan = new AutoCorePlan();
			autoCorePlan.Exclusive = (Isolation ? num : GameMask);
			autoCorePlan.Cores = AutoCores.PhysicalCount(num, cores);
			autoCorePlan.Logical = AutoCores.CountBits(GameMask);
			autoCorePlan.Allocated = allocated;
			autoCorePlan.Kind = "manual";
			autoCorePlan.Blocked = ValidateCurrent(allocated);
			return autoCorePlan;
		}
	}
}
