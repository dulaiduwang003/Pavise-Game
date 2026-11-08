using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class MemShield
	{
		private struct ProcessMemoryCounters
		{
			public uint Size;

			public uint PageFaultCount;

			public IntPtr PeakWorkingSetSize;

			public IntPtr WorkingSetSize;

			public IntPtr QuotaPeakPagedPoolUsage;

			public IntPtr QuotaPagedPoolUsage;

			public IntPtr QuotaPeakNonPagedPoolUsage;

			public IntPtr QuotaNonPagedPoolUsage;

			public IntPtr PagefileUsage;

			public IntPtr PeakPagefileUsage;
		}

		internal const string SnapKey = "MemShieldSnap";

		internal const uint HardMinEnable = 1u;

		private static readonly object opLk = new object();

		private static readonly object lk = new object();

		private static bool recoveryBlocked;

		public static bool HealFromCrash()
		{
			lock (opLk)
			{
				string memShieldSnapValue = Settings.LoadStr("MemShieldSnap", "");
				if (memShieldSnapValue.Length != 0)
				{
					int pid;
					long creation;
					ulong min;
					ulong max;
					uint flags;
					if (DecodeSnapshot(memShieldSnapValue, out pid, out creation, out min, out max, out flags))
					{
						bool restoreQuotaSucceeded;
						try
						{
							restoreQuotaSucceeded = RestoreQuota(pid, creation, min, max, flags);
						}
						catch
						{
							restoreQuotaSucceeded = false;
						}
						if (restoreQuotaSucceeded)
						{
							if (!Settings.SaveStr("MemShieldSnap", "") || Settings.LoadStr("MemShieldSnap", "").Length != 0)
							{
								lock (lk)
								{
									recoveryBlocked = true;
								}
								return false;
							}
							lock (lk)
							{
								recoveryBlocked = false;
							}
							return true;
						}
						lock (lk)
						{
							recoveryBlocked = true;
						}
						return false;
					}
					lock (lk)
					{
						recoveryBlocked = true;
					}
					return false;
				}
				lock (lk)
				{
					recoveryBlocked = false;
				}
				return true;
			}
		}

		public static bool HasResidue()
		{
			lock (lk)
			{
				if (recoveryBlocked)
				{
					return true;
				}
			}
			return Settings.LoadStr("MemShieldSnap", "").Length != 0;
		}

		internal static string EncodeSnapshot(int pid, long creation, ulong min, ulong max, uint flags)
		{
			return "1|" + pid.ToString(CultureInfo.InvariantCulture) + "|" + creation.ToString(CultureInfo.InvariantCulture) + "|" + min.ToString(CultureInfo.InvariantCulture) + "|" + max.ToString(CultureInfo.InvariantCulture) + "|" + flags.ToString(CultureInfo.InvariantCulture);
		}

		internal static bool DecodeSnapshot(string snap, out int pid, out long creation, out ulong min, out ulong max, out uint flags)
		{
			pid = 0;
			creation = 0L;
			min = 0uL;
			max = 0uL;
			flags = 0u;
			string[] parts = (snap ?? "").Split('|');
			if (parts.Length == 6 && parts[0] == "1" && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out creation) && ulong.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out min) && ulong.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out max) && uint.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out flags) && pid > 0)
			{
				return creation > 0;
			}
			return false;
		}

		private static bool RestoreQuota(int pid, long creation, ulong min, ulong max, uint flags)
		{
			if (pid <= 0 || creation <= 0)
			{
				return false;
			}
			bool gone;
			long creation2;
			ulong workingSet;
			ulong min2;
			ulong max2;
			uint flags2;
			if (!TryQueryTarget(pid, out gone, out creation2, out workingSet, out min2, out max2, out flags2))
			{
				return gone;
			}
			if (creation2 != creation)
			{
				return true;
			}
			if ((flags2 & 1) == 0)
			{
				return true;
			}
			TrySetTarget(pid, creation, min, max, flags);
			if (!TryQueryTarget(pid, out gone, out creation2, out workingSet, out min2, out max2, out flags2))
			{
				return gone;
			}
			if (creation2 == creation)
			{
				return (flags2 & 1) == 0;
			}
			return true;
		}

		private static bool TryQueryTarget(int pid, out bool gone, out long creation, out ulong workingSet, out ulong min, out ulong max, out uint flags)
		{
			gone = false;
			creation = 0L;
			workingSet = 0uL;
			min = 0uL;
			max = 0uL;
			flags = 0u;
			IntPtr intPtr = Native.OpenProcess(4352, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				gone = Native.LastOpenProcessFailureWasNoSuchProcess();
				return false;
			}
			try
			{
				long cpu;
				ulong io;
				if (!Native.QueryProcessSample(intPtr, out creation, out cpu, out io))
				{
					return false;
				}
				IntPtr minimum;
				IntPtr maximum;
				uint flags2;
				if (!GetProcessWorkingSetSizeEx(intPtr, out minimum, out maximum, out flags2))
				{
					return false;
				}
				min = (ulong)minimum.ToInt64();
				max = (ulong)maximum.ToInt64();
				flags = flags2;
				ProcessMemoryCounters processMemoryCounters = default(ProcessMemoryCounters);
				processMemoryCounters.Size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters));
				ProcessMemoryCounters counters = processMemoryCounters;
				if (!GetProcessMemoryInfo(intPtr, out counters, counters.Size))
				{
					return false;
				}
				workingSet = (ulong)counters.WorkingSetSize.ToInt64();
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private static void TrySetTarget(int pid, long creation, ulong min, ulong max, uint flags)
		{
			IntPtr intPtr = Native.OpenProcess(4352, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return;
			}
			try
			{
				long creation2;
				long cpu;
				ulong io;
				if (Native.QueryProcessSample(intPtr, out creation2, out cpu, out io) && creation2 == creation)
				{
					SetProcessWorkingSetSizeEx(intPtr, new IntPtr((long)min), new IntPtr((long)max), flags);
				}
			}
			catch
			{
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetProcessWorkingSetSizeEx(IntPtr process, out IntPtr minimum, out IntPtr maximum, out uint flags);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, IntPtr minimum, IntPtr maximum, uint flags);

		[DllImport("psapi.dll", SetLastError = true)]
		private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);
	}
}
