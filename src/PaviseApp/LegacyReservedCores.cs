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
			ulong legacyMaskResult = 0uL;
			int legacyMaskValue = 0;
			foreach (ulong numericValueEntry in physicalCores)
			{
				if (numericValueEntry != 0 && (numericValueEntry & 1) == 0 && (!hybrid || (numericValueEntry & perfMask) != 0))
				{
					legacyMaskResult |= numericValueEntry;
					if (++legacyMaskValue == 2)
					{
						break;
					}
				}
			}
			if (legacyMaskValue < 2)
			{
				return 0uL;
			}
			if (strictGame != 0 && (legacyMaskResult & ~strictGame) != 0)
			{
				return 0uL;
			}
			if (AutoCores.CountBits(allMask & ~legacyMaskResult) * 2 < AutoCores.CountBits(allMask))
			{
				return 0uL;
			}
			return legacyMaskResult;
		}

		internal static ulong BytesMask(byte[] bytes)
		{
			if (bytes == null)
			{
				return 0uL;
			}
			ulong bytesMaskResult = 0uL;
			for (int i = 0; i < bytes.Length && i < 8; i++)
			{
				bytesMaskResult |= (ulong)bytes[i] << i * 8;
			}
			return bytesMaskResult;
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
			string[] snapParts = snap.Split('-');
			if (snapParts.Length == 0 || snapParts.Length > 64)
			{
				return false;
			}
			byte[] rawBytes = new byte[snapParts.Length];
			for (int i = 0; i < snapParts.Length; i++)
			{
				if (snapParts[i].Length != 2 || !byte.TryParse(snapParts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rawBytes[i]))
				{
					return false;
				}
			}
			bytes = rawBytes;
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
			string prevReservedCpuSetsValue = Settings.LoadStr("PrevReservedCpuSets", "");
			bool matchesLoad = Settings.Load("ReservedCoresByPavise", false) || prevReservedCpuSetsValue.Length > 0;
			byte[] raw;
			try
			{
				raw = ReadRaw();
			}
			catch
			{
				return false;
			}
			ulong bytesMaskResult = BytesMask(raw);
			ulong currentLegacyMaskResult = CurrentLegacyMask();
			if (!matchesLoad)
			{
				if (raw != null && raw.Length == 8 && bytesMaskResult != 0 && bytesMaskResult == currentLegacyMaskResult)
				{
					Settings.SaveStr("LegacyReservedCoresSuspectV1", bytesMaskResult.ToString("X"));
				}
				else
				{
					Settings.Remove("LegacyReservedCoresSuspectV1");
				}
				return true;
			}
			Settings.Remove("LegacyReservedCoresSuspectV1");
			byte[] bytes;
			if (!TryDecodeSnapshot(prevReservedCpuSetsValue, out bytes))
			{
				Logger.Warn(Lang.F("log.autocore.legacycorrupt", "0x" + bytesMaskResult.ToString("X")));
				return false;
			}
			ulong bytesMaskResult2 = BytesMask(bytes);
			if (!SameBytes(raw, bytes))
			{
				if (raw == null || raw.Length != 8 || bytesMaskResult != currentLegacyMaskResult)
				{
					Logger.Log(Lang.F("log.autocore.legacychanged", "0x" + bytesMaskResult.ToString("X")));
					ForgetReceipt();
					return true;
				}
				bool writeRawSucceeded;
				try
				{
					writeRawSucceeded = WriteRaw(bytes);
				}
				catch
				{
					writeRawSucceeded = false;
				}
				if (!writeRawSucceeded)
				{
					Logger.Warn(Lang.F("log.autocore.legacyfailed", "0x" + bytesMaskResult.ToString("X")));
					return false;
				}
				RecordCleared(bytesMaskResult & ~bytesMaskResult2);
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
			ulong suspectResult = Suspect();
			if (suspectResult == 0)
			{
				return false;
			}
			byte[] raw;
			try
			{
				raw = ReadRaw();
			}
			catch
			{
				return false;
			}
			if (raw == null || raw.Length != 8 || BytesMask(raw) != suspectResult)
			{
				Settings.Remove("LegacyReservedCoresSuspectV1");
				return false;
			}
			bool writeRawSucceeded;
			try
			{
				writeRawSucceeded = WriteRaw(null);
			}
			catch
			{
				writeRawSucceeded = false;
			}
			if (!writeRawSucceeded)
			{
				Logger.Warn(Lang.F("log.autocore.legacyfailed", "0x" + suspectResult.ToString("X")));
				return false;
			}
			Settings.Remove("LegacyReservedCoresSuspectV1");
			RecordCleared(suspectResult);
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
