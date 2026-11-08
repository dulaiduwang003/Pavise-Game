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
			string allText = all.ToString("X", CultureInfo.InvariantCulture);
			if (physical != null)
			{
				foreach (ulong numericValueEntry in physical)
				{
					allText = allText + "," + numericValueEntry.ToString("X", CultureInfo.InvariantCulture);
				}
			}
			return allText;
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
			string[] parts = text.Split('|');
			bool allConditionsMet = parts.Length == 5 && parts[0] == "2" && parts[4] == "1";
			if ((parts.Length != 4 || !(parts[0] == "1")) && !allConditionsMet)
			{
				return null;
			}
			ulong value;
			if ((parts[3] != "0" && parts[3] != "1") || !Hex(parts[2], out value) || value == 0)
			{
				return null;
			}
			string[] parts2 = parts[1].Split(',');
			if (parts2.Length < 2 || parts2.Length > 65)
			{
				return null;
			}
			string[] textEntries = parts2;
			foreach (string entryText in textEntries)
			{
				ulong value2;
				if (!Hex(entryText, out value2) || value2 == 0)
				{
					return null;
				}
			}
			return new ManualCorePlan(value, parts[3] == "1", parts[1].ToUpperInvariant(), allConditionsMet);
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
			ulong numericValue = 0uL;
			if (physical == null || physical.Length == 0)
			{
				return "schedule.manual.topology";
			}
			foreach (ulong numericValueEntry in physical)
			{
				if (numericValueEntry == 0 || (numericValueEntry & ~all) != 0 || (numericValue & numericValueEntry) != 0)
				{
					return "schedule.manual.topology";
				}
				numericValue |= numericValueEntry;
			}
			if (numericValue != all)
			{
				return "schedule.manual.topology";
			}
			if ((GameMask & ~all) != 0 || AutoCores.CountBits(GameMask) < 1)
			{
				return "schedule.manual.minimum";
			}
			ulong wholeCoresResult = AutoCores.WholeCores(GameMask, physical);
			if (((Isolation ? wholeCoresResult : GameMask) & allocated) != 0)
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
			if (AutoCores.ValidateRange(wholeCoresResult, GameMask, all, physical, allocated, true) != null)
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
			ulong wholeCoresResult = AutoCores.WholeCores(GameMask, cores);
			AutoCorePlan autoCorePlan = new AutoCorePlan();
			autoCorePlan.Exclusive = (Isolation ? wholeCoresResult : GameMask);
			autoCorePlan.Cores = AutoCores.PhysicalCount(wholeCoresResult, cores);
			autoCorePlan.Logical = AutoCores.CountBits(GameMask);
			autoCorePlan.Allocated = allocated;
			autoCorePlan.Kind = "manual";
			autoCorePlan.Blocked = ValidateCurrent(allocated);
			return autoCorePlan;
		}
	}
}
