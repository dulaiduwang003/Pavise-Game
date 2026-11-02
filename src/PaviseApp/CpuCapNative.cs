using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class CpuCapNative
	{
		private struct BasicLimits
		{
			public long ProcessTime;

			public long JobTime;

			public uint Flags;

			public UIntPtr MinWorkingSet;

			public UIntPtr MaxWorkingSet;

			public uint ActiveProcesses;

			public UIntPtr Affinity;

			public uint Priority;

			public uint Scheduling;
		}

		private struct ExtendedLimits
		{
			public BasicLimits Basic;

			public ulong ReadOperations;

			public ulong WriteOperations;

			public ulong OtherOperations;

			public ulong ReadBytes;

			public ulong WriteBytes;

			public ulong OtherBytes;

			public UIntPtr ProcessMemory;

			public UIntPtr JobMemory;

			public UIntPtr PeakProcessMemory;

			public UIntPtr PeakJobMemory;
		}

		internal struct Rate
		{
			public uint Flags;

			public uint Value;
		}

		internal const string Prefix = "Local\\Pavise.CpuCap.V1.";

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr CreateJobObject(IntPtr security, string name);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		internal static extern IntPtr OpenJobObject(uint access, bool inherit, string name);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetInformationJobObject(IntPtr job, int info, ref ExtendedLimits value, uint size);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool SetInformationJobObject(IntPtr job, int info, ref Rate value, uint size);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool QueryInformationJobObject(IntPtr job, int info, out Rate value, uint size, IntPtr returned);

		[DllImport("kernel32.dll", SetLastError = true)]
		internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool inside);

		[DllImport("kernel32.dll", SetLastError = true)]
		internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

		internal static IntPtr OpenTarget(int pid, long expectedCreation)
		{
			IntPtr intPtr = Native.OpenProcess(1052929, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return intPtr;
			}
			long creation;
			long cpu;
			ulong io;
			if (IsAlive(intPtr) && Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && expectedCreation > 0 && creation == expectedCreation)
			{
				string text = Native.ImagePath(intPtr);
				if (!string.IsNullOrEmpty(text) && !FamilyBoundary.CatalogProtected(pid, creation, Path.GetFileNameWithoutExtension(text), text))
				{
					return intPtr;
				}
			}
			Native.CloseHandle(intPtr);
			return IntPtr.Zero;
		}

		internal static bool IsAlive(IntPtr process)
		{
			if (process != IntPtr.Zero)
			{
				return Native.WaitForSingleObject(process, 0u) == 258;
			}
			return false;
		}

		internal static IntPtr Create(string name)
		{
			IntPtr intPtr = CreateJobObject(IntPtr.Zero, name);
			if (intPtr == IntPtr.Zero)
			{
				return intPtr;
			}
			if (Marshal.GetLastWin32Error() == 183)
			{
				Native.CloseHandle(intPtr);
				return IntPtr.Zero;
			}
			ExtendedLimits extendedLimits = default(ExtendedLimits);
			extendedLimits.Basic = new BasicLimits
			{
				Flags = 4096u
			};
			ExtendedLimits value = extendedLimits;
			if (SetInformationJobObject(intPtr, 9, ref value, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
			{
				return intPtr;
			}
			Native.CloseHandle(intPtr);
			return IntPtr.Zero;
		}

		internal static bool ValidActivePercent(decimal percent)
		{
			if (percent >= 0.01m && percent <= 50m)
			{
				return decimal.Truncate(percent * 100m) == percent * 100m;
			}
			return false;
		}

		internal static bool SetRate(IntPtr job, decimal percent)
		{
			if (percent != 0m && !ValidActivePercent(percent))
			{
				return false;
			}
			Rate rate = default(Rate);
			rate.Flags = ((!(percent == 0m)) ? 5u : 0u);
			rate.Value = (uint)(percent * 100m);
			Rate value = rate;
			if (!SetInformationJobObject(job, 15, ref value, 8u))
			{
				return false;
			}
			Rate value2;
			if (QueryInformationJobObject(job, 15, out value2, 8u, IntPtr.Zero) && value2.Flags == value.Flags)
			{
				if (!(percent == 0m))
				{
					return value2.Value == value.Value;
				}
				return true;
			}
			return false;
		}

		internal static bool ReadRate(IntPtr job, out Rate rate)
		{
			return QueryInformationJobObject(job, 15, out rate, 8u, IntPtr.Zero);
		}
	}
}
