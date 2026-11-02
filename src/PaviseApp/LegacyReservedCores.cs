using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class LegacyReservedCores
	{
		private const string KeyPath = "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel";

		private const string ValueName = "ReservedCpuSets";

		private const string OnKey = "ReservedCoresByPavise";

		private const string SnapKey = "PrevReservedCpuSets";

		internal const string ClearedKey = "LegacyReservedCoresClearedV1";

		internal const string SuspectKey = "LegacyReservedCoresSuspectV1";

		internal const int MinPhysicalCores = 8;

		internal static ulong LegacyMask(ulong[] physicalCores, ulong perfMask, bool hybrid, ulong allMask, ulong strictGame)
		{
			if (physicalCores == null || physicalCores.Length < 8 || allMask == 0)
			{
				return 0uL;
			}
			ulong num = 0uL;
			int num2 = 0;
			foreach (ulong num3 in physicalCores)
			{
				if (num3 != 0 && (num3 & 1) == 0 && (!hybrid || (num3 & perfMask) != 0))
				{
					num |= num3;
					if (++num2 == 2)
					{
						break;
					}
				}
			}
			if (num2 < 2)
			{
				return 0uL;
			}
			if (strictGame != 0 && (num & ~strictGame) != 0)
			{
				return 0uL;
			}
			if (AutoCores.CountBits(allMask & ~num) * 2 < AutoCores.CountBits(allMask))
			{
				return 0uL;
			}
			return num;
		}

		internal static ulong BytesMask(byte[] bytes)
		{
			if (bytes == null)
			{
				return 0uL;
			}
			ulong num = 0uL;
			for (int i = 0; i < bytes.Length && i < 8; i++)
			{
				num |= (ulong)bytes[i] << i * 8;
			}
			return num;
		}

		private static bool SameBytes(byte[] a, byte[] b)
		{
			if (a == null || b == null)
			{
				return a == b;
			}
			if (a.Length != b.Length)
			{
				return false;
			}
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i])
				{
					return false;
				}
			}
			return true;
		}

		private static byte[] ReadRaw()
		{
			using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel"))
			{
				return (registryKey == null) ? null : (registryKey.GetValue("ReservedCpuSets") as byte[]);
			}
		}

		private static bool WriteRaw(byte[] value)
		{
			using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel", true))
			{
				if (registryKey == null)
				{
					return value == null;
				}
				if (value == null)
				{
					registryKey.DeleteValue("ReservedCpuSets", false);
				}
				else
				{
					registryKey.SetValue("ReservedCpuSets", value, RegistryValueKind.Binary);
				}
			}
			byte[] a = ReadRaw();
			return SameBytes(a, value);
		}

		internal static bool TryDecodeSnapshot(string snap, out byte[] bytes)
		{
			bytes = null;
			if (string.IsNullOrEmpty(snap))
			{
				return false;
			}
			if (snap == "__pavise_absent__")
			{
				return true;
			}
			string[] array = snap.Split('-');
			if (array.Length == 0 || array.Length > 64)
			{
				return false;
			}
			byte[] array2 = new byte[array.Length];
			for (int i = 0; i < array.Length; i++)
			{
				if (array[i].Length != 2 || !byte.TryParse(array[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out array2[i]))
				{
					return false;
				}
			}
			bytes = array2;
			return true;
		}

		private static ulong CurrentLegacyMask()
		{
			return LegacyMask(CpuTopology.PhysicalCoreMasks(), CpuTopology.PerfMask, CpuTopology.Hybrid, CpuTopology.AllMask, CpuTopology.StrictBoostMask);
		}

		internal static bool Heal()
		{
			if (CpuTopology.MultiGroup)
			{
				return true;
			}
			string text = Settings.LoadStr("PrevReservedCpuSets", "");
			bool flag = Settings.Load("ReservedCoresByPavise", false) || text.Length > 0;
			byte[] array;
			try
			{
				array = ReadRaw();
			}
			catch
			{
				return false;
			}
			ulong num = BytesMask(array);
			ulong num2 = CurrentLegacyMask();
			if (!flag)
			{
				if (array != null && array.Length == 8 && num != 0 && num == num2)
				{
					Settings.SaveStr("LegacyReservedCoresSuspectV1", num.ToString("X"));
				}
				else
				{
					Settings.Remove("LegacyReservedCoresSuspectV1");
				}
				return true;
			}
			Settings.Remove("LegacyReservedCoresSuspectV1");
			byte[] bytes;
			if (!TryDecodeSnapshot(text, out bytes))
			{
				Logger.Warn(Lang.F("log.autocore.legacycorrupt", "0x" + num.ToString("X")));
				return false;
			}
			ulong num3 = BytesMask(bytes);
			if (!SameBytes(array, bytes))
			{
				if (array == null || array.Length != 8 || num != num2)
				{
					Logger.Log(Lang.F("log.autocore.legacychanged", "0x" + num.ToString("X")));
					ForgetReceipt();
					return true;
				}
				bool flag2;
				try
				{
					flag2 = WriteRaw(bytes);
				}
				catch
				{
					flag2 = false;
				}
				if (!flag2)
				{
					Logger.Warn(Lang.F("log.autocore.legacyfailed", "0x" + num.ToString("X")));
					return false;
				}
				RecordCleared(num & ~num3);
			}
			ForgetReceipt();
			return true;
		}

		internal static ulong Suspect()
		{
			ulong mask;
			if (!ParseMask(Settings.LoadStr("LegacyReservedCoresSuspectV1", ""), out mask))
			{
				return 0uL;
			}
			return mask;
		}

		internal static bool ClearSuspect()
		{
			ulong num = Suspect();
			if (num == 0)
			{
				return false;
			}
			byte[] array;
			try
			{
				array = ReadRaw();
			}
			catch
			{
				return false;
			}
			if (array == null || array.Length != 8 || BytesMask(array) != num)
			{
				Settings.Remove("LegacyReservedCoresSuspectV1");
				return false;
			}
			bool flag;
			try
			{
				flag = WriteRaw(null);
			}
			catch
			{
				flag = false;
			}
			if (!flag)
			{
				Logger.Warn(Lang.F("log.autocore.legacyfailed", "0x" + num.ToString("X")));
				return false;
			}
			Settings.Remove("LegacyReservedCoresSuspectV1");
			RecordCleared(num);
			return true;
		}

		private static void RecordCleared(ulong freed)
		{
			if (freed != 0)
			{
				Settings.SaveStr("LegacyReservedCoresClearedV1", freed.ToString("X"));
				Logger.Log(Lang.F("log.autocore.legacycleared", CpuTopology.DescribeMask(freed)));
			}
		}

		private static void ForgetReceipt()
		{
			Settings.Remove("ReservedCoresByPavise");
			Settings.Remove("PrevReservedCpuSets");
		}

		private static bool ParseMask(string raw, out ulong mask)
		{
			mask = 0uL;
			if (raw.Length > 0 && ulong.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask))
			{
				return mask != 0;
			}
			return false;
		}

		internal static ulong PendingReboot(ulong liveAllocated)
		{
			ulong mask;
			if (!ParseMask(Settings.LoadStr("LegacyReservedCoresClearedV1", ""), out mask))
			{
				return 0uL;
			}
			if ((mask & liveAllocated) == 0)
			{
				Settings.Remove("LegacyReservedCoresClearedV1");
				return 0uL;
			}
			return mask & liveAllocated;
		}
	}
}
