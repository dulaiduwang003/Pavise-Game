using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PaviseApp
{
	internal sealed class OverviewTelemetryReader : IDisposable
	{
		private sealed class MemoryFacts
		{
			internal string Type;

			internal int Speed;

			internal ulong Capacity;
		}

		private struct MemoryStatus
		{
			internal uint Length;

			internal uint Load;

			internal ulong TotalPhysical;

			internal ulong AvailablePhysical;

			internal ulong TotalPageFile;

			internal ulong AvailablePageFile;

			internal ulong TotalVirtual;

			internal ulong AvailableVirtual;

			internal ulong AvailableExtendedVirtual;
		}

#pragma warning disable 0649
		private struct PdhValue
		{
			internal uint Status;

			internal double Value;
		}

		private struct PdhValueItem
		{
			internal IntPtr Name;

			internal PdhValue Value;
		}
#pragma warning restore 0649

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct DisplayMode
		{
			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
			internal string DeviceName;

			internal ushort SpecVersion;

			internal ushort DriverVersion;

			internal ushort Size;

			internal ushort DriverExtra;

			internal int Fields;

			internal int PositionX;

			internal int PositionY;

			internal int DisplayOrientation;

			internal int DisplayFixedOutput;

			internal short Color;

			internal short Duplex;

			internal short YResolution;

			internal short TTOption;

			internal short Collate;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
			internal string FormName;

			internal ushort LogPixels;

			internal int BitsPerPel;

			internal int PelsWidth;

			internal int PelsHeight;

			internal int DisplayFlags;

			internal int DisplayFrequency;

			internal int ICMMethod;

			internal int ICMIntent;

			internal int MediaType;

			internal int DitherType;

			internal int Reserved1;

			internal int Reserved2;

			internal int PanningWidth;

			internal int PanningHeight;
		}

		internal const string GpuScopeDescription = "Busiest 3D engine across all adapters";

		private const uint PdhMoreData = 2147485650u;

		private const uint PdhDouble = 512u;

		private const uint PdhNoCap100 = 32768u;

		private const uint MaximumGpuBuffer = 4194304u;

		private static readonly object hardwareGate = new object();

		private static OverviewHardwareInfo cachedHardware;

		private readonly object gate = new object();

		private bool disposed;

		private bool cpuPrimed;

		private bool gpuAttempted;

		private bool gpuPrimed;

		private ulong previousIdle;

		private ulong previousKernel;

		private ulong previousUser;

		private IntPtr gpuQuery;

		private IntPtr gpuCounter;

		private IntPtr gpuBuffer;

		private uint gpuBufferCapacity;

		internal OverviewHardwareInfo ReadHardware(string displayDeviceName)
		{
			lock (gate)
			{
				ThrowIfDisposed();
			}
			OverviewHardwareInfo overviewHardwareInfo;
			lock (hardwareGate)
			{
				if (cachedHardware == null)
				{
					cachedHardware = ReadStaticHardware();
				}
				overviewHardwareInfo = CopyHardware(cachedHardware);
			}
			OverviewGpuInfo gpu = OverviewGpuReader.Read();
			if (gpu != null)
			{
				overviewHardwareInfo.GpuName = gpu.Name;
				overviewHardwareInfo.GpuMemoryBytes = gpu.MemoryBytes;
			}
			try
			{
				DisplayMode value = default(DisplayMode);
				value.Size = (ushort)Marshal.SizeOf(typeof(DisplayMode));
				if (EnumDisplaySettingsW(string.IsNullOrWhiteSpace(displayDeviceName) ? null : displayDeviceName, -1, ref value))
				{
					overviewHardwareInfo.DisplayWidth = Math.Max(0, value.PelsWidth);
					overviewHardwareInfo.DisplayHeight = Math.Max(0, value.PelsHeight);
					overviewHardwareInfo.DisplayRefreshRate = ((value.DisplayFrequency > 1) ? value.DisplayFrequency : 0);
				}
			}
			catch
			{
			}
			return overviewHardwareInfo;
		}

		internal OverviewResourceSnapshot Sample()
		{
			lock (gate)
			{
				ThrowIfDisposed();
				OverviewResourceSnapshot overviewResourceSnapshot = new OverviewResourceSnapshot();
				overviewResourceSnapshot.GpuUsageDescription = "Busiest 3D engine across all adapters";
				OverviewResourceSnapshot overviewResourceSnapshot2 = overviewResourceSnapshot;
				overviewResourceSnapshot2.CpuPercent = SampleCpu();
				try
				{
					MemoryStatus memory = default(MemoryStatus);
					memory.Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
					if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical != 0 && memory.AvailablePhysical <= memory.TotalPhysical)
					{
						overviewResourceSnapshot2.TotalMemoryBytes = memory.TotalPhysical;
						overviewResourceSnapshot2.UsedMemoryBytes = memory.TotalPhysical - memory.AvailablePhysical;
					}
				}
				catch
				{
				}
				overviewResourceSnapshot2.GpuPercent = SampleGpu();
				return overviewResourceSnapshot2;
			}
		}

		internal void ResetCounters()
		{
			lock (gate)
			{
				ThrowIfDisposed();
				cpuPrimed = false;
				gpuPrimed = false;
			}
		}

		private double? SampleCpu()
		{
			try
			{
				if (GetActiveProcessorGroupCount() > 1)
				{
					return null;
				}
				ulong idle;
				ulong kernel;
				ulong user;
				if (!GetSystemTimes(out idle, out kernel, out user))
				{
					cpuPrimed = false;
					return null;
				}
				double? result = null;
				if (cpuPrimed && idle >= previousIdle && kernel >= previousKernel && user >= previousUser)
				{
					result = CpuPercentFromDeltas(idle - previousIdle, kernel - previousKernel, user - previousUser);
				}
				previousIdle = idle;
				previousKernel = kernel;
				previousUser = user;
				cpuPrimed = true;
				return result;
			}
			catch
			{
				cpuPrimed = false;
				return null;
			}
		}

		internal static double? CpuPercentFromDeltas(ulong idleDelta, ulong kernelDelta, ulong userDelta)
		{
			if (idleDelta > kernelDelta || kernelDelta > (ulong)(-1L - (long)userDelta))
			{
				return null;
			}
			ulong num = kernelDelta + userDelta;
			if (num == 0 || idleDelta > num)
			{
				return null;
			}
			return Math.Max(0.0, Math.Min(100.0, 100.0 * (1.0 - (double)idleDelta / (double)num)));
		}

		private double? SampleGpu()
		{
			try
			{
				if (!gpuAttempted)
				{
					gpuAttempted = true;
					if (PdhOpenQueryW(null, IntPtr.Zero, out gpuQuery) != 0 || gpuQuery == IntPtr.Zero)
					{
						CloseGpuQuery();
						return null;
					}
					if (PdhAddEnglishCounterW(gpuQuery, "\\GPU Engine(*engtype_3D)\\Utilization Percentage", IntPtr.Zero, out gpuCounter) != 0)
					{
						CloseGpuQuery();
						return null;
					}
				}
				if (gpuQuery == IntPtr.Zero)
				{
					return null;
				}
				if (PdhCollectQueryData(gpuQuery) != 0)
				{
					gpuPrimed = false;
					return null;
				}
				if (!gpuPrimed)
				{
					gpuPrimed = true;
					return null;
				}
				return ReadGpuArray();
			}
			catch
			{
				gpuPrimed = false;
				return null;
			}
		}

		private double? ReadGpuArray()
		{
			uint bytes = 0u;
			uint count = 0u;
			uint num = PdhGetFormattedCounterArrayW(gpuCounter, 33280u, ref bytes, ref count, IntPtr.Zero);
			if (num != 2147485650u || bytes == 0 || bytes > 4194304)
			{
				return null;
			}
			for (int i = 0; i < 2; i++)
			{
				if (bytes > gpuBufferCapacity)
				{
					IntPtr intPtr = Marshal.AllocHGlobal((int)bytes);
					if (gpuBuffer != IntPtr.Zero)
					{
						Marshal.FreeHGlobal(gpuBuffer);
					}
					gpuBuffer = intPtr;
					gpuBufferCapacity = bytes;
				}
				bytes = gpuBufferCapacity;
				num = PdhGetFormattedCounterArrayW(gpuCounter, 33280u, ref bytes, ref count, gpuBuffer);
				if (num == 2147485650u && bytes > gpuBufferCapacity && bytes <= 4194304)
				{
					continue;
				}
				if (num != 0)
				{
					return null;
				}
				int num2 = Marshal.SizeOf(typeof(PdhValueItem));
				if (count == 0 || (ulong)(count * num2) > (ulong)gpuBufferCapacity)
				{
					return null;
				}
				List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>((int)count);
				for (uint num3 = 0u; num3 < count; num3++)
				{
					PdhValueItem pdhValueItem = (PdhValueItem)Marshal.PtrToStructure(new IntPtr(gpuBuffer.ToInt64() + num3 * num2), typeof(PdhValueItem));
					if (pdhValueItem.Value.Status <= 1 && !(pdhValueItem.Name == IntPtr.Zero))
					{
						list.Add(new KeyValuePair<string, double>(Marshal.PtrToStringUni(pdhValueItem.Name), pdhValueItem.Value.Value));
					}
				}
				return AggregateGpu3D(list);
			}
			return null;
		}

		internal static double? AggregateGpu3D(IEnumerable<KeyValuePair<string, double>> values)
		{
			if (values == null)
			{
				return null;
			}
			Dictionary<string, double> dictionary = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, double> value2 in values)
			{
				string text = GpuEngineKey(value2.Key);
				if (text != null && !double.IsNaN(value2.Value) && !double.IsInfinity(value2.Value) && !(value2.Value < 0.0))
				{
					double value;
					dictionary.TryGetValue(text, out value);
					dictionary[text] = Math.Min(100.0, value + Math.Min(100.0, value2.Value));
				}
			}
			if (dictionary.Count == 0)
			{
				return null;
			}
			double num = 0.0;
			foreach (double value3 in dictionary.Values)
			{
				double val = value3;
				num = Math.Max(num, val);
			}
			return num;
		}

		internal static string GpuEngineKey(string instance)
		{
			if (string.IsNullOrEmpty(instance) || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			int num = instance.IndexOf('_', 4);
			if (num <= 4 || !Digits(instance, 4, num))
			{
				return null;
			}
			int num2 = instance.IndexOf("_engtype_3D", num, StringComparison.OrdinalIgnoreCase);
			if (num2 < 0)
			{
				return null;
			}
			int num3 = num2 + "_engtype_3D".Length;
			if (num3 < instance.Length && (instance[num3] != '#' || !Digits(instance, num3 + 1, instance.Length)))
			{
				return null;
			}
			string text = instance.Substring(num + 1, num2 - num - 1);
			if (!text.StartsWith("luid_", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			int num4 = text.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
			int num5 = text.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
			if (num4 <= 5 || num5 <= num4 + 6 || !Digits(text, num4 + 6, num5) || !Digits(text, num5 + 5, text.Length))
			{
				return null;
			}
			int num6 = text.IndexOf('_', 5);
			if (num6 < 0 || !HexWord(text, 5, num6) || !HexWord(text, num6 + 1, num4))
			{
				return null;
			}
			return text;
		}

		private static bool HexWord(string value, int first, int end)
		{
			if (end - first < 3 || end - first > 10 || value[first] != '0' || char.ToLowerInvariant(value[first + 1]) != 'x')
			{
				return false;
			}
			for (int i = first + 2; i < end; i++)
			{
				char c = char.ToLowerInvariant(value[i]);
				if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
				{
					return false;
				}
			}
			return true;
		}

		private static bool Digits(string value, int first, int end)
		{
			if (first >= end)
			{
				return false;
			}
			for (int i = first; i < end; i++)
			{
				if (value[i] < '0' || value[i] > '9')
				{
					return false;
				}
			}
			return true;
		}

		private static OverviewHardwareInfo ReadStaticHardware()
		{
			OverviewHardwareInfo overviewHardwareInfo = new OverviewHardwareInfo();
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
				{
					if (registryKey != null)
					{
						overviewHardwareInfo.CpuName = Compact(registryKey.GetValue("ProcessorNameString") as string);
					}
				}
			}
			catch
			{
			}
			try
			{
				overviewHardwareInfo.PhysicalCores = ReadPhysicalCoreCount();
			}
			catch
			{
			}
			try
			{
				overviewHardwareInfo.LogicalProcessors = (int)GetActiveProcessorCount(ushort.MaxValue);
			}
			catch
			{
				overviewHardwareInfo.LogicalProcessors = Environment.ProcessorCount;
			}
			try
			{
				ulong kilobytes;
				if (GetPhysicallyInstalledSystemMemory(out kilobytes) && kilobytes <= 18014398509481983L)
				{
					overviewHardwareInfo.InstalledMemoryBytes = kilobytes * 1024;
				}
			}
			catch
			{
			}
			try
			{
				Task<MemoryFacts> task = Task.Factory.StartNew((Func<MemoryFacts>)ReadMemoryFacts, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
				if (task.Wait(1800))
				{
					MemoryFacts result = task.Result;
					overviewHardwareInfo.MemoryType = result.Type;
					overviewHardwareInfo.MemorySpeedMts = result.Speed;
					if (overviewHardwareInfo.InstalledMemoryBytes == 0)
					{
						overviewHardwareInfo.InstalledMemoryBytes = result.Capacity;
					}
				}
			}
			catch
			{
			}
			return overviewHardwareInfo;
		}

		private static int ReadPhysicalCoreCount()
		{
			if (!CpuTopology.MultiGroup)
			{
				return Math.Max(0, CpuTopology.PhysicalCoreCount);
			}
			uint bytes = 0u;
			GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref bytes);
			if (bytes == 0 || bytes > 1048576)
			{
				return 0;
			}
			IntPtr intPtr = Marshal.AllocHGlobal((int)bytes);
			try
			{
				if (!GetLogicalProcessorInformationEx(0, intPtr, ref bytes))
				{
					return 0;
				}
				int num = 0;
				long num2;
				int num3;
				for (num2 = 0L; num2 + 8 <= bytes; num2 += num3)
				{
					IntPtr ptr = new IntPtr(intPtr.ToInt64() + num2);
					num3 = Marshal.ReadInt32(ptr, 4);
					if (num3 < 8 || num2 + num3 > bytes)
					{
						return 0;
					}
					if (Marshal.ReadInt32(ptr) == 0)
					{
						num++;
					}
				}
				return (num2 == bytes) ? num : 0;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
		}

		private static MemoryFacts ReadMemoryFacts()
		{
			MemoryFacts memoryFacts = new MemoryFacts();
			try
			{
				EnumerationOptions enumerationOptions = new EnumerationOptions();
				enumerationOptions.Timeout = TimeSpan.FromMilliseconds(1200.0);
				enumerationOptions.ReturnImmediately = false;
				enumerationOptions.Rewindable = false;
				EnumerationOptions options = enumerationOptions;
				using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher(new ManagementScope("\\\\.\\root\\cimv2"), new ObjectQuery("SELECT Capacity, SMBIOSMemoryType, ConfiguredClockSpeed FROM Win32_PhysicalMemory"), options))
				{
					using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
					{
						string text = null;
						int num = int.MaxValue;
						int num2 = 0;
						bool flag = true;
						bool flag2 = true;
						bool flag3 = true;
						ulong num3 = 0uL;
						foreach (ManagementObject item in managementObjectCollection)
						{
							using (item)
							{
								ulong num4 = ToUnsigned(item["Capacity"]);
								if (num4 == 0 || num4 > (ulong)(-1L - (long)num3))
								{
									flag3 = false;
								}
								else
								{
									num3 += num4;
								}
								string text2 = MemoryTypeName((int)ToUnsigned(item["SMBIOSMemoryType"]));
								if (text2 == null || (text != null && text != text2))
								{
									flag = false;
								}
								else
								{
									text = text2;
								}
								ulong num5 = ToUnsigned(item["ConfiguredClockSpeed"]);
								if (num5 == 0 || num5 > 100000)
								{
									flag2 = false;
								}
								else
								{
									num = Math.Min(num, (int)num5);
								}
								num2++;
							}
						}
						if (num2 > 0)
						{
							memoryFacts.Type = (flag ? text : null);
							memoryFacts.Speed = ((flag2 && num != int.MaxValue) ? num : 0);
							memoryFacts.Capacity = (flag3 ? num3 : 0);
						}
					}
				}
			}
			catch
			{
			}
			return memoryFacts;
		}

		internal static string MemoryTypeName(int smbiosType)
		{
			switch (smbiosType)
			{
			case 18:
				return "DDR";
			case 19:
				return "DDR2";
			case 20:
				return "DDR2 FB-DIMM";
			case 24:
				return "DDR3";
			case 26:
				return "DDR4";
			case 34:
				return "DDR5";
			case 27:
				return "LPDDR";
			case 28:
				return "LPDDR2";
			case 29:
				return "LPDDR3";
			case 30:
				return "LPDDR4";
			case 35:
				return "LPDDR5";
			default:
				return null;
			}
		}

		private static ulong ToUnsigned(object value)
		{
			try
			{
				return (value == null) ? 0 : Convert.ToUInt64(value);
			}
			catch
			{
				return 0uL;
			}
		}

		private static string Compact(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
			{
				text = text.Replace("  ", " ");
			}
			return text.Trim();
		}

		private static OverviewHardwareInfo CopyHardware(OverviewHardwareInfo value)
		{
			OverviewHardwareInfo overviewHardwareInfo = new OverviewHardwareInfo();
			overviewHardwareInfo.CpuName = value.CpuName;
			overviewHardwareInfo.GpuName = value.GpuName;
			overviewHardwareInfo.MemoryType = value.MemoryType;
			overviewHardwareInfo.PhysicalCores = value.PhysicalCores;
			overviewHardwareInfo.LogicalProcessors = value.LogicalProcessors;
			overviewHardwareInfo.MemorySpeedMts = value.MemorySpeedMts;
			overviewHardwareInfo.InstalledMemoryBytes = value.InstalledMemoryBytes;
			overviewHardwareInfo.GpuMemoryBytes = value.GpuMemoryBytes;
			return overviewHardwareInfo;
		}

		private void ThrowIfDisposed()
		{
			if (disposed)
			{
				throw new ObjectDisposedException("OverviewTelemetryReader");
			}
		}

		private void CloseGpuQuery()
		{
			if (gpuQuery != IntPtr.Zero)
			{
				try
				{
					PdhCloseQuery(gpuQuery);
				}
				catch
				{
				}
			}
			gpuQuery = (gpuCounter = IntPtr.Zero);
			gpuPrimed = false;
		}

		public void Dispose()
		{
			lock (gate)
			{
				if (!disposed)
				{
					disposed = true;
					CloseGpuQuery();
					if (gpuBuffer != IntPtr.Zero)
					{
						Marshal.FreeHGlobal(gpuBuffer);
					}
					gpuBuffer = IntPtr.Zero;
					gpuBufferCapacity = 0u;
				}
			}
		}

		[DllImport("kernel32.dll")]
		private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

		[DllImport("kernel32.dll")]
		private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);

		[DllImport("kernel32.dll")]
		private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);

		[DllImport("kernel32.dll")]
		private static extern uint GetActiveProcessorCount(ushort group);

		[DllImport("kernel32.dll")]
		private static extern ushort GetActiveProcessorGroupCount();

		[DllImport("kernel32.dll")]
		private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint bytes);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern bool EnumDisplaySettingsW(string device, int mode, ref DisplayMode value);

		[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
		private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

		[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
		private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

		[DllImport("pdh.dll")]
		private static extern uint PdhCollectQueryData(IntPtr query);

		[DllImport("pdh.dll", CharSet = CharSet.Unicode)]
		private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, ref uint count, IntPtr buffer);

		[DllImport("pdh.dll")]
		private static extern uint PdhCloseQuery(IntPtr query);
	}
}
